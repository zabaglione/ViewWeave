#include "bridge_worker_transport.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include "bridge_d3d12_internal.h"
#include "worker_transport_test_hooks.h"
#include <wrl/client.h>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <cctype>
#include <cstdlib>
#include <thread>
#include <vector>
#include <stdexcept>
#include <deque>
#include <mutex>
#include <array>
#include <algorithm>
#include <string>

using Microsoft::WRL::ComPtr;

#if defined(LKG_WORKER_TRANSPORT_TESTING)
uint32_t lkg_worker_test_thread_id(void* handle);
HWND lkg_worker_test_window(void* handle);
uint32_t lkg_worker_test_message_count(void* handle);
uintptr_t lkg_worker_test_context_device_identity(void* handle);
int32_t lkg_worker_test_get_transfer_slot_identity(void* handle, uintptr_t* producerTexture,
    uintptr_t* workerTexture, uint64_t* generation);
uint32_t lkg_worker_test_host_device_create_count(void);
uintptr_t lkg_worker_test_host_device_identity(void);
int32_t lkg_worker_test_get_preview_lease_resources(void* handle, uint64_t* endpointId,
    uint64_t* generation, uint64_t* sessionId, uintptr_t* stableInputTexture);
void lkg_worker_test_force_adapter_mismatch(void);
int32_t lkg_worker_test_set_d3d12_timeout(void* handle, uint64_t timeoutMilliseconds);
void lkg_worker_test_throw_during_cleanup(void);
void lkg_worker_test_throw_during_display_query(void);
void lkg_worker_test_delay_d3d12_consumer(uint32_t pendingPolls);
uint32_t lkg_worker_test_d3d12_consumer_pending_polls(void);
void lkg_worker_test_d3d12_set_removed(bool removed);
extern "C" {
void lkg_worker_test_configure_source_copy(uint32_t pendingPolls, uint32_t queryFailures,
    uint32_t presenterEarlyResult, bool throwAfterCopy, bool holdQuery);
int32_t lkg_worker_test_get_source_copy_state(void* handle, uint32_t slotIndex, uint32_t* flags);
int32_t lkg_worker_test_get_presenter_visibility(void* handle, uint32_t* visible);
int32_t lkg_worker_test_readback_presenter_rgba8(void* handle, uint8_t* rgba, uint32_t capacity,
    uint32_t* width, uint32_t* height, uint32_t* requiredBytes, uint32_t timeoutMilliseconds);
void lkg_worker_test_throw_after_worker_dequeue(void);
int32_t lkg_worker_test_fail_window_poll(void* handle);
}
#endif
extern "C" int32_t lkg_worker_shutdown_host(uint32_t timeoutMilliseconds);

static ComPtr<ID3D11Device> g_device;
static std::atomic<bool> g_initializeFailure{false};
static std::atomic<uint32_t> g_hostInitializeCount{0};
static std::atomic<uint32_t> g_hostUninitializeCount{0};
static std::atomic<uint32_t> g_hostLoadCount{0};
static std::atomic<uint32_t> g_hostUnloadCount{0};
static std::atomic<bool> g_fakeHostSdkOwned{false};
static std::atomic<bool> g_fakeHostFaulted{false};
static std::atomic<bool> g_fakeHostFinalizationFailure{false};
static std::atomic<unsigned long> g_hostFinalizerThread{0};
static std::mutex g_fakeHostConfigMutex;
static std::string g_fakeHostRuntime;
static std::string g_fakeHostApplication;
static std::string g_fakeLastError;
static std::atomic<uint32_t> g_drawCount{0};
static std::atomic<uint32_t> g_sdkDrawCalls{0};
static std::atomic<bool> g_holdDraw{false};
static std::atomic<bool> g_drawEntered{false};
static std::atomic<uint64_t> g_fakeSessionCounter{0};
static std::atomic<uint64_t> g_lastCreatedSession{0};
static std::atomic<uint64_t> g_lastDrawSession{0};
static std::atomic<uintptr_t> g_lastDrawTextureIdentity{0};
static std::atomic<uint32_t> g_lastDrawColumns{0};
static std::atomic<uint32_t> g_lastDrawRows{0};
static std::atomic<float> g_lastDrawAspect{0.0f};
static std::atomic<float> g_lastDrawZoom{0.0f};
static std::atomic<uint32_t> g_staticPresentAttempts{0};
static std::atomic<uint32_t> g_staticPresentDraws{0};
static std::atomic<uint64_t> g_staticPresentSession{0};
static std::atomic<uint64_t> g_staticPresentEndpoint{0};
static std::atomic<uint64_t> g_staticPresentGeneration{0};
static std::atomic<uintptr_t> g_staticPresentPrimary{0};
static std::atomic<uint32_t> g_staticPresentColumns{0};
static std::atomic<uint32_t> g_staticPresentRows{0};
static std::atomic<float> g_staticPresentAspect{0.0f};
static std::atomic<float> g_staticPresentZoom{0.0f};
static std::atomic<unsigned long> g_staticPresentThread{0};
static std::atomic<bool> g_staticPresentFailure{false};
static std::atomic<uint32_t> g_sessionDestroyCount{0};
static std::atomic<uint32_t> g_visibilityCalls{0};
static std::atomic<uint32_t> g_visibilityHideCalls{0};
static std::atomic<uint32_t> g_visibilityShowCalls{0};
static std::atomic<uint32_t> g_visibilityState{1};
static std::atomic<uint32_t> g_visibilityThread{0};
static std::atomic<uint32_t> g_visibilityDrawCount{0};
static std::atomic<uint64_t> g_visibilitySession{0};
static std::atomic<int32_t> g_visibilityFailureFor{-1};
static std::atomic<int> g_testCalibrationMode{0};
static std::atomic<uint32_t> g_testDisplayCount{1};
static std::atomic<bool> g_testHoldDisplayQuery{false};
static std::atomic<bool> g_testDisplayQueryEntered{false};
static std::atomic<bool> g_testReleaseDisplayQuery{false};
static std::atomic<bool> g_workerClaimed{false};
static std::atomic<unsigned long> g_producerThread{0};
static std::atomic<unsigned long> g_workerThread{0};
static std::atomic<unsigned long> g_lastDisplayQueryThread{0};
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
    if (WaitState(handle, LKG_WORKER_READY, timeoutMs)) return true;
    LkgWorkerStatus status{};
    if (lkg_worker_poll(handle, &status) == 0) {
        std::fprintf(stderr, "worker ready timeout state=%u code=%u error=%s\n",
            status.state, status.lastErrorCode, status.error);
    }
    return false;
}

static uint32_t PreviewPausePhase(void* handle) {
    uint32_t phase = UINT32_MAX;
    Check(lkg_worker_get_preview_pause_state(handle, &phase) == 0, "query preview pause phase");
    return phase;
}

static bool WaitPreviewPausePhase(void* handle, uint32_t phase, uint32_t timeoutMs = 2000) {
    const auto deadline = GetTickCount64() + timeoutMs;
    do {
        if (PreviewPausePhase(handle) == phase) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    } while (GetTickCount64() < deadline);
    return PreviewPausePhase(handle) == phase;
}

static bool WaitStaticPresentAttempts(uint32_t target, uint32_t timeoutMs = 2000) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    while (GetTickCount64() < deadline) {
        if (g_staticPresentAttempts.load(std::memory_order_acquire) >= target) return true;
        Sleep(1);
    }
    return g_staticPresentAttempts.load(std::memory_order_acquire) >= target;
}

static bool WaitPreviewLeaseState(void* handle, const LkgWorkerPreviewLeaseIdentityV1& identity,
    uint32_t state, uint32_t timeoutMs = 5000) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    LkgWorkerPreviewLeaseStatusV1 status{};
    do {
        if (lkg_worker_get_preview_lease_status_v1(handle, &identity, &status) == 0 && status.state == state)
            return true;
        Sleep(1);
    } while (GetTickCount64() < deadline);
    return lkg_worker_get_preview_lease_status_v1(handle, &identity, &status) == 0 && status.state == state;
}

static bool WaitLeaseWorkerState(void* handle, uint32_t state, uint32_t timeoutMs = 5000) {
    return WaitState(handle, state, timeoutMs);
}

static ULONG ReferenceCount(ID3D11Device* device) {
    if (!device) return 0;
    device->AddRef();
    return device->Release();
}

static ULONG ReferenceCount(IUnknown* object) {
    if (!object) return 0;
    object->AddRef();
    return object->Release();
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

static bool InitializeD3D12TestDevice();

static void BeginDevice() {
    D3D_FEATURE_LEVEL selected{};
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    bool created = false;
    if (InitializeD3D12TestDevice()) {
        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory4> factory;
        const LUID adapterLuid = g_testD3D12Device->GetAdapterLuid();
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) &&
            SUCCEEDED(factory->EnumAdapterByLuid(adapterLuid, IID_PPV_ARGS(&adapter))) &&
            SUCCEEDED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, 2, D3D11_SDK_VERSION, &g_device, &selected, nullptr))) {
            created = true;
        } else {
            g_testD3D12Fence.Reset();
            g_testD3D12GateFence.Reset();
            g_testD3D12Queue.Reset();
            g_testD3D12Device.Reset();
        }
    }
    if (!created) {
        Check(SUCCEEDED(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            levels, 2, D3D11_SDK_VERSION, &g_device, &selected, nullptr)), "D3D11CreateDevice");
    }
}

static void BeginContext(void** handle) {
    *handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, nullptr);
    Check(*handle != nullptr, "worker begin");
    Check(WaitReady(*handle), "worker ready");
}

static void* MakeWorkerDrawData(void* handle, void* texture, uint32_t columns, uint32_t rows,
    float aspect, float zoom) {
    const LkgWorkerDrawOptionsV1 options{ sizeof(LkgWorkerDrawOptionsV1), 1,
        LKG_WORKER_SOURCE_ENCODING_UNORM, 0 };
    return lkg_worker_make_draw_data_with_options_v1(handle, texture, columns, rows, aspect, zoom, &options);
}

static void* MakeWorkerDrawDataWithEncoding(void* handle, void* texture, uint32_t columns, uint32_t rows,
    float aspect, float zoom, uint32_t sourceEncoding) {
    const LkgWorkerDrawOptionsV1 options{ sizeof(LkgWorkerDrawOptionsV1), 1, sourceEncoding, 0 };
    return lkg_worker_make_draw_data_with_options_v1(handle, texture, columns, rows, aspect, zoom, &options);
}

static int32_t TryMakePreviewWorkerDrawData(void* handle, const LkgWorkerPreviewLeaseIdentityV1* identity,
    void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom, void** drawData) {
    const LkgWorkerDrawOptionsV1 options{ sizeof(LkgWorkerDrawOptionsV1), 1,
        LKG_WORKER_SOURCE_ENCODING_UNORM, 0 };
    return lkg_worker_try_make_preview_draw_data_with_options_v1(handle, identity, texture,
        columns, rows, aspect, zoom, &options, drawData);
}

