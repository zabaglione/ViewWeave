#include "bridge_shim.h"
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
#include <unordered_map>
#include <vector>

#if defined(_WIN32)
#include <windows.h>
#include <unknwn.h>
#include <d3d11.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "IUnityGraphicsD3D12.h"
#include "bridge_d3d12_internal.h"
#include "bridge_worker_transport.h"
using Library = HMODULE;
extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void);
#elif defined(__APPLE__)
#include "IUnityInterface.h"
#include "platform/macos/metal_provider_api.h"
#include <dlfcn.h>
using Library = void*;
struct IUnknown;
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
struct CalibrationSubpixelCell { float ROffsetX, ROffsetY, GOffsetX, GOffsetY, BOffsetX, BOffsetY; };
using GetCalibrationFn = Bool (*)(unsigned long, float*, float*, float*, int*, int*, float*, float*, int*, float*, float*, int*, int*, CalibrationSubpixelCell*);

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
    bool cleanupFailed{};
};

#if defined(_WIN32)
struct SessionCloseRecord {
    uint64_t sessionId{};
    uint32_t state{LKG_BRIDGE_SESSION_CLOSE_UNKNOWN};
    int32_t lastResult{};
    IUnknown* retainedDevice{};
    IUnknown* retainedTexture{};
    bool occupied{};
};

static std::mutex g_session_close_ledger_mutex;
static SessionCloseRecord g_session_close_ledger[LKG_BRIDGE_SESSION_CLOSE_LEDGER_CAPACITY]{};
static uint32_t g_session_close_ledger_count = 0;
#endif

static Library g_library{};
#if defined(_WIN32)
static std::wstring g_library_path;
#endif
static std::mutex g_mutex;
static std::string g_last_error;
static bool g_initialized = false;
#if defined(_WIN32)
static std::mutex g_sdk_ownership_mutex;
static HANDLE g_sdk_ownership_handle = nullptr;
static bool g_sdk_ownership_faulted = false;
static bool g_sdk_initialization_attempted = false;
static bool g_sdk_admission_closed = false;
#endif
static std::atomic<bool> g_worker_active{false};
static thread_local bool g_worker_scope = false;
static uint32_t g_successful_draws = 0;
static std::unordered_map<uint64_t, Session> g_sessions;
static std::atomic<uint64_t> g_next_session{1};
#if defined(_WIN32)
static IUnknown* g_unity_device = nullptr;
#endif
static std::atomic<uint32_t> g_unity_graphics_api{0};
static std::atomic<uint64_t> g_unity_device_generation{0};
static int32_t g_worker_render_event_id = 0;
static bool g_worker_render_event_id_reserved = false;
#if defined(_WIN32)
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
static IUnityGraphicsD3D12v8* g_unity_d3d12_v8 = nullptr;
#endif
static IUnityGraphicsD3D12v7* g_unity_d3d12_v7 = nullptr;
static ID3D12Device* g_unity_d3d12_device = nullptr;
static IUnityGraphics* g_unity_graphics = nullptr;
static IUnityInterfaces* g_unity_interfaces = nullptr;
#endif
static std::mutex g_unity_device_mutex;

#if defined(_WIN32)
static SessionCloseRecord* find_session_close_record_locked(uint64_t sessionId) {
    for (SessionCloseRecord& record : g_session_close_ledger) {
        if (record.occupied && record.sessionId == sessionId) return &record;
    }
    return nullptr;
}

static bool reserve_session_close_record(uint64_t* sessionId) {
    if (!sessionId) return false;
    std::lock_guard<std::mutex> lock(g_session_close_ledger_mutex);
    if (g_session_close_ledger_count >= LKG_BRIDGE_SESSION_CLOSE_LEDGER_CAPACITY) return false;
    SessionCloseRecord* freeRecord = nullptr;
    for (SessionCloseRecord& record : g_session_close_ledger) {
        if (!record.occupied) { freeRecord = &record; break; }
    }
    if (!freeRecord) return false;
    uint64_t nextId = g_next_session.load(std::memory_order_relaxed);
    for (;;) {
        if (nextId == 0 || nextId == UINT64_MAX) return false;
        if (g_next_session.compare_exchange_weak(nextId, nextId + 1,
                std::memory_order_relaxed, std::memory_order_relaxed)) break;
    }
    *freeRecord = SessionCloseRecord{};
    freeRecord->sessionId = nextId;
    freeRecord->state = LKG_BRIDGE_SESSION_CLOSE_PENDING;
    freeRecord->occupied = true;
    ++g_session_close_ledger_count;
    *sessionId = nextId;
    return true;
}

