#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <algorithm>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "bridge_shim.h"
#include "bridge_worker_transport.h"
#include "bridge_shutdown_contract.h"

namespace {
ID3D11Device* g_device = nullptr;
IUnityGraphicsDeviceEventCallback g_deviceCallback = nullptr;
uint32_t g_reservedEventCount = 0;
uint32_t g_registerCallbackCount = 0;
uint32_t g_unregisterCallbackCount = 0;
extern IUnityGraphics g_graphics;
extern IUnityGraphicsD3D11 g_graphicsD3D11;

struct ShimApi {
    HMODULE module = nullptr;
    using PluginLoadFn = void (UNITY_INTERFACE_API*)(IUnityInterfaces*);
    using PluginUnloadFn = void (UNITY_INTERFACE_API*)();
    PluginLoadFn pluginLoad = nullptr;
    PluginUnloadFn pluginUnload = nullptr;
};

struct FakeApi {
    HMODULE module = nullptr;
    LkgFakeBridgeResetFn reset = nullptr;
    LkgFakeBridgeSetDrawModeFn setDrawMode = nullptr;
    LkgFakeBridgeWaitDrawEnteredFn waitDrawEntered = nullptr;
    LkgFakeBridgeReleaseDrawFn releaseDraw = nullptr;
    LkgFakeBridgeGetEventsFn getEvents = nullptr;
};

struct WorkerApi {
    using BeginFn = void* (*)(const char*, const char*, uint32_t, void*);
    using PollFn = int32_t (*)(void*, LkgWorkerStatus*);
    using CloseFn = int32_t (*)(void*);
    using ReleaseFn = int32_t (*)(void*);
    using MakeDrawFn = void* (*)(void*, void*, uint32_t, uint32_t, float, float);
    using CancelDrawFn = int32_t (*)(void*);
    using RenderEventFn = void* (*)();
    using RenderEventIdFn = int32_t (*)(int32_t*);
    using PendingCountFn = uint32_t (*)();
    using CloseAllFn = int32_t (*)();
    using ShutdownHostFn = int32_t (*)(uint32_t);
    using CollectClosedFn = int32_t (*)();
    BeginFn begin = nullptr;
    PollFn poll = nullptr;
    CloseFn close = nullptr;
    ReleaseFn release = nullptr;
    MakeDrawFn makeDraw = nullptr;
    CancelDrawFn cancelDraw = nullptr;
    RenderEventFn renderEvent = nullptr;
    RenderEventIdFn renderEventId = nullptr;
    PendingCountFn pendingCount = nullptr;
    CloseAllFn closeAll = nullptr;
    ShutdownHostFn shutdownHost = nullptr;
    CollectClosedFn collectClosed = nullptr;
};

WorkerApi g_worker{};
std::vector<void*> g_liveWorkers;
std::vector<void*> g_liveTokens;
uint64_t g_harnessThread = 0;

static IUnityInterface* UNITY_INTERFACE_API GetInterface(UnityInterfaceGUID guid) {
    if (guid == GetUnityInterfaceGUID<IUnityGraphics>()) return reinterpret_cast<IUnityInterface*>(&g_graphics);
    if (guid == GetUnityInterfaceGUID<IUnityGraphicsD3D11>()) return reinterpret_cast<IUnityInterface*>(&g_graphicsD3D11);
    return nullptr;
}

static void UNITY_INTERFACE_API RegisterInterface(UnityInterfaceGUID, IUnityInterface*) {}

static IUnityInterface* UNITY_INTERFACE_API GetInterfaceSplit(unsigned long long high, unsigned long long low) {
    return GetInterface(UnityInterfaceGUID(high, low));
}

static void UNITY_INTERFACE_API RegisterInterfaceSplit(unsigned long long, unsigned long long, IUnityInterface*) {}

static UnityGfxRenderer UNITY_INTERFACE_API GetRenderer() { return kUnityGfxRendererD3D11; }

static void UNITY_INTERFACE_API RegisterDeviceEventCallback(IUnityGraphicsDeviceEventCallback callback) {
    ++g_registerCallbackCount;
    g_deviceCallback = callback;
}

static void UNITY_INTERFACE_API UnregisterDeviceEventCallback(IUnityGraphicsDeviceEventCallback callback) {
    ++g_unregisterCallbackCount;
    if (g_deviceCallback == callback) g_deviceCallback = nullptr;
}

static int UNITY_INTERFACE_API ReserveEventIDRange(int count) {
    g_reservedEventCount = count > 0 ? static_cast<uint32_t>(count) : 0;
    return 100;
}

static ID3D11Device* UNITY_INTERFACE_API GetDevice() { return g_device; }

IUnityGraphics g_graphics{{}, GetRenderer, RegisterDeviceEventCallback, UnregisterDeviceEventCallback, ReserveEventIDRange};
IUnityGraphicsD3D11 g_graphicsD3D11 = [] { IUnityGraphicsD3D11 value{}; value.GetDevice = GetDevice; return value; }();
static IUnityInterfaces g_interfaces{GetInterface, RegisterInterface, GetInterfaceSplit, RegisterInterfaceSplit};

static bool Check(bool passed, const char* message) {
    if (passed) return true;
    std::fprintf(stderr, "FAIL: %s\n", message);
    return false;
}

static std::string FullPath(const char* path) {
    char buffer[MAX_PATH]{};
    const DWORD length = GetFullPathNameA(path, MAX_PATH, buffer, nullptr);
    return length && length < MAX_PATH ? std::string(buffer, length) : std::string();
}

template<typename T>
static T LoadSymbol(HMODULE module, const char* name) {
    return reinterpret_cast<T>(GetProcAddress(module, name));
}

static bool LoadFakeApi(const char* path, FakeApi* fake) {
    fake->module = LoadLibraryExA(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!fake->module) return false;
    fake->reset = LoadSymbol<LkgFakeBridgeResetFn>(fake->module, "lkg_fake_bridge_reset");
    fake->setDrawMode = LoadSymbol<LkgFakeBridgeSetDrawModeFn>(fake->module, "lkg_fake_bridge_set_draw_mode");
    fake->waitDrawEntered = LoadSymbol<LkgFakeBridgeWaitDrawEnteredFn>(fake->module, "lkg_fake_bridge_wait_draw_entered");
    fake->releaseDraw = LoadSymbol<LkgFakeBridgeReleaseDrawFn>(fake->module, "lkg_fake_bridge_release_draw");
    fake->getEvents = LoadSymbol<LkgFakeBridgeGetEventsFn>(fake->module, "lkg_fake_bridge_get_events");
    return fake->reset && fake->setDrawMode && fake->waitDrawEntered && fake->releaseDraw && fake->getEvents;
}

static std::vector<LkgFakeBridgeEvent> Events(const FakeApi& fake) {
    const uint32_t count = fake.getEvents(nullptr, 0);
    std::vector<LkgFakeBridgeEvent> events(count);
    if (count) fake.getEvents(events.data(), count);
    return events;
}

static uint32_t Count(const std::vector<LkgFakeBridgeEvent>& events, uint64_t generation, uint32_t type) {
    return static_cast<uint32_t>(std::count_if(events.begin(), events.end(), [=](const LkgFakeBridgeEvent& event) {
        return event.generation == generation && event.type == type;
    }));
}

static const LkgFakeBridgeEvent* First(const std::vector<LkgFakeBridgeEvent>& events, uint64_t generation, uint32_t type) {
    const auto found = std::find_if(events.begin(), events.end(), [=](const LkgFakeBridgeEvent& event) {
        return event.generation == generation && event.type == type;
    });
    return found == events.end() ? nullptr : &*found;
}

static bool WaitForState(void* handle, uint32_t desiredState, uint32_t timeoutMs, LkgWorkerStatus* finalStatus) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    do {
        LkgWorkerStatus status{};
        if (g_worker.poll(handle, &status) != 0) return false;
        if (finalStatus) *finalStatus = status;
        if (status.state == desiredState) return true;
        if (status.state == LKG_WORKER_FAULT && desiredState != LKG_WORKER_FAULT) return false;
        Sleep(2);
    } while (GetTickCount64() < deadline);
    return false;
}

