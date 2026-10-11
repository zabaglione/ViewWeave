#include "bridge_worker_transport.h"
#include "bridge_worker_common.h"
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include "bridge_d3d12_internal.h"
#include "bridge_subpixel_metadata.h"
#include "manual_subpixel_presenter.h"
#include "../../common/subpixel_calibration.h"
#if defined(LKG_WORKER_TRANSPORT_TESTING)
#include "../../tests/worker_transport_test_hooks.h"
#endif
#include <atomic>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <condition_variable>
#include <cmath>
#include <cstdlib>
#include <mutex>
#include <memory>
#include <new>
#include <string>
#include <stdexcept>
#include <thread>
#include <unordered_set>
#include <unordered_map>
#include <utility>
#include <vector>

using Microsoft::WRL::ComPtr;

bool lkg_bridge_worker_claim();
void lkg_bridge_worker_release_claim();
void lkg_bridge_worker_enter();
void lkg_bridge_worker_leave();
extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void);
extern "C" int32_t lkg_bridge_sdk_shutdown_incomplete(void);
extern "C" int32_t lkg_bridge_close_sdk_admission(void);
extern "C" int32_t lkg_bridge_worker_host_begin_sdk(const char* runtimePath, const char* applicationName);
extern "C" int32_t lkg_bridge_worker_host_finalize_sdk(void);

struct WorkerContext {
    std::mutex mutex;
    std::thread thread;
    std::string runtimePath;
    std::string applicationName;
    uint32_t displayOrdinal = 0;
    bool metadataOnly = false;
    bool previewLeaseEndpoint = false;
    LkgWorkerOutputOptionsV1 outputOptions{ sizeof(LkgWorkerOutputOptionsV1), 1, LKG_WORKER_SUBPIXEL_AUTOMATIC, 0 };
    LkgWorkerSubpixelStatusV1 subpixelStatus{ sizeof(LkgWorkerSubpixelStatusV1), 1, 0,
        LKG_WORKER_SUBPIXEL_AUTOMATIC, UINT32_MAX, UINT32_MAX, 0, 0, 0, UINT32_MAX, 0, 0 };
    LkgWorkerSubpixelCalibrationV1 subpixelCalibration{ sizeof(LkgWorkerSubpixelCalibrationV1), 1 };
    LkgWorkerSubpixelCellV1 subpixelCells[4]{};
    std::unique_ptr<lkg::subpixel::ManualSubpixelPresenter> manualPresenter;
    ComPtr<ID3D11Texture2D> canonicalQuilt;
    uint32_t canonicalWidth = 0;
    uint32_t canonicalHeight = 0;
    uint32_t canonicalColumns = 0;
    uint32_t canonicalRows = 0;
    std::atomic<bool> leaseReturnRequested{false};
    std::atomic<bool> leaseFinalCloseRequested{false};
    bool leaseReturnProven = false;
    bool leaseAcquirePending = false;
    bool leaseActive = false;
    uint64_t leaseEndpointId = 0;
    uint64_t leaseGeneration = 0;
    void* currentLeaseToken = nullptr;
    uint64_t bridgeSessionId = 0;
    uint32_t fixedDisplayIndex = UINT32_MAX;
    LkgBridgeDisplayInfo fixedDisplayProfile{};
    LUID producerAdapterLuid{};
    bool producerAdapterKnown = false;
    uint64_t displaySnapshotGeneration = 0;
    uint64_t displayRequestedGeneration = 0;
    bool displayRefreshPending = false;
    bool displayCleanupFinished = false;
    int32_t displaySnapshotResult = 0;
    std::string displaySnapshotError;
    std::vector<LkgWorkerDisplaySnapshotItemV1> displaySnapshot;
    IUnknown* producerDevice = nullptr;
    LkgWorkerStatus status{};
    uint32_t previewPausePhase = LKG_WORKER_PREVIEW_RUNNING;
    bool previewPauseDesired = false;
    LkgWorkerPhysicalDisplayInfoV1 physicalDisplay{ static_cast<uint32_t>(sizeof(LkgWorkerPhysicalDisplayInfoV1)), 1, 0, 0, 0.0f, 0 };
    std::atomic<bool> closeRequested{false};
    std::atomic<bool> finished{false};
    bool hosted = false;
    bool sdkHostFatalFailure = false;
    std::condition_variable condition;
    struct Slot {
        Slot() = default;
        Slot(const Slot&) = delete;
        Slot& operator=(const Slot&) = delete;
        Slot(Slot&& other) noexcept { *this = std::move(other); }
        Slot& operator=(Slot&& other) noexcept {
            if (this == &other) return *this;
            producerTexture = std::move(other.producerTexture);
            producerMutex = std::move(other.producerMutex);
            workerTexture = std::move(other.workerTexture);
            workerMutex = std::move(other.workerMutex);
            sharedHandle = std::exchange(other.sharedHandle, nullptr);
            sharedTexture12 = std::move(other.sharedTexture12);
            sourceTexture12 = std::move(other.sourceTexture12);
            allocator12 = std::move(other.allocator12);
            commandList12 = std::move(other.commandList12);
            producerFence12 = std::move(other.producerFence12);
            producerDevice12 = std::move(other.producerDevice12);
            producerFenceValue12 = std::exchange(other.producerFenceValue12, 0);
            consumerQuery11 = std::move(other.consumerQuery11);
            sourceCopyRetirementQuery11 = std::move(other.sourceCopyRetirementQuery11);
            d3d12Slot = std::exchange(other.d3d12Slot, false);
            consumerCopyPending = std::exchange(other.consumerCopyPending, false);
            consumerTimedOut = std::exchange(other.consumerTimedOut, false);
            quarantined.store(other.quarantined.exchange(false, std::memory_order_acq_rel), std::memory_order_release);
            sourceRetirementUnproven.store(other.sourceRetirementUnproven.exchange(false, std::memory_order_acq_rel), std::memory_order_release);
            sourceCopyCompleted.store(other.sourceCopyCompleted.exchange(false, std::memory_order_acq_rel), std::memory_order_release);
            sourceCopyQueryIssued.store(other.sourceCopyQueryIssued.exchange(false, std::memory_order_acq_rel), std::memory_order_release);
            sourceMutexHeld.store(other.sourceMutexHeld.exchange(false, std::memory_order_acq_rel), std::memory_order_release);
            unknownSubmission = std::exchange(other.unknownSubmission, false);
            consumerCopyStartedTick = std::exchange(other.consumerCopyStartedTick, 0);
            generation = std::exchange(other.generation, 0);
            submittedAtTick = std::exchange(other.submittedAtTick, 0);
            timedOut = std::exchange(other.timedOut, false);
            inUse = std::exchange(other.inUse, false);
            return *this;
        }

        ComPtr<ID3D11Texture2D> producerTexture;
        ComPtr<IDXGIKeyedMutex> producerMutex;
        ComPtr<ID3D11Texture2D> workerTexture;
        ComPtr<IDXGIKeyedMutex> workerMutex;
        HANDLE sharedHandle = nullptr;
        ComPtr<ID3D12Resource> sharedTexture12;
        ComPtr<ID3D12Resource> sourceTexture12;
        ComPtr<ID3D12CommandAllocator> allocator12;
        ComPtr<ID3D12GraphicsCommandList> commandList12;
        ComPtr<ID3D12Fence> producerFence12;
        ComPtr<ID3D12Device> producerDevice12;
        UINT64 producerFenceValue12 = 0;
        ComPtr<ID3D11Query> consumerQuery11;
        ComPtr<ID3D11Query> sourceCopyRetirementQuery11;
        bool d3d12Slot = false;
        bool consumerCopyPending = false;
        bool consumerTimedOut = false;
        std::atomic<bool> quarantined{ false };
        std::atomic<bool> sourceRetirementUnproven{ false };
        std::atomic<bool> sourceCopyCompleted{ false };
        std::atomic<bool> sourceCopyQueryIssued{ false };
        std::atomic<bool> sourceMutexHeld{ false };
        bool unknownSubmission = false;
        uint64_t consumerCopyStartedTick = 0;
        uint64_t generation = 0;
        uint64_t submittedAtTick = 0;
        bool timedOut = false;
        bool inUse = false;
    } slots[3];
    struct Job {
        uint32_t slot = 0;
        uint32_t columns = 0;
        uint32_t rows = 0;
        float aspect = 0.0f;
        float zoom = 0.0f;
        uint32_t sourceEncoding = LKG_WORKER_SOURCE_ENCODING_UNORM;
        bool sourceWasTypeless = false;
    };
    Job jobs[3]{};
    uint32_t jobHead = 0;
    uint32_t jobCount = 0;
    bool inFlightJob = false;
    uint32_t inFlightSlot = 0;
    uint32_t pendingTokens = 0;
    uint32_t textureWidth = 0;
    uint32_t textureHeight = 0;
    DXGI_FORMAT textureFormat = DXGI_FORMAT_UNKNOWN;
    uint64_t deviceGeneration = 0;
    uint64_t nextSlotGeneration = 1;
    bool producerD3D12 = false;
    std::atomic<uint64_t> d3d12FenceTimeoutMs{ 10000 };
    bool slotsInitializing = false;
    bool renderFault = false;
    bool cleanupProven = false;
    IUnknown* retainedProducerDevice = nullptr;
    ComPtr<ID3D11Device> retainedWorkerDevice;
    ComPtr<ID3D11DeviceContext> retainedWorkerDeviceContext;
#if defined(_WIN32)
    HMODULE moduleReference = nullptr;
#endif
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    DWORD testThreadId = 0;
    HWND testWindow = nullptr;
    bool testPresenterVisible = true;
    std::atomic<bool> testFailWindowPoll{false};
    bool testReadbackRequested = false;
    bool testReadbackComplete = false;
    uint32_t testReadbackWaiters = 0;
    int32_t testReadbackResult = -1;
    uint32_t testReadbackWidth = 0;
    uint32_t testReadbackHeight = 0;
    std::vector<uint8_t> testReadbackPixels;
    std::string testReadbackError;
    std::atomic<uint32_t> testMessageCount{0};
    std::atomic<uintptr_t> testWorkerDeviceIdentity{0};
#endif
};

static bool D3D12OwnershipRetired(const WorkerContext::Slot& slot) {
    return !slot.unknownSubmission && slot.producerFenceValue12 == 0 && !slot.sourceTexture12 &&
        !slot.producerFence12 && !slot.producerDevice12 && !slot.consumerCopyPending &&
        !slot.sourceRetirementUnproven && !slot.sourceMutexHeld;
}

static bool SlotGpuOwnershipRetired(const WorkerContext::Slot& slot) {
    return !slot.inUse && !slot.quarantined && D3D12OwnershipRetired(slot);
}

struct WorkerHost {
    std::mutex mutex;
    std::condition_variable condition;
    WorkerContext* queued = nullptr;
    WorkerContext* active = nullptr;
    bool stopRequested = false;
    bool stopped = false;
    bool joined = false;
    bool sdkFaulted = false;
    int32_t sdkFinalizationResult = 0;
    std::thread thread;
    // The vendor SDK keeps D3D11 state across session windows. Keep its worker
    // device identity stable for the lifetime of the persistent SDK owner.
    ComPtr<ID3D11Device> workerDevice;
    ComPtr<ID3D11DeviceContext> workerDeviceContext;
    LUID workerAdapterLuid{};
    D3D_FEATURE_LEVEL workerFeatureLevel{};
    bool workerDeviceInitialized = false;
    bool workerDeviceFaulted = false;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    std::atomic<uint32_t> testWorkerDeviceCreateCount{0};
    std::atomic<uintptr_t> testWorkerDeviceIdentity{0};
    std::atomic<bool> testForceAdapterMismatch{false};
#endif
#if defined(_WIN32)
    HMODULE moduleReference = nullptr;
#endif
};

struct PreviewLeaseTokenRecord {
    WorkerContext* context = nullptr;
    uint64_t endpointId = 0;
    uint64_t generation = 0;
    uint32_t state = LKG_WORKER_PREVIEW_LEASE_ACTIVE;
    bool acknowledged = false;
    int32_t lastResult = 0;
    LkgWorkerStatus workerStatus{};
};

struct PreviewMetadataTokenRecord {
    WorkerContext* context = nullptr;
    uint64_t endpointId = 0;
    uint32_t displayOrdinal = 0;
    uint64_t requestedGeneration = 0;
    uint64_t endpointRequestedGeneration = 0;
    uint64_t snapshotGeneration = 0;
    uint32_t state = LKG_WORKER_CONNECTING;
    bool refreshPending = false;
    bool closeRequested = false;
    bool cleanupFinished = false;
    int32_t result = 0;
    std::string error;
    std::vector<LkgWorkerDisplaySnapshotItemV1> snapshot;
    bool ended = false;
};

static std::mutex registryMutex;
static std::unordered_set<WorkerContext*> registry;
static std::unordered_map<void*, std::unique_ptr<PreviewLeaseTokenRecord>> previewLeaseTokenRegistry;
static std::unordered_map<void*, std::unique_ptr<PreviewMetadataTokenRecord>> previewMetadataTokenRegistry;
static WorkerContext* previewLeaseEndpoint = nullptr;
static uint64_t nextPreviewEndpointId = 1;
static uint64_t nextPreviewLeaseGeneration = 1;
static bool finalShutdownRequested = false;
static std::atomic<bool> finalShutdownAdmissionClosed{false};
static std::atomic<uint32_t> finalShutdownState{LKG_WORKER_FINAL_SHUTDOWN_NONE};
static std::atomic<int32_t> finalShutdownLastResult{0};
static std::mutex hostRegistryMutex;
static WorkerHost* persistentHost = nullptr;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
static std::atomic<bool> testThrowDuringWorkerCleanup{false};
static std::atomic<bool> testThrowDuringDisplayQuery{false};
#endif

struct WorkerDrawData {
    uint64_t id = 0;
    WorkerContext* context = nullptr;
    IUnknown* texture = nullptr;
    uint32_t columns = 0;
    uint32_t rows = 0;
    float aspect = 0.0f;
    float zoom = 0.0f;
    uint32_t sourceEncoding = LKG_WORKER_SOURCE_ENCODING_UNORM;
    uint64_t leaseGeneration = 0;
    bool sourceWasTypeless = false;
    ~WorkerDrawData() { if (texture) texture->Release(); }
};

static std::mutex tokenMutex;
static std::unordered_map<uint64_t, std::unique_ptr<WorkerDrawData>> tokenRegistry;
static std::atomic<uint64_t> nextTokenId{1};
static std::atomic<uint32_t> testD3D12ConsumerPendingPolls{0};
static std::atomic<bool> testD3D12FenceRemoved{false};
#if defined(LKG_WORKER_TRANSPORT_TESTING)
static std::atomic<uint32_t> testSourceCopyPendingPolls{0};
static std::atomic<uint32_t> testSourceCopyQueryFailures{0};
static std::atomic<bool> testHoldSourceCopyQuery{false};
static std::atomic<uint32_t> testPresenterEarlyResult{0};
static std::atomic<bool> testThrowAfterSourceCopy{false};
static std::atomic<bool> testThrowAfterWorkerDequeue{false};
#endif

static bool ValidPreviewLeaseIdentity(const LkgWorkerPreviewLeaseIdentityV1* identity) {
    return identity && identity->structSize == sizeof(LkgWorkerPreviewLeaseIdentityV1) && identity->version == 1 &&
        identity->endpointId != 0 && identity->leaseGeneration != 0;
}

static PreviewLeaseTokenRecord* FindPreviewLeaseTokenLocked(void* handle) {
    const auto found = previewLeaseTokenRegistry.find(handle);
    return found == previewLeaseTokenRegistry.end() ? nullptr : found->second.get();
}

static bool PreviewMetadataTokenAttachedLocked(const PreviewMetadataTokenRecord& token) {
    return !token.ended && token.context && token.context == previewLeaseEndpoint &&
        token.context->leaseEndpointId == token.endpointId;
}

static bool PreviewMetadataTokenFinalized(const PreviewMetadataTokenRecord& token) {
    return !token.ended && token.closeRequested && token.state == LKG_WORKER_CLOSED && token.cleanupFinished &&
        finalShutdownState.load(std::memory_order_acquire) == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE;
}

static WorkerContext* ResolveActiveWorkerHandleLocked(void* handle, PreviewLeaseTokenRecord** leaseRecord = nullptr) {
    if (leaseRecord) *leaseRecord = nullptr;
    if (!handle) return nullptr;
    if (PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(handle)) {
        if (token->acknowledged || token->state != LKG_WORKER_PREVIEW_LEASE_ACTIVE || !token->context ||
            token->context != previewLeaseEndpoint || token->context->leaseEndpointId != token->endpointId ||
            token->context->leaseGeneration != token->generation || token->context->currentLeaseToken != handle)
            return nullptr;
        if (leaseRecord) *leaseRecord = token;
        return token->context;
    }
    auto* context = static_cast<WorkerContext*>(handle);
    return registry.find(context) == registry.end() ? nullptr : context;
}

static WorkerContext* ResolveMetadataWorkerHandleLocked(void* handle, PreviewMetadataTokenRecord** metadataRecord = nullptr) {
    if (metadataRecord) *metadataRecord = nullptr;
    if (!handle) return nullptr;
    const auto metadata = previewMetadataTokenRegistry.find(handle);
    if (metadata != previewMetadataTokenRegistry.end()) {
        PreviewMetadataTokenRecord* token = metadata->second.get();
        if (!PreviewMetadataTokenAttachedLocked(*token)) return nullptr;
        if (metadataRecord) *metadataRecord = token;
        return token->context;
    }
    return ResolveActiveWorkerHandleLocked(handle);
}

static UINT64 D3D12FenceCompletedValue(ID3D12Fence* fence) {
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    if (testD3D12FenceRemoved.load(std::memory_order_acquire)) return UINT64_MAX;
#endif
    return fence ? fence->GetCompletedValue() : 0;
}
static thread_local WorkerContext* renderExceptionContext = nullptr;
static thread_local uint32_t renderExceptionSlot = 3;
static void SetFault(WorkerContext& context, uint32_t code, const char* text);
static void PumpWorkerMessages(WorkerContext& context);

static constexpr uint32_t kMaximumMetadataDisplays = 256;

static bool QueryDisplaySnapshot(uint64_t generation, std::vector<LkgWorkerDisplaySnapshotItemV1>& items,
    int32_t& result, std::string& error) {
    items.clear();
    error.clear();
    result = 0;
    try {
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    if (testThrowDuringDisplayQuery.exchange(false, std::memory_order_acq_rel))
        throw std::runtime_error("injected display metadata query exception");
#endif
    uint32_t count = 0;
    result = lkg_bridge_get_display_count(&count);
    if (result != 0) {
        error = "Bridge display enumeration failed.";
        return false;
    }
    if (count > kMaximumMetadataDisplays) {
        result = -4;
        error = "Bridge returned more displays than the metadata limit.";
        return false;
    }
    try { items.reserve(count); }
    catch (...) {
        result = -5;
        error = "Display metadata allocation failed.";
        return false;
    }
    for (uint32_t ordinal = 0; ordinal < count; ++ordinal) {
        LkgWorkerDisplaySnapshotItemV1 item{};
        item.structSize = sizeof(item);
        item.version = 1;
        item.displayOrdinal = ordinal;
        item.reserved0 = 0;
        item.physical.structSize = sizeof(item.physical);
        item.physical.version = 1;
        LkgBridgePhysicalDisplayInfoV1 physical{};
        physical.structSize = sizeof(physical);
        physical.version = 1;
        result = lkg_bridge_get_display(ordinal, &item.profile);
        if (result != 0) {
            items.clear();
            error = "Bridge display profile lookup failed.";
            return false;
        }
        const int32_t physicalResult = lkg_bridge_get_physical_display_v1(item.profile.index, &physical);
        if (physicalResult == 0 && physical.valid && physical.pixelWidth > 0 && physical.pixelHeight > 0 &&
            std::isfinite(physical.dpi) && physical.dpi > 0.0f) {
            item.physical.pixelWidth = physical.pixelWidth;
            item.physical.pixelHeight = physical.pixelHeight;
            item.physical.dpi = physical.dpi;
            item.physical.valid = 1;
        } else {
            item.physical.pixelWidth = 0;
            item.physical.pixelHeight = 0;
            item.physical.dpi = 0.0f;
            item.physical.valid = 0;
        }
        items.push_back(item);
    }
    (void)generation;
    return true;
    } catch (...) {
        items.clear();
        result = -6;
        error = "Bridge display metadata query threw an exception.";
        return false;
    }
}

