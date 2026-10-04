using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.URP;

namespace ViewWeave.SrpAdditionalValidation
{
    public sealed class QuiltRendererMsaaLifecycleTests
    {
        [UnityTest]
        public IEnumerator UrpMsaaPreservesColorSpaceAcrossSamplesResizeAndCleanup()
        {
            ColorSpace activeColorSpace = QualitySettings.activeColorSpace;
            Assert.IsTrue(activeColorSpace == ColorSpace.Gamma || activeColorSpace == ColorSpace.Linear, "Unexpected active color space");
            bool expectedSrgb = activeColorSpace == ColorSpace.Linear;
            RenderPipelineAsset oldPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.msaaSampleCount = 1;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            GameObject host = new GameObject("QuiltRendererGammaMsaa");
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            UniversalAdditionalCameraData cameraData = host.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            UrpQuiltCamera guard = host.AddComponent<UrpQuiltCamera>();
            QuiltRenderer quilt = host.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = guard;
            quilt.settings.quiltWidth = 256;
            quilt.settings.quiltHeight = 192;
            quilt.settings.columns = 2;
            quilt.settings.rows = 2;
            quilt.settings.views = 4;
            quilt.settings.viewAspect = 4f / 3f;
            camera.allowMSAA = true;
            try
            {
                yield return null;
                yield return null;
                foreach (int requested in new[] { 1, 2, 4, 8 })
                {
                    pipeline.msaaSampleCount = requested;
                    quilt.RenderQuilt();
                    RenderTexture view = GetTexture(quilt, "viewTexture");
                    RenderTexture resolve = GetTexture(quilt, "resolveTexture");
                    int expected = requested <= 1 ? 1 : SupportedSamples(requested, 128, 96);
                    Assert.AreEqual(expected, view.antiAliasing, "Gamma view sample mismatch for requested " + requested);
                    Assert.AreEqual(quilt.QuiltTexture.sRGB, view.sRGB, "Gamma quilt/view sRGB mismatch for requested " + requested);
                    Assert.AreEqual(expectedSrgb, quilt.QuiltTexture.sRGB, "Quilt sRGB does not match active color space for requested " + requested);
                    Assert.AreEqual(expectedSrgb, view.sRGB, "View sRGB does not match active color space for requested " + requested);
                    Assert.AreEqual(expected > 1 ? 1 : 0, resolve == null ? 0 : resolve.antiAliasing, "Gamma resolve mismatch for requested " + requested);
                    if (resolve != null) { Assert.AreEqual(view.sRGB, resolve.sRGB, "Gamma view/resolve sRGB mismatch for requested " + requested); Assert.AreEqual(expectedSrgb, resolve.sRGB, "Resolve sRGB does not match active color space for requested " + requested); }
                    Assert.AreEqual(activeColorSpace, QualitySettings.activeColorSpace, "MSAA changed active color space");
                    Debug.Log("MSAA color validation requested=" + requested + " actual=" + view.antiAliasing + " quiltSrgb=" + quilt.QuiltTexture.sRGB + " viewSrgb=" + view.sRGB + " graphicsFormat=" + view.graphicsFormat + " colorSpace=" + QualitySettings.activeColorSpace);
                }
                quilt.settings.quiltWidth = 384;
                quilt.settings.quiltHeight = 256;
                quilt.RenderQuilt();
                Assert.AreEqual(quilt.QuiltTexture.sRGB, GetTexture(quilt, "viewTexture").sRGB, "Gamma resize changed sRGB state");
                quilt.enabled = false;
                Assert.AreEqual(0, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(0, quilt.ResolveAntiAliasingSamples);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }

        static RenderTexture GetTexture(QuiltRenderer quilt, string fieldName)
        {
            FieldInfo field = typeof(QuiltRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, fieldName + " field is missing");
            return field.GetValue(quilt) as RenderTexture;
        }

        static int SupportedSamples(int requested, int width, int height)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = requested
            };
            int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
            return supported == 2 || supported == 4 || supported == 8 ? supported : 1;
        }

