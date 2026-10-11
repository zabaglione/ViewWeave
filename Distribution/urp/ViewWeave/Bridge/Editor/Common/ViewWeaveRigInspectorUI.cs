using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ViewWeave.Editor
{
    internal sealed partial class ViewWeaveRigInspectorUI
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
        ViewWeaveRig rig;
        QuiltRenderer renderer;
        SerializedObject rigObject;
        SerializedObject rendererObject;
        SerializedObject sourceCameraObject;
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
        bool disposed;
        string framingStatusOverride = string.Empty;
        string referenceCameraAssignmentError = string.Empty;
        Camera referenceCameraAtAssignmentError;

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
            if (disposed) return;
            ClearReferenceCameraAssignmentError();
            structuralMutationFields.Clear();
            transportMutationFields.Clear();
            rig = rigProvider == null ? null : rigProvider();
            renderer = rig == null ? null : rig.Renderer != null ? rig.Renderer : rig.GetComponent<QuiltRenderer>();
            InitializeOutputUI();
            rigObject = rig == null ? null : new SerializedObject(rig);
            rendererObject = renderer == null ? null : new SerializedObject(renderer);
            sourceCameraObject = renderer == null || renderer.sourceCamera == null ? null : new SerializedObject(renderer.sourceCamera);
            MonoBehaviour guard = rigObject == null ? null : rigObject.FindProperty("guardBehaviour")?.objectReferenceValue as MonoBehaviour;
            guardObject = guard == null ? null : new SerializedObject(guard);
            root.Clear();

            if (rig == null)
            {
                var missing = new HelpBox("Assign a ViewWeaveRig to edit its camera, rendering, Bridge, and preview settings.", HelpBoxMessageType.Info);
                root.Add(missing);
                return;
            }

            if (renderer == null)
            {
                root.Add(new HelpBox("This Rig is missing its QuiltRenderer. Restore the component before editing rendering or output settings.", HelpBoxMessageType.Error));
                return;
            }

            AddIntro();
            BuildDisplayDiscoverySection();
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
            disposed = true;
        }

        public void Refresh(bool updateSelectionTarget = false)
        {
            if (disposed) return;
            // A scene close can destroy the target before Unity disables its Inspector.
            if (rig == null || renderer == null)
            {
                if ((rig == null && rigObject != null) || (renderer == null && rendererObject != null) ||
                    (rig != null && (rig.Renderer != null || rig.GetComponent<QuiltRenderer>() != null))) Rebuild();
                return;
            }
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
            var intro = new HelpBox("ViewWeave Rig is the entry point for camera, quality, capture, and display output settings. Technical output and render-pipeline components provide the backend.", HelpBoxMessageType.Info);
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
            referenceCameraField.RegisterValueChangedCallback(evt => OnReferenceCameraSelectionChanged(evt.newValue));
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
                value = renderer != null && renderer.physicalFraming != null && renderer.physicalFraming.enabled,
                tooltip = "Off by default. Turn this on explicitly to use the configured physical display size for 1:1 framing."
            };
            physicalFramingEnabledToggle.RegisterValueChangedCallback(evt => SetPhysicalFramingEnabled(evt.newValue));
            foldout.Add(physicalFramingEnabledToggle);
            var physicalToggleHelp = new HelpBox("Bridge display dimensions are acquired automatically when the scene opens. Connect the display and use Refresh Connected Displays at the top of this Inspector to retry. Turn this switch on to enable physical 1:1 framing.", HelpBoxMessageType.Info);
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
                AddCameraBoundProperty(foldout, sourceCameraObject, "near clip plane", "m_NearClipPlane", "Source Near Clip", RequestPreviewRefresh);
                AddCameraBoundProperty(foldout, sourceCameraObject, "far clip plane", "m_FarClipPlane", "Source Far Clip", RequestPreviewRefresh);
                AddBoundProperty(foldout, sourceCameraObject, "m_CullingMask", "Source Culling Mask", RequestPreviewRefresh);
            }

            focusTargetField = new ObjectField("Focus Target") { name = "focusTarget", objectType = typeof(UnityEngine.Object), allowSceneObjects = true };
            focusTargetField.value = focusTargetProvider == null ? null : focusTargetProvider();
            focusTargetField.RegisterValueChangedCallback(evt =>
            {
                setFocusTarget?.Invoke(evt.newValue);
                RequestPreviewRefresh();
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
                var profileField = new PropertyField(profile, "Local Quilt Profile") { name = "rigQualityProfile" };
                profileField.Bind(rigObject);
                profileField.TrackPropertyValue(profile, _ => RequestPreviewRefresh());
                foldout.Add(profileField);
                structuralMutationFields.Add(profileField);
                foldout.Add(new HelpBox("Local rendering uses this profile. Connected display output uses its calibrated geometry and the Output Quality setting.", HelpBoxMessageType.Info));
            }
            BuildOutputQuality(foldout);
            BuildPipelineRenderingSettings(foldout);
        }

        void BuildPipelineRenderingSettings(Foldout foldout)
        {
            SerializedProperty mode = guardObject?.FindProperty("renderingMode");
            if (mode == null || guardObject.FindProperty("historyMemoryBudgetMB") == null) return;
            bool hdrp = guardObject.FindProperty("exposureMode") != null;
            foldout.Add(new Label(hdrp ? "HDRP Rendering" : "URP Rendering"));
            AddPipelineProperty(foldout, "renderingMode", "View History Mode");
            AddPipelineProperty(foldout, "followReferenceRenderingSettings", "Follow Reference Rendering Settings");
            AddPipelineProperty(foldout, "historyMemoryBudgetMB", "History Resource Budget (MB)");
            AddPipelineProperty(foldout, "maximumHistoryCameras", "Maximum History Cameras");
            if (!hdrp)
            {
                foldout.Add(new HelpBox("Isolated View Histories keeps one Camera per rendered logical view. TAA follows the Reference Camera's Anti-aliasing setting and requires Post Processing, a single-sample target, a Universal Renderer, and no Camera Stack or dynamic resolution. Shared Camera Compatibility temporarily disables TAA. Stop preview/output before changing these settings.", HelpBoxMessageType.Info));
                return;
            }
            AddPipelineProperty(foldout, "exposureMode", "Quilt Exposure");
            AddPipelineProperty(foldout, "fallbackFixedExposureEV", "Manual Fixed EV");
            foldout.Add(new HelpBox("Isolated View Histories retains the scene's HDRP effects with one Camera per rendered logical view. Follow Scene meters the central view and shares its exposure across the Quilt; the first frames wait for metering. Manual Fixed EV explicitly overrides scene exposure. Higher EV makes the image darker. Stop preview/output before changing these settings.", HelpBoxMessageType.Info));
            var compatibility = new Foldout { text = "Shared Camera Compatibility Settings", value = false };
            AddPipelineProperty(compatibility, "adaptUnsupportedEffects", "Adapt Temporal Effects");
            AddPipelineProperty(compatibility, "fallbackFixedExposure", "Use Manual EV for Non-Fixed Exposure");
            compatibility.Add(new HelpBox("Used only in Shared Camera Compatibility mode: Motion Blur, SSR, SSGI and volumetric clouds are suppressed; TAA is disabled; volumetric fog uses Gaussian filtering. Non-Fixed exposure uses Manual Fixed EV when enabled. This mode changes the appearance of the scene.", HelpBoxMessageType.Warning));
            foldout.Add(compatibility);
        }

        void AddPipelineProperty(VisualElement parent, string propertyName, string label)
        {
            PropertyField field = AddBoundProperty(parent, guardObject, propertyName, label);
            if (field == null) return;
            field.name = propertyName;
            structuralMutationFields.Add(field);
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
            previewActions.Add(new Button(() => RequestPreviewRefresh()) { text = "Refresh Preview" });
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




        void BuildCapturePlaybackSection()
        {
            Foldout foldout = AddFoldout("Capture & Playback", false);
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            Button captureButton = new Button(() =>
            {
                if (!QuiltCaptureWindow.OpenForRenderer(renderer)) SetActionStatus("Quilt Capture kept its current target because recording is active or the Rig renderer is unavailable.");
            }) { text = "Capture" };
            Button playbackButton = BuildPlaybackButton();
            Button exportButton = new Button(() =>
            {
                if (!QuiltVideoExportWindow.OpenForRenderer(renderer)) SetActionStatus("Quilt Video Export kept its current recording or export job because one is active, or the Rig renderer is unavailable.");
            }) { text = "Video Export" };
            captureButton.SetEnabled(renderer != null);
            exportButton.SetEnabled(renderer != null);
            row.Add(captureButton);
            row.Add(playbackButton);
            row.Add(exportButton);
            foldout.Add(row);
            if (renderer == null) foldout.Add(new HelpBox("Quilt Capture and Quilt Video Export require the Rig's QuiltRenderer component.", HelpBoxMessageType.Info));
            AddPlaybackHelp(foldout);
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
            RequestPreviewRefresh();
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
            if (profileResolved && profile.hasPhysicalCalibration)
                physicalFramingStatus.text += " Last successful Bridge dimensions: " + profile.widthMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " x " +
                    profile.heightMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm at " + profile.dpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " DPI.";
            else physicalFramingStatus.text += " Connect a Looking Glass display and use Refresh Connected Displays at the top of this Inspector to acquire its dimensions.";
            if (!string.IsNullOrEmpty(framingStatusOverride)) physicalFramingStatus.text += " " + framingStatusOverride;
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
            RequestPreviewRefresh();
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
            RequestPreviewRefresh();
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
            RequestPreviewRefresh();
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
                SetReferenceCameraAssignmentError("Reference Camera was not changed: " + mutationError);
                return false;
            }
            if (!TryValidateReferenceCamera(candidate, out string reason))
            {
                if (referenceCameraField != null) referenceCameraField.SetValueWithoutNotify(renderer == null ? null : renderer.referenceCamera);
                SetReferenceCameraAssignmentError("Reference Camera was not changed: " + reason);
                return false;
            }

            if (renderer != null && renderer.referenceCamera != candidate)
            {
                Undo.RecordObject(renderer, "Set ViewWeave Reference Camera");
                renderer.referenceCamera = candidate;
                EditorUtility.SetDirty(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                rendererObject?.Update();
                RequestPreviewRefresh();
            }
            ClearReferenceCameraAssignmentError();
            if (referenceCameraField != null) referenceCameraField.SetValueWithoutNotify(candidate);
            RebuildFieldOfView();
            RefreshReferenceCameraState();
            return true;
        }

        void OnReferenceCameraSelectionChanged(UnityEngine.Object selection)
        {
            if (selection == null)
            {
                TryAssignReferenceCamera(null);
                return;
            }

            Camera candidate = selection as Camera;
            if (candidate == null && selection is GameObject gameObject)
                candidate = gameObject.GetComponent<Camera>();

            if (candidate == null)
            {
                if (referenceCameraField != null)
                    referenceCameraField.SetValueWithoutNotify(renderer == null ? null : renderer.referenceCamera);
                SetReferenceCameraAssignmentError("Reference Camera was not changed: Choose a Camera or GameObject with a Camera component on the same GameObject.");
                return;
            }

            TryAssignReferenceCamera(candidate);
        }

        void SetReferenceCameraAssignmentError(string message)
        {
            referenceCameraAssignmentError = message ?? string.Empty;
            referenceCameraAtAssignmentError = renderer == null ? null : renderer.referenceCamera;
            if (referenceCameraWarning != null)
            {
                referenceCameraWarning.text = referenceCameraAssignmentError;
                referenceCameraWarning.style.display = string.IsNullOrEmpty(referenceCameraAssignmentError) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        void ClearReferenceCameraAssignmentError()
        {
            referenceCameraAssignmentError = string.Empty;
            referenceCameraAtAssignmentError = null;
            if (referenceCameraWarning != null)
            {
                referenceCameraWarning.text = string.Empty;
                referenceCameraWarning.style.display = DisplayStyle.None;
            }
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
            if (!string.IsNullOrEmpty(referenceCameraAssignmentError) && renderer.referenceCamera != referenceCameraAtAssignmentError)
                ClearReferenceCameraAssignmentError();
            if (referenceCameraField != null && referenceCameraField.value != renderer.referenceCamera)
                referenceCameraField.SetValueWithoutNotify(renderer.referenceCamera);
            bool editable = TryValidateReferenceCamera(renderer.referenceCamera, out string reason);
            if (followReferenceFieldOfViewField != null)
                followReferenceFieldOfViewField.SetEnabled(editable && CanAuthor() && renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _));
            if (referenceCameraWarning != null)
            {
                if (!string.IsNullOrEmpty(referenceCameraAssignmentError))
                {
                    referenceCameraWarning.text = referenceCameraAssignmentError;
                    referenceCameraWarning.style.display = DisplayStyle.Flex;
                }
                else
                {
                    referenceCameraWarning.text = editable ? string.Empty : "Reference Camera is unavailable: " + reason + " Choose a Camera in the loaded Main Stage or the same Prefab Stage.";
                    referenceCameraWarning.style.display = editable ? DisplayStyle.None : DisplayStyle.Flex;
                }
            }
            if (editable != referenceCameraWasEditable)
            {
                referenceCameraWasEditable = editable;
                RebuildFieldOfView();
            }
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
            RequestPreviewRefresh();
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
            RequestPreviewRefresh();
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
            if (applied) RequestPreviewRefresh();
            SceneView.RepaintAll();
        }

        bool CanAuthor() => renderer != null && !playModeTransitioning;

        void SetActionStatus(string message)
        {
            if (actionStatus != null) actionStatus.text = message ?? string.Empty;
        }






        void ShowPreviewError(string value)
        {
            if (previewError == null) return;
            previewError.text = value ?? string.Empty;
            previewError.style.display = string.IsNullOrEmpty(value) ? DisplayStyle.None : DisplayStyle.Flex;
        }


        void RefreshMutationControlStates()
        {
            bool structuralAllowed = renderer != null && renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _);
            bool transportAllowed = renderer != null && renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out _);
            for (int i = 0; i < structuralMutationFields.Count; ++i)
                if (structuralMutationFields[i] != null) structuralMutationFields[i].SetEnabled(structuralAllowed);
            for (int i = 0; i < transportMutationFields.Count; ++i)
                if (transportMutationFields[i] != null) transportMutationFields[i].SetEnabled(transportAllowed);
            RefreshOutputMutationControls();
            if (autoRenderToggle != null) autoRenderToggle.SetEnabled(!playModeTransitioning && rig != null &&
                renderer != null && (structuralAllowed || renderer.TryCheckMutation(QuiltRendererMutationKind.AutoRender, false, out _)));
        }


        void UpdateSerializedObjects()
        {
            rigObject?.UpdateIfRequiredOrScript();
            rendererObject?.UpdateIfRequiredOrScript();
            sourceCameraObject?.UpdateIfRequiredOrScript();
            UpdateOutputSerializedObjects();
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
