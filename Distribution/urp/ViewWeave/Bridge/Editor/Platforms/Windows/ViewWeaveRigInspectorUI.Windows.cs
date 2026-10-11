using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    internal sealed partial class ViewWeaveRigInspectorUI
    {
        ViewWeaveBridgeSession session;
        BridgeWorkerQuiltOutput output;
        SerializedObject sessionObject;
        SerializedObject outputObject;
        FloatField zoomField;
        Label sessionStatus;
        Label sessionError;
        Button startWindowsButton, stopWindowsButton;

        void InitializeWindowsOutputUI()
        {
            session = rig == null ? null : rig.GetComponent<ViewWeaveBridgeSession>();
            output = rig == null ? null : rig.GetComponent<BridgeWorkerQuiltOutput>();
            sessionObject = session == null ? null : new SerializedObject(session);
            outputObject = output == null ? null : new SerializedObject(output);
        }

        void RefreshWindowsMutationControls()
        {
            if (zoomField != null) zoomField.SetEnabled(output != null && renderer != null &&
                renderer.TryCheckMutation(QuiltRendererMutationKind.OutputZoom, output.zoom, out _));
        }

        Button BuildWindowsPlaybackButton()
        {
            var button = new Button(() =>
            {
                if (!QuiltPlaybackWindow.OpenForSession(session)) SetActionStatus("Quilt Playback kept its current Bridge Session because playback is active or the Session is unavailable.");
            }) { text = "Playback" };
            button.SetEnabled(session != null);
            return button;
        }

        void BuildWindowsBridgeSection()
        {
            Foldout foldout = AddFoldout("Bridge", false);
            if (session == null)
            {
                foldout.Add(new HelpBox("No Bridge Session is attached. Local Edit Mode preview remains available when the active render pipeline supports this Rig.", HelpBoxMessageType.Info));
            }
            else
            {
                transportMutationFields.Add(AddBoundProperty(foldout, sessionObject, "startOnEnable", "Start Session On Enable"));
                transportMutationFields.Add(AddBoundProperty(foldout, sessionObject, "runtimePath", "Bridge Runtime Path"));
                transportMutationFields.Add(AddBoundProperty(foldout, sessionObject, "applicationName", "Application Name"));
                transportMutationFields.Add(AddBoundProperty(foldout, sessionObject, "displayOrdinal", "Display Ordinal"));
                BuildSubpixelMode(foldout);
                startWindowsButton = new Button(() => { session.StartSession(); Refresh(); }) { name = "startOutput", text = "Start Output" };
                stopWindowsButton = new Button(() => { session.StopSession(); Refresh(); }) { name = "stopOutput", text = "Stop Output" };
                foldout.Add(startWindowsButton);
                foldout.Add(stopWindowsButton);
            }
            if (output != null)
            {
                zoomField = new FloatField("Bridge Worker Zoom") { name = "bridgeWorkerZoom", value = output.zoom };
                zoomField.RegisterValueChangedCallback(OnZoomChanged);
                foldout.Add(zoomField);
            }
            if (session != null)
            {
                sessionStatus = new Label(string.Empty);
                sessionStatus.style.whiteSpace = WhiteSpace.Normal;
                foldout.Add(sessionStatus);
                sessionError = new Label(string.Empty);
                sessionError.style.whiteSpace = WhiteSpace.Normal;
                sessionError.style.color = new Color(1f, 0.45f, 0.35f);
                foldout.Add(sessionError);
                RefreshSessionStatus();
            }
            RefreshConnectedDisplays();
        }

        void RequestConnectedDisplayRefresh()
        {
            if (!BridgeEditorDisplayDiscovery.TryRefresh(rig, out string error)) SetActionStatus(error);
            RefreshConnectedDisplays();
        }

        void OnZoomChanged(ChangeEvent<float> evt)
        {
            if (output == null) return;
            float previous = output.zoom;
            if (evt.newValue.Equals(previous)) return;
            Undo.RecordObject(output, "Change Bridge Worker Zoom");
            if (!output.TrySetZoom(evt.newValue, out string error))
            {
                zoomField?.SetValueWithoutNotify(previous);
                SetActionStatus(error);
                return;
            }
            EditorUtility.SetDirty(output);
            PrefabUtility.RecordPrefabInstancePropertyModifications(output);
            SetActionStatus("Bridge Worker Zoom updated.");
        }

        void OnQualityChangedWindows(ChangeEvent<Enum> evt)
        {
            if (session == null) return;
            QuiltQualityPreset value = (QuiltQualityPreset)evt.newValue;
            if (!EditorApplication.isPlaying) Undo.RecordObject(session, "Change ViewWeave Session Quality");
            if (!session.SetQuality(value))
            {
                qualityField?.SetValueWithoutNotify(session.Quality);
                ShowSessionError(session.LastError);
            }
            else
            {
                if (!EditorApplication.isPlaying) EditorUtility.SetDirty(session);
                ShowSessionError(string.Empty);
            }
            BridgeEditorPreview.RequestRefresh();
            RefreshSessionStatus();
        }

        void StartPreviewWindows(bool outputToDevice)
        {
            if (rig == null || EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (EditorUtility.IsPersistent(rig.gameObject) || !rig.gameObject.scene.IsValid() || !rig.gameObject.scene.isLoaded)
            {
                ShowPreviewError("Preview requires a ViewWeaveRig instance in a loaded Scene or Prefab Stage.");
                return;
            }
            if (renderer == null || renderer.sourceCamera == null)
            {
                ShowPreviewError("Preview requires the Rig QuiltRenderer and its source Camera.");
                return;
            }
            if (!QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, renderer.sourceCamera, out _, out string sourceCameraError))
            {
                ShowPreviewError("Preview cannot start: " + sourceCameraError);
                return;
            }
            UnityEngine.Object target = focusTargetProvider == null ? null : focusTargetProvider();
            if (target != null && !QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, target, out _, out string targetError))
            {
                ShowPreviewError("Preview cannot start: " + targetError);
                return;
            }
            if (renderer != null && renderer.referenceCamera != null &&
                !QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, renderer.referenceCamera, out _, out string cameraError))
            {
                ShowPreviewError("Preview cannot start: " + cameraError);
                return;
            }
            if (!BridgeEditorPreview.Start(rig, outputToDevice, out string error)) ShowPreviewError(error);
            else ShowPreviewError(string.Empty);
            RefreshPreview();
        }

        void StopPreviewWindows()
        {
            if (rig == null || BridgeEditorPreview.Target != rig) return;
            previewContainer?.MarkDirtyRepaint();
            if (BridgeEditorPreview.IsPaused)
            {
                if (!BridgeEditorPreview.Start(rig, BridgeEditorPreview.OutputToDevice, out string error)) ShowPreviewError(error);
            }
            else BridgeEditorPreview.Stop();
            RefreshPreview();
        }

        void ClosePreviewWindows()
        {
            if (rig == null || BridgeEditorPreview.Target != rig) return;
            BridgeEditorPreview.CloseForTargetChange("Preview is closing at the user's request.");
            RefreshPreview();
        }

        void RefreshPreviewWindows()
        {
            if (previewStatus == null) return;
            bool activeForRig = BridgeEditorPreview.IsActive && !BridgeEditorPreview.IsStopping && BridgeEditorPreview.Target == rig;
            bool activeForOtherRig = BridgeEditorPreview.IsActive && BridgeEditorPreview.Target != null && BridgeEditorPreview.Target != rig;
            bool isPlayTransition = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            bool canStart = !isPlayTransition && !BridgeEditorPreview.IsActive && !BridgeEditorPreview.IsStopping;
            localPreviewButton?.SetEnabled(canStart);
            devicePreviewButton?.SetEnabled(canStart);
            if (stopPreviewButton != null)
            {
                stopPreviewButton.text = BridgeEditorPreview.IsPaused ? "Resume Preview" :
                    BridgeEditorPreview.IsTransitioningPause ? "Pause Pending" : "Pause Preview";
                stopPreviewButton.SetEnabled(!isPlayTransition && activeForRig && !BridgeEditorPreview.IsStopping && !BridgeEditorPreview.IsTransitioningPause);
            }
            closePreviewButton?.SetEnabled(!isPlayTransition && activeForRig && !BridgeEditorPreview.IsStopping);
            setFocusButton?.SetEnabled(CanAuthor());
            horizontalOrbitButton?.SetEnabled(CanAuthor());
            verticalOrbitButton?.SetEnabled(CanAuthor());
            previewModeField?.SetEnabled(activeForRig);
            selectedViewField?.SetEnabled(activeForRig && previewMode == PreviewViewMode.SelectedLogicalView);

            if (activeForOtherRig)
                previewStatus.text = "An Edit Mode preview is active for " + BridgeEditorPreview.Target.name + ". Close it from that Rig before starting another preview.";
            else if (BridgeEditorPreview.IsStopping)
                previewStatus.text = BridgeEditorPreview.Status ?? "Preview is stopping.";
            else if (activeForRig)
                previewStatus.text = BridgeEditorPreview.Status ?? (BridgeEditorPreview.IsPaused ? "Preview is paused." : "Preview is active.");
            else
                previewStatus.text = "No Edit Mode preview is active.";

            if (activeForRig)
            {
                if (!string.IsNullOrEmpty(BridgeEditorPreview.LastError)) ShowPreviewError(BridgeEditorPreview.LastError);
                else if (previewError != null) ShowPreviewError(string.Empty);
            }
            previewContainer?.MarkDirtyRepaint();
        }

        void DrawPreviewTextureWindows()
        {
            if (rig == null || !BridgeEditorPreview.IsActive || BridgeEditorPreview.IsStopping || BridgeEditorPreview.Target != rig)
            {
                EditorGUILayout.HelpBox(BridgeEditorPreview.IsStopping ? "Preview is stopping; the render texture is no longer displayed." : "No active preview texture.", MessageType.Info);
                return;
            }

            RenderTexture texture = BridgeEditorPreview.Texture;
            QuiltLayout layout = BridgeEditorPreview.Layout;
            if (texture == null || layout.Rows < 1 || layout.Columns < 1 || layout.QuiltWidth < 1 || layout.QuiltHeight < 1)
            {
                EditorGUILayout.HelpBox("Waiting for a completed preview quilt.", MessageType.Info);
                return;
            }

            Rect uv = new Rect(0f, 0f, 1f, 1f);
            bool selected = previewMode == PreviewViewMode.SelectedLogicalView;
            if (selected)
            {
                int view = selectedView == SelectedView.First ? 0 : selectedView == SelectedView.Last ? layout.ViewCount - 1 : layout.ViewCount / 2;
                RectInt tile = layout.GetTile(view);
                uv = new Rect(tile.x / (float)layout.QuiltWidth, tile.y / (float)layout.QuiltHeight,
                    tile.width / (float)layout.QuiltWidth, tile.height / (float)layout.QuiltHeight);
                EditorGUILayout.LabelField("Logical View", (view + 1) + " / " + layout.ViewCount);
            }
            float width = Mathf.Max(180f, EditorGUIUtility.currentViewWidth - 24f);
            float aspect = selected ? layout.PixelAspectRatio : layout.QuiltWidth / (float)layout.QuiltHeight;
            float height = Mathf.Clamp(width / Mathf.Max(0.01f, aspect), 120f, 320f);
            Rect previewRect = GUILayoutUtility.GetRect(1f, height, GUILayout.ExpandWidth(true));
            GUI.DrawTextureWithTexCoords(previewRect, texture, uv, true);
        }

        void RefreshSessionStatusWindows()
        {
            if (session == null) return;
            bool active = session.State == ViewWeaveBridgeSession.SessionState.Connecting || session.State == ViewWeaveBridgeSession.SessionState.Rendering || session.State == ViewWeaveBridgeSession.SessionState.Reconfiguring;
            bool closing = session.State == ViewWeaveBridgeSession.SessionState.Closing || BridgeWorkerQuiltOutput.ShutdownPendingCount != 0;
            startWindowsButton?.SetEnabled(EditorApplication.isPlaying && !playModeTransitioning && session.isActiveAndEnabled && !active && !closing && !BridgeDisplayDiscovery.IsBusy && !BridgeWorkerLifecycle.RecoveryPending);
            stopWindowsButton?.SetEnabled(EditorApplication.isPlaying && !playModeTransitioning && active);
            if (sessionStatus != null) sessionStatus.text = "Session state: " + session.State;
            if (sessionError != null) sessionError.text = string.IsNullOrEmpty(session.LastError) ? string.Empty : "Session error: " + session.LastError;
            if (qualityField != null && qualityField.value != (Enum)session.Quality) qualityField.SetValueWithoutNotify(session.Quality);
            if (qualityField != null)
            {
                string reason = string.Empty;
                bool allowed = renderer != null && renderer.TryCheckMutation(QuiltRendererMutationKind.Quality, out reason);
                if (BridgeDisplayDiscovery.IsBusy)
                {
                    allowed = false;
                    reason = "Bridge quality cannot change while connected display metadata is refreshing or cleaning up.";
                }
                qualityField.SetEnabled(!playModeTransitioning && allowed);
                qualityField.tooltip = allowed ? string.Empty : reason;
            }
        }

        void ShowSessionError(string message)
        {
            if (sessionError != null) sessionError.text = string.IsNullOrEmpty(message) ? string.Empty : "Session error: " + message;
        }

        void BuildWindowsQuality(Foldout foldout)
        {
            if (session != null)
            {
                qualityField = new EnumField("Output Quality", session.Quality) { name = "sessionQuality" };
                qualityField.RegisterValueChangedCallback(OnQualityChanged);
                foldout.Add(qualityField);
                var qualityHelp = new HelpBox("Windows output can apply quality changes while rendering. The connected display determines quilt geometry.", HelpBoxMessageType.Info);
                foldout.Add(qualityHelp);
            }
        }

    }
}
