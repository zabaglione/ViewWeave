#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <unknwn.h>
#include <algorithm>
#include <mutex>
#include <stdexcept>
#include <vector>
#include "bridge_shutdown_contract.h"

namespace {
std::mutex g_mutex;
std::vector<LkgFakeBridgeEvent> g_events;
ID3D11Device* g_device = nullptr;
uint64_t g_generation = 0;
uint64_t g_sequence = 0;
bool g_failDraw = false;
bool g_blockDraw = false;
bool g_failUninitialize = false;
bool g_blockUninitialize = false;
bool g_allowEmptyUninitialize = false;
bool g_throwUninitialize = false;
HANDLE g_drawEntered = nullptr;
HANDLE g_drawRelease = nullptr;
HANDLE g_uninitializeEntered = nullptr;
HANDLE g_uninitializeRelease = nullptr;

void Record(uint32_t type, const void* pointerValue, int32_t result) {
    std::lock_guard<std::mutex> lock(g_mutex);
    LkgFakeBridgeEvent event{};
    event.sequence = ++g_sequence;
    event.threadId = GetCurrentThreadId();
    event.generation = g_generation;
    event.pointerValue = reinterpret_cast<uint64_t>(pointerValue);
    event.type = type;
    event.result = result;
    g_events.push_back(event);
}
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_reset() {
    ID3D11Device* device = nullptr;
    HANDLE entered = nullptr;
    HANDLE release = nullptr;
    HANDLE uninitializeEntered = nullptr;
    HANDLE uninitializeRelease = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_events.clear();
        g_sequence = 0;
        g_generation = 0;
        g_failDraw = false;
        g_blockDraw = false;
        g_failUninitialize = false;
        g_blockUninitialize = false;
        g_allowEmptyUninitialize = false;
        g_throwUninitialize = false;
        device = g_device;
        g_device = nullptr;
        entered = g_drawEntered;
        release = g_drawRelease;
        uninitializeEntered = g_uninitializeEntered;
        uninitializeRelease = g_uninitializeRelease;
        g_drawEntered = nullptr;
        g_drawRelease = nullptr;
        g_uninitializeEntered = nullptr;
        g_uninitializeRelease = nullptr;
    }
    if (device) device->Release();
    if (entered) CloseHandle(entered);
    if (release) CloseHandle(release);
    if (uninitializeEntered) CloseHandle(uninitializeEntered);
    if (uninitializeRelease) CloseHandle(uninitializeRelease);
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_set_draw_mode(int32_t failDraw, int32_t blockDraw) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_failDraw = failDraw != 0;
    g_blockDraw = blockDraw != 0;
    if (g_drawEntered) ResetEvent(g_drawEntered);
    if (g_drawRelease) {
        if (g_blockDraw) ResetEvent(g_drawRelease);
        else SetEvent(g_drawRelease);
    }
}

extern "C" __declspec(dllexport) int32_t __cdecl lkg_fake_bridge_wait_draw_entered(uint32_t timeoutMilliseconds) {
    return g_drawEntered && WaitForSingleObject(g_drawEntered, timeoutMilliseconds) == WAIT_OBJECT_0 ? 1 : 0;
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_release_draw() {
    if (g_drawRelease) SetEvent(g_drawRelease);
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_set_uninitialize_mode(int32_t failUninitialize, int32_t blockUninitialize) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_failUninitialize = failUninitialize != 0;
    g_blockUninitialize = blockUninitialize != 0;
    if (g_uninitializeEntered) ResetEvent(g_uninitializeEntered);
    if (g_uninitializeRelease) {
        if (g_blockUninitialize) ResetEvent(g_uninitializeRelease);
        else SetEvent(g_uninitializeRelease);
    }
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_set_empty_uninitialize(int32_t allowEmptyUninitialize) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_allowEmptyUninitialize = allowEmptyUninitialize != 0;
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_set_uninitialize_throw(int32_t throwUninitialize) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_throwUninitialize = throwUninitialize != 0;
}

extern "C" __declspec(dllexport) int32_t __cdecl lkg_fake_bridge_wait_uninitialize_entered(uint32_t timeoutMilliseconds) {
    return g_uninitializeEntered && WaitForSingleObject(g_uninitializeEntered, timeoutMilliseconds) == WAIT_OBJECT_0 ? 1 : 0;
}

extern "C" __declspec(dllexport) void __cdecl lkg_fake_bridge_release_uninitialize() {
    if (g_uninitializeRelease) SetEvent(g_uninitializeRelease);
}

extern "C" __declspec(dllexport) uint32_t __cdecl lkg_fake_bridge_get_events(LkgFakeBridgeEvent* events, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const uint32_t count = static_cast<uint32_t>(g_events.size());
    if (events && capacity) {
        const uint32_t copied = (std::min)(count, capacity);
        std::copy_n(g_events.begin(), copied, events);
    }
    return count;
}

extern "C" __declspec(dllexport) bool initialize_bridge(const wchar_t*) {
    if (!g_drawEntered) g_drawEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_drawRelease) g_drawRelease = CreateEventW(nullptr, TRUE, TRUE, nullptr);
    if (!g_drawEntered || !g_drawRelease) return false;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        ++g_generation;
    }
    Record(LKG_FAKE_EVENT_INITIALIZE, nullptr, 1);
    return true;
}

