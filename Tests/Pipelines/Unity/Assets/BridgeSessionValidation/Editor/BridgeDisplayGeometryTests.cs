#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ViewWeave.Editor;

namespace ViewWeave.BridgeEditorPreviewValidation
{
    public sealed class BridgeDisplayGeometryTests
    {
        GameObject host, other;

        [TearDown]
        public void Cleanup()
        {
            if (host != null) Object.DestroyImmediate(host);
            if (other != null) Object.DestroyImmediate(other);
            QuiltDisplaySettingsResolver.RegisterProvider(BridgeEditorDisplaySettings.ResolveDisplayProfile);
        }

        ViewWeaveRig Create(bool metal, out MonoBehaviour output)
        {
            host = new GameObject("Bridge Display Geometry Test");
            var rig = host.AddComponent<ViewWeaveRig>();
            output = metal ? (MonoBehaviour)host.AddComponent<BridgeMetalQuiltOutput>() : host.AddComponent<BridgeWorkerQuiltOutput>();
            rig.SetOutput(output);
            rig.AutoRender = false;
            rig.ApplyProfile(QuiltQualityProfile.Native(320, 192, 4, 3, 1f, 40f));
            QuiltDisplaySettingsResolver.RegisterProvider(BridgeEditorDisplaySettings.ResolveDisplayProfile);
            return rig;
        }

        static BridgeDisplayProfile Profile(float aspect)
        {
            uint width = aspect < 1f ? 1440u : 3840u;
            uint height = aspect < 1f ? 2560u : 2160u;
            Assert.That(BridgeDisplayProfile.TryCreateCached(7, width, height, aspect, 38f,
                4092, 4096, 11, 6, false, 0, 0, 0, out BridgeDisplayProfile result), Is.True);
            return result;
        }

        static void Publish(ViewWeaveRig rig, MonoBehaviour output, BridgeDisplayProfile profile)
        {
            Assert.That(BridgeDisplayGeometryStore.TryConfiguration(rig.Renderer, out _, out string runtime,
                out string application, out uint ordinal), Is.True);
            BridgeDisplayMetadata.Publish(rig, output, runtime, application, ordinal, profile);
        }

