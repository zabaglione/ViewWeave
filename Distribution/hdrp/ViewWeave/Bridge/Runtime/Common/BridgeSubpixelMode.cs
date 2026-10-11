using UnityEngine;

namespace ViewWeave
{
    /// <summary>Requested subpixel processing, independent of the graphics backend.</summary>
    public enum BridgeSubpixelMode
    {
        [InspectorName("Automatic (Bridge Calibration)")] Automatic = 0,
        [InspectorName("Classic RGB (Diagnostic)")] ClassicRgb = 1,
        [InspectorName("Calibrated Cells (Required)")] CalibratedCells = 2
    }

    public static class BridgeSubpixelPolicy
    {
        public static bool Validate(BridgeSubpixelMode mode, out string error)
        {
            bool valid = mode == BridgeSubpixelMode.Automatic || mode == BridgeSubpixelMode.ClassicRgb ||
                mode == BridgeSubpixelMode.CalibratedCells;
            error = valid ? string.Empty : "Subpixel processing mode is invalid.";
            return valid;
        }

        public static bool TryResolveCellUsage(BridgeSubpixelMode mode, bool hasCalibratedCells,
            out bool useCells, out string error)
        {
            useCells = false;
            if (!Validate(mode, out error)) return false;
            if (mode == BridgeSubpixelMode.CalibratedCells && !hasCalibratedCells)
            {
                error = "Calibrated Cells requires a Bridge subpixel cell table for the selected display.";
                return false;
            }
            useCells = hasCalibratedCells && mode != BridgeSubpixelMode.ClassicRgb;
            return true;
        }

        public static bool ValidateBridgeManagedOutput(BridgeSubpixelMode mode, out string error)
        {
            if (!Validate(mode, out error)) return false;
            if (mode == BridgeSubpixelMode.Automatic) return true;
            error = "Bridge-managed output currently supports only Automatic subpixel processing. Manual modes are not implemented.";
            return false;
        }
    }
}
