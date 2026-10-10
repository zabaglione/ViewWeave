using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    [CustomEditor(typeof(BridgeMetalQuiltOutput))]
    public sealed class BridgeMetalQuiltOutputInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var output = (BridgeMetalQuiltOutput)target;
            using (new EditorGUI.DisabledScope(output.IsRunning)) DrawDefaultInspector();
            EditorGUILayout.HelpBox("macOS Metal / Looking Glass Go. Start in Play mode. Stop before changing quality or transport.", MessageType.Info);
            if (!string.IsNullOrEmpty(output.LastError)) EditorGUILayout.HelpBox(output.LastError, MessageType.Error);
            EditorGUILayout.LabelField("Presented frames", output.CompletedFrames.ToString());
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                if (GUILayout.Button(output.IsRunning ? "Stop Metal Output" : "Start Metal Output"))
                {
                    if (output.IsRunning) output.StopOutput(); else output.StartOutput();
                }
            }
            if (Application.isPlaying) Repaint();
        }
    }
}
