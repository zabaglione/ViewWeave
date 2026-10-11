using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ViewWeave;
using ViewWeave.Editor;

/// <summary>Live Bridge geometry checks across preview, reload, Play and Editor restart.</summary>
[InitializeOnLoad]
public static class BridgeDisplayGeometryValidation
{
    const string Prefix = "ViewWeave.DisplayGeometryValidation.";
    const string ScenePath = "Assets/SamplePlayerValidationGenerated/MetalCurrent/Validation.unity";
    [Serializable] public sealed class Check
    {
        public string stage, source;
        public float aspect, focusRatio, nearRatio, farRatio;
        public bool isConnected;
    }
    [Serializable] public sealed class Result
    {
        public bool passed, cloneRejected, temporaryIdentityCleared, awakeRecognitionRetained, reloadsDisabled;
        public string error, pipeline, unityVersion, graphicsApi;
        public float bridgeAspect;
        public int domainReloads, pendingResources, pendingRetirement;
        public List<Check> checks = new List<Check>();
    }

    static BridgeDisplayGeometryValidation()
    {
        SessionState.SetInt(Prefix + "reloads", SessionState.GetInt(Prefix + "reloads", 0) + 1);
        EditorApplication.update += Tick;
    }
    static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    static bool Retired => BridgeMetalEditorPreview.PendingResourceCount == 0 && BridgeMetalQuiltOutput.PendingRetirementCount == 0;
    static double Elapsed => EditorApplication.timeSinceStartup - SessionState.GetFloat(Prefix + "since", 0);
    static void Switch(int phase)
    { SessionState.SetInt(Prefix + "phase", phase); SessionState.SetFloat(Prefix + "since", (float)EditorApplication.timeSinceStartup); }
    static ViewWeaveRig Rig(string name) => GameObject.Find(name)?.GetComponent<ViewWeaveRig>();
    static float Expected => SessionState.GetFloat(Prefix + "aspect", 0);
    static Result Load() => JsonUtility.FromJson<Result>(SessionState.GetString(Prefix + "result", "{}"));
    static void Save(Result result) => SessionState.SetString(Prefix + "result", JsonUtility.ToJson(result));

    public static void Begin()
    {
        MetalCurrentValidationBuilder.Prepare();
        var rig = Rig("Quilt Rig");
        rig.ApplyProfile(QuiltQualityProfile.Native(960, 540, 8, 6, 1f, 40f));
        rig.SetOutput(null);
        rig.GetComponent<MetalCurrentValidationRunner>().enabled = false;
        var playOnly = UnityEngine.Object.Instantiate(rig.gameObject);
        playOnly.name = "Play Only Rig";
        playOnly.AddComponent<BridgeGeometryAwakeProbe>();
        EditorSceneManager.SaveScene(rig.gameObject.scene, ScenePath);
        SessionState.SetString(Prefix + "root", MetalCurrentValidationBuilder.Argument("-validationRoot"));
        SessionState.SetInt(Prefix + "initialReloads", SessionState.GetInt(Prefix + "reloads", 0));
        bool noReload = MetalCurrentValidationBuilder.Argument("-validationNoReload") == "true";
        SessionState.SetBool(Prefix + "previousOptionsEnabled", EditorSettings.enterPlayModeOptionsEnabled);
        SessionState.SetInt(Prefix + "previousOptions", (int)EditorSettings.enterPlayModeOptions);
        EditorSettings.enterPlayModeOptionsEnabled = noReload;
        EditorSettings.enterPlayModeOptions = noReload
            ? EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload : EnterPlayModeOptions.None;
        Save(new Result { pipeline = MetalCurrentValidationBuilder.Argument("-validationPipeline") ?? "URP",
            reloadsDisabled = noReload,
            unityVersion = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString() });
        Switch(1);
    }

