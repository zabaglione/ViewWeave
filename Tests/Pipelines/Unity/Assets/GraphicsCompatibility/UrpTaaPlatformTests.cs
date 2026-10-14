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
        public IEnumerator QuiltTaaHasIndependentAccumulatingHistoriesAndChangedPixels()
        {
            RequireSupportedDesktopEditor();
            Fixture fixture = null;
            Texture2D temporal = null, control = null;
            try
            {
                fixture = new Fixture(); fixture.Setup();
                yield return null; yield return null;
                fixture.SetupQuilt();
                int[] firstVersions = new int[4];
                TaaHistory[] histories = new TaaHistory[4];
                RenderTexture[] textures = new RenderTexture[4];
                Camera[] cameras = new Camera[4];
                for (int frame = 0; frame < 16; frame++)
                {
                    yield return null; fixture.Quilt.RenderQuilt();
                    Assert.AreEqual(4, fixture.Guard.HistoryCameraCount);
                    for (int view = 0; view < 4; view++)
                    {
                        Camera camera = fixture.Guard.GetHistoryCamera(view);
                        Assert.IsFalse(camera.enabled, "Owned views must never participate in automatic scene rendering");
                        var data = camera.GetComponent<UniversalAdditionalCameraData>();
                        var history = data.history.GetHistoryForRead<TaaHistory>();
                        Assert.IsNotNull(history, "A newly created disabled view did not initialize TAA");
                        Assert.IsNotNull(history.GetAccumulationTexture()?.rt);
                        Assert.AreEqual(1, history.GetAccumulationTexture().rt.antiAliasing);
                        if (frame == 0)
                        {
                            cameras[view] = camera; histories[view] = history;
                            textures[view] = history.GetAccumulationTexture().rt;
                            firstVersions[view] = history.GetAccumulationVersion();
                        }
                        else
                        {
                            Assert.AreSame(cameras[view], camera);
                            Assert.AreSame(histories[view], history);
                            Assert.Greater(history.GetAccumulationVersion(), firstVersions[view], "View history did not accumulate across real frames");
                        }
                        for (int other = 0; other < view; other++)
                        {
                            Assert.AreNotSame(histories[other], history, "Logical views shared a TAA history object");
                            Assert.AreNotSame(textures[other], history.GetAccumulationTexture().rt, "Logical views shared a TAA accumulation texture");
                        }
                    }
                }
                fixture.AssertQuiltTemporalJitter();
                int version = histories[0].GetAccumulationVersion();
                fixture.Quilt.RenderQuilt();
                Assert.AreEqual(version, histories[0].GetAccumulationVersion(), "Same-frame redraw must not advance history version");
                temporal = fixture.CaptureQuilt(); AssertLitContent(temporal, "Quilt TAA");
                fixture.SettingsData.antialiasing = AntialiasingMode.None;
                fixture.ResetObservations();
                for (int frame = 0; frame < 4; frame++) { yield return null; fixture.Quilt.RenderQuilt(); }
                fixture.AssertQuiltStableProjection();
                control = fixture.CaptureQuilt(); AssertLitContent(control, "Quilt TAA-off");
                AssertImagesDiffer(temporal, control);
                Assert.AreEqual(AntialiasingMode.None, fixture.SettingsData.antialiasing);
            }
            finally
            {
                if (temporal != null) Object.DestroyImmediate(temporal);
                if (control != null) Object.DestroyImmediate(control);
                fixture?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator QuiltHistoryPoolPrunesResizesRestartsAndRejectsInvalidTaa()
        {
            RequireSupportedDesktopEditor(); Fixture fixture = null;
            try
            {
                fixture = new Fixture(); fixture.Setup(); yield return null; yield return null;
                fixture.SetupQuilt(); fixture.Quilt.RenderQuilt();
                Camera left = fixture.Guard.GetHistoryCamera(0), right = fixture.Guard.GetHistoryCamera(3);
                fixture.Quilt.viewRenderMode = QuiltViewRenderMode.Quarter;
                yield return null; fixture.Quilt.RenderQuilt();
                Assert.AreEqual(2, fixture.Guard.HistoryCameraCount);
                Assert.AreSame(left, fixture.Guard.GetHistoryCamera(0)); Assert.AreSame(right, fixture.Guard.GetHistoryCamera(3));
                Assert.IsNull(fixture.Guard.GetHistoryCamera(1)); Assert.IsNull(fixture.Guard.GetHistoryCamera(2));
                fixture.Quilt.settings.quiltWidth += 64;
                yield return null; fixture.Quilt.RenderQuilt();
                Assert.AreNotSame(left, fixture.Guard.GetHistoryCamera(0));
                fixture.Quilt.ReleasePipelineRenderResources();
                Assert.AreEqual(0, fixture.Guard.HistoryCameraCount);
                yield return null;
                Assert.IsTrue(left == null, "Pruned/old cameras were not destroyed after a frame");
                fixture.Quilt.RenderQuilt(); Assert.AreEqual(2, fixture.Guard.HistoryCameraCount);
                fixture.Guard.maximumHistoryCameras = 1;
                Assert.Throws<System.InvalidOperationException>(() => fixture.Quilt.RenderQuilt());
                Assert.AreEqual(0, fixture.Guard.HistoryCameraCount);
                fixture.Guard.maximumHistoryCameras = 256;
                fixture.SettingsData.renderPostProcessing = false;
                Assert.Throws<System.NotSupportedException>(() => fixture.Quilt.RenderQuilt());
                Assert.IsFalse(fixture.SettingsData.renderPostProcessing, "The SDK silently rewrote authored settings");
                fixture.SettingsData.renderPostProcessing = true;
                fixture.Quilt.sourceCamera.allowDynamicResolution = true;
                Debug.Log("DynamicResolutionReject requested=true effective=" +
                    fixture.Quilt.sourceCamera.allowDynamicResolution.ToString().ToLowerInvariant() +
                    " graphicsApi=" + SystemInfo.graphicsDeviceType +
                    " sourceAA=" + fixture.CameraData.antialiasing +
                    " referenceAA=" + fixture.SettingsData.antialiasing);
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 &&
                    !fixture.Quilt.sourceCamera.allowDynamicResolution)
                {
                    Assert.AreEqual(GraphicsDeviceType.Direct3D11, SystemInfo.graphicsDeviceType);
                    Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.CameraData.antialiasing);
                    Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.SettingsData.antialiasing);
                    long revisionBeforeUnsupportedDynamicResolution = fixture.Quilt.QuiltRevision;
                    fixture.Quilt.RenderQuilt();
                    Assert.Greater(fixture.Quilt.QuiltRevision, revisionBeforeUnsupportedDynamicResolution,
                        "The D3D11 quilt should render when Unity does not enable dynamic resolution.");
                    Assert.AreEqual(2, fixture.Guard.HistoryCameraCount,
                        "The D3D11 fallback should retain both isolated TAA view histories.");
                    Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.CameraData.antialiasing);
                    Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.SettingsData.antialiasing);
                    Debug.Log("DynamicResolutionReject unsupported=true graphicsApi=Direct3D11 requested=true effective=false " +
                        "result=rendered historyCameraCount=" + fixture.Guard.HistoryCameraCount +
                        " quiltRevision=" + fixture.Quilt.QuiltRevision);
                }
                else
                {
                    Assert.IsTrue(fixture.Quilt.sourceCamera.allowDynamicResolution,
                        "Unity must enable dynamic resolution before the unsupported-feature rejection assertion.");
                    Assert.Throws<System.NotSupportedException>(() => fixture.Quilt.RenderQuilt());
                }
                fixture.Quilt.sourceCamera.allowDynamicResolution = false;
                fixture.StopObservations();
                fixture.Guard.renderingMode = UrpQuiltRenderingMode.SharedCameraCompatibility;
                fixture.Quilt.RenderQuilt(); Assert.AreEqual(0, fixture.Guard.HistoryCameraCount);
                Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.SettingsData.antialiasing);
                fixture.Guard.renderingMode = UrpQuiltRenderingMode.IsolatedViewHistories;
                fixture.Guard.historyMemoryBudgetMB = 1;
                fixture.Quilt.settings.quiltWidth = 1536; fixture.Quilt.settings.quiltHeight = 1024;
                yield return null;
                Assert.Throws<System.InsufficientMemoryException>(() => fixture.Quilt.RenderQuilt());
                Assert.AreEqual(0, fixture.Guard.HistoryCameraCount, "Budget failure did not retire owned resources");
            }
            finally { fixture?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator QuiltCopiesScriptedVolumesWithoutSharingOrRewritingReferenceState()
        {
            RequireSupportedDesktopEditor(); Fixture fixture = null; VolumeStack authored = null;
            try
            {
                fixture = new Fixture(); fixture.Setup(); yield return null; yield return null;
                fixture.SetupQuilt();
                var reference = fixture.Quilt.referenceCamera;
                reference.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                authored = VolumeManager.instance.CreateStack(); fixture.SettingsData.volumeStack = authored;
                authored.GetComponent<ColorAdjustments>().postExposure.Override(1.5f);
                var originalCurve = new TextureCurve(new[] { new Keyframe(.2f, .1f), new Keyframe(.6f, .8f) }, .5f, true, new Vector2(0f, 1f));
                var curves = authored.GetComponent<ColorCurves>(); curves.hueVsHue.value = originalCurve; curves.hueVsHue.overrideState = true;
                Texture2D originalCurveTexture = originalCurve.GetTexture();
                var privateStacks = new VolumeStack[4];
                var privateCurves = new TextureCurve[4];
                fixture.Quilt.RenderQuilt();
                for (int view = 0; view < 4; view++)
                {
                    var camera = fixture.Guard.GetHistoryCamera(view); var data = camera.GetComponent<UniversalAdditionalCameraData>();
                    Assert.AreEqual(VolumeFrameworkUpdateMode.ViaScripting, camera.GetVolumeFrameworkUpdateMode());
                    Assert.AreNotSame(authored, data.volumeStack); privateStacks[view] = data.volumeStack;
                    var copiedCurve = data.volumeStack.GetComponent<ColorCurves>().hueVsHue.value;
                    privateCurves[view] = copiedCurve;
                    Assert.AreNotSame(originalCurve, copiedCurve); Assert.AreNotSame(originalCurveTexture, copiedCurve.GetTexture());
                    foreach (float sample in new[] { 0f, .1f, .25f, .5f, .75f, .9f, 1f })
                        Assert.AreEqual(originalCurve.Evaluate(sample), copiedCurve.Evaluate(sample), .00001f, "Looped curve evaluation changed in the private copy");
                    Assert.AreEqual(1.5f, data.volumeStack.GetComponent<ColorAdjustments>().postExposure.value);
                }
                for (int frame = 0; frame < 3; frame++)
                {
                    yield return null; fixture.Quilt.RenderQuilt();
                    for (int view = 0; view < 4; view++)
                        Assert.AreSame(privateCurves[view], privateStacks[view].GetComponent<ColorCurves>().hueVsHue.value,
                            "Unchanged curve copies were replaced instead of reused");
                }
                authored.GetComponent<ColorAdjustments>().postExposure.Override(2.5f);
                originalCurve.MoveKey(0, new Keyframe(.2f, .4f)); originalCurve.GetTexture();
                yield return null; fixture.Quilt.RenderQuilt();
                Assert.AreEqual(2.5f, fixture.Guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>().volumeStack.GetComponent<ColorAdjustments>().postExposure.value);
                var changedCurve = fixture.Guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>().volumeStack.GetComponent<ColorCurves>().hueVsHue.value;
                Assert.AreEqual(originalCurve.Evaluate(.2f), changedCurve.Evaluate(.2f), .00001f);
                fixture.Quilt.ReleasePipelineRenderResources(); yield return null;
                foreach (var stack in privateStacks) Assert.IsFalse(stack.isValid, "Private stack was cached instead of destroyed");
                Assert.IsTrue(originalCurveTexture != null, "Stop destroyed the Reference Camera curve texture");
                Assert.AreSame(originalCurveTexture, originalCurve.GetTexture());
                Assert.IsTrue(authored.isValid); Assert.AreSame(authored, fixture.SettingsData.volumeStack);
                VolumeManager.instance.DestroyStack(authored); fixture.SettingsData.volumeStack = null; authored = null;
                reference.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
                fixture.Quilt.RenderQuilt();
                Assert.AreEqual(VolumeFrameworkUpdateMode.EveryFrame, fixture.Guard.GetHistoryCamera(0).GetVolumeFrameworkUpdateMode());
                Assert.AreEqual(0, fixture.Quilt.referenceCamera.GetComponents<AudioListener>().Length);
                Assert.AreEqual(0, fixture.Guard.GetHistoryCamera(0).GetComponents<AudioListener>().Length);
            }
            finally
            {
                if (authored != null && authored.isValid)
                {
                    VolumeManager.instance.DestroyStack(authored);
                    if (fixture?.SettingsData != null) fixture.SettingsData.volumeStack = null;
                }
                fixture?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator QuiltMsaaFollowsEffectiveRendererAndSourcePermission()
        {
            RequireSupportedDesktopEditor();
            foreach (bool sourceDeferred in new[] { true, false })
            {
                Fixture fixture = null;
                try
                {
                    fixture = new Fixture(); fixture.Setup(); fixture.ConfigureRendererPair(sourceDeferred);
                    yield return null; yield return null;
                    fixture.SetupQuilt(); fixture.Quilt.sourceCamera.allowMSAA = true;
                    fixture.CameraData.antialiasing = fixture.SettingsData.antialiasing = AntialiasingMode.None;
                    fixture.SettingsData.SetRenderer(1);
                    var descriptor = new RenderTextureDescriptor(TargetWidth, TargetHeight, RenderTextureFormat.ARGB32, 24) { msaaSamples = 4 };
                    int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
                    int forwardSamples = supported == 2 || supported == 4 || supported == 8 ? supported : 1;
                    fixture.Quilt.RenderQuilt();
                    Assert.AreEqual(sourceDeferred ? forwardSamples : 1, fixture.Quilt.ViewAntiAliasingSamples);
                    Assert.AreSame(fixture.SettingsData.scriptableRenderer,
                        fixture.Guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>().scriptableRenderer);
                    fixture.Guard.followReferenceRenderingSettings = false; fixture.Quilt.RenderQuilt();
                    Assert.AreEqual(sourceDeferred ? 1 : forwardSamples, fixture.Quilt.ViewAntiAliasingSamples);
                    fixture.Guard.followReferenceRenderingSettings = true; fixture.Quilt.sourceCamera.allowMSAA = false;
                    fixture.Quilt.RenderQuilt(); Assert.AreEqual(1, fixture.Quilt.ViewAntiAliasingSamples);
                    UniversalRenderPipelineAsset activeUrp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                    bool formatResolve = fixture.Quilt.sourceCamera.allowHDR && activeUrp != null && activeUrp.supportsHDR;
                    Assert.AreEqual(formatResolve ? 1 : 0, fixture.Quilt.ResolveAntiAliasingSamples,
                        "A single-sample HDR view still requires conversion to the LDR quilt atlas.");
                    fixture.Quilt.sourceCamera.allowMSAA = true;
                    if (!sourceDeferred)
                    {
                        fixture.SettingsData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                        yield return null; fixture.Quilt.RenderQuilt();
                        Assert.AreEqual(1, fixture.Quilt.ViewAntiAliasingSamples);
                        Assert.IsNotNull(fixture.Guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>()
                            .history.GetHistoryForRead<TaaHistory>()?.GetAccumulationTexture()?.rt,
                            "Deferred TAA was rejected despite the actual target using one sample");
                    }
                    else
                    {
                        fixture.SettingsData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                        Assert.Throws<System.NotSupportedException>(() => fixture.Quilt.RenderQuilt(),
                            "Forward TAA accepted a multisampled target");
                        Assert.AreEqual(0, fixture.Guard.HistoryCameraCount);
                        Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.SettingsData.antialiasing);
                    }
                    fixture.Guard.renderingMode = UrpQuiltRenderingMode.SharedCameraCompatibility;
                    Assert.AreEqual(sourceDeferred ? 1 : forwardSamples,
                        fixture.Guard.GetMsaaSampleCount(fixture.Quilt.sourceCamera, fixture.Quilt.referenceCamera));
                }
                finally { fixture?.Dispose(); }
                // The fixture's isolated scene unload is asynchronous in Play Mode.
                if (fixture?.PendingSceneUnload != null) yield return fixture.PendingSceneUnload;
            }
        }

        [UnityTest]
        public IEnumerator ScriptedPipelineSettingReleasesPrivateStacksAcrossRestarts()
        {
            RequireSupportedDesktopEditor(); Fixture fixture = null; VolumeStack authored = null;
            try
            {
                fixture = new Fixture(); fixture.Setup(); yield return null; yield return null;
                fixture.SetupQuilt(); fixture.SetPipelineVolumeMode(VolumeFrameworkUpdateMode.ViaScripting);
                // Asset validation can rebuild URP and its VolumeManager on the next frame.
                yield return null; yield return null;
                fixture.Quilt.RenderQuilt(); fixture.Quilt.ReleasePipelineRenderResources();
                Assert.AreEqual(VolumeFrameworkUpdateMode.UsePipelineSettings, fixture.Quilt.referenceCamera.GetVolumeFrameworkUpdateMode());
                authored = VolumeManager.instance.CreateStack(); fixture.SettingsData.volumeStack = authored;
                for (int cycle = 0; cycle < 3; cycle++)
                {
                    yield return null;
                    Assert.IsTrue(authored.isValid, "Reference stack was invalidated before quilt rendering");
                    fixture.Quilt.RenderQuilt();
                    var stacks = new VolumeStack[4];
                    for (int view = 0; view < 4; view++)
                    {
                        stacks[view] = fixture.Guard.GetHistoryCamera(view).GetComponent<UniversalAdditionalCameraData>().volumeStack;
                        Assert.IsNotNull(stacks[view]); Assert.IsTrue(stacks[view].isValid); Assert.AreNotSame(authored, stacks[view]);
                    }
                    fixture.Quilt.ReleasePipelineRenderResources();
                    foreach (var stack in stacks) Assert.IsFalse(stack.isValid, "Private stack survived stop in URP's cache");
                    Assert.IsTrue(authored.isValid);
                }
            }
            finally
            {
                if (authored != null && authored.isValid)
                {
                    VolumeManager.instance.DestroyStack(authored);
                    if (fixture?.SettingsData != null) fixture.SettingsData.volumeStack = null;
                }
                fixture?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator DisabledCameraRenderAdvancesTemporalHistoryOnSupportedDesktopApis()
        {
            RequireSupportedDesktopEditor();
            Fixture fixture = null;
            Texture2D temporal = null, control = null;
            try
            {
                fixture = new Fixture();
                fixture.Setup();
                yield return null; yield return null;
                fixture.DisableNormalRendering();
                fixture.ResetObservations();
                int firstVersion = -1, lastVersion = -1;
                for (int frame = 0; frame < 16; frame++)
                {
                    yield return null;
                    fixture.RenderManual();
                    var history = fixture.CameraData.history.GetHistoryForRead<TaaHistory>();
                    Assert.IsNotNull(history, "Manual Camera.Render did not create a TAA history");
                    lastVersion = history.GetAccumulationVersion();
                    if (firstVersion < 0) firstVersion = lastVersion;
                }
                Assert.Greater(lastVersion, firstVersion, "Manual Camera.Render did not advance TAA accumulation");
                fixture.AssertObservedTemporalJitter();
                temporal = fixture.Capture();
                AssertLitContent(temporal, "Manual TAA Camera");
                fixture.CameraData.antialiasing = AntialiasingMode.None;
                fixture.ResetObservations();
                for (int frame = 0; frame < 4; frame++) { yield return null; fixture.RenderManual(); }
                fixture.AssertObservedStableProjection();
                control = fixture.Capture();
                AssertImagesDiffer(temporal, control);
            }
            finally
            {
                if (temporal != null) Object.DestroyImmediate(temporal);
                if (control != null) Object.DestroyImmediate(control);
                fixture?.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator TemporalAntiAliasingUsesProjectionJitterAndChangesPixelsOnSupportedDesktopApis()
        {
            RequireSupportedDesktopEditor();
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
            RequireSupportedDesktopEditor();
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

        static void RequireSupportedDesktopEditor()
        {
            bool macMetal = Application.platform == RuntimePlatform.OSXEditor &&
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal;
            bool windowsD3D = Application.platform == RuntimePlatform.WindowsEditor &&
                (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11 ||
                 SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12);
            if (!macMetal && !windowsD3D)
                Assert.Ignore("This test requires macOS Metal or Windows Direct3D 11/12 in the Unity Editor.");
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
            public QuiltRenderer Quilt { get; private set; }
            public UniversalAdditionalCameraData SettingsData { get; private set; }
            public AsyncOperation PendingSceneUnload => sceneScope?.UnloadOperation;
            public void ConfigureRendererPair(bool sourceDeferred)
            {
                rendererData.renderingMode = sourceDeferred ? RenderingMode.Deferred : RenderingMode.Forward;
                rendererData.SetDirty();
                var referenceRenderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                referenceRenderer.renderingMode = sourceDeferred ? RenderingMode.Forward : RenderingMode.Deferred;
                referenceRenderer.postProcessData = rendererData.postProcessData; owned.Add(referenceRenderer);
                var serialized = new SerializedObject(pipeline);
                var renderers = serialized.FindProperty("m_RendererDataList"); Assert.IsNotNull(renderers);
                renderers.arraySize = 2; renderers.GetArrayElementAtIndex(1).objectReferenceValue = referenceRenderer;
                serialized.ApplyModifiedPropertiesWithoutUndo(); pipeline.msaaSampleCount = 4;
            }
            public void SetPipelineVolumeMode(VolumeFrameworkUpdateMode mode)
            {
                // Configure this fixture-owned asset through the public Editor serialization API.
                var serialized = new SerializedObject(pipeline);
                var property = serialized.FindProperty("m_VolumeFrameworkUpdateMode");
                Assert.IsNotNull(property); property.enumValueIndex = (int)mode; serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            public void StopObservations() { observer.TargetCamera = null; observer.ObserveChildrenOf = null; }
            public void SetupQuilt()
            {
                camera.enabled = false; camera.targetTexture = null;
                var referenceObject = new GameObject("ReferenceTaaCamera");
                referenceObject.transform.SetParent(root.transform, false);
                var reference = referenceObject.AddComponent<Camera>(); reference.CopyFrom(camera); reference.enabled = false;
                SettingsData = referenceObject.AddComponent<UniversalAdditionalCameraData>();
                SettingsData.renderPostProcessing = true; SettingsData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                SettingsData.volumeLayerMask = CameraData.volumeLayerMask;
                SettingsData.taaSettings.baseBlendFactor = 0.05f;
                Quilt = camera.gameObject.AddComponent<QuiltRenderer>(); Quilt.sourceCamera = camera;
                Quilt.referenceCamera = reference; Quilt.GuardBehaviour = Guard;
                Quilt.settings.quiltWidth = TargetWidth * 2; Quilt.settings.quiltHeight = TargetHeight * 2;
                Quilt.settings.columns = 2; Quilt.settings.rows = 2; Quilt.settings.views = 4;
                Quilt.settings.viewAspect = TargetWidth / (float)TargetHeight;
                Quilt.settings.fieldOfView = camera.fieldOfView; Quilt.settings.cameraDistance = 3f; Quilt.settings.viewCone = 25f;
                observer.ObserveChildrenOf = camera.transform;
                ResetObservations();
            }
            public Texture2D CaptureQuilt() => CaptureTexture(Quilt.QuiltTexture);
            public void AssertQuiltTemporalJitter() => AssertQuiltProjections(true);
            public void AssertQuiltStableProjection() => AssertQuiltProjections(false);
            void AssertQuiltProjections(bool temporal)
            {
                for (int view = 0; view < 4; view++)
                {
                    Camera id = Guard.GetHistoryCamera(view);
                    var samples = new List<ProjectionObservation>();
                    foreach (var observation in observer.Observations) if (observation.CameraId == id) samples.Add(observation);
                    Assert.GreaterOrEqual(samples.Count, temporal ? 8 : 2);
                    bool changed = false;
                    foreach (var sample in samples)
                    {
                        Assert.AreEqual(temporal ? AntialiasingMode.TemporalAntiAliasing : AntialiasingMode.None, sample.Mode);
                        if (MaxMatrixDifference(samples[0].Projection, sample.Projection) > 1e-6f) changed = true;
                    }
                    Assert.AreEqual(temporal, changed, "Unexpected temporal projection behavior in logical view " + view);
                }
            }

            public void DisableNormalRendering() { camera.enabled = false; }
            public void RenderManual()
            {
                Assert.IsFalse(camera.enabled);
                Assert.IsFalse(camera.isProcessingRenderRequest);
                camera.Render();
            }

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

            public Texture2D Capture() => CaptureTexture(target);
            static Texture2D CaptureTexture(RenderTexture target)
            {
                RenderTexture previous = RenderTexture.active;
                Texture2D image = null;
                try
                {
                    RenderTexture.active = target;
                    image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
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
                if (camera != null) camera.enabled = false;
                Quilt?.ReleasePipelineRenderResources();
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
                public readonly Camera CameraId;
                public readonly int Frame;
                public readonly AntialiasingMode Mode;
                public readonly Matrix4x4 Projection;

                public ProjectionObservation(Camera cameraId, int frame, AntialiasingMode mode, Matrix4x4 projection)
                {
                    CameraId = cameraId; Frame = frame;
                    Mode = mode;
                    Projection = projection;
                }
            }

            sealed class ProjectionObserverFeature : ScriptableRendererFeature
            {
                readonly List<ProjectionObservation> observations = new List<ProjectionObservation>();
                ProjectionObserverPass pass;

                public Camera TargetCamera { get; set; }
                public Transform ObserveChildrenOf { get; set; }
                bool Matches(Camera camera) => camera == TargetCamera || (ObserveChildrenOf != null && camera.transform.IsChildOf(ObserveChildrenOf));
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
                    if (pass != null && Matches(renderingData.cameraData.camera))
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
                        if (!owner.Matches(cameraData.camera))
                            return;
                        Assert.IsFalse(cameraData.camera.isProcessingRenderRequest,
                            "This TAA control must run through ordinary Camera.Render, not a render request");
                        owner.observations.Add(new ProjectionObservation(
                            cameraData.camera, Time.frameCount, cameraData.antialiasing, cameraData.GetProjectionMatrix()));
                    }
                }
            }
        }
    }
}
#endif
