#include "bridge_shim.h"
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <mutex>
#include <atomic>
#include <string>
#include <cwchar>
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#include <unknwn.h>
#include <d3d11.h>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
using Library = HMODULE;
extern "C" int32_t lkg_worker_shutdown_host(uint32_t timeoutMilliseconds);
extern "C" int32_t lkg_worker_close_all(void);
extern "C" int32_t lkg_worker_collect_closed(void);
extern "C" uint32_t lkg_worker_pending_count(void);
#else
#include <dlfcn.h>
using Library = void*;
struct IUnknown;
#endif

using WindowHandle = unsigned long;
using Bool = bool;
using InitializeFn = Bool (*)(
#if defined(_WIN32)
    const wchar_t*
#else
    const char*
#endif
);
using UninitializeFn = Bool (*)();
using VersionFn = Bool (*)(unsigned long*, unsigned long*, unsigned long*, int*, wchar_t*);
using GetDisplaysFn = Bool (*)(int*, unsigned long*);
using GetDimensionsFn = Bool (*)(unsigned long, unsigned long*, unsigned long*);
using GetViewconeFn = Bool (*)(unsigned long, float*);
using GetDefaultQuiltFn = Bool (*)(unsigned long, float*, int*, int*, int*, int*);
using GetPositionDisplayFn = Bool (*)(unsigned long, long*, long*);
using GetWindowDisplayFn = Bool (*)(WindowHandle, unsigned long*);

#if defined(_WIN32)
using ReleaseDeviceFn = Bool (*)(IUnknown*);
using InstanceWindowFn = Bool (*)(IUnknown*, WindowHandle*, unsigned long);
using ShowWindowFn = Bool (*)(WindowHandle, Bool);
using RegisterTextureFn = Bool (*)(WindowHandle, IUnknown*);
using UnregisterTextureFn = Bool (*)(WindowHandle, IUnknown*);
using DrawTextureFn = Bool (*)(WindowHandle, IUnknown*, unsigned long, unsigned long, float, float);
using CreateTextureFn = Bool (*)(WindowHandle, unsigned long, unsigned long, IUnknown**);
using CopyTextureFn = Bool (*)(WindowHandle, IUnknown*, IUnknown*);
using ReleaseTextureFn = Bool (*)(WindowHandle, IUnknown*);
using SaveTextureFn = Bool (*)(WindowHandle, wchar_t*, IUnknown*);
#endif

struct Session {
    WindowHandle window{};
    IUnknown* device{};
    IUnknown* bridgeTexture{};
    uint32_t display{};
    uint32_t pending{};
    bool closing{};
    bool registered{};
    bool snapshotAttempted{};
};

static Library g_library{};
#if defined(_WIN32)
static std::wstring g_library_path;
#endif
static std::mutex g_mutex;
static std::string g_last_error;
static bool g_initialized = false;
static std::atomic<bool> g_worker_active{false};
static thread_local bool g_worker_scope = false;
static uint32_t g_successful_draws = 0;
static std::unordered_map<uint64_t, Session> g_sessions;
static uint64_t g_next_session = 1;
static IUnknown* g_unity_device = nullptr;
static IUnityGraphics* g_unity_graphics = nullptr;
static IUnityInterfaces* g_unity_interfaces = nullptr;
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
#if defined(_WIN32)
static ReleaseDeviceFn g_release_device{};
static InstanceWindowFn g_instance_window{};
static ShowWindowFn g_show_window{};
static RegisterTextureFn g_register_texture{};
static UnregisterTextureFn g_unregister_texture{};
static DrawTextureFn g_draw_texture{};
static CreateTextureFn g_create_texture{};
static CopyTextureFn g_copy_texture{};
static ReleaseTextureFn g_release_texture{};
static SaveTextureFn g_save_texture{};
#endif

IUnknown* lkg_bridge_unity_device_addref() {
#if defined(_WIN32)
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    if (!g_unity_device) return nullptr;
    g_unity_device->AddRef();
#endif
    return g_unity_device;
}