static void mark_session_close_safe(uint64_t sessionId, int32_t result) {
    std::lock_guard<std::mutex> lock(g_session_close_ledger_mutex);
    SessionCloseRecord* record = find_session_close_record_locked(sessionId);
    if (!record || record->state != LKG_BRIDGE_SESSION_CLOSE_PENDING) return;
    record->state = LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE;
    record->lastResult = result;
}

static void mark_session_close_failed(uint64_t sessionId, int32_t result,
                                      IUnknown* retainedDevice, IUnknown* retainedTexture,
                                      uint32_t state = LKG_BRIDGE_SESSION_CLOSE_FAILED_RETAINED) {
    std::lock_guard<std::mutex> lock(g_session_close_ledger_mutex);
    SessionCloseRecord* record = find_session_close_record_locked(sessionId);
    if (!record || record->state != LKG_BRIDGE_SESSION_CLOSE_PENDING) return;
    record->state = state;
    record->lastResult = result;
    record->retainedDevice = retainedDevice;
    record->retainedTexture = retainedTexture;
}

#endif

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

#if defined(_WIN32)
IUnknown* lkg_bridge_unity_device_addref() {
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    if (!g_unity_device) return nullptr;
    g_unity_device->AddRef();
    return g_unity_device;
}
#endif

uint32_t lkg_bridge_graphics_api() {
    return g_unity_graphics_api.load(std::memory_order_acquire);
}

extern "C" uint64_t lkg_bridge_unity_device_generation() {
    return g_unity_device_generation.load(std::memory_order_acquire);
}

#if defined(_WIN32)
extern "C" int32_t lkg_bridge_unity_device_snapshot(IUnknown** device, uint64_t* generation, uint32_t* graphicsApi) {
    if (!device || !generation || !graphicsApi) return -1;
    *device = nullptr;
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    if (!g_unity_device) return -2;
    g_unity_device->AddRef();
    *device = g_unity_device;
    *generation = g_unity_device_generation.load(std::memory_order_relaxed);
    *graphicsApi = g_unity_graphics_api.load(std::memory_order_relaxed);
    return 0;
}
#endif

extern "C" int32_t lkg_bridge_get_worker_render_event_id(int32_t* eventId) {
    if (!eventId) return -1;
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    if (!g_worker_render_event_id_reserved) return -2;
    *eventId = g_worker_render_event_id;
    return 0;
}

#if defined(_WIN32)
extern "C" int32_t lkg_bridge_d3d12_snapshot(LkgD3D12Snapshot* snapshot) {
    if (!snapshot) return -1;
    snapshot->device = nullptr;
    snapshot->frameFence = nullptr;
    // This accessor is render-thread-only because Unity permits GetFrameFence there.
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    snapshot->generation = g_unity_device_generation.load(std::memory_order_relaxed);
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
    IUnityGraphicsD3D12v8* v8 = g_unity_d3d12_v8;
#endif
    ID3D12Device* device = g_unity_d3d12_device;
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
    ID3D12Fence* fence = v8 ? v8->GetFrameFence() : (g_unity_d3d12_v7 ? g_unity_d3d12_v7->GetFrameFence() : nullptr);
#else
    ID3D12Fence* fence = g_unity_d3d12_v7 ? g_unity_d3d12_v7->GetFrameFence() : nullptr;
#endif
    if (!device || !fence) return -2;
    fence->AddRef();
    device->AddRef();
    snapshot->device = device;
    snapshot->frameFence = fence;
    return 0;
}

extern "C" void lkg_bridge_d3d12_snapshot_release(LkgD3D12Snapshot* snapshot) {
    if (!snapshot) return;
    if (snapshot->frameFence) snapshot->frameFence->Release();
    if (snapshot->device) snapshot->device->Release();
    snapshot->frameFence = nullptr;
    snapshot->device = nullptr;
}

extern "C" uint64_t lkg_bridge_d3d12_submit(ID3D12GraphicsCommandList* list, ID3D12Resource* source) {
    if (!list || !source) return 0;
    // Called only from the configured Unity render callback.
    std::lock_guard<std::mutex> lock(g_unity_device_mutex);
    UnityGraphicsD3D12ResourceState state{source, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_COPY_SOURCE};
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
    if (g_unity_d3d12_v8) return g_unity_d3d12_v8->ExecuteCommandList(list, 1, &state);
#endif
    if (g_unity_d3d12_v7) return g_unity_d3d12_v7->ExecuteCommandList(list, 1, &state);
    return 0;
}
#endif

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

