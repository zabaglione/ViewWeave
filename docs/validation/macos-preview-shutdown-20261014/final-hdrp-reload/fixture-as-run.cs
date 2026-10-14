using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using ViewWeave;
using ViewWeave.Editor;

// Exercise an active device Preview transition without an explicit Preview Stop.
[InitializeOnLoad]
public static class MetalPreviewTransitionValidation
{
    const string Prefix = "ViewWeave.MetalPreviewTransition.";
    const string ScenePath = "Assets/SamplePlayerValidationGenerated/MetalCurrent/Validation.unity";

    [Serializable] public sealed class Cycle
    {
        public int index, previewFrames, previewRendered, playFrames, pendingResources, pendingRetirement;
        public bool paused, resumed, previewActiveAtPlayRequest, previewStoppedInPlay, returnedToEdit;
        public bool sourceProfileRestored;
        public bool sourceProfileUnchangedBeforePlay, profileRestoredInPlay, settingsRestoredInPlay;
        public int hiddenProducersBeforePlay, hiddenProducersInPlay;
        public int reloadsBeforePlay, reloadsInPlay;
        public int redPixels, greenPixels, magentaPixels;
        public MetalRenderTargetValidation.Evidence targets;
    }

    [Serializable] public sealed class Report
    {
        public bool passed, domainReloadDisabled;
        public string error, cleanupError, pipeline, actualPipeline, shaderTag, unityVersion, graphicsApi, gpu;
        public bool cleanupProven, exitRequested;
        public int pid, requestedCycles, reloadCount, pendingResources, pendingRetirement;
        public List<Cycle> cycles = new List<Cycle>();
    }

    static MetalPreviewTransitionValidation()
    {
        SessionState.SetInt(Prefix + "reloads", SessionState.GetInt(Prefix + "reloads", 0) + 1);
        EditorApplication.update += Tick;
    }

    static void Require(bool valid, string message)
    { if (!valid) throw new InvalidOperationException(message); }
    static double Elapsed => EditorApplication.timeSinceStartup - SessionState.GetFloat(Prefix + "since", 0);
    static bool Retired => !BridgeMetalEditorPreview.IsActive && !BridgeMetalEditorPreview.IsStopping &&
        BridgeMetalEditorPreview.PendingResourceCount == 0 && BridgeMetalQuiltOutput.PendingRetirementCount == 0;
    static Report Load() => JsonUtility.FromJson<Report>(SessionState.GetString(Prefix + "report", "{}"));
    static void Save(Report value) => SessionState.SetString(Prefix + "report", JsonUtility.ToJson(value));
    static void Phase(int value)
    {
        SessionState.SetInt(Prefix + "phase", value);
        SessionState.SetFloat(Prefix + "since", (float)EditorApplication.timeSinceStartup);
    }
    static ViewWeaveRig Rig() => GameObject.Find("Quilt Rig")?.GetComponent<ViewWeaveRig>();

    public static void BeginPrepared()
    {
        string root = MetalCurrentValidationBuilder.Argument("-validationRoot");
        Require(!string.IsNullOrEmpty(root), "A validation root is required.");
        Directory.CreateDirectory(root);
        Require(!File.Exists(Path.Combine(root, "transition.json")), "Refuse stale transition evidence.");
        EditorSceneManager.OpenScene(ScenePath);
        var rig = Rig();
        Require(rig != null, "Prepared Rig is missing.");
        var output = rig.GetComponent<BridgeMetalQuiltOutput>();
        Require(output != null, "Prepared Metal output is missing.");
        SessionState.SetBool(Prefix + "originalStart", output.startOnEnable);
        SessionState.SetBool(Prefix + "originalRunner", rig.GetComponent<MetalCurrentValidationRunner>().enabled);
        output.startOnEnable = true;
        rig.GetComponent<MetalCurrentValidationRunner>().enabled = false;
        SessionState.SetString(Prefix + "sourceProfile", ProfileSignature(rig));
        SessionState.SetBool(Prefix + "oldOptionsEnabled", EditorSettings.enterPlayModeOptionsEnabled);
        SessionState.SetInt(Prefix + "oldOptions", (int)EditorSettings.enterPlayModeOptions);
        bool disabled = MetalCurrentValidationBuilder.Argument("-validationNoReload") == "true";
        EditorSettings.enterPlayModeOptionsEnabled = disabled;
        EditorSettings.enterPlayModeOptions = disabled ? EnterPlayModeOptions.DisableDomainReload : EnterPlayModeOptions.None;
        SessionState.SetString(Prefix + "root", root);
        SessionState.SetInt(Prefix + "initialReloads", SessionState.GetInt(Prefix + "reloads", 0));
        Save(new Report
        {
            pipeline = MetalCurrentValidationBuilder.Argument("-validationPipeline") ?? "URP",
            domainReloadDisabled = disabled, requestedCycles = 2,
            unityVersion = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            gpu = SystemInfo.graphicsDeviceName, pid = Process.GetCurrentProcess().Id
        });
        Phase(1);
    }

