#include "bridge_worker_transport.h"
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3d12.h>
#include <dxgi.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include "bridge_d3d12_internal.h"
#include <atomic>
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
#include <vector>

using Microsoft::WRL::ComPtr;

bool lkg_bridge_worker_claim();
void lkg_bridge_worker_release_claim();
void lkg_bridge_worker_enter();
void lkg_bridge_worker_leave();

struct WorkerContext {
    std::mutex mutex;
    std::thread thread;
    std::string runtimePath;
    std::string applicationName;
    uint32_t displayOrdinal = 0;
    IUnknown* producerDevice = nullptr;
    LkgWorkerStatus status{};
    std::atomic<bool> closeRequested{false};
    std::atomic<bool> finished{false};
    bool hosted = false;
    std::condition_variable condition;
    struct Slot {
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
        bool d3d12Slot = false;
        bool consumerCopyPending = false;
        bool consumerTimedOut = false;
        bool quarantined = false;
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
    bool producerD3D12 = false;
    std::atomic<uint64_t> d3d12FenceTimeoutMs{ 10000 };
    bool slotsInitializing = false;
    bool renderFault = false;
#if defined(_WIN32)
    HMODULE moduleReference = nullptr;
#endif
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    DWORD testThreadId = 0;
    HWND testWindow = nullptr;
    std::atomic<uint32_t> testMessageCount{0};
#endif
};

struct WorkerHost {
    std::mutex mutex;
    std::condition_variable condition;
    WorkerContext* queued = nullptr;
    WorkerContext* active = nullptr;
    bool stopRequested = false;
    bool stopped = false;
    bool joined = false;
    std::thread thread;
#if defined(_WIN32)
    HMODULE moduleReference = nullptr;
#endif
};

static std::mutex registryMutex;
static std::unordered_set<WorkerContext*> registry;
static std::mutex hostRegistryMutex;
static WorkerHost* persistentHost = nullptr;

struct WorkerDrawData {
    uint64_t id = 0;
    WorkerContext* context = nullptr;
    IUnknown* texture = nullptr;
    uint32_t columns = 0;
    uint32_t rows = 0;
    float aspect = 0.0f;
    float zoom = 0.0f;
    ~WorkerDrawData() { if (texture) texture->Release(); }
};

static std::mutex tokenMutex;
static std::unordered_map<uint64_t, std::unique_ptr<WorkerDrawData>> tokenRegistry;
static std::atomic<uint64_t> nextTokenId{1};
static std::atomic<uint32_t> testD3D12ConsumerPendingPolls{0};
static std::atomic<bool> testD3D12FenceRemoved{false};

static UINT64 D3D12FenceCompletedValue(ID3D12Fence* fence) {
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    if (testD3D12FenceRemoved.load(std::memory_order_acquire)) return UINT64_MAX;
#endif
    return fence ? fence->GetCompletedValue() : 0;
}
static thread_local WorkerContext* renderExceptionContext = nullptr;
static thread_local uint32_t renderExceptionSlot = 3;
static void SetFault(WorkerContext& context, uint32_t code, const char* text);

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
        bool quarantined = false;
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
        snapshot.slots[i].quarantined = context.slots[i].quarantined;
    }
    return snapshot;
}

static void TraceWorkerShutdownSnapshot(const char* stage, const WorkerShutdownSnapshot& snapshot) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file,
                 "shutdown pid=%lu tid=%lu tick_ms=%llu session_id=%llu stage=%s pendingTokens=%u jobCount=%u inFlightJob=%d slot0.inUse=%d slot0.consumerCopyPending=%d slot0.quarantined=%d slot1.inUse=%d slot1.consumerCopyPending=%d slot1.quarantined=%d slot2.inUse=%d slot2.consumerCopyPending=%d slot2.quarantined=%d\n",
                 static_cast<unsigned long>(GetCurrentProcessId()),
                 static_cast<unsigned long>(GetCurrentThreadId()),
                 static_cast<unsigned long long>(GetTickCount64()),
                 static_cast<unsigned long long>(snapshot.sessionId),
                 stage ? stage : "unknown", snapshot.pendingTokens, snapshot.jobCount,
                 snapshot.inFlightJob ? 1 : 0,
                 snapshot.slots[0].inUse ? 1 : 0,
                 snapshot.slots[0].consumerCopyPending ? 1 : 0,
                 snapshot.slots[0].quarantined ? 1 : 0,
                 snapshot.slots[1].inUse ? 1 : 0,
                 snapshot.slots[1].consumerCopyPending ? 1 : 0,
                 snapshot.slots[1].quarantined ? 1 : 0,
                 snapshot.slots[2].inUse ? 1 : 0,
                 snapshot.slots[2].consumerCopyPending ? 1 : 0,
                 snapshot.slots[2].quarantined ? 1 : 0);
    std::fclose(file);
}

static void RunWorker(WorkerContext* context);