    static void Record(string stage, ViewWeaveRig rig, bool? connected = null)
    {
        Require(rig != null, "Expected Rig is missing: " + stage);
        Require(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out QuiltSettings effective,
            out QuiltEditorDisplayProfile display, out string error), error);
        Camera camera = rig.Renderer.EffectiveCamera;
        Require(QuiltFrustumGeometry.TryBuild(camera.transform, effective, camera.nearClipPlane, camera.farClipPlane,
            QuiltFramingResolver.EffectiveFieldOfView(rig.Renderer), out QuiltFrustumGeometry.Geometry geometry), "Gizmo geometry is invalid.");
        var check = new Check { stage = stage, source = display.source.ToString(), aspect = effective.viewAspect,
            focusRatio = Ratio(geometry.focusPlane), nearRatio = Ratio(geometry.nearPlane), farRatio = Ratio(geometry.farPlane),
            isConnected = display.isConnected };
        Result result = Load(); result.checks.Add(check); Save(result);
        foreach (float actual in new[] { check.aspect, check.focusRatio, check.nearRatio, check.farRatio })
            Require(Mathf.Abs(actual - Expected) < 0.00001f, stage + " does not match Bridge aspect: " + actual);
        if (connected.HasValue) Require(display.isConnected == connected.Value, stage + " reports the wrong connection state.");
    }
    static float Ratio(Vector3[] plane) => Vector3.Distance(plane[0], plane[3]) / Vector3.Distance(plane[0], plane[1]);

    static void Tick()
    {
        int phase = SessionState.GetInt(Prefix + "phase", 0);
        if (phase == 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try
        {
            Require(Elapsed < 90, "Geometry validation timed out at phase " + phase);
            ViewWeaveRig rig = Rig("Quilt Rig");
            if (phase == 1)
            {
                Require(BridgeMetalEditorPreview.Start(rig, true, out string error), error);
                Switch(2);
            }
            else if (phase == 2 && BridgeMetalEditorPreview.PresentedFrames >= 6)
            {
                SessionState.SetFloat(Prefix + "aspect", (float)BridgeMetalEditorPreview.Layout.AspectRatio);
                Require(Expected > 0 && Expected < 1f, "This live regression requires a portrait display reported by Bridge.");
                Record("device-active", rig, true);
                BridgeMetalEditorPreview.TogglePause(); Switch(3);
            }
            else if (phase == 3 && Elapsed > 0.5)
            {
                Require(BridgeMetalEditorPreview.IsPaused, "Device preview must remain paused.");
                Record("device-paused", rig, true);
                SessionState.SetInt(Prefix + "frames", (int)BridgeMetalEditorPreview.PresentedFrames);
                BridgeMetalEditorPreview.TogglePause(); Switch(4);
            }
            else if (phase == 4 && BridgeMetalEditorPreview.PresentedFrames >= SessionState.GetInt(Prefix + "frames", 0) + 3)
            { Record("device-resumed", rig, true); BridgeMetalEditorPreview.Stop(); Switch(5); }
            else if (phase == 5 && Retired)
            {
                Record("device-stopped", rig, false);
                Require(rig.Renderer.settings.viewAspect == 1f, "The stored local aspect must stay unchanged.");
                Require(BridgeMetalEditorPreview.Start(rig, false, out string error), error); Switch(6);
            }
            else if (phase == 6 && BridgeMetalEditorPreview.RenderedFrames >= 3)
            {
                Require(Mathf.Abs((float)BridgeMetalEditorPreview.Layout.AspectRatio - Expected) < 0.00001f, "Local preview must match remembered Bridge geometry.");
                Record("local-preview", rig, false); BridgeMetalEditorPreview.Stop(); Switch(7);
            }
            else if (phase == 7 && Retired)
            {
                Record("local-stopped", rig, false);
                Switch(8); EditorUtility.RequestScriptReload();
            }
            else if (phase == 8 && Elapsed > 1d)
            {
                Require(SessionState.GetInt(Prefix + "reloads", 0) > SessionState.GetInt(Prefix + "initialReloads", 0), "Script reload was not observed.");
                Record("domain-reloaded", rig, false);
                Require(QuiltDisplaySettingsResolver.TryResolve(Rig("Play Only Rig").Renderer, out _,
                    out QuiltEditorDisplayProfile before, out _), "Play-only authoring geometry lookup failed.");
                Require(before.source == QuiltDisplaySettingsSource.LocalRendererSettings,
                    "The Play-only Rig must not borrow another Rig's history.");
                Switch(9); EditorApplication.EnterPlaymode();
            }
            else if (phase == 9 && EditorApplication.isPlaying && Retired)
            {
                Application.runInBackground = true;
                ViewWeaveRig playOnly = Rig("Play Only Rig");
                var probe = playOnly.GetComponent<BridgeGeometryAwakeProbe>();
                Require(probe.startedSuccessfully, "Bridge acquisition in Awake failed: " + probe.error);
                Record("awake-recognition-after-stop", playOnly, false);
                Result result = Load(); result.awakeRecognitionRetained = true; Save(result);
                Require(playOnly.GetComponent<BridgeMetalQuiltOutput>().StartOutput(), playOnly.GetComponent<BridgeMetalQuiltOutput>().LastError);
                Switch(10);
            }
            else if (phase == 10)
            {
                ViewWeaveRig playOnly = Rig("Play Only Rig");
                var output = playOnly.GetComponent<BridgeMetalQuiltOutput>();
                Require(string.IsNullOrEmpty(output.LastError), output.LastError);
                if (output.CompletedFrames < 6) return;
                Require(Mathf.Abs(output.Profile.Aspect - Expected) < 0.00001f, "Play output must read fresh Bridge calibration.");
                Record("first-recognition-in-play", playOnly, true);
                output.StopOutput(); Switch(11);
            }
            else if (phase == 11 && Retired)
            {
                ViewWeaveRig playOnly = Rig("Play Only Rig");
                Record("play-output-stopped", playOnly, false);
                var clone = UnityEngine.Object.Instantiate(playOnly.gameObject);
                clone.name = "Runtime Clone";
                var cloneRig = clone.GetComponent<ViewWeaveRig>();
                Require(QuiltDisplaySettingsResolver.TryResolve(cloneRig.Renderer, out _, out QuiltEditorDisplayProfile cloneProfile, out _), "Clone lookup failed.");
                Require(cloneProfile.source == QuiltDisplaySettingsSource.LocalRendererSettings, "Runtime clone must not inherit authoring history.");
                Result result = Load(); result.cloneRejected = true; Save(result);
                UnityEngine.Object.Destroy(clone);
                Switch(12); EditorApplication.ExitPlaymode();
            }
            else if (phase == 12 && !EditorApplication.isPlayingOrWillChangePlaymode && Retired)
            {
                Record("after-play-exit", rig, false);
                ViewWeaveRig playOnly = Rig("Play Only Rig");
                Record("play-only-after-exit", playOnly, false);
                Require(!rig.TryGetEditorDisplayIdentity(rig.GetComponent<BridgeMetalQuiltOutput>(), rig.Renderer, out _) &&
                    !playOnly.TryGetEditorDisplayIdentity(playOnly.GetComponent<BridgeMetalQuiltOutput>(), playOnly.Renderer, out _), "Temporary Play identity must be cleared.");
                Result result = Load(); result.temporaryIdentityCleared = true; Save(result);
                Finish(null);
                Selection.activeGameObject = rig.gameObject;
                SceneView sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null) sceneView.LookAt(new Vector3(0, 0, 3f), Quaternion.identity, 3f, false, true);
                SceneView.RepaintAll();
            }
        }
        catch (Exception exception)
        {
            BridgeMetalEditorPreview.Stop();
            foreach (var output in UnityEngine.Object.FindObjectsByType<BridgeMetalQuiltOutput>(FindObjectsSortMode.None)) output.StopOutput();
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
            Finish(exception.ToString());
        }
    }

    static void Finish(string error)
    {
        Result result = Load(); result.passed = error == null; result.error = error ?? string.Empty;
        result.bridgeAspect = Expected;
        result.domainReloads = SessionState.GetInt(Prefix + "reloads", 0) - SessionState.GetInt(Prefix + "initialReloads", 0);
        result.pendingResources = BridgeMetalEditorPreview.PendingResourceCount;
        result.pendingRetirement = BridgeMetalQuiltOutput.PendingRetirementCount;
        EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(Prefix + "previousOptionsEnabled", false);
        EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(Prefix + "previousOptions", 0);
        string root = SessionState.GetString(Prefix + "root", "");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "geometry.json"), JsonUtility.ToJson(result, true));
        Switch(0);
        Debug.Log("Bridge geometry validation " + (result.passed ? "PASS" : "FAIL"));
    }

    public static void VerifyReopenedEditor()
    {
        string root = MetalCurrentValidationBuilder.Argument("-validationRoot");
        try
        {
            Result result = JsonUtility.FromJson<Result>(File.ReadAllText(Path.Combine(root, "geometry.json")));
            Require(result.passed, "The initial live run must pass.");
            EditorSceneManager.OpenScene(ScenePath);
            SessionState.SetFloat(Prefix + "aspect", result.bridgeAspect);
            Save(result);
            Record("editor-reopened", Rig("Quilt Rig"), false);
            Record("play-only-editor-reopened", Rig("Play Only Rig"), false);
            File.WriteAllText(Path.Combine(root, "reopened.json"), JsonUtility.ToJson(Load(), true));
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(root, "reopen-error.txt"), exception.ToString());
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Tests/Bridge Geometry/Exit Validation Editor")]
    static void Exit() => EditorApplication.Exit(0);

    [MenuItem("Tests/Bridge Geometry/Show Retained Geometry")]
    static void ShowRetainedGeometry()
    {
        ViewWeaveRig rig = Rig("Quilt Rig");
        Selection.activeGameObject = rig.gameObject;
        SceneView view = EditorWindow.GetWindow<SceneView>();
        view.LookAt(new Vector3(0, 0, 3f), Quaternion.identity, 3f, true, true);
        view.Focus();
        SceneView.RepaintAll();
    }
}
