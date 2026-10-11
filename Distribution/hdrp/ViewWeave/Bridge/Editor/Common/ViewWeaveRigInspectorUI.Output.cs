using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    // A single authoring frontend selects controls from the Rig's attached output.
    internal sealed partial class ViewWeaveRigInspectorUI
    {
        EnumField subpixelModeField;
        HelpBox displayDiscoveryStatus;
        Button refreshConnectedDisplaysButton;
        Label connectedDisplaysStatus;

        void BuildDisplayDiscoverySection()
        {
            Foldout foldout = AddFoldout("Looking Glass Display", true);
            displayDiscoveryStatus = new HelpBox("Reading Bridge device geometry", HelpBoxMessageType.Info) { name = "displayDiscoveryStatus" };
            foldout.Add(displayDiscoveryStatus);
            connectedDisplaysStatus = new Label { name = "connectedDisplaysStatus" };
            connectedDisplaysStatus.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(connectedDisplaysStatus);
            refreshConnectedDisplaysButton = new Button(RequestConnectedDisplayRefresh)
            { name = "refreshConnectedDisplays", text = "Refresh Connected Displays" };
            foldout.Add(refreshConnectedDisplaysButton);
            foldout.Add(new HelpBox("Device geometry is acquired automatically when the scene opens, without output or Preview. If you connect a display later, press Refresh Connected Displays. The last successful geometry is retained when disconnected.", HelpBoxMessageType.Info));
        }

        void RefreshConnectedDisplays()
        {
            if (displayDiscoveryStatus != null) displayDiscoveryStatus.text = BridgeEditorDisplayDiscovery.GetStatus(rig);
            bool canRefresh = !BridgeDisplayDiscovery.IsBusy && !playModeTransitioning;
            if (metalOutput != null)
                canRefresh &= !EditorApplication.isPlayingOrWillChangePlaymode && !metalOutput.IsRunning && !metalOutput.IsClosing &&
                    !BridgeMetalEditorPreview.IsActive && !BridgeMetalEditorPreview.IsStopping;
            else if (output != null)
                canRefresh &= output.State != BridgeWorkerQuiltOutput.WorkerState.Connecting &&
                    output.State != BridgeWorkerQuiltOutput.WorkerState.Closing && output.State != BridgeWorkerQuiltOutput.WorkerState.Fault;
            else canRefresh = false;
            refreshConnectedDisplaysButton?.SetEnabled(canRefresh);
            if (connectedDisplaysStatus == null) return;
            if (QuiltDisplaySettingsResolver.TryResolve(renderer, out _, out QuiltEditorDisplayProfile profile, out _) &&
                (profile.source == QuiltDisplaySettingsSource.BridgeSession ||
                 profile.source == QuiltDisplaySettingsSource.DevicePreview ||
                 profile.source == QuiltDisplaySettingsSource.ConnectedDisplaySnapshot ||
                 profile.source == QuiltDisplaySettingsSource.LastRecognizedDisplay))
            {
                if (profile.isConnected && displayDiscoveryStatus != null && !BridgeEditorDisplayDiscovery.HasCurrentRequest(rig))
                    displayDiscoveryStatus.text = "Bridge device geometry acquired from the active output";
                string dimensions = profile.hasPhysicalCalibration
                    ? "; aperture " + profile.widthMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " x " +
                      profile.heightMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm" : "; physical calibration unavailable";
                connectedDisplaysStatus.text = "Last successful Bridge geometry: aspect " + profile.aspect.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + dimensions +
                    (profile.isConnected ? "; active output" : "; retained metadata, no active output connection");
            }
            else connectedDisplaysStatus.text = "No Bridge geometry is available. Connect a Looking Glass display and press Refresh Connected Displays.";
        }

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
            foldout.Add(new HelpBox("Automatic uses calibrated subpixel cells when available and otherwise uses the default RGB/BGR pattern. Classic RGB forces evenly spaced RGB. Calibrated Cells requires a valid cell table and fails to start when calibration is unavailable. Stop output and close preview before changing this setting.", HelpBoxMessageType.Info));
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
