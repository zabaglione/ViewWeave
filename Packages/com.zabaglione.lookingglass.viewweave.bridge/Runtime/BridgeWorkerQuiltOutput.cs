using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ViewWeave
{
    [DisallowMultipleComponent]
    public sealed class BridgeWorkerQuiltOutput : MonoBehaviour, IQuiltOutput
    {
        static long nextSessionGeneration;
        long nextDisplayRefreshToken;
        string connectedSnapshotRuntimePath;
        string connectedSnapshotApplicationName;
        uint connectedSnapshotDisplayOrdinal;
        BridgeDisplaySnapshot connectedDisplaySnapshot;
#if UNITY_EDITOR
        [SerializeField, HideInInspector] BridgeDisplaySnapshotEditorCache editorConnectedDisplaySnapshotCache;
        bool restoringEditorSnapshotCache;
#endif
        public string runtimePath;
        public string applicationName = "ViewWeave";
        public uint displayOrdinal;
        public bool initializeOnEnable = true;
        [Range(0.01f, 4f)] public float zoom = 1f;

        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public BridgeDisplayProfile Profile { get; private set; }
        /// <summary>Returns the last successfully published snapshot only while this output's runtime, application, and display ordinal still match it and reconnection is not required.</summary>
        public BridgeDisplaySnapshot CurrentConfigurationDisplaySnapshot
        {
            get
            {
                if (ConnectedDisplaySnapshot == null || RequiresReconnect) return null;
                ViewWeaveBridgeSession session = GetComponent<ViewWeaveBridgeSession>();
                string currentRuntime = session == null ? runtimePath : session.runtimePath;
                string currentApplication = session == null ? applicationName : session.applicationName;
                uint currentOrdinal = session == null ? displayOrdinal : session.displayOrdinal;
                return IsConnectedSnapshotConfigurationCurrent(RequiresReconnect,
                       connectedSnapshotRuntimePath, connectedSnapshotApplicationName, connectedSnapshotDisplayOrdinal,
                       currentRuntime, currentApplication, currentOrdinal)
                    ? ConnectedDisplaySnapshot
                    : null;
            }
        }
        /// <summary>Returns the last snapshot result, even when this output's current configuration no longer matches it.</summary>
        public BridgeDisplaySnapshot ConnectedDisplaySnapshot
        {
            get
            {
#if UNITY_EDITOR
                TryRestoreEditorSnapshotCache();
#endif
                return connectedDisplaySnapshot;
            }
            private set => connectedDisplaySnapshot = value;
        }
        internal long LatestDisplayRefreshToken { get; private set; }
        public string DisplayRefreshStatus { get; private set; }
        public WorkerState State => state;
        public bool Ready => state == WorkerState.Ready;
        /// <summary>Raised on the main thread after an explicit connected-display refresh succeeds. This is not an automatic hotplug notification.</summary>
        public event Action<BridgeDisplaySnapshot> ConnectedDisplaySnapshotChanged;
        internal event Action<long, BridgeDisplaySnapshot, string> ConnectedDisplayRefreshCompleted;
        public bool RequiresReconnect { get; private set; }
        public bool TryResetTransfer()
        {
            if (state != WorkerState.Ready || handle == IntPtr.Zero) return false;
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out string imageGuardError))
            {
                if (!BridgeNativeImageGuard.IsValidationPending) LastError = imageGuardError;
                return false;
            }
#endif
            int result;
            try { result = BridgeWorkerNative.lkg_worker_reset_transfer(handle); }
            catch (EntryPointNotFoundException) { LastError = "Bridge worker reset transfer ABI entry point is missing"; return false; }
            catch (Exception error) { LastError = Sanitize(error.Message); return false; }
            if (result == -2) return false;
            if (result != 0) { LastError = "Bridge worker transfer reset failed (code " + result + ")"; return false; }
            if (command != null) { command.Release(); command = null; }
            ReleaseTransferTexture();
            return true;
        }

        public bool TrySetZoom(float value, out string error)
        {
            error = string.Empty;
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f)
            {
                error = "Bridge zoom must be finite and greater than zero.";
                return false;
            }
            QuiltRenderer renderer = GetComponent<QuiltRenderer>();
            if (renderer != null && !QuiltRendererMutationPolicy.TryCheck(renderer,
                    new QuiltRendererMutationRequest(QuiltRendererMutationKind.OutputZoom, value), out error)) return false;
            QuiltPhysicalFraming physical = renderer == null ? null : renderer.physicalFraming;
            if (physical != null && physical.enabled && value != 1f)
            {
                error = "Physical 1:1 framing requires Bridge zoom to be exactly 1.";
                return false;
            }
            zoom = value;
            return true;
        }
        public string LastError { get; private set; }
        public uint Submissions { get; private set; }
        /// <summary>Counts Submit calls passing readiness, handle, texture, and complete-grid checks; it does not imply queue submission or native completion.</summary>
        public uint SubmissionAttempts { get; private set; }
        public uint Completed => status.completed;
        public uint Dropped => status.dropped;
        public static int ShutdownPendingCount
        {
            get
            {
                int managed = BridgeWorkerReaper.PendingCount;
                if (Application.isEditor)
                {
                    if (!BridgeNativeImageGuard.TryValidateGlobalCalls(out bool loaded, out _, out _)) return Math.Max(1, managed);
                    if (!loaded) return managed;
                }
                try { return Math.Max(managed, checked((int)BridgeWorkerNative.lkg_worker_pending_count())); }
                catch (DllNotFoundException) { return managed; }
                catch (EntryPointNotFoundException) { return managed; }
            }
        }
        internal static bool HasNativeHandle
        {
            get
            {
                foreach (BridgeWorkerQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeWorkerQuiltOutput>())
                    if (output != null && output.handle != IntPtr.Zero) return true;
                return BridgeWorkerReaper.PendingCount != 0;
            }
        }
        internal static uint NativePendingCount
        {
            get
            {
                if (Application.isEditor)
                {
                    if (!BridgeNativeImageGuard.TryValidateGlobalCalls(out bool loaded, out _, out _)) return uint.MaxValue;
                    if (!loaded) return 0;
                }
                return BridgeWorkerNative.lkg_worker_pending_count();
            }
        }
        internal static int CloseAllNativeWorkers()
        {
            if (Application.isEditor && (!BridgeNativeImageGuard.TryValidateGlobalCalls(out bool loaded, out _, out _) || !loaded)) return -1;
            return BridgeWorkerNative.lkg_worker_close_all();
        }
        internal static int CollectClosedNativeWorkers()
        {
            if (Application.isEditor && (!BridgeNativeImageGuard.TryValidateGlobalCalls(out bool loaded, out _, out _) || !loaded)) return -1;
            return BridgeWorkerNative.lkg_worker_collect_closed();
        }
        public string TransferGraphicsFormat => transferGraphicsFormatName ?? string.Empty;
        public string TransferFormat => transferFormatName ?? string.Empty;
        public bool TransferIsSrgb => transferSrgbDiagnostic;
        public int TransferAntiAliasing => transferAntiAliasingDiagnostic;
        public string TransferDimension => transferDimensionName ?? string.Empty;

        public enum WorkerState { Closed, Connecting, Ready, Fault, Closing }
        WorkerState state;
        IntPtr handle;
        IntPtr transferNativePtr;
        RenderTexture transferTexture;
        bool transferSrgb;
        string transferGraphicsFormatName;
        string transferFormatName;
        bool transferSrgbDiagnostic;
        int transferAntiAliasingDiagnostic;
        string transferDimensionName;
        CommandBuffer command;
        int workerRenderEventId;
        BridgeWorkerNative.Status status;
        BridgeWorkerNative.PhysicalDisplayInfoV1 physicalDisplay;
        bool physicalDisplayRead;
        object playbackToken;
