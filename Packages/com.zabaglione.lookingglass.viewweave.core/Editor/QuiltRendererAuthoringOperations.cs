using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    /// <summary>Editor-only math and explicit Undo-backed operations for camera framing authoring.</summary>
    public static class QuiltRendererAuthoringOperations
    {
        public const float MinimumFocusDistance = 0.001f;
        public const float MaximumHorizontalOrbitDegrees = 180f;
        public const float MaximumVerticalOrbitDegrees = 89f;

        public static bool TryProjectForwardDistance(Vector3 origin, Vector3 forward, Vector3 worldPoint, out float distance)
        {
            distance = 0f;
            if (!IsFinite(origin) || !IsFinite(forward) || !IsFinite(worldPoint) || forward.sqrMagnitude < 0.000001f)
                return false;

            forward.Normalize();
            distance = Vector3.Dot(worldPoint - origin, forward);
            return IsFinite(distance) && distance > MinimumFocusDistance;
        }

        public static bool TryCalculateOrbitPose(Vector3 position, Quaternion rotation, float focusDistance, float horizontalDegrees, float verticalDegrees,
            out Vector3 resultPosition, out Quaternion resultRotation)
        {
            resultPosition = position;
            resultRotation = rotation;
            if (!IsFinite(position) || !IsFinite(rotation) || !IsFinite(focusDistance) || focusDistance <= MinimumFocusDistance ||
                !IsFinite(horizontalDegrees) || !IsFinite(verticalDegrees) || rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w < 0.000001f)
                return false;

            horizontalDegrees = Mathf.Clamp(horizontalDegrees, -MaximumHorizontalOrbitDegrees, MaximumHorizontalOrbitDegrees);
            verticalDegrees = Mathf.Clamp(verticalDegrees, -MaximumVerticalOrbitDegrees, MaximumVerticalOrbitDegrees);
            Quaternion yaw = Quaternion.AngleAxis(horizontalDegrees, Vector3.up);
            Vector3 pitchAxis = yaw * (rotation * Vector3.right);
            Quaternion pitch = Quaternion.AngleAxis(verticalDegrees, pitchAxis);
            Quaternion delta = pitch * yaw;
            Vector3 focusPoint = position + rotation * Vector3.forward * focusDistance;
            resultPosition = focusPoint + delta * (position - focusPoint);
            resultRotation = delta * rotation;
            return IsFinite(resultPosition) && IsFinite(resultRotation);
        }

        public static bool TrySetFocusFromWorldPoint(QuiltRenderer renderer, Vector3 worldPoint, out float distance)
        {
            distance = 0f;
            if (!TryGetEditableFramingCamera(renderer, out Camera framingCamera))
                return false;
            Transform framingTransform = framingCamera.transform;
            if (!TryProjectForwardDistance(framingTransform.position, framingTransform.rotation * Vector3.forward, worldPoint, out distance))
                return false;
            if (Mathf.Abs(renderer.settings.cameraDistance - distance) < 0.000001f)
                return true;

            Undo.RecordObject(renderer, "Set ViewWeave Focus From Selection");
            renderer.settings.cameraDistance = distance;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            return true;
        }

        public static bool TryOrbitEffectiveCamera(QuiltRenderer renderer, float horizontalDegrees, float verticalDegrees)
        {
            if (!TryGetEditableFramingCamera(renderer, out Camera framingCamera))
                return false;
            Transform framingTransform = framingCamera.transform;
            if (!TryCalculateOrbitPose(framingTransform.position, framingTransform.rotation, renderer.settings.cameraDistance,
                    horizontalDegrees, verticalDegrees, out Vector3 position, out Quaternion rotation))
                return false;
            if (position == framingTransform.position && rotation == framingTransform.rotation)
                return true;

            Undo.RecordObject(framingTransform, "Orbit ViewWeave Framing Camera");
            framingTransform.SetPositionAndRotation(position, rotation);
            EditorUtility.SetDirty(framingTransform);
            PrefabUtility.RecordPrefabInstancePropertyModifications(framingTransform);
            return true;
        }

        public static bool TryGetFocusPoint(QuiltRenderer renderer, out Camera framingCamera, out Vector3 focusPoint)
        {
            framingCamera = null;
            focusPoint = Vector3.zero;
            if (!TryGetEditableFramingCamera(renderer, out framingCamera))
                return false;
            Transform transform = framingCamera.transform;
            focusPoint = transform.position + transform.rotation * Vector3.forward * renderer.settings.cameraDistance;
            return IsFinite(focusPoint);
        }

        static bool TryGetEditableFramingCamera(QuiltRenderer renderer, out Camera framingCamera)
        {
            framingCamera = null;
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || renderer == null || renderer.settings == null || renderer.sourceCamera == null)
                return false;
            framingCamera = renderer.EffectiveCamera;
            if (framingCamera == null || renderer.sourceCamera.orthographic || framingCamera.orthographic ||
                (renderer.referenceCamera != null && (renderer.referenceCamera == renderer.sourceCamera || renderer.referenceCamera.transform.IsChildOf(renderer.sourceCamera.transform))) ||
                !IsFinite(renderer.settings.cameraDistance) || renderer.settings.cameraDistance <= MinimumFocusDistance)
                return false;
            Transform transform = framingCamera.transform;
            Quaternion rotation = transform.rotation;
            return IsFinite(transform.position) && IsFinite(rotation) &&
                   rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w >= 0.000001f;
        }

        static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
