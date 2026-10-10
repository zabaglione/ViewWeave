using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    public static class QuiltRendererSceneViewControls
    {
        const string ShowClippingKey = "ViewWeave.QuiltRenderer.ShowClippingPlanes";
        const string ShowExtremeViewsKey = "ViewWeave.QuiltRenderer.ShowExtremeViews";
        const string ShowFocusCursorKey = "ViewWeave.QuiltRenderer.ShowFocusCursor";

        public static bool ShowNearFar
        {
            get => EditorPrefs.GetBool(ShowClippingKey, true);
            set => SetPreference(ShowClippingKey, true, value);
        }

        public static bool ShowExtremeViews
        {
            get => EditorPrefs.GetBool(ShowExtremeViewsKey, false);
            set => SetPreference(ShowExtremeViewsKey, false, value);
        }

        public static bool Show3DFocusCursor
        {
            get => EditorPrefs.GetBool(ShowFocusCursorKey, false);
            set => SetPreference(ShowFocusCursorKey, false, value);
        }

        public static bool FrameFocusPlaneCenter(QuiltRenderer renderer)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (renderer == null || sceneView == null ||
                !QuiltRendererAuthoringOperations.TryGetFocusPoint(renderer, out _, out Vector3 focusPoint)) return false;
            float size = Mathf.Max(QuiltRendererAuthoringOperations.GetEffectiveFocusDistance(renderer) * 0.08f, 0.1f);
            sceneView.Frame(new Bounds(focusPoint, Vector3.one * size), false);
            return true;
        }

        static void SetPreference(string key, bool defaultValue, bool value)
        {
            if (EditorPrefs.GetBool(key, defaultValue) == value) return;
            EditorPrefs.SetBool(key, value);
            SceneView.RepaintAll();
        }
    }

    [CustomEditor(typeof(QuiltRenderer))]
    [CanEditMultipleObjects]
    public sealed class QuiltRendererEditor : UnityEditor.Editor
    {
        SerializedProperty settingsProperty;
        float horizontalOrbitDegrees;
        float verticalOrbitDegrees;

        void OnEnable()
        {
            settingsProperty = serializedObject.FindProperty("settings");
        }

        public override void OnInspectorGUI()
        {
            DrawTechnicalInspector();
            if (targets.Length == 1)
            {
                ViewWeaveRig rig = ((QuiltRenderer)target).GetComponent<ViewWeaveRig>();
                if (rig != null)
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox("This QuiltRenderer belongs to a ViewWeaveRig. Use the Rig authoring panel for normal camera, preview, and Bridge operations.", MessageType.Info);
                    if (ViewWeaveRigAuthoringLauncher.IsRegistered)
                    {
                        if (GUILayout.Button("Open ViewWeave Rig Authoring"))
                            ViewWeaveRigAuthoringLauncher.Open(rig);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("The Rig authoring window is provided by the optional ViewWeave Bridge Editor package.", MessageType.Info);
                    }
                }
            }
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Camera Framing", EditorStyles.boldLabel);

            if (targets.Length != 1)
            {
                EditorGUILayout.HelpBox("Camera framing controls require one selected QuiltRenderer. The standard multi-object inspector remains available above.", MessageType.Info);
                return;
            }

            serializedObject.Update();
            QuiltRenderer renderer = (QuiltRenderer)target;
            bool followsReferenceFov = renderer.referenceCamera != null && renderer.followReferenceFieldOfView;
            DrawReferenceCameraAuthoring(renderer);
            if (renderer.TryGetFraming(out QuiltCameraFraming currentFraming))
            {
                EditorGUI.BeginChangeCheck();
                bool follow = EditorGUILayout.Toggle("Follow Reference Camera FOV", renderer.followReferenceFieldOfView);
                if (EditorGUI.EndChangeCheck() && follow != renderer.followReferenceFieldOfView)
                {
                    if (!renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out string followError))
                        EditorUtility.DisplayDialog("ViewWeave", "FOV following was not changed: " + followError, "OK");
                    else
                    {
                        Undo.RecordObject(renderer, "Change ViewWeave FOV Following");
                        renderer.followReferenceFieldOfView = follow;
                        EditorUtility.SetDirty(renderer);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                }
                EditorGUI.BeginChangeCheck();
                float fieldOfView = EditorGUILayout.FloatField(followsReferenceFov ? "Reference Vertical Field of View" : "Vertical Field of View", currentFraming.VerticalFieldOfView);
                if (EditorGUI.EndChangeCheck()) TrySetFramingValue(renderer, new QuiltCameraFraming(fieldOfView, currentFraming.FocusDistance,
                    currentFraming.PhysicalFramingEnabled, currentFraming.ApertureWidthMillimeters,
                    currentFraming.ApertureHeightMillimeters, currentFraming.UnitsPerMeter), "Change ViewWeave Field of View");

                EditorGUI.BeginChangeCheck();
                QuiltViewRenderMode renderMode = (QuiltViewRenderMode)EditorGUILayout.EnumPopup("View Render Mode", renderer.viewRenderMode);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(renderer, "Change Quilt View Render Mode");
                    if (renderer.TrySetViewRenderMode(renderMode, out string modeError))
                    {
                        EditorUtility.SetDirty(renderer);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                    }
                    else EditorUtility.DisplayDialog("ViewWeave", modeError, "OK");
                }
            }
            if (renderer.physicalFraming != null && renderer.physicalFraming.enabled)
            {
                if (renderer.TryGetEffectiveSettings(out QuiltSettings effective, out string framingError))
                    EditorGUILayout.LabelField("Effective Zero-Parallax Distance", effective.cameraDistance.ToString("0.###", CultureInfo.InvariantCulture) + " units");
                else EditorGUILayout.HelpBox(framingError, MessageType.Error);
            }
            else
            {
                if (renderer.physicalFraming == null || !renderer.physicalFraming.enabled)
                {
                    float focusDistance = currentFraming.FocusDistance;
                    EditorGUI.BeginChangeCheck();
                    focusDistance = EditorGUILayout.FloatField(new GUIContent("Focus Distance", "Distance in Unity units from the effective framing camera to the zero-parallax plane. This is not depth-of-field focus."), focusDistance);
                    if (EditorGUI.EndChangeCheck() && !QuiltRendererAuthoringOperations.TrySetEffectiveFocusDistance(renderer, focusDistance))
                        EditorUtility.DisplayDialog("ViewWeave", "Focus Distance was not changed. Check its range, owner policy, and camera Stage.", "OK");
                }
            }
            QuiltPhysicalFraming physicalSettings = renderer.physicalFraming ?? new QuiltPhysicalFraming();
            EditorGUI.BeginChangeCheck();
            bool physicalEnabled = EditorGUILayout.Toggle("Use Physical 1:1 Framing", physicalSettings.enabled);
            float apertureWidth = EditorGUILayout.FloatField("Visible Aperture Width (mm)", physicalSettings.visibleApertureWidthMillimeters);
            float apertureHeight = EditorGUILayout.FloatField("Visible Aperture Height (mm)", physicalSettings.visibleApertureHeightMillimeters);
            float unitsPerMeter = EditorGUILayout.FloatField("Unity Units per Meter", physicalSettings.unitsPerMeter);
            if (EditorGUI.EndChangeCheck())
            {
                float candidateFov = followsReferenceFov ? renderer.referenceCamera.fieldOfView : renderer.settings.fieldOfView;
                TrySetFramingValue(renderer, new QuiltCameraFraming(candidateFov, currentFraming.FocusDistance,
                    physicalEnabled, apertureWidth, apertureHeight, unitsPerMeter), "Change Physical Display Framing");
            }
            if (renderer.physicalFraming != null && renderer.physicalFraming.enabled)
            {
                QuiltPhysicalFraming physical = renderer.physicalFraming;
                EditorGUILayout.LabelField("Physical Aperture", physical.visibleApertureWidthMillimeters.ToString("0.##", CultureInfo.InvariantCulture) + " x " +
                    physical.visibleApertureHeightMillimeters.ToString("0.##", CultureInfo.InvariantCulture) + " mm (" + physical.DiagonalInches.ToString("0.##", CultureInfo.InvariantCulture) + " in)");
            }
            if (QuiltDisplaySettingsResolver.TryGetConnectedPhysicalCalibration(renderer, out QuiltEditorDisplayProfile calibration))
            {
                using (new EditorGUI.DisabledScope(!renderer.TryCheckMutation(QuiltRendererMutationKind.Framing, out _)))
                {
                    if (GUILayout.Button("Use Connected Display Size") && renderer.TryGetFraming(out QuiltCameraFraming framing))
                        TrySetFramingValue(renderer, new QuiltCameraFraming(framing.VerticalFieldOfView, framing.FocusDistance, framing.PhysicalFramingEnabled,
                            calibration.widthMillimeters, calibration.heightMillimeters, framing.UnitsPerMeter), "Copy Connected Display Size");
                }
                EditorGUILayout.LabelField("Calibration DPI", calibration.dpi.ToString("0.##", CultureInfo.InvariantCulture));
            }
            else EditorGUILayout.HelpBox("No valid connected display calibration is available. Enter aperture dimensions manually; no default DPI is assumed.", MessageType.Info);
            if (serializedObject.ApplyModifiedProperties()) SceneView.RepaintAll();
            if (followsReferenceFov)
                EditorGUILayout.HelpBox("Vertical Field of View follows Reference Camera at render time. A manually entered value is replaced while this is enabled.", MessageType.Info);
            Camera sourceCamera = renderer.sourceCamera;
            if (sourceCamera == null)
            {
                EditorGUILayout.HelpBox("Assign a source camera to edit the clip planes and view its capture volume.", MessageType.Warning);
                return;
            }
            if (renderer.referenceCamera == sourceCamera && renderer.referenceCamera != null)
                EditorGUILayout.HelpBox("Reference Camera must be different from Source Camera. Source Camera is the disabled camera dedicated to Quilt rendering.", MessageType.Error);
            else if (renderer.referenceCamera != null && renderer.referenceCamera.transform.IsChildOf(sourceCamera.transform))
                EditorGUILayout.HelpBox("Reference Camera cannot be a child or descendant of Source Camera because copying the source pose would move the reference.", MessageType.Error);
            else if (renderer.referenceCamera != null && renderer.referenceCamera.orthographic)
                EditorGUILayout.HelpBox("Reference Camera must use perspective projection.", MessageType.Error);
            if (sourceCamera.orthographic)
                EditorGUILayout.HelpBox("The quilt renderer and capture-volume gizmo require a perspective camera.", MessageType.Warning);

            SerializedObject cameraObject = new SerializedObject(sourceCamera);
            cameraObject.Update();
            SerializedProperty nearClip = FindCameraProperty(cameraObject, "near clip plane", "m_NearClipPlane");
            SerializedProperty farClip = FindCameraProperty(cameraObject, "far clip plane", "m_FarClipPlane");
            if (nearClip != null) EditorGUILayout.PropertyField(nearClip, new GUIContent("Near Clip"));
            if (farClip != null) EditorGUILayout.PropertyField(farClip, new GUIContent("Far Clip"));
            if (cameraObject.ApplyModifiedProperties()) SceneView.RepaintAll();

            DrawAuthoringControls(renderer);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Scene View", EditorStyles.boldLabel);
            QuiltRendererSceneViewControls.ShowNearFar = EditorGUILayout.Toggle("Show Near / Far", QuiltRendererSceneViewControls.ShowNearFar);
            QuiltRendererSceneViewControls.ShowExtremeViews = EditorGUILayout.Toggle("Show Extreme Views", QuiltRendererSceneViewControls.ShowExtremeViews);
            QuiltRendererSceneViewControls.Show3DFocusCursor = EditorGUILayout.Toggle(new GUIContent("Show 3D Focus Cursor", "Shows a free-move Scene View handle at the focus-plane center. Only its forward-axis projection changes Focus Distance; camera position and rotation are unchanged."), QuiltRendererSceneViewControls.Show3DFocusCursor);
        }

        static void TrySetFramingValue(QuiltRenderer renderer, QuiltCameraFraming framing, string undoName)
        {
            Camera reference = renderer.referenceCamera != null && renderer.followReferenceFieldOfView ? renderer.referenceCamera : null;
            if (reference != null && !QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, reference, out _, out string stageError))
            {
                EditorUtility.DisplayDialog("ViewWeave", "Framing was not changed: " + stageError, "OK");
                return;
            }
            if (!renderer.TryCheckMutation(new QuiltRendererMutationRequest(framing), out string mutationError))
            {
                EditorUtility.DisplayDialog("ViewWeave", mutationError, "OK");
                return;
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(renderer, undoName);
            if (reference != null) Undo.RecordObject(reference, undoName);
            if (!renderer.TrySetFraming(framing, out string error))
            {
                EditorUtility.DisplayDialog("ViewWeave", error, "OK");
                return;
            }
            Undo.CollapseUndoOperations(group);
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            if (reference != null)
            {
                EditorUtility.SetDirty(reference);
                PrefabUtility.RecordPrefabInstancePropertyModifications(reference);
            }
        }

        void DrawTechnicalInspector()
        {
            serializedObject.UpdateIfRequiredOrScript();
            SerializedProperty iterator = serializedObject.GetIterator();
            bool enterChildren = true;
            QuiltRenderer renderer = target as QuiltRenderer;
            bool structuralEditsAllowed = renderer != null && renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out _);
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyPath == "settings") DrawTechnicalSettings(iterator.Copy(), structuralEditsAllowed);
                else if (iterator.propertyPath == "viewRenderMode" || iterator.propertyPath == "physicalFraming" || iterator.propertyPath == "followReferenceFieldOfView" || iterator.propertyPath == "referenceCamera")
                {
                    using (new EditorGUI.DisabledScope(true)) EditorGUILayout.PropertyField(iterator, true);
                }
                else if (iterator.propertyPath == "sourceCamera" || iterator.propertyPath == "outputBehaviour" || iterator.propertyPath == "guardBehaviour" || iterator.propertyPath == "useRenderRequests")
                {
                    using (new EditorGUI.DisabledScope(!structuralEditsAllowed)) EditorGUILayout.PropertyField(iterator, true);
                }
                else EditorGUILayout.PropertyField(iterator, true);
            }
            serializedObject.ApplyModifiedProperties();
        }

        static void DrawReferenceCameraAuthoring(QuiltRenderer renderer)
        {
            Camera candidate = (Camera)EditorGUILayout.ObjectField("Reference Camera", renderer.referenceCamera, typeof(Camera), true);
            if (candidate == renderer.referenceCamera) return;
            if (!renderer.TryCheckMutation(QuiltRendererMutationKind.Structural, out string ownerError))
            {
                EditorUtility.DisplayDialog("ViewWeave", "Reference Camera was not changed: " + ownerError, "OK");
                return;
            }
            if (candidate != null)
            {
                string reason = string.Empty;
                if (candidate == renderer.sourceCamera) reason = "Reference Camera must be separate from the Source Camera.";
                else if (candidate.orthographic) reason = "Reference Camera must use perspective projection.";
                else if (renderer.sourceCamera != null && candidate.transform.IsChildOf(renderer.sourceCamera.transform)) reason = "Reference Camera cannot be a child of the Source Camera.";
                else if (!QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, candidate, out _, out reason)) { }
                if (!string.IsNullOrEmpty(reason))
                {
                    EditorUtility.DisplayDialog("ViewWeave", "Reference Camera was not changed: " + reason, "OK");
                    return;
                }
            }
            Undo.RecordObject(renderer, "Set ViewWeave Reference Camera");
            renderer.referenceCamera = candidate;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
        }

        static void DrawTechnicalSettings(SerializedProperty settings, bool structuralEditsAllowed)
        {
            EditorGUILayout.LabelField(settings.displayName, EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            try
            {
                SerializedProperty property = settings.Copy();
                SerializedProperty end = property.GetEndProperty();
                bool enterChildren = true;
                while (property.NextVisible(enterChildren) && !SerializedProperty.EqualContents(property, end))
                {
                    enterChildren = false;
                    bool apiControlled = property.name == "cameraDistance" || property.name == "fieldOfView";
                    using (new EditorGUI.DisabledScope(apiControlled || !structuralEditsAllowed))
                        EditorGUILayout.PropertyField(property, true);
                }
            }
            finally { EditorGUI.indentLevel--; }
        }

        void DrawAuthoringControls(QuiltRenderer renderer)
        {
            Camera framingCamera = renderer.EffectiveCamera;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Camera Authoring", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField(new GUIContent("Effective Camera"), framingCamera, typeof(Camera), true);
                EditorGUILayout.ObjectField(new GUIContent("Effective Camera Transform"), framingCamera == null ? null : framingCamera.transform, typeof(Transform), true);
            }

            bool playLocked = EditorApplication.isPlayingOrWillChangePlaymode;
            if (renderer.referenceCamera != null)
                EditorGUILayout.HelpBox("Manual camera operations target Reference Camera above. Quilt rendering continues to copy its world pose to Source Camera. Cinemachine or animation may overwrite manual edits during Play Mode.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Manual camera operations target Source Camera above. These controls edit the camera pose only when explicitly invoked.", MessageType.Info);

            if (playLocked)
                EditorGUILayout.HelpBox("Camera authoring controls are disabled during a Play Mode transition.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(playLocked || framingCamera == null || renderer.settings == null))
            {
                if (GUILayout.Button(new GUIContent("Set Focus From Selected Object Center", "Projects the selected scene object's world bounds center onto the effective camera forward axis. Objects behind the camera are rejected.")))
                {
                    if (!TryGetSelectedSceneObjectCenter(renderer, out Vector3 center, out string reason))
                        ShowActionWarning(reason);
                    else if (!QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, center, out _))
                        ShowActionWarning("The selected center is behind the effective camera or the camera framing references are invalid.");
                    SceneView.RepaintAll();
                }

                SceneView activeSceneView = SceneView.lastActiveSceneView;
                using (new EditorGUI.DisabledScope(activeSceneView == null))
                {
                    if (GUILayout.Button("Frame Focus Plane Center in Scene View"))
                    {
                        if (!QuiltRendererSceneViewControls.FrameFocusPlaneCenter(renderer))
                            ShowActionWarning("The effective camera or focus distance is invalid.");
                    }
                }

                horizontalOrbitDegrees = EditorGUILayout.FloatField("Horizontal Orbit (degrees)", horizontalOrbitDegrees);
                using (new EditorGUI.DisabledScope(!IsFinite(horizontalOrbitDegrees)))
                {
                    if (GUILayout.Button("Apply Horizontal Orbit"))
                        ApplyOrbit(renderer, Mathf.Clamp(horizontalOrbitDegrees, -QuiltRendererAuthoringOperations.MaximumHorizontalOrbitDegrees, QuiltRendererAuthoringOperations.MaximumHorizontalOrbitDegrees), 0f);
                }

                verticalOrbitDegrees = EditorGUILayout.FloatField("Vertical Orbit (degrees)", verticalOrbitDegrees);
                using (new EditorGUI.DisabledScope(!IsFinite(verticalOrbitDegrees)))
                {
                    if (GUILayout.Button("Apply Vertical Orbit"))
                        ApplyOrbit(renderer, 0f, Mathf.Clamp(verticalOrbitDegrees, -QuiltRendererAuthoringOperations.MaximumVerticalOrbitDegrees, QuiltRendererAuthoringOperations.MaximumVerticalOrbitDegrees));
                }
                EditorGUILayout.LabelField("Orbit is pivoted at the zero-parallax point. Horizontal is clamped to +/-180 degrees; vertical is clamped to +/-89 degrees.", EditorStyles.miniLabel);
            }
        }

        static void ApplyOrbit(QuiltRenderer renderer, float horizontalDegrees, float verticalDegrees)
        {
            if (!QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, horizontalDegrees, verticalDegrees))
                ShowActionWarning("Orbit could not be applied. Check the perspective camera reference and focus distance.");
            SceneView.RepaintAll();
        }

        static bool TryGetSelectedSceneObjectCenter(QuiltRenderer renderer, out Vector3 center, out string reason)
        {
            if (Selection.transforms == null || Selection.transforms.Length != 1)
            {
                center = Vector3.zero;
                reason = "Select one scene object before setting the focus distance.";
                return false;
            }
            return QuiltRendererAuthoringOperations.TryGetSceneObjectCenter(renderer, Selection.activeGameObject, out center, out reason);
        }

        static void ShowActionWarning(string message)
        {
            if (string.IsNullOrEmpty(message)) return;
            EditorUtility.DisplayDialog("ViewWeave Camera Authoring", message, "OK");
        }

        void DrawSetting(string relativePath, string label, string tooltip)
        {
            SerializedProperty property = settingsProperty == null ? null : settingsProperty.FindPropertyRelative(relativePath);
            if (property != null) EditorGUILayout.PropertyField(property, new GUIContent(label, tooltip));
        }

        static SerializedProperty FindCameraProperty(SerializedObject cameraObject, string preferredName, string fallbackName)
        {
            SerializedProperty property = cameraObject.FindProperty(preferredName);
            return property != null ? property : cameraObject.FindProperty(fallbackName);
        }

        void OnSceneGUI()
        {
            if (target is QuiltRenderer renderer) DrawFrustum(renderer);
        }

        void DrawFrustum(QuiltRenderer renderer)
        {
            if (renderer == null || renderer.sourceCamera == null || renderer.settings == null)
                return;
            Camera framingCamera = renderer.EffectiveCamera;
            if (framingCamera == null || framingCamera.orthographic)
                return;
            Camera sourceCamera = renderer.sourceCamera;
            float fov = QuiltFramingResolver.EffectiveFieldOfView(renderer);
            if (!QuiltDisplaySettingsResolver.TryResolve(renderer, out QuiltSettings effectiveSettings, out QuiltEditorDisplayProfile display, out _) ||
                !QuiltFrustumGeometry.TryBuild(framingCamera.transform, effectiveSettings, sourceCamera.nearClipPlane, sourceCamera.farClipPlane, fov, out QuiltFrustumGeometry.Geometry geometry))
                return;

            Color oldColor = Handles.color;
            Matrix4x4 oldMatrix = Handles.matrix;
            try
            {
                Handles.matrix = Matrix4x4.identity;

                if (QuiltRendererSceneViewControls.ShowNearFar)
                {
                    Handles.color = new Color(0.25f, 0.8f, 1f, 0.85f);
                    DrawPlane(geometry.nearPlane);
                    Handles.Label(Center(geometry.nearPlane), "Near clip");
                    Handles.color = new Color(1f, 0.55f, 0.2f, 0.85f);
                    DrawPlane(geometry.farPlane);
                    Handles.Label(Center(geometry.farPlane), "Far clip");
                }

                Handles.color = new Color(0.75f, 0.3f, 1f, 0.95f);
                DrawPlane(geometry.focusPlane);
                Handles.Label(Center(geometry.focusPlane), "Zero-parallax focus");
                Vector3 focusCenter = Center(geometry.focusPlane);
                Handles.DrawLine(framingCamera.transform.position, focusCenter);
                string distanceLabel = effectiveSettings.cameraDistance.ToString("0.###", CultureInfo.InvariantCulture) + " units; " + display.source;
                if (renderer.physicalFraming != null && renderer.physicalFraming.enabled)
                    distanceLabel += "; " + renderer.physicalFraming.visibleApertureWidthMillimeters.ToString("0.##", CultureInfo.InvariantCulture) + " x " + renderer.physicalFraming.visibleApertureHeightMillimeters.ToString("0.##", CultureInfo.InvariantCulture) + " mm";
                Handles.Label((framingCamera.transform.position + focusCenter) * 0.5f, distanceLabel);

                if (QuiltRendererSceneViewControls.ShowExtremeViews)
                {
                    Handles.color = new Color(0.3f, 1f, 0.5f, 0.7f);
                    DrawPlane(geometry.leftNearPlane);
                    DrawPlane(geometry.leftFarPlane);
                    DrawRays(geometry.leftNearPlane, geometry.leftFarPlane);
                    Handles.Label(Center(geometry.leftFarPlane), "Left extreme view");
                    Handles.color = new Color(1f, 0.85f, 0.25f, 0.7f);
                    DrawPlane(geometry.rightNearPlane);
                    DrawPlane(geometry.rightFarPlane);
                    DrawRays(geometry.rightNearPlane, geometry.rightFarPlane);
                    Handles.Label(Center(geometry.rightFarPlane), "Right extreme view");
                }

                if (QuiltRendererSceneViewControls.ShowNearFar)
                {
                    Handles.color = new Color(0.8f, 0.8f, 0.8f, 0.55f);
                    DrawFrustumRays(geometry.nearPlane, geometry.farPlane);
                }
                if (!EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (QuiltRendererSceneViewControls.Show3DFocusCursor) DrawFocusCursorHandle(renderer, framingCamera.transform, geometry.focusPlane);
                    else DrawFocusDistanceHandle(renderer, framingCamera.transform, geometry.focusPlane);
                }
            }
            finally
            {
                Handles.matrix = oldMatrix;
                Handles.color = oldColor;
            }
        }

        static void DrawFocusDistanceHandle(QuiltRenderer renderer, Transform framingTransform, Vector3[] focusPlane)
        {
            Vector3 forward = framingTransform.rotation * Vector3.forward;
            Vector3 origin = framingTransform.position;
            Vector3 center = Center(focusPlane);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.Slider(center, forward, HandleUtility.GetHandleSize(center) * 0.12f, Handles.ArrowHandleCap, 0f);
            if (!EditorGUI.EndChangeCheck()) return;

            float distance = Vector3.Dot(moved - origin, forward);
            if (float.IsNaN(distance) || float.IsInfinity(distance) || distance <= 0.001f) return;
            if (QuiltRendererAuthoringOperations.TrySetEffectiveFocusDistance(renderer, distance)) SceneView.RepaintAll();
        }

        static void DrawFocusCursorHandle(QuiltRenderer renderer, Transform framingTransform, Vector3[] focusPlane)
        {
            Vector3 center = Center(focusPlane);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(center, Quaternion.identity);
            if (!EditorGUI.EndChangeCheck()) return;

            if (!QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, moved, out _))
                return;
            SceneView.RepaintAll();
        }

        static void DrawPlane(Vector3[] points)
        {
            if (points == null || points.Length != 4) return;
            Handles.DrawAAPolyLine(2f, points[0], points[1], points[2], points[3], points[0]);
        }

        static void DrawFrustumRays(Vector3[] nearPlane, Vector3[] farPlane)
        {
            for (int i = 0; i < 4; ++i) Handles.DrawLine(nearPlane[i], farPlane[i]);
        }

        static void DrawRays(Vector3[] nearPlane, Vector3[] farPlane)
        {
            for (int i = 0; i < 4; ++i) Handles.DrawLine(nearPlane[i], farPlane[i]);
        }

        static Vector3 Center(Vector3[] points)
        {
            Vector3 center = Vector3.zero;
            for (int i = 0; i < points.Length; ++i) center += points[i];
            return center / points.Length;
        }

        static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
