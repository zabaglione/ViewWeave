using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LookingGlass.Community
{
    [DisallowMultipleComponent]
    public sealed class BridgeWorkerQuiltOutput : MonoBehaviour, IQuiltOutput
    {
        public string runtimePath;
        public string applicationName = "LookingGlassCommunity";
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
        public uint Completed => status.completed;
        public uint Dropped => status.dropped;
        public static int ShutdownPendingCount => BridgeWorkerReaper.PendingCount;
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
        BridgeWorkerNative.Status status;

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
            if (!isActiveAndEnabled) return Fail("Bridge worker must be active before begin");
            if (handle != IntPtr.Zero || state == WorkerState.Connecting || state == WorkerState.Ready || state == WorkerState.Closing)
            {
                LastError = "Bridge worker is already active";
                return false;
            }
            if (Application.platform != RuntimePlatform.WindowsPlayer || Application.isEditor)
                return Fail("Bridge worker is supported in Windows Player only");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                return Fail("Bridge worker currently supports Direct3D11 only");
            if (string.IsNullOrWhiteSpace(runtimePath))
                return Fail("Set an absolute Bridge worker runtime path");
            status = default;
            Profile = default;
            Submissions = 0;
            transferGraphicsFormatName = string.Empty;
            transferFormatName = string.Empty;
            transferSrgbDiagnostic = false;
            transferAntiAliasingDiagnostic = 0;
            transferDimensionName = string.Empty;
            try
            {
                handle = BridgeWorkerNative.lkg_worker_begin(runtimePath, applicationName, displayOrdinal, IntPtr.Zero);
            }
            catch (DllNotFoundException) { return Fail("Bridge worker native library is missing"); }
            catch (EntryPointNotFoundException) { return Fail("Bridge worker ABI entry point is missing"); }
            catch (Exception error) { return Fail(Sanitize(error.Message)); }
            if (handle == IntPtr.Zero) return Fail("Bridge worker begin failed");
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
                        state = WorkerState.Closed;
                        BridgeWorkerReaper.Enqueue(handle, transferTexture, command, false);
                        handle = IntPtr.Zero;
                        transferTexture = null;
                        transferNativePtr = IntPtr.Zero;
                        transferSrgb = false;
                        command = null;
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
            QuiltTexture = quiltTexture;
            Layout = layout;
            if (state != WorkerState.Ready || handle == IntPtr.Zero) return;
            if (quiltTexture == null || layout.Columns <= 0 || layout.Rows <= 0 || layout.ViewCount != layout.Columns * layout.Rows)
            {
                LastError = "Bridge worker requires a complete quilt grid";
                return;
            }
            if (!EnsureTransferTexture(quiltTexture)) return;
            RenderTexture previous = RenderTexture.active;
            try { Graphics.Blit(quiltTexture, transferTexture, new Vector2(1f, -1f), new Vector2(0f, 1f)); }
            finally { RenderTexture.active = previous; }
            IntPtr drawData;
            try { drawData = BridgeWorkerNative.lkg_worker_make_draw_data(handle, transferNativePtr, (uint)layout.Columns, (uint)layout.Rows, layout.AspectRatio, zoom); }
            catch (Exception error) { LastError = Sanitize(error.Message); return; }
            if (drawData == IntPtr.Zero) return;
            bool executeAttempted = false;
            try
            {
                if (command == null) command = new CommandBuffer { name = "LookingGlass Bridge Worker Draw" };
                command.Clear();
                command.IssuePluginEventAndData(BridgeWorkerNative.lkg_worker_render_event_func(), 0, drawData);
                executeAttempted = true;
                Graphics.ExecuteCommandBuffer(command);
                Submissions++;
            }
            catch (Exception error)
            {
                if (!executeAttempted) BridgeWorkerNative.lkg_worker_cancel_draw_data(drawData);
                LastError = Sanitize(error.Message);
                if (executeAttempted) Dispose();
            }
        }

        public void Dispose()
        {
            if (handle == IntPtr.Zero) { ReleaseOwnedResources(); return; }
            try
            {
                int closeResult = BridgeWorkerNative.lkg_worker_close(handle);
                bool needsClose = closeResult != 0;
                if (needsClose) LastError = "Bridge worker close pending (code " + closeResult + ")";
                BridgeWorkerReaper.Enqueue(handle, transferTexture, command, needsClose);
                handle = IntPtr.Zero;
                transferTexture = null;
                transferNativePtr = IntPtr.Zero;
                transferSrgb = false;
                command = null;
                state = WorkerState.Closed;
            }
            catch (Exception error)
            {
                LastError = Sanitize(error.Message);
                BridgeWorkerReaper.Enqueue(handle, transferTexture, command, true);
                handle = IntPtr.Zero;
                transferTexture = null;
                transferNativePtr = IntPtr.Zero;
                transferSrgb = false;
                command = null;
                state = WorkerState.Closed;
            }
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
                transferTexture = new RenderTexture(descriptor) { name = "BridgeWorkerQuiltTransfer", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
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
        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c);
            return new string(chars.ToArray());
        }
    }
}
