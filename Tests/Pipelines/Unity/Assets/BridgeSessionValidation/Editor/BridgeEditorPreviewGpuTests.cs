using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.Editor;
using ViewWeave.HDRP;
using ViewWeave.URP;
using Object = UnityEngine.Object;

namespace ViewWeave.BridgeEditorPreviewValidation
{
    public sealed class BridgeEditorPreviewGpuTests
    {
        [Test]
        public void DisplayProfileSelectionRejectsUnavailableAndStalePreviewAndSessionSources()
        {
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(false, false, true, true, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, true, true, true, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, false, true, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, false, true, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, true, false, false));
            Assert.IsFalse(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, true, true, true));
            Assert.IsTrue(BridgeEditorPreview.IsPreviewProfileCurrent(true, false, true, true, true, false));

            BridgeDisplayProfile profile = MakeDisplayProfile(16f / 9f);
            var gameObject = new GameObject("Display profile selection test");
            QuiltRenderer renderer = gameObject.AddComponent<QuiltRenderer>();
            try
            {
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, false, profile, false, 1f));
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, default, false, 1f));
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, true, 1f));
                Assert.IsTrue(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1f));

                renderer.physicalFraming.enabled = true;
                renderer.physicalFraming.visibleApertureWidthMillimeters = 160f;
                renderer.physicalFraming.visibleApertureHeightMillimeters = 90f;
                Assert.IsTrue(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1f));
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1.01f));
                renderer.physicalFraming.visibleApertureWidthMillimeters = 150f;
                Assert.IsFalse(BridgeEditorPreview.IsDevicePreviewProfileCurrent(renderer, true, profile, false, 1f));
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }

            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(false, true, true, true, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, false, true, true, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, false, true, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, false, profile, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, default, true, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, profile, false, true));
            Assert.IsFalse(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, profile, true, false));
            Assert.IsTrue(ViewWeaveBridgeSession.IsEditorDisplayProfileCurrent(true, true, true, true, profile, true, true));
        }

        static BridgeDisplayProfile MakeDisplayProfile(float aspect)
        {
            int width = aspect > 1f ? 1920 : 1080;
            int height = aspect > 1f ? 1080 : 1920;
            return new BridgeDisplayProfile(new BridgeNative.DisplayInfo
            {
                index = 0,
                width = (uint)width,
                height = (uint)height,
                aspect = aspect,
                viewcone = 40f,
                quiltWidth = 4096,
                quiltHeight = 4096,
                quiltColumns = 8,
                quiltRows = 6
            });
        }

        [Test]
        public void AuthoringRefreshPreservesFocusFieldWhilePhysicalModeChanges()
        {
            var fixture = new Fixture(false);
            var root = new VisualElement();
            try
            {
                fixture.Setup();
                var ui = new ViewWeaveRigInspectorUI(root, () => fixture.Rig, () => null, _ => { });
                ui.Rebuild();
                FieldInfo field = typeof(ViewWeaveRigInspectorUI).GetField("focusDistanceField", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                PropertyField focusField = (PropertyField)field.GetValue(ui);
                Assert.NotNull(focusField);
                for (int i = 0; i < 3; ++i) ui.Refresh();
                Assert.AreSame(focusField, field.GetValue(ui), "Periodic refresh must retain the raw focus PropertyField instance.");

                fixture.Rig.Renderer.physicalFraming.enabled = true;
                fixture.Rig.Renderer.physicalFraming.visibleApertureWidthMillimeters = 160f;
                fixture.Rig.Renderer.physicalFraming.visibleApertureHeightMillimeters = 90f;
                ui.Refresh();
                Assert.AreSame(focusField, field.GetValue(ui), "Switching physical framing must not rebuild the focus field.");
            }
            finally
            {
                root.Clear();
                fixture.Dispose();
            }
        }

        [Test]
        public void EditorPreviewRenderBoundaryRequiresExactOwnerAndRejectsActiveSrpCallbacksAndCompletionReentry()
        {
            var producerObject = new GameObject("Editor preview callback boundary test");
            QuiltRenderer renderer = producerObject.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producerObject, renderer);
            BridgeEditorPreviewRetentionRecord wrongOwner = ScriptableObject.CreateInstance<BridgeEditorPreviewRetentionRecord>();
            FieldInfo retentionRequired = typeof(QuiltRenderer).GetField("editorPreviewRetentionRequired", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo completionCallback = typeof(QuiltRenderer).GetField("notifyingRenderComplete", BindingFlags.Instance | BindingFlags.NonPublic);

            try
            {
                Assert.IsTrue(QuiltRenderer.EditorPreviewRenderCallbackTrackingReady);
                Assert.IsTrue(renderer.BeginEditorPreviewRetention(owner));

                InvalidOperationException ownerError = Assert.Throws<InvalidOperationException>(
                    () => renderer.RenderEditorPreviewAtBoundary(wrongOwner));
                StringAssert.Contains("serialized retention owner", ownerError.Message);

                ScriptableRenderContext context = default;
                var cameras = new System.Collections.Generic.List<Camera>();
                QuiltRendererEditorCallbackTracker.OnBeginContextRendering(context, cameras);
                QuiltRendererEditorCallbackTracker.OnBeginCameraRendering(context, null);
                try
                {
                    Assert.IsTrue(QuiltRenderer.EditorPreviewRenderCallbackActive);
                    InvalidOperationException callbackError = Assert.Throws<InvalidOperationException>(
                        () => renderer.RenderEditorPreviewAtBoundary(owner));
                    StringAssert.Contains("outside SRP render callbacks", callbackError.Message);
                }
                finally
                {
                    QuiltRendererEditorCallbackTracker.OnEndCameraRendering(context, null);
                    QuiltRendererEditorCallbackTracker.OnEndContextRendering(context, cameras);
                }

                Assert.IsFalse(QuiltRenderer.EditorPreviewRenderCallbackActive);
                completionCallback.SetValue(renderer, true);
                InvalidOperationException reentryError = Assert.Throws<InvalidOperationException>(
                    () => renderer.RenderEditorPreviewAtBoundary(owner));
                StringAssert.Contains("quilt completion callback", reentryError.Message);
            }
            finally
            {
                completionCallback.SetValue(renderer, false);
                retentionRequired.SetValue(renderer, false);
                if (wrongOwner != null) Object.DestroyImmediate(wrongOwner);
                if (owner != null) Object.DestroyImmediate(owner);
                if (producerObject != null) Object.DestroyImmediate(producerObject);
            }
        }

        [UnityTest]
        public IEnumerator UrpEditorPreviewRendersAndRebuildsItsOwnedTextures()
        {
            return RunLocalPreview(false);
        }

        [UnityTest]
        public IEnumerator HdrpEditorPreviewRendersAndRebuildsItsOwnedTextures()
        {
            return RunLocalPreview(true);
        }

        [UnityTest]
        public IEnumerator RigAuthoringWindowRetargetAndCloseStopLocalPreview()
        {
            var fixture = new Fixture(false);
            ViewWeaveRig secondaryRig = null;
            ViewWeaveRigAuthoringWindow window = null;
            bool previewAttempted = false;
            try
            {
                fixture.Setup();
                secondaryRig = CreateSecondaryRig("Editor Preview Retarget Rig");
                previewAttempted = true;
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("The first local preview did not start: " + startError);
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "The first local preview did not produce a texture before window retarget.");

                window = ViewWeaveRigAuthoringWindow.OpenForRig(fixture.Rig);
                window = ViewWeaveRigAuthoringWindow.OpenForRig(secondaryRig);
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0,
                    "Changing the authoring window Rig did not stop and prove the previous preview.");
                Debug.Log("EDITOR_PREVIEW_WINDOW retarget active=false pending=0");

                if (!PreviewApi.Start(secondaryRig, false, out startError))
                    throw new AssertionException("The retargeted local preview did not start: " + startError);
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "The retargeted local preview did not produce a texture before window close.");
                window.Close();
                window = null;
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0,
                    "Closing the authoring window did not stop and prove its preview.");
                Debug.Log("EDITOR_PREVIEW_WINDOW close active=false pending=0");
            }
            finally
            {
                if (window != null) window.Close();
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                if (secondaryRig != null) Object.DestroyImmediate(secondaryRig.gameObject);
                fixture.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator LocalPreviewStopsBeforePlayModeTransition()
        {
            var fixture = new Fixture(false);
            bool previewAttempted = false;
            bool crossedPlayMode = false;
            try
            {
                fixture.Setup();
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("The local preview did not start before Play transition: " + startError);
                previewAttempted = true;
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "The local preview did not produce a texture before Play transition.");

                yield return new EnterPlayMode();
                crossedPlayMode = true;
                Assert.IsTrue(Application.isPlaying, "Unity must enter Play mode for the transition check.");
                Assert.IsFalse(PreviewApi.IsActive, "The Edit Mode preview must stop before Play mode begins.");
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount,
                    "The preview must finish retention proof before the Play mode transition completes.");
                Debug.Log("EDITOR_PREVIEW_PLAY_TRANSITION play=true active=false pending=0");

                yield return new ExitPlayMode();
                yield return null;
                Assert.IsFalse(Application.isPlaying, "The test must return Unity to Edit mode.");
                Assert.IsFalse(PreviewApi.IsActive, "Returning to Edit mode must not silently restart the preview.");
                Debug.Log("EDITOR_PREVIEW_PLAY_TRANSITION play=false active=false");
            }
            finally
            {
                if (previewAttempted && !Application.isPlaying &&
                    (PreviewApi.IsActive || PreviewApi.IsStopping || BridgeEditorPreviewRetention.PendingCount != 0))
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                if (!crossedPlayMode) fixture.Dispose();
                else Debug.Log("EDITOR_PREVIEW_PLAY_TRANSITION fixture_disposal_skipped_after_domain_reload=true");
            }
        }

        [UnityTest]
        public IEnumerator DeviceEditorPreviewStartsStopsAndRestartsWhenExplicitlyEnabled()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST=1 only when a Looking Glass Bridge and device are available.");
            Assert.IsTrue(BridgeGraphicsBackendPolicy.TrySelect(
                BridgeGraphicsBackendRoute.EditorPreview,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                SystemInfo.graphicsDeviceType,
                SystemInfo.renderingThreadingMode,
                out BridgeGraphicsBackendDecision backend,
                out string backendError), backendError);
            string initialPipelineName = GraphicsSettings.currentRenderPipeline == null
                ? "BuiltIn"
                : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            Debug.Log("EDITOR_PREVIEW_DEVICE environment unity=" + Application.unityVersion +
                ", graphics=" + SystemInfo.graphicsDeviceType +
                ", initialPipeline=" + initialPipelineName +
                ", route=" + backend.Route +
                ", unityProducer=" + backend.UnityProducer +
                ", bridgeConsumer=" + backend.BridgeConsumer);
            bool useHdrp = string.Equals(Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_PIPELINE"), "HDRP", StringComparison.OrdinalIgnoreCase);
            var fixture = new Fixture(useHdrp);
            bool previewAttempted = false;
            try
            {
                fixture.Setup();
                Debug.Log("EDITOR_PREVIEW_DEVICE fixturePipeline=" +
                    (GraphicsSettings.currentRenderPipeline == null ? "BuiltIn" : GraphicsSettings.currentRenderPipeline.GetType().FullName));
                fixture.Rig.gameObject.SetActive(false);
                var sourceOutput = fixture.Rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
                sourceOutput.initializeOnEnable = false;
                fixture.Rig.SetOutput(sourceOutput);
                if (!fixture.Rig.ConfigureComponents()) throw new AssertionException("The device test source Rig could not configure its Bridge output.");
                fixture.Rig.gameObject.SetActive(true);
                previewAttempted = true;
                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("first");
                StopDevicePreview("first");
                yield return WaitForDeviceStop("first");

                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("restart");
                StopDevicePreview("restart");
                yield return WaitForDeviceStop("restart");
            }
            finally
            {
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                fixture.Dispose();
            }
        }

        static void StartDevicePreview(ViewWeaveRig rig)
        {
            if (!PreviewApi.Start(rig, true, out string error))
                throw new AssertionException("Looking Glass Edit Mode preview failed to start: " + error);
            Debug.Log("EDITOR_PREVIEW_DEVICE start accepted");
        }

        static IEnumerator WaitForDeviceWorker(string label)
        {
            BridgeWorkerQuiltOutput output = null;
            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + 45.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                output = PreviewApi.ProducerOutput;
                if (output != null && output.Ready && output.Profile.IsValid && output.Submissions > 0 && output.Completed > 0)
                {
                    Debug.Log("EDITOR_PREVIEW_DEVICE ready phase=" + label + ", profile=" + output.Profile +
                        ", submissions=" + output.Submissions + ", completed=" + output.Completed);
                    yield break;
                }
                if (output != null && (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || !string.IsNullOrEmpty(output.LastError)))
                    Assert.Fail("Looking Glass Worker failed during " + label + " preview: " + output.LastError);
                yield return null;
            }
            Assert.Fail("Looking Glass Worker did not report Ready and completed submission during " + label +
                " preview. elapsedSeconds=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
        }

        static void StopDevicePreview(string label)
        {
            Assert.IsTrue(PreviewApi.Stop(), "The device preview Stop request should be accepted.");
            Debug.Log("EDITOR_PREVIEW_DEVICE stop_requested phase=" + label);
        }

        static IEnumerator WaitForDeviceStop(string label)
        {
            double startedAt = EditorApplication.timeSinceStartup;
            double deadline = startedAt + 30.0;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                if (!PreviewApi.IsActive && !PreviewApi.IsStopping && BridgeEditorPreviewRetention.PendingCount == 0 &&
                    BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
                {
                    Debug.Log("EDITOR_PREVIEW_DEVICE stopped phase=" + label + ", active=false, pending=0, elapsedSeconds=" +
                        (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                    yield break;
                }
                yield return null;
            }
            Assert.Fail("Device preview did not complete Worker handoff and GPU proof during " + label + " Stop. active=" +
                PreviewApi.IsActive + ", stopping=" + PreviewApi.IsStopping + ", retentionPending=" +
                BridgeEditorPreviewRetention.PendingCount + ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                ", elapsedSeconds=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                ", records=" + PreviewApi.DescribeRetentionRecords());
        }

        static IEnumerator RunLocalPreview(bool hdrp)
        {
            var fixture = new Fixture(hdrp);
            bool previewAttempted = false;
            try
            {
                fixture.Setup();
                QuiltRenderer sourceRenderer = fixture.Rig.Renderer;
                float storedAspect = sourceRenderer.settings.viewAspect;
                float storedDistance = sourceRenderer.settings.cameraDistance;
                sourceRenderer.physicalFraming.enabled = true;
                sourceRenderer.physicalFraming.visibleApertureWidthMillimeters = 800f;
                sourceRenderer.physicalFraming.visibleApertureHeightMillimeters = 450f;
                sourceRenderer.physicalFraming.unitsPerMeter = 1f;
                sourceRenderer.settings.fieldOfView = 60f;
                fixture.RenderSourceControl();
                AssertPhysicalSnapshot(sourceRenderer.LatestSnapshot, 800f / 450f, 60f, 0.45f,
                    "The source renderer should use the configured physical aperture without mutating its base settings.");
                Assert.AreEqual(storedAspect, sourceRenderer.settings.viewAspect, 0.0001f);
                Assert.AreEqual(storedDistance, sourceRenderer.settings.cameraDistance, 0.0001f);
                previewAttempted = true;
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("Local Editor preview did not start: " + startError);

                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture),
                    "Local Editor preview did not produce a created quilt texture.");
                RenderTexture first = PreviewApi.Texture;
                QuiltRenderer producerRenderer = PreviewApi.ProducerRenderer;
                Assert.IsNotNull(producerRenderer);
                Assert.IsTrue(QuiltDisplaySettingsResolver.TryResolve(producerRenderer, out QuiltSettings gizmoSettings,
                    out QuiltEditorDisplayProfile displayProfile, out string framingError), framingError);
                Assert.AreEqual(QuiltDisplaySettingsSource.LocalPreview, displayProfile.source);
                AssertPhysicalSnapshot(producerRenderer.LatestSnapshot, 800f / 450f, 60f, 0.45f,
                    "The hidden preview producer should copy and render the physical framing configuration.");
                Assert.AreEqual(gizmoSettings.viewAspect, producerRenderer.LatestSnapshot.Geometry.viewAspect, 0.0001f,
                    "The gizmo resolver and preview producer should use the same effective physical aspect.");
                Assert.AreEqual(gizmoSettings.cameraDistance, producerRenderer.LatestSnapshot.Geometry.cameraDistance, 0.0001f,
                    "The gizmo resolver and preview producer should use the same effective physical distance.");
                Assert.AreEqual(storedAspect, sourceRenderer.settings.viewAspect, 0.0001f,
                    "Physical rendering must not write the derived aspect into the source profile.");
                Assert.AreEqual(storedDistance, sourceRenderer.settings.cameraDistance, 0.0001f,
                    "Physical rendering must not write the derived distance into the source profile.");
                long physicalRevision = producerRenderer.LatestSnapshot.Revision;
                sourceRenderer.settings.fieldOfView = 45f;
                BridgeEditorPreview.RequestRefresh();
                yield return WaitFor(() => producerRenderer != null && producerRenderer.LatestSnapshot.IsValid &&
                    producerRenderer.LatestSnapshot.Revision > physicalRevision &&
                    Mathf.Abs(producerRenderer.LatestSnapshot.Geometry.fieldOfView - 45f) < 0.001f,
                    "The preview did not apply a changed field of view to physical framing.");
                AssertPhysicalSnapshot(producerRenderer.LatestSnapshot, 800f / 450f, 45f, 0.45f,
                    "Changing field of view should move the camera while keeping the physical plane height fixed.");
                Assert.AreEqual(storedAspect, sourceRenderer.settings.viewAspect, 0.0001f);
                Assert.AreEqual(storedDistance, sourceRenderer.settings.cameraDistance, 0.0001f);
                Debug.Log("EDITOR_PREVIEW_GPU cameras=" + PreviewApi.DescribeCameras(fixture.Rig.Renderer.sourceCamera, fixture.TargetObject));
                Assert.IsTrue(producerRenderer.HasRenderedQuilt);
                Assert.AreEqual(8, producerRenderer.LastRenderedViewCount);
                Assert.AreEqual(128, first.width);
                Assert.AreEqual(64, first.height);
                Assert.AreEqual(fixture.ExpectedViewSamples, producerRenderer.ViewAntiAliasingSamples,
                    "The preview view texture should follow the supported URP MSAA sample count.");
                Assert.AreEqual(fixture.ExpectedViewSamples > 1 ? 1 : 0, producerRenderer.ResolveAntiAliasingSamples,
                    "The preview resolve texture should be single-sampled when MSAA is active.");
                Debug.Log("EDITOR_PREVIEW_GPU render pipeline=" + (hdrp ? "HDRP" : "URP") + ", views=" + producerRenderer.LastRenderedViewCount +
                    ", samplesView=" + producerRenderer.ViewAntiAliasingSamples + ", samplesResolve=" + producerRenderer.ResolveAntiAliasingSamples);
                bool hasGreenTargetPixels = HasGreenTargetPixels(first, out string pixelSummary);
                Debug.Log("EDITOR_PREVIEW_GPU pixels=" + pixelSummary);
                Assert.IsTrue(hasGreenTargetPixels, "The quilt should contain green-dominant pixels from the unlit preview target. " + pixelSummary);

                fixture.Rig.ApplyProfile(new QuiltQualityProfile(256, 128, 4, 2, 8, 1f, 20f));
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture) &&
                    !ReferenceEquals(PreviewApi.Texture, first) && PreviewApi.Texture.width == 256,
                    "The preview did not stop, prove, and rebuild after a profile layout change.");
                Assert.IsFalse(first != null && first.IsCreated(), "The old preview texture must retire only after proof.");
                Assert.IsTrue(PreviewApi.ProducerRenderer.HasRenderedQuilt);
                Assert.AreEqual(8, PreviewApi.ProducerRenderer.LastRenderedViewCount);
                Assert.AreEqual(fixture.ExpectedViewSamples, PreviewApi.ProducerRenderer.ViewAntiAliasingSamples);
                Assert.AreEqual(fixture.ExpectedViewSamples > 1 ? 1 : 0, PreviewApi.ProducerRenderer.ResolveAntiAliasingSamples);
                Debug.Log("EDITOR_PREVIEW_GPU rebuild pipeline=" + (hdrp ? "HDRP" : "URP") + ", oldTextureRetired=true, newWidth=" + PreviewApi.Texture.width +
                    ", samplesView=" + PreviewApi.ProducerRenderer.ViewAntiAliasingSamples + ", samplesResolve=" + PreviewApi.ProducerRenderer.ResolveAntiAliasingSamples);

                PreviewApi.Stop();
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0,
                    "Editor preview resources did not complete stop and GPU proof.");
                Assert.IsTrue(BridgeEditorPreviewRetention.RequestStop(true));
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount);
                Debug.Log("EDITOR_PREVIEW_GPU stop pipeline=" + (hdrp ? "HDRP" : "URP") + ", active=false, pending=0");
            }
            finally
            {
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                fixture.Dispose();
            }
        }

        static void AssertPhysicalSnapshot(QuiltRenderSnapshot snapshot, float expectedAspect, float fieldOfView,
            double expectedPlaneHeight, string message)
        {
            Assert.IsTrue(snapshot.IsValid, message);
            Assert.IsTrue(snapshot.Geometry.physicalFramingEnabled, message);
            Assert.AreEqual(expectedAspect, snapshot.Geometry.viewAspect, 0.0001f, message);
            Assert.AreEqual(fieldOfView, snapshot.Geometry.fieldOfView, 0.001f, message);
            Assert.AreEqual(800f, snapshot.PhysicalFraming.visibleApertureWidthMillimeters, 0.001f, message);
            Assert.AreEqual(450f, snapshot.PhysicalFraming.visibleApertureHeightMillimeters, 0.001f, message);
            float planeHeight = 2f * snapshot.Geometry.cameraDistance * Mathf.Tan(snapshot.Geometry.fieldOfView * Mathf.Deg2Rad * 0.5f);
            Assert.AreEqual(expectedPlaneHeight, planeHeight, 0.001f, message);
        }

        static IEnumerator WaitFor(Func<bool> predicate, string failureMessage)
        {
            for (int frame = 0; frame < 180; frame++)
            {
                if (predicate()) yield break;
                yield return null;
            }
            Assert.Fail(failureMessage);
        }

        static bool IsCreated(RenderTexture texture) => texture != null && texture.IsCreated();

        static ViewWeaveRig CreateSecondaryRig(string name)
        {
            var rigObject = new GameObject(name);
            rigObject.SetActive(false);
            Camera camera = rigObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.allowMSAA = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            var renderer = rigObject.AddComponent<QuiltRenderer>();
            var rig = rigObject.AddComponent<ViewWeaveRig>();
            rig.ApplyProfile(new QuiltQualityProfile(128, 64, 4, 2, 8, 1f, 20f));
            renderer.settings.cameraDistance = 3f;
            renderer.settings.fieldOfView = 60f;
            if (GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)
            {
                var cameraData = rigObject.AddComponent<HDAdditionalCameraData>();
                cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = Color.black;
                rig.SetGuard(rigObject.AddComponent<HdrpQuiltCamera>());
            }
            else if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
            {
                var cameraData = rigObject.AddComponent<UniversalAdditionalCameraData>();
                if (cameraData.renderType != CameraRenderType.Base) cameraData.renderType = CameraRenderType.Base;
                rig.SetGuard(rigObject.AddComponent<UrpQuiltCamera>());
            }
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("The secondary preview Rig could not configure.");
            rigObject.SetActive(true);
            return rig;
        }

        static bool HasGreenTargetPixels(RenderTexture source, out string diagnostic)
        {
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                image.Apply(false, false);
                Color32[] pixels = image.GetPixels32();
                int greenPixels = 0;
                int nonBlackPixels = 0;
                byte maxRed = 0;
                byte maxGreen = 0;
                byte maxBlue = 0;
                foreach (Color32 pixel in pixels)
                {
                    if (pixel.r > maxRed) maxRed = pixel.r;
                    if (pixel.g > maxGreen) maxGreen = pixel.g;
                    if (pixel.b > maxBlue) maxBlue = pixel.b;
                    if (pixel.r > 32 || pixel.g > 32 || pixel.b > 32) nonBlackPixels++;
                    if (pixel.g > 32 && pixel.g > pixel.r * 2 && pixel.g > pixel.b * 2) greenPixels++;
                }
                diagnostic = "greenDominant=" + greenPixels + ", nonBlack=" + nonBlackPixels + ", maxRGB=" + maxRed + "/" + maxGreen + "/" + maxBlue;
                return greenPixels > 0;
            }
            finally
            {
                RenderTexture.active = previous;
                Object.DestroyImmediate(image);
            }
        }

        sealed class Fixture
        {
            readonly bool hdrp;
            RenderPipelineAsset previousDefault;
            RenderPipelineAsset previousQuality;
            RenderPipelineAsset pipeline;
            UniversalRendererData urpRendererData;
            UniversalRenderPipelineAsset urpPipeline;
            GameObject rigObject;
            GameObject targetObject;
            Material targetMaterial;
            ViewWeaveRig rig;

            internal ViewWeaveRig Rig => rig;
            internal GameObject TargetObject => targetObject;
            internal int ExpectedViewSamples { get; private set; } = 1;

            internal Fixture(bool hdrp) => this.hdrp = hdrp;

            internal void Setup()
            {
                previousDefault = GraphicsSettings.defaultRenderPipeline;
                previousQuality = QualitySettings.renderPipeline;
                if (hdrp)
                {
                    pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                }
                else
                {
                    urpRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                    urpPipeline = UniversalRenderPipelineAsset.Create(urpRendererData);
                    urpPipeline.msaaSampleCount = 4;
                    pipeline = urpPipeline;
                }
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;

                rigObject = new GameObject(hdrp ? "HDRP Editor Preview Rig" : "URP Editor Preview Rig");
                rigObject.SetActive(false);
                Camera sourceCamera = rigObject.AddComponent<Camera>();
                sourceCamera.clearFlags = CameraClearFlags.SolidColor;
                sourceCamera.allowMSAA = !hdrp;
                sourceCamera.backgroundColor = Color.black;
                sourceCamera.transform.position = Vector3.zero;
                sourceCamera.transform.rotation = Quaternion.identity;
                QuiltRenderer renderer = rigObject.AddComponent<QuiltRenderer>();
                rig = rigObject.AddComponent<ViewWeaveRig>();
                rig.ApplyProfile(new QuiltQualityProfile(128, 64, 4, 2, 8, 1f, 20f));
                renderer.settings.cameraDistance = 3f;
                renderer.settings.fieldOfView = 60f;
                if (hdrp)
                {
                    var cameraData = rigObject.AddComponent<HDAdditionalCameraData>();
                    cameraData.volumeLayerMask = 1;
                    cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    cameraData.backgroundColorHDR = Color.black;
                    rig.SetGuard(rigObject.AddComponent<HdrpQuiltCamera>());
                }
                else
                {
                    UniversalAdditionalCameraData cameraData = rigObject.AddComponent<UniversalAdditionalCameraData>();
                    if (cameraData.renderType != CameraRenderType.Base) cameraData.renderType = CameraRenderType.Base;
                    rig.SetGuard(rigObject.AddComponent<UrpQuiltCamera>());
                    var descriptor = new RenderTextureDescriptor(32, 32, RenderTextureFormat.ARGB32, 24) { msaaSamples = 4 };
                    int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
                    ExpectedViewSamples = supported == 2 || supported == 4 || supported == 8 ? supported : 1;
                    if (ExpectedViewSamples != 4)
                        TestContext.Progress.WriteLine("URP MSAA request=4 actual supported samples=" + ExpectedViewSamples);
                }
                if (!rig.ConfigureComponents()) throw new InvalidOperationException("Source Rig configuration failed.");

                targetObject = GameObject.CreatePrimitive(PrimitiveType.Quad);
                targetObject.name = "Editor Preview GPU target";
                targetObject.transform.position = new Vector3(0f, 0f, 2f);
                targetObject.transform.rotation = Quaternion.identity;
                Shader shader = Shader.Find(hdrp ? "HDRP/Unlit" : "Universal Render Pipeline/Unlit");
                if (shader == null) throw new InvalidOperationException("The pipeline unlit shader is unavailable.");
                targetMaterial = new Material(shader);
                if (targetMaterial.HasProperty("_BaseColor")) targetMaterial.SetColor("_BaseColor", Color.green);
                else if (targetMaterial.HasProperty("_UnlitColor")) targetMaterial.SetColor("_UnlitColor", Color.green);
                else if (targetMaterial.HasProperty("_Color")) targetMaterial.SetColor("_Color", Color.green);
                else throw new InvalidOperationException("The pipeline unlit shader has no supported color property.");
                targetObject.GetComponent<MeshRenderer>().sharedMaterial = targetMaterial;
                rigObject.SetActive(true);
            }

            internal void RenderSourceControl()
            {
                QuiltRenderer sourceRenderer = rig.Renderer;
                sourceRenderer.useRenderRequests = true;
                sourceRenderer.RenderQuilt();
                string summary;
                bool visible = HasGreenTargetPixels(sourceRenderer.QuiltTexture, out summary);
                Debug.Log("EDITOR_PREVIEW_GPU source_control pipeline=" + (hdrp ? "HDRP" : "URP") + ", " + summary +
                    ", camera=" + PreviewApi.DescribeCamera("source", sourceRenderer.sourceCamera, targetObject));
                Assert.IsTrue(visible, "The source Rig control should render green target pixels. " + summary);
            }

            internal void Dispose()
            {
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                if (targetObject != null) Object.DestroyImmediate(targetObject);
                if (targetMaterial != null) Object.DestroyImmediate(targetMaterial);
                if (rigObject != null) Object.DestroyImmediate(rigObject);
                if (pipeline != null) Object.DestroyImmediate(pipeline);
                if (urpRendererData != null) Object.DestroyImmediate(urpRendererData);
            }
        }

        static class PreviewApi
        {
            static readonly Type Service = Type.GetType("ViewWeave.Editor.BridgeEditorPreview, ViewWeave.Bridge.Editor", true);
            static readonly PropertyInfo ActiveProperty = Service.GetProperty("IsActive", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo StoppingProperty = Service.GetProperty("IsStopping", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo TextureProperty = Service.GetProperty("Texture", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerRendererField = Service.GetField("producerRenderer", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerOutputField = Service.GetField("producerOutput", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerCameraField = Service.GetField("producerCamera", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo StartMethod = Service.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo StopMethod = Service.GetMethod("Stop", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly Type RetentionRecord = Type.GetType("ViewWeave.BridgeEditorPreviewRetentionRecord, ViewWeave.Bridge", true);

            internal static bool IsActive => (bool)ActiveProperty.GetValue(null);
            internal static bool IsStopping => (bool)StoppingProperty.GetValue(null);
            internal static RenderTexture Texture => (RenderTexture)TextureProperty.GetValue(null);
            internal static QuiltRenderer ProducerRenderer => (QuiltRenderer)ProducerRendererField.GetValue(null);
            internal static BridgeWorkerQuiltOutput ProducerOutput => (BridgeWorkerQuiltOutput)ProducerOutputField.GetValue(null);
            internal static string DescribeCameras(Camera sourceCamera, GameObject targetObject)
            {
                Camera producer = (Camera)ProducerCameraField.GetValue(null);
                ulong targetSceneMask = targetObject == null ? 0 : EditorSceneManager.GetSceneCullingMask(targetObject.scene);
                return DescribeCamera("source", sourceCamera, targetObject) + "; " + DescribeCamera("producer", producer, targetObject) +
                    "; target.scene=" + (targetObject == null ? "null" : targetObject.scene.name) + ", target.sceneMask=" + targetSceneMask +
                    ", target.layer=" + (targetObject == null ? -1 : targetObject.layer);
            }

            internal static string DescribeCamera(string label, Camera camera, GameObject targetObject)
            {
                if (camera == null) return label + "=null";
                ulong sceneMask = EditorSceneManager.GetSceneCullingMask(camera.scene);
                return label + ".scene=" + camera.scene.name + ", sceneMask=" + sceneMask +
                    ", overrideMask=" + camera.overrideSceneCullingMask + ", cameraMask=" + camera.cullingMask +
                    ", targetVisibleByLayer=" + (targetObject != null && (camera.cullingMask & (1 << targetObject.layer)) != 0);
            }

            internal static bool Start(ViewWeaveRig rig, bool outputToDevice, out string error)
            {
                object[] arguments = { rig, outputToDevice, null };
                bool result = (bool)StartMethod.Invoke(null, arguments);
                error = arguments[2] as string;
                return result;
            }

            internal static bool Stop() => (bool)StopMethod.Invoke(null, null);

            internal static string DescribeRetentionRecords()
            {
                var records = Resources.FindObjectsOfTypeAll(RetentionRecord);
                var summaries = new string[records.Length];
                for (int i = 0; i < records.Length; i++)
                {
                    object record = records.GetValue(i);
                    Type type = record.GetType();
                    summaries[i] = "stop=" + GetRecordProperty(type, record, "StopRequested") +
                        ",detached=" + GetRecordProperty(type, record, "Detached") +
                        ",canRelease=" + GetRecordProperty(type, record, "CanRelease") +
                        ",handoff=" + GetRecordProperty(type, record, "WorkerHandoffComplete") +
                        ",workerComplete=" + GetRecordProperty(type, record, "WorkerRecoveryComplete") +
                        ",gpu=" + GetRecordProperty(type, record, "GpuProofComplete") +
                        ",diagnostic=" + GetRecordProperty(type, record, "Diagnostic") +
                        ",recovery=" + DescribeRecoveryRecord(type, record);
                }
                return string.Join("|", summaries);
            }

            static string DescribeRecoveryRecord(Type ownerType, object owner)
            {
                object recovery = GetRecordProperty(ownerType, owner, "WorkerRecoveryRecord");
                if (recovery == null) return "null";
                Type type = recovery.GetType();
                object handle = GetRecordProperty(type, recovery, "NativeHandle");
                string nativePoll = "not-polled";
                try
                {
                    long handleValue = Convert.ToInt64(handle);
                    int result = BridgeWorkerNative.lkg_worker_poll(new IntPtr(handleValue), out BridgeWorkerNative.Status status);
                    nativePoll = "result=" + result + ",state=" + status.state + ",errorCode=" + status.lastErrorCode +
                        ",error=" + (status.error ?? string.Empty);
                }
                catch (Exception error) { nativePoll = "exception=" + error.GetType().Name + ":" + error.Message; }
                return "generation=" + GetRecordProperty(type, recovery, "Generation") +
                    ",handle=" + handle +
                    ",nativeReleased=" + GetRecordProperty(type, recovery, "NativeReleased") +
                    ",needsClose=" + GetRecordProperty(type, recovery, "NeedsClose") +
                    ",canRelease=" + GetRecordProperty(type, recovery, "CanRelease") +
                    ",release=" + (Equals(GetRecordProperty(type, recovery, "NativeReleased"), true) ? "called" : "not-called") +
                    ",poll={" + nativePoll + "}" +
                    ",diagnostic=" + GetRecordProperty(type, recovery, "Diagnostic");
            }

            static object GetRecordProperty(Type type, object instance, string propertyName)
            {
                PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic);
                return property == null ? "missing" : property.GetValue(instance);
            }
        }
    }
}