static bool direct_call_rejected() {
    return g_worker_active.load(std::memory_order_acquire) && !g_worker_scope;
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

static void tracef(const char* format, ...) {
    char buffer[512]{};
    va_list args;
    va_start(args, format);
    std::vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    trace(buffer);
}

static unsigned long trace_thread_id() {
#if defined(_WIN32)
    return static_cast<unsigned long>(GetCurrentThreadId());
#else
    return 0;
#endif
}

#if defined(_WIN32)
static bool IsR8G8B8A8TypeFamily(DXGI_FORMAT format) {
    return format == DXGI_FORMAT_R8G8B8A8_TYPELESS ||
           format == DXGI_FORMAT_R8G8B8A8_UNORM ||
           format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
}

static bool IsCopyCompatible(const D3D11_TEXTURE2D_DESC& source,
                             const D3D11_TEXTURE2D_DESC& destination) {
    const bool formatCompatible = source.Format == destination.Format ||
        (IsR8G8B8A8TypeFamily(source.Format) && IsR8G8B8A8TypeFamily(destination.Format));
    return formatCompatible && source.Width == destination.Width &&
           source.Height == destination.Height &&
           source.MipLevels == destination.MipLevels && source.ArraySize == destination.ArraySize &&
           source.SampleDesc.Count == destination.SampleDesc.Count &&
           source.SampleDesc.Quality == destination.SampleDesc.Quality;
}

static bool IsResizeCompatible(const D3D11_TEXTURE2D_DESC& source,
                               const D3D11_TEXTURE2D_DESC& destination) {
    const bool formatCompatible = source.Format == destination.Format ||
        (IsR8G8B8A8TypeFamily(source.Format) && IsR8G8B8A8TypeFamily(destination.Format));
    return formatCompatible && source.MipLevels == destination.MipLevels &&
           source.ArraySize == destination.ArraySize && source.SampleDesc.Count == destination.SampleDesc.Count &&
           source.SampleDesc.Quality == destination.SampleDesc.Quality;
}

static bool get_texture_desc(IUnknown* texture, D3D11_TEXTURE2D_DESC* desc) {
    if (!texture || !desc) return false;
    ID3D11Resource* resource = reinterpret_cast<ID3D11Resource*>(texture);
    D3D11_RESOURCE_DIMENSION dimension{};
    resource->GetType(&dimension);
    if (dimension != D3D11_RESOURCE_DIMENSION_TEXTURE2D) return false;
    ID3D11Texture2D* texture2d = nullptr;
    if (FAILED(resource->QueryInterface(__uuidof(ID3D11Texture2D),
                                        reinterpret_cast<void**>(&texture2d)))) return false;
    texture2d->GetDesc(desc);
    texture2d->Release();
    return true;
}

static void trace_desc(const char* label, const D3D11_TEXTURE2D_DESC& desc) {
    tracef("%s width=%u height=%u format=%u sample_count=%u sample_quality=%u",
           label, desc.Width, desc.Height, static_cast<unsigned>(desc.Format),
           desc.SampleDesc.Count, desc.SampleDesc.Quality);
}

static void release_bridge_texture(Session& session) {
    if (session.registered && g_unregister_texture && session.bridgeTexture) {
        trace("unregister_begin");
        const bool ok = g_unregister_texture(session.window, session.bridgeTexture);
        tracef("unregister_end result=%d", ok ? 1 : 0);
    }
    if (session.bridgeTexture && g_release_texture) {
        trace("release_texture");
        g_release_texture(session.window, session.bridgeTexture);
    }
    session.bridgeTexture = nullptr;
    session.registered = false;
}

static std::wstring snapshot_path() {
    const char* value = std::getenv("LKG_BRIDGE_SNAPSHOT");
    if (!value || !*value) return {};
    if (std::strcmp(value, "1") == 0 || std::strcmp(value, "true") == 0) {
        value = "bridge_snapshot.png";
    }
    const int length = MultiByteToWideChar(CP_UTF8, 0, value, -1, nullptr, 0);
    if (length <= 0) return {};
    std::wstring result(static_cast<size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value, -1, result.data(), length);
    result.resize(static_cast<size_t>(length - 1));
    return result;
}
#endif

template <class T>
static T sym(const char* name) {
#if defined(_WIN32)
    return g_library ? reinterpret_cast<T>(GetProcAddress(g_library, name)) : nullptr;
#else
    return g_library ? reinterpret_cast<T>(dlsym(g_library, name)) : nullptr;
#endif
}

static void clear_symbols() {
    g_initialize = nullptr; g_uninitialize = nullptr; g_version = nullptr;
    g_get_displays = nullptr; g_get_dimensions = nullptr; g_get_viewcone = nullptr;
    g_get_default_quilt = nullptr; g_get_position_display = nullptr; g_get_window_display = nullptr;
#if defined(_WIN32)
    g_release_device = nullptr; g_instance_window = nullptr; g_show_window = nullptr;
    g_register_texture = nullptr; g_unregister_texture = nullptr; g_draw_texture = nullptr;
    g_create_texture = nullptr; g_copy_texture = nullptr; g_release_texture = nullptr;
    g_save_texture = nullptr;
#endif
}

static bool required_symbols() {
    return g_initialize && g_uninitialize && g_version && g_get_displays && g_get_default_quilt &&
#if defined(_WIN32)
           g_release_device && g_instance_window && g_show_window && g_register_texture &&
           g_unregister_texture && g_draw_texture && g_create_texture && g_copy_texture &&
           g_release_texture;
#else
           true;
#endif
}

#if defined(_WIN32)
static bool canonical_library_path(const char* path, std::wstring* result) {
    if (!path || !result) return false;
    wchar_t input[32768]{};
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, input, static_cast<int>(sizeof(input) / sizeof(input[0])))) return false;
    wchar_t full[32768]{};
    const DWORD length = GetFullPathNameW(input, static_cast<DWORD>(sizeof(full) / sizeof(full[0])), full, nullptr);
    if (!length || length >= sizeof(full) / sizeof(full[0])) return false;
    *result = std::wstring(full, length);
    return true;
}

