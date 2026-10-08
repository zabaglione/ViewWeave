using System;
using ViewWeave;
using UnityEditor;
using UnityEngine;

public static class QuiltCameraAuthoringTests
{
    [MenuItem("Tools/ViewWeave/Run Camera Authoring Validation")]
    public static void RunCameraAuthoringValidation()
    {
        Run();
    }

    public static void RunAllRegressions()
    {
        Run();
        QuiltCoreBatchTests.Run();
        QualityRigTests.Run();
    }

    public static void Run()
    {
        QuiltRendererAuthoringOperationsTests.Run();
        CheckProjectionBoundsAndFocusPlane();
        CheckParentTransformUsesCameraPose();
        CheckInvalidInputsAreRejected();
        CheckInspectorPropertiesAndUndo();
        Debug.Log("Quilt camera authoring tests passed");
    }

    static void CheckProjectionBoundsAndFocusPlane()
    {
        GameObject root = new GameObject("authoring-test-root");
        try
        {
            root.transform.position = new Vector3(2.1f, -3.2f, 4.3f);
            root.transform.rotation = Quaternion.Euler(13f, 47f, -21f);
            root.transform.localScale = new Vector3(2f, 0.4f, 3.5f);
            Camera camera = root.AddComponent<Camera>();
            camera.nearClipPlane = 0.13f;
            camera.farClipPlane = 73f;
            QuiltSettings settings = new QuiltSettings { views = 48, rows = 6, columns = 8, fieldOfView = 68f, viewAspect = 1.3f, cameraDistance = 2.7f, viewCone = 37f };
            Require(QuiltFrustumGeometry.TryBuild(camera.transform, settings, camera.nearClipPlane, camera.farClipPlane, out QuiltFrustumGeometry.Geometry geometry), "valid frustum geometry builds");

            float focusHalfHeight = settings.cameraDistance * Mathf.Tan(settings.fieldOfView * Mathf.Deg2Rad * 0.5f);
            float focusHalfWidth = focusHalfHeight * settings.viewAspect;
            Require(Mathf.Abs(Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[1]) * 0.5f - focusHalfHeight) < 0.0001f, "focus plane height follows vertical fov");
            Require(Mathf.Abs(Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[3]) * 0.5f - focusHalfWidth) < 0.0001f, "focus plane width follows view aspect");

            float maximumOffset = QuiltMath.ViewOffset(settings.views - 1, settings.views, settings.viewCone, settings.cameraDistance);
            CheckProjectedQuad(camera, settings, -maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.nearClipPlane, geometry.leftNearPlane, "left near");
            CheckProjectedQuad(camera, settings, maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.nearClipPlane, geometry.rightNearPlane, "right near");
            CheckProjectedQuad(camera, settings, -maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.farClipPlane, geometry.leftFarPlane, "left far");
            CheckProjectedQuad(camera, settings, maximumOffset, camera.nearClipPlane, camera.farClipPlane, camera.farClipPlane, geometry.rightFarPlane, "right far");

            for (int corner = 0; corner < 4; ++corner)
            {
                Vector2 expected = new Vector2(corner == 0 || corner == 1 ? -1f : 1f, corner == 0 || corner == 3 ? -1f : 1f);
                CheckProjectedPoint(camera, settings, -maximumOffset, camera.nearClipPlane, camera.farClipPlane, geometry.focusPlane[corner], expected, "left focus");
                CheckProjectedPoint(camera, settings, maximumOffset, camera.nearClipPlane, camera.farClipPlane, geometry.focusPlane[corner], expected, "right focus");
            }
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    static void CheckProjectedQuad(Camera camera, QuiltSettings settings, float eyeOffset, float nearClip, float farClip, float depth, Vector3[] quad, string label)
    {
        for (int i = 0; i < quad.Length; ++i)
        {
            Vector2 expected = new Vector2(i == 0 || i == 1 ? -1f : 1f, i == 0 || i == 3 ? -1f : 1f);
            CheckProjectedPoint(camera, settings, eyeOffset, nearClip, farClip, quad[i], expected, label + " corner " + i);
        }
        Require(Mathf.Abs(Vector3.Dot(quad[0] - camera.transform.position, camera.transform.rotation * Vector3.forward) - depth) < 0.0002f, label + " plane depth");
    }

    static void CheckProjectedPoint(Camera camera, QuiltSettings settings, float eyeOffset, float nearClip, float farClip, Vector3 point, Vector2 expected, string label)
    {
        Vector3 originalPosition = camera.transform.position;
        Quaternion originalRotation = camera.transform.rotation;
        try
        {
            camera.transform.SetPositionAndRotation(originalPosition + originalRotation * Vector3.right * eyeOffset, originalRotation);
            Matrix4x4 view = camera.worldToCameraMatrix;
            Matrix4x4 projection = QuiltMath.OffAxisProjection(settings.fieldOfView, settings.viewAspect, nearClip, farClip, eyeOffset, settings.cameraDistance);
            Vector4 clip = projection * (view * new Vector4(point.x, point.y, point.z, 1f));
            Require(Mathf.Abs(clip.w) > 0.000001f, label + " finite clip w");
            float x = clip.x / clip.w;
            float y = clip.y / clip.w;
            Require(Mathf.Abs(x - expected.x) < 0.0005f && Mathf.Abs(y - expected.y) < 0.0005f, label + " matches off-axis projection");
        }
        finally { camera.transform.SetPositionAndRotation(originalPosition, originalRotation); }
    }

