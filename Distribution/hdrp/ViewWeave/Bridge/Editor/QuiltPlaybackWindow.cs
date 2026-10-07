using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    public sealed class QuiltPlaybackWindow : EditorWindow
    {
        ViewWeaveBridgeSession session;
        QuiltPlaybackSource source;
        ObjectField sessionField;
        TextField pathField;
        Label sourceLabel;
        Label statusLabel;
        Label errorLabel;
        Button loadButton;
        Button startButton;
        Button pauseButton;
        Button stopButton;

        [MenuItem("Window/ViewWeave/Quilt Playback")]
        static void OpenWindow()
        {
            QuiltPlaybackWindow window = GetWindow<QuiltPlaybackWindow>();
            window.titleContent = new GUIContent("Quilt Playback");
            window.minSize = new Vector2(380f, 320f);
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.paddingLeft = 10;
            rootVisualElement.style.paddingRight = 10;
            rootVisualElement.style.paddingTop = 8;
            rootVisualElement.style.paddingBottom = 8;

            var heading = new Label("Play a validated quilt PNG or completed recording through an existing Ready Bridge session.");
            heading.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(heading);

            sessionField = new ObjectField("Bridge Session") { objectType = typeof(ViewWeaveBridgeSession), allowSceneObjects = true };
            sessionField.value = session;
            sessionField.RegisterValueChangedCallback(evt => session = evt.newValue as ViewWeaveBridgeSession);
            rootVisualElement.Add(sessionField);

            pathField = new TextField("Source Path");
            rootVisualElement.Add(pathField);
            var browseRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var pngButton = new Button(() => BrowseFile()) { text = "Choose PNG or manifest" };
            var folderButton = new Button(() => BrowseFolder()) { text = "Choose recording folder" };
            browseRow.Add(pngButton);
            browseRow.Add(folderButton);
            rootVisualElement.Add(browseRow);

            loadButton = new Button(LoadSource) { text = "Validate Source" };
            rootVisualElement.Add(loadButton);
            sourceLabel = new Label("No validated source.");
            sourceLabel.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(sourceLabel);

            var playbackRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            startButton = new Button(StartPlayback) { text = "Play" };
            pauseButton = new Button(TogglePause) { text = "Pause" };
            stopButton = new Button(StopPlayback) { text = "Stop" };
            playbackRow.Add(startButton);
            playbackRow.Add(pauseButton);
            playbackRow.Add(stopButton);
            rootVisualElement.Add(playbackRow);

            statusLabel = new Label("Playback can start only in Windows Editor Play mode with an existing Ready Bridge session.");
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(statusLabel);
            errorLabel = new Label(string.Empty);
            errorLabel.style.whiteSpace = WhiteSpace.Normal;
            errorLabel.style.color = new Color(1f, 0.45f, 0.35f);
            rootVisualElement.Add(errorLabel);

            if (session == null && Selection.activeGameObject != null) session = Selection.activeGameObject.GetComponent<ViewWeaveBridgeSession>();
            if (sessionField != null) sessionField.value = session;
            RefreshControls();
        }

        void OnEnable() { EditorApplication.update += RefreshControls; }

        void OnDisable()
        {
            EditorApplication.update -= RefreshControls;
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (lease != null) lease.StopPlayback();
        }

        void BrowseFile()
        {
            string selected = EditorUtility.OpenFilePanel("Select quilt PNG or recording manifest", string.Empty, "png,json");
            if (!string.IsNullOrEmpty(selected)) pathField.value = selected;
        }

        void BrowseFolder()
        {
            string selected = EditorUtility.OpenFolderPanel("Select completed quilt recording", string.Empty, string.Empty);
            if (!string.IsNullOrEmpty(selected)) pathField.value = selected;
        }

        void LoadSource()
        {
            try
            {
                source = QuiltPlaybackSource.Open(pathField.value);
                QuiltPlaybackInfo info = source.Info;
                string sourceGeometry = info.HasCameraGeometry ? "camera metadata available" : "camera metadata unavailable";
                string sequence = info.IsSequence ? info.FrameCount + " frames at " + info.FramesPerSecond + " fps" : "single quilt image";
                sourceLabel.text = info.Layout.QuiltWidth + "x" + info.Layout.QuiltHeight + ", grid " + info.Layout.Columns + "x" + info.Layout.Rows + ", " + sequence + ", geometry " + info.GeometrySource + " (" + sourceGeometry + "), bottom-left row-major.";
                errorLabel.text = string.Empty;
            }
            catch (Exception exception)
            {
                source = null;
                sourceLabel.text = "No validated source.";
                errorLabel.text = Sanitize(exception.GetType().Name + ": " + exception.Message);
            }
            RefreshControls();
        }

        void StartPlayback()
        {
            if (source == null) { errorLabel.text = "Validate a quilt source before playback."; return; }
            if (!BridgeQuiltPlaybackLease.TryStart(session, source, out string error)) errorLabel.text = Sanitize(error);
            else errorLabel.text = string.Empty;
            RefreshControls();
        }

        void TogglePause()
        {
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (lease == null) return;
            if (lease.State == BridgeQuiltPlaybackLease.LeaseState.Playing) lease.PausePlayback();
            else if (lease.State == BridgeQuiltPlaybackLease.LeaseState.Paused) lease.ResumePlayback();
            RefreshControls();
        }

        void StopPlayback()
        {
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (lease != null) lease.StopPlayback();
            RefreshControls();
        }

        void RefreshControls()
        {
            if (startButton == null) return;
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            bool busy = lease != null && lease.IsBusy;
            bool paused = busy && lease.State == BridgeQuiltPlaybackLease.LeaseState.Paused;
            bool playing = busy && lease.State == BridgeQuiltPlaybackLease.LeaseState.Playing;
            startButton.SetEnabled(lease == null && source != null && session != null && EditorApplication.isPlaying);
            loadButton.SetEnabled(lease == null);
            sessionField?.SetEnabled(lease == null);
            pathField?.SetEnabled(lease == null);
            pauseButton.SetEnabled(playing || paused);
            pauseButton.text = paused ? "Resume" : "Pause";
            stopButton.SetEnabled(busy);
            if (lease != null)
            {
                statusLabel.text = lease.StatusMessage;
                errorLabel.text = lease.ErrorMessage;
            }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }
    }
}
