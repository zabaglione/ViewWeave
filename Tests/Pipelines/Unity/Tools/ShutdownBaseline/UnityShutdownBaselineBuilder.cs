using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;

[InitializeOnLoad]
public static class UnityShutdownBaselineBuilder
{
    const string Generated = "Assets/Generated";
    const string RemovalKey = "ShutdownBaseline.RemovingPackages";
    static AddAndRemoveRequest removal;
    static ListRequest listing;
    static double deadline;
    static UnityShutdownBaselineBuilder()
    {
        if (SessionState.GetBool(RemovalKey, false)) EditorApplication.delayCall += ResumeRemoval;
    }
    static string Arg(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
        return null;
    }
    public static void RemoveViewWeave()
    {
        SessionState.SetBool(RemovalKey, true);
        deadline = EditorApplication.timeSinceStartup + 600d;
        removal = Client.AddAndRemove(Array.Empty<string>(), new[]
        {
            "com.zabaglione.lookingglass.viewweave.bridge", "com.zabaglione.lookingglass.viewweave.core",
            "com.zabaglione.lookingglass.viewweave.urp", "com.zabaglione.lookingglass.viewweave.hdrp"
        });
        EditorApplication.update += PollRemoval;
    }
    static void ResumeRemoval()
    {
        deadline = EditorApplication.timeSinceStartup + 600d;
        listing = Client.List();
        EditorApplication.update += PollRemoval;
    }
    static void PollRemoval()
    {
        if (EditorApplication.timeSinceStartup > deadline) { FinishRemoval(1); return; }
        if (removal != null)
        {
            if (!removal.IsCompleted) return;
            if (removal.Status != StatusCode.Success) { FinishRemoval(1); return; }
            removal = null; listing = Client.List();
        }
        if (listing == null || !listing.IsCompleted) return;
        bool valid = listing.Status == StatusCode.Success &&
            !listing.Result.Any(p => p.name.StartsWith("com.zabaglione.lookingglass.viewweave.", StringComparison.Ordinal));
        FinishRemoval(valid ? 0 : 1);
    }
    static void FinishRemoval(int code)
    {
        EditorApplication.update -= PollRemoval;
        SessionState.SetBool(RemovalKey, false);
        Debug.Log("Unity-only package isolation " + (code == 0 ? "PASS" : "FAIL"));
        EditorApplication.Exit(code);
    }

    public static void Prepare()
    {
        bool hdrp = Arg("-validationPipeline") == "HDRP";
        Directory.CreateDirectory(Generated); AssetDatabase.Refresh();
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderPipelineAsset pipeline;
        if (hdrp) pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
        else
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            AssetDatabase.CreateAsset(data, Generated + "/Renderer.asset");
            var urp = UniversalRenderPipelineAsset.Create(data); urp.msaaSampleCount = 4; pipeline = urp;
        }
        AssetDatabase.CreateAsset(pipeline, Generated + "/Pipeline.asset");
        PlayerSettings.colorSpace = ColorSpace.Linear;
        GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = pipeline;
        var camera = new GameObject("Baseline Camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
        if (hdrp)
        {
            var data = camera.gameObject.AddComponent<HDAdditionalCameraData>();
            data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; data.backgroundColorHDR = Color.black;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var exposure = profile.Add<Exposure>(true); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0f);
            AssetDatabase.CreateAsset(profile, Generated + "/Volume.asset");
            foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
            var volume = new GameObject("Fixed Exposure Volume").AddComponent<Volume>(); volume.isGlobal = true; volume.sharedProfile = profile;
        }
        else camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
        for (int i = 0; i < 2; ++i)
        {
            var item = GameObject.CreatePrimitive(i == 0 ? PrimitiveType.Cube : PrimitiveType.Sphere);
            item.transform.position = new Vector3(i == 0 ? -0.4f : 0.4f, 0f, 3f); item.transform.localScale = Vector3.one * 0.7f;
            var material = new Material(Shader.Find(hdrp ? "HDRP/Unlit" : "Universal Render Pipeline/Unlit"));
            material.SetColor(hdrp ? "_UnlitColor" : "_BaseColor", i == 0 ? Color.red : Color.green);
            AssetDatabase.CreateAsset(material, Generated + "/Object" + i + ".mat"); item.GetComponent<Renderer>().sharedMaterial = material;
        }
        var runner = camera.gameObject.AddComponent<UnityShutdownBaselineRunner>();
        runner.pipelineName = hdrp ? "HDRP" : "URP"; runner.reportDirectory = Arg("-validationRoot");
        EditorSceneManager.SaveScene(scene, Generated + "/Validation.unity"); AssetDatabase.SaveAssets();
    }
    public static void BuildWhenReady()
    {
        EditorSceneManager.OpenScene(Generated + "/Validation.unity");
        deadline = EditorApplication.timeSinceStartup + 90d; EditorApplication.update += BuildReady;
    }
    static void BuildReady()
    {
        bool hdrp = Arg("-validationPipeline") == "HDRP";
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (Shader.globalRenderPipeline != (hdrp ? "HDRenderPipeline" : "UniversalPipeline"))
        {
            if (EditorApplication.timeSinceStartup < deadline) { EditorApplication.QueuePlayerLoopUpdate(); SceneView.RepaintAll(); return; }
            EditorApplication.update -= BuildReady; EditorApplication.Exit(1); return;
        }
        EditorApplication.update -= BuildReady;
        try
        {
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, (int)OSArchitecture.ARM64);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Generated + "/Validation.unity" }, locationPathName = Arg("-buildOutput"),
                target = BuildTarget.StandaloneOSX,
                options = Arg("-validationConfiguration") == "Development" ? BuildOptions.Development : BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Baseline build failed.");
            EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
    }
}
