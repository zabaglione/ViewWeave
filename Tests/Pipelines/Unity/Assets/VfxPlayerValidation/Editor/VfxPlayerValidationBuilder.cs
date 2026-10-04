using System;
using System.IO;
using ViewWeave;
using ViewWeave.URP;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.VFX;
using ViewWeave.VfxPlayerValidation;

namespace ViewWeave.VfxPlayerValidation.Editor
{
    public static class VfxPlayerValidationBuilder
    {
        const string Generated = "Assets/VfxPlayerValidationGenerated";
        const string ScenePath = Generated + "/VfxPlayerValidation.unity";

        public static void Build()
        {
            string output = Arg("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            SceneSetup[] oldSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorBuildSettingsScene[] oldScenes = EditorBuildSettings.scenes;
            RenderPipelineAsset oldGraphics = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            Scene scene = default(Scene);
            try
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "VfxPlayerValidationGenerated"));
                AssetDatabase.Refresh();
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, HasLoaded(oldSetup) ? NewSceneMode.Additive : NewSceneMode.Single);
                EditorSceneManager.SetActiveScene(scene);
                UniversalRenderPipelineAsset off = Pipeline("VFX.URP.Off", GPUResidentDrawerMode.Disabled);
                UniversalRenderPipelineAsset on = Pipeline("VFX.URP.On", GPUResidentDrawerMode.InstancedDrawing);
                Material lit = MaterialAsset("VFX.Lit", new Color(.15f, .65f, .2f, 1f));
                VisualEffectAsset effectAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>("Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
                if (effectAsset == null) throw new InvalidOperationException("Simple_Burst.vfx is unavailable");

                GameObject root = new GameObject("VfxPlayerValidation.Root");
                root.SetActive(false);
                GameObject lightObject = new GameObject("VfxPlayerValidation.DirectionalLight");
                lightObject.transform.SetParent(root.transform, false);
                Light directional = lightObject.AddComponent<Light>();
                directional.type = LightType.Directional;
                directional.intensity = 2f;
                directional.shadows = LightShadows.None;
                lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                GameObject cameraObject = new GameObject("VfxPlayerValidation.Camera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 0f, -4f), Quaternion.identity);
                Camera source = cameraObject.AddComponent<Camera>();
                source.enabled = false;
                source.useOcclusionCulling = false;
                source.clearFlags = CameraClearFlags.SolidColor;
                source.backgroundColor = Color.black;
                source.fieldOfView = 50f;
                source.cullingMask = 1 << 31;
                cameraObject.AddComponent<UniversalAdditionalCameraData>();
                UrpQuiltCamera guard = cameraObject.AddComponent<UrpQuiltCamera>();
                QuiltRenderer quilt = cameraObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = source;
                quilt.GuardBehaviour = guard;
                quilt.autoRender = false;
                UrpVfxCameraDriver driver = cameraObject.AddComponent<UrpVfxCameraDriver>();
                driver.vfxLayers = 1 << 31;
                driver.driverFieldOfView = 90f;
                driver.runWhenManualRendering = false;
                driver.enableDriver = false;
                VfxPlayerValidationOutput outputComponent = cameraObject.AddComponent<VfxPlayerValidationOutput>();
                VfxPlayerValidationRunner runner = cameraObject.AddComponent<VfxPlayerValidationRunner>();

                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "VfxPlayerValidation.LitControl";
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(-.7f, 0f, 2.2f);
                cube.layer = 31;
                cube.GetComponent<Renderer>().sharedMaterial = lit;
                GameObject effectObject = new GameObject("VfxPlayerValidation.SimpleBurst");
                effectObject.transform.SetParent(root.transform, false);
                effectObject.transform.localPosition = new Vector3(0f, .5f, 2.2f);
                effectObject.layer = 31;
                VisualEffect effect = effectObject.AddComponent<VisualEffect>();
                effect.visualEffectAsset = effectAsset;
                effect.resetSeedOnPlay = false;
                effect.startSeed = 17u;
                effect.pause = true;
                VFXRenderer effectRenderer = effectObject.GetComponent<VFXRenderer>();

                quilt.settings.quiltWidth = 1024;
                quilt.settings.quiltHeight = 576;
                quilt.settings.columns = 8;
                quilt.settings.rows = 6;
                quilt.settings.views = 48;
                quilt.settings.viewAspect = 128f / 96f;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;

                SerializedObject serializedRunner = new SerializedObject(runner);
                Ref(serializedRunner, "grdOffPipeline", off);
                Ref(serializedRunner, "grdOnPipeline", on);
                Ref(serializedRunner, "quilt", quilt);
                Ref(serializedRunner, "sourceCamera", source);
                Ref(serializedRunner, "driver", driver);
                Ref(serializedRunner, "effect", effect);
                if (effectRenderer != null) Ref(serializedRunner, "effectRenderer", effectRenderer);
                Ref(serializedRunner, "output", outputComponent);
                quilt.OutputBehaviour = outputComponent;
                serializedRunner.ApplyModifiedPropertiesWithoutUndo();
                root.SetActive(true);
                GraphicsSettings.defaultRenderPipeline = off;
                QualitySettings.renderPipeline = off;
                EditorSceneManager.SaveScene(scene, ScenePath, false);
                GraphicsSettings.defaultRenderPipeline = on;
                QualitySettings.renderPipeline = on;
                BuildPlayer(output);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                EditorBuildSettings.scenes = oldScenes;
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (HasLoaded(oldSetup)) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
            }
        }