static bool bind_symbols() {
    g_initialize = sym<InitializeFn>("initialize_bridge"); g_uninitialize = sym<UninitializeFn>("uninitialize_bridge");
    g_version = sym<VersionFn>("get_bridge_version"); g_get_displays = sym<GetDisplaysFn>("get_displays");
    g_get_dimensions = sym<GetDimensionsFn>("get_dimensions_for_display"); g_get_viewcone = sym<GetViewconeFn>("get_viewcone_for_display");
    g_get_default_quilt = sym<GetDefaultQuiltFn>("get_default_quilt_settings_for_display");
    g_get_position_display = sym<GetPositionDisplayFn>("get_window_position_for_display");
    g_get_window_display = sym<GetWindowDisplayFn>("get_display_for_window");
    g_release_device = sym<ReleaseDeviceFn>("release_device_dx"); g_instance_window = sym<InstanceWindowFn>("instance_window_dx");
    g_show_window = sym<ShowWindowFn>("show_window"); g_register_texture = sym<RegisterTextureFn>("register_texture_dx");
    g_unregister_texture = sym<UnregisterTextureFn>("unregister_texture_dx"); g_draw_texture = sym<DrawTextureFn>("draw_interop_quilt_texture_dx");
    g_create_texture = sym<CreateTextureFn>("create_texture_dx"); g_copy_texture = sym<CopyTextureFn>("copy_texture_dx");
    g_release_texture = sym<ReleaseTextureFn>("release_texture_dx"); g_save_texture = sym<SaveTextureFn>("save_texture_to_file_dx");
    return required_symbols();
}
#endif

