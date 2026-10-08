using System;
using UnityEngine;

namespace ViewWeave
{
    [Serializable]
    public sealed class QuiltPhysicalFraming
    {
        public bool enabled;
        [Min(0f)] public float visibleApertureWidthMillimeters;
        [Min(0f)] public float visibleApertureHeightMillimeters;
        [Min(0.001f)] public float unitsPerMeter = 1f;

        public float Aspect => visibleApertureWidthMillimeters / visibleApertureHeightMillimeters;
        public float ApertureHeightInUnityUnits => visibleApertureHeightMillimeters * unitsPerMeter / 1000f;
        public float DiagonalInches => Mathf.Sqrt(visibleApertureWidthMillimeters * visibleApertureWidthMillimeters +
            visibleApertureHeightMillimeters * visibleApertureHeightMillimeters) / 25.4f;

        public QuiltPhysicalFraming Clone() => (QuiltPhysicalFraming)MemberwiseClone();

        public bool TryResolve(float fieldOfView, out float aspect, out float cameraDistance, out string error)
        {
            aspect = 0f;
            cameraDistance = 0f;
            error = string.Empty;
            if (!enabled) return false;
            if (!IsFinite(visibleApertureWidthMillimeters) || !IsFinite(visibleApertureHeightMillimeters) ||
                !IsFinite(unitsPerMeter) || visibleApertureWidthMillimeters <= 0f || visibleApertureHeightMillimeters <= 0f || unitsPerMeter <= 0f)
            {
                error = "Physical framing requires finite positive aperture dimensions and units per meter.";
                return false;
            }
            if (!IsFinite(fieldOfView) || fieldOfView <= 0f || fieldOfView >= 179f)
            {
                error = "Physical framing requires a finite vertical field of view between 0 and 179 degrees.";
                return false;
            }

            aspect = visibleApertureWidthMillimeters / visibleApertureHeightMillimeters;
            float halfHeight = ApertureHeightInUnityUnits * 0.5f;
            float tangent = Mathf.Tan(fieldOfView * Mathf.Deg2Rad * 0.5f);
            cameraDistance = halfHeight / tangent;
            if (!IsFinite(aspect) || aspect <= 0f || !IsFinite(cameraDistance) || cameraDistance <= 0f)
            {
                aspect = 0f;
                cameraDistance = 0f;
                error = "Physical framing dimensions and field of view produced invalid camera geometry.";
                return false;
            }
            return true;
        }

        public bool Matches(QuiltPhysicalFraming other)
        {
            return other != null && enabled == other.enabled &&
                visibleApertureWidthMillimeters == other.visibleApertureWidthMillimeters &&
                visibleApertureHeightMillimeters == other.visibleApertureHeightMillimeters && unitsPerMeter == other.unitsPerMeter;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static class QuiltFramingResolver
    {
        public static bool TryResolve(QuiltSettings source, QuiltPhysicalFraming physicalFraming, float effectiveFieldOfView,
            out QuiltSettings effective, out string error)
        {
            effective = null;
            error = string.Empty;
            if (source == null)
            {
                error = "Quilt settings are required.";
                return false;
            }

            try { source.Validate(); }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }

            effective = source.Clone();
            effective.fieldOfView = effectiveFieldOfView;
            if (physicalFraming != null && physicalFraming.enabled)
            {
                if (!physicalFraming.TryResolve(effectiveFieldOfView, out float aspect, out float distance, out error))
                {
                    effective = null;
                    return false;
                }
                effective.viewAspect = aspect;
                effective.cameraDistance = distance;
            }

            try
            {
                effective.Validate();
                effective.GetLayout();
                return true;
            }
            catch (Exception exception)
            {
                effective = null;
                error = exception.Message;
                return false;
            }
        }

        public static float EffectiveFieldOfView(QuiltRenderer renderer)
        {
            if (renderer == null || renderer.settings == null) return 0f;
            Camera reference = renderer.referenceCamera;
            return reference != null && renderer.followReferenceFieldOfView ? reference.fieldOfView : renderer.settings.fieldOfView;
        }
    }
}
