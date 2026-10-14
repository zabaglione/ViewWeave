using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;

// Current Bridge-calibration path fixture. Presentation completion is not optical proof.
public sealed class MetalCurrentValidationRunner : MonoBehaviour
{
    public BridgeMetalQuiltOutput output;
    public string pipelineName;
    public string reportDirectory;
    public bool Completed { get; private set; }
    public Report Result { get; } = new Report();
    QuiltRecorder recorder;

    [Serializable] public sealed class Report
    {
        public bool passed, development, editor;
        public string unityVersion, graphicsApi, pipeline, error, capture, sequence;
        public string actualPipeline, shaderPipelineTag;
        public int firstCompleted, restartedCompleted, recordedFrames, distinctColors;
        public int width, height, views, pendingRetirement;
        public int cellPatternMode, subpixelCellCount;
        public int redPixels, greenPixels, magentaPixels;
        public bool activeSubpixelChangeRejected, missingCellsRejected, classicRestartVerified;
        public string missingCellsError, restartSubpixelMode;
        public MetalRenderTargetValidation.Evidence targets;
    }

    IEnumerator Start()
    {
        Result.unityVersion = Application.unityVersion;
        Result.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
        Result.pipeline = pipelineName;
        Result.editor = Application.isEditor;
        Result.development = Debug.isDebugBuild;
        var run = Verify();
        string failure = null;
        while (true)
        {
            bool moved;
            try { moved = run.MoveNext(); }
            catch (Exception exception) { failure = exception.ToString(); break; }
            if (!moved) break;
            yield return run.Current;
        }
        if (recorder != null && recorder.IsRecording) recorder.CancelRecording();
        if (!string.IsNullOrEmpty(output.LastError)) failure = (failure ?? string.Empty) + " " + output.LastError;
        output.StopOutput();
        double deadline = Time.realtimeSinceStartupAsDouble + 30d;
        while ((output.IsClosing || BridgeMetalQuiltOutput.PendingRetirementCount != 0) && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Result.pendingRetirement = BridgeMetalQuiltOutput.PendingRetirementCount;
        if (output.IsClosing || Result.pendingRetirement != 0) failure = (failure ?? string.Empty) + " GPU retirement timed out.";
        if (!string.IsNullOrEmpty(output.LastError)) failure = (failure ?? string.Empty) + " " + output.LastError;
        Result.error = failure ?? string.Empty;
        Result.passed = failure == null;
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "runtime.json"), JsonUtility.ToJson(Result, true));
        Completed = true;
        Debug.Log("Metal current validation " + (Result.passed ? "PASS" : "FAIL") + ": " + JsonUtility.ToJson(Result));
        if (!Application.isEditor) Application.Quit(Result.passed ? 0 : 1);
    }

    IEnumerator Verify()
    {
        Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "Metal is required.");
        Require(output.StartOutput(), "Output start failed: " + output.LastError);
        Result.cellPatternMode = output.CellPatternMode;
        Result.subpixelCellCount = output.SubpixelCellCount;
        Result.activeSubpixelChangeRejected = !output.TrySetSubpixelMode(BridgeSubpixelMode.ClassicRgb, out _) &&
            output.SubpixelMode == BridgeSubpixelMode.Automatic;
        Require(Result.activeSubpixelChangeRejected, "Running output accepted a subpixel configuration change.");
        double deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (output.CompletedFrames < 60 && Time.realtimeSinceStartupAsDouble < deadline)
        {
            Require(string.IsNullOrEmpty(output.LastError), output.LastError);
            yield return null;
        }
        Require(output.CompletedFrames >= 60, "Initial presentation timed out.");
        Require(string.IsNullOrEmpty(output.LastError), output.LastError);
        Result.firstCompleted = (int)output.CompletedFrames;
        Result.actualPipeline = RenderPipelineManager.currentPipeline is UniversalRenderPipeline ? "UniversalRenderPipeline" :
            RenderPipelineManager.currentPipeline is HDRenderPipeline ? "HDRenderPipeline" : "OtherOrInactive";
        Result.shaderPipelineTag = Shader.globalRenderPipeline;
        Require(Result.actualPipeline == (pipelineName == "HDRP" ? "HDRenderPipeline" : "UniversalRenderPipeline") &&
            Result.shaderPipelineTag == (pipelineName == "HDRP" ? "HDRenderPipeline" : "UniversalPipeline"),
            "Actual render pipeline or shader tag differs from the requested fixture.");
        var renderer = output.rig.Renderer;
        Result.targets = MetalRenderTargetValidation.Capture(pipelineName);
        Result.width = renderer.LatestSnapshot.Layout.QuiltWidth;
        Result.height = renderer.LatestSnapshot.Layout.QuiltHeight;
        Result.views = renderer.LatestSnapshot.Layout.ViewCount;
        Directory.CreateDirectory(reportDirectory);
        Result.capture = Path.Combine(reportDirectory, "current.png");
        QuiltCaptureUtility.SaveLatest(renderer, Result.capture);
        QuiltCaptureUtility.SaveCentralViewLatest(renderer, Path.Combine(reportDirectory, "central.png"));
        var still = QuiltPlaybackSource.Open(Result.capture);
        Require(still.Info.FrameCount == 1, "Saved quilt cannot be reopened.");
        var image = still.LoadFrame(0);
        try
        {
            Color32[] pixels = image.GetPixels32();
            var colors = new System.Collections.Generic.HashSet<Color32>(pixels);
            Result.distinctColors = colors.Count;
            Require(colors.Count > 2, "Fixture quilt has no visible color variation.");
            foreach (Color32 pixel in pixels)
            {
                if (pixel.r > 2 * pixel.g && pixel.r > 2 * pixel.b && pixel.r > 24) Result.redPixels++;
                if (pixel.g > 2 * pixel.r && pixel.g > 2 * pixel.b && pixel.g > 24) Result.greenPixels++;
                if (pixel.r > 180 && pixel.b > 180 && pixel.g < 80) Result.magentaPixels++;
            }
            Require(Result.redPixels > 1000 && Result.greenPixels > 1000 && Result.magentaPixels == 0,
                "Fixture red/green drawing or missing-shader pixel gate failed.");
        }
        finally { Destroy(image); }
        recorder = output.gameObject.AddComponent<QuiltRecorder>();
        recorder.Renderer = renderer;
        recorder.StartRecording(reportDirectory, 1, 3);
        deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (recorder.IsRecording && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        Require(recorder.Status == QuiltRecordingStatus.Complete, "Recording failed: " + recorder.ErrorMessage);
        Result.recordedFrames = recorder.CompletedFrameCount;
        Result.sequence = recorder.RecordingDirectory;
        Require(Result.recordedFrames == 3 && QuiltPlaybackSource.Open(Result.sequence).Info.FrameCount == 3, "Recording frame count differs.");
        Require(string.IsNullOrEmpty(output.LastError), output.LastError);
        output.StopOutput();
        deadline = Time.realtimeSinceStartupAsDouble + 30d;
        while ((output.IsClosing || BridgeMetalQuiltOutput.PendingRetirementCount != 0) && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Require(!output.IsClosing && BridgeMetalQuiltOutput.PendingRetirementCount == 0, "Stop did not retire GPU resources.");
        Require(string.IsNullOrEmpty(output.LastError), output.LastError);
        Require(output.TrySetSubpixelMode(BridgeSubpixelMode.CalibratedCells, out string modeError), modeError);
        Result.missingCellsRejected = !output.StartOutput();
        Result.missingCellsError = output.LastError;
        Require(Result.missingCellsRejected && !string.IsNullOrEmpty(Result.missingCellsError), "Tableless Go accepted required-cell output.");
        deadline = Time.realtimeSinceStartupAsDouble + 30d;
        while ((output.IsClosing || BridgeMetalQuiltOutput.PendingRetirementCount != 0) && Time.realtimeSinceStartupAsDouble < deadline)
            yield return null;
        Require(output.TrySetSubpixelMode(BridgeSubpixelMode.ClassicRgb, out modeError), modeError);
        Require(output.StartOutput(), "Output restart failed: " + output.LastError);
        Result.restartSubpixelMode = output.SubpixelMode.ToString();
        deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (output.CompletedFrames < 30 && Time.realtimeSinceStartupAsDouble < deadline)
        {
            Require(string.IsNullOrEmpty(output.LastError), output.LastError);
            yield return null;
        }
        Require(output.CompletedFrames >= 30, "Restarted presentation timed out.");
        Require(string.IsNullOrEmpty(output.LastError), output.LastError);
        Result.restartedCompleted = (int)output.CompletedFrames;
        QuiltCaptureUtility.SaveLatest(renderer, Path.Combine(reportDirectory, "classic.png"));
        var classicImage = QuiltPlaybackSource.Open(Path.Combine(reportDirectory, "classic.png")).LoadFrame(0);
        try
        {
            int red = 0, green = 0, magenta = 0;
            foreach (Color32 pixel in classicImage.GetPixels32())
            {
                if (pixel.r > 2 * pixel.g && pixel.r > 2 * pixel.b && pixel.r > 24) red++;
                if (pixel.g > 2 * pixel.r && pixel.g > 2 * pixel.b && pixel.g > 24) green++;
                if (pixel.r > 180 && pixel.b > 180 && pixel.g < 80) magenta++;
            }
            Require(red > 1000 && green > 1000 && magenta == 0, "Classic RGB image gate failed.");
        }
        finally { Destroy(classicImage); }
        Result.classicRestartVerified = output.SubpixelMode == BridgeSubpixelMode.ClassicRgb;
        Require(Result.classicRestartVerified, "Classic RGB selection was not retained.");
    }

    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