extern "C" __declspec(dllexport) bool uninitialize_bridge() {
    bool failUninitialize = false;
    bool blockUninitialize = false;
    bool allowEmptyUninitialize = false;
    bool throwUninitialize = false;
    HANDLE entered = nullptr;
    HANDLE release = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (!g_uninitializeEntered) g_uninitializeEntered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!g_uninitializeRelease) g_uninitializeRelease = CreateEventW(nullptr, TRUE, TRUE, nullptr);
        entered = g_uninitializeEntered;
        release = g_uninitializeRelease;
        failUninitialize = g_failUninitialize;
        blockUninitialize = g_blockUninitialize;
        allowEmptyUninitialize = g_allowEmptyUninitialize;
        throwUninitialize = g_throwUninitialize;
    }
    if (!entered || !release) return false;
    SetEvent(entered);
    if (blockUninitialize) WaitForSingleObject(release, INFINITE);
    ID3D11Device* device = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        device = g_device;
        if (!throwUninitialize) g_device = nullptr;
    }
    Record(LKG_FAKE_EVENT_UNINITIALIZE, device, throwUninitialize ? -1 : (failUninitialize ? 0 : 1));
    if (throwUninitialize) throw std::runtime_error("fake uninitialize exception");
    if (device) device->Release();
    return (device != nullptr || allowEmptyUninitialize) && !failUninitialize;
}

extern "C" __declspec(dllexport) bool get_bridge_version(unsigned long* major, unsigned long* minor, unsigned long* build, int* postfix, wchar_t*) {
    if (major) *major = 1;
    if (minor) *minor = 0;
    if (build) *build = 0;
    if (postfix) *postfix = 0;
    return true;
}

extern "C" __declspec(dllexport) bool get_displays(int* count, unsigned long* ids) {
    if (count) *count = 1;
    if (ids) ids[0] = 0;
    return true;
}

extern "C" __declspec(dllexport) bool get_dimensions_for_display(unsigned long, unsigned long* width, unsigned long* height) {
    if (width) *width = 1920;
    if (height) *height = 1080;
    return true;
}

extern "C" __declspec(dllexport) bool get_viewcone_for_display(unsigned long, float* value) {
    if (value) *value = 40.0f;
    return true;
}

extern "C" __declspec(dllexport) bool get_default_quilt_settings_for_display(unsigned long, float* aspect, int* width, int* height, int* columns, int* rows) {
    if (aspect) *aspect = 1.0f;
    if (width) *width = 1920;
    if (height) *height = 1080;
    if (columns) *columns = 8;
    if (rows) *rows = 6;
    return true;
}

extern "C" __declspec(dllexport) bool get_window_position_for_display(unsigned long, long* x, long* y) {
    if (x) *x = 0;
    if (y) *y = 0;
    return true;
}

extern "C" __declspec(dllexport) bool get_display_for_window(unsigned long, unsigned long* display) {
    if (display) *display = 0;
    return true;
}

extern "C" __declspec(dllexport) bool release_device_dx(IUnknown* device) {
    Record(LKG_FAKE_EVENT_RELEASE_DEVICE, device, device ? 1 : 0);
    if (device) device->Release();
    return device != nullptr;
}

