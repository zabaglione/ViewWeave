using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;

// Controls separate shared Quilt rendering from metadata-only SDK ownership.
public sealed class ShutdownControlRunner : MonoBehaviour
{
    public string mode, pipelineName, reportDirectory, sdkPath;
    public QuiltRenderer quilt;
    [Serializable] public sealed class Result
    {
        public bool passed, editor, development, cleanupProven;
        public string error, mode, pipeline, unityVersion, graphicsApi;
        public string actualPipeline, shaderTag;
        public MetalRenderTargetValidation.Evidence targets;
        public int renderedQuilts, views, baselineCameras, peakCameras, remainingCameras;
        public int redPixels, greenPixels, magentaPixels;
        public uint metadataDisplays;
        public bool metadataCleanupFinished, metadataEnded;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct SnapshotStatus
    {
        public uint structSize, version, state, itemCount;
        public ulong snapshotGeneration, requestedGeneration;
        public uint pending, cleanupFinished;
        public int result;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string error;
    }
    static class Native
    {
        const string Plugin = "lookingglass_bridge_shim";
        [DllImport(Plugin, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr lkg_worker_begin_metadata_v1([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string app);
        [DllImport(Plugin, CallingConvention = CallingConvention.Cdecl)]
        public static extern int lkg_worker_get_display_snapshot_v1(IntPtr handle, ref SnapshotStatus status,
            IntPtr items, uint capacity, out uint required);
        [DllImport(Plugin, CallingConvention = CallingConvention.Cdecl)] public static extern int lkg_worker_close(IntPtr handle);
        [DllImport(Plugin, CallingConvention = CallingConvention.Cdecl)] public static extern int lkg_worker_end_metadata_v1(IntPtr handle);
    }
    IntPtr metadata, items;
    Result result;

    IEnumerator Start()
    {
        result = new Result
        {
            mode = mode, pipeline = pipelineName, unityVersion = Application.unityVersion,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(), editor = Application.isEditor, development = Debug.isDebugBuild
        };
        IEnumerator run = mode == "QuiltOnly" ? VerifyQuilt() : VerifyMetadata();
        string failure = null;
        while (true)
        {
            bool moved;
            try { moved = run.MoveNext(); }
            catch (Exception e) { failure = e.ToString(); break; }
            if (!moved) break;
            yield return run.Current;
        }
        if (metadata != IntPtr.Zero)
        {
            Native.lkg_worker_close(metadata);
            double deadline = Time.realtimeSinceStartupAsDouble + 30d;
            bool done = false;
            while (!done && Time.realtimeSinceStartupAsDouble < deadline)
            {
                var status = NewStatus();
                Native.lkg_worker_get_display_snapshot_v1(metadata, ref status, items, 16, out _);
                if (status.pending == 0 && status.cleanupFinished == 1)
                { done = Native.lkg_worker_end_metadata_v1(metadata) == 0; if (done) metadata = IntPtr.Zero; }
                if (!done) yield return null;
            }
            result.cleanupProven = done;
        }
        if (items != IntPtr.Zero && metadata == IntPtr.Zero) { Marshal.FreeHGlobal(items); items = IntPtr.Zero; }
        if (quilt != null)
        {
            quilt.autoRender = false; quilt.enabled = false;
            for (int i = 0; i < 10; ++i) yield return null;
        }
        result.error = failure ?? string.Empty;
        result.passed = failure == null && result.cleanupProven;
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "runtime.json"), JsonUtility.ToJson(result, true));
        Debug.Log("Shutdown control " + mode + " " + (result.passed ? "PASS" : "FAIL"));
        yield return new WaitForSecondsRealtime(5f);
        if (result.cleanupProven) Application.Quit(result.passed ? 0 : 1);
        else Debug.LogError("Metadata cleanup is unproven; control Player is retained.");
    }
    static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    static SnapshotStatus NewStatus() => new SnapshotStatus { structSize = 304, version = 1, error = string.Empty };
    IEnumerator VerifyMetadata()
    {
        Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "Metal control is required.");
        Require(mode == "MetadataOnly" && quilt == null, "Metadata control must have no Quilt renderer.");
        Require(Marshal.SizeOf<SnapshotStatus>() == 304, "Metadata status ABI mismatch.");
        items = Marshal.AllocHGlobal(16 * 76);
        metadata = Native.lkg_worker_begin_metadata_v1(sdkPath, "ViewWeave Metadata Shutdown Control");
        Require(metadata != IntPtr.Zero, "Metadata worker could not start.");
        double deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (Time.realtimeSinceStartupAsDouble < deadline)
        {
            var status = NewStatus();
            int code = Native.lkg_worker_get_display_snapshot_v1(metadata, ref status, items, 16, out uint count);
            Require(code == 0, "Metadata snapshot query failed.");
            if (status.pending == 0)
            {
                Require(status.result == 0 && status.cleanupFinished == 1 && count > 0, status.error);
                result.metadataDisplays = count; result.metadataCleanupFinished = true;
                Require(Native.lkg_worker_close(metadata) == 0, "Metadata Close failed.");
                Require(Native.lkg_worker_end_metadata_v1(metadata) == 0, "Metadata End failed.");
                metadata = IntPtr.Zero; result.metadataEnded = true; result.cleanupProven = true;
                yield break;
            }
            yield return null;
        }
        throw new TimeoutException("Metadata query timed out.");
    }
    IEnumerator VerifyQuilt()
    {
        Require(mode == "QuiltOnly" && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "Quilt-only Metal control is required.");
        Require(quilt != null && quilt.Output == null, "Quilt-only control must have no device output.");
        quilt.autoRender = false;
        yield return null;
        result.baselineCameras = Resources.FindObjectsOfTypeAll<Camera>().Length;
        long first = quilt.QuiltRevision;
        quilt.autoRender = true;
        double deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (quilt.QuiltRevision < first + 60 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        result.renderedQuilts = (int)(quilt.QuiltRevision - first);
        result.views = quilt.LatestSnapshot.LastRenderedViewCount;
        result.peakCameras = Resources.FindObjectsOfTypeAll<Camera>().Length;
        result.actualPipeline = RenderPipelineManager.currentPipeline is UniversalRenderPipeline ? "UniversalRenderPipeline" :
            RenderPipelineManager.currentPipeline is HDRenderPipeline ? "HDRenderPipeline" : "OtherOrInactive";
        result.shaderTag = Shader.globalRenderPipeline;
        Require(result.actualPipeline == (pipelineName == "HDRP" ? "HDRenderPipeline" : "UniversalRenderPipeline") &&
            result.shaderTag == (pipelineName == "HDRP" ? "HDRenderPipeline" : "UniversalPipeline"), "Control pipeline or shader tag mismatch.");
        result.targets = MetalRenderTargetValidation.Capture(pipelineName);
        Require(result.renderedQuilts >= 60 && result.views == 66, "Shared Quilt render gate failed.");
        RenderTexture old = RenderTexture.active;
        var image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = quilt.QuiltTexture;
            image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0); image.Apply(false, false);
            foreach (Color32 p in image.GetPixels32())
            {
                if (p.r > p.g * 2 && p.r > p.b * 2 && p.r > 24) ++result.redPixels;
                if (p.g > p.r * 2 && p.g > p.b * 2 && p.g > 24) ++result.greenPixels;
                if (p.r > 180 && p.b > 180 && p.g < 80) ++result.magentaPixels;
            }
            Require(result.redPixels > 1000 && result.greenPixels > 1000 && result.magentaPixels == 0, "Quilt-only image gate failed.");
            Directory.CreateDirectory(reportDirectory); File.WriteAllBytes(Path.Combine(reportDirectory, "quilt.png"), image.EncodeToPNG());
        }
        finally { RenderTexture.active = old; Destroy(image); }
        quilt.autoRender = false; quilt.enabled = false;
        for (int i = 0; i < 10; ++i) yield return null;
        result.remainingCameras = Resources.FindObjectsOfTypeAll<Camera>().Length;
        result.cleanupProven = quilt.QuiltTexture == null && result.remainingCameras <= result.baselineCameras;
        Require(result.cleanupProven, "Shared Quilt resources did not return to the camera baseline.");
    }
}
