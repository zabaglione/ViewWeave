#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave.Editor;

namespace ViewWeave.BridgeEditorPreviewValidation
{
    public sealed class QuiltEditorProducerTests
    {
        GameObject host;
        QuiltEditorResourceRecord resources;
        RenderPipelineAsset previousPipeline, previousQuality;
        UniversalRenderPipelineAsset pipeline;
        UniversalRendererData rendererData;

        [UnitySetUp]
        public IEnumerator Setup()
        {
            BridgeEditorDisplayDiscovery.AutomaticRequestsEnabled = false;
            previousPipeline = GraphicsSettings.defaultRenderPipeline;
            previousQuality = QualitySettings.renderPipeline;
            rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (resources != null) resources.Stop();
            if (host != null) Object.DestroyImmediate(host);
            GraphicsSettings.defaultRenderPipeline = previousPipeline;
            QualitySettings.renderPipeline = previousQuality;
            if (pipeline != null) Object.DestroyImmediate(pipeline);
            if (rendererData != null) Object.DestroyImmediate(rendererData);
            BridgeEditorDisplayDiscovery.Cancel();
            BridgeEditorDisplayDiscovery.AutomaticRequestsEnabled = true;
            yield return null;
            Assert.That(QuiltEditorResourceRecord.PendingCount, Is.Zero);
        }

        ViewWeaveRig Create(bool metal, float aspect)
        {
            host = new GameObject("Retained Geometry Preview Test");
            var rig = host.AddComponent<ViewWeaveRig>();
            Assert.That(ViewWeaveRigProviderRegistry.TryAddAdapter(host, rig.Renderer.sourceCamera, out MonoBehaviour guard), Is.True);
            rig.SetGuard(guard);
            MonoBehaviour output = metal ? (MonoBehaviour)host.AddComponent<BridgeMetalQuiltOutput>() : host.AddComponent<BridgeWorkerQuiltOutput>();
            rig.SetOutput(output);
            rig.AutoRender = false;
            rig.ApplyProfile(new QuiltQualityProfile(320, 192, 4, 3, 12, 1f, 40f));
            rig.Renderer.settings.fieldOfView = 55f;
            rig.Renderer.settings.cameraDistance = 6f;
            Publish(rig, output, aspect);
            Assert.That(rig.ValidateRig(out string error), Is.True, error);
            return rig;
        }

        static void Publish(ViewWeaveRig rig, MonoBehaviour output, float aspect)
        {
            uint width = aspect < 1f ? 1440u : 3840u;
            uint height = aspect < 1f ? 2560u : 2160u;
            Assert.That(BridgeDisplayProfile.TryCreateCached(7, width, height, aspect, 38f,
                4092, 4096, 11, 6, false, 0, 0, 0, out BridgeDisplayProfile profile), Is.True);
            Assert.That(BridgeDisplayGeometryStore.TryConfiguration(rig.Renderer, out _, out string runtime,
                out string application, out uint ordinal), Is.True);
            BridgeDisplayMetadata.Publish(rig, output, runtime, application, ordinal, profile);
        }

        [TestCase(true, 0.5625f)]
        [TestCase(false, 0.5625f)]
        [TestCase(true, 1.7777778f)]
        [TestCase(false, 1.7777778f)]
        public void RetainedGeometryKeepsPrivateRigAndRendererInAgreement(bool metal, float aspect)
        {
            ViewWeaveRig source = Create(metal, aspect);
            string sourceBefore = EditorJsonUtility.ToJson(source);
            string rendererBefore = EditorJsonUtility.ToJson(source.Renderer);
            ViewWeaveRig producer = QuiltEditorProducer.Create(source, out resources);
            Assert.That(producer.ValidateRig(out string error), Is.True, error);
            Assert.That(producer.Profile.Aspect, Is.EqualTo(aspect).Within(0.00001f));
            Assert.That(producer.Profile.ViewCone, Is.EqualTo(38f));
            Assert.That(producer.Profile.Views, Is.EqualTo(source.Profile.Views));
            Assert.That(producer.Renderer.settings.viewAspect, Is.EqualTo(producer.Profile.Aspect));
            Assert.That(producer.Renderer.settings.viewCone, Is.EqualTo(producer.Profile.ViewCone));
            Assert.That(producer.Renderer.settings.fieldOfView, Is.EqualTo(55f));
            Assert.That(producer.Renderer.settings.cameraDistance, Is.EqualTo(6f));
            QuiltEditorProducer.Sync(source, producer, false);
            Assert.That(producer.ValidateRig(out error), Is.True, error);
            Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(sourceBefore));
            Assert.That(EditorJsonUtility.ToJson(source.Renderer), Is.EqualTo(rendererBefore));
            Assert.That(producer.Renderer.QuiltTexture, Is.Null);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DeviceSyncPreservesAcquiredQualityInsteadOfRetainedSourceGeometry(bool metal)
        {
            ViewWeaveRig source = Create(metal, 0.5625f);
            ViewWeaveRig producer = QuiltEditorProducer.Create(source, out resources);
            QuiltQualityProfile connected = QuiltQualityProfile.Native(640, 384, 4, 3, 0.5625f, 36f);
            producer.ApplyProfile(connected);
            Publish(source, source.Renderer.OutputBehaviour, 16f / 9f);
            QuiltEditorProducer.Sync(source, producer, true);
            Assert.That(producer.ValidateRig(out string error), Is.True, error);
            Assert.That(producer.Profile.Aspect, Is.EqualTo(connected.Aspect));
            Assert.That(producer.Renderer.settings.viewAspect, Is.EqualTo(connected.Aspect));
            Assert.That(producer.Renderer.settings.viewCone, Is.EqualTo(connected.ViewCone));
            Assert.That(producer.Renderer.settings.quiltWidth, Is.EqualTo(connected.QuiltWidth));
            Assert.That(producer.Renderer.settings.fieldOfView, Is.EqualTo(55f));
            Assert.That(producer.Renderer.settings.cameraDistance, Is.EqualTo(6f));
            Assert.That(source.Profile.Aspect, Is.EqualTo(1f));
            Assert.That(source.Renderer.settings.viewAspect, Is.EqualTo(1f));
        }
    }
}
#endif
