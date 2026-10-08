using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    public sealed class QuiltVideoExportWindow : EditorWindow
    {
        const string FfmpegPref = "ViewWeave.QuiltVideoExport.ffmpeg";
        const string FfprobePref = "ViewWeave.QuiltVideoExport.ffprobe";
        TextField recordingField;
        TextField outputField;
        TextField ffmpegField;
        TextField ffprobeField;
        Label statusLabel;
        Label outputNameGuidance;
        ProgressBar progressBar;
        Button exportButton;
        Button cancelButton;
        QuiltVideoExportJob ownedJob;
        string requestedRecordingDirectory;

        [MenuItem("Window/ViewWeave/Quilt Video Export")]
        static void Open()
        {
            QuiltVideoExportWindow window = GetWindow<QuiltVideoExportWindow>();
            window.titleContent = new GUIContent("Quilt Video Export");
            window.minSize = new Vector2(480f, 420f);
            window.Show();
        }

        public static bool OpenForRenderer(QuiltRenderer renderer)
        {
            if (renderer == null) return false;
            QuiltVideoExportWindow window = GetWindow<QuiltVideoExportWindow>();
            window.titleContent = new GUIContent("Quilt Video Export");
            window.minSize = new Vector2(480f, 420f);
            QuiltVideoExportJob activeJob = window.ownedJob != null && !window.ownedJob.IsFinished ? window.ownedJob : QuiltVideoExporter.ActiveJob;
            if (activeJob != null && !activeJob.IsFinished)
            {
                window.Show();
                return false;
            }

            QuiltRecorder recorder = renderer.GetComponent<QuiltRecorder>();
            if (recorder != null && recorder.IsRecording)
            {
                window.Show();
                return false;
            }
            string recordingDirectory = recorder == null ? string.Empty : recorder.RecordingDirectory;
            if (!string.IsNullOrEmpty(recordingDirectory))
            {
                if (window.recordingField != null)
                {
                    if (string.IsNullOrWhiteSpace(window.recordingField.value)) window.recordingField.value = recordingDirectory;
                }
                else window.requestedRecordingDirectory = recordingDirectory;
            }
            window.Show();
            return true;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.paddingLeft = 12;
            rootVisualElement.style.paddingRight = 12;
            rootVisualElement.style.paddingTop = 10;
            rootVisualElement.style.paddingBottom = 10;

            Label title = new Label("Quilt Video Export") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 6 } };
            rootVisualElement.Add(title);
            Label description = new Label("Convert a completed quilt PNG sequence to a silent H.264 MP4. This is a post-process and does not render additional views.");
            description.style.whiteSpace = WhiteSpace.Normal;
            description.style.marginBottom = 10;
            rootVisualElement.Add(description);

            recordingField = AddPathField("Recording folder", "Select a folder containing manifest.json, frames.jsonl, and completed quilt frames.", false);
            if (!string.IsNullOrWhiteSpace(requestedRecordingDirectory) && string.IsNullOrWhiteSpace(recordingField.value))
            {
                recordingField.value = requestedRecordingDirectory;
                requestedRecordingDirectory = string.Empty;
            }
            outputField = AddPathField("Output MP4", "Choose a new .mp4 file. Existing MP4 or JSON sidecar files are not overwritten.", true);
            outputNameGuidance = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 4 } };
            rootVisualElement.Add(outputNameGuidance);
            outputField.RegisterValueChangedCallback(_ => RefreshOutputNameGuidance());
            recordingField.RegisterValueChangedCallback(_ => RefreshOutputNameGuidance());
            ffmpegField = AddTextField("FFmpeg executable", EditorPrefs.GetString(FfmpegPref, "ffmpeg"), "FFmpeg 7.0.2 or compatible executable available on PATH.");
            ffprobeField = AddTextField("ffprobe executable", EditorPrefs.GetString(FfprobePref, "ffprobe"), "ffprobe executable available on PATH.");

            HelpBox prerequisites = new HelpBox("The exporter does not install or download FFmpeg. H.264 yuv420p requires even dimensions; no scaling or padding is applied. Output is lossy and has no audio.", HelpBoxMessageType.Info);
            prerequisites.style.marginTop = 8;
            rootVisualElement.Add(prerequisites);

            VisualElement buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 12 } };
            exportButton = new Button(BeginExport) { text = "Export MP4" };
            cancelButton = new Button(CancelExport) { text = "Cancel", style = { marginLeft = 6 } };
            buttons.Add(exportButton);
            buttons.Add(cancelButton);
            rootVisualElement.Add(buttons);
            progressBar = new ProgressBar { title = "Progress", value = 0f, style = { marginTop = 10 } };
            rootVisualElement.Add(progressBar);
            statusLabel = new Label("Select a completed PNG sequence.") { style = { whiteSpace = WhiteSpace.Normal, marginTop = 6 } };
            rootVisualElement.Add(statusLabel);
            if (ownedJob == null) ownedJob = QuiltVideoExporter.ActiveJob;
            SyncJobState();
            RefreshOutputNameGuidance();
        }

        TextField AddPathField(string label, string browseHint, bool saveFile)
        {
            VisualElement row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6 } };
            TextField field = new TextField(label) { style = { flexGrow = 1 } };
            Button browse = new Button(() => Browse(field, browseHint, saveFile)) { text = "Browse...", style = { marginLeft = 6 } };
            row.Add(field);
            row.Add(browse);
            rootVisualElement.Add(row);
            return field;
        }

        TextField AddTextField(string label, string value, string tooltip)
        {
            TextField field = new TextField(label) { value = value, tooltip = tooltip };
            field.style.marginTop = 6;
            rootVisualElement.Add(field);
            return field;
        }

        void Browse(TextField field, string hint, bool saveFile)
        {
            string selected;
            if (saveFile)
            {
                string current = field.value;
                string directory = string.IsNullOrWhiteSpace(current) ? Application.dataPath : Path.GetDirectoryName(current);
                string name = string.IsNullOrWhiteSpace(current) ? SuggestedOutputName() : Path.GetFileName(current);
                selected = EditorUtility.SaveFilePanel(hint, directory, name, "mp4");
            }
            else selected = EditorUtility.OpenFolderPanel(hint, string.IsNullOrWhiteSpace(field.value) ? Application.dataPath : field.value, string.Empty);
            if (!string.IsNullOrEmpty(selected))
            {
                field.value = selected;
                RefreshOutputNameGuidance();
            }
        }

        string SuggestedOutputName()
        {
            try { return QuiltVideoExporter.RecommendedOutputFileName(QuiltVideoExporter.ValidateSequence(recordingField.value)); }
            catch { return "quilt.mp4"; }
        }

        void RefreshOutputNameGuidance()
        {
            if (outputNameGuidance == null) return;
            try
            {
                QuiltVideoSequenceInfo sequence = QuiltVideoExporter.ValidateSequence(recordingField.value);
                outputNameGuidance.text = QuiltVideoExporter.GetOutputNameGuidance(outputField.value, sequence);
            }
            catch (Exception exception)
            {
                outputNameGuidance.text = string.IsNullOrEmpty(outputField.value) ? string.Empty : Sanitize(exception.Message);
            }
        }

        void BeginExport()
        {
            if (ownedJob != null && !ownedJob.IsFinished) return;
            try
            {
                EditorPrefs.SetString(FfmpegPref, ffmpegField.value ?? string.Empty);
                EditorPrefs.SetString(FfprobePref, ffprobeField.value ?? string.Empty);
                ownedJob = QuiltVideoExporter.Start(recordingField.value, outputField.value, ffmpegField.value, ffprobeField.value);
                statusLabel.text = "Preparing export...";
                SyncJobState();
            }
            catch (Exception exception)
            {
                statusLabel.text = Sanitize(exception.Message);
            }
        }

        void CancelExport()
        {
            if (ownedJob != null) ownedJob.Cancel();
            SyncJobState();
        }

        void Update()
        {
            if (ownedJob == null) return;
            SyncJobState();
            if (ownedJob.IsFinished) ownedJob = null;
        }

        void SyncJobState()
        {
            if (exportButton == null || statusLabel == null) return;
            bool running = ownedJob != null && !ownedJob.IsFinished;
            exportButton.SetEnabled(!running);
            cancelButton?.SetEnabled(running);
            if (ownedJob != null)
            {
                statusLabel.text = string.IsNullOrEmpty(ownedJob.Error) ? ownedJob.Status : ownedJob.Status + "\n" + ownedJob.Error;
                if (!string.IsNullOrEmpty(ownedJob.LastDiagnosticPath)) statusLabel.text += "\nDiagnostic JSON: " + ownedJob.LastDiagnosticPath;
                if (progressBar != null) progressBar.value = ownedJob.Progress * 100f;
            }
        }

        void OnEnable() => EditorApplication.update += Update;

        void OnDisable()
        {
            EditorApplication.update -= Update;
            if (ownedJob != null && !ownedJob.IsFinished) ownedJob.Cancel();
            ownedJob = null;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Export failed.";
            System.Text.StringBuilder result = new System.Text.StringBuilder();
            for (int i = 0; i < value.Length && result.Length < 1200; ++i)
            {
                char c = value[i];
                result.Append(c >= 32 && c <= 126 ? c : '?');
            }
            return result.ToString();
        }
    }
}
