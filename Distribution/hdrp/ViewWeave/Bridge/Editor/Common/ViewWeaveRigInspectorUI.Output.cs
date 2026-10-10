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
            var button = new Button { text = "Playback", tooltip = "Quilt Playback is currently supported by the Windows Bridge Session backend." };
            button.SetEnabled(false);
            return button;
        }

        void AddPlaybackHelp(Foldout foldout)
        {
            if (metalOutput != null)
                foldout.Add(new HelpBox("Quilt Capture and Video Export are shared tools. Quilt Playback is not yet implemented for Metal output.", HelpBoxMessageType.Info));
            else if (session == null)
                foldout.Add(new HelpBox("Quilt Playback requires the Rig's Bridge Session component and a validated quilt source.", HelpBoxMessageType.Info));
        }

        void StartPreview(bool outputToDevice)
        {
            if (metalOutput == null) StartPreviewWindows(outputToDevice);
        }

        void StopPreview()
        {
            if (metalOutput == null) StopPreviewWindows();
        }

        void ClosePreview()
        {
            if (metalOutput == null) ClosePreviewWindows();
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
