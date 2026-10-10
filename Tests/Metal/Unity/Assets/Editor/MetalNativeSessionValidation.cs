using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using ViewWeave;

public static class MetalNativeSessionValidation
{
    [StructLayout(LayoutKind.Sequential)]
    struct Display
    {
        internal uint index, width, height;
        internal float aspect, viewcone;
        internal int quiltWidth, quiltHeight, columns, rows;
        internal float center, pitch, slope, dpi, flipX;
        internal int inverted;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Status { internal uint submitted, completed, dropped, error, pending, closing; }
    const string Library = "lookingglass_bridge_shim";
    [DllImport(Library)] static extern ulong lkg_metal_display_open(string path, string name, uint ordinal, out Display info);
    [DllImport(Library)] static extern int lkg_metal_display_submit(ulong id, IntPtr texture, out IntPtr data);
    [DllImport(Library)] static extern int lkg_metal_display_status(ulong id, out Status status);
    [DllImport(Library)] static extern void lkg_metal_display_close(ulong id);
    [DllImport(Library)] static extern IntPtr lkg_metal_display_render_event();
    [DllImport(Library)] static extern IntPtr lkg_metal_display_error();

    [MenuItem("Tools/ViewWeave/Validate Metal Native Cancellation")]
    public static void Run()
    {
        if (!BridgeRuntimeLocator.TryResolve(null, out string path, out string error)) throw new Exception(error);
        if (Marshal.SizeOf<Display>() != 60 || Marshal.SizeOf<Status>() != 24) throw new Exception("Invalid Metal ABI");
        ulong id = 0;
        RenderTexture source = null;
        var commands = new CommandBuffer();
        try
        {
            IntPtr stale = IntPtr.Zero;
            for (int cycle = 0; cycle < 3; ++cycle)
            {
                id = lkg_metal_display_open(path, "ViewWeave Metal cancellation validation", 0, out Display info);
                if (id == 0) throw new Exception(Marshal.PtrToStringAnsi(lkg_metal_display_error()));
                if (source == null)
                {
                    source = new RenderTexture((int)info.width,(int)info.height,0) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm };
                    source.Create();
                }
                if (stale != IntPtr.Zero)
                {
                    commands.Clear(); commands.IssuePluginEventAndData(lkg_metal_display_render_event(), 0, stale);
                    Graphics.ExecuteCommandBuffer(commands);
                    // Force a queue-tail proof so the stale event executes while a new generation is active.
                    var readback = AsyncGPUReadback.Request(source,0,0,1,0,1,0,1); readback.WaitForCompletion();
                    if (readback.hasError) throw new Exception("Cancellation queue-tail proof failed");
                }
                if (lkg_metal_display_submit(id, source.GetNativeTexturePtr(), out stale) != 0 || stale == IntPtr.Zero)
                    throw new Exception("Native admission failed");
                if (lkg_metal_display_submit(id, source.GetNativeTexturePtr(), out _) != 1)
                    throw new Exception("Busy admission did not drop the extra frame");
                if (lkg_metal_display_status(id, out Status status) != 0 || status.pending != 1 || status.submitted != 1 || status.dropped != 1)
                    throw new Exception("Unexpected admission counters");
                // Intentionally never enqueue the admitted token; Close must cancel it and permit restart.
                lkg_metal_display_close(id);
                if (lkg_metal_display_status(id, out _) == 0) throw new Exception("Unissued frame prevented close");
                id = 0;
                Debug.Log("Metal native cancellation passed: cycle=" + cycle + " dimensions=" + info.width + "x" + info.height);
            }
        }
        finally
        {
            if (id != 0) lkg_metal_display_close(id);
            commands.Release();
            if (source != null) { source.Release(); UnityEngine.Object.DestroyImmediate(source); }
        }
    }
}