LKG_EXPORT int32_t lkg_bridge_load(const char* path) {
    if (direct_call_rejected()) return -6;
    if (!path || !*path) return -1;
    tracef("load_begin thread_id=%lu", trace_thread_id());
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_sessions.empty()) return -5;
#if defined(_WIN32)
    std::wstring canonicalPath;
    if (!canonical_library_path(path, &canonicalPath)) {
        g_last_error = "bridge_load UTF8 path conversion failed path=" + ascii_path(path);
        trace(g_last_error.c_str());
        return -2;
    }
    if (g_library) {
        if (_wcsicmp(g_library_path.c_str(), canonicalPath.c_str()) != 0) {
            g_last_error = "bridge_load different resident path requested path=" + ascii_path(path);
            trace(g_last_error.c_str());
            return -5;
        }
        if (!bind_symbols()) {
            clear_symbols();
            g_last_error = "bridge_load resident symbols unavailable path=" + ascii_path(path);
            trace(g_last_error.c_str());
            return -4;
        }
        tracef("load_resident_rebound thread_id=%lu", trace_thread_id());
        g_last_error.clear();
        return 0;
    }
    wchar_t wide[32768]{};
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, wide, 32768)) {
        g_last_error = "bridge_load UTF8 path conversion failed path=" + ascii_path(path);
        trace(g_last_error.c_str());
        return -2;
    }
    g_library = LoadLibraryExW(canonicalPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
#else
    if (g_library) return 0;
    g_library = dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
    if (!g_library) {
#if defined(_WIN32)
        const DWORD win32_error = GetLastError();
        g_last_error = "bridge_load LoadLibraryExW failed win32_error=" + std::to_string(static_cast<unsigned long>(win32_error)) + " path=" + ascii_path(path);
#else
        g_last_error = "bridge_load dlopen failed path=" + ascii_path(path);
#endif
        trace(g_last_error.c_str());
        return -3;
    }
#if defined(_WIN32)
    g_library_path = canonicalPath;
    if (!bind_symbols()) {
        clear_symbols();
        FreeLibrary(g_library);
        g_library = nullptr;
        g_library_path.clear();
        g_last_error = "bridge_load required symbols unavailable path=" + ascii_path(path);
        trace(g_last_error.c_str());
        return -4;
    }
#else
    g_initialize = sym<InitializeFn>("initialize_bridge"); g_uninitialize = sym<UninitializeFn>("uninitialize_bridge");
    g_version = sym<VersionFn>("get_bridge_version"); g_get_displays = sym<GetDisplaysFn>("get_displays");
    g_get_dimensions = sym<GetDimensionsFn>("get_dimensions_for_display"); g_get_viewcone = sym<GetViewconeFn>("get_viewcone_for_display");
    g_get_default_quilt = sym<GetDefaultQuiltFn>("get_default_quilt_settings_for_display");
    g_get_position_display = sym<GetPositionDisplayFn>("get_window_position_for_display");
    g_get_window_display = sym<GetWindowDisplayFn>("get_display_for_window");
    if (!required_symbols()) { dlclose(g_library); g_library = nullptr; clear_symbols(); return -4; }
#endif
    g_last_error.clear();
    tracef("load_end result=0 thread_id=%lu", trace_thread_id());
    return 0;
}

LKG_EXPORT void lkg_bridge_unload(void) {
    if (direct_call_rejected()) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_sessions.empty()) return;
#if defined(_WIN32)
    if (g_initialized && g_uninitialize) { g_uninitialize(); g_initialized = false; }
    clear_symbols();
#else
    if (g_initialized && g_uninitialize) { g_uninitialize(); g_initialized = false; }
    if (g_library) dlclose(g_library);
    g_library = nullptr;
    clear_symbols();
#endif
}

LKG_EXPORT int32_t lkg_bridge_initialize(const char* app) {
    if (direct_call_rejected()) return -6;
    if (!g_initialize || !app) return -1;
    tracef("initialize_begin thread_id=%lu", trace_thread_id());
#if defined(_WIN32)
    wchar_t wide[1024]{};
    if (!MultiByteToWideChar(CP_UTF8, 0, app, -1, wide, 1024)) return -2;
    g_initialized = g_initialize(wide);
#else
    g_initialized = g_initialize(app);
#endif
    const int32_t result = g_initialized ? 0 : -3;
    tracef("initialize_end result=%d thread_id=%lu", result, trace_thread_id());
    if (result != 0) { g_last_error = "Bridge initialize returned false"; }
    return result;
}