static void PublishDisplaySnapshot(WorkerContext& context, uint64_t generation,
    std::vector<LkgWorkerDisplaySnapshotItemV1>&& items, int32_t result, std::string&& error) {
    std::lock_guard<std::mutex> lock(context.mutex);
    if (context.closeRequested.load(std::memory_order_acquire)) return;
    context.displaySnapshot = std::move(items);
    context.displaySnapshotGeneration = generation;
    context.displaySnapshotResult = result;
    context.displaySnapshotError = std::move(error);
    if (context.metadataOnly) context.status.state = LKG_WORKER_READY;
    context.condition.notify_all();
}

static int32_t RequestPreviewMetadataSnapshotLocked(WorkerContext& context, PreviewMetadataTokenRecord& token,
    uint64_t* requestedGeneration) {
    if (!requestedGeneration || token.ended || token.closeRequested || token.refreshPending ||
        finalShutdownRequested || context.leaseFinalCloseRequested.load(std::memory_order_acquire) ||
        context.closeRequested.load(std::memory_order_acquire) || context.status.state == LKG_WORKER_CLOSING ||
        context.status.state == LKG_WORKER_FAULT)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    const bool parkedLeaseEndpoint = context.previewLeaseEndpoint && context.leaseReturnProven &&
        !context.leaseActive && context.status.state == LKG_WORKER_CLOSED;
    if (context.status.state == LKG_WORKER_CLOSED && !parkedLeaseEndpoint)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    if (context.displayRequestedGeneration == UINT64_MAX || token.requestedGeneration == UINT64_MAX)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;

    ++context.displayRequestedGeneration;
    ++token.requestedGeneration;
    token.endpointRequestedGeneration = context.displayRequestedGeneration;
    token.refreshPending = true;
    token.cleanupFinished = false;
    token.result = 0;
    token.error.clear();
    if (token.snapshotGeneration == 0) token.state = LKG_WORKER_CONNECTING;
    else token.state = LKG_WORKER_READY;
    context.displayRefreshPending = true;
    *requestedGeneration = token.requestedGeneration;
    context.condition.notify_all();
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

static void PublishPreviewMetadataSnapshots(WorkerContext& context, uint64_t endpointGeneration,
    const std::vector<LkgWorkerDisplaySnapshotItemV1>& items, int32_t result, const std::string& error) noexcept {
    try {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        for (auto& entry : previewMetadataTokenRegistry) {
            PreviewMetadataTokenRecord& token = *entry.second;
            if (token.context != &context || token.ended || !token.refreshPending ||
                token.endpointRequestedGeneration > endpointGeneration) continue;
            try {
                token.snapshot = items;
                token.result = result;
                token.error = error;
            } catch (...) {
                token.snapshot.clear();
                token.result = -6;
                token.error = "Bridge metadata snapshot could not be copied to its request.";
            }
            token.snapshotGeneration = token.requestedGeneration;
            token.refreshPending = false;
            token.state = token.closeRequested ? LKG_WORKER_CLOSED : LKG_WORKER_READY;
            token.cleanupFinished = token.closeRequested;
        }
    } catch (...) {
    }
}

static void FinishPreviewMetadataSnapshots(WorkerContext& context) noexcept {
    try {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        for (auto& entry : previewMetadataTokenRegistry) {
            PreviewMetadataTokenRecord& token = *entry.second;
            if (token.context != &context || token.ended) continue;
            if (token.refreshPending) {
                token.snapshotGeneration = token.requestedGeneration;
                token.refreshPending = false;
                token.result = -7;
                token.error = "Bridge metadata owner stopped before publishing the requested snapshot.";
            }
            if (token.closeRequested) {
                token.state = LKG_WORKER_CLOSED;
                token.cleanupFinished = true;
            } else if (context.status.state == LKG_WORKER_FAULT) {
                token.state = LKG_WORKER_FAULT;
                token.cleanupFinished = true;
            }
        }
    } catch (...) {
    }
}

static bool RefreshDisplaySnapshot(WorkerContext& context, uint64_t generation) {
    std::vector<LkgWorkerDisplaySnapshotItemV1> items;
    int32_t result = 0;
    std::string error;
    const bool success = QueryDisplaySnapshot(generation, items, result, error);
    try {
        PublishDisplaySnapshot(context, generation, success ? std::vector<LkgWorkerDisplaySnapshotItemV1>(items) :
            std::vector<LkgWorkerDisplaySnapshotItemV1>{}, result, std::string(error));
    } catch (...) {
        result = -6;
        error = "Bridge metadata snapshot could not be published.";
        PublishDisplaySnapshot(context, generation, {}, result, std::string(error));
    }
    PublishPreviewMetadataSnapshots(context, generation, items, result, error);
    return success;
}

#if defined(LKG_WORKER_TRANSPORT_TESTING)
static LRESULT CALLBACK WorkerTestWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    WorkerContext* context = reinterpret_cast<WorkerContext*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lParam);
        context = static_cast<WorkerContext*>(create->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(context));
    }
    if (context && message == WM_APP + 1) {
        context->testMessageCount.fetch_add(1, std::memory_order_relaxed);
        return 0;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

static bool CreateWorkerTestWindow(WorkerContext& context) {
    context.testThreadId = GetCurrentThreadId();
    const wchar_t* className = L"ViewWeaveWorkerTransportTestWindow";
    WNDCLASSW windowClass{};
    windowClass.lpfnWndProc = WorkerTestWindowProc;
    windowClass.hInstance = GetModuleHandleW(nullptr);
    windowClass.lpszClassName = className;
    RegisterClassW(&windowClass);
    context.testWindow = CreateWindowExW(0, className, L"", 0, 0, 0, 0, 0, HWND_MESSAGE, nullptr, windowClass.hInstance, &context);
    return context.testWindow != nullptr;
}

static void DestroyWorkerTestWindow(WorkerContext& context) {
    if (context.testWindow) {
        DestroyWindow(context.testWindow);
        context.testWindow = nullptr;
    }
    UnregisterClassW(L"ViewWeaveWorkerTransportTestWindow", GetModuleHandleW(nullptr));
}
#endif

static void TraceWorker(const char* stage) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file, "worker tid=%lu stage=%s tick=%llu\n",
                 static_cast<unsigned long>(GetCurrentThreadId()),
                 stage ? stage : "unknown",
                 static_cast<unsigned long long>(GetTickCount64()));
    std::fclose(file);
}

static HRESULT PollSourceCopyQuery(ID3D11DeviceContext* workerContext, ID3D11Query* query, BOOL* complete) {
    if (!workerContext || !query || !complete) return E_POINTER;
    *complete = FALSE;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    if (testHoldSourceCopyQuery.load(std::memory_order_acquire)) return S_FALSE;
    uint32_t delayedPolls = testSourceCopyPendingPolls.load(std::memory_order_acquire);
    while (delayedPolls != 0 && !testSourceCopyPendingPolls.compare_exchange_weak(
        delayedPolls, delayedPolls - 1, std::memory_order_acq_rel)) {}
    if (delayedPolls != 0) return S_FALSE;
    uint32_t failures = testSourceCopyQueryFailures.load(std::memory_order_acquire);
    while (failures != 0 && !testSourceCopyQueryFailures.compare_exchange_weak(
        failures, failures - 1, std::memory_order_acq_rel)) {}
    if (failures != 0) return E_FAIL;
#endif
    return workerContext->GetData(query, complete, sizeof(*complete), D3D11_ASYNC_GETDATA_DONOTFLUSH);
}

static bool PollAndRecordSourceRetirement(WorkerContext& context, WorkerContext::Slot& slot,
    ID3D11DeviceContext* workerContext, bool releaseKeyedMutex) {
    if (slot.sourceRetirementUnproven) {
        if (!workerContext || !slot.sourceCopyRetirementQuery11) return false;
        BOOL complete = FALSE;
        const HRESULT queryResult = PollSourceCopyQuery(workerContext, slot.sourceCopyRetirementQuery11.Get(), &complete);
        if (queryResult != S_OK || !complete) return false;
        slot.sourceRetirementUnproven = false;
        slot.sourceCopyCompleted = true;
    }
    if (releaseKeyedMutex && slot.sourceMutexHeld) {
        if (!slot.workerMutex || FAILED(slot.workerMutex->ReleaseSync(0))) {
            slot.quarantined = true;
            return false;
        }
        slot.sourceMutexHeld = false;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        context.condition.notify_all();
    }
    return true;
}

static bool DrainSourceRetirement(WorkerContext& context, ID3D11DeviceContext* workerContext,
    uint64_t timeoutMs) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    bool allProven = true;
    for (;;) {
        bool pending = false;
        for (WorkerContext::Slot& slot : context.slots) {
        if (!slot.sourceRetirementUnproven && !slot.sourceMutexHeld) continue;
        pending = true;
            if (PollAndRecordSourceRetirement(context, slot, workerContext, true)) continue;
            if (GetTickCount64() >= deadline) {
                allProven = false;
                continue;
            }
            Sleep(1);
        }
        if (!pending || GetTickCount64() >= deadline) break;
    }
    for (const WorkerContext::Slot& slot : context.slots) {
        if (slot.sourceRetirementUnproven || slot.sourceMutexHeld) allProven = false;
    }
    return allProven;
}

static void TracePersistentVisibilityRetained(uint64_t endpointId, uint64_t generation,
    uint64_t sessionId, const char* operation) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file,
                 "worker pid=%lu tid=%lu stage=persistent-visibility-retained operation=%s endpoint_id=%llu lease_generation=%llu session_id=%llu sdk_visibility_changed=0 sdk_hide_called=0 logical_transition=1 tick_ms=%llu\n",
                 static_cast<unsigned long>(GetCurrentProcessId()),
                 static_cast<unsigned long>(GetCurrentThreadId()),
                 operation ? operation : "unknown",
                 static_cast<unsigned long long>(endpointId),
                 static_cast<unsigned long long>(generation),
                 static_cast<unsigned long long>(sessionId),
                 static_cast<unsigned long long>(GetTickCount64()));
    std::fclose(file);
}

static bool NativeTraceEnabled() {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    return path && *path;
}

static void TraceDisplayCount(int32_t result, uint32_t count, uint32_t displayOrdinal) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file, "worker tid=%lu stage=display-count countResult=%d count=%u displayOrdinal=%u tick_ms=%llu\n",
                 static_cast<unsigned long>(GetCurrentThreadId()), result, count, displayOrdinal,
                 static_cast<unsigned long long>(GetTickCount64()));
    std::fclose(file);
}

static void TraceWorkerShutdownEvent(const char* stage, uint64_t sessionId, int slotIndex = -1) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file,
                 "shutdown pid=%lu tid=%lu tick_ms=%llu session_id=%llu stage=%s slot=%d\n",
                 static_cast<unsigned long>(GetCurrentProcessId()),
                 static_cast<unsigned long>(GetCurrentThreadId()),
                 static_cast<unsigned long long>(GetTickCount64()),
                 static_cast<unsigned long long>(sessionId),
                 stage ? stage : "unknown", slotIndex);
    std::fclose(file);
}

struct WorkerShutdownSnapshot {
    struct SlotState {
        bool inUse = false;
        bool consumerCopyPending = false;
        bool producerFencePending = false;
        bool unknownSubmission = false;
        bool quarantined = false;
        bool sourceCopyUnproven = false;
        bool sourceMutexHeld = false;
    } slots[3];
    uint32_t pendingTokens = 0;
    uint32_t jobCount = 0;
    bool inFlightJob = false;
    uint64_t sessionId = 0;
};

// Caller must already hold context.mutex; this helper deliberately takes no lock.
static WorkerShutdownSnapshot CaptureWorkerShutdownSnapshot(const WorkerContext& context, uint64_t sessionId) {
    WorkerShutdownSnapshot snapshot{};
    snapshot.pendingTokens = context.pendingTokens;
    snapshot.jobCount = context.jobCount;
    snapshot.inFlightJob = context.inFlightJob;
    snapshot.sessionId = sessionId;
    for (uint32_t i = 0; i < 3; ++i) {
        snapshot.slots[i].inUse = context.slots[i].inUse;
        snapshot.slots[i].consumerCopyPending = context.slots[i].consumerCopyPending;
        snapshot.slots[i].producerFencePending = context.slots[i].producerFenceValue12 != 0;
        snapshot.slots[i].unknownSubmission = context.slots[i].unknownSubmission;
        snapshot.slots[i].quarantined = context.slots[i].quarantined;
        snapshot.slots[i].sourceCopyUnproven = context.slots[i].sourceRetirementUnproven;
        snapshot.slots[i].sourceMutexHeld = context.slots[i].sourceMutexHeld;
    }
    return snapshot;
}

static void TraceWorkerShutdownSnapshot(const char* stage, const WorkerShutdownSnapshot& snapshot) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file,
                 "shutdown pid=%lu tid=%lu tick_ms=%llu session_id=%llu stage=%s pendingTokens=%u jobCount=%u inFlightJob=%d slot0.inUse=%d slot0.producerFencePending=%d slot0.unknownSubmission=%d slot0.consumerCopyPending=%d slot0.sourceCopyUnproven=%d slot0.sourceMutexHeld=%d slot0.quarantined=%d slot1.inUse=%d slot1.producerFencePending=%d slot1.unknownSubmission=%d slot1.consumerCopyPending=%d slot1.sourceCopyUnproven=%d slot1.sourceMutexHeld=%d slot1.quarantined=%d slot2.inUse=%d slot2.producerFencePending=%d slot2.unknownSubmission=%d slot2.consumerCopyPending=%d slot2.sourceCopyUnproven=%d slot2.sourceMutexHeld=%d slot2.quarantined=%d\n",
                 static_cast<unsigned long>(GetCurrentProcessId()),
                 static_cast<unsigned long>(GetCurrentThreadId()),
                 static_cast<unsigned long long>(GetTickCount64()),
                 static_cast<unsigned long long>(snapshot.sessionId),
                 stage ? stage : "unknown", snapshot.pendingTokens, snapshot.jobCount,
                 snapshot.inFlightJob ? 1 : 0,
                 snapshot.slots[0].inUse ? 1 : 0,
                 snapshot.slots[0].producerFencePending ? 1 : 0,
                 snapshot.slots[0].unknownSubmission ? 1 : 0,
                 snapshot.slots[0].consumerCopyPending ? 1 : 0,
                 snapshot.slots[0].sourceCopyUnproven ? 1 : 0,
                 snapshot.slots[0].sourceMutexHeld ? 1 : 0,
                 snapshot.slots[0].quarantined ? 1 : 0,
                 snapshot.slots[1].inUse ? 1 : 0,
                 snapshot.slots[1].producerFencePending ? 1 : 0,
                 snapshot.slots[1].unknownSubmission ? 1 : 0,
                 snapshot.slots[1].consumerCopyPending ? 1 : 0,
                 snapshot.slots[1].sourceCopyUnproven ? 1 : 0,
                 snapshot.slots[1].sourceMutexHeld ? 1 : 0,
                 snapshot.slots[1].quarantined ? 1 : 0,
                 snapshot.slots[2].inUse ? 1 : 0,
                 snapshot.slots[2].producerFencePending ? 1 : 0,
                 snapshot.slots[2].unknownSubmission ? 1 : 0,
                 snapshot.slots[2].consumerCopyPending ? 1 : 0,
                 snapshot.slots[2].sourceCopyUnproven ? 1 : 0,
                 snapshot.slots[2].sourceMutexHeld ? 1 : 0,
                 snapshot.slots[2].quarantined ? 1 : 0);
    std::fclose(file);
}

static void RunWorker(WorkerContext* context, WorkerHost* host);
static WorkerHost* GetPersistentHost();
static bool SubmitToPersistentHost(WorkerContext* context);
static bool SameAdapterLuid(const LUID& left, const LUID& right);
static void SetError(WorkerContext& context, uint32_t code, const char* text);

static std::string CanonicalWorkerString(const char* value, bool foldCase) {
    std::string result = value ? value : "";
    for (char& character : result) {
        if (character == '/') character = '\\';
        if (foldCase) character = static_cast<char>(std::tolower(static_cast<unsigned char>(character)));
    }
    return result;
}

static bool CaptureProducerDevice(void* requestedDevice, IUnknown** deviceOut, uint64_t* deviceGeneration,
    bool* d3d12Out, LUID* adapterLuidOut) {
    if (!deviceOut || !deviceGeneration || !d3d12Out || !adapterLuidOut) return false;
    *deviceOut = nullptr;
    *deviceGeneration = 0;
    *d3d12Out = false;
    *adapterLuidOut = {};
    ComPtr<IUnknown> producer;
    if (requestedDevice) {
        producer.Attach(reinterpret_cast<IUnknown*>(requestedDevice));
        producer->AddRef();
    } else {
        uint32_t graphicsApi = 0;
        IUnknown* snapshotDevice = nullptr;
        if (lkg_bridge_unity_device_snapshot(&snapshotDevice, deviceGeneration, &graphicsApi) != 0 || !snapshotDevice || graphicsApi == 0)
            return false;
        producer.Attach(snapshotDevice);
    }
    ComPtr<ID3D12Device> producer12;
    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    if (producer.As(&producer12) == S_OK) {
        *adapterLuidOut = producer12->GetAdapterLuid();
        *d3d12Out = true;
    } else if (producer.As(&dxgiDevice) == S_OK && dxgiDevice->GetAdapter(&adapter) == S_OK) {
        DXGI_ADAPTER_DESC desc{};
        if (FAILED(adapter->GetDesc(&desc))) return false;
        *adapterLuidOut = desc.AdapterLuid;
        *deviceGeneration = lkg_bridge_unity_device_generation();
    } else {
        return false;
    }
    *deviceOut = producer.Detach();
    return true;
}

static void WritePreviewLeaseIdentity(LkgWorkerPreviewLeaseIdentityV1* identity, uint64_t endpointId, uint64_t generation) {
    if (!identity) return;
    identity->structSize = sizeof(*identity);
    identity->version = 1;
    identity->endpointId = endpointId;
    identity->leaseGeneration = generation;
}

static bool PreviewLeaseIdentityMatches(const PreviewLeaseTokenRecord& token,
    const LkgWorkerPreviewLeaseIdentityV1* identity) {
    return ValidPreviewLeaseIdentity(identity) && token.endpointId == identity->endpointId &&
        token.generation == identity->leaseGeneration;
}

