using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ViewWeave.Editor
{
    /// <summary>Optional Core-to-Bridge Editor entry point for opening the integrated Rig authoring window.</summary>
    public static class ViewWeaveRigAuthoringLauncher
    {
        static Action<ViewWeaveRig> openHandler;

        public static bool IsRegistered => openHandler != null;

        public static void Register(Action<ViewWeaveRig> handler) => openHandler = handler;

        public static bool Open(ViewWeaveRig rig)
        {
            if (rig == null || openHandler == null) return false;
            openHandler(rig);
            return true;
        }
    }

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
            if (renderer.physicalFraming != null && renderer.physicalFraming.enabled)
                return TrySetEffectiveFocusDistance(renderer, distance);
            if (Mathf.Abs(renderer.settings.cameraDistance - distance) < 0.000001f)
                return true;

            Undo.RecordObject(renderer, "Set ViewWeave Focus From Selection");
            renderer.settings.cameraDistance = distance;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            return true;
        }

        public static bool TrySetEffectiveFocusDistance(QuiltRenderer renderer, float distance)
        {
            if (renderer == null || renderer.settings == null || renderer.physicalFraming == null || !renderer.physicalFraming.enabled ||
                !TryGetEditableFramingCamera(renderer, out _) || !IsFinite(distance) || distance <= MinimumFocusDistance ||
                !renderer.physicalFraming.TryResolve(60f, out _, out _, out _))
                return false;
            float apertureHeight = renderer.physicalFraming.ApertureHeightInUnityUnits;
            float fieldOfView = 2f * Mathf.Atan(apertureHeight / (2f * distance)) * Mathf.Rad2Deg;
            if (!IsFinite(fieldOfView) || fieldOfView <= 0.01f || fieldOfView >= 178f) return false;
            Camera fieldOfViewCamera = renderer.referenceCamera != null && renderer.followReferenceFieldOfView ? renderer.referenceCamera : null;
            if (fieldOfViewCamera != null)
            {
                if (Mathf.Abs(fieldOfViewCamera.fieldOfView - fieldOfView) < 0.00001f) return true;
                Undo.RecordObject(fieldOfViewCamera, "Set ViewWeave Physical Focus");
                fieldOfViewCamera.fieldOfView = fieldOfView;
                EditorUtility.SetDirty(fieldOfViewCamera);
                PrefabUtility.RecordPrefabInstancePropertyModifications(fieldOfViewCamera);
                return true;
            }
            if (Mathf.Abs(renderer.settings.fieldOfView - fieldOfView) < 0.00001f) return true;
            Undo.RecordObject(renderer, "Set ViewWeave Physical Focus");
            renderer.settings.fieldOfView = fieldOfView;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            return true;
        }

        public static bool TryOrbitEffectiveCamera(QuiltRenderer renderer, float horizontalDegrees, float verticalDegrees)
        {
            if (!TryGetEditableFramingCamera(renderer, out Camera framingCamera))
                return false;
            Transform framingTransform = framingCamera.transform;
            float focusDistance = GetEffectiveFocusDistance(renderer);
            if (!TryCalculateOrbitPose(framingTransform.position, framingTransform.rotation, focusDistance,
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
            focusPoint = transform.position + transform.rotation * Vector3.forward * GetEffectiveFocusDistance(renderer);
            return IsFinite(focusPoint);
        }

        public static bool TryGetSceneObjectCenter(QuiltRenderer renderer, UnityEngine.Object target, out Vector3 center, out string reason)
        {
            center = Vector3.zero;
            reason = "Select one scene object before setting the focus distance.";
            GameObject selected = target as GameObject;
            if (selected == null && target is Component component) selected = component.gameObject;
            if (selected == null) return false;
            if (EditorUtility.IsPersistent(selected) || !selected.scene.IsValid() || !selected.scene.isLoaded)
            {
                reason = "The selected object must belong to a loaded scene.";
                return false;
            }
            if (renderer != null)
            {
                GameObject rigObject = renderer.gameObject;
                if (EditorUtility.IsPersistent(rigObject) || !rigObject.scene.IsValid() || !rigObject.scene.isLoaded)
                {
                    reason = "The ViewWeave Rig must belong to a loaded Scene or Prefab Stage.";
                    return false;
                }
                bool rigIsPreviewObject = EditorSceneManager.IsPreviewSceneObject(rigObject);
                bool targetIsPreviewObject = EditorSceneManager.IsPreviewSceneObject(selected);
                bool sameStage;
                if (rigIsPreviewObject || targetIsPreviewObject)
                {
                    PrefabStage rigPrefabStage = PrefabStageUtility.GetPrefabStage(rigObject);
                    PrefabStage targetPrefabStage = PrefabStageUtility.GetPrefabStage(selected);
                    sameStage = rigIsPreviewObject && targetIsPreviewObject && rigObject.scene == selected.scene &&
                                rigPrefabStage != null && rigPrefabStage == targetPrefabStage;
                }
                else
                {
                    sameStage = StageUtility.GetStageHandle(rigObject) == StageUtility.GetStageHandle(selected);
                }
                if (!sameStage)
                {
                    reason = "The object must belong to the loaded Main Stage or the same Prefab Stage as the ViewWeave Rig. Isolated Preview Scenes are not supported.";
                    return false;
                }
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

        static bool TryGetEditableFramingCamera(QuiltRenderer renderer, out Camera framingCamera)
        {
            framingCamera = null;
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || renderer == null || renderer.settings == null || renderer.sourceCamera == null)
                return false;
            if (EditorUtility.IsPersistent(renderer.gameObject) ||
                !TryGetSceneObjectCenter(renderer, renderer.sourceCamera, out _, out _))
                return false;
            if (renderer.referenceCamera != null &&
                !TryGetSceneObjectCenter(renderer, renderer.referenceCamera, out _, out _))
                return false;
            framingCamera = renderer.EffectiveCamera;
            if (framingCamera == null || renderer.sourceCamera.orthographic || framingCamera.orthographic ||
                (renderer.referenceCamera != null && (renderer.referenceCamera == renderer.sourceCamera || renderer.referenceCamera.transform.IsChildOf(renderer.sourceCamera.transform))) ||
                !renderer.TryGetEffectiveSettings(out QuiltSettings effectiveSettings, out _) ||
                !IsFinite(effectiveSettings.cameraDistance) || effectiveSettings.cameraDistance <= MinimumFocusDistance)
                return false;
            Transform transform = framingCamera.transform;
            Quaternion rotation = transform.rotation;
            return IsFinite(transform.position) && IsFinite(rotation) &&
                   rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w >= 0.000001f;
        }

        public static float GetEffectiveFocusDistance(QuiltRenderer renderer)
        {
            return renderer != null && renderer.TryGetEffectiveSettings(out QuiltSettings effective, out _) ? effective.cameraDistance : 0f;
        }

        static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        static bool IsFinite(Quaternion value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