static WorkerHost* GetPersistentHost() {
    std::lock_guard<std::mutex> lock(hostRegistryMutex);
    if (persistentHost) return persistentHost;
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
                {
                    std::unique_lock<std::mutex> lock(host->mutex);
                    host->condition.wait_for(lock, std::chrono::milliseconds(10), [&] { return host->queued || host->stopRequested; });
                    if (!host->queued && host->stopRequested) {
                        host->stopped = true;
                        host->condition.notify_all();
                        return;
                    }
                    if (!host->queued) {
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
                    }
                    context = host->queued;
                    host->queued = nullptr;
                    host->active = context;
                }
                RunWorker(context);
                {
                    std::lock_guard<std::mutex> lock(host->mutex);
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
    WorkerHost* host = GetPersistentHost();
    if (!host) return false;
    std::lock_guard<std::mutex> lock(host->mutex);
    if (host->stopRequested || host->queued || host->active) return false;
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
    if (host->joined) return 0;
    host->stopRequested = true;
    if (host->queued) host->queued->closeRequested.store(true, std::memory_order_release);
    if (host->active) host->active->closeRequested.store(true, std::memory_order_release);
    host->condition.notify_all();
    const bool stopped = host->condition.wait_for(lock, std::chrono::milliseconds(timeoutMilliseconds), [&] { return host->stopped && !host->active && !host->queued; });
    if (!stopped) return -1;
    if (host->thread.joinable()) host->thread.join();
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
    context.closeRequested.store(true, std::memory_order_release);
    context.condition.notify_all();
    TraceWorker("fault");
}

static void SetBridgeFault(WorkerContext& context, uint32_t code, const char* operation, int32_t result) {
    char bridgeError[160]{};
    lkg_bridge_last_error(bridgeError, sizeof(bridgeError));
    char message[256]{};
    std::snprintf(message, sizeof(message), "%s failed rc=%d last_error=%s", operation, result, bridgeError[0] ? bridgeError : "none");
    SetFault(context, code, message);
}

static bool CloseRequested(WorkerContext& context) {
    std::lock_guard<std::mutex> lock(context.mutex);
    return context.closeRequested.load(std::memory_order_acquire);
}

static bool IsValidRenderFormat(const D3D11_TEXTURE2D_DESC& desc) {
    return desc.Width != 0 && desc.Height != 0 && desc.MipLevels == 1 && desc.ArraySize == 1 &&
        (desc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS || desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM || desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) &&
        desc.SampleDesc.Count == 1;
}

static DXGI_FORMAT SharedRenderFormat(DXGI_FORMAT sourceFormat) {
    return sourceFormat == DXGI_FORMAT_R8G8B8A8_TYPELESS ? DXGI_FORMAT_R8G8B8A8_UNORM : sourceFormat;
}

static void SetRenderFault(WorkerContext& context, const char* message);
static void ClearD3D12Slot(WorkerContext::Slot& slot);

static bool IsValidD3D12RenderFormat(const D3D12_RESOURCE_DESC& desc) {
    return desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D && desc.Width != 0 && desc.Height != 0 &&
        desc.DepthOrArraySize == 1 && desc.MipLevels == 1 &&
        (desc.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS || desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM || desc.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) &&
        desc.SampleDesc.Count == 1 && desc.SampleDesc.Quality == 0;
}

static void TraceD3D12SourceDesc(const D3D12_RESOURCE_DESC& desc, ID3D12Device* device, uint64_t generation) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file, "d3d12-source-desc width=%llu height=%u format=%u mip=%u array=%u sampleCount=%u sampleQuality=%u flags=%u device=%p generation=%llu\n",
                 static_cast<unsigned long long>(desc.Width), desc.Height, static_cast<unsigned>(desc.Format),
                 desc.MipLevels, desc.DepthOrArraySize, desc.SampleDesc.Count, desc.SampleDesc.Quality,
                 static_cast<unsigned>(desc.Flags), device, static_cast<unsigned long long>(generation));
    std::fclose(file);
}

