#include "common/bridge_runtime_api.h"
#include "common/bridge_metadata.h"
using namespace lkg::metadata;
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <mutex>
#include <atomic>
#include <cmath>
#include <string>
#include <cwchar>
#include <vector>

#include "IUnityInterface.h"
#include "platform/macos/metal_provider_api.h"
#include "platform/macos/metal_display.h"
#include <dlfcn.h>
using Library = void*;

using WindowHandle = unsigned long;
using Bool = bool;
using InitializeFn = Bool (*)(
    const char*
);
using UninitializeFn = Bool (*)();
using GetPositionDisplayFn = Bool (*)(unsigned long, long*, long*);
using GetWindowDisplayFn = Bool (*)(WindowHandle, unsigned long*);


static Library g_library{};
static std::mutex g_mutex;
static std::string g_last_error;
static bool g_initialized = false;
static std::atomic<bool> g_worker_active{false};
static thread_local bool g_worker_scope = false;
static std::atomic<uint32_t> g_unity_graphics_api{0};
static std::atomic<uint64_t> g_unity_device_generation{0};
static int32_t g_worker_render_event_id = 0;
static bool g_worker_render_event_id_reserved = false;
static std::mutex g_unity_device_mutex;


static std::string ascii_path(const char* path) {
    if (!path) return "";
    std::string result;
    for (const unsigned char* cursor = reinterpret_cast<const unsigned char*>(path); *cursor; ++cursor)
        result.push_back(*cursor >= 0x20 && *cursor <= 0x7e ? static_cast<char>(*cursor) : '?');
    return result;
}
static InitializeFn g_initialize{};
static UninitializeFn g_uninitialize{};
static VersionFn g_version{};
static GetDisplaysFn g_get_displays{};
static GetDimensionsFn g_get_dimensions{};
static GetViewconeFn g_get_viewcone{};
static GetDefaultQuiltFn g_get_default_quilt{};
static GetPositionDisplayFn g_get_position_display{};
static GetWindowDisplayFn g_get_window_display{};
static GetCalibrationFn g_get_calibration{};


uint32_t lkg_bridge_graphics_api() {
    return g_unity_graphics_api.load(std::memory_order_acquire);
}

extern "C" uint64_t lkg_bridge_unity_device_generation() {
    return g_unity_device_generation.load(std::memory_order_acquire);
}


extern "C" int32_t lkg_bridge_get_worker_render_event_id(int32_t* eventId) {
    if (!eventId) return -1;
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    if (!g_worker_render_event_id_reserved) return -2;
    *eventId = g_worker_render_event_id;
    return 0;
}


static bool direct_call_rejected() {
    if (g_worker_active.load(std::memory_order_acquire) && !g_worker_scope) return true;
    return false;
}

bool lkg_bridge_worker_claim() {
    bool expected = false;
    return g_worker_active.compare_exchange_strong(expected, true, std::memory_order_acq_rel);
}

void lkg_bridge_worker_release_claim() {
    g_worker_scope = false;
    g_worker_active.store(false, std::memory_order_release);
}

void lkg_bridge_worker_enter() { g_worker_scope = true; }
void lkg_bridge_worker_leave() { g_worker_scope = false; }

static void trace(const char* message) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    std::ofstream file(path, std::ios::app);
    file << message << '\n';
    file.flush();
}

static bool trace_enabled() {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    return path && *path;
}

static void tracef(const char* format, ...) {
    char buffer[512]{};
    va_list args;
    va_start(args, format);
    std::vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    trace(buffer);
}

extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void) { return 0; }
extern "C" int32_t lkg_bridge_close_sdk_admission(void) { return 0; }
extern "C" int32_t lkg_bridge_sdk_shutdown_incomplete(void) { return 0; }
static bool has_sdk_ownership() { return true; }
static bool sdk_admission_is_closed() { return false; }

static unsigned long trace_thread_id() {
    return 0;
}

template <class T>
static T sym(const char* name) {
    return g_library ? reinterpret_cast<T>(dlsym(g_library, name)) : nullptr;
}

static void clear_symbols() {
    g_initialize = nullptr; g_uninitialize = nullptr; g_version = nullptr;
    g_get_displays = nullptr; g_get_dimensions = nullptr; g_get_viewcone = nullptr; g_get_calibration = nullptr;
    g_get_default_quilt = nullptr; g_get_position_display = nullptr; g_get_window_display = nullptr;
}

static bool required_symbols() {
    return g_initialize && g_uninitialize && g_version && g_get_displays && g_get_default_quilt;
}


