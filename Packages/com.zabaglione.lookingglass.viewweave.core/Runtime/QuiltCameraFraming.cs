using System;

namespace ViewWeave
{
    /// <summary>Mutable camera framing values exposed as an atomic authoring snapshot.</summary>
    [Serializable]
    public readonly struct QuiltCameraFraming : IEquatable<QuiltCameraFraming>
    {
        public readonly float VerticalFieldOfView;
        public readonly float FocusDistance;
        public readonly bool PhysicalFramingEnabled;
        public readonly float ApertureWidthMillimeters;
        public readonly float ApertureHeightMillimeters;
        public readonly float UnitsPerMeter;

        public QuiltCameraFraming(float verticalFieldOfView, float focusDistance, bool physicalFramingEnabled,
            float apertureWidthMillimeters, float apertureHeightMillimeters, float unitsPerMeter)
        {
            VerticalFieldOfView = verticalFieldOfView;
            FocusDistance = focusDistance;
            PhysicalFramingEnabled = physicalFramingEnabled;
            ApertureWidthMillimeters = apertureWidthMillimeters;
            ApertureHeightMillimeters = apertureHeightMillimeters;
            UnitsPerMeter = unitsPerMeter;
        }

        public bool Equals(QuiltCameraFraming other)
        {
            return VerticalFieldOfView.Equals(other.VerticalFieldOfView) &&
                   FocusDistance.Equals(other.FocusDistance) &&
                   PhysicalFramingEnabled == other.PhysicalFramingEnabled &&
                   ApertureWidthMillimeters.Equals(other.ApertureWidthMillimeters) &&
                   ApertureHeightMillimeters.Equals(other.ApertureHeightMillimeters) &&
                   UnitsPerMeter.Equals(other.UnitsPerMeter);
        }

        public override bool Equals(object obj) => obj is QuiltCameraFraming other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = VerticalFieldOfView.GetHashCode();
                hash = (hash * 397) ^ FocusDistance.GetHashCode();
                hash = (hash * 397) ^ PhysicalFramingEnabled.GetHashCode();
                hash = (hash * 397) ^ ApertureWidthMillimeters.GetHashCode();
                hash = (hash * 397) ^ ApertureHeightMillimeters.GetHashCode();
                return (hash * 397) ^ UnitsPerMeter.GetHashCode();
            }
        }

        public static bool operator ==(QuiltCameraFraming left, QuiltCameraFraming right) => left.Equals(right);
        public static bool operator !=(QuiltCameraFraming left, QuiltCameraFraming right) => !left.Equals(right);
    }
}
