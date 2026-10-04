#include "bridge_worker_transport.h"
#include <windows.h>
#include <d3d11.h>
#include "bridge_d3d12_internal.h"
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
#include <deque>
#include <mutex>
#include <array>

using Microsoft::WRL::ComPtr;

#if defined(LKG_WORKER_TRANSPORT_TESTING)
uint32_t lkg_worker_test_thread_id(void* handle);
HWND lkg_worker_test_window(void* handle);
uint32_t lkg_worker_test_message_count(void* handle);
int32_t lkg_worker_test_set_d3d12_timeout(void* handle, uint64_t timeoutMilliseconds);
void lkg_worker_test_delay_d3d12_consumer(uint32_t pendingPolls);
uint32_t lkg_worker_test_d3d12_consumer_pending_polls(void);
void lkg_worker_test_d3d12_set_removed(bool removed);
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
static std::atomic<bool> g_testingD3D12{false};
static std::atomic<uint8_t> g_expectedRed{ 0x22 };
static std::atomic<uint8_t> g_expectedGreen{ 0 };
static std::atomic<uint8_t> g_expectedBlue{ 0 };
static std::mutex g_expectedD3D12Mutex;
static std::deque<std::array<uint8_t, 3>> g_expectedD3D12Pixels;
static ComPtr<ID3D12Device> g_testD3D12Device;
static ComPtr<ID3D12CommandQueue> g_testD3D12Queue;
static ComPtr<ID3D12Fence> g_testD3D12Fence;
static ComPtr<ID3D12Fence> g_testD3D12GateFence;
static std::atomic<uint64_t> g_testD3D12FenceValue{ 0 };
static std::atomic<uint64_t> g_testD3D12Generation{ 1 };
static std::atomic<uint64_t> g_testD3D12SubmitOverride{ 0 };
static std::atomic<bool> g_testD3D12SubmitOverrideActive{ false };
static std::atomic<bool> g_testD3D12GateProducer{ false };
static std::atomic<uint64_t> g_testD3D12GateValue{ 1 };
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
    uint8_t expected[3] = { g_expectedRed.load(), g_expectedGreen.load(), g_expectedBlue.load() };
    if (g_testingD3D12.load()) {
        std::lock_guard<std::mutex> lock(g_expectedD3D12Mutex);
        Check(!g_expectedD3D12Pixels.empty(), "D3D12 expected pixel queue");
        const auto next = g_expectedD3D12Pixels.front();
        g_expectedD3D12Pixels.pop_front();
        expected[0] = next[0]; expected[1] = next[1]; expected[2] = next[2];
    }
    Check(pixel[0] == expected[0] && pixel[1] == expected[1] && pixel[2] == expected[2], "worker readback content");
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
int32_t lkg_bridge_unity_device_snapshot(IUnknown** device, uint64_t* generation, uint32_t* graphicsApi) {
    if (!device || !generation || !graphicsApi || !g_device) return -1;
    g_device->AddRef();
    *device = g_device.Get();
    *generation = 1;
    *graphicsApi = 1;
    return 0;
}
int32_t lkg_bridge_d3d12_snapshot(LkgD3D12Snapshot* snapshot) {
    if (!snapshot || !g_testD3D12Device || !g_testD3D12Fence) return -1;
    snapshot->device = g_testD3D12Device.Get();
    snapshot->frameFence = g_testD3D12Fence.Get();
    snapshot->generation = g_testD3D12Generation.load();
    snapshot->device->AddRef();
    snapshot->frameFence->AddRef();
    return 0;
}
void lkg_bridge_d3d12_snapshot_release(LkgD3D12Snapshot* snapshot) {
    if (!snapshot) return;
    if (snapshot->frameFence) snapshot->frameFence->Release();
    if (snapshot->device) snapshot->device->Release();
    snapshot->device = nullptr;
    snapshot->frameFence = nullptr;
}
uint64_t lkg_bridge_d3d12_submit(ID3D12GraphicsCommandList* list, ID3D12Resource*) {
    if (g_testD3D12SubmitOverrideActive.exchange(false)) return g_testD3D12SubmitOverride.load();
    if (!list || !g_testD3D12Queue || !g_testD3D12Fence) return 0;
    if (g_testD3D12GateProducer.load()) g_testD3D12Queue->Wait(g_testD3D12GateFence.Get(), g_testD3D12GateValue.load());
    ID3D12CommandList* lists[] = { list };
    g_testD3D12Queue->ExecuteCommandLists(1, lists);
    const uint64_t value = g_testD3D12FenceValue.fetch_add(1) + 1;
    if (FAILED(g_testD3D12Queue->Signal(g_testD3D12Fence.Get(), value))) return 0;
    return value;
}
uint64_t lkg_bridge_unity_device_generation() { return 1; }
bool lkg_bridge_worker_claim() { bool expected = false; return g_workerClaimed.compare_exchange_strong(expected, true); }
void lkg_bridge_worker_release_claim() { g_workerClaimed.store(false); }
void lkg_bridge_worker_enter() {}
void lkg_bridge_worker_leave() {}
int32_t lkg_bridge_get_worker_render_event_id(int32_t* eventId) { if (!eventId) return -1; *eventId = 0; return 0; }