static int32_t TryMakePreviewWorkerDrawDataWithEncoding(void* handle,
    const LkgWorkerPreviewLeaseIdentityV1* identity, void* texture, uint32_t columns, uint32_t rows,
    float aspect, float zoom, uint32_t sourceEncoding, void** drawData) {
    const LkgWorkerDrawOptionsV1 options{ sizeof(LkgWorkerDrawOptionsV1), 1, sourceEncoding, 0 };
    return lkg_worker_try_make_preview_draw_data_with_options_v1(handle, identity, texture,
        columns, rows, aspect, zoom, &options, drawData);
}

static LkgWorkerSubpixelStatusV1 GetSubpixelStatus(void* handle) {
    LkgWorkerSubpixelStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    Check(lkg_worker_get_subpixel_status_v1(handle, &status) == 0, "query NativeD3D11 subpixel status");
    Check(status.valid == 1 && status.backend == LKG_WORKER_SUBPIXEL_BACKEND_NATIVE_D3D11,
        "worker reports a valid native D3D11 presenter");
    return status;
}

static bool WaitSubpixelFrames(void* handle, uint32_t renderCompleted, uint32_t presentAccepted,
    uint32_t timeoutMs = 5000) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    do {
        LkgWorkerStatus worker{};
        if (lkg_worker_poll(handle, &worker) != 0 || worker.state == LKG_WORKER_FAULT ||
            worker.state == LKG_WORKER_CLOSED) return false;
        const LkgWorkerSubpixelStatusV1 presenter = GetSubpixelStatus(handle);
        if (presenter.renderCompleted >= renderCompleted && presenter.presentAccepted >= presentAccepted)
            return true;
        Sleep(1);
    } while (GetTickCount64() < deadline);
    return false;
}

static std::vector<uint8_t> ReadbackPresenter(void* handle, uint32_t expectedWidth, uint32_t expectedHeight) {
    const uint64_t byteCount64 = static_cast<uint64_t>(expectedWidth) * expectedHeight * 4;
    Check(byteCount64 <= UINT32_MAX, "presenter readback size is bounded");
    std::vector<uint8_t> pixels(static_cast<size_t>(byteCount64));
    uint32_t width = 0, height = 0, requiredBytes = 0;
    Check(lkg_worker_test_readback_presenter_rgba8(handle, pixels.data(),
        static_cast<uint32_t>(pixels.size()), &width, &height, &requiredBytes, 5000) == 0,
        "worker-thread presenter readback completes after GPU proof");
    Check(width == expectedWidth && height == expectedHeight && requiredBytes == pixels.size(),
        "presenter readback dimensions match native output");
    return pixels;
}

static void CheckRenderedPresenterPixels(const std::vector<uint8_t>& pixels, const char* message) {
    bool hasColor = false;
    Check((pixels.size() % 4) == 0, "presenter readback has RGBA pixels");
    for (size_t index = 0; index < pixels.size(); index += 4) {
        hasColor = hasColor || pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0;
        Check(pixels[index + 3] == 255, "presenter output alpha is opaque");
    }
    Check(hasColor, message);
}

static void CheckUniformPresenterColor(const std::vector<uint8_t>& pixels,
    const std::array<uint8_t, 3>& expected, uint8_t tolerance, const char* message) {
    Check(!pixels.empty() && (pixels.size() % 4) == 0, "presenter readback contains complete pixels");
    for (size_t index = 0; index < pixels.size(); index += 4) {
        for (size_t channel = 0; channel < 3; ++channel) {
            const int delta = static_cast<int>(pixels[index + channel]) - expected[channel];
            Check(std::abs(delta) <= tolerance, message);
        }
        Check(pixels[index + 3] == 255, "presenter output alpha is opaque");
    }
}

static void EndContext(void* handle);
static void ReleaseAfterFault(void* handle, const char* message);

static void TestCachedPhysicalDisplayCalibration() {
    g_testCalibrationMode.store(0);
    void* handle = nullptr;
    BeginContext(&handle);
    LkgWorkerPhysicalDisplayInfoV1 info{};
    info.structSize = sizeof(info);
    info.version = 1;
    Check(lkg_worker_get_physical_display_v1(handle, &info) == 0 && info.valid == 1, "cached physical calibration available");
    Check(info.pixelWidth == 1920 && info.pixelHeight == 1080 && info.dpi == 110.0f, "cached physical calibration values");
    info.structSize = sizeof(info) - 1;
    Check(lkg_worker_get_physical_display_v1(handle, &info) == -1, "cached physical calibration validates ABI size");
    EndContext(handle);

    g_testCalibrationMode.store(1);
    BeginContext(&handle);
    info = {};
    info.structSize = sizeof(info);
    info.version = 1;
    Check(lkg_worker_get_physical_display_v1(handle, &info) == 0 && info.valid == 0 && info.pixelWidth == 0 && info.dpi == 0.0f,
        "invalid vendor DPI becomes unavailable calibration");
    EndContext(handle);
}

