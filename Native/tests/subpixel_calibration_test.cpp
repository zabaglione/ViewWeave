#include "common/subpixel_calibration.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>

static void Check(bool passed, const char* message) {
    if (passed) return;
    std::fprintf(stderr, "FAIL %s\n", message);
    std::exit(1);
}

int main() {
    LkgCommonSubpixelCalibration calibration{};
    calibration.width = 2560;
    calibration.height = 1600;
    calibration.center = 0.5f;
    calibration.pitch = 50.0f;
    calibration.slope = -7.0f;
    calibration.dpi = 254.0f;
    calibration.flipX = 1.0f;
    calibration.viewCone = 40.0f;
    calibration.invertedViews = 1;
    calibration.redIndex = 0;
    calibration.blueIndex = 2;
    calibration.cellPatternMode = 0;
    calibration.cellCount = 0;
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_AUTOMATIC, nullptr) == nullptr,
        "Automatic accepts a calibrated no-cell RGB display");

    calibration.redIndex = 2;
    calibration.blueIndex = 0;
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_AUTOMATIC, nullptr) == nullptr,
        "Automatic accepts a calibrated no-cell BGR display");
    calibration.redIndex = 1;
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_AUTOMATIC, nullptr) != nullptr,
        "Automatic rejects unsupported channel indices instead of treating them as absent cells");

    calibration.cellPatternMode = 3;
    calibration.cellCount = 4;
    LkgCommonSubpixelCell cells[4]{};
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_AUTOMATIC, nullptr) != nullptr,
        "Automatic rejects a cell pattern whose actual table is unavailable");
    calibration.redIndex = 0;
    calibration.blueIndex = 2;
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_CALIBRATED_CELLS, cells) == nullptr,
        "Calibrated Cells accepts a valid actual table");
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_CLASSIC_RGB, nullptr) == nullptr,
        "Classic RGB ignores table and hardware channel-order metadata");

    calibration.pitch = std::numeric_limits<float>::max();
    calibration.width = 16384;
    calibration.dpi = std::numeric_limits<float>::min();
    Check(lkg_validate_subpixel_calibration(&calibration, LKG_COMMON_SUBPIXEL_CLASSIC_RGB, nullptr) != nullptr,
        "all modes reject derived optical overflow");
    std::puts("subpixel_calibration_test pass");
    return 0;
}