static int32_t AllocatePreviewLeaseTokenLocked(WorkerContext& context, PreviewLeaseTokenRecord** tokenOut, void** handleOut) {
    if (!tokenOut || !handleOut || nextPreviewLeaseGeneration == 0 || nextPreviewLeaseGeneration == UINT64_MAX)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    std::unique_ptr<PreviewLeaseTokenRecord> token(new (std::nothrow) PreviewLeaseTokenRecord());
    if (!token) return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    token->context = &context;
    token->endpointId = context.leaseEndpointId;
    token->generation = nextPreviewLeaseGeneration++;
    token->state = LKG_WORKER_PREVIEW_LEASE_ACTIVE;
    void* handle = token.get();
    try {
        auto inserted = previewLeaseTokenRegistry.emplace(handle, std::move(token));
        if (!inserted.second) return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
        *tokenOut = inserted.first->second.get();
        *handleOut = handle;
    } catch (...) {
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    }
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

static void RequestLeaseReturnLocked(WorkerContext& context, PreviewLeaseTokenRecord& token) {
    if (token.state != LKG_WORKER_PREVIEW_LEASE_ACTIVE) return;
    token.state = LKG_WORKER_PREVIEW_LEASE_RETURNING;
    context.leaseReturnRequested.store(true, std::memory_order_release);
    context.previewPauseDesired = false;
    context.condition.notify_all();
}

static bool ValidWorkerOutputOptions(const LkgWorkerOutputOptionsV1* options) {
    return options && options->structSize == sizeof(LkgWorkerOutputOptionsV1) && options->version == 1 &&
        options->subpixelMode <= LKG_WORKER_SUBPIXEL_CALIBRATED_CELLS && options->linearColorSpace <= 1;
}

static bool SameWorkerOutputOptions(const LkgWorkerOutputOptionsV1& left, const LkgWorkerOutputOptionsV1& right) {
    return left.subpixelMode == right.subpixelMode && left.linearColorSpace == right.linearColorSpace;
}

LKG_EXPORT int32_t lkg_worker_acquire_preview_lease_v1(const char* runtimePath, const char* applicationName,
    uint32_t displayOrdinal, void* producerDevice, void** leaseHandle, LkgWorkerPreviewLeaseIdentityV1* identity) {
    const LkgWorkerOutputOptionsV1 options{ sizeof(LkgWorkerOutputOptionsV1), 1, LKG_WORKER_SUBPIXEL_AUTOMATIC, 0 };
    return lkg_worker_acquire_preview_lease_with_output_v1(runtimePath, applicationName, displayOrdinal,
        producerDevice, &options, leaseHandle, identity);
}

LKG_EXPORT int32_t lkg_worker_acquire_preview_lease_with_output_v1(const char* runtimePath, const char* applicationName,
    uint32_t displayOrdinal, void* producerDevice, const LkgWorkerOutputOptionsV1* options,
    void** leaseHandle, LkgWorkerPreviewLeaseIdentityV1* identity) {
    if (leaseHandle) *leaseHandle = nullptr;
    if (identity) WritePreviewLeaseIdentity(identity, 0, 0);
    if (!runtimePath || !runtimePath[0] || !applicationName || !applicationName[0] || !leaseHandle || !identity ||
        !ValidWorkerOutputOptions(options))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    if (finalShutdownAdmissionClosed.load(std::memory_order_acquire))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;

    IUnknown* capturedDevice = nullptr;
    uint64_t deviceGeneration = 0;
    bool producerD3D12 = false;
    LUID adapterLuid{};
    if (!CaptureProducerDevice(producerDevice, &capturedDevice, &deviceGeneration, &producerD3D12, &adapterLuid))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;

    std::string canonicalPath = CanonicalWorkerString(runtimePath, true);
    std::string exactApplication(applicationName);
    WorkerContext* context = nullptr;
    bool newEndpoint = false;
    bool workerClaimAcquired = false;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        if (finalShutdownRequested || finalShutdownAdmissionClosed.load(std::memory_order_acquire)) {
            capturedDevice->Release();
            return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
        }
        context = previewLeaseEndpoint;
        if (context) {
            std::lock_guard<std::mutex> contextLock(context->mutex);
            const std::string existingPath = CanonicalWorkerString(context->runtimePath.c_str(), true);
            if (existingPath != canonicalPath || context->applicationName != exactApplication ||
                context->displayOrdinal != displayOrdinal || !SameWorkerOutputOptions(context->outputOptions, *options) ||
                !context->producerAdapterKnown ||
                !SameAdapterLuid(context->producerAdapterLuid, adapterLuid)) {
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_RESTART_REQUIRED;
            }
            if (context->status.state == LKG_WORKER_FAULT || context->leaseFinalCloseRequested.load(std::memory_order_acquire)) {
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
            }
            if (context->leaseActive || !context->leaseReturnProven || context->currentLeaseToken != nullptr) {
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
            }
        } else {
            if (!lkg_bridge_worker_claim()) {
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
            }
            workerClaimAcquired = true;
            if (nextPreviewEndpointId == 0 || nextPreviewEndpointId == UINT64_MAX) {
                lkg_bridge_worker_release_claim();
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
            }
            context = new (std::nothrow) WorkerContext();
            if (!context) {
                lkg_bridge_worker_release_claim();
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
            }
            context->runtimePath = std::move(canonicalPath);
            context->applicationName = std::move(exactApplication);
            context->displayOrdinal = displayOrdinal;
            context->producerDevice = capturedDevice;
            context->deviceGeneration = deviceGeneration;
            context->producerD3D12 = producerD3D12;
            context->producerAdapterLuid = adapterLuid;
            context->producerAdapterKnown = true;
            context->previewLeaseEndpoint = true;
            context->outputOptions = *options;
            context->subpixelStatus.requestedMode = options->subpixelMode;
            context->leaseActive = true;
            context->leaseReturnRequested.store(false, std::memory_order_release);
            context->leaseEndpointId = nextPreviewEndpointId++;
            context->status.state = LKG_WORKER_CONNECTING;
            if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                    reinterpret_cast<LPCWSTR>(&lkg_worker_acquire_preview_lease_v1), &context->moduleReference)) {
                delete context;
                lkg_bridge_worker_release_claim();
                capturedDevice->Release();
                return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
            }
            context->hosted = true;
            registry.insert(context);
            previewLeaseEndpoint = context;
            newEndpoint = true;
        }

        PreviewLeaseTokenRecord* token = nullptr;
        void* tokenHandle = nullptr;
        const int32_t tokenResult = AllocatePreviewLeaseTokenLocked(*context, &token, &tokenHandle);
        if (tokenResult != LKG_WORKER_PREVIEW_LEASE_RESULT_OK) {
            if (newEndpoint) {
                registry.erase(context);
                previewLeaseEndpoint = nullptr;
            }
            if (workerClaimAcquired) lkg_bridge_worker_release_claim();
            capturedDevice->Release();
            if (newEndpoint) {
                if (context->moduleReference) FreeLibrary(context->moduleReference);
                delete context;
            }
            return tokenResult;
        }
        if (newEndpoint) {
            context->currentLeaseToken = tokenHandle;
            context->leaseGeneration = token->generation;
            context->leaseReturnProven = false;
            token->workerStatus = context->status;
        } else {
            std::lock_guard<std::mutex> contextLock(context->mutex);
            context->producerDevice = capturedDevice;
            context->deviceGeneration = deviceGeneration;
            context->producerD3D12 = producerD3D12;
            context->leaseReturnRequested.store(false, std::memory_order_release);
            context->leaseReturnProven = false;
            context->previewPauseDesired = false;
            context->previewPausePhase = LKG_WORKER_PREVIEW_RESUMING;
            context->status.state = LKG_WORKER_CONNECTING;
            context->status.lastErrorCode = 0;
            context->status.error[0] = '\0';
            context->status.submitted = 0;
            context->status.completed = 0;
            context->status.dropped = 0;
            context->currentLeaseToken = tokenHandle;
            context->leaseGeneration = token->generation;
            context->leaseActive = true;
            token->workerStatus = context->status;
            context->leaseAcquirePending = true;
            context->condition.notify_all();
        }
        WritePreviewLeaseIdentity(identity, token->endpointId, token->generation);
        *leaseHandle = tokenHandle;
    }

    if (newEndpoint && !SubmitToPersistentHost(context)) {
        {
            std::lock_guard<std::mutex> registryLock(registryMutex);
            {
                std::lock_guard<std::mutex> contextLock(context->mutex);
                context->status.state = LKG_WORKER_FAULT;
                SetError(*context, 24, "persistent worker host unavailable");
            }
            PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(*leaseHandle);
            if (token) {
                token->state = LKG_WORKER_PREVIEW_LEASE_FAULTED;
                token->lastResult = LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
                token->workerStatus = context->status;
                token->context = nullptr;
            }
            registry.erase(context);
            if (previewLeaseEndpoint == context) previewLeaseEndpoint = nullptr;
        }
        if (context->producerDevice) {
            context->producerDevice->Release();
            context->producerDevice = nullptr;
        }
        if (context->moduleReference) {
            FreeLibrary(context->moduleReference);
            context->moduleReference = nullptr;
        }
        lkg_bridge_worker_release_claim();
        delete context;
        *leaseHandle = nullptr;
        WritePreviewLeaseIdentity(identity, 0, 0);
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    }
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

static WorkerHost* GetPersistentHost() {
    std::lock_guard<std::mutex> lock(hostRegistryMutex);
    if (persistentHost) return persistentHost;
    if (finalShutdownAdmissionClosed.load(std::memory_order_acquire)) return nullptr;
    WorkerHost* host = new (std::nothrow) WorkerHost();
    if (!host) return nullptr;
#if defined(_WIN32)
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                            reinterpret_cast<LPCWSTR>(&RunWorker), &host->moduleReference)) {
        delete host;
        return nullptr;
    }
#endif
    try {
        host->thread = std::thread([host]() {
            for (;;) {
                WorkerContext* context = nullptr;
                bool finalize = false;
                {
                    std::unique_lock<std::mutex> lock(host->mutex);
                    host->condition.wait_for(lock, std::chrono::milliseconds(10), [&] { return host->queued || host->stopRequested; });
                    if (!host->queued && host->stopRequested) {
                        finalize = true;
                    } else if (!host->queued) {
                        lock.unlock();
                        MSG message{};
                        for (uint32_t count = 0; count < 64; ++count) {
                            if (!PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE)) break;
                            if (message.message == WM_QUIT) {
                                lock.lock();
                                host->stopRequested = true;
                                if (host->queued) host->queued->closeRequested.store(true, std::memory_order_release);
                                host->condition.notify_all();
                                break;
                            }
                            TranslateMessage(&message);
                            DispatchMessageW(&message);
                        }
                        continue;
                    } else {
                        context = host->queued;
                        host->queued = nullptr;
                        host->active = context;
                    }
                }
                if (finalize) {
                    lkg_bridge_worker_enter();
                    int32_t finalResult = host->sdkFaulted ? -1 : lkg_bridge_worker_host_finalize_sdk();
                    if (finalResult == 0) {
                        WorkerContext* endpoint = nullptr;
                        {
                            std::lock_guard<std::mutex> registryLock(registryMutex);
                            endpoint = previewLeaseEndpoint;
                        }
                    }
                    if (finalResult == 0) {
                        // SDK finalization is the lifetime boundary for the
                        // vendor's cached D3D11 device state. Release our COM
                        // references only after it succeeds.
                        host->workerDeviceContext.Reset();
                        host->workerDevice.Reset();
                        host->workerDeviceInitialized = false;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
                        host->testWorkerDeviceIdentity.store(0, std::memory_order_release);
#endif
                    }
                    lkg_bridge_worker_leave();
                    {
                        std::lock_guard<std::mutex> lock(host->mutex);
                        host->sdkFinalizationResult = finalResult;
                        host->stopped = true;
                        host->condition.notify_all();
                    }
                    return;
                }
                RunWorker(context, host);
                {
                    std::lock_guard<std::mutex> lock(host->mutex);
                    if (!context->cleanupProven || context->sdkHostFatalFailure) host->sdkFaulted = true;
                    context->finished.store(true, std::memory_order_release);
                    lkg_bridge_worker_release_claim();
                    host->active = nullptr;
                    host->condition.notify_all();
                }
            }
        });
    } catch (...) {
#if defined(_WIN32)
        if (host->moduleReference) FreeLibrary(host->moduleReference);
#endif
        delete host;
        return nullptr;
    }
    persistentHost = host;
    return host;
}

static bool SubmitToPersistentHost(WorkerContext* context) {
    if (finalShutdownAdmissionClosed.load(std::memory_order_acquire)) return false;
    WorkerHost* host = GetPersistentHost();
    if (!host) return false;
    std::lock_guard<std::mutex> lock(host->mutex);
    if (finalShutdownAdmissionClosed.load(std::memory_order_acquire) || host->stopRequested || host->sdkFaulted ||
        host->queued || host->active) return false;
    context->hosted = true;
    host->queued = context;
    host->condition.notify_all();
    return true;
}

extern "C" LKG_EXPORT int32_t lkg_worker_shutdown_host(uint32_t timeoutMilliseconds) {
    WorkerHost* host = nullptr;
    {
        std::lock_guard<std::mutex> lock(hostRegistryMutex);
        host = persistentHost;
    }
    if (!host) return 0;
    std::unique_lock<std::mutex> lock(host->mutex);
    if (host->joined) return host->sdkFinalizationResult == 0 ? 0 : -2;
    host->stopRequested = true;
    if (host->queued) {
        host->queued->leaseFinalCloseRequested.store(true, std::memory_order_release);
        host->queued->closeRequested.store(true, std::memory_order_release);
    }
    if (host->active) {
        host->active->leaseFinalCloseRequested.store(true, std::memory_order_release);
        host->active->closeRequested.store(true, std::memory_order_release);
    }
    host->condition.notify_all();
    const bool stopped = host->condition.wait_for(lock, std::chrono::milliseconds(timeoutMilliseconds), [&] { return host->stopped && !host->active && !host->queued; });
    if (!stopped) return -1;
    if (host->thread.joinable()) host->thread.join();
    if (host->sdkFinalizationResult != 0) {
        host->joined = true;
        return -2;
    }
#if defined(_WIN32)
    if (host->moduleReference) FreeLibrary(host->moduleReference);
    host->moduleReference = nullptr;
#endif
    host->joined = true;
    return 0;
}

static void PumpWorkerMessages(WorkerContext& context) {
    MSG message{};
    for (uint32_t count = 0; count < 64; ++count) {
        const BOOL result = PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE);
        if (result == 0) return;
        if (message.message == WM_QUIT) {
            SetFault(context, 18, "worker received WM_QUIT");
            return;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
}

static const char* FormatName(DXGI_FORMAT format) {
    switch (format) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return "R8G8B8A8_TYPELESS";
    case DXGI_FORMAT_R8G8B8A8_UNORM: return "R8G8B8A8_UNORM";
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return "R8G8B8A8_UNORM_SRGB";
    default: return "OTHER";
    }
}

static void TraceSourceDesc(const D3D11_TEXTURE2D_DESC& desc) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file, "source-desc width=%u height=%u mip=%u array=%u format=%u(%s) sampleCount=%u sampleQuality=%u\n",
                 desc.Width, desc.Height, desc.MipLevels, desc.ArraySize,
                 static_cast<unsigned>(desc.Format), FormatName(desc.Format),
                 desc.SampleDesc.Count, desc.SampleDesc.Quality);
    std::fclose(file);
}

static bool EnqueueJob(WorkerContext& context, const WorkerContext::Job& job) {
    if (context.jobCount >= 3) return false;
    const uint32_t tail = (context.jobHead + context.jobCount) % 3;
    context.jobs[tail] = job;
    ++context.jobCount;
    return true;
}

static bool PopJob(WorkerContext& context, WorkerContext::Job& job) {
    if (context.jobCount == 0) return false;
    job = context.jobs[context.jobHead];
    context.jobHead = (context.jobHead + 1) % 3;
    --context.jobCount;
    return true;
}

static bool RequeueFront(WorkerContext& context, const WorkerContext::Job& job) {
    if (context.jobCount >= 3) return false;
    context.jobHead = (context.jobHead + 2) % 3;
    context.jobs[context.jobHead] = job;
    ++context.jobCount;
    return true;
}

IUnknown* lkg_bridge_unity_device_addref();
extern "C" int32_t lkg_bridge_get_worker_render_event_id(int32_t* eventId);

static void SetError(WorkerContext& context, uint32_t code, const char* text) {
    context.status.lastErrorCode = code;
    std::snprintf(context.status.error, sizeof(context.status.error), "%s", text ? text : "worker failure");
}

static void SetFault(WorkerContext& context, uint32_t code, const char* text) {
    std::lock_guard<std::mutex> lock(context.mutex);
    SetError(context, code, text);
    context.status.state = LKG_WORKER_FAULT;
    context.subpixelStatus.valid = 0;
    context.subpixelStatus.effectiveMode = UINT32_MAX;
    context.subpixelStatus.backend = UINT32_MAX;
    context.previewPausePhase = LKG_WORKER_PREVIEW_FAULTED;
    context.previewPauseDesired = false;
    context.closeRequested.store(true, std::memory_order_release);
    context.condition.notify_all();
    TraceWorker("fault");
}

static const char* ValidateNativeSubpixelCalibration(const LkgWorkerSubpixelCalibrationV1& value,
    uint32_t requestedMode, const LkgWorkerSubpixelCellV1 cells[4]) {
    LkgCommonSubpixelCalibration calibration{};
    calibration.width = value.nativeWidth;
    calibration.height = value.nativeHeight;
    calibration.center = value.center;
    calibration.pitch = value.pitch;
    calibration.slope = value.slope;
    calibration.dpi = value.dpi;
    calibration.flipX = value.flipX;
    calibration.viewCone = value.viewCone;
    calibration.fringe = value.fringe;
    calibration.invertedViews = value.invertedViews;
    calibration.cellPatternMode = value.cellPatternMode;
    calibration.cellCount = value.cellCount;
    calibration.redIndex = value.redIndex;
    calibration.blueIndex = value.blueIndex;
    LkgCommonSubpixelCell converted[4]{};
    if (cells) for (size_t i = 0; i < 4; ++i) {
        converted[i] = { cells[i].redX, cells[i].redY, cells[i].greenX, cells[i].greenY,
            cells[i].blueX, cells[i].blueY };
    }
    return lkg_validate_subpixel_calibration(&calibration, requestedMode, cells ? converted : nullptr);
}

static void SetBridgeFault(WorkerContext& context, uint32_t code, const char* operation, int32_t result) {
    char bridgeError[160]{};
    lkg_bridge_last_error(bridgeError, sizeof(bridgeError));
    char message[256]{};
    std::snprintf(message, sizeof(message), "%s failed rc=%d last_error=%s", operation, result, bridgeError[0] ? bridgeError : "none");
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.status.state == LKG_WORKER_FAULT) return;
        SetError(context, code, message);
        context.status.state = LKG_WORKER_FAULT;
        context.subpixelStatus.valid = 0;
        context.subpixelStatus.effectiveMode = UINT32_MAX;
        context.subpixelStatus.backend = UINT32_MAX;
        context.previewPausePhase = LKG_WORKER_PREVIEW_FAULTED;
        context.previewPauseDesired = false;
        context.closeRequested.store(true, std::memory_order_release);
        context.condition.notify_all();
    }
    TraceWorker("fault");
}

static int32_t DrawWorkerImage(WorkerContext& context, ID3D11DeviceContext* workerContext,
    ID3D11Device* workerDevice, uint64_t session, ID3D11Texture2D* source,
    const WorkerContext::Job& job, ComPtr<ID3D11Query>& sourceCopyQuery, bool* sourceRetirementProven) {
    (void)session;
    if (sourceRetirementProven) *sourceRetirementProven = true;
    if (!context.manualPresenter || !source || !workerContext) {
        SetFault(context, 27, "native subpixel presenter input is unavailable");
        return -1;
    }
    if (job.columns == 0 || job.rows == 0 || job.columns > 4096 || job.rows > 4096 ||
        job.columns > UINT32_MAX / job.rows) {
        SetFault(context, 28, "native subpixel input Quilt grid is invalid");
        return -1;
    }

    D3D11_TEXTURE2D_DESC sourceDescription{};
    source->GetDesc(&sourceDescription);
    if ((sourceDescription.Format != DXGI_FORMAT_R8G8B8A8_UNORM &&
         sourceDescription.Format != DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) ||
        sourceDescription.Width == 0 || sourceDescription.Height == 0 ||
        sourceDescription.MipLevels != 1 || sourceDescription.ArraySize != 1 ||
        sourceDescription.SampleDesc.Count != 1 || sourceDescription.Width % job.columns != 0 ||
        sourceDescription.Height % job.rows != 0) {
        SetFault(context, 29, "native subpixel input requires a validated single-sample RGBA8 transport Quilt");
        return -1;
    }
    if (context.manualPresenter->HasPendingGpuWork()) {
        SetFault(context, 30, "native subpixel previous frame has not retired");
        return -1;
    }
    if (!context.canonicalQuilt || context.canonicalWidth != sourceDescription.Width ||
        context.canonicalHeight != sourceDescription.Height) {
        D3D11_TEXTURE2D_DESC canonicalDescription{};
        canonicalDescription.Width = sourceDescription.Width;
        canonicalDescription.Height = sourceDescription.Height;
        canonicalDescription.MipLevels = 1;
        canonicalDescription.ArraySize = 1;
        canonicalDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        canonicalDescription.SampleDesc.Count = 1;
        canonicalDescription.Usage = D3D11_USAGE_DEFAULT;
        canonicalDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> canonical;
        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(sourceDevice.GetAddressOf());
        const HRESULT createResult = sourceDevice ? sourceDevice->CreateTexture2D(
            &canonicalDescription, nullptr, &canonical) : E_POINTER;
        if (FAILED(createResult)) {
            char message[144]{};
            std::snprintf(message, sizeof(message), "raw canonical Quilt allocation failed hr=0x%08lx",
                static_cast<unsigned long>(createResult));
            SetFault(context, 31, message);
            return -1;
        }
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            context.canonicalQuilt = std::move(canonical);
            context.canonicalWidth = sourceDescription.Width;
            context.canonicalHeight = sourceDescription.Height;
        }
    }
    if (context.canonicalColumns != job.columns || context.canonicalRows != job.rows) {
        std::lock_guard<std::mutex> lock(context.mutex);
        context.canonicalColumns = job.columns;
        context.canonicalRows = job.rows;
    }
    if (!workerDevice || !sourceRetirementProven) {
        SetFault(context, 39, "native subpixel copy retirement tracking is unavailable");
        return -1;
    }
    if (!sourceCopyQuery) {
        D3D11_QUERY_DESC queryDescription{ D3D11_QUERY_EVENT, 0 };
        const HRESULT queryResult = workerDevice->CreateQuery(&queryDescription, &sourceCopyQuery);
        if (FAILED(queryResult)) {
            char message[160]{};
            std::snprintf(message, sizeof(message), "source copy retirement query creation failed hr=0x%08lx",
                static_cast<unsigned long>(queryResult));
            SetFault(context, 39, message);
            return -1;
        }
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        context.slots[job.slot].sourceRetirementUnproven = true;
        context.slots[job.slot].sourceCopyCompleted = false;
        context.slots[job.slot].sourceCopyQueryIssued = false;
    }
    workerContext->CopyResource(context.canonicalQuilt.Get(), source);
    workerContext->End(sourceCopyQuery.Get());
    workerContext->Flush();
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        context.slots[job.slot].sourceCopyQueryIssued = true;
    }
    const uint32_t completedBefore = context.manualPresenter->RenderCompleted();
    std::string presenterError;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    const uint32_t injectedPresent = testPresenterEarlyResult.exchange(0, std::memory_order_acq_rel);
    const lkg::subpixel::ManualPresentResult presentResult = injectedPresent == 1
        ? lkg::subpixel::ManualPresentResult::Failed
        : injectedPresent == 2 ? lkg::subpixel::ManualPresentResult::Occluded
        : context.manualPresenter->Present(context.canonicalQuilt.Get(), job.columns, job.rows,
            job.sourceEncoding == LKG_WORKER_SOURCE_ENCODING_SRGB, job.aspect, job.zoom, &presenterError);
    const bool throwAfterCopy = testThrowAfterSourceCopy.exchange(false, std::memory_order_acq_rel);
    if (throwAfterCopy) throw std::runtime_error("injected exception after raw source copy");