static bool WaitForDisplaySnapshot(void* handle, uint64_t generation, uint32_t timeoutMs = 2000) {
    const auto start = GetTickCount64();
    while (GetTickCount64() - start < timeoutMs) {
        LkgWorkerDisplaySnapshotStatusV1 status{};
        status.structSize = sizeof(status);
        status.version = 1;
        uint32_t required = 0;
        const int32_t result = lkg_worker_get_display_snapshot_v1(handle, &status, nullptr, 0, &required);
        if ((result == 0 || result == -3) && status.snapshotGeneration >= generation && status.pending == 0) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

static bool WaitForMetadataClose(void* handle, uint32_t timeoutMs = 2000) {
    const auto start = GetTickCount64();
    while (GetTickCount64() - start < timeoutMs) {
        LkgWorkerDisplaySnapshotStatusV1 status{};
        status.structSize = sizeof(status);
        status.version = 1;
        uint32_t required = 0;
        const int32_t result = lkg_worker_get_display_snapshot_v1(handle, &status, nullptr, 0, &required);
        if ((result == 0 || result == -3) && status.state == LKG_WORKER_CLOSED &&
            status.pending == 0 && status.cleanupFinished == 1) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

static LkgWorkerDisplaySnapshotStatusV1 GetDisplaySnapshot(void* handle, std::vector<LkgWorkerDisplaySnapshotItemV1>& items) {
    LkgWorkerDisplaySnapshotStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    uint32_t required = 0;
    int32_t result = lkg_worker_get_display_snapshot_v1(handle, &status, nullptr, 0, &required);
    Check(result == 0 || result == -3, "metadata snapshot size query");
    items.resize(required);
    status = {};
    status.structSize = sizeof(status);
    status.version = 1;
    result = lkg_worker_get_display_snapshot_v1(handle, &status, items.data(), static_cast<uint32_t>(items.size()), &required);
    Check(result == 0, "metadata snapshot list query");
    Check(required == items.size(), "metadata snapshot count consistency");
    return status;
}

static void TestMetadataOnlyRefreshLifecycle() {
    g_testDisplayCount.store(1);
    g_testCalibrationMode.store(0);
    g_testHoldDisplayQuery.store(false);
    g_testDisplayQueryEntered.store(false);
    g_testReleaseDisplayQuery.store(false);
    void* handle = lkg_worker_begin_metadata_v1("test-runtime", "worker-transport-test");
    Check(handle != nullptr, "metadata-only begin");
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    Check(lkg_worker_test_window(handle) == nullptr, "metadata-only worker creates no window");
    Check(lkg_worker_test_message_count(handle) == 0, "metadata-only worker pumps no window messages");
#endif
    Check(lkg_worker_begin_metadata_v1("metadata-runtime", "concurrent-metadata-test") == nullptr,
        "concurrent SDK metadata owner rejected");
    Check(WaitForDisplaySnapshot(handle, 1), "initial metadata snapshot ready");

    std::vector<LkgWorkerDisplaySnapshotItemV1> items;
    LkgWorkerDisplaySnapshotStatusV1 status = GetDisplaySnapshot(handle, items);
    Check(status.state == LKG_WORKER_READY && status.snapshotGeneration == 1 && status.result == 0 && items.size() == 1,
        "initial metadata snapshot header");
    Check(items[0].structSize == sizeof(items[0]) && items[0].version == 1 && items[0].displayOrdinal == 0 &&
        items[0].profile.index == 0 && items[0].physical.valid == 1 && items[0].physical.pixelWidth == 1920 &&
        items[0].physical.structSize == sizeof(items[0].physical) && items[0].physical.version == 1 &&
        items[0].physical.pixelHeight == 1080 && items[0].physical.dpi == 110.0f,
        "metadata display profile and calibration values");
    LkgWorkerDisplaySnapshotStatusV1 invalidStatus = status;
    invalidStatus.structSize -= 1;
    uint32_t required = 0;
    Check(lkg_worker_get_display_snapshot_v1(handle, &invalidStatus, nullptr, 0, &required) == -1,
        "metadata snapshot validates ABI size");

    uint64_t generation = 0;
    g_testDisplayCount.store(0);
    Check(lkg_worker_request_display_refresh_v1(handle, &generation) == 0 && generation == 2,
        "request no-device metadata refresh");
    Check(WaitForDisplaySnapshot(handle, generation), "no-device metadata refresh completes");
    status = GetDisplaySnapshot(handle, items);
    Check(status.result == 0 && status.itemCount == 0 && items.empty(), "zero connected displays is a successful empty snapshot");

    g_testDisplayCount.store(2);
    Check(lkg_worker_request_display_refresh_v1(handle, &generation) == 0 && generation == 3,
        "request hot-plug metadata refresh");
    Check(WaitForDisplaySnapshot(handle, generation), "hot-plug metadata refresh completes");
    status = GetDisplaySnapshot(handle, items);
    Check(status.result == 0 && status.itemCount == 2 && items.size() == 2 && items[1].displayOrdinal == 1 && items[1].profile.index == 1,
        "later connected display appears in refreshed snapshot");

    g_testHoldDisplayQuery.store(true);
    g_testDisplayQueryEntered.store(false);
    g_testReleaseDisplayQuery.store(false);
    Check(lkg_worker_request_display_refresh_v1(handle, &generation) == 0 && generation == 4,
        "request refresh before close");
    const auto waitStart = GetTickCount64();
    while (!g_testDisplayQueryEntered.load(std::memory_order_acquire) && GetTickCount64() - waitStart < 2000)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    Check(g_testDisplayQueryEntered.load(std::memory_order_acquire), "metadata query entered before close");
    Check(lkg_worker_close(handle) == 0, "metadata close accepted during active refresh");
    uint64_t ignoredGeneration = 0;
    Check(lkg_worker_request_display_refresh_v1(handle, &ignoredGeneration) == -3, "refresh rejected after close begins");
    Check(lkg_worker_release(handle) == -2, "metadata owner cannot release before cleanup completes");
    g_testReleaseDisplayQuery.store(true, std::memory_order_release);
    Check(WaitState(handle, LKG_WORKER_CLOSED, 2000), "metadata worker closes after active query returns");
    Check(lkg_worker_collect_closed() == 0, "metadata snapshot survives collect-closed");
    status = GetDisplaySnapshot(handle, items);
    Check(status.snapshotGeneration == 3 && status.requestedGeneration == 4 && status.cleanupFinished == 1 &&
        status.itemCount == 2 && status.result == 0,
        "close takes priority over publishing the in-flight refresh and preserves the last snapshot");
    Check(lkg_worker_pending_count() == 1, "closed metadata handle remains pending until managed release");
    Check(lkg_worker_end_metadata_v1(handle) == 0, "metadata owner releases closed snapshot");
    Check(lkg_worker_get_display_snapshot_v1(handle, &status, nullptr, 0, &required) == -2,
        "metadata snapshot becomes unavailable after owner release");
    g_testHoldDisplayQuery.store(false);
    g_testDisplayQueryEntered.store(false);
    g_testReleaseDisplayQuery.store(false);
}

static void TestMetadataInitializationFailureIsPublished() {
    g_initializeFailure.store(true);
    void* handle = lkg_worker_begin_metadata_v1("metadata-runtime", "worker-metadata-init-failure-test");
    Check(handle != nullptr, "metadata init failure begin");
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "metadata init failure fault");
    std::vector<LkgWorkerDisplaySnapshotItemV1> items;
    LkgWorkerDisplaySnapshotStatusV1 status{};
    const auto start = GetTickCount64();
    do {
        status = GetDisplaySnapshot(handle, items);
        if (status.cleanupFinished) break;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    } while (GetTickCount64() - start < 2000);
    if (!(status.state == LKG_WORKER_FAULT && status.snapshotGeneration == status.requestedGeneration &&
        status.result != 0 && status.pending == 0 && status.cleanupFinished == 1 && status.error[0] != '\0')) {
        std::fprintf(stderr, "metadata init snapshot state=%u generation=%llu requested=%llu result=%d pending=%u cleanup=%u error=%s\n",
            status.state, static_cast<unsigned long long>(status.snapshotGeneration),
            static_cast<unsigned long long>(status.requestedGeneration), status.result, status.pending,
            status.cleanupFinished, status.error);
    }
    Check(status.state == LKG_WORKER_FAULT && status.snapshotGeneration == status.requestedGeneration &&
        status.result != 0 && status.pending == 0 && status.cleanupFinished == 1 && status.error[0] != '\0',
        "metadata initialization failure completes snapshot with an error");
    Check(lkg_worker_end_metadata_v1(handle) == -3,
        "metadata init failure retains the context because SDK cleanup was not proven");
    g_initializeFailure.store(false);
}

static void TestPersistentSdkConfigurationMismatchIsRejected() {
    auto expectMismatch = [](const char* path, const char* app, const char* label) {
        void* handle = lkg_worker_begin_metadata_v1(path, app);
        Check(handle != nullptr, "mismatched metadata context accepted for asynchronous fault reporting");
        Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "mismatched metadata configuration faults context");
        std::vector<LkgWorkerDisplaySnapshotItemV1> items;
        LkgWorkerDisplaySnapshotStatusV1 status{};
        const auto start = GetTickCount64();
        do {
            status = GetDisplaySnapshot(handle, items);
            if (status.cleanupFinished) break;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        } while (GetTickCount64() - start < 2000);
        if (!(status.cleanupFinished == 1 && status.result != 0 && status.error[0] != '\0' &&
            std::strstr(status.error, "different runtime path or application name") != nullptr)) {
            std::fprintf(stderr, "configuration mismatch label=%s error=%s\n", label, status.error);
        }
        Check(status.cleanupFinished == 1 && status.result != 0 && status.error[0] != '\0' &&
            std::strstr(status.error, "different runtime path or application name") != nullptr,
            "persistent SDK reports an explicit configuration mismatch");
        Check(lkg_worker_end_metadata_v1(handle) == 0, "mismatched metadata context releases independently");
    };
    expectMismatch("different-runtime", "worker-transport-test", "runtime");
    expectMismatch("test-runtime", "different-application", "application");
    Check(g_hostInitializeCount.load() == 1 && g_hostUninitializeCount.load() == 0 &&
        g_fakeHostSdkOwned.load(), "configuration mismatch preserves host SDK ownership");
}

static void TestMetadataAfterRenderReusesPersistentSdk() {
    void* handle = lkg_worker_begin_metadata_v1("TEST-RUNTIME", "worker-transport-test");
    Check(handle != nullptr, "metadata after render begin with canonical equivalent path");
    Check(WaitForDisplaySnapshot(handle, 1), "metadata after render query ready");
    Check(lkg_worker_close(handle) == 0, "metadata after render close");
    Check(WaitState(handle, LKG_WORKER_CLOSED, 2000), "metadata after render context cleanup");
    Check(lkg_worker_end_metadata_v1(handle) == 0, "metadata after render context release");
    Check(g_hostInitializeCount.load() == 1 && g_hostUninitializeCount.load() == 0 &&
        g_hostLoadCount.load() == 1 && g_hostUnloadCount.load() == 0,
        "metadata-render-metadata uses one host SDK initialization until final shutdown");
}

static void TestPersistentD3D11DeviceReuseAndAdapterGate() {
    void* metadataBefore = lkg_worker_begin_metadata_v1("test-runtime", "worker-transport-test");
    Check(metadataBefore != nullptr && WaitForDisplaySnapshot(metadataBefore, 1),
        "metadata-only query completes before the first rendering context");
    Check(lkg_worker_close(metadataBefore) == 0 && WaitState(metadataBefore, LKG_WORKER_CLOSED, 2000),
        "metadata-only context closes before the first rendering context");
    Check(lkg_worker_end_metadata_v1(metadataBefore) == 0, "metadata-only context releases before first render");
    Check(lkg_worker_test_host_device_create_count() == 0 && lkg_worker_test_host_device_identity() == 0,
        "metadata-only contexts do not create a D3D11 device");

    void* first = nullptr;
    BeginContext(&first);
    const uintptr_t firstIdentity = lkg_worker_test_context_device_identity(first);
    Check(firstIdentity != 0 && firstIdentity == lkg_worker_test_host_device_identity(),
        "first render context borrows the host-owned D3D11 device");
    Check(lkg_worker_test_host_device_create_count() == 1, "host creates one persistent D3D11 device");
    EndContext(first);

    void* second = nullptr;
    BeginContext(&second);
    const uintptr_t secondIdentity = lkg_worker_test_context_device_identity(second);
    Check(secondIdentity == firstIdentity && lkg_worker_test_host_device_create_count() == 1,
        "successive render context borrows the same D3D11 COM identity");
    EndContext(second);

    void* metadataAfter = lkg_worker_begin_metadata_v1("TEST-RUNTIME", "worker-transport-test");
    Check(metadataAfter != nullptr && WaitForDisplaySnapshot(metadataAfter, 1),
        "metadata-only query succeeds between rendering contexts");
    Check(lkg_worker_close(metadataAfter) == 0 && WaitState(metadataAfter, LKG_WORKER_CLOSED, 2000),
        "interleaved metadata-only context closes safely");
    Check(lkg_worker_end_metadata_v1(metadataAfter) == 0, "interleaved metadata-only context releases");
    Check(lkg_worker_test_host_device_identity() == firstIdentity &&
        lkg_worker_test_host_device_create_count() == 1,
        "metadata context preserves the persistent rendering device");

    void* third = nullptr;
    BeginContext(&third);
    Check(lkg_worker_test_context_device_identity(third) == firstIdentity,
        "render after metadata reuses the original D3D11 COM identity");
    EndContext(third);

    lkg_worker_test_force_adapter_mismatch();
    void* mismatched = lkg_worker_begin("test-runtime", "worker-transport-test", 0, nullptr);
    Check(mismatched != nullptr, "adapter mismatch is reported through a worker context");
    Check(WaitState(mismatched, LKG_WORKER_FAULT, 2000), "adapter mismatch faults without creating another device");
    LkgWorkerStatus mismatchStatus{};
    Check(lkg_worker_poll(mismatched, &mismatchStatus) == 0 &&
        std::strstr(mismatchStatus.error, "differs from persistent worker") != nullptr &&
        std::strstr(mismatchStatus.error, "restart required") != nullptr,
        "adapter mismatch reports restart requirement");
    ReleaseAfterFault(mismatched, "adapter mismatch cleanup release");
    Check(lkg_worker_test_host_device_create_count() == 1 &&
        lkg_worker_test_host_device_identity() == firstIdentity,
        "adapter mismatch retains the original host device and does not recreate it");
    Check(lkg_worker_shutdown_host(5000) == 0, "persistent D3D11 host finalization succeeds");
    Check(lkg_worker_test_host_device_identity() == 0, "host releases its D3D11 device after SDK finalization");
    Check(g_hostInitializeCount.load() == 1 && g_hostUninitializeCount.load() == 1 &&
        !g_fakeHostSdkOwned.load(), "render-metadata-render host SDK finalizes exactly once");
}

static void TestActiveWorkerDisplayRefreshDoesNotMutateRenderStatus() {
    g_testDisplayCount.store(1);
    void* handle = nullptr;
    BeginContext(&handle);
    LkgWorkerStatus before{};
    Check(lkg_worker_poll(handle, &before) == 0 && before.state == LKG_WORKER_READY, "active worker status before metadata refresh");
    g_testDisplayCount.store(2);
    uint64_t generation = 0;
    Check(lkg_worker_request_display_refresh_v1(handle, &generation) == 0 && generation == 1,
        "active rendering worker accepts independent display refresh");
    Check(WaitForDisplaySnapshot(handle, generation), "active rendering worker metadata refresh completes");
    std::vector<LkgWorkerDisplaySnapshotItemV1> items;
    LkgWorkerDisplaySnapshotStatusV1 snapshot = GetDisplaySnapshot(handle, items);
    Check(snapshot.itemCount == 2 && items.size() == 2, "active worker returns independent display list");
    LkgWorkerStatus after{};
    Check(lkg_worker_poll(handle, &after) == 0, "active worker status after metadata refresh");
    Check(after.state == before.state && after.displayIndex == before.displayIndex && after.width == before.width &&
        after.height == before.height && after.aspect == before.aspect && after.viewcone == before.viewcone &&
        after.quiltWidth == before.quiltWidth && after.quiltHeight == before.quiltHeight &&
        after.quiltColumns == before.quiltColumns && after.quiltRows == before.quiltRows &&
        after.submitted == before.submitted && after.completed == before.completed,
        "metadata refresh does not mutate the active rendering profile or status");

    lkg_worker_test_throw_during_display_query();
    Check(lkg_worker_request_display_refresh_v1(handle, &generation) == 0 && generation == 2,
        "active worker accepts refresh that will throw during enumeration");
    const bool throwingRefreshCompleted = WaitForDisplaySnapshot(handle, generation);
    if (!throwingRefreshCompleted) {
        std::vector<LkgWorkerDisplaySnapshotItemV1> failedItems;
        const LkgWorkerDisplaySnapshotStatusV1 failedStatus = GetDisplaySnapshot(handle, failedItems);
        std::fprintf(stderr, "throwing refresh state=%u generation=%llu requested=%llu result=%d pending=%u error=%s\n",
            failedStatus.state, static_cast<unsigned long long>(failedStatus.snapshotGeneration),
            static_cast<unsigned long long>(failedStatus.requestedGeneration), failedStatus.result,
            failedStatus.pending, failedStatus.error);
    }
    Check(throwingRefreshCompleted, "throwing metadata refresh completes with a result");
    snapshot = GetDisplaySnapshot(handle, items);
    Check(snapshot.result != 0 && snapshot.error[0] != '\0', "throwing metadata query becomes snapshot error");
    LkgWorkerStatus afterException{};
    Check(lkg_worker_poll(handle, &afterException) == 0 && afterException.state == LKG_WORKER_READY &&
        afterException.displayIndex == before.displayIndex && afterException.width == before.width &&
        afterException.height == before.height && afterException.viewcone == before.viewcone,
        "metadata query exception does not fault active rendering worker");
    EndContext(handle);
    g_testDisplayCount.store(1);
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
    void* first = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
    void* second = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
    void* third = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
    void* fourth = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
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
        void* data = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
        if (data) callback(0, data);
        else std::this_thread::yield();
    }
    Check(WaitCompleted(handle, target, 5000), "120 draw completion");
    Check(WaitSubpixelFrames(handle, target, target), "native presenter completed and accepted all bounded frames");
    const auto pixels = ReadbackPresenter(handle, 32, 32);
    CheckRenderedPresenterPixels(pixels, "native presenter readback contains rendered color output");
    Check(lkg_worker_reset_transfer(handle) == 0, "reset after drain");
    Check(g_workerThread.load() != 0 && g_workerThread.load() != g_producerThread.load(), "worker thread identity");
    EndContext(handle);
}

static void TestTypelessSource() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTypelessTexture(24, 24);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    void* data = MakeWorkerDrawDataWithEncoding(handle, texture.Get(), 4, 2, 1.0f, 1.0f,
        LKG_WORKER_SOURCE_ENCODING_SRGB);
    Check(data != nullptr, "typeless token");
    callback(0, data);
    Check(WaitCompleted(handle, 1, 5000), "typeless completion");
    Check(WaitSubpixelFrames(handle, 1, 1), "typeless source reaches native presenter");
    const auto pixels = ReadbackPresenter(handle, 32, 32);
    CheckRenderedPresenterPixels(pixels, "typeless SRGB-declared transfer renders nonempty pixels");
    EndContext(handle);
}