#if defined(_WIN32)
static bool acquire_sdk_ownership() {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    if (g_sdk_admission_closed || g_sdk_ownership_faulted || g_sdk_initialization_attempted || g_sdk_ownership_handle) return false;
    wchar_t name[96]{};
    swprintf_s(name, L"Local\\ViewWeaveBridgeSdk-%lu", static_cast<unsigned long>(GetCurrentProcessId()));
    SetLastError(ERROR_SUCCESS);
    HANDLE handle = CreateMutexW(nullptr, FALSE, name);
    if (!handle) return false;
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(handle);
        return false;
    }
    g_sdk_ownership_handle = handle;
    g_sdk_initialization_attempted = true;
    return true;
}

static void release_sdk_ownership_after_success() {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    if (g_sdk_ownership_handle) {
        CloseHandle(g_sdk_ownership_handle);
        g_sdk_ownership_handle = nullptr;
    }
    g_sdk_initialization_attempted = false;
}

static void retain_sdk_ownership_fault() {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    g_sdk_ownership_faulted = true;
    g_sdk_initialization_attempted = true;
}

static bool has_sdk_ownership() {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    return g_sdk_ownership_handle != nullptr && !g_sdk_ownership_faulted;
}

extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void) {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    return g_sdk_ownership_faulted ? 1 : 0;
}
extern "C" int32_t lkg_bridge_close_sdk_admission(void) {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    g_sdk_admission_closed = true;
    return 0;
}
extern "C" int32_t lkg_bridge_sdk_shutdown_incomplete(void) {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    return (g_sdk_initialization_attempted || g_sdk_ownership_handle != nullptr) ? 1 : 0;
}
static bool sdk_admission_is_closed() {
    std::lock_guard<std::mutex> lock(g_sdk_ownership_mutex);
    return g_sdk_admission_closed;
}
#else
extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void) { return 0; }
extern "C" int32_t lkg_bridge_close_sdk_admission(void) { return 0; }
extern "C" int32_t lkg_bridge_sdk_shutdown_incomplete(void) { return 0; }
static bool has_sdk_ownership() { return true; }
static bool sdk_admission_is_closed() { return false; }
#endif

static unsigned long trace_thread_id() {
#if defined(_WIN32)
    return static_cast<unsigned long>(GetCurrentThreadId());
#else
    return 0;
#endif
}

#if defined(_WIN32)
static void trace_shutdownf(uint64_t sessionId, const char* format, ...) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    char event[512]{};
    va_list args;
    va_start(args, format);
    std::vsnprintf(event, sizeof(event), format, args);
    va_end(args);
    char line[768]{};
    std::snprintf(line, sizeof(line),
                  "shutdown pid=%lu tid=%lu tick_ms=%llu session_id=%llu event=%s",
                  static_cast<unsigned long>(GetCurrentProcessId()), trace_thread_id(),
                  static_cast<unsigned long long>(GetTickCount64()),
                  static_cast<unsigned long long>(sessionId), event);
    trace(line);
}

static bool uninitialize_sdk_owned(uint64_t sessionId, const char* source) {
    {
        std::lock_guard<std::mutex> ownershipLock(g_sdk_ownership_mutex);
        if (!g_sdk_ownership_handle || g_sdk_ownership_faulted) {
            trace_shutdownf(sessionId, "uninitialize rejected source=%s owner=%d fault=%d",
                source ? source : "unknown", g_sdk_ownership_handle ? 1 : 0, g_sdk_ownership_faulted ? 1 : 0);
            return false;
        }
    }
    bool result = false;
    try {
        trace_shutdownf(sessionId, "uninitialize_bridge begin source=%s", source ? source : "unknown");
        result = g_uninitialize && g_uninitialize();
    } catch (...) {
        trace_shutdownf(sessionId, "uninitialize_bridge exception source=%s", source ? source : "unknown");
        result = false;
    }
    trace_shutdownf(sessionId, "uninitialize_bridge end result=%d source=%s", result ? 1 : 0, source ? source : "unknown");
    g_initialized = false;
    if (result) {
        release_sdk_ownership_after_success();
    } else {
        retain_sdk_ownership_fault();
    }
    return result;
}
#endif

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

