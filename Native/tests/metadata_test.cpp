#include "common/bridge_metadata.h"
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>

namespace {
int declaredCount = 2, returnedCount = 2;
bool calibrationSucceeds = true, calibrationThrows = false;
float calibrationDpi = 300;
void Check(bool condition, const char* label) {
    if (condition) return;
    std::fprintf(stderr, "FAIL %s\n", label); std::exit(1);
}
bool Version(unsigned long* major, unsigned long* minor, unsigned long* build, int*, wchar_t*) {
    *major = 2; *minor = 6; *build = 3; return true;
}
bool Displays(int* count, unsigned long* ids) {
    if (!ids) { *count = declaredCount; return true; }
    // Model a display list changing between count and list queries without overrunning storage.
    if (*count >= 1) ids[0] = 7;
    if (*count >= 2) ids[1] = 42;
    *count = returnedCount; return true;
}
bool Dimensions(unsigned long id, unsigned long* w, unsigned long* h) {
    *w = id == 42 ? 1440 : 1920; *h = id == 42 ? 2560 : 1080; return true;
}
bool Viewcone(unsigned long, float* v) { *v = 54; return true; }
bool Quilt(unsigned long, float* aspect, int* w, int* h, int* columns, int* rows) {
    *aspect = .5625f; *w = *h = 4092; *columns = 11; *rows = 6; return true;
}
bool Calibration(unsigned long, float*, float*, float*, int* w, int* h, float* dpi,
    float*, int*, float*, float*, int*, int* cells, lkg::metadata::CalibrationSubpixelCell*) {
    if (calibrationThrows) throw 1;
    *w = 1440; *h = 2560; *dpi = calibrationDpi; *cells = 0; return calibrationSucceeds;
}
}
int main() {
    const lkg::metadata::Symbols api{Version, Displays, Dimensions, Viewcone, Quilt, Calibration};
    char version[16]{};
    Check(lkg::metadata::ReadVersion(api, version, sizeof(version)) == 5 && !std::strcmp(version, "2.6.3"), "version conversion");
    Check(lkg::metadata::ReadVersion({}, version, sizeof(version)) == -1, "missing version symbol");
    uint32_t count = 0;
    Check(lkg::metadata::ReadDisplayCount(api, &count) == 0 && count == 2, "display count");
    LkgBridgeDisplayInfo display{};
    Check(lkg::metadata::ReadDisplay(api, 1, &display) == 0 && display.index == 42 && display.width == 1440 &&
        display.height == 2560 && display.quilt_columns == 11 && display.quilt_rows == 6, "ordinal maps to physical ID");
    Check(lkg::metadata::ReadDisplay(api, 2, &display) == -2, "invalid ordinal");
    returnedCount = 3;
    Check(lkg::metadata::ReadDisplay(api, 1, &display) == -3, "growing list rejects stale capacity");
    returnedCount = 1;
    Check(lkg::metadata::ReadDisplay(api, 1, &display) == -3, "shrinking list rejects removed ordinal");
    returnedCount = 2; declaredCount = 257;
    Check(lkg::metadata::ReadDisplay(api, 0, &display) == -2, "bounded enumeration");
    LkgBridgePhysicalDisplayInfoV1 physical{sizeof(physical), 1, 0, 0, 0, 0};
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == 0 && physical.valid == 1 && physical.dpi == 300, "physical calibration");
    calibrationDpi = std::numeric_limits<float>::quiet_NaN();
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == -3 && physical.valid == 0 && physical.pixelWidth == 0, "invalid calibration clears prior result");
    calibrationDpi = 300; calibrationThrows = true;
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == -3 && physical.valid == 0, "calibration exception");
    physical.version = 2;
    Check(lkg::metadata::ReadPhysicalDisplay(api, 42, &physical) == -1, "ABI version check");
    std::puts("metadata_test pass"); return 0;
}