static void ResetVisibilityTestState() {
    g_holdDraw.store(false, std::memory_order_release);
    g_drawEntered.store(false, std::memory_order_release);
    g_visibilityCalls.store(0, std::memory_order_release);
    g_visibilityHideCalls.store(0, std::memory_order_release);
    g_visibilityShowCalls.store(0, std::memory_order_release);
    g_visibilityState.store(1, std::memory_order_release);
    g_visibilityThread.store(0, std::memory_order_release);
    g_visibilityDrawCount.store(0, std::memory_order_release);
    g_visibilitySession.store(0, std::memory_order_release);
    g_visibilityFailureFor.store(-1, std::memory_order_release);
    g_lastDrawSession.store(0, std::memory_order_release);
    g_lastDrawTextureIdentity.store(0, std::memory_order_release);
    g_lastDrawColumns.store(0, std::memory_order_release);
    g_lastDrawRows.store(0, std::memory_order_release);
    g_lastDrawAspect.store(0.0f, std::memory_order_release);
    g_lastDrawZoom.store(0.0f, std::memory_order_release);
    g_staticPresentAttempts.store(0, std::memory_order_release);
    g_staticPresentDraws.store(0, std::memory_order_release);
    g_staticPresentSession.store(0, std::memory_order_release);
    g_staticPresentEndpoint.store(0, std::memory_order_release);
    g_staticPresentGeneration.store(0, std::memory_order_release);
    g_staticPresentPrimary.store(0, std::memory_order_release);
    g_staticPresentColumns.store(0, std::memory_order_release);
    g_staticPresentRows.store(0, std::memory_order_release);
    g_staticPresentAspect.store(0.0f, std::memory_order_release);
    g_staticPresentZoom.store(0.0f, std::memory_order_release);
    g_staticPresentThread.store(0, std::memory_order_release);
    g_staticPresentFailure.store(false, std::memory_order_release);
    g_sessionDestroyCount.store(0, std::memory_order_release);
}

static uint32_t GetPresenterVisibility(void* handle) {
    uint32_t visible = UINT32_MAX;
    Check(lkg_worker_test_get_presenter_visibility(handle, &visible) == 0,
        "query native presenter visibility");
    Check(visible <= 1, "native presenter visibility value is valid");
    return visible;
}

static void TestPreviewPauseResumePreservesSessionAndDrains() {
    void* handle = nullptr;
    BeginContext(&handle);
    LkgWorkerSubpixelStatusV1 initial = GetSubpixelStatus(handle);
    Check(initial.requestedMode == LKG_WORKER_SUBPIXEL_AUTOMATIC &&
        initial.effectiveMode == LKG_WORKER_SUBPIXEL_AUTOMATIC && initial.cellCount == 0,
        "default Automatic mode uses the valid scalar calibration fixture");
    Check(GetPresenterVisibility(handle) == 1, "native presenter starts visible");
    Check(PreviewPausePhase(handle) == LKG_WORKER_PREVIEW_RUNNING, "preview starts in running phase");
    Check(lkg_worker_set_preview_paused(handle, 2) == -1, "invalid preview pause value rejected");
    Check(lkg_worker_get_preview_pause_state(handle, nullptr) == -1, "null preview phase output rejected");

    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 32);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    void* token = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
    Check(token != nullptr, "pre-pause render token accepted");
    Check(lkg_worker_set_preview_paused(handle, 1) == 0, "pause request accepted");
    Check(PreviewPausePhase(handle) == LKG_WORKER_PREVIEW_PAUSING,
        "pause waits for already admitted producer work");
    Check(GetPresenterVisibility(handle) == 1, "presenter remains visible until admitted work drains");
    Check(MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f) == nullptr,
        "new submissions rejected immediately while pausing");
    callback(0, token);
    Check(WaitCompleted(handle, 1, 5000), "in-flight producer frame drains before pause");
    Check(WaitSubpixelFrames(handle, 1, 1), "native render and accepted-present counters advance");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_PAUSED), "pause acknowledged after drain");
    Check(GetPresenterVisibility(handle) == 0, "pause hides the native output window");
    LkgWorkerStatus pausedStatus{};
    Check(lkg_worker_poll(handle, &pausedStatus) == 0 && pausedStatus.state == LKG_WORKER_READY,
        "paused worker remains ready");
    Check(MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f) == nullptr,
        "submissions remain rejected while paused");

    Check(lkg_worker_set_preview_paused(handle, 0) == 0, "resume request accepted");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_RUNNING), "resume acknowledged after show");
    Check(GetPresenterVisibility(handle) == 1, "resume shows the native output window");
    token = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
    Check(token != nullptr, "render submission accepted after resume");
    callback(0, token);
    Check(WaitCompleted(handle, 2, 5000), "native output completes after resume");
    Check(WaitSubpixelFrames(handle, 2, 2), "native counters include the resumed frame");
    Check(lkg_worker_reset_transfer(handle) == 0, "native source and output work retire before transfer reset");
    EndContext(handle);
}

static void TestPreviewPauseVisibilityFailuresDoNotAcknowledge() {
    void* handle = nullptr;
    BeginContext(&handle);
    Check(GetPresenterVisibility(handle) == 1, "native presenter starts visible for pause transition");
    Check(lkg_worker_set_preview_paused(handle, 1) == 0, "native presenter pause request accepted");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_PAUSED), "native presenter pause completes");
    Check(GetPresenterVisibility(handle) == 0, "worker observes the actual hidden presenter state");
    Check(lkg_worker_set_preview_paused(handle, 0) == 0, "native presenter resume request accepted");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_RUNNING), "native presenter resume completes");
    Check(GetPresenterVisibility(handle) == 1, "worker observes the actual visible presenter state");
    EndContext(handle);
}

static void TestWindowPollFailureFaultsAndDrains() {
    void* handle = nullptr;
    BeginContext(&handle);
    Check(lkg_worker_test_fail_window_poll(handle) == 0,
        "native window health failure is configured for the active worker");
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000),
        "persistent presenter window health failure faults the worker");
    ReleaseAfterFault(handle, "window health fault drains and releases worker resources");
    Check(lkg_worker_pending_count() == 0,
        "window health fault cleanup is proven before worker collection");
}

