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
extern "C" int32_t lkg_bridge_sdk_shutdown_faulted(void);
extern "C" int32_t lkg_bridge_sdk_shutdown_incomplete(void);
extern "C" int32_t lkg_bridge_close_sdk_admission(void);

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
static bool finalShutdownRequested = false;
static std::atomic<bool> finalShutdownAdmissionClosed{false};
static std::atomic<uint32_t> finalShutdownState{LKG_WORKER_FINAL_SHUTDOWN_NONE};
static std::atomic<int32_t> finalShutdownLastResult{0};
static std::mutex hostRegistryMutex;
static WorkerHost* persistentHost = nullptr;
#if defined(LKG_WORKER_TRANSPORT_TESTING)
static std::atomic<bool> testThrowDuringWorkerCleanup{false};
#endif

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
    if (finalShutdownAdmissionClosed.load(std::memory_order_acquire)) return false;
    WorkerHost* host = GetPersistentHost();
    if (!host) return false;
    std::lock_guard<std::mutex> lock(host->mutex);
    if (finalShutdownAdmissionClosed.load(std::memory_order_acquire) || host->stopRequested || host->queued || host->active) return false;
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
                cleanupProven = queryResult == 0 && closeStatus.state == LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE;
                if (cleanupProven) cleanupProven = lkg_bridge_ack_session_close_v1(session) == 0;
                TraceWorkerShutdownEvent(cleanupProven ? "session-close-safe-acked" : "session-close-not-proven-retained",
                    cleanupSessionId, static_cast<int>(closeStatus.state));
            } else {
                cleanupProven = false;
                TraceWorkerShutdownEvent("session-close-callback-unavailable-retained", cleanupSessionId);
            }
            session = 0;
        }
        if (initialized && cleanupProven) {
            lkg_bridge_uninitialize();
            initialized = false;
            if (lkg_bridge_sdk_shutdown_faulted() != 0) cleanupProven = false;
        }
        if (lkg_bridge_sdk_shutdown_faulted() != 0) cleanupProven = false;
        if (loaded && cleanupProven) {
            lkg_bridge_unload();
            loaded = false;
        }
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

#if defined(LKG_WORKER_TRANSPORT_TESTING)
LKG_EXPORT void lkg_worker_test_throw_during_cleanup(void) {
    testThrowDuringWorkerCleanup.store(true, std::memory_order_release);
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
    if (!handle || !texture) return nullptr;
    lkg::common::ImageDescriptor request{};
    request.columns = columns;
    request.rows = rows;
    request.aspect = aspect;
    request.zoom = zoom;
    if (lkg::common::ValidateDrawRequest(request) != lkg::common::ValidationResult::Valid) return nullptr;
    auto* context = static_cast<WorkerContext*>(handle);
    std::lock_guard<std::mutex> registryLock(registryMutex);
    if (finalShutdownRequested || registry.find(context) == registry.end()) return nullptr;
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

LKG_EXPORT int32_t lkg_worker_request_final_shutdown(void) {
    if (lkg_bridge_close_sdk_admission() != 0) return -2;
    {
        std::lock_guard<std::mutex> registryLock(registryMutex);
        if (finalShutdownRequested) return 0;
        finalShutdownRequested = true;
        finalShutdownAdmissionClosed.store(true, std::memory_order_release);
        finalShutdownState.store(LKG_WORKER_FINAL_SHUTDOWN_DRAINING, std::memory_order_release);
        finalShutdownLastResult.store(0, std::memory_order_release);
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
    {
        std::lock_guard<std::mutex> hostRegistryLock(hostRegistryMutex);
        host = persistentHost;
    }
    if (host) {
        std::lock_guard<std::mutex> hostLock(host->mutex);
        hostJoined = host->joined;
        hostRunning = !host->stopped && !host->joined;
    }

    const bool sdkFaulted = lkg_bridge_sdk_shutdown_faulted() != 0;
    const bool sdkIncomplete = lkg_bridge_sdk_shutdown_incomplete() != 0;
    uint32_t state = finalShutdownState.load(std::memory_order_acquire);
    int32_t lastResult = finalShutdownLastResult.load(std::memory_order_acquire);
    if (sdkFaulted) {
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
            {
                std::lock_guard<std::mutex> lock(context->mutex);
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
        if (!context->cleanupProven || !context->finished.load(std::memory_order_acquire)) return -2;
    }
    if (context->thread.joinable()) context->thread.join();
#if defined(_WIN32)
    if (context->moduleReference) FreeLibrary(context->moduleReference);
#endif
    registry.erase(context);
    delete context;
    return 0;
}
