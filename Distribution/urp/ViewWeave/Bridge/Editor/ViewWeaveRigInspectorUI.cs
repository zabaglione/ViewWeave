using System;
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
        PropertyField followReferenceFieldOfViewField;
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
        VisualElement fieldOfViewContainer;
        VisualElement focusDistanceContainer;
        PropertyField focusDistanceField;
        Label effectiveFocusDistanceLabel;
        HelpBox physicalFocusError;
        Label physicalFramingStatus;
        Button copyConnectedDisplayButton;
        Toggle physicalFramingEnabledToggle;
        Button localPreviewButton;
        Button devicePreviewButton;
        Button stopPreviewButton;
        Button setFocusButton;
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
            RefreshPhysicalFramingState();
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
            followReferenceFieldOfViewField = AddBoundProperty(foldout, rendererObject, "followReferenceFieldOfView", "Follow Reference Camera FOV", () =>
            {
                RebuildFieldOfView();
                BridgeEditorPreview.RequestRefresh();
            });
            RefreshReferenceCameraState();
            focusDistanceContainer = new VisualElement();
            foldout.Add(focusDistanceContainer);
            focusDistanceField = AddBoundProperty(focusDistanceContainer, rendererObject, "settings.cameraDistance", "Focus Distance", BridgeEditorPreview.RequestRefresh);
            effectiveFocusDistanceLabel = new Label { name = "effectiveFocusDistance" };
            focusDistanceContainer.Add(effectiveFocusDistanceLabel);
            physicalFocusError = new HelpBox(string.Empty, HelpBoxMessageType.Error) { name = "physicalFocusError" };
            physicalFocusError.style.whiteSpace = WhiteSpace.Normal;
            focusDistanceContainer.Add(physicalFocusError);
            var physicalFoldout = new Foldout { text = "Physical Display Framing", value = false };
            physicalFoldout.style.marginTop = 4;
            physicalFoldout.style.marginBottom = 4;
            foldout.Add(physicalFoldout);
            physicalFramingEnabledToggle = new Toggle("Use Physical 1:1 Framing")
            {
                name = "physicalFramingEnabled",
                value = renderer != null && renderer.physicalFraming != null && renderer.physicalFraming.enabled
            };
            physicalFramingEnabledToggle.RegisterValueChangedCallback(evt => SetPhysicalFramingEnabled(evt.newValue));
            physicalFoldout.Add(physicalFramingEnabledToggle);
            AddBoundProperty(physicalFoldout, rendererObject, "physicalFraming.visibleApertureWidthMillimeters", "Visible Aperture Width (mm)", PhysicalFramingChanged);
            AddBoundProperty(physicalFoldout, rendererObject, "physicalFraming.visibleApertureHeightMillimeters", "Visible Aperture Height (mm)", PhysicalFramingChanged);
            AddBoundProperty(physicalFoldout, rendererObject, "physicalFraming.unitsPerMeter", "Unity Units per Meter", PhysicalFramingChanged);
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
            var selectionButton = new Button(UseSelection) { name = "useSelection", text = "Use Selection" };
            targetButtons.Add(selectionButton);
            setFocusButton = new Button(SetFocusFromTarget) { text = "Set Focus From Target Center" };
            targetButtons.Add(setFocusButton);
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
            Foldout foldout = AddFoldout("Rendering", false);
            AddBoundProperty(foldout, rendererObject, "viewRenderMode", "Quilt View Render Mode", BridgeEditorPreview.RequestRefresh);
            SerializedProperty profile = rigObject == null ? null : rigObject.FindProperty("profile");
            if (profile != null)
            {
                var profileField = new PropertyField(profile, "Rig Quality Profile (Local Preview)") { name = "rigQualityProfile" };
                profileField.Bind(rigObject);
                profileField.TrackPropertyValue(profile, _ => BridgeEditorPreview.RequestRefresh());
                foldout.Add(profileField);
                foldout.Add(new HelpBox("Local preview uses this Rig profile for quilt geometry. Device preview uses the connected device profile and attached Session Quality (Preview when no Session is attached).", HelpBoxMessageType.Info));
            }
            if (session != null)
            {
                qualityField = new EnumField("Session Quality", session.Quality) { name = "sessionQuality" };
                qualityField.RegisterValueChangedCallback(OnQualityChanged);
                foldout.Add(qualityField);
                var qualityHelp = new HelpBox("Quality changes in Play Mode use ViewWeaveBridgeSession.SetQuality so a live worker is reconfigured through its normal shutdown and restart path.", HelpBoxMessageType.Info);
                foldout.Add(qualityHelp);
            }
        }

        void BuildPreviewSection()
        {
            Foldout foldout = AddFoldout("Preview", true);
            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Column } };
            localPreviewButton = new Button(() => StartPreview(false)) { text = "Start Local Edit Mode Preview" };
            devicePreviewButton = new Button(() => StartPreview(true)) { text = "Start Looking Glass Device Preview" };
            stopPreviewButton = new Button(StopPreview) { text = "Stop Preview" };
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
                return;
            }

            AddBoundProperty(foldout, sessionObject, "startOnEnable", "Start Session On Enable");
            AddBoundProperty(foldout, sessionObject, "runtimePath", "Bridge Runtime Path");
            AddBoundProperty(foldout, sessionObject, "applicationName", "Application Name");
            AddBoundProperty(foldout, sessionObject, "displayOrdinal", "Display Ordinal");
            if (output != null) AddBoundProperty(foldout, outputObject, "zoom", "Bridge Worker Zoom");
            sessionStatus = new Label(string.Empty);
            sessionStatus.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(sessionStatus);
            sessionError = new Label(string.Empty);
            sessionError.style.whiteSpace = WhiteSpace.Normal;
            sessionError.style.color = new Color(1f, 0.45f, 0.35f);
            foldout.Add(sessionError);
            RefreshSessionStatus();
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
            AddBoundProperty(foldout, rendererObject, "useRenderRequests", "Use Render Requests");
            AddBoundProperty(foldout, rigObject, "outputBehaviour", "Output Component");
            AddBoundProperty(foldout, rigObject, "guardBehaviour", "Render Guard Component");
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

        void UseSelection()
        {
            setFocusTarget?.Invoke(ResolveSelectionTarget());
            if (focusTargetField != null && focusTargetProvider != null)
                focusTargetField.SetValueWithoutNotify(focusTargetProvider());
            BridgeEditorPreview.RequestRefresh();
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
                var referenceObject = new SerializedObject(renderer.referenceCamera);
                SerializedProperty property = referenceObject.FindProperty("m_FieldOfView") ?? referenceObject.FindProperty("field of view");
                if (property != null)
                {
                    var field = new PropertyField(property, "Reference Vertical Field of View") { name = "referenceFieldOfView" };
                    field.Bind(referenceObject);
                    field.TrackPropertyValue(property, _ => BridgeEditorPreview.RequestRefresh());
                    fieldOfViewContainer.Add(field);
                }
                fieldOfViewContainer.Add(new HelpBox("Rendering follows this Reference Camera FOV and replaces the renderer FOV before each render. Cinemachine or animation may overwrite it during preview.", HelpBoxMessageType.Info));
            }
            else
            {
                PropertyField field = AddBoundProperty(fieldOfViewContainer, rendererObject, "settings.fieldOfView", "Vertical Field of View", BridgeEditorPreview.RequestRefresh);
                if (field != null) field.name = "rendererFieldOfView";
                if (renderer.referenceCamera != null)
                    fieldOfViewContainer.Add(new HelpBox("Reference Camera FOV is not followed. Rendering uses the stored QuiltRenderer FOV.", HelpBoxMessageType.Info));
            }
        }

        void PhysicalFramingChanged()
        {
            RefreshPhysicalFramingState();
            BridgeEditorPreview.RequestRefresh();
        }

        void SetPhysicalFramingEnabled(bool enabled)
        {
            if (renderer == null) return;
            if (enabled)
            {
                QuiltPhysicalFraming framing = renderer.physicalFraming;
                float fieldOfView = QuiltFramingResolver.EffectiveFieldOfView(renderer);
                string error = "framing settings are missing.";
                QuiltPhysicalFraming candidate = framing == null ? null : framing.Clone();
                if (candidate != null) candidate.enabled = true;
                if (candidate == null || !candidate.TryResolve(fieldOfView, out _, out _, out error))
                {
                    if (physicalFramingStatus != null)
                        physicalFramingStatus.text = "Physical framing was not enabled: " + error;
                    physicalFramingEnabledToggle?.SetValueWithoutNotify(false);
                    return;
                }
            }
            Undo.RecordObject(renderer, enabled ? "Enable Physical Display Framing" : "Disable Physical Display Framing");
            if (renderer.physicalFraming == null) renderer.physicalFraming = new QuiltPhysicalFraming();
            renderer.physicalFraming.enabled = enabled;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            rendererObject?.Update();
            RefreshPhysicalFramingState();
            BridgeEditorPreview.RequestRefresh();
        }

        void RefreshPhysicalFramingState()
        {
            if (renderer == null || focusDistanceContainer == null) return;
            bool physicalEnabled = renderer.physicalFraming != null && renderer.physicalFraming.enabled;
            physicalFramingEnabledToggle?.SetValueWithoutNotify(physicalEnabled);
            QuiltSettings effective = null;
            string error = string.Empty;
            bool effectiveValid = physicalEnabled && renderer.TryGetEffectiveSettings(out effective, out error);
            focusDistanceField.style.display = physicalEnabled ? DisplayStyle.None : DisplayStyle.Flex;
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
                physicalFramingStatus.text += " Connected calibration: " + calibration.widthMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " x " +
                    calibration.heightMillimeters.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " mm at " + calibration.dpi.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " DPI; configured aperture " + (matches ? "matches" : "differs") + ".";
            }
            else physicalFramingStatus.text += " No valid connected physical calibration is available; saved dimensions are manual values or a previous explicit copy.";
            copyConnectedDisplayButton?.SetEnabled(canCopy);
            if (copyConnectedDisplayButton != null) copyConnectedDisplayButton.tooltip = canCopy ? "Copy the active Bridge calibration into the Rig's serialized aperture fields." : "A connected display with valid pixel dimensions and calibration DPI is required.";
        }

        void CopyConnectedDisplaySize()
        {
            if (renderer == null || !QuiltDisplaySettingsResolver.TryGetConnectedPhysicalCalibration(renderer, out QuiltEditorDisplayProfile profile)) return;
            var candidate = renderer.physicalFraming == null ? new QuiltPhysicalFraming() : renderer.physicalFraming.Clone();
            candidate.enabled = true;
            candidate.visibleApertureWidthMillimeters = profile.widthMillimeters;
            candidate.visibleApertureHeightMillimeters = profile.heightMillimeters;
            float fieldOfView = QuiltFramingResolver.EffectiveFieldOfView(renderer);
            if (!candidate.TryResolve(fieldOfView, out _, out _, out string error))
            {
                if (physicalFramingStatus != null) physicalFramingStatus.text = "Connected display size was not copied: " + error;
                return;
            }
            Undo.RecordObject(renderer, "Copy Connected Display Size");
            if (renderer.physicalFraming == null) renderer.physicalFraming = new QuiltPhysicalFraming();
            renderer.physicalFraming.visibleApertureWidthMillimeters = candidate.visibleApertureWidthMillimeters;
            renderer.physicalFraming.visibleApertureHeightMillimeters = candidate.visibleApertureHeightMillimeters;
            renderer.physicalFraming.enabled = true;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            rendererObject?.UpdateIfRequiredOrScript();
            RefreshPhysicalFramingState();
            BridgeEditorPreview.RequestRefresh();
        }

        bool TryAssignReferenceCamera(Camera candidate)
        {
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
                followReferenceFieldOfViewField.SetEnabled(editable && CanAuthor());
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
            if (EditorApplication.isPlaying)
            {
                if (!session.SetQuality(value)) ShowSessionError(session.LastError);
                else ShowSessionError(string.Empty);
            }
            else
            {
                Undo.RecordObject(session, "Change ViewWeave Session Quality");
                if (sessionObject == null) sessionObject = new SerializedObject(session);
                sessionObject.Update();
                SerializedProperty property = sessionObject.FindProperty("quality");
                if (property != null)
                {
                    int index = Array.IndexOf(property.enumNames, value.ToString());
                    if (index >= 0)
                    {
                        property.enumValueIndex = index;
                        sessionObject.ApplyModifiedProperties();
                    }
                }
            }
            BridgeEditorPreview.RequestRefresh();
            RefreshSessionStatus();
        }

        void SetFocusFromTarget()
        {
            if (!CanAuthor()) return;
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
            SetActionStatus("Focus Distance set to " + distance.ToString("0.###") + " Unity units.");
            BridgeEditorPreview.RequestRefresh();
            SceneView.RepaintAll();
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

        bool CanAuthor() => renderer != null && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;

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
            BridgeEditorPreview.Stop();
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
            stopPreviewButton?.SetEnabled(!isPlayTransition && activeForRig && !BridgeEditorPreview.IsStopping);
            setFocusButton?.SetEnabled(CanAuthor());
            horizontalOrbitButton?.SetEnabled(CanAuthor());
            verticalOrbitButton?.SetEnabled(CanAuthor());
            previewModeField?.SetEnabled(activeForRig);
            selectedViewField?.SetEnabled(activeForRig && previewMode == PreviewViewMode.SelectedLogicalView);

            if (activeForOtherRig)
                previewStatus.text = "An Edit Mode preview is active for " + BridgeEditorPreview.Target.name + ". Stop it from that Rig before starting another preview.";
            else if (BridgeEditorPreview.IsStopping)
                previewStatus.text = BridgeEditorPreview.Status ?? "Preview is stopping.";
            else if (activeForRig)
                previewStatus.text = BridgeEditorPreview.Status ?? "Preview is active.";
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
            qualityField?.SetEnabled(!playModeTransitioning);
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