static void TestPreviewLeaseReusesSessionAndStableSdkInput() {
    LkgWorkerOutputOptionsV1 outputOptions{ sizeof(LkgWorkerOutputOptionsV1), 1,
        LKG_WORKER_SUBPIXEL_AUTOMATIC, 0 };
    auto acquire = [&](void** lease, LkgWorkerPreviewLeaseIdentityV1* identity) {
        return lkg_worker_acquire_preview_lease_with_output_v1("TEST-RUNTIME", "ViewWeave", 0,
            g_device.Get(), &outputOptions, lease, identity);
    };
    void* lease = nullptr;
    LkgWorkerPreviewLeaseIdentityV1 identity{};
    Check(acquire(&lease, &identity) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && lease != nullptr &&
        identity.structSize == sizeof(identity) && identity.version == 1 && identity.endpointId != 0 &&
        identity.leaseGeneration != 0, "Automatic preview lease acquires a versioned identity");
    Check(WaitLeaseWorkerState(lease, LKG_WORKER_READY), "Automatic preview lease becomes ready");
    Check(GetPresenterVisibility(lease) == 1, "preview lease exposes the native presenter");
    LkgWorkerSubpixelStatusV1 status = GetSubpixelStatus(lease);
    Check(status.requestedMode == LKG_WORKER_SUBPIXEL_AUTOMATIC &&
        status.effectiveMode == LKG_WORKER_SUBPIXEL_AUTOMATIC && status.cellCount == 0,
        "preview lease uses Automatic scalar calibration when no cell table exists");

    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    void* draw = nullptr;
    Check(TryMakePreviewWorkerDrawData(lease, &identity, texture.Get(), 4, 4, 1.0f, 1.0f, &draw) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK && draw != nullptr, "lease admits an options-v1 draw");
    callback(0, draw);
    Check(WaitCompleted(lease, 1, 5000) && WaitSubpixelFrames(lease, 1, 1),
        "lease draw completes on the native presenter");

    Check(lkg_worker_set_preview_paused(lease, 1) == 0 &&
        WaitPreviewPausePhase(lease, LKG_WORKER_PREVIEW_PAUSED), "preview lease pauses after GPU retirement");
    Check(GetPresenterVisibility(lease) == 0, "paused preview lease hides native output");
    Check(lkg_worker_set_preview_paused(lease, 0) == 0 &&
        WaitPreviewPausePhase(lease, LKG_WORKER_PREVIEW_RUNNING), "preview lease resumes");
    Check(GetPresenterVisibility(lease) == 1, "resumed preview lease shows native output");

    LkgWorkerPreviewLeaseIdentityV1 staleIdentity = identity;
    ++staleIdentity.leaseGeneration;
    Check(lkg_worker_request_preview_lease_return_v1(lease, &staleIdentity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_STALE, "stale preview generation cannot request return");
    Check(lkg_worker_request_preview_lease_return_v1(lease, &identity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK, "active preview lease begins return");
    Check(WaitPreviewLeaseState(lease, identity, LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN),
        "lease return waits for source retirement and native hide proof");
    Check(GetPresenterVisibility(lease) == 1, "returned lease retains the last native image while parked");
    Check(lkg_worker_ack_preview_lease_return_v1(lease, &identity) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK,
        "proven lease return can be acknowledged");

    void* nextLease = nullptr;
    LkgWorkerPreviewLeaseIdentityV1 nextIdentity{};
    Check(acquire(&nextLease, &nextIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && nextLease != nullptr &&
        nextIdentity.endpointId == identity.endpointId && nextIdentity.leaseGeneration > identity.leaseGeneration,
        "reacquire reuses the native endpoint with a fresh lease generation");
    Check(WaitLeaseWorkerState(nextLease, LKG_WORKER_READY) && GetPresenterVisibility(nextLease) == 1,
        "reacquired lease restores the native output window");

    auto typelessSrgb = MakeTypelessTexture(16, 16);
    LkgWorkerStatus srgbWorkerBaseline{};
    Check(lkg_worker_poll(nextLease, &srgbWorkerBaseline) == 0 &&
        srgbWorkerBaseline.state == LKG_WORKER_READY,
        "reacquired lease starts from a ready worker status baseline");
    const LkgWorkerSubpixelStatusV1 srgbPresenterBaseline = GetSubpixelStatus(nextLease);
    draw = nullptr;
    Check(TryMakePreviewWorkerDrawDataWithEncoding(nextLease, &nextIdentity, typelessSrgb.Get(), 4, 4,
        1.0f, 1.0f, LKG_WORKER_SOURCE_ENCODING_SRGB, &draw) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && draw != nullptr,
        "reacquired lease admits explicitly encoded SRGB source bytes");
    callback(0, draw);
    bool srgbFrameCompleted = false;
    bool srgbPresenterCompleted = false;
    LkgWorkerStatus srgbWorkerStatus{};
    LkgWorkerSubpixelStatusV1 srgbPresenterStatus{};
    srgbPresenterStatus.structSize = sizeof(srgbPresenterStatus);
    srgbPresenterStatus.version = 1;
    const ULONGLONG srgbDeadline = GetTickCount64() + 5000;
    do {
        const int32_t workerResult = lkg_worker_poll(nextLease, &srgbWorkerStatus);
        const int32_t presenterResult = lkg_worker_get_subpixel_status_v1(nextLease, &srgbPresenterStatus);
        if (workerResult != 0 || presenterResult != 0 || srgbWorkerStatus.state == LKG_WORKER_FAULT ||
            srgbWorkerStatus.state == LKG_WORKER_CLOSED) break;
        srgbFrameCompleted = srgbWorkerStatus.completed > srgbWorkerBaseline.completed;
        srgbPresenterCompleted = srgbPresenterStatus.valid == 1 &&
            srgbPresenterStatus.requestedMode == LKG_WORKER_SUBPIXEL_AUTOMATIC &&
            srgbPresenterStatus.effectiveMode == LKG_WORKER_SUBPIXEL_AUTOMATIC &&
            srgbPresenterStatus.backend == LKG_WORKER_SUBPIXEL_BACKEND_NATIVE_D3D11 &&
            srgbPresenterStatus.renderCompleted > srgbPresenterBaseline.renderCompleted &&
            srgbPresenterStatus.presentAccepted > srgbPresenterBaseline.presentAccepted;
        if (srgbFrameCompleted && srgbPresenterCompleted) break;
        Sleep(1);
    } while (GetTickCount64() < srgbDeadline);
    if (!srgbFrameCompleted || !srgbPresenterCompleted) {
        uint32_t copyFlags = 0;
        const int32_t copyResult = lkg_worker_test_get_source_copy_state(nextLease, 0, &copyFlags);
        uint32_t visible = UINT32_MAX;
        const int32_t visibilityResult = lkg_worker_test_get_presenter_visibility(nextLease, &visible);
        srgbWorkerStatus = {};
        srgbPresenterStatus = {};
        srgbPresenterStatus.structSize = sizeof(srgbPresenterStatus);
        srgbPresenterStatus.version = 1;
        const int32_t workerPollResult = lkg_worker_poll(nextLease, &srgbWorkerStatus);
        const int32_t presenterQueryResult = lkg_worker_get_subpixel_status_v1(nextLease, &srgbPresenterStatus);
        std::fprintf(stderr,
            "SRGB lease diagnostic worker_rc=%d state=%u submitted=%u completed=%u baseline_completed=%u dropped=%u code=%u error=%s presenter_rc=%d valid=%u requested=%u effective=%u backend=%u render=%u baseline_render=%u accepted=%u baseline_accepted=%u copy_rc=%d copy_flags=0x%08x visibility_rc=%d visible=%u\n",
            workerPollResult, srgbWorkerStatus.state, srgbWorkerStatus.submitted, srgbWorkerStatus.completed,
            srgbWorkerBaseline.completed,
            srgbWorkerStatus.dropped, srgbWorkerStatus.lastErrorCode,
            srgbWorkerStatus.error, presenterQueryResult,
            srgbPresenterStatus.valid, srgbPresenterStatus.requestedMode, srgbPresenterStatus.effectiveMode,
            srgbPresenterStatus.backend, srgbPresenterStatus.renderCompleted, srgbPresenterBaseline.renderCompleted,
            srgbPresenterStatus.presentAccepted, srgbPresenterBaseline.presentAccepted,
            copyResult, copyFlags, visibilityResult, visible);
    }
    Check(srgbFrameCompleted && srgbPresenterCompleted,
        "SRGB source frame reaches completed and accepted NativeD3D11 counters");
    const auto srgbPixels = ReadbackPresenter(nextLease, 32, 32);
    CheckRenderedPresenterPixels(srgbPixels, "explicit SRGB encoding produces a native presenter image");

    Check(lkg_worker_request_preview_lease_return_v1(nextLease, &nextIdentity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK, "second preview lease begins return");
    Check(WaitPreviewLeaseState(nextLease, nextIdentity, LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN),
        "second preview lease proves return");
    Check(lkg_worker_ack_preview_lease_return_v1(nextLease, &nextIdentity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK, "second preview lease ACK succeeds");
    Check(lkg_worker_request_final_shutdown() == 0, "native preview endpoint final shutdown accepted");
    LkgWorkerFinalShutdownStatusV1 finalStatus{};
    finalStatus.structSize = sizeof(finalStatus);
    finalStatus.version = 1;
    Check(lkg_worker_wait_final_shutdown_v1(5000, &finalStatus) == 0 &&
        finalStatus.state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE && finalStatus.hostJoined == 1 &&
        finalStatus.registeredContexts == 0, "native preview final shutdown proves worker cleanup");
}

static void TestSourceCopyRetirementGate() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 32);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_FAILED, false, true);
    void* token = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
    Check(token != nullptr, "source-retirement gate token admitted");
    callback(0, token);

    uint32_t flags = 0;
    const ULONGLONG deadline = GetTickCount64() + 1000;
    do {
        Check(lkg_worker_test_get_source_copy_state(handle, 0, &flags) == 0,
            "query source-copy slot while presenter rejects early");
        if ((flags & LKG_WORKER_TEST_SOURCE_UNPROVEN) != 0) break;
        Sleep(1);
    } while (GetTickCount64() < deadline);
    Check((flags & (LKG_WORKER_TEST_SLOT_IN_USE | LKG_WORKER_TEST_SOURCE_UNPROVEN |
        LKG_WORKER_TEST_SOURCE_MUTEX_HELD)) == (LKG_WORKER_TEST_SLOT_IN_USE |
        LKG_WORKER_TEST_SOURCE_UNPROVEN | LKG_WORKER_TEST_SOURCE_MUTEX_HELD),
        "early presenter failure retains the source slot and keyed mutex until copy proof");
    uintptr_t producerTexture = 0;
    uintptr_t workerTexture = 0;
    uint64_t generation = 0;
    const int32_t slotIdentityResult = lkg_worker_test_get_transfer_slot_identity(handle,
        &producerTexture, &workerTexture, &generation);
    if (slotIdentityResult != 0 || producerTexture == 0 || workerTexture == 0 || generation == 0) {
        std::fprintf(stderr, "source slot snapshot failed rc=%d producer=0x%llx worker=0x%llx generation=%llu flags=0x%x\n",
            slotIdentityResult, static_cast<unsigned long long>(producerTexture),
            static_cast<unsigned long long>(workerTexture), static_cast<unsigned long long>(generation), flags);
    }
    Check(slotIdentityResult == 0 && producerTexture != 0 && workerTexture != 0 && generation != 0,
        "source slot identities remain available while retirement is unknown");
    ComPtr<IDXGIKeyedMutex> producerMutex;
    Check(SUCCEEDED(reinterpret_cast<IUnknown*>(producerTexture)->QueryInterface(IID_PPV_ARGS(&producerMutex))),
        "query producer keyed mutex while worker owns source");
    const HRESULT prematureAcquire = producerMutex->AcquireSync(0, 0);
    if (prematureAcquire == WAIT_OBJECT_0) producerMutex->ReleaseSync(0);
    Check(prematureAcquire == WAIT_TIMEOUT, "producer cannot reacquire source before the copy query proves retirement");
    Check(lkg_worker_reset_transfer(handle) == -2,
        "transfer reset is refused while source retirement and mutex ownership are unknown");
    Check(lkg_worker_close(handle) == 0, "close request accepted while source copy query is held");
    Check(lkg_worker_release(handle) == -2, "context release waits for source copy retirement proof");
    Check(lkg_worker_collect_closed() == 0 && lkg_worker_pending_count() == 1,
        "closed-context collection is blocked while source retirement remains unknown");

    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_NORMAL, false, false);
    Check(WaitState(handle, LKG_WORKER_FAULT, 5000) || WaitState(handle, LKG_WORKER_CLOSED, 5000),
        "worker resolves its bounded presenter failure and drains the real source-copy query");
    const ULONGLONG retirementDeadline = GetTickCount64() + 5000;
    do {
        Check(lkg_worker_test_get_source_copy_state(handle, 0, &flags) == 0,
            "query source slot after releasing the held query");
        if ((flags & (LKG_WORKER_TEST_SOURCE_UNPROVEN | LKG_WORKER_TEST_SOURCE_MUTEX_HELD)) == 0) break;
        Sleep(1);
    } while (GetTickCount64() < retirementDeadline);
    Check((flags & (LKG_WORKER_TEST_SOURCE_UNPROVEN | LKG_WORKER_TEST_SOURCE_MUTEX_HELD)) == 0 &&
        (flags & LKG_WORKER_TEST_SOURCE_COPY_COMPLETED) != 0,
        "late real EVENT query proof returns source mutex and records copy completion");
    Check(producerMutex->AcquireSync(0, 0) == WAIT_OBJECT_0,
        "producer safely reacquires the source after late query proof");
    producerMutex->ReleaseSync(0);
    ReleaseAfterFault(handle, "source-retirement fault context releases after proof");
    Check(lkg_worker_pending_count() == 0, "source-retirement context collection finishes after proof");
}

static void TestPreviewLeaseReturnWaitsForSourceCopyProof() {
    LkgWorkerOutputOptionsV1 options{ sizeof(LkgWorkerOutputOptionsV1), 1,
        LKG_WORKER_SUBPIXEL_AUTOMATIC, 0 };
    void* lease = nullptr;
    LkgWorkerPreviewLeaseIdentityV1 identity{};
    Check(lkg_worker_acquire_preview_lease_with_output_v1("TEST-RUNTIME", "ViewWeave", 0,
        g_device.Get(), &options, &lease, &identity) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && lease,
        "lease source-proof fixture acquires an Automatic endpoint");
    Check(WaitLeaseWorkerState(lease, LKG_WORKER_READY), "lease source-proof endpoint becomes ready");
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 32);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_OCCLUDED, false, true);
    void* draw = nullptr;
    Check(TryMakePreviewWorkerDrawData(lease, &identity, texture.Get(), 4, 4, 1.0f, 1.0f, &draw) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK && draw, "lease admits source-proof fixture draw");
    callback(0, draw);

    uint32_t flags = 0;
    const ULONGLONG deadline = GetTickCount64() + 1000;
    do {
        Check(lkg_worker_test_get_source_copy_state(lease, 0, &flags) == 0,
            "lease source-copy state is observable while query is held");
        if ((flags & LKG_WORKER_TEST_SOURCE_UNPROVEN) != 0) break;
        Sleep(1);
    } while (GetTickCount64() < deadline);
    Check((flags & (LKG_WORKER_TEST_SLOT_IN_USE | LKG_WORKER_TEST_SOURCE_UNPROVEN |
        LKG_WORKER_TEST_SOURCE_MUTEX_HELD)) == (LKG_WORKER_TEST_SLOT_IN_USE |
        LKG_WORKER_TEST_SOURCE_UNPROVEN | LKG_WORKER_TEST_SOURCE_MUTEX_HELD),
        "lease slot remains owned while the copy query is unproven");
    Check(lkg_worker_request_preview_lease_return_v1(lease, &identity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK, "lease return request enters draining state");
    Check(WaitPreviewLeaseState(lease, identity, LKG_WORKER_PREVIEW_LEASE_RETURNING),
        "lease stays RETURNING while source-copy completion is unknown");
    Check(lkg_worker_ack_preview_lease_return_v1(lease, &identity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY, "lease ACK is refused before source-copy proof");
    Check(lkg_worker_test_get_source_copy_state(lease, 0, &flags) == 0 &&
        (flags & LKG_WORKER_TEST_SOURCE_UNPROVEN) != 0,
        "RETURNING lease still reports the unproven source slot");
    Check(GetSubpixelStatus(lease).presentAccepted == 0,
        "occluded frame never increments accepted-present count");

    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_NORMAL, false, false);
    Check(WaitPreviewLeaseState(lease, identity, LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN),
        "lease return becomes proven after real EVENT query completion");
    Check(lkg_worker_test_get_source_copy_state(lease, 0, &flags) == 0 &&
        (flags & (LKG_WORKER_TEST_SOURCE_UNPROVEN | LKG_WORKER_TEST_SOURCE_MUTEX_HELD)) == 0 &&
        (flags & LKG_WORKER_TEST_SOURCE_COPY_COMPLETED) != 0,
        "lease return proof follows completed source-copy query");
    Check(lkg_worker_ack_preview_lease_return_v1(lease, &identity) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_OK, "proven lease return can be acknowledged");
    Check(lkg_worker_request_final_shutdown() == 0, "lease source-proof endpoint shutdown accepted");
    LkgWorkerFinalShutdownStatusV1 finalStatus{};
    finalStatus.structSize = sizeof(finalStatus);
    finalStatus.version = 1;
    Check(lkg_worker_wait_final_shutdown_v1(5000, &finalStatus) == 0 &&
        finalStatus.state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE && finalStatus.registeredContexts == 0,
        "lease source-proof fixture releases endpoint after ACK");
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
        auto data = MakeWorkerDrawData(handle, first.Get(), 4, 2, 1.0f, 1.0f);
        Check(data != nullptr, "reset first token");
        callback(0, data);
        Check(WaitCompleted(handle, firstTarget, 5000), "reset first completion");
        Check(WaitSubpixelFrames(handle, firstTarget, firstTarget), "reset first native render retirement");
        Check(lkg_worker_reset_transfer(handle) == 0, "quiescent transfer reset");
        const uint32_t secondTarget = firstTarget + 1;
        auto second = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 16);
        data = MakeWorkerDrawData(handle, second.Get(), 4, 2, 1.0f, 1.0f);
        Check(data != nullptr, "reset second token");
        callback(0, data);
        Check(WaitCompleted(handle, secondTarget, 5000), "reset second completion");
        Check(WaitSubpixelFrames(handle, secondTarget, secondTarget), "reset second native render retirement");
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
    auto data = MakeWorkerDrawData(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
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
    void* stale = MakeWorkerDrawData(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
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
    const auto collectStart = std::chrono::steady_clock::now();
    const auto collectDeadline = collectStart + std::chrono::milliseconds(2000);
    int32_t lastCollect = 0;
    for (;;) {
        lastCollect = lkg_worker_collect_closed();
        const auto now = std::chrono::steady_clock::now();
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - collectStart).count();
        if (lastCollect < 0) {
            std::fprintf(stderr, "FAIL collect closed context elapsed_ms=%lld last_collect=%d pending=%u\n",
                static_cast<long long>(elapsed), lastCollect, lkg_worker_pending_count());
            std::exit(1);
        }
        if (lastCollect >= 1) break;
        if (now >= collectDeadline) {
            std::fprintf(stderr, "FAIL collect closed context timeout elapsed_ms=%lld last_collect=%d pending=%u\n",
                static_cast<long long>(elapsed), lastCollect, lkg_worker_pending_count());
            std::exit(1);
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    Check(lkg_worker_pending_count() == 0, "close all pending drain");
    LkgWorkerStatus status{};
    Check(lkg_worker_poll(handle, &status) == -2, "poll after close all collect");

    void* next = nullptr;
    BeginContext(&next);
    auto nextTexture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    void* nextToken = MakeWorkerDrawData(next, nextTexture.Get(), 2, 2, 1.0f, 1.0f);
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
    void* data = MakeWorkerDrawData(handle, badFormat.Get(), 2, 2, 1.0f, 1.0f);
    Check(data == nullptr, "non-RGBA8 source must be rejected before draw token creation");
    LkgWorkerStatus rejectedFormatStatus{};
    Check(lkg_worker_poll(handle, &rejectedFormatStatus) == 0 &&
        rejectedFormatStatus.state == LKG_WORKER_READY && lkg_worker_pending_count() == 0,
        "rejected source format must not enqueue work or fault the worker");
    EndContext(handle);

    BeginContext(&handle);
    auto first = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto second = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 16);
    data = MakeWorkerDrawData(handle, first.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "resize first token");
    callback(0, data);
    data = MakeWorkerDrawData(handle, second.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "resize second token");
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "resize fault");
    ReleaseAfterFault(handle, "resize release");
}

