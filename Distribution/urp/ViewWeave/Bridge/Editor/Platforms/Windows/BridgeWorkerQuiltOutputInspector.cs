using UnityEditor;
using UnityEngine;
using ViewWeave;

namespace ViewWeave.Editor
{
    [CustomEditor(typeof(BridgeWorkerQuiltOutput))]
    [CanEditMultipleObjects]
    public sealed class BridgeWorkerQuiltOutputInspector : UnityEditor.Editor
    {
        const string OwnedHelp = "Settings are read-only while the attached Quilt Renderer cannot safely accept structural changes. Use Window > ViewWeave > Rig Authoring for supported live controls, or stop the active owner to edit component details.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool readOnly = false;
            string reason = string.Empty;
            foreach (Object selected in targets)
            {
                if (!IsLockedForStructuralMutation(selected as BridgeWorkerQuiltOutput, out reason)) continue;
                readOnly = true;
                break;
            }
            if (readOnly)
                EditorGUILayout.HelpBox(OwnedHelp + (string.IsNullOrEmpty(reason) ? string.Empty : "\n" + reason), MessageType.Info);
            using (new EditorGUI.DisabledScope(readOnly))
                DrawPropertiesExcluding(serializedObject, "m_Script");
            serializedObject.ApplyModifiedProperties();
        }

        internal static bool IsLockedForStructuralMutation(BridgeWorkerQuiltOutput output, out string reason)
        {
            reason = string.Empty;
            QuiltRenderer renderer = output == null ? null : output.GetComponent<QuiltRenderer>();
            return renderer != null && !renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out reason);
        }
    }
}
