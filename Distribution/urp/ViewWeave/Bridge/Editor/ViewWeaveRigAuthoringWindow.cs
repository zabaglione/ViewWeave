using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    public sealed class ViewWeaveRigAuthoringWindow : EditorWindow
    {
        [SerializeField] ViewWeaveRig rig;
        UnityEngine.Object focusTarget;
        ObjectField rigField;
        VisualElement content;
        ViewWeaveRigInspectorUI view;

        [MenuItem("Window/ViewWeave/Rig Authoring")]
        static void OpenWindow() => OpenForRig(ResolveSelectedRig());

        [InitializeOnLoadMethod]
        static void RegisterCoreLauncher()
        {
            ViewWeaveRigAuthoringLauncher.Register(rig => { OpenForRig(rig); });
        }

        public static ViewWeaveRigAuthoringWindow OpenForRig(ViewWeaveRig targetRig)
        {
            ViewWeaveRigAuthoringWindow window = GetWindow<ViewWeaveRigAuthoringWindow>();
            window.titleContent = new GUIContent("ViewWeave Rig");
            window.minSize = new Vector2(430f, 620f);
            if (targetRig != null) window.AssignRig(targetRig);
            else if (window.rig == null) window.AssignRig(ResolveSelectedRig());
            window.RebuildGUI();
            window.Show();
            return window;
        }

        public void CreateGUI() => RebuildGUI();

        void OnEnable()
        {
            Selection.selectionChanged += OnSelectionChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.update += OnEditorUpdate;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            if (rig == null) AssignRig(ResolveSelectedRig());
            else focusTarget = ResolveSelectionTarget(rig);
        }

        void OnDisable()
        {
            Selection.selectionChanged -= OnSelectionChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            view?.Dispose();
            if (BridgeEditorPreview.IsActive && !BridgeEditorPreview.IsStopping && BridgeEditorPreview.Target == rig)
                BridgeEditorPreview.Stop();
            view = null;
        }

        void RebuildGUI()
        {
            if (rootVisualElement == null) return;
            rootVisualElement.Clear();
            rootVisualElement.style.paddingLeft = 10;
            rootVisualElement.style.paddingRight = 10;
            rootVisualElement.style.paddingTop = 8;
            rootVisualElement.style.paddingBottom = 8;

            rigField = new ObjectField("ViewWeave Rig") { objectType = typeof(ViewWeaveRig), allowSceneObjects = true, value = rig };
            rigField.RegisterValueChangedCallback(evt =>
            {
                AssignRig(evt.newValue as ViewWeaveRig);
                RebuildContent();
            });
            rootVisualElement.Add(rigField);
            content = new ScrollView(ScrollViewMode.Vertical);
            content.style.flexGrow = 1f;
            rootVisualElement.Add(content);
            RebuildContent();
        }

        void RebuildContent()
        {
            if (content == null) return;
            view?.Dispose();
            view = new ViewWeaveRigInspectorUI(content, () => rig, () => focusTarget, value => focusTarget = value);
            view.Rebuild();
        }

        void OnSelectionChanged()
        {
            focusTarget = ResolveSelectionTarget(rig);
            view?.Refresh();
        }

        void OnUndoRedo() => view?.Rebuild();
        void OnEditorUpdate() => view?.Refresh();
        void OnPlayModeStateChanged(PlayModeStateChange state) => view?.OnPlayModeStateChanged(state);

        void AssignRig(ViewWeaveRig nextRig)
        {
            if (rig == nextRig) return;
            if (ShouldStopPreviewOnRigChange(rig, nextRig, BridgeEditorPreview.Target, BridgeEditorPreview.IsActive, BridgeEditorPreview.IsStopping))
                BridgeEditorPreview.Stop();
            rig = nextRig;
            focusTarget = ResolveSelectionTarget(rig);
            if (rigField != null) rigField.SetValueWithoutNotify(rig);
        }

        internal static bool ShouldStopPreviewOnRigChange(ViewWeaveRig currentRig, ViewWeaveRig nextRig,
            ViewWeaveRig previewTarget, bool previewActive, bool previewStopping)
        {
            return currentRig != nextRig && currentRig != null && previewTarget == currentRig && previewActive && !previewStopping;
        }

        static ViewWeaveRig ResolveSelectedRig()
        {
            GameObject selected = Selection.activeGameObject;
            return selected == null ? null : selected.GetComponent<ViewWeaveRig>();
        }

        static UnityEngine.Object ResolveSelectionTarget(ViewWeaveRig currentRig)
        {
            UnityEngine.Object selected = Selection.activeObject;
            GameObject selectedObject = selected as GameObject;
            if (selectedObject == null && selected is Component component) selectedObject = component.gameObject;
            return selectedObject == (currentRig == null ? null : currentRig.gameObject) ? null : selected;
        }
    }
}