static bool WaitForRelease(void* handle, uint32_t timeoutMs) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    do {
        const int32_t result = g_worker.release(handle);
        if (result == 0) {
            g_liveWorkers.erase(std::remove(g_liveWorkers.begin(), g_liveWorkers.end(), handle), g_liveWorkers.end());
            return true;
        }
        if (result != -2) return false;
        Sleep(2);
    } while (GetTickCount64() < deadline);
    return false;
}

static bool WaitForCompleted(void* handle, uint32_t completed, uint32_t timeoutMs, LkgWorkerStatus* finalStatus) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    do {
        LkgWorkerStatus status{};
        if (g_worker.poll(handle, &status) != 0) return false;
        if (finalStatus) *finalStatus = status;
        if (status.completed >= completed || status.state == LKG_WORKER_FAULT) return status.completed >= completed;
        Sleep(2);
    } while (GetTickCount64() < deadline);
    return false;
}

static void* StartWorker(const std::string& fakePath, const char* appName) {
    void* worker = g_worker.begin(fakePath.c_str(), appName, 0, g_device);
    if (worker) g_liveWorkers.push_back(worker);
    return worker;
}

static bool SubmitDraw(void* worker, ID3D11Texture2D* source, int eventId) {
    void* token = g_worker.makeDraw(worker, source, 1, 1, 1.0f, 1.0f);
    if (!token) return false;
    g_liveTokens.push_back(token);
    auto callback = reinterpret_cast<UnityRenderingEventAndData>(g_worker.renderEvent());
    if (!callback) {
        g_worker.cancelDraw(token);
        g_liveTokens.erase(std::remove(g_liveTokens.begin(), g_liveTokens.end(), token), g_liveTokens.end());
        return false;
    }
    callback(eventId, token);
    g_liveTokens.erase(std::remove(g_liveTokens.begin(), g_liveTokens.end(), token), g_liveTokens.end());
    return true;
}

