using System;
using ViewWeave;
using ViewWeave.Editor;
using UnityEditor;
using UnityEngine;

public static class QuiltRendererAuthoringOperationsTests
{
    public static void Run()
    {
        CheckForwardProjection();
        CheckOrbitPreservesFocusAndClamps();
        CheckFocusActionAndUndo();
        CheckOrbitActionAndUndo();
        CheckReferenceCameraTargetAndUndo();
        Debug.Log("Quilt renderer authoring operation tests passed");
    }

    static void CheckForwardProjection()
    {
        Vector3 origin = new Vector3(2f, -1f, 4f);
        Quaternion rotation = Quaternion.Euler(14f, 37f, -8f);
        Vector3 forward = rotation * Vector3.forward;
        Vector3 right = rotation * Vector3.right;
        Vector3 point = origin + forward * 5.25f + right * 3f;
        Require(QuiltRendererAuthoringOperations.TryProjectForwardDistance(origin, forward, point, out float distance), "front point projects onto camera axis");
        Require(Mathf.Abs(distance - 5.25f) < 0.0001f, "lateral offset does not change projected distance");
        Require(!QuiltRendererAuthoringOperations.TryProjectForwardDistance(origin, forward, origin - forward, out _), "point behind camera is rejected");
        Require(!QuiltRendererAuthoringOperations.TryProjectForwardDistance(origin, Vector3.zero, point, out _), "zero forward vector is rejected");
        Require(!QuiltRendererAuthoringOperations.TryProjectForwardDistance(origin, forward, new Vector3(float.NaN, 0f, 0f), out _), "non-finite point is rejected");
    }

    static void CheckOrbitPreservesFocusAndClamps()
    {
        Vector3 position = new Vector3(-3f, 2f, 8f);
        Quaternion rotation = Quaternion.Euler(7f, 21f, -4f);
        float distance = 6f;
        Vector3 focus = position + rotation * Vector3.forward * distance;
        Require(QuiltRendererAuthoringOperations.TryCalculateOrbitPose(position, rotation, distance, 32f, -17f, out Vector3 movedPosition, out Quaternion movedRotation), "orbit pose is calculated");
        Vector3 movedFocus = movedPosition + movedRotation * Vector3.forward * distance;
        Require(Vector3.Distance(focus, movedFocus) < 0.0002f, "orbit retains world focus point");
        Require(QuiltRendererAuthoringOperations.TryCalculateOrbitPose(position, rotation, distance, 900f, 140f, out Vector3 clampedPosition, out Quaternion clampedRotation), "out-of-range orbit deltas are clamped");
        Require(IsFinite(clampedPosition) && IsFinite(clampedRotation), "clamped orbit pose is finite");
        Require(QuiltRendererAuthoringOperations.TryCalculateOrbitPose(position, rotation, distance, 180f, 89f, out Vector3 expectedClampPosition, out Quaternion expectedClampRotation), "maximum orbit values are calculated");
        Require(Vector3.Distance(clampedPosition, expectedClampPosition) < 0.0001f && Quaternion.Angle(clampedRotation, expectedClampRotation) < 0.001f,
            "horizontal and vertical orbit angles clamp to documented limits");
        Require(!QuiltRendererAuthoringOperations.TryCalculateOrbitPose(position, rotation, float.PositiveInfinity, 1f, 1f, out _, out _), "invalid focus distance is rejected");
        Require(!QuiltRendererAuthoringOperations.TryCalculateOrbitPose(position, rotation, distance, float.NaN, 0f, out _, out _), "invalid orbit angle is rejected");
    }

