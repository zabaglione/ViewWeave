#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave.Editor;

namespace ViewWeave.RigCameraCreationValidation
{
    public sealed class MetalEditorPreviewTests
    {
        GameObject host;
        ViewWeaveRigAuthoringWindow window;
        RenderPipelineAsset previousPipeline, previousQuality;
        UniversalRenderPipelineAsset pipeline;
        UniversalRendererData rendererData;

        [UnitySetUp]
        public IEnumerator SetupPipeline()
        {
            if (Application.platform != RuntimePlatform.OSXEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                Assert.Ignore("Metal preview lifecycle requires macOS Editor with Metal.");
            previousPipeline = GraphicsSettings.defaultRenderPipeline;
            previousQuality = QualitySettings.renderPipeline;
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            yield return null;
        }

        ViewWeaveRig CreateRig()
        {
            if (Application.platform != RuntimePlatform.OSXEditor || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal)
                Assert.Ignore("Metal preview lifecycle requires macOS Editor with Metal.");
            Assert.That(ViewWeaveRigProviderRegistry.HasSupportedProvider(), Is.True, "The URP adapter must be registered.");
            host = new GameObject("Metal Preview Lifecycle Test");
            var rig = host.AddComponent<ViewWeaveRig>();
            Assert.That(ViewWeaveRigProviderRegistry.TryAddAdapter(host, rig.Renderer.sourceCamera, out MonoBehaviour adapter), Is.True);
            rig.SetGuard(adapter);
            rig.ApplyProfile(QuiltQualityProfile.Native(320, 192, 4, 3, 1f, 40f));
            rig.AutoRender = false;
            return rig;
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            BridgeMetalEditorPreview.Stop();
            if (window != null) window.Close();
            double deadline = EditorApplication.timeSinceStartup + 10d;
            while (BridgeMetalEditorPreview.PendingResourceCount != 0 && EditorApplication.timeSinceStartup < deadline) yield return null;
            if (host != null) Object.DestroyImmediate(host);
            Assert.That(BridgeMetalEditorPreview.PendingResourceCount, Is.Zero, "Editor GPU resources must retire");
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQuality;
            if (pipeline != null) Object.DestroyImmediate(pipeline);
            if (rendererData != null) Object.DestroyImmediate(rendererData);
        }

        IEnumerator WaitForFrame()
        {
            double deadline = EditorApplication.timeSinceStartup + 10d;
            while (BridgeMetalEditorPreview.RenderedFrames == 0 && BridgeMetalEditorPreview.IsActive && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(BridgeMetalEditorPreview.RenderedFrames, Is.GreaterThan(0), BridgeMetalEditorPreview.LastError);
        }

        [UnityTest]
        public IEnumerator ClosingRigWhileInspectorExistsDoesNotRefreshDestroyedTargets()
        {
            ViewWeaveRig rig = CreateRig();
            var inspector = UnityEditor.Editor.CreateEditor(rig);
            try
            {
                inspector.CreateInspectorGUI();
                Object.DestroyImmediate(host);
                host = null;
                yield return null;
                yield return null;
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                Object.DestroyImmediate(inspector);
            }
        }

        [UnityTest]
        public IEnumerator LocalPreviewPauseClosePreservesSourceAndReleasesOwnership()
        {
            ViewWeaveRig rig = CreateRig();
            string before = JsonUtility.ToJson(rig.Renderer.settings);
            string cameraBefore = EditorJsonUtility.ToJson(rig.Renderer.sourceCamera);
            Assert.That(BridgeMetalEditorPreview.Start(rig, false, out string error), Is.True, error);
            yield return WaitForFrame();
            Assert.That(BridgeMetalEditorPreview.Texture, Is.Not.Null);
            Assert.That(rig.Renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out _), Is.False);
            Assert.That(QuiltRendererOperationLease.TryAcquire(rig.Renderer, QuiltRendererOperationKind.Recording, out _, out _), Is.False);
            BridgeMetalEditorPreview.TogglePause();
            int pausedFrame = BridgeMetalEditorPreview.RenderedFrames;
            double until = EditorApplication.timeSinceStartup + 0.3d;
            while (EditorApplication.timeSinceStartup < until) yield return null;
            Assert.That(BridgeMetalEditorPreview.RenderedFrames, Is.EqualTo(pausedFrame));
            BridgeMetalEditorPreview.TogglePause();
            BridgeMetalEditorPreview.RequestRefresh();
            until = EditorApplication.timeSinceStartup + 5d;
            while (BridgeMetalEditorPreview.RenderedFrames == pausedFrame && EditorApplication.timeSinceStartup < until) yield return null;
            Assert.That(BridgeMetalEditorPreview.RenderedFrames, Is.GreaterThan(pausedFrame));
            BridgeMetalEditorPreview.Stop();
            Assert.That(rig.Renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out _), Is.True);
            Assert.That(JsonUtility.ToJson(rig.Renderer.settings), Is.EqualTo(before));
            Assert.That(EditorJsonUtility.ToJson(rig.Renderer.sourceCamera), Is.EqualTo(cameraBefore));
            Assert.That(rig.AutoRender, Is.False);
        }

