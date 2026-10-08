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
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                rigObject.AddComponent<ViewWeaveBridgeSession>();
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();

                PropertyField profileField = root.Q<PropertyField>("rigQualityProfile");
                Assert.That(profileField, Is.Not.Null);
                Assert.That(profileField.bindingPath, Is.EqualTo("profile"));
                Assert.That(root.Q<EnumField>("sessionQuality"), Is.Not.Null);

                SerializedObject rigData = new SerializedObject(rig);
                SerializedProperty width = rigData.FindProperty("profile").FindPropertyRelative("width");
                width.intValue = 2048;
                rigData.ApplyModifiedProperties();
                Assert.That(rig.Profile.QuiltWidth, Is.EqualTo(2048), "the profile field must edit Rig profile data used by local preview");
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
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
        public void UseSelectionFocusActionUpdatesRendererAndUndoRestoresPriorFocus()
        {
            GameObject rigObject = new GameObject("Inspector Focus Rig");
            GameObject focusObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            UnityEditor.Editor editor = null;
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                renderer.settings.cameraDistance = 2f;
                focusObject.transform.position = new Vector3(0f, 0f, 7f);
                Selection.activeGameObject = focusObject;
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                object view = GetField<object>(editor, "view");
                Invoke(view, "UseSelection");

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
                if (editor != null) Object.DestroyImmediate(editor);
                Object.DestroyImmediate(rigObject);
                Object.DestroyImmediate(focusObject);
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

                PropertyField followedField = root.Q<PropertyField>("referenceFieldOfView");
                Assert.That(followedField, Is.Not.Null);
                SerializedObject referenceSerialized = new SerializedObject(referenceCamera);
                SerializedProperty referenceFov = FindCameraFovProperty(referenceSerialized);
                Assert.That(referenceFov, Is.Not.Null);
                Assert.That(followedField.bindingPath, Is.EqualTo(referenceFov.propertyPath));
                Assert.That(root.Q<PropertyField>("rendererFieldOfView"), Is.Null);

                referenceFov.floatValue = 79f;
                referenceSerialized.ApplyModifiedProperties();
                Assert.That(referenceCamera.fieldOfView, Is.EqualTo(79f).Within(0.001f));
                Assert.That(sourceCamera.fieldOfView, Is.EqualTo(originalSourceFov).Within(0.001f), "reference FOV edits must not be saved to the dedicated source camera");
                Assert.That(renderer.settings.fieldOfView, Is.EqualTo(51f).Within(0.001f), "followed FOV edits must not be saved to the preview copy");

                SerializedObject rendererSerialized = new SerializedObject(renderer);
                rendererSerialized.FindProperty("followReferenceFieldOfView").boolValue = false;
                rendererSerialized.ApplyModifiedProperties();
                Invoke(GetField<object>(editor, "view"), "RebuildFieldOfView");
                PropertyField manualField = root.Q<PropertyField>("rendererFieldOfView");
                Assert.That(manualField, Is.Not.Null);
                Assert.That(manualField.bindingPath, Is.EqualTo("settings.fieldOfView"));
                Assert.That(root.Q<PropertyField>("referenceFieldOfView"), Is.Null);

                rendererSerialized.Update();
                rendererSerialized.FindProperty("settings.fieldOfView").floatValue = 63f;
                rendererSerialized.ApplyModifiedProperties();
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
            try
            {
                ViewWeaveRig rig = rigObject.AddComponent<ViewWeaveRig>();
                QuiltRenderer renderer = rigObject.GetComponent<QuiltRenderer>();
                Camera foreignCamera = cameraObject.AddComponent<Camera>();
                SceneManager.MoveGameObjectToScene(cameraObject, previewScene);
                editor = UnityEditor.Editor.CreateEditor(rig, typeof(ViewWeaveRigInspector));
                VisualElement root = editor.CreateInspectorGUI();
                object view = GetField<object>(editor, "view");

                Assert.That(Invoke(view, "TryAssignReferenceCamera", foreignCamera), Is.EqualTo(false));
                Assert.That(renderer.referenceCamera, Is.Null, "an unsafe reference must not be assigned from the authoring UI");
                Assert.That(root.Q<HelpBox>("referenceCameraWarning").text, Does.Contain("Isolated Preview Scenes are not supported"));
                Assert.That(root.Q<PropertyField>("rendererFieldOfView"), Is.Not.Null);

                renderer.referenceCamera = foreignCamera;
                Invoke(view, "RefreshReferenceCameraState");
                Assert.That(root.Q<PropertyField>("referenceFieldOfView"), Is.Null, "an unsafe existing reference must not expose an editable FOV field");
                Assert.That(root.Q<HelpBox>("referenceCameraWarning").text, Does.Contain("unavailable"));
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
                if (rigObject != null) Object.DestroyImmediate(rigObject);
                if (previewScene.IsValid() && previewScene.isLoaded) EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, InstancePrivate);
            Assert.That(field, Is.Not.Null, "Expected private field " + name);
            return (T)field.GetValue(target);
        }

        static bool ShouldStopPreview(ViewWeaveRig currentRig, ViewWeaveRig nextRig, ViewWeaveRig previewTarget,
            bool previewActive, bool previewStopping)
        {
            MethodInfo method = typeof(ViewWeaveRigAuthoringWindow).GetMethod("ShouldStopPreviewOnRigChange", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { currentRig, nextRig, previewTarget, previewActive, previewStopping });
        }

        static SerializedProperty FindCameraFovProperty(SerializedObject serializedCamera)
        {
            return serializedCamera.FindProperty("m_FieldOfView") ?? serializedCamera.FindProperty("field of view");
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