static void TestInitializationAndDrawFault() {
    void* handle = nullptr;
    BeginContext(&handle);
    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_FAILED, false, false);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto data = MakeWorkerDrawData(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "draw failure token");
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "draw failure fault");
    ReleaseAfterFault(handle, "draw failure release");
    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_NORMAL, false, false);
}

static void TestDrawExceptionDrain() {
    void* handle = nullptr;
    BeginContext(&handle);
    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    auto data = MakeWorkerDrawData(handle, texture.Get(), 2, 2, 1.0f, 1.0f);
    Check(data != nullptr, "draw exception token");
    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_NORMAL, true, false);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    callback(0, data);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "draw exception fault");
    ReleaseAfterFault(handle, "draw exception release");
    lkg_worker_test_configure_source_copy(0, 0, LKG_WORKER_TEST_PRESENTER_NORMAL, false, false);
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

int32_t lkg_bridge_load(const char*) { g_hostLoadCount.fetch_add(1); return 0; }
void lkg_bridge_unload(void) { g_hostUnloadCount.fetch_add(1); }
int32_t lkg_bridge_initialize(const char*) {
    g_hostInitializeCount.fetch_add(1);
    return g_initializeFailure.load() ? -101 : 0;
}
void lkg_bridge_uninitialize(void) { g_hostUninitializeCount.fetch_add(1); }
int32_t lkg_bridge_is_idle(void) { return 1; }
int32_t lkg_bridge_last_error(char* utf8, uint32_t capacity) {
    if (!utf8 || !capacity) return 0;
    std::lock_guard<std::mutex> lock(g_fakeHostConfigMutex);
    std::snprintf(utf8, capacity, "%s", g_fakeLastError.c_str());
    return static_cast<int32_t>(g_fakeLastError.size());
}
uint32_t lkg_bridge_successful_draw_count(void) { return g_drawCount.load(); }
int32_t lkg_bridge_is_initialized(void) { return 0; }
extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void) { return g_fakeHostFaulted.load() ? 1 : 0; }
extern "C" int32_t lkg_bridge_close_sdk_admission(void) { return 0; }
extern "C" int32_t lkg_bridge_sdk_shutdown_incomplete(void) { return g_fakeHostSdkOwned.load() ? 1 : 0; }
extern "C" int32_t lkg_bridge_worker_host_begin_sdk(const char* runtimePath, const char* applicationName) {
    if (g_workerThread.load(std::memory_order_acquire) != GetCurrentThreadId()) return -6;
    auto canonicalize = [](const char* value) {
        std::string result = value ? value : "";
        for (char& character : result) {
            if (character == '/') character = '\\';
            character = static_cast<char>(std::tolower(static_cast<unsigned char>(character)));
        }
        return result;
    };
    const std::string requestedPath = canonicalize(runtimePath);
    const std::string requestedApplication = applicationName ? applicationName : "";
    std::lock_guard<std::mutex> lock(g_fakeHostConfigMutex);
    if (g_fakeHostSdkOwned.load()) {
        if (requestedPath != g_fakeHostRuntime || requestedApplication != g_fakeHostApplication) {
            g_fakeLastError = "Persistent worker host already owns the Bridge SDK for a different runtime path or application name";
            return -20;
        }
        return g_fakeHostFaulted.load() ? -21 : 0;
    }
    g_hostLoadCount.fetch_add(1);
    g_fakeHostRuntime = requestedPath;
    g_fakeHostApplication = requestedApplication;
    g_fakeHostSdkOwned.store(true);
    const int32_t result = lkg_bridge_initialize(applicationName);
    if (result != 0) {
        g_fakeHostFaulted.store(true);
        g_fakeLastError = "Bridge initialize returned false";
        return -21;
    }
    g_fakeLastError.clear();
    return 0;
}
extern "C" int32_t lkg_bridge_worker_host_finalize_sdk(void) {
    g_hostFinalizerThread.store(GetCurrentThreadId());
    if (!g_fakeHostSdkOwned.load()) return g_fakeHostFaulted.load() ? -1 : 0;
    g_hostUninitializeCount.fetch_add(1);
    if (g_fakeHostFinalizationFailure.load()) {
        g_fakeHostFaulted.store(true);
        return -2;
    }
    g_fakeHostSdkOwned.store(false);
    g_fakeHostRuntime.clear();
    g_fakeHostApplication.clear();
    return 0;
}
extern "C" int32_t lkg_bridge_get_session_close_status_v1(uint64_t session, LkgBridgeSessionCloseStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgBridgeSessionCloseStatusV1) || status->version != 1) return -1;
    status->sessionId = session;
    status->state = LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE;
    status->lastResult = 0;
    status->reserved0 = 0;
    status->reserved1 = 0;
    return 0;
}
extern "C" int32_t lkg_bridge_ack_session_close_v1(uint64_t) { return 0; }
int32_t lkg_bridge_version(char*, uint32_t) { return 0; }
int32_t lkg_bridge_get_display_count(uint32_t* count) {
    if (!count) return -1;
    if (g_workerThread.load(std::memory_order_acquire) != GetCurrentThreadId()) return -99;
    g_lastDisplayQueryThread.store(GetCurrentThreadId(), std::memory_order_release);
    if (g_testHoldDisplayQuery.load(std::memory_order_acquire)) {
        g_testDisplayQueryEntered.store(true, std::memory_order_release);
        while (!g_testReleaseDisplayQuery.load(std::memory_order_acquire)) Sleep(1);
    }
    *count = g_testDisplayCount.load(std::memory_order_acquire);
    return 0;
}
int32_t lkg_bridge_get_display(uint32_t ordinal, LkgBridgeDisplayInfo* info) {
    if (!info || ordinal >= g_testDisplayCount.load(std::memory_order_acquire)) return -2;
    *info = LkgBridgeDisplayInfo{ ordinal, 32u + ordinal, 32, 1.0f, 1.0f, 128, 128, 4, 4 };
    return 0;
}
extern "C" int32_t lkg_bridge_get_worker_subpixel_calibration_v1(uint32_t displayIndex,
    LkgWorkerSubpixelCalibrationV1* calibration) {
    if (!calibration || calibration->structSize != sizeof(LkgWorkerSubpixelCalibrationV1) ||
        calibration->version != 1) return -1;
    *calibration = LkgWorkerSubpixelCalibrationV1{};
    calibration->structSize = sizeof(*calibration);
    calibration->version = 1;
    calibration->displayIndex = displayIndex;
    calibration->nativeWidth = 32u + displayIndex;
    calibration->nativeHeight = 32;
    calibration->center = 0.5f;
    calibration->pitch = 50.0f;
    calibration->slope = -7.0f;
    calibration->dpi = 254.0f;
    calibration->flipX = 1.0f;
    calibration->viewCone = 40.0f;
    calibration->fringe = 0.0f;
    calibration->invertedViews = 1;
    calibration->cellPatternMode = 0;
    calibration->cellCount = 0;
    calibration->redIndex = 0;
    calibration->blueIndex = 2;
    return 0;
}
int32_t lkg_bridge_get_physical_display_v1(uint32_t, LkgBridgePhysicalDisplayInfoV1* info) {
    if (!info || info->structSize != sizeof(LkgBridgePhysicalDisplayInfoV1) || info->version != 1) return -1;
    if (g_testCalibrationMode.load() != 0) {
        info->pixelWidth = 1920;
        info->pixelHeight = 1080;
        info->dpi = 0.0f;
        info->valid = 0;
        return -3;
    }
    info->pixelWidth = 1920;
    info->pixelHeight = 1080;
    info->dpi = 110.0f;
    info->valid = 1;
    return 0;
}
uint64_t lkg_bridge_create_d3d11_session(void* device, uint32_t) {
    if (!device) return 0;
    const uint64_t id = g_fakeSessionCounter.fetch_add(1, std::memory_order_acq_rel) + 1;
    g_lastCreatedSession.store(id, std::memory_order_release);
    return id;
}
int32_t lkg_bridge_destroy_session(uint64_t) {
    g_sessionDestroyCount.fetch_add(1, std::memory_order_acq_rel);
    return 0;
}
int32_t lkg_bridge_set_session_visible(uint64_t session, uint32_t visible) {
    if (visible > 1) return -8;
    g_visibilityCalls.fetch_add(1, std::memory_order_acq_rel);
    g_visibilityThread.store(GetCurrentThreadId(), std::memory_order_release);
    g_visibilitySession.store(session, std::memory_order_release);
    g_visibilityDrawCount.store(g_drawCount.load(std::memory_order_acquire), std::memory_order_release);
    int32_t expectedFailure = static_cast<int32_t>(visible);
    if (g_visibilityFailureFor.compare_exchange_strong(expectedFailure, -1, std::memory_order_acq_rel)) return -5;
    const uint32_t previousVisibility = g_visibilityState.exchange(visible, std::memory_order_acq_rel);
    if (previousVisibility == visible) return 0;
    if (visible) g_visibilityShowCalls.fetch_add(1, std::memory_order_acq_rel);
    else g_visibilityHideCalls.fetch_add(1, std::memory_order_acq_rel);
    return 0;
}
void* lkg_bridge_render_event_func(void) {
    return reinterpret_cast<void*>(+[](int eventId, void* data) {
        if (eventId == 1) delete static_cast<uint64_t*>(data);
    });
}
void* lkg_bridge_make_draw_data(uint64_t, void*, uint32_t, uint32_t, float, float) { return nullptr; }
void* lkg_bridge_make_close_data(uint64_t session) { return new uint64_t(session); }
int32_t lkg_bridge_smoke_draw(uint64_t, void*, uint32_t, uint32_t, float, float) {
    g_sdkDrawCalls.fetch_add(1, std::memory_order_acq_rel);
    return -202;
}

