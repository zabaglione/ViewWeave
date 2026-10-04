#include "bridge_worker_transport.h"
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <thread>
#include <vector>
#include <stdexcept>

using Microsoft::WRL::ComPtr;

#if defined(LKG_WORKER_TRANSPORT_TESTING)
uint32_t lkg_worker_test_thread_id(void* handle);
HWND lkg_worker_test_window(void* handle);
uint32_t lkg_worker_test_message_count(void* handle);
#endif

static ComPtr<ID3D11Device> g_device;
static std::atomic<bool> g_initializeFailure{false};
static std::atomic<bool> g_drawFailure{false};
static std::atomic<bool> g_drawThrow{false};
static std::atomic<uint32_t> g_drawCount{0};
static std::atomic<bool> g_workerClaimed{false};
static std::atomic<unsigned long> g_producerThread{0};
static std::atomic<unsigned long> g_workerThread{0};
static std::atomic<bool> g_copyVerified{false};
static constexpr uint8_t kExpectedRed = 0x22;

static void Fail(const char* message) {
    std::fprintf(stderr, "FAIL %s\n", message);
    std::exit(1);
}

static void Check(bool condition, const char* message) {
    if (!condition) Fail(message);
}

static bool WaitState(void* handle, uint32_t state, uint32_t timeoutMs) {
    const auto start = GetTickCount64();
    LkgWorkerStatus status{};
    while (GetTickCount64() - start < timeoutMs) {
        if (lkg_worker_poll(handle, &status) == 0 && status.state == state) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

static bool WaitReady(void* handle, uint32_t timeoutMs = 2000) {
    return WaitState(handle, LKG_WORKER_READY, timeoutMs);
}

static ComPtr<ID3D11Texture2D> MakeTexture(DXGI_FORMAT format, uint32_t width, uint32_t height) {
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> texture;
    Check(SUCCEEDED(g_device->CreateTexture2D(&desc, nullptr, &texture)), "CreateTexture2D");
    if (format == DXGI_FORMAT_R8G8B8A8_UNORM) {
        std::vector<uint8_t> pixels(width * height * 4, 0);
        for (size_t index = 0; index < pixels.size(); index += 4) pixels[index] = kExpectedRed;
        ComPtr<ID3D11DeviceContext> context;
        g_device->GetImmediateContext(&context);
        context->UpdateSubresource(texture.Get(), 0, nullptr, pixels.data(), width * 4, 0);
        context->Flush();
    }
    return texture;
}

static ComPtr<ID3D11Texture2D> MakeTypelessTexture(uint32_t width, uint32_t height) {
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> texture;
    Check(SUCCEEDED(g_device->CreateTexture2D(&desc, nullptr, &texture)), "Create typeless texture");
    D3D11_RENDER_TARGET_VIEW_DESC viewDesc{};
    viewDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    viewDesc.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    ComPtr<ID3D11RenderTargetView> view;
    Check(SUCCEEDED(g_device->CreateRenderTargetView(texture.Get(), &viewDesc, &view)), "Create typeless RTV");
    ComPtr<ID3D11DeviceContext> context;
    g_device->GetImmediateContext(&context);
    const float red = static_cast<float>(kExpectedRed) / 255.0f;
    const float color[4] = { red, 0.0f, 0.0f, 1.0f };
    context->ClearRenderTargetView(view.Get(), color);
    context->Flush();
    return texture;
}

static bool WaitCompleted(void* handle, uint32_t target, uint32_t timeoutMs) {
    const auto start = GetTickCount64();
    LkgWorkerStatus status{};
    while (GetTickCount64() - start < timeoutMs) {
        Check(lkg_worker_poll(handle, &status) == 0, "poll completion");
        if (status.state == LKG_WORKER_FAULT || status.state == LKG_WORKER_CLOSED) {
            std::fprintf(stderr, "completion stopped state=%u code=%u error=%s\n", status.state, status.lastErrorCode, status.error);
            Fail("completion worker fault");
        }
        if (status.completed >= target) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

static void BeginDevice() {
    D3D_FEATURE_LEVEL selected{};
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    Check(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        levels, 2, D3D11_SDK_VERSION, &g_device, &selected, nullptr)), "D3D11CreateDevice");
}

static void BeginContext(void** handle) {
    *handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, nullptr);
    Check(*handle != nullptr, "worker begin");
    Check(WaitReady(*handle), "worker ready");
}

static void EndContext(void* handle) {
    Check(lkg_worker_close(handle) == 0, "worker close");
    Check(WaitState(handle, LKG_WORKER_CLOSED, 2000) || WaitState(handle, LKG_WORKER_FAULT, 2000), "worker closed");
    const auto start = GetTickCount64();
    int32_t result = -2;
    while (GetTickCount64() - start < 2000 && result == -2) {
        result = lkg_worker_release(handle);
        if (result == -2) std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    Check(result == 0, "worker release");
    LkgWorkerStatus status{};
    Check(lkg_worker_poll(handle, &status) == -2, "poll after release");
}

static void ReleaseAfterFault(void* handle, const char* message) {
    const auto start = GetTickCount64();
    int32_t result = -2;
    while (GetTickCount64() - start < 2000 && result == -2) {
        result = lkg_worker_release(handle);
        if (result == -2) std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    Check(result == 0, message);
}

static void TestBoundedCompletion() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 32);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    g_producerThread.store(GetCurrentThreadId());
    void* first = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
    void* second = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
    void* third = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
    void* fourth = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
    Check(first && second && third && !fourth, "three token bound");
    Check(lkg_worker_reset_transfer(handle) == -2, "reset rejects pending tokens");
    Check(lkg_worker_cancel_draw_data(first) == 0, "cancel pending token");
    callback(0, second);
    callback(0, third);
    uint32_t target = 120;
    const auto deadline = GetTickCount64() + 5000;
    while (true) {
        LkgWorkerStatus status{};
        Check(lkg_worker_poll(handle, &status) == 0, "poll bounded");
        if (status.submitted >= target) break;
        Check(GetTickCount64() < deadline, "120 submission deadline");
        void* data = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
        if (data) callback(0, data);
        else std::this_thread::yield();
    }
    Check(WaitCompleted(handle, target, 5000), "120 draw completion");
    Check(g_drawCount.load() >= target, "stub draw count");
    Check(g_copyVerified.load(), "GPU copy readback");
    Check(lkg_worker_reset_transfer(handle) == 0, "reset after drain");
    Check(g_workerThread.load() != 0 && g_workerThread.load() != g_producerThread.load(), "worker thread identity");
    EndContext(handle);
}

static void TestTypelessSource() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTypelessTexture(24, 24);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    g_copyVerified.store(false);
    void* data = lkg_worker_make_draw_data(handle, texture.Get(), 4, 2, 1.0f, 1.0f);
    Check(data != nullptr, "typeless token");
    callback(0, data);
    Check(WaitCompleted(handle, 1, 5000), "typeless completion");
    Check(g_copyVerified.load(), "typeless GPU copy readback");
    EndContext(handle);
}

