using System;
using System.IO;
using LookingGlass.BridgeWorkerValidation;
using LookingGlass.Community;
using LookingGlass.Community.URP;
using LookingGlass.Community.HDRP;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace LookingGlass.BridgeWorkerValidation.Editor
{
    public static class BridgeWorkerValidationBuilder
    {
        const string Generated = "Assets/BridgeWorkerValidationGenerated";
        const string ScenePath = Generated + "/BridgeWorkerValidation.unity";
        const string DemoScenePath = Generated + "/ViewWeaveDemo.unity";

        public static void BuildDemo()
        {
            string output = Arg("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            SceneSetup[] oldSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorBuildSettingsScene[] oldScenes = EditorBuildSettings.scenes;
            RenderPipelineAsset oldGraphics = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            ColorSpace oldColorSpace = PlayerSettings.colorSpace;
            Scene scene = default;
            try
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "BridgeWorkerValidationGenerated"));
                AssetDatabase.Refresh();
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SetActiveScene(scene);
                UniversalRenderPipelineAsset pipeline = CreatePipeline("Demo", GPUResidentDrawerMode.InstancedDrawing);
                Material red = CreateMaterial("ViewWeaveDemo.Red", new Color(.75f, .05f, .03f, 1f));
                Material green = CreateMaterial("ViewWeaveDemo.Green", Color.green);
                GameObject root = new GameObject("ViewWeaveDemo.Root");
                GameObject lightObject = new GameObject("ViewWeaveDemo.Light");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2f;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                GameObject cameraObject = new GameObject("ViewWeaveDemo.Camera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.localPosition = Vector3.zero;
                cameraObject.SetActive(false);
                Camera source = cameraObject.AddComponent<Camera>();
                source.enabled = false;
                source.clearFlags = CameraClearFlags.SolidColor;
                source.backgroundColor = Color.black;
                source.useOcclusionCulling = false;
                cameraObject.AddComponent<UniversalAdditionalCameraData>();
                UrpQuiltCamera guard = cameraObject.AddComponent<UrpQuiltCamera>();
                QuiltRenderer quilt = cameraObject.AddComponent<QuiltRenderer>();
                BridgeWorkerQuiltOutput outputComponent = cameraObject.AddComponent<BridgeWorkerQuiltOutput>();
                outputComponent.initializeOnEnable = false;
                outputComponent.enabled = true;
                ViewWeaveRig rig = cameraObject.AddComponent<ViewWeaveRig>();
                rig.SetGuard(guard);
                rig.SetOutput(outputComponent);
                ViewWeaveBridgeSession session = cameraObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = true;
                cameraObject.SetActive(true);
                BridgeWorkerDemoRuntime demo = cameraObject.AddComponent<BridgeWorkerDemoRuntime>();
                quilt.sourceCamera = source;
                quilt.GuardBehaviour = guard;
                quilt.OutputBehaviour = outputComponent;
                quilt.autoRender = false;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                CreateDemoGeometry(root.transform, red, green);
                SerializedObject sessionSerialized = new SerializedObject(session);
                SerializedProperty quality = sessionSerialized.FindProperty("quality");
                if (quality != null) quality.enumValueIndex = (int)QuiltQualityPreset.Native;
                sessionSerialized.ApplyModifiedPropertiesWithoutUndo();
                SerializedObject demoSerialized = new SerializedObject(demo);
                demoSerialized.FindProperty("session").objectReferenceValue = session;
                demoSerialized.FindProperty("output").objectReferenceValue = outputComponent;
                demoSerialized.FindProperty("quilt").objectReferenceValue = quilt;
                demoSerialized.ApplyModifiedPropertiesWithoutUndo();
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                EditorSceneManager.SaveScene(scene, DemoScenePath, false);
                BuildPlayer(output, DemoScenePath);
            }
            finally
            {
                PlayerSettings.colorSpace = oldColorSpace;
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                EditorBuildSettings.scenes = oldScenes;
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (oldSetup != null && oldSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
            }
        }

        public static void Build()
        {
            string output = Arg("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            SceneSetup[] oldSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorBuildSettingsScene[] oldScenes = EditorBuildSettings.scenes;
            RenderPipelineAsset oldGraphics = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            Scene scene = default;
            try
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "BridgeWorkerValidationGenerated"));
                AssetDatabase.Refresh();
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SetActiveScene(scene);
                UniversalRenderPipelineAsset pipelineOff = CreatePipeline("Off", GPUResidentDrawerMode.Disabled);
                UniversalRenderPipelineAsset pipelineOn = CreatePipeline("On", GPUResidentDrawerMode.InstancedDrawing);
                Material red = CreateMaterial("BridgeWorker.Red", new Color(.7f, .04f, .03f, 1f));
                Material green = CreateMaterial("BridgeWorker.Green", Color.green);
                GameObject root = new GameObject("BridgeWorkerValidation.Root");
                GameObject lightObject = new GameObject("BridgeWorkerValidation.Light");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2f;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                GameObject cameraObject = new GameObject("BridgeWorkerValidation.Camera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 0f, -6f), Quaternion.identity);
                cameraObject.SetActive(false);
                Camera source = cameraObject.AddComponent<Camera>();
                source.enabled = false;
                source.clearFlags = CameraClearFlags.SolidColor;
                source.backgroundColor = Color.black;
                source.useOcclusionCulling = false;
                cameraObject.AddComponent<UniversalAdditionalCameraData>();
                UrpQuiltCamera guard = cameraObject.AddComponent<UrpQuiltCamera>();
                QuiltRenderer quilt = cameraObject.AddComponent<QuiltRenderer>();
                BridgeWorkerQuiltOutput outputComponent = cameraObject.AddComponent<BridgeWorkerQuiltOutput>();
                outputComponent.initializeOnEnable = false;
                outputComponent.enabled = true;
                ViewWeaveRig rig = cameraObject.AddComponent<ViewWeaveRig>();
                rig.SetGuard(guard);
                rig.SetOutput(outputComponent);
                ViewWeaveBridgeSession session = cameraObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = false;
                cameraObject.SetActive(true);
                BridgeWorkerValidationRunner runner = cameraObject.AddComponent<BridgeWorkerValidationRunner>();
                quilt.sourceCamera = source;
                quilt.GuardBehaviour = guard;
                quilt.OutputBehaviour = outputComponent;
                quilt.autoRender = false;
                quilt.settings.cameraDistance = 6f;
                quilt.settings.fieldOfView = 50f;
                CreateGeometry(root.transform, red);
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "BridgeWorkerValidation.DynamicGreenSphere";
                sphere.transform.SetParent(root.transform, false);
                sphere.transform.localPosition = new Vector3(-.6f, .5f, -.5f);
                sphere.GetComponent<Renderer>().sharedMaterial = green;
                SerializedObject serialized = new SerializedObject(runner);
                serialized.FindProperty("quilt").objectReferenceValue = quilt;
                serialized.FindProperty("output").objectReferenceValue = outputComponent;
                serialized.FindProperty("rig").objectReferenceValue = rig;
                serialized.FindProperty("session").objectReferenceValue = session;
                serialized.FindProperty("grdOffPipeline").objectReferenceValue = pipelineOff;
                serialized.FindProperty("grdOnPipeline").objectReferenceValue = pipelineOn;
                serialized.FindProperty("sourceCamera").objectReferenceValue = source;
                serialized.FindProperty("dynamicSphere").objectReferenceValue = sphere.transform;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                GraphicsSettings.defaultRenderPipeline = pipelineOff;
                QualitySettings.renderPipeline = pipelineOff;
                EditorSceneManager.SaveScene(scene, ScenePath, false);
                GraphicsSettings.defaultRenderPipeline = pipelineOn;
                QualitySettings.renderPipeline = pipelineOn;
                BuildPlayer(output);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                EditorBuildSettings.scenes = oldScenes;
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (oldSetup != null && oldSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
            }
        }

        public static void BuildHdrp()
        {
            string output = Arg("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            SceneSetup[] oldSetup = EditorSceneManager.GetSceneManagerSetup();
            EditorBuildSettingsScene[] oldScenes = EditorBuildSettings.scenes;
            RenderPipelineAsset oldGraphics = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            ColorSpace oldColorSpace = PlayerSettings.colorSpace;
            Scene scene = default;
            try
            {
                Directory.CreateDirectory(Path.Combine(Application.dataPath, "BridgeWorkerValidationGenerated"));
                AssetDatabase.Refresh();
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SetActiveScene(scene);
                HDRenderPipelineAsset pipelineOff = CreateHdrpPipeline("Off", GPUResidentDrawerMode.Disabled);
                HDRenderPipelineAsset pipelineOn = CreateHdrpPipeline("On", GPUResidentDrawerMode.InstancedDrawing);
                Material red = CreateHdrpMaterial("BridgeWorker.HDRP.Red", new Color(.7f, .04f, .03f, 1f));
                Material green = CreateHdrpMaterial("BridgeWorker.HDRP.Green", Color.green);
                GameObject root = new GameObject("BridgeWorkerValidation.HDRP.Root");
                GameObject lightObject = new GameObject("BridgeWorkerValidation.HDRP.Light");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2f;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                lightObject.AddComponent<HDAdditionalLightData>();
                light.lightUnit = LightUnit.Lux;
                light.intensity = 10f;
                GameObject cameraObject = new GameObject("BridgeWorkerValidation.HDRP.Camera");
                cameraObject.layer = 31;
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 0f, -6f), Quaternion.identity);
                cameraObject.SetActive(false);
                Camera source = cameraObject.AddComponent<Camera>();
                source.enabled = false;
                source.clearFlags = CameraClearFlags.SolidColor;
                source.backgroundColor = Color.black;
                source.useOcclusionCulling = false;
                HDAdditionalCameraData cameraData = cameraObject.AddComponent<HDAdditionalCameraData>();
                cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = Color.black;
                cameraData.volumeLayerMask = 1 << 31;
                HdrpQuiltCamera guard = cameraObject.AddComponent<HdrpQuiltCamera>();
                QuiltRenderer quilt = cameraObject.AddComponent<QuiltRenderer>();
                BridgeWorkerQuiltOutput outputComponent = cameraObject.AddComponent<BridgeWorkerQuiltOutput>();
                outputComponent.initializeOnEnable = false;
                outputComponent.enabled = true;
                ViewWeaveRig rig = cameraObject.AddComponent<ViewWeaveRig>();
                rig.SetGuard(guard);
                rig.SetOutput(outputComponent);
                ViewWeaveBridgeSession session = cameraObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = false;
                cameraObject.SetActive(true);
                BridgeWorkerValidationRunner runner = cameraObject.AddComponent<BridgeWorkerValidationRunner>();
                quilt.sourceCamera = source;
                quilt.GuardBehaviour = guard;
                quilt.OutputBehaviour = outputComponent;
                quilt.autoRender = false;
                quilt.settings.cameraDistance = 6f;
                quilt.settings.fieldOfView = 50f;
                CreateGeometry(root.transform, red);
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "BridgeWorkerValidation.HDRP.DynamicGreenSphere";
                sphere.transform.SetParent(root.transform, false);
                sphere.transform.localPosition = new Vector3(-.6f, .5f, -.5f);
                sphere.GetComponent<Renderer>().sharedMaterial = green;
                CreateHdrpExposure(root.transform);
                SerializedObject serialized = new SerializedObject(runner);
                serialized.FindProperty("quilt").objectReferenceValue = quilt;
                serialized.FindProperty("output").objectReferenceValue = outputComponent;
                serialized.FindProperty("rig").objectReferenceValue = rig;
                serialized.FindProperty("session").objectReferenceValue = session;
                serialized.FindProperty("grdOffPipeline").objectReferenceValue = pipelineOff;
                serialized.FindProperty("grdOnPipeline").objectReferenceValue = pipelineOn;
                serialized.FindProperty("sourceCamera").objectReferenceValue = source;
                serialized.FindProperty("dynamicSphere").objectReferenceValue = sphere.transform;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                GraphicsSettings.defaultRenderPipeline = pipelineOff;
                QualitySettings.renderPipeline = pipelineOff;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                EditorSceneManager.SaveScene(scene, ScenePath, false);
                GraphicsSettings.defaultRenderPipeline = pipelineOn;
                QualitySettings.renderPipeline = pipelineOn;
                BuildPlayer(output);
            }
            finally
            {
                PlayerSettings.colorSpace = oldColorSpace;
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                EditorBuildSettings.scenes = oldScenes;
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (oldSetup != null && oldSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
            }
        }

        static void BuildPlayer(string output)
        {
            BuildPlayer(output, ScenePath);
        }

        static void BuildPlayer(string output, string scenePath)
        {
            EditorBuildSettingsScene[] oldScenes = EditorBuildSettings.scenes;
            bool oldMt = PlayerSettings.MTRendering;
            bool oldJobs = PlayerSettings.graphicsJobs;
            bool oldStatic = PlayerSettings.GetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64);
            bool oldApplicationRun = Application.runInBackground;
            bool oldRun = PlayerSettings.runInBackground;
            bool oldFrameTimingStats = PlayerSettings.enableFrameTimingStats;
            bool oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            GraphicsDeviceType[] oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            var graphicsSettings = assets.Length == 0 ? null : new SerializedObject(assets[0]);
            var brgStripping = graphicsSettings == null ? null : graphicsSettings.FindProperty("m_BrgStripping");
            if (brgStripping == null) throw new InvalidOperationException("GraphicsSettings.m_BrgStripping is unavailable");
            int oldBrgStripping = brgStripping.intValue;
            try
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
                PlayerSettings.MTRendering = true;
                PlayerSettings.graphicsJobs = false;
                Application.runInBackground = true;
                PlayerSettings.runInBackground = true;
                PlayerSettings.enableFrameTimingStats = true;
                PlayerSettings.SetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                brgStripping.intValue = (int)BatchRendererGroupStrippingMode.KeepAll;
                graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
                BuildPlayerOptions options = new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Bridge worker player build failed: " + report.summary.result);
            }
            finally
            {
                brgStripping.intValue = oldBrgStripping;
                graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
                EditorBuildSettings.scenes = oldScenes;
                PlayerSettings.MTRendering = oldMt;
                PlayerSettings.graphicsJobs = oldJobs;
                PlayerSettings.SetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64, oldStatic);
                Application.runInBackground = oldApplicationRun;
                PlayerSettings.runInBackground = oldRun;
                PlayerSettings.enableFrameTimingStats = oldFrameTimingStats;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, oldDefaultApis);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, oldApis);
            }
        }

        static UniversalRenderPipelineAsset CreatePipeline(string suffix, GPUResidentDrawerMode mode)
        {
            UniversalRendererData data = ScriptableObject.CreateInstance<UniversalRendererData>();
            data.renderingMode = RenderingMode.ForwardPlus;
            UniversalRenderPipelineAsset asset = UniversalRenderPipelineAsset.Create(data);
            asset.name = "BridgeWorkerValidation.URP." + suffix;
            asset.useSRPBatcher = true;
            asset.gpuResidentDrawerMode = mode;
            asset.gpuResidentDrawerEnableOcclusionCullingInCameras = false;
            asset.smallMeshScreenPercentage = 0f;
            CreateAsset(data, Generated + "/BridgeWorkerValidation." + suffix + ".renderer.asset");
            CreateAsset(asset, Generated + "/BridgeWorkerValidation." + suffix + ".asset");
            return asset;
        }

        static HDRenderPipelineAsset CreateHdrpPipeline(string suffix, GPUResidentDrawerMode mode)
        {
            HDRenderPipelineAsset asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.name = "BridgeWorkerValidation.HDRP." + suffix;
            asset.gpuResidentDrawerMode = mode;
            var settings = asset.currentPlatformRenderPipelineSettings;
            settings.gpuResidentDrawerSettings.enableOcclusionCullingInCameras = false;
            settings.gpuResidentDrawerSettings.smallMeshScreenPercentage = 0f;
            asset.currentPlatformRenderPipelineSettings = settings;
            CreateAsset(asset, Generated + "/BridgeWorkerValidation.HDRP." + suffix + ".asset");
            return asset;
        }

        static Material CreateHdrpMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("HDRP/Lit");
            if (shader == null) throw new InvalidOperationException("HDRP Lit shader unavailable");
            Material material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", .25f);
            CreateAsset(material, Generated + "/" + name + ".mat");
            return material;
        }

        static void CreateHdrpExposure(Transform parent)
        {
            GameObject volumeObject = new GameObject("BridgeWorkerValidation.HDRP.FixedExposure");
            volumeObject.layer = 31;
            volumeObject.transform.SetParent(parent, false);
            Volume volume = volumeObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
            Exposure exposure = profile.Add<Exposure>(true);
            exposure.mode.value = ExposureMode.Fixed;
            exposure.mode.overrideState = true;
            exposure.fixedExposure.value = 4f;
            exposure.fixedExposure.overrideState = true;
            VisualEnvironment visualEnvironment = profile.Add<VisualEnvironment>(true);
            visualEnvironment.skyType.value = 0;
            visualEnvironment.skyType.overrideState = true;
            visualEnvironment.cloudType.value = 0;
            visualEnvironment.cloudType.overrideState = true;
            IndirectLightingController indirectLighting = profile.Add<IndirectLightingController>(true);
            indirectLighting.indirectDiffuseLightingMultiplier.value = 0f;
            indirectLighting.indirectDiffuseLightingMultiplier.overrideState = true;
            UnityEngine.Rendering.HighDefinition.Bloom bloom = profile.Add<UnityEngine.Rendering.HighDefinition.Bloom>(true);
            bloom.intensity.value = 0f;
            bloom.intensity.overrideState = true;
            UnityEngine.Rendering.HighDefinition.Tonemapping tonemapping = profile.Add<UnityEngine.Rendering.HighDefinition.Tonemapping>(true);
            tonemapping.mode.value = UnityEngine.Rendering.HighDefinition.TonemappingMode.Neutral;
            tonemapping.mode.overrideState = true;
            UnityEngine.Rendering.HighDefinition.MotionBlur motionBlur = profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
            motionBlur.active = true;
            motionBlur.intensity.value = 0f;
            motionBlur.intensity.overrideState = true;
            UnityEngine.Rendering.HighDefinition.ScreenSpaceReflection reflection = profile.Add<UnityEngine.Rendering.HighDefinition.ScreenSpaceReflection>(true);
            reflection.enabled.value = false;
            reflection.enabled.overrideState = true;
            GlobalIllumination gi = profile.Add<GlobalIllumination>(true);
            gi.enable.value = false;
            gi.enable.overrideState = true;
            VolumetricClouds clouds = profile.Add<VolumetricClouds>(true);
            clouds.enable.value = false;
            clouds.enable.overrideState = true;
            Fog fog = profile.Add<Fog>(true);
            fog.enabled.value = false;
            fog.enabled.overrideState = true;
            fog.enableVolumetricFog.value = false;
            fog.enableVolumetricFog.overrideState = true;
            volume.sharedProfile = profile;
            string profilePath = Generated + "/BridgeWorkerValidation.HDRP.FixedExposure.profile.asset";
            CreateAsset(profile, profilePath);
            AddProfileComponentsToAsset(profile, profilePath);
        }

        static void AddProfileComponentsToAsset(VolumeProfile profile, string path)
        {
            if (profile == null) throw new InvalidOperationException("HDRP volume profile is null");
            foreach (VolumeComponent component in profile.components)
            {
                if (component == null) throw new InvalidOperationException("HDRP volume profile contains a null component");
                AssetDatabase.AddObjectToAsset(component, path);
                EditorUtility.SetDirty(component);
            }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
        }

        static Material CreateMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable");
            Material material = new Material(shader) { name = name };
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Smoothness", .25f);
            CreateAsset(material, Generated + "/" + name + ".mat");
            return material;
        }

        static Mesh CreateGeometry(Transform parent, Material material)
        {
            GameObject template = GameObject.CreatePrimitive(PrimitiveType.Cube);
            template.name = "BridgeWorkerValidation.MeshTemplate";
            template.transform.SetParent(parent, false);
            template.GetComponent<Renderer>().sharedMaterial = material;
            Mesh mesh = template.GetComponent<MeshFilter>().sharedMesh;
            for (int i = 0; i < 75; ++i)
            {
                GameObject item = new GameObject("BridgeWorkerValidation.Mesh." + i);
                item.transform.SetParent(parent, false);
                item.transform.localPosition = new Vector3((i % 15 - 7f) * .3f, (i / 15 - 2f) * .3f, .8f + (i % 3) * .1f);
                item.transform.localScale = Vector3.one * .2f;
                item.AddComponent<MeshFilter>().sharedMesh = mesh;
                item.AddComponent<MeshRenderer>().sharedMaterial = material;
            }
            UnityEngine.Object.DestroyImmediate(template);
            return mesh;
        }

        static void CreateDemoGeometry(Transform parent, Material red, Material green)
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "ViewWeaveDemo.ForegroundCube";
            cube.transform.SetParent(parent, false);
            cube.transform.localPosition = new Vector3(0f, 0f, 2.4f);
            cube.transform.localScale = new Vector3(1.4f, 1f, .6f);
            cube.GetComponent<Renderer>().sharedMaterial = red;
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "ViewWeaveDemo.BackgroundSphere";
            sphere.transform.SetParent(parent, false);
            sphere.transform.localPosition = new Vector3(0f, .35f, 3.2f);
            sphere.transform.localScale = Vector3.one * .75f;
            sphere.GetComponent<Renderer>().sharedMaterial = green;
        }

        static void CreateAsset(UnityEngine.Object asset, string path)
        {
            UnityEngine.Object old = AssetDatabase.LoadMainAssetAtPath(path);
            if (old != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(asset, path);
        }

        static string Arg(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
