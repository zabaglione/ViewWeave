#include "metal_calibration.h"
#include <cmath>
#include <cstddef>
#include <limits>

static_assert(sizeof(LkgMetalDisplayInfo) == 60, "Metal display V1 ABI mismatch");
static_assert(sizeof(LkgMetalDisplayInfoV2) == 76, "Metal display V2 ABI mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV2, calibration) == 8, "Metal calibration offset mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV2, redIndex) == 68, "Metal channel offset mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV2, blueIndex) == 72, "Metal channel offset mismatch");

namespace lkg::metal {
bool IsDisplayInfoV2HeaderValid(const LkgMetalDisplayInfoV2* info) {
    return info && info->structSize == sizeof(LkgMetalDisplayInfoV2) && info->apiVersion == 2;
}
const char* ValidateCalibration(const LkgMetalDisplayInfo& info, int cellMode, int cellCount,
    float fringe, int redIndex, int blueIndex) {
    const auto& d = info.display;
    if (!d.width || !d.height || d.width > 16384 || d.height > 16384)
        return "Invalid calibration: display dimensions must be between 1 and 16384 pixels";
    if (d.quilt_width <= 0 || d.quilt_height <= 0 || d.quilt_width > 16384 || d.quilt_height > 16384 ||
        d.quilt_columns <= 0 || d.quilt_rows <= 0 ||
        d.quilt_columns > d.quilt_width || d.quilt_rows > d.quilt_height ||
        static_cast<int64_t>(d.quilt_columns) * d.quilt_rows > 4096)
        return "Invalid calibration: Quilt dimensions or view grid are out of range";
    if (!std::isfinite(d.aspect) || d.aspect <= 0 || !std::isfinite(d.viewcone) ||
        d.viewcone <= 0 || d.viewcone >= 179)
        return "Invalid calibration: aspect and view cone must be finite and positive";
    if (!std::isfinite(info.center) || !std::isfinite(info.pitch) || info.pitch <= 0 ||
        !std::isfinite(info.slope) || std::abs(info.slope) <= 0.00001f ||
        !std::isfinite(info.dpi) || info.dpi <= 0 || !std::isfinite(fringe))
        return "Invalid calibration: optical values must be finite with positive pitch and DPI and nonzero slope";
    if ((info.flipX != 0 && info.flipX != 1) || (info.invertedViews != 0 && info.invertedViews != 1))
        return "Invalid calibration: flipX and invertedViews must be zero or one";
    if (cellCount < 0) return "Invalid calibration: negative subpixel cell count";
    if (cellMode != 0 || cellCount != 0)
        return "Unsupported calibration: subpixel cell tables and nonzero cell-pattern modes are not implemented";
    if (!((redIndex == 0 && blueIndex == 2) || (redIndex == 2 && blueIndex == 0)))
        return "Unsupported calibration: expected an RGB or BGR subpixel order";
    // Validate exactly the float conversion used by Unity, including intermediate overflow.
    float pitch = info.pitch * d.width / info.dpi * std::cos(std::atan(1.0f / info.slope));
    float tiltDenominator = d.width * info.slope;
    float tilt = static_cast<float>(d.height) / tiltDenominator;
    if (!std::isfinite(pitch) || pitch <= 0 || !std::isfinite(tiltDenominator) || !std::isfinite(tilt))
        return "Invalid calibration: converted optical pitch or tilt is out of range";
    // Include every screen coordinate and channel in either horizontal direction.
    // Leave headroom for float rounding in the shader's multiply/add sequence.
    double phaseBound = (1.0 + std::abs(static_cast<double>(tilt)) + 2.0 / (3.0 * d.width)) * pitch +
        std::abs(static_cast<double>(info.center));
    if (!std::isfinite(phaseBound) || phaseBound > std::numeric_limits<float>::max() * 0.5)
        return "Invalid calibration: optical phase exceeds the shader numeric range";
    return nullptr;
}
}