#else
    const lkg::subpixel::ManualPresentResult presentResult = context.manualPresenter->Present(
        context.canonicalQuilt.Get(), job.columns, job.rows,
        job.sourceEncoding == LKG_WORKER_SOURCE_ENCODING_SRGB, job.aspect, job.zoom, &presenterError);
#endif
    const ULONGLONG presentationStart = GetTickCount64();
    const uint64_t presentationTimeout = context.d3d12FenceTimeoutMs.load(std::memory_order_acquire);
    bool laterPresenterQueryProvesCopy = false;
    while (context.manualPresenter->HasPendingGpuWork()) {
        if (!context.manualPresenter->PollGpuCompletion(&presenterError)) {
            break;
        }
        if (!context.manualPresenter->HasPendingGpuWork()) break;
        if (GetTickCount64() - presentationStart > presentationTimeout) {
            presenterError = "manual GPU rendering timed out";
            break;
        }
        PumpWorkerMessages(context);
        Sleep(1);
    }
    laterPresenterQueryProvesCopy = !context.manualPresenter->HasPendingGpuWork() &&
        context.manualPresenter->RenderCompleted() != completedBefore;

    bool copyRetired = laterPresenterQueryProvesCopy;
    if (!copyRetired) {
        const ULONGLONG copyDeadline = GetTickCount64() + presentationTimeout;
        for (;;) {
            BOOL copyComplete = FALSE;
            const HRESULT queryResult = PollSourceCopyQuery(workerContext, sourceCopyQuery.Get(), &copyComplete);
            if (queryResult == S_OK && copyComplete) {
                copyRetired = true;
                break;
            }
            if (FAILED(queryResult)) {
                presenterError = "independent source copy retirement query failed";
                break;
            }
            if (GetTickCount64() >= copyDeadline) {
                presenterError = "independent source copy retirement query timed out";
                break;
            }
            PumpWorkerMessages(context);
            Sleep(1);
        }
    }
    *sourceRetirementProven = copyRetired;
    if (copyRetired) {
        std::lock_guard<std::mutex> lock(context.mutex);
        context.slots[job.slot].sourceRetirementUnproven = false;
        context.slots[job.slot].sourceCopyCompleted = true;
    }
    if (!copyRetired) {
        char message[384]{};
        std::snprintf(message, sizeof(message), "source retirement was not proven; resources quarantined: %.280s",
            presenterError.empty() ? "presentation did not provide a completion proof" : presenterError.c_str());
        SetFault(context, 42, message);
        return -1;
    }
    if (presentResult == lkg::subpixel::ManualPresentResult::Failed) {
        char message[384]{};
        std::snprintf(message, sizeof(message), "manual subpixel presentation failed after source retirement: %.320s",
            presenterError.c_str());
        SetFault(context, 40, message);
        return -1;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        context.subpixelStatus.renderCompleted = context.manualPresenter->RenderCompleted();
        context.subpixelStatus.presentAccepted = context.manualPresenter->PresentAccepted();
    }
    return 0;
}

static bool CloseRequested(WorkerContext& context) {
    std::lock_guard<std::mutex> lock(context.mutex);
    return context.closeRequested.load(std::memory_order_acquire);
}

#include "backends/d3d11_d3d12_interop.inc"
#include "backends/d3d12_backend.inc"
#include "backends/d3d11_backend.inc"
static void WorkerRenderEventImpl(int, WorkerDrawData* data) {
    if (!data || !data->context || !data->texture) return;
    WorkerContext& context = *data->context;
    auto abandon = [&]() {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.pendingTokens) --context.pendingTokens;
        ++context.status.dropped;
        context.condition.notify_all();
    };
    if (data->leaseGeneration != 0) {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (!context.previewLeaseEndpoint || !context.leaseActive || context.leaseGeneration != data->leaseGeneration ||
            context.leaseReturnRequested.load(std::memory_order_acquire)) {
            if (context.pendingTokens) --context.pendingTokens;
            ++context.status.dropped;
            context.condition.notify_all();
            return;
        }
    }
    ComPtr<IUnknown> texture(data->texture);
    if (context.producerD3D12) {
        WorkerRenderEventD3D12(context, *data, texture.Get());
        return;
    }
    ComPtr<ID3D11Texture2D> source;
    D3D11_TEXTURE2D_DESC sourceDesc{};
    if (texture.As(&source) != S_OK) {
        SetRenderFault(context, "source texture is not D3D11 texture2D");
        abandon();
        return;
    }
    source->GetDesc(&sourceDesc);
    TraceSourceDesc(sourceDesc);
    ComPtr<IUnknown> unityDeviceUnknown;
    unityDeviceUnknown.Attach(lkg_bridge_unity_device_addref());
    ComPtr<ID3D11Device> unityDevice;
    ComPtr<ID3D11Device> sourceDevice;
    source->GetDevice(&sourceDevice);
    if (!unityDeviceUnknown || unityDeviceUnknown.As(&unityDevice) != S_OK || !sourceDevice || sourceDevice.Get() != unityDevice.Get()) {
        SetRenderFault(context, "source texture device does not match Unity device");
        abandon();
        return;
    }
    if (!EnsureSharedSlots(context, unityDevice.Get(), sourceDesc)) {
        abandon();
        return;
    }
    uint32_t selectedSlot = 3;
    bool submitted = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.status.state != LKG_WORKER_READY || context.closeRequested.load(std::memory_order_acquire)) {
            selectedSlot = 3;
        } else {
            for (uint32_t index = 0; index < 3; ++index) {
                if (!context.slots[index].inUse && !context.slots[index].quarantined &&
                    !context.slots[index].sourceRetirementUnproven && !context.slots[index].sourceMutexHeld) {
                    selectedSlot = index;
                    context.slots[index].inUse = true;
                    break;
                }
            }
        }
    }
    if (selectedSlot == 3) {
        abandon();
        return;
    }
    renderExceptionContext = &context;
    renderExceptionSlot = selectedSlot;
    WorkerContext::Slot& slot = context.slots[selectedSlot];
    const HRESULT producerAcquire = slot.producerMutex ? slot.producerMutex->AcquireSync(0, 0) : E_FAIL;
    if (producerAcquire != WAIT_OBJECT_0) {
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            slot.inUse = false;
        }
        if (producerAcquire != WAIT_TIMEOUT) SetRenderFault(context, "producer keyed mutex acquire failed");
        abandon();
        return;
    }
    ComPtr<ID3D11DeviceContext> deviceContext;
    unityDevice->GetImmediateContext(&deviceContext);
    if (!deviceContext) {
        slot.producerMutex->ReleaseSync(0);
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            slot.inUse = false;
        }
        SetRenderFault(context, "Unity D3D11 immediate context unavailable");
        abandon();
        return;
    }
    deviceContext->CopyResource(slot.producerTexture.Get(), source.Get());
    const HRESULT producerRelease = slot.producerMutex->ReleaseSync(1);
    if (FAILED(producerRelease)) {
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            slot.inUse = false;
            slot.quarantined = true;
        }
        SetRenderFault(context, "producer keyed mutex release failed after transport copy");
        abandon();
        return;
    }
    bool rejectSubmission = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        rejectSubmission = context.closeRequested.load(std::memory_order_acquire) ||
            context.leaseReturnRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY;
        if (rejectSubmission) {
            slot.inUse = false;
        } else {
            if (!EnqueueJob(context, WorkerContext::Job{ selectedSlot, data->columns, data->rows, data->aspect,
                    data->zoom, data->sourceEncoding, data->sourceWasTypeless })) {
                slot.inUse = false;
            } else {
                ++context.status.submitted;
                submitted = true;
                context.condition.notify_one();
            }
        }
    }
    if (rejectSubmission) {
        if (slot.producerMutex->AcquireSync(1, 0) == WAIT_OBJECT_0) {
            slot.producerMutex->ReleaseSync(0);
        }
    }
    if (submitted) {
        TraceWorker("render-submit");
        return;
    }
    abandon();
}

static void WorkerRenderEvent(int eventId, void* value) {
    const uint64_t id = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(value));
    if (id == 0) return;
    std::unique_ptr<WorkerDrawData> owned;
    {
        std::lock_guard<std::mutex> lock(tokenMutex);
        const auto found = tokenRegistry.find(id);
        if (found == tokenRegistry.end()) return;
        owned = std::move(found->second);
        tokenRegistry.erase(found);
    }
    try {
        WorkerRenderEventImpl(eventId, owned.get());
        renderExceptionContext = nullptr;
        renderExceptionSlot = 3;
    } catch (...) {
        WorkerContext* context = owned->context;
        if (context) {
            try {
                SetFault(*context, 16, "render event exception");
                std::lock_guard<std::mutex> lock(context->mutex);
                if (renderExceptionContext == context && renderExceptionSlot < 3) {
                    context->slots[renderExceptionSlot].inUse = false;
                }
                if (context->pendingTokens) --context->pendingTokens;
                ++context->status.dropped;
                context->condition.notify_all();
            } catch (...) {
            }
        }
        renderExceptionContext = nullptr;
        renderExceptionSlot = 3;
    }
}

static bool SameAdapterLuid(const LUID& left, const LUID& right) {
    return left.LowPart == right.LowPart && left.HighPart == right.HighPart;
}

static bool FixedProfileStillCompatible(const WorkerContext& context) {
    try {
        uint32_t count = 0;
        if (lkg_bridge_get_display_count(&count) != 0 || context.displayOrdinal >= count) return false;
        LkgBridgeDisplayInfo current{};
        if (lkg_bridge_get_display(context.displayOrdinal, &current) != 0) return false;
        const LkgBridgeDisplayInfo& fixed = context.fixedDisplayProfile;
        return current.index == fixed.index && current.width == fixed.width && current.height == fixed.height &&
            current.quilt_width == fixed.quilt_width && current.quilt_height == fixed.quilt_height &&
            current.quilt_columns == fixed.quilt_columns && current.quilt_rows == fixed.quilt_rows &&
            std::isfinite(current.aspect) && std::isfinite(current.viewcone) &&
            std::fabs(current.aspect - fixed.aspect) < 0.0001f &&
            std::fabs(current.viewcone - fixed.viewcone) < 0.0001f;
    } catch (...) {
        return false;
    }
}

static void RunWorker(WorkerContext* context, WorkerHost* host) {
    lkg_bridge_worker_enter();
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    if (!context->metadataOnly && !CreateWorkerTestWindow(*context)) {
        SetFault(*context, 19, "worker test window creation failed");
    }
#endif
    ComPtr<IUnknown> producer;
    producer.Attach(context->producerDevice);
    context->producerDevice = nullptr;
    ComPtr<ID3D12Device> producer12;
    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIFactory4> factory4;
    ComPtr<ID3D11Device> workerDevice;
    ComPtr<ID3D11DeviceContext> workerDeviceContext;
    uint64_t session = 0;
    const char* runtimePath = context->runtimePath.c_str();

    auto cleanup = [&]() {
        const uint64_t cleanupSessionId = session;
        bool cleanupProven = true;
        {
            std::lock_guard<std::mutex> lock(context->mutex);
            context->cleanupProven = false;
        }
        if (producer && !context->retainedProducerDevice) {
            producer->AddRef();
            context->retainedProducerDevice = producer.Get();
        }
        if (workerDevice) context->retainedWorkerDevice = workerDevice;
        if (workerDeviceContext) context->retainedWorkerDeviceContext = workerDeviceContext;
        TraceWorkerShutdownEvent("cleanup-begin", cleanupSessionId);
        if (!DrainD3D12Ownership(*context, workerDevice.Get(), workerDeviceContext.Get(), 10000))
            cleanupProven = false;
        if (!DrainSourceRetirement(*context, workerDeviceContext.Get(), 10000)) cleanupProven = false;
        if (context->manualPresenter) {
            std::string presenterError;
            if (!context->manualPresenter->SetVisible(false, &presenterError)) cleanupProven = false;
            const ULONGLONG deadline = GetTickCount64() + 10000;
            while (context->manualPresenter->HasPendingGpuWork() && GetTickCount64() < deadline) {
                if (!context->manualPresenter->PollGpuCompletion(&presenterError)) {
                    cleanupProven = false;
                    break;
                }
                if (context->manualPresenter->HasPendingGpuWork()) Sleep(1);
            }
            if (context->manualPresenter->HasPendingGpuWork()) cleanupProven = false;
            if (cleanupProven && !context->manualPresenter->Close(&presenterError)) cleanupProven = false;
            if (cleanupProven) context->manualPresenter.reset();
            if (!cleanupProven) TraceWorkerShutdownEvent("manual-presenter-close-not-proven-retained", cleanupSessionId);
        }
        if (session != 0) {
            void* closeData = lkg_bridge_make_close_data(session);
            auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_bridge_render_event_func());
            if (closeData && callback) {
                callback(1, closeData);
                lkg_bridge_destroy_session(session);
                LkgBridgeSessionCloseStatusV1 closeStatus{};
                closeStatus.structSize = sizeof(closeStatus);
                closeStatus.version = 1;
                const int32_t queryResult = lkg_bridge_get_session_close_status_v1(session, &closeStatus);
                cleanupProven = cleanupProven && queryResult == 0 && closeStatus.state == LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE;
                if (cleanupProven) cleanupProven = lkg_bridge_ack_session_close_v1(session) == 0;
                TraceWorkerShutdownEvent(cleanupProven ? "session-close-safe-acked" : "session-close-not-proven-retained",
                    cleanupSessionId, static_cast<int>(closeStatus.state));
            } else {
                cleanupProven = false;
                TraceWorkerShutdownEvent("session-close-callback-unavailable-retained", cleanupSessionId);
            }
            session = 0;
        }
        if (lkg_bridge_sdk_shutdown_faulted() != 0) cleanupProven = false;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        if (cleanupProven && testThrowDuringWorkerCleanup.exchange(false, std::memory_order_acq_rel))
            throw std::runtime_error("injected worker cleanup exception");
#endif
        if (cleanupProven) {
            if (context->producerDevice) {
                context->producerDevice->Release();
                context->producerDevice = nullptr;
            }
            for (uint32_t slotIndex = 0; slotIndex < 3; ++slotIndex) {
                WorkerContext::Slot& slot = context->slots[slotIndex];
                if (!slot.d3d12Slot) continue;
                TraceWorkerShutdownEvent("clear-d3d12-slot-begin", cleanupSessionId, static_cast<int>(slotIndex));
                ClearD3D12Slot(slot);
                TraceWorkerShutdownEvent("clear-d3d12-slot-end", cleanupSessionId, static_cast<int>(slotIndex));
            }
            if (context->retainedProducerDevice) {
                context->retainedProducerDevice->Release();
                context->retainedProducerDevice = nullptr;
            }
            context->retainedWorkerDevice.Reset();
            context->retainedWorkerDeviceContext.Reset();
        } else {
            SetFault(*context, 22, "vendor cleanup not proven; resources retained");
        }
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        DestroyWorkerTestWindow(*context);
#endif
        WorkerShutdownSnapshot cleanupEndSnapshot{};
        {
            std::lock_guard<std::mutex> lock(context->mutex);
            context->cleanupProven = cleanupProven;
            if (context->status.state != LKG_WORKER_FAULT) context->status.state = LKG_WORKER_CLOSED;
            if (context->metadataOnly) {
                if (context->status.state == LKG_WORKER_FAULT && context->displaySnapshotGeneration < context->displayRequestedGeneration &&
                    context->displaySnapshotError.empty()) {
                    context->displayRefreshPending = false;
                    context->displaySnapshotGeneration = context->displayRequestedGeneration;
                    context->displaySnapshotResult = -7;
                    context->displaySnapshotError = context->status.error[0] ? context->status.error : "Bridge metadata worker failed before publishing a snapshot.";
                }
            if (!cleanupProven && context->displaySnapshotError.empty()) {
                    context->displaySnapshotResult = -6;
                    context->displaySnapshotError = "Bridge metadata SDK cleanup was not proven.";
                }
                context->displayCleanupFinished = true;
            }
            if (NativeTraceEnabled())
                cleanupEndSnapshot = CaptureWorkerShutdownSnapshot(*context, cleanupSessionId);
        }
        FinishPreviewMetadataSnapshots(*context);
        TraceWorkerShutdownSnapshot("cleanup-end", cleanupEndSnapshot);
    };
    auto finish = [&]() noexcept {
        try {
            cleanup();
        } catch (...) {
            try {
                {
                    std::lock_guard<std::mutex> lock(context->mutex);
                    context->cleanupProven = false;
                }
                SetFault(*context, 15, "worker cleanup exception");
            } catch (...) {
            }
        }
        lkg_bridge_worker_leave();
        if (!context->hosted) {
            context->finished.store(true, std::memory_order_release);
            lkg_bridge_worker_release_claim();
        }
    };

    if (context->closeRequested.load(std::memory_order_acquire)) {
        SetFault(*context, 18, "worker host stopping before start");
        finish();
        return;
    }

    try {
    const int32_t beginResult = lkg_bridge_worker_host_begin_sdk(runtimePath, context->applicationName.c_str());
    if (beginResult != 0) {
        context->sdkHostFatalFailure = beginResult != -20 && beginResult != -22;
        SetBridgeFault(*context, beginResult == -20 ? 23 : 3, "worker_host_sdk_begin", beginResult);
        finish();
        return;
    }
    if (context->metadataOnly) {
        for (;;) {
            uint64_t generation = 0;
            {
                std::unique_lock<std::mutex> lock(context->mutex);
                context->condition.wait(lock, [&] {
                    return context->displayRefreshPending || context->closeRequested.load(std::memory_order_acquire);
                });
                if (context->closeRequested.load(std::memory_order_acquire)) {
                    context->displayRefreshPending = false;
                    if (context->status.state != LKG_WORKER_FAULT) context->status.state = LKG_WORKER_CLOSING;
                    break;
                }
                generation = context->displayRequestedGeneration;
                context->displayRefreshPending = false;
            }
            RefreshDisplaySnapshot(*context, generation);
        }
        finish();
        return;
    }
    if (producer.As(&producer12) == S_OK) {
        const LUID luid = producer12->GetAdapterLuid();
        if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory4))) ||
            FAILED(factory4->EnumAdapterByLuid(luid, IID_PPV_ARGS(&adapter)))) {
            SetFault(*context, 4, "producer D3D12 adapter unavailable");
            finish();
            return;
        }
    } else if (producer.As(&dxgiDevice) != S_OK || dxgiDevice->GetAdapter(&adapter) != S_OK) {
        SetFault(*context, 4, "producer D3D11 adapter unavailable");
        finish();
        return;
    }
    DXGI_ADAPTER_DESC adapterDesc{};
    if (FAILED(adapter->GetDesc(&adapterDesc))) {
        SetFault(*context, 5, "producer adapter identity unavailable; restart required");
        finish();
        return;
    }
    if (host->workerDeviceFaulted) {
        SetFault(*context, 5, "persistent worker D3D11 device is unavailable; restart required");
        finish();
        return;
    }
    if (host->workerDeviceInitialized) {
        bool adapterMatches = SameAdapterLuid(adapterDesc.AdapterLuid, host->workerAdapterLuid);
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        if (host->testForceAdapterMismatch.exchange(false, std::memory_order_acq_rel)) adapterMatches = false;
#endif
        if (!host->workerDevice || !host->workerDeviceContext || !adapterMatches) {
            char adapterError[192]{};
            std::snprintf(adapterError, sizeof(adapterError),
                "producer adapter %08lX:%08lX differs from persistent worker D3D11 adapter %08lX:%08lX; restart required",
                static_cast<unsigned long>(adapterDesc.AdapterLuid.HighPart),
                static_cast<unsigned long>(adapterDesc.AdapterLuid.LowPart),
                static_cast<unsigned long>(host->workerAdapterLuid.HighPart),
                static_cast<unsigned long>(host->workerAdapterLuid.LowPart));
            SetFault(*context, 5, adapterError);
            finish();
            return;
        }
        const HRESULT removedReason = host->workerDevice->GetDeviceRemovedReason();
        if (FAILED(removedReason)) {
            host->workerDeviceFaulted = true;
            SetFault(*context, 5, "persistent worker D3D11 device was removed; restart required");
            finish();
            return;
        }
        workerDevice = host->workerDevice;
        workerDeviceContext = host->workerDeviceContext;
    } else {
        D3D_FEATURE_LEVEL selected{};
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        if (D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                levels, 2, D3D11_SDK_VERSION, &workerDevice, &selected, nullptr) != S_OK) {
            SetFault(*context, 5, "worker D3D11 device creation failed; restart if the device was removed");
            finish();
            return;
        }
        workerDevice->GetImmediateContext(&workerDeviceContext);
        if (!workerDeviceContext) {
            SetFault(*context, 5, "worker D3D11 immediate context unavailable");
            finish();
            return;
        }
        const HRESULT removedReason = workerDevice->GetDeviceRemovedReason();
        if (FAILED(removedReason)) {
            workerDevice.Reset();
            workerDeviceContext.Reset();
            host->workerDeviceFaulted = true;
            SetFault(*context, 5, "new worker D3D11 device was removed during initialization; restart required");
            finish();
            return;
        }
        host->workerDevice = workerDevice;
        host->workerDeviceContext = workerDeviceContext;
        host->workerAdapterLuid = adapterDesc.AdapterLuid;
        host->workerFeatureLevel = selected;
        host->workerDeviceInitialized = true;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        host->testWorkerDeviceCreateCount.fetch_add(1, std::memory_order_acq_rel);
        host->testWorkerDeviceIdentity.store(reinterpret_cast<uintptr_t>(host->workerDevice.Get()), std::memory_order_release);