    static void Tick()
    {
        int phase = SessionState.GetInt(Prefix + "phase", 0);
        if (phase == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try
        {
            Require(Elapsed < 90d, "Transition timed out at phase " + phase);
            Report report = Load();
            var rig = Rig();
            Require(rig != null, "Transition Rig is missing.");
            var output = rig.GetComponent<BridgeMetalQuiltOutput>();
            Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "Metal is required.");
            if (phase >= 2 && phase <= 8)
                Require(string.IsNullOrEmpty(BridgeMetalEditorPreview.LastError), BridgeMetalEditorPreview.LastError);
            if (phase >= 6 && phase <= 8) Require(string.IsNullOrEmpty(output.LastError), output.LastError);
            if (phase == 1)
            {
                string expected = report.pipeline == "HDRP" ? "HDRenderPipeline" : "UniversalPipeline";
                if (Shader.globalRenderPipeline != expected || GraphicsSettings.currentRenderPipeline == null) return;
                report.actualPipeline = RenderPipelineManager.currentPipeline?.GetType().Name ?? string.Empty;
                report.shaderTag = Shader.globalRenderPipeline;
                Require(report.actualPipeline == (report.pipeline == "HDRP" ? "HDRenderPipeline" : "UniversalRenderPipeline"), "Wrong active pipeline.");
                if (!Retired || EditorApplication.timeSinceStartup < SessionState.GetFloat(Prefix + "nextStart", 0)) return;
                if (!BridgeMetalEditorPreview.Start(rig, true, out string error))
                {
                    SessionState.SetString(Prefix + "startError", error);
                    SessionState.SetFloat(Prefix + "nextStart", (float)EditorApplication.timeSinceStartup + 0.5f);
                    return;
                }
                report.cycles.Add(new Cycle { index = report.cycles.Count + 1 });
                Save(report); Phase(2);
            }
            else if (phase == 2 && BridgeMetalEditorPreview.PresentedFrames >= 15)
            {
                Require(BridgeMetalEditorPreview.OutputToDevice && BridgeMetalEditorPreview.IsActive, "Device Preview is not active.");
                CapturePreview(report.cycles[report.cycles.Count - 1]);
                report.cycles[report.cycles.Count - 1].targets = MetalRenderTargetValidation.Capture(report.pipeline);
                Save(report);
                BridgeMetalEditorPreview.TogglePause(); Phase(3);
            }
            else if (phase == 3 && Elapsed > 0.4d)
            {
                SessionState.SetInt(Prefix + "pausedFrames", (int)BridgeMetalEditorPreview.PresentedFrames); Phase(4);
            }
            else if (phase == 4 && Elapsed > 0.6d)
            {
                Require(BridgeMetalEditorPreview.IsPaused && BridgeMetalEditorPreview.PresentedFrames == SessionState.GetInt(Prefix + "pausedFrames", -1), "Device Preview pause did not hold.");
                report.cycles[report.cycles.Count - 1].paused = true; Save(report);
                BridgeMetalEditorPreview.TogglePause(); Phase(5);
            }
            else if (phase == 5 && BridgeMetalEditorPreview.PresentedFrames >= SessionState.GetInt(Prefix + "pausedFrames", 0) + 3)
            {
                var cycle = report.cycles[report.cycles.Count - 1];
                cycle.resumed = true; cycle.previewFrames = (int)BridgeMetalEditorPreview.PresentedFrames;
                cycle.previewRendered = BridgeMetalEditorPreview.RenderedFrames;
                cycle.hiddenProducersBeforePlay = HiddenProducers(rig);
                Require(cycle.hiddenProducersBeforePlay > 0, "No hidden Preview producer was observed.");
                cycle.previewActiveAtPlayRequest = BridgeMetalEditorPreview.IsActive && BridgeMetalEditorPreview.OutputToDevice;
                Require(cycle.previewActiveAtPlayRequest, "Preview must still be active when Play is requested.");
                cycle.sourceProfileUnchangedBeforePlay = ProfileSignature(rig) == SessionState.GetString(Prefix + "sourceProfile", "");
                Require(cycle.sourceProfileUnchangedBeforePlay, "Preview changed the source Rig profile.");
                SessionState.SetString(Prefix + "beforePlaySettings", JsonUtility.ToJson(rig.Renderer.settings));
                cycle.reloadsBeforePlay = SessionState.GetInt(Prefix + "reloads", 0);
                Save(report); Phase(6);
                EditorApplication.EnterPlaymode();
            }
            else if (phase == 6 && EditorApplication.isPlaying)
            {
                var cycle = report.cycles[report.cycles.Count - 1];
                cycle.previewStoppedInPlay = Retired;
                if (!cycle.previewStoppedInPlay || !output.IsRunning || output.CompletedFrames < 30) return;
                cycle.hiddenProducersInPlay = HiddenProducers(rig);
                Require(cycle.hiddenProducersInPlay == 0, "Hidden Preview producer remains in Play.");
                cycle.reloadsInPlay = SessionState.GetInt(Prefix + "reloads", 0);
                Require(report.domainReloadDisabled ? cycle.reloadsInPlay == cycle.reloadsBeforePlay : cycle.reloadsInPlay > cycle.reloadsBeforePlay,
                    "Observed domain reload does not match the requested configuration.");
                cycle.playFrames = (int)output.CompletedFrames; Save(report);
                // Prevent automatic re-start while stopping; exercise the normal Stop then Edit return.
                output.startOnEnable = false;
                output.StopOutput(); Phase(7);
            }
            else if (phase == 7 && !output.IsRunning && !output.IsClosing && Retired)
            {
                var cycle = report.cycles[report.cycles.Count - 1];
                cycle.profileRestoredInPlay = ProfileSignature(rig) == SessionState.GetString(Prefix + "sourceProfile", "");
                cycle.settingsRestoredInPlay = JsonUtility.ToJson(rig.Renderer.settings) == SessionState.GetString(Prefix + "beforePlaySettings", "");
                Require(cycle.profileRestoredInPlay && cycle.settingsRestoredInPlay, "Stop did not restore profile/settings before leaving Play.");
                Save(report); Phase(8); EditorApplication.ExitPlaymode();
            }
            else if (phase == 8 && !EditorApplication.isPlayingOrWillChangePlaymode && Retired)
            {
                var cycle = report.cycles[report.cycles.Count - 1];
                cycle.returnedToEdit = true;
                cycle.pendingResources = BridgeMetalEditorPreview.PendingResourceCount;
                cycle.pendingRetirement = BridgeMetalQuiltOutput.PendingRetirementCount;
                cycle.sourceProfileRestored = ProfileSignature(rig) == SessionState.GetString(Prefix + "sourceProfile", "");
                Require(cycle.sourceProfileRestored, "Source Rig profile changed after Play.");
                Save(report);
                if (report.cycles.Count < report.requestedCycles)
                { output.startOnEnable = true; Phase(1); }
                else Finish(null);
            }
            else if (phase == 90 && !EditorApplication.isPlayingOrWillChangePlaymode && !output.IsClosing && Retired)
                Finish(SessionState.GetString(Prefix + "failure", "Transition failed."));
        }
        catch (Exception exception)
        {
            if (phase != 90) SessionState.SetString(Prefix + "failure", exception.ToString());
            else SessionState.SetString(Prefix + "cleanupError", exception.ToString());
            BridgeMetalEditorPreview.Stop();
            var rig = Rig();
            var output = rig == null ? null : rig.GetComponent<BridgeMetalQuiltOutput>();
            if (output != null) { output.startOnEnable = false; output.StopOutput(); }
            if (phase == 90) Finish(SessionState.GetString(Prefix + "failure", "Cleanup failed."));
            else
            { Phase(90); if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode(); }
        }
    }

