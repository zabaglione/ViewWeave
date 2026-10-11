#pragma once
#include "metal_display.h"
#include <cstddef>
#include <string>

namespace lkg::metal {
// JSON avoids the SDK's unbounded C cell-buffer write. The native fingerprint
// must match the same display in the Bridge response before any cells are used.
bool ParseBridgeCellResponse(const char* json, size_t bytes, LkgMetalDisplayInfoV3& profile,
    long x, long y, std::string& error);
bool ReadBridgeCellTable(LkgMetalDisplayInfoV3& profile, long x, long y, std::string& error,
    uint16_t port = 33334);
}
