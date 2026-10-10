using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
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
        public int firstCompleted, restartedCompleted, recordedFrames, distinctColors;
        public int width, height, views, pendingRetirement;
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
        double deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (output.CompletedFrames < 60 && Time.realtimeSinceStartupAsDouble < deadline)
        {
            Require(string.IsNullOrEmpty(output.LastError), output.LastError);
            yield return null;
        }
        Require(output.CompletedFrames >= 60, "Initial presentation timed out.");
        Require(string.IsNullOrEmpty(output.LastError), output.LastError);
        Result.firstCompleted = (int)output.CompletedFrames;
        var renderer = output.rig.Renderer;
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
            var colors = new System.Collections.Generic.HashSet<Color32>(image.GetPixels32());
            Result.distinctColors = colors.Count;
            Require(colors.Count > 2, "Fixture quilt has no visible color variation.");
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
        Require(output.StartOutput(), "Output restart failed: " + output.LastError);
        deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (output.CompletedFrames < 30 && Time.realtimeSinceStartupAsDouble < deadline)
        {
            Require(string.IsNullOrEmpty(output.LastError), output.LastError);
            yield return null;
        }
        Require(output.CompletedFrames >= 30, "Restarted presentation timed out.");
        Require(string.IsNullOrEmpty(output.LastError), output.LastError);
        Result.restartedCompleted = (int)output.CompletedFrames;
    }

    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