static bool WaitAndClose(void* worker, bool duplicateClose, LkgWorkerStatus* status) {
    if (g_worker.close(worker) != 0) return false;
    if (duplicateClose && g_worker.close(worker) != 0) return false;
    if (!WaitForState(worker, LKG_WORKER_CLOSED, 5000, status)) return false;
    return WaitForRelease(worker, 5000);
}

static bool WaitForFaultDrain(void* worker, uint32_t timeoutMs, LkgWorkerStatus* finalStatus) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    do {
        LkgWorkerStatus status{};
        if (g_worker.poll(worker, &status) != 0) return false;
        if (finalStatus) *finalStatus = status;
        if (status.state == LKG_WORKER_FAULT && status.dropped > 0) return true;
        Sleep(2);
    } while (GetTickCount64() < deadline);
    return false;
}

static bool DrainAfterFailure(FakeApi& fake) {
    fake.releaseDraw();
    for (void* token : g_liveTokens) g_worker.cancelDraw(token);
    g_liveTokens.clear();
    g_worker.closeAll();
    const ULONGLONG deadline = GetTickCount64() + 8000;
    while (GetTickCount64() < deadline && !g_liveWorkers.empty()) {
        for (auto it = g_liveWorkers.begin(); it != g_liveWorkers.end();) {
            const int32_t result = g_worker.release(*it);
            if (result == 0) it = g_liveWorkers.erase(it);
            else if (result == -2) ++it;
            else return false;
        }
        Sleep(2);
    }
    if (!g_liveWorkers.empty() || g_worker.pendingCount() != 0) return false;
    return g_worker.shutdownHost(5000) == 0;
}

