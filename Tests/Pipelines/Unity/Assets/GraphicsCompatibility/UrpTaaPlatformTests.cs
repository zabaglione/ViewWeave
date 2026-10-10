#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave.URP;

namespace ViewWeave.GraphicsCompatibility
{
    public sealed class UrpTaaPlatformTests
    {
        const int TargetWidth = 192;
        const int TargetHeight = 128;

        [UnityTest]
        public IEnumerator TemporalAntiAliasingUsesProjectionJitterAndChangesMetalPixels()
        {
            RequireMetalEditor();
            Fixture fixture = null;
            Texture2D temporalImage = null;
            Texture2D controlImage = null;
            try
            {
                fixture = new Fixture();
                fixture.Setup();
                yield return null;
                yield return null;

                fixture.ResetObservations();
                for (int frame = 0; frame < 16; ++frame)
                    yield return null;

                fixture.AssertObservedTemporalJitter();
                temporalImage = fixture.Capture();
                AssertLitContent(temporalImage, "TAA camera");

                fixture.CameraData.antialiasing = AntialiasingMode.None;
                fixture.ResetObservations();
                for (int frame = 0; frame < 4; ++frame)
                    yield return null;

                fixture.AssertObservedStableProjection();
                controlImage = fixture.Capture();
                AssertLitContent(controlImage, "TAA-off control camera");
                AssertImagesDiffer(temporalImage, controlImage);
            }
            finally
            {
                if (temporalImage != null) Object.DestroyImmediate(temporalImage);
                if (controlImage != null) Object.DestroyImmediate(controlImage);
                fixture?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator QuiltGuardTemporarilyDisablesAndRestoresTemporalAntiAliasing()
        {
            RequireMetalEditor();
            Fixture fixture = null;
            try
            {
                fixture = new Fixture();
                fixture.Setup();
                yield return null;
                yield return null;

                fixture.CameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                using (fixture.Guard.PrepareForQuilt())
                {
                    Assert.AreEqual(AntialiasingMode.None, fixture.CameraData.antialiasing,
                        "The quilt render guard must disable temporal AA while a quilt view is rendered.");
                }

                Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.CameraData.antialiasing,
                    "The quilt render guard must restore the camera's temporal AA mode when the view scope ends.");
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        static void RequireMetalEditor()
        {
            if (Application.platform != RuntimePlatform.OSXEditor)
                Assert.Ignore("This platform test requires the macOS Unity Editor.");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                Assert.Ignore("This platform test requires the Metal graphics device.");
        }

        static void AssertLitContent(Texture2D image, string label)
        {
            Color[] pixels = image.GetPixels();
            int litPixels = 0;
            for (int index = 0; index < pixels.Length; ++index)
            {
                if (pixels[index].g > 0.1f)
                    ++litPixels;
            }

            Assert.Greater(litPixels, 100, label + " produced too few green pixels; the scene geometry was not rendered.");
        }

        static void AssertImagesDiffer(Texture2D temporalImage, Texture2D controlImage)
        {
            Assert.AreEqual(temporalImage.width, controlImage.width);
            Assert.AreEqual(temporalImage.height, controlImage.height);
            Color[] temporalPixels = temporalImage.GetPixels();
            Color[] controlPixels = controlImage.GetPixels();
            int changedPixels = 0;
            float totalDifference = 0f;
            for (int index = 0; index < temporalPixels.Length; ++index)
            {
                float difference = Mathf.Abs(temporalPixels[index].r - controlPixels[index].r)
                    + Mathf.Abs(temporalPixels[index].g - controlPixels[index].g)
                    + Mathf.Abs(temporalPixels[index].b - controlPixels[index].b);
                totalDifference += difference;
                if (difference > 0.02f)
                    ++changedPixels;
            }

            Assert.Greater(changedPixels, 20,
                "The TAA output did not differ meaningfully from the same camera rendered with TAA disabled.");
            Assert.Greater(totalDifference, 1f,
                "The TAA output had no measurable pixel difference from the TAA-off control.");
        }

        sealed class Fixture
        {
            readonly List<Object> owned = new List<Object>();
            readonly List<ProjectionObservation> observations = new List<ProjectionObservation>();
            RenderPipelineAsset previousDefaultPipeline;
            RenderPipelineAsset previousQualityPipeline;
            RenderTexture previousActiveTexture;
            UniversalRenderPipelineAsset pipeline;
            UniversalRendererData rendererData;
            ProjectionObserverFeature observer;
            GameObject root;
            Camera camera;
            RenderTexture target;
            GraphicsFixtureScope sceneScope;

            public UniversalAdditionalCameraData CameraData { get; private set; }
            public UrpQuiltCamera Guard { get; private set; }

            public void Setup()
            {
                previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
                previousQualityPipeline = QualitySettings.renderPipeline;
                previousActiveTexture = RenderTexture.active;

                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>(
                    "Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                Assert.IsNotNull(rendererData.postProcessData, "URP PostProcessData asset is unavailable.");

                root = new GameObject("UrpTaaPlatformFixture");
                GameObject cameraObject = new GameObject("OrdinaryTaaCamera");
                cameraObject.transform.SetParent(root.transform, false);
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 10f;
                camera.allowHDR = false;
                camera.allowMSAA = false;
                camera.allowDynamicResolution = false;

                CameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                CameraData.renderType = CameraRenderType.Base;
                CameraData.renderPostProcessing = true;
                CameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                CameraData.antialiasingQuality = AntialiasingQuality.High;

                Guard = cameraObject.AddComponent<UrpQuiltCamera>();

                RenderTextureDescriptor descriptor = new RenderTextureDescriptor(
                    TargetWidth, TargetHeight, RenderTextureFormat.ARGB32, 24)
                {
                    msaaSamples = 1,
                    useDynamicScale = false
                };
                target = new RenderTexture(descriptor) { name = "UrpTaaPlatformTarget" };
                Assert.IsTrue(target.Create(), "Could not create the TAA render target.");
                camera.targetTexture = target;

                AddAliasingGeometry();
                observer = ScriptableObject.CreateInstance<ProjectionObserverFeature>();
                observer.TargetCamera = camera;
                rendererData.rendererFeatures.Add(observer);
                observer.Create();
                rendererData.SetDirty();

                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                pipeline.msaaSampleCount = 1;
                pipeline.renderScale = 1f;
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;

                sceneScope = GraphicsFixtureScope.Create(root);
                sceneScope.Apply(camera);
                camera.enabled = true;

                Assert.AreEqual(1, pipeline.msaaSampleCount, "The URP asset must use one MSAA sample.");
                Assert.AreEqual(1, target.antiAliasing, "The render target must use one MSAA sample.");
                Assert.IsFalse(camera.allowMSAA, "MSAA must be disabled for the temporal AA test.");
                Assert.IsFalse(camera.allowDynamicResolution, "Dynamic resolution must be disabled for the temporal AA test.");
                Assert.IsFalse(target.useDynamicScale, "The render target must not use dynamic scaling.");
                Assert.AreEqual(CameraRenderType.Base, CameraData.renderType, "The test camera must not be an overlay camera.");
                Assert.IsTrue(CameraData.cameraStack == null || CameraData.cameraStack.Count == 0,
                    "The test camera must not use camera stacking.");
            }

            void AddAliasingGeometry()
            {
                GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bar.name = "HighContrastDiagonalBar";
                bar.transform.SetParent(root.transform, false);
                bar.transform.localPosition = new Vector3(0f, 0f, 3f);
                bar.transform.localRotation = Quaternion.Euler(0f, 0f, 23f);
                bar.transform.localScale = new Vector3(1.35f, 0.14f, 0.08f);

                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                Assert.IsNotNull(shader, "URP Unlit shader is unavailable.");
                Material material = new Material(shader) { color = Color.green };
                bar.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(bar);
                owned.Add(material);
            }

            public void ResetObservations()
            {
                observations.Clear();
                observer.ResetObservations();
            }

            public void AssertObservedTemporalJitter()
            {
                CopyObservations();
                Assert.GreaterOrEqual(observations.Count, 8,
                    "The observer did not see enough real camera render frames with TAA enabled.");
                AssertDistinctFrames(8);
                Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, observations[0].Mode,
                    "URP camera data did not report TAA during a TAA render.");

                Vector2 firstJitter = GetJitter(observations[0].Projection);
                bool changed = false;
                for (int index = 1; index < observations.Count; ++index)
                {
                    Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, observations[index].Mode,
                        "URP camera data did not report TAA during a TAA render.");
                    if ((GetJitter(observations[index].Projection) - firstJitter).sqrMagnitude > 1e-10f)
                        changed = true;
                }

                Assert.IsTrue(changed,
                    "UniversalCameraData.GetProjectionMatrix did not expose changing TAA projection jitter.");
            }

            public void AssertObservedStableProjection()
            {
                CopyObservations();
                Assert.GreaterOrEqual(observations.Count, 2,
                    "The observer did not see multiple TAA-off control frames.");
                AssertDistinctFrames(2);
                for (int index = 0; index < observations.Count; ++index)
                    Assert.AreEqual(AntialiasingMode.None, observations[index].Mode,
                        "URP camera data did not report the TAA-off control mode.");

                Matrix4x4 first = observations[0].Projection;
                for (int index = 1; index < observations.Count; ++index)
                    Assert.LessOrEqual(MaxMatrixDifference(first, observations[index].Projection), 1e-6f,
                        "The TAA-off control projection changed between frames.");
            }

            void CopyObservations()
            {
                observations.Clear();
                observations.AddRange(observer.Observations);
            }

            void AssertDistinctFrames(int minimum)
            {
                HashSet<int> frames = new HashSet<int>();
                for (int index = 0; index < observations.Count; ++index)
                    frames.Add(observations[index].Frame);
                Assert.GreaterOrEqual(frames.Count, minimum,
                    "The camera was not observed rendering across enough distinct Unity frames.");
            }

            public Texture2D Capture()
            {
                RenderTexture previous = RenderTexture.active;
                Texture2D image = null;
                try
                {
                    RenderTexture.active = target;
                    image = new Texture2D(TargetWidth, TargetHeight, TextureFormat.RGBA32, false);
                    image.ReadPixels(new Rect(0, 0, TargetWidth, TargetHeight), 0, 0);
                    image.Apply();
                    return image;
                }
                catch
                {
                    if (image != null) Object.DestroyImmediate(image);
                    throw;
                }
                finally
                {
                    RenderTexture.active = previous;
                }
            }

            public void Dispose()
            {
                if (camera != null)
                    camera.enabled = false;
                GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline;
                QualitySettings.renderPipeline = previousQualityPipeline;
                sceneScope?.Dispose();
                RenderTexture.active = previousActiveTexture;
                if (target != null)
                {
                    target.Release();
                    Object.DestroyImmediate(target);
                }
                for (int index = owned.Count - 1; index >= 0; --index)
                    if (owned[index] != null) Object.DestroyImmediate(owned[index]);
                if (observer != null) Object.DestroyImmediate(observer);
                if (root != null) Object.DestroyImmediate(root);
                if (pipeline != null) Object.DestroyImmediate(pipeline);
                if (rendererData != null) Object.DestroyImmediate(rendererData);
            }

            static Vector2 GetJitter(Matrix4x4 projection)
            {
                return new Vector2(projection.m02, projection.m12);
            }

            static float MaxMatrixDifference(Matrix4x4 left, Matrix4x4 right)
            {
                float max = 0f;
                for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                    max = Mathf.Max(max, Mathf.Abs(left[row, column] - right[row, column]));
                return max;
            }

            sealed class ProjectionObservation
            {
                public readonly int Frame;
                public readonly AntialiasingMode Mode;
                public readonly Matrix4x4 Projection;

                public ProjectionObservation(int frame, AntialiasingMode mode, Matrix4x4 projection)
                {
                    Frame = frame;
                    Mode = mode;
                    Projection = projection;
                }
            }

            sealed class ProjectionObserverFeature : ScriptableRendererFeature
            {
                readonly List<ProjectionObservation> observations = new List<ProjectionObservation>();
                ProjectionObserverPass pass;

                public Camera TargetCamera { get; set; }
                public IReadOnlyList<ProjectionObservation> Observations => observations;

                public override void Create()
                {
                    pass = new ProjectionObserverPass(this)
                    {
                        renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
                    };
                }

                public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
                {
                    if (pass != null && TargetCamera != null && renderingData.cameraData.camera == TargetCamera)
                        renderer.EnqueuePass(pass);
                }

                public void ResetObservations()
                {
                    observations.Clear();
                }

                sealed class ProjectionObserverPass : ScriptableRenderPass
                {
                    readonly ProjectionObserverFeature owner;

                    public ProjectionObserverPass(ProjectionObserverFeature feature)
                    {
                        owner = feature;
                    }

                    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
                    {
                        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                        if (cameraData.camera != owner.TargetCamera)
                            return;
                        owner.observations.Add(new ProjectionObservation(
                            Time.frameCount, cameraData.antialiasing, cameraData.GetProjectionMatrix()));
                    }
                }
            }
        }
    }
}
#endif
