using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using Unity.Profiling;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using LookingGlass.Community;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Rendering;
#endif

namespace LookingGlass.GRDValidation
{
    public enum GrdPipelineKind { Urp, Hdrp }
    public enum GrdLoopPhase { BeforeFinish, AfterFinish, Automatic }

    public sealed class GrdValidationPlayModeTests
    {
        const int TileWidth = 128;
        const int TileHeight = 96;
        static readonly string ResultDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "grd-validation"));

        [UnityTest]
        public IEnumerator URP_1_Off() { yield return RunCase(GrdPipelineKind.Urp, 1, false, GrdLoopPhase.BeforeFinish); }
        [UnityTest] public IEnumerator URP_1_On() { yield return RunCase(GrdPipelineKind.Urp, 1, true, GrdLoopPhase.BeforeFinish); }
        [UnityTest] public IEnumerator URP_8_Off() { yield return RunCase(GrdPipelineKind.Urp, 8, false, GrdLoopPhase.BeforeFinish); }
        [UnityTest] public IEnumerator URP_8_On() { yield return RunCase(GrdPipelineKind.Urp, 8, true, GrdLoopPhase.BeforeFinish); }
        [UnityTest] public IEnumerator URP_48_Off() { yield return RunCase(GrdPipelineKind.Urp, 48, false, GrdLoopPhase.BeforeFinish); }
        [UnityTest] public IEnumerator URP_48_On() { yield return RunCase(GrdPipelineKind.Urp, 48, true, GrdLoopPhase.BeforeFinish); }
        [UnityTest] public IEnumerator URP_8_On_AfterFinish() { yield return RunCase(GrdPipelineKind.Urp, 8, true, GrdLoopPhase.AfterFinish); }
        [UnityTest] public IEnumerator URP_8_Off_AfterFinish() { yield return RunCase(GrdPipelineKind.Urp, 8, false, GrdLoopPhase.AfterFinish); }
        [UnityTest] public IEnumerator URP_1_On_Automatic() { yield return RunCase(GrdPipelineKind.Urp, 1, true, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator URP_1_Off_Automatic() { yield return RunCase(GrdPipelineKind.Urp, 1, false, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator URP_8_On_Automatic() { yield return RunCase(GrdPipelineKind.Urp, 8, true, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator URP_8_Off_Automatic() { yield return RunCase(GrdPipelineKind.Urp, 8, false, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator URP_48_On_Automatic() { yield return RunCase(GrdPipelineKind.Urp, 48, true, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator URP_48_Off_Automatic() { yield return RunCase(GrdPipelineKind.Urp, 48, false, GrdLoopPhase.Automatic); }

        [UnityTest] public IEnumerator HDRP_1_Off_Automatic() { yield return RunCase(GrdPipelineKind.Hdrp, 1, false, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator HDRP_1_On_Automatic() { yield return RunCase(GrdPipelineKind.Hdrp, 1, true, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator HDRP_8_Off_Automatic() { yield return RunCase(GrdPipelineKind.Hdrp, 8, false, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator HDRP_8_On_Automatic() { yield return RunCase(GrdPipelineKind.Hdrp, 8, true, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator HDRP_48_Off_Automatic() { yield return RunCase(GrdPipelineKind.Hdrp, 48, false, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator HDRP_48_On_Automatic() { yield return RunCase(GrdPipelineKind.Hdrp, 48, true, GrdLoopPhase.Automatic); }
        [UnityTest] public IEnumerator URP_ValidationStackReuse() { yield return RunStackReuseCase(GrdPipelineKind.Urp); }
        [UnityTest] public IEnumerator HDRP_ValidationStackReuse() { yield return RunStackReuseCase(GrdPipelineKind.Hdrp); }
        [UnityTest] public IEnumerator URP_ValidationStackMotionBlurToggle() { yield return RunMotionBlurToggleCase(GrdPipelineKind.Urp); }
        [UnityTest] public IEnumerator HDRP_ValidationStackMotionBlurToggle() { yield return RunMotionBlurToggleCase(GrdPipelineKind.Hdrp); }

        IEnumerator RunStackReuseCase(GrdPipelineKind pipelineKind)
        {
            var state = new FixtureState(pipelineKind, 8, false, GrdLoopPhase.BeforeFinish);
            try
            {
                state.Setup();
                yield return null;
                yield return null;
                state.InitializeAfterPipelineWarmup();
                state.ValidateStackReuse();
            }
            finally { state.Dispose(); }
        }

        IEnumerator RunMotionBlurToggleCase(GrdPipelineKind pipelineKind)
        {
            var state = new FixtureState(pipelineKind, 8, false, GrdLoopPhase.BeforeFinish);
            try
            {
                state.Setup();
                yield return null;
                yield return null;
                state.InitializeAfterPipelineWarmup();
                state.ValidateMotionBlurToggle();
            }
            finally { state.Dispose(); }
        }
        [UnityTest] public IEnumerator URP_ReleaseActiveTexture() { yield return RunReleaseActiveTextureCase(); }
        [UnityTest] public IEnumerator URP_ReleaseActiveTextureOnResize() { yield return RunReleaseActiveTextureResizeCase(); }
        [UnityTest] public IEnumerator URP_ReleaseExternalActiveTexture() { yield return RunReleaseExternalActiveTextureCase(); }

        IEnumerator RunReleaseActiveTextureCase()
        {
            var state = new FixtureState(GrdPipelineKind.Urp, 1, false, GrdLoopPhase.BeforeFinish);
            try
            {
                state.Setup();
                yield return null;
                yield return null;
                state.InitializeAfterPipelineWarmup();
                yield return state.ValidateReleaseActiveTexture();
            }
            finally
            {
                state.Dispose();
            }
        }

        IEnumerator RunReleaseActiveTextureResizeCase()
        {
            var state = new FixtureState(GrdPipelineKind.Urp, 1, false, GrdLoopPhase.BeforeFinish);
            try
            {
                state.Setup();
                yield return null;
                yield return null;
                state.InitializeAfterPipelineWarmup();
                yield return state.ValidateReleaseActiveTextureOnResize();
            }
            finally
            {
                state.Dispose();
            }
        }

        IEnumerator RunReleaseExternalActiveTextureCase()
        {
            var state = new FixtureState(GrdPipelineKind.Urp, 1, false, GrdLoopPhase.BeforeFinish);
            try
            {
                state.Setup();
                yield return null;
                yield return null;
                state.InitializeAfterPipelineWarmup();
                yield return state.ValidateReleaseExternalActiveTexture();
            }
            finally
            {
                state.Dispose();
            }
        }

        IEnumerator RunCase(GrdPipelineKind pipelineKind, int views, bool grdEnabled, GrdLoopPhase phase)
        {
            var state = new FixtureState(pipelineKind, views, grdEnabled, phase);
            try
            {
                state.Setup();
                yield return null;
                yield return null;
                state.InitializeAfterPipelineWarmup();
                for (var i = 0; i < 10; ++i) yield return null;
                state.CaptureBeforeDynamicMove();
                state.MoveDynamicTarget();
                yield return null;
                state.AssertResult();
                if (phase == GrdLoopPhase.Automatic) yield return state.ValidateAutomaticLifecycle();
            }
            finally
            {
                state.Dispose();
            }
        }

        sealed class FixtureState
        {
            readonly GrdPipelineKind pipelineKind;
            readonly int views;
            readonly bool grdEnabled;
            readonly GrdLoopPhase phase;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            readonly List<Renderer> renderers = new List<Renderer>();
            readonly GrdFinishFrameFixture loopFixture = new GrdFinishFrameFixture();
            RenderPipelineAsset oldPipeline;
            RenderPipelineAsset oldQualityPipeline;
            RenderPipelineAsset pipeline;
            UniversalRendererData urpRenderer;
            VolumeProfile hdrpVolumeProfile;
            Camera camera;
            QuiltRenderer quilt;
            Material material;
            Material sphereMaterial;
            Mesh sharedCubeMesh;
            ProfilerRecorder rendererRecorder;
            ProfilerRecorder visibleRecorder;
            bool profilerWasEnabled;
            bool categoryWasEnabled;
            bool editorKeepAllSet;
#if UNITY_EDITOR
            BatchRendererGroupStrippingMode oldBatchRendererGroupStrippingMode;
            SerializedObject graphicsSettingsObject;
            SerializedProperty brgStrippingProperty;
#endif
            bool hadException;
            string exceptionText;
            GameObject dynamicTarget;
            GrdFrameOutput frameOutput;
            Color32[] beforeDynamicMove;
            Vector2[] beforeTileCentroids;
            int mutationFrame = -1;

            public FixtureState(GrdPipelineKind pipelineKind, int views, bool grdEnabled, GrdLoopPhase phase)
            {
                this.pipelineKind = pipelineKind;
                this.views = views;
                this.grdEnabled = grdEnabled;
                this.phase = phase;
            }

            public void Setup()
            {
                oldPipeline = GraphicsSettings.defaultRenderPipeline;
                oldQualityPipeline = QualitySettings.renderPipeline;
                SetupEditorBatchRendererGroupPolicy();
                if (pipelineKind == GrdPipelineKind.Urp)
                {
                    urpRenderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                    urpRenderer.renderingMode = RenderingMode.ForwardPlus;
                    pipeline = UniversalRenderPipelineAsset.Create(urpRenderer);
                    ((UniversalRenderPipelineAsset)pipeline).gpuResidentDrawerMode = grdEnabled ? GPUResidentDrawerMode.InstancedDrawing : GPUResidentDrawerMode.Disabled;
                    ((UniversalRenderPipelineAsset)pipeline).useSRPBatcher = true;
                    ((UniversalRenderPipelineAsset)pipeline).gpuResidentDrawerEnableOcclusionCullingInCameras = false;
                    ((UniversalRenderPipelineAsset)pipeline).smallMeshScreenPercentage = 0f;
                }
                else
                {
                    pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                    var hdrp = (HDRenderPipelineAsset)pipeline;
                    hdrp.gpuResidentDrawerMode = grdEnabled ? GPUResidentDrawerMode.InstancedDrawing : GPUResidentDrawerMode.Disabled;
                    var hdrpSettings = hdrp.currentPlatformRenderPipelineSettings;
                    hdrpSettings.gpuResidentDrawerSettings.enableOcclusionCullingInCameras = false;
                    hdrpSettings.gpuResidentDrawerSettings.smallMeshScreenPercentage = 0f;
                    hdrp.currentPlatformRenderPipelineSettings = hdrpSettings;
                }
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                SetupScene();
                profilerWasEnabled = Profiler.enabled;
                categoryWasEnabled = Profiler.IsCategoryEnabled(new ProfilerCategory("GPU Resident Drawer"));
                Profiler.enabled = true;
                Profiler.SetCategoryEnabled(new ProfilerCategory("GPU Resident Drawer"), true);
            }

            public void InitializeAfterPipelineWarmup()
            {
                quilt.RenderQuilt();
                if (grdEnabled)
                {
                    rendererRecorder = ProfilerRecorder.StartNew(new ProfilerCategory("GPU Resident Drawer"), "GRD Renderers", 15);
                    visibleRecorder = ProfilerRecorder.StartNew(new ProfilerCategory("GPU Resident Drawer"), "Visible Instances", 15);
                }
                if (phase == GrdLoopPhase.Automatic) quilt.autoRender = true;
                else loopFixture.Install(quilt, phase, SetException);
            }

            public void ValidateStackReuse()
            {
                quilt.autoRender = false;
                quilt.RenderQuilt();
                var urpGuard = quilt.GuardBehaviour as LookingGlass.Community.URP.UrpQuiltCamera;
                var hdrpGuard = quilt.GuardBehaviour as LookingGlass.Community.HDRP.HdrpQuiltCamera;
                var first = urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount;
                quilt.RenderQuilt();
                var second = urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount;
                Assert.That(second, Is.EqualTo(first));
                quilt.GuardBehaviour.enabled = false;
                quilt.GuardBehaviour.enabled = true;
                quilt.RenderQuilt();
                var third = urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount;
                Assert.That(third, Is.EqualTo(first + 1));
            }

            public void ValidateMotionBlurToggle()
            {
                quilt.autoRender = false;
                quilt.RenderQuilt();
                var urpGuard = quilt.GuardBehaviour as LookingGlass.Community.URP.UrpQuiltCamera;
                var hdrpGuard = quilt.GuardBehaviour as LookingGlass.Community.HDRP.HdrpQuiltCamera;
                var first = urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount;
                var volumeObject = NewObject(new GameObject("GRD.Validation.MotionBlurVolume"));
                volumeObject.layer = 0;
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 100f;
                var profile = NewObject(ScriptableObject.CreateInstance<VolumeProfile>());
                volume.profile = profile;
                VolumeComponent motionBlur;
                if (urpGuard != null)
                {
                    var effect = profile.Add<UnityEngine.Rendering.Universal.MotionBlur>(true);
                    motionBlur = effect;
                    effect.active = false;
                    effect.intensity.overrideState = true;
                    effect.intensity.value = 0.5f;
                }
                else
                {
                    var effect = profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
                    motionBlur = effect;
                    effect.active = false;
                    effect.intensity.overrideState = true;
                    effect.intensity.value = 0.5f;
                }
                quilt.RenderQuilt();
                Assert.That(urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount, Is.EqualTo(first));
                motionBlur.active = true;
                Assert.Throws<NotSupportedException>(() => quilt.RenderQuilt());
                Assert.That(urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount, Is.EqualTo(first));
                motionBlur.active = false;
                quilt.RenderQuilt();
                Assert.That(urpGuard != null ? urpGuard.ValidationStackCreationCount : hdrpGuard.ValidationStackCreationCount, Is.EqualTo(first));
            }

            void SetupEditorBatchRendererGroupPolicy()
            {
#if UNITY_EDITOR
                var graphicsAsset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
                if (graphicsAsset == null || graphicsAsset.Length == 0) throw new InvalidOperationException("GraphicsSettings.asset is unavailable");
                graphicsSettingsObject = new SerializedObject(graphicsAsset[0]);
                brgStrippingProperty = graphicsSettingsObject.FindProperty("m_BrgStripping");
                if (brgStrippingProperty == null) throw new InvalidOperationException("GraphicsSettings.m_BrgStripping is unavailable");
                oldBatchRendererGroupStrippingMode = (BatchRendererGroupStrippingMode)brgStrippingProperty.intValue;
                editorKeepAllSet = oldBatchRendererGroupStrippingMode != BatchRendererGroupStrippingMode.KeepAll;
                if (editorKeepAllSet)
                {
                    brgStrippingProperty.intValue = (int)BatchRendererGroupStrippingMode.KeepAll;
                    graphicsSettingsObject.ApplyModifiedPropertiesWithoutUndo();
                }
                if (EditorGraphicsSettings.batchRendererGroupShaderStrippingMode != BatchRendererGroupStrippingMode.KeepAll)
                    throw new InvalidOperationException("BRG stripping mode did not become KeepAll");
#endif
            }

            void SetupScene()
            {
                var root = NewObject(new GameObject("GRD.Validation.Root"));
                var lightObject = NewObject(new GameObject("GRD.Validation.DirectionalLight"));
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 2f;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                if (pipelineKind == GrdPipelineKind.Hdrp)
                {
                    lightObject.AddComponent<HDAdditionalLightData>();
                    light.intensity = 2f;
                }
                var cameraObject = NewObject(new GameObject("GRD.Validation.Camera"));
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.useOcclusionCulling = false;
                camera.transform.SetPositionAndRotation(new Vector3(0f, 1.1f, -6f), Quaternion.Euler(0f, 0f, 0f));
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                if (pipelineKind == GrdPipelineKind.Urp)
                {
                    var data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                    data.renderType = CameraRenderType.Base;
                    data.renderPostProcessing = false;
                }
                else
                {
                    var data = cameraObject.AddComponent<HDAdditionalCameraData>();
                    data.volumeLayerMask = 1;
                    data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    data.backgroundColorHDR = Color.black;
                    SetupHdrpValidationVolume();
                }
                material = NewObject(CreateMaterial());
                sphereMaterial = NewObject(CreateMaterial(Color.green, "GRD.Validation.SphereMaterial"));
                CreateGeometry(root.transform);
                var quiltObject = cameraObject;
                MonoBehaviour guard;
                if (pipelineKind == GrdPipelineKind.Urp)
                    guard = quiltObject.AddComponent<LookingGlass.Community.URP.UrpQuiltCamera>();
                else
                    guard = quiltObject.AddComponent<LookingGlass.Community.HDRP.HdrpQuiltCamera>();
                quilt = quiltObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = camera;
                quilt.GuardBehaviour = guard;
                frameOutput = quiltObject.AddComponent<GrdFrameOutput>();
                quilt.OutputBehaviour = frameOutput;
                quilt.settings.columns = views == 1 ? 1 : (views == 8 ? 4 : 8);
                quilt.settings.rows = views == 1 ? 1 : (views == 8 ? 2 : 6);
                quilt.settings.quiltWidth = TileWidth * quilt.settings.columns;
                quilt.settings.quiltHeight = TileHeight * quilt.settings.rows;
                quilt.settings.views = views;
                quilt.settings.viewAspect = 4f / 3f;
                quilt.settings.cameraDistance = 4f;
            }

            void SetupHdrpValidationVolume()
            {
                var volumeObject = NewObject(new GameObject("GRD.Validation.HDRP.Volume"));
                volumeObject.layer = 0;
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
                hdrpVolumeProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                volume.profile = hdrpVolumeProfile;
                var exposure = hdrpVolumeProfile.Add<Exposure>(true);
                exposure.mode.value = ExposureMode.Fixed;
                exposure.mode.overrideState = true;
                exposure.fixedExposure.value = 4f;
                exposure.fixedExposure.overrideState = true;
                var visualEnvironment = hdrpVolumeProfile.Add<VisualEnvironment>(true);
                visualEnvironment.skyType.value = 0;
                visualEnvironment.skyType.overrideState = true;
                visualEnvironment.cloudType.value = 0;
                visualEnvironment.cloudType.overrideState = true;
                var indirectLighting = hdrpVolumeProfile.Add<IndirectLightingController>(true);
                indirectLighting.indirectDiffuseLightingMultiplier.value = 0f;
                indirectLighting.indirectDiffuseLightingMultiplier.overrideState = true;
                var bloom = hdrpVolumeProfile.Add<UnityEngine.Rendering.HighDefinition.Bloom>(true);
                bloom.intensity.value = 0f;
                bloom.intensity.overrideState = true;
                var motionBlur = hdrpVolumeProfile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
                motionBlur.active = true;
                motionBlur.intensity.value = 0f;
                var reflection = hdrpVolumeProfile.Add<ScreenSpaceReflection>(true);
                reflection.enabled.value = false;
                reflection.enabled.overrideState = true;
                var gi = hdrpVolumeProfile.Add<GlobalIllumination>(true);
                gi.enable.value = false;
                gi.enable.overrideState = true;
                var clouds = hdrpVolumeProfile.Add<VolumetricClouds>(true);
                clouds.enable.value = false;
                clouds.enable.overrideState = true;
                var fog = hdrpVolumeProfile.Add<Fog>(true);
                fog.enabled.value = false;
                fog.enabled.overrideState = true;
                fog.enableVolumetricFog.value = false;
                fog.enableVolumetricFog.overrideState = true;
            }

            void CreateGeometry(Transform parent)
            {
                var template = GameObject.CreatePrimitive(PrimitiveType.Cube);
                template.name = "GRD.Validation.CubeTemplate";
                template.transform.SetParent(parent, false);
                template.GetComponent<Renderer>().sharedMaterial = material;
                sharedCubeMesh = template.GetComponent<MeshFilter>().sharedMesh;
                renderers.Add(template.GetComponent<Renderer>());
                owned.Add(template);
                for (var i = 0; i < 72; ++i)
                {
                    var item = new GameObject("GRD.Validation.Cube." + i);
                    item.transform.SetParent(parent, false);
                    item.transform.localPosition = new Vector3((i % 12 - 5.5f) * 0.32f, (i / 12 - 2.5f) * 0.32f, 1.2f + (i % 3) * 0.1f);
                    item.transform.localScale = Vector3.one * 0.22f;
                    var filter = item.AddComponent<MeshFilter>();
                    filter.sharedMesh = sharedCubeMesh;
                    var renderer = item.AddComponent<MeshRenderer>();
                    renderer.sharedMaterial = material;
                    renderers.Add(renderer);
                    owned.Add(item);
                }
                var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "GRD.Validation.Sphere";
                sphere.transform.SetParent(parent, false);
                sphere.transform.localPosition = new Vector3(-0.6f, 0.5f, -0.5f);
                sphere.GetComponent<Renderer>().sharedMaterial = sphereMaterial;
                renderers.Add(sphere.GetComponent<Renderer>());
                owned.Add(sphere);
                dynamicTarget = sphere;
                var plane = GameObject.CreatePrimitive(PrimitiveType.Plane);
                plane.name = "GRD.Validation.Plane";
                plane.transform.SetParent(parent, false);
                plane.transform.localPosition = new Vector3(0f, -1f, 1f);
                plane.transform.localScale = Vector3.one * 3f;
                plane.GetComponent<Renderer>().sharedMaterial = material;
                renderers.Add(plane.GetComponent<Renderer>());
                owned.Add(plane);
            }

            Material CreateMaterial() { return CreateMaterial(new Color(0.8f, 0.05f, 0.05f, 1f), "GRD.Validation.SharedMaterial"); }

            Material CreateMaterial(Color color, string materialName)
            {
                var shaderName = pipelineKind == GrdPipelineKind.Urp ? "Universal Render Pipeline/Lit" : "HDRP/Lit";
                var shader = Shader.Find(shaderName);
                if (shader == null) throw new InvalidOperationException("shader unavailable: " + shaderName);
                var result = new Material(shader) { name = materialName };
                result.SetColor("_BaseColor", color);
                result.SetFloat("_Smoothness", 0.25f);
                if (pipelineKind == GrdPipelineKind.Hdrp)
                {
                    if (result.HasProperty("_EmissiveColor")) result.SetColor("_EmissiveColor", Color.black);
                    if (result.HasProperty("_EmissiveIntensity")) result.SetFloat("_EmissiveIntensity", 0f);
                }
                return result;
            }

            public void RenderThroughLateUpdateFixture()
            {
                if (!grdEnabled)
                    quilt.RenderQuilt();
            }

            public void CaptureBeforeDynamicMove()
            {
                if (quilt != null && quilt.QuiltTexture != null)
                {
                    beforeDynamicMove = Capture(quilt.QuiltTexture);
                    beforeTileCentroids = TileCentroids(beforeDynamicMove, quilt.QuiltTexture.width, quilt.settings.GetLayout());
                }
            }

            public void MoveDynamicTarget()
            {
                if (dynamicTarget != null)
                {
                    dynamicTarget.transform.localPosition += new Vector3(1.2f, 0f, 0f);
                    mutationFrame = Time.frameCount;
                }
            }

            public IEnumerator ValidateAutomaticLifecycle()
            {
                var initial = frameOutput.SubmissionCount;
                var initialFrame = Time.frameCount;
                var initialRenderedFrame = frameOutput.LastRenderedFrame;
                var disabledCount = initial;
                var disabledFrame = -1;
                var disabledRenderedFrame = -1;
                var enabledCount = initial;
                var enabledFrame = -1;
                var enabledRenderedFrame = -1;
                SaveAutomaticLifecycleEvidence(initial, initialFrame, initialRenderedFrame, disabledCount, disabledFrame, disabledRenderedFrame, enabledCount, enabledFrame, enabledRenderedFrame, "initial");
                quilt.enabled = false;
                yield return null;
                disabledCount = frameOutput.SubmissionCount;
                disabledFrame = Time.frameCount;
                disabledRenderedFrame = frameOutput.LastRenderedFrame;
                SaveAutomaticLifecycleEvidence(initial, initialFrame, initialRenderedFrame, disabledCount, disabledFrame, disabledRenderedFrame, enabledCount, enabledFrame, enabledRenderedFrame, "disabled");
                if (disabledCount != initial) throw new AssertionException("disabled automatic renderer submitted a frame; initialCount=" + initial + ", count=" + disabledCount + ", Time.frameCount=" + disabledFrame + ", LastRenderedFrame=" + disabledRenderedFrame);
                quilt.enabled = true;
                yield return null;
                enabledCount = frameOutput.SubmissionCount;
                enabledFrame = Time.frameCount;
                enabledRenderedFrame = frameOutput.LastRenderedFrame;
                SaveAutomaticLifecycleEvidence(initial, initialFrame, initialRenderedFrame, disabledCount, disabledFrame, disabledRenderedFrame, enabledCount, enabledFrame, enabledRenderedFrame, "enabled");
                if (enabledCount != initial + 1) throw new AssertionException("enabled automatic renderer did not submit exactly one frame; initialCount=" + initial + ", count=" + enabledCount + ", Time.frameCount=" + enabledFrame + ", LastRenderedFrame=" + enabledRenderedFrame);
                quilt.autoRender = false;
                yield return null;
                var disabledAutoRenderCount = frameOutput.SubmissionCount;
                var disabledAutoRenderFrame = Time.frameCount;
                var disabledAutoRenderLastRenderedFrame = frameOutput.LastRenderedFrame;
                SaveAutomaticLifecycleEvidence(initial, initialFrame, initialRenderedFrame, disabledCount, disabledFrame, disabledRenderedFrame, enabledCount, enabledFrame, enabledRenderedFrame, "autoRender-false");
                if (disabledAutoRenderCount != initial + 1) throw new AssertionException("autoRender=false still submitted a frame; initialCount=" + initial + ", count=" + disabledAutoRenderCount + ", Time.frameCount=" + disabledAutoRenderFrame + ", LastRenderedFrame=" + disabledAutoRenderLastRenderedFrame);
            }

            public IEnumerator ValidateReleaseActiveTexture()
            {
                quilt.RenderQuilt();
                RenderTexture.active = quilt.QuiltTexture;
                quilt.enabled = false;
                if (RenderTexture.active != null) throw new AssertionException("owned active quilt texture was not unbound on disable");
                yield break;
            }

            public IEnumerator ValidateReleaseActiveTextureOnResize()
            {
                quilt.RenderQuilt();
                RenderTexture.active = quilt.QuiltTexture;
                quilt.settings.quiltWidth += TileWidth;
                quilt.RenderQuilt();
                if (RenderTexture.active != null) throw new AssertionException("owned active quilt texture was restored after resize");
                yield break;
            }

            public IEnumerator ValidateReleaseExternalActiveTexture()
            {
                quilt.RenderQuilt();
                var external = new RenderTexture(32, 32, 0, RenderTextureFormat.ARGB32);
                external.Create();
                try
                {
                    RenderTexture.active = external;
                    quilt.enabled = false;
                    if (RenderTexture.active != external) throw new AssertionException("external active texture was changed on disable");
                }
                finally
                {
                    if (RenderTexture.active == external) RenderTexture.active = null;
                    external.Release();
                    UnityEngine.Object.DestroyImmediate(external);
                }
                yield break;
            }

            void SaveAutomaticLifecycleEvidence(int initialCount, int initialFrame, int initialRenderedFrame, int disabledCount, int disabledFrame, int disabledRenderedFrame, int enabledCount, int enabledFrame, int enabledRenderedFrame, string stage)
            {
                Directory.CreateDirectory(ResultDirectory);
                var name = pipelineKind.ToString().ToLowerInvariant() + "-" + phase.ToString().ToLowerInvariant() + "-grd-" + grdEnabled + "-views-" + views + "-lifecycle.json";
                var count = frameOutput.SubmissionCount;
                var frame = Time.frameCount;
                var lastRenderedFrame = frameOutput.LastRenderedFrame;
                var json = "{\"pipeline\":\"" + pipelineKind + "\",\"phase\":\"" + phase + "\",\"stage\":\"" + stage + "\",\"views\":" + views + ",\"grdEnabled\":" + grdEnabled.ToString().ToLowerInvariant() + ",\"initialCount\":" + initialCount + ",\"count\":" + count + ",\"Time.frameCount\":" + frame + ",\"frameOutput.LastRenderedFrame\":" + lastRenderedFrame + ",\"initialFrame\":" + initialFrame + ",\"initialLastRenderedFrame\":" + initialRenderedFrame + ",\"disabledCount\":" + disabledCount + ",\"disabledFrame\":" + disabledFrame + ",\"disabledLastRenderedFrame\":" + disabledRenderedFrame + ",\"enabledCount\":" + enabledCount + ",\"enabledFrame\":" + enabledFrame + ",\"enabledLastRenderedFrame\":" + enabledRenderedFrame + "}";
                File.WriteAllText(Path.Combine(ResultDirectory, name), json);
            }

            public void AssertResult()
            {
                if (hadException)
                {
                    if (quilt != null && quilt.QuiltTexture != null)
                    {
                        var failedImage = Capture(quilt.QuiltTexture);
                        SaveEvidence(failedImage, quilt.QuiltTexture.width, quilt.QuiltTexture.height, quilt.settings.GetLayout());
                    }
                    throw new AssertionException(exceptionText);
                }
                if (quilt == null || quilt.QuiltTexture == null) throw new AssertionException("quilt texture was not produced");
                var image = Capture(quilt.QuiltTexture);
                var layout = quilt.settings.GetLayout();
                SaveEvidence(image, quilt.QuiltTexture.width, quilt.QuiltTexture.height, layout);
                if (frameOutput == null || frameOutput.LastRenderedFrame != mutationFrame)
                    throw new AssertionException("render frame did not match mutation frame");
                var nonBlack = 0;
                for (var i = 0; i < image.Length; ++i)
                    if (image[i].a > 0 && (image[i].r > 2 || image[i].g > 2 || image[i].b > 2)) nonBlack++;
                if (nonBlack < 32) throw new AssertionException("quilt contains no visible geometry");
                if (views > 1 && !ViewsDiffer(image, quilt.QuiltTexture.width, layout))
                    throw new AssertionException("quilt views are identical");
                if (beforeDynamicMove != null && !PixelsDiffer(beforeDynamicMove, image))
                    throw new AssertionException("dynamic target movement produced no frame change");
                if (beforeTileCentroids != null && !AllTileCentroidsMoved(beforeTileCentroids, TileCentroids(image, quilt.QuiltTexture.width, layout)))
                    throw new AssertionException("dynamic target movement did not move visible geometry in every tile");
                if (grdEnabled)
                {
                    if (!rendererRecorder.Valid || !visibleRecorder.Valid || rendererRecorder.LastValue <= 0 || visibleRecorder.LastValue <= 0)
                        throw new AssertionException("GPU Resident Drawer counters did not report active renderers");
                }
            }

            static bool ViewsDiffer(Color32[] image, int width, QuiltLayout layout)
            {
                var first = layout.GetTile(0);
                var last = layout.GetTile(layout.ViewCount - 1);
                var differences = 0;
                for (var y = 0; y < Mathf.Min(first.height, last.height); ++y)
                    for (var x = 0; x < Mathf.Min(first.width, last.width); ++x)
                    {
                        var a = image[(first.y + y) * width + first.x + x];
                        var b = image[(last.y + y) * width + last.x + x];
                        if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 6) differences++;
                    }
                return differences >= 8;
            }

            static bool PixelsDiffer(Color32[] a, Color32[] b)
            {
                if (a == null || b == null || a.Length != b.Length) return true;
                for (var i = 0; i < a.Length; ++i)
                    if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b) return true;
                return false;
            }

            static Vector2[] TileCentroids(Color32[] image, int width, QuiltLayout layout)
            {
                var result = new Vector2[layout.ViewCount];
                for (var i = 0; i < layout.ViewCount; ++i)
                {
                    var tile = layout.GetTile(i);
                    var sum = Vector2.zero;
                    var count = 0;
                    for (var y = tile.y; y < tile.yMax; ++y)
                        for (var x = tile.x; x < tile.xMax; ++x)
                        {
                            var p = image[y * width + x];
                            if (p.a > 0 && p.g > 15 && p.g > p.r * 1.5f && p.g > p.b * 1.5f) { sum += new Vector2(x - tile.x, y - tile.y); count++; }
                        }
                    result[i] = count == 0 ? new Vector2(-1f, -1f) : sum / count;
                }
                return result;
            }

            static bool AllTileCentroidsMoved(Vector2[] before, Vector2[] after)
            {
                if (before.Length != after.Length) return false;
                for (var i = 0; i < before.Length; ++i)
                    if (before[i].x < 0f || after[i].x < 0f || after[i].x - before[i].x <= 1f) return false;
                return true;
            }

            static Color32[] Capture(RenderTexture source)
            {
                var old = RenderTexture.active;
                var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = source;
                    image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    image.Apply(false, false);
                    return image.GetPixels32();
                }
                finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
            }

            void SaveEvidence(Color32[] image, int width, int height, QuiltLayout layout)
            {
                Directory.CreateDirectory(ResultDirectory);
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.SetPixels32(image);
                texture.Apply(false, false);
                var name = pipelineKind.ToString().ToLowerInvariant() + "-" + phase.ToString().ToLowerInvariant() + "-grd-" + grdEnabled + "-views-" + views;
                File.WriteAllBytes(Path.Combine(ResultDirectory, name + ".png"), texture.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(texture);
                if (beforeDynamicMove != null)
                {
                    var beforeTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    beforeTexture.SetPixels32(beforeDynamicMove);
                    beforeTexture.Apply(false, false);
                    File.WriteAllBytes(Path.Combine(ResultDirectory, name + "-before.png"), beforeTexture.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(beforeTexture);
                }
                var grdRenderers = rendererRecorder.Valid ? rendererRecorder.LastValue : 0;
                var visibleInstances = visibleRecorder.Valid ? visibleRecorder.LastValue : 0;
                var before = CentroidsJson(beforeTileCentroids);
                var after = CentroidsJson(TileCentroids(image, width, layout));
                var renderFrame = frameOutput == null ? -1 : frameOutput.LastRenderedFrame;
                var json = "{\"pipeline\":\"" + pipelineKind + "\",\"phase\":\"" + phase + "\",\"grdEnabled\":" + grdEnabled.ToString().ToLowerInvariant() + ",\"views\":" + views + ",\"mutationFrame\":" + mutationFrame + ",\"renderFrame\":" + renderFrame + ",\"beforeTileCentroids\":" + before + ",\"afterTileCentroids\":" + after + ",\"width\":" + width + ",\"height\":" + height + ",\"tileCount\":" + layout.ViewCount + ",\"grdRenderers\":" + grdRenderers + ",\"visibleInstances\":" + visibleInstances + ",\"graphicsDeviceType\":\"" + SystemInfo.graphicsDeviceType + "\",\"graphicsDeviceName\":\"" + EscapeJson(SystemInfo.graphicsDeviceName) + "\",\"graphicsMultiThreaded\":" + SystemInfo.graphicsMultiThreaded.ToString().ToLowerInvariant() + ",\"unityVersion\":\"" + Application.unityVersion + "\",\"isEditor\":" + Application.isEditor.ToString().ToLowerInvariant() + "}";
                File.WriteAllText(Path.Combine(ResultDirectory, name + ".json"), json);
            }

            static string CentroidsJson(Vector2[] values)
            {
                if (values == null) return "[]";
                var result = "[";
                for (var i = 0; i < values.Length; ++i) result += (i == 0 ? "" : ",") + "[" + values[i].x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "," + values[i].y.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                return result + "]";
            }

            static string EscapeJson(string value) { return value.Replace("\\", "\\\\").Replace("\"", "\\\""); }

            void SetException(Exception error) { hadException = true; exceptionText = error.ToString(); }

            T NewObject<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }

            public void Dispose()
            {
                loopFixture.Uninstall();
                rendererRecorder.Dispose();
                visibleRecorder.Dispose();
                Profiler.SetCategoryEnabled(new ProfilerCategory("GPU Resident Drawer"), categoryWasEnabled);
                Profiler.enabled = profilerWasEnabled;
#if UNITY_EDITOR
                if (editorKeepAllSet && brgStrippingProperty != null)
                {
                    brgStrippingProperty.intValue = (int)oldBatchRendererGroupStrippingMode;
                    graphicsSettingsObject.ApplyModifiedPropertiesWithoutUndo();
                }
#endif
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQualityPipeline;
                if (hdrpVolumeProfile != null) UnityEngine.Object.DestroyImmediate(hdrpVolumeProfile);
                foreach (var value in owned) if (value != null) UnityEngine.Object.DestroyImmediate(value);
                if (pipeline != null) UnityEngine.Object.DestroyImmediate(pipeline);
                if (urpRenderer != null) UnityEngine.Object.DestroyImmediate(urpRenderer);
                owned.Clear();
            }
        }

        sealed class GrdFrameOutput : MonoBehaviour, IQuiltOutput
        {
            public RenderTexture QuiltTexture { get; private set; }
            public QuiltLayout Layout { get; private set; }
            public int LastRenderedFrame { get; private set; } = -1;
            public int SubmissionCount { get; private set; }
            public void Submit(RenderTexture quiltTexture, QuiltLayout layout) { QuiltTexture = quiltTexture; Layout = layout; LastRenderedFrame = Time.frameCount; SubmissionCount++; }
        }

        sealed class GrdFinishFrameFixture
        {
            QuiltRenderer renderer;
            Action<Exception> onException;
            bool installed;

            public void Install(QuiltRenderer renderer, GrdLoopPhase phase, Action<Exception> onException)
            {
                this.renderer = renderer;
                this.onException = onException;
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                if (!InsertBeforeFinish(ref loop, phase)) throw new InvalidOperationException("PostLateUpdate.FinishFrameRendering was not found");
                PlayerLoop.SetPlayerLoop(loop);
                installed = true;
            }

            bool InsertBeforeFinish(ref PlayerLoopSystem root, GrdLoopPhase phase)
            {
                if (root.subSystemList == null) return false;
                var children = new List<PlayerLoopSystem>(root.subSystemList);
                for (var i = 0; i < children.Count; ++i)
                {
                    var child = children[i];
                    if (child.type == typeof(PostLateUpdate.FinishFrameRendering))
                    {
                        var callback = new PlayerLoopSystem { type = typeof(GrdFinishFrameFixture), updateDelegate = Invoke };
                        var list = new List<PlayerLoopSystem>(root.subSystemList);
                        list.Insert(phase == GrdLoopPhase.BeforeFinish ? i : i + 1, callback);
                        root.subSystemList = list.ToArray();
                        return true;
                    }
                    if (InsertBeforeFinish(ref child, phase)) { children[i] = child; root.subSystemList = children.ToArray(); return true; }
                }
                return false;
            }

            void Invoke()
            {
                if (renderer == null || !renderer.isActiveAndEnabled || renderer.IsRendering) return;
                try { renderer.RenderQuilt(); }
                catch (Exception error) { onException?.Invoke(error); }
            }

            public void Uninstall()
            {
                if (!installed) return;
                var loop = PlayerLoop.GetCurrentPlayerLoop();
                RemoveInsertedNode(ref loop);
                PlayerLoop.SetPlayerLoop(loop);
                renderer = null;
                onException = null;
                installed = false;
            }

            bool RemoveInsertedNode(ref PlayerLoopSystem root)
            {
                var list = root.subSystemList;
                if (list == null) return false;
                var changed = false;
                var kept = new List<PlayerLoopSystem>(list.Length);
                foreach (var child in list)
                {
                    if (child.updateDelegate != null && child.updateDelegate.Target == this)
                    {
                        changed = true;
                        continue;
                    }
                    var copy = child;
                    if (RemoveInsertedNode(ref copy)) changed = true;
                    kept.Add(copy);
                }
                if (changed) root.subSystemList = kept.ToArray();
                return changed;
            }
        }
    }
}