static bool EnsureD3D12Slots(WorkerContext& context, ID3D12Device* device, const D3D12_RESOURCE_DESC& source, uint64_t generation) {
    if (!IsValidD3D12RenderFormat(source)) {
        char message[256]{};
        std::snprintf(message, sizeof(message), "D3D12 source unsupported width=%llu height=%u format=%u mip=%u array=%u sampleCount=%u device=%p",
                      static_cast<unsigned long long>(source.Width), source.Height, static_cast<unsigned>(source.Format),
                      source.MipLevels, source.DepthOrArraySize, source.SampleDesc.Count, device);
        SetRenderFault(context, message);
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.textureWidth != 0 && (context.textureWidth != source.Width || context.textureHeight != source.Height ||
            context.textureFormat != source.Format || context.slots[0].generation != generation)) {
            context.renderFault = true;
            SetError(context, 10, "D3D12 source resize or device generation change requires reopen");
            context.status.state = LKG_WORKER_FAULT;
            context.closeRequested.store(true, std::memory_order_release);
            context.condition.notify_all();
            return false;
        }
        if (context.textureWidth != 0 || context.slotsInitializing) return context.textureWidth != 0;
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY) return false;
        context.slotsInitializing = true;
    }
    WorkerContext::Slot temporary[3]{};
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC sharedDesc = source;
    sharedDesc.Format = SharedRenderFormat(source.Format);
    sharedDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    sharedDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    for (WorkerContext::Slot& slot : temporary) {
        if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &sharedDesc,
                D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&slot.sharedTexture12))) ||
            FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator12))) ||
            FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot.allocator12.Get(), nullptr,
                IID_PPV_ARGS(&slot.commandList12)))) {
            {
                std::lock_guard<std::mutex> lock(context.mutex);
                context.slotsInitializing = false;
                context.condition.notify_all();
            }
            for (WorkerContext::Slot& temporarySlot : temporary) ClearD3D12Slot(temporarySlot);
            SetRenderFault(context, "D3D12 shared slot creation failed");
            return false;
        }
        if (FAILED(slot.commandList12->Close()) ||
            FAILED(device->CreateSharedHandle(slot.sharedTexture12.Get(), nullptr, GENERIC_ALL, nullptr, &slot.sharedHandle))) {
            {
                std::lock_guard<std::mutex> lock(context.mutex);
                context.slotsInitializing = false;
                context.condition.notify_all();
            }
            for (WorkerContext::Slot& temporarySlot : temporary) ClearD3D12Slot(temporarySlot);
            SetRenderFault(context, "D3D12 shared handle creation failed");
            return false;
        }
        slot.d3d12Slot = true;
        slot.generation = generation;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY || context.textureWidth != 0) {
            context.slotsInitializing = false;
            context.condition.notify_all();
            for (WorkerContext::Slot& temporarySlot : temporary) ClearD3D12Slot(temporarySlot);
            return context.textureWidth != 0;
        }
        for (uint32_t index = 0; index < 3; ++index) {
            context.slots[index] = std::move(temporary[index]);
            temporary[index].sharedHandle = nullptr;
        }
        context.textureWidth = static_cast<uint32_t>(source.Width);
        context.textureHeight = source.Height;
        context.textureFormat = source.Format;
        context.slotsInitializing = false;
    }
    return true;
}

static void SetRenderFault(WorkerContext& context, const char* message) {
    std::lock_guard<std::mutex> lock(context.mutex);
    context.renderFault = true;
    SetError(context, 9, message);
    context.status.state = LKG_WORKER_FAULT;
    context.closeRequested.store(true, std::memory_order_release);
    context.condition.notify_all();
}

static bool EnsureSharedSlots(WorkerContext& context, ID3D11Device* device, const D3D11_TEXTURE2D_DESC& source) {
    if (!IsValidRenderFormat(source)) {
        char message[256]{};
        std::snprintf(message, sizeof(message), "source texture unsupported width=%u height=%u mip=%u array=%u format=%u(%s) sampleCount=%u sampleQuality=%u",
                      source.Width, source.Height, source.MipLevels, source.ArraySize,
                      static_cast<unsigned>(source.Format), FormatName(source.Format),
                      source.SampleDesc.Count, source.SampleDesc.Quality);
        SetRenderFault(context, message);
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.textureWidth != 0 && (context.textureWidth != source.Width || context.textureHeight != source.Height || context.textureFormat != source.Format)) {
            context.renderFault = true;
            SetError(context, 10, "resize requires reopen");
            context.status.state = LKG_WORKER_FAULT;
            context.closeRequested.store(true, std::memory_order_release);
            context.condition.notify_all();
            return false;
        }
        if (context.textureWidth != 0 || context.slotsInitializing) return context.textureWidth != 0;
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY) return false;
        context.slotsInitializing = true;
    }
    D3D11_TEXTURE2D_DESC desc = source;
    desc.Format = SharedRenderFormat(source.Format);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    WorkerContext::Slot temporary[3]{};
    for (WorkerContext::Slot& slot : temporary) {
        ComPtr<IDXGIResource> resource;
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &slot.producerTexture)) ||
            FAILED(slot.producerTexture.As(&slot.producerMutex)) ||
            FAILED(slot.producerTexture.As(&resource)) ||
            FAILED(resource->GetSharedHandle(&slot.sharedHandle))) {
            {
                std::lock_guard<std::mutex> lock(context.mutex);
                context.slotsInitializing = false;
                context.condition.notify_all();
            }
            SetRenderFault(context, "shared texture creation failed");
            return false;
        }
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY || context.textureWidth != 0) {
            context.slotsInitializing = false;
            context.condition.notify_all();
            return context.textureWidth != 0;
        }
        for (uint32_t index = 0; index < 3; ++index) {
            context.slots[index].producerTexture = std::move(temporary[index].producerTexture);
            context.slots[index].producerMutex = std::move(temporary[index].producerMutex);
            context.slots[index].sharedHandle = temporary[index].sharedHandle;
            temporary[index].sharedHandle = nullptr;
        }
        context.textureWidth = source.Width;
        context.textureHeight = source.Height;
        context.textureFormat = source.Format;
        context.slotsInitializing = false;
    }
    return true;
}

