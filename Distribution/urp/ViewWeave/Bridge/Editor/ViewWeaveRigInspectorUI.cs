using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    internal sealed class ViewWeaveRigInspectorUI
    {
        enum PreviewViewMode
        {
            FullQuilt,
            SelectedLogicalView
        }

        enum SelectedView
        {
            First,
            Center,
            Last
        }

        readonly VisualElement root;
        readonly Func<ViewWeaveRig> rigProvider;
        readonly Func<UnityEngine.Object> focusTargetProvider;
        readonly Action<UnityEngine.Object> setFocusTarget;
        readonly List<PropertyField> structuralMutationFields = new List<PropertyField>();
        readonly List<PropertyField> transportMutationFields = new List<PropertyField>();
        FloatField zoomField;
        ViewWeaveRig rig;
        QuiltRenderer renderer;
        ViewWeaveBridgeSession session;
        BridgeWorkerQuiltOutput output;
        SerializedObject rigObject;
        SerializedObject rendererObject;
        SerializedObject sourceCameraObject;
        SerializedObject sessionObject;
        SerializedObject outputObject;
        SerializedObject guardObject;
        ObjectField focusTargetField;
        ObjectField referenceCameraField;
        Toggle followReferenceFieldOfViewField;
        HelpBox referenceCameraWarning;
        EnumField qualityField;
        EnumField previewModeField;
        EnumField selectedViewField;
        IMGUIContainer previewContainer;
        Label previewStatus;
        HelpBox previewError;
        Label actionStatus;
        Label sessionStatus;
        Label sessionError;
        Button refreshConnectedDisplaysButton;
        Label connectedDisplaysStatus;
        VisualElement fieldOfViewContainer;
        VisualElement focusDistanceContainer;
        PropertyField focusDistanceField;
        FloatField focusDistanceInput;
        FloatField fieldOfViewInput;
        FloatField apertureWidthInput;
        FloatField apertureHeightInput;
        FloatField unitsPerMeterInput;
        EnumField viewRenderModeField;
        Label effectiveFocusDistanceLabel;
        HelpBox physicalFocusError;
        Label physicalFramingStatus;
        Button copyConnectedDisplayButton;
        Toggle physicalFramingEnabledToggle;
        Button localPreviewButton;
        Button devicePreviewButton;
        Button stopPreviewButton;
        Button closePreviewButton;
        Button setFocusButton;
        Button fitViewButton;
        HelpBox focusModeHelp;
        HelpBox fitViewHelp;
        Button horizontalOrbitButton;
        Button verticalOrbitButton;
        Toggle autoRenderToggle;
        HelpBox autoRenderWarning;
        FloatField horizontalOrbitField;
        FloatField verticalOrbitField;
        PreviewViewMode previewMode = PreviewViewMode.FullQuilt;
        SelectedView selectedView = SelectedView.Center;
        bool playModeTransitioning;
        bool referenceCameraWasEditable;
        bool updatingFramingControls;
        bool pendingCalibrationCopy;
        QuiltRenderer pendingCopyRenderer;
        ViewWeaveRig pendingCopyRig;
        BridgeWorkerQuiltOutput pendingCopyOutput;
        BridgeWorkerQuiltOutput pendingCopyRequestOutput;
        ViewWeaveBridgeSession pendingCopySession;
        QuiltCameraFraming pendingCopyFraming;
        uint pendingCopyOrdinal;
        float pendingCopyZoom;
        long pendingCopyRequestToken;
        string pendingCopySessionRuntimePath;
        string pendingCopySessionApplicationName;
        string pendingCopyOutputRuntimePath;
        string pendingCopyOutputApplicationName;
        uint pendingCopyOutputOrdinal;
        BridgeDisplaySnapshot lastRenderedDisplaySnapshot;
        string lastRenderedDisplayStatus;
        string lastRenderedDisplayText;
        string framingStatusOverride = string.Empty;

        public ViewWeaveRigInspectorUI(VisualElement root, Func<ViewWeaveRig> rigProvider,
            Func<UnityEngine.Object> focusTargetProvider, Action<UnityEngine.Object> setFocusTarget)
        {
            this.root = root;
            this.rigProvider = rigProvider;
            this.focusTargetProvider = focusTargetProvider;
            this.setFocusTarget = setFocusTarget;
        }

        public void Rebuild()
        {
            CancelPendingCalibrationCopy();
            structuralMutationFields.Clear();
            transportMutationFields.Clear();
            rig = rigProvider == null ? null : rigProvider();
            renderer = rig == null ? null : rig.Renderer != null ? rig.Renderer : rig.GetComponent<QuiltRenderer>();
            session = rig == null ? null : rig.GetComponent<ViewWeaveBridgeSession>();
            output = rig == null ? null : rig.GetComponent<BridgeWorkerQuiltOutput>();
            rigObject = rig == null ? null : new SerializedObject(rig);
            rendererObject = renderer == null ? null : new SerializedObject(renderer);
            sourceCameraObject = renderer == null || renderer.sourceCamera == null ? null : new SerializedObject(renderer.sourceCamera);
            sessionObject = session == null ? null : new SerializedObject(session);
            outputObject = output == null ? null : new SerializedObject(output);
            MonoBehaviour guard = rigObject == null ? null : rigObject.FindProperty("guardBehaviour")?.objectReferenceValue as MonoBehaviour;
            guardObject = guard == null ? null : new SerializedObject(guard);
            root.Clear();

            if (rig == null)
            {
                var missing = new HelpBox("Assign a ViewWeaveRig to edit its camera, rendering, Bridge, and preview settings.", HelpBoxMessageType.Info);
                root.Add(missing);
                return;
            }

            AddIntro();
            BuildCameraSection();
            BuildRenderingSection();
            BuildPreviewSection();
            BuildCapturePlaybackSection();
            BuildBridgeSection();
            BuildAdvancedSection();
            Refresh();
        }

        public void Dispose()
        {
            CancelPendingCalibrationCopy();
        }

        public void Refresh(bool updateSelectionTarget = false)
        {
            UpdateSerializedObjects();
            if (autoRenderToggle != null && rig != null && autoRenderToggle.value != rig.AutoRender)
                autoRenderToggle.SetValueWithoutNotify(rig.AutoRender);
            if (focusTargetField != null)
            {
                if (updateSelectionTarget) setFocusTarget?.Invoke(ResolveSelectionTarget());
                UnityEngine.Object target = focusTargetProvider == null ? null : focusTargetProvider();
                if (focusTargetField.value != target) focusTargetField.SetValueWithoutNotify(target);
            }
            RefreshReferenceCameraState();
            RefreshPreview();
            RefreshSessionStatus();
            RefreshConnectedDisplays();
            RefreshPhysicalFramingState();
            RefreshMutationControlStates();
        }

        public void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            playModeTransitioning = state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode;
            Refresh();
        }

        void AddIntro()
        {
            var intro = new HelpBox("Edit Mode authoring preview is independent of the Bridge runtime Session. Device preview submits frames to the selected Looking Glass; neither preview starts Unity Play Mode or starts the producer Session.", HelpBoxMessageType.Info);
            intro.style.marginBottom = 6;
            root.Add(intro);
        }

        void BuildCameraSection()
        {
            Foldout foldout = AddFoldout("Camera & Focus", true);
            foldout.name = "cameraAndFocus";
            referenceCameraField = new ObjectField("Reference Camera")
            {
                name = "referenceCamera",
                objectType = typeof(Camera),
                allowSceneObjects = true,
                value = renderer == null ? null : renderer.referenceCamera
            };
            referenceCameraField.RegisterValueChangedCallback(evt => TryAssignReferenceCamera(evt.newValue as Camera));
            foldout.Add(referenceCameraField);
            referenceCameraWarning = new HelpBox(string.Empty, HelpBoxMessageType.Warning) { name = "referenceCameraWarning" };
            referenceCameraWarning.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(referenceCameraWarning);
            followReferenceFieldOfViewField = new Toggle("Follow Reference Camera FOV")
            {
                name = "followReferenceFieldOfView",
                value = renderer != null && renderer.followReferenceFieldOfView
            };
            followReferenceFieldOfViewField.RegisterValueChangedCallback(OnFollowReferenceFovChanged);
            foldout.Add(followReferenceFieldOfViewField);
            RefreshReferenceCameraState();
            focusDistanceContainer = new VisualElement();
            foldout.Add(focusDistanceContainer);
            focusDistanceInput = new FloatField("Focus Distance") { name = "focusDistance" };
            focusDistanceInput.RegisterValueChangedCallback(evt => OnFocusDistanceChanged(evt.newValue));
            focusDistanceContainer.Add(focusDistanceInput);
            effectiveFocusDistanceLabel = new Label { name = "effectiveFocusDistance" };
            focusDistanceContainer.Add(effectiveFocusDistanceLabel);
            physicalFocusError = new HelpBox(string.Empty, HelpBoxMessageType.Error) { name = "physicalFocusError" };
            physicalFocusError.style.whiteSpace = WhiteSpace.Normal;
            focusDistanceContainer.Add(physicalFocusError);
            physicalFramingEnabledToggle = new Toggle("Use Physical 1:1 Framing")
            {
                name = "physicalFramingEnabled",
                value = renderer != null && renderer.physicalFraming != null && renderer.physicalFraming.enabled
            };
            physicalFramingEnabledToggle.RegisterValueChangedCallback(evt => SetPhysicalFramingEnabled(evt.newValue));
            foldout.Add(physicalFramingEnabledToggle);
            var physicalToggleHelp = new HelpBox("Use the display's physical size for 1:1 zero-parallax framing. Use Connected Display Size below to read calibration from Bridge and enable this setting.", HelpBoxMessageType.Info);
            physicalToggleHelp.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(physicalToggleHelp);
            var physicalFoldout = new Foldout { text = "Physical Display Framing", value = false };
            physicalFoldout.style.marginTop = 4;
            physicalFoldout.style.marginBottom = 4;
            foldout.Add(physicalFoldout);
            apertureWidthInput = new FloatField("Visible Aperture Width (mm)") { name = "apertureWidthMillimeters" };
            apertureHeightInput = new FloatField("Visible Aperture Height (mm)") { name = "apertureHeightMillimeters" };
            unitsPerMeterInput = new FloatField("Unity Units per Meter") { name = "unitsPerMeter" };
            apertureWidthInput.RegisterValueChangedCallback(_ => OnPhysicalDimensionsChanged());
            apertureHeightInput.RegisterValueChangedCallback(_ => OnPhysicalDimensionsChanged());
            unitsPerMeterInput.RegisterValueChangedCallback(_ => OnPhysicalDimensionsChanged());
            physicalFoldout.Add(apertureWidthInput);
            physicalFoldout.Add(apertureHeightInput);
            physicalFoldout.Add(unitsPerMeterInput);
            physicalFramingStatus = new Label { name = "physicalFramingStatus" };
            physicalFramingStatus.style.whiteSpace = WhiteSpace.Normal;
            physicalFoldout.Add(physicalFramingStatus);
            copyConnectedDisplayButton = new Button(CopyConnectedDisplaySize) { name = "copyConnectedDisplaySize", text = "Use Connected Display Size" };
            physicalFoldout.Add(copyConnectedDisplayButton);
            RefreshPhysicalFramingState();
            fieldOfViewContainer = new VisualElement();
            foldout.Add(fieldOfViewContainer);
            RebuildFieldOfView();

            Camera source = renderer == null ? null : renderer.sourceCamera;
            if (source == null)
            {
                foldout.Add(new HelpBox("Assign the Rig source Camera before editing clip planes or culling.", HelpBoxMessageType.Warning));
            }
            else
            {
                if (sourceCameraObject == null) sourceCameraObject = new SerializedObject(source);
                AddCameraBoundProperty(foldout, sourceCameraObject, "near clip plane", "m_NearClipPlane", "Source Near Clip", BridgeEditorPreview.RequestRefresh);
                AddCameraBoundProperty(foldout, sourceCameraObject, "far clip plane", "m_FarClipPlane", "Source Far Clip", BridgeEditorPreview.RequestRefresh);
                AddBoundProperty(foldout, sourceCameraObject, "m_CullingMask", "Source Culling Mask", BridgeEditorPreview.RequestRefresh);
            }

            focusTargetField = new ObjectField("Focus Target") { name = "focusTarget", objectType = typeof(UnityEngine.Object), allowSceneObjects = true };
            focusTargetField.value = focusTargetProvider == null ? null : focusTargetProvider();
            focusTargetField.RegisterValueChangedCallback(evt =>
            {
                setFocusTarget?.Invoke(evt.newValue);
                BridgeEditorPreview.RequestRefresh();
            });
            foldout.Add(focusTargetField);

            var targetButtons = new VisualElement { style = { flexDirection = FlexDirection.Column } };
            setFocusButton = new Button(SetFocusFromTarget) { text = "Set Focus From Target Center" };
            targetButtons.Add(setFocusButton);
            focusModeHelp = new HelpBox(string.Empty, HelpBoxMessageType.Info) { name = "focusModeHelp" };
            focusModeHelp.style.whiteSpace = WhiteSpace.Normal;
            targetButtons.Add(focusModeHelp);
            fitViewButton = new Button(FitViewToTarget) { name = "fitViewToTarget", text = "Fit View To Target" };
            targetButtons.Add(fitViewButton);
            fitViewHelp = new HelpBox("Fits the target bounds inside the current view with 10% margin while keeping the camera pose and target scale unchanged. Turn off Use Physical 1:1 Framing first.", HelpBoxMessageType.Info)
            {
                name = "fitViewHelp"
            };
            fitViewHelp.style.whiteSpace = WhiteSpace.Normal;
            targetButtons.Add(fitViewHelp);
            foldout.Add(targetButtons);

            horizontalOrbitField = new FloatField("Horizontal Orbit (degrees)") { value = 0f };
            verticalOrbitField = new FloatField("Vertical Orbit (degrees)") { value = 0f };
            foldout.Add(horizontalOrbitField);
            foldout.Add(verticalOrbitField);
            var orbitButtons = new VisualElement { style = { flexDirection = FlexDirection.Column } };
            horizontalOrbitButton = new Button(() => ApplyOrbit(true)) { text = "Apply Horizontal Orbit" };
            verticalOrbitButton = new Button(() => ApplyOrbit(false)) { text = "Apply Vertical Orbit" };
            orbitButtons.Add(horizontalOrbitButton);
            orbitButtons.Add(verticalOrbitButton);
            foldout.Add(orbitButtons);
            var orbitHelp = new Label("Orbit pivots at the zero-parallax point. Horizontal is clamped to +/-180 degrees and vertical to +/-89 degrees.");
            orbitHelp.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(orbitHelp);

            var sceneViewControls = new Foldout { text = "Scene View Controls", value = false };
            var showNearFar = new Toggle("Show Near / Far") { name = "showNearFar", value = QuiltRendererSceneViewControls.ShowNearFar };
            showNearFar.RegisterValueChangedCallback(evt => QuiltRendererSceneViewControls.ShowNearFar = evt.newValue);
            sceneViewControls.Add(showNearFar);
            var showExtremeViews = new Toggle("Show Extreme Views") { name = "showExtremeViews", value = QuiltRendererSceneViewControls.ShowExtremeViews };
            showExtremeViews.RegisterValueChangedCallback(evt => QuiltRendererSceneViewControls.ShowExtremeViews = evt.newValue);
            sceneViewControls.Add(showExtremeViews);
            var show3DFocusCursor = new Toggle("Show 3D Focus Cursor")
            {
                name = "show3DFocusCursor",
                value = QuiltRendererSceneViewControls.Show3DFocusCursor,
                tooltip = "Show the free-move handle at the focus plane. Its forward-axis projection changes focus distance; it does not move the Camera."
            };
            show3DFocusCursor.RegisterValueChangedCallback(evt => QuiltRendererSceneViewControls.Show3DFocusCursor = evt.newValue);
            sceneViewControls.Add(show3DFocusCursor);
            var frameFocusButton = new Button(() =>
            {
                if (!QuiltRendererSceneViewControls.FrameFocusPlaneCenter(renderer))
                    SetActionStatus("Could not frame the focus plane. Check the Scene View and camera settings.");
            }) { name = "frameFocusPlane", text = "Frame Focus Plane Center in Scene View" };
            sceneViewControls.Add(frameFocusButton);
            foldout.Add(sceneViewControls);
            actionStatus = new Label(string.Empty);
            actionStatus.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(actionStatus);
        }

        void BuildRenderingSection()
        {
            Foldout foldout = AddFoldout("Rendering", true);
            viewRenderModeField = new EnumField("Quilt View Render Mode", renderer == null ? QuiltViewRenderMode.Full : renderer.viewRenderMode)
            {
                name = "viewRenderMode"
            };
            viewRenderModeField.RegisterValueChangedCallback(OnViewRenderModeChanged);
            foldout.Add(viewRenderModeField);
            SerializedProperty profile = rigObject == null ? null : rigObject.FindProperty("profile");
            if (profile != null)
            {
                var profileField = new PropertyField(profile, "Rig Quality Profile (Local Preview)") { name = "rigQualityProfile" };
                profileField.Bind(rigObject);
                profileField.TrackPropertyValue(profile, _ => BridgeEditorPreview.RequestRefresh());
                foldout.Add(profileField);
                structuralMutationFields.Add(profileField);
                foldout.Add(new HelpBox("Local preview uses this Rig profile for quilt geometry. Device preview uses the connected device profile and attached Session Quality (Balanced when no Session is attached).", HelpBoxMessageType.Info));
            }
            if (session != null)
            {
                qualityField = new EnumField("Session Quality", session.Quality) { name = "sessionQuality" };
                qualityField.RegisterValueChangedCallback(OnQualityChanged);
                foldout.Add(qualityField);
                var qualityHelp = new HelpBox("Quality changes use ViewWeaveBridgeSession.SetQuality. A Ready worker is quiesced, its transfer is reset, and the new profile is applied without reconnecting the Bridge Session.", HelpBoxMessageType.Info);
                foldout.Add(qualityHelp);
            }
        }

        void BuildPreviewSection()
        {
            Foldout foldout = AddFoldout("Preview", true);
            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Column } };
            localPreviewButton = new Button(() => StartPreview(false)) { text = "Start Local Edit Mode Preview" };
            devicePreviewButton = new Button(() => StartPreview(true)) { text = "Start Looking Glass Device Preview" };
            stopPreviewButton = new Button(StopPreview) { text = "Stop Preview", name = "pauseResumePreview" };
            buttonRow.Add(localPreviewButton);
            buttonRow.Add(devicePreviewButton);
            buttonRow.Add(stopPreviewButton);
            foldout.Add(buttonRow);

            var previewActions = new VisualElement { style = { flexDirection = FlexDirection.Column } };
            previewModeField = new EnumField("Preview Image", previewMode);
            previewModeField.RegisterValueChangedCallback(evt =>
            {
                previewMode = (PreviewViewMode)evt.newValue;
                RefreshPreview();
            });
            selectedViewField = new EnumField("Logical View", selectedView);
            selectedViewField.RegisterValueChangedCallback(evt =>
            {
                selectedView = (SelectedView)evt.newValue;
                RefreshPreview();
            });
            previewActions.Add(previewModeField);
            previewActions.Add(selectedViewField);
            previewActions.Add(new Button(() => BridgeEditorPreview.RequestRefresh()) { text = "Refresh Preview" });
            foldout.Add(previewActions);

            previewStatus = new Label(string.Empty);
            previewStatus.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(previewStatus);
            previewError = new HelpBox(string.Empty, HelpBoxMessageType.Error);
            previewError.style.display = DisplayStyle.None;
            foldout.Add(previewError);
            previewContainer = new IMGUIContainer(DrawPreviewTexture);
            previewContainer.style.minHeight = 120;
            previewContainer.style.marginTop = 4;
            previewContainer.style.marginBottom = 4;
            foldout.Add(previewContainer);
        }

        void BuildBridgeSection()
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
            }
            if (output != null)
            {
                zoomField = new FloatField("Bridge Worker Zoom") { name = "bridgeWorkerZoom", value = output.zoom };
                zoomField.RegisterValueChangedCallback(OnZoomChanged);
                foldout.Add(zoomField);
                refreshConnectedDisplaysButton = new Button(RequestConnectedDisplayRefresh)
                {
                    name = "refreshConnectedDisplays",
                    text = "Refresh Connected Displays"
                };
                foldout.Add(refreshConnectedDisplaysButton);
                connectedDisplaysStatus = new Label { name = "connectedDisplaysStatus" };
                connectedDisplaysStatus.style.whiteSpace = WhiteSpace.Normal;
                foldout.Add(connectedDisplaysStatus);
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
            if (output == null) return;
            if (!BridgeEditorPreview.TryRequestDisplayRefresh(output, out string error)) SetActionStatus(error);
            RefreshConnectedDisplays();
        }

        void RefreshConnectedDisplays()
        {
            if (output == null) return;
            if (refreshConnectedDisplaysButton != null)
                refreshConnectedDisplaysButton.SetEnabled(!BridgeDisplayDiscovery.IsBusy &&
                    output.State != BridgeWorkerQuiltOutput.WorkerState.Connecting &&
                    output.State != BridgeWorkerQuiltOutput.WorkerState.Closing &&
                    output.State != BridgeWorkerQuiltOutput.WorkerState.Fault);
            if (connectedDisplaysStatus == null) return;
            BridgeDisplaySnapshot snapshot = output.CurrentConfigurationDisplaySnapshot;
            bool snapshotBelongsToAnotherConfiguration = false;
            if (snapshot == null)
            {
                snapshot = BridgeDisplayDiscovery.Snapshot;
                snapshotBelongsToAnotherConfiguration = snapshot != null;
            }
            string refreshStatus = output.DisplayRefreshStatus ?? string.Empty;
            if (ReferenceEquals(snapshot, lastRenderedDisplaySnapshot) && refreshStatus == lastRenderedDisplayStatus &&
                lastRenderedDisplayText != null)
            {
                if (connectedDisplaysStatus.text != lastRenderedDisplayText) connectedDisplaysStatus.text = lastRenderedDisplayText;
                return;
            }
            if (snapshot == null)
            {
                lastRenderedDisplayText = string.IsNullOrEmpty(refreshStatus)
                    ? "No display snapshot is available. Refresh to query connected displays and calibration."
                    : refreshStatus;
                connectedDisplaysStatus.text = lastRenderedDisplayText;
                lastRenderedDisplaySnapshot = snapshot;
                lastRenderedDisplayStatus = refreshStatus;
                return;
            }
            var lines = new List<string>
            {
                snapshotBelongsToAnotherConfiguration
                    ? "Global metadata snapshot for display discovery only; it is not bound to this Rig and cannot be used as its calibration. Refresh this Rig to bind a snapshot."
                    : string.IsNullOrEmpty(refreshStatus)
                        ? "Last connected display refresh generation: " + snapshot.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : refreshStatus
            };
            for (int i = 0; i < snapshot.Displays.Count; ++i)
            {
                BridgeDisplayProfile profile = snapshot.Displays[i];
                string calibration = profile.HasPhysicalCalibration
                    ? profile.PhysicalWidthMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " x " +
                      profile.PhysicalHeightMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm at " +
                      profile.CalibrationDpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " DPI"
                    : "physical calibration unavailable";
                lines.Add(profile.ToString() + "; " + calibration);
            }
            lastRenderedDisplayText = string.Join("\n", lines);
            connectedDisplaysStatus.text = lastRenderedDisplayText;
            lastRenderedDisplaySnapshot = snapshot;
            lastRenderedDisplayStatus = refreshStatus;
        }

        void BuildCapturePlaybackSection()
        {
            Foldout foldout = AddFoldout("Capture & Playback", false);
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            Button captureButton = new Button(() =>
            {
                if (!QuiltCaptureWindow.OpenForRenderer(renderer)) SetActionStatus("Quilt Capture kept its current target because recording is active or the Rig renderer is unavailable.");
            }) { text = "Capture" };
            Button playbackButton = new Button(() =>
            {
                if (!QuiltPlaybackWindow.OpenForSession(session)) SetActionStatus("Quilt Playback kept its current Bridge Session because playback is active or the Session is unavailable.");
            }) { text = "Playback" };
            Button exportButton = new Button(() =>
            {
                if (!QuiltVideoExportWindow.OpenForRenderer(renderer)) SetActionStatus("Quilt Video Export kept its current recording or export job because one is active, or the Rig renderer is unavailable.");
            }) { text = "Video Export" };
            captureButton.SetEnabled(renderer != null);
            playbackButton.SetEnabled(session != null);
            exportButton.SetEnabled(renderer != null);
            row.Add(captureButton);
            row.Add(playbackButton);
            row.Add(exportButton);
            foldout.Add(row);
            if (renderer == null) foldout.Add(new HelpBox("Quilt Capture and Quilt Video Export require the Rig's QuiltRenderer component.", HelpBoxMessageType.Info));
            if (session == null) foldout.Add(new HelpBox("Quilt Playback requires the Rig's Bridge Session component and a validated quilt source.", HelpBoxMessageType.Info));
            if (renderer != null) foldout.Add(new HelpBox("These shortcuts open the existing capture, playback, and export tools. They do not start or stop the producer Session.", HelpBoxMessageType.Info));
        }

        void BuildAdvancedSection()
        {
            Foldout foldout = AddFoldout("Advanced", false);
            closePreviewButton = new Button(ClosePreview) { text = "Close Preview", name = "closePreview" };
            foldout.Add(closePreviewButton);
            ObjectField sourceField = new ObjectField("Rig Source Camera") { objectType = typeof(Camera), allowSceneObjects = true, value = renderer == null ? null : renderer.sourceCamera };
            sourceField.SetEnabled(false);
            foldout.Add(sourceField);
            autoRenderToggle = new Toggle("Auto Render in Play Mode") { name = "autoRender", value = rig != null && rig.AutoRender };
            autoRenderToggle.RegisterValueChangedCallback(OnAutoRenderChanged);
            foldout.Add(autoRenderToggle);
            autoRenderWarning = new HelpBox(string.Empty, HelpBoxMessageType.Error) { name = "autoRenderWarning" };
            autoRenderWarning.style.display = DisplayStyle.None;
            foldout.Add(autoRenderWarning);
            foldout.Add(new HelpBox("This uses ViewWeaveRig.AutoRender validation. Automatic rendering runs in Play Mode; Edit Mode preview is started separately.", HelpBoxMessageType.Info));
            structuralMutationFields.Add(AddBoundProperty(foldout, rendererObject, "useRenderRequests", "Use Render Requests"));
            structuralMutationFields.Add(AddBoundProperty(foldout, rigObject, "outputBehaviour", "Output Component"));
            structuralMutationFields.Add(AddBoundProperty(foldout, rigObject, "guardBehaviour", "Render Guard Component"));
            if (guardObject != null)
            {
                bool hasHdrpSettings = guardObject.FindProperty("adaptUnsupportedEffects") != null ||
                    guardObject.FindProperty("fallbackFixedExposure") != null || guardObject.FindProperty("fallbackFixedExposureEV") != null;
                if (hasHdrpSettings)
                {
                    foldout.Add(new Label("HDRP Guard Settings"));
                    PropertyField adaptEffects = AddBoundProperty(foldout, guardObject, "adaptUnsupportedEffects", "Adapt Unsupported Effects");
                    PropertyField fixedExposure = AddBoundProperty(foldout, guardObject, "fallbackFixedExposure", "Fallback to Fixed Exposure");
                    PropertyField fixedExposureEV = AddBoundProperty(foldout, guardObject, "fallbackFixedExposureEV", "Fallback Fixed Exposure EV");
                    if (adaptEffects != null) adaptEffects.name = "adaptUnsupportedEffects";
                    if (fixedExposure != null) fixedExposure.name = "fallbackFixedExposure";
                    if (fixedExposureEV != null) fixedExposureEV.name = "fallbackFixedExposureEV";
                }
            }
            foldout.Add(new HelpBox("ViewWeaveRig is the authoring entry point. QuiltRenderer, BridgeWorkerQuiltOutput, Bridge Session, and pipeline adapters remain available here as technical components for compatibility and diagnostics.", HelpBoxMessageType.Info));
        }

        void OnAutoRenderChanged(ChangeEvent<bool> evt)
        {
            if (rig == null) return;
            bool previous = rig.AutoRender;
            if (evt.newValue == previous) return;
            if (autoRenderWarning != null) autoRenderWarning.style.display = DisplayStyle.None;

            Undo.RecordObject(rig, "Change ViewWeave Auto Render");
            if (renderer != null) Undo.RecordObject(renderer, "Change ViewWeave Auto Render");
            try
            {
                rig.AutoRender = evt.newValue;
                EditorUtility.SetDirty(rig);
                PrefabUtility.RecordPrefabInstancePropertyModifications(rig);
                if (renderer != null)
                {
                    EditorUtility.SetDirty(renderer);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                }
                SetActionStatus(evt.newValue ? "Auto Render enabled after Rig validation." : "Auto Render disabled.");
            }
            catch (Exception error)
            {
                autoRenderToggle?.SetValueWithoutNotify(previous);
                if (autoRenderWarning != null)
                {
                    autoRenderWarning.text = "Auto Render was not changed: " + error.Message;
                    autoRenderWarning.style.display = DisplayStyle.Flex;
                }
            }
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

        Foldout AddFoldout(string title, bool expanded)
        {
            var foldout = new Foldout { text = title, value = expanded };
            foldout.style.marginTop = 4;
            foldout.style.marginBottom = 4;
            root.Add(foldout);
            return foldout;
        }

        static PropertyField AddBoundProperty(VisualElement parent, SerializedObject serializedObject, string path, string label, Action onChanged = null)
        {
            if (serializedObject == null) return null;
            serializedObject.UpdateIfRequiredOrScript();
            SerializedProperty property = serializedObject.FindProperty(path);
            if (property == null) return null;
            var field = new PropertyField(property, label);
            field.Bind(serializedObject);
            if (onChanged != null) field.TrackPropertyValue(property, _ => onChanged());
            parent.Add(field);
            return field;
        }

        static PropertyField AddCameraBoundProperty(VisualElement parent, SerializedObject serializedObject, string preferredPath, string fallbackPath, string label, Action onChanged = null)
        {
            if (serializedObject == null) return null;
            serializedObject.UpdateIfRequiredOrScript();
            SerializedProperty property = serializedObject.FindProperty(preferredPath) ?? serializedObject.FindProperty(fallbackPath);
            if (property == null) return null;
            var field = new PropertyField(property, label);
            field.Bind(serializedObject);
            if (onChanged != null) field.TrackPropertyValue(property, _ => onChanged());
            parent.Add(field);
            return field;
        }

        void RebuildFieldOfView()
        {
            if (fieldOfViewContainer == null || renderer == null || rendererObject == null) return;
            fieldOfViewContainer.Clear();
            bool referenceCameraEditable = TryValidateReferenceCamera(renderer.referenceCamera, out string referenceError);
            if (!referenceCameraEditable)
            {
                fieldOfViewContainer.Add(new HelpBox("Reference Camera FOV is unavailable: " + referenceError, HelpBoxMessageType.Warning));
            }
            else if (renderer.referenceCamera != null && renderer.followReferenceFieldOfView)
            {
                fieldOfViewInput = new FloatField("Reference Vertical Field of View") { name = "referenceFieldOfView" };
                fieldOfViewInput.RegisterValueChangedCallback(evt => OnFieldOfViewChanged(evt.newValue));
                fieldOfViewContainer.Add(fieldOfViewInput);
                fieldOfViewContainer.Add(new HelpBox("Rendering follows this Reference Camera FOV and replaces the renderer FOV before each render. Cinemachine or animation may overwrite it during preview.", HelpBoxMessageType.Info));
            }
            else
            {
                fieldOfViewInput = new FloatField("Vertical Field of View") { name = "rendererFieldOfView" };
                fieldOfViewInput.RegisterValueChangedCallback(evt => OnFieldOfViewChanged(evt.newValue));
                fieldOfViewContainer.Add(fieldOfViewInput);
                if (renderer.referenceCamera != null)
                    fieldOfViewContainer.Add(new HelpBox("Reference Camera FOV is not followed. Rendering uses the stored QuiltRenderer FOV.", HelpBoxMessageType.Info));
            }
            RefreshFramingControls();
        }

        void OnFollowReferenceFovChanged(ChangeEvent<bool> evt)
        {
            if (renderer == null || updatingFramingControls) return;
            if (!renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out string error))
            {
                followReferenceFieldOfViewField.SetValueWithoutNotify(renderer.followReferenceFieldOfView);
                SetPhysicalStatus("FOV following was not changed: " + error);
                return;
            }
            if (evt.newValue && renderer.referenceCamera != null && !TryValidateReferenceCamera(renderer.referenceCamera, out error))
            {
                followReferenceFieldOfViewField.SetValueWithoutNotify(renderer.followReferenceFieldOfView);
                SetPhysicalStatus("FOV following was not changed: " + error);
                return;
            }
            Undo.RecordObject(renderer, "Change ViewWeave FOV Following");
            renderer.followReferenceFieldOfView = evt.newValue;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            RebuildFieldOfView();
            RefreshReferenceCameraState();
            BridgeEditorPreview.RequestRefresh();
        }

        void SetPhysicalFramingEnabled(bool enabled)
        {
            if (renderer == null || updatingFramingControls) return;
            if (!renderer.TryGetFraming(out QuiltCameraFraming framing)) return;
            CommitFraming(new QuiltCameraFraming(framing.VerticalFieldOfView, framing.FocusDistance, enabled,
                apertureWidthInput == null ? framing.ApertureWidthMillimeters : apertureWidthInput.value,
                apertureHeightInput == null ? framing.ApertureHeightMillimeters : apertureHeightInput.value,
                unitsPerMeterInput == null ? framing.UnitsPerMeter : unitsPerMeterInput.value),
                enabled ? "Enable Physical Display Framing" : "Disable Physical Display Framing");
        }

        void RefreshPhysicalFramingState()
        {
            if (renderer == null || focusDistanceContainer == null) return;
            bool physicalEnabled = renderer.physicalFraming != null && renderer.physicalFraming.enabled;
            updatingFramingControls = true;
            physicalFramingEnabledToggle?.SetValueWithoutNotify(physicalEnabled);
            if (renderer.physicalFraming != null)
            {
                apertureWidthInput?.SetValueWithoutNotify(renderer.physicalFraming.visibleApertureWidthMillimeters);
                apertureHeightInput?.SetValueWithoutNotify(renderer.physicalFraming.visibleApertureHeightMillimeters);
                unitsPerMeterInput?.SetValueWithoutNotify(renderer.physicalFraming.unitsPerMeter);
            }
            updatingFramingControls = false;
            RefreshFramingControls();
            QuiltSettings effective = null;
            string error = string.Empty;
            bool effectiveValid = physicalEnabled && renderer.TryGetEffectiveSettings(out effective, out error);
            if (focusDistanceInput != null) focusDistanceInput.style.display = physicalEnabled ? DisplayStyle.None : DisplayStyle.Flex;
            effectiveFocusDistanceLabel.style.display = physicalEnabled && effectiveValid ? DisplayStyle.Flex : DisplayStyle.None;
            effectiveFocusDistanceLabel.text = effectiveValid
                ? "Effective Focus Distance: " + effective.cameraDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " units"
                : string.Empty;
            physicalFocusError.style.display = physicalEnabled && !effectiveValid ? DisplayStyle.Flex : DisplayStyle.None;
            physicalFocusError.text = error;

            bool profileResolved = QuiltDisplaySettingsResolver.TryResolve(renderer, out QuiltSettings effectiveSettings,
                out QuiltEditorDisplayProfile profile, out string profileError);
            if (renderer.physicalFraming != null && renderer.physicalFraming.enabled)
            {
                string dimensions = renderer.physicalFraming.visibleApertureWidthMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " x " +
                    renderer.physicalFraming.visibleApertureHeightMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm (" +
                    renderer.physicalFraming.DiagonalInches.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " in diagonal)";
                physicalFramingStatus.text = profileResolved
                    ? "Geometry source: " + profile.source + "; effective aspect: " + effectiveSettings.viewAspect.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + "; aperture: " + dimensions + ". Physical 1:1 framing controls the zero-parallax plane, not all-depth optical scale."
                    : "Geometry source: " + profile.source + "; physical framing is invalid: " + profileError;
            }
            else physicalFramingStatus.text = "Display aspect source: " + profile.source + "; aspect: " + profile.aspect.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) + ". Physical framing is disabled.";
            bool canCopy = QuiltDisplaySettingsResolver.TryGetConnectedPhysicalCalibration(renderer, out QuiltEditorDisplayProfile calibration);
            if (canCopy)
            {
                bool matches = physicalEnabled && Math.Abs(renderer.physicalFraming.visibleApertureWidthMillimeters - calibration.widthMillimeters) < 0.1f &&
                    Math.Abs(renderer.physicalFraming.visibleApertureHeightMillimeters - calibration.heightMillimeters) < 0.1f;
                string calibrationSource = calibration.source == QuiltDisplaySettingsSource.ConnectedDisplaySnapshot
                    ? "Last Bridge refresh" : "Active Bridge profile";
                physicalFramingStatus.text += " " + calibrationSource + " calibration: " + calibration.widthMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " x " +
                    calibration.heightMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm at " + calibration.dpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " DPI; configured aperture " + (matches ? "matches" : "differs") + ".";
            }
            else physicalFramingStatus.text += " No valid connected physical calibration is available; saved dimensions are manual values or a previous explicit copy.";
            string mutationReason = string.Empty;
            bool canCopyMutation = renderer.TryCheckMutation(QuiltRendererMutationKind.Framing, out mutationReason);
            if (canCopy && canCopyMutation && renderer.TryGetFraming(out QuiltCameraFraming currentFraming))
                canCopyMutation = renderer.TryCheckMutation(new QuiltRendererMutationRequest(new QuiltCameraFraming(
                    currentFraming.VerticalFieldOfView, currentFraming.FocusDistance, true,
                    calibration.widthMillimeters, calibration.heightMillimeters, currentFraming.UnitsPerMeter)), out mutationReason);
            bool canRequestCalibration = output != null && !BridgeDisplayDiscovery.IsBusy &&
                BridgeEditorPreview.CanRefreshDisplaysForRig(rig) &&
                (output.Ready || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed) &&
                (session == null || session.State == ViewWeaveBridgeSession.SessionState.Idle || session.State == ViewWeaveBridgeSession.SessionState.Rendering);
            copyConnectedDisplayButton?.SetEnabled(canCopy ? canCopyMutation : canRequestCalibration && canCopyMutation);
            if (copyConnectedDisplayButton != null) copyConnectedDisplayButton.tooltip = canCopyMutation
                ? canCopy ? "Copy the latest valid Bridge calibration into the Rig's serialized aperture fields."
                    : canRequestCalibration ? "Refresh Bridge metadata now, then copy calibration if this Rig remains unchanged."
                    : "Refresh Connected Displays is unavailable while a Bridge owner is starting, stopping, or recovering."
                : mutationReason;
            if (!string.IsNullOrEmpty(framingStatusOverride)) physicalFramingStatus.text += " " + framingStatusOverride;
        }

        void CopyConnectedDisplaySize()
        {
            if (renderer == null || !renderer.TryGetFraming(out QuiltCameraFraming framing)) return;
            if (QuiltDisplaySettingsResolver.TryGetConnectedPhysicalCalibration(renderer, out QuiltEditorDisplayProfile profile))
            {
                CommitFraming(new QuiltCameraFraming(framing.VerticalFieldOfView, framing.FocusDistance, true,
                    profile.widthMillimeters, profile.heightMillimeters, framing.UnitsPerMeter), "Copy Connected Display Size");
                return;
            }
            if (output == null)
            {
                SetPhysicalStatus("No valid display calibration is cached. Configure a Bridge runtime, then use Refresh Connected Displays.");
                return;
            }
            pendingCalibrationCopy = true;
            pendingCopyRenderer = renderer;
            pendingCopyRig = rig;
            pendingCopyOutput = output;
            pendingCopySession = session;
            pendingCopyFraming = framing;
            pendingCopyOrdinal = session == null ? output.displayOrdinal : session.displayOrdinal;
            pendingCopyZoom = output.zoom;
            pendingCopySessionRuntimePath = session == null ? string.Empty : session.runtimePath;
            pendingCopySessionApplicationName = session == null ? string.Empty : session.applicationName;
            pendingCopyOutputRuntimePath = output.runtimePath ?? string.Empty;
            pendingCopyOutputApplicationName = output.applicationName ?? string.Empty;
            pendingCopyOutputOrdinal = output.displayOrdinal;
            if (!BridgeEditorPreview.TryRequestDisplayRefresh(output, out BridgeWorkerQuiltOutput requestOutput, out string error))
            {
                CancelPendingCalibrationCopy();
                SetPhysicalStatus("Connected calibration was not copied: " + error);
                return;
            }
            pendingCopyRequestOutput = requestOutput;
            pendingCopyRequestToken = requestOutput.LatestDisplayRefreshToken;
            requestOutput.ConnectedDisplayRefreshCompleted += OnCalibrationSnapshotPublished;
            SetPhysicalStatus("Refreshing Bridge metadata. Calibration will be copied only if this Rig and its framing remain unchanged.");
        }

        void OnCalibrationSnapshotPublished(long requestToken, BridgeDisplaySnapshot snapshot, string requestError)
        {
            if (!pendingCalibrationCopy) return;
            if (requestToken != pendingCopyRequestToken)
            {
                CancelPendingCalibrationCopy();
                return;
            }
            QuiltRenderer expectedRenderer = pendingCopyRenderer;
            ViewWeaveRig expectedRig = pendingCopyRig;
            BridgeWorkerQuiltOutput expectedOutput = pendingCopyOutput;
            BridgeWorkerQuiltOutput expectedRequestOutput = pendingCopyRequestOutput;
            ViewWeaveBridgeSession expectedSession = pendingCopySession;
            QuiltCameraFraming expectedFraming = pendingCopyFraming;
            uint expectedOrdinal = pendingCopyOrdinal;
            float expectedZoom = pendingCopyZoom;
            string expectedSessionRuntimePath = pendingCopySessionRuntimePath;
            string expectedSessionApplicationName = pendingCopySessionApplicationName;
            string expectedOutputRuntimePath = pendingCopyOutputRuntimePath;
            string expectedOutputApplicationName = pendingCopyOutputApplicationName;
            uint expectedOutputOrdinal = pendingCopyOutputOrdinal;
            CancelPendingCalibrationCopy();

            if (!string.IsNullOrEmpty(requestError))
            {
                SetPhysicalStatus("Connected calibration was not copied: " + requestError);
                return;
            }
            if (snapshot == null || rigProvider == null || rigProvider() != expectedRig || rig != expectedRig ||
                renderer != expectedRenderer || output != expectedOutput || session != expectedSession ||
                expectedRequestOutput == null || expectedRequestOutput.LatestDisplayRefreshToken != requestToken ||
                expectedRequestOutput.ConnectedDisplaySnapshot != snapshot ||
                expectedRequestOutput.CurrentConfigurationDisplaySnapshot != snapshot ||
                !BridgeEditorPreview.CanApplyDisplaySnapshotToRig(expectedRig) ||
                EditorApplication.isPlayingOrWillChangePlaymode || expectedRenderer == null || expectedOutput == null ||
                !expectedRenderer.TryGetFraming(out QuiltCameraFraming current) || current != expectedFraming ||
                expectedOutput.zoom != expectedZoom || (expectedSession != null && expectedSession.displayOrdinal != expectedOrdinal) ||
                (expectedSession != null && (!string.Equals(expectedSession.runtimePath ?? string.Empty, expectedSessionRuntimePath ?? string.Empty, StringComparison.Ordinal) ||
                                             !string.Equals(expectedSession.applicationName ?? string.Empty, expectedSessionApplicationName ?? string.Empty, StringComparison.Ordinal))) ||
                !string.Equals(expectedOutput.runtimePath ?? string.Empty, expectedOutputRuntimePath ?? string.Empty, StringComparison.Ordinal) ||
                !string.Equals(expectedOutput.applicationName ?? string.Empty, expectedOutputApplicationName ?? string.Empty, StringComparison.Ordinal) ||
                expectedOutput.displayOrdinal != expectedOutputOrdinal ||
                !snapshot.TryGetDisplay(expectedOrdinal, out BridgeDisplayProfile profile) || !profile.HasPhysicalCalibration)
            {
                SetPhysicalStatus("Connected calibration was not copied because the Rig changed, the query was stale, or valid calibration was unavailable.");
                return;
            }

            bool copied = CommitFraming(new QuiltCameraFraming(expectedFraming.VerticalFieldOfView, expectedFraming.FocusDistance, true,
                profile.PhysicalWidthMillimeters, profile.PhysicalHeightMillimeters, expectedFraming.UnitsPerMeter),
                "Copy Connected Display Size");
            if (!copied) SetPhysicalStatus("Connected calibration was found but could not be applied under the current renderer owner policy.");
        }

        void CancelPendingCalibrationCopy()
        {
            if (pendingCalibrationCopy && pendingCopyRequestOutput != null)
                pendingCopyRequestOutput.ConnectedDisplayRefreshCompleted -= OnCalibrationSnapshotPublished;
            pendingCalibrationCopy = false;
            pendingCopyRenderer = null;
            pendingCopyRig = null;
            pendingCopyOutput = null;
            pendingCopyRequestOutput = null;
            pendingCopySession = null;
            pendingCopyFraming = default;
            pendingCopyOrdinal = 0;
            pendingCopyZoom = 0f;
            pendingCopyRequestToken = 0;
            pendingCopySessionRuntimePath = string.Empty;
            pendingCopySessionApplicationName = string.Empty;
            pendingCopyOutputRuntimePath = string.Empty;
            pendingCopyOutputApplicationName = string.Empty;
            pendingCopyOutputOrdinal = 0;
        }

        void OnFieldOfViewChanged(float value)
        {
            if (renderer == null || updatingFramingControls || !renderer.TryGetFraming(out QuiltCameraFraming current)) return;
            CommitFraming(new QuiltCameraFraming(value, current.FocusDistance, current.PhysicalFramingEnabled,
                current.ApertureWidthMillimeters, current.ApertureHeightMillimeters, current.UnitsPerMeter),
                "Change ViewWeave Field of View");
        }

        void OnFocusDistanceChanged(float value)
        {
            if (renderer == null || updatingFramingControls) return;
            if (!QuiltRendererAuthoringOperations.TrySetEffectiveFocusDistance(renderer, value))
            {
                SetPhysicalStatus("Focus Distance was not changed. Check the requested distance, active owner policy, and editable camera Stage.");
                RefreshFramingControls();
                return;
            }
            BridgeEditorPreview.RequestRefresh();
            RefreshFramingControls();
            RefreshPhysicalFramingState();
        }

        void OnPhysicalDimensionsChanged()
        {
            if (renderer == null || updatingFramingControls || !renderer.TryGetFraming(out QuiltCameraFraming current)) return;
            CommitFraming(new QuiltCameraFraming(current.VerticalFieldOfView, current.FocusDistance,
                physicalFramingEnabledToggle != null && physicalFramingEnabledToggle.value,
                apertureWidthInput == null ? current.ApertureWidthMillimeters : apertureWidthInput.value,
                apertureHeightInput == null ? current.ApertureHeightMillimeters : apertureHeightInput.value,
                unitsPerMeterInput == null ? current.UnitsPerMeter : unitsPerMeterInput.value), "Change Physical Display Framing");
        }

        bool CommitFraming(QuiltCameraFraming candidate, string undoName)
        {
            if (renderer == null) return false;
            if (renderer.followReferenceFieldOfView && renderer.referenceCamera != null &&
                !TryValidateReferenceCamera(renderer.referenceCamera, out string referenceError))
            {
                SetPhysicalStatus("Framing was not changed: " + referenceError);
                RefreshFramingControls();
                return false;
            }
            if (!renderer.TryCheckMutation(new QuiltRendererMutationRequest(candidate), out string mutationError))
            {
                SetPhysicalStatus("Framing was not changed: " + mutationError);
                RefreshFramingControls();
                return false;
            }
            Camera reference = renderer.referenceCamera != null && renderer.followReferenceFieldOfView ? renderer.referenceCamera : null;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(renderer, undoName);
            if (reference != null) Undo.RecordObject(reference, undoName);
            if (!renderer.TrySetFraming(candidate, out string error))
            {
                SetPhysicalStatus("Framing was not changed: " + error);
                RefreshFramingControls();
                return false;
            }
            Undo.CollapseUndoOperations(undoGroup);
            MarkFramingDirty(reference);
            SetPhysicalStatus(string.Empty);
            BridgeEditorPreview.RequestRefresh();
            RefreshFramingControls();
            RefreshPhysicalFramingState();
            return true;
        }

        void OnViewRenderModeChanged(ChangeEvent<Enum> evt)
        {
            if (renderer == null) return;
            QuiltViewRenderMode value = (QuiltViewRenderMode)evt.newValue;
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Change Quilt View Render Mode");
            Undo.RecordObject(renderer, "Change Quilt View Render Mode");
            if (!renderer.TrySetViewRenderMode(value, out string error))
            {
                viewRenderModeField?.SetValueWithoutNotify(renderer.viewRenderMode);
                SetActionStatus("View Render Mode was not changed: " + error);
                return;
            }
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            SetActionStatus(string.Empty);
            BridgeEditorPreview.RequestRefresh();
        }

        void MarkFramingDirty(Camera reference)
        {
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            if (reference == null) return;
            EditorUtility.SetDirty(reference);
            PrefabUtility.RecordPrefabInstancePropertyModifications(reference);
        }

        void RefreshFramingControls()
        {
            if (renderer == null) return;
            updatingFramingControls = true;
            if (renderer.TryGetFraming(out QuiltCameraFraming framing))
            {
                focusDistanceInput?.SetValueWithoutNotify(framing.FocusDistance);
                fieldOfViewInput?.SetValueWithoutNotify(framing.VerticalFieldOfView);
                apertureWidthInput?.SetValueWithoutNotify(framing.ApertureWidthMillimeters);
                apertureHeightInput?.SetValueWithoutNotify(framing.ApertureHeightMillimeters);
                unitsPerMeterInput?.SetValueWithoutNotify(framing.UnitsPerMeter);
            }
            followReferenceFieldOfViewField?.SetValueWithoutNotify(renderer.followReferenceFieldOfView);
            viewRenderModeField?.SetValueWithoutNotify(renderer.viewRenderMode);
            updatingFramingControls = false;

            bool canFrame = renderer.TryCheckMutation(QuiltRendererMutationKind.Framing, out string framingReason);
            bool referenceEditable = renderer.referenceCamera == null || TryValidateReferenceCamera(renderer.referenceCamera, out _);
            bool physicalEnabled = renderer.physicalFraming != null && renderer.physicalFraming.enabled;
            focusDistanceInput?.SetEnabled(canFrame && referenceEditable && (renderer.physicalFraming == null || !renderer.physicalFraming.enabled));
            fieldOfViewInput?.SetEnabled(canFrame && referenceEditable);
            apertureWidthInput?.SetEnabled(canFrame);
            apertureHeightInput?.SetEnabled(canFrame);
            unitsPerMeterInput?.SetEnabled(canFrame);
            physicalFramingEnabledToggle?.SetEnabled(canFrame);
            if (setFocusButton != null)
            {
                setFocusButton.text = physicalEnabled ? "Set Physical Focus From Target Center" : "Set Focus From Target Center";
                setFocusButton.SetEnabled(CanAuthor() && canFrame && referenceEditable);
            }
            if (focusModeHelp != null)
                focusModeHelp.text = physicalEnabled
                    ? "Physical 1:1 framing keeps aperture size and units unchanged; this action adjusts vertical FOV to place the target center on the zero-parallax plane."
                    : "Sets Focus Distance to the target center's forward distance and keeps the current vertical FOV.";
            fitViewButton?.SetEnabled(CanAuthor() && canFrame && referenceEditable && !physicalEnabled);
            if (fitViewHelp != null)
                fitViewHelp.text = physicalEnabled
                    ? "Turn off Use Physical 1:1 Framing above to enable Fit View To Target."
                    : "Fits the target bounds inside the current view with 10% margin while keeping the camera pose and target scale unchanged.";
            followReferenceFieldOfViewField?.SetEnabled(referenceEditable && renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _));
            viewRenderModeField?.SetEnabled(renderer.TryCheckMutation(QuiltRendererMutationKind.SamplingMode, out _));
            if (!canFrame && !string.IsNullOrEmpty(framingReason)) SetPhysicalStatus(framingReason);
        }

        void SetPhysicalStatus(string message)
        {
            framingStatusOverride = message ?? string.Empty;
            if (physicalFramingStatus != null) physicalFramingStatus.text = framingStatusOverride;
        }

        bool TryAssignReferenceCamera(Camera candidate)
        {
            if (renderer != null && renderer.referenceCamera != candidate &&
                !renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out string mutationError))
            {
                referenceCameraField?.SetValueWithoutNotify(renderer.referenceCamera);
                SetPhysicalStatus("Reference Camera was not changed: " + mutationError);
                return false;
            }
            if (!TryValidateReferenceCamera(candidate, out string reason))
            {
                if (referenceCameraField != null) referenceCameraField.SetValueWithoutNotify(renderer == null ? null : renderer.referenceCamera);
                if (referenceCameraWarning != null)
                {
                    referenceCameraWarning.text = "Reference Camera was not changed: " + reason;
                    referenceCameraWarning.style.display = DisplayStyle.Flex;
                }
                return false;
            }

            if (renderer != null && renderer.referenceCamera != candidate)
            {
                Undo.RecordObject(renderer, "Set ViewWeave Reference Camera");
                renderer.referenceCamera = candidate;
                EditorUtility.SetDirty(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                rendererObject?.Update();
                BridgeEditorPreview.RequestRefresh();
            }
            if (referenceCameraField != null) referenceCameraField.SetValueWithoutNotify(candidate);
            RebuildFieldOfView();
            RefreshReferenceCameraState();
            return true;
        }

        bool TryValidateReferenceCamera(Camera candidate, out string reason)
        {
            reason = string.Empty;
            if (candidate == null) return true;
            if (renderer == null || renderer.sourceCamera == null)
            {
                reason = "Assign the Rig source Camera before choosing a Reference Camera.";
                return false;
            }
            if (!QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, candidate, out _, out reason)) return false;
            if (candidate == renderer.sourceCamera || candidate.transform.IsChildOf(renderer.sourceCamera.transform))
            {
                reason = "The Reference Camera must be separate from the Rig source Camera and its children.";
                return false;
            }
            return true;
        }

        void RefreshReferenceCameraState()
        {
            if (renderer == null) return;
            if (referenceCameraField != null && referenceCameraField.value != renderer.referenceCamera)
                referenceCameraField.SetValueWithoutNotify(renderer.referenceCamera);
            bool editable = TryValidateReferenceCamera(renderer.referenceCamera, out string reason);
            if (followReferenceFieldOfViewField != null)
                followReferenceFieldOfViewField.SetEnabled(editable && CanAuthor() && renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _));
            if (referenceCameraWarning != null)
            {
                referenceCameraWarning.text = editable ? string.Empty : "Reference Camera is unavailable: " + reason + " Choose a Camera in the loaded Main Stage or the same Prefab Stage.";
                referenceCameraWarning.style.display = editable ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (editable != referenceCameraWasEditable)
            {
                referenceCameraWasEditable = editable;
                RebuildFieldOfView();
            }
        }

        void OnQualityChanged(ChangeEvent<Enum> evt)
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

        void SetFocusFromTarget()
        {
            if (!CanAuthor()) return;
            bool physical = renderer.physicalFraming != null && renderer.physicalFraming.enabled;
            float previousFieldOfView = renderer.TryGetFraming(out QuiltCameraFraming before) ? before.VerticalFieldOfView : 0f;
            float previousFocusDistance = GetCurrentEffectiveFocusDistance();
            if (!QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, focusTargetProvider == null ? null : focusTargetProvider(), out Vector3 center, out string reason))
            {
                SetActionStatus(reason);
                return;
            }
            if (!QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, center, out float distance))
            {
                SetActionStatus("The target center is behind the effective camera or the camera framing references are invalid.");
                return;
            }
            if (physical && renderer.TryGetFraming(out QuiltCameraFraming after))
                SetActionStatus("Physical focus set: vertical FOV " + previousFieldOfView.ToString("0.###") + "° -> " + after.VerticalFieldOfView.ToString("0.###") +
                    "°; effective focus " + previousFocusDistance.ToString("0.###") + " -> " + distance.ToString("0.###") + " Unity units.");
            else
                SetActionStatus("Focus Distance set to " + distance.ToString("0.###") + " Unity units; vertical FOV is unchanged.");
            BridgeEditorPreview.RequestRefresh();
            Refresh();
            SceneView.RepaintAll();
        }

        void FitViewToTarget()
        {
            if (!CanAuthor()) return;
            if (!QuiltRendererAuthoringOperations.TryApplyFitViewToTarget(renderer,
                    focusTargetProvider == null ? null : focusTargetProvider(), out float previousFov,
                    out float fieldOfView, out float focusDistance, out string reason))
            {
                SetActionStatus("Fit View To Target was not applied: " + reason);
                return;
            }
            SetActionStatus("View fitted: vertical FOV " + previousFov.ToString("0.###") + "° -> " + fieldOfView.ToString("0.###") +
                "°; focus distance " + focusDistance.ToString("0.###") + " Unity units.");
            BridgeEditorPreview.RequestRefresh();
            Refresh();
            SceneView.RepaintAll();
        }

        float GetCurrentEffectiveFocusDistance()
        {
            return renderer != null && renderer.TryGetEffectiveSettings(out QuiltSettings effective, out _)
                ? effective.cameraDistance
                : 0f;
        }

        void ApplyOrbit(bool horizontal)
        {
            if (!CanAuthor()) return;
            float amount = horizontal ? horizontalOrbitField.value : verticalOrbitField.value;
            if (float.IsNaN(amount) || float.IsInfinity(amount))
            {
                SetActionStatus("Enter a finite orbit angle.");
                return;
            }
            amount = horizontal
                ? Mathf.Clamp(amount, -QuiltRendererAuthoringOperations.MaximumHorizontalOrbitDegrees, QuiltRendererAuthoringOperations.MaximumHorizontalOrbitDegrees)
                : Mathf.Clamp(amount, -QuiltRendererAuthoringOperations.MaximumVerticalOrbitDegrees, QuiltRendererAuthoringOperations.MaximumVerticalOrbitDegrees);
            bool applied = QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, horizontal ? amount : 0f, horizontal ? 0f : amount);
            SetActionStatus(applied ? "Orbit applied." : "Orbit could not be applied. Check the perspective camera reference and focus distance.");
            if (applied) BridgeEditorPreview.RequestRefresh();
            SceneView.RepaintAll();
        }

        bool CanAuthor() => renderer != null && !playModeTransitioning;

        void SetActionStatus(string message)
        {
            if (actionStatus != null) actionStatus.text = message ?? string.Empty;
        }

        void StartPreview(bool outputToDevice)
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

        void StopPreview()
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

        void ClosePreview()
        {
            if (rig == null || BridgeEditorPreview.Target != rig) return;
            BridgeEditorPreview.CloseForTargetChange("Preview is closing at the user's request.");
            RefreshPreview();
        }

        void RefreshPreview()
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

        void DrawPreviewTexture()
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

        void ShowPreviewError(string value)
        {
            if (previewError == null) return;
            previewError.text = value ?? string.Empty;
            previewError.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void RefreshSessionStatus()
        {
            if (session == null) return;
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

        void RefreshMutationControlStates()
        {
            bool structuralAllowed = renderer != null && renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _);
            bool transportAllowed = renderer != null && renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out _);
            for (int i = 0; i < structuralMutationFields.Count; ++i)
                if (structuralMutationFields[i] != null) structuralMutationFields[i].SetEnabled(structuralAllowed);
            for (int i = 0; i < transportMutationFields.Count; ++i)
                if (transportMutationFields[i] != null) transportMutationFields[i].SetEnabled(transportAllowed);
            if (zoomField != null) zoomField.SetEnabled(output != null && renderer != null &&
                renderer.TryCheckMutation(QuiltRendererMutationKind.OutputZoom, output.zoom, out _));
            if (autoRenderToggle != null) autoRenderToggle.SetEnabled(!playModeTransitioning && rig != null &&
                renderer != null && (structuralAllowed || renderer.TryCheckMutation(QuiltRendererMutationKind.AutoRender, false, out _)));
        }

        void ShowSessionError(string message)
        {
            if (sessionError != null) sessionError.text = string.IsNullOrEmpty(message) ? string.Empty : "Session error: " + message;
        }

        void UpdateSerializedObjects()
        {
            rigObject?.UpdateIfRequiredOrScript();
            rendererObject?.UpdateIfRequiredOrScript();
            sourceCameraObject?.UpdateIfRequiredOrScript();
            sessionObject?.UpdateIfRequiredOrScript();
            outputObject?.UpdateIfRequiredOrScript();
            guardObject?.UpdateIfRequiredOrScript();
        }

        UnityEngine.Object ResolveSelectionTarget()
        {
            UnityEngine.Object selected = Selection.activeObject;
            GameObject selectedObject = selected as GameObject;
            if (selectedObject == null && selected is Component component) selectedObject = component.gameObject;
            return selectedObject == (rig == null ? null : rig.gameObject) ? null : selected;
        }
    }
}
