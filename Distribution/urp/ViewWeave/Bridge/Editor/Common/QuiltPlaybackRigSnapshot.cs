using UnityEngine;

namespace ViewWeave.Editor
{
    internal sealed class QuiltPlaybackRigSnapshot
    {
        readonly QuiltSettings settings;
        readonly QuiltPhysicalFraming physical;
        readonly Camera camera, reference;
        readonly MonoBehaviour guard;
        readonly bool followFov, requests, hasReference;
        readonly QuiltViewRenderMode mode;
        readonly float near, far, referenceNear, referenceFar, referenceFov;

        internal QuiltPlaybackRigSnapshot(QuiltRenderer renderer)
        {
            settings = renderer.settings.Clone();
            physical = renderer.physicalFraming == null ? null : renderer.physicalFraming.Clone();
            camera = renderer.sourceCamera;
            reference = renderer.referenceCamera;
            hasReference = reference != null;
            guard = renderer.GuardBehaviour;
            followFov = renderer.followReferenceFieldOfView;
            requests = renderer.useRenderRequests;
            mode = renderer.viewRenderMode;
            near = camera.nearClipPlane;
            far = camera.farClipPlane;
            referenceNear = reference == null ? 0 : reference.nearClipPlane;
            referenceFar = reference == null ? 0 : reference.farClipPlane;
            referenceFov = reference == null ? 0 : reference.fieldOfView;
        }

        internal bool Matches(QuiltRenderer renderer)
        {
            if (renderer == null || renderer.settings == null || camera == null || renderer.sourceCamera != camera ||
                hasReference && reference == null || renderer.referenceCamera != reference || renderer.GuardBehaviour != guard || renderer.viewRenderMode != mode ||
                renderer.followReferenceFieldOfView != followFov || renderer.useRenderRequests != requests ||
                camera.nearClipPlane != near || camera.farClipPlane != far ||
                reference != null && (reference.nearClipPlane != referenceNear || reference.farClipPlane != referenceFar || reference.fieldOfView != referenceFov)) return false;
            QuiltSettings value = renderer.settings;
            if (value.quiltWidth != settings.quiltWidth || value.quiltHeight != settings.quiltHeight || value.columns != settings.columns ||
                value.rows != settings.rows || value.views != settings.views || value.viewAspect != settings.viewAspect ||
                value.viewCone != settings.viewCone || value.cameraDistance != settings.cameraDistance || value.fieldOfView != settings.fieldOfView) return false;
            QuiltPhysicalFraming framing = renderer.physicalFraming;
            return physical == null ? framing == null : framing != null && framing.enabled == physical.enabled &&
                framing.visibleApertureWidthMillimeters == physical.visibleApertureWidthMillimeters &&
                framing.visibleApertureHeightMillimeters == physical.visibleApertureHeightMillimeters && framing.unitsPerMeter == physical.unitsPerMeter;
        }
    }
}