static bool CheckBaseLifecycle(const std::vector<LkgFakeBridgeEvent>& events, uint64_t generation, bool hadDraw, bool drawShouldSucceed = true) {
    bool ok = true;
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_INITIALIZE) == 1, "initialize should be called once");
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_UNREGISTER_TEXTURE) == (hadDraw ? 1u : 0u), "unregister count should match texture registration");
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_RELEASE_TEXTURE) == (hadDraw ? 1u : 0u), "release texture count should match texture creation");
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_RELEASE_DEVICE) == 1, "shim device reference should be released once");
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_UNINITIALIZE) == 1, "uninitialize should be called once");
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_REGISTER_TEXTURE) == (hadDraw ? 1u : 0u), "register count should match texture registration");
    ok &= Check(Count(events, generation, LKG_FAKE_EVENT_CREATE_TEXTURE) == (hadDraw ? 1u : 0u), "create texture count should match source texture creation");
    const LkgFakeBridgeEvent* unregisterEvent = First(events, generation, LKG_FAKE_EVENT_UNREGISTER_TEXTURE);
    const LkgFakeBridgeEvent* releaseTextureEvent = First(events, generation, LKG_FAKE_EVENT_RELEASE_TEXTURE);
    const LkgFakeBridgeEvent* releaseDeviceEvent = First(events, generation, LKG_FAKE_EVENT_RELEASE_DEVICE);
    const LkgFakeBridgeEvent* uninitializeEvent = First(events, generation, LKG_FAKE_EVENT_UNINITIALIZE);
    const LkgFakeBridgeEvent* createTextureEvent = First(events, generation, LKG_FAKE_EVENT_CREATE_TEXTURE);
    const LkgFakeBridgeEvent* registerTextureEvent = First(events, generation, LKG_FAKE_EVENT_REGISTER_TEXTURE);
    const LkgFakeBridgeEvent* drawEnterEvent = First(events, generation, LKG_FAKE_EVENT_DRAW_ENTER);
    const LkgFakeBridgeEvent* drawExitEvent = First(events, generation, LKG_FAKE_EVENT_DRAW_EXIT);
    const LkgFakeBridgeEvent* instanceEvent = First(events, generation, LKG_FAKE_EVENT_INSTANCE_WINDOW);
    if (releaseDeviceEvent && uninitializeEvent)
        ok &= Check(releaseDeviceEvent->sequence < uninitializeEvent->sequence, "device release must precede uninitialize");
    if (releaseDeviceEvent) ok &= Check(releaseDeviceEvent->result == 1, "shim-held device reference release should succeed");
    if (uninitializeEvent) ok &= Check(uninitializeEvent->result == 1 && uninitializeEvent->pointerValue != 0,
        "fake-owned device reference should be released once during uninitialize");
    if (hadDraw && unregisterEvent && releaseTextureEvent && releaseDeviceEvent && uninitializeEvent) {
        ok &= Check(unregisterEvent->sequence < releaseTextureEvent->sequence, "unregister must precede texture release");
        ok &= Check(releaseTextureEvent->sequence < releaseDeviceEvent->sequence, "texture release must precede device release");
        ok &= Check(createTextureEvent && registerTextureEvent && createTextureEvent->pointerValue == registerTextureEvent->pointerValue &&
            registerTextureEvent->pointerValue == unregisterEvent->pointerValue && unregisterEvent->pointerValue == releaseTextureEvent->pointerValue,
            "create, register, unregister, and release must refer to one texture");
        ok &= Check(drawEnterEvent && drawExitEvent && Count(events, generation, LKG_FAKE_EVENT_DRAW_ENTER) == 1 &&
            Count(events, generation, LKG_FAKE_EVENT_DRAW_EXIT) == 1, "one draw should have exactly one enter and exit");
        if (drawEnterEvent && drawExitEvent)
            ok &= Check(drawEnterEvent->pointerValue == drawExitEvent->pointerValue, "draw enter and exit must refer to the same texture");
        if (drawExitEvent)
            ok &= Check(drawExitEvent->result == (drawShouldSucceed ? 1 : 0), "fake draw result should match the configured outcome");
    }
    if (instanceEvent && releaseDeviceEvent)
        ok &= Check(instanceEvent->pointerValue == releaseDeviceEvent->pointerValue, "instance and release must refer to the shim-held device pointer");
    const LkgFakeBridgeEvent* initializeEvent = First(events, generation, LKG_FAKE_EVENT_INITIALIZE);
    if (initializeEvent) ok &= Check(initializeEvent->threadId != g_harnessThread, "vendor calls must run away from the harness thread");
    for (const auto& event : events) {
        if (event.generation == generation && initializeEvent)
            ok &= Check(event.threadId == initializeEvent->threadId, "vendor calls in one generation must use one worker thread");
    }
    if (initializeEvent && releaseDeviceEvent) ok &= Check(initializeEvent->threadId == releaseDeviceEvent->threadId, "initialize and release must use the same worker thread");
    if (initializeEvent && uninitializeEvent) ok &= Check(initializeEvent->threadId == uninitializeEvent->threadId, "initialize and uninitialize must use the same worker thread");
    return ok;
}