#endif
    }
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    context->testWorkerDeviceIdentity.store(reinterpret_cast<uintptr_t>(workerDevice.Get()), std::memory_order_release);
#endif
    {
        uint32_t count = 0;
        LkgBridgeDisplayInfo display{};
        const int32_t countResult = lkg_bridge_get_display_count(&count);
        if (NativeTraceEnabled()) TraceDisplayCount(countResult, count, context->displayOrdinal);
        const int32_t displayResult = countResult == 0 && context->displayOrdinal < count ? lkg_bridge_get_display(context->displayOrdinal, &display) : countResult;
        if (displayResult != 0 || context->displayOrdinal >= count) {
            SetBridgeFault(*context, 6, "bridge_display_profile", displayResult != 0 ? displayResult : -4);
            finish();
            return;
        }
        LkgBridgePhysicalDisplayInfoV1 physical{};
        physical.structSize = sizeof(physical);
        physical.version = 1;
        const int32_t physicalResult = lkg_bridge_get_physical_display_v1(display.index, &physical);
        if (physicalResult == 0 && physical.valid && physical.pixelWidth > 0 && physical.pixelHeight > 0 && std::isfinite(physical.dpi) && physical.dpi > 0.0f) {
            std::lock_guard<std::mutex> lock(context->mutex);
            context->physicalDisplay.pixelWidth = physical.pixelWidth;
            context->physicalDisplay.pixelHeight = physical.pixelHeight;
            context->physicalDisplay.dpi = physical.dpi;
            context->physicalDisplay.valid = 1;
        }
        LkgWorkerSubpixelCalibrationV1 calibration{};
        calibration.structSize = sizeof(calibration);
        calibration.version = 1;
        const int32_t calibrationResult = lkg_bridge_get_worker_subpixel_calibration_v1(display.index, &calibration);
        if (calibrationResult != 0) {
            char message[192]{};
            std::snprintf(message, sizeof(message), "Subpixel calibration unavailable rc=%d", calibrationResult);
            SetFault(*context, 35, message);
            finish();
            return;
        }
        if (display.width != calibration.nativeWidth || display.height != calibration.nativeHeight) {
            SetFault(*context, 34, "SDK display dimensions differ from subpixel calibration dimensions");
            finish();
            return;
        }
        const uint32_t requestedMode = context->outputOptions.subpixelMode;
        const bool requireCells = requestedMode == LKG_WORKER_SUBPIXEL_CALIBRATED_CELLS ||
            (requestedMode == LKG_WORKER_SUBPIXEL_AUTOMATIC && calibration.cellCount > 0);
        if (requireCells) {
            std::string metadataError;
            if (!lkg::subpixel::ReadBridgeCellTable(calibration, context->subpixelCells, metadataError)) {
                char message[384]{};
                std::snprintf(message, sizeof(message), "%s metadata unavailable: %.300s",
                    requestedMode == LKG_WORKER_SUBPIXEL_CALIBRATED_CELLS ? "Calibrated Cells" : "Automatic cell calibration",
                    metadataError.c_str());
                SetFault(*context, 36, message);
                finish();
                return;
            }
        }
        if (const char* validationError = ValidateNativeSubpixelCalibration(calibration, requestedMode,
                requireCells ? context->subpixelCells : nullptr)) {
            SetFault(*context, 33, validationError);
            finish();
            return;
        }
        auto presenter = std::unique_ptr<lkg::subpixel::ManualSubpixelPresenter>(
            new (std::nothrow) lkg::subpixel::ManualSubpixelPresenter());
        if (!presenter) {
            SetFault(*context, 37, "native subpixel presenter allocation failed");
            finish();
            return;
        }
        std::string presenterError;
#if defined(LKG_WORKER_TRANSPORT_TESTING) && defined(LKG_SUBPIXEL_PRESENTER_TESTS)
        const bool presenterCreated = presenter->CreateOffscreenForTest(workerDevice.Get(), calibration,
            context->subpixelCells, requestedMode, context->outputOptions.linearColorSpace != 0,
            display.aspect, &presenterError);
#else
        const bool presenterCreated = presenter->Create(workerDevice.Get(), calibration, context->subpixelCells,
            requestedMode, context->outputOptions.linearColorSpace != 0, display.aspect, &presenterError);
#endif
        if (!presenterCreated) {
            char message[384]{};
            std::snprintf(message, sizeof(message), "native subpixel presenter unavailable: %.320s",
                presenterError.c_str());
            SetFault(*context, 38, message);
            context->manualPresenter = std::move(presenter);
            finish();
            return;
        }
        context->subpixelCalibration = calibration;
        context->manualPresenter = std::move(presenter);
        context->fixedDisplayIndex = display.index;
        context->fixedDisplayProfile = display;
        bool readyPublished = false;
        {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->status.displayIndex = display.index;
        context->status.width = display.width;
        context->status.height = display.height;
        context->status.aspect = display.aspect;
        context->status.viewcone = display.viewcone;
        context->status.quiltWidth = display.quilt_width;
        context->status.quiltHeight = display.quilt_height;
        context->status.quiltColumns = display.quilt_columns;
        context->status.quiltRows = display.quilt_rows;
        context->status.state = context->closeRequested.load(std::memory_order_acquire) ? LKG_WORKER_CLOSING : LKG_WORKER_READY;
        context->subpixelStatus.structSize = sizeof(context->subpixelStatus);
        context->subpixelStatus.version = 1;
        context->subpixelStatus.valid = 1u;
        context->subpixelStatus.requestedMode = context->outputOptions.subpixelMode;
        context->subpixelStatus.effectiveMode = context->outputOptions.subpixelMode;
        context->subpixelStatus.cellPatternMode = calibration.cellPatternMode < 0 ? UINT32_MAX :
            static_cast<uint32_t>(calibration.cellPatternMode);
        context->subpixelStatus.cellCount = calibration.cellCount < 0 ? 0u : static_cast<uint32_t>(calibration.cellCount);
        context->subpixelStatus.nativeWidth = calibration.nativeWidth;
        context->subpixelStatus.nativeHeight = calibration.nativeHeight;
        context->subpixelStatus.backend = LKG_WORKER_SUBPIXEL_BACKEND_NATIVE_D3D11;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        context->testPresenterVisible = true;
#endif
        readyPublished = context->status.state == LKG_WORKER_READY;
        }
        if (readyPublished) TraceWorker("ready");
    }
    while (true) {
        PumpWorkerMessages(*context);
        if (!context->closeRequested.load(std::memory_order_acquire) && context->manualPresenter) {
            std::string windowError;
            bool windowStateValid = context->manualPresenter->PollWindowState(&windowError);
#if defined(LKG_WORKER_TRANSPORT_TESTING)
            if (context->testFailWindowPoll.load(std::memory_order_acquire)) windowStateValid = false;
#endif
            if (!windowStateValid) {
                SetFault(*context, 44, "native output window state failed");
            }
        }
#if defined(LKG_WORKER_TRANSPORT_TESTING) && defined(LKG_SUBPIXEL_PRESENTER_TESTS)
        bool testReadbackRequested = false;
        {
            std::lock_guard<std::mutex> lock(context->mutex);
            if (context->testReadbackRequested) {
                context->testReadbackRequested = false;
                testReadbackRequested = true;
            }
        }
        if (testReadbackRequested) {
            std::vector<uint8_t> pixels;
            uint32_t width = 0;
            uint32_t height = 0;
            std::string readbackError;
            const bool readbackSucceeded = context->manualPresenter &&
                context->manualPresenter->ReadbackLastPresentedRGBA8(pixels, &width, &height, &readbackError);
            {
                std::lock_guard<std::mutex> lock(context->mutex);
                context->testReadbackPixels = std::move(pixels);
                context->testReadbackWidth = width;
                context->testReadbackHeight = height;
                context->testReadbackError = std::move(readbackError);
                context->testReadbackResult = readbackSucceeded ? 0 : -1;
                context->testReadbackComplete = true;
                context->condition.notify_all();
            }
        }
#endif
        WorkerContext::Job job{};
        uint64_t displayRefreshGeneration = 0;
        uint32_t visibilityRequest = UINT32_MAX;
        bool returnLeaseNow = false;
        {
            std::unique_lock<std::mutex> lock(context->mutex);
            context->condition.wait_for(lock, std::chrono::milliseconds(10), [&] {
                return context->jobCount != 0 || context->displayRefreshPending ||
                    (context->previewPausePhase == LKG_WORKER_PREVIEW_PAUSED && !context->previewPauseDesired) ||
#if defined(LKG_WORKER_TRANSPORT_TESTING) && defined(LKG_SUBPIXEL_PRESENTER_TESTS)
                    context->testReadbackRequested ||
#endif
                    context->leaseReturnRequested.load(std::memory_order_acquire) ||
                    context->leaseAcquirePending || context->leaseFinalCloseRequested.load(std::memory_order_acquire) ||
                    (context->closeRequested.load(std::memory_order_acquire) && context->pendingTokens == 0);
            });
            const bool closing = context->closeRequested.load(std::memory_order_acquire) ||
                context->leaseFinalCloseRequested.load(std::memory_order_acquire);
            if (closing) {
                context->displayRefreshPending = false;
                context->previewPauseDesired = false;
            }
            if (context->jobCount == 0 && closing && context->pendingTokens == 0) break;
            if (context->displayRefreshPending) {
                displayRefreshGeneration = context->displayRequestedGeneration;
                context->displayRefreshPending = false;
            }
            if (displayRefreshGeneration != 0) {
                lock.unlock();
                RefreshDisplaySnapshot(*context, displayRefreshGeneration);
                continue;
            }
            const bool returnRequested = context->previewLeaseEndpoint &&
                context->leaseReturnRequested.load(std::memory_order_acquire) && !closing;
            if (returnRequested) {
                bool slotsDrained = !context->slotsInitializing && context->pendingTokens == 0 &&
                    context->jobCount == 0 && !context->inFlightJob;
                for (const WorkerContext::Slot& slot : context->slots)
                    slotsDrained = slotsDrained && SlotGpuOwnershipRetired(slot);
                if (slotsDrained) {
                    visibilityRequest = 0;
                    returnLeaseNow = true;
                }
            }
            if (!closing && visibilityRequest == UINT32_MAX) {
                if (context->previewPausePhase == LKG_WORKER_PREVIEW_PAUSED) {
                    if (context->previewPauseDesired) {
                        lock.unlock();
                        continue;
                    }
                    context->previewPausePhase = LKG_WORKER_PREVIEW_RESUMING;
                    visibilityRequest = 1;
                } else if (context->previewPausePhase == LKG_WORKER_PREVIEW_RESUMING) {
                    visibilityRequest = 1;
                } else if (context->previewPauseDesired) {
                    if (context->previewPausePhase == LKG_WORKER_PREVIEW_RUNNING)
                        context->previewPausePhase = LKG_WORKER_PREVIEW_PAUSING;
                    bool slotsDrained = !context->slotsInitializing && context->pendingTokens == 0 &&
                        context->jobCount == 0 && !context->inFlightJob;
                    for (const WorkerContext::Slot& slot : context->slots)
                        slotsDrained = slotsDrained && SlotGpuOwnershipRetired(slot);
                    if (context->previewPausePhase == LKG_WORKER_PREVIEW_PAUSING && slotsDrained)
                        visibilityRequest = 0;
                } else if (context->previewPausePhase == LKG_WORKER_PREVIEW_PAUSING) {
                    context->previewPausePhase = LKG_WORKER_PREVIEW_RUNNING;
                }
            }
            if (visibilityRequest != UINT32_MAX) {
                const bool retainPersistentVisibility = returnLeaseNow && context->previewLeaseEndpoint && visibilityRequest == 0;
                const uint64_t visibilityEndpointId = context->leaseEndpointId;
                const uint64_t visibilityGeneration = context->leaseGeneration;
                const uint64_t visibilitySessionId = session;
                const char* retainedOperation = returnLeaseNow ? "lease-return" : "pause";
                lock.unlock();
                int32_t visibleResult = -1;
                bool visibilityFailed = false;
                if (retainPersistentVisibility) {
                    TracePersistentVisibilityRetained(visibilityEndpointId, visibilityGeneration,
                        visibilitySessionId, retainedOperation);
                    std::lock_guard<std::mutex> stateLock(context->mutex);
                    context->previewPausePhase = context->previewPauseDesired &&
                        !context->closeRequested.load(std::memory_order_acquire)
                        ? LKG_WORKER_PREVIEW_PAUSED : LKG_WORKER_PREVIEW_RESUMING;
                    context->condition.notify_all();
                } else {
                    std::string visibilityError;
                    visibleResult = context->manualPresenter &&
                        context->manualPresenter->SetVisible(visibilityRequest != 0, &visibilityError) ? 0 : -1;
                    std::lock_guard<std::mutex> stateLock(context->mutex);
#if defined(LKG_WORKER_TRANSPORT_TESTING)
                    if (visibleResult == 0) context->testPresenterVisible = visibilityRequest != 0;
#endif
                    if (visibleResult != 0) {
                        context->previewPausePhase = LKG_WORKER_PREVIEW_FAULTED;
                        visibilityFailed = true;
                    } else if (visibilityRequest == 0) {
                        context->previewPausePhase = context->previewPauseDesired &&
                            !context->closeRequested.load(std::memory_order_acquire)
                            ? LKG_WORKER_PREVIEW_PAUSED : LKG_WORKER_PREVIEW_RESUMING;
                    } else {
                        context->previewPausePhase = context->previewPauseDesired &&
                            !context->closeRequested.load(std::memory_order_acquire)
                            ? LKG_WORKER_PREVIEW_PAUSING : LKG_WORKER_PREVIEW_RUNNING;
                    }
                    context->condition.notify_all();
                }
                if (visibilityFailed)
                    SetFault(*context, 23, visibilityRequest ? "Native subpixel preview resume failed" : "Native subpixel preview pause failed");
                else if (returnLeaseNow) {
                    for (uint32_t slotIndex = 0; slotIndex < 3; ++slotIndex) {
                        WorkerContext::Slot& slot = context->slots[slotIndex];
                        if (slot.d3d12Slot) {
                            ClearD3D12Slot(slot);
                        } else {
                            slot.workerMutex.Reset();
                            slot.workerTexture.Reset();
                            slot.producerMutex.Reset();
                            slot.producerTexture.Reset();
                            slot.sharedHandle = nullptr;
                        }
                        slot.inUse = false;
                        slot.consumerCopyPending = false;
                        slot.consumerTimedOut = false;
                        slot.quarantined = false;
                        slot.unknownSubmission = false;
                    }
                    context->textureWidth = 0;
                    context->textureHeight = 0;
                    context->textureFormat = DXGI_FORMAT_UNKNOWN;
                    producer.Reset();
                    producer12.Reset();
                    dxgiDevice.Reset();
                    adapter.Reset();
                    factory4.Reset();

                    void* returnedToken = nullptr;
                    uint64_t returnedGeneration = 0;
                    {
                        std::lock_guard<std::mutex> stateLock(context->mutex);
                        returnedToken = context->currentLeaseToken;
                        returnedGeneration = context->leaseGeneration;
                        context->producerDevice = nullptr;
                        context->leaseActive = false;
                        context->leaseReturnProven = true;
                        context->leaseAcquirePending = false;
                        context->previewPausePhase = LKG_WORKER_PREVIEW_PAUSED;
                        context->status.state = LKG_WORKER_CLOSED;
                        context->condition.notify_all();
                    }
                    {
                        std::lock_guard<std::mutex> registryLock(registryMutex);
                        PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(returnedToken);
                        if (token && token->endpointId == context->leaseEndpointId && token->generation == returnedGeneration &&
                            !token->acknowledged) {
                            token->state = LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN;
                            token->lastResult = LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
                            std::lock_guard<std::mutex> contextLock(context->mutex);
                            token->workerStatus = context->status;
                        }
                    }
                    TraceWorker("lease-return-proven");
                    IUnknown* nextProducer = nullptr;
                    bool finalClose = false;
                    for (;;) {
                        uint64_t parkedRefreshGeneration = 0;
                        {
                            std::unique_lock<std::mutex> stateLock(context->mutex);
                            context->condition.wait_for(stateLock, std::chrono::milliseconds(10), [&] {
                                return context->displayRefreshPending || context->leaseAcquirePending ||
                                    context->leaseFinalCloseRequested.load(std::memory_order_acquire) ||
                                    context->closeRequested.load(std::memory_order_acquire);
                            });
                            finalClose = context->leaseFinalCloseRequested.load(std::memory_order_acquire) ||
                                context->closeRequested.load(std::memory_order_acquire);
                            if (!finalClose && context->displayRefreshPending) {
                                parkedRefreshGeneration = context->displayRequestedGeneration;
                                context->displayRefreshPending = false;
                            } else if (!finalClose && context->leaseAcquirePending) {
                                nextProducer = context->producerDevice;
                                context->producerDevice = nullptr;
                                context->leaseAcquirePending = false;
                                break;
                            }
                        }
                        if (!finalClose && context->manualPresenter) {
                            std::string windowError;
                            bool windowStateValid = context->manualPresenter->PollWindowState(&windowError);
#if defined(LKG_WORKER_TRANSPORT_TESTING)
                            if (context->testFailWindowPoll.load(std::memory_order_acquire)) windowStateValid = false;
#endif
                            if (!windowStateValid) {
                                SetFault(*context, 43, "native output window state failed while preview lease was parked");
                                finalClose = true;
                            }
                        }
                        if (finalClose) break;
                        if (parkedRefreshGeneration != 0) {
                            RefreshDisplaySnapshot(*context, parkedRefreshGeneration);
                            continue;
                        }
                    }
                    if (finalClose) break;
                    if (!nextProducer) {
                        SetFault(*context, 25, "preview lease resumed without a producer device");
                        continue;
                    }
                    producer.Attach(nextProducer);
                    if (!FixedProfileStillCompatible(*context)) {
                        SetFault(*context, 35, "Bridge profile changed while preview was returned; restart required");
                        continue;
                    }
                    std::string visibilityError;
                    const int32_t showResult = context->manualPresenter &&
                        context->manualPresenter->SetVisible(true, &visibilityError) ? 0 : -1;
                    if (showResult != 0) {
                        SetFault(*context, 26, "native subpixel output could not resume its presenter window");
                        continue;
                    }
                    {
                        std::lock_guard<std::mutex> stateLock(context->mutex);
#if defined(LKG_WORKER_TRANSPORT_TESTING)
                        context->testPresenterVisible = true;
#endif
                        context->leaseReturnRequested.store(false, std::memory_order_release);
                        context->leaseReturnProven = false;
                        context->previewPausePhase = LKG_WORKER_PREVIEW_RUNNING;
                        context->previewPauseDesired = false;
                        context->status.state = LKG_WORKER_READY;
                        context->condition.notify_all();
                    }
                    TraceWorker("lease-resumed");
                }
                continue;
            }
            if (!PopJob(*context, job)) continue;
            context->inFlightJob = true;
            context->inFlightSlot = job.slot;
        }
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        if (testThrowAfterWorkerDequeue.exchange(false, std::memory_order_acq_rel))
            throw std::runtime_error("injected worker exception after dequeue");
#endif
        WorkerContext::Slot* slot = &context->slots[job.slot];
        if (slot->d3d12Slot) {
            ProcessD3D12WorkerJob(*context, job, workerDevice.Get(), workerDeviceContext.Get(), session);
            // D3D12 slots must retire only through their producer-fence and
            // D3D11 consumer-query proof path. Device removal alone cannot
            // retire the producer's outstanding GPU work.
            if (FAILED(workerDevice->GetDeviceRemovedReason())) host->workerDeviceFaulted = true;
            continue;
        }
        const HRESULT removedReason = workerDevice->GetDeviceRemovedReason();
        if (FAILED(removedReason)) {
            host->workerDeviceFaulted = true;
            SetFault(*context, 5, "persistent worker D3D11 device was removed during rendering; restart required");
            std::lock_guard<std::mutex> lock(context->mutex);
            slot->inUse = false;
            if (context->pendingTokens) --context->pendingTokens;
            ++context->status.dropped;
            context->inFlightJob = false;
            context->condition.notify_all();
            continue;
        }
        if (!slot->workerTexture) {
            if (FAILED(workerDevice->OpenSharedResource(slot->sharedHandle, IID_PPV_ARGS(&slot->workerTexture))) || FAILED(slot->workerTexture.As(&slot->workerMutex))) {
                SetFault(*context, 12, "worker shared texture open failed");
                std::lock_guard<std::mutex> lock(context->mutex);
                slot->inUse = false;
                if (context->pendingTokens) --context->pendingTokens;
                context->inFlightJob = false;
                context->condition.notify_all();
                continue;
            }
        }
        const HRESULT workerAcquire = slot->workerMutex->AcquireSync(1, 0);
        if (workerAcquire != WAIT_OBJECT_0) {
            if (workerAcquire != WAIT_TIMEOUT) {
                SetFault(*context, 14, "worker keyed mutex acquire failed");
            }
            bool retry = false;
            {
                std::lock_guard<std::mutex> lock(context->mutex);
                if (workerAcquire == WAIT_TIMEOUT && !context->closeRequested.load(std::memory_order_acquire)) {
                    retry = RequeueFront(*context, job);
                    if (retry) context->inFlightJob = false;
                    context->condition.notify_one();
                }
            }
            if (retry) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            } else {
                std::lock_guard<std::mutex> lock(context->mutex);
                slot->inUse = false;
                if (context->pendingTokens) --context->pendingTokens;
                ++context->status.dropped;
                context->inFlightJob = false;
                context->condition.notify_all();
            }
            continue;
        }
        {
            std::lock_guard<std::mutex> lock(context->mutex);
            slot->sourceMutexHeld = true;
        }
        int32_t drawResult = -1;
        bool sourceRetirementProven = true;
        try {
            drawResult = DrawWorkerImage(*context, workerDeviceContext.Get(), workerDevice.Get(), session,
                slot->workerTexture.Get(), job, slot->sourceCopyRetirementQuery11, &sourceRetirementProven);
        } catch (...) {
            sourceRetirementProven = !slot->sourceRetirementUnproven;
            SetFault(*context, 45, "worker threw while rendering; source retirement is being verified");
        }
        if (sourceRetirementProven) {
            const HRESULT sourceRelease = slot->workerMutex->ReleaseSync(0);
            if (SUCCEEDED(sourceRelease)) {
                std::lock_guard<std::mutex> lock(context->mutex);
                slot->sourceMutexHeld = false;
            }
            else {
                {
                    std::lock_guard<std::mutex> lock(context->mutex);
                    slot->quarantined = true;
                }
                sourceRetirementProven = false;
                SetFault(*context, 46, "worker source keyed mutex release failed");
            }
        } else {
            std::lock_guard<std::mutex> lock(context->mutex);
            slot->quarantined = true;
        }
        if (drawResult != 0) {
            SetBridgeFault(*context, 13, "bridge_draw", drawResult);
        }
        bool completedDraw = false;
        {
            std::lock_guard<std::mutex> lock(context->mutex);
            slot->inUse = false;
            if (context->pendingTokens) --context->pendingTokens;
            context->inFlightJob = false;
            if (drawResult == 0) ++context->status.completed;
            else ++context->status.dropped;
            completedDraw = drawResult == 0;
            context->condition.notify_all();
        }
        TraceWorker(completedDraw ? "draw-complete" : "draw-drop");
    }
    WorkerShutdownSnapshot loopExitSnapshot{};
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        if (context->status.state != LKG_WORKER_FAULT) context->status.state = LKG_WORKER_CLOSING;
        if (NativeTraceEnabled())
            loopExitSnapshot = CaptureWorkerShutdownSnapshot(*context, session);
    }
    TraceWorkerShutdownSnapshot("worker-loop-exit", loopExitSnapshot);
    finish();
    } catch (...) {
        SetFault(*context, 8, "worker thread exception");
        std::unique_lock<std::mutex> lock(context->mutex);
        if (context->inFlightJob) {
            WorkerContext::Slot& slot = context->slots[context->inFlightSlot];
            if (slot.sourceRetirementUnproven || slot.sourceMutexHeld || slot.d3d12Slot) slot.quarantined = true;
            slot.inUse = false;
            context->inFlightJob = false;
            if (context->pendingTokens) --context->pendingTokens;
            ++context->status.dropped;
        }
        while (context->jobCount != 0) {
            WorkerContext::Job droppedJob{};
            PopJob(*context, droppedJob);
            WorkerContext::Slot& slot = context->slots[droppedJob.slot];
            if (slot.d3d12Slot || slot.sourceRetirementUnproven || slot.sourceMutexHeld) slot.quarantined = true;
            slot.inUse = false;
            if (context->pendingTokens) --context->pendingTokens;
            ++context->status.dropped;
        }
        for (WorkerContext::Slot& slot : context->slots) {
            if (slot.d3d12Slot || slot.sourceRetirementUnproven || slot.sourceMutexHeld) slot.quarantined = true;
            slot.inUse = false;
        }
        context->condition.notify_all();
        context->condition.wait(lock, [&] { return context->pendingTokens == 0 && context->jobCount == 0; });
        lock.unlock();
        finish();
    }
}

