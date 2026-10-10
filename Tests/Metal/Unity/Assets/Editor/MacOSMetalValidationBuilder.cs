#if UNITY_EDITOR
using System;
using System.IO;
using ViewWeave;
using ViewWeave.URP;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

public static class MacOSMetalValidationBuilder
{
    const string GeneratedFolder = "Assets/ViewWeaveMetalValidation";
    const string ScenePath = GeneratedFolder + "/ViewWeaveMetalValidation.unity";
    const string RendererPath = GeneratedFolder + "/ViewWeaveMetalRenderer.asset";
    const string PipelinePath = GeneratedFolder + "/ViewWeaveMetalPipeline.asset";
    const string CubeMaterialPath = GeneratedFolder + "/MetalValidationCube.mat";
    const string SphereMaterialPath = GeneratedFolder + "/MetalValidationSphere.mat";

    [MenuItem("Tools/ViewWeave/Metal Validation/Prepare Scene")]
    public static void Prepare()
    {
        Scene scene = CreateAndSaveScene();
        EditorSceneManager.SetActiveScene(scene);
        Selection.activeGameObject = GameObject.Find("ViewWeaveMetalValidation");
        Debug.Log("ViewWeave macOS Metal validation scene prepared: " + ScenePath);
    }

    [MenuItem("Tools/ViewWeave/Metal Validation/Build Apple Silicon Player")]
    public static void Build()
    {
        SceneSetup[] oldSceneSetup = EditorSceneManager.GetSceneManagerSetup();
        EditorBuildSettingsScene[] oldBuildScenes = EditorBuildSettings.scenes;
        RenderPipelineAsset oldGraphicsPipeline = GraphicsSettings.defaultRenderPipeline;
        RenderPipelineAsset oldQualityPipeline = QualitySettings.renderPipeline;
        BuildTarget oldTarget = EditorUserBuildSettings.activeBuildTarget;
        bool oldMetalRendering = PlayerSettings.MTRendering;
        bool oldRunInBackground = PlayerSettings.runInBackground;
        int oldArchitecture = PlayerSettings.GetArchitecture(NamedBuildTarget.Standalone);
        bool oldDefaultGraphicsApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX);
        GraphicsDeviceType[] oldGraphicsApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
        Scene scene = default;
        bool targetChanged = false;
        Exception buildError = null;
        try
        {
            scene = CreateAndSaveScene();
            UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            AssetDatabase.SaveAssets();

            MetalShaderValidation.Run();

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX))
                throw new InvalidOperationException("Unity could not switch to the macOS standalone target");
            targetChanged = true;

            PlayerSettings.MTRendering = true;
            PlayerSettings.runInBackground = true;
            PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, (int)OSArchitecture.ARM64);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };

            string outputPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../../.local/macos-validation/ViewWeaveMetal.app"));
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("macOS player build failed: " + report.summary.result);
            Debug.Log("ViewWeave Apple Silicon Metal validation player built: " + outputPath);
        }
        catch (Exception exception)
        {
            buildError = exception;
            Debug.LogException(exception);
            throw;
        }
        finally
        {
            try
            {
                EditorBuildSettings.scenes = oldBuildScenes;
                GraphicsSettings.defaultRenderPipeline = oldGraphicsPipeline;
                QualitySettings.renderPipeline = oldQualityPipeline;
                AssetDatabase.SaveAssets();
                PlayerSettings.MTRendering = oldMetalRendering;
                PlayerSettings.runInBackground = oldRunInBackground;
                PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, oldArchitecture);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, oldDefaultGraphicsApis);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, oldGraphicsApis);
                if (targetChanged && oldTarget != BuildTarget.NoTarget)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(oldTarget), oldTarget);

                if (HasRestorableSceneSetup(oldSceneSetup))
                {
                    if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                    EditorSceneManager.RestoreSceneManagerSetup(oldSceneSetup);
                }
                else if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.SetActiveScene(scene);
                }
                else if (!SceneManager.GetActiveScene().IsValid())
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            catch (Exception cleanupException)
            {
                if (buildError == null) throw;
                Debug.LogException(cleanupException);
            }
        }
    }

    static bool HasRestorableSceneSetup(SceneSetup[] setup)
    {
        if (setup == null) return false;
        foreach (SceneSetup entry in setup)
            if (entry.isLoaded && entry.isActive) return true;
        return false;
    }

    static Scene CreateAndSaveScene()
    {
        EnsureGeneratedFolder();
        UniversalRendererData rendererData = LoadOrCreateRendererData();
        UniversalRenderPipelineAsset pipeline = LoadOrCreatePipeline(rendererData);
        Material cubeMaterial = LoadOrCreateMaterial(CubeMaterialPath, "MetalValidation.Cube", new Color(0.9f, 0.18f, 0.06f, 1f));
        Material sphereMaterial = LoadOrCreateMaterial(SphereMaterialPath, "MetalValidation.Sphere", new Color(0.05f, 0.55f, 0.95f, 1f));
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        if (!scene.IsValid() || !scene.isLoaded)
        {
            Scene active = SceneManager.GetActiveScene();
            bool blankInitialScene = SceneManager.sceneCount == 1 && string.IsNullOrEmpty(active.path) && !active.isDirty;
            scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                Application.isBatchMode || blankInitialScene ? NewSceneMode.Single : NewSceneMode.Additive);
        }
        else
            foreach (GameObject existingRoot in scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(existingRoot);
        GameObject root = new GameObject("ViewWeaveMetalValidation");
        GameObject cube = CreatePrimitive(root.transform, PrimitiveType.Cube, "RotatingCube",
            new Vector3(-0.55f, 0.35f, 0.6f), new Vector3(0.9f, 0.9f, 0.9f), cubeMaterial);
        CreatePrimitive(root.transform, PrimitiveType.Sphere, "BlueSphere",
            new Vector3(0.65f, 0.2f, 1.2f), Vector3.one * 0.8f, sphereMaterial);
        CreatePrimitive(root.transform, PrimitiveType.Plane, "Ground",
            new Vector3(0f, -0.38f, 0.5f), new Vector3(0.18f, 1f, 0.18f), cubeMaterial);

        GameObject lightObject = new GameObject("DirectionalLight");
        lightObject.transform.SetParent(root.transform, false);
        lightObject.transform.rotation = Quaternion.Euler(38f, -28f, 0f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 2.2f;

        GameObject previewObject = new GameObject("PreviewCamera");
        previewObject.transform.SetParent(root.transform, false);
        previewObject.transform.SetPositionAndRotation(new Vector3(0f, 1.1f, -6.2f), Quaternion.Euler(5f, 0f, 0f));
        Camera previewCamera = previewObject.AddComponent<Camera>();
        previewCamera.tag = "MainCamera";
        previewCamera.clearFlags = CameraClearFlags.SolidColor;
        previewCamera.backgroundColor = new Color(0.035f, 0.045f, 0.075f, 1f);
        previewCamera.fieldOfView = 48f;
        previewObject.AddComponent<UniversalAdditionalCameraData>();

        GameObject quiltObject = new GameObject("QuiltRig");
        quiltObject.transform.SetParent(root.transform, false);
        quiltObject.transform.SetPositionAndRotation(new Vector3(0f, 0.8f, -6.2f), Quaternion.Euler(5f, 0f, 0f));
        Camera quiltCamera = quiltObject.AddComponent<Camera>();
        quiltCamera.enabled = false;
        quiltCamera.clearFlags = CameraClearFlags.SolidColor;
        quiltCamera.backgroundColor = previewCamera.backgroundColor;
        quiltCamera.fieldOfView = previewCamera.fieldOfView;
        UniversalAdditionalCameraData cameraData = quiltObject.AddComponent<UniversalAdditionalCameraData>();
        cameraData.renderType = CameraRenderType.Base;
        UrpQuiltCamera guard = quiltObject.AddComponent<UrpQuiltCamera>();
        ViewWeaveRig rig;
        BridgeMetalQuiltOutput output;
        GraphicsSettings.defaultRenderPipeline = null;
        QualitySettings.renderPipeline = null;
        try
        {
            rig = quiltObject.AddComponent<ViewWeaveRig>();
            output = quiltObject.AddComponent<BridgeMetalQuiltOutput>();
            output.rig = rig;
            output.quality = QuiltQualityPreset.Preview;
            output.applicationName = "ViewWeave Metal Validation";
            output.startOnEnable = true;

            rig.ApplyProfile(new QuiltQualityProfile(960, 540, 8, 6, 48, 16f / 9f, 40f));
            rig.SetGuard(guard);
            rig.SetOutput(output);
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("ViewWeaveRig could not configure for the URP Metal scene");
        }
        finally
        {
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
        }
        if (!rig.ConfigureComponents()) throw new InvalidOperationException("ViewWeaveRig could not configure after URP activation");
        QuiltRenderer renderer = quiltObject.GetComponent<QuiltRenderer>();
        renderer.settings.fieldOfView = quiltCamera.fieldOfView;
        renderer.settings.cameraDistance = 6.2f;

        MacOSMetalValidationHarness harness = root.AddComponent<MacOSMetalValidationHarness>();
        harness.rotatingObject = cube.transform;
        harness.output = output;

        EditorSceneManager.SetActiveScene(scene);
        if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("Validation scene could not be saved");
        AssetDatabase.SaveAssets();
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        return scene;
    }

    static void EnsureGeneratedFolder()
    {
        if (AssetDatabase.IsValidFolder(GeneratedFolder)) return;
        if (!AssetDatabase.IsValidFolder("Assets/ViewWeaveMetalValidation"))
            AssetDatabase.CreateFolder("Assets", "ViewWeaveMetalValidation");
    }

    static UniversalRendererData LoadOrCreateRendererData()
    {
        UniversalRendererData data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererPath);
        if (data != null) return data;
        data = ScriptableObject.CreateInstance<UniversalRendererData>();
        data.name = "ViewWeave Metal Renderer";
        data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
            "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
        if (data.postProcessData == null) throw new InvalidOperationException("URP PostProcessData asset is unavailable");
        AssetDatabase.CreateAsset(data, RendererPath);
        return data;
    }

    static UniversalRenderPipelineAsset LoadOrCreatePipeline(UniversalRendererData data)
    {
        UniversalRenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
        if (pipeline != null) return pipeline;
        pipeline = UniversalRenderPipelineAsset.Create(data);
        pipeline.name = "ViewWeave Metal Validation URP";
        pipeline.supportsHDR = false;
        pipeline.msaaSampleCount = 1;
        pipeline.renderScale = 1f;
        AssetDatabase.CreateAsset(pipeline, PipelinePath);
        return pipeline;
    }

    static Material LoadOrCreateMaterial(string path, string materialName, Color color)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) throw new InvalidOperationException("URP Lit shader is unavailable");
        material = new Material(shader) { name = materialName };
        material.SetColor("_BaseColor", color);
        material.SetFloat("_Smoothness", 0.3f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    static GameObject CreatePrimitive(Transform parent, PrimitiveType type, string name,
        Vector3 localPosition, Vector3 localScale, Material material)
    {
        GameObject item = GameObject.CreatePrimitive(type);
        item.name = name;
        item.transform.SetParent(parent, false);
        item.transform.localPosition = localPosition;
        item.transform.localScale = localScale;
        item.GetComponent<Renderer>().sharedMaterial = material;
        return item;
    }

}
#endif