static bool CreateWarpDevice() {
    const D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    D3D_FEATURE_LEVEL selected{};
    HRESULT result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 2, D3D11_SDK_VERSION,
        &g_device, &selected, nullptr);
    if (result == E_INVALIDARG) {
        result = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested + 1, 1, D3D11_SDK_VERSION,
            &g_device, &selected, nullptr);
    }
    if (FAILED(result)) {
        std::fprintf(stderr, "WARP D3D11 creation failed: 0x%08lx\n", static_cast<unsigned long>(result));
        return false;
    }
    std::printf("WARP D3D11 device created, feature level 0x%04x\n", static_cast<unsigned>(selected));
    return true;
}

static ID3D11Texture2D* CreateSourceTexture() {
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = 64;
    desc.Height = 64;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    ID3D11Texture2D* texture = nullptr;
    const HRESULT result = g_device->CreateTexture2D(&desc, nullptr, &texture);
    if (FAILED(result)) {
        std::fprintf(stderr, "WARP shared texture creation failed: 0x%08lx\n", static_cast<unsigned long>(result));
        return nullptr;
    }
    return texture;
}

static bool LoadShim(const char* path, ShimApi* shim) {
    shim->module = LoadLibraryExA(path, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!shim->module) return false;
    shim->pluginLoad = LoadSymbol<ShimApi::PluginLoadFn>(shim->module, "UnityPluginLoad");
    shim->pluginUnload = LoadSymbol<ShimApi::PluginUnloadFn>(shim->module, "UnityPluginUnload");
    return shim->pluginLoad && shim->pluginUnload;
}

static bool LoadWorkerApi(HMODULE module) {
    g_worker.begin = LoadSymbol<WorkerApi::BeginFn>(module, "lkg_worker_begin");
    g_worker.poll = LoadSymbol<WorkerApi::PollFn>(module, "lkg_worker_poll");
    g_worker.close = LoadSymbol<WorkerApi::CloseFn>(module, "lkg_worker_close");
    g_worker.release = LoadSymbol<WorkerApi::ReleaseFn>(module, "lkg_worker_release");
    g_worker.makeDraw = LoadSymbol<WorkerApi::MakeDrawFn>(module, "lkg_worker_make_draw_data");
    g_worker.cancelDraw = LoadSymbol<WorkerApi::CancelDrawFn>(module, "lkg_worker_cancel_draw_data");
    g_worker.renderEvent = LoadSymbol<WorkerApi::RenderEventFn>(module, "lkg_worker_render_event_func");
    g_worker.renderEventId = LoadSymbol<WorkerApi::RenderEventIdFn>(module, "lkg_worker_render_event_id");
    g_worker.pendingCount = LoadSymbol<WorkerApi::PendingCountFn>(module, "lkg_worker_pending_count");
    g_worker.closeAll = LoadSymbol<WorkerApi::CloseAllFn>(module, "lkg_worker_close_all");
    g_worker.shutdownHost = LoadSymbol<WorkerApi::ShutdownHostFn>(module, "lkg_worker_shutdown_host");
    g_worker.collectClosed = LoadSymbol<WorkerApi::CollectClosedFn>(module, "lkg_worker_collect_closed");
    return g_worker.begin && g_worker.poll && g_worker.close && g_worker.release && g_worker.makeDraw &&
        g_worker.cancelDraw && g_worker.renderEvent && g_worker.renderEventId && g_worker.pendingCount && g_worker.closeAll &&
        g_worker.shutdownHost && g_worker.collectClosed;
}