        static UniversalRenderPipelineAsset Pipeline(string name, GPUResidentDrawerMode mode)
        {
            UniversalRendererData data = ScriptableObject.CreateInstance<UniversalRendererData>();
            data.renderingMode = RenderingMode.ForwardPlus;
            UniversalRenderPipelineAsset asset = UniversalRenderPipelineAsset.Create(data);
            asset.name = name;
            asset.gpuResidentDrawerMode = mode;
            asset.gpuResidentDrawerEnableOcclusionCullingInCameras = false;
            asset.smallMeshScreenPercentage = 0f;
            asset.useSRPBatcher = true;
            CreateAsset(data, Generated + "/" + name + ".renderer.asset");
            CreateAsset(asset, Generated + "/" + name + ".asset");
            return asset;
        }

        static Material MaterialAsset(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable");
            Material material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            CreateAsset(material, Generated + "/" + name + ".mat");
            return material;
        }

        static void BuildPlayer(string output)
        {
            var oldScenes = EditorBuildSettings.scenes;
            var oldMt = PlayerSettings.MTRendering;
            var oldJobs = PlayerSettings.graphicsJobs;
            var oldStatic = PlayerSettings.GetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64);
            var oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldPlayerRun = PlayerSettings.runInBackground;
            var graphicsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            var graphicsSettings = graphicsAssets.Length == 0 ? null : new SerializedObject(graphicsAssets[0]);
            var brgStripping = graphicsSettings == null ? null : graphicsSettings.FindProperty("m_BrgStripping");
            if (brgStripping == null) throw new InvalidOperationException("GraphicsSettings.m_BrgStripping is unavailable");
            var oldBrgStripping = brgStripping.intValue;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            try
            {
                PlayerSettings.MTRendering = true;
                PlayerSettings.graphicsJobs = false;
                PlayerSettings.runInBackground = true;
                brgStripping.intValue = (int)BatchRendererGroupStrippingMode.KeepAll;
                graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
                PlayerSettings.SetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("VFX player build failed: " + report.summary.result);
            }
            finally
            {
                EditorBuildSettings.scenes = oldScenes;
                PlayerSettings.MTRendering = oldMt;
                PlayerSettings.graphicsJobs = oldJobs;
                PlayerSettings.runInBackground = oldPlayerRun;
                PlayerSettings.SetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64, oldStatic);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, oldDefaultApis);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, oldApis);
                brgStripping.intValue = oldBrgStripping;
                graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        static bool HasLoaded(SceneSetup[] setup) { if (setup == null) return false; for (var i = 0; i < setup.Length; ++i) if (setup[i].isLoaded) return true; return false; }
        static void CreateAsset(UnityEngine.Object asset, string path) { if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path); AssetDatabase.CreateAsset(asset, path); }
        static void Ref(SerializedObject serializedObject, string name, UnityEngine.Object value) { serializedObject.FindProperty(name).objectReferenceValue = value; }
        static string Arg(string key) { var args = Environment.GetCommandLineArgs(); for (var i = 0; i < args.Length - 1; ++i) if (args[i] == key) return args[i + 1]; return null; }
    }
}
