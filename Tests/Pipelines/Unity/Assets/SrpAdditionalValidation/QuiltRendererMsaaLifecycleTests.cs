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
#if UNITY_EDITOR
        sealed class RecordingPreviewOwner : ScriptableObject, IQuiltEditorResourceRetentionOwner
        {
            public int TrackedTextureCount;
            public int StopRequestCount;
            public bool TrackTexture(RenderTexture texture) { TrackedTextureCount++; return texture != null; }
            public void RequestStopAndTakeOwnership(QuiltRenderer renderer) { StopRequestCount++; }
        }
#endif

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
            camera.allowHDR = false;
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
                    RenderTextureFormat viewFormat = ExpectedViewFormat(camera, pipeline);
                    int expected = requested <= 1 ? 1 : SupportedSamples(requested, 128, 96, viewFormat, viewFormat == RenderTextureFormat.ARGBHalf ? false : expectedSrgb);
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

        static RenderTextureFormat ExpectedViewFormat(Camera camera, UniversalRenderPipelineAsset pipeline)
        {
            return camera.allowHDR && pipeline.supportsHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
        }

        static int SupportedSamples(int requested, int width, int height, RenderTextureFormat format, bool sRgb)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(width, height, format, 24)
            {
                msaaSamples = requested,
                sRGB = sRgb
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
            camera.allowHDR = false;
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
                    RenderTextureFormat viewFormat = ExpectedViewFormat(camera, pipeline);
                    int expected = requested <= 1 ? 1 : SupportedSamples(requested, 128, 96, viewFormat, viewFormat == RenderTextureFormat.ARGBHalf ? false : quilt.QuiltTexture.sRGB);
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
            camera.allowHDR = false;
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
            camera.allowHDR = false;
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
                RenderTextureFormat initialFormat = ExpectedViewFormat(camera, pipeline);
                int expectedInitial = SupportedSamples(4, 128, 96, initialFormat, initialFormat == RenderTextureFormat.ARGBHalf ? false : quilt.QuiltTexture.sRGB);
                Assert.AreEqual(expectedInitial, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(expectedInitial > 1 ? 1 : 0, quilt.ResolveAntiAliasingSamples);
                Assert.AreEqual(256, quilt.QuiltTexture.width);
                Assert.AreEqual(192, quilt.QuiltTexture.height);
                quilt.settings.quiltWidth = 384;
                quilt.settings.quiltHeight = 256;
                quilt.RenderQuilt();
                int expectedResized = SupportedSamples(4, 192, 128, initialFormat, initialFormat == RenderTextureFormat.ARGBHalf ? false : quilt.QuiltTexture.sRGB);
                Assert.AreEqual(expectedResized, quilt.ViewAntiAliasingSamples);
                Assert.AreEqual(expectedResized > 1 ? 1 : 0, quilt.ResolveAntiAliasingSamples);
                Assert.AreEqual(384, quilt.QuiltTexture.width);
                Assert.AreEqual(256, quilt.QuiltTexture.height);
                quilt.settings.quiltWidth = 512;
                quilt.settings.quiltHeight = 256;
                quilt.RenderQuilt();
                int expectedFinal = SupportedSamples(4, 256, 128, initialFormat, initialFormat == RenderTextureFormat.ARGBHalf ? false : quilt.QuiltTexture.sRGB);
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

        [Test]
        public void CoreDefaultsToLdrWithoutAnOptionalViewFormatProvider()
        {
            GameObject host = new GameObject("QuiltRenderer.NoViewFormatProvider");
            try
            {
                Camera camera = host.AddComponent<Camera>();
                QuiltRenderer quilt = host.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = camera;
                MethodInfo method = typeof(QuiltRenderer).GetMethod("GetRequestedViewTextureFormat", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(method);
                Assert.AreEqual(RenderTextureFormat.ARGB32, method.Invoke(quilt, new object[] { camera }));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator HdrFormatReplacementRequestsPreviewStopBeforeReleasingOwnedTexture()
        {
            Assert.IsTrue(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf), "ARGBHalf render textures are required for this replacement test.");
            RenderPipelineAsset oldPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.supportsHDR = true;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            GameObject host = new GameObject("QuiltRenderer.PreviewFormatReplacement");
            RecordingPreviewOwner owner = ScriptableObject.CreateInstance<RecordingPreviewOwner>();
            Camera camera = host.AddComponent<Camera>();
            camera.allowHDR = false;
            UrpQuiltCamera guard = host.AddComponent<UrpQuiltCamera>();
            QuiltRenderer quilt = host.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = guard;
            MethodInfo beginRetention = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo ensureTextures = typeof(QuiltRenderer).GetMethod("EnsureTextures", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo retentionRequired = typeof(QuiltRenderer).GetField("editorPreviewRetentionRequired", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo retentionOwner = typeof(QuiltRenderer).GetField("editorPreviewRetentionOwner", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(beginRetention);
            Assert.IsNotNull(ensureTextures);
            Assert.IsNotNull(retentionRequired);
            Assert.IsNotNull(retentionOwner);
            RenderTexture originalView = null;
            try
            {
                yield return null;
                yield return null;
                Assert.IsTrue((bool)beginRetention.Invoke(quilt, new object[] { owner }));
                QuiltLayout layout = new QuiltLayout(64, 64, 1, 1, 1);
                ensureTextures.Invoke(quilt, new object[] { layout });
                originalView = GetTexture(quilt, "viewTexture");
                Assert.AreEqual(RenderTextureFormat.ARGB32, originalView.format);
                Assert.AreEqual(2, owner.TrackedTextureCount, "The atlas and view targets must both be retained.");

                camera.allowHDR = true;
                TargetInvocationException wrapped = Assert.Throws<TargetInvocationException>(
                    () => ensureTextures.Invoke(quilt, new object[] { layout }));
                Assert.IsInstanceOf<System.InvalidOperationException>(wrapped.InnerException);
                StringAssert.Contains("replacement requires stop", wrapped.InnerException.Message);
                Assert.AreEqual(1, owner.StopRequestCount, "A format transition must request preview stop before releasing the old view.");
                Assert.AreSame(originalView, GetTexture(quilt, "viewTexture"));
                Assert.IsTrue(originalView.IsCreated(), "The retained view must survive until the stop/proof handoff.");
                Assert.AreEqual(2, owner.TrackedTextureCount, "The replacement must not be tracked before the stop gate.");

                retentionRequired.SetValue(quilt, false);
                quilt.enabled = false;
                Assert.IsNull(GetTexture(quilt, "viewTexture"), "Ending retention must release the old LDR view.");
                retentionOwner.SetValue(quilt, null);
                camera.allowHDR = true;
                RecordingPreviewOwner hdrOwner = ScriptableObject.CreateInstance<RecordingPreviewOwner>();
                try
                {
                    quilt.enabled = true;
                    Assert.IsTrue((bool)beginRetention.Invoke(quilt, new object[] { hdrOwner }));
                    ensureTextures.Invoke(quilt, new object[] { layout });
                    Assert.AreEqual(3, hdrOwner.TrackedTextureCount, "HDR preview must retain atlas, HDR view, and LDR resolve targets.");
                    Assert.AreEqual(RenderTextureFormat.ARGBHalf, GetTexture(quilt, "viewTexture").format);
                    Assert.AreEqual(RenderTextureFormat.ARGB32, GetTexture(quilt, "resolveTexture").format);
                }
                finally
                {
                    retentionRequired.SetValue(quilt, false);
                    quilt.enabled = false;
                    Assert.IsNull(GetTexture(quilt, "viewTexture"), "Ending HDR retention must release the view target.");
                    Assert.IsNull(GetTexture(quilt, "resolveTexture"), "Ending HDR retention must release the resolve target.");
                    Object.DestroyImmediate(hdrOwner);
                }
            }
            finally
            {
                retentionRequired.SetValue(quilt, false);
                quilt.enabled = false;
                Assert.IsNull(GetTexture(quilt, "viewTexture"), "Disabling after retention ends must release the view.");
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(owner);
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }
#endif

        [UnityTest]
        public IEnumerator UrpHdrViewResolvesToLdrAndRecreatesOnFormatToggle()
        {
            Assert.IsTrue(SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf), "ARGBHalf is required for this HDR view test.");
            RenderPipelineAsset oldPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQuality = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.supportsHDR = true;
            pipeline.msaaSampleCount = 4;
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            GameObject host = new GameObject("QuiltRenderer.HdrViewResolve");
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.allowHDR = true;
            camera.allowMSAA = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(2f, 0.5f, 0.25f, 0.25f);
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
            Texture2D readback = null;
            try
            {
                yield return null;
                yield return null;
                quilt.RenderQuilt();
                RenderTexture firstView = GetTexture(quilt, "viewTexture");
                RenderTexture resolve = GetTexture(quilt, "resolveTexture");
                Assert.AreEqual(RenderTextureFormat.ARGB32, quilt.QuiltTexture.format);
                Assert.AreEqual(RenderTextureFormat.ARGBHalf, firstView.format);
                Assert.IsFalse(firstView.sRGB, "HDR source views must use a linear target.");
                Assert.IsNotNull(resolve, "HDR view format conversion must resolve to an LDR atlas-compatible target.");
                Assert.AreEqual(RenderTextureFormat.ARGB32, resolve.format);
                Assert.AreEqual(quilt.QuiltTexture.graphicsFormat, resolve.graphicsFormat);
                Assert.AreEqual(quilt.QuiltTexture.sRGB, resolve.sRGB);
                Assert.AreEqual(1, resolve.antiAliasing);
                Assert.AreEqual(SupportedSamples(4, 128, 96, RenderTextureFormat.ARGBHalf, false), firstView.antiAliasing);

                RenderTexture oldActive = RenderTexture.active;
                try
                {
                    RenderTexture.active = firstView;
                    GL.Clear(false, true, new Color(2f, 0.5f, 0.25f, 0.25f));
                }
                finally
                {
                    RenderTexture.active = oldActive;
                }
                MethodInfo resolveMethod = typeof(QuiltRenderer).GetMethod("ResolveViewTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(resolveMethod);
                Assert.AreSame(resolve, resolveMethod.Invoke(quilt, null));
                oldActive = RenderTexture.active;
                readback = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                try
                {
                    RenderTexture.active = resolve;
                    readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                    readback.Apply(false, false);
                }
                finally
                {
                    RenderTexture.active = oldActive;
                }
                Color resolved = readback.GetPixel(0, 0);
                Assert.Greater(resolved.r, 0.95f, "HDR input should reach the LDR maximum without a custom tone map.");
                float expectedGreen = resolve.sRGB && QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? Mathf.LinearToGammaSpace(0.5f) : 0.5f;
                float expectedBlue = resolve.sRGB && QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? Mathf.LinearToGammaSpace(0.25f) : 0.25f;
                Assert.That(resolved.g, Is.EqualTo(expectedGreen).Within(0.08f), "The resolve green channel must follow the destination sRGB state.");
                Assert.That(resolved.b, Is.EqualTo(expectedBlue).Within(0.08f), "The resolve blue channel must follow the destination sRGB state.");
                Assert.That(resolved.a, Is.EqualTo(0.25f).Within(0.02f), "The resolve must preserve alpha.");

                camera.allowMSAA = false;
                pipeline.msaaSampleCount = 1;
                quilt.RenderQuilt();
                RenderTexture singleSampleHdrView = GetTexture(quilt, "viewTexture");
                RenderTexture singleSampleHdrResolve = GetTexture(quilt, "resolveTexture");
                if (firstView.antiAliasing > 1)
                    Assert.AreNotSame(firstView, singleSampleHdrView, "Changing sample count must replace the view target.");
                else
                    Assert.AreSame(firstView, singleSampleHdrView, "A supported one-sample target should be reused when its format is unchanged.");
                Assert.AreEqual(RenderTextureFormat.ARGBHalf, singleSampleHdrView.format);
                Assert.AreEqual(1, singleSampleHdrView.antiAliasing);
                Assert.IsNotNull(singleSampleHdrResolve, "Single-sample HDR still needs an LDR atlas conversion.");
                Assert.AreEqual(1, singleSampleHdrResolve.antiAliasing);

                pipeline.supportsHDR = false;
                quilt.RenderQuilt();
                RenderTexture ldrView = GetTexture(quilt, "viewTexture");
                Assert.AreNotSame(singleSampleHdrView, ldrView, "A format transition must replace the view target.");
                Assert.AreEqual(RenderTextureFormat.ARGB32, ldrView.format);
                Assert.IsNull(GetTexture(quilt, "resolveTexture"), "A compatible single-sample LDR view must not retain a resolve target.");
                Assert.AreEqual(RenderTextureFormat.ARGB32, quilt.QuiltTexture.format, "The quilt atlas remains LDR.");

                pipeline.supportsHDR = true;
                camera.allowHDR = false;
                quilt.RenderQuilt();
                Assert.AreSame(ldrView, GetTexture(quilt, "viewTexture"), "A source camera with HDR disabled must retain the LDR target.");
            }
            finally
            {
                if (readback != null) Object.DestroyImmediate(readback);
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }
    }
}
