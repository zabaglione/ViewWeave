using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    // A single authoring frontend selects controls from the Rig's attached output.
    internal sealed partial class ViewWeaveRigInspectorUI
    {
        EnumField subpixelModeField;

        void InitializeOutputUI()
        {
            subpixelModeField = null;
            InitializeWindowsOutputUI();
            InitializeMetalOutputUI();
        }

        void BuildOutputQuality(Foldout foldout)
        {
            if (metalOutput != null) BuildMetalQuality(foldout);
            else BuildWindowsQuality(foldout);
        }

        void BuildBridgeSection()
        {
            if (metalOutput != null) BuildMetalBridgeSection();
            else BuildWindowsBridgeSection();
        }

        void OnQualityChanged(ChangeEvent<System.Enum> evt)
        {
            if (metalOutput != null) OnMetalQualityChanged(evt);
            else OnQualityChangedWindows(evt);
        }

        void RefreshSessionStatus()
        {
            if (metalOutput != null) RefreshMetalOutputState();
            else RefreshSessionStatusWindows();
            RefreshSubpixelMode();
        }

        void BuildSubpixelMode(Foldout foldout)
        {
            BridgeSubpixelMode value = metalOutput != null ? metalOutput.SubpixelMode : session.SubpixelMode;
            subpixelModeField = new EnumField("Subpixel Processing", value) { name = "subpixelMode" };
            subpixelModeField.RegisterValueChangedCallback(OnSubpixelModeChanged);
            foldout.Add(subpixelModeField);
            foldout.Add(new HelpBox(metalOutput != null
                ? "Automatic uses Bridge calibration. Classic RGB forces evenly spaced RGB for diagnostics. Calibrated Cells requires a Bridge cell table. Stop output and close preview before changing this setting."
                : "This setting is saved with the Rig. Windows Bridge-managed output currently supports Automatic only; manual modes report an error when device output starts.", HelpBoxMessageType.Info));
            RefreshSubpixelMode();
        }

        void OnSubpixelModeChanged(ChangeEvent<Enum> evt)
        {
            UnityEngine.Object target = metalOutput != null ? (UnityEngine.Object)metalOutput : session;
            Undo.RecordObject(target, "Change ViewWeave Subpixel Processing");
            string error;
            bool applied = metalOutput != null
                ? metalOutput.TrySetSubpixelMode((BridgeSubpixelMode)evt.newValue, out error)
                : session.TrySetSubpixelMode((BridgeSubpixelMode)evt.newValue, out error);
            if (applied)
            {
                EditorUtility.SetDirty(target);
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            }
            SetActionStatus(error);
            RefreshSubpixelMode();
        }

        void RefreshSubpixelMode()
        {
            if (subpixelModeField == null) return;
            subpixelModeField.SetValueWithoutNotify(metalOutput != null ? metalOutput.SubpixelMode : session.SubpixelMode);
            string reason;
            bool allowed = metalOutput != null ? metalOutput.CanChangeConfiguration(out reason) : session.CanChangeSubpixelMode(out reason);
            subpixelModeField.SetEnabled(allowed && !playModeTransitioning);
            subpixelModeField.tooltip = allowed ? string.Empty : reason;
        }

        void RefreshOutputMutationControls()
        {
            if (metalOutput != null) RefreshMetalTransportControls();
            else RefreshWindowsMutationControls();
        }

        void UpdateOutputSerializedObjects()
        {
            metalOutputObject?.UpdateIfRequiredOrScript();
            sessionObject?.UpdateIfRequiredOrScript();
            outputObject?.UpdateIfRequiredOrScript();
        }

        void RequestPreviewRefresh()
        {
            if (metalOutput == null) BridgeEditorPreview.RequestRefresh();
            else BridgeMetalEditorPreview.RequestRefresh();
            previewContainer?.MarkDirtyRepaint();
        }

        bool CanRequestConnectedCalibration() => metalOutput == null && CanRequestWindowsConnectedCalibration();

        void RequestConnectedCalibration(QuiltCameraFraming framing)
        {
            if (metalOutput != null) SetPhysicalStatus("Connect Metal output in Play mode to obtain the display's physical calibration.");
            else RequestWindowsConnectedCalibration(framing);
        }

        Button BuildPlaybackButton()
        {
            if (metalOutput == null) return BuildWindowsPlaybackButton();
            return new Button(() => QuiltPlaybackWindow.OpenForRig(rig)) { text = "Playback" };
        }

        void AddPlaybackHelp(Foldout foldout)
        {
            if (metalOutput != null)
                foldout.Add(new HelpBox("Quilt Playback supports full-grid PNG images and completed PNG recordings through running Metal output in Play mode.", HelpBoxMessageType.Info));
            else if (session == null)
                foldout.Add(new HelpBox("Quilt Playback requires the Rig's Bridge Session component and a validated quilt source.", HelpBoxMessageType.Info));
        }

        void StartPreview(bool outputToDevice)
        {
            if (metalOutput == null) StartPreviewWindows(outputToDevice);
            else
            {
                BridgeMetalEditorPreview.Start(rig, outputToDevice, out string error);
                ShowPreviewError(error);
                RefreshPreview();
            }
        }

        void StopPreview()
        {
            if (metalOutput == null) StopPreviewWindows();
            else if (BridgeMetalEditorPreview.Target == rig) { BridgeMetalEditorPreview.TogglePause(); RefreshPreview(); }
        }

        void ClosePreview()
        {
            if (metalOutput == null) ClosePreviewWindows();
            else if (BridgeMetalEditorPreview.Target == rig) { BridgeMetalEditorPreview.Stop(); RefreshPreview(); }
        }

        void RefreshPreview()
        {
            if (metalOutput != null) RefreshMetalPreview();
            else RefreshPreviewWindows();
        }

        void DrawPreviewTexture()
        {
            if (metalOutput != null) DrawMetalPreviewTexture();
            else DrawPreviewTextureWindows();
        }
    }
}
