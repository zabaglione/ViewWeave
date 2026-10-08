using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    [CustomEditor(typeof(QuiltRenderer))]
    [CanEditMultipleObjects]
    public sealed class QuiltRendererEditor : UnityEditor.Editor
    {
        const string ShowClippingKey = "ViewWeave.QuiltRenderer.ShowClippingPlanes";
        const string ShowExtremeViewsKey = "ViewWeave.QuiltRenderer.ShowExtremeViews";
        const string ShowFocusCursorKey = "ViewWeave.QuiltRenderer.ShowFocusCursor";
        static bool showClipping = true;
        static bool showExtremeViews;
        static bool showFocusCursor;

        SerializedProperty settingsProperty;
        float horizontalOrbitDegrees;
        float verticalOrbitDegrees;

        void OnEnable()
        {
            settingsProperty = serializedObject.FindProperty("settings");
            showClipping = EditorPrefs.GetBool(ShowClippingKey, true);
            showExtremeViews = EditorPrefs.GetBool(ShowExtremeViewsKey, false);
            showFocusCursor = EditorPrefs.GetBool(ShowFocusCursorKey, false);
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
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
            DrawSetting("cameraDistance", "Focus Distance", "Distance in Unity units from the effective framing camera to the zero-parallax plane. This is not depth-of-field focus.");
            DrawSetting("fieldOfView", "Vertical Field of View", followsReferenceFov ? "The reference camera's FOV replaces this value before every render while FOV following is enabled." : null);
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
            bool clipping = EditorGUILayout.Toggle("Show Near / Far", showClipping);
            if (clipping != showClipping)
            {
                showClipping = clipping;
                EditorPrefs.SetBool(ShowClippingKey, showClipping);
                SceneView.RepaintAll();
            }
            bool extremes = EditorGUILayout.Toggle("Show Extreme Views", showExtremeViews);
            if (extremes != showExtremeViews)
            {
                showExtremeViews = extremes;
                EditorPrefs.SetBool(ShowExtremeViewsKey, showExtremeViews);
                SceneView.RepaintAll();
            }
            bool focusCursor = EditorGUILayout.Toggle(new GUIContent("Show 3D Focus Cursor", "Shows a free-move Scene View handle at the focus-plane center. Only its forward-axis projection changes Focus Distance; camera position and rotation are unchanged."), showFocusCursor);
            if (focusCursor != showFocusCursor)
            {
                showFocusCursor = focusCursor;
                EditorPrefs.SetBool(ShowFocusCursorKey, showFocusCursor);
                SceneView.RepaintAll();
            }
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

            bool playLocked = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            if (renderer.referenceCamera != null)
                EditorGUILayout.HelpBox("Manual camera operations target Reference Camera above. Quilt rendering continues to copy its world pose to Source Camera. Cinemachine or animation preview can overwrite manual edits; edit while not playing or previewing.", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Manual camera operations target Source Camera above. These controls edit the camera pose only when explicitly invoked.", MessageType.Info);

            if (playLocked)
                EditorGUILayout.HelpBox("Camera authoring controls are disabled while entering or running Play Mode.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(playLocked || framingCamera == null || renderer.settings == null))
            {
                if (GUILayout.Button(new GUIContent("Set Focus From Selected Object Center", "Projects the selected scene object's world bounds center onto the effective camera forward axis. Objects behind the camera are rejected.")))
                {
                    if (!TryGetSelectedSceneObjectCenter(out Vector3 center, out string reason))
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
                        if (QuiltRendererAuthoringOperations.TryGetFocusPoint(renderer, out _, out Vector3 focusPoint))
                        {
                            float size = Mathf.Max(renderer.settings.cameraDistance * 0.08f, 0.1f);
                            activeSceneView.Frame(new Bounds(focusPoint, Vector3.one * size), false);
                        }
                        else ShowActionWarning("The effective camera or focus distance is invalid.");
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

        static bool TryGetSelectedSceneObjectCenter(out Vector3 center, out string reason)
        {
            center = Vector3.zero;
            reason = "Select one scene object before setting the focus distance.";
            if (Selection.transforms == null || Selection.transforms.Length != 1 || Selection.activeGameObject == null)
                return false;
            GameObject selected = Selection.activeGameObject;
            if (EditorUtility.IsPersistent(selected) || !selected.scene.IsValid() || UnityEditor.SceneManagement.EditorSceneManager.IsPreviewSceneObject(selected))
            {
                reason = "The selected object must belong to a loaded scene.";
                return false;
            }

            Renderer[] renderers = selected.GetComponentsInChildren<Renderer>();
            bool hasBounds = false;
            Bounds bounds = default;
            for (int i = 0; i < renderers.Length; ++i)
            {
                if (renderers[i] == null || !renderers[i].enabled) continue;
                if (!hasBounds) { bounds = renderers[i].bounds; hasBounds = true; }
                else bounds.Encapsulate(renderers[i].bounds);
            }
            if (!hasBounds)
            {
                Collider[] colliders = selected.GetComponentsInChildren<Collider>();
                for (int i = 0; i < colliders.Length; ++i)
                {
                    if (colliders[i] == null || !colliders[i].enabled) continue;
                    if (!hasBounds) { bounds = colliders[i].bounds; hasBounds = true; }
                    else bounds.Encapsulate(colliders[i].bounds);
                }
            }
            center = hasBounds ? bounds.center : selected.transform.position;
            if (!IsFinite(center))
            {
                reason = "The selected object center is invalid.";
                return false;
            }
            reason = string.Empty;
            return true;
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
            if (targets.Length == 1) DrawFrustum((QuiltRenderer)target);
        }

        void DrawFrustum(QuiltRenderer renderer)
        {
            if (renderer == null || renderer.sourceCamera == null || renderer.settings == null)
                return;
            Camera framingCamera = renderer.EffectiveCamera;
            if (framingCamera == null || framingCamera.orthographic)
                return;
            Camera sourceCamera = renderer.sourceCamera;
            float fov = renderer.referenceCamera != null && renderer.followReferenceFieldOfView
                ? renderer.referenceCamera.fieldOfView
                : renderer.settings.fieldOfView;
            if (!QuiltFrustumGeometry.TryBuild(framingCamera.transform, renderer.settings, sourceCamera.nearClipPlane, sourceCamera.farClipPlane, fov, out QuiltFrustumGeometry.Geometry geometry))
                return;

            Color oldColor = Handles.color;
            Matrix4x4 oldMatrix = Handles.matrix;
            try
            {
                Handles.matrix = Matrix4x4.identity;

                if (showClipping)
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
                Handles.Label((framingCamera.transform.position + focusCenter) * 0.5f, renderer.settings.cameraDistance.ToString("0.###", CultureInfo.InvariantCulture) + " units");

                if (showExtremeViews)
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

                if (showClipping)
                {
                    Handles.color = new Color(0.8f, 0.8f, 0.8f, 0.55f);
                    DrawFrustumRays(geometry.nearPlane, geometry.farPlane);
                }
                if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    if (showFocusCursor) DrawFocusCursorHandle(renderer, framingCamera.transform, geometry.focusPlane);
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
            Undo.RecordObject(renderer, "Change ViewWeave Focus Distance");
            renderer.settings.cameraDistance = distance;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            SceneView.RepaintAll();
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