LKG_EXPORT void lkg_bridge_uninitialize(void) {
    if (direct_call_rejected()) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_sessions.empty()) return;
    tracef("uninitialize_begin thread_id=%lu", trace_thread_id());
    bool result = true;
    if (g_uninitialize && g_initialized) { result = g_uninitialize(); g_initialized = false; }
    tracef("uninitialize_end result=%d thread_id=%lu", result ? 1 : 0, trace_thread_id());
    if (!result) g_last_error = "Bridge uninitialize returned false";
}
LKG_EXPORT int32_t lkg_bridge_is_idle(void) { std::lock_guard<std::mutex> lock(g_mutex); return g_sessions.empty() ? 1 : 0; }
LKG_EXPORT int32_t lkg_bridge_last_error(char* out, uint32_t capacity) { if (direct_call_rejected()) return -6; std::lock_guard<std::mutex> lock(g_mutex); if (out && capacity) std::snprintf(out, capacity, "%s", g_last_error.c_str()); return static_cast<int32_t>(g_last_error.size()); }
LKG_EXPORT uint32_t lkg_bridge_successful_draw_count(void) { std::lock_guard<std::mutex> lock(g_mutex); return g_successful_draws; }
LKG_EXPORT int32_t lkg_bridge_is_initialized(void) { return direct_call_rejected() ? -6 : (g_initialized ? 1 : 0); }

LKG_EXPORT int32_t lkg_bridge_version(char* out, uint32_t capacity) {
    if (!g_version) return -1;
    unsigned long major = 0, minor = 0, build = 0; int postfix = 0;
    if (!g_version(&major, &minor, &build, &postfix, nullptr)) return -2;
    const std::string value = std::to_string(major) + "." + std::to_string(minor) + "." + std::to_string(build);
    if (out && capacity) std::snprintf(out, capacity, "%s", value.c_str());
    return static_cast<int32_t>(value.size());
}

LKG_EXPORT int32_t lkg_bridge_get_display_count(uint32_t* count) {
    if (direct_call_rejected()) return -6;
    if (!g_get_displays || !count) return -1;
    int number = 0; if (!g_get_displays(&number, nullptr) || number < 0) return -2;
    *count = static_cast<uint32_t>(number); return 0;
}

LKG_EXPORT int32_t lkg_bridge_get_display(uint32_t ordinal, LkgBridgeDisplayInfo* info) {
    if (direct_call_rejected()) return -6;
    if (!g_get_displays || !info) return -1;
    int number = 0; if (!g_get_displays(&number, nullptr) || ordinal >= static_cast<uint32_t>(number)) return -2;
    std::vector<unsigned long> ids(static_cast<size_t>(number)); if (!g_get_displays(&number, ids.data())) return -3;
    const unsigned long id = ids[ordinal]; std::memset(info, 0, sizeof(*info)); info->index = static_cast<uint32_t>(id);
    if (g_get_dimensions) { unsigned long width = 0, height = 0; if (g_get_dimensions(id, &width, &height)) { info->width = static_cast<uint32_t>(width); info->height = static_cast<uint32_t>(height); } }
    if (g_get_viewcone) g_get_viewcone(id, &info->viewcone);
    if (g_get_default_quilt) g_get_default_quilt(id, &info->aspect, &info->quilt_width, &info->quilt_height, &info->quilt_columns, &info->quilt_rows);
    return 0;
}

#if defined(_WIN32)
LKG_EXPORT uint64_t lkg_bridge_create_d3d11_session(void* device, uint32_t display) {
    if (direct_call_rejected()) return 0;
    if (!device) device = g_unity_device;
    if (!device || !g_instance_window || !g_show_window) { g_last_error = "Unity D3D11 device unavailable"; return 0; }
    tracef("instance_window_display=%u", display);
    if (g_get_position_display) { long x = 0, y = 0; if (g_get_position_display(display, &x, &y)) tracef("display_position=%ld,%ld", x, y); }
    trace("instance_window_main_begin");
    IUnknown* dx = reinterpret_cast<IUnknown*>(device); dx->AddRef(); WindowHandle window = 0;
    const bool created = g_instance_window(dx, &window, display);
    tracef("instance_window_end result=%d thread_id=%lu", created ? 1 : 0, trace_thread_id());
    if (!created) { dx->Release(); g_last_error = "Bridge window creation failed"; trace("instance_window_main_fail"); return 0; }
    trace("instance_window_main_end");
    const bool shown = g_show_window(window, true); tracef("show_window=true result=%d", shown ? 1 : 0);
    if (!shown) { dx->Release(); g_last_error = "Bridge window show failed"; return 0; }
    unsigned long actual = 0;
    if (g_get_window_display && (!g_get_window_display(window, &actual) || actual != display)) { dx->Release(); g_last_error = "Bridge window display mismatch"; tracef("window_display_mismatch expected=%u actual=%lu", display, actual); return 0; }
    std::lock_guard<std::mutex> lock(g_mutex); const uint64_t id = g_next_session++;
    g_sessions.emplace(id, Session{window, dx, nullptr, display, 0, false, false, false}); return id;
}

