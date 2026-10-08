using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace ViewWeave
{
    [DisallowMultipleComponent]
    public sealed class BridgeWorkerQuiltOutput : MonoBehaviour, IQuiltOutput
    {
        static long nextSessionGeneration;
        public string runtimePath;
        public string applicationName = "ViewWeave";
        public uint displayOrdinal;
        public bool initializeOnEnable = true;
        [Range(0.01f, 4f)] public float zoom = 1f;

        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public BridgeDisplayProfile Profile { get; private set; }
        public WorkerState State => state;
        public bool Ready => state == WorkerState.Ready;
        public bool TryResetTransfer()
        {
            if (state != WorkerState.Ready || handle == IntPtr.Zero) return false;
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
        internal static uint NativePendingCount => BridgeWorkerNative.lkg_worker_pending_count();
        internal static int CloseAllNativeWorkers() => BridgeWorkerNative.lkg_worker_close_all();
        internal static int CollectClosedNativeWorkers() => BridgeWorkerNative.lkg_worker_collect_closed();
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
        object playbackToken;
#if UNITY_EDITOR
        object editorPreviewOwner;
        BridgeEditorPreviewRetentionRecord editorPreviewRetentionRecord;
#endif
        bool disposalInProgress;
        long sessionGeneration;
        long lastReaperHandoffGeneration;
        public long SessionGeneration => sessionGeneration;
        public long LastReaperHandoffGeneration => lastReaperHandoffGeneration;
        public event Action<long> PlaybackReaperHandoff;

        void OnEnable()
        {
            if (initializeOnEnable) TryBegin();
        }

        void Update()
        {
            Poll();
        }

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
            if (owner == null || !ReferenceEquals(editorPreviewOwner, owner) || playbackToken != null) return false;
            return SubmitCore(quiltTexture, layout);
        }

        internal bool EndEditorPreview(object owner, BridgeEditorPreviewRetentionRecord retentionRecord)
        {
            if (owner == null || retentionRecord == null || !ReferenceEquals(editorPreviewOwner, owner) || playbackToken != null ||
                !ReferenceEquals(editorPreviewRetentionRecord, retentionRecord)) return false;
            Dispose();
            editorPreviewOwner = null;
            return retentionRecord.WorkerHandoffComplete;
        }
#endif

        bool TryBeginCore(BridgeGraphicsBackendRoute route)
        {
            if (!isActiveAndEnabled) return Fail("Bridge worker must be active before begin");
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
            Profile = default;
            Submissions = 0;
            SubmissionAttempts = 0;
            transferGraphicsFormatName = string.Empty;
            transferFormatName = string.Empty;
            transferSrgbDiagnostic = false;
            transferAntiAliasingDiagnostic = 0;
            transferDimensionName = string.Empty;
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
            if (!BridgeNativeImageGuard.TryInspectEditorProcess(out imageGuardError)) return Fail(imageGuardError);
#endif
            try
            {
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
                    Profile = new BridgeDisplayProfile(info);
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
                if (handle == IntPtr.Zero) { ReleaseOwnedResources(); return; }
                bool needsClose = false;
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
        void TransferOwnedResourcesToReaper(bool needsClose, Action<IntPtr, RenderTexture, CommandBuffer, bool, BridgeEditorPreviewRetentionRecord> enqueue,
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
            enqueue(ownedHandle, ownedTexture, ownedCommand, needsClose, previewOwner);
            RecordReaperHandoff(sessionGeneration);
        }
#endif

        internal void TransferOwnedResourcesToReaper(bool needsClose, Action<IntPtr, RenderTexture, CommandBuffer, bool> enqueue)
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
            enqueue(ownedHandle, ownedTexture, ownedCommand, needsClose);
            RecordReaperHandoff(sessionGeneration);
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
