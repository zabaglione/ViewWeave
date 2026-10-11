#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.HDRP;

namespace ViewWeave.GraphicsCompatibility
{
    [Explicit("Windows HDRP effect characterization; run this fixture explicitly.")]
    public sealed class WindowsHdrpEffectPixelTests
    {
        const int TileSize = 256;
        const int ViewCount = 4;
        Fixture activeFixture;
        AsyncOperation ownedSceneUnload;
        Scene ownedFixtureScene;

        enum Effect
        {
            Fxaa,
            Smaa,
            Taa,
            MotionBlur,
            Ssr,
            Ssgi,
            VolumetricFog,
            VolumetricClouds
        }

        [UnityTest] public IEnumerator FxaaChangesDiagonalEdgePixels() => Run(Effect.Fxaa);
        [UnityTest] public IEnumerator SmaaChangesDiagonalEdgePixels() => Run(Effect.Smaa);
        [UnityTest] public IEnumerator TaaAdvancesIndependentViewHistoriesAndChangesPixels() => Run(Effect.Taa);
        [UnityTest] public IEnumerator MotionBlurChangesMovingObjectPixels() => Run(Effect.MotionBlur);
        [UnityTest] public IEnumerator SsrChangesScreenSpaceReflectionPixels() => Run(Effect.Ssr);
        [UnityTest] public IEnumerator SsgiChangesIndirectReceiverPixels() => Run(Effect.Ssgi);
        [UnityTest] public IEnumerator VolumetricFogChangesScatteringPixels() => Run(Effect.VolumetricFog);
        [UnityTest] public IEnumerator VolumetricCloudsChangeSkyPixels() => Run(Effect.VolumetricClouds);

        IEnumerator Run(Effect effect)
        {
            RequireWindowsDirect3D();
            activeFixture = new Fixture(effect);
            activeFixture.Setup();
            yield return null;
            yield return null;

            activeFixture.SetEnabled(false);
            yield return activeFixture.RenderStage("off-a", effect == Effect.Taa ? 8 : 6);
            activeFixture.ResetViewHistories();
            activeFixture.SetEnabled(false);
            yield return activeFixture.RenderStage("off-b", effect == Effect.Taa ? 8 : 6);
            activeFixture.ResetViewHistories();
            activeFixture.SetEnabled(true);
            yield return activeFixture.RenderStage("on", effect == Effect.Taa ? 12 : 6);

            activeFixture.AssertEvidence();
        }

        [UnityTearDown]
        public IEnumerator WaitForOwnedFixtureSceneUnload()
        {
            string cleanupError = string.Empty;
            Fixture fixture = activeFixture;
            if (fixture != null)
            {
                ownedFixtureScene = fixture.FixtureScene;
                try { fixture.Dispose(); }
                catch (Exception exception) { cleanupError = exception.GetType().Name + ": " + exception.Message; }
                finally
                {
                    ownedSceneUnload = fixture.UnloadOperation;
                    ownedFixtureScene = fixture.FixtureScene;
                    activeFixture = null;
                }
            }

            double deadline = Time.realtimeSinceStartupAsDouble + 20.0;
            while (((ownedSceneUnload != null && !ownedSceneUnload.isDone) ||
                    (ownedFixtureScene.IsValid() && ownedFixtureScene.isLoaded)) &&
                   Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;

            Assert.IsTrue(ownedSceneUnload == null || ownedSceneUnload.isDone,
                "The effect fixture scene unload operation did not complete before the teardown deadline.");
            Assert.IsFalse(ownedFixtureScene.IsValid() && ownedFixtureScene.isLoaded,
                "The effect fixture scene remained loaded after the teardown deadline.");
            bool unloadDone = ownedSceneUnload != null && ownedSceneUnload.isDone;
            Debug.Log("HDRP_EFFECT_SCENE_TEARDOWN unloadOperationPresent=" + (ownedSceneUnload != null) +
                ", unloadDone=" + unloadDone +
                ", sceneLoaded=" + (ownedFixtureScene.IsValid() && ownedFixtureScene.isLoaded));
            Assert.IsTrue(string.IsNullOrEmpty(cleanupError),
                "The effect fixture cleanup threw " + cleanupError);
            ownedSceneUnload = null;
            ownedFixtureScene = default;
        }

        static void RequireWindowsDirect3D()
        {
            bool windowsEditor = Application.platform == RuntimePlatform.WindowsEditor;
            bool supportedApi = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            if (!windowsEditor || !supportedApi)
                Assert.Ignore("This test requires Windows Editor with Direct3D 11 or Direct3D 12.");
        }

        static bool TryGetExistingHDCamera(Camera camera, string historyChannelName, out HDCamera hdCamera)
        {
            Type channelType = typeof(HDCamera).GetNestedType("HistoryChannel", BindingFlags.NonPublic);
            if (channelType == null)
                throw new InvalidOperationException("HDRP HDCamera history channel type was not found.");
            MethodInfo tryGet = typeof(HDCamera).GetMethod("TryGet", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(Camera), typeof(HDCamera).MakeByRefType(), typeof(int), channelType }, null);
            if (tryGet == null)
                throw new InvalidOperationException("HDRP HDCamera.TryGet history lookup was not found.");

            object channel = Enum.Parse(channelType, historyChannelName);
            object[] arguments = { camera, null, 0, channel };
            bool found = (bool)tryGet.Invoke(null, arguments);
            hdCamera = found ? arguments[1] as HDCamera : null;
            return found && hdCamera != null;
        }

        sealed class Fixture
        {
            readonly Effect effect;
            readonly string outputDirectory;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            RenderPipelineAsset oldDefaultPipeline;
            RenderPipelineAsset oldQualityPipeline;
            RenderTexture oldActive;
            SceneState oldSceneState;
            GameObject root;
            GameObject movingObject;
            Camera camera;
            Camera referenceCamera;
            Camera ordinaryCamera;
            QuiltRenderer quilt;
            HdrpQuiltCamera guard;
            HDAdditionalCameraData cameraData;
            HDAdditionalCameraData referenceCameraData;
            HDAdditionalCameraData ordinaryCameraData;
            HDRenderPipelineAsset pipeline;
            VolumeProfile profile;
            MotionBlur motionBlur;
            Tonemapping tonemapping;
            Bloom bloom;
            ScreenSpaceReflection reflection;
            GlobalIllumination gi;
            Fog fog;
            VolumetricClouds clouds;
            Light directionalLight;
            Light fogSpotlight;
            GraphicsFixtureScope scope;
            Scene fixtureScene;
            RenderTexture cameraTarget;
            readonly Dictionary<string, ImagePair> captures = new Dictionary<string, ImagePair>();
            readonly Dictionary<string, bool> ordinaryTaaHistoryByStage = new Dictionary<string, bool>();
            readonly Dictionary<string, HDAdditionalCameraData.AntialiasingMode> ordinaryTaaModeByStage =
                new Dictionary<string, HDAdditionalCameraData.AntialiasingMode>();
            readonly List<RenderTexture>[] taaHistoryTextures =
                { new List<RenderTexture>(), new List<RenderTexture>(), new List<RenderTexture>(), new List<RenderTexture>() };
            int ordinaryRenderCount;
            int quiltRenderCount;
            bool motionBlurStageEnabled;
            bool neutralizeAntialiasingPostProcessing;
            bool ordinaryTaaHistoryAllocated;
            string ordinaryTaaHistoryTextureId = string.Empty;
            bool disposed;

            public AsyncOperation UnloadOperation => scope == null ? null : scope.UnloadOperation;
            public Scene FixtureScene => fixtureScene;

            struct SceneState
            {
                public Light sun;
                public Color ambient;
            }

            sealed class ImagePair
            {
                public PixelImage Camera;
                public PixelImage Quilt;
            }

            sealed class CameraState
            {
                public RenderTexture Target;
                public Vector3 Position;
                public Quaternion Rotation;
                public Matrix4x4 Projection;
                public HDAdditionalCameraData.AntialiasingMode Antialiasing;
                public bool[] FrameSettings;
                public bool[] FrameSettingOverrides;
            }

            sealed class PixelImage
            {
                public int Width;
                public int Height;
                public Color32[] Pixels;
                public bool AllPixelsFinite;
            }

            public Fixture(Effect value)
            {
                effect = value;
                string api = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12 ? "d3d12" : "d3d11";
                string run = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local",
                    "windows-current-feature-validation-20261011", "gpu-effects", value.ToString().ToLowerInvariant(), api, run));
            }

