using System;
using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;
using ViewWeave.URP;
using ViewWeave.HDRP;

public static class MetalCurrentValidationBuilder
{
    const string Generated = "Assets/SamplePlayerValidationGenerated/MetalCurrent";
    static double buildDeadline;
    [Serializable] sealed class BuildEvidence
    {
        public string pipeline, activeAsset, activePipeline, shaderPipelineTag;
        public int configuredQualityAssets;
        public bool preparedInSeparateProcess, succeeded;
    }
    static bool IsHdrp => Argument("-validationPipeline") == "HDRP";
    static string ExpectedTag => IsHdrp ? "HDRenderPipeline" : "UniversalPipeline";
    static bool CorrectAsset(RenderPipelineAsset asset) => IsHdrp ? asset is HDRenderPipelineAsset : asset is UniversalRenderPipelineAsset;
    static bool CorrectPipeline() => IsHdrp ? RenderPipelineManager.currentPipeline is HDRenderPipeline : RenderPipelineManager.currentPipeline is UniversalRenderPipeline;
    public static string Argument(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
        return null;
    }

    public static void Prepare()
    {
        bool hdrp = Argument("-validationPipeline") == "HDRP";
        string root = Argument("-validationRoot");
        if (string.IsNullOrEmpty(root)) throw new InvalidOperationException("-validationRoot is required.");
        Directory.CreateDirectory(Generated);
        AssetDatabase.Refresh();
        foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { Generated }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!AssetDatabase.IsValidFolder(path)) AssetDatabase.DeleteAsset(path);
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        RenderPipelineAsset pipeline;
        if (hdrp) pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
        else
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            AssetDatabase.CreateAsset(data, Generated + "/Renderer.asset");
            var urp = UniversalRenderPipelineAsset.Create(data);
            urp.msaaSampleCount = 4;
            pipeline = urp;
        }
        AssetDatabase.CreateAsset(pipeline, Generated + "/Pipeline.asset");
        GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = null;
        PlayerSettings.colorSpace = ColorSpace.Linear;
        var reference = new GameObject("Reference Camera").AddComponent<Camera>();
        reference.clearFlags = CameraClearFlags.SolidColor;
        reference.backgroundColor = Color.black;
        reference.fieldOfView = 50f;
        if (hdrp)
        {
            var data = reference.gameObject.AddComponent<HDAdditionalCameraData>();
            data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            data.backgroundColorHDR = Color.black;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var exposure = profile.Add<Exposure>(true);
            exposure.mode.Override(ExposureMode.Fixed);
            exposure.fixedExposure.Override(0f);
            AssetDatabase.CreateAsset(profile, Generated + "/Volume.asset");
            foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
            var volume = new GameObject("Fixed Exposure Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.sharedProfile = profile;
        }
        else reference.gameObject.AddComponent<UniversalAdditionalCameraData>();
        var camera = new GameObject("Quilt Rig").AddComponent<Camera>();
        camera.CopyFrom(reference);
        camera.enabled = false;
        MonoBehaviour guard;
        if (hdrp)
        {
            camera.gameObject.AddComponent<HDAdditionalCameraData>();
            guard = camera.gameObject.AddComponent<HdrpQuiltCamera>();
        }
        else
        {
            camera.gameObject.AddComponent<UniversalAdditionalCameraData>();
            guard = camera.gameObject.AddComponent<UrpQuiltCamera>();
        }
        var rig = camera.gameObject.AddComponent<ViewWeaveRig>();
        var output = camera.gameObject.AddComponent<BridgeMetalQuiltOutput>();
        output.rig = rig;
        output.startOnEnable = false;
        output.quality = QuiltQualityPreset.Preview;
        output.applicationName = "ViewWeave Current Metal Validation";
        rig.SetGuard(guard);
        rig.SetOutput(output);
        rig.ApplyProfile(new QuiltQualityProfile(960, 540, 8, 6, 48, 16f / 9f, 40f));
        if (!rig.ConfigureComponents()) throw new InvalidOperationException("Rig configuration failed.");
        rig.Renderer.referenceCamera = reference;
        rig.Renderer.settings.cameraDistance = 3f;
        rig.Renderer.settings.fieldOfView = 50f;
        GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = pipeline;
        for (int i = 0; i < 2; i++)
        {
            var item = GameObject.CreatePrimitive(i == 0 ? PrimitiveType.Cube : PrimitiveType.Sphere);
            item.transform.position = new Vector3(i == 0 ? -0.4f : 0.4f, 0f, 3f);
            item.transform.localScale = Vector3.one * 0.7f;
            var shader = Shader.Find(hdrp ? "HDRP/Unlit" : "Universal Render Pipeline/Unlit");
            if (shader == null) throw new InvalidOperationException("Fixture shader missing.");
            var material = new Material(shader);
            material.SetColor(hdrp ? "_UnlitColor" : "_BaseColor", i == 0 ? Color.red : Color.green);
            AssetDatabase.CreateAsset(material, Generated + "/Object" + i + ".mat");
            item.GetComponent<Renderer>().sharedMaterial = material;
        }
        var runner = camera.gameObject.AddComponent<MetalCurrentValidationRunner>();
        runner.output = output;
        runner.pipelineName = hdrp ? "HDRP" : "URP";
        runner.reportDirectory = Path.GetFullPath(root);
        EditorSceneManager.SaveScene(scene, Generated + "/Validation.unity");
        AssetDatabase.SaveAssets();
        Selection.activeGameObject = camera.gameObject;
        Debug.Log("Metal current fixture prepared: " + runner.pipelineName);
    }

    // Open in a fresh, non-batch Editor after Prepare has saved the fixture and exited.
    public static void BuildWhenReady()
    {
        EditorSceneManager.OpenScene(Generated + "/Validation.unity");
        buildDeadline = EditorApplication.timeSinceStartup + 90d;
        EditorApplication.update += WaitForPipeline;
    }

    static void WaitForPipeline()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (!CorrectPipeline() || Shader.globalRenderPipeline != ExpectedTag)
        {
            if (EditorApplication.timeSinceStartup < buildDeadline)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                return;
            }
            EditorApplication.update -= WaitForPipeline;
            Debug.LogError("Prepared render pipeline did not become active: " + Shader.globalRenderPipeline);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.update -= WaitForPipeline;
        try { Build(); EditorApplication.Exit(0); }
        catch (Exception exception) { Debug.LogException(exception); EditorApplication.Exit(1); }
    }

    public static void Build()
    {
        string output = Argument("-buildOutput");
        if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required.");
        var oldGraphics = GraphicsSettings.defaultRenderPipeline;
        var oldQuality = QualitySettings.renderPipeline;
        var oldColor = PlayerSettings.colorSpace;
        var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
        bool oldDefault = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX);
        bool oldBackground = PlayerSettings.runInBackground;
        int oldArchitecture = PlayerSettings.GetArchitecture(NamedBuildTarget.Standalone);
        var oldSetup = EditorSceneManager.GetSceneManagerSetup();
        try
        {
            var prepared = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(Generated + "/Pipeline.asset");
            if (!CorrectAsset(prepared) || GraphicsSettings.currentRenderPipeline != prepared || !CorrectPipeline() ||
                Shader.globalRenderPipeline != ExpectedTag)
                throw new InvalidOperationException("The saved fixture pipeline must be active before building.");
            var assets = new List<RenderPipelineAsset>();
            QualitySettings.GetAllRenderPipelineAssetsForPlatform(NamedBuildTarget.Standalone.TargetName, ref assets);
            foreach (var asset in assets)
                if (asset != null && !CorrectAsset(asset)) throw new InvalidOperationException("Standalone quality settings contain a different render pipeline.");
            var evidence = new BuildEvidence
            {
                pipeline = IsHdrp ? "HDRP" : "URP", activeAsset = AssetDatabase.GetAssetPath(prepared),
                activePipeline = IsHdrp ? "HDRenderPipeline" : "UniversalRenderPipeline",
                shaderPipelineTag = Shader.globalRenderPipeline, configuredQualityAssets = assets.Count,
                preparedInSeparateProcess = true
            };
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, (int)OSArchitecture.ARM64);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Generated + "/Validation.unity" }, locationPathName = Path.GetFullPath(output),
                target = BuildTarget.StandaloneOSX,
                options = Argument("-validationConfiguration") == "Development" ? BuildOptions.Development : BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Player build failed: " + report.summary.result);
            evidence.succeeded = true;
            File.WriteAllText(Path.Combine(Argument("-validationRoot"), "build.json"), JsonUtility.ToJson(evidence, true));
        }
        finally
        {
            GraphicsSettings.defaultRenderPipeline = oldGraphics;
            QualitySettings.renderPipeline = oldQuality;
            PlayerSettings.colorSpace = oldColor;
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, oldApis);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, oldDefault);
            PlayerSettings.runInBackground = oldBackground;
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, oldArchitecture);
            if (oldSetup.Length > 0 && !string.IsNullOrEmpty(oldSetup[0].path)) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
            AssetDatabase.SaveAssets();
        }
    }
}
