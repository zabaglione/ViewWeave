using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
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
        const string DevicePlayTransitionRigName = "ViewWeave_DevicePreviewPlayTransition_Rig";
        const string DevicePlayTransitionTargetName = "ViewWeave_DevicePreviewPlayTransition_Target";
        const string DevicePlayTransitionPipelineName = "ViewWeave_DevicePreviewPlayTransition_Pipeline";
        const string DevicePlayTransitionRendererDataName = "ViewWeave_DevicePreviewPlayTransition_RendererData";
        const string DevicePlayTransitionStatePrefix = "ViewWeave.EditorPreview.DevicePlayTransition.";
        const string DevicePlayTransitionAssetFolderPrefix = "Assets/__ViewWeaveDevicePlayTransition_";

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
                FieldInfo field = typeof(ViewWeaveRigInspectorUI).GetField("focusDistanceInput", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(field);
                FloatField focusField = (FloatField)field.GetValue(ui);
                Assert.NotNull(focusField);
                for (int i = 0; i < 3; ++i) ui.Refresh();
                Assert.AreSame(focusField, field.GetValue(ui), "Periodic refresh must retain the focus input instance.");

                fixture.Rig.Renderer.physicalFraming.enabled = true;
                fixture.Rig.Renderer.physicalFraming.visibleApertureWidthMillimeters = 160f;
                fixture.Rig.Renderer.physicalFraming.visibleApertureHeightMillimeters = 90f;
                ui.Refresh();
                Assert.AreSame(focusField, field.GetValue(ui), "Switching physical framing must not rebuild the focus field.");
                Assert.AreEqual(DisplayStyle.None, focusField.style.display.value, "Stored base focus distance should be hidden while physical framing is enabled.");
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
        public IEnumerator UrpEditorPreviewRendersAndRejectsProfileMutationWhileOwned()
        {
            return RunLocalPreview(false);
        }

        [UnityTest]
        public IEnumerator HdrpEditorPreviewRendersAndRejectsProfileMutationWhileOwned()
        {
            return RunLocalPreview(true);
        }

        [UnityTest]
        public IEnumerator UrpViewRenderDensityChangesAtFrameBoundaryAndPreservesLogicalQuiltResources()
        {
            var fixture = new Fixture(false);
            bool previewAttempted = false;
            bool callbackMutationAttempted = false;
            bool callbackMutationAccepted = true;
            string callbackMutationError = string.Empty;
            QuiltRenderer producerRenderer = null;
            Action<QuiltRenderer> completionCallback = renderer =>
            {
                if (callbackMutationAttempted) return;
                callbackMutationAttempted = true;
                callbackMutationAccepted = renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out callbackMutationError);
            };
            try
            {
                fixture.Setup();
                QuiltRenderer sourceRenderer = fixture.Rig.Renderer;
                previewAttempted = true;
                if (!PreviewApi.Start(fixture.Rig, false, out string startError))
                    throw new AssertionException("The URP local preview did not start: " + startError);
                producerRenderer = PreviewApi.ProducerRenderer;
                Assert.IsNotNull(producerRenderer, "The preview producer should be constructed before its first frame.");
                producerRenderer.QuiltRendered += completionCallback;
                yield return WaitFor(() => PreviewApi.IsActive && IsCreated(PreviewApi.Texture) &&
                    callbackMutationAttempted && producerRenderer.HasRenderedQuilt,
                    "The initial Full preview frame and its completion callback did not run.");

                Assert.IsFalse(callbackMutationAccepted, "Sampling changes must be rejected during completion notification.");
                StringAssert.Contains("completion notification", callbackMutationError);
                QuiltRenderSnapshot initialSnapshot = producerRenderer.LatestSnapshot;
                Assert.AreEqual(QuiltViewRenderMode.Full, initialSnapshot.ViewRenderMode);
                Assert.AreEqual(8, initialSnapshot.LastRenderedViewCount);
                Assert.AreEqual(8, initialSnapshot.Layout.ViewCount);
                Assert.AreEqual(4, initialSnapshot.Layout.Columns);
                Assert.AreEqual(2, initialSnapshot.Layout.Rows);
                RenderTexture quiltTexture = PreviewApi.Texture;
                RenderTexture viewTexture = PreviewApi.ViewTexture;
                Assert.IsNotNull(viewTexture);

                QuiltViewRenderMode[] modes = { QuiltViewRenderMode.Half, QuiltViewRenderMode.Quarter };
                int[] expectedCounts = { 4, 2 };
                for (int i = 0; i < modes.Length; ++i)
                {
                    Assert.IsTrue(sourceRenderer.TrySetViewRenderMode(modes[i], out string modeError), modeError);
                    long previousRevision = producerRenderer.LatestSnapshot.Revision;
                    PreviewApi.RequestRefresh();
                    QuiltViewRenderMode expectedMode = modes[i];
                    yield return WaitFor(() => producerRenderer != null && producerRenderer.LatestSnapshot.IsValid &&
                        producerRenderer.LatestSnapshot.Revision > previousRevision &&
                        producerRenderer.LatestSnapshot.ViewRenderMode == expectedMode,
                        "The preview did not apply " + expectedMode + " at the next requested frame boundary.");

                    QuiltRenderSnapshot snapshot = producerRenderer.LatestSnapshot;
                    Assert.AreEqual(expectedCounts[i], snapshot.LastRenderedViewCount, expectedMode + " should render its expected sparse view count.");
                    Assert.AreEqual(initialSnapshot.Layout.QuiltWidth, snapshot.Layout.QuiltWidth);
                    Assert.AreEqual(initialSnapshot.Layout.QuiltHeight, snapshot.Layout.QuiltHeight);
                    Assert.AreEqual(initialSnapshot.Layout.Columns, snapshot.Layout.Columns);
                    Assert.AreEqual(initialSnapshot.Layout.Rows, snapshot.Layout.Rows);
                    Assert.AreEqual(initialSnapshot.Layout.ViewCount, snapshot.Layout.ViewCount,
                        "Sampling density must preserve the logical quilt view count.");
                    Assert.AreSame(quiltTexture, PreviewApi.Texture, "Changing sampling density should not rebuild the quilt texture.");
                    Assert.AreSame(viewTexture, PreviewApi.ViewTexture, "Changing sampling density should not rebuild the view texture.");
                }
            }
            finally
            {
                if (producerRenderer != null) producerRenderer.QuiltRendered -= completionCallback;
                if (previewAttempted)
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                fixture.Dispose();
            }
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
                if (previewAttempted && !Application.isPlaying)
                    Debug.Log("EDITOR_PREVIEW_DEVICE_CLEANUP active=" + PreviewApi.IsActive +
                        ", stopping=" + PreviewApi.IsStopping +
                        ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                        ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount);
                if (!crossedPlayMode)
                {
                    fixture.Dispose();
                    SessionState.SetBool(DevicePlayTransitionStatePrefix + "captured", false);
                    SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", true);
                }
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

        [UnityTest]
        public IEnumerator DevicePreviewStopsBeforePlayAndRuntimeSessionRendersWhenExplicitlyEnabled()
        {
            if (Environment.GetEnvironmentVariable("VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST") != "1")
                Assert.Ignore("Set VIEWWEAVE_EDITOR_PREVIEW_DEVICE_TEST=1 only for the owned isolated project with Bridge and a connected display.");
            if (SessionState.GetBool(DevicePlayTransitionStatePrefix + "captured", false) ||
                SessionState.GetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false))
                throw new InvalidOperationException("A previous device-transition fixture still owns render-pipeline state or assets; refusing to overwrite its recovery record.");

            bool previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousOptions = EditorSettings.enterPlayModeOptions;
            bool disableDomainAndSceneReload = Environment.GetEnvironmentVariable(
                "VIEWWEAVE_EDITOR_PREVIEW_DISABLE_DOMAIN_AND_SCENE_RELOAD_TEST") == "1";
            CaptureDeviceTransitionPlayOptions(previousOptionsEnabled, previousOptions);
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = disableDomainAndSceneReload
                ? EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload
                : EnterPlayModeOptions.None;

            bool previewAttempted = false;
            bool crossedPlayMode = false;
            bool exitingEditModeObserved = false;
            var fixture = new Fixture(false);
            BridgeWorkerQuiltOutput sourceOutput = null;
            ViewWeaveBridgeSession session = null;
            Action<BridgeDisplaySnapshot> onSnapshot = null;
            Action<PlayModeStateChange> playModeObserver = null;
            try
            {
                string pipelineAssetDirectory = CreateOwnedDeviceTransitionAssetFolder();
                fixture.Setup(pipelineAssetDirectory);
                CaptureDeviceTransitionPipelineSettings(
                    fixture.PreviousDefaultPipeline,
                    fixture.PreviousQualityPipeline,
                    previousOptionsEnabled,
                    previousOptions);
                fixture.Rig.gameObject.SetActive(false);
                fixture.Rig.gameObject.name = DevicePlayTransitionRigName;
                fixture.TargetObject.name = DevicePlayTransitionTargetName;
                fixture.SetOwnedPipelineNames(DevicePlayTransitionPipelineName, DevicePlayTransitionRendererDataName);
                sourceOutput = fixture.Rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
                sourceOutput.initializeOnEnable = false;
                fixture.Rig.SetOutput(sourceOutput);
                session = fixture.Rig.gameObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = false;
                if (!fixture.Rig.ConfigureComponents())
                    throw new AssertionException("The DevicePreview-to-Play source Rig could not configure its Bridge output.");
                fixture.Rig.gameObject.SetActive(true);

                BridgeDisplaySnapshot refreshedSnapshot = null;
                onSnapshot = value => refreshedSnapshot = value;
                sourceOutput.ConnectedDisplaySnapshotChanged += onSnapshot;
                if (!sourceOutput.TryRequestDisplayRefresh(out string refreshError))
                    throw new AssertionException("The explicit pre-preview display refresh was rejected: " + refreshError);
                double metadataDeadline = EditorApplication.timeSinceStartup + 30.0;
                double nextMetadataLog = EditorApplication.timeSinceStartup + 5.0;
                while (EditorApplication.timeSinceStartup < metadataDeadline &&
                       !(refreshedSnapshot != null &&
                         sourceOutput.CurrentConfigurationDisplaySnapshot == refreshedSnapshot &&
                         !BridgeDisplayDiscovery.IsBusy))
                {
                    if (EditorApplication.timeSinceStartup >= nextMetadataLog)
                    {
                        Debug.Log("EDITOR_PREVIEW_DEVICE_METADATA_WAIT busy=" + BridgeDisplayDiscovery.IsBusy +
                            ", status=" + (BridgeDisplayDiscovery.Status ?? string.Empty) +
                            ", discoveryError=" + (BridgeDisplayDiscovery.LastError ?? string.Empty) +
                            ", outputEnabled=" + sourceOutput.enabled +
                            ", outputActive=" + sourceOutput.gameObject.activeInHierarchy +
                            ", outputActiveAndEnabled=" + sourceOutput.isActiveAndEnabled +
                            ", outputStatus=" + (sourceOutput.DisplayRefreshStatus ?? string.Empty) +
                            ", outputError=" + (sourceOutput.LastError ?? string.Empty) +
                            ", snapshot=" + (refreshedSnapshot != null) +
                            ", rawSnapshot=" + (sourceOutput.ConnectedDisplaySnapshot != null) +
                            ", currentSnapshot=" + (sourceOutput.CurrentConfigurationDisplaySnapshot != null) +
                            ", shutdownPending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                            ", native=" + ReadDiscoveryDiagnostic());
                        nextMetadataLog += 5.0;
                    }
                    yield return null;
                }
                if (refreshedSnapshot == null || sourceOutput.CurrentConfigurationDisplaySnapshot != refreshedSnapshot ||
                    BridgeDisplayDiscovery.IsBusy)
                    throw new AssertionException("The explicit pre-preview display refresh did not publish a current configuration snapshot. " +
                        "busy=" + BridgeDisplayDiscovery.IsBusy +
                        ", status=" + (BridgeDisplayDiscovery.Status ?? string.Empty) +
                        ", discoveryError=" + (BridgeDisplayDiscovery.LastError ?? string.Empty) +
                        ", outputEnabled=" + sourceOutput.enabled +
                        ", outputActive=" + sourceOutput.gameObject.activeInHierarchy +
                        ", outputActiveAndEnabled=" + sourceOutput.isActiveAndEnabled +
                        ", outputStatus=" + (sourceOutput.DisplayRefreshStatus ?? string.Empty) +
                        ", outputError=" + (sourceOutput.LastError ?? string.Empty) +
                        ", snapshot=" + (refreshedSnapshot != null) +
                        ", rawSnapshot=" + (sourceOutput.ConnectedDisplaySnapshot != null) +
                        ", currentSnapshot=" + (sourceOutput.CurrentConfigurationDisplaySnapshot != null) +
                        ", shutdownPending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                        ", native=" + ReadDiscoveryDiagnostic());
                sourceOutput.ConnectedDisplaySnapshotChanged -= onSnapshot;
                onSnapshot = null;
                Assert.Greater(refreshedSnapshot.Displays.Count, 0, "Bridge should report the connected display.");
                Assert.IsTrue(refreshedSnapshot.TryGetDisplay(sourceOutput.displayOrdinal, out BridgeDisplayProfile profile),
                    "The selected display ordinal must be present in the refreshed snapshot.");
                Assert.IsTrue(profile.IsValid, "The selected display profile must have valid geometry.");
                Assert.IsTrue(profile.HasPhysicalCalibration, "The selected display must include physical calibration data.");
                Debug.Log("EDITOR_PREVIEW_DEVICE_METADATA generation=" + refreshedSnapshot.Generation +
                    ", displays=" + refreshedSnapshot.Displays.Count + ", calibration=true");

                session.startOnEnable = true;
                previewAttempted = true;
                Debug.Log("EDITOR_PREVIEW_DEVICE playOptions=" + EditorSettings.enterPlayModeOptions);
                StartDevicePreview(fixture.Rig);
                yield return WaitForDeviceWorker("before-play");
                BridgeWorkerQuiltOutput previewOutput = PreviewApi.ProducerOutput;
                Assert.IsNotNull(previewOutput);
                Assert.Greater(previewOutput.Submissions, 0);
                Assert.Greater(previewOutput.Completed, 0);

                playModeObserver = state =>
                {
                    if (state == PlayModeStateChange.ExitingEditMode) exitingEditModeObserved = true;
                };
                EditorApplication.playModeStateChanged += playModeObserver;
                yield return new EnterPlayMode();
                crossedPlayMode = true;
                Assert.IsTrue(Application.isPlaying, "Unity must enter Play mode for the device transition check.");
                ViewWeaveRig reloadedRig = FindSceneRig(DevicePlayTransitionRigName);
                Assert.IsNotNull(reloadedRig,
                    "Exactly one uniquely named device-transition Rig must survive the Editor scene reload. " + DescribeMatchingSceneObjects(DevicePlayTransitionRigName));
                GameObject reloadedTarget = FindSceneObject(DevicePlayTransitionTargetName);
                Assert.IsNotNull(reloadedTarget, "The uniquely named device-transition target must survive the Editor scene reload.");
                session = reloadedRig.GetComponent<ViewWeaveBridgeSession>();
                sourceOutput = reloadedRig.GetComponent<BridgeWorkerQuiltOutput>();
                Assert.IsNotNull(session, "The reloaded device-transition Rig must retain its Bridge Session component.");
                Assert.IsNotNull(sourceOutput, "The reloaded device-transition Rig must retain its Bridge output component.");
                yield return WaitForDevicePreviewStop(session, sourceOutput, () => exitingEditModeObserved);
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY editorProducerStopped=true retentionPending=0 nativePending=0");

                double runtimeStartedAt = EditorApplication.timeSinceStartup;
                double runtimeDeadline = runtimeStartedAt + 45.0;
                while (EditorApplication.timeSinceStartup < runtimeDeadline)
                {
                    if (session != null && session.State == ViewWeaveBridgeSession.SessionState.Rendering &&
                        sourceOutput != null && sourceOutput.Ready && sourceOutput.Completed > 0) break;
                    if (session != null && session.State == ViewWeaveBridgeSession.SessionState.Fault)
                        Assert.Fail("The runtime Bridge Session faulted after the Editor preview stopped: " + session.LastError);
                    yield return null;
                }
                Assert.IsNotNull(session);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Rendering, session.State,
                    "The runtime Bridge Session did not reach Rendering after the Editor preview stopped.");
                Assert.IsTrue(sourceOutput.Ready, "The runtime Bridge worker must be Ready.");
                Assert.Greater(sourceOutput.Completed, 0, "The runtime Bridge worker must complete a submission.");
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY runtimeState=" + session.State + ", submissions=" + sourceOutput.Submissions +
                    ", completed=" + sourceOutput.Completed);

                session.StopSession();
                double stopDeadline = EditorApplication.timeSinceStartup + 30.0;
                while (EditorApplication.timeSinceStartup < stopDeadline &&
                       (session.State != ViewWeaveBridgeSession.SessionState.Idle ||
                        sourceOutput.State != BridgeWorkerQuiltOutput.WorkerState.Closed ||
                        BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 || BridgeEditorPreviewRetention.PendingCount != 0))
                    yield return null;
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State,
                    "The runtime Session did not return to Idle after Stop.");
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, sourceOutput.State,
                    "The runtime Bridge worker must close after Stop.");
                Assert.AreEqual(0, BridgeWorkerQuiltOutput.ShutdownPendingCount,
                    "Native cleanup must be complete after Stop.");
                Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount,
                    "Preview retention proof must be complete after Stop.");
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY runtimeStopped=true nativePending=0 retentionPending=0");

                yield return new ExitPlayMode();
                Assert.IsFalse(Application.isPlaying, "The hardware test must return Unity to Edit Mode.");
                Assert.IsFalse(PreviewApi.IsActive, "Returning to Edit Mode must not restart the Editor preview.");
                ViewWeaveRig editModeRig = FindSceneRig(DevicePlayTransitionRigName);
                Assert.IsNotNull(editModeRig, "The owned Rig fixture must be available for cleanup after returning to Edit Mode.");
                GameObject editModeTarget = FindSceneObject(DevicePlayTransitionTargetName);
                Assert.IsNotNull(editModeTarget, "The owned target fixture must be available for cleanup after returning to Edit Mode.");
                CleanupDeviceTransitionFixture();
                Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY editModeReturned=true editorPreviewRestarted=false");
            }
            finally
            {
                if (playModeObserver != null) EditorApplication.playModeStateChanged -= playModeObserver;
                if (onSnapshot != null && sourceOutput != null) sourceOutput.ConnectedDisplaySnapshotChanged -= onSnapshot;
                if (Application.isPlaying && session == null)
                {
                    ViewWeaveRig currentRig = FindSceneRig(DevicePlayTransitionRigName);
                    if (currentRig != null)
                    {
                        session = currentRig.GetComponent<ViewWeaveBridgeSession>();
                        sourceOutput = currentRig.GetComponent<BridgeWorkerQuiltOutput>();
                    }
                }
                if (session != null && Application.isPlaying && session.State != ViewWeaveBridgeSession.SessionState.Idle)
                    session.StopSession();
                if (previewAttempted && !Application.isPlaying &&
                    (PreviewApi.IsActive || PreviewApi.IsStopping || BridgeEditorPreviewRetention.PendingCount != 0))
                {
                    PreviewApi.Stop();
                    BridgeEditorPreviewRetention.RequestStop(true);
                }
                if (!crossedPlayMode)
                {
                    fixture.Dispose();
                    CleanupDeviceTransitionFixture();
                }
                else if (!Application.isPlaying)
                {
                    CleanupDeviceTransitionFixture();
                }
                else Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY fixtureCleanupDeferredUntilEditorExit=true");
                RestoreDeviceTransitionPlayOptions();
            }
        }

        static void CaptureDeviceTransitionPipelineSettings(
            RenderPipelineAsset previousDefault,
            RenderPipelineAsset previousQuality,
            bool previousOptionsEnabled,
            EnterPlayModeOptions previousOptions)
        {
            if ((previousDefault != null && !EditorUtility.IsPersistent(previousDefault)) ||
                (previousQuality != null && !EditorUtility.IsPersistent(previousQuality)))
                throw new InvalidOperationException("The device transition test requires persistent or null original render pipeline assets so it can restore them after domain reload.");
            string defaultPath = previousDefault == null ? string.Empty : AssetDatabase.GetAssetPath(previousDefault);
            string qualityPath = previousQuality == null ? string.Empty : AssetDatabase.GetAssetPath(previousQuality);
            if ((previousDefault != null && string.IsNullOrEmpty(defaultPath)) ||
                (previousQuality != null && string.IsNullOrEmpty(qualityPath)))
                throw new InvalidOperationException("The device transition test cannot safely restore an original render pipeline asset without an AssetDatabase path.");
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "captured", true);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "defaultNull", previousDefault == null);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "qualityNull", previousQuality == null);
            SessionState.SetString(DevicePlayTransitionStatePrefix + "defaultPath", defaultPath);
            SessionState.SetString(DevicePlayTransitionStatePrefix + "qualityPath", qualityPath);
            CaptureDeviceTransitionPlayOptions(previousOptionsEnabled, previousOptions);
        }

        static string CreateOwnedDeviceTransitionAssetFolder()
        {
            string folderName = "__ViewWeaveDevicePlayTransition_" + Guid.NewGuid().ToString("N");
            string path = "Assets/" + folderName;
            if (AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("A unique device-transition asset folder unexpectedly already exists.");
            string guid = AssetDatabase.CreateFolder("Assets", folderName);
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("The isolated device-transition asset folder could not be created.");
            SessionState.SetString(DevicePlayTransitionStatePrefix + "assetFolder", path);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", true);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false);
            return path;
        }

        static void CleanupDeviceTransitionFixture()
        {
            if (SessionState.GetBool(DevicePlayTransitionStatePrefix + "cleanupDone", false)) return;
            RestoreDeviceTransitionPipelineSettings();

            GameObject target = FindSceneObject(DevicePlayTransitionTargetName);
            if (target != null)
            {
                MeshRenderer renderer = target.GetComponent<MeshRenderer>();
                Material material = renderer == null ? null : renderer.sharedMaterial;
                if (material != null && !EditorUtility.IsPersistent(material)) Object.DestroyImmediate(material);
                Object.DestroyImmediate(target);
            }

            ViewWeaveRig rig = FindSceneRig(DevicePlayTransitionRigName);
            if (rig != null) Object.DestroyImmediate(rig.gameObject);

            DestroyTransientAsset<UniversalRenderPipelineAsset>(DevicePlayTransitionPipelineName);
            DestroyTransientAsset<UniversalRendererData>(DevicePlayTransitionRendererDataName);
            DeleteOwnedDeviceTransitionAssetFolder();
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "cleanupDone", true);
            Debug.Log("EDITOR_PREVIEW_DEVICE_PLAY fixtureCleanup=true");
        }

        static void DeleteOwnedDeviceTransitionAssetFolder()
        {
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false)) return;
            string path = SessionState.GetString(DevicePlayTransitionStatePrefix + "assetFolder", string.Empty);
            if (string.IsNullOrEmpty(path) || !path.StartsWith(DevicePlayTransitionAssetFolderPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete a device-transition asset folder outside the exact owned path prefix.");
            if (AssetDatabase.IsValidFolder(path) && !AssetDatabase.DeleteAsset(path))
                throw new InvalidOperationException("The owned device-transition pipeline asset folder could not be deleted.");
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "assetFolderOwned", false);
            SessionState.SetString(DevicePlayTransitionStatePrefix + "assetFolder", string.Empty);
        }

        static void RestoreDeviceTransitionPipelineSettings()
        {
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "captured", false)) return;
            RenderPipelineAsset defaultAsset = SessionState.GetBool(DevicePlayTransitionStatePrefix + "defaultNull", true)
                ? null
                : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SessionState.GetString(DevicePlayTransitionStatePrefix + "defaultPath", string.Empty));
            RenderPipelineAsset qualityAsset = SessionState.GetBool(DevicePlayTransitionStatePrefix + "qualityNull", true)
                ? null
                : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(SessionState.GetString(DevicePlayTransitionStatePrefix + "qualityPath", string.Empty));
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "defaultNull", true) && defaultAsset == null)
                throw new InvalidOperationException("The original default render pipeline asset could not be restored from its saved AssetDatabase path.");
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "qualityNull", true) && qualityAsset == null)
                throw new InvalidOperationException("The original quality render pipeline asset could not be restored from its saved AssetDatabase path.");
            GraphicsSettings.defaultRenderPipeline = defaultAsset;
            QualitySettings.renderPipeline = qualityAsset;
            Assert.AreSame(defaultAsset, GraphicsSettings.defaultRenderPipeline, "The device transition test must restore the original default render pipeline.");
            Assert.AreSame(qualityAsset, QualitySettings.renderPipeline, "The device transition test must restore the original quality render pipeline.");
            RestoreDeviceTransitionPlayOptions();
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "captured", false);
        }

        static void CaptureDeviceTransitionPlayOptions(bool optionsEnabled, EnterPlayModeOptions options)
        {
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "optionsStored", true);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "optionsEnabled", optionsEnabled);
            SessionState.SetInt(DevicePlayTransitionStatePrefix + "options", (int)options);
        }

        static IEnumerator WaitForDevicePreviewStop(
            ViewWeaveBridgeSession session,
            BridgeWorkerQuiltOutput sourceOutput,
            Func<bool> exitingEditModeObserved)
        {
            const double timeoutSeconds = 30.0;
            double startedAt = EditorApplication.timeSinceStartup;
            double nextLogAt = startedAt + 5.0;
            Func<bool> stopped = () => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                BridgeEditorPreviewRetention.PendingCount == 0 && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0;

            while (!stopped() && EditorApplication.timeSinceStartup - startedAt < timeoutSeconds)
            {
                double elapsed = EditorApplication.timeSinceStartup - startedAt;
                if (elapsed >= nextLogAt - startedAt)
                {
                    Debug.Log("EDITOR_PREVIEW_DEVICE_STOP_WAIT elapsed=" + elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                        ", " + DescribeDevicePreviewStopState(session, sourceOutput, exitingEditModeObserved()));
                    nextLogAt += 5.0;
                }
                yield return null;
            }

            if (!stopped())
            {
                string diagnostic = "elapsed=" + (EditorApplication.timeSinceStartup - startedAt).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) +
                    ", " + DescribeDevicePreviewStopState(session, sourceOutput, exitingEditModeObserved());
                Debug.Log("EDITOR_PREVIEW_DEVICE_STOP_TIMEOUT " + diagnostic);
                Assert.Fail("The Edit Mode device producer did not stop and prove its resources before runtime startup. " + diagnostic);
            }
        }

        static string DescribeDevicePreviewStopState(
            ViewWeaveBridgeSession session,
            BridgeWorkerQuiltOutput sourceOutput,
            bool exitingEditModeObserved)
        {
            Type tracker = typeof(QuiltRenderer).Assembly.GetType("ViewWeave.QuiltRendererEditorCallbackTracker");
            object ReadTracker(string propertyName)
            {
                PropertyInfo property = tracker?.GetProperty(propertyName, BindingFlags.Static | BindingFlags.NonPublic);
                return property == null ? "missing" : property.GetValue(null);
            }

            BridgeWorkerQuiltOutput producer = PreviewApi.ProducerOutput;
            string summary = "exitingEditMode=" + exitingEditModeObserved +
                ", previewActive=" + PreviewApi.IsActive +
                ", previewStopping=" + PreviewApi.IsStopping +
                ", previewStatus=" + PreviewApi.Status +
                ", previewError=" + PreviewApi.LastError +
                ", callbackReady=" + ReadTracker("IsReady") +
                ", callbackInside=" + ReadTracker("IsInsideRenderCallback") +
                ", contextDepth=" + ReadTracker("ContextDepth") +
                ", cameraDepth=" + ReadTracker("CameraDepth") +
                ", retentionPending=" + BridgeEditorPreviewRetention.PendingCount +
                ", retentionRecords={" + PreviewApi.DescribeRetentionStateWithoutNativeCalls() + "}" +
                ", nativePending=" + BridgeWorkerQuiltOutput.ShutdownPendingCount +
                ", imageValidationPending=" + PreviewApi.NativeImageValidationPending +
                ", sourceSession=" + (session == null ? "null" : session.State.ToString()) +
                ", sourceSessionError=" + (session == null ? string.Empty : session.LastError) +
                ", sourceOutput=" + DescribeOutputState(sourceOutput) +
                ", producerOutput=" + DescribeOutputState(producer);
            return ToAscii(summary);
        }

        static string DescribeOutputState(BridgeWorkerQuiltOutput output)
        {
            if (output == null) return "null";
            return "ready=" + output.Ready + ", state=" + output.State +
                ", submissions=" + output.Submissions + ", completed=" + output.Completed +
                ", error=" + (output.LastError ?? string.Empty);
        }

        static string ToAscii(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] characters = new char[value.Length];
            int length = 0;
            foreach (char character in value)
                if (character >= 0x20 && character <= 0x7e) characters[length++] = character;
            return new string(characters, 0, length);
        }

        static void RestoreDeviceTransitionPlayOptions()
        {
            if (!SessionState.GetBool(DevicePlayTransitionStatePrefix + "optionsStored", false)) return;
            EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(
                DevicePlayTransitionStatePrefix + "optionsEnabled", false);
            EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(
                DevicePlayTransitionStatePrefix + "options", (int)EnterPlayModeOptions.None);
            SessionState.SetBool(DevicePlayTransitionStatePrefix + "optionsStored", false);
        }

        static void DestroyTransientAsset<T>(string exactName) where T : Object
        {
            T found = null;
            foreach (T candidate in Resources.FindObjectsOfTypeAll<T>())
            {
                if (candidate == null || candidate.name != exactName || EditorUtility.IsPersistent(candidate)) continue;
                if (found != null) return;
                found = candidate;
            }
            if (found != null) Object.DestroyImmediate(found);
        }

        static ViewWeaveRig FindSceneRig(string exactName)
        {
            ViewWeaveRig found = null;
            foreach (ViewWeaveRig candidate in Resources.FindObjectsOfTypeAll<ViewWeaveRig>())
            {
                if (candidate == null || candidate.gameObject.name != exactName || EditorUtility.IsPersistent(candidate)) continue;
                if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        static GameObject FindSceneObject(string exactName)
        {
            GameObject found = null;
            foreach (GameObject candidate in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (candidate == null || candidate.name != exactName || EditorUtility.IsPersistent(candidate)) continue;
                if (!candidate.scene.IsValid() || !candidate.scene.isLoaded) continue;
                if (found != null) return null;
                found = candidate;
            }
            return found;
        }

        static string DescribeMatchingSceneObjects(string exactName)
        {
            var matches = Resources.FindObjectsOfTypeAll<ViewWeaveRig>();
            var summaries = new System.Collections.Generic.List<string>();
            foreach (ViewWeaveRig candidate in matches)
            {
                if (candidate == null) continue;
                summaries.Add(candidate.gameObject.name + "@" + candidate.gameObject.scene.name +
                    ", loaded=" + candidate.gameObject.scene.isLoaded + ", persistent=" + EditorUtility.IsPersistent(candidate));
            }
            return "Expected=" + exactName + ", scene Rigs=" + string.Join("; ", summaries);
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
                Assert.IsTrue(QuiltDisplaySettingsResolver.TryResolve(sourceRenderer, out QuiltSettings gizmoSettings,
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
                Assert.IsTrue(sourceRenderer.TryGetFraming(out QuiltCameraFraming liveFraming));
                Assert.IsTrue(sourceRenderer.TrySetFraming(new QuiltCameraFraming(45f, liveFraming.FocusDistance,
                    liveFraming.PhysicalFramingEnabled, liveFraming.ApertureWidthMillimeters,
                    liveFraming.ApertureHeightMillimeters, liveFraming.UnitsPerMeter), out string framingUpdateError), framingUpdateError);
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

                QuiltQualityProfile originalProfile = fixture.Rig.Profile;
                QuiltLayout originalLayout = producerRenderer.LatestSnapshot.Layout;
                InvalidOperationException profileError = Assert.Throws<InvalidOperationException>(
                    () => fixture.Rig.ApplyProfile(new QuiltQualityProfile(256, 128, 4, 2, 8, 1f, 20f)));
                StringAssert.Contains("Editor preview owns this Rig", profileError.Message);
                Assert.AreEqual(originalProfile.QuiltWidth, fixture.Rig.Profile.QuiltWidth);
                Assert.AreEqual(originalProfile.QuiltHeight, fixture.Rig.Profile.QuiltHeight);
                Assert.AreEqual(originalLayout.QuiltWidth, producerRenderer.LatestSnapshot.Layout.QuiltWidth);
                Assert.AreEqual(originalLayout.QuiltHeight, producerRenderer.LatestSnapshot.Layout.QuiltHeight);
                Assert.AreSame(first, PreviewApi.Texture, "A rejected structural change must not rebuild or replace preview textures.");
                Debug.Log("EDITOR_PREVIEW_GPU profile_mutation pipeline=" + (hdrp ? "HDRP" : "URP") + ", rejected=true, profileUnchanged=true");

                PreviewApi.Stop();
                yield return WaitFor(() => !PreviewApi.IsActive && !PreviewApi.IsStopping &&
                    BridgeEditorPreviewRetention.PendingCount == 0 && (!first || !first.IsCreated()),
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

        static string ReadDiscoveryDiagnostic()
        {
            try
            {
                Assembly bridgeAssembly = typeof(BridgeWorkerQuiltOutput).Assembly;
                Type discoveryType = bridgeAssembly.GetType("ViewWeave.BridgeDisplayDiscovery");
                Type nativeType = bridgeAssembly.GetType("ViewWeave.BridgeDisplayDiscoveryNative");
                Type guardType = bridgeAssembly.GetType("ViewWeave.BridgeNativeImageGuard");
                if (discoveryType == null || nativeType == null || guardType == null) return "reflectionTypesMissing";

                const BindingFlags staticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
                object GetStatic(string name) => discoveryType.GetField(name, staticPrivate)?.GetValue(null);
                object nativeHandle = GetStatic("handle");
                object moduleIdentity = GetStatic("moduleIdentity");
                double startedAt = GetStatic("requestStartedAt") is double started ? started : 0.0;
                bool validationPending = guardType.GetProperty("IsValidationPending", staticPrivate)?.GetValue(null) is bool pending && pending;
                bool identityValid = false;
                string identityError = string.Empty;
                int nativeResult = int.MinValue;
                uint requiredCount = 0;
                object nativeStatus = null;

                if (nativeHandle is IntPtr handle && handle != IntPtr.Zero && moduleIdentity != null && !validationPending)
                {
                    MethodInfo validate = guardType.GetMethod("TryValidateOwnerIdentity", staticPrivate);
                    if (validate != null)
                    {
                        object[] validateArgs = { moduleIdentity, null };
                        identityValid = validate.Invoke(null, validateArgs) is bool valid && valid;
                        identityError = validateArgs[1] as string ?? string.Empty;
                    }
                    if (identityValid)
                    {
                        Type statusType = nativeType.GetNestedType("SnapshotStatusV1", BindingFlags.NonPublic);
                        object adapter = nativeType.GetProperty("Adapter", staticPrivate).GetValue(null);
                        MethodInfo getSnapshot = adapter.GetType().GetMethod("GetDisplaySnapshot");
                        nativeStatus = Activator.CreateInstance(statusType);
                        statusType.GetField("structSize").SetValue(nativeStatus, (uint)Marshal.SizeOf(statusType));
                        statusType.GetField("version").SetValue(nativeStatus, 1u);
                        object[] queryArgs = { handle, nativeStatus, null, 0u, 0u };
                        nativeResult = (int)getSnapshot.Invoke(adapter, queryArgs);
                        nativeStatus = queryArgs[1];
                        requiredCount = (uint)queryArgs[4];
                    }
                }

                string nativeState = "notQueried";
                if (nativeStatus != null)
                {
                    Type statusType = nativeStatus.GetType();
                    object Read(string name) => statusType.GetField(name).GetValue(nativeStatus);
                    nativeState = "nativeResult=" + nativeResult +
                        ", state=" + Read("state") +
                        ", generation=" + Read("snapshotGeneration") +
                        ", requested=" + Read("requestedGeneration") +
                        ", pending=" + Read("pending") +
                        ", cleanup=" + Read("cleanupFinished") +
                        ", result=" + Read("result") +
                        ", count=" + Read("itemCount") +
                        ", required=" + requiredCount +
                        ", error=" + (Read("error") as string ?? string.Empty);
                }

                double elapsed = startedAt == 0.0 ? 0.0 : Time.realtimeSinceStartupAsDouble - startedAt;
                object candidate = GetStatic("candidateSnapshot");
                return "kind=" + GetStatic("requestKind") +
                    ", candidate=" + (candidate != null) +
                    ", closeRequested=" + GetStatic("closeRequested") +
                    ", elapsed=" + elapsed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                    ", epoch=" + GetStatic("lastTickEditorEpoch") +
                    ", validationPending=" + validationPending +
                    ", identityValid=" + identityValid +
                    ", identityError=" + identityError +
                    ", recoveryPending=" + GetStatic("recoveryPending") +
                    ", " + nativeState;
            }
            catch (Exception exception)
            {
                return "diagnosticException=" + exception.GetType().Name;
            }
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
            internal RenderPipelineAsset PreviousDefaultPipeline => previousDefault;
            internal RenderPipelineAsset PreviousQualityPipeline => previousQuality;
            internal int ExpectedViewSamples { get; private set; } = 1;

            internal Fixture(bool hdrp) => this.hdrp = hdrp;

            internal void Setup(string persistentUrpAssetDirectory = null)
            {
                previousDefault = GraphicsSettings.defaultRenderPipeline;
                previousQuality = QualitySettings.renderPipeline;
                if (hdrp)
                {
                    pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                }
                else
                {
                    if (string.IsNullOrEmpty(persistentUrpAssetDirectory))
                    {
                        urpRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                        urpPipeline = UniversalRenderPipelineAsset.Create(urpRendererData);
                    }
                    else
                    {
                        if (!AssetDatabase.IsValidFolder(persistentUrpAssetDirectory) ||
                            !persistentUrpAssetDirectory.StartsWith(DevicePlayTransitionAssetFolderPrefix, StringComparison.Ordinal))
                            throw new InvalidOperationException("Persistent URP assets require the test-owned device-transition folder.");
                        urpRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                        AssetDatabase.CreateAsset(urpRendererData, persistentUrpAssetDirectory + "/RendererData.asset");
                        urpPipeline = UniversalRenderPipelineAsset.Create(urpRendererData);
                        AssetDatabase.CreateAsset(urpPipeline, persistentUrpAssetDirectory + "/Pipeline.asset");
                    }
                    urpPipeline.msaaSampleCount = 4;
                    if (!string.IsNullOrEmpty(persistentUrpAssetDirectory)) AssetDatabase.SaveAssets();
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

            internal void SetOwnedPipelineNames(string pipelineName, string rendererDataName)
            {
                if (pipeline != null) pipeline.name = pipelineName;
                if (urpRendererData != null) urpRendererData.name = rendererDataName;
            }

            internal void Dispose()
            {
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                if (targetObject != null) Object.DestroyImmediate(targetObject);
                if (targetMaterial != null) Object.DestroyImmediate(targetMaterial);
                if (rigObject != null) Object.DestroyImmediate(rigObject);
                if (pipeline != null && !EditorUtility.IsPersistent(pipeline)) Object.DestroyImmediate(pipeline);
                if (urpRendererData != null && !EditorUtility.IsPersistent(urpRendererData)) Object.DestroyImmediate(urpRendererData);
            }
        }

        static class PreviewApi
        {
            static readonly Type Service = Type.GetType("ViewWeave.Editor.BridgeEditorPreview, ViewWeave.Bridge.Editor", true);
            static readonly PropertyInfo ActiveProperty = Service.GetProperty("IsActive", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo StoppingProperty = Service.GetProperty("IsStopping", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo StatusProperty = Service.GetProperty("Status", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo LastErrorProperty = Service.GetProperty("LastError", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo TextureProperty = Service.GetProperty("Texture", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly PropertyInfo LayoutProperty = Service.GetProperty("Layout", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerRendererField = Service.GetField("producerRenderer", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ViewTextureField = typeof(QuiltRenderer).GetField("viewTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerOutputField = Service.GetField("producerOutput", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly FieldInfo ProducerCameraField = Service.GetField("producerCamera", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo StartMethod = Service.GetMethod("Start", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo StopMethod = Service.GetMethod("Stop", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly MethodInfo RequestRefreshMethod = Service.GetMethod("RequestRefresh", BindingFlags.Static | BindingFlags.NonPublic);
            static readonly Type RetentionRecord = Type.GetType("ViewWeave.BridgeEditorPreviewRetentionRecord, ViewWeave.Bridge", true);
            static readonly PropertyInfo ValidationPendingProperty = typeof(BridgeWorkerQuiltOutput).Assembly
                .GetType("ViewWeave.BridgeNativeImageGuard", true)
                .GetProperty("IsValidationPending", BindingFlags.Static | BindingFlags.NonPublic);

            internal static bool IsActive => (bool)ActiveProperty.GetValue(null);
            internal static bool IsStopping => (bool)StoppingProperty.GetValue(null);
            internal static string Status => (string)StatusProperty.GetValue(null) ?? string.Empty;
            internal static string LastError => (string)LastErrorProperty.GetValue(null) ?? string.Empty;
            internal static bool NativeImageValidationPending => (bool)ValidationPendingProperty.GetValue(null);
            internal static RenderTexture Texture => (RenderTexture)TextureProperty.GetValue(null);
            internal static QuiltLayout Layout => (QuiltLayout)LayoutProperty.GetValue(null);
            internal static QuiltRenderer ProducerRenderer => (QuiltRenderer)ProducerRendererField.GetValue(null);
            internal static RenderTexture ViewTexture => (RenderTexture)ViewTextureField.GetValue(ProducerRenderer);
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

            internal static void RequestRefresh() => RequestRefreshMethod.Invoke(null, null);

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

            internal static string DescribeRetentionStateWithoutNativeCalls()
            {
                var records = Resources.FindObjectsOfTypeAll(RetentionRecord);
                var summaries = new string[records.Length];
                for (int i = 0; i < records.Length; i++)
                {
                    object record = records.GetValue(i);
                    Type type = record.GetType();
                    object recovery = GetRecordProperty(type, record, "WorkerRecoveryRecord");
                    string recoveryState = "null";
                    if (recovery != null && !(recovery is string))
                    {
                        Type recoveryType = recovery.GetType();
                        recoveryState = "nativeReleased=" + GetRecordProperty(recoveryType, recovery, "NativeReleased") +
                            ",needsClose=" + GetRecordProperty(recoveryType, recovery, "NeedsClose") +
                            ",gpuProof=" + GetRecordProperty(recoveryType, recovery, "HasGpuReleaseProof") +
                            ",canRelease=" + GetRecordProperty(recoveryType, recovery, "CanRelease") +
                            ",diagnostic=" + GetRecordProperty(recoveryType, recovery, "Diagnostic");
                    }
                    summaries[i] = "stop=" + GetRecordProperty(type, record, "StopRequested") +
                        ",detached=" + GetRecordProperty(type, record, "Detached") +
                        ",gpuProof=" + GetRecordProperty(type, record, "GpuProofComplete") +
                        ",handoff=" + GetRecordProperty(type, record, "WorkerHandoffComplete") +
                        ",workerComplete=" + GetRecordProperty(type, record, "WorkerRecoveryComplete") +
                        ",canRelease=" + GetRecordProperty(type, record, "CanRelease") +
                        ",diagnostic=" + GetRecordProperty(type, record, "Diagnostic") +
                        ",recovery={" + recoveryState + "}";
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
