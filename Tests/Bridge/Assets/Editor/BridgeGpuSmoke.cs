using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using ViewWeave;

public static class BridgeGpuSmoke
{
    public static void Run()
    {
        RunWithRuntimePath(Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH"));
    }

    public static void RunWithRuntimePath(string configuredRuntimePath)
    {
        GameObject go = null;
        BridgeQuiltOutput output = null;
        RenderTexture quilt = null;
        bool outputDisposed = false;
        try
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new Exception("requires Direct3D11");
            string runtimePath = configuredRuntimePath;
            if (string.IsNullOrWhiteSpace(runtimePath)) throw new Exception("LKG_BRIDGE_PATH or configured runtimePath is required");
            go = new GameObject("BridgeGpuSmoke");
            output = go.AddComponent<BridgeQuiltOutput>();
            output.bridgeLibraryPath = runtimePath;
            if (!output.TryInitialize()) throw new Exception(output.LastError);
            quilt = new RenderTexture(512, 256, 0, RenderTextureFormat.ARGB32); quilt.Create();
            RenderTexture.active = quilt; GL.Clear(true, true, Color.cyan); RenderTexture.active = null;
            output.Submit(quilt, new QuiltLayout(512, 256, 2, 4, 8));
            AsyncGPUReadback.Request(quilt).WaitForCompletion();
            if (BridgeNativeProbe.Draws() < 1) throw new Exception("native draw count is zero: " + BridgeNativeProbe.Error());
            if (BridgeNativeProbe.Error().Length != 0) throw new Exception(BridgeNativeProbe.Error());
            output.Dispose();
            outputDisposed = true;
            AsyncGPUReadback.Request(quilt).WaitForCompletion();
            if (BridgeNativeProbe.Initialized() != 0) throw new Exception("Bridge remained initialized after close");
            Debug.Log("BRIDGE_GPU_SMOKE_PASS draws=" + BridgeNativeProbe.Draws());
        }
        catch (Exception ex) { Debug.LogError("BRIDGE_GPU_SMOKE_FAIL " + ToAscii(ex.Message)); throw; }
        finally
        {
            if (!outputDisposed && output != null)
            {
                try { output.Dispose(); } catch (Exception ex) { Debug.LogError("BRIDGE_GPU_SMOKE_CLEANUP_FAIL " + ToAscii(ex.Message)); }
            }
            if (quilt != null)
            {
                try
                {
                    if (RenderTexture.active == quilt) RenderTexture.active = null;
                    quilt.Release();
                }
                finally { UnityEngine.Object.DestroyImmediate(quilt); }
            }
            if (go != null) UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static string ToAscii(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        char[] chars = text.ToCharArray();
        for (int index = 0; index < chars.Length; ++index)
        {
            if (chars[index] < 0x20 || chars[index] > 0x7e) chars[index] = '?';
        }
        return new string(chars);
    }

    static class BridgeNativeProbe
    {
        const string L = "lookingglass_bridge_shim";
        [System.Runtime.InteropServices.DllImport(L, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] internal static extern uint lkg_bridge_successful_draw_count();
        [System.Runtime.InteropServices.DllImport(L, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] internal static extern int lkg_bridge_is_initialized();
        [System.Runtime.InteropServices.DllImport(L, CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)] internal static extern int lkg_bridge_last_error(IntPtr p, uint n);
        internal static uint Draws() => lkg_bridge_successful_draw_count();
        internal static int Initialized() => lkg_bridge_is_initialized();
        internal static string Error() { int n=lkg_bridge_last_error(IntPtr.Zero,0); if(n<=0)return string.Empty; IntPtr p=System.Runtime.InteropServices.Marshal.AllocHGlobal(n+1); try { lkg_bridge_last_error(p,(uint)n+1); return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p)??string.Empty; } finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(p); } }
    }
}