#if UNITY_EDITOR
        object editorPreviewOwner;
        BridgeEditorPreviewRetentionRecord editorPreviewRetentionRecord;
        bool editorPreviewSubmissionsPaused;
#endif
        bool disposalInProgress;
        long sessionGeneration;
        long lastReaperHandoffGeneration;
        BridgeNativeModuleIdentity nativeModuleIdentity;
        internal IntPtr NativeHandle => handle;
        internal BridgeNativeModuleIdentity NativeModuleIdentity => nativeModuleIdentity;
        public long SessionGeneration => sessionGeneration;
        public long LastReaperHandoffGeneration => lastReaperHandoffGeneration;
        public event Action<long> PlaybackReaperHandoff;

        void OnEnable()
        {
            if (initializeOnEnable) TryBegin();
        }

        void Update()
        {
            BridgeDisplayDiscovery.Tick();
            Poll();
        }

        public bool TryRequestDisplayRefresh(out string error)
        {
            error = string.Empty;
            if (state == WorkerState.Connecting || state == WorkerState.Closing || state == WorkerState.Fault)
            {
                error = "Connected displays cannot refresh while the Bridge worker is connecting, stopping, or recovering.";
                DisplayRefreshStatus = error;
                return false;
            }
            if (BridgeDisplayDiscovery.IsBusy)
            {
                error = BridgeDisplayDiscovery.Status;
                if (string.IsNullOrEmpty(error)) error = "A connected display refresh or Bridge reconfiguration is already in progress.";
                DisplayRefreshStatus = error;
                return false;
            }
            ViewWeaveBridgeSession session = GetComponent<ViewWeaveBridgeSession>();
            if (session != null && (session.State == ViewWeaveBridgeSession.SessionState.Connecting ||
                session.State == ViewWeaveBridgeSession.SessionState.Reconfiguring ||
                session.State == ViewWeaveBridgeSession.SessionState.Closing ||
                session.State == ViewWeaveBridgeSession.SessionState.Fault))
            {
                error = "Connected displays cannot refresh while the Session is connecting, reconfiguring, stopping, or recovering.";
                DisplayRefreshStatus = error;
                return false;
            }
            string configuredRuntimePath = session == null ? runtimePath : session.runtimePath;
            string configuredApplicationName = session == null ? applicationName : session.applicationName;
            uint configuredDisplayOrdinal = session == null ? displayOrdinal : session.displayOrdinal;
            if (state == WorkerState.Ready && session != null)
            {
                if (displayOrdinal != configuredDisplayOrdinal || !string.Equals(applicationName ?? string.Empty,
                        configuredApplicationName ?? string.Empty, StringComparison.Ordinal) ||
                    !BridgeRuntimeLocator.TryResolve(configuredRuntimePath, out string resolvedSessionRuntime, out error) ||
                    !PathsEqual(resolvedSessionRuntime, runtimePath))
                {
                    if (string.IsNullOrEmpty(error))
                        error = "The live Session configuration differs from the active Bridge worker. Stop and reconnect before refreshing displays.";
                    DisplayRefreshStatus = error;
                    return false;
                }
            }
            uint requestedDisplayOrdinal = state == WorkerState.Ready ? displayOrdinal : configuredDisplayOrdinal;
            long requestToken = ++nextDisplayRefreshToken;
            LatestDisplayRefreshToken = requestToken;

            long requestedSessionGeneration = state == WorkerState.Ready ? sessionGeneration : 0;
            IntPtr requestedHandle = state == WorkerState.Ready ? handle : IntPtr.Zero;
            Action<BridgeDisplaySnapshot> onSuccess = snapshot =>
            {
                if (this == null || !isActiveAndEnabled || requestToken != LatestDisplayRefreshToken ||
                    (requestedSessionGeneration != 0 &&
                     (sessionGeneration != requestedSessionGeneration || handle != requestedHandle || state != WorkerState.Ready))) return;
                if (!IsCurrentDisplayRefreshConfiguration(configuredRuntimePath, configuredApplicationName,
                        configuredDisplayOrdinal, requestedSessionGeneration != 0))
                {
                    const string staleMessage = "Connected display metadata was discarded because the Session or display selection changed while refreshing.";
                    DisplayRefreshStatus = staleMessage;
                    try { ConnectedDisplayRefreshCompleted?.Invoke(requestToken, null, staleMessage); }
                    catch { }
                    return;
                }
                ConnectedDisplaySnapshot = snapshot;
                connectedSnapshotRuntimePath = configuredRuntimePath ?? string.Empty;
                connectedSnapshotApplicationName = configuredApplicationName ?? string.Empty;
                connectedSnapshotDisplayOrdinal = configuredDisplayOrdinal;
                if (!TryRefreshMatchesCurrentProfile(snapshot, requestedDisplayOrdinal, out string profileError))
                {
                    RequiresReconnect = true;
#if UNITY_EDITOR
                    editorConnectedDisplaySnapshotCache = null;
#endif
                    DisplayRefreshStatus = profileError;
                }
                else
                {
#if UNITY_EDITOR
                    editorConnectedDisplaySnapshotCache = BridgeDisplaySnapshotEditorCache.TryCreate(this, snapshot,
                        RequiresReconnect, configuredRuntimePath, configuredApplicationName, configuredDisplayOrdinal);
#endif
                    DisplayRefreshStatus = snapshot == null ? "Connected display refresh returned no snapshot." :
                        "Connected displays refreshed: " + snapshot.Displays.Count.ToString(CultureInfo.InvariantCulture) + " found.";
                }
                try { ConnectedDisplaySnapshotChanged?.Invoke(snapshot); }
                catch { }
                try { ConnectedDisplayRefreshCompleted?.Invoke(requestToken, snapshot, string.Empty); }
                catch { }
            };
            Action<string> onFailure = message =>
            {
                if (this == null || !isActiveAndEnabled || requestToken != LatestDisplayRefreshToken ||
                    (requestedSessionGeneration != 0 &&
                     (sessionGeneration != requestedSessionGeneration || handle != requestedHandle || state != WorkerState.Ready))) return;
                DisplayRefreshStatus = message ?? "Connected display refresh failed.";
                try { ConnectedDisplayRefreshCompleted?.Invoke(requestToken, null, DisplayRefreshStatus); }
                catch { }
            };

            bool requested;
            if (state == WorkerState.Ready && handle != IntPtr.Zero)
            {
                requested = BridgeDisplayDiscovery.TryRequestRefresh(handle, nativeModuleIdentity, displayOrdinal,
                    requestedSessionGeneration, this, onSuccess, onFailure, out error);
            }
            else
            {
                if (BridgeWorkerQuiltOutput.HasNativeHandle)
                {
                    error = "Another Bridge worker is active. Refresh through its owning preview or Session.";
                    DisplayRefreshStatus = error;
                    return false;
                }
                if (!BridgeRuntimeLocator.TryResolve(configuredRuntimePath, out string resolvedRuntime, out error))
                {
                    DisplayRefreshStatus = error;
                    return false;
                }
                requested = BridgeDisplayDiscovery.TryRequest(resolvedRuntime, configuredApplicationName, configuredDisplayOrdinal,
                    this, onSuccess, onFailure, out error);
            }
            DisplayRefreshStatus = requested ? "Refreshing connected display list..." : error;
            return requested;
        }

