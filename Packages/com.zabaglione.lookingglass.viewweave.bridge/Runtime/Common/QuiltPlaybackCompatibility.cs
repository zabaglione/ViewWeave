using System;
using UnityEngine;

namespace ViewWeave
{
    internal static class QuiltPlaybackCompatibility
    {
        internal static bool SourceMatchesRenderer(QuiltPlaybackInfo info, QuiltRenderer value, out string error)
        {
            error = string.Empty;
            QuiltLayout layout = info.Layout;
            if (!value.TryGetEffectiveSettings(out QuiltSettings settings, out error)) return false;
            if (layout.QuiltWidth != settings.quiltWidth || layout.QuiltHeight != settings.quiltHeight || layout.Columns != settings.columns || layout.Rows != settings.rows || layout.ViewCount != settings.views || Math.Abs(layout.AspectRatio - settings.viewAspect) > 0.0001f)
            {
                error = "Quilt source layout must match the active Bridge Renderer dimensions, grid, view count, and aspect.";
                return false;
            }
            Camera sourceCamera = value.sourceCamera;
            QuiltPhysicalFraming physical = value.physicalFraming;
            bool physicalMismatch = info.Geometry.physicalFramingEnabled != (physical != null && physical.enabled);
            if (physical != null && physical.enabled)
            {
                physicalMismatch |= Math.Abs(info.Geometry.visibleApertureWidthMillimeters - physical.visibleApertureWidthMillimeters) > 0.001f ||
                                    Math.Abs(info.Geometry.visibleApertureHeightMillimeters - physical.visibleApertureHeightMillimeters) > 0.001f ||
                                    Math.Abs(info.Geometry.unitsPerMeter - physical.unitsPerMeter) > 0.000001f;
            }
            if (info.HasCameraGeometry && (sourceCamera == null || Math.Abs(info.Geometry.fieldOfView - settings.fieldOfView) > 0.001f ||
                Math.Abs(info.Geometry.cameraDistance - settings.cameraDistance) > 0.001f || Math.Abs(info.Geometry.viewCone - settings.viewCone) > 0.001f ||
                Math.Abs(info.Geometry.nearClip - sourceCamera.nearClipPlane) > 0.001f || Math.Abs(info.Geometry.farClip - sourceCamera.farClipPlane) > 0.001f || physicalMismatch))
            {
                error = "Quilt source camera geometry does not match the active Renderer lens settings.";
                return false;
            }
            return true;
        }

    }
}
