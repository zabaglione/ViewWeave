using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using ViewWeave;
using ViewWeave.Editor;

[InitializeOnLoad]
public static class MetalCurrentEditorValidation
{
    const string Prefix = "ViewWeave.MetalCurrent.";
    [Serializable] sealed class Report
    {
        public bool passed;
        public string pipeline, error, runtimeJson;
        public int localFrames, deviceFrames, sequenceObservedMask, pendingResources, pendingRetirement;
    }
    static MetalCurrentEditorValidation() { EditorApplication.update += Tick; }
    public static void Begin()
    {
        MetalCurrentValidationBuilder.Prepare();
        SessionState.SetString(Prefix + "root", MetalCurrentValidationBuilder.Argument("-validationRoot"));
        SessionState.SetString(Prefix + "pipeline", MetalCurrentValidationBuilder.Argument("-validationPipeline") ?? "URP");
        Switch(1);
    }
    static void Switch(int phase)
    {
        SessionState.SetInt(Prefix + "phase", phase);
        SessionState.SetFloat(Prefix + "since", (float)EditorApplication.timeSinceStartup);
    }
    static double Elapsed => EditorApplication.timeSinceStartup - SessionState.GetFloat(Prefix + "since", 0);
    static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static bool Retired => BridgeMetalEditorPreview.PendingResourceCount == 0 && BridgeMetalQuiltOutput.PendingRetirementCount == 0;
    static void Tick()
    {
        int phase = SessionState.GetInt(Prefix + "phase", 0);
        if (phase == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try
        {
            Require(Elapsed < 90, "Editor validation timed out at phase " + phase);
            var rig = UnityEngine.Object.FindAnyObjectByType<ViewWeaveRig>();
            var output = UnityEngine.Object.FindAnyObjectByType<BridgeMetalQuiltOutput>();
            if (phase < 11) Require(rig != null && output != null, "Fixture rig is missing.");
            if (phase >= 11 && output != null) Require(string.IsNullOrEmpty(output.LastError), output.LastError);
            if (phase <= 10) Require(string.IsNullOrEmpty(BridgeMetalEditorPreview.LastError), BridgeMetalEditorPreview.LastError);
            if (phase >= 13 && phase <= 19) Require(string.IsNullOrEmpty(BridgeMetalQuiltPlayback.LastError), BridgeMetalQuiltPlayback.LastError);
            if (phase == 1)
            {
                Require(BridgeMetalEditorPreview.Start(rig, false, out string error), error);
                Switch(2);
            }
            else if (phase == 2)
            {
                Require(string.IsNullOrEmpty(BridgeMetalEditorPreview.LastError), BridgeMetalEditorPreview.LastError);
                if (BridgeMetalEditorPreview.RenderedFrames < 6) return;
                BridgeMetalEditorPreview.TogglePause();
                SessionState.SetInt(Prefix + "frames", BridgeMetalEditorPreview.RenderedFrames);
                Switch(3);
            }
            else if (phase == 3 && Elapsed > 0.6)
            {
                Require(BridgeMetalEditorPreview.IsPaused && BridgeMetalEditorPreview.RenderedFrames == SessionState.GetInt(Prefix + "frames", -1), "Local preview did not pause.");
                BridgeMetalEditorPreview.TogglePause(); Switch(4);
            }
            else if (phase == 4)
            {
                if (BridgeMetalEditorPreview.RenderedFrames < SessionState.GetInt(Prefix + "frames", 0) + 3) return;
                SessionState.SetInt(Prefix + "local", BridgeMetalEditorPreview.RenderedFrames);
                BridgeMetalEditorPreview.Stop(); Switch(5);
            }
            else if (phase == 5 && Retired)
            {
                Require(!BridgeMetalEditorPreview.IsActive, "Local preview is still active.");
                Require(BridgeMetalEditorPreview.Start(rig, true, out string error), error); Switch(6);
            }
            else if (phase == 6)
            {
                Require(string.IsNullOrEmpty(BridgeMetalEditorPreview.LastError), BridgeMetalEditorPreview.LastError);
                if (BridgeMetalEditorPreview.PresentedFrames < 30) return;
                BridgeMetalEditorPreview.TogglePause(); Switch(7);
            }
            else if (phase == 7 && Elapsed > 0.3)
            {
                SessionState.SetInt(Prefix + "frames", (int)BridgeMetalEditorPreview.PresentedFrames); Switch(8);
            }
            else if (phase == 8 && Elapsed > 0.6)
            {
                Require(BridgeMetalEditorPreview.IsPaused && BridgeMetalEditorPreview.PresentedFrames == SessionState.GetInt(Prefix + "frames", -1), "Device preview did not pause.");
                BridgeMetalEditorPreview.TogglePause(); Switch(9);
            }
            else if (phase == 9)
            {
                Require(string.IsNullOrEmpty(BridgeMetalEditorPreview.LastError), BridgeMetalEditorPreview.LastError);
                if (BridgeMetalEditorPreview.PresentedFrames < SessionState.GetInt(Prefix + "frames", 0) + 15) return;
                SessionState.SetInt(Prefix + "device", (int)BridgeMetalEditorPreview.PresentedFrames);
                BridgeMetalEditorPreview.Stop(); Switch(10);
            }
            else if (phase == 10 && Retired)
            { Switch(11); EditorApplication.EnterPlaymode(); }
            else if (phase == 11 && EditorApplication.isPlaying)
            {
                var runner = UnityEngine.Object.FindAnyObjectByType<MetalCurrentValidationRunner>();
                if (runner == null || !runner.Completed) return;
                SessionState.SetString(Prefix + "runtime", JsonUtility.ToJson(runner.Result));
                Require(runner.Result.passed, runner.Result.error);
                SessionState.SetString(Prefix + "capture", runner.Result.capture);
                SessionState.SetString(Prefix + "sequence", runner.Result.sequence);
                Require(output.StartOutput(), output.LastError); Switch(12);
            }
            else if (phase == 12)
            {
                Require(string.IsNullOrEmpty(output.LastError), output.LastError);
                if (output.CompletedFrames < 10) return;
                SessionState.SetInt(Prefix + "frames", (int)output.SubmittedFrames);
                Require(BridgeMetalQuiltPlayback.TryStart(output, QuiltPlaybackSource.Open(SessionState.GetString(Prefix + "capture", "")), out string error), error);
                Switch(13);
            }
            else if (phase == 13)
            {
                Require(string.IsNullOrEmpty(BridgeMetalQuiltPlayback.LastError), BridgeMetalQuiltPlayback.LastError);
                // A still is submitted once and held. Wait beyond all earlier live submissions.
                if (BridgeMetalQuiltPlayback.CurrentFrame != 0 || output.CompletedFrames <= SessionState.GetInt(Prefix + "frames", 0) || Elapsed < 0.6) return;
                BridgeMetalQuiltPlayback.Stop(); Switch(14);
            }
            else if (phase == 14 && Retired)
            {
                Require(!BridgeMetalQuiltPlayback.IsActive, "Still playback is still active.");
                SessionState.SetInt(Prefix + "frames", (int)output.SubmittedFrames);
                Require(BridgeMetalQuiltPlayback.TryStart(output, QuiltPlaybackSource.Open(SessionState.GetString(Prefix + "sequence", "")), out string error), error);
                SessionState.SetInt(Prefix + "mask", 0);
                Switch(15);
            }
            else if (phase == 15)
            {
                Require(string.IsNullOrEmpty(BridgeMetalQuiltPlayback.LastError), BridgeMetalQuiltPlayback.LastError);
                int frame = BridgeMetalQuiltPlayback.CurrentFrame;
                int mask = SessionState.GetInt(Prefix + "mask", 0);
                if (frame >= 0) mask |= 1 << frame;
                SessionState.SetInt(Prefix + "mask", mask);
                if (frame != 0 || output.CompletedFrames <= SessionState.GetInt(Prefix + "frames", 0)) return;
                BridgeMetalQuiltPlayback.TogglePause(); Switch(16);
            }
            else if (phase == 16 && Elapsed > 0.3)
            { SessionState.SetInt(Prefix + "pausedFrame", BridgeMetalQuiltPlayback.CurrentFrame); Switch(17); }
            else if (phase == 17 && Elapsed > 0.6)
            {
                Require(BridgeMetalQuiltPlayback.IsPaused && BridgeMetalQuiltPlayback.CurrentFrame == SessionState.GetInt(Prefix + "pausedFrame", -1), "Sequence playback did not pause.");
                BridgeMetalQuiltPlayback.TogglePause(); Switch(18);
            }
            else if (phase == 18)
            {
                Require(string.IsNullOrEmpty(BridgeMetalQuiltPlayback.LastError), BridgeMetalQuiltPlayback.LastError);
                int frame = BridgeMetalQuiltPlayback.CurrentFrame;
                int mask = SessionState.GetInt(Prefix + "mask", 0);
                if (frame >= 0) mask |= 1 << frame;
                SessionState.SetInt(Prefix + "mask", mask);
                if (BridgeMetalQuiltPlayback.IsActive || BridgeMetalQuiltPlayback.IsStopping || !Retired) return;
                Require(mask == 7 && BridgeMetalQuiltPlayback.SkippedFrames == 0, "Sequence did not present all three frame indices.");
                Require(output.IsRunning && rig.Renderer.autoRender, "Live output did not resume after sequence EOF.");
                Switch(19);
            }
            else if (phase == 19 && Retired)
            { output.StopOutput(); Switch(20); }
            else if (phase == 20 && !output.IsClosing && Retired)
            { Switch(21); EditorApplication.ExitPlaymode(); }
            else if (phase == 21 && !EditorApplication.isPlayingOrWillChangePlaymode && Retired) Finish(null);
        }
        catch (Exception exception)
        {
            BridgeMetalQuiltPlayback.Stop(); BridgeMetalEditorPreview.Stop();
            var output = UnityEngine.Object.FindAnyObjectByType<BridgeMetalQuiltOutput>();
            if (output != null) output.StopOutput();
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            Finish(exception.ToString());
        }
    }
    static void Finish(string error)
    {
        var report = new Report
        {
            passed = error == null, error = error ?? string.Empty,
            pipeline = SessionState.GetString(Prefix + "pipeline", ""),
            localFrames = SessionState.GetInt(Prefix + "local", 0), deviceFrames = SessionState.GetInt(Prefix + "device", 0),
            sequenceObservedMask = SessionState.GetInt(Prefix + "mask", 0), runtimeJson = SessionState.GetString(Prefix + "runtime", ""),
            pendingResources = BridgeMetalEditorPreview.PendingResourceCount, pendingRetirement = BridgeMetalQuiltOutput.PendingRetirementCount
        };
        string root = SessionState.GetString(Prefix + "root", "");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "editor.json"), JsonUtility.ToJson(report, true));
        Switch(0);
        Debug.Log("Metal current Editor " + (report.passed ? "PASS" : "FAIL") + ": " + JsonUtility.ToJson(report));
    }
}
