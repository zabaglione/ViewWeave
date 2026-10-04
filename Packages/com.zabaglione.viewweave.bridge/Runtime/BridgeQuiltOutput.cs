using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LookingGlass.Community
{
    [DisallowMultipleComponent]
    public sealed class BridgeQuiltOutput : MonoBehaviour, IQuiltOutput
    {
        [Tooltip("Absolute path to the installed Bridge runtime DLL or dylib.")]
        public string bridgeLibraryPath;
        public string applicationName = "LookingGlassCommunity";
        public uint displayIndex;
        public bool initializeOnEnable = true;
        [Range(0.01f, 4f)] public float zoom = 1f;
        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public ulong Session { get; private set; }
        public BridgeDisplayProfile Profile { get; private set; }
        float viewAspect = 1f;
        uint actualDisplayIndex;
        RenderTexture bridgeTransferTexture;
        bool bridgeTransferSrgb;
        public string LastError { get; private set; }

        void OnEnable() { if (initializeOnEnable) TryInitialize(); }
        void OnDisable() { Dispose(); }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            QuiltTexture = quiltTexture;
            Layout = layout;
            if (Session == 0) return;
            if (quiltTexture == null)
            { LastError = "Bridge output requires a quilt texture"; return; }
            if (layout.Columns <= 0 || layout.Rows <= 0 || layout.ViewCount != layout.Columns * layout.Rows)
            { LastError = "Bridge output requires a complete quilt grid"; return; }
            if (!EnsureBridgeTransferTexture(quiltTexture)) return;

            // The Bridge D3D11 interop path consumes top-left image rows while Unity quilts use bottom-left coordinates.
            // Flip only the private transfer texture so the public QuiltTexture remains unchanged.
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(quiltTexture, bridgeTransferTexture, new Vector2(1f, -1f), new Vector2(0f, 1f));
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
            if (BridgeNative.QueueDraw(Session, bridgeTransferTexture, layout, layout.AspectRatio, zoom) != 0)
                LastError = NativeError();
        }

        bool EnsureBridgeTransferTexture(RenderTexture source)
        {
            int width = source.width;
            int height = source.height;
            bool sourceSrgb = source.sRGB;
            if (width <= 0 || height <= 0)
            { LastError = "Bridge output received an invalid quilt size"; return false; }
            if (bridgeTransferTexture != null &&
                (bridgeTransferTexture.width != width || bridgeTransferTexture.height != height ||
                 bridgeTransferTexture.format != RenderTextureFormat.ARGB32 ||
                 bridgeTransferSrgb != sourceSrgb))
                ReleaseBridgeTransferTexture();
            if (bridgeTransferTexture == null)
            {
                bridgeTransferSrgb = sourceSrgb;
                bridgeTransferTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32,
                    sourceSrgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
                { name = "BridgeQuiltTransfer", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                bridgeTransferTexture.Create();
            }
            if (!bridgeTransferTexture.IsCreated())
            { LastError = "Bridge transfer texture could not be created"; ReleaseBridgeTransferTexture(); return false; }
            return true;
        }

        void ReleaseBridgeTransferTexture()
        {
            if (bridgeTransferTexture == null) return;
            if (bridgeTransferTexture.IsCreated()) bridgeTransferTexture.Release();
            if (Application.isPlaying) Destroy(bridgeTransferTexture);
            else DestroyImmediate(bridgeTransferTexture);
            bridgeTransferTexture = null;
            bridgeTransferSrgb = false;
        }

        string NativeError()
        {
            int length = BridgeNative.lkg_bridge_last_error(IntPtr.Zero, 0);
            if (length <= 0) return "Bridge render submission failed";
            IntPtr ptr = System.Runtime.InteropServices.Marshal.AllocHGlobal(length + 1);
            try { BridgeNative.lkg_bridge_last_error(ptr, (uint)(length + 1)); return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? "Bridge render submission failed"; }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(ptr); }
        }

        public bool TryInitialize()
        {
            Dispose();
            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) { LastError = "Bridge D3D11 output is currently Windows-only"; return false; }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) { LastError = "Bridge output currently supports Direct3D11 only"; return false; }
            if (SystemInfo.renderingThreadingMode != RenderingThreadingMode.Direct)
            {
                LastError = "Bridge D3D11 interop requires Direct rendering threading; experimental support requires -force-gfx-direct";
                return false;
            }
            if (string.IsNullOrWhiteSpace(bridgeLibraryPath)) { LastError = "Set an absolute Bridge runtime library path"; return false; }
            int result;
            try { result = BridgeNative.lkg_bridge_load(bridgeLibraryPath); }
            catch (DllNotFoundException) { LastError = "Native shim library is missing"; return false; }
            if (result != 0) { LastError = "Bridge runtime could not be loaded (code " + result + ")"; return false; }
            result = BridgeNative.lkg_bridge_initialize(applicationName);
            if (result != 0) { LastError = "Bridge initialization failed (code " + result + ")"; BridgeNative.lkg_bridge_unload(); return false; }
            if (BridgeNative.lkg_bridge_get_display(displayIndex, out var display) != 0)
            { LastError = "Requested Looking Glass display was not found"; BridgeNative.lkg_bridge_uninitialize(); return false; }
            Profile = new BridgeDisplayProfile(display);
            if (!Profile.IsValid) { LastError = "Bridge returned an invalid display profile"; BridgeNative.lkg_bridge_uninitialize(); return false; }
            actualDisplayIndex = display.index;
            if (display.aspect > 0f) viewAspect = display.aspect;
            Session = BridgeNative.lkg_bridge_create_d3d11_session(IntPtr.Zero, actualDisplayIndex);
            if (Session == 0) { LastError = "Unity D3D11 device hook or Bridge display session is unavailable"; Dispose(); return false; }
            LastError = string.Empty;
            return true;
        }

        public void Dispose()
        {
            if (Session != 0) { ulong session = Session; Session = 0; BridgeNative.QueueClose(session); }
            ReleaseBridgeTransferTexture();
        }
    }
}