static bool release_bridge_texture(uint64_t sessionId, Session& session, bool shutdownTrace) {
    if (session.registered && session.bridgeTexture) {
        if (!g_unregister_texture) {
            session.cleanupFailed = true;
            return false;
        }
        trace("unregister_begin");
#if defined(_WIN32)
        if (shutdownTrace) trace_shutdownf(sessionId, "unregister_texture_dx begin");
#endif
        bool ok = false;
        try { ok = g_unregister_texture(session.window, session.bridgeTexture); }
        catch (...) { ok = false; }
        tracef("unregister_end result=%d", ok ? 1 : 0);
#if defined(_WIN32)
        if (shutdownTrace) trace_shutdownf(sessionId, "unregister_texture_dx end result=%d", ok ? 1 : 0);
#endif
        if (!ok) { session.cleanupFailed = true; return false; }
        session.registered = false;
    } else {
#if defined(_WIN32)
        if (shutdownTrace) trace_shutdownf(sessionId,
            "unregister_texture_dx skipped registered=%d texture=%d symbol=%d",
            session.registered ? 1 : 0, session.bridgeTexture ? 1 : 0, g_unregister_texture ? 1 : 0);
#endif
        if (session.registered) { session.cleanupFailed = true; return false; }
    }
    if (session.bridgeTexture && g_release_texture) {
        trace("release_texture");
#if defined(_WIN32)
        if (shutdownTrace) trace_shutdownf(sessionId, "release_texture_dx begin");
#endif
        bool released = false;
        try { released = g_release_texture(session.window, session.bridgeTexture); }
        catch (...) { released = false; }
#if defined(_WIN32)
        if (shutdownTrace) trace_shutdownf(sessionId, "release_texture_dx end result=%d", released ? 1 : 0);
#endif
        if (!released) { session.cleanupFailed = true; return false; }
    } else if (session.bridgeTexture) {
        session.cleanupFailed = true;
        return false;
    } else {
#if defined(_WIN32)
        if (shutdownTrace) trace_shutdownf(sessionId,
            "release_texture_dx skipped texture=%d symbol=%d",
            session.bridgeTexture ? 1 : 0, g_release_texture ? 1 : 0);
#endif
    }
    session.bridgeTexture = nullptr;
    session.registered = false;
    return true;
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
    g_get_displays = nullptr; g_get_dimensions = nullptr; g_get_viewcone = nullptr; g_get_calibration = nullptr;
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
    g_get_calibration = sym<GetCalibrationFn>("get_calibration_for_display");
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
    g_get_calibration = sym<GetCalibrationFn>("get_calibration_for_display");
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
    if (g_initialized && g_uninitialize) {
        uninitialize_sdk_owned(0, "bridge_unload");
    } else {
        trace_shutdownf(0, "uninitialize_bridge skipped initialized=%d symbol=%d source=bridge_unload",
                        g_initialized ? 1 : 0, g_uninitialize ? 1 : 0);
    }
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
#if defined(_WIN32)
    if (!acquire_sdk_ownership()) {
        g_last_error = "Bridge SDK process ownership unavailable";
        trace_shutdownf(0, "initialize rejected sdk_owner=0");
        return -7;
    }
#endif
    tracef("initialize_begin thread_id=%lu", trace_thread_id());
    bool initialized = false;
#if defined(_WIN32)
    wchar_t wide[1024]{};
    if (!MultiByteToWideChar(CP_UTF8, 0, app, -1, wide, 1024)) {
        release_sdk_ownership_after_success();
        return -2;
    }
    try { initialized = g_initialize(wide); }
    catch (...) { initialized = false; }
#else
    initialized = g_initialize(app);
#endif
    g_initialized = initialized;
    const int32_t result = g_initialized ? 0 : -3;
    tracef("initialize_end result=%d thread_id=%lu", result, trace_thread_id());
#if defined(_WIN32)
    if (result != 0) retain_sdk_ownership_fault();
#endif
    if (result != 0) { g_last_error = "Bridge initialize returned false"; }
    return result;
}

