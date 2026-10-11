#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using ViewWeave.Editor;

namespace ViewWeave.RigCameraCreationValidation
{
    public sealed class RigMetalInspectorTests
    {
        GameObject gameObject;
        UnityEditor.Editor inspector;
        InspectorHost window;
        sealed class InspectorHost : EditorWindow { }

        [TearDown]
        public void TearDown()
        {
            if (window != null) window.Close();
            if (inspector != null) Object.DestroyImmediate(inspector);
            if (gameObject != null) Object.DestroyImmediate(gameObject);
        }

        VisualElement CreateMetalInspector(out ViewWeaveRig rig, out BridgeMetalQuiltOutput output)
        {
            gameObject = new GameObject("Metal Rig Inspector Test");
            rig = gameObject.AddComponent<ViewWeaveRig>();
            output = gameObject.AddComponent<BridgeMetalQuiltOutput>();
            output.rig = rig;
            rig.SetOutput(output);
            inspector = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
            VisualElement root = inspector.CreateInspectorGUI();
            window = ScriptableObject.CreateInstance<InspectorHost>();
            window.Show();
            window.rootVisualElement.Add(root);
            return root;
        }

        [Test]
        public void RigOwnsQualityTransportAndCommonCameraControlsForMetal()
        {
            VisualElement root = CreateMetalInspector(out _, out BridgeMetalQuiltOutput output);
            EnumField quality = root.Q<EnumField>("sessionQuality");
            Assert.That(quality, Is.Not.Null);
            Assert.That(quality.label, Is.EqualTo("Output Quality"));
            Assert.That(root.Q<ObjectField>("referenceCamera"), Is.Not.Null);
            Assert.That(root.Q<EnumField>("viewRenderMode"), Is.Not.Null);
            Assert.That(root.Query<PropertyField>().ToList().Exists(field => field.bindingPath == "runtimePath"), Is.True);
            Assert.That(root.Q<Button>("startOutput"), Is.Not.Null);
            Assert.That(root.Q<Button>("stopOutput"), Is.Not.Null);
            Assert.That(root.Q<Button>("startOutput").enabledSelf, Is.False, "native output requires Play mode");
            quality.value = QuiltQualityPreset.Preview;
            Assert.That(output.quality, Is.EqualTo(QuiltQualityPreset.Preview), "the Rig UI must configure its attached Metal output");
        }

        [Test]
        public void QualityRejectsUnknownValuesAndRecordingOwnership()
        {
            CreateMetalInspector(out ViewWeaveRig rig, out BridgeMetalQuiltOutput output);
            Assert.That(output.TrySetQuality((QuiltQualityPreset)999, out string invalid), Is.False);
            Assert.That(invalid, Is.Not.Empty);
            Assert.That(output.quality, Is.EqualTo(QuiltQualityPreset.Balanced));
            Assert.That(QuiltRendererOperationLease.TryAcquire(rig.Renderer, QuiltRendererOperationKind.Recording, out QuiltRendererOperationLease lease, out string error), Is.True, error);
            using (lease)
            {
                Assert.That(output.TrySetQuality(QuiltQualityPreset.Native, out error), Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(output.quality, Is.EqualTo(QuiltQualityPreset.Balanced));
            }
            Assert.That(output.TrySetQuality(QuiltQualityPreset.Native, out error), Is.True, error);
        }

        [Test]
        public void SubpixelControlPersistsAndSupportsUndo()
        {
            VisualElement root = CreateMetalInspector(out _, out BridgeMetalQuiltOutput output);
            EnumField mode = root.Q<EnumField>("subpixelMode");
            Assert.That(mode, Is.Not.Null);
            Assert.That(output.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.Automatic));
            mode.value = BridgeSubpixelMode.ClassicRgb;
            Undo.FlushUndoRecordObjects();
            Assert.That(output.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.ClassicRgb));
            var serialized = new SerializedObject(output);
            Assert.That(serialized.FindProperty("subpixelMode").intValue, Is.EqualTo((int)BridgeSubpixelMode.ClassicRgb));
            Undo.PerformUndo();
            Assert.That(output.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.Automatic));
        }

        [Test]
        public void MetalOutputMissingSerializedModeDefaultsToAutomatic()
        {
            gameObject = new GameObject("Legacy Metal Output Serialization");
            BridgeMetalQuiltOutput output = gameObject.AddComponent<BridgeMetalQuiltOutput>();
            EditorJsonUtility.FromJsonOverwrite("{\"applicationName\":\"LegacyOutput\"}", output);
            Assert.That(output.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.Automatic));
        }

        [Test]
        public void SubpixelModeRejectsInvalidValuesAndRecordingOwnership()
        {
            VisualElement root = CreateMetalInspector(out ViewWeaveRig rig, out BridgeMetalQuiltOutput output);
            Assert.That(output.TrySetSubpixelMode((BridgeSubpixelMode)999, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(QuiltRendererOperationLease.TryAcquire(rig.Renderer, QuiltRendererOperationKind.Recording,
                out QuiltRendererOperationLease lease, out error), Is.True, error);
            using (lease)
            {
                Assert.That(output.TrySetSubpixelMode(BridgeSubpixelMode.ClassicRgb, out error), Is.False);
                root.Q<EnumField>("subpixelMode").value = BridgeSubpixelMode.ClassicRgb;
                Assert.That(output.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.Automatic));
            }
            Assert.That(output.TrySetSubpixelMode(BridgeSubpixelMode.CalibratedCells, out error), Is.True, error);
        }

        [TestCase(BridgeSubpixelMode.Automatic, false, true, false)]
        [TestCase(BridgeSubpixelMode.Automatic, true, true, true)]
        [TestCase(BridgeSubpixelMode.ClassicRgb, false, true, false)]
        [TestCase(BridgeSubpixelMode.ClassicRgb, true, true, false)]
        [TestCase(BridgeSubpixelMode.CalibratedCells, false, false, false)]
        [TestCase(BridgeSubpixelMode.CalibratedCells, true, true, true)]
        [TestCase((BridgeSubpixelMode)999, true, false, false)]
        public void SubpixelPolicyRequiresCellsWithoutGuessingCalibration(BridgeSubpixelMode mode,
            bool hasCells, bool accepted, bool expectedCells)
        {
            Assert.That(BridgeSubpixelPolicy.TryResolveCellUsage(mode, hasCells, out bool useCells, out string error), Is.EqualTo(accepted));
            Assert.That(useCells, Is.EqualTo(expectedCells));
            Assert.That(string.IsNullOrEmpty(error), Is.EqualTo(accepted));
        }

        [TestCase(BridgeSubpixelMode.Automatic, true)]
        [TestCase(BridgeSubpixelMode.ClassicRgb, false)]
        [TestCase(BridgeSubpixelMode.CalibratedCells, false)]
        [TestCase((BridgeSubpixelMode)999, false)]
        public void LegacyDirectSubpixelPolicyAcceptsAutomaticOnly(BridgeSubpixelMode mode, bool accepted)
        {
            Assert.That(BridgeSubpixelPolicy.ValidateBridgeManagedOutput(mode, out string error), Is.EqualTo(accepted));
            Assert.That(string.IsNullOrEmpty(error), Is.EqualTo(accepted));
        }
    }
}
#endif
