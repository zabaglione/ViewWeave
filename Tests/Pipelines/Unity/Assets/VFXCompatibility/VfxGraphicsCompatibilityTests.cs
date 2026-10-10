using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.VFX;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.Universal;
using ViewWeave;
using ViewWeave.URP;
using ViewWeave.HDRP;

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.Rendering.HighDefinition;
#endif

namespace ViewWeave.VFXCompatibility
{
    sealed class VfxCameraPrepareBehaviour : MonoBehaviour
    {
        public Camera targetCamera;

        void Update()
        {
            if (targetCamera == null)
            {
                enabled = false;
                return;
            }
            VFXManager.PrepareCamera(targetCamera);
            enabled = false;
        }
    }

    public sealed class VfxGraphicsCompatibilityTests
    {
        [UnityTest] public IEnumerator URP_SimpleBurst_GRD_Off() => Run("urp", false);
        [UnityTest] public IEnumerator URP_SimpleBurst_GRD_On() => Run("urp", true);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_GRD_Off() => Run("hdrp", false);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_GRD_On() => Run("hdrp", true);
        [UnityTest] public IEnumerator URP_SimpleBurst_GRD_Off_QuiltOnly() => Run("urp", false, true);
        [UnityTest] public IEnumerator URP_SimpleBurst_GRD_On_QuiltOnly() => Run("urp", true, true);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_GRD_Off_QuiltOnly() => Run("hdrp", false, true);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_GRD_On_QuiltOnly() => Run("hdrp", true, true);
        [UnityTest] public IEnumerator URP_SimpleBurst_Driver1x1() => RunExperimental("urp", false, false, 1, 1, false);
        [UnityTest] public IEnumerator URP_SimpleBurst_Driver128x96() => RunExperimental("urp", false, false, 128, 96, false);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_Separated512() => RunExperimental("hdrp", false, false, 512, 384, true);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_Separated1x1() => RunExperimental("hdrp", false, false, 1, 1, true);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_SeparatedQuiltOnly() => RunExperimental("hdrp", false, true, 512, 384, true);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_SeparatedNormalOnly() => RunNormalOnly();
        [UnityTest] public IEnumerator HDRP_SimpleBurst_Driver8_GRD_Off() => RunDriver("hdrp", false, 8);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_Driver8_GRD_On() => RunDriver("hdrp", true, 8);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_Driver48_GRD_Off() => RunDriver("hdrp", false, 48);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_Driver48_GRD_On() => RunDriver("hdrp", true, 48);
        [UnityTest] public IEnumerator HDRP_SimpleBurst_SeparatedNormalOnly_CloneRequestDiagnostic() => RunHdrpCloneRequestDiagnostic();

        static IEnumerator Run(string pipelineName, bool grdEnabled)
        {
            return Run(pipelineName, grdEnabled, false);
        }