    static void CheckFocusActionAndUndo()
    {
        GameObject go = new GameObject("authoring-focus-action-test");
        int initialGroup = Undo.GetCurrentGroup();
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.cameraDistance = 2f;
            Vector3 originalPosition = camera.transform.position;
            Quaternion originalRotation = camera.transform.rotation;
            Vector3 target = originalPosition + originalRotation * Vector3.forward * 7f + originalRotation * Vector3.right * 4f;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Focus Authoring Test");
            Require(QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, target, out float appliedDistance), "focus action accepts a front scene point");
            Undo.FlushUndoRecordObjects();
            Require(Mathf.Abs(appliedDistance - 7f) < 0.0001f, "focus action uses forward projection");
            Require(Mathf.Abs(renderer.settings.cameraDistance - 7f) < 0.0001f, "focus action updates zero-parallax distance");
            Require(camera.transform.position == originalPosition && camera.transform.rotation == originalRotation, "focus action leaves camera pose unchanged");
            Require(!QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, originalPosition - originalRotation * Vector3.forward, out _), "focus action rejects a point behind camera");
            Undo.PerformUndo();
            Require(Mathf.Abs(renderer.settings.cameraDistance - 2f) < 0.0001f, "focus action undo restores prior distance");
        }
        finally
        {
            Undo.RevertAllDownToGroup(initialGroup);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static void CheckOrbitActionAndUndo()
    {
        GameObject go = new GameObject("authoring-orbit-action-test");
        int initialGroup = Undo.GetCurrentGroup();
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.cameraDistance = 4f;
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            Vector3 focus = position + rotation * Vector3.forward * renderer.settings.cameraDistance;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Orbit Authoring Test");
            Require(QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, 24f, -12f), "orbit action updates effective camera");
            Undo.FlushUndoRecordObjects();
            Require(Vector3.Distance(focus, camera.transform.position + camera.transform.rotation * Vector3.forward * renderer.settings.cameraDistance) < 0.0002f,
                "orbit action preserves focus point in world space");
            Require(camera.transform.position != position || camera.transform.rotation != rotation, "orbit action changes camera position and rotation");
            Undo.PerformUndo();
            Require(camera.transform.position == position && camera.transform.rotation == rotation, "orbit action undo restores camera transform");
        }
        finally
        {
            Undo.RevertAllDownToGroup(initialGroup);
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    static void CheckReferenceCameraTargetAndUndo()
    {
        GameObject rig = new GameObject("authoring-reference-rig-test");
        GameObject referenceObject = new GameObject("authoring-reference-camera-test");
        int initialGroup = Undo.GetCurrentGroup();
        try
        {
            Camera sourceCamera = rig.AddComponent<Camera>();
            sourceCamera.enabled = false;
            QuiltRenderer renderer = rig.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = sourceCamera;
            renderer.settings.cameraDistance = 5f;
            Camera referenceCamera = referenceObject.AddComponent<Camera>();
            renderer.referenceCamera = referenceCamera;
            Vector3 sourcePosition = sourceCamera.transform.position;
            Quaternion sourceRotation = sourceCamera.transform.rotation;
            Vector3 referencePosition = referenceCamera.transform.position;
            Quaternion referenceRotation = referenceCamera.transform.rotation;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Reference Camera Orbit Test");
            Require(QuiltRendererAuthoringOperations.TryOrbitEffectiveCamera(renderer, 18f, 0f), "orbit action accepts a valid reference camera");
            Undo.FlushUndoRecordObjects();
            Require(referenceCamera.transform.position != referencePosition || referenceCamera.transform.rotation != referenceRotation,
                "orbit action edits the displayed effective reference camera");
            Require(sourceCamera.transform.position == sourcePosition && sourceCamera.transform.rotation == sourceRotation,
                "orbit action leaves the dedicated source camera transform unchanged");
            Undo.PerformUndo();
            Require(referenceCamera.transform.position == referencePosition && referenceCamera.transform.rotation == referenceRotation,
                "reference camera orbit undo restores the transform");
        }
        finally
        {
            Undo.RevertAllDownToGroup(initialGroup);
            UnityEngine.Object.DestroyImmediate(rig);
            UnityEngine.Object.DestroyImmediate(referenceObject);
        }
    }

    static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
