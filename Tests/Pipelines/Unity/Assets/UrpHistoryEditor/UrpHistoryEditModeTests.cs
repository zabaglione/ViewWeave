using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave.URP;

namespace ViewWeave.UrpHistoryEditor
{
    public sealed class UrpHistoryEditModeTests
    {
        [UnityTest]
        public IEnumerator DisabledViewsAccumulateOutsidePlayModeAndRestartAfterPause()
        {
            Assert.IsFalse(Application.isPlaying);
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal) Assert.Ignore("Metal is required for this host test");
            var oldDefault = GraphicsSettings.defaultRenderPipeline; var oldQuality = QualitySettings.renderPipeline;
            GameObject root = null; UniversalRendererData renderer = null; UniversalRenderPipelineAsset pipeline = null;
            Material material = null; QuiltRenderer quilt = null;
            try
            {
                renderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                renderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                pipeline = UniversalRenderPipelineAsset.Create(renderer); pipeline.msaaSampleCount = 1;
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                root = new GameObject("EditModeTaaFixture");
                var sourceObject = new GameObject("QuiltCamera"); sourceObject.transform.SetParent(root.transform, false);
                var camera = sourceObject.AddComponent<Camera>(); camera.enabled = false;
                camera.cullingMask = 1 << 31; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                camera.allowHDR = false; camera.allowMSAA = false;
                var data = sourceObject.AddComponent<UniversalAdditionalCameraData>();
                data.renderPostProcessing = true; data.antialiasing = AntialiasingMode.TemporalAntiAliasing; data.volumeLayerMask = 0;
                var guard = sourceObject.AddComponent<UrpQuiltCamera>();
                quilt = sourceObject.AddComponent<QuiltRenderer>(); quilt.sourceCamera = camera; quilt.GuardBehaviour = guard;
                quilt.settings.quiltWidth = 384; quilt.settings.quiltHeight = 256;
                quilt.settings.columns = quilt.settings.rows = 2; quilt.settings.views = 4;
                quilt.settings.viewAspect = 1.5f; quilt.settings.cameraDistance = 3f; quilt.settings.fieldOfView = 60f; quilt.settings.viewCone = 25f;
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube); bar.transform.SetParent(root.transform, false); bar.layer = 31;
                bar.transform.position = new Vector3(0f, 0f, 3f); bar.transform.rotation = Quaternion.Euler(0f, 0f, 23f);
                bar.transform.localScale = new Vector3(1.35f, .14f, .08f);
                material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = Color.green };
                bar.GetComponent<Renderer>().sharedMaterial = material;
                int initialFrame = -1; int[] initialVersions = new int[4];
                for (int frame = 0; frame < 12; frame++)
                {
                    EditorApplication.QueuePlayerLoopUpdate(); yield return null;
                    quilt.RenderQuilt();
                    for (int view = 0; view < 4; view++)
                    {
                        var history = guard.GetHistoryCamera(view).GetComponent<UniversalAdditionalCameraData>().history.GetHistoryForRead<TaaHistory>();
                        Assert.IsNotNull(history); Assert.IsNotNull(history.GetAccumulationTexture()?.rt);
                        if (frame == 0) initialVersions[view] = history.GetAccumulationVersion();
                        if (frame == 11) Assert.Greater(history.GetAccumulationVersion(), initialVersions[view], "Edit Mode history did not advance");
                    }
                    if (frame == 0) initialFrame = Time.frameCount;
                }
                Assert.Greater(Time.frameCount, initialFrame, "Queued Editor frames did not progress");
                Camera left = guard.GetHistoryCamera(0); var leftHistory = left.GetComponent<UniversalAdditionalCameraData>().history.GetHistoryForRead<TaaHistory>();
                int version = leftHistory.GetAccumulationVersion(); quilt.RenderQuilt();
                Assert.AreEqual(version, leftHistory.GetAccumulationVersion(), "Same Editor frame advanced the history version");
                // Pause: retain cameras but do not render. Resume must advance the same view history.
                for (int frame = 0; frame < 3; frame++) { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                quilt.RenderQuilt(); Assert.AreSame(left, guard.GetHistoryCamera(0));
                Assert.Greater(leftHistory.GetAccumulationVersion(), version);
                quilt.ReleasePipelineRenderResources(); Assert.AreEqual(0, guard.HistoryCameraCount); Assert.IsTrue(left == null);
                EditorApplication.QueuePlayerLoopUpdate(); yield return null; quilt.RenderQuilt();
                Assert.AreEqual(4, guard.HistoryCameraCount);
                Assert.IsNotNull(guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>().history.GetHistoryForRead<TaaHistory>());
                // Unity uses live volumes in Edit Mode even when the authored camera requests ViaScripting.
                camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                // Exercise the actual public Metal local-preview service, including its private producer.
                quilt.ReleasePipelineRenderResources(); quilt.GuardBehaviour = null;
                var rig = sourceObject.AddComponent<ViewWeaveRig>(); rig.SetGuard(guard);
                rig.ApplyProfile(new QuiltQualityProfile(384, 256, 2, 2, 4, 1.5f, 25f));
                quilt.settings.cameraDistance = 3f; quilt.settings.fieldOfView = 60f;
                for (int i = 0; i < 3; i++) { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                Assert.IsTrue(ViewWeave.Editor.BridgeMetalEditorPreview.Start(rig, false, out string previewError), previewError);
                double deadline = EditorApplication.timeSinceStartup + 15d;
                while (ViewWeave.Editor.BridgeMetalEditorPreview.RenderedFrames < 8 && EditorApplication.timeSinceStartup < deadline)
                { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                Assert.GreaterOrEqual(ViewWeave.Editor.BridgeMetalEditorPreview.RenderedFrames, 8, ViewWeave.Editor.BridgeMetalEditorPreview.LastError);
                var producer = FindPreviewGuard(guard);
                Assert.IsNotNull(producer); Assert.AreEqual(4, producer.HistoryCameraCount);
                for (int view = 0; view < 4; view++)
                    Assert.IsNull(producer.GetHistoryCamera(view).GetComponent<UniversalAdditionalCameraData>().volumeStack,
                        "Edit Mode views retained an unused scripted VolumeStack");
                Camera previewCamera = producer.GetHistoryCamera(0);
                var previewHistory = previewCamera.GetComponent<UniversalAdditionalCameraData>().history.GetHistoryForRead<TaaHistory>();
                Assert.IsNotNull(previewHistory); int previewVersion = previewHistory.GetAccumulationVersion();
                ViewWeave.Editor.BridgeMetalEditorPreview.TogglePause();
                int pausedFrames = ViewWeave.Editor.BridgeMetalEditorPreview.RenderedFrames;
                deadline = EditorApplication.timeSinceStartup + .5d;
                while (EditorApplication.timeSinceStartup < deadline) { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                Assert.AreEqual(pausedFrames, ViewWeave.Editor.BridgeMetalEditorPreview.RenderedFrames);
                ViewWeave.Editor.BridgeMetalEditorPreview.TogglePause();
                deadline = EditorApplication.timeSinceStartup + 10d;
                while (ViewWeave.Editor.BridgeMetalEditorPreview.RenderedFrames < pausedFrames + 3 && EditorApplication.timeSinceStartup < deadline)
                { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                Assert.AreSame(previewCamera, producer.GetHistoryCamera(0));
                Assert.Greater(previewHistory.GetAccumulationVersion(), previewVersion);
                ViewWeave.Editor.BridgeMetalEditorPreview.Stop();
                deadline = EditorApplication.timeSinceStartup + 15d;
                while ((ViewWeave.Editor.BridgeMetalEditorPreview.IsStopping || ViewWeave.Editor.BridgeMetalEditorPreview.PendingResourceCount != 0) && EditorApplication.timeSinceStartup < deadline)
                { EditorApplication.QueuePlayerLoopUpdate(); yield return null; }
                Assert.IsFalse(ViewWeave.Editor.BridgeMetalEditorPreview.IsActive);
                Assert.AreEqual(0, ViewWeave.Editor.BridgeMetalEditorPreview.PendingResourceCount);
                Assert.IsTrue(previewCamera == null, "Preview stop retained a logical-view Camera");

            }
            finally
            {
                ViewWeave.Editor.BridgeMetalEditorPreview.Stop();
                quilt?.ReleasePipelineRenderResources();
                if (root != null)
                {
                    foreach (var data in root.GetComponentsInChildren<UniversalAdditionalCameraData>(true))
                        if (data.volumeStack != null && data.volumeStack.isValid)
                        { VolumeManager.instance.DestroyStack(data.volumeStack); data.volumeStack = null; }
                    Object.DestroyImmediate(root);
                } if (material != null) Object.DestroyImmediate(material);
                GraphicsSettings.defaultRenderPipeline = oldDefault; QualitySettings.renderPipeline = oldQuality;
                if (pipeline != null) Object.DestroyImmediate(pipeline); if (renderer != null) Object.DestroyImmediate(renderer);
            }
        }
        static UrpQuiltCamera FindPreviewGuard(UrpQuiltCamera original)
        {
            foreach (var guard in Resources.FindObjectsOfTypeAll<UrpQuiltCamera>())
                if (guard != original && guard.gameObject.name == "ViewWeave Editor Quilt Producer") return guard;
            return null;
        }
    }
}