        static void AssertGeometry(ViewWeaveRig rig, float aspect)
        {
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out QuiltSettings settings,
                out QuiltEditorDisplayProfile profile, out string error), Is.True, error);
            Assert.That(settings.viewAspect, Is.EqualTo(aspect).Within(0.00001f));
            Assert.That(profile.source, Is.EqualTo(QuiltDisplaySettingsSource.LastRecognizedDisplay));
            Assert.That(profile.isConnected, Is.False, "Retained geometry must not claim a current device connection.");
            Camera camera = rig.Renderer.EffectiveCamera;
            Assert.That(QuiltFrustumGeometry.TryBuild(camera.transform, settings, camera.nearClipPlane,
                camera.farClipPlane, out QuiltFrustumGeometry.Geometry geometry), Is.True);
            foreach (Vector3[] plane in new[] { geometry.focusPlane, geometry.nearPlane, geometry.farPlane })
            {
                float ratio = Vector3.Distance(plane[0], plane[3]) / Vector3.Distance(plane[0], plane[1]);
                Assert.That(ratio, Is.EqualTo(aspect).Within(0.00001f));
            }
        }

        [TestCase(true, 0.5625f)]
        [TestCase(true, 1.7777778f)]
        [TestCase(false, 0.5625f)]
        [TestCase(false, 1.7777778f)]
        public void BothOutputsRetainBridgeGeometryWithNoActiveConnection(bool metal, float aspect)
        {
            ViewWeaveRig rig = Create(metal, out MonoBehaviour output);
            Publish(rig, output, Profile(aspect));
            AssertGeometry(rig, aspect);
            AssertGeometry(rig, aspect);
            Assert.That(rig.Renderer.settings.viewAspect, Is.EqualTo(1f), "Authoring history must not rewrite stored renderer settings.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NewBridgeResultUpdatesGeometryWhileInvalidResultDoesNotEraseIt(bool metal)
        {
            ViewWeaveRig rig = Create(metal, out MonoBehaviour output);
            Publish(rig, output, Profile(0.5625f));
            Publish(rig, output, default);
            AssertGeometry(rig, 0.5625f);
            Publish(rig, output, Profile(16f / 9f));
            AssertGeometry(rig, 16f / 9f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ChangedSelectionOrOutputDoesNotReusePreviousDisplay(bool metal)
        {
            ViewWeaveRig rig = Create(metal, out MonoBehaviour output);
            Publish(rig, output, Profile(0.5625f));
            if (metal) ((BridgeMetalQuiltOutput)output).displayOrdinal = 1;
            else ((BridgeWorkerQuiltOutput)output).displayOrdinal = 1;
            Assert.That(BridgeDisplayGeometryStore.TryResolve(rig.Renderer, out _), Is.False);
            if (metal) ((BridgeMetalQuiltOutput)output).displayOrdinal = 0;
            else ((BridgeWorkerQuiltOutput)output).displayOrdinal = 0;
            if (metal) ((BridgeMetalQuiltOutput)output).runtimePath = "another-runtime";
            else ((BridgeWorkerQuiltOutput)output).runtimePath = "another-runtime";
            Assert.That(BridgeDisplayGeometryStore.TryResolve(rig.Renderer, out _), Is.False);
            other = new GameObject("Replacement Output");
            var replacement = other.AddComponent<BridgeMetalQuiltOutput>();
            rig.SetOutput(replacement);
            Assert.That(BridgeDisplayGeometryStore.TryResolve(rig.Renderer, out _), Is.False);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SoleTransportRetainsGeometryAfterItsTemporaryBindingIsRestored(bool metal)
        {
            ViewWeaveRig rig = Create(metal, out MonoBehaviour output);
            Publish(rig, output, Profile(0.5625f));
            rig.SetOutput(null);
            AssertGeometry(rig, 0.5625f);
            if (metal) host.AddComponent<BridgeWorkerQuiltOutput>();
            else host.AddComponent<BridgeMetalQuiltOutput>();
            Assert.That(BridgeDisplayGeometryStore.TryResolve(rig.Renderer, out _), Is.False,
                "An unbound Rig with multiple transports must not choose a platform implicitly.");
        }

        [System.Serializable] sealed class SnapshotEnvelope
        { public BridgeDisplaySnapshotEditorCache editorConnectedDisplaySnapshotCache; }

        [Test]
        public void OlderWindowsSnapshotCannotOverrideNewerRecognizedGeometry()
        {
            ViewWeaveRig rig = Create(false, out MonoBehaviour component);
            var output = (BridgeWorkerQuiltOutput)component;
            BridgeDisplayProfile older = Profile(16f / 9f);
            var snapshot = new BridgeDisplaySnapshot(1, new[] { 0u }, new[] { older });
            var cache = BridgeDisplaySnapshotEditorCache.TryCreate(output, snapshot, false, output.runtimePath,
                output.applicationName, output.displayOrdinal);
            Assert.That(cache, Is.Not.Null);
            Assert.That(cache.TryRestore(output, false, output.runtimePath, output.applicationName, output.displayOrdinal, out _), Is.True);
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(new SnapshotEnvelope
                { editorConnectedDisplaySnapshotCache = cache }), output);
            Assert.That(output.CurrentConfigurationDisplaySnapshot, Is.Not.Null, EditorJsonUtility.ToJson(output));
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out _, out QuiltEditorDisplayProfile before, out _), Is.True);
            Assert.That(before.source, Is.EqualTo(QuiltDisplaySettingsSource.LocalRendererSettings),
                "A legacy Windows cache must not bypass the common successful-recognition store.");
            Assert.That(before.isConnected, Is.False);
            Publish(rig, output, older);
            AssertGeometry(rig, older.Aspect);
            Publish(rig, output, Profile(0.5625f));
            AssertGeometry(rig, 0.5625f);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void LocalPreviewSettingsUseTheSameRetainedGeometry(bool metal)
        {
            ViewWeaveRig rig = Create(metal, out MonoBehaviour output);
            Publish(rig, output, Profile(0.5625f));
            QuiltSettings preview = rig.Renderer.settings.Clone();
            BridgeDisplayGeometryStore.ApplyToLocalPreview(rig.Renderer, preview);
            Assert.That(preview.viewAspect, Is.EqualTo(0.5625f));
            Assert.That(preview.viewCone, Is.EqualTo(38f));
            Assert.That(preview.quiltWidth, Is.EqualTo(rig.Renderer.settings.quiltWidth));
            Assert.That(preview.views, Is.EqualTo(rig.Renderer.settings.views));
        }

        [Test]
        public void AnotherRendererCannotBorrowAnAttachedOutputsGeometry()
        {
            ViewWeaveRig rig = Create(true, out MonoBehaviour output);
            Publish(rig, output, Profile(0.5625f));
            other = new GameObject("Another Display Geometry Rig");
            var anotherRig = other.AddComponent<ViewWeaveRig>();
            anotherRig.SetOutput(output);
            Assert.That(BridgeDisplayGeometryStore.TryResolve(anotherRig.Renderer, out _), Is.False);
        }

        [Test]
        public void RetainedPhysicalCalibrationDoesNotEnableConnectedCalibrationActions()
        {
            ViewWeaveRig rig = Create(true, out MonoBehaviour output);
            Assert.That(BridgeDisplayProfile.TryCreateCached(7, 1440, 2560, 0.5625f, 38f,
                4092, 4096, 11, 6, true, 60f, 60f / 0.5625f, 610f, out BridgeDisplayProfile physical), Is.True);
            Publish(rig, output, physical);
            AssertGeometry(rig, 0.5625f);
            Assert.That(QuiltDisplaySettingsResolver.TryGetConnectedPhysicalCalibration(rig.Renderer, out _), Is.False);
        }
    }
}
#endif