static void TestTransferResetResize() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    constexpr int kResetResizeCycles = 100;
    for (int cycle = 0; cycle < kResetResizeCycles; ++cycle) {
        LkgWorkerStatus before{};
        Check(lkg_worker_poll(handle, &before) == 0, "reset cycle poll");
        const uint32_t firstTarget = before.completed + 1;
        auto first = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 24, 24);
        g_copyVerified.store(false);
        auto data = lkg_worker_make_draw_data(handle, first.Get(), 4, 2, 1.0f, 1.0f);
        Check(data != nullptr, "reset first token");
        callback(0, data);
        Check(WaitCompleted(handle, firstTarget, 5000), "reset first completion");
        Check(g_copyVerified.load(), "reset first readback");
        Check(lkg_worker_reset_transfer(handle) == 0, "quiescent transfer reset");
        const uint32_t secondTarget = firstTarget + 1;
        auto second = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 16);
        g_copyVerified.store(false);
        data = lkg_worker_make_draw_data(handle, second.Get(), 4, 2, 1.0f, 1.0f);
        Check(data != nullptr, "reset second token");
        callback(0, data);
        Check(WaitCompleted(handle, secondTarget, 5000), "reset second completion");
        Check(g_copyVerified.load(), "reset second readback");
        Check(lkg_worker_reset_transfer(handle) == 0, "cycle transfer reset");
    }
    Check(lkg_worker_close(handle) == 0, "reset close");
    Check(lkg_worker_reset_transfer(handle) == -3, "reset rejects closing worker");
    EndContext(handle);
    Check(lkg_worker_reset_transfer(handle) == -1, "reset rejects released worker");
}

static void TestCloseDrainAndExclusiveBegin() {
    void* handle = nullptr;
    BeginContext(&handle);
    void* second = lkg_worker_begin("test-runtime", "worker-transport-test", 0, nullptr);
    Check(second == nullptr, "exclusive begin");
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto data = lkg_worker_make_draw_data(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "close pending token");
    Check(lkg_worker_close(handle) == 0, "close pending");
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    callback(0, data);
    EndContext(handle);
}

