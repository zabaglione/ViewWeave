using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using LookingGlass.Community;
using LookingGlass.Community.URP;

namespace LookingGlass.SrpAdditionalValidation
{
    public sealed class QuiltRendererMsaaLifecycleTests
    {
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
                Assert.AreEqual(4, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(1, quilt.ResolveAntiAliasingSamples);
                Assert.AreEqual(256, quilt.QuiltTexture.width);
                Assert.AreEqual(192, quilt.QuiltTexture.height);
                quilt.settings.quiltWidth = 384;
                quilt.settings.quiltHeight = 256;
                quilt.RenderQuilt();
                Assert.AreEqual(4, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(1, quilt.ResolveAntiAliasingSamples);
                Assert.AreEqual(384, quilt.QuiltTexture.width);
                Assert.AreEqual(256, quilt.QuiltTexture.height);
                camera.allowMSAA = false;
                quilt.RenderQuilt();
                Assert.AreEqual(1, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(0, quilt.ResolveAntiAliasingSamples);
                quilt.settings.quiltWidth = 512;
                quilt.settings.quiltHeight = 256;
                camera.allowMSAA = true;
                quilt.RenderQuilt();
                Assert.AreEqual(4, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(1, quilt.ResolveAntiAliasingSamples);
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
