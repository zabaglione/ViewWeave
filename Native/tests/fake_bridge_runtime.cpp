#include <windows.h>

static int g_initialDisplayCount = 1;
static int g_populatedDisplayCount = 1;

extern "C" __declspec(dllexport) bool initialize_bridge(const wchar_t*) { return true; }
extern "C" __declspec(dllexport) bool uninitialize_bridge() { return true; }
extern "C" __declspec(dllexport) bool get_bridge_version(unsigned long* major, unsigned long* minor, unsigned long* build, int* postfix, wchar_t*) {
    if (major) *major = 1;
    if (minor) *minor = 0;
    if (build) *build = 0;
    if (postfix) *postfix = 0;
    return true;
}
extern "C" __declspec(dllexport) void set_fake_display_counts(int initialCount, int populatedCount) {
    g_initialDisplayCount = initialCount;
    g_populatedDisplayCount = populatedCount;
}
extern "C" __declspec(dllexport) bool get_displays(int* count, unsigned long* ids) {
    if (!count) return false;
    if (!ids) {
        *count = g_initialDisplayCount;
        return true;
    }
    const int capacity = *count;
    const int actual = g_populatedDisplayCount;
    *count = actual;
    for (int index = 0; index < actual && index < capacity; ++index) ids[index] = static_cast<unsigned long>(100 + index);
    return true;
}
extern "C" __declspec(dllexport) bool get_dimensions_for_display(unsigned long, unsigned long* width, unsigned long* height) { if (width) *width = 1; if (height) *height = 1; return true; }
extern "C" __declspec(dllexport) bool get_viewcone_for_display(unsigned long, float* value) { if (value) *value = 40.0f; return true; }
extern "C" __declspec(dllexport) bool get_default_quilt_settings_for_display(unsigned long, float*, int*, int*, int*, int*) { return true; }
extern "C" __declspec(dllexport) bool get_window_position_for_display(unsigned long, long* x, long* y) { if (x) *x = 0; if (y) *y = 0; return true; }
extern "C" __declspec(dllexport) bool get_display_for_window(unsigned long, unsigned long* display) { if (display) *display = 0; return true; }
extern "C" __declspec(dllexport) bool release_device_dx(void*) { return true; }
extern "C" __declspec(dllexport) bool instance_window_dx(void*, unsigned long*, unsigned long) { return false; }
extern "C" __declspec(dllexport) bool show_window(unsigned long, bool) { return true; }
extern "C" __declspec(dllexport) bool register_texture_dx(unsigned long, void*) { return true; }
extern "C" __declspec(dllexport) bool unregister_texture_dx(unsigned long, void*) { return true; }
extern "C" __declspec(dllexport) bool draw_interop_quilt_texture_dx(unsigned long, void*, unsigned long, unsigned long, float, float) { return true; }
extern "C" __declspec(dllexport) bool create_texture_dx(unsigned long, unsigned long, unsigned long, void**) { return false; }
extern "C" __declspec(dllexport) bool copy_texture_dx(unsigned long, void*, void*) { return false; }
extern "C" __declspec(dllexport) bool release_texture_dx(unsigned long, void*) { return true; }
extern "C" __declspec(dllexport) bool save_texture_to_file_dx(unsigned long, wchar_t*, void*) { return false; }