LKG_EXPORT int32_t lkg_bridge_load(const char* path) {
    if (direct_call_rejected()) return -6;
    if (!path || !*path) return -1;
    tracef("load_begin thread_id=%lu", trace_thread_id());
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_library) return 0;
    g_library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!g_library) {
        g_last_error = "bridge_load dlopen failed path=" + ascii_path(path);
        trace(g_last_error.c_str());
        return -3;
    }
    g_initialize = sym<InitializeFn>("initialize_bridge"); g_uninitialize = sym<UninitializeFn>("uninitialize_bridge");
    g_version = sym<VersionFn>("get_bridge_version"); g_get_displays = sym<GetDisplaysFn>("get_displays");
    g_get_calibration = sym<GetCalibrationFn>("get_calibration_for_display");
    g_get_dimensions = sym<GetDimensionsFn>("get_dimensions_for_display"); g_get_viewcone = sym<GetViewconeFn>("get_viewcone_for_display");
    g_get_default_quilt = sym<GetDefaultQuiltFn>("get_default_quilt_settings_for_display");
    g_get_position_display = sym<GetPositionDisplayFn>("get_window_position_for_display");
    g_get_window_display = sym<GetWindowDisplayFn>("get_display_for_window");
    if (!required_symbols()) { dlclose(g_library); g_library = nullptr; clear_symbols(); return -4; }
    g_last_error.clear();
    tracef("load_end result=0 thread_id=%lu", trace_thread_id());
    return 0;
}

LKG_EXPORT void lkg_bridge_unload(void) {
    if (direct_call_rejected()) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_initialized && g_uninitialize) { g_uninitialize(); g_initialized = false; }
    if (g_library) dlclose(g_library);
    g_library = nullptr;
    clear_symbols();
}

LKG_EXPORT int32_t lkg_bridge_initialize(const char* app) {
    if (direct_call_rejected()) return -6;
    if (!g_initialize || !app) return -1;
    tracef("initialize_begin thread_id=%lu", trace_thread_id());
    bool initialized = false;
    initialized = g_initialize(app);
    g_initialized = initialized;
    const int32_t result = g_initialized ? 0 : -3;
    tracef("initialize_end result=%d thread_id=%lu", result, trace_thread_id());
    if (result != 0) { g_last_error = "Bridge initialize returned false"; }
    return result;
}

LKG_EXPORT void lkg_bridge_uninitialize(void) {
    if (direct_call_rejected()) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    tracef("uninitialize_begin thread_id=%lu", trace_thread_id());
    bool result = true;
    if (g_uninitialize && g_initialized) {
        result = g_uninitialize();
        g_initialized = false;
    }
    tracef("uninitialize_end result=%d thread_id=%lu", result ? 1 : 0, trace_thread_id());
    if (!result) g_last_error = "Bridge uninitialize returned false";
}
LKG_EXPORT int32_t lkg_bridge_is_idle(void) { std::lock_guard<std::mutex> lock(g_mutex); return 1; }
LKG_EXPORT int32_t lkg_bridge_last_error(char* out, uint32_t capacity) { if (direct_call_rejected()) return -6; std::lock_guard<std::mutex> lock(g_mutex); if (out && capacity) std::snprintf(out, capacity, "%s", g_last_error.c_str()); return static_cast<int32_t>(g_last_error.size()); }
LKG_EXPORT uint32_t lkg_bridge_successful_draw_count(void) { std::lock_guard<std::mutex> lock(g_mutex); return 0; }
LKG_EXPORT int32_t lkg_bridge_is_initialized(void) { return direct_call_rejected() ? -6 : (g_initialized ? 1 : 0); }

static lkg::metadata::Symbols metadata_symbols() {
    return {g_version, g_get_displays, g_get_dimensions, g_get_viewcone, g_get_default_quilt, g_get_calibration};
}
LKG_EXPORT int32_t lkg_bridge_version(char* out, uint32_t capacity) {
    if (direct_call_rejected()) return -6;
    return lkg::metadata::ReadVersion(metadata_symbols(), out, capacity);
}
LKG_EXPORT int32_t lkg_bridge_get_display_count(uint32_t* count) {
    if (direct_call_rejected()) return -6;
    if (!has_sdk_ownership() || !g_initialized) return -7;
    return lkg::metadata::ReadDisplayCount(metadata_symbols(), count);
}
LKG_EXPORT int32_t lkg_bridge_get_display(uint32_t ordinal, LkgBridgeDisplayInfo* info) {
    if (direct_call_rejected()) return -6;
    if (!has_sdk_ownership() || !g_initialized) return -7;
    return lkg::metadata::ReadDisplay(metadata_symbols(), ordinal, info);
}
LKG_EXPORT int32_t lkg_bridge_get_physical_display_v1(uint32_t displayIndex, LkgBridgePhysicalDisplayInfoV1* info) {
    if (direct_call_rejected()) return -6;
    if (!info || info->structSize != sizeof(LkgBridgePhysicalDisplayInfoV1) || info->version != 1) return -1;
    // Preserve admission-before-query behavior, including zeroed rejected results.
    info->pixelWidth = 0; info->pixelHeight = 0; info->dpi = 0.0f; info->valid = 0;
    if (!has_sdk_ownership() || !g_initialized) return -7;
    return lkg::metadata::ReadPhysicalDisplay(metadata_symbols(), displayIndex, info);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    lkg_metal_unity_plugin_load(interfaces);
    lkg_metal_display_plugin_load(interfaces);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    lkg_metal_unity_plugin_unload();
    lkg_metal_display_plugin_unload();
}