    static void CheckParentTransformUsesCameraPose()
    {
        GameObject parent = new GameObject("scaled-parent");
        GameObject child = new GameObject("source-camera");
        try
        {
            parent.transform.position = new Vector3(-4f, 1f, 8f);
            parent.transform.rotation = Quaternion.Euler(23f, 71f, 12f);
            parent.transform.localScale = new Vector3(3f, 0.25f, 5f);
            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = new Vector3(0.6f, 1.2f, -0.9f);
            child.transform.localRotation = Quaternion.Euler(-7f, 15f, 31f);
            Camera camera = child.AddComponent<Camera>();
            QuiltSettings settings = new QuiltSettings { views = 8, rows = 2, columns = 4, fieldOfView = 51f, viewAspect = 0.85f, cameraDistance = 3.1f, viewCone = 24f };
            Require(QuiltFrustumGeometry.TryBuild(camera.transform, settings, camera.nearClipPlane, camera.farClipPlane, out QuiltFrustumGeometry.Geometry geometry), "scaled parent geometry builds");
            Vector3 expectedFocusCenter = camera.transform.position + camera.transform.rotation * Vector3.forward * settings.cameraDistance;
            Require(Vector3.Distance(Average(geometry.focusPlane), expectedFocusCenter) < 0.0001f, "focus center follows camera position and rotation, not inherited scale");
            Require(Mathf.Abs(Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[1]) - 2f * settings.cameraDistance * Mathf.Tan(settings.fieldOfView * Mathf.Deg2Rad * 0.5f)) < 0.0001f, "parent scale does not distort frustum dimensions");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(child);
            UnityEngine.Object.DestroyImmediate(parent);
        }
    }

    static void CheckInvalidInputsAreRejected()
    {
        GameObject go = new GameObject("invalid-authoring-test");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            QuiltSettings settings = new QuiltSettings();
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 0f, 100f, out _), "zero near clip is rejected");
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 1f, 1f, out _), "non-increasing clip range is rejected");
            settings.fieldOfView = float.NaN;
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 0.1f, 100f, out _), "non-finite fov is rejected");
            settings.fieldOfView = 60f;
            settings.cameraDistance = float.PositiveInfinity;
            Require(!QuiltFrustumGeometry.TryBuild(camera.transform, settings, 0.1f, 100f, out _), "non-finite focus distance is rejected");
            Require(!QuiltFrustumGeometry.TryBuild(null, settings, 0.1f, 100f, out _), "missing transform is rejected");
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static void CheckInspectorPropertiesAndUndo()
    {
        GameObject go = new GameObject("authoring-inspector-test");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;

            SerializedObject cameraObject = new SerializedObject(camera);
            SerializedProperty near = cameraObject.FindProperty("near clip plane") ?? cameraObject.FindProperty("m_NearClipPlane");
            SerializedProperty far = cameraObject.FindProperty("far clip plane") ?? cameraObject.FindProperty("m_FarClipPlane");
            Require(near != null && far != null, "inspector resolves source camera clip properties");
            Debug.Log("Camera clip serialized paths: " + near.propertyPath + ", " + far.propertyPath);

            bool sceneDirtyBeforeRepaint = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty;
            Vector3 sourcePositionBeforeRepaint = camera.transform.position;
            Quaternion sourceRotationBeforeRepaint = camera.transform.rotation;
            for (int repaint = 0; repaint < 5; ++repaint)
                Require(QuiltFrustumGeometry.TryBuild(camera.transform, renderer.settings, camera.nearClipPlane, camera.farClipPlane, out _), "repaint geometry remains valid");
            Require(sceneDirtyBeforeRepaint == UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().isDirty, "geometry refresh does not dirty the scene");
            Require(camera.transform.position == sourcePositionBeforeRepaint && camera.transform.rotation == sourceRotationBeforeRepaint, "geometry refresh preserves the source camera pose");

            float previousDistance = renderer.settings.cameraDistance;
            int group = Undo.GetCurrentGroup();
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Change ViewWeave Focus Distance");
            Undo.RecordObject(renderer, "Change ViewWeave Focus Distance");
            renderer.settings.cameraDistance = 7.125f;
            EditorUtility.SetDirty(renderer);
            Undo.FlushUndoRecordObjects();
            Require(Mathf.Abs(renderer.settings.cameraDistance - 7.125f) < 0.000001f, "numeric focus edit is applied");
            Undo.PerformUndo();
            Require(Mathf.Abs(renderer.settings.cameraDistance - previousDistance) < 0.000001f, "focus edit undo restores the exact value");
            Undo.RevertAllDownToGroup(group);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static Vector3 Average(Vector3[] points)
    {
        Vector3 sum = Vector3.zero;
        foreach (Vector3 point in points) sum += point;
        return sum / points.Length;
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
