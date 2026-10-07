using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    public sealed class QuiltCaptureWindow : EditorWindow
    {
        QuiltRenderer quiltRenderer;
        string outputRoot;
        string statusMessage = string.Empty;
        int fps = 30;
        int frameCount = 120;
        Vector2 scroll;

        [MenuItem("Window/ViewWeave/Quilt Capture")]
        static void Open()
        {
            QuiltCaptureWindow window = GetWindow<QuiltCaptureWindow>();
            window.titleContent = new GUIContent("Quilt Capture");
            window.minSize = new Vector2(360f, 420f);
            window.Show();
        }

        void OnEnable()
        {
            EditorApplication.update += Repaint;
            if (quiltRenderer == null && Selection.activeGameObject != null)
                quiltRenderer = Selection.activeGameObject.GetComponent<QuiltRenderer>();
        }

        void OnDisable()
        {
            EditorApplication.update -= Repaint;
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Quilt Capture", EditorStyles.boldLabel);
            quiltRenderer = (QuiltRenderer)EditorGUILayout.ObjectField("Quilt Renderer", quiltRenderer, typeof(QuiltRenderer), true);
            if (quiltRenderer == null)
            {
                EditorGUILayout.HelpBox("Assign a GameObject with a Quilt Renderer.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawLatestQuilt();
            DrawRecorder();
            if (!string.IsNullOrEmpty(statusMessage)) EditorGUILayout.HelpBox(statusMessage, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        void DrawLatestQuilt()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Latest completed quilt", EditorStyles.boldLabel);
            RenderTexture texture = quiltRenderer.QuiltTexture;
            bool hasQuilt = quiltRenderer.HasRenderedQuilt && texture != null && texture.IsCreated();
            if (!hasQuilt)
            {
                EditorGUILayout.HelpBox("No completed quilt is available yet. Render the scene once or enter Play mode with Auto Render enabled.", MessageType.Info);
                return;
            }

            float previewHeight = Mathf.Min(280f, Mathf.Max(120f, position.width * texture.height / texture.width));
            Rect previewRect = GUILayoutUtility.GetRect(1f, previewHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(previewRect, texture, null, ScaleMode.ScaleToFit);
            QuiltRenderSnapshot snapshot = quiltRenderer.LatestSnapshot;
            EditorGUILayout.LabelField("Image", texture.width + " x " + texture.height + "   " + snapshot.Layout.Columns + " x " + snapshot.Layout.Rows + "   " + snapshot.LastRenderedViewCount + "/" + snapshot.Layout.ViewCount + " rendered views");
            bool current = quiltRenderer.IsLatestQuiltCurrent;
            if (!current) EditorGUILayout.HelpBox("The image is stale because the renderer settings or camera geometry changed after its last completed render.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(!current))
            {
                if (GUILayout.Button("Save Latest Quilt PNG...")) SaveLatest();
                if (GUILayout.Button("Save Central View PNG...")) SaveCentralView();
            }
            if ((snapshot.Layout.ViewCount & 1) == 0)
                EditorGUILayout.HelpBox("The central image uses logical view " + ((snapshot.Layout.ViewCount - 1) / 2) + " (the lower of the two middle views).", MessageType.Info);
        }

        void DrawRecorder()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("PNG sequence recording", EditorStyles.boldLabel);
            QuiltRecorder recorder = quiltRenderer.GetComponent<QuiltRecorder>();
            if (recorder == null)
            {
                EditorGUILayout.HelpBox("Recording stores each completed Auto Render quilt as PNG. It does not render extra views or record at real-time speed.", MessageType.Info);
                if (GUILayout.Button("Add Recorder Component"))
                {
                    Undo.AddComponent<QuiltRecorder>(quiltRenderer.gameObject).Renderer = quiltRenderer;
                    statusMessage = "Recorder component added.";
                }
                return;
            }

            using (new EditorGUI.DisabledScope(recorder.IsRecording))
            {
                fps = EditorGUILayout.IntField("Frames per second", fps);
                frameCount = EditorGUILayout.IntField("Frame count", frameCount);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Output folder", string.IsNullOrEmpty(outputRoot) ? "Not selected" : outputRoot);
                if (GUILayout.Button("Choose...", GUILayout.Width(76f)))
                {
                    string chosen = EditorUtility.OpenFolderPanel("Choose Quilt recording folder", string.IsNullOrEmpty(outputRoot) ? Application.dataPath : outputRoot, string.Empty);
                    if (!string.IsNullOrEmpty(chosen)) outputRoot = chosen;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.LabelField("Status", recorder.Status.ToString());
            if (!string.IsNullOrEmpty(recorder.RecordingDirectory)) EditorGUILayout.SelectableLabel(recorder.RecordingDirectory, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            if (!string.IsNullOrEmpty(recorder.ErrorMessage)) EditorGUILayout.HelpBox(recorder.ErrorMessage, MessageType.Error);
            if (recorder.IsRecording)
            {
                EditorGUILayout.LabelField("Completed frames", recorder.CompletedFrameCount + " / " + recorder.RequestedFrameCount);
                EditorGUILayout.BeginHorizontal();
                if (recorder.IsPaused)
                {
                    using (new EditorGUI.DisabledScope(!Application.isPlaying))
                    {
                        if (GUILayout.Button("Resume Recording"))
                        {
                            try
                            {
                                recorder.ResumeRecording();
                                statusMessage = "Recording resumed. The resume frame is skipped.";
                            }
                            catch (Exception error) { statusMessage = Sanitize(error.GetType().Name + ": " + error.Message); }
                        }
                    }
                }
                else if (GUILayout.Button("Pause Recording"))
                {
                    try
                    {
                        recorder.PauseRecording();
                        statusMessage = "Recording paused. Scene simulation continues without saving frames.";
                    }
                    catch (Exception error) { statusMessage = Sanitize(error.GetType().Name + ": " + error.Message); }
                }
                if (GUILayout.Button("Stop Recording")) recorder.StopRecording();
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                using (new EditorGUI.DisabledScope(!Application.isPlaying || string.IsNullOrWhiteSpace(outputRoot) || !quiltRenderer.autoRender))
                {
                    if (GUILayout.Button("Start PNG Sequence"))
                    {
                        try
                        {
                            recorder.Renderer = quiltRenderer;
                            recorder.StartRecording(outputRoot, fps, frameCount);
                            statusMessage = "Recording started. The current frame is skipped so all saved frames use the requested fixed simulation step.";
                        }
                        catch (Exception error) { statusMessage = Sanitize(error.GetType().Name + ": " + error.Message); }
                    }
                }
                if (!Application.isPlaying) EditorGUILayout.HelpBox("Enter Play mode, enable Auto Render, then start recording.", MessageType.Info);
                else if (!quiltRenderer.autoRender) EditorGUILayout.HelpBox("Enable Auto Render on the selected Quilt Renderer before recording.", MessageType.Warning);
            }
        }

        void SaveLatest()
        {
            QuiltRenderSnapshot snapshot = quiltRenderer.LatestSnapshot;
            string fileName = QuiltCaptureUtility.BuildFileName(quiltRenderer.gameObject.name, snapshot);
            string path = EditorUtility.SaveFilePanel("Save Quilt PNG", string.IsNullOrEmpty(outputRoot) ? Application.dataPath : outputRoot, Path.GetFileNameWithoutExtension(fileName), "png");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                QuiltCaptureUtility.SaveLatest(quiltRenderer, path);
                string sidecar = Path.ChangeExtension(path, ".json");
                statusMessage = "Saved PNG and JSON sidecar: " + Path.GetFileName(path) + " / " + Path.GetFileName(sidecar);
            }
            catch (Exception error) { statusMessage = Sanitize(error.GetType().Name + ": " + error.Message); }
        }

        void SaveCentralView()
        {
            QuiltRenderSnapshot snapshot = quiltRenderer.LatestSnapshot;
            string fileName = Path.GetFileNameWithoutExtension(QuiltCaptureUtility.BuildFileName(quiltRenderer.gameObject.name, snapshot)) + "_central.png";
            string path = EditorUtility.SaveFilePanel("Save Central View PNG", string.IsNullOrEmpty(outputRoot) ? Application.dataPath : outputRoot, Path.GetFileNameWithoutExtension(fileName), "png");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                QuiltCaptureUtility.SaveCentralViewLatest(quiltRenderer, path);
                string sidecar = Path.ChangeExtension(path, ".json");
                statusMessage = "Saved central view PNG and JSON sidecar: " + Path.GetFileName(path) + " / " + Path.GetFileName(sidecar);
            }
            catch (Exception error) { statusMessage = Sanitize(error.GetType().Name + ": " + error.Message); }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] safe = new char[value.Length];
            int count = 0;
            for (int i = 0; i < value.Length; ++i)
                if (value[i] >= 0x20 && value[i] <= 0x7e) safe[count++] = value[i];
            return new string(safe, 0, count);
        }
    }
}
