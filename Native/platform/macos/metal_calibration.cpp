#include "metal_calibration.h"
#include "../../common/subpixel_calibration.h"
#include <cmath>
#include <cstddef>
#include <limits>
#include <initializer_list>
#include <utility>

static_assert(sizeof(LkgMetalDisplayInfo) == 60, "Metal display V1 ABI mismatch");
static_assert(sizeof(LkgMetalSubpixelCell) == 24, "Metal subpixel cell ABI mismatch");
static_assert(sizeof(LkgMetalDisplayInfoV3) == 180, "Metal display V3 ABI mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV3, calibration) == 8, "Metal calibration offset mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV3, redIndex) == 68, "Metal channel offset mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV3, blueIndex) == 72, "Metal channel offset mismatch");
static_assert(offsetof(LkgMetalDisplayInfoV3, cells) == 84, "Metal cell offset mismatch");

namespace lkg::metal {
bool IsDisplayInfoV3HeaderValid(const LkgMetalDisplayInfoV3* info) {
    return info && info->structSize == sizeof(LkgMetalDisplayInfoV3) && info->apiVersion == 3;
}
const char* ValidateCellPattern(int mode, int count) {
    return lkg_validate_subpixel_cell_pattern(mode, count, LKG_COMMON_SUBPIXEL_AUTOMATIC);
}
const char* ValidateCalibration(const LkgMetalDisplayInfo& info, int cellMode, int cellCount,
    float fringe, int redIndex, int blueIndex, const LkgMetalSubpixelCell* cells, uint32_t requestedMode) {
    const auto& d = info.display;
    if (d.quilt_width <= 0 || d.quilt_height <= 0 || d.quilt_width > 16384 || d.quilt_height > 16384 ||
        d.quilt_columns <= 0 || d.quilt_rows <= 0 || d.quilt_columns > d.quilt_width || d.quilt_rows > d.quilt_height ||
        static_cast<int64_t>(d.quilt_columns) * d.quilt_rows > 4096)
        return "Invalid calibration: Quilt dimensions or view grid are out of range";
    if (!std::isfinite(d.aspect) || d.aspect <= 0.0f)
        return "Invalid calibration: aspect must be finite and positive";
    LkgCommonSubpixelCalibration common{};
    common.width = d.width; common.height = d.height;
    common.center = info.center; common.pitch = info.pitch; common.slope = info.slope;
    common.dpi = info.dpi; common.flipX = info.flipX; common.viewCone = d.viewcone;
    common.fringe = fringe; common.invertedViews = info.invertedViews;
    common.cellPatternMode = cellMode; common.cellCount = cellCount;
    common.redIndex = redIndex; common.blueIndex = blueIndex;
    LkgCommonSubpixelCell converted[4]{};
    if (cells && cellCount <= 4) for (int i = 0; i < cellCount; ++i) {
        converted[i] = { cells[i].redX, cells[i].redY, cells[i].greenX, cells[i].greenY,
            cells[i].blueX, cells[i].blueY };
    }
    return lkg_validate_subpixel_calibration(&common, requestedMode, cells ? converted : nullptr);
}
}