static void TestCloseAllRetiresLateToken() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    void* stale = lkg_worker_make_draw_data(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
    Check(stale != nullptr, "close all token");
    Check(lkg_worker_pending_count() == 0, "ready worker is not shutdown pending");
    Check(lkg_worker_close_all() == 0, "close all");
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    callback(0, stale);
    Check(WaitState(handle, LKG_WORKER_CLOSED, 2000) || WaitState(handle, LKG_WORKER_FAULT, 2000), "close all completion");
    const auto deadline = GetTickCount64() + 2000;
    while (GetTickCount64() < deadline && lkg_worker_pending_count() != 1) {
        lkg_worker_collect_closed();
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    Check(lkg_worker_pending_count() == 1, "closed context remains pending until collect");
    Check(lkg_worker_collect_closed() >= 1, "collect closed context");
    Check(lkg_worker_pending_count() == 0, "close all pending drain");
    LkgWorkerStatus status{};
    Check(lkg_worker_poll(handle, &status) == -2, "poll after close all collect");

    void* next = nullptr;
    BeginContext(&next);
    auto nextTexture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    void* nextToken = lkg_worker_make_draw_data(next, nextTexture.Get(), 2, 2, 1.0f, 1.0f);
    Check(nextToken != nullptr, "new token after collect");
    callback(0, stale);
    Check(lkg_worker_pending_count() == 0, "stale token did not affect new ready context");
    callback(0, nextToken);
    EndContext(next);
}

static void TestFaultCases() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto badFormat = MakeTexture(DXGI_FORMAT_B8G8R8A8_UNORM, 16, 16);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    void* data = lkg_worker_make_draw_data(handle, badFormat.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "bad format token");
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "bad format fault");
    ReleaseAfterFault(handle, "bad format release");

    BeginContext(&handle);
    auto first = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto second = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 16);
    data = lkg_worker_make_draw_data(handle, first.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "resize first token");
    callback(0, data);
    data = lkg_worker_make_draw_data(handle, second.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "resize second token");
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "resize fault");
    ReleaseAfterFault(handle, "resize release");
}

static void TestInitializationAndDrawFault() {
    g_initializeFailure.store(true);
    void* handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, nullptr);
    Check(handle != nullptr, "initializer failure begin");
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "initializer failure fault");
    ReleaseAfterFault(handle, "initializer failure release");
    g_initializeFailure.store(false);

    BeginContext(&handle);
    g_drawFailure.store(true);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto data = lkg_worker_make_draw_data(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "draw failure token");
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "draw failure fault");
    ReleaseAfterFault(handle, "draw failure release");
    g_drawFailure.store(false);
}

static void TestDrawExceptionDrain() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto data = lkg_worker_make_draw_data(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "draw exception token");
    g_drawThrow.store(true);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "draw exception fault");
    ReleaseAfterFault(handle, "draw exception release");
    g_drawThrow.store(false);
}

static void TestCloseAndPollResponsiveness() {
    void* handle = nullptr;
    BeginContext(&handle);
    LkgWorkerStatus status{};
    const auto start = GetTickCount64();
    for (int index = 0; index < 1000; ++index) {
        Check(lkg_worker_poll(handle, &status) == 0, "poll responsiveness");
    }
    Check(GetTickCount64() - start < 1000, "poll responsiveness budget");
    EndContext(handle);
}