static void WorkerRenderEventD3D12(WorkerContext& context, WorkerDrawData& data, IUnknown* texture) {
    auto abandon = [&]() {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.pendingTokens) --context.pendingTokens;
        ++context.status.dropped;
        context.condition.notify_all();
    };
    ComPtr<ID3D12Resource> source;
    if (texture->QueryInterface(IID_PPV_ARGS(&source)) != S_OK) {
        SetRenderFault(context, "source texture is not a D3D12 resource");
        abandon();
        return;
    }
    const D3D12_RESOURCE_DESC sourceDesc = source->GetDesc();
    ComPtr<ID3D12Device> sourceDevice;
    if (source->GetDevice(IID_PPV_ARGS(&sourceDevice)) != S_OK) {
        SetRenderFault(context, "source D3D12 device unavailable");
        abandon();
        return;
    }
    LkgD3D12Snapshot snapshot{};
    if (lkg_bridge_d3d12_snapshot(&snapshot) != 0 || !snapshot.device || !snapshot.frameFence) {
        SetRenderFault(context, "Unity D3D12 snapshot unavailable");
        abandon();
        return;
    }
    TraceD3D12SourceDesc(sourceDesc, sourceDevice.Get(), snapshot.generation);
    auto releaseSnapshot = [&]() { lkg_bridge_d3d12_snapshot_release(&snapshot); };
    ComPtr<IUnknown> snapshotIdentity;
    ComPtr<IUnknown> sourceIdentity;
    if (snapshot.generation != context.deviceGeneration || snapshot.generation != lkg_bridge_unity_device_generation() ||
        snapshot.device->QueryInterface(IID_PPV_ARGS(&snapshotIdentity)) != S_OK ||
        sourceDevice->QueryInterface(IID_PPV_ARGS(&sourceIdentity)) != S_OK ||
        snapshotIdentity.Get() != sourceIdentity.Get()) {
        releaseSnapshot();
        SetRenderFault(context, "source D3D12 device or generation does not match Unity");
        abandon();
        return;
    }
    if (!EnsureD3D12Slots(context, snapshot.device, sourceDesc, snapshot.generation)) {
        releaseSnapshot();
        abandon();
        return;
    }
    uint32_t selectedSlot = 3;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.status.state == LKG_WORKER_READY && !context.closeRequested.load(std::memory_order_acquire)) {
            for (uint32_t index = 0; index < 3; ++index) {
                if (!context.slots[index].inUse) {
                    selectedSlot = index;
                    context.slots[index].inUse = true;
                    break;
                }
            }
        }
    }
    if (selectedSlot == 3) {
        releaseSnapshot();
        abandon();
        return;
    }
    WorkerContext::Slot& slot = context.slots[selectedSlot];
    if (FAILED(slot.allocator12->Reset()) || FAILED(slot.commandList12->Reset(slot.allocator12.Get(), nullptr))) {
        { std::lock_guard<std::mutex> lock(context.mutex); slot.inUse = false; }
        releaseSnapshot();
        SetRenderFault(context, "D3D12 command allocator reset failed");
        abandon();
        return;
    }
    D3D12_RESOURCE_BARRIER barriers[2]{};
    barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[0].Transition.pResource = slot.sharedTexture12.Get();
    barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    slot.commandList12->ResourceBarrier(1, barriers);
    slot.commandList12->CopyResource(slot.sharedTexture12.Get(), source.Get());
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    slot.commandList12->ResourceBarrier(1, barriers);
    if (FAILED(slot.commandList12->Close())) {
        { std::lock_guard<std::mutex> lock(context.mutex); slot.inUse = false; }
        releaseSnapshot();
        SetRenderFault(context, "D3D12 copy command list close failed");
        abandon();
        return;
    }
    UINT64 fenceValue = 0;
    bool submissionAllowed = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        submissionAllowed = context.status.state == LKG_WORKER_READY && !context.closeRequested.load(std::memory_order_acquire);
        if (submissionAllowed) fenceValue = lkg_bridge_d3d12_submit(slot.commandList12.Get(), source.Get());
    }
    if (!submissionAllowed) {
        { std::lock_guard<std::mutex> lock(context.mutex); slot.inUse = false; }
        releaseSnapshot();
        abandon();
        return;
    }
    if (fenceValue == 0 || fenceValue == UINT64_MAX) {
        slot.sourceTexture12 = source;
        slot.producerFence12 = snapshot.frameFence;
        slot.producerDevice12 = snapshot.device;
        slot.producerFenceValue12 = fenceValue;
        slot.unknownSubmission = true;
        slot.submittedAtTick = GetTickCount64();
        releaseSnapshot();
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            EnqueueJob(context, WorkerContext::Job{ selectedSlot, data.columns, data.rows, data.aspect, data.zoom });
            ++context.status.submitted;
            context.condition.notify_one();
        }
        SetRenderFault(context, "D3D12 submission returned an invalid fence value; submission state unknown");
        return;
    }
    slot.sourceTexture12 = source;
    slot.producerFence12 = snapshot.frameFence;
    slot.producerDevice12 = snapshot.device;
    slot.producerFenceValue12 = fenceValue;
    slot.unknownSubmission = false;
    slot.submittedAtTick = GetTickCount64();
    slot.timedOut = false;
    releaseSnapshot();
    bool submitted = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        WorkerContext::Job job{ selectedSlot, data.columns, data.rows, data.aspect, data.zoom };
        if (EnqueueJob(context, job)) {
            ++context.status.submitted;
            submitted = true;
            context.condition.notify_one();
        }
    }
    if (submitted) {
        TraceWorker("d3d12-render-submit");
        return;
    }
    SetRenderFault(context, "D3D12 submission could not enter worker queue");
}