            public void Setup()
            {
                oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
                oldQualityPipeline = QualitySettings.renderPipeline;
                oldActive = RenderTexture.active;
                oldSceneState.sun = RenderSettings.sun;
                oldSceneState.ambient = RenderSettings.ambientLight;

                pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                RenderPipelineSettings settings = pipeline.currentPlatformRenderPipelineSettings;
                settings.supportedLitShaderMode = RenderPipelineSettings.SupportedLitShaderMode.ForwardOnly;
                settings.supportSSR = true;
                settings.supportSSGI = true;
                settings.supportVolumetrics = true;
                settings.supportVolumetricClouds = true;
                settings.supportMotionVectors = true;
                pipeline.currentPlatformRenderPipelineSettings = settings;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                owned.Add(pipeline);

                root = new GameObject("WindowsHdrpEffectPixelFixture");
                root.layer = GraphicsFixtureScope.Layer;
                camera = new GameObject("EffectCamera").AddComponent<Camera>();
                camera.transform.SetParent(root.transform, false);
                camera.enabled = false;
                camera.clearFlags = effect == Effect.VolumetricClouds ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = effect == Effect.VolumetricClouds ? 75f : 55f;
                camera.transform.localRotation = Quaternion.Euler(effect == Effect.VolumetricClouds ? -8f : 10f, 0f, 0f);
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.cullingMask = 1 << GraphicsFixtureScope.Layer;

                cameraData = camera.gameObject.AddComponent<HDAdditionalCameraData>();
                cameraData.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                cameraData.customRenderingSettings = true;
                cameraData.clearColorMode = effect == Effect.VolumetricClouds
                    ? HDAdditionalCameraData.ClearColorMode.Sky
                    : HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = Color.black;
                EnableFrameSetting(FrameSettingsField.Postprocess, true);
                EnableFrameSetting(FrameSettingsField.Antialiasing, true);
                EnableFrameSetting(FrameSettingsField.SSR, true);
                EnableFrameSetting(FrameSettingsField.SSGI, true);
                EnableFrameSetting(FrameSettingsField.MotionVectors, true);
                EnableFrameSetting(FrameSettingsField.ObjectMotionVectors, true);
                EnableFrameSetting(FrameSettingsField.Volumetrics, true);
                EnableFrameSetting(FrameSettingsField.VolumetricClouds, true);

                guard = camera.gameObject.AddComponent<HdrpQuiltCamera>();
                guard.renderingMode = HdrpQuiltRenderingMode.IsolatedViewHistories;
                quilt = camera.gameObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = camera;
                quilt.GuardBehaviour = guard;
                quilt.settings.quiltWidth = TileSize * 2;
                quilt.settings.quiltHeight = TileSize * 2;
                quilt.settings.columns = 2;
                quilt.settings.rows = 2;
                quilt.settings.views = ViewCount;
                quilt.settings.viewAspect = 1f;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = camera.fieldOfView;
                quilt.settings.viewCone = 18f;

                referenceCamera = new GameObject("EffectReferenceCamera").AddComponent<Camera>();
                referenceCamera.transform.SetParent(root.transform, false);
                referenceCamera.CopyFrom(camera);
                referenceCamera.enabled = false;
                referenceCameraData = referenceCamera.gameObject.AddComponent<HDAdditionalCameraData>();
                referenceCameraData.volumeLayerMask = cameraData.volumeLayerMask;
                referenceCameraData.customRenderingSettings = cameraData.customRenderingSettings;
                referenceCameraData.renderingPathCustomFrameSettings = cameraData.renderingPathCustomFrameSettings;
                referenceCameraData.renderingPathCustomFrameSettingsOverrideMask = cameraData.renderingPathCustomFrameSettingsOverrideMask;
                referenceCameraData.clearColorMode = cameraData.clearColorMode;
                referenceCameraData.backgroundColorHDR = cameraData.backgroundColorHDR;
                referenceCameraData.clearDepth = cameraData.clearDepth;
                AssertCameraClearSettingsEqual(cameraData, referenceCameraData, "source/reference");
                quilt.referenceCamera = referenceCamera;

                AddSceneObjects();
                AddValidationVolume();
                AddLighting();

                cameraTarget = new RenderTexture(TileSize, TileSize, 24, RenderTextureFormat.ARGB32)
                {
                    name = "WindowsHdrpEffectOrdinaryCameraTarget",
                    antiAliasing = 1
                };
                Assert.IsTrue(cameraTarget.Create(), "Could not create the ordinary camera target.");

                scope = GraphicsFixtureScope.Create(root);
                scope.Apply(camera);
                fixtureScene = root.scene;
            }

            void EnableFrameSetting(FrameSettingsField field, bool enabled)
            {
                cameraData.renderingPathCustomFrameSettings.SetEnabled(field, enabled);
                cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = true;
            }

            void AddSceneObjects()
            {
                GameObject edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
                edge.name = "HighContrastDiagonalEdge";
                edge.layer = GraphicsFixtureScope.Layer;
                edge.transform.SetParent(root.transform, false);
                edge.transform.localPosition = new Vector3(-0.25f, 0.05f, 3f);
                edge.transform.localRotation = Quaternion.Euler(0f, 0f, 27f);
                edge.transform.localScale = new Vector3(1.9f, 0.07f, 0.06f);
                SetUnlit(edge, Color.white);
                owned.Add(edge);

                movingObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                movingObject.name = "MotionVectorTarget";
                movingObject.layer = GraphicsFixtureScope.Layer;
                movingObject.transform.SetParent(root.transform, false);
                movingObject.transform.localPosition = new Vector3(-0.7f, 0.25f, 2.5f);
                movingObject.transform.localScale = new Vector3(0.32f, 0.32f, 0.08f);
                movingObject.GetComponent<Renderer>().motionVectorGenerationMode = MotionVectorGenerationMode.Object;
                SetUnlit(movingObject, Color.magenta);
                owned.Add(movingObject);

                if (effect == Effect.Ssr || effect == Effect.Ssgi)
                {
                    GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                    floor.name = "ReflectionAndIndirectReceiver";
                    floor.layer = GraphicsFixtureScope.Layer;
                    floor.transform.SetParent(root.transform, false);
                    floor.transform.localPosition = new Vector3(0f, -0.8f, 4f);
                    floor.transform.localScale = new Vector3(0.55f, 1f, 0.8f);
                    SetLit(floor, Color.white, 0.98f, false);
                    owned.Add(floor);

                    GameObject emitter = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    emitter.name = "ScreenVisibleEmissiveSource";
                    emitter.layer = GraphicsFixtureScope.Layer;
                    emitter.transform.SetParent(root.transform, false);
                    emitter.transform.localPosition = new Vector3(-0.6f, -0.05f, 2.5f);
                    emitter.transform.localScale = new Vector3(0.32f, 0.48f, 0.08f);
                    SetLit(emitter, new Color(1f, 0.04f, 0.015f), 0.9f, true);
                    owned.Add(emitter);

                    GameObject receiver = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    receiver.name = "UnlitByDirectLightReceiver";
                    receiver.layer = GraphicsFixtureScope.Layer;
                    receiver.transform.SetParent(root.transform, false);
                    receiver.transform.localPosition = new Vector3(0.65f, -0.05f, 3.2f);
                    receiver.transform.localScale = new Vector3(0.7f, 0.55f, 0.08f);
                    SetLit(receiver, Color.white, 0.5f, false);
                    owned.Add(receiver);
                }
            }

            void SetUnlit(GameObject item, Color color)
            {
                Shader shader = Shader.Find("HDRP/Unlit");
                Assert.IsNotNull(shader, "HDRP Unlit shader is unavailable.");
                var material = new Material(shader) { color = color };
                item.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(material);
            }

            void SetLit(GameObject item, Color color, float smoothness, bool emission)
            {
                Shader shader = Shader.Find("HDRP/Lit");
                Assert.IsNotNull(shader, "HDRP Lit shader is unavailable.");
                var material = new Material(shader) { color = color };
                if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
                if (emission)
                {
                    if (material.HasProperty("_EmissiveColorMode")) material.SetFloat("_EmissiveColorMode", 0f);
                    if (material.HasProperty("_UseEmissiveIntensity")) material.SetFloat("_UseEmissiveIntensity", 1f);
                    if (material.HasProperty("_EmissiveIntensity")) material.SetFloat("_EmissiveIntensity", 8f);
                    material.SetColor("_EmissiveColor", new Color(8f, 0.15f, 0.05f, 1f));
                }
                item.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(material);
            }