static bool RunContract(const std::string& fakePath, const FakeApi& fake, ID3D11Texture2D* source) {
    int eventId = 0;
    if (!Check(g_reservedEventCount == 1, "Unity callback must reserve exactly one event ID") ||
        !Check(g_worker.renderEventId(&eventId) == 0 && eventId > 0, "Unity callback must reserve a positive event ID")) return false;

    LkgWorkerStatus status{};
    void* first = StartWorker(fakePath, "shutdown-contract-first");
    if (!Check(first != nullptr, "first worker should start") || !Check(WaitForState(first, LKG_WORKER_READY, 5000, &status), "first worker should reach Ready")) return false;
    if (!Check(SubmitDraw(first, source, eventId), "first draw token should be submitted") ||
        !Check(WaitForCompleted(first, 1, 5000, &status), "first draw should complete")) return false;
    if (!Check(WaitAndClose(first, true, &status), "first worker should close after duplicate close") ||
        !Check(status.completed == 1, "first close should retain one completed draw")) return false;
    std::vector<LkgFakeBridgeEvent> events = Events(fake);
    if (!CheckBaseLifecycle(events, 1, true)) return false;
    const LkgFakeBridgeEvent* firstThreadEvent = First(events, 1, LKG_FAKE_EVENT_INITIALIZE);
    if (!Check(firstThreadEvent != nullptr, "first generation should have initialization record")) return false;
    const uint64_t firstThreadId = firstThreadEvent->threadId;

    void* second = StartWorker(fakePath, "shutdown-contract-restart");
    if (!Check(second != nullptr, "restart worker should start") || !Check(WaitForState(second, LKG_WORKER_READY, 5000, &status), "restart worker should reach Ready")) return false;
    if (!Check(SubmitDraw(second, source, eventId), "restart draw token should be submitted") ||
        !Check(WaitForCompleted(second, 1, 5000, &status), "restart draw should complete") ||
        !Check(WaitAndClose(second, false, &status), "restart worker should close")) return false;
    events = Events(fake);
    if (!CheckBaseLifecycle(events, 2, true)) return false;
    const LkgFakeBridgeEvent* secondThreadEvent = First(events, 2, LKG_FAKE_EVENT_INITIALIZE);
    if (!Check(secondThreadEvent && secondThreadEvent->threadId == firstThreadId,
        "same-process restart should reuse the persistent worker thread")) return false;

    fake.setDrawMode(1, 0);
    void* failedDraw = StartWorker(fakePath, "shutdown-contract-failed-draw");
    if (!Check(failedDraw != nullptr, "failed-draw worker should start") || !Check(WaitForState(failedDraw, LKG_WORKER_READY, 5000, &status), "failed-draw worker should reach Ready") ||
        !Check(SubmitDraw(failedDraw, source, eventId), "failed draw token should be submitted") ||
        !Check(WaitForFaultDrain(failedDraw, 5000, &status), "draw failure should publish FAULT and dropped result")) return false;
    if (!Check(WaitForRelease(failedDraw, 5000), "failed draw cleanup should finish before release succeeds")) return false;
    if (!Check(status.completed == 0 && status.dropped == 1 && status.lastErrorCode != 0,
        "draw failure should remain visible in final worker status")) return false;
    events = Events(fake);
    if (!CheckBaseLifecycle(events, 3, true, false)) return false;

    fake.setDrawMode(0, 0);
    void* pending = StartWorker(fakePath, "shutdown-contract-pending-token");
    if (!Check(pending != nullptr, "pending-token worker should start") || !Check(WaitForState(pending, LKG_WORKER_READY, 5000, &status), "pending-token worker should reach Ready")) return false;
    void* unissued = g_worker.makeDraw(pending, source, 1, 1, 1.0f, 1.0f);
    if (unissued) g_liveTokens.push_back(unissued);
    if (!Check(unissued != nullptr, "unissued draw token should be reserved") || !Check(g_worker.close(pending) == 0, "pending-token worker should accept close")) return false;
    if (!Check(g_worker.pendingCount() > 0, "unissued token should keep shutdown pending") ||
        !Check(g_worker.release(pending) == -2, "pending token must prevent context release")) return false;
    events = Events(fake);
    const uint64_t pendingGeneration = 4;
    if (!Check(Count(events, pendingGeneration, LKG_FAKE_EVENT_RELEASE_DEVICE) == 0 &&
        Count(events, pendingGeneration, LKG_FAKE_EVENT_UNINITIALIZE) == 0,
        "pending token must prevent vendor teardown")) return false;
    if (!Check(g_worker.cancelDraw(unissued) == 0, "unissued token cancellation should succeed")) return false;
    g_liveTokens.erase(std::remove(g_liveTokens.begin(), g_liveTokens.end(), unissued), g_liveTokens.end());
    if (!Check(WaitForState(pending, LKG_WORKER_CLOSED, 5000, &status), "cancelled pending token should allow cleanup")) return false;
    if (!Check(WaitForRelease(pending, 5000), "pending-token worker should release after cleanup")) return false;
    events = Events(fake);
    if (!CheckBaseLifecycle(events, pendingGeneration, false)) return false;

    fake.setDrawMode(0, 1);
    void* inFlight = StartWorker(fakePath, "shutdown-contract-inflight");
    if (!Check(inFlight != nullptr, "in-flight worker should start") || !Check(WaitForState(inFlight, LKG_WORKER_READY, 5000, &status), "in-flight worker should reach Ready") ||
        !Check(SubmitDraw(inFlight, source, eventId), "in-flight draw token should be submitted") ||
        !Check(fake.waitDrawEntered(5000) == 1, "fake vendor draw should enter the blocking gate")) return false;
    if (!Check(g_worker.close(inFlight) == 0, "in-flight worker should accept close") ||
        !Check(g_worker.release(inFlight) == -2, "in-flight draw must prevent context release")) return false;
    events = Events(fake);
    const uint64_t inFlightGeneration = 5;
    if (!Check(Count(events, inFlightGeneration, LKG_FAKE_EVENT_RELEASE_DEVICE) == 0 &&
        Count(events, inFlightGeneration, LKG_FAKE_EVENT_UNINITIALIZE) == 0,
        "in-flight draw must prevent vendor teardown")) return false;
    fake.releaseDraw();
    if (!Check(WaitForState(inFlight, LKG_WORKER_CLOSED, 5000, &status), "unblocked draw should finish before closed state")) return false;
    if (!Check(WaitForRelease(inFlight, 5000), "in-flight worker should release after cleanup")) return false;
    events = Events(fake);
    if (!CheckBaseLifecycle(events, inFlightGeneration, true)) return false;
    const LkgFakeBridgeEvent* drawExit = First(events, inFlightGeneration, LKG_FAKE_EVENT_DRAW_EXIT);
    const LkgFakeBridgeEvent* unregisterEvent = First(events, inFlightGeneration, LKG_FAKE_EVENT_UNREGISTER_TEXTURE);
    if (!Check(drawExit && unregisterEvent && drawExit->sequence < unregisterEvent->sequence,
        "in-flight draw must exit before unregister and teardown")) return false;
    if (!Check(status.completed == 1 && status.dropped == 0 && drawExit->result == 1,
        "in-flight draw must exit successfully before teardown")) return false;
    fake.setDrawMode(0, 0);
    return true;
}
}