LKG_EXPORT void lkg_bridge_uninitialize(void) {
    if (direct_call_rejected()) return;
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_sessions.empty()) return;
    tracef("uninitialize_begin thread_id=%lu", trace_thread_id());
    bool result = true;
    if (g_uninitialize && g_initialized) {
#if defined(_WIN32)
        result = uninitialize_sdk_owned(0, "bridge_uninitialize");
#else
        result = g_uninitialize();
        g_initialized = false;
#endif
    } else {
#if defined(_WIN32)
        trace_shutdownf(0, "uninitialize_bridge skipped initialized=%d symbol=%d source=bridge_uninitialize",
                        g_initialized ? 1 : 0, g_uninitialize ? 1 : 0);
#endif
    }
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
    if (!has_sdk_ownership() || !g_initialized) return -7;
    if (!g_get_displays || !count) return -1;
    int number = 0; if (!g_get_displays(&number, nullptr) || number < 0) return -2;
    *count = static_cast<uint32_t>(number); return 0;
}

LKG_EXPORT int32_t lkg_bridge_get_display(uint32_t ordinal, LkgBridgeDisplayInfo* info) {
    if (direct_call_rejected()) return -6;
    if (!has_sdk_ownership() || !g_initialized) return -7;
    if (!g_get_displays || !info) return -1;
    int number = 0; if (!g_get_displays(&number, nullptr) || ordinal >= static_cast<uint32_t>(number)) return -2;
    std::vector<unsigned long> ids(static_cast<size_t>(number)); if (!g_get_displays(&number, ids.data())) return -3;
    const unsigned long id = ids[ordinal]; std::memset(info, 0, sizeof(*info)); info->index = static_cast<uint32_t>(id);
    if (g_get_dimensions) { unsigned long width = 0, height = 0; if (g_get_dimensions(id, &width, &height)) { info->width = static_cast<uint32_t>(width); info->height = static_cast<uint32_t>(height); } }
    if (g_get_viewcone) g_get_viewcone(id, &info->viewcone);
    if (g_get_default_quilt) g_get_default_quilt(id, &info->aspect, &info->quilt_width, &info->quilt_height, &info->quilt_columns, &info->quilt_rows);
    return 0;
}

