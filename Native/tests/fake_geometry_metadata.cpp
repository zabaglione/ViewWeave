#include <cstring>
#include <thread>
#include <chrono>
#include <cstdlib>
static bool initFailure = false, queryFailure = false, shutdownFailure = false;
static bool dimensionsFailure = false, coneFailure = false, quiltFailure = false;
extern "C" {
bool initialize_bridge(const char* name) {
    initFailure = std::strstr(name, "init-failure"); queryFailure = std::strstr(name, "query-failure");
    shutdownFailure = std::strstr(name, "shutdown-failure");
    dimensionsFailure = std::strstr(name, "dimensions-failure"); coneFailure = std::strstr(name, "cone-failure");
    quiltFailure = std::strstr(name, "quilt-failure");
    if (std::strstr(name, "slow")) std::this_thread::sleep_for(std::chrono::milliseconds(100));
    return !initFailure;
}
bool uninitialize_bridge() { return !shutdownFailure; }
bool get_displays(int* count, unsigned long* indices) {
    if (queryFailure) return false;
    if (indices) { indices[0] = 17; indices[1] = 29; }
    *count = 2; return true;
}
bool get_dimensions_for_display(unsigned long index, unsigned long* width, unsigned long* height) {
    *width = index == 17 ? 1440 : 3840; *height = index == 17 ? 2560 : 2160; return !dimensionsFailure;
}
bool get_viewcone_for_display(unsigned long, float* cone) { *cone = 40; return !coneFailure; }
bool get_default_quilt_settings_for_display(unsigned long index, float* aspect, int* width, int* height, int* columns, int* rows) {
    *aspect = index == 17 ? 9.0f / 16 : 16.0f / 9; *width = 4092; *height = 4096; *columns = 11; *rows = 6; return !quiltFailure;
}
bool get_calibration_for_display(unsigned long index, float*, float*, float*, int* width, int* height,
    float* dpi, float*, int*, float*, float*, int* mode, int* cells, void* cellTable) {
    // Geometry acquisition must not request subpixel payloads or depend on their layout.
    if (cellTable) std::abort();
    *width = index == 17 ? 1440 : 3840; *height = index == 17 ? 2560 : 2160;
    *dpi = 300; *mode = 99; *cells = 4; return true;
}
}