static void D3D12WorkerJobDeferred(WorkerContext& context, const WorkerContext::Job& job) {
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        RequeueFront(context, job);
        context.inFlightJob = false;
        context.condition.notify_one();
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
}

static void D3D12WorkerJobQuarantine(WorkerContext& context, const WorkerContext::Job& job, const char* message) {
    WorkerContext::Slot& slot = context.slots[job.slot];
    if (!slot.quarantined) {
        slot.quarantined = true;
        SetFault(context, 21, message);
    }
    D3D12WorkerJobDeferred(context, job);
}

static void D3D12WorkerJobRetire(WorkerContext& context, uint32_t slotIndex) {
    WorkerContext::Slot& slot = context.slots[slotIndex];
    slot.sourceTexture12.Reset();
    slot.producerFence12.Reset();
    slot.producerDevice12.Reset();
    slot.producerFenceValue12 = 0;
    slot.unknownSubmission = false;
    slot.timedOut = false;
    slot.consumerTimedOut = false;
    slot.consumerCopyPending = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        slot.inUse = false;
        if (context.pendingTokens) --context.pendingTokens;
        ++context.status.dropped;
        context.inFlightJob = false;
        context.condition.notify_all();
    }
}

static void ProcessD3D12WorkerJob(WorkerContext& context, const WorkerContext::Job& job,
                                  ID3D11Device* workerDevice, ID3D11DeviceContext* workerContext, uint64_t session) {
    WorkerContext::Slot& slot = context.slots[job.slot];
    if (slot.unknownSubmission) {
        const UINT64 completed = D3D12FenceCompletedValue(slot.producerFence12.Get());
        const HRESULT removedReason = slot.producerDevice12 ? slot.producerDevice12->GetDeviceRemovedReason() : E_FAIL;
        if (completed == UINT64_MAX || FAILED(removedReason)) {
            if (!slot.quarantined) {
                slot.quarantined = true;
                SetFault(context, 21, "D3D12 unknown submission retired after confirmed device removal");
            }
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        if (!slot.timedOut && GetTickCount64() - slot.submittedAtTick > context.d3d12FenceTimeoutMs.load(std::memory_order_acquire)) {
            slot.timedOut = true;
            D3D12WorkerJobQuarantine(context, job, "D3D12 submission state remains unknown; restart required");
            return;
        }
        D3D12WorkerJobDeferred(context, job);
        return;
    }
    if (!slot.consumerCopyPending) {
        if (!slot.producerFence12 || slot.producerFenceValue12 == 0 || slot.producerFenceValue12 == UINT64_MAX) {
            slot.unknownSubmission = true;
            D3D12WorkerJobQuarantine(context, job, "D3D12 producer fence value is invalid; submission state unknown");
            return;
        }
        const UINT64 completed = D3D12FenceCompletedValue(slot.producerFence12.Get());
        if (completed == UINT64_MAX) {
            if (!slot.quarantined) {
                slot.quarantined = true;
                SetFault(context, 21, "D3D12 producer fence reports device removal");
            }
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        if (completed < slot.producerFenceValue12) {
            if (GetTickCount64() - slot.submittedAtTick > context.d3d12FenceTimeoutMs.load(std::memory_order_acquire) && !slot.timedOut) {
                slot.timedOut = true;
                SetFault(context, 22, "D3D12 producer fence timed out; slot quarantined");
            }
            D3D12WorkerJobDeferred(context, job);
            return;
        }
        if (slot.timedOut) {
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        slot.sourceTexture12.Reset();
        slot.producerFence12.Reset();
        slot.producerDevice12.Reset();
        slot.producerFenceValue12 = 0;
    }
    if (!slot.workerTexture) {
        ComPtr<ID3D11Device1> workerDevice1;
        HRESULT openResult = slot.sharedHandle ? workerDevice->QueryInterface(IID_PPV_ARGS(&workerDevice1)) : E_HANDLE;
        if (SUCCEEDED(openResult)) openResult = workerDevice1->OpenSharedResource1(slot.sharedHandle, IID_PPV_ARGS(&slot.workerTexture));
        if (FAILED(openResult)) {
            char message[128]{};
            std::snprintf(message, sizeof(message), "D3D12 NT shared resource open failed hr=0x%08lx", static_cast<unsigned long>(openResult));
            SetFault(context, 23, message);
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        CloseHandle(slot.sharedHandle);
        slot.sharedHandle = nullptr;
        D3D11_TEXTURE2D_DESC workerDesc{};
        workerDesc.Width = context.textureWidth;
        workerDesc.Height = context.textureHeight;
        workerDesc.MipLevels = 1;
        workerDesc.ArraySize = 1;
        workerDesc.Format = SharedRenderFormat(context.textureFormat);
        workerDesc.SampleDesc.Count = 1;
        workerDesc.Usage = D3D11_USAGE_DEFAULT;
        workerDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        if (FAILED(workerDevice->CreateTexture2D(&workerDesc, nullptr, &slot.producerTexture))) {
            SetFault(context, 24, "D3D12 private D3D11 texture creation failed");
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        D3D11_QUERY_DESC queryDesc{ D3D11_QUERY_EVENT, 0 };
        if (FAILED(workerDevice->CreateQuery(&queryDesc, &slot.consumerQuery11))) {
            SetFault(context, 25, "D3D11 consumer query creation failed");
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
    }
    if (!slot.consumerCopyPending) {
        workerContext->CopyResource(slot.producerTexture.Get(), slot.workerTexture.Get());
        workerContext->End(slot.consumerQuery11.Get());
        workerContext->Flush();
        slot.consumerCopyPending = true;
        slot.consumerCopyStartedTick = GetTickCount64();
    }
    if (!slot.consumerTimedOut && GetTickCount64() - slot.consumerCopyStartedTick > context.d3d12FenceTimeoutMs.load(std::memory_order_acquire)) {
        slot.consumerTimedOut = true;
        D3D12WorkerJobQuarantine(context, job, "D3D11 consumer copy timed out; slot quarantined");
        return;
    }
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    uint32_t injectedPolls = testD3D12ConsumerPendingPolls.load(std::memory_order_acquire);
    while (injectedPolls != 0 && !testD3D12ConsumerPendingPolls.compare_exchange_weak(injectedPolls, injectedPolls - 1, std::memory_order_acq_rel)) {}
    if (injectedPolls != 0) {
        D3D12WorkerJobDeferred(context, job);
        return;
    }
#endif
    BOOL consumerComplete = FALSE;
    const HRESULT queryResult = workerContext->GetData(slot.consumerQuery11.Get(), &consumerComplete, sizeof(consumerComplete), D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (queryResult == S_FALSE || (SUCCEEDED(queryResult) && !consumerComplete)) {
        D3D12WorkerJobDeferred(context, job);
        return;
    }
    if (FAILED(queryResult)) {
        const HRESULT removedReason = workerDevice ? workerDevice->GetDeviceRemovedReason() : E_FAIL;
        if (FAILED(removedReason)) {
            slot.consumerCopyPending = false;
            slot.quarantined = true;
            SetFault(context, 28, "D3D11 consumer device removal confirmed; slot retired");
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        if (!slot.quarantined) {
            slot.quarantined = true;
            slot.consumerTimedOut = true;
            SetFault(context, 26, "D3D11 consumer query failed; waiting for safe retirement");
        }
        D3D12WorkerJobDeferred(context, job);
        return;
    }
    slot.consumerCopyPending = false;
    if (slot.consumerTimedOut || slot.quarantined) {
        D3D12WorkerJobRetire(context, job.slot);
        return;
    }
    int32_t drawResult = -1;
    try {
        drawResult = lkg_bridge_smoke_draw(session, slot.producerTexture.Get(), job.columns, job.rows, job.aspect, job.zoom);
    } catch (...) {
        SetFault(context, 27, "D3D12 Bridge draw threw after consumer copy completed");
        D3D12WorkerJobRetire(context, job.slot);
        return;
    }
    if (drawResult != 0) SetBridgeFault(context, 13, "bridge_draw", drawResult);
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        slot.inUse = false;
        if (context.pendingTokens) --context.pendingTokens;
        context.inFlightJob = false;
        if (drawResult == 0) ++context.status.completed;
        else ++context.status.dropped;
        context.condition.notify_all();
    }
    TraceWorker(drawResult == 0 ? "d3d12-draw-complete" : "d3d12-draw-drop");
}

static void ClearD3D12Slot(WorkerContext::Slot& slot) {
    slot.sourceTexture12.Reset();
    slot.producerFence12.Reset();
    slot.producerDevice12.Reset();
    slot.sharedTexture12.Reset();
    slot.allocator12.Reset();
    slot.commandList12.Reset();
    slot.consumerQuery11.Reset();
    slot.workerTexture.Reset();
    slot.producerTexture.Reset();
    slot.producerMutex.Reset();
    slot.workerMutex.Reset();
    if (slot.sharedHandle) {
        CloseHandle(slot.sharedHandle);
        slot.sharedHandle = nullptr;
    }
    slot.producerFenceValue12 = 0;
    slot.unknownSubmission = false;
    slot.timedOut = false;
    slot.consumerTimedOut = false;
    slot.consumerCopyStartedTick = 0;
    slot.quarantined = false;
    slot.consumerCopyPending = false;
    slot.d3d12Slot = false;
    slot.generation = 0;
    slot.inUse = false;
}

static void WorkerRenderEventImpl(int, WorkerDrawData* data) {
    if (!data || !data->context || !data->texture) return;
    WorkerContext& context = *data->context;
    auto abandon = [&]() {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.pendingTokens) --context.pendingTokens;
        ++context.status.dropped;
        context.condition.notify_all();
    };
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
                if (!context.slots[index].inUse) {
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
    slot.producerMutex->ReleaseSync(1);
    bool rejectSubmission = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        rejectSubmission = context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY;
        if (rejectSubmission) {
            slot.inUse = false;
        } else {
            if (!EnqueueJob(context, WorkerContext::Job{ selectedSlot, data->columns, data->rows, data->aspect, data->zoom })) {
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

static void RunWorker(WorkerContext* context) {
    lkg_bridge_worker_enter();
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    if (!CreateWorkerTestWindow(*context)) {
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
    bool loaded = false;
    bool initialized = false;
    const char* runtimePath = context->runtimePath.c_str();

    auto cleanup = [&]() {
        const uint64_t cleanupSessionId = session;
        TraceWorkerShutdownEvent("cleanup-begin", cleanupSessionId);
        if (session != 0) {
            void* closeData = lkg_bridge_make_close_data(session);
            auto callback = reinterpret_cast<void (*)(int, void*)>(lkg_bridge_render_event_func());
            if (closeData && callback) callback(1, closeData);
            lkg_bridge_destroy_session(session);
            session = 0;
        }
        if (initialized) {
            lkg_bridge_uninitialize();
            initialized = false;
        }
        if (loaded) {
            lkg_bridge_unload();
            loaded = false;
        }
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
#if defined(LKG_WORKER_TRANSPORT_TESTING)
        DestroyWorkerTestWindow(*context);
#endif
        WorkerShutdownSnapshot cleanupEndSnapshot{};
        {
            std::lock_guard<std::mutex> lock(context->mutex);
            if (context->status.state != LKG_WORKER_FAULT) context->status.state = LKG_WORKER_CLOSED;
            if (NativeTraceEnabled())
                cleanupEndSnapshot = CaptureWorkerShutdownSnapshot(*context, cleanupSessionId);
        }
        TraceWorkerShutdownSnapshot("cleanup-end", cleanupEndSnapshot);
    };
    auto finish = [&]() noexcept {
        try {
            cleanup();
        } catch (...) {
            try {
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
    const int32_t initializedResult = lkg_bridge_is_initialized();
    if (initializedResult != 0) {
        SetBridgeFault(*context, 1, "bridge_is_initialized", initializedResult);
        finish();
        return;
    }
    const int32_t loadResult = lkg_bridge_load(runtimePath);
    if (loadResult != 0) {
        SetBridgeFault(*context, 2, "bridge_load", loadResult);
        finish();
        return;
    }
    loaded = true;
    const int32_t initializeResult = lkg_bridge_initialize(context->applicationName.c_str());
    if (initializeResult != 0) {
        SetBridgeFault(*context, 3, "bridge_initialize", initializeResult);
        finish();
        return;
    }
    initialized = true;
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
    {
        D3D_FEATURE_LEVEL selected{};
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        if (D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION, &workerDevice, &selected, nullptr) != S_OK) {
            SetFault(*context, 5, "worker D3D11 device creation failed");
            finish();
            return;
        }
        workerDevice->GetImmediateContext(&workerDeviceContext);
        if (!workerDeviceContext) {
            SetFault(*context, 5, "worker D3D11 immediate context unavailable");
            finish();
            return;
        }
    }
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
        session = lkg_bridge_create_d3d11_session(workerDevice.Get(), display.index);
        if (session == 0) {
            SetBridgeFault(*context, 7, "bridge_create_session", -1);
            finish();
            return;
        }
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
        readyPublished = context->status.state == LKG_WORKER_READY;
        }
        if (readyPublished) TraceWorker("ready");
    }
    while (true) {
        PumpWorkerMessages(*context);
        WorkerContext::Job job{};
        {
            std::unique_lock<std::mutex> lock(context->mutex);
            context->condition.wait_for(lock, std::chrono::milliseconds(10), [&] { return context->jobCount != 0 || (context->closeRequested.load(std::memory_order_acquire) && context->pendingTokens == 0); });
            if (context->jobCount == 0 && context->closeRequested.load(std::memory_order_acquire) && context->pendingTokens == 0) break;
            if (!PopJob(*context, job)) continue;
            context->inFlightJob = true;
            context->inFlightSlot = job.slot;
        }
        WorkerContext::Slot* slot = &context->slots[job.slot];
        if (slot->d3d12Slot) {
            ProcessD3D12WorkerJob(*context, job, workerDevice.Get(), workerDeviceContext.Get(), session);
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
        const int32_t drawResult = lkg_bridge_smoke_draw(session, slot->workerTexture.Get(), job.columns, job.rows, job.aspect, job.zoom);
        slot->workerMutex->ReleaseSync(0);
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
            const bool d3d12Pending = context->slots[context->inFlightSlot].d3d12Slot;
            if (!d3d12Pending) context->slots[context->inFlightSlot].inUse = false;
            context->inFlightJob = false;
            if (!d3d12Pending) {
                if (context->pendingTokens) --context->pendingTokens;
                ++context->status.dropped;
            }
        }
        while (context->jobCount != 0) {
            WorkerContext::Job droppedJob{};
            PopJob(*context, droppedJob);
            if (!context->slots[droppedJob.slot].d3d12Slot) {
                context->slots[droppedJob.slot].inUse = false;
                if (context->pendingTokens) --context->pendingTokens;
                ++context->status.dropped;
            }
        }
        for (WorkerContext::Slot& slot : context->slots) if (!slot.d3d12Slot) slot.inUse = false;
        context->condition.notify_all();
        context->condition.wait(lock, [&] { return context->pendingTokens == 0 && context->jobCount == 0; });
        lock.unlock();
        finish();
    }
}

LKG_EXPORT void* lkg_worker_begin(const char* runtimePath, const char* applicationName, uint32_t displayOrdinal, void* producerDevice) {
    if (!runtimePath || !runtimePath[0] || !applicationName || !applicationName[0]) return nullptr;
    if (!lkg_bridge_worker_claim()) return nullptr;
    WorkerContext* context = nullptr;
    try {
        context = new WorkerContext();
        context->runtimePath = runtimePath;
        for (char& character : context->runtimePath) if (character == '/') character = '\\';
        context->applicationName = applicationName;
        context->displayOrdinal = displayOrdinal;
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
        ComPtr<ID3D12Device> producer12;
        context->producerD3D12 = context->producerDevice->QueryInterface(IID_PPV_ARGS(&producer12)) == S_OK;
        if (!context->producerD3D12) context->deviceGeneration = 0;
        context->status.state = LKG_WORKER_CONNECTING;
        {
            std::lock_guard<std::mutex> lock(registryMutex);
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
        delete context;
        lkg_bridge_worker_release_claim();
        return nullptr;
    }
    return context;
}

#if defined(LKG_WORKER_TRANSPORT_TESTING)
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
    auto* context = static_cast<WorkerContext*>(handle);
    return context ? context->testThreadId : 0;
}

LKG_EXPORT HWND lkg_worker_test_window(void* handle) {
    auto* context = static_cast<WorkerContext*>(handle);
    return context ? context->testWindow : nullptr;
}

LKG_EXPORT uint32_t lkg_worker_test_message_count(void* handle) {
    auto* context = static_cast<WorkerContext*>(handle);
    return context ? context->testMessageCount.load(std::memory_order_relaxed) : 0;
}
#endif

LKG_EXPORT int32_t lkg_worker_poll(void* handle, LkgWorkerStatus* status) {
    if (!handle || !status) return -1;
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (registry.find(context) == registry.end()) return -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    *status = context->status;
    return 0;
}

LKG_EXPORT int32_t lkg_worker_close(void* handle) {
    if (!handle) return -1;
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (registry.find(context) == registry.end()) return -2;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state == LKG_WORKER_CLOSED || context->status.state == LKG_WORKER_FAULT) return 0;
    context->closeRequested.store(true, std::memory_order_release);
    if (context->status.state == LKG_WORKER_CONNECTING || context->status.state == LKG_WORKER_READY) context->status.state = LKG_WORKER_CLOSING;
    context->condition.notify_all();
    return 0;
}

LKG_EXPORT int32_t lkg_worker_reset_transfer(void* handle) {
    if (!handle) return -1;
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (registry.find(context) == registry.end()) return -1;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state != LKG_WORKER_READY || context->closeRequested.load(std::memory_order_acquire)) return -3;
    if (context->slotsInitializing || context->pendingTokens != 0 || context->jobCount != 0 || context->inFlightJob) return -2;
    for (const WorkerContext::Slot& slot : context->slots) {
        if (slot.inUse) return -2;
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

LKG_EXPORT void* lkg_worker_make_draw_data(void* handle, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom) {
    if (!handle || !texture || columns == 0 || rows == 0 || !std::isfinite(aspect) || !std::isfinite(zoom) || aspect <= 0.0f || zoom <= 0.0f) return nullptr;
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (registry.find(context) == registry.end()) return nullptr;
    std::lock_guard<std::mutex> lock(context->mutex);
    if (context->status.state != LKG_WORKER_READY || context->closeRequested.load(std::memory_order_acquire) || context->pendingTokens >= 3) {
        ++context->status.dropped;
        return nullptr;
    }
    auto data = std::unique_ptr<WorkerDrawData>(new (std::nothrow) WorkerDrawData{ 0, context, reinterpret_cast<IUnknown*>(texture), columns, rows, aspect, zoom });
    if (!data) return nullptr;
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
        for (WorkerContext* context : registry) {
            if (!context) continue;
            std::lock_guard<std::mutex> lock(context->mutex);
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
    for (WorkerContext* context : registry) {
        if (!context) continue;
        std::lock_guard<std::mutex> lock(context->mutex);
        const bool closing = context->closeRequested.load(std::memory_order_acquire) ||
            context->status.state == LKG_WORKER_CLOSING || context->status.state == LKG_WORKER_CLOSED ||
            context->status.state == LKG_WORKER_FAULT;
        if (closing) ++pending;
    }
    return pending;
}

LKG_EXPORT int32_t lkg_worker_collect_closed(void) {
    std::vector<WorkerContext*> reclaim;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        for (auto it = registry.begin(); it != registry.end();) {
            WorkerContext* context = *it;
            bool finished = false;
            bool idleHost = true;
            {
                std::lock_guard<std::mutex> lock(context->mutex);
                finished = context->finished.load(std::memory_order_acquire) && context->pendingTokens == 0 && context->jobCount == 0 && !context->inFlightJob;
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
                }
            }
            if (finished && idleHost) {
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
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (registry.find(context) == registry.end()) return -2;
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        if (!context->finished.load(std::memory_order_acquire)) return -2;
    }
    if (context->thread.joinable()) context->thread.join();
#if defined(_WIN32)
    if (context->moduleReference) FreeLibrary(context->moduleReference);
#endif
    registry.erase(context);
    delete context;
    return 0;
}
