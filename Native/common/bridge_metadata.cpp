#include "common/bridge_metadata.h"
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace lkg::metadata {
int32_t ReadVersion(const Symbols& symbols, char* out, uint32_t capacity) {
    if (!symbols.version) return -1;
    unsigned long major = 0, minor = 0, build = 0; int postfix = 0;
    if (!symbols.version(&major, &minor, &build, &postfix, nullptr)) return -2;
    const std::string value = std::to_string(major) + "." + std::to_string(minor) + "." + std::to_string(build);
    if (out && capacity) std::snprintf(out, capacity, "%s", value.c_str());
    return static_cast<int32_t>(value.size());
}

int32_t ReadDisplayCount(const Symbols& symbols, uint32_t* count) {
    if (!symbols.displays || !count) return -1;
    int number = 0; if (!symbols.displays(&number, nullptr) || number < 0) return -2;
    *count = static_cast<uint32_t>(number); return 0;
}

int32_t ReadDisplay(const Symbols& symbols, uint32_t ordinal, LkgBridgeDisplayInfo* info) {
    if (!symbols.displays || !symbols.dimensions || !symbols.viewcone || !symbols.defaultQuilt || !info) return -1;
    int number = 0;
    if (!symbols.displays(&number, nullptr) || number <= 0 || number > kMaximumBridgeDisplays || ordinal >= static_cast<uint32_t>(number)) return -2;
    const int capacity = number;
    std::vector<unsigned long> ids(static_cast<size_t>(capacity));
    number = capacity;
    if (!symbols.displays(&number, ids.data()) || number <= 0 || number > capacity || ordinal >= static_cast<uint32_t>(number)) return -3;
    const unsigned long id = ids[ordinal];
    unsigned long width = 0, height = 0;
    LkgBridgeDisplayInfo candidate{};
    if (id > UINT32_MAX || !symbols.dimensions(id, &width, &height) || width > UINT32_MAX || height > UINT32_MAX ||
        !symbols.viewcone(id, &candidate.viewcone) || !symbols.defaultQuilt(id, &candidate.aspect,
            &candidate.quilt_width, &candidate.quilt_height, &candidate.quilt_columns, &candidate.quilt_rows)) return -4;
    candidate.index = static_cast<uint32_t>(id); candidate.width = static_cast<uint32_t>(width); candidate.height = static_cast<uint32_t>(height);
    *info = candidate;
    return 0;
}

int32_t ReadPhysicalDisplay(const Symbols& symbols, uint32_t displayIndex, LkgBridgePhysicalDisplayInfoV1* info) {
    if (!info || info->structSize != sizeof(LkgBridgePhysicalDisplayInfoV1) || info->version != 1) return -1;
    info->pixelWidth = 0;
    info->pixelHeight = 0;
    info->dpi = 0.0f;
    info->valid = 0;
    if (!symbols.calibration) return -2;
    float center = 0.0f, pitch = 0.0f, slope = 0.0f, dpi = 0.0f, flipX = 0.0f, viewcone = 0.0f, fringe = 0.0f;
    int width = 0, height = 0, inverted = 0, cellPatternMode = 0, numberOfCells = 0;
    try {
        if (!symbols.calibration(static_cast<unsigned long>(displayIndex), &center, &pitch, &slope, &width, &height, &dpi, &flipX,
            &inverted, &viewcone, &fringe, &cellPatternMode, &numberOfCells, nullptr)) return -3;
    } catch (...) {
        return -3;
    }
    if (width <= 0 || height <= 0 || !std::isfinite(dpi) || dpi <= 0.0f || numberOfCells < 0) return -3;
    info->pixelWidth = static_cast<uint32_t>(width);
    info->pixelHeight = static_cast<uint32_t>(height);
    info->dpi = dpi;
    info->valid = 1;
    return 0;
}

}