#if UNITY_EDITOR
        void TryRestoreEditorSnapshotCache()
        {
            if (restoringEditorSnapshotCache || connectedDisplaySnapshot != null ||
                editorConnectedDisplaySnapshotCache == null || Application.isPlaying) return;
            restoringEditorSnapshotCache = true;
            try
            {
                ViewWeaveBridgeSession session = GetComponent<ViewWeaveBridgeSession>();
                string currentRuntime = session == null ? runtimePath : session.runtimePath;
                string currentApplication = session == null ? applicationName : session.applicationName;
                uint currentOrdinal = session == null ? displayOrdinal : session.displayOrdinal;
                if (!editorConnectedDisplaySnapshotCache.TryRestore(this, RequiresReconnect,
                        currentRuntime, currentApplication, currentOrdinal, out BridgeDisplaySnapshot restored))
                {
                    editorConnectedDisplaySnapshotCache = null;
                    return;
                }
                connectedDisplaySnapshot = restored;
                connectedSnapshotRuntimePath = currentRuntime ?? string.Empty;
                connectedSnapshotApplicationName = currentApplication ?? string.Empty;
                connectedSnapshotDisplayOrdinal = currentOrdinal;
            }
            finally { restoringEditorSnapshotCache = false; }
        }
#endif

        bool IsCurrentDisplayRefreshConfiguration(string requestedRuntimePath, string requestedApplicationName,
            uint requestedOrdinal, bool activeWorkerRefresh)
        {
            ViewWeaveBridgeSession session = GetComponent<ViewWeaveBridgeSession>();
            string currentRuntime = session == null ? runtimePath : session.runtimePath;
            string currentApplication = session == null ? applicationName : session.applicationName;
            uint currentOrdinal = session == null ? displayOrdinal : session.displayOrdinal;
            if (activeWorkerRefresh && (state != WorkerState.Ready || handle == IntPtr.Zero)) return false;
            return string.Equals(currentRuntime ?? string.Empty, requestedRuntimePath ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(currentApplication ?? string.Empty, requestedApplicationName ?? string.Empty, StringComparison.Ordinal) &&
                   currentOrdinal == requestedOrdinal;
        }

        static bool PathsEqual(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Replace('/', '\\').TrimEnd('\\'),
                (right ?? string.Empty).Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        internal bool TryRefreshMatchesCurrentProfile(BridgeDisplaySnapshot snapshot, uint requestedDisplayOrdinal, out string error)
        {
            error = string.Empty;
            if (state != WorkerState.Ready || handle == IntPtr.Zero) return true;
            return TryMatchRefreshedDisplay(Profile, displayOrdinal, requestedDisplayOrdinal, snapshot, out error);
        }

        internal static bool IsConnectedSnapshotConfigurationCurrent(bool requiresReconnect,
            string snapshotRuntimePath, string snapshotApplicationName, uint snapshotDisplayOrdinal,
            string currentRuntimePath, string currentApplicationName, uint currentDisplayOrdinal)
        {
            return !requiresReconnect &&
                   string.Equals(snapshotRuntimePath ?? string.Empty, currentRuntimePath ?? string.Empty, StringComparison.Ordinal) &&
                   string.Equals(snapshotApplicationName ?? string.Empty, currentApplicationName ?? string.Empty, StringComparison.Ordinal) &&
                   snapshotDisplayOrdinal == currentDisplayOrdinal;
        }

        internal static bool TryMatchRefreshedDisplay(BridgeDisplayProfile activeProfile, uint currentDisplayOrdinal,
            uint requestedDisplayOrdinal, BridgeDisplaySnapshot snapshot, out string error)
        {
            error = string.Empty;
            if (currentDisplayOrdinal != requestedDisplayOrdinal)
            {
                error = "The selected display ordinal changed during refresh. Stop and reconnect before rendering.";
                return false;
            }
            if (snapshot == null || !snapshot.TryGetDisplay(requestedDisplayOrdinal, out BridgeDisplayProfile refreshed))
            {
                error = "The selected Looking Glass is absent from the latest display refresh. Stop and reconnect to refresh its profile.";
                return false;
            }
            if (!ProfilesEquivalent(activeProfile, refreshed))
            {
                error = "The selected Looking Glass profile changed during display refresh. Stop and reconnect before rendering again.";
                return false;
            }
            return true;
        }

        static bool ProfilesEquivalent(BridgeDisplayProfile left, BridgeDisplayProfile right)
        {
            return left.IsValid && right.IsValid && left.DisplayIndex == right.DisplayIndex && left.Width == right.Width &&
                   left.Height == right.Height && NearlyEqual(left.Aspect, right.Aspect) && NearlyEqual(left.Viewcone, right.Viewcone) &&
                   left.QuiltWidth == right.QuiltWidth && left.QuiltHeight == right.QuiltHeight &&
                   left.Columns == right.Columns && left.Rows == right.Rows &&
                   left.HasPhysicalCalibration == right.HasPhysicalCalibration &&
                   (!left.HasPhysicalCalibration || NearlyEqual(left.PhysicalWidthMillimeters, right.PhysicalWidthMillimeters) &&
                    NearlyEqual(left.PhysicalHeightMillimeters, right.PhysicalHeightMillimeters) && NearlyEqual(left.CalibrationDpi, right.CalibrationDpi));
        }

        static bool NearlyEqual(float left, float right) => Mathf.Abs(left - right) <= 0.0001f;

        public bool TryBegin()
        {
            return TryBeginCore(BridgeGraphicsBackendRoute.Worker);
        }

#if UNITY_EDITOR
        internal bool TryBeginEditorPreview(object owner, BridgeEditorPreviewRetentionRecord retentionRecord, out string error)
        {
            error = string.Empty;
            if (owner == null || retentionRecord == null) { error = "An Editor preview owner and retention record are required."; return false; }
            if (editorPreviewOwner != null || playbackToken != null) { error = "Bridge output is already owned."; return false; }
            editorPreviewRetentionRecord = retentionRecord;
            editorPreviewSubmissionsPaused = false;
            if (!TryBeginCore(BridgeGraphicsBackendRoute.EditorPreview))
            {
                error = LastError;
                if (editorPreviewRetentionRecord.WorkerRecoveryRecord == null)
                {
                    editorPreviewRetentionRecord.SetWorkerProofExpected(false);
                    editorPreviewRetentionRecord = null;
                }
                return false;
            }
            editorPreviewOwner = owner;
            return true;
        }

        internal bool TrySubmitEditorPreview(object owner, RenderTexture quiltTexture, QuiltLayout layout)
        {
            if (owner == null || !ReferenceEquals(editorPreviewOwner, owner) || playbackToken != null || editorPreviewSubmissionsPaused) return false;
            return SubmitCore(quiltTexture, layout);
        }

        internal bool TrySetEditorPreviewPaused(object owner, bool paused, out string error)
        {
            error = string.Empty;
            if (owner == null || !ReferenceEquals(editorPreviewOwner, owner) || playbackToken != null)
            {
                error = "The active Editor preview does not own this Bridge output.";
                return false;
            }
            if (state != WorkerState.Ready || handle == IntPtr.Zero)
            {
                error = "The Bridge worker must be Ready before pausing or resuming the Editor preview.";
                return false;
            }
            if (paused) editorPreviewSubmissionsPaused = true;
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out string imageGuardError))
            {
                error = imageGuardError;
                return false;
            }
#endif
            try
            {
                int result = BridgeWorkerNative.lkg_worker_set_preview_paused(handle, paused ? 1u : 0u);
                if (result != 0)
                {
                    error = "Bridge worker rejected the Editor preview pause request (code " + result.ToString(CultureInfo.InvariantCulture) + ").";
                    return false;
                }
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                error = "The loaded Bridge native library does not support reusable preview pause. Restart the Unity Editor after updating the package.";
                return false;
            }
            catch (DllNotFoundException)
            {
                error = "Bridge worker native library is missing.";
                return false;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.Message);
                return false;
            }
        }

        internal bool EditorPreviewImageValidationPending => BridgeNativeImageGuard.IsValidationPending;

        internal bool TryGetEditorPreviewPausePhase(object owner, out EditorPreviewPausePhase phase, out string error)
        {
            phase = EditorPreviewPausePhase.Faulted;
            error = string.Empty;
            if (owner == null || !ReferenceEquals(editorPreviewOwner, owner) || playbackToken != null || handle == IntPtr.Zero)
            {
                error = "The active Editor preview does not own a live Bridge worker.";
                return false;
            }
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out string imageGuardError))
            {
                error = imageGuardError;
                return false;
            }