            void AddValidationVolume()
            {
                neutralizeAntialiasingPostProcessing = effect == Effect.Fxaa || effect == Effect.Smaa ||
                    effect == Effect.Taa || effect == Effect.MotionBlur;
                GameObject volumeObject = new GameObject("HdrpEffectVolume");
                volumeObject.layer = GraphicsFixtureScope.Layer;
                volumeObject.transform.SetParent(root.transform, false);
                Volume volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                Exposure exposure = profile.Add<Exposure>(true);
                exposure.mode.value = ExposureMode.Fixed;
                exposure.mode.overrideState = true;
                bool highIlluminanceEffect = effect == Effect.VolumetricFog || effect == Effect.VolumetricClouds;
                exposure.fixedExposure.value = highIlluminanceEffect ? 12f : 0f;
                exposure.fixedExposure.overrideState = true;

                motionBlur = profile.Add<MotionBlur>(true);
                motionBlur.active = true;
                motionBlur.intensity.value = 1f;
                motionBlur.intensity.overrideState = true;
                motionBlur.minimumVelocity.value = 1f;
                motionBlur.minimumVelocity.overrideState = true;
                motionBlur.maximumVelocity.value = 128f;
                motionBlur.maximumVelocity.overrideState = true;

                reflection = profile.Add<ScreenSpaceReflection>(true);
                reflection.enabled.value = false;
                reflection.enabled.overrideState = true;
                reflection.tracing.value = RayCastingMode.RayMarching;
                reflection.tracing.overrideState = true;
                reflection.reflectSky.value = false;
                reflection.reflectSky.overrideState = true;
                reflection.minSmoothness = 0.05f;
                reflection.smoothnessFadeStart = 0.15f;

                gi = profile.Add<GlobalIllumination>(true);
                gi.enable.value = false;
                gi.enable.overrideState = true;
                gi.tracing.value = RayCastingMode.RayMarching;
                gi.tracing.overrideState = true;
                gi.rayMiss.value = RayMarchingFallbackHierarchy.None;
                gi.rayMiss.overrideState = true;

                fog = profile.Add<Fog>(true);
                fog.enabled.value = effect == Effect.VolumetricFog;
                fog.enabled.overrideState = true;
                fog.colorMode.value = FogColorMode.ConstantColor;
                fog.colorMode.overrideState = true;
                fog.color.value = new Color(0.65f, 0.72f, 0.86f, 1f);
                fog.color.overrideState = true;
                fog.meanFreePath.value = 12f;
                fog.meanFreePath.overrideState = true;
                fog.enableVolumetricFog.value = false;
                fog.enableVolumetricFog.overrideState = true;
                fog.albedo.value = Color.white;
                fog.albedo.overrideState = true;
                fog.anisotropy.value = 0.7f;
                fog.anisotropy.overrideState = true;
                fog.fogControlMode = FogControl.Manual;
                fog.volumetricFogBudget = 1f;

                clouds = profile.Add<VolumetricClouds>(true);
                clouds.enable.value = false;
                clouds.enable.overrideState = true;
                clouds.cloudControl.value = VolumetricClouds.CloudControl.Simple;
                clouds.cloudControl.overrideState = true;
                clouds.cloudSimpleMode.value = VolumetricClouds.CloudSimpleMode.Quality;
                clouds.cloudSimpleMode.overrideState = true;
                clouds.cloudPreset = VolumetricClouds.CloudPresets.Cloudy;
                FieldInfo cloudPresetField = typeof(VolumetricClouds).GetField("m_CloudPreset", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(cloudPresetField, "HDRP cloud preset parameter is unavailable.");
                VolumeParameter<VolumetricClouds.CloudPresets> cloudPreset =
                    (VolumeParameter<VolumetricClouds.CloudPresets>)cloudPresetField.GetValue(clouds);
                cloudPreset.value = VolumetricClouds.CloudPresets.Cloudy;
                cloudPreset.overrideState = true;
                clouds.numPrimarySteps.value = 96;
                clouds.numPrimarySteps.overrideState = true;
                clouds.numLightSteps.value = 8;
                clouds.numLightSteps.overrideState = true;
                clouds.cloudMapSpeedMultiplier.value = 0f;
                clouds.cloudMapSpeedMultiplier.overrideState = true;

                VisualEnvironment environment = profile.Add<VisualEnvironment>(true);
                environment.skyType.value = effect == Effect.VolumetricClouds ? (int)SkyType.PhysicallyBased : 0;
                environment.skyType.overrideState = true;
                environment.cloudType.value = 0;
                environment.cloudType.overrideState = true;
                environment.windSpeed.value = 0f;
                environment.windSpeed.overrideState = true;
                if (effect == Effect.VolumetricClouds)
                    profile.Add<PhysicallyBasedSky>(true);

                if (neutralizeAntialiasingPostProcessing)
                {
                    tonemapping = profile.Add<Tonemapping>(true);
                    tonemapping.mode.Override(TonemappingMode.None);
                    bloom = profile.Add<Bloom>(true);
                    bloom.intensity.Override(0f);
                }

                volume.sharedProfile = profile;
                owned.Add(volumeObject);
                owned.Add(profile);
            }

            void AddLighting()
            {
                GameObject sunObject = new GameObject("HdrpEffectDirectionalLight");
                sunObject.layer = GraphicsFixtureScope.Layer;
                sunObject.transform.SetParent(root.transform, false);
                sunObject.transform.rotation = Quaternion.Euler(42f, -28f, 0f);
                Light sun = sunObject.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.lightUnit = LightUnit.Lux;
                sun.intensity = effect == Effect.Ssr ? 1f : 12000f;
                sunObject.AddComponent<HDAdditionalLightData>();
                sun.cullingMask = 1 << GraphicsFixtureScope.Layer;
                directionalLight = sun;
                RenderSettings.sun = sun;
                owned.Add(sunObject);

                GameObject spotObject = new GameObject("HdrpFogScatteringSpot");
                spotObject.layer = GraphicsFixtureScope.Layer;
                spotObject.transform.SetParent(root.transform, false);
                spotObject.transform.localPosition = new Vector3(0f, 1.2f, 1.6f);
                spotObject.transform.localRotation = Quaternion.Euler(65f, 0f, 0f);
                Light spot = spotObject.AddComponent<Light>();
                spot.type = LightType.Spot;
                spot.range = 8f;
                spot.intensity = 2400f;
                spot.spotAngle = 28f;
                spot.innerSpotAngle = 15f;
                spot.lightUnit = LightUnit.Lumen;
                spotObject.AddComponent<HDAdditionalLightData>();
                spot.cullingMask = 1 << GraphicsFixtureScope.Layer;
                fogSpotlight = spot;
                bool edgeOnlyEffect = effect == Effect.Fxaa || effect == Effect.Smaa ||
                    effect == Effect.Taa || effect == Effect.MotionBlur;
                directionalLight.enabled = !edgeOnlyEffect && effect != Effect.Ssgi;
                fogSpotlight.enabled = effect == Effect.VolumetricFog;
                RenderSettings.ambientLight = Color.black;
                owned.Add(spotObject);
            }

            public void SetEnabled(bool enabled)
            {
                motionBlurStageEnabled = effect == Effect.MotionBlur && enabled;
                motionBlur.active = true;
                motionBlur.intensity.value = motionBlurStageEnabled ? 1f : 0f;
                motionBlur.intensity.overrideState = true;
                reflection.enabled.value = enabled && effect == Effect.Ssr;
                gi.enable.value = enabled && effect == Effect.Ssgi;
                fog.enabled.value = effect == Effect.VolumetricFog;
                fog.enableVolumetricFog.value = enabled && effect == Effect.VolumetricFog;
                clouds.enable.value = enabled && effect == Effect.VolumetricClouds;
                cameraData.antialiasing = enabled ? AntialiasingFor(effect) : HDAdditionalCameraData.AntialiasingMode.None;
                referenceCameraData.antialiasing = cameraData.antialiasing;
                if (effect == Effect.Taa && enabled)
                {
                    Assert.IsTrue(cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Postprocess),
                        "The authored source camera must enable HDRP post-processing for TAA.");
                    Assert.IsTrue(cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Antialiasing),
                        "The authored source camera must enable the HDRP antialiasing frame setting for TAA.");
                    Assert.IsTrue(referenceCameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Antialiasing),
                        "The Reference Camera must enable the HDRP antialiasing frame setting for TAA.");
                }
            }

            static HDAdditionalCameraData.AntialiasingMode AntialiasingFor(Effect value)
            {
                switch (value)
                {
                    case Effect.Fxaa: return HDAdditionalCameraData.AntialiasingMode.FastApproximateAntialiasing;
                    case Effect.Smaa: return HDAdditionalCameraData.AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                    case Effect.Taa: return HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                    default: return HDAdditionalCameraData.AntialiasingMode.None;
                }
            }

            public IEnumerator RenderStage(string stage, int frames)
            {
                CreateOrdinaryCamera();
                cameraTarget.Release();
                cameraTarget.Create();
                for (int frame = 0; frame < frames; ++frame)
                {
                    yield return null;
                    float x = effect == Effect.MotionBlur ? -0.85f + 0.28f * frame : -0.7f;
                    movingObject.transform.localPosition = new Vector3(x, 0.25f, 2.5f);
                    RenderOrdinary(frame);
                    PixelImage ordinaryBeforeQuilt = null;
                    if (effect == Effect.Ssr && frame == 0)
                    {
                        ordinaryBeforeQuilt = Read(cameraTarget);
                        SaveImage(ordinaryBeforeQuilt, stage + "-ordinary-immediate.png");
                    }
                    long revisionBefore = quilt.QuiltRevision;
                    CameraState sourceBefore = CaptureCameraState(camera, cameraData);
                    CameraState referenceBefore = CaptureCameraState(referenceCamera, referenceCameraData);
                    quilt.RenderQuilt();
                    Assert.IsTrue(quilt.HasRenderedQuilt, "Quilt rendering did not complete.");
                    Assert.AreEqual(revisionBefore + 1, quilt.QuiltRevision,
                        "A completed quilt render must advance the revision exactly once.");
                    AssertCameraStateUnchanged(camera, cameraData, sourceBefore, "source camera");
                    AssertCameraStateUnchanged(referenceCamera, referenceCameraData, referenceBefore, "reference camera");
                    ++quiltRenderCount;
                    RecordEffectiveViewStates();
                    RecordTaaHistories();
                    if (ordinaryBeforeQuilt != null)
                    {
                        PixelImage ordinaryAfterQuilt = Read(cameraTarget);
                        SaveImage(ordinaryAfterQuilt, stage + "-ordinary-after-quilt.png");
                        float targetDifference = MeanAbs(ordinaryBeforeQuilt, ordinaryAfterQuilt,
                            new RectInt(0, 0, ordinaryBeforeQuilt.Width, ordinaryBeforeQuilt.Height));
                        Debug.Log("HDRP_EFFECT_SSR_ORDINARY_TARGET stage=" + stage +
                            ", beforeQuiltAfterCameraRender=ordinary-immediate.png" +
                            ", afterQuilt=ordinary-after-quilt.png" +
                            ", meanAbsDifference=" + targetDifference.ToString("R", CultureInfo.InvariantCulture));
                    }
                }
                captures[stage] = new ImagePair
                {
                    Camera = Read(cameraTarget),
                    Quilt = Read(quilt.QuiltTexture)
                };
                SaveImage(captures[stage].Camera, stage + "-ordinary-camera.png");
                SaveImage(captures[stage].Quilt, stage + "-quilt.png");
                if (effect == Effect.Taa)
                {
                    ordinaryTaaHistoryByStage[stage] = ordinaryTaaHistoryAllocated;
                    ordinaryTaaModeByStage[stage] = ordinaryCameraData.antialiasing;
                }
                UnityEngine.Object.DestroyImmediate(ordinaryCamera.gameObject);
                ordinaryCamera = null;
                ordinaryCameraData = null;
            }

            void CreateOrdinaryCamera()
            {
                GameObject cameraObject = new GameObject("OrdinaryControlCamera");
                cameraObject.layer = GraphicsFixtureScope.Layer;
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.SetPositionAndRotation(camera.transform.position, camera.transform.rotation);
                ordinaryCamera = cameraObject.AddComponent<Camera>();
                ordinaryCamera.CopyFrom(camera);
                ordinaryCamera.enabled = false;
                ordinaryCamera.targetTexture = cameraTarget;
                ordinaryCamera.cullingMask = camera.cullingMask;
                ordinaryCameraData = cameraObject.AddComponent<HDAdditionalCameraData>();
                ordinaryCameraData.volumeLayerMask = cameraData.volumeLayerMask;
                ordinaryCameraData.customRenderingSettings = cameraData.customRenderingSettings;
                ordinaryCameraData.renderingPathCustomFrameSettings = cameraData.renderingPathCustomFrameSettings;
                ordinaryCameraData.renderingPathCustomFrameSettingsOverrideMask = cameraData.renderingPathCustomFrameSettingsOverrideMask;
                ordinaryCameraData.clearColorMode = cameraData.clearColorMode;
                ordinaryCameraData.backgroundColorHDR = cameraData.backgroundColorHDR;
                ordinaryCameraData.antialiasing = cameraData.antialiasing;
                ordinaryCameraData.hasPersistentHistory = effect == Effect.Taa;
            }

            static readonly FrameSettingsField[] CapturedFrameSettings =
            {
                FrameSettingsField.Postprocess,
                FrameSettingsField.Antialiasing,
                FrameSettingsField.SSR,
                FrameSettingsField.SSGI,
                FrameSettingsField.MotionVectors,
                FrameSettingsField.ObjectMotionVectors,
                FrameSettingsField.Volumetrics,
                FrameSettingsField.VolumetricClouds
            };

            static CameraState CaptureCameraState(Camera value, HDAdditionalCameraData data)
            {
                bool[] frameSettings = new bool[CapturedFrameSettings.Length];
                bool[] frameSettingOverrides = new bool[CapturedFrameSettings.Length];
                for (int index = 0; index < frameSettings.Length; ++index)
                {
                    frameSettings[index] = data.renderingPathCustomFrameSettings.IsEnabled(CapturedFrameSettings[index]);
                    frameSettingOverrides[index] = data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)CapturedFrameSettings[index]];
                }
                return new CameraState
                {
                    Target = value.targetTexture,
                    Position = value.transform.position,
                    Rotation = value.transform.rotation,
                    Projection = value.projectionMatrix,
                    Antialiasing = data.antialiasing,
                    FrameSettings = frameSettings,
                    FrameSettingOverrides = frameSettingOverrides
                };
            }

            static void AssertCameraStateUnchanged(Camera value, HDAdditionalCameraData data, CameraState before, string label)
            {
                Assert.AreSame(before.Target, value.targetTexture, label + " target texture changed during quilt rendering.");
                Assert.AreEqual(before.Position, value.transform.position, label + " position changed during quilt rendering.");
                Assert.AreEqual(before.Rotation, value.transform.rotation, label + " rotation changed during quilt rendering.");
                Assert.AreEqual(before.Projection, value.projectionMatrix, label + " projection changed during quilt rendering.");
                Assert.AreEqual(before.Antialiasing, data.antialiasing, label + " anti-aliasing changed during quilt rendering.");
                for (int index = 0; index < before.FrameSettings.Length; ++index)
                {
                    Assert.AreEqual(before.FrameSettings[index],
                        data.renderingPathCustomFrameSettings.IsEnabled(CapturedFrameSettings[index]),
                        label + " frame setting changed during quilt rendering: " + CapturedFrameSettings[index]);
                    Assert.AreEqual(before.FrameSettingOverrides[index],
                        data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)CapturedFrameSettings[index]],
                        label + " frame setting override changed during quilt rendering: " + CapturedFrameSettings[index]);
                }
            }

            void RenderOrdinary(int frame)
            {
                ordinaryCamera.targetTexture = cameraTarget;
                ordinaryCamera.Render();
                ++ordinaryRenderCount;
                if (effect == Effect.Taa)
                {
                    bool foundHDCamera = TryGetExistingHDCamera(ordinaryCamera, "RenderLoopHistory", out HDCamera hdCamera);
                    RTHandle history = foundHDCamera
                        ? hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.TemporalAntialiasing)
                        : null;
                    ordinaryTaaHistoryAllocated = history != null && history.rt != null;
                    ordinaryTaaHistoryTextureId = ordinaryTaaHistoryAllocated ? history.rt.GetEntityId().ToString() : "none";
                    Debug.Log("HDRP_EFFECT_TAA_ORDINARY frame=" + frame +
                        ", historyChannel=RenderLoopHistory, historyCameraFound=" + foundHDCamera +
                        ", antialiasing=" + ordinaryCameraData.antialiasing +
                        ", postprocessFrameSetting=" + ordinaryCameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Postprocess) +
                        ", postprocessOverride=" + ordinaryCameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Postprocess] +
                        ", antialiasingFrameSetting=" + ordinaryCameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Antialiasing) +
                        ", antialiasingOverride=" + ordinaryCameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Antialiasing] +
                        ", effectiveCameraState=" + (foundHDCamera ? FormatEffectiveCameraState(hdCamera, ordinaryCameraData, profile) : "not-found") +
                        ", historySlot=" + (int)HDCameraFrameHistoryType.TemporalAntialiasing +
                        ", historyAllocated=" + ordinaryTaaHistoryAllocated +
                        ", historyTextureId=" + ordinaryTaaHistoryTextureId);
                }
                else if (effect == Effect.Ssr || effect == Effect.MotionBlur)
                {
                    bool foundHDCamera = TryGetExistingHDCamera(ordinaryCamera, "RenderLoopHistory", out HDCamera hdCamera);
                    if (foundHDCamera)
                        Debug.Log("HDRP_EFFECT_CAMERA_STATE " + FormatEffectiveCameraState(hdCamera, ordinaryCameraData, profile));
                }

                bool foundMotionBlurState = TryGetExistingHDCamera(ordinaryCamera, "RenderLoopHistory", out HDCamera motionBlurCamera);
                Assert.IsTrue(foundMotionBlurState, "HDRP did not retain the ordinary camera for Motion Blur stack validation.");
                AssertEffectiveVolumeControls(motionBlurCamera, "ordinary camera");
            }

            void AssertEffectiveVolumeControls(HDCamera hdCamera, string cameraLabel)
            {
                Assert.IsNotNull(hdCamera.volumeStack, "HDRP did not provide a VolumeStack for " + cameraLabel + ".");
                MotionBlur effectiveMotionBlur = hdCamera.volumeStack.GetComponent<MotionBlur>();
                Assert.IsNotNull(effectiveMotionBlur, "HDRP did not provide Motion Blur volume state for " + cameraLabel + ".");
                Assert.IsTrue(effectiveMotionBlur.active, "Motion Blur volume override is inactive for " + cameraLabel + ".");
                float expectedIntensity = motionBlurStageEnabled ? 1f : 0f;
                Assert.AreEqual(expectedIntensity, motionBlur.intensity.value,
                    "Unexpected authored Motion Blur intensity for " + cameraLabel + ".");
                Assert.IsTrue(motionBlur.intensity.overrideState,
                    "Motion Blur intensity override is disabled for " + cameraLabel + ".");
                Assert.AreEqual(expectedIntensity, effectiveMotionBlur.intensity.value,
                    "Unexpected effective Motion Blur intensity for " + cameraLabel + ".");

                if (!neutralizeAntialiasingPostProcessing) return;
                Assert.IsNotNull(tonemapping, "The AA control scene is missing its authored Tonemapping override.");
                Assert.IsNotNull(bloom, "The AA control scene is missing its authored Bloom override.");
                Tonemapping effectiveTonemapping = hdCamera.volumeStack.GetComponent<Tonemapping>();
                Bloom effectiveBloom = hdCamera.volumeStack.GetComponent<Bloom>();
                Assert.IsNotNull(effectiveTonemapping, "HDRP did not provide Tonemapping state for " + cameraLabel + ".");
                Assert.IsNotNull(effectiveBloom, "HDRP did not provide Bloom state for " + cameraLabel + ".");
                Assert.IsTrue(effectiveTonemapping.active, "Tonemapping volume override is inactive for " + cameraLabel + ".");
                Assert.IsTrue(effectiveTonemapping.mode.overrideState, "Tonemapping mode override is disabled for " + cameraLabel + ".");
                Assert.AreEqual(TonemappingMode.None, tonemapping.mode.value,
                    "Unexpected authored Tonemapping mode for " + cameraLabel + ".");
                Assert.AreEqual(TonemappingMode.None, effectiveTonemapping.mode.value,
                    "Unexpected effective Tonemapping mode for " + cameraLabel + ".");
                Assert.IsTrue(effectiveBloom.active, "Bloom volume override is inactive for " + cameraLabel + ".");
                Assert.IsTrue(bloom.intensity.overrideState, "Bloom intensity override is disabled for " + cameraLabel + ".");
                Assert.IsTrue(effectiveBloom.intensity.overrideState,
                    "Effective Bloom intensity override is disabled for " + cameraLabel + ".");
                Assert.AreEqual(0f, bloom.intensity.value, "Unexpected authored Bloom intensity for " + cameraLabel + ".");
                Assert.AreEqual(0f, effectiveBloom.intensity.value, "Unexpected effective Bloom intensity for " + cameraLabel + ".");
            }

            static void AssertCameraClearSettingsEqual(HDAdditionalCameraData expected, HDAdditionalCameraData actual,
                string cameraLabel)
            {
                Assert.AreEqual(expected.clearColorMode, actual.clearColorMode,
                    "HDRP clear-color mode differs for " + cameraLabel + ".");
                Assert.AreEqual(expected.backgroundColorHDR, actual.backgroundColorHDR,
                    "HDRP background color differs for " + cameraLabel + ".");
                Assert.AreEqual(expected.clearDepth, actual.clearDepth,
                    "HDRP clear-depth setting differs for " + cameraLabel + ".");
            }

            static string FormatEffectiveCameraState(HDCamera hdCamera, HDAdditionalCameraData cameraData,
                VolumeProfile authoredProfile)
            {
                FrameSettings effective = hdCamera.frameSettings;
                VolumeStack stack = hdCamera.volumeStack;
                Exposure effectiveExposure = stack != null ? stack.GetComponent<Exposure>() : null;
                ScreenSpaceReflection effectiveReflection = stack != null ? stack.GetComponent<ScreenSpaceReflection>() : null;
                MotionBlur effectiveMotionBlur = stack != null ? stack.GetComponent<MotionBlur>() : null;
                Fog effectiveFog = stack != null ? stack.GetComponent<Fog>() : null;
                Exposure authoredExposure = null;
                ScreenSpaceReflection authoredReflection = null;
                MotionBlur authoredMotionBlur = null;
                Fog authoredFog = null;
                bool hasAuthoredExposure = authoredProfile != null && authoredProfile.TryGet(out authoredExposure);
                bool hasAuthoredReflection = authoredProfile != null && authoredProfile.TryGet(out authoredReflection);
                bool hasAuthoredMotionBlur = authoredProfile != null && authoredProfile.TryGet(out authoredMotionBlur);
                bool hasAuthoredFog = authoredProfile != null && authoredProfile.TryGet(out authoredFog);

                return "camera=" + hdCamera.camera.name +
                    ", effectivePostprocess=" + effective.IsEnabled(FrameSettingsField.Postprocess) +
                    ", effectiveAntialiasing=" + effective.IsEnabled(FrameSettingsField.Antialiasing) +
                    ", effectiveSSRFrameSetting=" + effective.IsEnabled(FrameSettingsField.SSR) +
                    ", effectiveMotionVectors=" + effective.IsEnabled(FrameSettingsField.MotionVectors) +
                    ", authoredPostprocess=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Postprocess) +
                    ", authoredAntialiasing=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Antialiasing) +
                    ", authoredSSRFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.SSR) +
                    ", authoredMotionVectors=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.MotionVectors) +
                    ", effectiveExposure=" + (effectiveExposure == null ? "missing" :
                        effectiveExposure.mode.value + "/" + effectiveExposure.fixedExposure.value.ToString("R", CultureInfo.InvariantCulture) +
                        "/" + effectiveExposure.compensation.value.ToString("R", CultureInfo.InvariantCulture)) +
                    ", authoredExposure=" + (!hasAuthoredExposure ? "missing" :
                        authoredExposure.mode.value + "/" + authoredExposure.fixedExposure.value.ToString("R", CultureInfo.InvariantCulture) +
                        "/" + authoredExposure.compensation.value.ToString("R", CultureInfo.InvariantCulture)) +
                    ", effectiveSSR=" + (effectiveReflection == null ? "missing" :
                        effectiveReflection.active + "/" + effectiveReflection.enabled.value) +
                    ", authoredSSR=" + (!hasAuthoredReflection ? "missing" :
                        authoredReflection.active + "/" + authoredReflection.enabled.value) +
                    ", effectiveMotionBlur=" + (effectiveMotionBlur == null ? "missing" :
                        effectiveMotionBlur.active + "/" + effectiveMotionBlur.intensity.value.ToString("R", CultureInfo.InvariantCulture)) +
                    ", authoredMotionBlur=" + (!hasAuthoredMotionBlur ? "missing" :
                        authoredMotionBlur.active + "/" + authoredMotionBlur.intensity.value.ToString("R", CultureInfo.InvariantCulture)) +
                    ", effectiveFog=" + (effectiveFog == null ? "missing" : effectiveFog.enabled.value.ToString()) +
                    ", authoredFog=" + (!hasAuthoredFog ? "missing" : authoredFog.enabled.value.ToString());
            }

            void RecordEffectiveViewStates()
            {
                for (int view = 0; view < ViewCount; ++view)
                {
                    Camera viewCamera = guard.GetHistoryCamera(view);
                    Assert.IsNotNull(viewCamera, "HDRP did not create a history camera for effect " + effect + " view " + view + ".");
                    HDAdditionalCameraData data = viewCamera.GetComponent<HDAdditionalCameraData>();
                    Assert.IsNotNull(data, "HDRP history camera is missing additional data for effect " + effect + " view " + view + ".");
                    AssertCameraClearSettingsEqual(cameraData, data, "quilt view " + view);
                    bool found = TryGetExistingHDCamera(viewCamera, "CustomUserHistory0", out HDCamera hdCamera);
                    Assert.IsTrue(found, "HDRP did not retain effect history view " + view + ".");
                    AssertEffectiveVolumeControls(hdCamera, "quilt view " + view);
                    Debug.Log("HDRP_EFFECT_VIEW_STATE effect=" + effect + ", view=" + view +
                        ", historyChannel=CustomUserHistory0, cameraFound=" + found +
                        ", state=" + (found ? FormatEffectiveCameraState(hdCamera, data, profile) : "not-found"));
                }
            }

            void RecordTaaHistories()
            {
                if (effect != Effect.Taa) return;
                if (cameraData.antialiasing != HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing)
                {
                    Assert.AreEqual(HDAdditionalCameraData.AntialiasingMode.None, referenceCameraData.antialiasing,
                        "TAA-off control camera retained temporal anti-aliasing.");
                    return;
                }
                Assert.AreEqual(ViewCount, guard.HistoryCameraCount, "HDRP did not create all isolated view cameras.");
                for (int view = 0; view < ViewCount; ++view)
                {
                    Camera historyCamera = guard.GetHistoryCamera(view);
                    Assert.IsNotNull(historyCamera);
                    HDAdditionalCameraData data = historyCamera.GetComponent<HDAdditionalCameraData>();
                    Assert.IsTrue(data.hasPersistentHistory, "An isolated view does not preserve temporal history.");
                    Assert.AreEqual(HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing, data.antialiasing);
                    bool foundHDCamera = TryGetExistingHDCamera(historyCamera, "CustomUserHistory0", out HDCamera hdCamera);
                    Assert.IsTrue(foundHDCamera,
                        "HDRP did not create the existing CustomUserHistory0 camera used by StandardRequest.");
                    RTHandle history = hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.TemporalAntialiasing);
                    Assert.IsNotNull(history);
                    Assert.IsNotNull(history.rt, "HDRP TAA did not allocate a persistent history texture.");
                    taaHistoryTextures[view].Add(history.rt);
                    Debug.Log("HDRP_EFFECT_TAA_VIEW view=" + view + ", historyChannel=CustomUserHistory0, " +
                        FormatEffectiveCameraState(hdCamera, data, profile) +
                        ", historySlot=" + (int)HDCameraFrameHistoryType.TemporalAntialiasing +
                        ", historyTextureId=" + history.rt.GetEntityId().ToString());
                }
            }

            public void ResetViewHistories()
            {
                quilt.ReleasePipelineRenderResources();
                for (int view = 0; view < ViewCount; ++view) taaHistoryTextures[view].Clear();
            }

            public void AssertEvidence()
            {
                ImagePair offA = captures["off-a"];
                ImagePair offB = captures["off-b"];
                ImagePair on = captures["on"];
                string summary = "effect=" + effect + "\n" +
                    "graphicsApi=" + SystemInfo.graphicsDeviceType + "\n" +
                    "views=" + ViewCount + "\n" +
                    "tileSize=" + TileSize + "\n" +
                    "ordinaryCameraRenderCount=" + ordinaryRenderCount + "\n" +
                    "quiltRenderCount=" + quiltRenderCount + "\n" +
                    "fixedExposureEV=" + ((effect == Effect.VolumetricFog || effect == Effect.VolumetricClouds) ? 12 : 0) + "\n" +
                    "baseFogEnabled=" + fog.enabled.value + "\n" +
                    "baseFogOverride=" + fog.enabled.overrideState + "\n" +
                    "volumetricFogEnabled=" + fog.enableVolumetricFog.value + "\n" +
                    "postprocessFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Postprocess) + "\n" +
                    "postprocessOverride=" + cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Postprocess] + "\n" +
                    "antialiasingFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Antialiasing) + "\n" +
                    "antialiasingOverride=" + cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.Antialiasing] + "\n" +
                    "ssrFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.SSR) + "\n" +
                    "ssrOverride=" + cameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.SSR] + "\n" +
                    "ssrVolumeEnabled=" + reflection.enabled.value + "\n" +
                    "sunEnabled=" + directionalLight.enabled + "\n" +
                    "sunLux=" + directionalLight.intensity.ToString("R", CultureInfo.InvariantCulture) + "\n" +
                    "fogSpotEnabled=" + fogSpotlight.enabled + "\n" +
                    "ordinaryTaaHistoryAllocated=" + ordinaryTaaHistoryAllocated + "\n" +
                    "ordinaryTaaHistoryTextureId=" + ordinaryTaaHistoryTextureId + "\n" +
                    "antialiasing=" + cameraData.antialiasing + "\n" +
                    "motionBlurActive=" + motionBlur.active + "\n" +
                    "ssrEnabled=" + reflection.enabled.value + "\n" +
                    "ssgiEnabled=" + gi.enable.value + "\n" +
                    "volumetricCloudsEnabled=" + clouds.enable.value + "\n";
                Debug.Log("HDRP_EFFECT_RUNTIME_FLAGS effect=" + effect +
                    ", postprocess=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Postprocess) +
                    ", antialiasingFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Antialiasing) +
                    ", ssrFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.SSR) +
                    ", ssrEnabled=" + reflection.enabled.value + ", baseFogEnabled=" + fog.enabled.value +
                    ", volumetricFogEnabled=" + fog.enableVolumetricFog.value +
                    ", fixedExposureEV=" + ((effect == Effect.VolumetricFog || effect == Effect.VolumetricClouds) ? 12 : 0) +
                    ", sunEnabled=" + directionalLight.enabled + ", sunLux=" + directionalLight.intensity.ToString("R", CultureInfo.InvariantCulture) +
                    ", fogSpotEnabled=" + fogSpotlight.enabled);
                Directory.CreateDirectory(outputDirectory);
                File.WriteAllText(Path.Combine(outputDirectory, "evidence.txt"), summary, System.Text.Encoding.ASCII);
                AssertImage(offA.Camera, "ordinary-camera off baseline");
                AssertImage(offB.Camera, "ordinary-camera off repeat");
                AssertImage(on.Camera, "ordinary-camera effect enabled");
                AssertImage(offA.Quilt, "quilt off baseline");
                AssertImage(offB.Quilt, "quilt off repeat");
                AssertImage(on.Quilt, "quilt effect enabled");
                AssertNoiseBound(offA.Camera, offB.Camera, "ordinary-camera");
                AssertNoiseBound(offA.Quilt, offB.Quilt, "quilt");
                if (effect == Effect.Fxaa || effect == Effect.Smaa || effect == Effect.Taa || effect == Effect.MotionBlur)
                {
                    AssertUnlitEdgeBaseline(offA.Camera, null, "ordinary-camera");
                    QuiltLayout edgeLayout = quilt.settings.GetLayout();
                    for (int view = 0; view < ViewCount; ++view)
                        AssertUnlitEdgeBaseline(offA.Quilt, edgeLayout.GetTile(view), "view-" + view);
                }
                if (effect == Effect.Ssr) AssertSsrReflectionPixels(offA, on, quilt.settings.GetLayout());
                AssertChangedRegions(offA.Camera, offB.Camera, on.Camera, null, "ordinary-camera");

                QuiltLayout layout = quilt.settings.GetLayout();
                for (int view = 0; view < ViewCount; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    AssertImageRegion(offA.Quilt, tile, view);
                    AssertChangedRegions(offA.Quilt, offB.Quilt, on.Quilt, tile, "view-" + view);
                }
                AssertFeatureDirection(offA, offB, on, layout);
                if (effect == Effect.Taa)
                {
                    Assert.AreEqual(HDAdditionalCameraData.AntialiasingMode.None, ordinaryTaaModeByStage["off-a"],
                        "The ordinary off control did not disable temporal anti-aliasing.");
                    Assert.AreEqual(HDAdditionalCameraData.AntialiasingMode.None, ordinaryTaaModeByStage["off-b"],
                        "The repeated ordinary off control did not disable temporal anti-aliasing.");
                    Assert.IsFalse(ordinaryTaaHistoryByStage["off-a"],
                        "The ordinary off control unexpectedly allocated TAA history.");
                    Assert.IsFalse(ordinaryTaaHistoryByStage["off-b"],
                        "The repeated ordinary off control unexpectedly allocated TAA history.");
                    Assert.AreEqual(HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing, ordinaryTaaModeByStage["on"],
                        "The ordinary on control did not enable temporal anti-aliasing.");
                    Assert.IsTrue(ordinaryTaaHistoryByStage["on"],
                        "The ordinary on control did not allocate TAA history.");
                    AssertTaaHistoryIndependence();
                }

            }

            void AssertChangedRegions(PixelImage offA, PixelImage offB, PixelImage on, RectInt? tile, string label)
            {
                RectInt roi = GetEffectRoi(offA, tile);
                float noise = MeanAbs(offA, offB, roi);
                float difference = MeanAbs(offA, on, roi);
                Assert.Greater(difference, Mathf.Max(0.25f, noise * 3f),
                    label + " mean absolute pixel difference did not exceed max(0.25, off/off noise * 3); difference=" + difference + " noise=" + noise);
            }

            void AssertNoiseBound(PixelImage first, PixelImage second, string label)
            {
                RectInt roi = GetEffectRoi(first, null);
                float noise = MeanAbs(first, second, roi);
                Assert.IsFalse(float.IsNaN(noise) || float.IsInfinity(noise), label + " off/off noise is not finite.");
            }

            void AssertImage(PixelImage image, string label)
            {
                Assert.IsNotNull(image);
                Assert.IsTrue(image.AllPixelsFinite, label + " contains a NaN or infinity pixel.");
                int visible = 0;
                for (int index = 0; index < image.Pixels.Length; ++index)
                    if (Luminance(image.Pixels[index]) > 6f) ++visible;
                Assert.Greater(visible, 20, label + " is blank.");
            }

            void AssertImageRegion(PixelImage image, RectInt tile, int view)
            {
                RectInt roi = GetEffectRoi(image, tile);
                int visible = 0;
                for (int y = roi.y; y < roi.yMax; ++y)
                for (int x = roi.x; x < roi.xMax; ++x)
                    if (Luminance(image.Pixels[y * image.Width + x]) > 6f) ++visible;
                Assert.Greater(visible, 20, "Quilt control ROI is blank in logical view " + view + ".");
            }

            void AssertUnlitEdgeBaseline(PixelImage image, RectInt? tile, string label)
            {
                RectInt bounds = tile ?? new RectInt(0, 0, image.Width, image.Height);
                int blackPixels = 0;
                int brightPixels = 0;
                for (int y = bounds.y; y < bounds.yMax; ++y)
                for (int x = bounds.x; x < bounds.xMax; ++x)
                {
                    float luminance = Luminance(image.Pixels[y * image.Width + x]);
                    if (luminance <= 8f) ++blackPixels;
                    if (luminance >= 247f) ++brightPixels;
                }
                Debug.Log("HDRP_EFFECT_UNLIT_BASELINE effect=" + effect + ", label=" + label +
                    ", fixedExposureEV=0, blackPixels=" + blackPixels + ", brightPixels=" + brightPixels);
                Assert.Greater(blackPixels, 20, label + " unlit baseline lost its black background.");
                Assert.Greater(brightPixels, 20, label + " unlit baseline lost its high-contrast white edge.");
            }

            void AssertSsrReflectionPixels(ImagePair off, ImagePair on, QuiltLayout layout)
            {
                float offSignal = RedSignal(off.Camera, null);
                float onSignal = RedSignal(on.Camera, null);
                int offRedPixels = CountRedPixels(off.Camera, null);
                int onRedPixels = CountRedPixels(on.Camera, null);
                Debug.Log("HDRP_EFFECT_SSR_REFLECTION_ROI ordinaryCamera=true, postprocess=" +
                    cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.Postprocess) +
                    ", ssrFrameSetting=" + cameraData.renderingPathCustomFrameSettings.IsEnabled(FrameSettingsField.SSR) +
                    ", ssrEnabled=" + reflection.enabled.value + ", offSignal=" + offSignal +
                    ", onSignal=" + onSignal + ", offRedPixels=" + offRedPixels + ", onRedPixels=" + onRedPixels);
                Assert.Greater(offRedPixels, 0, "SSR off-baseline ROI did not retain the screen-visible red source.");
                Assert.Greater(onRedPixels, 0, "SSR enabled ROI did not contain red reflection pixels.");
                Assert.Greater(onSignal, offSignal, "SSR did not increase the red reflection signal in the ordinary-camera ROI.");
                for (int view = 0; view < ViewCount; ++view)
                {
                    int viewRedPixels = CountRedPixels(on.Quilt, layout.GetTile(view));
                    Debug.Log("HDRP_EFFECT_SSR_REFLECTION_ROI view=" + view + ", onRedPixels=" + viewRedPixels);
                    Assert.Greater(viewRedPixels, 0, "SSR enabled ROI did not contain red pixels in logical view " + view + ".");
                }
            }

            void AssertFeatureDirection(ImagePair offA, ImagePair offB, ImagePair on, QuiltLayout layout)
            {
                AssertFeatureDirection(offA.Camera, on.Camera, null, "ordinary-camera");
                for (int view = 0; view < ViewCount; ++view)
                    AssertFeatureDirection(offA.Quilt, on.Quilt, layout.GetTile(view), "view-" + view);
            }

            void AssertFeatureDirection(PixelImage before, PixelImage after, RectInt? tile, string label)
            {
                if (effect == Effect.Fxaa || effect == Effect.Smaa || effect == Effect.Taa)
                {
                    Assert.Greater(IntermediateEdgePixels(after, tile), IntermediateEdgePixels(before, tile),
                        "Anti-aliasing did not increase resolved intermediate edge coverage in " + label + ".");
                }
                else if (effect == Effect.MotionBlur)
                {
                    float offTrail = MagentaTrailSignal(before, tile);
                    float onTrail = MagentaTrailSignal(after, tile);
                    Assert.Greater(onTrail, offTrail, "Motion blur did not increase magenta trail coverage in " + label + ".");
                }
                else if (effect == Effect.Ssr)
                {
                    Assert.Greater(RedSignal(after, tile), RedSignal(before, tile),
                        "SSR did not increase the red screen-space reflection signal in " + label + ".");
                }
                else if (effect == Effect.Ssgi)
                {
                    Assert.Greater(RedSignal(after, tile), RedSignal(before, tile),
                        "SSGI did not increase the red indirect-light signal in " + label + ".");
                }
                else if (effect == Effect.VolumetricFog)
                {
                    Assert.Greater(MeanLuminance(after, tile), MeanLuminance(before, tile),
                        "Volumetric fog did not increase the spotlight scattering signal in " + label + ".");
                }
                else if (effect == Effect.VolumetricClouds)
                {
                    RectInt roi = GetEffectRoi(after, tile);
                    Assert.Greater(RegionVariance(after, roi), RegionVariance(before, roi),
                        "Volumetric clouds did not increase structure in the fixed sky ROI for " + label + ".");
                }
            }

            void AssertTaaHistoryIndependence()
            {
                Assert.AreEqual(ViewCount, guard.HistoryCameraCount, "TAA history camera count changed.");
                HashSet<Camera> cameras = new HashSet<Camera>();
                HashSet<RenderTexture> currentHistories = new HashSet<RenderTexture>();
                for (int view = 0; view < ViewCount; ++view)
                {
                    Camera historyCamera = guard.GetHistoryCamera(view);
                    Assert.IsTrue(cameras.Add(historyCamera), "Two logical views share a camera.");
                    Assert.GreaterOrEqual(taaHistoryTextures[view].Count, 12, "A logical view did not render enough real frames.");
                    Assert.Greater(new HashSet<RenderTexture>(taaHistoryTextures[view]).Count, 1,
                        "A logical view TAA history did not advance across frames.");
                    RenderTexture current = taaHistoryTextures[view][taaHistoryTextures[view].Count - 1];
                    Assert.IsTrue(currentHistories.Add(current), "Two logical views share the same TAA history texture.");
                }
            }

            RectInt GetEffectRoi(PixelImage image, RectInt? tile)
            {
                RectInt bounds = tile ?? new RectInt(0, 0, image.Width, image.Height);
                switch (effect)
                {
                    case Effect.Ssr: return new RectInt(bounds.x, bounds.y, bounds.width, bounds.height / 2);
                    case Effect.Ssgi: return new RectInt(bounds.x + bounds.width / 2, bounds.y + bounds.height / 4, bounds.width / 2, bounds.height / 2);
                    case Effect.VolumetricFog: return new RectInt(bounds.x + bounds.width / 4, bounds.y + bounds.height / 4, bounds.width / 2, bounds.height / 2);
                    case Effect.VolumetricClouds: return new RectInt(bounds.x, bounds.y + bounds.height * 2 / 3, bounds.width, bounds.height / 3);
                    default: return bounds;
                }
            }

            static PixelImage Read(RenderTexture source)
            {
                RenderTexture previous = RenderTexture.active;
                Texture2D texture = null;
                try
                {
                    RenderTexture.active = source;
                    texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                    texture.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                    texture.Apply();
                    Color[] floatingPixels = texture.GetPixels();
                    bool finite = true;
                    for (int index = 0; index < floatingPixels.Length; ++index)
                    {
                        Color pixel = floatingPixels[index];
                        if (float.IsNaN(pixel.r) || float.IsInfinity(pixel.r) ||
                            float.IsNaN(pixel.g) || float.IsInfinity(pixel.g) ||
                            float.IsNaN(pixel.b) || float.IsInfinity(pixel.b) ||
                            float.IsNaN(pixel.a) || float.IsInfinity(pixel.a))
                        {
                            finite = false;
                            break;
                        }
                    }
                    return new PixelImage { Width = texture.width, Height = texture.height, Pixels = texture.GetPixels32(), AllPixelsFinite = finite };
                }
                finally
                {
                    RenderTexture.active = previous;
                    if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                }
            }

            void SaveImage(PixelImage image, string path)
            {
                Texture2D texture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false);
                try
                {
                    texture.SetPixels32(image.Pixels);
                    texture.Apply();
                    Directory.CreateDirectory(outputDirectory);
                    File.WriteAllBytes(Path.Combine(outputDirectory, path), texture.EncodeToPNG());
                }
                finally { UnityEngine.Object.DestroyImmediate(texture); }
            }

            static float MeanAbs(PixelImage left, PixelImage right, RectInt roi)
            {
                double sum = 0;
                long count = 0;
                for (int y = roi.y; y < roi.yMax; ++y)
                for (int x = roi.x; x < roi.xMax; ++x)
                {
                    int index = y * left.Width + x;
                    Color32 a = left.Pixels[index], b = right.Pixels[index];
                    sum += Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b);
                    count += 3;
                }
                return count == 0 ? 0f : (float)(sum / count);
            }

            static float IntermediateEdgePixels(PixelImage image, RectInt? tile)
            {
                RectInt bounds = tile ?? new RectInt(0, 0, image.Width, image.Height);
                int count = 0;
                for (int y = bounds.y; y < bounds.yMax; ++y)
                for (int x = bounds.x; x < bounds.xMax; ++x)
                {
                    float luma = Luminance(image.Pixels[y * image.Width + x]);
                    if (luma > 18f && luma < 237f) ++count;
                }
                return count;
            }

            static float MagentaTrailSignal(PixelImage image, RectInt? tile)
            {
                int count = 0;
                RectInt bounds = tile ?? new RectInt(0, 0, image.Width, image.Height);
                int startX = bounds.x + bounds.width / 5;
                int endX = bounds.x + bounds.width * 3 / 5;
                int startY = bounds.y + bounds.height / 4;
                int endY = bounds.y + bounds.height * 3 / 4;
                for (int y = startY; y < endY; ++y)
                for (int x = startX; x < endX; ++x)
                {
                    Color32 pixel = image.Pixels[y * image.Width + x];
                    if (pixel.r > 50 && pixel.b > 50 && pixel.g < 100) ++count;
                }
                return count;
            }

            float RedSignal(PixelImage image, RectInt? tile)
            {
                RectInt roi = GetEffectRoi(image, tile);
                double sum = 0;
                long count = 0;
                for (int y = roi.y; y < roi.yMax; ++y)
                for (int x = roi.x; x < roi.xMax; ++x)
                {
                    Color32 pixel = image.Pixels[y * image.Width + x];
                    sum += Math.Max(0, pixel.r - pixel.b);
                    ++count;
                }
                return count == 0 ? 0f : (float)(sum / count);
            }

            int CountRedPixels(PixelImage image, RectInt? tile)
            {
                RectInt roi = GetEffectRoi(image, tile);
                int count = 0;
                for (int y = roi.y; y < roi.yMax; ++y)
                for (int x = roi.x; x < roi.xMax; ++x)
                {
                    Color32 pixel = image.Pixels[y * image.Width + x];
                    if (pixel.r > 50 && pixel.r > pixel.g * 1.4f && pixel.r > pixel.b * 1.4f) ++count;
                }
                return count;
            }

            float MeanLuminance(PixelImage image, RectInt? tile)
            {
                RectInt roi = GetEffectRoi(image, tile);
                double sum = 0;
                long count = 0;
                for (int y = roi.y; y < roi.yMax; ++y)
                for (int x = roi.x; x < roi.xMax; ++x)
                {
                    sum += Luminance(image.Pixels[y * image.Width + x]);
                    ++count;
                }
                return count == 0 ? 0f : (float)(sum / count);
            }

            static float RegionVariance(PixelImage image, RectInt roi)
            {
                double sum = 0, squares = 0;
                long count = 0;
                for (int y = roi.y; y < roi.yMax; ++y)
                for (int x = roi.x; x < roi.xMax; ++x)
                {
                    double value = Luminance(image.Pixels[y * image.Width + x]);
                    sum += value;
                    squares += value * value;
                    ++count;
                }
                double mean = sum / count;
                return (float)(squares / count - mean * mean);
            }

            static float Luminance(Color32 pixel)
            {
                return 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                Exception cleanupFailure = null;
                TryCleanup(() => { if (quilt != null) quilt.ReleasePipelineRenderResources(); }, ref cleanupFailure);
                TryCleanup(() => { if (camera != null) camera.targetTexture = null; }, ref cleanupFailure);
                TryCleanup(() =>
                {
                    if (cameraTarget != null)
                    {
                        cameraTarget.Release();
                        UnityEngine.Object.DestroyImmediate(cameraTarget);
                    }
                }, ref cleanupFailure);
                TryCleanup(() => { if (scope != null) scope.Dispose(); }, ref cleanupFailure);
                TryCleanup(() => { if (root != null) UnityEngine.Object.DestroyImmediate(root); }, ref cleanupFailure);
                for (int index = owned.Count - 1; index >= 0; --index)
                {
                    int ownedIndex = index;
                    TryCleanup(() =>
                    {
                        if (owned[ownedIndex] != null) UnityEngine.Object.DestroyImmediate(owned[ownedIndex]);
                    }, ref cleanupFailure);
                }
                TryCleanup(() => GraphicsSettings.defaultRenderPipeline = oldDefaultPipeline, ref cleanupFailure);
                TryCleanup(() => QualitySettings.renderPipeline = oldQualityPipeline, ref cleanupFailure);
                TryCleanup(() => RenderSettings.sun = oldSceneState.sun, ref cleanupFailure);
                TryCleanup(() => RenderSettings.ambientLight = oldSceneState.ambient, ref cleanupFailure);
                TryCleanup(() => RenderTexture.active = oldActive, ref cleanupFailure);
                if (cleanupFailure != null)
                    throw new InvalidOperationException("One or more effect fixture cleanup steps failed.", cleanupFailure);
            }

            static void TryCleanup(Action action, ref Exception firstFailure)
            {
                try { action(); }
                catch (Exception exception) { if (firstFailure == null) firstFailure = exception; }
            }
        }
    }
}
#endif