        [UnityTest]
        public IEnumerator UrpMsaaRequestedSamplesFollowDeviceSupport()
        {
            RenderPipelineAsset oldPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.msaaSampleCount = 1;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            GameObject host = new GameObject("QuiltRendererMsaaSupport");
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            UniversalAdditionalCameraData cameraData = host.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            UrpQuiltCamera guard = host.AddComponent<UrpQuiltCamera>();
            QuiltRenderer quilt = host.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = guard;
            quilt.settings.quiltWidth = 256;
            quilt.settings.quiltHeight = 192;
            quilt.settings.columns = 2;
            quilt.settings.rows = 2;
            quilt.settings.views = 4;
            quilt.settings.viewAspect = 4f / 3f;
            camera.allowMSAA = true;
            try
            {
                yield return null;
                yield return null;
                foreach (int requested in new[] { 1, 2, 4, 8 })
                {
                    pipeline.msaaSampleCount = requested;
                    quilt.RenderQuilt();
                    int expected = requested <= 1 ? 1 : SupportedSamples(requested, 128, 96);
                    RenderTexture view = GetTexture(quilt, "viewTexture");
                    RenderTexture resolve = GetTexture(quilt, "resolveTexture");
                    Assert.AreEqual(expected, view.antiAliasing, "view sample mismatch for requested " + requested);
                    Assert.AreEqual(expected > 1 ? 1 : 0, resolve == null ? 0 : resolve.antiAliasing, "resolve lifecycle mismatch for requested " + requested);
                    Debug.Log("MSAA lifecycle requested=" + requested + " expected=" + expected + " actual=" + view.antiAliasing + " colorSpace=" + QualitySettings.activeColorSpace);
                }
                camera.allowMSAA = false;
                pipeline.msaaSampleCount = 8;
                quilt.RenderQuilt();
                Assert.AreEqual(1, GetTexture(quilt, "viewTexture").antiAliasing);
                Assert.IsNull(GetTexture(quilt, "resolveTexture"));
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }

        [UnityTest]
        public IEnumerator UrpDeferredRendererFallsBackToSingleSample()
        {
            RenderPipelineAsset oldPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            rendererData.renderingMode = RenderingMode.Deferred;
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.msaaSampleCount = 8;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            GameObject host = new GameObject("QuiltRendererMsaaDeferred");
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            UniversalAdditionalCameraData cameraData = host.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            UrpQuiltCamera guard = host.AddComponent<UrpQuiltCamera>();
            QuiltRenderer quilt = host.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = guard;
            quilt.settings.quiltWidth = 256;
            quilt.settings.quiltHeight = 192;
            quilt.settings.columns = 2;
            quilt.settings.rows = 2;
            quilt.settings.views = 4;
            quilt.settings.viewAspect = 4f / 3f;
            camera.allowMSAA = true;
            try
            {
                yield return null;
                yield return null;
                quilt.RenderQuilt();
                Assert.AreEqual(1, GetTexture(quilt, "viewTexture").antiAliasing);
                Assert.IsNull(GetTexture(quilt, "resolveTexture"));
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }

        [UnityTest]
        public IEnumerator UrpMsaaRecreatesResolveOnResizeAndDisable()
        {
            RenderPipelineAsset oldPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.msaaSampleCount = 4;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            GameObject host = new GameObject("QuiltRendererMsaaLifecycle");
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            UniversalAdditionalCameraData cameraData = host.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            UrpQuiltCamera guard = host.AddComponent<UrpQuiltCamera>();
            QuiltRenderer quilt = host.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = guard;
            quilt.settings.quiltWidth = 256;
            quilt.settings.quiltHeight = 192;
            quilt.settings.columns = 2;
            quilt.settings.rows = 2;
            quilt.settings.views = 4;
            quilt.settings.viewAspect = 4f / 3f;
            quilt.settings.viewCone = 20f;
            camera.allowMSAA = true;
            try
            {
                yield return null;
                yield return null;
                quilt.RenderQuilt();
                int expectedInitial = SupportedSamples(4, 128, 96);
                Assert.AreEqual(expectedInitial, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(expectedInitial > 1 ? 1 : 0, quilt.ResolveAntiAliasingSamples);
                Assert.AreEqual(256, quilt.QuiltTexture.width);
                Assert.AreEqual(192, quilt.QuiltTexture.height);
                quilt.settings.quiltWidth = 384;
                quilt.settings.quiltHeight = 256;
                quilt.RenderQuilt();
                int expectedResized = SupportedSamples(4, 192, 128);
                Assert.AreEqual(expectedResized, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(expectedResized > 1 ? 1 : 0, quilt.ResolveAntiAliasingSamples);
                Assert.AreEqual(384, quilt.QuiltTexture.width);
                Assert.AreEqual(256, quilt.QuiltTexture.height);
                quilt.settings.quiltWidth = 512;
                quilt.settings.quiltHeight = 256;
                quilt.RenderQuilt();
                int expectedFinal = SupportedSamples(4, 256, 128);
                Assert.AreEqual(expectedFinal, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(expectedFinal > 1 ? 1 : 0, quilt.ResolveAntiAliasingSamples);
                quilt.enabled = false;
                Assert.AreEqual(0, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(0, quilt.ResolveAntiAliasingSamples);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }
    }
}