        [UnityTest]
        public IEnumerator ClosingAuthoringWindowStopsItsMetalPreview()
        {
            ViewWeaveRig rig = CreateRig();
            window = ViewWeaveRigAuthoringWindow.OpenForRig(rig);
            Assert.That(BridgeMetalEditorPreview.Start(rig, false, out string error), Is.True, error);
            yield return WaitForFrame();
            window.Close();
            Assert.That(BridgeMetalEditorPreview.IsActive, Is.False);
            Assert.That(rig.Renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out _), Is.True);
        }

        [UnityTest]
        public IEnumerator InvalidSourceLayoutStopsPreviewWithoutLeakingOwnership()
        {
            ViewWeaveRig rig = CreateRig();
            Assert.That(BridgeMetalEditorPreview.Start(rig, false, out string error), Is.True, error);
            yield return WaitForFrame();
            rig.Renderer.settings.quiltWidth = 0;
            double deadline = EditorApplication.timeSinceStartup + 5d;
            while (BridgeMetalEditorPreview.IsActive && EditorApplication.timeSinceStartup < deadline) yield return null;
            Assert.That(BridgeMetalEditorPreview.IsActive, Is.False);
            Assert.That(BridgeMetalEditorPreview.LastError, Is.Not.Empty);
            rig.Renderer.settings.quiltWidth = 320;
            Assert.That(rig.Renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out _), Is.True);
        }

        [Test]
        public void PlaybackUsesBoundOutputWhenBothPlatformComponentsExist()
        {
            ViewWeaveRig rig = CreateRig();
            host.AddComponent<ViewWeaveBridgeSession>();
            var metal = host.AddComponent<BridgeMetalQuiltOutput>();
            var windows = host.GetComponent<BridgeWorkerQuiltOutput>();
            rig.SetOutput(windows);
            Assert.That(QuiltPlaybackWindow.ResolveMetalOutput(rig), Is.Null, "Windows binding must take precedence over an attached Metal component.");
            rig.SetOutput(metal);
            Assert.That(QuiltPlaybackWindow.ResolveMetalOutput(rig), Is.SameAs(metal));
            rig.SetOutput(null);
            Assert.That(QuiltPlaybackWindow.ResolveMetalOutput(rig), Is.Null, "An idle Windows Session remains the selected backend.");
        }

        [Test]
        public void PlaybackRequiresPlayAndDoesNotAcquireOwnershipInEditMode()
        {
            Assert.That(BridgeMetalQuiltPlayback.TryStart(null, null, out string error), Is.False);
            Assert.That(error, Does.Contain("Play mode"));
            Assert.That(BridgeMetalQuiltPlayback.IsActive, Is.False);
            Assert.That(BridgeMetalQuiltPlayback.PendingResourceCount, Is.Zero);
        }
    }
}
#endif