static bool WaitD3D12Fence(uint64_t value) {
    if (!g_testD3D12Fence || g_testD3D12Fence->GetCompletedValue() >= value) return true;
    HANDLE eventHandle = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!eventHandle) return false;
    const HRESULT result = g_testD3D12Fence->SetEventOnCompletion(value, eventHandle);
    const DWORD waitResult = SUCCEEDED(result) ? WaitForSingleObject(eventHandle, 5000) : WAIT_FAILED;
    CloseHandle(eventHandle);
    return waitResult == WAIT_OBJECT_0;
}

static bool InitializeD3D12TestDevice() {
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
    for (UINT index = 0;; ++index) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(index, &adapter) == DXGI_ERROR_NOT_FOUND) break;
        DXGI_ADAPTER_DESC1 desc{};
        adapter->GetDesc1(&desc);
        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
        if (FAILED(D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&g_testD3D12Device)))) continue;
        D3D12_COMMAND_QUEUE_DESC queueDesc{};
        queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        if (FAILED(g_testD3D12Device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&g_testD3D12Queue))) ||
            FAILED(g_testD3D12Device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_testD3D12Fence))) ||
            FAILED(g_testD3D12Device->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_testD3D12GateFence)))) {
            g_testD3D12Device.Reset();
            g_testD3D12Queue.Reset();
            g_testD3D12Fence.Reset();
            continue;
        }
        g_testD3D12FenceValue.store(0);
        g_testD3D12GateProducer.store(false);
        g_testD3D12GateValue.store(1);
        return true;
    }
    return false;
}

static ComPtr<ID3D12Resource> MakeD3D12PatternTexture(const std::array<uint8_t, 3>& rgb,
                                                       DXGI_FORMAT resourceFormat = DXGI_FORMAT_R8G8B8A8_UNORM) {
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = 32;
    desc.Height = 16;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = resourceFormat;
    desc.SampleDesc.Count = 1;
    desc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    D3D12_CLEAR_VALUE clear{};
    clear.Format = resourceFormat == DXGI_FORMAT_R8G8B8A8_TYPELESS ? DXGI_FORMAT_R8G8B8A8_UNORM : desc.Format;
    clear.Color[0] = static_cast<float>(rgb[0]) / 255.0f;
    clear.Color[1] = static_cast<float>(rgb[1]) / 255.0f;
    clear.Color[2] = static_cast<float>(rgb[2]) / 255.0f;
    clear.Color[3] = 1.0f;
    ComPtr<ID3D12Resource> texture;
    Check(SUCCEEDED(g_testD3D12Device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc,
        D3D12_RESOURCE_STATE_RENDER_TARGET, &clear, IID_PPV_ARGS(&texture))), "D3D12 pattern texture");
    D3D12_DESCRIPTOR_HEAP_DESC rtvDesc{};
    rtvDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    rtvDesc.NumDescriptors = 1;
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    Check(SUCCEEDED(g_testD3D12Device->CreateDescriptorHeap(&rtvDesc, IID_PPV_ARGS(&rtvHeap))), "D3D12 RTV heap");
    D3D12_RENDER_TARGET_VIEW_DESC viewDesc{};
    viewDesc.Format = clear.Format;
    viewDesc.ViewDimension = D3D12_RTV_DIMENSION_TEXTURE2D;
    g_testD3D12Device->CreateRenderTargetView(texture.Get(), &viewDesc, rtvHeap->GetCPUDescriptorHandleForHeapStart());
    ComPtr<ID3D12CommandAllocator> allocator;
    ComPtr<ID3D12GraphicsCommandList> list;
    Check(SUCCEEDED(g_testD3D12Device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator))), "D3D12 setup allocator");
    Check(SUCCEEDED(g_testD3D12Device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list))), "D3D12 setup list");
    g_testD3D12Queue->Wait(g_testD3D12Fence.Get(), g_testD3D12FenceValue.load());
    const float color[] = { clear.Color[0], clear.Color[1], clear.Color[2], 1.0f };
    list->ClearRenderTargetView(rtvHeap->GetCPUDescriptorHandleForHeapStart(), color, 0, nullptr);
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = texture.Get();
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
    list->ResourceBarrier(1, &barrier);
    Check(SUCCEEDED(list->Close()), "D3D12 setup list close");
    ID3D12CommandList* lists[] = { list.Get() };
    g_testD3D12Queue->ExecuteCommandLists(1, lists);
    const uint64_t fenceValue = g_testD3D12FenceValue.fetch_add(1) + 1;
    Check(SUCCEEDED(g_testD3D12Queue->Signal(g_testD3D12Fence.Get(), fenceValue)), "D3D12 setup signal");
    Check(WaitD3D12Fence(fenceValue), "D3D12 setup completion");
    return texture;
}

