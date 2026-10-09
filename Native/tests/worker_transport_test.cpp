#include "bridge_worker_transport.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include "bridge_d3d12_internal.h"
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
static std::atomic<bool> g_drawFailure{false};
static std::atomic<bool> g_drawThrow{false};
static std::atomic<uint32_t> g_drawCount{0};
static std::atomic<bool> g_holdDraw{false};
static std::atomic<bool> g_drawEntered{false};
static std::atomic<uint64_t> g_fakeSessionCounter{0};
static std::atomic<uint64_t> g_lastCreatedSession{0};
static std::atomic<uint64_t> g_lastDrawSession{0};
static std::atomic<uintptr_t> g_lastDrawTextureIdentity{0};
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
    g_sessionDestroyCount.store(0, std::memory_order_release);
}

static void TestPreviewPauseResumePreservesSessionAndDrains() {
    ResetVisibilityTestState();
    void* handle = nullptr;
    BeginContext(&handle);
    Check(PreviewPausePhase(handle) == LKG_WORKER_PREVIEW_RUNNING, "preview starts in running phase");
    Check(lkg_worker_set_preview_paused(handle, 2) == -1, "invalid preview pause value rejected");
    Check(lkg_worker_get_preview_pause_state(handle, nullptr) == -1, "null preview phase output rejected");

    auto texture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 32);
    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    g_holdDraw.store(true, std::memory_order_release);
    void* token = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
    Check(token != nullptr, "pre-pause render token accepted");
    callback(0, token);
    const auto drawDeadline = GetTickCount64() + 2000;
    while (!g_drawEntered.load(std::memory_order_acquire) && GetTickCount64() < drawDeadline)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    Check(g_drawEntered.load(std::memory_order_acquire), "render entered before pause request");
    const uint64_t session = g_lastCreatedSession.load(std::memory_order_acquire);
    const uintptr_t workerDeviceBefore = lkg_worker_test_host_device_identity();
    Check(session != 0 && workerDeviceBefore != 0 && g_lastDrawSession.load() == session,
        "persistent session and host device created");
    const uint32_t destroyCountBeforePause = g_sessionDestroyCount.load(std::memory_order_acquire);
    Check(lkg_worker_set_preview_paused(handle, 1) == 0, "pause request accepted");
    Check(PreviewPausePhase(handle) == LKG_WORKER_PREVIEW_PAUSING, "pause request published as pausing");
    Check(lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f) == nullptr,
        "new submissions rejected immediately while pausing");
    Check(g_visibilityHideCalls.load(std::memory_order_acquire) == 0,
        "window is not hidden while producer draw is in flight");
    g_holdDraw.store(false, std::memory_order_release);
    Check(WaitCompleted(handle, 1, 2000), "in-flight producer draw drains before hide");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_PAUSED), "pause acknowledged after draw drain");
    uintptr_t producerSlotBefore = 0;
    uintptr_t workerSlotBefore = 0;
    uint64_t slotGenerationBefore = 0;
    Check(lkg_worker_test_get_transfer_slot_identity(handle, &producerSlotBefore, &workerSlotBefore,
        &slotGenerationBefore) == 0 && producerSlotBefore != 0 && workerSlotBefore != 0,
        "transfer slots remain allocated while paused");
    Check(g_visibilityHideCalls.load(std::memory_order_acquire) == 1 &&
        g_visibilityState.load(std::memory_order_acquire) == 0, "pause hides the existing session window");
    Check(g_visibilityDrawCount.load(std::memory_order_acquire) >= 1,
        "hide occurs after the in-flight draw completed");
    Check(g_visibilitySession.load(std::memory_order_acquire) == session,
        "pause visibility applies to the original session");
    Check(g_visibilityThread.load(std::memory_order_acquire) == lkg_worker_test_thread_id(handle),
        "pause visibility runs on persistent worker owner thread");
    LkgWorkerStatus pausedStatus{};
    Check(lkg_worker_poll(handle, &pausedStatus) == 0 && pausedStatus.state == LKG_WORKER_READY,
        "paused worker remains ready");
    Check(lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f) == nullptr,
        "submissions remain rejected while paused");

    Check(lkg_worker_set_preview_paused(handle, 0) == 0, "resume request accepted");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_RUNNING), "resume acknowledged after show");
    Check(g_visibilityShowCalls.load(std::memory_order_acquire) == 1 &&
        g_visibilityState.load(std::memory_order_acquire) == 1, "resume shows the same session window");
    Check(g_visibilitySession.load(std::memory_order_acquire) == session &&
        g_visibilityThread.load(std::memory_order_acquire) == lkg_worker_test_thread_id(handle),
        "resume remains on the same session and owner thread");
    Check(lkg_worker_test_host_device_identity() == workerDeviceBefore &&
        g_fakeSessionCounter.load(std::memory_order_acquire) == session &&
        g_sessionDestroyCount.load(std::memory_order_acquire) == destroyCountBeforePause,
        "pause/resume does not recreate the session or worker device");
    token = lkg_worker_make_draw_data(handle, texture.Get(), 4, 3, 1.0f, 1.0f);
    Check(token != nullptr, "render submission accepted after resume");
    callback(0, token);
    Check(WaitCompleted(handle, 2, 2000), "render completes after resume");
    Check(g_lastDrawSession.load(std::memory_order_acquire) == session &&
        g_sessionDestroyCount.load(std::memory_order_acquire) == destroyCountBeforePause,
        "resumed render continues through the original live session");
    uintptr_t producerSlotAfter = 0;
    uintptr_t workerSlotAfter = 0;
    uint64_t slotGenerationAfter = 0;
    Check(lkg_worker_test_get_transfer_slot_identity(handle, &producerSlotAfter, &workerSlotAfter,
        &slotGenerationAfter) == 0 && producerSlotAfter == producerSlotBefore &&
        workerSlotAfter == workerSlotBefore && slotGenerationAfter == slotGenerationBefore,
        "resume preserves producer and worker transfer slots and generation");

    Check(lkg_worker_set_preview_paused(handle, 1) == 0, "second pause accepted before close");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_PAUSED), "second pause acknowledged");
    EndContext(handle);
    Check(g_sessionDestroyCount.load(std::memory_order_acquire) == destroyCountBeforePause + 1,
        "session is destroyed only by close after preview pause/resume");
}

