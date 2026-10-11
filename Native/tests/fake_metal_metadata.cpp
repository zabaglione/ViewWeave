// Test runtime intentionally has no get_device_serial_for_display export.
#include <stdint.h>
extern "C" {
bool initialize_bridge(const char*) { return true; }
bool uninitialize_bridge() { return true; }
bool get_displays(int* count, unsigned long* indices) {
    if (indices) { indices[0] = 0; indices[1] = 1; indices[2] = 2; }
    *count = 3; return true;
}
bool get_dimensions_for_display(unsigned long, unsigned long* width, unsigned long* height) {
    *width = 17; *height = 23; return true;
}
bool get_window_position_for_display(unsigned long, long* x, long* y) { *x = 123456; *y = 123456; return true; }
bool get_default_quilt_settings_for_display(unsigned long, float* aspect, int* width, int* height, int* columns, int* rows) {
    *aspect = 17.0f / 23; *width = 4096; *height = 4096; *columns = 8; *rows = 6; return true;
}
bool get_ri_for_display(unsigned long index, int* value) { *value = index == 1 ? 2 : 0; return true; }
bool get_bi_for_display(unsigned long index, int* value) { *value = index == 1 ? 0 : 2; return true; }
bool get_calibration_for_display(unsigned long index, float* center, float* pitch, float* slope, int* width, int* height,
    float* dpi, float* flip, int* invert, float* cone, float* fringe, int* mode, int* cells, void*) {
    *center = 0.17f; *pitch = 45; *slope = -7.5f; *width = 17; *height = 23;
    *dpi = 300; *flip = index == 1 ? 1 : 0; *invert = 0; *cone = 40; *fringe = 0;
    *mode = index == 2 ? 99 : 0; *cells = 0; return true;
}
}