int32_t lkg_bridge_redraw_retained_session(uint64_t session, uint64_t endpointId, uint64_t generation) {
    g_staticPresentAttempts.fetch_add(1, std::memory_order_acq_rel);
    g_staticPresentSession.store(session, std::memory_order_release);
    g_staticPresentEndpoint.store(endpointId, std::memory_order_release);
    g_staticPresentGeneration.store(generation, std::memory_order_release);
    g_staticPresentPrimary.store(g_lastDrawTextureIdentity.load(std::memory_order_acquire), std::memory_order_release);
    g_staticPresentColumns.store(g_lastDrawColumns.load(std::memory_order_acquire), std::memory_order_release);
    g_staticPresentRows.store(g_lastDrawRows.load(std::memory_order_acquire), std::memory_order_release);
    g_staticPresentAspect.store(g_lastDrawAspect.load(std::memory_order_acquire), std::memory_order_release);
    g_staticPresentZoom.store(g_lastDrawZoom.load(std::memory_order_acquire), std::memory_order_release);
    g_staticPresentThread.store(GetCurrentThreadId(), std::memory_order_release);
    return -202;
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
void lkg_bridge_worker_enter() { g_workerThread.store(GetCurrentThreadId(), std::memory_order_release); }
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
    if (g_testD3D12Device && g_testD3D12Queue && g_testD3D12Fence && g_testD3D12GateFence) return true;
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
            g_testD3D12GateFence.Reset();
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
    void* handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 worker begin");
    Check(WaitReady(handle), "D3D12 worker ready");
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    constexpr uint32_t targetDraws = 24;
    uint32_t accepted = 0;
    std::array<uint8_t, 3> lastAcceptedRgb{};
    for (uint32_t sequence = 0; sequence < targetDraws * 4 && accepted < targetDraws; ++sequence) {
        const std::array<uint8_t, 3> rgb = {
            static_cast<uint8_t>(24 + (sequence * 37) % 208),
            static_cast<uint8_t>(20 + (sequence * 61) % 216),
            static_cast<uint8_t>(16 + (sequence * 83) % 224)
        };
        auto texture = MakeD3D12PatternTexture(rgb);
        void* data = MakeWorkerDrawData(handle, texture.Get(), 4, 4, 1.0f, 1.0f);
        if (!data) continue;
        LkgWorkerStatus before{};
        Check(lkg_worker_poll(handle, &before) == 0, "D3D12 worker pre-submit poll");
        callback(0, data);
        LkgWorkerStatus after{};
        Check(lkg_worker_poll(handle, &after) == 0, "D3D12 worker post-submit poll");
        if (after.submitted > before.submitted) {
            ++accepted;
            lastAcceptedRgb = rgb;
            if (accepted % 3 == 0) Check(WaitCompleted(handle, accepted, 5000), "D3D12 ring batch completion");
        }
    }
    Check(accepted == targetDraws, "D3D12 ring accepted sequence");
    Check(WaitCompleted(handle, targetDraws, 5000), "D3D12 ring final completion");
    Check(WaitSubpixelFrames(handle, targetDraws, targetDraws), "D3D12 producer frames reach native presenter");
    const auto ringPixels = ReadbackPresenter(handle, 32, 32);
    CheckUniformPresenterColor(ringPixels, lastAcceptedRgb, 1,
        "D3D12 ring final image matches the last accepted producer color");
    Check(lkg_worker_reset_transfer(handle) == 0, "D3D12 ring drained reset");
    EndContext(handle);

    const LkgWorkerOutputOptionsV1 d3d12LinearOptions{ sizeof(LkgWorkerOutputOptionsV1), 1,
        LKG_WORKER_SUBPIXEL_AUTOMATIC, 1 };
    handle = lkg_worker_begin_with_output_v1("test-runtime", "worker-transport-test", 0,
        g_testD3D12Device.Get(), &d3d12LinearOptions);
    Check(handle != nullptr, "D3D12 typeless worker begin");
    Check(WaitReady(handle), "D3D12 typeless worker ready");
    const std::array<uint8_t, 3> typelessRgb = { 128, 64, 32 };
    auto typelessTexture = MakeD3D12PatternTexture(typelessRgb, DXGI_FORMAT_R8G8B8A8_TYPELESS);
    void* typelessData = MakeWorkerDrawDataWithEncoding(handle, typelessTexture.Get(), 4, 4, 1.0f, 1.0f,
        LKG_WORKER_SOURCE_ENCODING_SRGB);
    Check(typelessData != nullptr, "D3D12 typeless token");
    callback(0, typelessData);
    Check(WaitCompleted(handle, 1, 5000), "D3D12 typeless completion");
    Check(WaitSubpixelFrames(handle, 1, 1), "D3D12 typeless SRGB bytes reach native output");
    const auto typelessPixels = ReadbackPresenter(handle, 32, 32);
    CheckUniformPresenterColor(typelessPixels, typelessRgb, 1,
        "D3D12 Linear plus explicit SRGB preserves uniform midtone output values");
    Check(lkg_worker_reset_transfer(handle) == 0, "D3D12 typeless drained reset");
    EndContext(handle);
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 gate worker begin");
    Check(WaitReady(handle), "D3D12 gate worker ready");
    const std::array<uint8_t, 3> gatedRgb = { 91, 143, 207 };
    auto gatedTexture = MakeD3D12PatternTexture(gatedRgb);
    void* gatedData = MakeWorkerDrawData(handle, gatedTexture.Get(), 4, 4, 1.0f, 1.0f);
    Check(gatedData != nullptr, "D3D12 gated token");
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 consumer timeout worker begin");
    Check(WaitReady(handle), "D3D12 consumer timeout worker ready");
    Check(lkg_worker_test_set_d3d12_timeout(handle, 40) == 0, "D3D12 consumer timeout setup");
    auto consumerTimeoutTexture = MakeD3D12PatternTexture({ 107, 149, 211 });
    void* consumerTimeoutData = MakeWorkerDrawData(handle, consumerTimeoutTexture.Get(), 4, 4, 1.0f, 1.0f);
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 timeout worker begin");
    Check(WaitReady(handle), "D3D12 timeout worker ready");
    Check(lkg_worker_test_set_d3d12_timeout(handle, 40) == 0, "D3D12 test timeout setup");
    auto timeoutTexture = MakeD3D12PatternTexture({ 113, 157, 201 });
    void* timeoutData = MakeWorkerDrawData(handle, timeoutTexture.Get(), 4, 4, 1.0f, 1.0f);
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr, "D3D12 generation worker begin");
    Check(WaitReady(handle), "D3D12 generation worker ready");
    auto staleTexture = MakeD3D12PatternTexture({ 71, 112, 183 });
    void* staleData = MakeWorkerDrawData(handle, staleTexture.Get(), 4, 4, 1.0f, 1.0f);
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
        void* unknownData = MakeWorkerDrawData(unknownHandle, unknownTexture.Get(), 4, 4, 1.0f, 1.0f);
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
    verifyUnknownSubmission(0, "worker-transport-test");
    verifyUnknownSubmission(UINT64_MAX, "worker-transport-test");
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
    Check(handle != nullptr && WaitReady(handle), "D3D12 outer-exception worker ready");
    auto exceptionTexture = MakeD3D12PatternTexture({ 109, 147, 203 });
    void* exceptionData = MakeWorkerDrawData(handle, exceptionTexture.Get(), 4, 4, 1.0f, 1.0f);
    Check(exceptionData != nullptr, "D3D12 outer-exception token accepted");
    g_testD3D12GateValue.store(4, std::memory_order_release);
    g_testD3D12GateProducer.store(true, std::memory_order_release);
    lkg_worker_test_throw_after_worker_dequeue();
    callback(0, exceptionData);
    g_testD3D12GateProducer.store(false, std::memory_order_release);
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "in-flight D3D12 job reaches the worker outer catch");
    uint32_t ownershipFlags = 0;
    const ULONGLONG ownershipDeadline = GetTickCount64() + 1000;
    do {
        Check(lkg_worker_test_get_source_copy_state(handle, 0, &ownershipFlags) == 0,
            "query D3D12 ownership after outer catch");
        if ((ownershipFlags & LKG_WORKER_TEST_D3D12_OWNERSHIP_UNPROVEN) != 0) break;
        Sleep(1);
    } while (GetTickCount64() < ownershipDeadline);
    Check((ownershipFlags & LKG_WORKER_TEST_D3D12_OWNERSHIP_UNPROVEN) != 0,
        "outer catch retains the producer fence and D3D12 slot without proof");
    Check(lkg_worker_reset_transfer(handle) != 0, "transfer reset is rejected for the faulted unproven D3D12 slot");
    Check(lkg_worker_release(handle) == -2 && lkg_worker_collect_closed() == 0 &&
        lkg_worker_pending_count() == 1,
        "release and collection stay blocked while the producer fence is gated");
    Check(SUCCEEDED(g_testD3D12GateFence->Signal(4)), "signal late D3D12 producer completion proof");
    ReleaseAfterFault(handle, "outer-catch D3D12 resources release after late producer fence proof");
    Check(lkg_worker_pending_count() == 0, "outer-catch context is collected after late fence proof");
    g_testD3D12Device.Reset();
    g_testD3D12Queue.Reset();
    g_testD3D12Fence.Reset();
    g_testD3D12GateFence.Reset();
}

