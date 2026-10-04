using System;
using UnityEngine;

namespace LookingGlass.Community
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(ViewWeaveRig), typeof(BridgeWorkerQuiltOutput))]
    public sealed class ViewWeaveBridgeSession : MonoBehaviour
    {
        public enum SessionState
        {
            Idle,
            Connecting,
            Rendering,
            Reconfiguring,
            Closing,
            Fault
        }

        public bool startOnEnable;
        public string runtimePath;
        public string applicationName = "LookingGlassCommunity";
        public uint displayOrdinal;
        [SerializeField] QuiltQualityPreset quality = QuiltQualityPreset.Preview;

        public SessionState State { get; private set; }
        public string LastError { get; private set; }
        public QuiltQualityPreset Quality => quality;
        public BridgeDisplayProfile Profile => output == null ? default : output.Profile;

        ViewWeaveRig rig;
        BridgeWorkerQuiltOutput output;
        bool restartRequested;
        QuiltQualityPreset pendingQuality;
        float closingSince;
        bool initialized;
        bool pendingStart;
        bool profileApplied;

        void Awake()
        {
            rig = GetComponent<ViewWeaveRig>();
            output = GetComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            output.applicationName = applicationName;
            output.displayOrdinal = displayOrdinal;
            rig.SetOutput(output);
            State = SessionState.Idle;
            LastError = string.Empty;
            initialized = true;
            if (output.State != BridgeWorkerQuiltOutput.WorkerState.Closed)
            {
                Fail("Bridge worker was already active before session initialization");
            }
        }

        void OnEnable()
        {
            if (initialized && startOnEnable) pendingStart = true;
        }

        void Start()
        {
            if (startOnEnable) pendingStart = true;
        }

        void Update()
        {
            if (output == null) return;
            if (pendingStart && isActiveAndEnabled && State == SessionState.Idle)
            {
                pendingStart = false;
                StartSession();
            }
            if (State == SessionState.Closing)
            {
                if (BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
                {
                    State = SessionState.Idle;
                    if (restartRequested)
                    {
                        restartRequested = false;
                        quality = pendingQuality;
                        StartSession();
                    }
                }
                else if (Time.realtimeSinceStartup - closingSince > 10f)
                {
                    Fail("Bridge worker shutdown timed out after 10 seconds");
                }
                return;
            }
            if (State == SessionState.Reconfiguring)
            {
                output.Poll();
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed || output.State == BridgeWorkerQuiltOutput.WorkerState.Closing || !string.IsNullOrEmpty(output.LastError))
                {
                    Fail(string.IsNullOrEmpty(output.LastError) ? "Bridge worker stopped during quality reconfiguration" : output.LastError);
                    return;
                }
                if (output.TryResetTransfer())
                {
                    if (!string.IsNullOrEmpty(output.LastError)) { Fail(output.LastError); return; }
                    quality = pendingQuality;
                    if (!ApplyReadyProfile()) return;
                    profileApplied = true;
                    State = SessionState.Rendering;
                }
                else if (!string.IsNullOrEmpty(output.LastError))
                {
                    Fail(output.LastError);
                }
                else if (Time.realtimeSinceStartup - closingSince > 10f)
                {
                    Fail("Bridge worker transfer reset timed out");
                }
                return;
            }
            if (State == SessionState.Idle || State == SessionState.Fault) return;
            if (rig != null && rig.Renderer != null && !string.IsNullOrEmpty(rig.Renderer.LastAutoRenderError))
            {
                Fail(rig.Renderer.LastAutoRenderError);
                return;
            }
            output.Poll();
            if (output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
            {
                if (!string.IsNullOrEmpty(output.LastError)) Fail(output.LastError);
                else Fail("Bridge worker closed unexpectedly");
                return;
            }
            if (output.State == BridgeWorkerQuiltOutput.WorkerState.Connecting)
            {
                State = SessionState.Connecting;
                return;
            }
            if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
            {
                Fail(output.LastError);
                return;
            }
            if (output.State == BridgeWorkerQuiltOutput.WorkerState.Ready)
            {
                if (!profileApplied)
                {
                    if (!ApplyReadyProfile()) return;
                    profileApplied = true;
                }
                State = SessionState.Rendering;
                return;
            }
        }

        public bool StartSession()
        {
            if (State == SessionState.Connecting || State == SessionState.Rendering || State == SessionState.Reconfiguring || State == SessionState.Closing)
            {
                SetError("Bridge session is already active");
                return false;
            }
            if (!initialized || !isActiveAndEnabled || output == null || !output.isActiveAndEnabled)
            {
                return Fail("Bridge session and worker output must be active before begin");
            }
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), quality))
            {
                return Fail("Bridge session quality preset is invalid");
            }
            if (BridgeWorkerQuiltOutput.ShutdownPendingCount != 0)
            {
                return Fail("Bridge worker shutdown is still pending");
            }
            if (Application.isEditor)
            {
                return Fail("Bridge worker is supported in Windows Player only");
            }
            if (!ValidateRig()) return false;
            rig.Renderer.ResetAutoRenderError();
            if (!ResolveRuntimePath(out string resolvedPath)) return false;
            output.runtimePath = resolvedPath;
            output.applicationName = applicationName;
            output.displayOrdinal = displayOrdinal;
            output.initializeOnEnable = false;
            if (!output.TryBegin())
            {
                Fail(output.LastError);
                return false;
            }
            LastError = string.Empty;
            profileApplied = false;
            State = SessionState.Connecting;
            return true;
        }

        public void StopSession()
        {
            restartRequested = false;
            pendingStart = false;
            profileApplied = false;
            pendingQuality = quality;
            if (rig != null) rig.AutoRender = false;
            if (output == null)
            {
                State = SessionState.Idle;
                return;
            }
            output.Dispose();
            if (BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
            {
                State = SessionState.Idle;
                return;
            }
            closingSince = Time.realtimeSinceStartup;
            State = SessionState.Closing;
        }

        public bool SetQuality(QuiltQualityPreset value)
        {
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), value))
            {
                SetError("Bridge session quality preset is invalid");
                return false;
            }
            if (State == SessionState.Idle || State == SessionState.Fault)
            {
                quality = value;
                pendingQuality = value;
                return true;
            }
            if (State == SessionState.Rendering && quality == value) return true;
            quality = value;
            pendingQuality = value;
            if (State == SessionState.Rendering)
            {
                pendingQuality = value;
                if (rig != null) rig.AutoRender = false;
                closingSince = Time.realtimeSinceStartup;
                State = SessionState.Reconfiguring;
                return true;
            }
            if (State == SessionState.Reconfiguring || State == SessionState.Connecting) return true;
            if (State == SessionState.Closing)
            {
                restartRequested = true;
                return true;
            }
            if (State != SessionState.Closing)
            {
                StopSession();
                if (State == SessionState.Idle) return StartSession();
            }
            restartRequested = true;
            return true;
        }

        bool ApplyReadyProfile()
        {
            BridgeDisplayProfile profile = output.Profile;
            if (!profile.IsValid) return Fail("Bridge worker returned an invalid display profile");
            try
            {
                QuiltQualityProfile selected = CreateQualityProfile(profile, quality);
                rig.ApplyProfile(selected);
                rig.SetOutput(output);
                rig.AutoRender = true;
                return true;
            }
            catch (Exception error)
            {
                return Fail(Sanitize(error.Message));
            }
        }

        public static QuiltQualityProfile CreateQualityProfile(BridgeDisplayProfile profile, QuiltQualityPreset preset)
        {
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
            switch (preset)
            {
                case QuiltQualityPreset.Balanced:
                    return QuiltQualityProfile.Balanced(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                case QuiltQualityPreset.Native:
                    return QuiltQualityProfile.Native(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                default:
                    return QuiltQualityProfile.Preview(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
            }
        }

        bool ValidateRig()
        {
            if (rig == null || output == null) return Fail("ViewWeaveRig and BridgeWorkerQuiltOutput are required");
            try
            {
                if (!rig.ValidateRig(out string error)) return Fail(Sanitize(error));
                return true;
            }
            catch (Exception error)
            {
                return Fail(Sanitize(error.Message));
            }
        }

        bool ResolveRuntimePath(out string resolvedPath)
        {
            if (BridgeRuntimeLocator.TryResolve(runtimePath, out resolvedPath, out string error)) return true;
            return Fail(error);
        }

        bool Fail(string message)
        {
            SetError(message);
            if (rig != null) rig.AutoRender = false;
            if (output != null) output.Dispose();
            profileApplied = false;
            pendingStart = false;
            restartRequested = false;
            State = SessionState.Fault;
            return false;
        }

        void SetError(string message)
        {
            LastError = Sanitize(message);
        }

        void OnDisable()
        {
            pendingStart = false;
            StopSession();
        }

        void OnDestroy()
        {
            StopSession();
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }
    }
}