    static void CapturePreview(Cycle cycle)
    {
        RenderTexture source = BridgeMetalEditorPreview.Texture;
        Require(source != null, "Preview texture is missing.");
        RenderTexture old = RenderTexture.active;
        var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = source;
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            image.Apply(false, false);
            foreach (Color32 pixel in image.GetPixels32())
            {
                if (pixel.r > 2 * pixel.g && pixel.r > 2 * pixel.b && pixel.r > 24) ++cycle.redPixels;
                if (pixel.g > 2 * pixel.r && pixel.g > 2 * pixel.b && pixel.g > 24) ++cycle.greenPixels;
                if (pixel.r > 180 && pixel.b > 180 && pixel.g < 80) ++cycle.magentaPixels;
            }
            Require(cycle.redPixels > 1000 && cycle.greenPixels > 1000 && cycle.magentaPixels == 0, "Preview image gate failed.");
            File.WriteAllBytes(Path.Combine(SessionState.GetString(Prefix + "root", ""), "preview-" + cycle.index + ".png"), image.EncodeToPNG());
            Report report = Load(); report.cycles[report.cycles.Count - 1] = cycle; Save(report);
        }
        finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
    }

    static int HiddenProducers(ViewWeaveRig source)
    {
        int count = 0;
        foreach (ViewWeaveRig value in Resources.FindObjectsOfTypeAll<ViewWeaveRig>())
            if (value != source && value.gameObject.name == "ViewWeave Editor Quilt Producer" &&
                (value.gameObject.hideFlags & HideFlags.DontSave) == HideFlags.DontSave) ++count;
        return count;
    }

    static string ProfileSignature(ViewWeaveRig rig)
    {
        QuiltQualityProfile p = rig.Profile;
        return p.QuiltWidth + ":" + p.QuiltHeight + ":" + p.Columns + ":" + p.Rows + ":" + p.Views + ":" +
            p.Aspect.ToString("R", CultureInfo.InvariantCulture) + ":" + p.ViewCone.ToString("R", CultureInfo.InvariantCulture);
    }

    static void Finish(string error)
    {
        Report report = Load();
        report.passed = error == null; report.error = error ?? string.Empty;
        report.reloadCount = SessionState.GetInt(Prefix + "reloads", 0) - SessionState.GetInt(Prefix + "initialReloads", 0);
        report.pendingResources = BridgeMetalEditorPreview.PendingResourceCount;
        report.pendingRetirement = BridgeMetalQuiltOutput.PendingRetirementCount;
        report.cleanupError = SessionState.GetString(Prefix + "cleanupError", "");
        EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(Prefix + "oldOptionsEnabled", false);
        EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(Prefix + "oldOptions", 0);
        var rig = Rig();
        var output = rig == null ? null : rig.GetComponent<BridgeMetalQuiltOutput>();
        report.cleanupProven = !EditorApplication.isPlayingOrWillChangePlaymode && Retired &&
            (output == null || !output.IsRunning && !output.IsClosing);
        if (rig != null && report.cleanupProven)
        {
            rig.GetComponent<BridgeMetalQuiltOutput>().startOnEnable = SessionState.GetBool(Prefix + "originalStart", false);
            rig.GetComponent<MetalCurrentValidationRunner>().enabled = SessionState.GetBool(Prefix + "originalRunner", true);
        }
        else if (rig != null)
        {
            output.startOnEnable = false;
            rig.GetComponent<MetalCurrentValidationRunner>().enabled = false;
        }
        report.exitRequested = report.cleanupProven;
        if (!report.cleanupProven) report.passed = false;
        if (report.domainReloadDisabled && report.reloadCount != 0) report.passed = false;
        File.WriteAllText(Path.Combine(SessionState.GetString(Prefix + "root", ""), "transition.json"), JsonUtility.ToJson(report, true));
        Phase(0);
        UnityEngine.Debug.Log("Metal Preview transition " + (report.passed ? "PASS" : "FAIL"));
        if (report.exitRequested) EditorApplication.Exit(report.passed ? 0 : 1);
        else UnityEngine.Debug.LogError("Validation cleanup is unproven; the Editor is retained for inspection.");
    }
}
