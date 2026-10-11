#pragma once

#include <stdint.h>

typedef struct LkgCommonSubpixelCalibration {
    uint32_t width;
    uint32_t height;
    float center;
    float pitch;
    float slope;
    float dpi;
    float flipX;
    float viewCone;
    float fringe;
    int32_t invertedViews;
    int32_t cellPatternMode;
    int32_t cellCount;
    int32_t redIndex;
    int32_t blueIndex;
} LkgCommonSubpixelCalibration;

typedef struct LkgCommonSubpixelCell {
    float redX;
    float redY;
    float greenX;
    float greenY;
    float blueX;
    float blueY;
} LkgCommonSubpixelCell;

enum LkgCommonSubpixelMode {
    LKG_COMMON_SUBPIXEL_AUTOMATIC = 0,
    LKG_COMMON_SUBPIXEL_CLASSIC_RGB = 1,
    LKG_COMMON_SUBPIXEL_CALIBRATED_CELLS = 2
};

#ifdef __cplusplus
extern "C" {
#endif

const char* lkg_validate_subpixel_calibration(const LkgCommonSubpixelCalibration* calibration,
    uint32_t requestedMode, const LkgCommonSubpixelCell cells[4]);
const char* lkg_validate_subpixel_cell_pattern(int32_t cellPatternMode, int32_t cellCount,
    uint32_t requestedMode);

#ifdef __cplusplus
}
#endif
