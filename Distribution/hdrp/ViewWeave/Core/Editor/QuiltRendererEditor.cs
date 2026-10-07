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
        static bool showClipping = true;
        static bool showExtremeViews;

        SerializedProperty settingsProperty;

        void OnEnable()
        {
            settingsProperty = serializedObject.FindProperty("settings");
            showClipping = EditorPrefs.GetBool(ShowClippingKey, true);
            showExtremeViews = EditorPrefs.GetBool(ShowExtremeViewsKey, false);
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
            DrawSetting("cameraDistance", "Focus Distance", "Zero-parallax plane distance from the source camera. This is not depth-of-field focus.");
            DrawSetting("fieldOfView", "Vertical Field of View", null);
            if (serializedObject.ApplyModifiedProperties()) SceneView.RepaintAll();

            QuiltRenderer renderer = (QuiltRenderer)target;
            Camera sourceCamera = renderer.sourceCamera;
            if (sourceCamera == null)
            {
                EditorGUILayout.HelpBox("Assign a source camera to edit the clip planes and view its capture volume.", MessageType.Warning);
                return;
            }
            if (sourceCamera.orthographic)
                EditorGUILayout.HelpBox("The quilt renderer and capture-volume gizmo require a perspective camera.", MessageType.Warning);

            SerializedObject cameraObject = new SerializedObject(sourceCamera);
            cameraObject.Update();
            SerializedProperty nearClip = FindCameraProperty(cameraObject, "near clip plane", "m_NearClipPlane");
            SerializedProperty farClip = FindCameraProperty(cameraObject, "far clip plane", "m_FarClipPlane");
            if (nearClip != null) EditorGUILayout.PropertyField(nearClip, new GUIContent("Near Clip"));
            if (farClip != null) EditorGUILayout.PropertyField(farClip, new GUIContent("Far Clip"));
            if (cameraObject.ApplyModifiedProperties()) SceneView.RepaintAll();

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
            if (renderer == null || renderer.sourceCamera == null || renderer.sourceCamera.orthographic || renderer.settings == null)
                return;
            Camera camera = renderer.sourceCamera;
            if (!QuiltFrustumGeometry.TryBuild(camera.transform, renderer.settings, camera.nearClipPlane, camera.farClipPlane, out QuiltFrustumGeometry.Geometry geometry))
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
                DrawFocusDistanceHandle(renderer, geometry.focusPlane);
            }
            finally
            {
                Handles.matrix = oldMatrix;
                Handles.color = oldColor;
            }
        }

        static void DrawFocusDistanceHandle(QuiltRenderer renderer, Vector3[] focusPlane)
        {
            Camera camera = renderer.sourceCamera;
            Transform transform = camera.transform;
            Vector3 forward = transform.rotation * Vector3.forward;
            Vector3 origin = transform.position;
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
    }
}
