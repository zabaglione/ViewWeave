#include "subpixel_calibration.h"

#include <cmath>
#include <initializer_list>
#include <limits>
#include <utility>

namespace {

const char* ValidateCellMode(int32_t mode, int32_t count) {
    if (mode < 0 || mode > 4) return "Unsupported calibration: expected cell-pattern mode 0 through 4";
    if (count < 0 || count > 4) return "Unsupported calibration: expected at most four subpixel cells";
    const int32_t required = mode == 0 ? (count == 0 ? 0 : 1) : mode == 3 ? 4 : 2;
    return count >= required ? nullptr : "Invalid calibration: too few cells for the selected pattern";
}

bool SupportedChannelOrder(int32_t redIndex, int32_t blueIndex) {
    return (redIndex == 0 && blueIndex == 2) || (redIndex == 2 && blueIndex == 0);
}

} // namespace

extern "C" const char* lkg_validate_subpixel_cell_pattern(int32_t cellPatternMode, int32_t cellCount,
    uint32_t requestedMode) {
    if (requestedMode > LKG_COMMON_SUBPIXEL_CALIBRATED_CELLS)
        return "Invalid output mode: expected Automatic, Classic RGB, or Calibrated Cells";
    if (requestedMode == LKG_COMMON_SUBPIXEL_CLASSIC_RGB) return nullptr;
    if (const char* error = ValidateCellMode(cellPatternMode, cellCount)) return error;
    if (requestedMode == LKG_COMMON_SUBPIXEL_CALIBRATED_CELLS && cellCount == 0)
        return "Invalid calibration: Calibrated Cells requires an actual cell table";
    return nullptr;
}

extern "C" const char* lkg_validate_subpixel_calibration(const LkgCommonSubpixelCalibration* calibration,
    uint32_t requestedMode, const LkgCommonSubpixelCell cells[4]) {
    if (!calibration) return "Invalid calibration: calibration input is missing";
    if (requestedMode > LKG_COMMON_SUBPIXEL_CALIBRATED_CELLS)
        return "Invalid output mode: expected Automatic, Classic RGB, or Calibrated Cells";
    const auto& c = *calibration;
    if (!c.width || !c.height || c.width > 16384 || c.height > 16384)
        return "Invalid calibration: display dimensions must be between 1 and 16384 pixels";
    if (!std::isfinite(c.center) || !std::isfinite(c.pitch) || c.pitch <= 0.0f ||
        !std::isfinite(c.slope) || std::abs(c.slope) <= 0.00001f ||
        !std::isfinite(c.dpi) || c.dpi <= 0.0f || !std::isfinite(c.flipX) ||
        !std::isfinite(c.viewCone) || c.viewCone <= 0.0f || c.viewCone >= 179.0f ||
        !std::isfinite(c.fringe))
        return "Invalid calibration: optical values must be finite with positive pitch and DPI";
    if ((c.flipX != 0.0f && c.flipX != 1.0f) || (c.invertedViews != 0 && c.invertedViews != 1))
        return "Invalid calibration: flipX and invertedViews must be zero or one";

    if (const char* error = lkg_validate_subpixel_cell_pattern(c.cellPatternMode, c.cellCount, requestedMode))
        return error;
    bool usesCells = false;
    if (requestedMode == LKG_COMMON_SUBPIXEL_CLASSIC_RGB) {
        // Classic RGB deliberately ignores SDK cell offsets and channel order.
        usesCells = false;
    } else {
        usesCells = requestedMode == LKG_COMMON_SUBPIXEL_CALIBRATED_CELLS ||
            c.cellPatternMode != 0 || c.cellCount != 0;
        if (!usesCells && !SupportedChannelOrder(c.redIndex, c.blueIndex))
            return "Unsupported calibration: expected RGB or BGR channel indices";
        if (usesCells && !SupportedChannelOrder(c.redIndex, c.blueIndex))
            return "Unsupported calibration: expected RGB or BGR channel indices";
        if (usesCells && !cells)
            return "Invalid calibration: selected cell pattern requires an actual cell table";
        if (usesCells && c.invertedViews != 1)
            return "Unsupported calibration: cell-table output currently requires invertedViews equal to one";
    }

    const float convertedPitch = c.pitch * static_cast<float>(c.width) /
        c.dpi * std::cos(std::atan(1.0f / c.slope));
    const float tiltDenominator = static_cast<float>(c.width) * c.slope;
    const float tilt = static_cast<float>(c.height) / tiltDenominator;
    if (!std::isfinite(convertedPitch) || convertedPitch <= 0.0f || !std::isfinite(tiltDenominator) ||
        !std::isfinite(tilt))
        return "Invalid calibration: converted optical pitch or tilt is out of range";

    const double numericLimit = std::numeric_limits<float>::max() * 0.5;
    const double phaseBound = (1.0 + std::abs(static_cast<double>(tilt)) +
        2.0 / (3.0 * c.width)) * convertedPitch + std::abs(static_cast<double>(c.center));
    if (!std::isfinite(phaseBound) || phaseBound > numericLimit)
        return "Invalid calibration: optical phase exceeds the shader numeric range";

    if (usesCells) {
        for (int32_t i = 0; i < c.cellCount; ++i) {
            const float offsets[] = { cells[i].redX, cells[i].redY, cells[i].greenX, cells[i].greenY,
                cells[i].blueX, cells[i].blueY };
            for (int pair = 0; pair < 3; ++pair) {
                const float offsetX = offsets[pair * 2];
                const float offsetY = offsets[pair * 2 + 1];
                if (!std::isfinite(offsetX) || !std::isfinite(offsetY))
                    return "Invalid calibration: subpixel offsets must be finite";
                const double xBound = 1.0 + std::abs(static_cast<double>(offsetX)) / c.width;
                const double yBound = 1.0 + std::abs(static_cast<double>(offsetY)) / c.height;
                const double yTiltBound = yBound * std::abs(static_cast<double>(tilt));
                const double coordinateBound = xBound + yTiltBound;
                if (xBound > numericLimit || yBound > numericLimit || yTiltBound > numericLimit ||
                    coordinateBound > numericLimit)
                    return "Invalid calibration: subpixel coordinates exceed the shader numeric range";
                const double cellPhaseBound = coordinateBound * convertedPitch +
                    std::abs(static_cast<double>(c.center));
                if (!std::isfinite(cellPhaseBound) || cellPhaseBound > numericLimit)
                    return "Invalid calibration: subpixel phase exceeds the shader numeric range";
            }
        }
    }
    return nullptr;
}
