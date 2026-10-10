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
            return TrySetEffectiveFocusDistance(renderer, distance);
        }

        public static bool TryCalculateFitToBounds(Vector3 cameraPosition, Quaternion cameraRotation, Bounds worldBounds,
            float aspect, float nearClip, float farClip, out float verticalFieldOfView, out float focusDistance, out string reason)
        {
            verticalFieldOfView = 0f;
            focusDistance = 0f;
            reason = "The target bounds cannot be fitted to this camera.";
            float rotationMagnitude = cameraRotation.x * cameraRotation.x + cameraRotation.y * cameraRotation.y +
                                      cameraRotation.z * cameraRotation.z + cameraRotation.w * cameraRotation.w;
            if (!IsFinite(cameraPosition) || !IsFinite(cameraRotation) || !IsFinite(rotationMagnitude) || rotationMagnitude < 0.000001f ||
                !IsFinite(worldBounds.center) || !IsFinite(worldBounds.extents) || worldBounds.extents.x < 0f || worldBounds.extents.y < 0f || worldBounds.extents.z < 0f ||
                !IsFinite(aspect) || aspect <= 0f || !IsFinite(nearClip) || !IsFinite(farClip) || nearClip <= 0f || farClip <= nearClip)
            {
                reason = "Camera framing, target bounds, aspect, or clip planes are invalid.";
                return false;
            }

            Quaternion inverseRotation = Quaternion.Inverse(cameraRotation);
            Vector3 extents = worldBounds.extents;
            float maxTangent = 0f;
            for (int corner = 0; corner < 8; ++corner)
            {
                Vector3 worldCorner = worldBounds.center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                Vector3 localCorner = inverseRotation * (worldCorner - cameraPosition);
                if (!IsFinite(localCorner) || localCorner.z <= nearClip || localCorner.z >= farClip)
                {
                    reason = "Every target bounds corner must be in front of and between the source Camera clip planes.";
                    return false;
                }

                float tangent = Mathf.Max(Mathf.Abs(localCorner.y) / localCorner.z,
                    Mathf.Abs(localCorner.x) / (localCorner.z * aspect));
                if (!IsFinite(tangent))
                {
                    reason = "The target bounds produce an invalid field of view.";
                    return false;
                }
                maxTangent = Mathf.Max(maxTangent, tangent);
            }

            focusDistance = Vector3.Dot(worldBounds.center - cameraPosition, cameraRotation * Vector3.forward);
            verticalFieldOfView = 2f * Mathf.Atan(1.1f * maxTangent) * Mathf.Rad2Deg;
            if (!IsFinite(focusDistance) || focusDistance <= MinimumFocusDistance || !IsFinite(verticalFieldOfView) ||
                verticalFieldOfView <= 0f || verticalFieldOfView >= 179f)
            {
                verticalFieldOfView = 0f;
                focusDistance = 0f;
                reason = "The target bounds do not produce a valid focus distance and field of view.";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        public static bool TryFitViewToTarget(QuiltRenderer renderer, UnityEngine.Object target,
            out float verticalFieldOfView, out float focusDistance, out string reason)
        {
            verticalFieldOfView = 0f;
            focusDistance = 0f;
            if (renderer == null || renderer.physicalFraming != null && renderer.physicalFraming.enabled)
            {
                reason = "Turn off Use Physical 1:1 Framing before fitting the view to a target.";
                return false;
            }
            if (!TryGetSceneObjectBounds(renderer, target, out Bounds bounds, out bool hasBounds, out reason)) return false;
            if (!hasBounds)
            {
                reason = "The target needs an enabled Renderer or Collider in its hierarchy to fit the view.";
                return false;
            }
            if (!TryGetEditableFramingCamera(renderer, out Camera framingCamera))
            {
                reason = "The effective framing Camera is unavailable or invalid.";
                return false;
            }
            if (framingCamera.orthographic || renderer.sourceCamera.orthographic)
            {
                reason = "Fit View To Target requires a perspective Camera.";
                return false;
            }
            if (!QuiltDisplaySettingsResolver.TryResolve(renderer, out QuiltSettings effectiveSettings, out _, out string displayError))
            {
                reason = "The effective display settings are invalid: " + displayError;
                return false;
            }
            return TryCalculateFitToBounds(framingCamera.transform.position, framingCamera.transform.rotation, bounds,
                effectiveSettings.viewAspect, renderer.sourceCamera.nearClipPlane, renderer.sourceCamera.farClipPlane,
                out verticalFieldOfView, out focusDistance, out reason);
        }

        public static bool TryApplyFitViewToTarget(QuiltRenderer renderer, UnityEngine.Object target,
            out float previousFieldOfView, out float verticalFieldOfView, out float focusDistance, out string reason)
        {
            previousFieldOfView = 0f;
            verticalFieldOfView = 0f;
            focusDistance = 0f;
            if (!TryFitViewToTarget(renderer, target, out verticalFieldOfView, out focusDistance, out reason)) return false;
            if (!renderer.TryGetFraming(out QuiltCameraFraming current))
            {
                reason = "The current camera framing is unavailable.";
                return false;
            }
            previousFieldOfView = current.VerticalFieldOfView;
            QuiltCameraFraming candidate = new QuiltCameraFraming(verticalFieldOfView, focusDistance, false,
                current.ApertureWidthMillimeters, current.ApertureHeightMillimeters, current.UnitsPerMeter);
            if (!renderer.TryCheckMutation(new QuiltRendererMutationRequest(candidate), out reason) ||
                !renderer.TryValidateFraming(candidate, out reason)) return false;

            Camera reference = renderer.referenceCamera != null && renderer.followReferenceFieldOfView ? renderer.referenceCamera : null;
            const string undoName = "Fit ViewWeave Camera To Target";
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(undoName);
            Undo.RecordObject(renderer, undoName);
            if (reference != null) Undo.RecordObject(reference, undoName);
            if (!renderer.TrySetFraming(candidate, out reason)) return false;
            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            if (reference != null)
            {
                EditorUtility.SetDirty(reference);
                PrefabUtility.RecordPrefabInstancePropertyModifications(reference);
            }
            reason = string.Empty;
            return true;
        }

        public static bool TrySetEffectiveFocusDistance(QuiltRenderer renderer, float distance)
        {
            if (renderer == null || !IsFinite(distance) || distance <= MinimumFocusDistance ||
                !TryGetEditableFramingCamera(renderer, out _)) return false;
            if (!renderer.TryGetFraming(out QuiltCameraFraming current) ||
                !renderer.TryValidateFocusDistance(distance, out _)) return false;
            bool physical = current.PhysicalFramingEnabled;
            Camera fieldOfViewCamera = physical && renderer.referenceCamera != null && renderer.followReferenceFieldOfView
                ? renderer.referenceCamera
                : null;
            float previousDistance = physical && renderer.TryGetEffectiveSettings(out QuiltSettings currentEffective, out _)
                ? currentEffective.cameraDistance
                : current.FocusDistance;
            if (Mathf.Abs(previousDistance - distance) < 0.000001f) return true;

            if (fieldOfViewCamera != null)
                Undo.RecordObject(fieldOfViewCamera, "Set ViewWeave Physical Focus");
            else
                Undo.RecordObject(renderer, "Set ViewWeave Focus Distance");
            if (!renderer.TrySetFocusDistance(distance, out _)) return false;
            if (fieldOfViewCamera != null)
            {
                EditorUtility.SetDirty(fieldOfViewCamera);
                PrefabUtility.RecordPrefabInstancePropertyModifications(fieldOfViewCamera);
            }
            else
            {
                EditorUtility.SetDirty(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
            }
            return true;
        }

        public static bool TryOrbitEffectiveCamera(QuiltRenderer renderer, float horizontalDegrees, float verticalDegrees)
        {
            if (renderer == null || !renderer.TryCheckMutation(QuiltRendererMutationKind.Framing, out _))
                return false;
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
            if (!TryGetSceneObjectBounds(renderer, target, out Bounds bounds, out bool hasBounds, out reason)) return false;
            GameObject selected = target as GameObject;
            if (selected == null && target is Component component) selected = component.gameObject;
            center = hasBounds ? bounds.center : selected.transform.position;
            if (!IsFinite(center))
            {
                reason = "The selected object center is invalid.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        public static bool TryGetSceneObjectBounds(QuiltRenderer renderer, UnityEngine.Object target,
            out Bounds bounds, out bool hasBounds, out string reason)
        {
            bounds = default;
            hasBounds = false;
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
            if (hasBounds && (!IsFinite(bounds.center) || !IsFinite(bounds.extents)))
            {
                reason = "The selected object bounds are invalid.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        static bool TryGetEditableFramingCamera(QuiltRenderer renderer, out Camera framingCamera)
        {
            framingCamera = null;
            if (renderer == null || renderer.settings == null || renderer.sourceCamera == null)
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