#endif
            try
            {
                if (BridgeWorkerNative.lkg_worker_get_preview_pause_state(handle, out uint nativePhase) != 0)
                {
                    error = "Bridge worker could not report the Editor preview pause state.";
                    return false;
                }
                if (nativePhase > (uint)EditorPreviewPausePhase.Faulted)
                {
                    error = "Bridge worker returned an invalid Editor preview pause state.";
                    return false;
                }
                phase = (EditorPreviewPausePhase)nativePhase;
                return true;
            }
            catch (EntryPointNotFoundException)
            {
                error = "The loaded Bridge native library does not support reusable preview pause. Restart the Unity Editor after updating the package.";
                return false;
            }
            catch (DllNotFoundException)
            {
                error = "Bridge worker native library is missing.";
                return false;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.Message);
                return false;
            }
        }

        internal void CompleteEditorPreviewResume(object owner)
        {
            if (owner != null && ReferenceEquals(editorPreviewOwner, owner)) editorPreviewSubmissionsPaused = false;
        }

        internal bool EndEditorPreview(object owner, BridgeEditorPreviewRetentionRecord retentionRecord)
        {
            if (owner == null || retentionRecord == null || !ReferenceEquals(editorPreviewOwner, owner) || playbackToken != null ||
                !ReferenceEquals(editorPreviewRetentionRecord, retentionRecord)) return false;
            Dispose();
            editorPreviewOwner = null;
            editorPreviewSubmissionsPaused = false;
            return retentionRecord.WorkerHandoffComplete;
        }

        internal enum EditorPreviewPausePhase : uint { Running = 0, Pausing = 1, Paused = 2, Resuming = 3, Faulted = 4 }
