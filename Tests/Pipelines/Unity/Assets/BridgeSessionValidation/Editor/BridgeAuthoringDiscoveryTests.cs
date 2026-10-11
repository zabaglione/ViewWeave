#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using ViewWeave.Editor;

namespace ViewWeave.BridgeEditorPreviewValidation
{
    public sealed class BridgeAuthoringDiscoveryTests
    {
        GameObject host;
        FakeAdapter adapter;
        [SetUp] public void Setup()
        {
            BridgeEditorDisplayDiscovery.AutomaticRequestsEnabled = false;
            adapter = new FakeAdapter();
            BridgeDisplayDiscoveryNative.InstallTestAdapter(adapter);
        }
        [TearDown] public void Cleanup()
        {
            BridgeEditorDisplayDiscovery.Cancel();
            BridgeDisplayDiscovery.ResetForTests();
            if (host != null) UnityEngine.Object.DestroyImmediate(host);
            BridgeEditorDisplayDiscovery.AutomaticRequestsEnabled = true;
        }
        ViewWeaveRig Create(bool metal)
        {
            host = new GameObject("Metadata Only Authoring");
            ViewWeaveRig rig = host.AddComponent<ViewWeaveRig>();
            MonoBehaviour output = metal ? (MonoBehaviour)host.AddComponent<BridgeMetalQuiltOutput>() : host.AddComponent<BridgeWorkerQuiltOutput>();
            if (output is BridgeMetalQuiltOutput m) { m.runtimePath = "/synthetic/bridge"; m.startOnEnable = false; }
            else { var w = (BridgeWorkerQuiltOutput)output; w.runtimePath = "/synthetic/bridge"; }
            rig.SetOutput(output); rig.AutoRender = false;
            rig.ApplyProfile(QuiltQualityProfile.Native(320,192,4,3,1f,40f));
            QuiltDisplaySettingsResolver.RegisterProvider(BridgeEditorDisplaySettings.ResolveDisplayProfile);
            return rig;
        }
        static void Complete()
        { BridgeDisplayDiscovery.TickForTests(); BridgeDisplayDiscovery.TickForTests(); }
        [TestCase(QuiltDisplaySettingsSource.LocalRendererSettings)]
        [TestCase(QuiltDisplaySettingsSource.LocalPreview)]
        public void LocalGeometryIsNotPresentedAsSuccessfulBridgeAcquisition(QuiltDisplaySettingsSource source)
        {
            ViewWeaveRig rig = Create(true);
            UnityEditor.Editor inspector = null;
            try
            {
                QuiltDisplaySettingsResolver.RegisterProvider((QuiltRenderer renderer, out QuiltEditorDisplayProfile profile) =>
                {
                    profile = new QuiltEditorDisplayProfile(16f / 9f, 40f, source, false, false, 0, 0, 0, "Local preview");
                    return true;
                });
                inspector = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = inspector.CreateInspectorGUI();
                string text = root.Q<Label>("connectedDisplaysStatus").text;
                StringAssert.Contains("No Bridge geometry", text);
                StringAssert.DoesNotContain("Last successful Bridge geometry", text);
                Assert.That(adapter.BeginCount, Is.Zero);
            }
            finally
            {
                if (inspector != null) UnityEngine.Object.DestroyImmediate(inspector);
                QuiltDisplaySettingsResolver.RegisterProvider(BridgeEditorDisplaySettings.ResolveDisplayProfile);
            }
        }
        [TestCase(true, 1440u, 2560u)]
        [TestCase(false, 1440u, 2560u)]
        [TestCase(true, 3840u, 2160u)]
        [TestCase(false, 3840u, 2160u)]
        public void IdleDiscoveryAppliesGeometryAndPhysicalSizeWithoutOutput(bool metal, uint width, uint height)
        {
            ViewWeaveRig rig = Create(metal); adapter.Width = width; adapter.Height = height;
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out string error), Is.True, error);
            Complete();
            Assert.That(adapter.EndCount, Is.EqualTo(1));
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out QuiltSettings settings, out var profile, out error), Is.True, error);
            Assert.That(settings.viewAspect, Is.EqualTo((float)width/height).Within(1e-6));
            Assert.That(profile.source, Is.EqualTo(QuiltDisplaySettingsSource.LastRecognizedDisplay));
            Assert.That(profile.isConnected, Is.False);
            Assert.That(rig.Renderer.TryGetFraming(out var framing), Is.True);
            Assert.That(framing.ApertureWidthMillimeters, Is.EqualTo(width/300f*25.4f).Within(.001));
            Assert.That(framing.ApertureHeightMillimeters, Is.EqualTo(height/300f*25.4f).Within(.001));
            Assert.That(framing.PhysicalFramingEnabled, Is.False);
            Assert.That(rig.Renderer.QuiltTexture, Is.Null);
            Assert.That(BridgeMetalEditorPreview.IsActive, Is.False);
            Assert.That(BridgeEditorPreview.IsActive, Is.False);
            if (metal) Assert.That(host.GetComponent<BridgeMetalQuiltOutput>().IsRunning, Is.False);
            else Assert.That(host.GetComponent<BridgeWorkerQuiltOutput>().State, Is.EqualTo(BridgeWorkerQuiltOutput.WorkerState.Closed));
        }
        [TestCase(true)] [TestCase(false)]
        public void MissingDisplayKeepsGeometryAndOffersConnectionRetry(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True); Complete();
            adapter.Empty = true;
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True); Complete();
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out var settings, out _, out _), Is.True);
            Assert.That(settings.viewAspect, Is.EqualTo(9f/16).Within(1e-6));
            StringAssert.Contains("Connect a Looking Glass", BridgeEditorDisplayDiscovery.GetStatus(rig));
            adapter.Empty = false; adapter.Width = 3840; adapter.Height = 2160;
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True); Complete();
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out settings, out _, out _), Is.True);
            Assert.That(settings.viewAspect, Is.EqualTo(16f/9).Within(1e-6));
        }
        [TestCase(true)] [TestCase(false)]
        public void ConfigurationChangeDuringRequestDiscardsResult(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True);
            if (metal) host.GetComponent<BridgeMetalQuiltOutput>().displayOrdinal = 1;
            else host.GetComponent<BridgeWorkerQuiltOutput>().displayOrdinal = 1;
            Complete();
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out var settings, out var profile, out _), Is.True);
            Assert.That(settings.viewAspect, Is.EqualTo(1f));
            Assert.That(profile.source, Is.EqualTo(QuiltDisplaySettingsSource.LocalRendererSettings));
        }
        [TestCase(true)] [TestCase(false)]
        public void HiddenPreviewProducerCannotAcquireAuthoringMetadata(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            host.hideFlags = HideFlags.HideAndDontSave;
            BridgeEditorDisplayDiscovery.AutomaticRequestsEnabled = true;
            BridgeEditorDisplayDiscovery.EnsureRequested(rig.Renderer);
            BridgeEditorDisplayDiscovery.PumpForTests();
            Assert.That(adapter.BeginCount, Is.EqualTo(0));
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.False);
        }
        [TestCase(true)] [TestCase(false)]
        public void AutomaticFailureIsAttemptedOnceAndPlayCancellationCanRequeue(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            BridgeEditorDisplayDiscovery.AutomaticRequestsEnabled = true;
            adapter.Empty = true;
            BridgeEditorDisplayDiscovery.EnsureRequested(rig.Renderer);
            BridgeEditorDisplayDiscovery.PumpForTests(); Complete();
            BridgeEditorDisplayDiscovery.EnsureRequested(rig.Renderer);
            BridgeEditorDisplayDiscovery.PumpForTests();
            Assert.That(adapter.BeginCount, Is.EqualTo(1), "No recurring device polling after failure.");
            BridgeEditorDisplayDiscovery.Cancel();
            adapter.Empty = false;
            BridgeEditorDisplayDiscovery.EnsureRequested(rig.Renderer);
            BridgeEditorDisplayDiscovery.PumpForTests(); Complete();
            Assert.That(adapter.BeginCount, Is.EqualTo(2));
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out var settings, out _, out _), Is.True);
            Assert.That(settings.viewAspect, Is.EqualTo(9f/16).Within(1e-6));
        }
        [TestCase(true)] [TestCase(false)]
        public void AutomaticDimensionsPreservePhysicalSwitchUnitsAndSupportUndo(bool physicalEnabled)
        {
            ViewWeaveRig rig = Create(true);
            var physical = rig.Renderer.physicalFraming;
            physical.enabled = physicalEnabled; physical.visibleApertureWidthMillimeters = 120;
            physical.visibleApertureHeightMillimeters = 80; physical.unitsPerMeter = 2;
            float fov = rig.Renderer.settings.fieldOfView, focus = rig.Renderer.settings.cameraDistance;
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True); Complete();
            Assert.That(rig.Renderer.physicalFraming.enabled, Is.EqualTo(physicalEnabled));
            Assert.That(rig.Renderer.physicalFraming.unitsPerMeter, Is.EqualTo(2));
            Assert.That(rig.Renderer.settings.fieldOfView, Is.EqualTo(fov));
            Assert.That(rig.Renderer.settings.cameraDistance, Is.EqualTo(focus));
            UnityEditor.Undo.FlushUndoRecordObjects(); UnityEditor.Undo.PerformUndo();
            Assert.That(rig.Renderer.physicalFraming.visibleApertureWidthMillimeters, Is.EqualTo(120));
            Assert.That(rig.Renderer.physicalFraming.visibleApertureHeightMillimeters, Is.EqualTo(80));
            Assert.That(rig.Renderer.physicalFraming.enabled, Is.EqualTo(physicalEnabled));
        }
        [TestCase(true)] [TestCase(false)]
        public void MetadataAcquisitionDoesNotBypassFramingOwnerPolicy(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            rig.Renderer.physicalFraming.visibleApertureWidthMillimeters = 160;
            rig.Renderer.physicalFraming.visibleApertureHeightMillimeters = 90;
            Assert.That(QuiltRendererOperationLease.TryAcquire(rig.Renderer, QuiltRendererOperationKind.Recording,
                out QuiltRendererOperationLease lease, out string error), Is.True, error);
            using (lease)
            {
                Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out error), Is.True, error); Complete();
                Assert.That(rig.Renderer.physicalFraming.visibleApertureWidthMillimeters, Is.EqualTo(160));
                Assert.That(rig.Renderer.physicalFraming.visibleApertureHeightMillimeters, Is.EqualTo(90));
            }
        }
        [TestCase(true)] [TestCase(false)]
        public void MissingPhysicalCalibrationPreservesSavedAperture(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            rig.Renderer.physicalFraming.visibleApertureWidthMillimeters = 160;
            rig.Renderer.physicalFraming.visibleApertureHeightMillimeters = 90;
            adapter.Physical = false;
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True); Complete();
            Assert.That(rig.Renderer.physicalFraming.visibleApertureWidthMillimeters, Is.EqualTo(160));
            Assert.That(rig.Renderer.physicalFraming.visibleApertureHeightMillimeters, Is.EqualTo(90));
        }
        [TestCase(true)] [TestCase(false)]
        public void ReloadCancellationDoesNotApplyUnfinishedMetadata(bool metal)
        {
            ViewWeaveRig rig = Create(metal);
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True);
            BridgeEditorDisplayDiscovery.Cancel(); Complete();
            Assert.That(adapter.EndCount, Is.EqualTo(1));
            Assert.That(QuiltDisplaySettingsResolver.TryResolve(rig.Renderer, out _, out var profile, out _), Is.True);
            Assert.That(profile.source, Is.EqualTo(QuiltDisplaySettingsSource.LocalRendererSettings));
        }
        [Test]
        public void ConnectedProfileDoesNotHideFailedDiscoveryStatus()
        {
            ViewWeaveRig rig = Create(true);
            adapter.Empty = true;
            Assert.That(BridgeEditorDisplayDiscovery.TryRefresh(rig, out _), Is.True);
            Complete();
            string expected = BridgeEditorDisplayDiscovery.GetStatus(rig);
            StringAssert.Contains("Connect a Looking Glass", expected);
            UnityEditor.Editor inspector = null;
            try
            {
                QuiltDisplaySettingsResolver.RegisterProvider((QuiltRenderer renderer, out QuiltEditorDisplayProfile profile) =>
                {
                    profile = new QuiltEditorDisplayProfile(9f / 16f, 40f, QuiltDisplaySettingsSource.BridgeSession,
                        true, false, 0, 0, 0, "Active output profile");
                    return true;
                });
                inspector = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = inspector.CreateInspectorGUI();
                Assert.That(root.Q<HelpBox>("displayDiscoveryStatus").text, Is.EqualTo(expected));
                StringAssert.Contains("active output", root.Q<Label>("connectedDisplaysStatus").text);
            }
            finally
            {
                if (inspector != null) UnityEngine.Object.DestroyImmediate(inspector);
                QuiltDisplaySettingsResolver.RegisterProvider(BridgeEditorDisplaySettings.ResolveDisplayProfile);
            }
        }

        sealed class FakeAdapter : IBridgeDisplayDiscoveryNativeAdapter
        {
            internal uint Width = 1440, Height = 2560;
            internal bool Empty, closed, Physical = true;
            internal int EndCount, BeginCount;
            public IntPtr BeginMetadata(string path, string application) { closed = false; BeginCount++; return new IntPtr(17); }
            public int RequestDisplayRefresh(IntPtr handle, out ulong generation) { generation = 1; return 0; }
            public int Close(IntPtr handle) { closed = true; return 0; }
            public int EndMetadata(IntPtr handle) { EndCount++; return 0; }
            public int GetDisplaySnapshot(IntPtr handle, ref BridgeDisplayDiscoveryNative.SnapshotStatusV1 status,
                BridgeDisplayDiscoveryNative.SnapshotItemV1[] items, uint capacity, out uint count)
            {
                count = Empty ? 0u : 1u; status.itemCount = count; status.snapshotGeneration = status.requestedGeneration = 1;
                status.state = closed ? 4u : 1u; status.cleanupFinished = closed ? 1u : 0u;
                if (!Empty) items[0] = new BridgeDisplayDiscoveryNative.SnapshotItemV1
                {
                    structSize = (uint)Marshal.SizeOf(typeof(BridgeDisplayDiscoveryNative.SnapshotItemV1)), version = 1,
                    profile = new BridgeDisplayDiscoveryNative.DisplayInfo { index = 17, width = Width, height = Height,
                        aspect = (float)Width/Height, viewcone = 40, quiltWidth = 4092, quiltHeight = 4096, quiltColumns = 11, quiltRows = 6 },
                    physical = new BridgeDisplayDiscoveryNative.PhysicalDisplayInfoV1 { structSize = 24, version = 1,
                        pixelWidth = Physical ? Width : 0, pixelHeight = Physical ? Height : 0, dpi = Physical ? 300 : 0, valid = Physical ? 1u : 0u }
                };
                return 0;
            }
        }
    }
}
#endif