LKG_EXPORT int32_t lkg_bridge_destroy_session(uint64_t id) { if (direct_call_rejected()) return -6; std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(id); if (it == g_sessions.end()) return -1; it->second.closing = true; return 0; }

LKG_EXPORT int32_t lkg_bridge_smoke_draw(uint64_t id, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom) {
    trace("draw_enter"); std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(id);
    if (it == g_sessions.end() || !texture || !g_draw_texture || !g_register_texture) { g_last_error = "invalid draw session or texture"; return -1; }
    Session& session = it->second; if (!session.window) { g_last_error = "Bridge session has no window"; return -4; }
    D3D11_TEXTURE2D_DESC source{}; if (!get_texture_desc(reinterpret_cast<IUnknown*>(texture), &source)) { g_last_error = "Source is not a D3D11 texture2D"; return -2; }
    trace_desc("source_desc", source);
    if (!session.bridgeTexture) { trace("create_texture_begin"); if (!g_create_texture(session.window, source.Width, source.Height, &session.bridgeTexture)) { g_last_error = "Bridge compatible texture creation failed"; return -2; } trace("create_texture_end"); }
    D3D11_TEXTURE2D_DESC destination{}; if (!get_texture_desc(session.bridgeTexture, &destination)) { g_last_error = "Bridge texture descriptor query failed"; release_bridge_texture(session); return -2; }
    trace_desc("dest_desc", destination);
    if (!IsCopyCompatible(source, destination)) {
        if ((source.Width != destination.Width || source.Height != destination.Height) && IsResizeCompatible(source, destination)) {
            trace("resize_bridge_texture_begin");
            release_bridge_texture(session);
            if (!g_create_texture(session.window, source.Width, source.Height, &session.bridgeTexture)) {
                g_last_error = "Bridge compatible texture resize failed";
                return -2;
            }
            session.registered = false;
            if (!get_texture_desc(session.bridgeTexture, &destination) || !IsCopyCompatible(source, destination)) {
                g_last_error = "Resized Bridge texture descriptors are incompatible";
                release_bridge_texture(session);
                return -2;
            }
            trace("resize_bridge_texture_end");
        } else {
            trace("copy_validation_failed"); g_last_error = "Source and Bridge texture descriptors are incompatible"; release_bridge_texture(session); return -2;
        }
    }
    trace("copy_begin"); if (!g_copy_texture(session.window, reinterpret_cast<IUnknown*>(texture), session.bridgeTexture)) { g_last_error = "Bridge texture copy failed"; trace("copy_failed"); return -2; } trace("copy_end");
    if (!session.registered) { trace("register_begin"); if (!g_register_texture(session.window, session.bridgeTexture)) { g_last_error = "Bridge texture registration failed"; return -2; } session.registered = true; trace("register_end"); }
    if (!session.snapshotAttempted) { session.snapshotAttempted = true; std::wstring snapshot = snapshot_path(); if (!snapshot.empty() && g_save_texture) { std::vector<wchar_t> path(snapshot.begin(), snapshot.end()); path.push_back(L'\0'); const bool saved = g_save_texture(session.window, path.data(), session.bridgeTexture); tracef("snapshot_save result=%d", saved ? 1 : 0); } }
    trace("draw_begin"); const bool ok = g_draw_texture(session.window, session.bridgeTexture, columns, rows, aspect, zoom); tracef("draw_end result=%d", ok ? 1 : 0); trace("draw_exit");
    if (!ok) { g_last_error = "Bridge quilt draw failed"; return -3; } g_last_error.clear(); ++g_successful_draws; return 0;
}

