#include "common/bridge_metadata.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>

namespace {
struct SyntheticDisplay {
    unsigned long id;
    unsigned long width, height;
    float aspect, viewcone;
    int quiltWidth, quiltHeight, columns, rows;
    int calibrationWidth, calibrationHeight;
    float dpi;
};
constexpr std::array<SyntheticDisplay, 3> displays{{
    {7, 1920, 1080, 16.0f / 9.0f, 35.0f, 4000, 4000, 5, 9, 1920, 1080, 120.0f},
    {42, 1440, 2560, 9.0f / 16.0f, 54.0f, 4092, 4092, 11, 6, 1440, 2560, 300.0f},
    {301, 5120, 2880, 16.0f / 9.0f, 50.0f, 8192, 8192, 8, 6, 5120, 2880, 190.0f},
}};
int declaredCount = static_cast<int>(displays.size()), returnedCount = static_cast<int>(displays.size());
bool calibrationSucceeds = true, calibrationThrows = false;
bool dimensionsFails = false, viewconeFails = false, quiltFails = false;
float calibrationDpiOverride = 0.0f;
unsigned long lastCalibrationDisplayId = 0;
void Check(bool condition, const char* label) {
    if (condition) return;
    std::fprintf(stderr, "FAIL %s\n", label); std::exit(1);
}
const SyntheticDisplay* FindDisplay(unsigned long id) {
    for (const auto& display : displays) if (display.id == id) return &display;
    return nullptr;
}
bool Version(unsigned long* major, unsigned long* minor, unsigned long* build, int*, wchar_t*) {
    *major = 2; *minor = 6; *build = 3; return true;
}
bool Displays(int* count, unsigned long* ids) {
    if (!ids) { *count = declaredCount; return true; }
    // Model a display list changing between count and list queries without overrunning storage.
    const int capacity = *count;
    const int available = std::min(returnedCount, static_cast<int>(displays.size()));
    for (int i = 0; i < capacity && i < available; ++i) ids[i] = displays[static_cast<size_t>(i)].id;
    *count = returnedCount; return true;
}
bool Dimensions(unsigned long id, unsigned long* w, unsigned long* h) {
    const SyntheticDisplay* display = FindDisplay(id);
    if (!display) return false;
    *w = display->width; *h = display->height; return !dimensionsFails;
}
bool Viewcone(unsigned long id, float* value) {
    const SyntheticDisplay* display = FindDisplay(id);
    if (!display) return false;
    *value = display->viewcone; return !viewconeFails;
}
bool Quilt(unsigned long id, float* aspect, int* width, int* height, int* columns, int* rows) {
    const SyntheticDisplay* display = FindDisplay(id);
    if (!display) return false;
    *aspect = display->aspect; *width = display->quiltWidth; *height = display->quiltHeight;
    *columns = display->columns; *rows = display->rows; return !quiltFails;
}
bool Calibration(unsigned long id, float*, float*, float*, int* width, int* height, float* dpi,
    float*, int*, float*, float*, int*, int* cells, lkg::metadata::CalibrationSubpixelCell*) {
    if (calibrationThrows) throw 1;
    lastCalibrationDisplayId = id;
    const SyntheticDisplay* display = FindDisplay(id);
    if (!display || !calibrationSucceeds) return false;
    *width = display->calibrationWidth; *height = display->calibrationHeight;
    *dpi = calibrationDpiOverride == 0.0f ? display->dpi : calibrationDpiOverride;
    *cells = 0; return true;
}
}
int main() {
    const lkg::metadata::Symbols api{Version, Displays, Dimensions, Viewcone, Quilt, Calibration};
    char version[16]{};
    Check(lkg::metadata::ReadVersion(api, version, sizeof(version)) == 5 && !std::strcmp(version, "2.6.3"), "version conversion");
    Check(lkg::metadata::ReadVersion({}, version, sizeof(version)) == -1, "missing version symbol");
    uint32_t count = 0;
    Check(lkg::metadata::ReadDisplayCount(api, &count) == 0 && count == displays.size(), "display count");

    const LkgBridgeDisplayInfo sentinel{9001, 9002, 9003, 1.25f, 2.5f, 9004, 9005, 9006, 9007};
    auto unchanged = [&](const LkgBridgeDisplayInfo& value, const char* label) {
        Check(value.index == sentinel.index && value.width == sentinel.width && value.height == sentinel.height &&
            value.aspect == sentinel.aspect && value.viewcone == sentinel.viewcone &&
            value.quilt_width == sentinel.quilt_width && value.quilt_height == sentinel.quilt_height &&
            value.quilt_columns == sentinel.quilt_columns && value.quilt_rows == sentinel.quilt_rows, label);
    };
    Check(lkg::metadata::ReadDisplay(api, 0, nullptr) == -1, "null display result");
    for (int missing = 0; missing != 4; ++missing) {
        lkg::metadata::Symbols incomplete = api;
        if (missing == 0) incomplete.displays = nullptr;
        if (missing == 1) incomplete.dimensions = nullptr;
        if (missing == 2) incomplete.viewcone = nullptr;
        if (missing == 3) incomplete.defaultQuilt = nullptr;
        LkgBridgeDisplayInfo result = sentinel;
        Check(lkg::metadata::ReadDisplay(incomplete, 0, &result) == -1, "missing display query symbol");
        unchanged(result, "missing symbol leaves display result unchanged");
    }
    for (int failure = 0; failure != 3; ++failure) {
        dimensionsFails = failure == 0;
        viewconeFails = failure == 1;
        quiltFails = failure == 2;
        LkgBridgeDisplayInfo result = sentinel;
        Check(lkg::metadata::ReadDisplay(api, 0, &result) == -4, "SDK metadata query failure");
        unchanged(result, "failed SDK query leaves display result unchanged");
    }
    dimensionsFails = viewconeFails = quiltFails = false;
    for (uint32_t ordinal = 0; ordinal < displays.size(); ++ordinal) {
        const SyntheticDisplay& expected = displays[ordinal];
        LkgBridgeDisplayInfo display{};
        Check(lkg::metadata::ReadDisplay(api, ordinal, &display) == 0, "synthetic display query");
        Check(display.index == expected.id, "ordinal resolves the intended nonsequential physical ID");
        Check(display.width == expected.width && display.height == expected.height, "physical screen dimensions follow ID");
        Check(std::fabs(display.aspect - expected.aspect) < 0.0001f &&
            std::fabs(display.viewcone - expected.viewcone) < 0.0001f, "aspect and viewcone follow ID");
        Check(display.quilt_width == expected.quiltWidth && display.quilt_height == expected.quiltHeight &&
            display.quilt_columns == expected.columns && display.quilt_rows == expected.rows, "quilt metadata follows ID");

        LkgBridgePhysicalDisplayInfoV1 physical{sizeof(physical), 1, 0, 0, 0, 0};
        Check(lkg::metadata::ReadPhysicalDisplay(api, display.index, &physical) == 0 && physical.valid == 1,
            "calibration query uses resolved display ID");
        Check(lastCalibrationDisplayId == expected.id && physical.pixelWidth == expected.calibrationWidth &&
            physical.pixelHeight == expected.calibrationHeight && std::fabs(physical.dpi - expected.dpi) < 0.001f,
            "calibration metadata follows intended display ID");
    }
    LkgBridgeDisplayInfo display{};
    Check(lkg::metadata::ReadDisplay(api, 3, &display) == -2, "invalid ordinal");
    returnedCount = 4;
    Check(lkg::metadata::ReadDisplay(api, 1, &display) == -3, "growing list rejects stale capacity");
    returnedCount = 1;
    Check(lkg::metadata::ReadDisplay(api, 1, &display) == -3, "shrinking list rejects removed ordinal");
    returnedCount = static_cast<int>(displays.size());
    declaredCount = 257;
    Check(lkg::metadata::ReadDisplay(api, 0, &display) == -2, "bounded enumeration");
    declaredCount = static_cast<int>(displays.size());

    LkgBridgePhysicalDisplayInfoV1 physical{sizeof(physical), 1, 0, 0, 0, 0};
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == 0 && physical.valid == 1 && physical.dpi == 300,
        "physical calibration");
    calibrationDpiOverride = std::numeric_limits<float>::quiet_NaN();
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == -3 && physical.valid == 0 && physical.pixelWidth == 0,
        "invalid calibration clears prior result");
    calibrationDpiOverride = 0; calibrationThrows = true;
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == -3 && physical.valid == 0, "calibration exception");
    physical.version = 2;
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == -1, "ABI version check");
    std::puts("metadata_test pass"); return 0;
}
