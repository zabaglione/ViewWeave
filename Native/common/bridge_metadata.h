#pragma once
#include "common/bridge_runtime_api.h"
#include <cwchar>

// Native SDK scalar widths follow the target ABI; public ViewWeave structs remain fixed-width.
namespace lkg::metadata {
using VersionFn = bool (*)(unsigned long*, unsigned long*, unsigned long*, int*, wchar_t*);
using GetDisplaysFn = bool (*)(int*, unsigned long*);
using GetDimensionsFn = bool (*)(unsigned long, unsigned long*, unsigned long*);
using GetViewconeFn = bool (*)(unsigned long, float*);
using GetDefaultQuiltFn = bool (*)(unsigned long, float*, int*, int*, int*, int*);
struct CalibrationSubpixelCell { float ROffsetX, ROffsetY, GOffsetX, GOffsetY, BOffsetX, BOffsetY; };
using GetCalibrationFn = bool (*)(unsigned long, float*, float*, float*, int*, int*, float*, float*, int*, float*, float*, int*, int*, CalibrationSubpixelCell*);
constexpr int kMaximumBridgeDisplays = 256;
struct Symbols {
    VersionFn version{};
    GetDisplaysFn displays{};
    GetDimensionsFn dimensions{};
    GetViewconeFn viewcone{};
    GetDefaultQuiltFn defaultQuilt{};
    GetCalibrationFn calibration{};
};
// Callers own module loading, SDK admission, initialization, locking, and lifetime.
int32_t ReadVersion(const Symbols&, char* out, uint32_t capacity);
int32_t ReadDisplayCount(const Symbols&, uint32_t* count);
int32_t ReadDisplay(const Symbols&, uint32_t ordinal, LkgBridgeDisplayInfo* info);
int32_t ReadPhysicalDisplay(const Symbols&, uint32_t displayIndex, LkgBridgePhysicalDisplayInfoV1* info);
}