static void TestD3D12RingColors() {
    if (!InitializeD3D12TestDevice()) {
        std::fprintf(stdout, "D3D12 ring test skipped: no hardware D3D12 adapter\n");
        return;
    }
    g_testingD3D12.store(true);
    void* handle = lkg_worker_begin("test-runtime", "worker-transport-d3d12-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 worker begin");
    Check(WaitReady(handle), "D3D12 worker ready");
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    constexpr uint32_t targetDraws = 24;
    uint32_t accepted = 0;
    for (uint32_t sequence = 0; sequence < targetDraws * 4 && accepted < targetDraws; ++sequence) {
        const std::array<uint8_t, 3> rgb = {
            static_cast<uint8_t>(24 + (sequence * 37) % 208),
            static_cast<uint8_t>(20 + (sequence * 61) % 216),
            static_cast<uint8_t>(16 + (sequence * 83) % 224)
        };
        auto texture = MakeD3D12PatternTexture(rgb);
        void* data = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
        if (!data) continue;
        {
            std::lock_guard<std::mutex> lock(g_expectedD3D12Mutex);
            g_expectedD3D12Pixels.push_back(rgb);
        }
        LkgWorkerStatus before{};
        Check(lkg_worker_poll(handle, &before) == 0, "D3D12 worker pre-submit poll");
        callback(0, data);
        LkgWorkerStatus after{};
        Check(lkg_worker_poll(handle, &after) == 0, "D3D12 worker post-submit poll");
        if (after.submitted > before.submitted) {
            ++accepted;
            if (accepted % 3 == 0) Check(WaitCompleted(handle, accepted, 5000), "D3D12 ring batch completion");
        } else {
            std::lock_guard<std::mutex> lock(g_expectedD3D12Mutex);
            if (!g_expectedD3D12Pixels.empty()) g_expectedD3D12Pixels.pop_back();
        }
    }
    Check(accepted == targetDraws, "D3D12 ring accepted sequence");
    Check(WaitCompleted(handle, targetDraws, 5000), "D3D12 ring final completion");
    Check(lkg_worker_reset_transfer(handle) == 0, "D3D12 ring drained reset");
    EndContext(handle);
    handle = lkg_worker_begin("test-runtime", "worker-transport-d3d12-typeless-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 typeless worker begin");
    Check(WaitReady(handle), "D3D12 typeless worker ready");
    const std::array<uint8_t, 3> typelessRgb = { 63, 127, 191 };
    auto typelessTexture = MakeD3D12PatternTexture(typelessRgb, DXGI_FORMAT_R8G8B8A8_TYPELESS);
    void* typelessData = lkg_worker_make_draw_data(handle, typelessTexture.Get(), 4, 3, 1.0f, 1.0f);
    Check(typelessData != nullptr, "D3D12 typeless token");
    {
        std::lock_guard<std::mutex> lock(g_expectedD3D12Mutex);
        g_expectedD3D12Pixels.push_back(typelessRgb);
    }
    callback(0, typelessData);
    Check(WaitCompleted(handle, 1, 5000), "D3D12 typeless completion");
    Check(lkg_worker_reset_transfer(handle) == 0, "D3D12 typeless drained reset");
    EndContext(handle);
    handle = lkg_worker_begin("test-runtime", "worker-transport-d3d12-gate-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 gate worker begin");
    Check(WaitReady(handle), "D3D12 gate worker ready");
    const std::array<uint8_t, 3> gatedRgb = { 91, 143, 207 };
    auto gatedTexture = MakeD3D12PatternTexture(gatedRgb);
    void* gatedData = lkg_worker_make_draw_data(handle, gatedTexture.Get(), 4, 3, 1.0f, 1.0f);
    Check(gatedData != nullptr, "D3D12 gated token");
    {
        std::lock_guard<std::mutex> lock(g_expectedD3D12Mutex);
        g_expectedD3D12Pixels.push_back(gatedRgb);
    }
    g_testD3D12GateProducer.store(true);
    lkg_worker_test_delay_d3d12_consumer(8);
    const ULONGLONG callbackStart = GetTickCount64();
    callback(0, gatedData);
    const ULONGLONG callbackDuration = GetTickCount64() - callbackStart;
    g_testD3D12GateProducer.store(false);
    Check(callbackDuration < 100, "D3D12 callback does not wait for producer fence");
    Check(lkg_worker_reset_transfer(handle) == -2, "D3D12 reset waits for producer fence");
    LkgWorkerStatus gatedStatus{};
    Check(lkg_worker_poll(handle, &gatedStatus) == 0 && gatedStatus.submitted == 1 && gatedStatus.completed == 0,
          "D3D12 producer fence remains pending");
    Check(lkg_worker_close(handle) == 0, "D3D12 close with producer pending");
    Check(lkg_worker_release(handle) == -2, "D3D12 release waits for producer fence");
    Check(lkg_worker_pending_count() == 1, "D3D12 close remains pending");
    Check(SUCCEEDED(g_testD3D12GateFence->Signal(1)), "D3D12 release producer gate");
    Check(WaitState(handle, LKG_WORKER_CLOSED, 5000), "D3D12 close drains gated frame");
    LkgWorkerStatus drainedGateStatus{};
    Check(lkg_worker_poll(handle, &drainedGateStatus) == 0 && drainedGateStatus.completed == 1,
          "D3D12 gated frame completion count");
    EndContext(handle);
    handle = lkg_worker_begin("test-runtime", "worker-transport-d3d12-consumer-timeout-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 consumer timeout worker begin");
    Check(WaitReady(handle), "D3D12 consumer timeout worker ready");
    Check(lkg_worker_test_set_d3d12_timeout(handle, 40) == 0, "D3D12 consumer timeout setup");
    auto consumerTimeoutTexture = MakeD3D12PatternTexture({ 107, 149, 211 });
    void* consumerTimeoutData = lkg_worker_make_draw_data(handle, consumerTimeoutTexture.Get(), 4, 3, 1.0f, 1.0f);
    Check(consumerTimeoutData != nullptr, "D3D12 consumer timeout token");
    lkg_worker_test_delay_d3d12_consumer(80);
    callback(0, consumerTimeoutData);
    const ULONGLONG consumerTimeoutDeadline = GetTickCount64() + 2000;
    LkgWorkerStatus consumerTimeoutStatus{};
    while (GetTickCount64() < consumerTimeoutDeadline) {
        Check(lkg_worker_poll(handle, &consumerTimeoutStatus) == 0, "D3D12 consumer timeout poll");
        if (consumerTimeoutStatus.state == LKG_WORKER_FAULT) break;
        Sleep(1);
    }
    if (consumerTimeoutStatus.state != LKG_WORKER_FAULT) {
        std::fprintf(stderr, "D3D12 consumer timeout state=%u submitted=%u completed=%u dropped=%u code=%u pendingPolls=%u error=%s\n",
                     consumerTimeoutStatus.state, consumerTimeoutStatus.submitted, consumerTimeoutStatus.completed,
                     consumerTimeoutStatus.dropped, consumerTimeoutStatus.lastErrorCode,
                     lkg_worker_test_d3d12_consumer_pending_polls(), consumerTimeoutStatus.error);
        Fail("D3D11 consumer query timeout fault");
    }
    Check(lkg_worker_release(handle) == -2, "D3D12 consumer timeout remains pending");
    ReleaseAfterFault(handle, "D3D12 late consumer completion cleanup");
    handle = lkg_worker_begin("test-runtime", "worker-transport-d3d12-timeout-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 timeout worker begin");
    Check(WaitReady(handle), "D3D12 timeout worker ready");
    Check(lkg_worker_test_set_d3d12_timeout(handle, 40) == 0, "D3D12 test timeout setup");
    auto timeoutTexture = MakeD3D12PatternTexture({ 113, 157, 201 });
    void* timeoutData = lkg_worker_make_draw_data(handle, timeoutTexture.Get(), 4, 3, 1.0f, 1.0f);
    Check(timeoutData != nullptr, "D3D12 timeout token");
    g_testD3D12GateValue.store(3);
    g_testD3D12GateProducer.store(true);
    callback(0, timeoutData);
    g_testD3D12GateProducer.store(false);
    const ULONGLONG timeoutDeadline = GetTickCount64() + 2000;
    LkgWorkerStatus timeoutStatus{};
    while (GetTickCount64() < timeoutDeadline) {
        Check(lkg_worker_poll(handle, &timeoutStatus) == 0, "D3D12 timeout poll");
        if (timeoutStatus.state == LKG_WORKER_FAULT) break;
        Sleep(1);
    }
    Check(timeoutStatus.state == LKG_WORKER_FAULT, "D3D12 producer timeout fault");
    Check(lkg_worker_release(handle) == -2, "D3D12 timed out slot quarantined");
    Check(SUCCEEDED(g_testD3D12GateFence->Signal(3)), "D3D12 late producer completion");
    ReleaseAfterFault(handle, "D3D12 late producer cleanup");
    g_testD3D12GateValue.store(3);
    g_testD3D12Generation.store(2);
    handle = lkg_worker_begin("test-runtime", "worker-transport-d3d12-generation-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 generation worker begin");
    Check(WaitReady(handle), "D3D12 generation worker ready");
    auto staleTexture = MakeD3D12PatternTexture({ 71, 112, 183 });
    void* staleData = lkg_worker_make_draw_data(handle, staleTexture.Get(), 4, 3, 1.0f, 1.0f);
    Check(staleData != nullptr, "D3D12 generation token");
    callback(0, staleData);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "D3D12 generation mismatch fault");
    ReleaseAfterFault(handle, "D3D12 generation mismatch release");
    g_testD3D12Generation.store(1);
    auto verifyUnknownSubmission = [&](uint64_t returnedValue, const char* name) {
        void* unknownHandle = lkg_worker_begin("test-runtime", name, 0, g_testD3D12Device.Get());
        Check(unknownHandle != nullptr, "D3D12 unknown worker begin");
        Check(WaitReady(unknownHandle), "D3D12 unknown worker ready");
        auto unknownTexture = MakeD3D12PatternTexture({ 137, 173, 219 });
        void* unknownData = lkg_worker_make_draw_data(unknownHandle, unknownTexture.Get(), 4, 3, 1.0f, 1.0f);
        Check(unknownData != nullptr, "D3D12 unknown token");
        g_testD3D12SubmitOverride.store(returnedValue);
        g_testD3D12SubmitOverrideActive.store(true);
        callback(0, unknownData);
        Check(WaitState(unknownHandle, LKG_WORKER_FAULT, 2000), "D3D12 unknown submission fault");
        Check(lkg_worker_release(unknownHandle) == -2, "D3D12 unknown submission remains pending");
        Sleep(25);
        LkgWorkerStatus unknownStatus{};
        Check(lkg_worker_poll(unknownHandle, &unknownStatus) == 0 && unknownStatus.state == LKG_WORKER_FAULT &&
              unknownStatus.completed == 0 && unknownStatus.dropped == 0, "D3D12 invalid return is not treated as completion");
        lkg_worker_test_d3d12_set_removed(true);
        ReleaseAfterFault(unknownHandle, "D3D12 unknown submission removed-device retirement");
        lkg_worker_test_d3d12_set_removed(false);
        Check(lkg_worker_pending_count() == 0, "D3D12 unknown submission pending drained after removal");
    };
    verifyUnknownSubmission(0, "worker-transport-d3d12-return-zero-test");
    verifyUnknownSubmission(UINT64_MAX, "worker-transport-d3d12-return-max-test");
    g_testingD3D12.store(false);
    g_testD3D12Device.Reset();
    g_testD3D12Queue.Reset();
    g_testD3D12Fence.Reset();
    g_testD3D12GateFence.Reset();
}

int main() {
    BeginDevice();
    TestD3D12RingColors();
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
