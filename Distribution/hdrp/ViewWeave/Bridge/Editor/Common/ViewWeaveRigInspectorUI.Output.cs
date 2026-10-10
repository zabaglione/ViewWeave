using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    // A single authoring frontend selects controls from the Rig's attached output.
    internal sealed partial class ViewWeaveRigInspectorUI
    {
        void InitializeOutputUI()
        {
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
