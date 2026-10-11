#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.Editor;
using Object = UnityEngine.Object;

namespace ViewWeave.RigCameraCreationValidation
{
    public sealed class RigAuthoringInspectorTests
    {
        const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        Object previousSelection;
        int initialUndoGroup;

        sealed class InspectorTestHostWindow : EditorWindow
        {
        }

        [SetUp]
        public void SetUp()
        {
            previousSelection = Selection.activeObject;
            initialUndoGroup = Undo.GetCurrentGroup();
        }

        [TearDown]
        public void TearDown()
        {
            Undo.RevertAllDownToGroup(initialUndoGroup);
            Selection.activeObject = previousSelection;
        }

        [UnityTest]
        public IEnumerator AuthoringWindowKeepsItsRigWhenSelectionChangesAndOnlyUpdatesFocusTarget()
        {
            GameObject rigObject = new GameObject("Authoring Window Rig");
            GameObject selectedTarget = new GameObject("Selected Focus Target");
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                renderer.settings.cameraDistance = 3f;
                ViewWeaveRigAuthoringWindow window = ViewWeaveRigAuthoringWindow.OpenForRig(rig);
                Assert.That(GetField<ViewWeaveRig>(window, "rig"), Is.SameAs(rig));

                Selection.activeGameObject = selectedTarget;
                yield return null;

                Assert.That(GetField<ViewWeaveRig>(window, "rig"), Is.SameAs(rig), "selection changes must not retarget the authoring window");
                Assert.That(GetField<Object>(window, "focusTarget"), Is.SameAs(selectedTarget), "selection changes should update only the focus target");
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(3f), "selection changes must not configure the Rig or alter focus");
                window.Close();
            }
            finally
            {
                if (EditorWindow.HasOpenInstances<ViewWeaveRigAuthoringWindow>())
                {
                    foreach (ViewWeaveRigAuthoringWindow window in Resources.FindObjectsOfTypeAll<ViewWeaveRigAuthoringWindow>())
                        if (window != null) window.Close();
                }
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(selectedTarget);
            }
            yield break;
        }

        [Test]
        public void OpeningForAnotherRigUsesTheSamePinnedTargetAssignmentPath()
        {
            GameObject firstObject = new GameObject("First Authoring Rig");
            GameObject secondObject = new GameObject("Second Authoring Rig");
            try
            {
                ViewWeaveRig first = firstObject.AddComponent<ViewWeaveRig>();
                ViewWeaveRig second = secondObject.AddComponent<ViewWeaveRig>();
                ViewWeaveRigAuthoringWindow window = ViewWeaveRigAuthoringWindow.OpenForRig(first);

                Assert.That(ViewWeaveRigAuthoringWindow.OpenForRig(second), Is.SameAs(window));
                Assert.That(GetField<ViewWeaveRig>(window, "rig"), Is.SameAs(second));
                Assert.That(ShouldStopPreview(first, second, first, true, false), Is.True, "switching away from the preview Rig should request stop");
                Assert.That(ShouldStopPreview(second, null, first, true, false), Is.False, "closing after switching away must not stop the old Rig's pending preview");
                Assert.That(ShouldStopPreview(first, first, first, true, false), Is.False, "assigning the same Rig must preserve its preview");
                Assert.That(ShouldStopPreview(first, second, second, true, false), Is.False, "a preview targeting another Rig must not be stopped");
                Assert.That(ShouldStopPreview(first, second, first, false, true), Is.False, "a stop already in progress must not be requested again");
                window.Close();
            }
            finally
            {
                foreach (ViewWeaveRigAuthoringWindow window in Resources.FindObjectsOfTypeAll<ViewWeaveRigAuthoringWindow>())
                    if (window != null) window.Close();
                Object.DestroyImmediate(firstObject);
                Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void RigOnValidatePreservesComponentsAndSettingsWhileEditorPreviewResourcesAreRetained()
        {
            GameObject rigObject = new GameObject("Retained Preview Rig");
            Object retentionRecord = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rig.Renderer;
                Assert.That(renderer, Is.Not.Null);
                renderer.settings.fieldOfView = 73f;
                renderer.settings.cameraDistance = 4.25f;

                Type retentionType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType(
                    "ViewWeave.BridgeEditorPreviewRetentionRecord");
                Assert.That(retentionType, Is.Not.Null);
                MethodInfo create = retentionType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(create, Is.Not.Null);
                retentionRecord = (Object)create.Invoke(null, new object[] { rigObject, renderer });
                Assert.That(retentionRecord, Is.Not.Null);

                MethodInfo beginRetention = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", InstancePrivate);
                Assert.That(beginRetention, Is.Not.Null);
                Assert.That(beginRetention.Invoke(renderer, new[] { retentionRecord }), Is.EqualTo(true));
                PropertyInfo mutationRequirement = typeof(QuiltRenderer).GetProperty("HasMutationOwnerRequirement", InstancePrivate);
                Assert.That(mutationRequirement, Is.Not.Null);
                Assert.That(mutationRequirement.GetValue(renderer), Is.EqualTo(false),
                    "the regression must exercise serialized preview retention without a registered mutation owner");

                Invoke(rig, "OnValidate");

                Assert.That(GetField<Object>(renderer, "editorPreviewRetentionOwner"), Is.SameAs(retentionRecord));
                Assert.That(renderer.settings.fieldOfView, Is.EqualTo(73f).Within(0.001f),
                    "OnValidate must not reapply the Rig profile while preview resources are retained");
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(4.25f).Within(0.001f));
                Assert.That(rig.Renderer, Is.SameAs(renderer));
            }
            finally
            {
                if (retentionRecord != null) Object.DestroyImmediate(retentionRecord);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void RigAwakePreservesRetainedPreviewBindingsAndExplicitConfigureRemainsRejected()
        {
            GameObject rigObject = new GameObject("Retained Preview Awake Rig");
            rigObject.SetActive(false);
            Object retentionRecord = null;
            try
            {
                Camera camera = rigObject.AddComponent<Camera>();
                QuiltRenderer renderer = rigObject.AddComponent<QuiltRenderer>();
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                BridgeWorkerQuiltOutput output = rigObject.AddComponent<BridgeWorkerQuiltOutput>();
                output.initializeOnEnable = false;
                rig.SetOutput(output);
                Assert.That(rig.ConfigureComponents(), Is.True);
                renderer.settings.fieldOfView = 71f;
                renderer.settings.cameraDistance = 4.25f;

                Type retentionType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType(
                    "ViewWeave.BridgeEditorPreviewRetentionRecord", true);
                MethodInfo create = retentionType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(create, Is.Not.Null);
                retentionRecord = (Object)create.Invoke(null, new object[] { rigObject, renderer });
                Assert.That(retentionRecord, Is.Not.Null);
                MethodInfo beginRetention = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", InstancePrivate);
                Assert.That(beginRetention, Is.Not.Null);
                Assert.That(beginRetention.Invoke(renderer, new[] { retentionRecord }), Is.EqualTo(true));

                Invoke(rig, "Awake");

                Assert.That(rig.Renderer, Is.SameAs(renderer));
                Assert.That(renderer.sourceCamera, Is.SameAs(camera));
                Assert.That(renderer.OutputBehaviour, Is.SameAs(output));
                Assert.That(camera.enabled, Is.False);
                Assert.That(renderer.settings.fieldOfView, Is.EqualTo(71f).Within(0.001f));
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(4.25f).Within(0.001f));
                LogAssert.Expect(LogType.Error,
                    "ViewWeaveRig configuration failed: rig components cannot be reconfigured while the renderer is owned: An editor preview owner has no active mutation-policy registration.");
                Assert.That(rig.ConfigureComponents(), Is.False,
                    "explicit configuration must remain rejected while preview resources are retained");
                Assert.That(renderer.settings.fieldOfView, Is.EqualTo(71f).Within(0.001f));
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(4.25f).Within(0.001f));
            }
            finally
            {
                if (retentionRecord != null) Object.DestroyImmediate(retentionRecord);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void RigAwakeConfiguresAnUnownedRigNormally()
        {
            GameObject rigObject = new GameObject("Unowned Awake Rig");
            rigObject.SetActive(false);
            try
            {
                Camera camera = rigObject.AddComponent<Camera>();
                QuiltRenderer renderer = rigObject.AddComponent<QuiltRenderer>();
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                camera.enabled = true;
                Assert.That(rig.Renderer, Is.SameAs(renderer));
                renderer.sourceCamera = null;

                Invoke(rig, "Awake");

                Assert.That(rig.Renderer, Is.SameAs(renderer));
                Assert.That(renderer.sourceCamera, Is.SameAs(camera));
                Assert.That(camera.enabled, Is.False,
                    "an unowned Rig Awake must still perform its regular source-camera configuration");
            }
            finally
            {
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void TechnicalComponentInspectorsLockWithRendererOwnerAndUnlockAfterOwnerRelease()
        {
            GameObject ownerObject = new GameObject("Owned Technical Inspector Rig");
            ownerObject.SetActive(false);
            Object retentionRecord = null;
            try
            {
                ownerObject.AddComponent<Camera>();
                QuiltRenderer renderer = ownerObject.AddComponent<QuiltRenderer>();
                ownerObject.AddComponent<ViewWeaveRig>();
                BridgeWorkerQuiltOutput output = ownerObject.AddComponent<BridgeWorkerQuiltOutput>();
                output.initializeOnEnable = false;
                ViewWeaveBridgeSession session = ownerObject.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = false;

                Type hdrpCameraType = Type.GetType("ViewWeave.HDRP.HdrpQuiltCamera, ViewWeave.HDRP", true);
                Object hdrpCamera = ownerObject.AddComponent(hdrpCameraType);
                Type hdrpInspectorType = Type.GetType("ViewWeave.HDRP.Editor.HdrpQuiltCameraInspector, ViewWeave.HDRP.Editor", true);
                Type outputInspectorType = typeof(ViewWeaveRigAuthoringWindow).Assembly.GetType("ViewWeave.Editor.BridgeWorkerQuiltOutputInspector", true);
                Type sessionInspectorType = typeof(ViewWeaveRigAuthoringWindow).Assembly.GetType("ViewWeave.Editor.ViewWeaveBridgeSessionInspector", true);

                AssertInspectorLock(outputInspectorType, output, false, "an unowned output remains editable");
                AssertInspectorLock(sessionInspectorType, session, false, "an unowned Session remains editable");
                AssertInspectorLock(hdrpInspectorType, hdrpCamera, false, "an unowned HDRP guard remains editable");

                Type retentionType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeEditorPreviewRetentionRecord", true);
                MethodInfo create = retentionType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(create, Is.Not.Null);
                retentionRecord = (Object)create.Invoke(null, new object[] { ownerObject, renderer });
                Assert.That(retentionRecord, Is.Not.Null);

                MethodInfo beginRetention = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", InstancePrivate);
                Assert.That(beginRetention, Is.Not.Null);
                Assert.That(beginRetention.Invoke(renderer, new[] { retentionRecord }), Is.EqualTo(true));

                AssertInspectorLock(outputInspectorType, output, true, "an active Rig owner must lock raw output fields");
                AssertInspectorLock(sessionInspectorType, session, true, "an active Rig owner must lock raw Session fields");
                AssertInspectorLock(hdrpInspectorType, hdrpCamera, true, "an active Rig owner must lock raw HDRP guard fields");
            }
            finally
            {
                if (retentionRecord != null) Object.DestroyImmediate(retentionRecord);
                Object.DestroyImmediate(ownerObject);
            }
        }

        [Test]
        public void SceneViewControlsUseSharedEditorPreferences()
        {
            bool oldNearFar = QuiltRendererSceneViewControls.ShowNearFar;
            bool oldExtreme = QuiltRendererSceneViewControls.ShowExtremeViews;
            bool oldCursor = QuiltRendererSceneViewControls.Show3DFocusCursor;
            try
            {
                QuiltRendererSceneViewControls.ShowNearFar = !oldNearFar;
                QuiltRendererSceneViewControls.ShowExtremeViews = !oldExtreme;
                QuiltRendererSceneViewControls.Show3DFocusCursor = !oldCursor;
                Assert.That(QuiltRendererSceneViewControls.ShowNearFar, Is.EqualTo(!oldNearFar));
                Assert.That(QuiltRendererSceneViewControls.ShowExtremeViews, Is.EqualTo(!oldExtreme));
                Assert.That(QuiltRendererSceneViewControls.Show3DFocusCursor, Is.EqualTo(!oldCursor));
            }
            finally
            {
                QuiltRendererSceneViewControls.ShowNearFar = oldNearFar;
                QuiltRendererSceneViewControls.ShowExtremeViews = oldExtreme;
                QuiltRendererSceneViewControls.Show3DFocusCursor = oldCursor;
            }
        }

        [Test]
        public void RigQualityProfileRemainsEditableWhenSessionIsAttached()
        {
            GameObject rigObject = new GameObject("Profile With Session Rig");
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow window = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                rigObject.AddComponent<ViewWeaveBridgeSession>();
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                window = ScriptableObject.CreateInstance<InspectorTestHostWindow>();
                window.Show();
                window.rootVisualElement.Add(root);

                PropertyField profileField = root.Q<PropertyField>("rigQualityProfile");
                Assert.That(profileField, Is.Not.Null);
                Assert.That(profileField.bindingPath, Is.EqualTo("profile"));
                EnumField sessionQuality = root.Q<EnumField>("sessionQuality");
                Assert.That(sessionQuality, Is.Not.Null);
                Assert.That(sessionQuality.value, Is.EqualTo(QuiltQualityPreset.Balanced));
                Assert.That(sessionQuality.enabledInHierarchy, Is.True, "an idle Session should expose its quality setting");
                EnumField subpixelMode = root.Q<EnumField>("subpixelMode");
                Assert.That(subpixelMode, Is.Not.Null, "Windows Session must expose the shared subpixel setting");
                Assert.That(subpixelMode.panel, Is.Not.Null);
                Assert.That(subpixelMode.enabledInHierarchy, Is.True);
                var session = rigObject.GetComponent<ViewWeaveBridgeSession>();
                Assert.That(session.CanChangeSubpixelMode(out string reason), Is.True, reason);
                Assert.That(subpixelMode.value, Is.EqualTo(BridgeSubpixelMode.Automatic));
                subpixelMode.value = BridgeSubpixelMode.CalibratedCells;
                Assert.That(session.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.CalibratedCells));
                Assert.That(new SerializedObject(session).FindProperty("subpixelMode").intValue,
                    Is.EqualTo((int)BridgeSubpixelMode.CalibratedCells));
                EnumField renderMode = root.Q<EnumField>("viewRenderMode");
                Assert.That(renderMode, Is.Not.Null);
                Assert.That(renderMode.value, Is.EqualTo(QuiltViewRenderMode.Full));
                Assert.That(FindFoldout(root, "Rendering").value, Is.True, "Rendering should be open by default");

                SerializedObject rigData = new SerializedObject(rig);
                SerializedProperty width = rigData.FindProperty("profile").FindPropertyRelative("width");
                width.intValue = 2048;
                rigData.ApplyModifiedProperties();
                Assert.That(rig.Profile.QuiltWidth, Is.EqualTo(2048), "the profile field must edit Rig profile data used by local preview");
            }
            finally
            {
                if (window != null) window.Close();
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ConnectedDisplaySizeCopiesCalibrationAndPreservesFramingToggleAndUndoRestoresBoth(bool physicalFramingEnabled)
        {
            GameObject rigObject = new GameObject("Connected Calibration Copy Rig");
            UnityEditor.Editor editor = null;
            QuiltDisplaySettingsResolver.RegisterProvider(ProvideConnectedCalibration);
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Assert.That(renderer.physicalFraming.enabled, Is.False, "physical 1:1 framing must default off");
                renderer.physicalFraming.enabled = physicalFramingEnabled;
                renderer.physicalFraming.visibleApertureWidthMillimeters = 120f;
                renderer.physicalFraming.visibleApertureHeightMillimeters = 80f;
                renderer.physicalFraming.unitsPerMeter = 2f;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                object view = GetField<object>(editor, "view");
                Button copyButton = root.Q<Button>("copyConnectedDisplaySize");
                Assert.That(copyButton, Is.Not.Null);
                Assert.That(copyButton.enabledInHierarchy, Is.True);

                Invoke(view, "CopyConnectedDisplaySize");
                Assert.That(renderer.physicalFraming.enabled, Is.EqualTo(physicalFramingEnabled), "copying dimensions must preserve the current physical framing switch state");
                Assert.That(renderer.physicalFraming.visibleApertureWidthMillimeters, Is.EqualTo(344.65018f).Within(0.01f));
                Assert.That(renderer.physicalFraming.visibleApertureHeightMillimeters, Is.EqualTo(193.86572f).Within(0.01f));
                Assert.That(renderer.physicalFraming.unitsPerMeter, Is.EqualTo(2f), "copy must preserve project units per meter");

                Undo.PerformUndo();
                Assert.That(renderer.physicalFraming.enabled, Is.EqualTo(physicalFramingEnabled));
                Assert.That(renderer.physicalFraming.visibleApertureWidthMillimeters, Is.EqualTo(120f).Within(0.001f));
                Assert.That(renderer.physicalFraming.visibleApertureHeightMillimeters, Is.EqualTo(80f).Within(0.001f));
                Assert.That(renderer.physicalFraming.unitsPerMeter, Is.EqualTo(2f).Within(0.001f));
            }
            finally
            {
                QuiltDisplaySettingsResolver.RegisterProvider(null);
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void ConnectedDisplayCopyWithoutValidCalibrationReportsErrorAndLeavesRigUntouched()
        {
            GameObject rigObject = new GameObject("Missing Calibration Copy Rig");
            UnityEditor.Editor editor = null;
            QuiltDisplaySettingsResolver.RegisterProvider(NoConnectedCalibration);
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                renderer.physicalFraming.enabled = false;
                renderer.physicalFraming.visibleApertureWidthMillimeters = 123f;
                renderer.physicalFraming.visibleApertureHeightMillimeters = 77f;
                renderer.physicalFraming.unitsPerMeter = 1.5f;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                object view = GetField<object>(editor, "view");

                Invoke(view, "CopyConnectedDisplaySize");
                Assert.That(renderer.physicalFraming.enabled, Is.False);
                Assert.That(renderer.physicalFraming.visibleApertureWidthMillimeters, Is.EqualTo(123f).Within(0.001f));
                Assert.That(renderer.physicalFraming.visibleApertureHeightMillimeters, Is.EqualTo(77f).Within(0.001f));
                Assert.That(renderer.physicalFraming.unitsPerMeter, Is.EqualTo(1.5f).Within(0.001f));
                StringAssert.Contains("No valid display calibration is cached", root.Q<Label>("physicalFramingStatus").text);
            }
            finally
            {
                QuiltDisplaySettingsResolver.RegisterProvider(null);
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void RigAuthoringExposesExplicitConnectedDisplayRefreshWithoutStartingSession()
        {
            GameObject rigObject = new GameObject("Display Refresh Controls Rig");
            UnityEditor.Editor editor = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                var output = rigObject.AddComponent<BridgeWorkerQuiltOutput>();
                output.initializeOnEnable = false;
                rig.SetOutput(output);
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();

                Button refresh = root.Q<Button>("refreshConnectedDisplays");
                Assert.That(refresh, Is.Not.Null);
                Assert.That(refresh.text, Is.EqualTo("Refresh Connected Displays"));
                Assert.That(root.Q<Label>("connectedDisplaysStatus"), Is.Not.Null);
                Assert.That(rig.GetComponent<ViewWeaveBridgeSession>(), Is.Null, "display metadata refresh controls must not add a Bridge Session");
                Assert.That(output.Ready, Is.False);
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void RecordingLeaseRejectsFramingSamplingAndSessionQualityWithoutMutation()
        {
            GameObject rigObject = new GameObject("Mutation Policy Lease Rig");
            QuiltRendererOperationLease lease = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                var session = rigObject.AddComponent<ViewWeaveBridgeSession>();
                Assert.That(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Recording,
                    out lease, out string leaseError), Is.True, leaseError);
                QuiltCameraFraming beforeFraming = default;
                Assert.That(renderer.TryGetFraming(out beforeFraming), Is.True);
                QuiltViewRenderMode beforeMode = renderer.viewRenderMode;
                QuiltQualityPreset beforeQuality = session.Quality;

                Assert.That(renderer.TrySetFraming(new QuiltCameraFraming(beforeFraming.VerticalFieldOfView + 1f,
                    beforeFraming.FocusDistance, beforeFraming.PhysicalFramingEnabled,
                    beforeFraming.ApertureWidthMillimeters, beforeFraming.ApertureHeightMillimeters, beforeFraming.UnitsPerMeter),
                    out string framingError), Is.False);
                Assert.That(framingError, Does.Contain("recording or playback lease"));
                Assert.That(renderer.TrySetViewRenderMode(QuiltViewRenderMode.Half, out string modeError), Is.False);
                Assert.That(modeError, Does.Contain("recording or playback lease"));
                Assert.That(session.SetQuality(QuiltQualityPreset.Native), Is.False);

                Assert.That(renderer.TryGetFraming(out QuiltCameraFraming afterFraming), Is.True);
                Assert.That(afterFraming.VerticalFieldOfView, Is.EqualTo(beforeFraming.VerticalFieldOfView).Within(0.001f));
                Assert.That(renderer.viewRenderMode, Is.EqualTo(beforeMode));
                Assert.That(session.Quality, Is.EqualTo(beforeQuality));
            }
            finally
            {
                if (lease != null) lease.Dispose();
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void AutoRenderToggleUsesRigValidationAndKeepsInvalidEnableOff()
        {
            GameObject rigObject = new GameObject("Auto Render Validation Rig");
            UnityEditor.Editor editor = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                Toggle autoRender = root.Q<Toggle>("autoRender");
                object view = GetField<object>(editor, "view");

                Assert.That(autoRender, Is.Not.Null);
                autoRender.SetValueWithoutNotify(true);
                ChangeEvent<bool> change = ChangeEvent<bool>.GetPooled(false, true);
                try { Invoke(view, "OnAutoRenderChanged", change); }
                finally { change.Dispose(); }

                Assert.That(rig.AutoRender, Is.False, "the UI must preserve ViewWeaveRig validation when enabling auto render");
                Assert.That(renderer.autoRender, Is.False);
                Assert.That(root.Q<HelpBox>("autoRenderWarning").text, Is.Not.Empty);
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void HdrpGuardSettingsAreExposedFromTheReferencedGuard()
        {
            Type guardType = null;
            foreach (Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                guardType = assembly.GetType("ViewWeave.HDRP.HdrpQuiltCamera", false);
                if (guardType != null) break;
            }
            Assert.That(guardType, Is.Not.Null, "HDRP package assembly must be loaded in the test project");

            GameObject rigObject = new GameObject("HDRP Guard Settings Rig");
            UnityEditor.Editor editor = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                MonoBehaviour guard = rigObject.AddComponent(guardType) as MonoBehaviour;
                rig.SetGuard(guard);
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();

                Assert.That(root.Q<PropertyField>("adaptUnsupportedEffects"), Is.Not.Null);
                Assert.That(root.Q<PropertyField>("fallbackFixedExposure"), Is.Not.Null);
                PropertyField evField = root.Q<PropertyField>("fallbackFixedExposureEV");
                Assert.That(evField, Is.Not.Null);
                Assert.That(evField.bindingPath, Is.EqualTo("fallbackFixedExposureEV"));

                SerializedObject guardData = new SerializedObject(guard);
                guardData.FindProperty("fallbackFixedExposureEV").floatValue = 14f;
                guardData.ApplyModifiedProperties();
                Assert.That(guardType.GetField("fallbackFixedExposureEV").GetValue(guard), Is.EqualTo(14f));
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void AssignedFocusTargetActionUpdatesRendererAndUndoRestoresPriorFocus()
        {
            GameObject rigObject = new GameObject("Inspector Focus Rig");
            GameObject focusObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object priorActiveSelection = Selection.activeObject;
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow host = null;
            VisualElement root = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                renderer.settings.cameraDistance = 2f;
                focusObject.transform.position = new Vector3(0f, 0f, 7f);
                Selection.activeObject = null;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                root = editor.CreateInspectorGUI();
                ObjectField focusField = root.Q<ObjectField>("focusTarget");
                Assert.That(focusField, Is.Not.Null);
                host = EditorWindow.CreateWindow<InspectorTestHostWindow>();
                host.Show();
                host.rootVisualElement.Add(root);
                focusField.value = focusObject;
                object view = GetField<object>(editor, "view");

                Assert.That(GetField<Object>(editor, "focusTarget"), Is.SameAs(focusObject));
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(2f), "choosing a target must not edit focus until the explicit action runs");

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Rig Inspector Focus Test");
                Invoke(view, "SetFocusFromTarget");
                Undo.FlushUndoRecordObjects();
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(7f).Within(0.001f));
                Undo.PerformUndo();
                Assert.That(renderer.settings.cameraDistance, Is.EqualTo(2f).Within(0.001f));
            }
            finally
            {
                if (host != null)
                {
                    if (root != null) host.rootVisualElement.Remove(root);
                    host.Close();
                }
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(focusObject);
                Selection.activeObject = priorActiveSelection;
            }
        }

        [Test]
        public void FocusAndFitControlsExplainAndEnforcePhysicalFramingModes()
        {
            GameObject rigObject = new GameObject("Inspector Focus Modes Rig");
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow window = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                renderer.physicalFraming = new QuiltPhysicalFraming
                {
                    enabled = false,
                    visibleApertureWidthMillimeters = 344.65018f,
                    visibleApertureHeightMillimeters = 193.86572f,
                    unitsPerMeter = 1f
                };
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                window = ScriptableObject.CreateInstance<InspectorTestHostWindow>();
                window.Show();
                window.rootVisualElement.Add(root);
                Foldout cameraFoldout = root.Q<Foldout>("cameraAndFocus");
                Toggle physicalToggle = root.Q<Toggle>("physicalFramingEnabled");
                Button focusButton = root.Query<Button>().ToList().Find(button => button.text == "Set Focus From Target Center");
                Button fitButton = root.Q<Button>("fitViewToTarget");
                HelpBox focusHelp = root.Q<HelpBox>("focusModeHelp");
                HelpBox fitHelp = root.Q<HelpBox>("fitViewHelp");

                Assert.That(cameraFoldout, Is.Not.Null);
                Assert.That(physicalToggle, Is.Not.Null);
                Assert.That(physicalToggle.GetFirstAncestorOfType<Foldout>(), Is.SameAs(cameraFoldout),
                    "physical framing switch is visible directly in Camera & Focus, outside the dimensions foldout");
                Assert.That(focusButton, Is.Not.Null);
                Assert.That(fitButton, Is.Not.Null);
                Assert.That(fitButton.enabledSelf, Is.True, "fit is enabled while physical framing is off");
                Assert.That(focusHelp.text, Does.Contain("keeps the current vertical FOV"));
                Assert.That(root.Query<Button>().ToList().Exists(button => button.text == "Use Selection"), Is.False);

                physicalToggle.value = true;
                Assert.That(renderer.physicalFraming.enabled, Is.True);
                Assert.That(root.Query<Button>().ToList().Exists(button => button.text == "Set Physical Focus From Target Center"), Is.True);
                Assert.That(fitButton.enabledSelf, Is.False, "fit is disabled while physical framing is on");
                Assert.That(fitHelp.text, Does.Contain("Turn off Use Physical 1:1 Framing"));
                Assert.That(focusHelp.text, Does.Contain("adjusts vertical FOV"));
            }
            finally
            {
                if (window != null) window.Close();
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
            }
        }

        [Test]
        public void ExplicitFocusTargetSurvivesInspectorRebuildAndUndoRefresh()
        {
            GameObject rigObject = new GameObject("Explicit Focus Target Rig");
            GameObject selectedObject = new GameObject("Current Selection");
            GameObject explicitTarget = new GameObject("Explicit Focus Target");
            UnityEditor.Editor editor = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                Selection.activeGameObject = selectedObject;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                ObjectField focusField = root.Q<ObjectField>("focusTarget");
                Assert.That(focusField, Is.Not.Null);

                FieldInfo focusTargetState = typeof(ViewWeaveRigInspector).GetField("focusTarget", InstancePrivate);
                Assert.That(focusTargetState, Is.Not.Null);
                focusTargetState.SetValue(editor, explicitTarget);
                object view = GetField<object>(editor, "view");
                Invoke(view, "Rebuild");

                Assert.That(root.Q<ObjectField>("focusTarget").value, Is.SameAs(explicitTarget),
                    "rebuilding after Undo must preserve a manually assigned Focus Target instead of replacing it with Selection");
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(selectedObject);
                Object.DestroyImmediate(explicitTarget);
            }
        }

        [Test]
        public void FollowedFovFieldTargetsReferenceCameraAndManualFovTargetsRendererSettings()
        {
            GameObject rigObject = new GameObject("FOV Inspector Rig");
            GameObject referenceObject = new GameObject("Reference Camera");
            UnityEditor.Editor editor = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Camera sourceCamera = renderer.sourceCamera;
                float originalSourceFov = sourceCamera.fieldOfView;
                Camera referenceCamera = referenceObject.AddComponent<Camera>();
                renderer.referenceCamera = referenceCamera;
                renderer.followReferenceFieldOfView = true;
                renderer.settings.fieldOfView = 51f;
                Selection.activeGameObject = rigObject;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();

                FloatField followedField = root.Q<FloatField>("referenceFieldOfView");
                Assert.That(followedField, Is.Not.Null);
                Assert.That(followedField.label, Is.EqualTo("Reference Vertical Field of View"));
                Assert.That(root.Q<FloatField>("rendererFieldOfView"), Is.Null);

                Invoke(GetField<object>(editor, "view"), "OnFieldOfViewChanged", 79f);
                Assert.That(referenceCamera.fieldOfView, Is.EqualTo(79f).Within(0.001f));
                Assert.That(sourceCamera.fieldOfView, Is.EqualTo(originalSourceFov).Within(0.001f), "reference FOV edits must not be saved to the dedicated source camera");
                Assert.That(renderer.settings.fieldOfView, Is.EqualTo(51f).Within(0.001f), "followed FOV edits must not be saved to the preview copy");

                SerializedObject rendererSerialized = new SerializedObject(renderer);
                rendererSerialized.FindProperty("followReferenceFieldOfView").boolValue = false;
                rendererSerialized.ApplyModifiedProperties();
                Invoke(GetField<object>(editor, "view"), "RebuildFieldOfView");
                FloatField manualField = root.Q<FloatField>("rendererFieldOfView");
                Assert.That(manualField, Is.Not.Null);
                Assert.That(manualField.label, Is.EqualTo("Vertical Field of View"));
                Assert.That(root.Q<FloatField>("referenceFieldOfView"), Is.Null);

                Invoke(GetField<object>(editor, "view"), "OnFieldOfViewChanged", 63f);
                Assert.That(renderer.settings.fieldOfView, Is.EqualTo(63f).Within(0.001f));
                Assert.That(referenceCamera.fieldOfView, Is.EqualTo(79f).Within(0.001f));
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(referenceObject);
            }
        }

        [Test]
        public void FocusTargetFromDifferentPreviewStageIsRejected()
        {
            GameObject rigObject = new GameObject("Stage Rig");
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            GameObject target = new GameObject("Other Stage Target");
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                SceneManager.MoveGameObjectToScene(target, previewScene);
                Assert.That(QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, target, out _, out string reason), Is.False);
                Assert.That(reason, Does.Contain("Isolated Preview Scenes are not supported"));
                renderer.referenceCamera = target.AddComponent<Camera>();
                Assert.That(QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, 5f, 0f), Is.False,
                    "orbit must reject a Reference Camera in a different scene or Prefab Stage");
            }
            finally
            {
                Object.DestroyImmediate(rigObject);
                if (target != null) Object.DestroyImmediate(target);
                if (previewScene.IsValid() && previewScene.isLoaded) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        [Test]
        public void FocusTargetFromLoadedAdditiveSceneInMainStageIsAccepted()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            string fixturePath = AssetDatabase.GenerateUniqueAssetPath("Assets/RigCameraCreationValidation/Editor/RigAuthoringAdditiveMainStageFixture.unity");
            Scene mainScene = default;
            Scene additiveScene = default;
            GameObject rigObject = null;
            GameObject target = null;
            try
            {
                mainScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.That(EditorSceneManager.SaveScene(mainScene, fixturePath), Is.True, "the additive-scene fixture requires a saved Main Stage scene");
                additiveScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                EditorSceneManager.SetActiveScene(mainScene);

                rigObject = new GameObject("Additive Stage Rig");
                target = new GameObject("Additive Stage Target");
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                SceneManager.MoveGameObjectToScene(target, additiveScene);
                Assert.That(QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, target, out _, out string reason), Is.True, reason);
            }
            finally
            {
                if (target != null) Object.DestroyImmediate(target);
                if (rigObject != null) Object.DestroyImmediate(rigObject);
                if (additiveScene.IsValid() && additiveScene.isLoaded) EditorSceneManager.CloseScene(additiveScene, true);
                if (mainScene.IsValid() && mainScene.isLoaded) EditorSceneManager.CloseScene(mainScene, true);
                if (previousSetup != null && previousSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                AssetDatabase.DeleteAsset(fixturePath);
            }
        }

        [Test]
        public void ReferenceCameraAssignmentFromAnotherPreviewStageIsRejected()
        {
            GameObject rigObject = new GameObject("Reference Stage Rig");
            Scene previewScene = EditorSceneManager.NewPreviewScene();
            GameObject cameraObject = new GameObject("Foreign Reference Camera");
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow host = null;
            VisualElement root = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Camera foreignCamera = cameraObject.AddComponent<Camera>();
                SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                root = editor.CreateInspectorGUI();
                host = EditorWindow.CreateWindow<InspectorTestHostWindow>();
                host.Show();
                host.rootVisualElement.Add(root);
                ObjectField field = root.Q<ObjectField>("referenceCamera");
                object view = GetField<object>(editor, "view");

                SendObjectFieldChange(field, null, foreignCamera);
                Assert.That(renderer.referenceCamera, Is.Null,
                    "the selection resolver must still reject a Camera from another Preview Stage");
                Assert.That(root.Q<HelpBox>("referenceCameraWarning").text, Does.Contain("Isolated Preview Scenes are not supported"));
                Invoke(view, "Refresh", false);
                Assert.That(renderer.referenceCamera, Is.Null,
                    "a rejected selection must not replace the existing reference after an inspector refresh");
                Assert.That(root.Q<HelpBox>("referenceCameraWarning").text, Does.Contain("Isolated Preview Scenes are not supported"),
                    "refresh must preserve the reason for the rejected selection");

                Assert.That(Invoke(view, "TryAssignReferenceCamera", foreignCamera), Is.EqualTo(false));
                Assert.That(renderer.referenceCamera, Is.Null, "an unsafe reference must not be assigned from the authoring UI");
                Assert.That(root.Q<HelpBox>("referenceCameraWarning").text, Does.Contain("Isolated Preview Scenes are not supported"));
                Assert.That(root.Q<FloatField>("rendererFieldOfView"), Is.Not.Null);

                renderer.referenceCamera = foreignCamera;
                Invoke(view, "RefreshReferenceCameraState");
                Assert.That(root.Q<FloatField>("referenceFieldOfView"), Is.Null, "an unsafe existing reference must not expose an editable FOV field");
                Assert.That(root.Q<HelpBox>("referenceCameraWarning").text, Does.Contain("unavailable"));
            }
            finally
            {
                if (host != null)
                {
                    if (root != null) host.rootVisualElement.Remove(root);
                    host.Close();
                }
                if (editor != null) Object.DestroyImmediate(editor);
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
                if (rigObject != null) Object.DestroyImmediate(rigObject);
                if (previewScene.IsValid() && previewScene.isLoaded) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        [Test]
        public void ReferenceCameraObjectFieldResolvesUntaggedGameObjectAndSupportsUndoRedo()
        {
            GameObject rigObject = new GameObject("Reference Camera Resolver Rig");
            GameObject previousCameraObject = new GameObject("Previous Reference Camera");
            GameObject referenceObject = new GameObject("Untagged Reference Camera");
            GameObject directCameraObject = new GameObject("Direct Reference Camera");
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow host = null;
            VisualElement root = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Camera previousCamera = previousCameraObject.AddComponent<Camera>();
                Camera referenceCamera = referenceObject.AddComponent<Camera>();
                Camera directCamera = directCameraObject.AddComponent<Camera>();
                referenceObject.tag = "Untagged";
                renderer.referenceCamera = previousCamera;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                root = editor.CreateInspectorGUI();
                host = EditorWindow.CreateWindow<InspectorTestHostWindow>();
                host.Show();
                host.rootVisualElement.Add(root);
                ObjectField field = root.Q<ObjectField>("referenceCamera");

                Assert.That(field, Is.Not.Null);
                Assert.That(field.objectType, Is.EqualTo(typeof(Camera)), "the picker should remain restricted to Camera objects");
                Assert.That(field.allowSceneObjects, Is.True);

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Resolve Reference Camera GameObject");
                field.value = referenceObject;
                Undo.FlushUndoRecordObjects();
                Assert.That(renderer.referenceCamera, Is.SameAs(referenceCamera),
                    "a tagless GameObject selection must resolve only its same-GameObject Camera component");
                Assert.That(field.value, Is.SameAs(referenceCamera));

                Undo.PerformUndo();
                field = root.Q<ObjectField>("referenceCamera");
                Assert.That(field, Is.Not.Null, "Undo should rebuild the current Reference Camera field");
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera));
                Assert.That(field.value, Is.SameAs(previousCamera));

                Undo.PerformRedo();
                field = root.Q<ObjectField>("referenceCamera");
                Assert.That(field, Is.Not.Null, "Redo should rebuild the current Reference Camera field");
                Assert.That(renderer.referenceCamera, Is.SameAs(referenceCamera));
                Assert.That(field.value, Is.SameAs(referenceCamera));

                field.value = directCamera;
                Assert.That(renderer.referenceCamera, Is.SameAs(directCamera), "a Camera object should pass through unchanged");
                Assert.That(field.value, Is.SameAs(directCamera));
            }
            finally
            {
                if (host != null)
                {
                    if (root != null) host.rootVisualElement.Remove(root);
                    host.Close();
                }
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(previousCameraObject);
                Object.DestroyImmediate(referenceObject);
                Object.DestroyImmediate(directCameraObject);
            }
        }

        [Test]
        public void UnresolvableReferenceCameraSelectionsKeepPreviousValueUntilValidOrCleared()
        {
            GameObject rigObject = new GameObject("Reference Camera Invalid Selection Rig");
            GameObject previousObject = new GameObject("Previous Reference Camera");
            GameObject noCameraObject = new GameObject("No Camera Selection");
            GameObject childOnlyObject = new GameObject("Child Camera Selection");
            GameObject childCameraObject = new GameObject("Child Camera Only");
            GameObject nextObject = new GameObject("Next Reference Camera");
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow host = null;
            VisualElement root = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Camera previousCamera = previousObject.AddComponent<Camera>();
                Camera nextCamera = nextObject.AddComponent<Camera>();
                childCameraObject.transform.SetParent(childOnlyObject.transform);
                childCameraObject.AddComponent<Camera>();
                renderer.referenceCamera = previousCamera;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                root = editor.CreateInspectorGUI();
                host = EditorWindow.CreateWindow<InspectorTestHostWindow>();
                host.Show();
                host.rootVisualElement.Add(root);
                ObjectField field = root.Q<ObjectField>("referenceCamera");
                object view = GetField<object>(editor, "view");
                HelpBox warning = root.Q<HelpBox>("referenceCameraWarning");

                SendObjectFieldChange(field, previousCamera, noCameraObject);
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera));
                Assert.That(field.value, Is.SameAs(previousCamera));
                Assert.That(warning.text, Does.Contain("Camera component on the same GameObject"));
                Invoke(view, "Refresh", false);
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera));
                Assert.That(warning.text, Does.Contain("Camera component on the same GameObject"),
                    "the assignment warning must remain visible across editor refreshes");

                SendObjectFieldChange(field, previousCamera, childOnlyObject);
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera),
                    "a Camera on a child GameObject must not resolve the selected parent");
                Assert.That(field.value, Is.SameAs(previousCamera));
                Assert.That(warning.text, Does.Contain("Camera component on the same GameObject"));

                field.value = nextCamera;
                Assert.That(renderer.referenceCamera, Is.SameAs(nextCamera));
                Assert.That(warning.text, Is.Empty, "a valid assignment must clear the previous warning");

                SendObjectFieldChange(field, nextCamera, noCameraObject);
                Assert.That(renderer.referenceCamera, Is.SameAs(nextCamera));
                field.value = null;
                Assert.That(renderer.referenceCamera, Is.Null, "explicit null must clear the reference camera");
                Assert.That(field.value, Is.Null);
                Assert.That(warning.text, Is.Empty, "explicit clearing must clear the previous warning");
            }
            finally
            {
                if (host != null)
                {
                    if (root != null) host.rootVisualElement.Remove(root);
                    host.Close();
                }
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(previousObject);
                Object.DestroyImmediate(noCameraObject);
                Object.DestroyImmediate(childOnlyObject);
                Object.DestroyImmediate(childCameraObject);
                Object.DestroyImmediate(nextObject);
            }
        }

        [Test]
        public void ReferenceCameraObjectFieldPreservesMutationOwnerAndRigCameraRestrictions()
        {
            GameObject rigObject = new GameObject("Reference Camera Owner Restriction Rig");
            GameObject previousObject = new GameObject("Previous Reference Camera");
            GameObject candidateObject = new GameObject("Candidate Reference Camera");
            GameObject sourceChildObject = new GameObject("Rig Source Child Camera");
            UnityEditor.Editor editor = null;
            InspectorTestHostWindow host = null;
            VisualElement root = null;
            Object retentionRecord = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Camera previousCamera = previousObject.AddComponent<Camera>();
                Camera candidateCamera = candidateObject.AddComponent<Camera>();
                Camera childCamera = sourceChildObject.AddComponent<Camera>();
                sourceChildObject.transform.SetParent(renderer.sourceCamera.transform);
                renderer.referenceCamera = previousCamera;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                root = editor.CreateInspectorGUI();
                host = EditorWindow.CreateWindow<InspectorTestHostWindow>();
                host.Show();
                host.rootVisualElement.Add(root);
                ObjectField field = root.Q<ObjectField>("referenceCamera");
                HelpBox warning = root.Q<HelpBox>("referenceCameraWarning");

                SendObjectFieldChange(field, previousCamera, renderer.sourceCamera);
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera), "the Rig source Camera cannot be its own reference");
                Assert.That(warning.text, Does.Contain("separate from the Rig source Camera"));

                SendObjectFieldChange(field, previousCamera, childCamera);
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera), "a descendant of the Rig source Camera cannot be its reference");
                Assert.That(warning.text, Does.Contain("separate from the Rig source Camera"));

                Type retentionType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType(
                    "ViewWeave.BridgeEditorPreviewRetentionRecord", true);
                MethodInfo create = retentionType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(create, Is.Not.Null);
                retentionRecord = (Object)create.Invoke(null, new object[] { rigObject, renderer });
                Assert.That(retentionRecord, Is.Not.Null);
                MethodInfo beginRetention = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", InstancePrivate);
                Assert.That(beginRetention, Is.Not.Null);
                Assert.That(beginRetention.Invoke(renderer, new[] { retentionRecord }), Is.EqualTo(true));

                SendObjectFieldChange(field, previousCamera, candidateCamera);
                Assert.That(renderer.referenceCamera, Is.SameAs(previousCamera), "an owned renderer must reject camera changes");
                Assert.That(field.value, Is.SameAs(previousCamera));
                Assert.That(warning.text, Does.Contain("Reference Camera was not changed"));
            }
            finally
            {
                if (retentionRecord != null)
                {
                    Object.DestroyImmediate(retentionRecord);
                }
                if (host != null)
                {
                    if (root != null) host.rootVisualElement.Remove(root);
                    host.Close();
                }
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(previousObject);
                Object.DestroyImmediate(candidateObject);
                Object.DestroyImmediate(sourceChildObject);
            }
        }

        static void SendObjectFieldChange(ObjectField field, Object oldValue, Object newValue)
        {
            Assert.That(field, Is.Not.Null);
            ChangeEvent<Object> change = ChangeEvent<Object>.GetPooled(oldValue, newValue);
            try
            {
                change.target = field;
                field.SendEvent(change);
            }
            finally { change.Dispose(); }
        }

        static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, InstancePrivate);
            Assert.That(field, Is.Not.Null, "Expected private field " + name);
            return (T)field.GetValue(target);
        }

        static void AssertInspectorLock(Type inspectorType, Object component, bool expected, string message)
        {
            MethodInfo method = inspectorType.GetMethod("IsLockedForStructuralMutation", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Expected structural lock check on " + inspectorType.Name);
            object[] arguments = { component, null };
            bool locked = (bool)method.Invoke(null, arguments);
            Assert.That(locked, Is.EqualTo(expected), message);
            if (expected) Assert.That(arguments[1] as string, Is.Not.Empty, "locked inspectors must explain the current owner restriction");
        }

        static bool ProvideConnectedCalibration(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = new QuiltEditorDisplayProfile(16f / 9f, 40f, QuiltDisplaySettingsSource.ConnectedDisplaySnapshot,
                true, true, 344.65018f, 193.86572f, 283f, "Fake connected calibration for Undo test");
            return renderer != null;
        }

        static bool NoConnectedCalibration(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            return false;
        }

        static Foldout FindFoldout(VisualElement root, string label)
        {
            foreach (Foldout foldout in root.Query<Foldout>().ToList())
                if (foldout.text == label) return foldout;
            Assert.Fail("Expected foldout " + label);
            return null;
        }

        static bool ShouldStopPreview(ViewWeaveRig currentRig, ViewWeaveRig nextRig, ViewWeaveRig previewTarget,
            bool previewActive, bool previewStopping)
        {
            MethodInfo method = typeof(ViewWeaveRigAuthoringWindow).GetMethod("ShouldStopPreviewOnRigChange", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { currentRig, nextRig, previewTarget, previewActive, previewStopping });
        }

        static void Invoke(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name, InstancePrivate | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Expected private method " + name);
            try { method.Invoke(target, null); }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { throw exception.InnerException; }
        }

        static object Invoke(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(name, InstancePrivate | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, "Expected private method " + name);
            try { return method.Invoke(target, arguments); }
            catch (TargetInvocationException exception) when (exception.InnerException != null) { throw exception.InnerException; }
        }
    }
}
#endif