static void TestCleanupExceptionRetainsContext(void) {
    const ULONG referencesBeforeBegin = ReferenceCount(g_device.Get());
    void* handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_device.Get());
    Check(handle != nullptr, "cleanup-exception worker begin");
    Check(WaitReady(handle), "cleanup-exception worker ready");
    lkg_worker_test_throw_during_cleanup();
    Check(lkg_worker_close(handle) == 0, "cleanup-exception close accepted");
    Check(WaitState(handle, LKG_WORKER_FAULT, 5000), "cleanup exception faults worker");
    Check(lkg_worker_shutdown_host(5000) == -2, "cleanup exception retains host SDK module pin");
    Check(g_hostUninitializeCount.load() == 0 && g_fakeHostSdkOwned.load(),
        "unproven context cleanup prevents process SDK finalization");
    Check(ReferenceCount(g_device.Get()) > referencesBeforeBegin,
        "cleanup exception retains producer-device references after worker exit");
    Check(lkg_worker_release(handle) == -2, "cleanup exception prevents worker release");
    Check(lkg_worker_collect_closed() == 0, "cleanup exception prevents context collection");
    LkgWorkerFinalShutdownStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    Check(lkg_worker_get_final_shutdown_status_v1(&status) == 0 &&
        status.state == LKG_WORKER_FINAL_SHUTDOWN_NONE && status.registeredContexts == 1 &&
        status.closedUncollectedContexts == 1,
        "cleanup exception leaves a registered uncollected context");
    Check(lkg_worker_release(handle) == -2, "joined host does not make unproven context releasable");
}

int main(int argc, char** argv) {
    BeginDevice();
    if (argc == 2 && std::strcmp(argv[1], "--cleanup-exception-only") == 0) {
        TestCleanupExceptionRetainsContext();
        std::fprintf(stdout, "worker_cleanup_exception_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--host-initialize-failure-only") == 0) {
        TestMetadataInitializationFailureIsPublished();
        Check(lkg_worker_shutdown_host(5000) == -2, "host initialization fault retains SDK owner state");
        Check(g_hostInitializeCount.load() == 1 && g_hostUninitializeCount.load() == 0 &&
            g_fakeHostSdkOwned.load(), "failed SDK initialization is not finalized as success");
        std::fprintf(stdout, "worker_host_initialize_failure_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--host-uninitialize-failure-only") == 0) {
        void* handle = nullptr;
        BeginContext(&handle);
        Check(lkg_worker_test_host_device_identity() != 0, "worker host owns D3D11 device before finalization");
        EndContext(handle);
        g_fakeHostFinalizationFailure.store(true);
        Check(lkg_worker_shutdown_host(5000) == -2, "failed host uninitialize remains incomplete");
        Check(g_hostUninitializeCount.load() == 1 && g_fakeHostSdkOwned.load(),
            "failed final uninitialize is attempted once and retains SDK ownership");
        Check(lkg_worker_test_host_device_identity() != 0,
            "failed final uninitialize retains the host-owned D3D11 device");
        Check(lkg_worker_begin_metadata_v1("test-runtime", "worker-transport-test") == nullptr,
            "finalization fault rejects further SDK contexts");
        std::fprintf(stdout, "worker_host_uninitialize_failure_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--persistent-d3d11-device-only") == 0) {
        TestPersistentD3D11DeviceReuseAndAdapterGate();
        std::fprintf(stdout, "persistent_d3d11_device_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--preview-pause-only") == 0) {
        TestPreviewPauseResumePreservesSessionAndDrains();
        TestPreviewPauseVisibilityFailuresDoNotAcknowledge();
        std::fprintf(stdout, "worker_preview_pause_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--preview-lease-only") == 0) {
        TestPreviewLeaseReusesSessionAndStableSdkInput();
        std::fprintf(stdout, "worker_preview_lease_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--source-copy-retirement-only") == 0) {
        TestSourceCopyRetirementGate();
        std::fprintf(stdout, "worker_source_copy_retirement_test pass\n");
        return 0;
    }
    if (argc == 2 && std::strcmp(argv[1], "--lease-source-copy-retirement-only") == 0) {
        TestPreviewLeaseReturnWaitsForSourceCopyProof();
        std::fprintf(stdout, "worker_lease_source_copy_retirement_test pass\n");
        return 0;
    }
    TestD3D12RingColors();
    TestCachedPhysicalDisplayCalibration();
    TestMetadataOnlyRefreshLifecycle();
    TestPersistentSdkConfigurationMismatchIsRejected();
    TestActiveWorkerDisplayRefreshDoesNotMutateRenderStatus();
    TestPreviewPauseResumePreservesSessionAndDrains();
    TestPreviewPauseVisibilityFailuresDoNotAcknowledge();
    TestWindowPollFailureFaultsAndDrains();
    TestSourceCopyRetirementGate();
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
    TestMetadataAfterRenderReusesPersistentSdk();
    TestPersistentHostThreadReuse();
    TestWorkerMessagePump();
    TestPersistentHostIdleQuit();
#endif
    Check(g_sdkDrawCalls.load(std::memory_order_acquire) == 0 && g_drawCount.load() == 0 &&
        g_staticPresentAttempts.load(std::memory_order_acquire) == 0 &&
        g_visibilityCalls.load(std::memory_order_acquire) == 0,
        "native worker path never falls back to SDK draw or retained-session presentation");
    std::fprintf(stdout, "worker_transport_test pass\n");
    return 0;
}