LKG_EXPORT void* lkg_worker_begin(const char* runtimePath, const char* applicationName, uint32_t displayOrdinal, void* producerDevice) {
    const LkgWorkerOutputOptionsV1 options{ sizeof(LkgWorkerOutputOptionsV1), 1, LKG_WORKER_SUBPIXEL_AUTOMATIC, 0 };
    return lkg_worker_begin_with_output_v1(runtimePath, applicationName, displayOrdinal, producerDevice, &options);
}

LKG_EXPORT void* lkg_worker_begin_with_output_v1(const char* runtimePath, const char* applicationName,
    uint32_t displayOrdinal, void* producerDevice, const LkgWorkerOutputOptionsV1* options) {
    if (!runtimePath || !runtimePath[0] || !applicationName || !applicationName[0] || !ValidWorkerOutputOptions(options)) return nullptr;
    if (!lkg_bridge_worker_claim()) return nullptr;
    WorkerContext* context = nullptr;
    try {
        context = new WorkerContext();
        context->runtimePath = runtimePath;
        for (char& character : context->runtimePath) if (character == '/') character = '\\';
        context->applicationName = applicationName;
        context->displayOrdinal = displayOrdinal;
        context->outputOptions = *options;
        context->subpixelStatus.requestedMode = options->subpixelMode;
        context->producerDevice = reinterpret_cast<IUnknown*>(producerDevice);
        if (context->producerDevice) {
            context->producerDevice->AddRef();
            context->deviceGeneration = lkg_bridge_unity_device_generation();
        } else {
            uint32_t graphicsApi = 0;
            if (lkg_bridge_unity_device_snapshot(&context->producerDevice, &context->deviceGeneration, &graphicsApi) != 0 || graphicsApi == 0)
                context->producerDevice = nullptr;
        }
        if (!context->producerDevice) {
            delete context;
            lkg_bridge_worker_release_claim();
            return nullptr;
        }
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(&lkg_worker_begin), &context->moduleReference)) {
            throw std::runtime_error("worker module pin failed");
        }
        ComPtr<ID3D12Device> producer12;
        context->producerD3D12 = context->producerDevice->QueryInterface(IID_PPV_ARGS(&producer12)) == S_OK;
        if (!context->producerD3D12) context->deviceGeneration = 0;
        context->status.state = LKG_WORKER_CONNECTING;
        {
            std::lock_guard<std::mutex> lock(registryMutex);
            if (finalShutdownRequested) throw std::runtime_error("final worker shutdown already requested");
            registry.insert(context);
        }
        if (!SubmitToPersistentHost(context)) {
            std::lock_guard<std::mutex> lock(registryMutex);
            registry.erase(context);
            throw std::runtime_error("persistent worker host unavailable");
        }
    } catch (...) {
        if (context && context->producerDevice) {
            context->producerDevice->Release();
            context->producerDevice = nullptr;
        }
        if (context && context->moduleReference) {
            FreeLibrary(context->moduleReference);
            context->moduleReference = nullptr;
        }
        delete context;
        lkg_bridge_worker_release_claim();
        return nullptr;
    }
    return context;
}

LKG_EXPORT void* lkg_worker_begin_metadata_v1(const char* runtimePath, const char* applicationName) {
    if (!runtimePath || !runtimePath[0] || !applicationName || !applicationName[0]) return nullptr;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        if (previewLeaseEndpoint) {
            WorkerContext* endpoint = previewLeaseEndpoint;
            std::lock_guard<std::mutex> contextLock(endpoint->mutex);
            if (finalShutdownRequested || endpoint->leaseFinalCloseRequested.load(std::memory_order_acquire) ||
                CanonicalWorkerString(endpoint->runtimePath.c_str(), true) != CanonicalWorkerString(runtimePath, true) ||
                endpoint->applicationName != applicationName ||
                endpoint->status.state == LKG_WORKER_FAULT)
                return nullptr;
            std::unique_ptr<PreviewMetadataTokenRecord> token(new (std::nothrow) PreviewMetadataTokenRecord());
            if (!token) return nullptr;
            token->context = endpoint;
            token->endpointId = endpoint->leaseEndpointId;
            token->displayOrdinal = endpoint->displayOrdinal;
            void* handle = token.get();
            PreviewMetadataTokenRecord* proxy = token.get();
            try {
                if (!previewMetadataTokenRegistry.emplace(handle, std::move(token)).second) return nullptr;
            } catch (...) {
                return nullptr;
            }
            uint64_t requestedGeneration = 0;
            if (!proxy || RequestPreviewMetadataSnapshotLocked(*endpoint, *proxy, &requestedGeneration) != 0) {
                previewMetadataTokenRegistry.erase(handle);
                return nullptr;
            }
            return handle;
        }
    }
    if (!lkg_bridge_worker_claim()) return nullptr;
    WorkerContext* context = nullptr;
    try {
        context = new WorkerContext();
        context->runtimePath = runtimePath;
        for (char& character : context->runtimePath) if (character == '/') character = '\\';
        context->applicationName = applicationName;
        context->metadataOnly = true;
        context->displayRequestedGeneration = 1;
        context->displayRefreshPending = true;
        context->status.state = LKG_WORKER_CONNECTING;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                reinterpret_cast<LPCWSTR>(&lkg_worker_begin_metadata_v1), &context->moduleReference))
            throw std::runtime_error("metadata worker module pin failed");
        {
            std::lock_guard<std::mutex> lock(registryMutex);
            if (finalShutdownRequested) throw std::runtime_error("final worker shutdown already requested");
            registry.insert(context);
        }
        if (!SubmitToPersistentHost(context)) {
            std::lock_guard<std::mutex> lock(registryMutex);
            registry.erase(context);
            throw std::runtime_error("persistent metadata worker host unavailable");
        }
    } catch (...) {
        if (context && context->moduleReference) {
            FreeLibrary(context->moduleReference);
            context->moduleReference = nullptr;
        }
        delete context;
        lkg_bridge_worker_release_claim();
        return nullptr;
    }
    return context;
}