#if defined(LKG_WORKER_TRANSPORT_TESTING)
static void TestWorkerMessagePump() {
    void* handle = nullptr;
    BeginContext(&handle);
    HWND window = lkg_worker_test_window(handle);
    Check(window != nullptr, "worker test window");
    Check(PostMessageW(window, WM_APP + 1, 0, 0) != FALSE, "post worker message");
    const auto messageDeadline = GetTickCount64() + 1000;
    while (lkg_worker_test_message_count(handle) == 0 && GetTickCount64() < messageDeadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    Check(lkg_worker_test_message_count(handle) == 1, "worker message dispatch");
    LkgWorkerStatus status{};
    Check(lkg_worker_poll(handle, &status) == 0 && status.state == LKG_WORKER_READY, "worker remains ready after message");
    EndContext(handle);

    BeginContext(&handle);
    const DWORD threadId = lkg_worker_test_thread_id(handle);
    Check(threadId != 0, "worker thread id");
    Check(PostThreadMessageW(threadId, WM_QUIT, 0, 0) != FALSE, "post worker quit");
    Check(WaitState(handle, LKG_WORKER_FAULT, 1000), "worker quit fault");
    ReleaseAfterFault(handle, "worker quit release");
}
#endif

#include "bridge_worker_host_affinity_test.cpp"

static void VerifyWorkerTexture(void* texturePointer) {
    ComPtr<ID3D11Texture2D> texture;
    auto* unknown = static_cast<IUnknown*>(texturePointer);
    Check(unknown && SUCCEEDED(unknown->QueryInterface(IID_PPV_ARGS(&texture))), "worker texture query");
    ComPtr<ID3D11Device> workerDevice;
    texture->GetDevice(&workerDevice);
    Check(workerDevice && workerDevice.Get() != g_device.Get(), "worker device identity");
    g_workerThread.store(GetCurrentThreadId());
    D3D11_TEXTURE2D_DESC desc{};
    texture->GetDesc(&desc);
    Check(desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM, "worker shared format normalized to UNORM");
    desc.Usage = D3D11_USAGE_STAGING;
    desc.BindFlags = 0;
    desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    desc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    Check(SUCCEEDED(workerDevice->CreateTexture2D(&desc, nullptr, &staging)), "worker readback texture");
    ComPtr<ID3D11DeviceContext> context;
    workerDevice->GetImmediateContext(&context);
    context->CopyResource(staging.Get(), texture.Get());
    context->Flush();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(SUCCEEDED(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)), "worker readback map");
    const auto* pixel = static_cast<const uint8_t*>(mapped.pData);
    Check(pixel[0] == kExpectedRed, "worker readback content");
    context->Unmap(staging.Get(), 0);
    g_copyVerified.store(true);
}

int32_t lkg_bridge_load(const char*) { return 0; }
void lkg_bridge_unload(void) {}
int32_t lkg_bridge_initialize(const char*) { return g_initializeFailure.load() ? -101 : 0; }
void lkg_bridge_uninitialize(void) {}
int32_t lkg_bridge_is_idle(void) { return 1; }
int32_t lkg_bridge_last_error(char* utf8, uint32_t capacity) { if (utf8 && capacity) utf8[0] = 0; return 0; }
uint32_t lkg_bridge_successful_draw_count(void) { return g_drawCount.load(); }
int32_t lkg_bridge_is_initialized(void) { return 0; }
int32_t lkg_bridge_version(char*, uint32_t) { return 0; }
int32_t lkg_bridge_get_display_count(uint32_t* count) { if (!count) return -1; *count = 1; return 0; }
int32_t lkg_bridge_get_display(uint32_t ordinal, LkgBridgeDisplayInfo* info) {
    if (!info || ordinal != 0) return -2;
    *info = LkgBridgeDisplayInfo{ 0, 32, 32, 1.0f, 1.0f, 128, 128, 4, 4 };
    return 0;
}
uint64_t lkg_bridge_create_d3d11_session(void* device, uint32_t) { return device ? 1 : 0; }
int32_t lkg_bridge_destroy_session(uint64_t) { return 0; }
void* lkg_bridge_render_event_func(void) { return nullptr; }
void* lkg_bridge_make_draw_data(uint64_t, void*, uint32_t, uint32_t, float, float) { return nullptr; }
void* lkg_bridge_make_close_data(uint64_t) { return nullptr; }
int32_t lkg_bridge_smoke_draw(uint64_t, void* texture, uint32_t, uint32_t, float, float) {
    if (g_drawThrow.load()) throw std::runtime_error("stub draw exception");
    if (g_drawFailure.load()) return -202;
    VerifyWorkerTexture(texture);
    ++g_drawCount;
    return 0;
}

IUnknown* lkg_bridge_unity_device_addref() {
    if (!g_device) return nullptr;
    g_device->AddRef();
    return g_device.Get();
}
bool lkg_bridge_worker_claim() { bool expected = false; return g_workerClaimed.compare_exchange_strong(expected, true); }
void lkg_bridge_worker_release_claim() { g_workerClaimed.store(false); }
void lkg_bridge_worker_enter() {}
void lkg_bridge_worker_leave() {}

int main() {
    BeginDevice();
    TestTypelessSource();
    TestTransferResetResize();
    TestBoundedCompletion();
    TestFaultCases();
    TestInitializationAndDrawFault();
    TestDrawExceptionDrain();
    TestCloseAndPollResponsiveness();
    TestCloseDrainAndExclusiveBegin();
    TestCloseAllRetiresLateToken();
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    TestPersistentHostThreadReuse();
    TestWorkerMessagePump();
    TestPersistentHostIdleQuit();
#endif
    std::fprintf(stdout, "worker_transport_test pass draws=%u\n", g_drawCount.load());
    return 0;
}