#endif

        bool TryBeginCore(BridgeGraphicsBackendRoute route)
        {
            if (!isActiveAndEnabled) return Fail("Bridge worker must be active before begin");
            if (BridgeDisplayDiscovery.IsBusy)
            {
                LastError = "Bridge display metadata is still refreshing or cleaning up. Wait before starting a worker.";
                return false;
            }
            if (handle != IntPtr.Zero || state == WorkerState.Connecting || state == WorkerState.Ready || state == WorkerState.Closing)
            {
                LastError = "Bridge worker is already active";
                return false;
            }
            if (BridgeWorkerLifecycle.RecoveryPending)
                return Fail("Bridge worker shutdown recovery is still pending");
            if (!BridgeGraphicsBackendPolicy.TrySelect(
                route,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                SystemInfo.graphicsDeviceType,
                SystemInfo.renderingThreadingMode,
                out BridgeGraphicsBackendDecision backend,
                out string backendError))
                return Fail(backendError);
            if (backend.BridgeConsumer != BridgeGraphicsApi.Direct3D11)
                return Fail("Bridge worker requires the Direct3D11 Bridge consumer route");
            if (string.IsNullOrWhiteSpace(runtimePath))
                return Fail("Set an absolute Bridge worker runtime path");
            status = default;
            physicalDisplay = new BridgeWorkerNative.PhysicalDisplayInfoV1 { structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.PhysicalDisplayInfoV1)), version = 1 };
            physicalDisplayRead = false;
            Profile = default;
            RequiresReconnect = false;
            Submissions = 0;
            SubmissionAttempts = 0;
            transferGraphicsFormatName = string.Empty;
            transferFormatName = string.Empty;
            transferSrgbDiagnostic = false;
            transferAntiAliasingDiagnostic = 0;
            transferDimensionName = string.Empty;
            nativeModuleIdentity = default;
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryInspectEditorProcess(out string imageGuardError)) return Fail(imageGuardError);
#endif
            try
            {
                int eventIdResult = BridgeWorkerNative.lkg_worker_render_event_id(out workerRenderEventId);
                if (eventIdResult != 0)
                {
                    string message = "Bridge worker render event ID is unavailable (code " + eventIdResult.ToString(CultureInfo.InvariantCulture) + ").";
                    if (Application.isEditor)
                        message += " If this followed a package update, restart the Unity Editor and retry.";
                    return Fail(message);
                }
            }
            catch (DllNotFoundException) { return Fail("Bridge worker native library is missing"); }
            catch (EntryPointNotFoundException) { return Fail("Bridge worker event ID ABI entry point is missing"); }
            catch (Exception error) { return Fail(Sanitize(error.Message)); }
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryCaptureCurrentIdentity(out nativeModuleIdentity, out imageGuardError)) return Fail(imageGuardError);
#endif
            try
            {
#if UNITY_EDITOR
                if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out imageGuardError)) return Fail(imageGuardError);