int main(int argc, char** argv) {
    if (argc != 3) return 2;
    const std::string shimPath = FullPath(argv[1]);
    const std::string fakePath = FullPath(argv[2]);
    if (shimPath.empty() || fakePath.empty()) return 3;

    FakeApi fake{};
    ShimApi shim{};
    if (!LoadFakeApi(fakePath.c_str(), &fake)) return 4;
    fake.reset();
    g_harnessThread = GetCurrentThreadId();
    if (!CreateWarpDevice()) return 5;
    ID3D11Texture2D* source = CreateSourceTexture();
    if (!source) return 6;
    if (!LoadShim(shimPath.c_str(), &shim)) return 7;
    if (!LoadWorkerApi(shim.module)) return 9;
    shim.pluginLoad(&g_interfaces);
    bool ok = Check(g_deviceCallback != nullptr, "UnityPluginLoad should register a device callback") &&
        Check(g_registerCallbackCount == 1 && g_reservedEventCount == 1,
            "Unity device callback should be registered and reserve one event ID");
    if (ok) ok = RunContract(fakePath, fake, source);
    if (!ok) {
        if (!DrainAfterFailure(fake)) {
            std::fprintf(stderr, "FAIL: could not confirm worker drain; leaving shim loaded until process exit\n");
            ExitProcess(10);
        }
    }
    if (shim.pluginUnload) shim.pluginUnload();
    ok = ok && Check(g_unregisterCallbackCount == 1 && g_deviceCallback == nullptr,
        "UnityPluginUnload should unregister the device callback");
    if (source) source->Release();
    if (g_device) {
        g_device->Release();
        g_device = nullptr;
    }
    if (shim.module) FreeLibrary(shim.module);
    fake.reset();
    if (fake.module) FreeLibrary(fake.module);
    if (!ok) return 8;
    std::puts("PASS: native Bridge shutdown contract scenarios completed");
    return 0;
}