extern "C" __declspec(dllexport) bool instance_window_dx(IUnknown* device, unsigned long* window, unsigned long) {
    if (!device || !window) return false;
    ID3D11Device* queried = nullptr;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&queried)))) return false;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_device) g_device->Release();
        g_device = queried;
    }
    *window = 1;
    Record(LKG_FAKE_EVENT_INSTANCE_WINDOW, device, 1);
    return true;
}

extern "C" __declspec(dllexport) bool show_window(unsigned long, bool) {
    Record(LKG_FAKE_EVENT_SHOW_WINDOW, nullptr, 1);
    return true;
}

extern "C" __declspec(dllexport) bool register_texture_dx(unsigned long, IUnknown* texture) {
    Record(LKG_FAKE_EVENT_REGISTER_TEXTURE, texture, texture ? 1 : 0);
    return texture != nullptr;
}

extern "C" __declspec(dllexport) bool unregister_texture_dx(unsigned long, IUnknown* texture) {
    Record(LKG_FAKE_EVENT_UNREGISTER_TEXTURE, texture, texture ? 1 : 0);
    return texture != nullptr;
}

extern "C" __declspec(dllexport) bool draw_interop_quilt_texture_dx(unsigned long, IUnknown* texture, unsigned long, unsigned long, float, float) {
    bool fail = false;
    bool block = false;
    HANDLE entered = nullptr;
    HANDLE release = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        fail = g_failDraw;
        block = g_blockDraw;
        entered = g_drawEntered;
        release = g_drawRelease;
    }
    Record(LKG_FAKE_EVENT_DRAW_ENTER, texture, 1);
    if (entered) SetEvent(entered);
    if (block && (!release || WaitForSingleObject(release, 5000) != WAIT_OBJECT_0)) {
        Record(LKG_FAKE_EVENT_DRAW_EXIT, texture, 0);
        return false;
    }
    Record(LKG_FAKE_EVENT_DRAW_EXIT, texture, fail ? 0 : 1);
    return !fail;
}

extern "C" __declspec(dllexport) bool create_texture_dx(unsigned long, unsigned long width, unsigned long height, IUnknown** texture) {
    ID3D11Device* device = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        device = g_device;
        if (device) device->AddRef();
    }
    if (!device || !texture || width == 0 || height == 0) {
        if (device) device->Release();
        return false;
    }
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = static_cast<UINT>(width);
    desc.Height = static_cast<UINT>(height);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    ID3D11Texture2D* created = nullptr;
    const HRESULT result = device->CreateTexture2D(&desc, nullptr, &created);
    device->Release();
    if (FAILED(result)) return false;
    *texture = created;
    Record(LKG_FAKE_EVENT_CREATE_TEXTURE, created, 1);
    return true;
}

extern "C" __declspec(dllexport) bool copy_texture_dx(unsigned long, IUnknown* source, IUnknown* destination) {
    ID3D11Device* device = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        device = g_device;
        if (device) device->AddRef();
    }
    if (!device || !source || !destination) {
        if (device) device->Release();
        return false;
    }
    ID3D11DeviceContext* context = nullptr;
    ID3D11Resource* src = nullptr;
    ID3D11Resource* dst = nullptr;
    bool ok = SUCCEEDED(source->QueryInterface(IID_PPV_ARGS(&src))) &&
        SUCCEEDED(destination->QueryInterface(IID_PPV_ARGS(&dst)));
    if (ok) {
        device->GetImmediateContext(&context);
        ok = context != nullptr;
        if (ok) context->CopyResource(dst, src);
    }
    if (context) context->Release();
    if (src) src->Release();
    if (dst) dst->Release();
    device->Release();
    return ok;
}

extern "C" __declspec(dllexport) bool release_texture_dx(unsigned long, IUnknown* texture) {
    Record(LKG_FAKE_EVENT_RELEASE_TEXTURE, texture, texture ? 1 : 0);
    if (texture) texture->Release();
    return texture != nullptr;
}

extern "C" __declspec(dllexport) bool save_texture_to_file_dx(unsigned long, wchar_t*, IUnknown*) {
    return false;
}
