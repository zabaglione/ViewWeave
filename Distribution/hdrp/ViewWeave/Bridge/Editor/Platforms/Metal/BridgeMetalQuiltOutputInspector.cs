using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    [CustomEditor(typeof(BridgeMetalQuiltOutput))]
    public sealed class BridgeMetalQuiltOutputInspector : UnityEditor.Editor
    {
        bool showDiagnostics;
        public override void OnInspectorGUI()
        {
            var output = (BridgeMetalQuiltOutput)target;
            ViewWeaveRig owner = output.rig != null ? output.rig : output.GetComponent<ViewWeaveRig>();
            if (owner != null)
            {
                EditorGUILayout.HelpBox("Configure camera, output quality, transport, and start/stop from the ViewWeave Rig component.", MessageType.Info);
                showDiagnostics = EditorGUILayout.Foldout(showDiagnostics, "Technical Diagnostics", true);
                if (!showDiagnostics) return;
                using (new EditorGUI.DisabledScope(true)) DrawDefaultInspector();
            }
            else
            {
                EditorGUILayout.HelpBox("Attach or assign a ViewWeave Rig to use the unified authoring UI.", MessageType.Info);
                using (new EditorGUI.DisabledScope(!output.CanChangeConfiguration(out _))) DrawDefaultInspector();
            }
            if (!string.IsNullOrEmpty(output.LastError)) EditorGUILayout.HelpBox(output.LastError, MessageType.Error);
            EditorGUILayout.LabelField("Presented frames", output.CompletedFrames.ToString());
            if (Application.isPlaying) Repaint();
        }
    }
}
