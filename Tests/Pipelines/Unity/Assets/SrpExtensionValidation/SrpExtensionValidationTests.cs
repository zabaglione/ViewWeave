using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using LookingGlass.Community;
using LookingGlass.Community.URP;
using LookingGlass.Community.HDRP;
using HD = UnityEngine.Rendering.HighDefinition;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace LookingGlass.SrpExtensionValidation
{
    public sealed class SrpExtensionValidationTests
    {
        [UnityTest]
        public IEnumerator UrpRenderGraphPassRunsForEveryQuiltView()
        {
            UrpFixture fixture = null;
            try
            {
                fixture = new UrpFixture();
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.Capture(false);
                fixture.Capture(true);
                fixture.AssertResult("urp-render-graph");
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator HdrpCustomPassRunsForEveryQuiltView()
        {
            HdrpFixture fixture = null;
            try
            {
                fixture = new HdrpFixture();
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.Capture(false);
                fixture.Capture(true);
                fixture.AssertResult("hdrp-custom-pass");
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        abstract class FixtureBase
        {
            protected const int TileWidth = 128;
            protected const int TileHeight = 96;
            protected const int Views = 8;
            protected readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
            protected GameObject Root;
            protected Camera Camera;
            protected QuiltRenderer Quilt;
            protected Texture2D Before;
            protected Texture2D After;
            protected RenderPipelineAsset PreviousDefault;
            protected RenderPipelineAsset PreviousQuality;
            protected Scene FixtureScene;
            protected Scene PreviousActiveScene;
            protected RenderTexture PreviousActiveTexture;
            readonly List<Behaviour> ExternalBehaviours = new List<Behaviour>();
            readonly List<bool> ExternalBehaviourStates = new List<bool>();
            Light PreviousSun;
            protected abstract string Name { get; }
            protected abstract void SetExtensionEnabled(bool enabled);
            protected abstract int ExecutionCount { get; }
            protected abstract void SetupPipelineAndExtension();

            public void Setup()
            {
                PreviousDefault = GraphicsSettings.defaultRenderPipeline;
                PreviousQuality = QualitySettings.renderPipeline;
                PreviousActiveScene = SceneManager.GetActiveScene();
                PreviousActiveTexture = RenderTexture.active;
                PreviousSun = RenderSettings.sun;
                SnapshotExternalObjects();
                FixtureScene = SceneManager.CreateScene("SrpExtensionValidation_" + Name);
                SceneManager.SetActiveScene(FixtureScene);
                Root = new GameObject("SrpExtensionFixture_" + Name);
                SceneManager.MoveGameObjectToScene(Root, FixtureScene);
                Root.layer = 31;
                Owned.Add(Root);
                SetupPipelineAndExtension();
                GameObject cameraObject = new GameObject("DedicatedQuiltCamera");
                cameraObject.transform.SetParent(Root.transform, false);
                Owned.Add(cameraObject);
                Camera = cameraObject.AddComponent<Camera>();
                Camera.enabled = false;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = Color.black;
                Camera.fieldOfView = 50f;
                Camera.nearClipPlane = 0.1f;
                Camera.farClipPlane = 100f;
                Camera.cullingMask = 1 << 31;
                Quilt = cameraObject.AddComponent<QuiltRenderer>();
                Quilt.sourceCamera = Camera;
                Quilt.settings.quiltWidth = TileWidth * 4;
                Quilt.settings.quiltHeight = TileHeight * 2;
                Quilt.settings.columns = 4;
                Quilt.settings.rows = 2;
                Quilt.settings.views = Views;
                Quilt.settings.viewAspect = TileWidth / (float)TileHeight;
                Quilt.settings.cameraDistance = 3f;
                Quilt.settings.fieldOfView = 50f;
                Quilt.settings.viewCone = 30f;
                AddLitControlObject();
                AddGuard(cameraObject);
                AddLight();
            }

            void AddGuard(GameObject cameraObject)
            {
                Component guard = AddPipelineGuard(cameraObject);
                Quilt.GuardBehaviour = guard as MonoBehaviour;
            }

            protected abstract Component AddPipelineGuard(GameObject cameraObject);

            void AddLitControlObject()
            {
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "LitControlCube";
                cube.transform.SetParent(Root.transform, false);
                cube.layer = 31;
                cube.transform.localPosition = new Vector3(0f, 0f, 3f);
                Owned.Add(cube);
                Shader shader = Shader.Find(LitShaderName);
                if (shader == null) Assert.Fail("Lit shader is unavailable: " + LitShaderName);
                Material material = new Material(shader);
                material.color = Color.white;
                cube.GetComponent<Renderer>().sharedMaterial = material;
                Owned.Add(material);
            }

            protected abstract string LitShaderName { get; }

            void AddLight()
            {
                GameObject lightObject = new GameObject("FixtureDirectionalLight");
                lightObject.transform.SetParent(Root.transform, false);
                lightObject.layer = 31;
                lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                Owned.Add(lightObject);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.2f;
                light.cullingMask = 1 << 31;
                if (lightObject.GetComponent<HD.HDAdditionalLightData>() == null)
                    lightObject.AddComponent<HD.HDAdditionalLightData>();
                RenderSettings.sun = light;
            }

            void SnapshotExternalObjects()
            {
                foreach (Camera camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    ExternalBehaviours.Add(camera);
                    ExternalBehaviourStates.Add(camera.enabled);
                    camera.enabled = false;
                }
                foreach (Light light in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    ExternalBehaviours.Add(light);
                    ExternalBehaviourStates.Add(light.enabled);
                    light.enabled = false;
                }
            }

            public void Capture(bool enabled)
            {
                SetExtensionEnabled(enabled);
                int start = ExecutionCount;
                Quilt.RenderQuilt();
                int executed = ExecutionCount - start;
                Texture2D image = CaptureTexture(Quilt.QuiltTexture);
                if (enabled) After = image; else Before = image;
                if (enabled && executed != Views)
                    Assert.Fail("extension execution count was " + executed + ", expected exactly " + Views);
                if (!enabled) AssertBaseVisible(image);
            }

            void AssertBaseVisible(Texture2D image)
            {
                Color[] pixels = image.GetPixels();
                QuiltLayout layout = Quilt.settings.GetLayout();
                for (int view = 0; view < Views; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    int visible = 0;
                    for (int y = tile.y; y < tile.yMax; ++y)
                    for (int x = tile.x; x < tile.xMax; ++x)
                    {
                        Color pixel = pixels[y * image.width + x];
                        if (pixel.r + pixel.g + pixel.b > 0.06f) visible++;
                    }
                    Assert.Greater(visible, 20, "control scene was blank in tile " + view);
                }
            }

            public void AssertResult(string prefix)
            {
                if (Before == null || After == null) Assert.Fail("extension captures are missing");
                Color[] before = Before.GetPixels();
                Color[] after = After.GetPixels();
                int[] tileChanges = new int[Views];
                QuiltLayout layout = Quilt.settings.GetLayout();
                for (int view = 0; view < Views; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    int changed = 0;
                    for (int y = tile.y; y < tile.yMax; ++y)
                    for (int x = tile.x; x < tile.xMax; ++x)
                    {
                        int index = y * Before.width + x;
                        if (ColorDistance(before[index], after[index]) > 0.01f) changed++;
                    }
                    tileChanges[view] = changed;
                    Assert.Greater(changed, 20, "extension did not change tile " + view);
                }
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "srp-extension-validation"));
                Directory.CreateDirectory(path);
                File.WriteAllBytes(Path.Combine(path, prefix + "-before.png"), Before.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(path, prefix + "-after.png"), After.EncodeToPNG());
                File.WriteAllText(Path.Combine(path, prefix + "-diagnostic.txt"), "views=" + Views + " executionCount=" + ExecutionCount + " tileChanges=" + string.Join(",", tileChanges) + "\n");
            }

            static float ColorDistance(Color a, Color b)
            {
                return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
            }

            static Texture2D CaptureTexture(RenderTexture source)
            {
                RenderTexture previous = RenderTexture.active;
                Texture2D result = null;
                try
                {
                    RenderTexture.active = source;
                    result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    result.Apply();
                    return result;
                }
                finally
                {
                    RenderTexture.active = previous;
                }
            }

            public virtual void Dispose()
            {
                for (int i = 0; i < ExternalBehaviours.Count; ++i)
                    if (ExternalBehaviours[i] != null) ExternalBehaviours[i].enabled = ExternalBehaviourStates[i];
                RenderSettings.sun = PreviousSun;
                GraphicsSettings.defaultRenderPipeline = PreviousDefault;
                QualitySettings.renderPipeline = PreviousQuality;
                RenderTexture.active = PreviousActiveTexture;
                if (Before != null) UnityEngine.Object.DestroyImmediate(Before);
                if (After != null) UnityEngine.Object.DestroyImmediate(After);
                for (int i = Owned.Count - 1; i >= 0; --i)
                    if (Owned[i] != null) UnityEngine.Object.DestroyImmediate(Owned[i]);
                if (PreviousActiveScene.IsValid() && PreviousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(PreviousActiveScene);
                if (FixtureScene.IsValid() && FixtureScene.isLoaded) SceneManager.UnloadSceneAsync(FixtureScene);
            }
        }

        sealed class UrpFixture : FixtureBase
        {
            UniversalRenderPipelineAsset pipeline;
            UniversalRendererData rendererData;
            SrpMarkerRendererFeature feature;
            protected override string Name => "URP";
            protected override string LitShaderName => "Universal Render Pipeline/Lit";
            protected override int ExecutionCount => SrpMarkerRendererFeature.ExecutionCount;

            protected override void SetupPipelineAndExtension()
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
#if UNITY_EDITOR
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
#endif
                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                feature = ScriptableObject.CreateInstance<SrpMarkerRendererFeature>();
                rendererData.rendererFeatures.Add(feature);
                feature.Create();
                rendererData.SetDirty();
                Owned.Add(feature);
                Owned.Add(rendererData);
                Owned.Add(pipeline);
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                SrpMarkerRendererFeature.ResetCounter();
            }

            protected override Component AddPipelineGuard(GameObject cameraObject)
            {
                UniversalAdditionalCameraData data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                data.renderType = CameraRenderType.Base;
                data.renderPostProcessing = false;
                data.volumeLayerMask = 1 << 31;
                return cameraObject.AddComponent<UrpQuiltCamera>();
            }

            protected override void SetExtensionEnabled(bool enabled)
            {
                feature.SetActive(enabled);
            }
        }

        sealed class HdrpFixture : FixtureBase
        {
            HD.HDRenderPipelineAsset pipeline;
            HD.CustomPassVolume volume;
            MarkerCustomPass pass;
            protected override string Name => "HDRP";
            protected override string LitShaderName => "HDRP/Lit";
            protected override int ExecutionCount => MarkerCustomPass.ExecutionCount;

            protected override void SetupPipelineAndExtension()
            {
                pipeline = ScriptableObject.CreateInstance<HD.HDRenderPipelineAsset>();
                HD.RenderPipelineSettings pipelineSettings = pipeline.currentPlatformRenderPipelineSettings;
                pipelineSettings.supportedLitShaderMode = HD.RenderPipelineSettings.SupportedLitShaderMode.ForwardOnly;
                pipeline.currentPlatformRenderPipelineSettings = pipelineSettings;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                Owned.Add(pipeline);
                AddHdrpValidationVolume();
                MarkerCustomPass.ResetCounter();
            }

            void AddHdrpValidationVolume()
            {
                GameObject volumeObject = new GameObject("HdrpValidationVolume");
                volumeObject.layer = 31;
                volumeObject.transform.SetParent(Root.transform, false);
                Owned.Add(volumeObject);
                Volume volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
                VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
                HD.Exposure exposure = profile.Add<HD.Exposure>(true);
                exposure.mode.value = HD.ExposureMode.Fixed;
                exposure.mode.overrideState = true;
                exposure.fixedExposure.value = 4f;
                exposure.fixedExposure.overrideState = true;
                HD.MotionBlur motionBlur = profile.Add<HD.MotionBlur>(true);
                motionBlur.active = true;
                motionBlur.intensity.value = 0f;
                motionBlur.intensity.overrideState = true;
                HD.ScreenSpaceReflection reflection = profile.Add<HD.ScreenSpaceReflection>(true);
                reflection.enabled.value = false;
                reflection.enabled.overrideState = true;
                HD.GlobalIllumination gi = profile.Add<HD.GlobalIllumination>(true);
                gi.active = true;
                gi.enable.value = false;
                gi.enable.overrideState = true;
                HD.Fog fog = profile.Add<HD.Fog>(true);
                fog.enabled.value = false;
                fog.enabled.overrideState = true;
                volume.sharedProfile = profile;
                Owned.Add(profile);
            }

            protected override Component AddPipelineGuard(GameObject cameraObject)
            {
                HD.HDAdditionalCameraData data = cameraObject.AddComponent<HD.HDAdditionalCameraData>();
                data.volumeLayerMask = 1 << 31;
                data.clearColorMode = HD.HDAdditionalCameraData.ClearColorMode.Color;
                data.backgroundColorHDR = Color.black;
                HdrpQuiltCamera guard = cameraObject.AddComponent<HdrpQuiltCamera>();
                GameObject volumeObject = new GameObject("HdrpCustomPassVolume");
                volumeObject.layer = 31;
                volumeObject.transform.SetParent(Root.transform, false);
                Owned.Add(volumeObject);
                volume = volumeObject.AddComponent<HD.CustomPassVolume>();
                volume.isGlobal = true;
                volume.priority = 100f;
                volume.targetCamera = cameraObject.GetComponent<Camera>();
                volume.injectionPoint = HD.CustomPassInjectionPoint.BeforeTransparent;
                pass = volume.AddPassOfType<MarkerCustomPass>();
                pass.targetColorBuffer = HD.CustomPass.TargetBuffer.Camera;
                pass.targetDepthBuffer = HD.CustomPass.TargetBuffer.None;
                return guard;
            }

            protected override void SetExtensionEnabled(bool enabled)
            {
                pass.enabled = enabled;
            }
        }

        sealed class SrpMarkerRendererFeature : ScriptableRendererFeature
        {
            public static int ExecutionCount { get; private set; }
            MarkerPass pass;

            public static void ResetCounter() { ExecutionCount = 0; }

            public override void Create()
            {
                pass = new MarkerPass();
                pass.renderPassEvent = RenderPassEvent.AfterRendering;
                pass.requiresIntermediateTexture = true;
            }

            public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
            {
                renderer.EnqueuePass(pass);
            }

            sealed class MarkerPass : ScriptableRenderPass
            {
                sealed class PassData
                {
                    public Color color;
                }

                public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
                {
                    UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                    using (var builder = renderGraph.AddRasterRenderPass<PassData>("ViewWeave SRP marker", out PassData data))
                    {
                        data.color = new Color(1f, 0.05f, 0.8f, 1f);
                        builder.SetRenderAttachment(resources.activeColorTexture, 0);
                        builder.AllowPassCulling(false);
                        builder.SetRenderFunc(static (PassData value, RasterGraphContext context) =>
                        {
                            ExecutionCount++;
                            context.cmd.ClearRenderTarget(RTClearFlags.Color, value.color, 1f, 0);
                        });
                    }
                }
            }
        }

        sealed class MarkerCustomPass : HD.CustomPass
        {
            public static int ExecutionCount { get; private set; }
            public static void ResetCounter() { ExecutionCount = 0; }

            protected override void Execute(HD.CustomPassContext context)
            {
                ExecutionCount++;
                CoreUtils.SetRenderTarget(context.cmd, context.cameraColorBuffer, ClearFlag.None);
                CoreUtils.ClearRenderTarget(context.cmd, ClearFlag.Color, new Color(0.05f, 1f, 0.8f, 1f));
            }
        }
    }
}