#endif
                handle = BridgeWorkerNative.lkg_worker_begin(runtimePath, applicationName, displayOrdinal, IntPtr.Zero);
            }
            catch (DllNotFoundException) { return Fail("Bridge worker native library is missing"); }
            catch (EntryPointNotFoundException) { return Fail("Bridge worker ABI entry point is missing"); }
            catch (Exception error) { return Fail(Sanitize(error.Message)); }
            if (handle == IntPtr.Zero) return Fail("Bridge worker begin failed");
            sessionGeneration = ++nextSessionGeneration;
            state = WorkerState.Connecting;
            LastError = string.Empty;
            return true;
        }

        public void Poll()
        {
            if (handle == IntPtr.Zero || state == WorkerState.Closed) return;
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out string imageGuardError))
            {
                if (BridgeNativeImageGuard.IsValidationPending) return;
                LastError = imageGuardError;
                TransferOwnedResourcesToReaper(true);
                return;
            }
#endif
            try
            {
                if (BridgeWorkerNative.lkg_worker_poll(handle, out status) != 0)
                {
                    FailFromStatus("Bridge worker poll failed");
                    return;
                }
                switch (status.state)
                {
                    case 0: state = WorkerState.Connecting; break;
                    case 1: state = WorkerState.Ready; break;
                    case 2: state = WorkerState.Fault; break;
                    case 3: state = WorkerState.Closing; break;
                    case 4:
                        TransferOwnedResourcesToReaper(false);
                        break;
                    default: Fail("Bridge worker returned an invalid state"); break;
                }
                if (state == WorkerState.Ready)
                {
                    if (!IsValidStatus(status)) { Fail("Bridge worker returned invalid display geometry"); return; }
                    var info = new BridgeNative.DisplayInfo
                    {
                        index = status.displayIndex, width = status.width, height = status.height,
                        aspect = status.aspect, viewcone = status.viewcone, quiltWidth = status.quiltWidth,
                        quiltHeight = status.quiltHeight, quiltColumns = status.quiltColumns, quiltRows = status.quiltRows
                    };
                    if (!physicalDisplayRead)
                    {
                        physicalDisplayRead = true;
                        try
                        {
                            BridgeWorkerNative.PhysicalDisplayInfoV1 calibration = physicalDisplay;
                            if (BridgeWorkerNative.lkg_worker_get_physical_display_v1(handle, ref calibration) == 0)
                                physicalDisplay = calibration;
                        }
                        catch (DllNotFoundException) { }
                        catch (EntryPointNotFoundException) { }
                    }
                    Profile = new BridgeDisplayProfile(info, physicalDisplay);
                }
                if (state == WorkerState.Fault) FailFromStatus("Bridge worker fault");
                if (state == WorkerState.Closed) ReleaseOwnedResources();
            }
            catch (DllNotFoundException) { Fail("Bridge worker native library is missing"); }
            catch (EntryPointNotFoundException) { Fail("Bridge worker ABI entry point is missing"); }
            catch (Exception error) { Fail(Sanitize(error.Message)); }
        }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            if (playbackToken != null) return;
#if UNITY_EDITOR
            if (editorPreviewOwner != null) return;
#endif
            SubmitCore(quiltTexture, layout);
        }

        internal bool TryBeginPlaybackOutput(object token, out string error)
        {
            error = string.Empty;
            if (token == null) { error = "A playback ownership token is required."; return false; }
            if (playbackToken != null) { error = "The Bridge output is already owned by playback."; return false; }
            if (state != WorkerState.Ready || handle == IntPtr.Zero) { error = "Playback requires the active Bridge output to be Ready."; return false; }
            if (!string.IsNullOrEmpty(LastError)) { error = "Bridge output has an unresolved error: " + LastError; return false; }
            playbackToken = token;
            return true;
        }

        internal bool TrySubmitPlayback(object token, RenderTexture quiltTexture, QuiltLayout layout)
        {
            if (token == null || !ReferenceEquals(playbackToken, token)) return false;
            return SubmitCore(quiltTexture, layout);
        }

        internal bool TryResetTransferForPlayback(object token)
        {
            if (token == null || !ReferenceEquals(playbackToken, token)) return false;
            return TryResetTransfer();
        }

        internal void ClearPlaybackTexture(object token, RenderTexture expected)
        {
            if (token == null || !ReferenceEquals(playbackToken, token)) return;
            if (ReferenceEquals(QuiltTexture, expected)) QuiltTexture = null;
        }

        internal void EndPlaybackOutput(object token)
        {
            if (token == null || !ReferenceEquals(playbackToken, token)) return;
            playbackToken = null;
        }

        internal bool HasReaperHandoff(long generation)
        {
            return generation != 0 && lastReaperHandoffGeneration == generation;
        }

        bool SubmitCore(RenderTexture quiltTexture, QuiltLayout layout)
        {
            QuiltTexture = quiltTexture;
            Layout = layout;
            if (state != WorkerState.Ready || handle == IntPtr.Zero) return false;
            if (RequiresReconnect)
            {
                LastError = string.IsNullOrEmpty(DisplayRefreshStatus)
                    ? "The selected Looking Glass profile changed. Stop and reconnect before rendering."
                    : DisplayRefreshStatus;
                return false;
            }
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out string imageGuardError))
            {
                if (BridgeNativeImageGuard.IsValidationPending) return false;
                LastError = imageGuardError;
                Dispose();
                return false;
            }
