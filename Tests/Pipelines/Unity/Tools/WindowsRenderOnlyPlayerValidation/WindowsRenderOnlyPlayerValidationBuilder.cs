#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using ViewWeave;
using ViewWeave.HDRP;
using ViewWeave.PlayerValidation;

namespace ViewWeave.WindowsRenderOnlyPlayerValidation.Editor
{
    public static class WindowsRenderOnlyPlayerValidationBuilder
    {
        const string GeneratedRoot = "Assets/WindowsRenderOnlyPlayerValidationGenerated";
        const string ScenePath = GeneratedRoot + "/WindowsRenderOnlyPlayerValidation.unity";

        public static void BuildHdrpD3D12Release()
        {
            string output = Argument("-buildOutput");
            if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("-buildOutput is required.");
            string api = Argument("-buildGraphicsApi") ?? "d3d12";
            if (!string.Equals(api, "d3d12", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only the HDRP D3D12 case is supported.");

            RenderPipelineAsset oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQualityPipeline = QualitySettings.renderPipeline;
            EditorBuildSettingsScene[] oldBuildScenes = EditorBuildSettings.scenes;
            BuildTarget oldBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            ColorSpace oldColorSpace = PlayerSettings.colorSpace;
            bool oldRunInBackground = PlayerSettings.runInBackground;
            bool oldMTRendering = PlayerSettings.MTRendering;
            bool oldGraphicsJobs = PlayerSettings.graphicsJobs;
            bool oldDefaultGraphicsApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            GraphicsDeviceType[] oldGraphicsApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            SceneSetup[] oldSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            bool hadOpenScene = HasLoadedScene(oldSceneSetup);
            Scene scene = default;
            HDRenderPipelineAsset pipeline = null;
            VolumeProfile profile = null;

            try
            {
                Directory.CreateDirectory(GeneratedRoot);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, hadOpenScene ? NewSceneMode.Additive : NewSceneMode.Single);
                EditorSceneManager.SetActiveScene(scene);

                pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                pipeline.name = "WindowsRenderOnlyPlayerValidation.HDRP";
                CreateAsset(pipeline, GeneratedRoot + "/WindowsRenderOnlyPlayerValidation.HDRP.asset");
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;

                profile = CreateExposureProfile();
                var root = new GameObject("WindowsRenderOnlyPlayerValidationRoot");
                var cameraObject = new GameObject("WindowsRenderOnlyPlayerValidationCamera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.localPosition = new Vector3(0f, 1.4f, -5f);
                cameraObject.transform.localRotation = Quaternion.identity;
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.useOcclusionCulling = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.fieldOfView = 50f;

                HDAdditionalCameraData cameraData = cameraObject.AddComponent<HDAdditionalCameraData>();
                cameraData.volumeLayerMask = 1 << 0;
                cameraData.customRenderingSettings = false;
                cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = Color.black;

                HdrpQuiltCamera guard = cameraObject.AddComponent<HdrpQuiltCamera>();
                guard.renderingMode = HdrpQuiltRenderingMode.IsolatedViewHistories;
                guard.exposureMode = HdrpQuiltExposureMode.ManualFixed;
                guard.fallbackFixedExposure = false;
                guard.fallbackFixedExposureEV = 4f;
                guard.adaptUnsupportedEffects = false;

                QuiltRenderer quilt = cameraObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = camera;
                quilt.GuardBehaviour = guard;
                quilt.autoRender = false;
                quilt.useRenderRequests = true;
                quilt.viewRenderMode = QuiltViewRenderMode.Full;
                quilt.settings.quiltWidth = 512;
                quilt.settings.quiltHeight = 512;
                quilt.settings.columns = 2;
                quilt.settings.rows = 2;
                quilt.settings.views = 4;
                quilt.settings.viewAspect = 1f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.cameraDistance = 6f;
                quilt.settings.viewCone = 12f;

                PlayerValidationOutput outputComponent = cameraObject.AddComponent<PlayerValidationOutput>();
                quilt.OutputBehaviour = outputComponent;

                Volume volume = new GameObject("WindowsRenderOnlyPlayerValidationExposure").AddComponent<Volume>();
                volume.gameObject.transform.SetParent(root.transform, false);
                volume.isGlobal = true;
                volume.priority = 100f;
                volume.weight = 1f;
                volume.sharedProfile = profile;

                GameObject red = GameObject.CreatePrimitive(PrimitiveType.Cube);
                red.name = "AsymmetricRedNearTarget";
                red.transform.SetParent(root.transform, false);
                red.transform.localPosition = new Vector3(-0.8f, 1.2f, 1f);
                red.transform.localRotation = Quaternion.Euler(8f, 12f, 4f);
                red.transform.localScale = new Vector3(1.1f, 0.95f, 0.7f);
                red.GetComponent<Renderer>().sharedMaterial = CreateLitMaterial("WindowsRenderOnly.Red", new Color(0.95f, 0.04f, 0.025f, 1f));

                GameObject green = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                green.name = "AsymmetricGreenFarTarget";
                green.transform.SetParent(root.transform, false);
                green.transform.localPosition = new Vector3(0.8f, 1.45f, 2.3f);
                green.transform.localScale = new Vector3(1.05f, 0.9f, 0.85f);
                green.GetComponent<Renderer>().sharedMaterial = CreateLitMaterial("WindowsRenderOnly.Green", new Color(0.025f, 0.88f, 0.06f, 1f));

                var lightObject = new GameObject("WindowsRenderOnlyDirectionalLight");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                lightObject.AddComponent<HDAdditionalLightData>();
                light.lightUnit = LightUnit.Lux;
                light.intensity = 10f;
                light.color = Color.white;
                lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.25f, 0.25f, 0.25f, 1f);
                RenderSettings.ambientIntensity = 1f;

                WindowsRenderOnlyPlayerValidationRunner runner = cameraObject.AddComponent<WindowsRenderOnlyPlayerValidationRunner>();
                SerializedObject serialized = new SerializedObject(runner);
                serialized.FindProperty("sourceCamera").objectReferenceValue = camera;
                serialized.FindProperty("quilt").objectReferenceValue = quilt;
                serialized.FindProperty("output").objectReferenceValue = outputComponent;
                serialized.FindProperty("hdrpGuard").objectReferenceValue = guard;
                serialized.FindProperty("globalVolume").objectReferenceValue = volume;
                serialized.FindProperty("movingTarget").objectReferenceValue = red.transform;
                serialized.FindProperty("fixtureDirectionalLight").objectReferenceValue = light;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                EditorSceneManager.SaveScene(scene, ScenePath, false);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.runInBackground = true;
                PlayerSettings.MTRendering = true;
                PlayerSettings.graphicsJobs = false;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D12 });
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64 &&
                    !EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64))
                    throw new InvalidOperationException("Could not select StandaloneWindows64.");