        static IEnumerator Run(string pipelineName, bool grdEnabled, bool quiltOnly)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(pipelineName, grdEnabled, quiltOnly);
                fixture.Setup();
                yield return fixture.PrepareVfxAsset();
                yield return fixture.WarmupAndPause();
                fixture.SetEffectEnabled(false);
                yield return fixture.WaitForRender();
                fixture.Capture("before");
                fixture.SetEffectEnabled(true);
                yield return fixture.WaitForRender();
                fixture.Capture("after");
                fixture.AssertEvidence();
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        static IEnumerator RunExperimental(string pipelineName, bool grdEnabled, bool quiltOnly, int driverWidth, int driverHeight, bool separated)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(pipelineName, grdEnabled, quiltOnly, driverWidth, driverHeight, separated, false, false);
                fixture.Setup();
                yield return fixture.PrepareVfxAsset();
                yield return fixture.WarmupAndPause();
                fixture.SetEffectEnabled(false);
                yield return fixture.WaitForRender();
                fixture.Capture("before");
                fixture.SetEffectEnabled(true);
                yield return fixture.WaitForRender();
                fixture.Capture("after");
                fixture.AssertEvidence();
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        static IEnumerator RunNormalOnly()
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture("hdrp", false, false, 512, 384, true, false, true);
                fixture.Setup();
                yield return fixture.PrepareVfxAsset();
                yield return fixture.WarmupNormalOnly();
                fixture.SetEffectEnabled(false);
                yield return fixture.WaitForRender();
                fixture.CaptureNormalOnly("before");
                fixture.SetEffectEnabled(true);
                yield return fixture.WaitForRender();
                fixture.CaptureNormalOnly("after");
                fixture.AssertNormalOnlyEvidence();
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        static IEnumerator RunDriver(string pipelineName, bool grdEnabled, int viewCount)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(pipelineName, grdEnabled, false, 1, 1, true, false, false, viewCount, true);
                fixture.Setup();
                yield return fixture.PrepareVfxAsset();
                yield return null;
                fixture.AssertDriverRunning();
                yield return fixture.WarmupAndPause();
                fixture.SetEffectEnabled(false);
                yield return fixture.WaitForRender();
                fixture.Capture("before");
                fixture.SetEffectEnabled(true);
                yield return fixture.WaitForRender();
                fixture.Capture("after");
                fixture.AssertEvidence();
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        static IEnumerator RunHdrpCloneRequestDiagnostic()
        {
#if !UNITY_EDITOR
            Assert.Fail("VFX compatibility fixtures require the Unity Editor");
            yield break;
#else
            Fixture fixture = null;
            try
            {
                fixture = new Fixture("hdrp", false, false, 512, 384, true, false, true);
                fixture.Setup();
                yield return fixture.PrepareVfxAsset();
                yield return fixture.WarmupNormalOnly();
                fixture.WriteShaderEvidence("diagnostic-shader");
                fixture.SetEffectEnabled(false);
                yield return fixture.WaitForRender();
                fixture.CaptureNormalOnly("diagnostic-normal-before");
                fixture.SetEffectEnabled(true);
                yield return fixture.WaitForRender();
                fixture.CaptureNormalOnly("diagnostic-normal-after");
                int normalChanged = fixture.CountNormalChanged();
                fixture.WriteDiagnosticEvidence("diagnostic-normal-result", "stage=normal-camera\nchangedPixels=" + normalChanged + "\n");
                Assert.Greater(normalChanged, 20, "HDRP normal camera clone control changed fewer than 20 pixels");
                fixture.SetEffectEnabled(false);
                yield return fixture.RenderStandardRequestFrames(3);
                fixture.CaptureNormalOnly("diagnostic-request-before");
                fixture.SetEffectEnabled(true);
                yield return fixture.RenderStandardRequestFrames(3);
                fixture.CaptureNormalOnly("diagnostic-request-after");
                int requestChanged = fixture.CountNormalChanged();
                fixture.WriteDiagnosticEvidence("diagnostic-request-result", "stage=standard-request\nframesBefore=3\nframesAfter=3\nchangedPixels=" + requestChanged + "\n");
                fixture.SetEffectEnabled(false);
                yield return fixture.RenderStandardRequestFrames(3, true);
                fixture.CaptureNormalOnly("diagnostic-request-enabled-before");
                fixture.SetEffectEnabled(true);
                yield return fixture.RenderStandardRequestFrames(3, true);
                fixture.CaptureNormalOnly("diagnostic-request-enabled-after");
                int enabledRequestChanged = fixture.CountNormalChanged();
                fixture.WriteDiagnosticEvidence("diagnostic-request-enabled-result", "stage=standard-request-camera-enabled-during-submit\nframesBefore=3\nframesAfter=3\nchangedPixels=" + enabledRequestChanged + "\ncameraEnabledDuringYield=false\n");
                Assert.Greater(requestChanged, 20, "HDRP StandardRequest clone changed fewer than 20 pixels");
                Assert.Greater(enabledRequestChanged, 20, "HDRP StandardRequest enabled-camera variant changed fewer than 20 pixels");
            }
            finally
            {
                fixture?.Dispose();
            }
#endif
        }

        sealed class Fixture
        {
            const int TileWidth = 128;
            const int TileHeight = 96;
            const int Seed = 17;
            readonly string pipelineName;
            readonly bool grdEnabled;
            readonly bool quiltOnly;
            readonly int driverWidth;
            readonly int driverHeight;
            readonly bool separated;
            readonly bool extraPrepare;
            readonly bool normalOnly;
            readonly int views;
            readonly int columns;
            readonly int rows;
            readonly bool useDriver;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            readonly string evidenceDirectory;
            GameObject root;
            Scene fixtureScene;
            Scene previousActiveScene;
            readonly List<LightState> savedLights = new List<LightState>();
            readonly List<CameraState> savedCameras = new List<CameraState>();
            Light previousSun;
            GameObject cube;
            Camera camera;
            VisualEffect effect;
            QuiltRenderer quilt;
            Camera controlCamera;
            RenderTexture controlTarget;
            Texture2D before;
            Texture2D after;
            Texture2D controlBefore;
            Texture2D controlAfter;
            Texture2D normalBefore;
            Texture2D normalAfter;
            RenderTexture normalTarget;
            VFXRenderer vfxRenderer;
            HdrpVfxCameraDriverExperimental hdrpDriver;
            VfxCameraPrepareBehaviour prepareBehaviour;
            RenderPipelineAsset oldDefaultPipeline;
            RenderPipelineAsset oldQualityPipeline;
            RenderPipelineAsset ownedPipeline;
            UnityEngine.Object ownedRendererData;
            int requestedSteps;
            const int MaxWarmupSteps = 30;
            bool autoAdvanceDuringWarmup;
            int aliveCount;
            bool culled;
            string clonedAssetPath;
            bool clonedAssetOwned;
            bool assetPrepared;
            bool shaderEvidenceWritten;

            public Fixture(string pipeline, bool grd, bool onlyQuilt)
                : this(pipeline, grd, onlyQuilt, 512, 384, false, true, false)
            {
            }

            public Fixture(string pipeline, bool grd, bool onlyQuilt, int width, int height, bool useSeparatedGeometry, bool useExtraPrepare, bool useNormalOnly)
                : this(pipeline, grd, onlyQuilt, width, height, useSeparatedGeometry, useExtraPrepare, useNormalOnly, 8, false)
            {
            }