LKG_EXPORT int32_t lkg_worker_request_preview_lease_return_v1(void* leaseHandle,
    const LkgWorkerPreviewLeaseIdentityV1* identity) {
    if (!leaseHandle || !ValidPreviewLeaseIdentity(identity))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(leaseHandle);
    if (!token || !PreviewLeaseIdentityMatches(*token, identity) || token->acknowledged)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    if (token->state == LKG_WORKER_PREVIEW_LEASE_RETURNING ||
        token->state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
    WorkerContext* context = token->context;
    if (!context || registry.find(context) == registry.end() || context != previewLeaseEndpoint ||
        context->leaseEndpointId != token->endpointId || context->leaseGeneration != token->generation ||
        context->currentLeaseToken != leaseHandle)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    std::lock_guard<std::mutex> contextLock(context->mutex);
    if (context->status.state == LKG_WORKER_FAULT || context->leaseFinalCloseRequested.load(std::memory_order_acquire)) {
        token->state = LKG_WORKER_PREVIEW_LEASE_FAULTED;
        token->lastResult = LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    }
    RequestLeaseReturnLocked(*context, *token);
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

LKG_EXPORT int32_t lkg_worker_get_preview_lease_status_v1(void* leaseHandle,
    const LkgWorkerPreviewLeaseIdentityV1* identity, LkgWorkerPreviewLeaseStatusV1* status) {
    if (!leaseHandle || !ValidPreviewLeaseIdentity(identity) || !status)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(leaseHandle);
    if (!token || !PreviewLeaseIdentityMatches(*token, identity)) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    LkgWorkerPreviewLeaseStatusV1 value{};
    value.structSize = sizeof(value);
    value.version = 1;
    value.state = token->state;
    value.endpointId = token->endpointId;
    value.leaseGeneration = token->generation;
    value.lastResult = token->lastResult;
    if (token->state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN || token->acknowledged ||
        token->state == LKG_WORKER_PREVIEW_LEASE_FAULTED) {
        value.workerStatus = token->workerStatus;
    } else if (token->context && registry.find(token->context) != registry.end()) {
        std::lock_guard<std::mutex> contextLock(token->context->mutex);
        value.workerStatus = token->context->status;
        if (token->context->status.state == LKG_WORKER_FAULT) value.state = LKG_WORKER_PREVIEW_LEASE_FAULTED;
    } else {
        value.workerStatus = token->workerStatus;
    }
    *status = value;
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

LKG_EXPORT int32_t lkg_worker_ack_preview_lease_return_v1(void* leaseHandle,
    const LkgWorkerPreviewLeaseIdentityV1* identity) {
    if (!leaseHandle || !ValidPreviewLeaseIdentity(identity))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(leaseHandle);
    if (!token || !PreviewLeaseIdentityMatches(*token, identity)) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    if (token->acknowledged) return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
    if (token->state != LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    WorkerContext* context = token->context;
    if (!context || registry.find(context) == registry.end() || context != previewLeaseEndpoint ||
        context->leaseEndpointId != token->endpointId || context->leaseGeneration != token->generation)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    std::lock_guard<std::mutex> contextLock(context->mutex);
    if (!context->leaseReturnProven || context->leaseActive || context->currentLeaseToken != leaseHandle)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    token->workerStatus = context->status;
    token->acknowledged = true;
    context->currentLeaseToken = nullptr;
    context->condition.notify_all();
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

#if defined(LKG_WORKER_TRANSPORT_TESTING)
static WorkerContext* ResolveTestWorkerContextLocked(void* handle) {
    if (!handle) return nullptr;
    const auto direct = registry.find(static_cast<WorkerContext*>(handle));
    if (direct != registry.end()) return *direct;

    PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(handle);
    if (!token || token->acknowledged || !token->context ||
        (token->state != LKG_WORKER_PREVIEW_LEASE_ACTIVE &&
            token->state != LKG_WORKER_PREVIEW_LEASE_RETURNING &&
            token->state != LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN))
        return nullptr;
    WorkerContext* context = token->context;
    if (registry.find(context) == registry.end() || context != previewLeaseEndpoint ||
        context->leaseEndpointId != token->endpointId || context->leaseGeneration != token->generation ||
        context->currentLeaseToken != handle)
        return nullptr;
    return context;
}

extern "C" LKG_EXPORT int32_t lkg_worker_test_fail_window_poll(void* handle) {
    if (!handle) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveTestWorkerContextLocked(handle);
    if (!context) return -2;
    std::lock_guard<std::mutex> contextLock(context->mutex);
    if (context->closeRequested.load(std::memory_order_acquire) || context->finished.load(std::memory_order_acquire))
        return -2;
    context->testFailWindowPoll.store(true, std::memory_order_release);
    context->condition.notify_all();
    return 0;
}

LKG_EXPORT void lkg_worker_test_throw_during_cleanup(void) {
    testThrowDuringWorkerCleanup.store(true, std::memory_order_release);
}

LKG_EXPORT void lkg_worker_test_throw_during_display_query(void) {
    testThrowDuringDisplayQuery.store(true, std::memory_order_release);
}

extern "C" LKG_EXPORT void lkg_worker_test_configure_source_copy(uint32_t pendingPolls, uint32_t queryFailures,
    uint32_t presenterEarlyResult, bool throwAfterCopy, bool holdQuery) {
    testSourceCopyPendingPolls.store(pendingPolls, std::memory_order_release);
    testSourceCopyQueryFailures.store(queryFailures, std::memory_order_release);
    testPresenterEarlyResult.store(presenterEarlyResult, std::memory_order_release);
    testThrowAfterSourceCopy.store(throwAfterCopy, std::memory_order_release);
    testHoldSourceCopyQuery.store(holdQuery, std::memory_order_release);
}

extern "C" LKG_EXPORT void lkg_worker_test_throw_after_worker_dequeue(void) {
    testThrowAfterWorkerDequeue.store(true, std::memory_order_release);
}

extern "C" LKG_EXPORT int32_t lkg_worker_test_get_source_copy_state(void* handle, uint32_t slotIndex, uint32_t* flags) {
    if (!handle || !flags || slotIndex >= 3) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveTestWorkerContextLocked(handle);
    if (!context) return -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    const WorkerContext::Slot& slot = context->slots[slotIndex];
    *flags = (slot.inUse ? LKG_WORKER_TEST_SLOT_IN_USE : 0u) |
        (slot.sourceRetirementUnproven ? LKG_WORKER_TEST_SOURCE_UNPROVEN : 0u) |
        (slot.sourceCopyCompleted ? LKG_WORKER_TEST_SOURCE_COPY_COMPLETED : 0u) |
        (slot.sourceMutexHeld ? LKG_WORKER_TEST_SOURCE_MUTEX_HELD : 0u) |
        (slot.quarantined ? LKG_WORKER_TEST_SLOT_QUARANTINED : 0u) |
        (slot.sourceCopyQueryIssued ? LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED : 0u) |
        (slot.d3d12Slot && (slot.unknownSubmission || slot.producerFenceValue12 != 0 ||
            slot.sourceTexture12 || slot.producerFence12 || slot.producerDevice12 || slot.consumerCopyPending)
            ? LKG_WORKER_TEST_D3D12_OWNERSHIP_UNPROVEN : 0u);
    return 0;
}

extern "C" LKG_EXPORT int32_t lkg_worker_test_get_presenter_visibility(void* handle, uint32_t* visible) {
    if (!handle || !visible) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveTestWorkerContextLocked(handle);
    if (!context) return -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    *visible = context->testPresenterVisible ? 1u : 0u;
    return 0;
}

extern "C" LKG_EXPORT int32_t lkg_worker_test_readback_presenter_rgba8(void* handle, uint8_t* rgba,
    uint32_t capacity, uint32_t* width, uint32_t* height, uint32_t* requiredBytes, uint32_t timeoutMilliseconds) {
    if (!handle || !width || !height || !requiredBytes || timeoutMilliseconds == 0) return -1;
    std::unique_lock<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveTestWorkerContextLocked(handle);
    if (!context) return -2;
    std::unique_lock<std::mutex> contextLock(context->mutex);
    if (context->closeRequested.load(std::memory_order_acquire) || context->finished.load(std::memory_order_acquire))
        return -2;
    context->testReadbackComplete = false;
    context->testReadbackResult = -1;
    context->testReadbackPixels.clear();
    context->testReadbackError.clear();
    context->testReadbackRequested = true;
    ++context->testReadbackWaiters;
    context->condition.notify_all();
    registryLock.unlock();
    if (!context->condition.wait_for(contextLock, std::chrono::milliseconds(timeoutMilliseconds), [&] {
            return context->testReadbackComplete || context->finished.load(std::memory_order_acquire);
        })) {
        --context->testReadbackWaiters;
        return -3;
    }
    if (!context->testReadbackComplete || context->testReadbackResult != 0) {
        --context->testReadbackWaiters;
        return -4;
    }
    const size_t byteCount = context->testReadbackPixels.size();
    if (byteCount > UINT32_MAX) {
        --context->testReadbackWaiters;
        return -5;
    }
    *width = context->testReadbackWidth;
    *height = context->testReadbackHeight;
    *requiredBytes = static_cast<uint32_t>(byteCount);
    if (!rgba || capacity < byteCount) {
        --context->testReadbackWaiters;
        return -6;
    }
    std::memcpy(rgba, context->testReadbackPixels.data(), byteCount);
    --context->testReadbackWaiters;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_test_set_d3d12_timeout(void* handle, uint64_t timeoutMilliseconds) {
    auto* context = static_cast<WorkerContext*>(handle);
    if (!context || timeoutMilliseconds == 0) return -1;
    context->d3d12FenceTimeoutMs.store(timeoutMilliseconds, std::memory_order_release);
    return 0;
}

LKG_EXPORT void lkg_worker_test_delay_d3d12_consumer(uint32_t pendingPolls) {
    testD3D12ConsumerPendingPolls.store(pendingPolls, std::memory_order_release);
}

LKG_EXPORT uint32_t lkg_worker_test_d3d12_consumer_pending_polls(void) {
    return testD3D12ConsumerPendingPolls.load(std::memory_order_acquire);
}

LKG_EXPORT void lkg_worker_test_d3d12_set_removed(bool removed) {
    testD3D12FenceRemoved.store(removed, std::memory_order_release);
}

LKG_EXPORT uint32_t lkg_worker_test_thread_id(void* handle) {
    if (!handle) return 0;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    return context ? context->testThreadId : 0;
}

LKG_EXPORT HWND lkg_worker_test_window(void* handle) {
    if (!handle) return nullptr;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    return context ? context->testWindow : nullptr;
}

LKG_EXPORT uint32_t lkg_worker_test_message_count(void* handle) {
    auto* context = static_cast<WorkerContext*>(handle);
    return context ? context->testMessageCount.load(std::memory_order_relaxed) : 0;
}

LKG_EXPORT uintptr_t lkg_worker_test_context_device_identity(void* handle) {
    auto* context = static_cast<WorkerContext*>(handle);
    return context ? context->testWorkerDeviceIdentity.load(std::memory_order_acquire) : 0;
}

LKG_EXPORT int32_t lkg_worker_test_get_preview_lease_resources(void* handle, uint64_t* endpointId,
    uint64_t* generation, uint64_t* sessionId, uintptr_t* stableInputTexture) {
    if (!handle || !endpointId || !generation || !sessionId || !stableInputTexture) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(handle);
    if (!token || token->acknowledged || token->state != LKG_WORKER_PREVIEW_LEASE_ACTIVE || !token->context ||
        registry.find(token->context) == registry.end() || token->context != previewLeaseEndpoint ||
        token->context->leaseGeneration != token->generation || token->context->currentLeaseToken != handle)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    std::lock_guard<std::mutex> contextLock(token->context->mutex);
    *endpointId = token->endpointId;
    *generation = token->generation;
    *sessionId = token->context->bridgeSessionId;
    *stableInputTexture = token->context->canonicalQuilt
        ? reinterpret_cast<uintptr_t>(token->context->canonicalQuilt.Get()) : 0;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_test_get_transfer_slot_identity(void* handle, uintptr_t* producerTexture,
    uintptr_t* workerTexture, uint64_t* generation) {
    if (!handle || !producerTexture || !workerTexture || !generation) return -1;
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (registry.find(context) == registry.end()) return -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    const WorkerContext::Slot& slot = context->slots[0];
    *producerTexture = reinterpret_cast<uintptr_t>(slot.d3d12Slot
        ? static_cast<void*>(slot.sourceTexture12.Get()) : static_cast<void*>(slot.producerTexture.Get()));
    *workerTexture = reinterpret_cast<uintptr_t>(slot.d3d12Slot
        ? static_cast<void*>(slot.sharedTexture12.Get()) : static_cast<void*>(slot.workerTexture.Get()));
    *generation = slot.generation;
    return 0;
}

LKG_EXPORT uint32_t lkg_worker_test_host_device_create_count(void) {
    std::lock_guard<std::mutex> lock(hostRegistryMutex);
    return persistentHost ? persistentHost->testWorkerDeviceCreateCount.load(std::memory_order_acquire) : 0;
}

LKG_EXPORT uintptr_t lkg_worker_test_host_device_identity(void) {
    std::lock_guard<std::mutex> lock(hostRegistryMutex);
    return persistentHost ? persistentHost->testWorkerDeviceIdentity.load(std::memory_order_acquire) : 0;
}

LKG_EXPORT void lkg_worker_test_force_adapter_mismatch(void) {
    std::lock_guard<std::mutex> lock(hostRegistryMutex);
    if (persistentHost) persistentHost->testForceAdapterMismatch.store(true, std::memory_order_release);
}
#endif

LKG_EXPORT int32_t lkg_worker_poll(void* handle, LkgWorkerStatus* status) {
    if (!handle || !status) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    const auto metadata = previewMetadataTokenRegistry.find(handle);
    if (metadata != previewMetadataTokenRegistry.end()) {
        const PreviewMetadataTokenRecord& token = *metadata->second;
        if (!PreviewMetadataTokenAttachedLocked(token) && !PreviewMetadataTokenFinalized(token))
            return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        LkgWorkerStatus value{};
        value.state = token.closeRequested && token.refreshPending ? LKG_WORKER_CLOSING : token.state;
        value.displayIndex = token.displayOrdinal;
        value.lastErrorCode = token.result;
        std::snprintf(value.error, sizeof(value.error), "%s", token.error.c_str());
        *status = value;
        return 0;
    }
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    if (!context) return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    *status = context->status;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_get_physical_display_v1(void* handle, LkgWorkerPhysicalDisplayInfoV1* info) {
    if (!handle || !info || info->structSize != sizeof(LkgWorkerPhysicalDisplayInfoV1) || info->version != 1) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    const auto metadata = previewMetadataTokenRegistry.find(handle);
    if (metadata != previewMetadataTokenRegistry.end()) {
        const PreviewMetadataTokenRecord& token = *metadata->second;
        if (!PreviewMetadataTokenAttachedLocked(token) && !PreviewMetadataTokenFinalized(token))
            return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        for (const LkgWorkerDisplaySnapshotItemV1& item : token.snapshot) {
            if (item.displayOrdinal != token.displayOrdinal) continue;
            info->pixelWidth = item.physical.pixelWidth;
            info->pixelHeight = item.physical.pixelHeight;
            info->dpi = item.physical.dpi;
            info->valid = item.physical.valid;
            return 0;
        }
        info->pixelWidth = 0;
        info->pixelHeight = 0;
        info->dpi = 0.0f;
        info->valid = 0;
        return token.refreshPending ? LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY : 0;
    }
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    if (!context) return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    info->pixelWidth = context->physicalDisplay.pixelWidth;
    info->pixelHeight = context->physicalDisplay.pixelHeight;
    info->dpi = context->physicalDisplay.dpi;
    info->valid = context->physicalDisplay.valid;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_get_subpixel_status_v1(void* handle, LkgWorkerSubpixelStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgWorkerSubpixelStatusV1) || status->version != 1) return -1;
    LkgWorkerSubpixelStatusV1 value{};
    value.structSize = sizeof(value);
    value.version = 1;
    value.requestedMode = UINT32_MAX;
    value.effectiveMode = UINT32_MAX;
    value.cellPatternMode = UINT32_MAX;
    value.backend = UINT32_MAX;
    if (!handle) { *status = value; return -2; }
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    if (!context) {
        *status = value;
        return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    }
    std::lock_guard<std::mutex> contextLock(context->mutex);
    value = context->subpixelStatus;
    value.structSize = sizeof(value);
    value.version = 1;
    *status = value;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_request_display_refresh_v1(void* handle, uint64_t* requestedGeneration) {
    if (!handle || !requestedGeneration) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewMetadataTokenRecord* metadataToken = nullptr;
    WorkerContext* context = ResolveMetadataWorkerHandleLocked(handle, &metadataToken);
    if (!context) return FindPreviewLeaseTokenLocked(handle) || previewMetadataTokenRegistry.find(handle) != previewMetadataTokenRegistry.end()
        ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (metadataToken) return RequestPreviewMetadataSnapshotLocked(*context, *metadataToken, requestedGeneration);
    const bool parkedLeaseEndpoint = context->previewLeaseEndpoint && context->leaseReturnProven &&
        !context->leaseActive && context->status.state == LKG_WORKER_CLOSED;
    if (finalShutdownRequested || context->leaseFinalCloseRequested.load(std::memory_order_acquire) ||
        context->closeRequested.load(std::memory_order_acquire) ||
        context->status.state == LKG_WORKER_CLOSING ||
        (context->status.state == LKG_WORKER_CLOSED && !parkedLeaseEndpoint) ||
        context->status.state == LKG_WORKER_FAULT) return -3;
    if (!metadataToken && context->previewLeaseEndpoint && context->leaseReturnRequested.load(std::memory_order_acquire)) return -3;
    if (context->displayRequestedGeneration == UINT64_MAX) return -4;
    ++context->displayRequestedGeneration;
    context->displayRefreshPending = true;
    *requestedGeneration = context->displayRequestedGeneration;
    context->condition.notify_all();
    return 0;
}

LKG_EXPORT int32_t lkg_worker_get_display_snapshot_v1(void* handle, LkgWorkerDisplaySnapshotStatusV1* status,
    LkgWorkerDisplaySnapshotItemV1* items, uint32_t capacity, uint32_t* requiredCount) {
    if (!handle || !status || !requiredCount || status->structSize != sizeof(LkgWorkerDisplaySnapshotStatusV1) || status->version != 1 ||
        (capacity != 0 && !items)) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewMetadataTokenRecord* metadataToken = nullptr;
    PreviewMetadataTokenRecord* directMetadataToken = nullptr;
    const auto metadataEntry = previewMetadataTokenRegistry.find(handle);
    if (metadataEntry != previewMetadataTokenRegistry.end()) {
        directMetadataToken = metadataEntry->second.get();
        if (!PreviewMetadataTokenAttachedLocked(*directMetadataToken) &&
            !PreviewMetadataTokenFinalized(*directMetadataToken))
            return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        metadataToken = directMetadataToken;
    }
    WorkerContext* context = metadataToken ? metadataToken->context : ResolveMetadataWorkerHandleLocked(handle, &metadataToken);
    if (!context && !(metadataToken && PreviewMetadataTokenFinalized(*metadataToken)))
        return FindPreviewLeaseTokenLocked(handle) || previewMetadataTokenRegistry.find(handle) != previewMetadataTokenRegistry.end()
            ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    LkgWorkerDisplaySnapshotStatusV1 snapshotStatus{};
    snapshotStatus.structSize = sizeof(snapshotStatus);
    snapshotStatus.version = 1;
    if (metadataToken) {
        snapshotStatus.state = metadataToken->closeRequested && metadataToken->refreshPending
            ? LKG_WORKER_CLOSING : metadataToken->state;
        snapshotStatus.itemCount = static_cast<uint32_t>(metadataToken->snapshot.size());
        snapshotStatus.snapshotGeneration = metadataToken->snapshotGeneration;
        snapshotStatus.requestedGeneration = metadataToken->requestedGeneration;
        snapshotStatus.pending = metadataToken->refreshPending ? 1u : 0u;
        snapshotStatus.cleanupFinished = metadataToken->cleanupFinished ? 1u : 0u;
        snapshotStatus.result = metadataToken->result;
        std::snprintf(snapshotStatus.error, sizeof(snapshotStatus.error), "%s", metadataToken->error.c_str());
        *requiredCount = snapshotStatus.itemCount;
        *status = snapshotStatus;
        if (capacity < snapshotStatus.itemCount) return -3;
        for (uint32_t index = 0; index < snapshotStatus.itemCount; ++index)
            items[index] = metadataToken->snapshot[index];
        return 0;
    }
    std::lock_guard<std::mutex> lock(context->mutex);
    snapshotStatus.state = context->status.state;
    snapshotStatus.itemCount = static_cast<uint32_t>(context->displaySnapshot.size());
    snapshotStatus.snapshotGeneration = context->displaySnapshotGeneration;
    snapshotStatus.requestedGeneration = context->displayRequestedGeneration;
    snapshotStatus.pending = context->displayRefreshPending || context->displaySnapshotGeneration < context->displayRequestedGeneration ? 1u : 0u;
    snapshotStatus.cleanupFinished = context->displayCleanupFinished ? 1u : 0u;
    snapshotStatus.result = context->displaySnapshotResult;
    std::snprintf(snapshotStatus.error, sizeof(snapshotStatus.error), "%s", context->displaySnapshotError.c_str());
    *requiredCount = snapshotStatus.itemCount;
    *status = snapshotStatus;
    if (capacity < snapshotStatus.itemCount) return -3;
    for (uint32_t index = 0; index < snapshotStatus.itemCount; ++index) items[index] = context->displaySnapshot[index];
    return 0;
}

LKG_EXPORT int32_t lkg_worker_end_metadata_v1(void* handle) {
    if (!handle) return -1;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        const auto proxy = previewMetadataTokenRegistry.find(handle);
        if (proxy != previewMetadataTokenRegistry.end()) {
            PreviewMetadataTokenRecord& token = *proxy->second;
            if (token.ended) return 0;
            if (PreviewMetadataTokenFinalized(token)) {
                token.ended = true;
                return 0;
            }
            if (!PreviewMetadataTokenAttachedLocked(token)) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
            if (!token.closeRequested || token.refreshPending || !token.cleanupFinished ||
                token.state != LKG_WORKER_CLOSED) return -3;
            token.ended = true;
            return 0;
        }
    }
    auto* context = static_cast<WorkerContext*>(handle);
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        if (registry.find(context) == registry.end()) return -2;
        std::lock_guard<std::mutex> lock(context->mutex);
        if (!context->metadataOnly || !context->cleanupProven || !context->finished.load(std::memory_order_acquire)) return -3;
    }
    WorkerHost* host = nullptr;
    {
        std::lock_guard<std::mutex> hostRegistryLock(hostRegistryMutex);
        host = persistentHost;
    }
    if (host) {
        std::lock_guard<std::mutex> hostLock(host->mutex);
        if (host->active == context || host->queued == context) return -3;
    }
    return lkg_worker_release(handle);
}

LKG_EXPORT int32_t lkg_worker_close(void* handle) {
    if (!handle) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    const auto metadata = previewMetadataTokenRegistry.find(handle);
    if (metadata != previewMetadataTokenRegistry.end()) {
        PreviewMetadataTokenRecord& token = *metadata->second;
        if (token.ended) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        if (PreviewMetadataTokenFinalized(token)) return 0;
        if (!PreviewMetadataTokenAttachedLocked(token)) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        token.closeRequested = true;
        token.state = token.refreshPending ? LKG_WORKER_CLOSING : LKG_WORKER_CLOSED;
        token.cleanupFinished = !token.refreshPending;
        return 0;
    }
    if (PreviewLeaseTokenRecord* existingToken = FindPreviewLeaseTokenLocked(handle)) {
        if (existingToken->acknowledged) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        if (existingToken->state == LKG_WORKER_PREVIEW_LEASE_RETURNING ||
            existingToken->state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN)
            return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
    }
    PreviewLeaseTokenRecord* token = nullptr;
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle, &token);
    if (!context) return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    if (token) {
        std::lock_guard<std::mutex> contextLock(context->mutex);
        RequestLeaseReturnLocked(*context, *token);
        return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
    }
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state == LKG_WORKER_CLOSED || context->status.state == LKG_WORKER_FAULT) return 0;
    context->previewPauseDesired = false;
    context->closeRequested.store(true, std::memory_order_release);
    if (context->status.state == LKG_WORKER_CONNECTING || context->status.state == LKG_WORKER_READY) context->status.state = LKG_WORKER_CLOSING;
    context->condition.notify_all();
    return 0;
}

LKG_EXPORT int32_t lkg_worker_set_preview_paused(void* handle, uint32_t paused) {
    if (!handle || paused > 1) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    if (!context) return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    if (finalShutdownRequested) return -4;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->metadataOnly || context->status.state != LKG_WORKER_READY ||
        context->closeRequested.load(std::memory_order_acquire)) return -3;
    if (context->previewPausePhase == LKG_WORKER_PREVIEW_FAULTED) return -5;
    context->previewPauseDesired = paused != 0;
    if (paused != 0) {
        if (context->previewPausePhase == LKG_WORKER_PREVIEW_RUNNING ||
            context->previewPausePhase == LKG_WORKER_PREVIEW_RESUMING)
            context->previewPausePhase = LKG_WORKER_PREVIEW_PAUSING;
    } else if (context->previewPausePhase == LKG_WORKER_PREVIEW_PAUSED) {
        context->previewPausePhase = LKG_WORKER_PREVIEW_RESUMING;
    }
    context->condition.notify_all();
    return 0;
}

LKG_EXPORT int32_t lkg_worker_get_preview_pause_state(void* handle, uint32_t* phase) {
    if (!handle || !phase) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    if (!context) return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    *phase = context->status.state == LKG_WORKER_FAULT
        ? LKG_WORKER_PREVIEW_FAULTED : context->previewPausePhase;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_reset_transfer(void* handle) {
    if (!handle) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle);
    if (!context) return FindPreviewLeaseTokenLocked(handle) ? LKG_WORKER_PREVIEW_LEASE_RESULT_STALE : -1;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state != LKG_WORKER_READY || context->closeRequested.load(std::memory_order_acquire) ||
        context->previewPausePhase != LKG_WORKER_PREVIEW_RUNNING || context->previewPauseDesired) return -3;
    if (context->slotsInitializing || context->pendingTokens != 0 || context->jobCount != 0 || context->inFlightJob) return -2;
    for (const WorkerContext::Slot& slot : context->slots) {
        if (!SlotGpuOwnershipRetired(slot)) return -2;
    }
    for (WorkerContext::Slot& slot : context->slots) {
        if (slot.d3d12Slot) {
            ClearD3D12Slot(slot);
            continue;
        }
        slot.workerMutex.Reset();
        slot.workerTexture.Reset();
        slot.producerMutex.Reset();
        slot.producerTexture.Reset();
        if (slot.sharedHandle) {
            // GetSharedHandle returns a legacy shared handle owned by the
            // resource. It must remain open until the resource references are
            // released; calling CloseHandle here is invalid for this handle
            // type and can break later transfer/reopen cycles.
            slot.sharedHandle = nullptr;
        }
    }
    context->textureWidth = 0;
    context->textureHeight = 0;
    context->textureFormat = DXGI_FORMAT_UNKNOWN;
    context->condition.notify_all();
    return 0;
}

static bool ValidWorkerDrawOptions(const LkgWorkerDrawOptionsV1* options) {
    return options && options->structSize == sizeof(LkgWorkerDrawOptionsV1) && options->version == 1 &&
        options->sourceEncoding <= LKG_WORKER_SOURCE_ENCODING_SRGB && options->reserved0 == 0;
}

static bool ManualSourceEncodingMatchesTexture(IUnknown* texture, uint32_t sourceEncoding, bool* sourceWasTypeless) {
    if (sourceWasTypeless) *sourceWasTypeless = false;
    if (!texture || sourceEncoding > LKG_WORKER_SOURCE_ENCODING_SRGB) return false;
    DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
    ComPtr<ID3D11Texture2D> texture11;
    if (texture->QueryInterface(IID_PPV_ARGS(&texture11)) == S_OK) {
        D3D11_TEXTURE2D_DESC description{};
        texture11->GetDesc(&description);
        format = description.Format;
    } else {
        ComPtr<ID3D12Resource> resource12;
        if (texture->QueryInterface(IID_PPV_ARGS(&resource12)) != S_OK) return false;
        format = resource12->GetDesc().Format;
    }
    if (format == DXGI_FORMAT_R8G8B8A8_TYPELESS) {
        if (sourceWasTypeless) *sourceWasTypeless = true;
        return true;
    }
    if (format == DXGI_FORMAT_R8G8B8A8_UNORM)
        return sourceEncoding == LKG_WORKER_SOURCE_ENCODING_UNORM;
    if (format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB)
        return sourceEncoding == LKG_WORKER_SOURCE_ENCODING_SRGB;
    return false;
}

LKG_EXPORT void* lkg_worker_make_draw_data(void* handle, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom) {
    (void)handle; (void)texture; (void)columns; (void)rows; (void)aspect; (void)zoom;
    return nullptr;
}

LKG_EXPORT void* lkg_worker_make_draw_data_with_options_v1(void* handle, void* texture, uint32_t columns,
    uint32_t rows, float aspect, float zoom, const LkgWorkerDrawOptionsV1* options) {
    if (!handle || !texture || !ValidWorkerDrawOptions(options)) return nullptr;
    lkg::common::ImageDescriptor request{};
    request.columns = columns;
    request.rows = rows;
    request.aspect = aspect;
    request.zoom = zoom;
    if (lkg::common::ValidateDrawRequest(request) != lkg::common::ValidationResult::Valid) return nullptr;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewLeaseTokenRecord* leaseToken = nullptr;
    WorkerContext* context = ResolveActiveWorkerHandleLocked(handle, &leaseToken);
    if (finalShutdownRequested || !context) return nullptr;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state != LKG_WORKER_READY || context->closeRequested.load(std::memory_order_acquire) ||
        context->leaseReturnRequested.load(std::memory_order_acquire) ||
        context->previewPauseDesired || context->previewPausePhase != LKG_WORKER_PREVIEW_RUNNING || context->pendingTokens >= 3) {
        ++context->status.dropped;
        return nullptr;
    }
    bool sourceWasTypeless = false;
    if (!ManualSourceEncodingMatchesTexture(reinterpret_cast<IUnknown*>(texture), options->sourceEncoding, &sourceWasTypeless)) {
        ++context->status.dropped;
        return nullptr;
    }
    auto data = std::unique_ptr<WorkerDrawData>(new (std::nothrow) WorkerDrawData{ 0, context, reinterpret_cast<IUnknown*>(texture), columns, rows, aspect, zoom });
    if (!data) return nullptr;
    data->sourceEncoding = options->sourceEncoding;
    data->sourceWasTypeless = sourceWasTypeless;
    if (leaseToken) data->leaseGeneration = leaseToken->generation;
    data->texture->AddRef();
    const uint64_t id = nextTokenId.fetch_add(1, std::memory_order_relaxed);
    if (id == 0 || id == UINT64_MAX) return nullptr;
    data->id = id;
    try {
        std::lock_guard<std::mutex> tokenLock(tokenMutex);
        tokenRegistry.emplace(id, std::move(data));
    } catch (...) {
        return nullptr;
    }
    ++context->pendingTokens;
    return reinterpret_cast<void*>(static_cast<uintptr_t>(id));
}

LKG_EXPORT int32_t lkg_worker_try_make_preview_draw_data_v1(void* leaseHandle,
    const LkgWorkerPreviewLeaseIdentityV1* identity, void* texture, uint32_t columns, uint32_t rows,
    float aspect, float zoom, void** drawData) {
    if (drawData) *drawData = nullptr;
    (void)leaseHandle; (void)identity; (void)texture; (void)columns; (void)rows; (void)aspect; (void)zoom;
    if (drawData) *drawData = nullptr;
    return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
}

LKG_EXPORT int32_t lkg_worker_try_make_preview_draw_data_with_options_v1(void* leaseHandle,
    const LkgWorkerPreviewLeaseIdentityV1* identity, void* texture, uint32_t columns, uint32_t rows,
    float aspect, float zoom, const LkgWorkerDrawOptionsV1* options, void** drawData) {
    if (drawData) *drawData = nullptr;
    if (!leaseHandle || !ValidPreviewLeaseIdentity(identity) || !texture || !drawData || !ValidWorkerDrawOptions(options))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;

    lkg::common::ImageDescriptor request{};
    request.columns = columns;
    request.rows = rows;
    request.aspect = aspect;
    request.zoom = zoom;
    if (lkg::common::ValidateDrawRequest(request) != lkg::common::ValidationResult::Valid)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;

    std::lock_guard<std::mutex> registryLock(registryMutex);
    PreviewLeaseTokenRecord* leaseToken = FindPreviewLeaseTokenLocked(leaseHandle);
    if (!leaseToken || !PreviewLeaseIdentityMatches(*leaseToken, identity))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    if (leaseToken->acknowledged || leaseToken->state != LKG_WORKER_PREVIEW_LEASE_ACTIVE ||
        !leaseToken->context || leaseToken->context != previewLeaseEndpoint ||
        leaseToken->context->leaseEndpointId != identity->endpointId)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;

    WorkerContext* context = leaseToken->context;
    std::lock_guard<std::mutex> contextLock(context->mutex);
    if (!context->previewLeaseEndpoint || !context->leaseActive || context->leaseGeneration != identity->leaseGeneration ||
        context->currentLeaseToken != leaseHandle || context->closeRequested.load(std::memory_order_acquire) ||
        context->leaseReturnRequested.load(std::memory_order_acquire))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
    if (finalShutdownRequested || context->status.state == LKG_WORKER_FAULT || context->renderFault)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    if (context->status.state != LKG_WORKER_READY || context->previewPauseDesired ||
        context->previewPausePhase != LKG_WORKER_PREVIEW_RUNNING)
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    bool sourceWasTypeless = false;
    if (!ManualSourceEncodingMatchesTexture(reinterpret_cast<IUnknown*>(texture), options->sourceEncoding, &sourceWasTypeless))
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    if (context->pendingTokens >= 3) {
        ++context->status.dropped;
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    }

    const uint64_t id = nextTokenId.fetch_add(1, std::memory_order_relaxed);
    if (id == 0 || id == UINT64_MAX) return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    std::unique_ptr<WorkerDrawData> data(new (std::nothrow) WorkerDrawData{
        id, context, reinterpret_cast<IUnknown*>(texture), columns, rows, aspect, zoom,
        options->sourceEncoding, identity->leaseGeneration });
    if (!data) return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    data->sourceWasTypeless = sourceWasTypeless;
    data->texture->AddRef();
    try {
        std::lock_guard<std::mutex> tokenLock(tokenMutex);
        auto inserted = tokenRegistry.emplace(id, std::move(data));
        if (!inserted.second) return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    } catch (...) {
        return LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE;
    }
    ++context->pendingTokens;
    *drawData = reinterpret_cast<void*>(static_cast<uintptr_t>(id));
    return LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
}

LKG_EXPORT int32_t lkg_worker_cancel_draw_data(void* drawData) {
    if (!drawData) return -1;
    const uint64_t id = static_cast<uint64_t>(reinterpret_cast<uintptr_t>(drawData));
    if (id == 0) return -1;
    std::unique_ptr<WorkerDrawData> data;
    {
        std::lock_guard<std::mutex> tokenLock(tokenMutex);
        const auto found = tokenRegistry.find(id);
        if (found == tokenRegistry.end()) return -2;
        data = std::move(found->second);
        tokenRegistry.erase(found);
    }
    WorkerContext* context = data->context;
    if (context) {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        if (registry.find(context) != registry.end()) {
            std::lock_guard<std::mutex> lock(context->mutex);
            if (context->pendingTokens) --context->pendingTokens;
            ++context->status.dropped;
            context->condition.notify_all();
        }
    }
    return 0;
}

static void RetireUnclaimedTokens() {
    std::vector<std::unique_ptr<WorkerDrawData>> retired;
    {
        std::lock_guard<std::mutex> tokenLock(tokenMutex);
        for (auto& entry : tokenRegistry) retired.push_back(std::move(entry.second));
        tokenRegistry.clear();
    }
    if (retired.empty()) return;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    for (const auto& token : retired) {
        WorkerContext* context = token ? token->context : nullptr;
        if (!context || registry.find(context) == registry.end()) continue;
        std::lock_guard<std::mutex> lock(context->mutex);
        if (context->pendingTokens) --context->pendingTokens;
        ++context->status.dropped;
        context->condition.notify_all();
    }
}

LKG_EXPORT int32_t lkg_worker_close_all(void) {
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        for (auto& entry : previewMetadataTokenRegistry) {
            PreviewMetadataTokenRecord& token = *entry.second;
            if (token.ended) continue;
            token.closeRequested = true;
            token.state = token.refreshPending ? LKG_WORKER_CLOSING : LKG_WORKER_CLOSED;
            token.cleanupFinished = !token.refreshPending;
        }
        for (WorkerContext* context : registry) {
            if (!context) continue;
            std::lock_guard<std::mutex> lock(context->mutex);
            if (context->previewLeaseEndpoint) {
                if (finalShutdownRequested) {
                    context->leaseFinalCloseRequested.store(true, std::memory_order_release);
                    context->closeRequested.store(true, std::memory_order_release);
                    context->previewPauseDesired = false;
                    if (context->status.state == LKG_WORKER_CONNECTING || context->status.state == LKG_WORKER_READY)
                        context->status.state = LKG_WORKER_CLOSING;
                } else if (context->leaseActive && context->currentLeaseToken) {
                    PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(context->currentLeaseToken);
                    if (token) RequestLeaseReturnLocked(*context, *token);
                }
                context->condition.notify_all();
                continue;
            }
            context->previewPauseDesired = false;
            context->closeRequested.store(true, std::memory_order_release);
            if (context->status.state == LKG_WORKER_CONNECTING || context->status.state == LKG_WORKER_READY)
                context->status.state = LKG_WORKER_CLOSING;
            context->condition.notify_all();
        }
    }
    RetireUnclaimedTokens();
    return 0;
}