LKG_EXPORT int32_t lkg_bridge_get_physical_display_v1(uint32_t displayIndex, LkgBridgePhysicalDisplayInfoV1* info) {
    if (direct_call_rejected()) return -6;
    if (!info || info->structSize != sizeof(LkgBridgePhysicalDisplayInfoV1) || info->version != 1) return -1;
    info->pixelWidth = 0;
    info->pixelHeight = 0;
    info->dpi = 0.0f;
    info->valid = 0;
    if (!has_sdk_ownership() || !g_initialized) return -7;
    if (!g_get_calibration) return -2;
    float center = 0.0f, pitch = 0.0f, slope = 0.0f, dpi = 0.0f, flipX = 0.0f, viewcone = 0.0f, fringe = 0.0f;
    int width = 0, height = 0, inverted = 0, cellPatternMode = 0, numberOfCells = 0;
    try {
        if (!g_get_calibration(static_cast<unsigned long>(displayIndex), &center, &pitch, &slope, &width, &height, &dpi, &flipX,
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

#if defined(_WIN32)
LKG_EXPORT uint64_t lkg_bridge_create_d3d11_session(void* device, uint32_t display) {
    if (direct_call_rejected()) return 0;
    if (sdk_admission_is_closed()) { g_last_error = "Bridge SDK admission is closed"; return 0; }
    if (!has_sdk_ownership() || !g_initialized) { g_last_error = "Bridge SDK process ownership unavailable"; return 0; }
    if (!device) device = g_unity_device;
    if (!device || !g_instance_window || !g_show_window) { g_last_error = "Unity D3D11 device unavailable"; return 0; }
    uint64_t id = 0;
    if (!reserve_session_close_record(&id)) {
        g_last_error = "Bridge session close ledger is full";
        return 0;
    }
    tracef("instance_window_display=%u", display);
    if (g_get_position_display) { long x = 0, y = 0; if (g_get_position_display(display, &x, &y)) tracef("display_position=%ld,%ld", x, y); }
    trace("instance_window_main_begin");
    IUnknown* dx = reinterpret_cast<IUnknown*>(device); dx->AddRef(); WindowHandle window = 0;
    bool created = false;
    try { created = g_instance_window(dx, &window, display); }
    catch (...) { created = false; }
    tracef("instance_window_end result=%d thread_id=%lu", created ? 1 : 0, trace_thread_id());
    if (!created) {
        mark_session_close_failed(id, -2, dx, nullptr, LKG_BRIDGE_SESSION_CLOSE_UNKNOWN);
        g_last_error = "Bridge window creation failed"; trace("instance_window_main_fail"); return 0;
    }
    trace("instance_window_main_end");
    bool shown = false;
    try { shown = g_show_window(window, true); }
    catch (...) { shown = false; }
    tracef("show_window=true result=%d", shown ? 1 : 0);
    if (!shown) {
        mark_session_close_failed(id, -3, dx, nullptr, LKG_BRIDGE_SESSION_CLOSE_UNKNOWN);
        g_last_error = "Bridge window show failed"; return 0;
    }
    unsigned long actual = 0;
    if (g_get_window_display) {
        bool matched = false;
        try { matched = g_get_window_display(window, &actual) && actual == display; }
        catch (...) { matched = false; }
        if (!matched) {
            mark_session_close_failed(id, -4, dx, nullptr, LKG_BRIDGE_SESSION_CLOSE_UNKNOWN);
            g_last_error = "Bridge window display mismatch";
            tracef("window_display_mismatch expected=%u actual=%lu", display, actual);
            return 0;
        }
    }
    try {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_sessions.emplace(id, Session{window, dx, nullptr, display, 0, false, false, false, false});
    } catch (...) {
        mark_session_close_failed(id, -5, dx, nullptr, LKG_BRIDGE_SESSION_CLOSE_UNKNOWN);
        g_last_error = "Bridge session allocation failed";
        return 0;
    }
    return id;
}

LKG_EXPORT int32_t lkg_bridge_destroy_session(uint64_t id) { if (direct_call_rejected()) return -6; if (!has_sdk_ownership()) return -7; std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(id); if (it == g_sessions.end()) return -1; it->second.closing = true; return 0; }

LKG_EXPORT int32_t lkg_bridge_get_session_close_status_v1(uint64_t id, LkgBridgeSessionCloseStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgBridgeSessionCloseStatusV1) || status->version != 1) return -1;
#if defined(_WIN32)
    std::lock_guard<std::mutex> lock(g_session_close_ledger_mutex);
    const SessionCloseRecord* record = find_session_close_record_locked(id);
    status->structSize = sizeof(LkgBridgeSessionCloseStatusV1);
    status->version = 1;
    status->sessionId = id;
    status->state = record ? record->state : LKG_BRIDGE_SESSION_CLOSE_UNKNOWN;
    status->lastResult = record ? record->lastResult : -2;
    status->reserved0 = 0;
    status->reserved1 = 0;
    return record ? 0 : -2;
#else
    status->structSize = sizeof(LkgBridgeSessionCloseStatusV1);
    status->version = 1;
    status->sessionId = id;
    status->state = LKG_BRIDGE_SESSION_CLOSE_UNKNOWN;
    status->lastResult = -2;
    status->reserved0 = 0;
    status->reserved1 = 0;
    return -2;
#endif
}

LKG_EXPORT int32_t lkg_bridge_ack_session_close_v1(uint64_t id) {
#if defined(_WIN32)
    std::lock_guard<std::mutex> lock(g_session_close_ledger_mutex);
    SessionCloseRecord* record = find_session_close_record_locked(id);
    if (!record) return -2;
    if (record->state != LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE) return -3;
    if (record->retainedDevice || record->retainedTexture) return -4;
    *record = SessionCloseRecord{};
    if (g_session_close_ledger_count) --g_session_close_ledger_count;
    return 0;
#else
    (void)id;
    return -2;
#endif
}

LKG_EXPORT int32_t lkg_bridge_smoke_draw(uint64_t id, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom) {
    if (!has_sdk_ownership() || !g_initialized) return -7;
    trace("draw_enter"); std::lock_guard<std::mutex> lock(g_mutex); auto it = g_sessions.find(id);
    if (it == g_sessions.end() || !texture || !g_draw_texture || !g_register_texture) { g_last_error = "invalid draw session or texture"; return -1; }
    Session& session = it->second; if (!session.window) { g_last_error = "Bridge session has no window"; return -4; }
    D3D11_TEXTURE2D_DESC source{}; if (!get_texture_desc(reinterpret_cast<IUnknown*>(texture), &source)) { g_last_error = "Source is not a D3D11 texture2D"; return -2; }
    trace_desc("source_desc", source);
    if (!session.bridgeTexture) { trace("create_texture_begin"); if (!g_create_texture(session.window, source.Width, source.Height, &session.bridgeTexture)) { g_last_error = "Bridge compatible texture creation failed"; return -2; } trace("create_texture_end"); }
    D3D11_TEXTURE2D_DESC destination{}; if (!get_texture_desc(session.bridgeTexture, &destination)) { g_last_error = "Bridge texture descriptor query failed"; release_bridge_texture(id, session, false); return -2; }
    trace_desc("dest_desc", destination);
    if (!IsCopyCompatible(source, destination)) {
        if ((source.Width != destination.Width || source.Height != destination.Height) && IsResizeCompatible(source, destination)) {
            trace("resize_bridge_texture_begin");
            release_bridge_texture(id, session, false);
            if (!g_create_texture(session.window, source.Width, source.Height, &session.bridgeTexture)) {
                g_last_error = "Bridge compatible texture resize failed";
                return -2;
            }
            session.registered = false;
            if (!get_texture_desc(session.bridgeTexture, &destination) || !IsCopyCompatible(source, destination)) {
                g_last_error = "Resized Bridge texture descriptors are incompatible";
                release_bridge_texture(id, session, false);
                return -2;
            }
            trace("resize_bridge_texture_end");
        } else {
            trace("copy_validation_failed"); g_last_error = "Source and Bridge texture descriptors are incompatible"; release_bridge_texture(id, session, false); return -2;
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
            IUnknown* retainedDevice = session.device;
            IUnknown* retainedTexture = session.bridgeTexture;
            if (retainedDevice) retainedDevice->AddRef();
            if (retainedTexture) retainedTexture->AddRef();

            bool cleanupSafe = !session.cleanupFailed && release_bridge_texture(drawData->session, session, true);
            if (cleanupSafe && session.device) {
                if (g_release_device) {
                    trace_shutdownf(drawData->session, "release_device_dx begin");
                    bool released = false;
                    try { released = g_release_device(session.device); }
                    catch (...) { released = false; }
                    trace_shutdownf(drawData->session, "release_device_dx end result=%d", released ? 1 : 0);
                    if (released) session.device = nullptr;
                    else cleanupSafe = false;
                } else {
                    trace_shutdownf(drawData->session, "release_device_dx skipped device=1 symbol=0");
                    cleanupSafe = false;
                }
            }
            const bool lastSession = g_sessions.size() == 1;
            if (cleanupSafe && lastSession && g_initialized) {
                tracef("uninitialize_begin thread_id=%lu", trace_thread_id());
                bool result = false;
                if (g_uninitialize) result = uninitialize_sdk_owned(drawData->session, "last_session_close");
                else trace_shutdownf(drawData->session, "uninitialize_bridge skipped symbol=0");
                tracef("uninitialize_end result=%d thread_id=%lu", result ? 1 : 0, trace_thread_id());
                if (!result) {
                    cleanupSafe = false;
                    g_last_error = "Bridge uninitialize returned false";
                }
            }
            g_sessions.erase(it);
            if (cleanupSafe) {
                if (retainedTexture) retainedTexture->Release();
                if (retainedDevice) retainedDevice->Release();
                mark_session_close_safe(drawData->session, 0);
            } else {
                mark_session_close_failed(drawData->session, -5, retainedDevice, retainedTexture);
                trace_shutdownf(drawData->session, "session_close_failed resources_retained=1");
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
LKG_EXPORT int32_t lkg_bridge_get_session_close_status_v1(uint64_t id, LkgBridgeSessionCloseStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgBridgeSessionCloseStatusV1) || status->version != 1) return -1;
    status->structSize = sizeof(LkgBridgeSessionCloseStatusV1);
    status->version = 1;
    status->sessionId = id;
    status->state = LKG_BRIDGE_SESSION_CLOSE_UNKNOWN;
    status->lastResult = -2;
    status->reserved0 = 0;
    status->reserved1 = 0;
    return -2;
}
LKG_EXPORT int32_t lkg_bridge_ack_session_close_v1(uint64_t) { return -2; }
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
        if (!g_worker_render_event_id_reserved && g_unity_graphics && g_unity_graphics->ReserveEventIDRange) {
            g_worker_render_event_id = g_unity_graphics->ReserveEventIDRange(1);
            g_worker_render_event_id_reserved = true;
        }
        if (g_unity_device) { g_unity_device->Release(); g_unity_device = nullptr; }
        if (g_unity_d3d12_device) { g_unity_d3d12_device->Release(); g_unity_d3d12_device = nullptr; }
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
        g_unity_d3d12_v8 = nullptr;
#endif
        g_unity_d3d12_v7 = nullptr;
        g_unity_graphics_api = 0;
        g_unity_device_generation.fetch_add(1, std::memory_order_release);
        if (g_unity_graphics && g_unity_interfaces) {
            const UnityGfxRenderer renderer = g_unity_graphics->GetRenderer();
            if (renderer == kUnityGfxRendererD3D11) {
                IUnityGraphicsD3D11* d3d11 = g_unity_interfaces->Get<IUnityGraphicsD3D11>();
                IUnknown* device = d3d11 ? reinterpret_cast<IUnknown*>(d3d11->GetDevice()) : nullptr;
                if (device) {
                    device->AddRef();
                    g_unity_device = device;
                    g_unity_graphics_api = 1;
                }
            } else if (renderer == kUnityGfxRendererD3D12) {
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
                g_unity_d3d12_v8 = g_unity_interfaces->Get<IUnityGraphicsD3D12v8>();
                if (!g_unity_d3d12_v8) g_unity_d3d12_v7 = g_unity_interfaces->Get<IUnityGraphicsD3D12v7>();
                ID3D12Device* device = g_unity_d3d12_v8 ? g_unity_d3d12_v8->GetDevice() : (g_unity_d3d12_v7 ? g_unity_d3d12_v7->GetDevice() : nullptr);
#else
                g_unity_d3d12_v7 = g_unity_interfaces->Get<IUnityGraphicsD3D12v7>();
                ID3D12Device* device = g_unity_d3d12_v7 ? g_unity_d3d12_v7->GetDevice() : nullptr;
#endif
                if (device) {
                    device->AddRef();
                    g_unity_d3d12_device = device;
                    g_unity_device = reinterpret_cast<IUnknown*>(device);
                    g_unity_device->AddRef();
                    g_unity_graphics_api = 2;
                    UnityD3D12PluginEventConfig config{};
                    config.graphicsQueueAccess = kUnityD3D12GraphicsQueueAccess_DontCare;
                    config.flags = kUnityD3D12EventConfigFlag_SyncWorkerThreads;
                    config.ensureActiveRenderTextureIsBound = false;
                    if (g_worker_render_event_id_reserved) {
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
                        if (g_unity_d3d12_v8) g_unity_d3d12_v8->ConfigureEvent(g_worker_render_event_id, &config);
                        else g_unity_d3d12_v7->ConfigureEvent(g_worker_render_event_id, &config);
#else
                        g_unity_d3d12_v7->ConfigureEvent(g_worker_render_event_id, &config);
#endif
                    }
                }
            }
        }
        return;
    }
    if (eventType != kUnityGfxDeviceEventShutdown) return;
    lkg_worker_close_all();
    const ULONGLONG deadline = GetTickCount64() + 1000;
    while (lkg_worker_pending_count() != 0 && GetTickCount64() < deadline) {
        lkg_worker_collect_closed();
        Sleep(1);
    }
    lkg_worker_collect_closed();
    {
        std::lock_guard<std::mutex> lock(g_unity_device_mutex);
        if (g_unity_device) { g_unity_device->Release(); g_unity_device = nullptr; }
        if (g_unity_d3d12_device) { g_unity_d3d12_device->Release(); g_unity_d3d12_device = nullptr; }
#if defined(LKG_UNITY_GRAPHICS_D3D12_V8)
        g_unity_d3d12_v8 = nullptr;
#endif
        g_unity_d3d12_v7 = nullptr;
        g_unity_graphics_api = 0;
        g_unity_device_generation.fetch_add(1, std::memory_order_release);
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
    const int32_t requestResult = lkg_worker_request_final_shutdown();
    LkgWorkerFinalShutdownStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    const int32_t waitResult = requestResult == 0 ? lkg_worker_wait_final_shutdown_v1(5000, &status) : -2;
    if (waitResult == 0 && status.state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE) {
        trace_shutdownf(0, "final_shutdown result=complete registered=%u closed_uncollected=%u host_running=%u host_joined=%u",
            status.registeredContexts, status.closedUncollectedContexts, status.hostRunning, status.hostJoined);
    } else {
        trace_shutdownf(0, "final_shutdown result=failed request_result=%d wait_result=%d state=%u registered=%u closed_uncollected=%u host_running=%u host_joined=%u last_result=%d",
            requestResult, waitResult, status.state, status.registeredContexts, status.closedUncollectedContexts,
            status.hostRunning, status.hostJoined, status.lastResult);
    }
    g_unity_interfaces = nullptr;
}
#elif defined(__APPLE__)
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces) {
    lkg_metal_unity_plugin_load(interfaces);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload() {
    lkg_metal_unity_plugin_unload();
}
#endif
