#pragma once
#include "metal_display.h"

namespace lkg::metal {
// Pure validation also runs in portable native tests; no device names are involved.
const char* ValidateCalibration(const LkgMetalDisplayInfo& info, int cellMode, int cellCount,
    float fringe, int redIndex, int blueIndex, const LkgMetalSubpixelCell* cells = nullptr,
    uint32_t requestedMode = 0);
const char* ValidateCellPattern(int mode, int count);
bool IsDisplayInfoV3HeaderValid(const LkgMetalDisplayInfoV3* info);
}
