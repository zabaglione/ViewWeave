#pragma once
#include "metal_display.h"

namespace lkg::metal {
// Pure validation also runs in portable native tests; no device names are involved.
const char* ValidateCalibration(const LkgMetalDisplayInfo& info, int cellMode, int cellCount,
    float fringe, int redIndex, int blueIndex);
bool IsDisplayInfoV2HeaderValid(const LkgMetalDisplayInfoV2* info);
}
