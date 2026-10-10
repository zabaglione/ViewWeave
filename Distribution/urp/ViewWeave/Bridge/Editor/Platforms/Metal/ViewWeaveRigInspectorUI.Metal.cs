using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    internal sealed partial class ViewWeaveRigInspectorUI
    {
        BridgeMetalQuiltOutput metalOutput;
        SerializedObject metalOutputObject;
        Button startMetalButton, stopMetalButton;
        Label metalStatus;
        HelpBox metalError;

        void InitializeMetalOutputUI()
        {
            metalOutput = renderer == null ? null : renderer.OutputBehaviour as BridgeMetalQuiltOutput;
            if (metalOutput == null && session == null && rig != null) metalOutput = rig.GetComponent<BridgeMetalQuiltOutput>();
            metalOutputObject = metalOutput == null ? null : new SerializedObject(metalOutput);
        }

        void BuildMetalQuality(Foldout foldout)
        {
            qualityField = new EnumField("Output Quality", metalOutput.quality) { name = "sessionQuality" };
            qualityField.RegisterValueChangedCallback(OnQualityChanged);
            foldout.Add(qualityField);
            foldout.Add(new HelpBox("Stop Metal output to change quality, then start it again. The connected display determines quilt geometry.", HelpBoxMessageType.Info));
        }

        void BuildMetalBridgeSection()
        {
            Foldout foldout = AddFoldout("Bridge", true);
            transportMutationFields.Add(AddBoundProperty(foldout, metalOutputObject, "startOnEnable", "Start Output On Enable"));
            transportMutationFields.Add(AddBoundProperty(foldout, metalOutputObject, "runtimePath", "Bridge Runtime Path"));
            transportMutationFields.Add(AddBoundProperty(foldout, metalOutputObject, "applicationName", "Application Name"));
            transportMutationFields.Add(AddBoundProperty(foldout, metalOutputObject, "displayOrdinal", "Display Ordinal"));
            startMetalButton = new Button(() => { metalOutput.StartOutput(); Refresh(); }) { name = "startOutput", text = "Start Output" };
            stopMetalButton = new Button(() => { metalOutput.StopOutput(); Refresh(); }) { name = "stopOutput", text = "Stop Output" };
            foldout.Add(startMetalButton);
            foldout.Add(stopMetalButton);
            metalStatus = new Label { name = "outputStatus" };
            metalStatus.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(metalStatus);
            metalError = new HelpBox(string.Empty, HelpBoxMessageType.Error) { name = "outputError" };
            foldout.Add(metalError);
            foldout.Add(new HelpBox("Use Preview for Edit Mode display, or Start Output in Play mode. Connected calibration is available during device preview or output.", HelpBoxMessageType.Info));
        }

        void OnMetalQualityChanged(ChangeEvent<Enum> evt)
        {
            Undo.RecordObject(metalOutput, "Change ViewWeave Output Quality");
            if (!metalOutput.TrySetQuality((QuiltQualityPreset)evt.newValue, out string error))
            {
                qualityField.SetValueWithoutNotify(metalOutput.quality);
                SetActionStatus(error);
            }
            else
            {
                EditorUtility.SetDirty(metalOutput);
                PrefabUtility.RecordPrefabInstancePropertyModifications(metalOutput);
                SetActionStatus(string.Empty);
            }
            RefreshMetalOutputState();
        }

        void RefreshMetalOutputState()
        {
            if (metalOutput == null) return;
            qualityField?.SetValueWithoutNotify(metalOutput.quality);
            bool editable = metalOutput.CanChangeConfiguration(out string reason) && !playModeTransitioning;
            qualityField?.SetEnabled(editable);
            if (qualityField != null) qualityField.tooltip = editable ? string.Empty : reason;
            bool running = metalOutput.IsRunning;
            bool closing = metalOutput.IsClosing || BridgeMetalQuiltOutput.PendingRetirementCount != 0;
            startMetalButton?.SetEnabled(EditorApplication.isPlaying && !playModeTransitioning && !running && !closing);
            stopMetalButton?.SetEnabled(EditorApplication.isPlaying && !playModeTransitioning && running);
            if (metalStatus != null)
                metalStatus.text = "Output state: " + (running ? rig != null && rig.AutoRender ? "Rendering" : "Paused" : closing ? "Stopping" : "Idle") +
                    "; presented frames: " + metalOutput.CompletedFrames + "; dropped frames: " + metalOutput.DroppedFrames;
            if (metalError != null)
            {
                metalError.text = metalOutput.LastError ?? string.Empty;
                metalError.style.display = string.IsNullOrEmpty(metalError.text) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        void RefreshMetalTransportControls()
        {
            bool editable = !playModeTransitioning && metalOutput.CanChangeConfiguration(out _);
            foreach (var field in transportMutationFields) field?.SetEnabled(editable);
        }

        void RefreshMetalPreview()
        {
            bool active = BridgeMetalEditorPreview.IsActive && BridgeMetalEditorPreview.Target == rig;
            bool canStart = !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !BridgeMetalEditorPreview.IsActive &&
                !BridgeMetalEditorPreview.IsStopping && BridgeMetalEditorPreview.PendingResourceCount == 0;
            localPreviewButton?.SetEnabled(canStart);
            devicePreviewButton?.SetEnabled(canStart);
            stopPreviewButton?.SetEnabled(active && !playModeTransitioning);
            if (stopPreviewButton != null) stopPreviewButton.text = BridgeMetalEditorPreview.IsPaused ? "Resume Preview" : "Pause Preview";
            closePreviewButton?.SetEnabled(active && !playModeTransitioning);
            bool hasTexture = active ? BridgeMetalEditorPreview.Texture != null : metalOutput.IsRunning && metalOutput.QuiltTexture != null;
            previewModeField?.SetEnabled(hasTexture);
            selectedViewField?.SetEnabled(hasTexture && previewMode == PreviewViewMode.SelectedLogicalView);
            setFocusButton?.SetEnabled(CanAuthor());
            horizontalOrbitButton?.SetEnabled(CanAuthor());
            verticalOrbitButton?.SetEnabled(CanAuthor());
            if (previewStatus != null) previewStatus.text = BridgeMetalEditorPreview.Status;
            ShowPreviewError(BridgeMetalEditorPreview.LastError);
            previewContainer?.MarkDirtyRepaint();
        }

        void DrawMetalPreviewTexture()
        {
            bool editPreview = BridgeMetalEditorPreview.IsActive && BridgeMetalEditorPreview.Target == rig;
            RenderTexture texture = editPreview ? BridgeMetalEditorPreview.Texture : metalOutput.QuiltTexture;
            QuiltLayout layout = editPreview ? BridgeMetalEditorPreview.Layout : metalOutput.Layout;
            if (texture == null || layout.ViewCount < 1 || layout.QuiltWidth < 1 || layout.QuiltHeight < 1)
            {
                EditorGUILayout.HelpBox("Start Edit Mode preview or Play output to inspect the rendered quilt.", MessageType.Info);
                return;
            }
            bool selected = previewMode == PreviewViewMode.SelectedLogicalView;
            Rect uv = new Rect(0f, 0f, 1f, 1f);
            if (selected)
            {
                int view = selectedView == SelectedView.First ? 0 : selectedView == SelectedView.Last ? layout.ViewCount - 1 : layout.ViewCount / 2;
                RectInt tile = layout.GetTile(view);
                uv = new Rect(tile.x / (float)layout.QuiltWidth, tile.y / (float)layout.QuiltHeight,
                    tile.width / (float)layout.QuiltWidth, tile.height / (float)layout.QuiltHeight);
            }
            float width = Mathf.Max(180f, EditorGUIUtility.currentViewWidth - 24f);
            float aspect = selected ? layout.PixelAspectRatio : layout.QuiltWidth / (float)layout.QuiltHeight;
            Rect previewRect = GUILayoutUtility.GetRect(1f, Mathf.Clamp(width / Mathf.Max(0.01f, aspect), 120f, 320f), GUILayout.ExpandWidth(true));
            GUI.DrawTextureWithTexCoords(previewRect, texture, uv, true);
        }
    }
}