                AssetDatabase.SaveAssets();
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = Path.GetFullPath(output),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded)
                    throw new InvalidOperationException("HDRP D3D12 Release build failed: " + report.summary.result);
                Debug.Log("WINDOWS_RENDER_ONLY_PLAYER_BUILD_PASS api=Direct3D12 development=false pipeline=HDRP");
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldDefaultPipeline;
                QualitySettings.renderPipeline = oldQualityPipeline;
                EditorBuildSettings.scenes = oldBuildScenes;
                PlayerSettings.runInBackground = oldRunInBackground;
                PlayerSettings.colorSpace = oldColorSpace;
                PlayerSettings.MTRendering = oldMTRendering;
                PlayerSettings.graphicsJobs = oldGraphicsJobs;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, oldDefaultGraphicsApis);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, oldGraphicsApis);
                if (scene.IsValid() && scene.isLoaded)
                    try { EditorSceneManager.CloseScene(scene, true); }
                    catch (Exception error) { Debug.LogError("Render-only scene close failed: " + error.GetType().Name); }
                if (hadOpenScene && oldSceneSetup != null && oldSceneSetup.Length > 0)
                    try { EditorSceneManager.RestoreSceneManagerSetup(oldSceneSetup); }
                    catch (Exception error) { Debug.LogError("Render-only scene restore failed: " + error.GetType().Name); }
                if (oldBuildTarget != BuildTarget.NoTarget && EditorUserBuildSettings.activeBuildTarget != oldBuildTarget)
                    EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(oldBuildTarget), oldBuildTarget);
            }
        }

        static VolumeProfile CreateExposureProfile()
        {
            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.name = "WindowsRenderOnlyPlayerValidation.ExposureEV4";
            Exposure exposure = profile.Add<Exposure>(true);
            exposure.active = true;
            exposure.mode.value = ExposureMode.Fixed;
            exposure.mode.overrideState = true;
            exposure.fixedExposure.value = 4f;
            exposure.fixedExposure.overrideState = true;
            exposure.compensation.value = 0f;
            exposure.compensation.overrideState = true;
            Bloom bloom = profile.Add<Bloom>(true);
            bloom.active = true;
            bloom.intensity.value = 0f;
            bloom.intensity.overrideState = true;
            Fog fog = profile.Add<Fog>(true);
            fog.active = true;
            fog.enabled.value = false;
            fog.enabled.overrideState = true;
            VisualEnvironment environment = profile.Add<VisualEnvironment>(true);
            environment.active = true;
            environment.skyType.value = 0;
            environment.skyType.overrideState = true;
            environment.cloudType.value = 0;
            environment.cloudType.overrideState = true;
            IndirectLightingController indirectLighting = profile.Add<IndirectLightingController>(true);
            indirectLighting.active = true;
            indirectLighting.indirectDiffuseLightingMultiplier.value = 0f;
            indirectLighting.indirectDiffuseLightingMultiplier.overrideState = true;
            Tonemapping tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.active = true;
            tonemapping.mode.value = TonemappingMode.Neutral;
            tonemapping.mode.overrideState = true;
            string profilePath = GeneratedRoot + "/WindowsRenderOnlyPlayerValidation.ExposureEV4.asset";
            CreateAsset(profile, profilePath);
            foreach (VolumeComponent component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
            AssetDatabase.SaveAssets();
            VolumeProfile reloaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            Exposure persistedExposure;
            VisualEnvironment persistedEnvironment;
            IndirectLightingController persistedIndirectLighting;
            Tonemapping persistedTonemapping;
            if (reloaded == null || !reloaded.TryGet(out persistedExposure) || persistedExposure == null ||
                !persistedExposure.active || persistedExposure.mode.value != ExposureMode.Fixed ||
                !persistedExposure.mode.overrideState || !Mathf.Approximately(persistedExposure.fixedExposure.value, 4f) ||
                !persistedExposure.fixedExposure.overrideState ||
                !reloaded.TryGet(out persistedEnvironment) || persistedEnvironment == null || !persistedEnvironment.active ||
                !persistedEnvironment.skyType.overrideState || persistedEnvironment.skyType.value != 0 ||
                !persistedEnvironment.cloudType.overrideState || persistedEnvironment.cloudType.value != 0 ||
                !reloaded.TryGet(out persistedIndirectLighting) || persistedIndirectLighting == null || !persistedIndirectLighting.active ||
                !persistedIndirectLighting.indirectDiffuseLightingMultiplier.overrideState ||
                !Mathf.Approximately(persistedIndirectLighting.indirectDiffuseLightingMultiplier.value, 0f) ||
                !reloaded.TryGet(out persistedTonemapping) || persistedTonemapping == null || !persistedTonemapping.active ||
                !persistedTonemapping.mode.overrideState || persistedTonemapping.mode.value != TonemappingMode.Neutral)
                throw new InvalidOperationException("Saved HDRP Volume controls did not persist their configured values.");
            return reloaded;
        }

        static Material CreateLitMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("HDRP/Lit");
            if (shader == null) throw new InvalidOperationException("HDRP/Lit shader is unavailable.");
            var material = new Material(shader) { name = name, color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.15f);
            CreateAsset(material, GeneratedRoot + "/" + name + ".mat");
            return material;
        }

        static void CreateAsset(UnityEngine.Object value, string path)
        {
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(value, path);
            EditorUtility.SetDirty(value);
        }

        static bool HasLoadedScene(SceneSetup[] setup)
        {
            if (setup == null) return false;
            for (int i = 0; i < setup.Length; i++) if (setup[i].isLoaded) return true;
            return false;
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
#endif
