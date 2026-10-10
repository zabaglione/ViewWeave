using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    [CustomEditor(typeof(ViewWeaveRig))]
    [CanEditMultipleObjects]
    public sealed class ViewWeaveRigInspector : UnityEditor.Editor
    {
        ViewWeaveRigInspectorUI view;
        UnityEngine.Object focusTarget;

        void OnEnable()
        {
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            view?.Dispose();
            view = null;
        }

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            if (targets == null || targets.Length != 1)
            {
                root.Add(new HelpBox("ViewWeaveRig authoring controls require one selected Rig. Select a single Rig to edit focus, preview, and Bridge settings.", HelpBoxMessageType.Info));
                return root;
            }
            focusTarget = ResolveSelectionTarget((ViewWeaveRig)target);
            view?.Dispose();
            view = new ViewWeaveRigInspectorUI(root, () => target as ViewWeaveRig, () => focusTarget, value => focusTarget = value);
            view.Rebuild();
            return root;
        }

        void OnSelectionChanged()
        {
            ViewWeaveRig rig = target as ViewWeaveRig;
            focusTarget = ResolveSelectionTarget(rig);
            view?.Refresh();
        }

        void OnUndoRedo() => view?.Rebuild();
        void OnEditorUpdate()
        {
            if (target == null)
            {
                view?.Dispose();
                view = null;
                return;
            }
            view?.Refresh();
        }
        void OnPlayModeStateChanged(PlayModeStateChange state) => view?.OnPlayModeStateChanged(state);

        static UnityEngine.Object ResolveSelectionTarget(ViewWeaveRig rig)
        {
            UnityEngine.Object selected = Selection.activeObject;
            GameObject selectedObject = selected as GameObject;
            if (selectedObject == null && selected is Component component) selectedObject = component.gameObject;
            return selectedObject == (rig == null ? null : rig.gameObject) ? null : selected;
        }
    }
}