            public Fixture(string pipeline, bool grd, bool onlyQuilt, int width, int height, bool useSeparatedGeometry, bool useExtraPrepare, bool useNormalOnly, int viewCount, bool driver)
            {
                pipelineName = pipeline;
                grdEnabled = grd;
                quiltOnly = onlyQuilt;
                driverWidth = width;
                driverHeight = height;
                separated = useSeparatedGeometry;
                extraPrepare = useExtraPrepare;
                normalOnly = useNormalOnly;
                views = viewCount;
                columns = viewCount == 48 ? 8 : 4;
                rows = viewCount == 48 ? 6 : 2;
                useDriver = driver;
                evidenceDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "vfx-compatibility"));
            }

            public void Setup()
            {
#if !UNITY_EDITOR
                Assert.Fail("VFX compatibility fixtures require the Unity Editor");
#else
                VisualEffectAsset asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>("Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
                Assert.IsNotNull(asset, "VFX asset unavailable at Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
#endif
                previousActiveScene = SceneManager.GetActiveScene();
                previousSun = RenderSettings.sun;
                SaveAndDisableSceneRenderers();
                fixtureScene = SceneManager.CreateScene("VFXCompatibilityFixture_" + pipelineName + "_" + (grdEnabled ? "On" : "Off"));
                SceneManager.SetActiveScene(fixtureScene);
                root = new GameObject("VFXCompatibility_" + pipelineName); owned.Add(root);
                oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
                oldQualityPipeline = QualitySettings.renderPipeline;
                SetupPipeline();
                GameObject cameraObject = new GameObject("VFXCompatibility.Camera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.transform.localPosition = Vector3.zero;
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = 50f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 100f;
                if (extraPrepare)
                {
                    prepareBehaviour = cameraObject.AddComponent<VfxCameraPrepareBehaviour>();
                    prepareBehaviour.targetCamera = camera;
                }
                if (pipelineName == "urp")
                {
                    UniversalAdditionalCameraData cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                    cameraData.renderType = CameraRenderType.Base;
                    cameraData.renderPostProcessing = true;
                    quilt = cameraObject.AddComponent<QuiltRenderer>();
                    UrpQuiltCamera guard = cameraObject.AddComponent<UrpQuiltCamera>();
                    quilt.GuardBehaviour = guard;
                }
                else
                {
                    HDAdditionalCameraData cameraData = cameraObject.AddComponent<HDAdditionalCameraData>();
                    cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    cameraData.backgroundColorHDR = Color.black;
                    cameraData.volumeLayerMask = 1 << 31;
                    quilt = cameraObject.AddComponent<QuiltRenderer>();
                    HdrpQuiltCamera guard = cameraObject.AddComponent<HdrpQuiltCamera>();
                    quilt.GuardBehaviour = guard;
                    if (useDriver)
                    {
                        hdrpDriver = cameraObject.AddComponent<HdrpVfxCameraDriverExperimental>();
                        hdrpDriver.vfxLayers = 1 << 31;
                        hdrpDriver.runWhenManualRendering = true;
                        hdrpDriver.enableDriver = true;
                    }
                }
                quilt.sourceCamera = camera;
                quilt.autoRender = false;
                GameObject effectObject = new GameObject("VFXCompatibility.Effect");
                effectObject.transform.SetParent(root.transform, false);
                effectObject.transform.localPosition = separated ? new Vector3(0f, .5f, 0f) : new Vector3(0f, .5f, 2.2f);
                effect = effectObject.AddComponent<VisualEffect>();
#if UNITY_EDITOR
                effect.visualEffectAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>("Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
#endif
                effect.resetSeedOnPlay = false;
                effect.startSeed = Seed;
                effect.pause = true;
                vfxRenderer = effectObject.GetComponent<VFXRenderer>();
                if (vfxRenderer == null) vfxRenderer = effectObject.AddComponent<VFXRenderer>();
                Assert.IsNotNull(vfxRenderer, "VFXRenderer is unavailable");
                quilt.settings.quiltWidth = TileWidth * columns;
                quilt.settings.quiltHeight = TileHeight * rows;
                quilt.settings.columns = columns;
                quilt.settings.rows = rows;
                quilt.settings.views = views;
                quilt.settings.viewAspect = (float)TileWidth / TileHeight;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;
                GameObject controlObject = new GameObject("VFXCompatibility.ControlCamera");
                controlObject.transform.SetParent(root.transform, false);
                controlObject.transform.localPosition = separated ? new Vector3(0f, 0f, -4f) : Vector3.zero;
                controlCamera = controlObject.AddComponent<Camera>();
                controlCamera.enabled = !quiltOnly && !normalOnly && !useDriver;
                controlCamera.clearFlags = CameraClearFlags.SolidColor;
                controlCamera.backgroundColor = Color.black;
                controlCamera.fieldOfView = 50f;
                controlCamera.nearClipPlane = 0.01f;
                controlCamera.farClipPlane = 100f;
                if (pipelineName == "urp") controlObject.AddComponent<UniversalAdditionalCameraData>();
                else
                {
                    HDAdditionalCameraData controlData = controlObject.AddComponent<HDAdditionalCameraData>();
                    controlData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    controlData.backgroundColorHDR = Color.black;
                    controlData.volumeLayerMask = 1 << 31;
                }
                controlCamera.cullingMask = 1 << 31;
                if (pipelineName == "urp") controlObject.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
                controlTarget = new RenderTexture(driverWidth, driverHeight, 24, RenderTextureFormat.ARGB32) { name = "VFXCompatibility.ControlTarget" };
                controlTarget.Create();
                controlCamera.targetTexture = controlTarget;
                owned.Add(controlObject);
                AddLitCube();
                if (separated)
                {
                    camera.transform.localPosition = new Vector3(0f, 0f, -4f);
                    controlCamera.transform.localPosition = new Vector3(0f, 0f, -4f);
                }
                AddLightAndExposure();
                if (normalOnly)
                {
                    normalTarget = new RenderTexture(driverWidth, driverHeight, 24, RenderTextureFormat.ARGB32) { name = "VFXCompatibility.NormalTarget" };
                    normalTarget.Create();
                    camera.targetTexture = normalTarget;
                    camera.enabled = true;
                }
                SetLayerRecursive(root, 31);
                camera.cullingMask = 1 << 31;
                controlCamera.cullingMask = 1 << 31;
                Directory.CreateDirectory(evidenceDirectory);
                WriteEvidence("setup", "pipeline=" + pipelineName + "\ngrd=" + grdEnabled + "\ntestType=" + (normalOnly ? "normal-only" : (quiltOnly ? "quilt-only" : (useDriver ? "driver" : "control"))) + "\ndriverWidth=" + driverWidth + "\ndriverHeight=" + driverHeight + "\nseparatedGeometry=" + separated + "\nasset=Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx\nseed=" + Seed + "\nviews=" + views + "\npreviousScene=" + Sanitize(previousActiveScene.name) + "\nfixtureScene=" + Sanitize(fixtureScene.name) + "\nsavedLights=" + savedLights.Count + "\nsavedCameras=" + savedCameras.Count + "\npreviousSun=" + (previousSun == null ? "null" : Sanitize(previousSun.name)) + "\nsourceCameraEnabled=" + camera.enabled + "\ncontrolCameraEnabled=" + controlCamera.enabled + "\ndriverEnabled=" + (hdrpDriver != null) + "\nvfxPrepareCameraUpdate=" + extraPrepare + "\nvfxPrepareCameraLate=" + extraPrepare + "\nlayer=31\n");
            }

            void SetupPipeline()
            {
#if UNITY_EDITOR
                if (pipelineName == "urp")
                {
                    UniversalRendererData data = ScriptableObject.CreateInstance<UniversalRendererData>();
                    data.renderingMode = RenderingMode.ForwardPlus;
                    ownedRendererData = data;
                    UniversalRenderPipelineAsset asset = UniversalRenderPipelineAsset.Create(data);
                    asset.gpuResidentDrawerMode = grdEnabled ? GPUResidentDrawerMode.InstancedDrawing : GPUResidentDrawerMode.Disabled;
                    asset.gpuResidentDrawerEnableOcclusionCullingInCameras = false;
                    asset.smallMeshScreenPercentage = 0f;
                    asset.useSRPBatcher = true;
                    ownedPipeline = asset;
                }
                else
                {
                    HDRenderPipelineAsset asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                    asset.gpuResidentDrawerMode = grdEnabled ? GPUResidentDrawerMode.InstancedDrawing : GPUResidentDrawerMode.Disabled;
                    var platformSettings = asset.currentPlatformRenderPipelineSettings;
                    platformSettings.gpuResidentDrawerSettings.enableOcclusionCullingInCameras = false;
                    platformSettings.gpuResidentDrawerSettings.smallMeshScreenPercentage = 0f;
                    asset.currentPlatformRenderPipelineSettings = platformSettings;
                    ownedPipeline = asset;
                }
                GraphicsSettings.defaultRenderPipeline = ownedPipeline;
                QualitySettings.renderPipeline = ownedPipeline;
#else
                Assert.Fail("VFX compatibility fixtures require the Unity Editor");
#endif
            }

            void AddLightAndExposure()
            {
                GameObject lightObject = new GameObject("VFXCompatibility.DirectionalLight");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = pipelineName == "hdrp" ? 10f : 2f;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                light.cullingMask = 1 << 31;
                RenderSettings.sun = light;
                owned.Add(lightObject);
#if UNITY_EDITOR
                if (pipelineName == "hdrp")
                {
                    lightObject.AddComponent<HDAdditionalLightData>();
                    GameObject volumeObject = new GameObject("VFXCompatibility.FixedExposure");
                    volumeObject.transform.SetParent(root.transform, false);
                    volumeObject.layer = 31;
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
                    UnityEngine.Rendering.HighDefinition.MotionBlur motionBlur = profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
                    motionBlur.active = true;
                    motionBlur.intensity.value = 0f;
                    motionBlur.intensity.overrideState = true;
                    ScreenSpaceReflection reflection = profile.Add<ScreenSpaceReflection>(true);
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
                    owned.Add(volumeObject);
                    owned.Add(profile);
                }
#endif
            }

            void SaveAndDisableSceneRenderers()
            {
                Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
                foreach (Light item in lights)
                {
                    if (item == null) continue;
                    savedLights.Add(new LightState { component = item, enabled = item.enabled });
                    item.enabled = false;
                }
                Camera[] cameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
                foreach (Camera item in cameras)
                {
                    if (item == null) continue;
                    savedCameras.Add(new CameraState { component = item, enabled = item.enabled });
                    item.enabled = false;
                }
            }

            static void SetLayerRecursive(GameObject target, int layer)
            {
                target.layer = layer;
                foreach (Transform child in target.transform) SetLayerRecursive(child.gameObject, layer);
            }

            void AddLitCube()
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "VFXCompatibility_LitCube";
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(0f, 0f, 3f);
                string shaderName = pipelineName == "hdrp" ? "HDRP/Lit" : "Universal Render Pipeline/Lit";
                Shader shader = Shader.Find(shaderName);
                Assert.IsNotNull(shader, shaderName + " shader is unavailable");
                Material material = new Material(shader);
                material.color = Color.white;
                cube.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(cube);
                owned.Add(material);
            }

            public IEnumerator PrepareVfxAsset()
            {
#if !UNITY_EDITOR
                yield break;
#else
                if (assetPrepared) yield break;
                yield return null;
                yield return null;
                bool hdrp = pipelineName == "hdrp";
                bool pipelineActive = hdrp ? GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset : GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
                WriteEvidence("asset-preparation", "stage=pipeline-active\npipeline=" + pipelineName + "\nactive=" + pipelineActive + "\n");
                Assert.IsTrue(pipelineActive, pipelineName + " pipeline is not active before VFX asset import");
                if (quiltOnly)
                {
                    quilt.RenderQuilt();
                    yield return null;
                }
                string sourceAssetPath = "Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx";
                clonedAssetPath = AssetDatabase.GenerateUniqueAssetPath("Assets/VFXCompatibility/Simple_Burst_" + pipelineName + "_Diagnostic.vfx");
                bool copied = AssetDatabase.CopyAsset(sourceAssetPath, clonedAssetPath);
                WriteEvidence("asset-preparation", "stage=copy\npipeline=" + pipelineName + "\nsource=" + sourceAssetPath + "\nclone=" + clonedAssetPath + "\ncopySucceeded=" + copied + "\n");
                Assert.IsTrue(copied, "failed to copy VFX asset for " + pipelineName);
                clonedAssetOwned = true;
                AssetDatabase.ImportAsset(clonedAssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                VisualEffectAsset clone = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(clonedAssetPath);
                WriteEvidence("asset-preparation", "stage=import\npipeline=" + pipelineName + "\ncloneLoaded=" + (clone != null) + "\n");
                Assert.IsNotNull(clone, pipelineName + " VFX clone did not import");
                ReplaceVisualEffectAsset(clone);
                yield return null;
                assetPrepared = true;
#endif
            }

            public IEnumerator WarmupAndPause()
            {
                yield return null;
                for (int cacheFrame = 0; cacheFrame < 60; ++cacheFrame)
                {
                    PrepareVfxCamera();
                    quilt.RenderQuilt();
                    yield return null;
                }
                EnsureShaderEvidence();
                effect.Reinit();
                effect.Play();
                effect.pause = false;
                requestedSteps = 0;
                autoAdvanceDuringWarmup = true;
                for (int step = 0; step < MaxWarmupSteps; ++step)
                {
                    effect.Simulate(1f / 60f, 1);
                    requestedSteps++;
                    PrepareVfxCamera();
                    quilt.RenderQuilt();
                    yield return null;
                }
                effect.pause = true;
                autoAdvanceDuringWarmup = false;
                double readbackStarted = Time.realtimeSinceStartupAsDouble;
                while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartupAsDouble - readbackStarted < 5.0)
                {
                    PrepareVfxCamera();
                    quilt.RenderQuilt();
                    yield return null;
                    aliveCount = effect.aliveParticleCount;
                }
                aliveCount = effect.aliveParticleCount;
                culled = effect.culled;
                WriteEvidence("simulation", "maxWarmupSteps=" + MaxWarmupSteps + "\nrequestedSteps=" + requestedSteps + "\nautoAdvanceDuringWarmup=true\naliveParticleCount=" + aliveCount + "\nculled=" + culled + "\n");
                Assert.Greater(aliveCount, 0, "VFX warmup produced no live particles");
            }

            public void AssertDriverRunning()
            {
                Assert.IsNotNull(hdrpDriver, "HDRP VFX driver was not created");
                Assert.IsTrue(hdrpDriver.IsRunning, hdrpDriver.LastError);
                Assert.IsFalse(camera.enabled, "source camera must remain disabled while the driver is active");
            }

            public void ReplaceVisualEffectAsset(VisualEffectAsset asset)
            {
                Assert.IsNotNull(asset, "diagnostic VFX asset is null");
                effect.visualEffectAsset = asset;
                effect.Reinit();
            }

            public IEnumerator RenderStandardRequestFrames(int frameCount, bool enableDuringSubmit = false)
            {
                Assert.IsNotNull(normalTarget, "normal target is unavailable for StandardRequest");
                camera.enabled = false;
                RenderPipeline.StandardRequest request = new RenderPipeline.StandardRequest { destination = normalTarget };
                Assert.IsTrue(RenderPipeline.SupportsRenderRequest(camera, request), "HDRP does not support StandardRequest for diagnostic camera");
                for (int frame = 0; frame < frameCount; ++frame)
                {
                    camera.enabled = enableDuringSubmit;
                    try
                    {
                        RenderPipeline.SubmitRenderRequest(camera, request);
                    }
                    finally
                    {
                        camera.enabled = false;
                    }
                    yield return null;
                }
            }

            public int CountNormalChanged()
            {
                return CountChanged(normalBefore, normalAfter);
            }

            public void WriteShaderEvidence(string name)
            {
                WriteVfxShaderEvidence(name, "HDRenderPipeline");
            }

            public void WriteVfxShaderEvidence(string name, string expectedTag)
            {
                Material material = vfxRenderer == null ? null : vfxRenderer.sharedMaterial;
                Shader shader = material == null ? null : material.shader;
                string content = "stage=shader\nmaterial=" + (material == null ? "null" : Sanitize(material.name)) + "\nshader=" + (shader == null ? "null" : Sanitize(shader.name)) + "\nshaderSupported=" + (shader != null && shader.isSupported) + "\nshaderPassCount=" + (material == null ? 0 : material.passCount) + "\nrenderPipelineTag=" + (material == null ? "null" : Sanitize(material.GetTag("RenderPipeline", false, "<missing>"))) + "\n";
                if (material != null)
                    for (int pass = 0; pass < material.passCount; ++pass)
                        content += "pass=" + pass + " name=" + Sanitize(material.GetPassName(pass)) + "\n";
                WriteEvidence(name, content);
                Assert.IsNotNull(material, "diagnostic VFX material is unavailable");
                Assert.IsNotNull(shader, "diagnostic VFX shader is unavailable");
                Assert.AreEqual(expectedTag, material.GetTag("RenderPipeline", false, "<missing>"), "diagnostic VFX shader has an unexpected RenderPipeline tag");
                shaderEvidenceWritten = true;
            }

            void EnsureShaderEvidence()
            {
                if (!shaderEvidenceWritten)
                    WriteVfxShaderEvidence("asset-shader", pipelineName == "hdrp" ? "HDRenderPipeline" : "UniversalPipeline");
            }

            public void WriteDiagnosticEvidence(string name, string content)
            {
                WriteEvidence(name, content);
            }

            public IEnumerator WarmupNormalOnly()
            {
                yield return null;
                for (int cacheFrame = 0; cacheFrame < 60; ++cacheFrame) yield return null;
                EnsureShaderEvidence();
                effect.Reinit();
                effect.Play();
                effect.pause = false;
                requestedSteps = 0;
                autoAdvanceDuringWarmup = true;
                for (int step = 0; step < MaxWarmupSteps; ++step)
                {
                    effect.Simulate(1f / 60f, 1);
                    requestedSteps++;
                    yield return null;
                }
                effect.pause = true;
                autoAdvanceDuringWarmup = false;
                double readbackStarted = Time.realtimeSinceStartupAsDouble;
                while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartupAsDouble - readbackStarted < 5.0)
                {
                    yield return null;
                    aliveCount = effect.aliveParticleCount;
                }
                aliveCount = effect.aliveParticleCount;
                culled = effect.culled;
                WriteEvidence("normal-only-simulation", "maxWarmupSteps=" + MaxWarmupSteps + "\nrequestedSteps=" + requestedSteps + "\naliveParticleCount=" + aliveCount + "\nculled=" + culled + "\n");
                Assert.Greater(aliveCount, 0, "VFX warmup produced no live particles");
            }

            public void SetEffectEnabled(bool enabled)
            {
                if (vfxRenderer != null) vfxRenderer.enabled = enabled;
                effect.enabled = true;
                effect.pause = true;
            }

            public IEnumerator WaitForRender()
            {
                yield return null;
                yield return null;
                yield return null;
            }

            void PrepareVfxCamera()
            {
                if (!extraPrepare) return;
                VFXManager.PrepareCamera(camera);
            }

            public void Capture(string suffix)
            {
                try
                {
                    PrepareVfxCamera();
                    quilt.RenderQuilt();
                    RenderTexture previous = RenderTexture.active;
                    try
                    {
                        RenderTexture.active = quilt.QuiltTexture;
                        Texture2D image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
                        image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
                        image.Apply();
                        if (suffix == "before")
                        {
                            if (before != null) UnityEngine.Object.DestroyImmediate(before);
                            before = image;
                        }
                        else
                        {
                            if (after != null) UnityEngine.Object.DestroyImmediate(after);
                            after = image;
                        }
                        File.WriteAllBytes(Path.Combine(evidenceDirectory, FilePrefix() + "-" + suffix + ".png"), image.EncodeToPNG());
                    }
                    finally
                    {
                        RenderTexture.active = previous;
                    }
                    if (controlCamera.enabled)
                    {
                        Texture2D controlImage = ReadControlImage();
                        if (suffix == "before") controlBefore = controlImage; else controlAfter = controlImage;
                        File.WriteAllBytes(Path.Combine(evidenceDirectory, FilePrefix() + "-control-" + suffix + ".png"), controlImage.EncodeToPNG());
                    }
                }
                catch (Exception error)
                {
                    WriteEvidence(suffix + "-error", "error=" + error.GetType().Name + "\n");
                    throw;
                }
            }

            public void CaptureNormalOnly(string suffix)
            {
                Texture2D image = ReadTargetImage(normalTarget);
                if (suffix.EndsWith("before"))
                {
                    if (normalBefore != null) UnityEngine.Object.DestroyImmediate(normalBefore);
                    normalBefore = image;
                }
                else
                {
                    if (normalAfter != null) UnityEngine.Object.DestroyImmediate(normalAfter);
                    normalAfter = image;
                }
                File.WriteAllBytes(Path.Combine(evidenceDirectory, FilePrefix() + "-" + suffix + ".png"), image.EncodeToPNG());
            }

            public void AssertNormalOnlyEvidence()
            {
                int changed = CountChanged(normalBefore, normalAfter);
                Shader shader = vfxRenderer.sharedMaterial == null ? null : vfxRenderer.sharedMaterial.shader;
                int passCount = shader == null ? 0 : shader.passCount;
                bool shaderSupported = shader != null && shader.isSupported;
                WriteEvidence("normal-only-result", "changedPixels=" + changed + "\naliveParticleCount=" + aliveCount + "\nculled=" + culled + "\nshader=" + (shader == null ? "null" : Sanitize(shader.name)) + "\nshaderSupported=" + shaderSupported + "\nshaderPassCount=" + passCount + "\n");
                Assert.IsNotNull(normalBefore, "normal-only before image was not captured");
                Assert.IsNotNull(normalAfter, "normal-only after image was not captured");
                Assert.Greater(changed, 20, "normal camera VFX changed fewer than 20 pixels");
            }

            static int CountChanged(Texture2D first, Texture2D second)
            {
                if (first == null || second == null || first.width != second.width || first.height != second.height) return 0;
                Color[] firstPixels = first.GetPixels();
                Color[] secondPixels = second.GetPixels();
                int changed = 0;
                for (int index = 0; index < firstPixels.Length; ++index)
                    if (Mathf.Abs(firstPixels[index].r - secondPixels[index].r) + Mathf.Abs(firstPixels[index].g - secondPixels[index].g) + Mathf.Abs(firstPixels[index].b - secondPixels[index].b) > 0.01f) changed++;
                return changed;
            }

            static Texture2D ReadTargetImage(RenderTexture target)
            {
                RenderTexture previous = RenderTexture.active;
                Texture2D image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = target;
                    image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
                    image.Apply();
                    return image;
                }
                finally { RenderTexture.active = previous; }
            }

            public void AssertEvidence()
            {
                int changed = -1;
                bool allTilesChanged = false;
                string tileEvidence = string.Empty;
                if (before != null && after != null)
                {
                    Color[] first = before.GetPixels();
                    Color[] second = after.GetPixels();
                    changed = 0;
                    for (int i = 0; i < first.Length; ++i)
                        if (Mathf.Abs(first[i].r - second[i].r) + Mathf.Abs(first[i].g - second[i].g) + Mathf.Abs(first[i].b - second[i].b) > 0.01f) changed++;
                    allTilesChanged = true;
                    for (int view = 0; view < views; ++view)
                    {
                        RectInt tile = quilt.settings.GetLayout().GetTile(view);
                        int tileChanged = 0;
                        for (int y = tile.y; y < tile.yMax; ++y)
                            for (int x = tile.x; x < tile.xMax; ++x)
                            {
                                int index = y * before.width + x;
                                if (Mathf.Abs(first[index].r - second[index].r) + Mathf.Abs(first[index].g - second[index].g) + Mathf.Abs(first[index].b - second[index].b) > 0.01f) tileChanged++;
                            }
                        allTilesChanged &= tileChanged > 20;
                        tileEvidence += "tile=" + view + " changed=" + tileChanged + "\n";
                    }
                }
                int controlChanged = -1;
                if (controlBefore != null && controlAfter != null)
                {
                    Color[] controlFirst = controlBefore.GetPixels();
                    Color[] controlSecond = controlAfter.GetPixels();
                    controlChanged = 0;
                    for (int i = 0; i < controlFirst.Length; ++i)
                        if (Mathf.Abs(controlFirst[i].r - controlSecond[i].r) + Mathf.Abs(controlFirst[i].g - controlSecond[i].g) + Mathf.Abs(controlFirst[i].b - controlSecond[i].b) > 0.01f) controlChanged++;
                }
                bool requiresNormalControlValidation = !quiltOnly && driverWidth * driverHeight > 20;
                bool normalControlVisualValidated = requiresNormalControlValidation && controlChanged > 20;
                WriteEvidence("result", "changedPixels=" + changed + "\nallTilesChanged=" + allTilesChanged + "\n" + tileEvidence + "aliveParticleCount=" + aliveCount + "\nculled=" + culled + "\nrequestedSteps=" + requestedSteps + "\ncontrolChangedPixels=" + controlChanged + "\nnormalControlVisualValidated=" + normalControlVisualValidated + "\ncontrolCameraEnabled=" + (controlCamera != null && controlCamera.enabled) + "\n");
                Assert.IsNotNull(before, "VFX before image was not captured");
                Assert.IsNotNull(after, "VFX after image was not captured");
                Assert.Greater(changed, 20, "VFX renderer toggle changed fewer than 20 quilt pixels");
                Assert.IsTrue(allTilesChanged, "VFX renderer toggle changed fewer than 20 pixels in one or more tiles");
                if (requiresNormalControlValidation)
                {
                    Assert.IsNotNull(controlBefore, "control camera before image was not captured");
                    Assert.IsNotNull(controlAfter, "control camera after image was not captured");
                    Assert.Greater(controlChanged, 20, "standard control camera changed fewer than 20 pixels");
                }
            }

            Texture2D ReadControlImage()
            {
                RenderTexture previous = RenderTexture.active;
                Texture2D image = new Texture2D(controlTarget.width, controlTarget.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = controlTarget;
                    image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
                    image.Apply();
                    return image;
                }
                finally { RenderTexture.active = previous; }
            }

            void WriteEvidence(string name, string content)
            {
                Directory.CreateDirectory(evidenceDirectory);
                File.WriteAllText(Path.Combine(evidenceDirectory, FilePrefix() + "-" + name + ".txt"), content);
            }

            string FilePrefix()
            {
                string experiment = extraPrepare ? "" : "-driver" + driverWidth + "x" + driverHeight + (separated ? "-separated" : "-normal") + "-noextra";
                return pipelineName + "-grd-" + grdEnabled + "-views" + views + experiment + (normalOnly ? "-normal-only" : "") + (quiltOnly ? "-quilt-only" : "") + (useDriver ? "-driver-enabled" : "");
            }

            static string Sanitize(string value)
            {
                if (string.IsNullOrEmpty(value)) return "";
                char[] chars = new char[value.Length];
                int count = 0;
                foreach (char valueChar in value)
                    if (valueChar >= 0x20 && valueChar <= 0x7e) chars[count++] = valueChar;
                return new string(chars, 0, count);
            }

            public void Dispose()
            {
                RenderTexture.active = null;
                if (before != null) UnityEngine.Object.DestroyImmediate(before);
                if (after != null && after != before) UnityEngine.Object.DestroyImmediate(after);
                if (normalBefore != null) UnityEngine.Object.DestroyImmediate(normalBefore);
                if (normalAfter != null && normalAfter != normalBefore) UnityEngine.Object.DestroyImmediate(normalAfter);
                if (controlBefore != null) UnityEngine.Object.DestroyImmediate(controlBefore);
                if (controlAfter != null && controlAfter != controlBefore) UnityEngine.Object.DestroyImmediate(controlAfter);
                if (camera != null && camera.targetTexture == normalTarget) camera.targetTexture = null;
                if (normalTarget != null) { normalTarget.Release(); UnityEngine.Object.DestroyImmediate(normalTarget); }
                if (controlCamera != null) controlCamera.targetTexture = null;
                if (camera != null && camera.targetTexture == controlTarget) camera.targetTexture = null;
                if (controlTarget != null) { controlTarget.Release(); UnityEngine.Object.DestroyImmediate(controlTarget); }
                foreach (UnityEngine.Object item in owned)
                {
                    if (item != null) UnityEngine.Object.DestroyImmediate(item);
                }
                GraphicsSettings.defaultRenderPipeline = oldDefaultPipeline;
                QualitySettings.renderPipeline = oldQualityPipeline;
#if UNITY_EDITOR
                if (clonedAssetOwned && !string.IsNullOrEmpty(clonedAssetPath))
                    AssetDatabase.DeleteAsset(clonedAssetPath);
#endif
                if (ownedPipeline != null) UnityEngine.Object.DestroyImmediate(ownedPipeline);
                if (ownedRendererData != null) UnityEngine.Object.DestroyImmediate(ownedRendererData);
                foreach (LightState state in savedLights) if (state.component != null) state.component.enabled = state.enabled;
                foreach (CameraState state in savedCameras) if (state.component != null) state.component.enabled = state.enabled;
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded) SceneManager.SetActiveScene(previousActiveScene);
                RenderSettings.sun = previousSun;
                if (fixtureScene.IsValid() && fixtureScene.isLoaded) SceneManager.UnloadSceneAsync(fixtureScene);
            }

            struct LightState { public Light component; public bool enabled; }
            struct CameraState { public Camera component; public bool enabled; }
        }
    }
}