#endif
            if (quiltTexture == null || layout.Columns <= 0 || layout.Rows <= 0 || layout.ViewCount != layout.Columns * layout.Rows)
            {
                LastError = "Bridge worker requires a complete quilt grid";
                return false;
            }
            SubmissionAttempts++;
            if (!EnsureTransferTexture(quiltTexture)) return false;
            RenderTexture previous = RenderTexture.active;
            try { Graphics.Blit(quiltTexture, transferTexture, new Vector2(1f, -1f), new Vector2(0f, 1f)); }
            finally { RenderTexture.active = previous; }
            IntPtr drawData;
            try { drawData = BridgeWorkerNative.lkg_worker_make_draw_data(handle, transferNativePtr, (uint)layout.Columns, (uint)layout.Rows, layout.AspectRatio, zoom); }
            catch (Exception error) { LastError = Sanitize(error.Message); return false; }
            if (drawData == IntPtr.Zero) return false;
            bool executeAttempted = false;
            try
            {
                if (command == null) command = new CommandBuffer { name = "ViewWeave Bridge Worker Draw" };
                command.Clear();
                command.IssuePluginEventAndData(BridgeWorkerNative.lkg_worker_render_event_func(), workerRenderEventId, drawData);
                executeAttempted = true;
                Graphics.ExecuteCommandBuffer(command);
                Submissions++;
                return true;
            }
            catch (Exception error)
            {
                if (!executeAttempted) BridgeWorkerNative.lkg_worker_cancel_draw_data(drawData);
                LastError = Sanitize(error.Message);
                if (executeAttempted) Dispose();
                return false;
            }
        }

        public void Dispose()
        {
            if (disposalInProgress) return;
            disposalInProgress = true;
            try
            {
                NotifyDisplayRefreshCancelled("Connected display refresh was cancelled because its Bridge output stopped.");
                BridgeDisplayDiscovery.Cancel(this);
                if (handle == IntPtr.Zero) { ReleaseOwnedResources(); return; }
                bool needsClose = false;
#if UNITY_EDITOR
                if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(nativeModuleIdentity, out string imageGuardError))
                {
                    LastError = imageGuardError;
                    TransferOwnedResourcesToReaper(true);
                    return;
                }
#endif
                try
                {
                    int closeResult = BridgeWorkerNative.lkg_worker_close(handle);
                    needsClose = closeResult != 0;
                    if (needsClose) LastError = "Bridge worker close pending (code " + closeResult + ")";
                }
                catch (Exception error)
                {
                    LastError = Sanitize(error.Message);
                    needsClose = true;
                }
                TransferOwnedResourcesToReaper(needsClose);
            }
            finally { disposalInProgress = false; }
        }

        void NotifyDisplayRefreshCancelled(string reason)
        {
            long token = LatestDisplayRefreshToken;
            if (token == 0 || ConnectedDisplayRefreshCompleted == null) return;
            try { ConnectedDisplayRefreshCompleted.Invoke(token, null, reason); }
            catch { }
        }

        void TransferOwnedResourcesToReaper(bool needsClose)
        {
#if UNITY_EDITOR
            if (editorPreviewRetentionRecord != null)
            {
                TransferOwnedResourcesToReaper(needsClose, BridgeWorkerReaper.EnqueueEditorPreview, editorPreviewRetentionRecord);
                return;
            }
#endif
            TransferOwnedResourcesToReaper(needsClose, BridgeWorkerReaper.Enqueue);
        }

#if UNITY_EDITOR
        void TransferOwnedResourcesToReaper(bool needsClose, Action<IntPtr, RenderTexture, CommandBuffer, bool, BridgeNativeModuleIdentity, BridgeEditorPreviewRetentionRecord> enqueue,
            BridgeEditorPreviewRetentionRecord previewOwner)
        {
            if (enqueue == null) throw new ArgumentNullException(nameof(enqueue));
            if (handle == IntPtr.Zero) return;
            IntPtr ownedHandle = handle;
            RenderTexture ownedTexture = transferTexture;
            CommandBuffer ownedCommand = command;
            handle = IntPtr.Zero;
            transferTexture = null;
            transferNativePtr = IntPtr.Zero;
            transferSrgb = false;
            command = null;
            state = WorkerState.Closed;
            enqueue(ownedHandle, ownedTexture, ownedCommand, needsClose, nativeModuleIdentity, previewOwner);
            RecordReaperHandoff(sessionGeneration);
            BridgeDisplayDiscovery.NotifyWorkerCleanupHandedOff(this, ownedHandle, sessionGeneration);
        }