LKG_EXPORT uint32_t lkg_worker_pending_count(void) {
    uint32_t pending = 0;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    for (const auto& entry : previewMetadataTokenRegistry) {
        const PreviewMetadataTokenRecord& token = *entry.second;
        if (!token.ended && (token.closeRequested || token.state == LKG_WORKER_FAULT)) ++pending;
    }
    for (WorkerContext* context : registry) {
        if (!context) continue;
        std::lock_guard<std::mutex> lock(context->mutex);
        if (context->previewLeaseEndpoint) {
            PreviewLeaseTokenRecord* currentToken = FindPreviewLeaseTokenLocked(context->currentLeaseToken);
            const bool leaseReturnPending = currentToken && !currentToken->acknowledged &&
                (currentToken->state == LKG_WORKER_PREVIEW_LEASE_RETURNING ||
                 currentToken->state == LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN);
            if (context->leaseFinalCloseRequested.load(std::memory_order_acquire) || leaseReturnPending ||
                (context->closeRequested.load(std::memory_order_acquire) && !context->finished.load(std::memory_order_acquire)))
                ++pending;
            continue;
        }
        const bool closing = context->closeRequested.load(std::memory_order_acquire) ||
            context->status.state == LKG_WORKER_CLOSING || context->status.state == LKG_WORKER_CLOSED ||
            context->status.state == LKG_WORKER_FAULT;
        if (closing) ++pending;
    }
    return pending;
}

LKG_EXPORT int32_t lkg_worker_request_final_shutdown(void) {
    if (lkg_bridge_close_sdk_admission() != 0) return -2;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        if (finalShutdownRequested) return 0;
        finalShutdownRequested = true;
        finalShutdownAdmissionClosed.store(true, std::memory_order_release);
        finalShutdownState.store(LKG_WORKER_FINAL_SHUTDOWN_DRAINING, std::memory_order_release);
        finalShutdownLastResult.store(0, std::memory_order_release);
        for (auto& entry : previewMetadataTokenRegistry) {
            PreviewMetadataTokenRecord& token = *entry.second;
            if (token.ended) continue;
            token.closeRequested = true;
            token.state = token.refreshPending ? LKG_WORKER_CLOSING : LKG_WORKER_CLOSED;
            token.cleanupFinished = !token.refreshPending;
        }
        for (WorkerContext* context : registry) {
            if (!context || !context->previewLeaseEndpoint) continue;
            std::lock_guard<std::mutex> contextLock(context->mutex);
            if (context->currentLeaseToken) {
                PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(context->currentLeaseToken);
                if (token && !token->acknowledged && token->state == LKG_WORKER_PREVIEW_LEASE_ACTIVE) {
                    token->state = LKG_WORKER_PREVIEW_LEASE_RETURNING;
                    token->lastResult = LKG_WORKER_PREVIEW_LEASE_RESULT_OK;
                }
            }
            context->leaseReturnRequested.store(true, std::memory_order_release);
            context->leaseFinalCloseRequested.store(true, std::memory_order_release);
            context->closeRequested.store(true, std::memory_order_release);
            context->previewPauseDesired = false;
            if (context->status.state == LKG_WORKER_CONNECTING || context->status.state == LKG_WORKER_READY)
                context->status.state = LKG_WORKER_CLOSING;
            context->condition.notify_all();
        }
    }
    lkg_worker_close_all();
    WorkerHost* host = nullptr;
    {
        std::lock_guard<std::mutex> hostRegistryLock(hostRegistryMutex);
        host = persistentHost;
    }
    if (host) {
        std::lock_guard<std::mutex> hostLock(host->mutex);
        host->stopRequested = true;
        if (host->queued) host->queued->closeRequested.store(true, std::memory_order_release);
        if (host->active) host->active->closeRequested.store(true, std::memory_order_release);
        host->condition.notify_all();
    }
    return 0;
}

static bool ValidateFinalShutdownStatusRequest(const LkgWorkerFinalShutdownStatusV1* status) {
    return status && status->structSize == sizeof(LkgWorkerFinalShutdownStatusV1) && status->version == 1;
}

LKG_EXPORT int32_t lkg_worker_get_final_shutdown_status_v1(LkgWorkerFinalShutdownStatusV1* status) {
    if (!ValidateFinalShutdownStatusRequest(status)) return -1;
    bool requested = false;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        requested = finalShutdownRequested;
    }
    if (requested) {
        lkg_worker_collect_closed();
        lkg_worker_shutdown_host(0);
    }

    uint32_t registeredContexts = 0;
    uint32_t closedUncollectedContexts = 0;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        registeredContexts = static_cast<uint32_t>(registry.size());
        for (WorkerContext* context : registry) {
            if (!context) continue;
            std::lock_guard<std::mutex> contextLock(context->mutex);
            const bool terminal = context->status.state == LKG_WORKER_CLOSED || context->status.state == LKG_WORKER_FAULT;
            if (terminal && context->finished.load(std::memory_order_acquire)) ++closedUncollectedContexts;
        }
    }

    WorkerHost* host = nullptr;
    bool hostRunning = false;
    bool hostJoined = true;
    bool hostFaulted = false;
    {
        std::lock_guard<std::mutex> hostRegistryLock(hostRegistryMutex);
        host = persistentHost;
    }
    if (host) {
        std::lock_guard<std::mutex> hostLock(host->mutex);
        hostJoined = host->joined;
        hostRunning = !host->stopped && !host->joined;
        hostFaulted = host->sdkFaulted || host->sdkFinalizationResult != 0;
    }

    const bool sdkFaulted = lkg_bridge_sdk_shutdown_faulted() != 0;
    const bool sdkIncomplete = lkg_bridge_sdk_shutdown_incomplete() != 0;
    uint32_t state = finalShutdownState.load(std::memory_order_acquire);
    int32_t lastResult = finalShutdownLastResult.load(std::memory_order_acquire);
    if (sdkFaulted || (requested && hostFaulted)) {
        state = LKG_WORKER_FINAL_SHUTDOWN_FAULT;
        lastResult = -2;
    } else if (requested && registeredContexts == 0 && hostJoined && !sdkIncomplete) {
        state = LKG_WORKER_FINAL_SHUTDOWN_COMPLETE;
        lastResult = 0;
    } else if (requested && state != LKG_WORKER_FINAL_SHUTDOWN_TIMED_OUT) {
        state = registeredContexts == 0 && hostJoined && !sdkIncomplete
            ? LKG_WORKER_FINAL_SHUTDOWN_JOINING
            : LKG_WORKER_FINAL_SHUTDOWN_DRAINING;
        lastResult = 0;
    }
    finalShutdownState.store(state, std::memory_order_release);
    finalShutdownLastResult.store(lastResult, std::memory_order_release);
    status->structSize = sizeof(LkgWorkerFinalShutdownStatusV1);
    status->version = 1;
    status->state = state;
    status->registeredContexts = registeredContexts;
    status->closedUncollectedContexts = closedUncollectedContexts;
    status->hostRunning = hostRunning ? 1u : 0u;
    status->hostJoined = hostJoined ? 1u : 0u;
    status->lastResult = lastResult;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_wait_final_shutdown_v1(uint32_t timeoutMilliseconds, LkgWorkerFinalShutdownStatusV1* status) {
    if (!ValidateFinalShutdownStatusRequest(status)) return -1;
    if (lkg_worker_request_final_shutdown() != 0) return -2;
    const ULONGLONG deadline = GetTickCount64() + timeoutMilliseconds;
    for (;;) {
        if (lkg_worker_get_final_shutdown_status_v1(status) != 0) return -1;
        if (status->state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE) return 0;
        if (status->state == LKG_WORKER_FINAL_SHUTDOWN_FAULT && status->registeredContexts == 0 && status->hostJoined == 1) return -2;
        if (GetTickCount64() >= deadline) {
            if (status->state == LKG_WORKER_FINAL_SHUTDOWN_FAULT) return -2;
            finalShutdownState.store(LKG_WORKER_FINAL_SHUTDOWN_TIMED_OUT, std::memory_order_release);
            finalShutdownLastResult.store(1, std::memory_order_release);
            status->state = LKG_WORKER_FINAL_SHUTDOWN_TIMED_OUT;
            status->lastResult = 1;
            return 1;
        }
        Sleep(1);
    }
}

LKG_EXPORT int32_t lkg_worker_collect_closed(void) {
    std::vector<WorkerContext*> reclaim;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        for (auto it = registry.begin(); it != registry.end();) {
            WorkerContext* context = *it;
            bool finished = false;
            bool idleHost = true;
            bool finalLeaseHostComplete = false;
            {
                std::lock_guard<std::mutex> lock(context->mutex);
                if (context->metadataOnly) {
                    ++it;
                    continue;
                }
                if (context->previewLeaseEndpoint &&
                    (!finalShutdownRequested || !context->leaseFinalCloseRequested.load(std::memory_order_acquire))) {
                    ++it;
                    continue;
                }
                finished = context->cleanupProven && context->finished.load(std::memory_order_acquire) &&
                    context->pendingTokens == 0 && context->jobCount == 0 && !context->inFlightJob;
            }
            if (context->hosted) {
                WorkerHost* host = nullptr;
                {
                    std::lock_guard<std::mutex> hostLock(hostRegistryMutex);
                    host = persistentHost;
                }
                if (host) {
                    std::lock_guard<std::mutex> hostLock(host->mutex);
                    idleHost = host->active != context && host->queued != context;
                    finalLeaseHostComplete = host->joined && host->sdkFinalizationResult == 0;
                }
            }
            if (context->previewLeaseEndpoint && !finalLeaseHostComplete) idleHost = false;
            if (finished && idleHost) {
                if (context->previewLeaseEndpoint) {
                    if (previewLeaseEndpoint == context) previewLeaseEndpoint = nullptr;
                    for (auto& tokenEntry : previewLeaseTokenRegistry) {
                        if (tokenEntry.second->endpointId == context->leaseEndpointId)
                            tokenEntry.second->context = nullptr;
                    }
                    for (auto& tokenEntry : previewMetadataTokenRegistry) {
                        if (tokenEntry.second->endpointId == context->leaseEndpointId)
                            tokenEntry.second->context = nullptr;
                    }
                }
                reclaim.push_back(context);
                it = registry.erase(it);
            } else {
                ++it;
            }
        }
    }
    for (WorkerContext* context : reclaim) {
        if (context->thread.joinable()) context->thread.join();
#if defined(_WIN32)
        if (context->moduleReference) FreeLibrary(context->moduleReference);
#endif
        delete context;
    }
    return static_cast<int32_t>(reclaim.size());
}

LKG_EXPORT void* lkg_worker_render_event_func(void) {
    return reinterpret_cast<void*>(&WorkerRenderEvent);
}

LKG_EXPORT int32_t lkg_worker_render_event_id(int32_t* eventId) {
    return lkg_bridge_get_worker_render_event_id(eventId);
}

LKG_EXPORT int32_t lkg_worker_release(void* handle) {
    if (!handle) return -1;
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (PreviewLeaseTokenRecord* token = FindPreviewLeaseTokenLocked(handle)) {
        if (token->acknowledged) return LKG_WORKER_PREVIEW_LEASE_RESULT_STALE;
        if (token->state == LKG_WORKER_PREVIEW_LEASE_ACTIVE && token->context &&
            registry.find(token->context) != registry.end()) {
            std::lock_guard<std::mutex> contextLock(token->context->mutex);
            RequestLeaseReturnLocked(*token->context, *token);
        }
        return LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY;
    }
    auto* context = static_cast<WorkerContext*>(handle);
    if (registry.find(context) == registry.end()) return -2;
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        if (!context->cleanupProven || !context->finished.load(std::memory_order_acquire)
#if defined(LKG_WORKER_TRANSPORT_TESTING) && defined(LKG_SUBPIXEL_PRESENTER_TESTS)
            || context->testReadbackWaiters != 0
#endif
            ) return -2;
    }
    if (context->thread.joinable()) context->thread.join();
#if defined(_WIN32)
    if (context->moduleReference) FreeLibrary(context->moduleReference);
#endif
    registry.erase(context);
    delete context;
    return 0;
}
