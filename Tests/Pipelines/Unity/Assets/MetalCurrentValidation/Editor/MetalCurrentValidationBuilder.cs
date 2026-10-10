using System;
using System.IO;
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
            Prepare();
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