#endif

        internal void TransferOwnedResourcesToReaper(bool needsClose, Action<IntPtr, RenderTexture, CommandBuffer, bool, BridgeNativeModuleIdentity> enqueue)
        {
            if (enqueue == null) throw new ArgumentNullException(nameof(enqueue));
            if (handle == IntPtr.Zero) return;
            IntPtr ownedHandle = handle;
            RenderTexture ownedTexture = transferTexture;
            CommandBuffer ownedCommand = command;
            handle = IntPtr.Zero;
            transferTexture = null;
            transferNativePtr = IntPtr.Zero;
            transferSrgb = false;
            command = null;
            state = WorkerState.Closed;
            enqueue(ownedHandle, ownedTexture, ownedCommand, needsClose, nativeModuleIdentity);
            RecordReaperHandoff(sessionGeneration);
            BridgeDisplayDiscovery.NotifyWorkerCleanupHandedOff(this, ownedHandle, sessionGeneration);
        }

        void OnDisable() { Dispose(); }
        void OnDestroy() { Dispose(); }

        bool EnsureTransferTexture(RenderTexture source)
        {
            if (source.width <= 0 || source.height <= 0) { LastError = "Bridge worker received invalid quilt size"; return false; }
            if (transferTexture != null && (transferTexture.width != source.width || transferTexture.height != source.height || transferSrgb != source.sRGB))
            {
                LastError = "Bridge worker transfer size changed; reopen the worker";
                Dispose();
                return false;
            }
            if (transferTexture == null)
            {
                transferSrgb = source.sRGB;
                GraphicsFormat transferFormat = source.sRGB ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm;
                if (!SystemInfo.IsFormatSupported(transferFormat, FormatUsage.Render))
                {
                    LastError = "Bridge worker RGBA8 render format is unsupported";
                    Dispose();
                    return false;
                }
                RenderTextureDescriptor descriptor = new RenderTextureDescriptor(source.width, source.height, transferFormat, 0)
                {
                    msaaSamples = 1,
                    mipCount = 1,
                    volumeDepth = 1,
                    dimension = TextureDimension.Tex2D,
                    useMipMap = false,
                    autoGenerateMips = false,
                    sRGB = source.sRGB
                };
                transferTexture = new RenderTexture(descriptor) { name = "BridgeWorkerQuiltTransfer", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                transferTexture.Create();
                transferNativePtr = transferTexture.IsCreated() ? transferTexture.GetNativeTexturePtr() : IntPtr.Zero;
            }
            if (!transferTexture.IsCreated() || transferNativePtr == IntPtr.Zero || transferTexture.graphicsFormat != (source.sRGB ? GraphicsFormat.R8G8B8A8_SRGB : GraphicsFormat.R8G8B8A8_UNorm) || transferTexture.antiAliasing != 1 || transferTexture.dimension != TextureDimension.Tex2D)
            {
                LastError = "Bridge worker transfer texture does not match RGBA8 requirements";
                Dispose();
                return false;
            }
            transferGraphicsFormatName = transferTexture.graphicsFormat.ToString();
            transferFormatName = transferTexture.format.ToString();
            transferSrgbDiagnostic = transferTexture.sRGB;
            transferAntiAliasingDiagnostic = transferTexture.antiAliasing;
            transferDimensionName = transferTexture.dimension.ToString();
            return true;
        }

        bool Fail(string message) { LastError = message; state = WorkerState.Fault; if (handle != IntPtr.Zero) Dispose(); return false; }
        void FailFromStatus(string fallback) { LastError = string.IsNullOrEmpty(status.error) ? fallback : Sanitize(status.error); state = WorkerState.Fault; Dispose(); }
        static bool IsValidStatus(BridgeWorkerNative.Status value)
        {
            long cells = (long)value.quiltColumns * value.quiltRows;
            return value.width > 0 && value.height > 0 && value.quiltWidth > 0 && value.quiltHeight > 0 &&
                value.quiltColumns > 0 && value.quiltRows > 0 && cells <= int.MaxValue &&
                value.quiltWidth / value.quiltColumns >= 1 && value.quiltHeight / value.quiltRows >= 1 &&
                value.quiltWidth <= SystemInfo.maxTextureSize && value.quiltHeight <= SystemInfo.maxTextureSize &&
                value.aspect > 0f && !float.IsNaN(value.aspect) && !float.IsInfinity(value.aspect) &&
                value.viewcone >= 0f && value.viewcone < 179f && !float.IsNaN(value.viewcone) && !float.IsInfinity(value.viewcone);
        }
        void ReleaseOwnedResources()
        {
            if (command != null) { command.Release(); command = null; }
            ReleaseTransferTexture();
            state = WorkerState.Closed;
        }
        void ReleaseTransferTexture()
        {
            if (transferTexture == null) return;
            if (RenderTexture.active == transferTexture) RenderTexture.active = null;
            if (transferTexture.IsCreated()) transferTexture.Release();
            if (Application.isPlaying) Destroy(transferTexture); else DestroyImmediate(transferTexture);
            transferTexture = null; transferNativePtr = IntPtr.Zero; transferSrgb = false;
        }

        void RecordReaperHandoff(long generation)
        {
            if (generation == 0) return;
            lastReaperHandoffGeneration = generation;
            Action<long> handler = PlaybackReaperHandoff;
            if (handler == null) return;
            foreach (Action<long> callback in handler.GetInvocationList())
            {
                try { callback(generation); }
                catch (Exception error) { Debug.LogError("[BridgeWorker] Playback handoff notification failed: " + Sanitize(error.Message)); }
            }
        }
        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c);
            return new string(chars.ToArray());
        }
    }
}