static void TestPreviewPauseVisibilityFailuresDoNotAcknowledge() {
    void* handle = nullptr;
    ResetVisibilityTestState();
    BeginContext(&handle);
    g_visibilityFailureFor.store(0, std::memory_order_release);
    Check(lkg_worker_set_preview_paused(handle, 1) == 0, "failing pause request accepted asynchronously");
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "failed hide faults worker");
    Check(PreviewPausePhase(handle) == LKG_WORKER_PREVIEW_FAULTED &&
        g_visibilityHideCalls.load(std::memory_order_acquire) == 0 &&
        g_visibilityState.load(std::memory_order_acquire) == 1,
        "failed hide never acknowledges paused");
    ReleaseAfterFault(handle, "failed pause context release");

    ResetVisibilityTestState();
    BeginContext(&handle);
    Check(lkg_worker_set_preview_paused(handle, 1) == 0, "pause before resume failure accepted");
    Check(WaitPreviewPausePhase(handle, LKG_WORKER_PREVIEW_PAUSED), "pause before resume failure acknowledged");
    g_visibilityFailureFor.store(1, std::memory_order_release);
    Check(lkg_worker_set_preview_paused(handle, 0) == 0, "failing resume request accepted asynchronously");
    Check(WaitState(handle, LKG_WORKER_FAULT, 2000), "failed show faults worker");
    Check(PreviewPausePhase(handle) == LKG_WORKER_PREVIEW_FAULTED &&
        g_visibilityShowCalls.load(std::memory_order_acquire) == 0 &&
        g_visibilityState.load(std::memory_order_acquire) == 0,
        "failed show never acknowledges running");
    ReleaseAfterFault(handle, "failed resume context release");
}

