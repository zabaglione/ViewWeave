using System;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    public sealed class QuiltPlaybackWindow : EditorWindow
    {
        ViewWeaveRig rig;
        ViewWeaveBridgeSession session => rig == null ? null : rig.GetComponent<ViewWeaveBridgeSession>();
        BridgeMetalQuiltOutput metal => ResolveMetalOutput(rig);
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
        string openRequestMessage;

        public static BridgeMetalQuiltOutput ResolveMetalOutput(ViewWeaveRig targetRig)
        {
            if (targetRig == null) return null;
            MonoBehaviour bound = targetRig.Renderer == null ? null : targetRig.Renderer.OutputBehaviour;
            if (bound != null) return bound as BridgeMetalQuiltOutput;
            return targetRig.GetComponent<ViewWeaveBridgeSession>() == null ? targetRig.GetComponent<BridgeMetalQuiltOutput>() : null;
        }

        [MenuItem("Window/ViewWeave/Quilt Playback")]
        static void OpenWindow()
        {
            QuiltPlaybackWindow window = GetWindow<QuiltPlaybackWindow>();
            window.titleContent = new GUIContent("Quilt Playback");
            window.minSize = new Vector2(380f, 320f);
        }

        public static bool OpenForSession(ViewWeaveBridgeSession targetSession)
        {
            return targetSession != null && OpenForRig(targetSession.GetComponent<ViewWeaveRig>());
        }

        public static bool OpenForRig(ViewWeaveRig targetRig)
        {
            if (targetRig == null) return false;
            QuiltPlaybackWindow window = GetWindow<QuiltPlaybackWindow>();
            window.titleContent = new GUIContent("Quilt Playback");
            window.minSize = new Vector2(380f, 320f);
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (lease != null && lease.IsBusy || BridgeMetalQuiltPlayback.IsActive)
            {
                window.openRequestMessage = "Playback is active. The existing Rig target was retained.";
                if (window.errorLabel != null) window.errorLabel.text = window.openRequestMessage;
                window.Show();
                return false;
            }

            window.rig = targetRig;
            if (window.sessionField != null) window.sessionField.SetValueWithoutNotify(targetRig);
            window.openRequestMessage = string.Empty;
            window.Show();
            return true;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.paddingLeft = 10;
            rootVisualElement.style.paddingRight = 10;
            rootVisualElement.style.paddingTop = 8;
            rootVisualElement.style.paddingBottom = 8;

            var heading = new Label("Play a validated quilt PNG or completed PNG recording through the Rig's running output in Play mode.");
            heading.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(heading);

            sessionField = new ObjectField("ViewWeave Rig") { objectType = typeof(ViewWeaveRig), allowSceneObjects = true, name = "playbackTarget" };
            sessionField.value = rig;
            sessionField.RegisterValueChangedCallback(evt => rig = evt.newValue as ViewWeaveRig);
            rootVisualElement.Add(sessionField);

            pathField = new TextField("Source Path") { name = "playbackSourcePath" };
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
            startButton = new Button(StartPlayback) { text = "Play", name = "startPlayback" };
            pauseButton = new Button(TogglePause) { text = "Pause" };
            stopButton = new Button(StopPlayback) { text = "Stop" };
            playbackRow.Add(startButton);
            playbackRow.Add(pauseButton);
            playbackRow.Add(stopButton);
            rootVisualElement.Add(playbackRow);

            statusLabel = new Label("Playback requires Editor Play mode and a running Metal output or Ready Windows Bridge session.");
            statusLabel.style.whiteSpace = WhiteSpace.Normal;
            rootVisualElement.Add(statusLabel);
            errorLabel = new Label(string.Empty);
            errorLabel.style.whiteSpace = WhiteSpace.Normal;
            errorLabel.style.color = new Color(1f, 0.45f, 0.35f);
            rootVisualElement.Add(errorLabel);

            if (rig == null && Selection.activeGameObject != null) rig = Selection.activeGameObject.GetComponent<ViewWeaveRig>();
            if (sessionField != null) sessionField.value = rig;
            if (!string.IsNullOrEmpty(openRequestMessage) && errorLabel != null) errorLabel.text = openRequestMessage;
            RefreshControls();
        }

        void OnEnable() { EditorApplication.update += RefreshControls; }

        void OnDisable()
        {
            EditorApplication.update -= RefreshControls;
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (lease != null) lease.StopPlayback();
            BridgeMetalQuiltPlayback.Stop();
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
            bool started = metal != null
                ? BridgeMetalQuiltPlayback.TryStart(metal, source, out string error)
                : BridgeQuiltPlaybackLease.TryStart(session, source, out error);
            errorLabel.text = started ? string.Empty : Sanitize(error);
            RefreshControls();
        }

        void TogglePause()
        {
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (BridgeMetalQuiltPlayback.IsActive) { BridgeMetalQuiltPlayback.TogglePause(); RefreshControls(); return; }
            if (lease == null) return;
            if (lease.State == BridgeQuiltPlaybackLease.LeaseState.Playing) lease.PausePlayback();
            else if (lease.State == BridgeQuiltPlaybackLease.LeaseState.Paused) lease.ResumePlayback();
            RefreshControls();
        }

        void StopPlayback()
        {
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            if (lease != null) lease.StopPlayback();
            BridgeMetalQuiltPlayback.Stop();
            RefreshControls();
        }

        void RefreshControls()
        {
            if (startButton == null) return;
            BridgeQuiltPlaybackLease lease = BridgeQuiltPlaybackLease.Active;
            bool metalActive = BridgeMetalQuiltPlayback.IsActive;
            bool metalPending = BridgeMetalQuiltPlayback.PendingResourceCount != 0;
            bool busy = lease != null && lease.IsBusy || metalActive;
            bool paused = metalActive ? BridgeMetalQuiltPlayback.IsPaused : lease != null && lease.State == BridgeQuiltPlaybackLease.LeaseState.Paused;
            bool playing = metalActive ? !BridgeMetalQuiltPlayback.IsPaused && !BridgeMetalQuiltPlayback.IsStopping : lease != null && lease.State == BridgeQuiltPlaybackLease.LeaseState.Playing;
            startButton.SetEnabled(lease == null && !metalActive && !metalPending && source != null && rig != null && EditorApplication.isPlaying &&
                (metal == null ? session != null : metal.IsRunning));
            loadButton.SetEnabled(lease == null && !metalActive && !metalPending);
            sessionField?.SetEnabled(lease == null && !metalActive && !metalPending);
            pathField?.SetEnabled(lease == null && !metalActive && !metalPending);
            pauseButton.SetEnabled(playing || paused);
            pauseButton.text = paused ? "Resume" : "Pause";
            stopButton.SetEnabled(busy);
            if (metal != null || metalActive)
            {
                statusLabel.text = BridgeMetalQuiltPlayback.Status;
                if (metalActive || metalPending) errorLabel.text = BridgeMetalQuiltPlayback.LastError;
            }
            else if (lease != null)
            {
                statusLabel.text = lease.StatusMessage;
                errorLabel.text = string.IsNullOrEmpty(openRequestMessage) ? lease.ErrorMessage : openRequestMessage;
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
