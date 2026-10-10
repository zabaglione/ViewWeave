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
        CheckFitCalculationsContainBounds();
        CheckFitApplicationAndUndo();
        CheckPhysicalFocusMathRemainsCompatible();
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
        GameObject referenceObject = new GameObject("authoring-focus-reference-test");
        int initialGroup = Undo.GetCurrentGroup();
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.cameraDistance = 2f;
            Camera referenceCamera = referenceObject.AddComponent<Camera>();
            referenceCamera.enabled = false;
            referenceCamera.fieldOfView = 38f;
            renderer.referenceCamera = referenceCamera;
            renderer.followReferenceFieldOfView = false;
            Vector3 originalPosition = referenceCamera.transform.position;
            Quaternion originalRotation = referenceCamera.transform.rotation;
            Vector3 target = originalPosition + originalRotation * Vector3.forward * 7f + originalRotation * Vector3.right * 4f;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Focus Authoring Test");
            Require(QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, target, out float appliedDistance), "focus action accepts a front scene point");
            Undo.FlushUndoRecordObjects();
            Require(Mathf.Abs(appliedDistance - 7f) < 0.0001f, "focus action uses forward projection");
            Require(Mathf.Abs(renderer.settings.cameraDistance - 7f) < 0.0001f, "focus action updates zero-parallax distance");
            Require(camera.transform.position == originalPosition && camera.transform.rotation == originalRotation, "focus action leaves camera pose unchanged");
            Require(Mathf.Abs(referenceCamera.fieldOfView - 38f) < 0.0001f, "Focus with Reference Camera FOV following off leaves its FOV unchanged");
            Require(!QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, originalPosition - originalRotation * Vector3.forward, out _), "focus action rejects a point behind camera");
            Undo.PerformUndo();
            Require(Mathf.Abs(renderer.settings.cameraDistance - 2f) < 0.0001f, "focus action undo restores prior distance");
        }
        finally
        {
            Undo.RevertAllDownToGroup(initialGroup);
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(referenceObject);
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

    static void CheckFitCalculationsContainBounds()
    {
        Vector3 cameraPosition = new Vector3(3f, -2f, 5f);
        Quaternion cameraRotation = Quaternion.Euler(19f, 41f, -7f);
        Bounds bounds = new Bounds(cameraPosition + cameraRotation * new Vector3(0.8f, -0.35f, 8f), new Vector3(2.8f, 1.4f, 2f));
        const float aspect = 1.37f;
        Require(QuiltRendererAuthoringOperations.TryCalculateFitToBounds(cameraPosition, cameraRotation, bounds,
            aspect, 0.3f, 100f, out float fieldOfView, out float focusDistance, out string reason), "rotated camera and non-square aspect fit target bounds: " + reason);
        Require(focusDistance > 0f && fieldOfView > 0f && fieldOfView < 179f, "fit returns valid focus and field of view");

        Quaternion inverse = Quaternion.Inverse(cameraRotation);
        float tangent = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
        Vector3 extents = bounds.extents;
        for (int corner = 0; corner < 8; ++corner)
        {
            Vector3 worldCorner = bounds.center + new Vector3((corner & 1) == 0 ? -extents.x : extents.x,
                (corner & 2) == 0 ? -extents.y : extents.y, (corner & 4) == 0 ? -extents.z : extents.z);
            Vector3 localCorner = inverse * (worldCorner - cameraPosition);
            Require(Mathf.Abs(localCorner.y) / localCorner.z <= tangent + 0.0001f, "fit contains every corner vertically with margin");
            Require(Mathf.Abs(localCorner.x) / (localCorner.z * aspect) <= tangent + 0.0001f, "fit contains every corner horizontally with margin");
        }

        Require(!QuiltRendererAuthoringOperations.TryCalculateFitToBounds(cameraPosition, cameraRotation,
            new Bounds(cameraPosition, Vector3.one), aspect, 0.3f, 100f, out _, out _, out _), "bounds crossing the near clip are rejected");
        Require(!QuiltRendererAuthoringOperations.TryCalculateFitToBounds(cameraPosition, cameraRotation,
            new Bounds(cameraPosition + cameraRotation * Vector3.forward * 100f, Vector3.one), aspect, 0.3f, 100f, out _, out _, out _), "bounds crossing the far clip are rejected");
        Require(!QuiltRendererAuthoringOperations.TryCalculateFitToBounds(cameraPosition, cameraRotation,
            new Bounds(cameraPosition - cameraRotation * Vector3.forward * 4f, Vector3.one), aspect, 0.3f, 100f, out _, out _, out _), "bounds behind the camera are rejected");
        Require(!QuiltRendererAuthoringOperations.TryCalculateFitToBounds(cameraPosition, cameraRotation,
            bounds, 0f, 0.3f, 100f, out _, out _, out _), "invalid aspect is rejected");
    }

    static void CheckFitApplicationAndUndo()
    {
        GameObject rig = new GameObject("authoring-fit-rig-test");
        GameObject target = new GameObject("authoring-fit-target-test");
        GameObject scaledCameraParent = new GameObject("authoring-fit-scaled-parent-test");
        GameObject referenceObject = null;
        int initialGroup = Undo.GetCurrentGroup();
        try
        {
            Camera sourceCamera = rig.AddComponent<Camera>();
            sourceCamera.enabled = false;
            sourceCamera.nearClipPlane = 0.3f;
            sourceCamera.farClipPlane = 50f;
            QuiltRenderer renderer = rig.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = sourceCamera;
            renderer.settings.fieldOfView = 45f;
            renderer.settings.cameraDistance = 3f;
            renderer.settings.viewAspect = 1.5f;
            target.transform.position = new Vector3(1.2f, 0.5f, 8f);
            BoxCollider collider = target.AddComponent<BoxCollider>();
            collider.size = new Vector3(2f, 1.5f, 2f);
            referenceObject = new GameObject("authoring-fit-reference-test");
            Camera referenceCamera = referenceObject.AddComponent<Camera>();
            referenceCamera.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
            referenceCamera.fieldOfView = 38f;
            renderer.referenceCamera = referenceCamera;
            renderer.followReferenceFieldOfView = false;
            Vector3 cameraPosition = referenceCamera.transform.position;
            Quaternion cameraRotation = referenceCamera.transform.rotation;
            Require(QuiltRendererAuthoringOperations.TryFitViewToTarget(renderer, target,
                out float unparentedFov, out float unparentedDistance, out string reason), "unparented target fit calculates: " + reason);
            scaledCameraParent.transform.localScale = new Vector3(2.5f, 0.5f, 1.75f);
            referenceCamera.transform.SetParent(scaledCameraParent.transform, true);
            referenceCamera.transform.localScale = Vector3.one;
            Require(referenceCamera.transform.position == cameraPosition && referenceCamera.transform.rotation == cameraRotation,
                "changing camera parent scale leaves camera world position and rotation unchanged");
            Require(QuiltRendererAuthoringOperations.TryFitViewToTarget(renderer, target,
                out float scaledParentFov, out float scaledParentDistance, out reason), "scaled-parent target fit calculates: " + reason);
            Require(Mathf.Abs(unparentedFov - scaledParentFov) < 0.0001f && Mathf.Abs(unparentedDistance - scaledParentDistance) < 0.0001f,
                "camera parent scale does not change fit results for the same world pose");
            Vector3 targetScale = target.transform.lossyScale;
            Vector3 parentScale = scaledCameraParent.transform.localScale;

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Fit View Authoring Test");
            Require(QuiltRendererAuthoringOperations.TryApplyFitViewToTarget(renderer, target,
                out float previousFov, out float fittedFov, out float focusDistance, out reason), "fit operation applies a valid target: " + reason);
            Undo.FlushUndoRecordObjects();
            Require(Mathf.Abs(previousFov - 45f) < 0.0001f && Mathf.Abs(renderer.settings.fieldOfView - fittedFov) < 0.0001f,
                "fit applies calculated FOV to the renderer when reference following is off");
            Require(Mathf.Abs(renderer.settings.cameraDistance - focusDistance) < 0.0001f, "fit updates focus distance");
            Require(referenceCamera.transform.position == cameraPosition && referenceCamera.transform.rotation == cameraRotation,
                "fit keeps camera pose unchanged");
            Require(scaledCameraParent.transform.localScale == parentScale, "fit keeps a scaled camera parent unchanged");
            Require(target.transform.lossyScale == targetScale, "fit keeps target scale unchanged");
            Undo.PerformUndo();
            Require(Mathf.Abs(renderer.settings.fieldOfView - 45f) < 0.0001f && Mathf.Abs(renderer.settings.cameraDistance - 3f) < 0.0001f,
                "fit undo restores renderer framing");
            Require(Mathf.Abs(referenceCamera.fieldOfView - 38f) < 0.0001f,
                "with reference FOV following off, fit leaves Reference Camera FOV unchanged");
            renderer.followReferenceFieldOfView = true;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Fit Reference Camera Test");
            Require(QuiltRendererAuthoringOperations.TryApplyFitViewToTarget(renderer, target,
                out _, out float referenceFov, out _, out reason), "fit applies with followed reference camera: " + reason);
            Undo.FlushUndoRecordObjects();
            Require(Mathf.Abs(referenceCamera.fieldOfView - referenceFov) < 0.0001f, "fit writes the followed reference Camera FOV");
            Undo.PerformUndo();
            Require(Mathf.Abs(referenceCamera.fieldOfView - 38f) < 0.0001f, "fit undo restores followed reference Camera FOV");

            GameObject emptyTarget = new GameObject("authoring-fit-empty-target-test");
            float unchangedFov = referenceCamera.fieldOfView;
            float unchangedDistance = renderer.settings.cameraDistance;
            Require(!QuiltRendererAuthoringOperations.TryApplyFitViewToTarget(renderer, emptyTarget,
                out _, out _, out _, out _), "fit rejects a target without enabled Renderer or Collider bounds");
            Require(referenceCamera.fieldOfView == unchangedFov && renderer.settings.cameraDistance == unchangedDistance,
                "empty target rejection leaves framing unchanged");
            UnityEngine.Object.DestroyImmediate(emptyTarget);

            Require(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback,
                out QuiltRendererOperationLease playbackLease, out string leaseError), "fit guard test acquires playback lease: " + leaseError);
            try
            {
                unchangedFov = referenceCamera.fieldOfView;
                unchangedDistance = renderer.settings.cameraDistance;
                Require(!QuiltRendererAuthoringOperations.TryApplyFitViewToTarget(renderer, target,
                    out _, out _, out _, out _), "active owner policy can deny Fit View To Target");
                Require(referenceCamera.fieldOfView == unchangedFov && renderer.settings.cameraDistance == unchangedDistance,
                    "owner denial leaves renderer and reference camera framing unchanged");
            }
            finally
            {
                playbackLease.Dispose();
            }

            renderer.physicalFraming = new QuiltPhysicalFraming
            {
                enabled = true,
                visibleApertureWidthMillimeters = 344.65018f,
                visibleApertureHeightMillimeters = 193.86572f,
                unitsPerMeter = 1f
            };
            float storedDistance = renderer.settings.cameraDistance;
            float storedFov = referenceCamera.fieldOfView;
            Require(!QuiltRendererAuthoringOperations.TryApplyFitViewToTarget(renderer, target,
                out _, out _, out _, out _), "fit is rejected while physical framing is enabled");
            Require(renderer.settings.cameraDistance == storedDistance && referenceCamera.fieldOfView == storedFov && renderer.physicalFraming.enabled,
                "physical guard rejection leaves framing unchanged");
        }
        finally
        {
            Undo.RevertAllDownToGroup(initialGroup);
            UnityEngine.Object.DestroyImmediate(rig);
            UnityEngine.Object.DestroyImmediate(target);
            if (referenceObject != null) UnityEngine.Object.DestroyImmediate(referenceObject);
            UnityEngine.Object.DestroyImmediate(scaledCameraParent);
        }
    }

    static void CheckPhysicalFocusMathRemainsCompatible()
    {
        GameObject go = new GameObject("authoring-physical-focus-math-test");
        try
        {
            Camera camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(new Vector3(-0.19f, 0.65330756f, 4.4257894f), Quaternion.Euler(0f, 180f, 0f));
            QuiltRenderer renderer = go.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.fieldOfView = 26.991467f;
            renderer.settings.cameraDistance = 1f;
            renderer.physicalFraming = new QuiltPhysicalFraming
            {
                enabled = true,
                visibleApertureWidthMillimeters = 344.65018f,
                visibleApertureHeightMillimeters = 193.86572f,
                unitsPerMeter = 1f
            };
            Vector3 target = new Vector3(0f, 0f, 0f);
            Require(QuiltRendererAuthoringOperations.TrySetFocusFromWorldPoint(renderer, target, out float distance),
                "physical focus accepts target center with the existing calibration");
            Require(Mathf.Abs(distance - 4.4257894f) < 0.0002f, "physical focus keeps forward-projected distance math");
            Require(Mathf.Abs(renderer.settings.fieldOfView - 2.509f) < 0.002f,
                "physical focus solves the existing 26.991467-to-2.509 degree aperture calculation");
        }
        finally
        {
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