static void lkg_render_event(int eventId, void* data) {
    if (!data) return; auto* drawData = static_cast<LkgBridgeDrawData*>(data);
    if (eventId == 1) {
        trace("close_begin"); std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(drawData->session);
        if (it != g_sessions.end() && it->second.pending == 0) {
            Session& session = it->second;
            release_bridge_texture(session); if (g_release_device && session.device) { g_release_device(session.device); session.device = nullptr; }
            g_sessions.erase(it); if (g_sessions.empty() && g_initialized) {
                tracef("uninitialize_begin thread_id=%lu", trace_thread_id());
                const bool result = g_uninitialize ? g_uninitialize() : true;
                g_initialized = false;
                tracef("uninitialize_end result=%d thread_id=%lu", result ? 1 : 0, trace_thread_id());
                if (!result) g_last_error = "Bridge uninitialize returned false";
            }
        }
        trace("close_end"); delete drawData; return;
    }
    lkg_bridge_smoke_draw(drawData->session, drawData->texture, drawData->columns, drawData->rows, drawData->aspect, drawData->zoom);
    { std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(drawData->session); if (it != g_sessions.end() && it->second.pending) --it->second.pending; }
    if (drawData->texture) reinterpret_cast<IUnknown*>(drawData->texture)->Release(); delete drawData;
}
#else
LKG_EXPORT uint64_t lkg_bridge_create_d3d11_session(void*, uint32_t) { return 0; }
LKG_EXPORT int32_t lkg_bridge_destroy_session(uint64_t) { return -2; }
LKG_EXPORT int32_t lkg_bridge_smoke_draw(uint64_t, void*, uint32_t, uint32_t, float, float) { return -2; }
static void lkg_render_event(int, void*) {}
#endif

LKG_EXPORT void* lkg_bridge_render_event_func(void) { if (direct_call_rejected()) return nullptr; return reinterpret_cast<void*>(&lkg_render_event); }
LKG_EXPORT void* lkg_bridge_make_draw_data(uint64_t session, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom) {
    if (!texture) return nullptr;
    { std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(session); if (it == g_sessions.end() || it->second.closing) return nullptr; ++it->second.pending; }
#if defined(_WIN32)
    reinterpret_cast<IUnknown*>(texture)->AddRef();
#endif
    return new LkgBridgeDrawData{session, texture, columns, rows, aspect, zoom};
}
LKG_EXPORT void* lkg_bridge_make_close_data(uint64_t session) {
    if (direct_call_rejected()) return nullptr;
    std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(session); if (it == g_sessions.end()) return nullptr; it->second.closing = true; return new LkgBridgeDrawData{session, nullptr, 0, 0, 0, 0};
}

#if defined(_WIN32)
static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType) {
    if (eventType == kUnityGfxDeviceEventInitialize) {
        std::lock_guard<std::mutex> lock(g_unity_device_mutex);
        if (g_unity_device) { g_unity_device->Release(); g_unity_device = nullptr; }
        IUnityGraphicsD3D11* d3d11 = g_unity_interfaces ? g_unity_interfaces->Get<IUnityGraphicsD3D11>() : nullptr;
        if (d3d11) {
            IUnknown* device = reinterpret_cast<IUnknown*>(d3d11->GetDevice());
            if (device) {
                device->AddRef();
                g_unity_device = device;
            }
        }
        return;
    }
    if (eventType != kUnityGfxDeviceEventShutdown) return;
    lkg_worker_close_all();
    const ULONGLONG deadline = GetTickCount64() + 1000;
    while (lkg_worker_pending_count() != 0 && GetTickCount64() < deadline) {
        Sleep(1);
    }
    {
        std::lock_guard<std::mutex> lock(g_unity_device_mutex);
        if (g_unity_device) { g_unity_device->Release(); g_unity_device = nullptr; }
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    if (!interfaces) return;
    g_unity_interfaces = interfaces;
    g_unity_graphics = interfaces->Get<IUnityGraphics>();
    if (g_unity_graphics) g_unity_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventShutdown);
    if (g_unity_graphics) g_unity_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_unity_graphics = nullptr;
    lkg_worker_shutdown_host(1000);
    lkg_worker_collect_closed();
    g_unity_interfaces = nullptr;
}
#endif