static void TestPreviewLeaseReusesSessionAndStableSdkInput() {
    ResetVisibilityTestState();
    Check(g_hostInitializeCount.load() == 0 && g_hostUninitializeCount.load() == 0,
        "preview lease test starts with an uninitialized fake SDK");

    void* startupMetadata = lkg_worker_begin_metadata_v1("TEST-RUNTIME", "ViewWeave");
    Check(startupMetadata != nullptr && WaitForDisplaySnapshot(startupMetadata, 1),
        "legacy startup metadata performs its initial query before the first preview lease");
    Check(lkg_worker_close(startupMetadata) == 0 && WaitForMetadataClose(startupMetadata),
        "legacy startup metadata closes only after its initial query and cleanup proof");
    Check(lkg_worker_end_metadata_v1(startupMetadata) == 0 && g_hostInitializeCount.load() == 1 &&
        g_hostUninitializeCount.load() == 0,
        "legacy metadata releases its worker while preserving exact application SDK ownership");

    auto acquire = [](void** handle, LkgWorkerPreviewLeaseIdentityV1* identity) {
        return lkg_worker_acquire_preview_lease_v1("TEST-RUNTIME", "ViewWeave", 0,
            g_device.Get(), handle, identity);
    };
    void* firstHandle = nullptr;
    LkgWorkerPreviewLeaseIdentityV1 firstIdentity{};
    Check(acquire(&firstHandle, &firstIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && firstHandle != nullptr &&
        firstIdentity.structSize == sizeof(firstIdentity) && firstIdentity.version == 1 &&
        firstIdentity.endpointId != 0 && firstIdentity.leaseGeneration != 0,
        "first preview lease acquires an opaque versioned token");
    Check(WaitLeaseWorkerState(firstHandle, LKG_WORKER_READY), "first preview lease becomes ready");
    const uint32_t firstWorkerThread = lkg_worker_test_thread_id(firstHandle);
    Check(firstWorkerThread != 0, "preview lease has a persistent SDK owner thread");
    const HWND firstWindow = lkg_worker_test_window(firstHandle);
    Check(firstWindow != nullptr, "first lease creates a Bridge owner window");

    uint64_t firstEndpoint = 0;
    uint64_t firstGeneration = 0;
    uint64_t firstSession = 0;
    uintptr_t firstStableInput = 0;
    Check(lkg_worker_test_get_preview_lease_resources(firstHandle, &firstEndpoint, &firstGeneration,
        &firstSession, &firstStableInput) == 0 && firstEndpoint == firstIdentity.endpointId &&
        firstGeneration == firstIdentity.leaseGeneration && firstSession != 0 && firstStableInput != 0,
        "first lease owns a session and profile-sized stable SDK input");

    void* busyHandle = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
    LkgWorkerPreviewLeaseIdentityV1 busyIdentity{ 0xFFFFFFFFu, 9, 77, 88 };
    Check(acquire(&busyHandle, &busyIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY && busyHandle == nullptr &&
        busyIdentity.structSize == sizeof(busyIdentity) && busyIdentity.version == 1 &&
        busyIdentity.endpointId == 0 && busyIdentity.leaseGeneration == 0,
        "concurrent acquire is retryable and clears all output identity fields");
    Check(lkg_worker_ack_preview_lease_return_v1(firstHandle, &firstIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY,
        "active lease cannot be acknowledged as returned");
    LkgWorkerPreviewLeaseIdentityV1 staleIdentity = firstIdentity;
    ++staleIdentity.leaseGeneration;
    Check(lkg_worker_request_preview_lease_return_v1(firstHandle, &staleIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_STALE,
        "stale lease generation cannot request return");
    Check(lkg_worker_request_preview_lease_return_v1(reinterpret_cast<void*>(static_cast<uintptr_t>(0x12345)),
        &firstIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_STALE,
        "unknown lease token is rejected rather than treated as a returned proof");

    auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_worker_render_event_func());
    auto firstTexture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 16, 16);
    void* draw = lkg_worker_make_draw_data(firstHandle, firstTexture.Get(), 4, 4, 1.0f, 1.0f);
    Check(draw != nullptr, "first lease accepts a profile-grid render");
    void* consumedFirstLeaseDrawCallback = draw;
    callback(0, draw);
    Check(WaitCompleted(firstHandle, 1, 5000), "first lease draw completes through stable input");
    Check(g_lastDrawSession.load(std::memory_order_acquire) == firstSession &&
        g_lastDrawTextureIdentity.load(std::memory_order_acquire) == firstStableInput,
        "Bridge receives the persistent stable input rather than a transfer slot");
    Check(lkg_worker_make_draw_data(firstHandle, firstTexture.Get(), 4, 3, 1.0f, 1.0f) == nullptr,
        "incompatible source grid is rejected without remapping");

    LkgWorkerStatus beforeReturn{};
    Check(lkg_worker_poll(firstHandle, &beforeReturn) == 0, "poll first lease before return");
    void* delayedDraw = lkg_worker_make_draw_data(firstHandle, firstTexture.Get(), 4, 4, 1.0f, 1.0f);
    Check(delayedDraw != nullptr, "unsubmitted draw token created before lease return");

    Check(lkg_worker_request_preview_lease_return_v1(firstHandle, &firstIdentity) == 0 &&
        lkg_worker_request_preview_lease_return_v1(firstHandle, &firstIdentity) == 0,
        "duplicate lease return request is idempotent");
    Check(lkg_worker_make_draw_data(firstHandle, firstTexture.Get(), 4, 4, 1.0f, 1.0f) == nullptr,
        "new submissions are rejected as soon as lease return is requested");
    void* rejectedLeaseDraw = reinterpret_cast<void*>(1);
    Check(lkg_worker_try_make_preview_draw_data_v1(firstHandle, &firstIdentity, firstTexture.Get(),
        4, 4, 1.0f, 1.0f, &rejectedLeaseDraw) == LKG_WORKER_PREVIEW_LEASE_RESULT_STALE && !rejectedLeaseDraw,
        "draw admission reports returning lease as stale instead of retryable queue busy");
    callback(0, delayedDraw);
    LkgWorkerPreviewLeaseStatusV1 returnedDrawStatus{};
    const int32_t statusAfterReturn = lkg_worker_get_preview_lease_status_v1(firstHandle, &firstIdentity, &returnedDrawStatus);
    Check(statusAfterReturn == 0 && returnedDrawStatus.workerStatus.completed == beforeReturn.completed &&
        returnedDrawStatus.workerStatus.dropped >= beforeReturn.dropped + 1,
        "late draw callback is dropped and cannot submit after return begins");
    Check(lkg_worker_pending_count() == 1, "returning lease is counted as outstanding ownership");
    Check(lkg_worker_ack_preview_lease_return_v1(firstHandle, &firstIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY,
        "return cannot be acknowledged before native hide and drain proof");
    Check(WaitPreviewLeaseState(firstHandle, firstIdentity, LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN),
        "lease return is proven after hide and producer drain");
    LkgWorkerPreviewLeaseStatusV1 firstReturned{};
    Check(lkg_worker_get_preview_lease_status_v1(firstHandle, &firstIdentity, &firstReturned) == 0 &&
        firstReturned.lastResult == 0 && firstReturned.workerStatus.state == LKG_WORKER_CLOSED &&
        firstReturned.endpointId == firstEndpoint && firstReturned.leaseGeneration == firstGeneration,
        "returned lease status carries its identity and closed worker status");
    Check(lkg_worker_pending_count() == 1, "unacknowledged return remains outstanding");
    void* unackedHandle = reinterpret_cast<void*>(static_cast<uintptr_t>(1));
    LkgWorkerPreviewLeaseIdentityV1 unackedIdentity{ 0xFFFFFFFFu, 9, 77, 88 };
    Check(acquire(&unackedHandle, &unackedIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY &&
        unackedHandle == nullptr && unackedIdentity.endpointId == 0 && unackedIdentity.leaseGeneration == 0,
        "returned but unacknowledged lease blocks the next acquire");
    Check(lkg_worker_ack_preview_lease_return_v1(firstHandle, &firstIdentity) == 0 &&
        lkg_worker_ack_preview_lease_return_v1(firstHandle, &firstIdentity) == 0 &&
        lkg_worker_pending_count() == 0,
        "return acknowledgement is idempotent and releases the acquire barrier");
    Check(lkg_worker_get_preview_lease_status_v1(firstHandle, &firstIdentity, &firstReturned) == 0 &&
        firstReturned.state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN && firstReturned.lastResult == 0 &&
        firstReturned.workerStatus.state == LKG_WORKER_CLOSED,
        "acknowledged return proof remains bound to its original generation");
    LkgWorkerStatus stalePoll{};
    Check(lkg_worker_poll(firstHandle, &stalePoll) == LKG_WORKER_PREVIEW_LEASE_RESULT_STALE,
        "old generation handle is stale after acknowledgement");
    Check(lkg_worker_make_draw_data(firstHandle, firstTexture.Get(), 4, 4, 1.0f, 1.0f) == nullptr,
        "old generation cannot create draw data after acknowledgement");
    firstTexture.Reset();

    void* idleMetadata = lkg_worker_begin_metadata_v1("test-runtime", "ViewWeave");
    Check(idleMetadata != nullptr, "idle metadata query attaches to the parked endpoint");
    Check(WaitForDisplaySnapshot(idleMetadata, 1), "idle metadata proxy runs its initial query on the endpoint");
    std::vector<LkgWorkerDisplaySnapshotItemV1> displayItems;
    LkgWorkerDisplaySnapshotStatusV1 displayStatus = GetDisplaySnapshot(idleMetadata, displayItems);
    Check(displayStatus.result == 0 && displayStatus.pending == 0 && displayStatus.itemCount == 1 &&
        displayStatus.snapshotGeneration == 1 && displayItems[0].profile.quilt_columns == 4 &&
        displayItems[0].profile.quilt_rows == 4,
        "idle metadata proxy publishes its own initial snapshot generation");
    uint64_t refreshGeneration = 0;
    Check(lkg_worker_request_display_refresh_v1(idleMetadata, &refreshGeneration) == 0 && refreshGeneration != 0,
        "idle metadata refresh is accepted on the persistent owner");
    Check(WaitForDisplaySnapshot(idleMetadata, refreshGeneration), "idle endpoint publishes refreshed display metadata");
    Check(g_lastDisplayQueryThread.load(std::memory_order_acquire) == firstWorkerThread,
        "idle metadata refresh runs on the same persistent SDK owner thread");
    displayStatus = GetDisplaySnapshot(idleMetadata, displayItems);
    Check(displayStatus.result == 0 && displayStatus.pending == 0 && displayStatus.itemCount == 1 &&
        displayStatus.snapshotGeneration == refreshGeneration &&
        displayItems[0].profile.quilt_columns == 4 && displayItems[0].profile.quilt_rows == 4,
        "idle metadata snapshot reads the same SDK owner without creating a worker");
    LkgWorkerPhysicalDisplayInfoV1 metadataPhysical{};
    metadataPhysical.structSize = sizeof(metadataPhysical);
    metadataPhysical.version = 1;
    Check(lkg_worker_get_physical_display_v1(idleMetadata, &metadataPhysical) == 0 && metadataPhysical.valid == 1 &&
        metadataPhysical.pixelWidth == 1920 && metadataPhysical.pixelHeight == 1080,
        "metadata proxy physical display lookup uses its captured snapshot");
    Check(lkg_worker_end_metadata_v1(idleMetadata) == -3,
        "metadata proxy cannot end before its close proof is complete");
    Check(lkg_worker_close(idleMetadata) == 0 && WaitForMetadataClose(idleMetadata),
        "metadata proxy close waits for its query and proves closure without closing the endpoint");
    Check(lkg_worker_pending_count() == 1,
        "closed metadata proxy remains pending until its owner releases the handle");
    Check(lkg_worker_end_metadata_v1(idleMetadata) == 0 && lkg_worker_pending_count() == 0,
        "metadata proxy releases only after closed cleanup proof");
    Check(g_hostInitializeCount.load() == 1 && g_hostUninitializeCount.load() == 0 &&
        g_sessionDestroyCount.load() == 0,
        "metadata refresh preserves the initialized SDK and hidden session");

    g_testHoldDisplayQuery.store(true, std::memory_order_release);
    g_testDisplayQueryEntered.store(false, std::memory_order_release);
    g_testReleaseDisplayQuery.store(false, std::memory_order_release);
    void* closingMetadata = lkg_worker_begin_metadata_v1("test-runtime", "ViewWeave");
    Check(closingMetadata != nullptr, "in-flight metadata proxy begins on the parked endpoint");
    const auto queryWaitStart = GetTickCount64();
    while (!g_testDisplayQueryEntered.load(std::memory_order_acquire) && GetTickCount64() - queryWaitStart < 2000)
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    Check(g_testDisplayQueryEntered.load(std::memory_order_acquire),
        "metadata proxy initial query entered the SDK owner thread");
    Check(lkg_worker_close(closingMetadata) == 0,
        "metadata close is accepted while its owner-thread query is in flight");
    std::vector<LkgWorkerDisplaySnapshotItemV1> inFlightItems;
    LkgWorkerDisplaySnapshotStatusV1 inFlightStatus = GetDisplaySnapshot(closingMetadata, inFlightItems);
    Check(inFlightStatus.state == LKG_WORKER_CLOSING && inFlightStatus.pending == 1 &&
        inFlightStatus.cleanupFinished == 0 && lkg_worker_end_metadata_v1(closingMetadata) == -3,
        "metadata close cannot claim cleanup while the owner query is still blocked");
    g_testReleaseDisplayQuery.store(true, std::memory_order_release);
    Check(WaitForMetadataClose(closingMetadata),
        "in-flight metadata cancellation completes only after the owner query returns");
    Check(lkg_worker_end_metadata_v1(closingMetadata) == 0 &&
        g_lastDisplayQueryThread.load(std::memory_order_acquire) == firstWorkerThread &&
        g_sessionDestroyCount.load() == 0 && g_hostUninitializeCount.load() == 0,
        "in-flight metadata close releases its proxy without destroying the persistent endpoint");
    g_testHoldDisplayQuery.store(false, std::memory_order_release);

    void* secondHandle = nullptr;
    LkgWorkerPreviewLeaseIdentityV1 secondIdentity{};
    Check(acquire(&secondHandle, &secondIdentity) == 0 && secondHandle != nullptr &&
        secondIdentity.endpointId == firstIdentity.endpointId &&
        secondIdentity.leaseGeneration > firstIdentity.leaseGeneration,
        "next lease keeps endpoint identity and advances its generation");
    Check(WaitLeaseWorkerState(secondHandle, LKG_WORKER_READY), "second preview lease becomes ready");
    Check(lkg_worker_test_window(secondHandle) == firstWindow,
        "reacquire retains the same Bridge owner window");
    Check(lkg_worker_get_preview_lease_status_v1(firstHandle, &firstIdentity, &firstReturned) == 0 &&
        firstReturned.state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN &&
        firstReturned.workerStatus.state == LKG_WORKER_CLOSED,
        "old ACKed generation still reports its own return proof after a newer acquire");
    const uint64_t firstSubmitted = firstReturned.workerStatus.submitted;
    const uint64_t firstCompleted = firstReturned.workerStatus.completed;
    const uint64_t firstDropped = firstReturned.workerStatus.dropped;
    Check(lkg_worker_ack_preview_lease_return_v1(firstHandle, &firstIdentity) == 0 &&
        lkg_worker_request_preview_lease_return_v1(firstHandle, &firstIdentity) == LKG_WORKER_PREVIEW_LEASE_RESULT_STALE,
        "old ACK retry remains idempotent while return requests reject the acknowledged generation");
    LkgWorkerStatus beforeStaleCallback{};
    Check(lkg_worker_poll(secondHandle, &beforeStaleCallback) == 0 &&
        beforeStaleCallback.submitted == 0 && beforeStaleCallback.completed == 0 && beforeStaleCallback.dropped == 0,
        "new lease generation starts render counters at zero");
    callback(0, consumedFirstLeaseDrawCallback);
    LkgWorkerStatus afterStaleCallback{};
    Check(lkg_worker_poll(secondHandle, &afterStaleCallback) == 0 &&
        afterStaleCallback.submitted == beforeStaleCallback.submitted &&
        afterStaleCallback.completed == beforeStaleCallback.completed &&
        afterStaleCallback.dropped == beforeStaleCallback.dropped,
        "consumed old-generation callback replay cannot affect the new lease after its source is released");
    uint64_t secondEndpoint = 0;
    uint64_t secondGeneration = 0;
    uint64_t secondSession = 0;
    uintptr_t secondStableInput = 0;
    Check(lkg_worker_test_get_preview_lease_resources(secondHandle, &secondEndpoint, &secondGeneration,
        &secondSession, &secondStableInput) == 0 && secondEndpoint == firstEndpoint &&
        secondGeneration == secondIdentity.leaseGeneration && secondSession == firstSession &&
        secondStableInput == firstStableInput,
        "reacquire preserves the endpoint, Bridge session, and stable SDK input identity");
    Check(g_visibilityState.load(std::memory_order_acquire) == 1 && g_sessionDestroyCount.load() == 0,
        "reacquire shows the same session without destroying it");

    void* activeMetadata = lkg_worker_begin_metadata_v1("TEST-RUNTIME", "ViewWeave");
    Check(activeMetadata != nullptr && WaitForDisplaySnapshot(activeMetadata, 1),
        "active lease metadata proxy runs its initial query on the same endpoint");
    uint64_t activeRefreshGeneration = 0;
    Check(lkg_worker_request_display_refresh_v1(activeMetadata, &activeRefreshGeneration) == 0 &&
        WaitForDisplaySnapshot(activeMetadata, activeRefreshGeneration),
        "active lease metadata refresh completes without replacing the rendering lease");
    Check(lkg_worker_close(activeMetadata) == 0 && WaitForMetadataClose(activeMetadata) &&
        lkg_worker_end_metadata_v1(activeMetadata) == 0,
        "active metadata proxy closes and ends independently of its rendering lease");
    LkgWorkerStatus activeAfterMetadata{};
    Check(lkg_worker_poll(secondHandle, &activeAfterMetadata) == 0 && activeAfterMetadata.state == LKG_WORKER_READY &&
        activeAfterMetadata.submitted == 0 && activeAfterMetadata.completed == 0 &&
        activeAfterMetadata.dropped == 0,
        "metadata query preserves active lease render status");

    Check(lkg_worker_reset_transfer(secondHandle) == 0, "quiescent lease transfer slots reset for a new quality size");
    auto resizedTexture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 16);
    draw = lkg_worker_make_draw_data(secondHandle, resizedTexture.Get(), 4, 4, 1.0f, 1.0f);
    Check(draw != nullptr, "second lease accepts a resized source quilt with the fixed grid");
    callback(0, draw);
    Check(WaitCompleted(secondHandle, 1, 5000), "resized source quilt uploads and draws");
    LkgWorkerStatus afterSecondGenerationDraw{};
    Check(lkg_worker_poll(secondHandle, &afterSecondGenerationDraw) == 0 &&
        afterSecondGenerationDraw.submitted == 1 && afterSecondGenerationDraw.completed == 1 &&
        afterSecondGenerationDraw.dropped == 0,
        "first draw in the new generation increments only its own counters");
    LkgWorkerPreviewLeaseStatusV1 firstAfterSecondDraw{};
    Check(lkg_worker_get_preview_lease_status_v1(firstHandle, &firstIdentity, &firstAfterSecondDraw) == 0 &&
        firstAfterSecondDraw.workerStatus.submitted == firstSubmitted &&
        firstAfterSecondDraw.workerStatus.completed == firstCompleted &&
        firstAfterSecondDraw.workerStatus.dropped == firstDropped,
        "new-generation draws do not mutate the prior generation return snapshot");
    Check(g_lastDrawSession.load() == firstSession && g_lastDrawTextureIdentity.load() == firstStableInput,
        "resized quality source still draws through the same stable input and session");

    Check(lkg_worker_close_all() == 0, "close_all requests lease return without finalizing the endpoint");
    Check(lkg_worker_request_preview_lease_return_v1(secondHandle, &secondIdentity) == 0,
        "close_all return can be observed idempotently by the lease owner");
    Check(WaitPreviewLeaseState(secondHandle, secondIdentity, LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN),
        "close_all parks the endpoint after proving return");
    Check(lkg_worker_pending_count() == 1, "close_all return remains pending until acknowledgement");
    Check(lkg_worker_ack_preview_lease_return_v1(secondHandle, &secondIdentity) == 0,
        "close_all parked lease acknowledgement");

    void* thirdHandle = nullptr;
    LkgWorkerPreviewLeaseIdentityV1 thirdIdentity{};
    Check(acquire(&thirdHandle, &thirdIdentity) == 0 && thirdHandle != nullptr &&
        thirdIdentity.endpointId == firstEndpoint,
        "close_all does not prevent a later lease on the parked endpoint");
    Check(WaitLeaseWorkerState(thirdHandle, LKG_WORKER_READY), "third preview lease becomes ready");
    uint64_t thirdEndpoint = 0;
    uint64_t thirdGeneration = 0;
    uint64_t thirdSession = 0;
    uintptr_t thirdStableInput = 0;
    Check(lkg_worker_test_get_preview_lease_resources(thirdHandle, &thirdEndpoint, &thirdGeneration,
        &thirdSession, &thirdStableInput) == 0 && thirdEndpoint == firstEndpoint &&
        thirdSession == firstSession && thirdStableInput == firstStableInput && lkg_worker_test_window(thirdHandle) == firstWindow,
        "third lease after close_all preserves the same SDK resources");

    for (uint32_t cycle = 0; cycle < 8; ++cycle) {
        void* returningHandle = thirdHandle;
        const LkgWorkerPreviewLeaseIdentityV1 returningIdentity = thirdIdentity;
        Check(lkg_worker_request_preview_lease_return_v1(thirdHandle, &thirdIdentity) == 0 &&
            WaitPreviewLeaseState(thirdHandle, thirdIdentity, LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN),
            "reacquire race cycle proves prior lease return");
        Check(lkg_worker_ack_preview_lease_return_v1(thirdHandle, &thirdIdentity) == 0,
            "reacquire race cycle acknowledges prior generation");
        thirdHandle = nullptr;
        thirdIdentity = {};
        Check(acquire(&thirdHandle, &thirdIdentity) == 0 && thirdHandle != nullptr &&
            thirdIdentity.endpointId == firstEndpoint && thirdIdentity.leaseGeneration > returningIdentity.leaseGeneration,
            "immediate reacquire after return proof preserves endpoint and advances generation");
        Check(WaitLeaseWorkerState(thirdHandle, LKG_WORKER_READY),
            "worker observes the fully published generation during immediate reacquire");
        uint64_t cycleEndpoint = 0;
        uint64_t cycleGeneration = 0;
        uint64_t cycleSession = 0;
        uintptr_t cycleStableInput = 0;
        Check(lkg_worker_test_get_preview_lease_resources(thirdHandle, &cycleEndpoint, &cycleGeneration,
            &cycleSession, &cycleStableInput) == 0 && cycleEndpoint == firstEndpoint &&
            cycleGeneration == thirdIdentity.leaseGeneration && cycleSession == firstSession &&
            cycleStableInput == firstStableInput && lkg_worker_test_window(thirdHandle) == firstWindow,
            "repeated immediate reacquire keeps session, input, and owner window stable");
        Check(lkg_worker_get_preview_lease_status_v1(returningHandle, &returningIdentity,
            &firstReturned) == 0 && firstReturned.state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN &&
            firstReturned.workerStatus.state == LKG_WORKER_CLOSED,
            "reacquire race retains the prior generation proof independently");
    }

    auto admissionTexture = MakeTexture(DXGI_FORMAT_R8G8B8A8_UNORM, 32, 32);
    const ULONG admissionReferenceBase = ReferenceCount(static_cast<IUnknown*>(admissionTexture.Get()));
    LkgWorkerStatus admissionStart{};
    Check(lkg_worker_poll(thirdHandle, &admissionStart) == 0 && admissionStart.state == LKG_WORKER_READY &&
        admissionStart.submitted == 0 && admissionStart.completed == 0 && admissionStart.dropped == 0,
        "draw admission starts from clean counters on the current lease generation");
    LkgWorkerPreviewLeaseIdentityV1 malformedIdentity = thirdIdentity;
    malformedIdentity.structSize = 0;
    void* rejectedAdmission = reinterpret_cast<void*>(1);
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &malformedIdentity, admissionTexture.Get(),
        4, 4, 1.0f, 1.0f, &rejectedAdmission) == LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT && !rejectedAdmission,
        "draw admission rejects malformed identity and clears output");
    Check(lkg_worker_try_make_preview_draw_data_v1(reinterpret_cast<void*>(static_cast<uintptr_t>(0x12345)),
        &thirdIdentity, admissionTexture.Get(), 4, 4, 1.0f, 1.0f, &rejectedAdmission) ==
        LKG_WORKER_PREVIEW_LEASE_RESULT_STALE && !rejectedAdmission,
        "draw admission rejects an unknown opaque handle");
    LkgWorkerPreviewLeaseIdentityV1 staleDrawIdentity = thirdIdentity;
    ++staleDrawIdentity.leaseGeneration;
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &staleDrawIdentity, admissionTexture.Get(),
        4, 4, 1.0f, 1.0f, &rejectedAdmission) == LKG_WORKER_PREVIEW_LEASE_RESULT_STALE && !rejectedAdmission,
        "draw admission rejects a stale generation");

    void* queuedDraws[3]{};
    for (uint32_t i = 0; i < 3; ++i) {
        Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &thirdIdentity, admissionTexture.Get(),
            4, 4, 1.0f, 1.0f, &queuedDraws[i]) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && queuedDraws[i],
            "draw admission accepts each slot up to the bounded queue capacity");
    }
    const ULONG admissionReferenceFull = ReferenceCount(static_cast<IUnknown*>(admissionTexture.Get()));
    Check(admissionReferenceFull == admissionReferenceBase + 3,
        "each accepted draw payload owns one source reference");
    void* overCapacityDraw = reinterpret_cast<void*>(1);
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &thirdIdentity, admissionTexture.Get(),
        4, 4, 1.0f, 1.0f, &overCapacityDraw) == LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY && !overCapacityDraw &&
        ReferenceCount(static_cast<IUnknown*>(admissionTexture.Get())) == admissionReferenceFull,
        "only a valid saturated queue returns busy and the rejected request allocates no payload or reference");
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &thirdIdentity, admissionTexture.Get(),
        0, 4, 1.0f, 1.0f, &overCapacityDraw) == LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT && !overCapacityDraw,
        "invalid descriptor remains invalid rather than being masked by queue busy");
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &thirdIdentity, admissionTexture.Get(),
        3, 3, 1.0f, 1.0f, &overCapacityDraw) == LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT && !overCapacityDraw,
        "profile-incompatible grid remains invalid rather than being masked by queue busy");
    LkgWorkerStatus fullQueueStatus{};
    Check(lkg_worker_poll(thirdHandle, &fullQueueStatus) == 0 && fullQueueStatus.submitted == 0 &&
        fullQueueStatus.completed == 0 && fullQueueStatus.dropped == 1,
        "saturated admission increments only the drop counter");
    callback(0, queuedDraws[0]);
    callback(0, queuedDraws[1]);
    callback(0, queuedDraws[2]);
    Check(WaitCompleted(thirdHandle, 3, 5000), "admitted draws complete after queue drain");
    LkgWorkerStatus drainedQueueStatus{};
    Check(lkg_worker_poll(thirdHandle, &drainedQueueStatus) == 0 && drainedQueueStatus.submitted == 3 &&
        drainedQueueStatus.completed == 3 && drainedQueueStatus.dropped == 1 &&
        lkg_worker_reset_transfer(thirdHandle) == 0,
        "drained admission retires exactly three accepted payloads and one saturated drop");
    void* replacementDraw = nullptr;
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &thirdIdentity, admissionTexture.Get(),
        4, 4, 1.0f, 1.0f, &replacementDraw) == LKG_WORKER_PREVIEW_LEASE_RESULT_OK && replacementDraw,
        "draw admission accepts again after the full queue drains");
    callback(0, replacementDraw);
    Check(WaitCompleted(thirdHandle, 4, 5000), "post-drain admitted draw completes");
    Check(lkg_worker_poll(thirdHandle, &drainedQueueStatus) == 0 && drainedQueueStatus.submitted == 4 &&
        drainedQueueStatus.completed == 4 && drainedQueueStatus.dropped == 1,
        "post-drain draw increments the new generation counters without another drop");

    Check(lkg_worker_set_preview_paused(thirdHandle, 1) == 0 &&
        WaitPreviewPausePhase(thirdHandle, LKG_WORKER_PREVIEW_PAUSED),
        "lease pause reaches its acknowledged paused phase");
    Check(lkg_worker_try_make_preview_draw_data_v1(thirdHandle, &thirdIdentity, admissionTexture.Get(),
        4, 4, 1.0f, 1.0f, &rejectedAdmission) == LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE && !rejectedAdmission,
        "paused lease admission is rejected as a lifecycle failure, never queue busy");
    Check(lkg_worker_set_preview_paused(thirdHandle, 0) == 0 &&
        WaitPreviewPausePhase(thirdHandle, LKG_WORKER_PREVIEW_RUNNING),
        "lease resumes after paused admission rejection");

    void* finalMetadata = lkg_worker_begin_metadata_v1("TEST-RUNTIME", "ViewWeave");
    Check(finalMetadata != nullptr && WaitForDisplaySnapshot(finalMetadata, 1),
        "final shutdown metadata proxy completes its initial query");
    Check(lkg_worker_close(finalMetadata) == 0 && WaitForMetadataClose(finalMetadata),
        "final shutdown metadata proxy is closed before endpoint teardown");

    Check(lkg_worker_request_final_shutdown() == 0, "explicit final shutdown closes active lease and endpoint");
    LkgWorkerFinalShutdownStatusV1 finalStatus{};
    finalStatus.structSize = sizeof(finalStatus);
    finalStatus.version = 1;
    Check(lkg_worker_wait_final_shutdown_v1(5000, &finalStatus) == 0 &&
        finalStatus.state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE && finalStatus.hostJoined == 1 &&
        finalStatus.registeredContexts == 0,
        "final shutdown proves SDK release and collects the endpoint");
    std::vector<LkgWorkerDisplaySnapshotItemV1> finalItems;
    LkgWorkerDisplaySnapshotStatusV1 finalSnapshot = GetDisplaySnapshot(finalMetadata, finalItems);
    Check(finalSnapshot.state == LKG_WORKER_CLOSED && finalSnapshot.cleanupFinished == 1 &&
        lkg_worker_end_metadata_v1(finalMetadata) == 0,
        "metadata close proof remains readable and releasable after explicit endpoint finalization");
    Check(lkg_worker_pending_count() == 0 && g_hostInitializeCount.load() == 1 &&
        g_hostUninitializeCount.load() == 1 && g_sessionDestroyCount.load() == 1,
        "final shutdown uninitializes once and closes the single persistent session once");
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
    void* handle = nullptr;
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
    if (visible) g_visibilityShowCalls.fetch_add(1, std::memory_order_acq_rel);
    else g_visibilityHideCalls.fetch_add(1, std::memory_order_acq_rel);
    g_visibilityState.store(visible, std::memory_order_release);
    return 0;
}
void* lkg_bridge_render_event_func(void) {
    return reinterpret_cast<void*>(+[](int eventId, void* data) {
        if (eventId == 1) delete static_cast<uint64_t*>(data);
    });
}
void* lkg_bridge_make_draw_data(uint64_t, void*, uint32_t, uint32_t, float, float) { return nullptr; }
void* lkg_bridge_make_close_data(uint64_t session) { return new uint64_t(session); }
int32_t lkg_bridge_smoke_draw(uint64_t session, void* texture, uint32_t, uint32_t, float, float) {
    if (g_drawThrow.load()) throw std::runtime_error("stub draw exception");
    if (g_drawFailure.load()) return -202;
    VerifyWorkerTexture(texture);
    g_lastDrawSession.store(session, std::memory_order_release);
    g_lastDrawTextureIdentity.store(reinterpret_cast<uintptr_t>(texture), std::memory_order_release);
    g_drawEntered.store(true, std::memory_order_release);
    while (g_holdDraw.load(std::memory_order_acquire)) Sleep(1);
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
    g_testingD3D12.store(true);
    void* handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
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
    handle = lkg_worker_begin("test-runtime", "worker-transport-test", 0, g_testD3D12Device.Get());
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
    verifyUnknownSubmission(0, "worker-transport-test");
    verifyUnknownSubmission(UINT64_MAX, "worker-transport-test");
    g_testingD3D12.store(false);
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
    TestD3D12RingColors();
    TestCachedPhysicalDisplayCalibration();
    TestMetadataOnlyRefreshLifecycle();
    TestPersistentSdkConfigurationMismatchIsRejected();
    TestActiveWorkerDisplayRefreshDoesNotMutateRenderStatus();
    TestPreviewPauseResumePreservesSessionAndDrains();
    TestPreviewPauseVisibilityFailuresDoNotAcknowledge();
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
    std::fprintf(stdout, "worker_transport_test pass draws=%u\n", g_drawCount.load());
    return 0;
}
