using System;
using UnityEngine;

namespace ViewWeave
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
        public string applicationName = "ViewWeave";
        public uint displayOrdinal;
        [SerializeField] QuiltQualityPreset quality = QuiltQualityPreset.Balanced;
        [SerializeField] BridgeSubpixelMode subpixelMode = BridgeSubpixelMode.Automatic;

        public SessionState State { get; private set; }
        public string LastError { get; private set; }
        public QuiltQualityPreset Quality => quality;
        public BridgeSubpixelMode SubpixelMode => subpixelMode;
        public BridgeDisplayProfile Profile => output == null ? default : output.Profile;

        ViewWeaveRig rig;
        BridgeWorkerQuiltOutput output;
        bool restartRequested;
        QuiltQualityPreset pendingQuality;
        float closingSince;
        float connectingSince;
        bool initialized;
        bool pendingStart;
        bool profileApplied;
        string profileRuntimePathAtStart;
        string profileApplicationNameAtStart;
        uint profileDisplayOrdinalAtStart;
        BridgeSubpixelMode subpixelModeAtStart;
        bool completionWatchdogArmed;
        uint watchdogSubmissions;
        uint watchdogCompleted;
        float completionProgressSince;
        object playbackToken;
        Action<string> playbackInvalidated;
        IDisposable mutationOwnerRegistration;
        Camera sourceCameraAtStart;
        Camera referenceCameraAtStart;
        MonoBehaviour guardAtStart;
        MonoBehaviour outputBehaviourAtStart;
        bool useRenderRequestsAtStart;
        string outputRuntimePathAtStart;
        string outputApplicationNameAtStart;
        uint outputDisplayOrdinalAtStart;

        void Awake()
        {
            State = SessionState.Idle;
            LastError = string.Empty;
            // An inactive Windows transport must not replace another backend's binding.
            if (enabled) InitializeDependencies();
        }

        void InitializeDependencies()
        {
            ResolveDependencies();
            if (!initialized) return;
            if (output.State != BridgeWorkerQuiltOutput.WorkerState.Closed)
            {
                Fail("Bridge worker was already active before session initialization");
            }
        }

        void OnEnable()
        {
            if (!initialized) InitializeDependencies();
            else ResolveDependencies();
            if (startOnEnable) pendingStart = true;
        }

        void ResolveDependencies()
        {
            if (rig == null) rig = GetComponent<ViewWeaveRig>();
            if (output == null) output = GetComponent<BridgeWorkerQuiltOutput>();
            if (output == null || rig == null)
            {
                initialized = false;
                return;
            }
            output.initializeOnEnable = false;
            output.applicationName = applicationName;
            output.displayOrdinal = displayOrdinal;
            if (!rig.HasOutputBinding(output)) rig.SetOutput(output);
            initialized = true;
        }

        void Start()
        {
            if (startOnEnable) pendingStart = true;
        }

        void Update()
        {
            ReleaseMutationOwnerIfStopped();
            if (output == null) return;
            if ((State == SessionState.Connecting || State == SessionState.Rendering || State == SessionState.Reconfiguring) &&
                mutationOwnerRegistration == null)
            {
                Fail("Bridge Session lost its renderer mutation ownership registration.");
                return;
            }
            if ((State == SessionState.Connecting || State == SessionState.Rendering || State == SessionState.Reconfiguring) &&
                HasUnsafeLiveConfigurationChange())
            {
                Fail("Bridge transport or renderer structure changed during the live Session. Stop the Session before changing those settings.");
                return;
            }
            if (pendingStart && isActiveAndEnabled && State == SessionState.Idle && !BridgeWorkerLifecycle.RecoveryPending &&
                !BridgeDisplayDiscovery.IsBusy && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
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
                    BridgeDisplayMetadata.Publish(rig, output, runtimePath, applicationName, displayOrdinal, output.Profile);
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
            if (profileApplied && State == SessionState.Rendering)
            {
                if (output.RequiresReconnect)
                {
                    Fail(string.IsNullOrEmpty(output.DisplayRefreshStatus)
                        ? "The selected Looking Glass profile changed. Stop and reconnect before rendering."
                        : output.DisplayRefreshStatus);
                    return;
                }
                if (!IsFinite(output.zoom) || output.zoom <= 0f)
                {
                    Fail("Bridge zoom must remain finite and greater than zero while the Session is active.");
                    return;
                }
                string physicalError = GetPhysicalOutputError();
                if (!string.IsNullOrEmpty(physicalError)) { Fail(physicalError); return; }
            }
            if (rig != null && !rig.AutoRender)
            {
                ResetCompletionWatchdog();
            }
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
                if (Time.realtimeSinceStartup - connectingSince > 10f)
                {
                    Fail("Bridge worker connection timed out after 10 seconds");
                }
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
                    BridgeDisplayMetadata.Publish(rig, output, runtimePath, applicationName, displayOrdinal, output.Profile);
                }
                State = SessionState.Rendering;
                PollCompletionWatchdog();
                return;
            }
            PollCompletionWatchdog();
        }

        public bool CanChangeSubpixelMode(out string error)
        {
            error = string.Empty;
            if ((State != SessionState.Idle && State != SessionState.Fault) || playbackToken != null ||
                BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 ||
                (output != null && output.State != BridgeWorkerQuiltOutput.WorkerState.Closed))
            {
                error = "Stop Bridge output and wait for cleanup before changing subpixel processing.";
                return false;
            }
            ViewWeaveRig owner = rig != null ? rig : GetComponent<ViewWeaveRig>();
            return owner == null || owner.Renderer == null ||
                owner.Renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out error);
        }

        public bool TrySetSubpixelMode(BridgeSubpixelMode value, out string error)
        {
            if (!BridgeSubpixelPolicy.Validate(value, out error) || !CanChangeSubpixelMode(out error)) return false;
            subpixelMode = value;
            return true;
        }

        public bool StartSession()
        {
            ReleaseMutationOwnerIfStopped();
            if (playbackToken != null)
            {
                SetError("Bridge playback cleanup is still holding this session.");
                return false;
            }
            if (State == SessionState.Connecting || State == SessionState.Rendering || State == SessionState.Reconfiguring || State == SessionState.Closing)
            {
                SetError("Bridge session is already active");
                return false;
            }
            if (!BridgeSubpixelPolicy.Validate(subpixelMode, out string subpixelError))
                return Fail(subpixelError);
            if (BridgeDisplayDiscovery.IsBusy)
            {
                SetError("Bridge display metadata is still refreshing or cleaning up. Wait before starting a Session.");
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
            if (Application.isEditor && BridgeWorkerLifecycle.RecoveryPending)
                return Fail("Bridge worker requires Windows D3D11 or D3D12 Play mode in the Editor");
            if (!BridgeGraphicsBackendPolicy.TrySelect(
                BridgeGraphicsBackendRoute.Worker,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                UnityEngine.SystemInfo.graphicsDeviceType,
                UnityEngine.SystemInfo.renderingThreadingMode,
                out _,
                out string backendError))
                return Fail(backendError);
            if (!ValidateRig()) return false;
            if (!rig.Renderer.TryGetEffectiveSettings(out _, out string framingError))
                return Fail("Physical framing is invalid: " + Sanitize(framingError));
            if (rig.Renderer.physicalFraming != null && rig.Renderer.physicalFraming.enabled && output.zoom != 1f)
                return Fail("Physical 1:1 framing requires Bridge zoom to be exactly 1.");
            rig.Renderer.ResetAutoRenderError();
            if (!ResolveRuntimePath(out string resolvedPath)) return false;
            output.runtimePath = resolvedPath;
            output.applicationName = applicationName;
            output.displayOrdinal = displayOrdinal;
            if (mutationOwnerRegistration == null)
            {
                rig.Renderer.SetMutationOwnerRequired(true);
                mutationOwnerRegistration = QuiltRendererMutationPolicy.RegisterOwner(rig.Renderer, this,
                    GetMutationBlockReason, out string registrationError);
                if (mutationOwnerRegistration == null)
                {
                    rig.Renderer.SetMutationOwnerRequired(false);
                    return Fail("Bridge Session could not register renderer ownership: " + Sanitize(registrationError));
                }
            }
            sourceCameraAtStart = rig.Renderer.sourceCamera;
            referenceCameraAtStart = rig.Renderer.referenceCamera;
            guardAtStart = rig.Renderer.GuardBehaviour;
            outputBehaviourAtStart = rig.Renderer.OutputBehaviour;
            useRenderRequestsAtStart = rig.Renderer.useRenderRequests;
            outputRuntimePathAtStart = output.runtimePath ?? string.Empty;
            outputApplicationNameAtStart = output.applicationName ?? string.Empty;
            outputDisplayOrdinalAtStart = output.displayOrdinal;
            profileRuntimePathAtStart = runtimePath ?? string.Empty;
            profileApplicationNameAtStart = applicationName ?? string.Empty;
            profileDisplayOrdinalAtStart = displayOrdinal;
            subpixelModeAtStart = subpixelMode;
            output.initializeOnEnable = false;
            if (!output.TryBegin())
            {
                Fail(output.LastError);
                return false;
            }
            LastError = string.Empty;
            profileApplied = false;
            ResetCompletionWatchdog();
            connectingSince = Time.realtimeSinceStartup;
            State = SessionState.Connecting;
            return true;
        }

        internal bool TryAcquirePlayback(object token, Action<string> invalidated, out long generation, out string error)
        {
            generation = 0;
            error = string.Empty;
#if UNITY_EDITOR
            if (!Application.isEditor || !Application.isPlaying || Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = "Quilt playback requires Windows Editor Play mode.";
                return false;
            }
#else
            error = "Quilt playback is available only in the Unity Editor.";
            return false;
#endif
            if (token == null || invalidated == null) { error = "Playback ownership is invalid."; return false; }
            if (!isActiveAndEnabled || State != SessionState.Rendering || output == null || !output.isActiveAndEnabled || !output.Ready || rig == null || !rig.isActiveAndEnabled || !rig.AutoRender || rig.Renderer == null || !rig.Renderer.isActiveAndEnabled)
            {
                error = "Playback requires an active Ready Bridge session with Auto Render enabled.";
                return false;
            }
            if (playbackToken != null) { error = "This Bridge session is already leased by playback."; return false; }
            if (!output.TryBeginPlaybackOutput(token, out error)) return false;
            playbackToken = token;
            playbackInvalidated = invalidated;
            try
            {
                rig.AutoRender = false;
                generation = output.SessionGeneration;
                ResetCompletionWatchdog();
                return true;
            }
            catch (Exception exception)
            {
                output.EndPlaybackOutput(token);
                playbackToken = null;
                playbackInvalidated = null;
                error = Sanitize(exception.Message);
                return false;
            }
        }

        internal void ReleasePlayback(object token)
        {
            if (token == null || !ReferenceEquals(playbackToken, token)) return;
            if (output != null) output.EndPlaybackOutput(token);
            playbackToken = null;
            playbackInvalidated = null;
        }

        internal void NotifyPlaybackFailure(object token, string reason)
        {
            if (token == null || !ReferenceEquals(playbackToken, token)) return;
            Fail("Quilt playback stopped after a Bridge submission failure: " + Sanitize(reason));
        }

        void InvalidatePlayback(string reason)
        {
            Action<string> callback = playbackInvalidated;
            if (callback == null) return;
            try { callback(Sanitize(reason)); }
            catch (Exception error) { Debug.LogError("[ViewWeave] Playback invalidation callback failed: " + Sanitize(error.Message)); }
        }

        public void StopSession()
        {
            InvalidatePlayback("Bridge session stopped while quilt playback was active.");
            restartRequested = false;
            pendingStart = false;
            profileApplied = false;
            ResetCompletionWatchdog();
            pendingQuality = quality;
            if (rig != null) rig.AutoRender = false;
            if (output == null)
            {
                State = SessionState.Idle;
                ReleaseMutationOwnerIfStopped();
                return;
            }
            output.Dispose();
            if (BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
            {
                State = SessionState.Idle;
                ReleaseMutationOwnerIfStopped();
                return;
            }
            closingSince = Time.realtimeSinceStartup;
            State = SessionState.Closing;
        }

        public bool SetQuality(QuiltQualityPreset value)
        {
            if (playbackToken != null)
            {
                SetError("Bridge quality cannot change while quilt playback owns the session.");
                return false;
            }
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), value))
            {
                SetError("Bridge session quality preset is invalid");
                return false;
            }
            if (BridgeDisplayDiscovery.IsBusy)
            {
                SetError("Bridge quality cannot change while connected display metadata is refreshing or cleaning up.");
                return false;
            }
            ViewWeaveRig mutationRig = rig != null ? rig : GetComponent<ViewWeaveRig>();
            QuiltRenderer mutationRenderer = mutationRig == null
                ? GetComponent<QuiltRenderer>()
                : mutationRig.Renderer != null ? mutationRig.Renderer : mutationRig.GetComponent<QuiltRenderer>();
            if (mutationRenderer != null &&
                !QuiltRendererMutationPolicy.TryCheck(mutationRenderer, QuiltRendererMutationKind.Quality, out string mutationError))
            {
                SetError(mutationError);
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
                ResetCompletionWatchdog();
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
            string physicalError = GetPhysicalOutputError(profile);
            if (!string.IsNullOrEmpty(physicalError)) return Fail(physicalError);
            try
            {
                QuiltQualityProfile selected = CreateQualityProfile(profile, quality);
                if (mutationOwnerRegistration != null)
                {
                    if (!rig.ApplyProfileForBridgeOwner(selected, this, out string profileError)) return Fail(Sanitize(profileError));
                    if (!rig.SetOutputForBridgeOwner(output, this, out string outputError)) return Fail(Sanitize(outputError));
                }
                else
                {
                    rig.ApplyProfile(selected);
                    rig.SetOutput(output);
                }
                if (mutationOwnerRegistration != null)
                {
                    if (!rig.SetAutoRenderForBridgeOwner(true, this, out string autoRenderError)) return Fail(Sanitize(autoRenderError));
                }
                else rig.AutoRender = true;
                return true;
            }
            catch (Exception error)
            {
                return Fail(Sanitize(error.Message));
            }
        }

        string GetPhysicalOutputError(BridgeDisplayProfile profile = default)
        {
            QuiltPhysicalFraming physical = rig == null || rig.Renderer == null ? null : rig.Renderer.physicalFraming;
            if (physical == null || !physical.enabled) return string.Empty;
            if (!rig.Renderer.TryGetEffectiveSettings(out _, out string framingError))
                return "Physical framing became invalid: " + Sanitize(framingError);
            if (output != null && output.zoom != 1f)
                return "Physical 1:1 framing requires Bridge zoom to be exactly 1.";
            BridgeDisplayProfile connected = profile.IsValid ? profile : output == null ? default : output.Profile;
            if (connected.IsValid && !connected.MatchesPhysicalAspect(physical.visibleApertureWidthMillimeters, physical.visibleApertureHeightMillimeters))
                return "Physical aperture aspect does not match the connected display profile.";
            return string.Empty;
        }

        string GetMutationBlockReason(QuiltRendererMutationRequest request)
        {
            if (rig == null || rig.Renderer == null || output == null)
                return "The Bridge Session renderer owner is unavailable.";
            if (request.Kind == QuiltRendererMutationKind.AutoRender && request.HasBooleanValue && !request.BooleanValue)
                return string.Empty;
            if (State == SessionState.Closing || State == SessionState.Fault)
                return "The Bridge Session is stopping or recovering; wait until its worker cleanup completes.";

            switch (request.Kind)
            {
                case QuiltRendererMutationKind.Framing:
                    if (State != SessionState.Rendering || !profileApplied || !output.Ready)
                        return "Framing can change only while the Bridge Session is Ready and rendering.";
                    if (request.HasFraming && request.Framing.PhysicalFramingEnabled)
                    {
                        if (!IsFinite(output.zoom) || output.zoom != 1f)
                            return "Physical 1:1 framing requires Bridge zoom to be exactly 1.";
                        if (!output.Profile.IsValid || !output.Profile.MatchesPhysicalAspect(
                                request.Framing.ApertureWidthMillimeters, request.Framing.ApertureHeightMillimeters))
                            return "Physical aperture aspect must match the connected display profile.";
                    }
                    return string.Empty;
                case QuiltRendererMutationKind.SamplingMode:
                    return State == SessionState.Rendering && profileApplied && output.Ready
                        ? string.Empty
                        : "View sampling can change only while the Bridge Session is Ready and rendering.";
                case QuiltRendererMutationKind.Quality:
                    if (playbackToken != null) return "Bridge quality cannot change while playback owns the Session.";
                    if (BridgeDisplayDiscovery.IsBusy) return "Bridge quality cannot change while connected display metadata is refreshing or cleaning up.";
                    return State == SessionState.Closing || State == SessionState.Fault
                        ? "Bridge quality cannot change while worker cleanup or recovery is pending."
                        : string.Empty;
                case QuiltRendererMutationKind.Recording:
                    return State == SessionState.Rendering && output.Ready && playbackToken == null
                        ? string.Empty
                        : "Recording requires a Ready Bridge Session without another active playback owner.";
                case QuiltRendererMutationKind.Playback:
                    return State == SessionState.Rendering && output.Ready && playbackToken == null
                        ? string.Empty
                        : "Playback requires a Ready Bridge Session without an existing playback owner.";
                case QuiltRendererMutationKind.AutoRender:
                    if (!request.HasBooleanValue || !request.BooleanValue) return string.Empty;
                    return State == SessionState.Rendering && output.Ready && playbackToken == null
                        ? string.Empty
                        : "Auto Render can be enabled only while the Bridge Session is Ready and not leased by playback.";
                case QuiltRendererMutationKind.OutputZoom:
                    if (!request.HasFloatValue || !IsFinite(request.FloatValue) || request.FloatValue <= 0f)
                        return "Bridge zoom must be finite and greater than zero.";
                    if (rig.Renderer.physicalFraming != null && rig.Renderer.physicalFraming.enabled && request.FloatValue != 1f)
                        return "Physical 1:1 framing requires Bridge zoom to be exactly 1.";
                    return State == SessionState.Rendering && output.Ready && playbackToken == null
                        ? string.Empty
                        : "Bridge zoom can change only while the Session is Ready and not leased by playback.";
                default:
                    return "The Bridge Session owns this renderer; stop the Session before changing its structure or transport.";
            }
        }

        bool HasUnsafeLiveConfigurationChange()
        {
            if (rig == null || rig.Renderer == null || output == null) return true;
            QuiltRenderer renderer = rig.Renderer;
            return subpixelMode != subpixelModeAtStart ||
                   renderer.sourceCamera != sourceCameraAtStart || renderer.referenceCamera != referenceCameraAtStart ||
                   renderer.GuardBehaviour != guardAtStart || renderer.OutputBehaviour != outputBehaviourAtStart ||
                   renderer.useRenderRequests != useRenderRequestsAtStart ||
                   !string.Equals(runtimePath ?? string.Empty, profileRuntimePathAtStart, StringComparison.Ordinal) ||
                   !string.Equals(applicationName ?? string.Empty, profileApplicationNameAtStart, StringComparison.Ordinal) ||
                   displayOrdinal != profileDisplayOrdinalAtStart ||
                   !string.Equals(output.runtimePath ?? string.Empty, outputRuntimePathAtStart, StringComparison.Ordinal) ||
                   !string.Equals(output.applicationName ?? string.Empty, outputApplicationNameAtStart, StringComparison.Ordinal) ||
                   output.displayOrdinal != outputDisplayOrdinalAtStart;
        }

        void ReleaseMutationOwnerIfStopped()
        {
            bool settledState = State == SessionState.Idle || State == SessionState.Fault;
            bool outputClosed = output == null || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed;
            if (mutationOwnerRegistration == null)
            {
                if (settledState && outputClosed && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0 &&
                    playbackToken == null && rig != null && rig.Renderer != null)
                    rig.Renderer.SetMutationOwnerRequired(false);
                return;
            }
            if (settledState && outputClosed && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0 && playbackToken == null)
            {
                mutationOwnerRegistration.Dispose();
                mutationOwnerRegistration = null;
                if (rig != null && rig.Renderer != null) rig.Renderer.SetMutationOwnerRequired(false);
            }
        }

        internal bool TryGetEditorDisplayProfile(QuiltRenderer requestedRenderer, out BridgeDisplayProfile profile)
        {
            profile = default;
            if (rig == null || output == null) return false;
            bool configurationMatches = string.Equals(runtimePath ?? string.Empty, profileRuntimePathAtStart, StringComparison.Ordinal) &&
                                        string.Equals(applicationName ?? string.Empty, profileApplicationNameAtStart, StringComparison.Ordinal) &&
                                        displayOrdinal == profileDisplayOrdinalAtStart;
            bool outputConfigurationMatches = string.Equals(output.applicationName ?? string.Empty, profileApplicationNameAtStart, StringComparison.Ordinal) &&
                                              output.displayOrdinal == profileDisplayOrdinalAtStart;
            BridgeDisplayProfile current = output.Profile;
            if (output.RequiresReconnect || !IsEditorDisplayProfileCurrent(isActiveAndEnabled && State == SessionState.Rendering,
                    profileApplied, rig.Renderer == requestedRenderer, output.isActiveAndEnabled && output.Ready,
                    current, configurationMatches, outputConfigurationMatches) ||
                !string.IsNullOrEmpty(GetPhysicalOutputError(current))) return false;
            profile = current;
            return true;
        }

        internal static bool IsEditorDisplayProfileCurrent(bool activeSession, bool profileApplied,
            bool rendererMatches, bool workerReady, BridgeDisplayProfile profile, bool configurationMatches,
            bool outputConfigurationMatches)
        {
            return activeSession && profileApplied && rendererMatches && workerReady && profile.IsValid &&
                   configurationMatches && outputConfigurationMatches;
        }

        public static QuiltQualityProfile CreateQualityProfile(BridgeDisplayProfile profile, QuiltQualityPreset preset)
        {
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
            switch (preset)
            {
                case QuiltQualityPreset.Preview:
                    return QuiltQualityProfile.Preview(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                case QuiltQualityPreset.Balanced:
                    return QuiltQualityProfile.Balanced(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                case QuiltQualityPreset.Native:
                    return QuiltQualityProfile.Native(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                default:
                    return QuiltQualityProfile.Balanced(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
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
            InvalidatePlayback("Bridge session faulted while quilt playback was active.");
            SetError(message);
            if (rig != null) rig.AutoRender = false;
            if (output != null) output.Dispose();
            profileApplied = false;
            pendingStart = false;
            restartRequested = false;
            ResetCompletionWatchdog();
            State = SessionState.Fault;
            return false;
        }

        void PollCompletionWatchdog()
        {
            if (State != SessionState.Rendering || rig == null || !rig.AutoRender || output == null)
            {
                ResetCompletionWatchdog();
                return;
            }
            uint submissions = output.Submissions;
            uint completed = output.Completed;
            if (!completionWatchdogArmed)
            {
                if (submissions != watchdogSubmissions)
                {
                    watchdogSubmissions = submissions;
                    watchdogCompleted = completed;
                    completionProgressSince = Time.realtimeSinceStartup;
                    completionWatchdogArmed = true;
                }
                return;
            }
            if (completed != watchdogCompleted)
            {
                watchdogCompleted = completed;
                completionProgressSince = Time.realtimeSinceStartup;
            }
            if (submissions != watchdogSubmissions)
            {
                watchdogSubmissions = submissions;
            }
            if (completionWatchdogArmed && Time.realtimeSinceStartup - completionProgressSince > 10f)
            {
                Fail("Bridge worker completion timed out after 10 seconds");
            }
        }

        void ResetCompletionWatchdog()
        {
            completionWatchdogArmed = false;
            watchdogSubmissions = output == null ? 0u : output.Submissions;
            watchdogCompleted = output == null ? 0u : output.Completed;
            completionProgressSince = 0f;
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

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
