#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <algorithm>
#include <atomic>
#include <thread>
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "bridge_shim.h"
#include "bridge_worker_transport.h"
#include "bridge_shutdown_contract.h"
#include "worker_transport_test_hooks.h"

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
    using BridgeLoadFn = int32_t (*)(const char*);
    using BridgeUnloadFn = void (*)();
    using BridgeInitializeFn = int32_t (*)(const char*);
    using BridgeUninitializeFn = void (*)();
    using CreateSessionFn = uint64_t (*)(void*, uint32_t);
    using DestroySessionFn = int32_t (*)(uint64_t);
    using MakeDrawDataFn = void* (*)(uint64_t, void*, uint32_t, uint32_t, float, float);
    using MakeCloseDataFn = void* (*)(uint64_t);
    using RenderEventFuncFn = void* (*)();
    using GetSessionCloseStatusFn = int32_t (*)(uint64_t, LkgBridgeSessionCloseStatusV1*);
    using AckSessionCloseFn = int32_t (*)(uint64_t);
    PluginLoadFn pluginLoad = nullptr;
    PluginUnloadFn pluginUnload = nullptr;
    BridgeLoadFn bridgeLoad = nullptr;
    BridgeUnloadFn bridgeUnload = nullptr;
    BridgeInitializeFn bridgeInitialize = nullptr;
    BridgeUninitializeFn bridgeUninitialize = nullptr;
    CreateSessionFn createSession = nullptr;
    DestroySessionFn destroySession = nullptr;
    MakeDrawDataFn makeDrawData = nullptr;
    MakeCloseDataFn makeCloseData = nullptr;
    RenderEventFuncFn renderEventFunc = nullptr;
    GetSessionCloseStatusFn getSessionCloseStatus = nullptr;
    AckSessionCloseFn ackSessionClose = nullptr;
};

struct FakeApi {
    HMODULE module = nullptr;
    LkgFakeBridgeResetFn reset = nullptr;
    LkgFakeBridgeSetDrawModeFn setDrawMode = nullptr;
    LkgFakeBridgeWaitDrawEnteredFn waitDrawEntered = nullptr;
    LkgFakeBridgeReleaseDrawFn releaseDraw = nullptr;
    LkgFakeBridgeGetEventsFn getEvents = nullptr;
    LkgFakeBridgeSetUninitializeModeFn setUninitializeMode = nullptr;
    LkgFakeBridgeSetUninitializeThrowFn setUninitializeThrow = nullptr;
    LkgFakeBridgeSetEmptyUninitializeFn setEmptyUninitialize = nullptr;
    LkgFakeBridgeWaitUninitializeEnteredFn waitUninitializeEntered = nullptr;
    LkgFakeBridgeReleaseUninitializeFn releaseUninitialize = nullptr;
};

struct WorkerApi {
    using BeginFn = void* (*)(const char*, const char*, uint32_t, void*);
    using PollFn = int32_t (*)(void*, LkgWorkerStatus*);
    using CloseFn = int32_t (*)(void*);
    using ReleaseFn = int32_t (*)(void*);
    using MakeDrawFn = void* (*)(void*, void*, uint32_t, uint32_t, float, float, const LkgWorkerDrawOptionsV1*);
    using CancelDrawFn = int32_t (*)(void*);
    using RenderEventFn = void* (*)();
    using RenderEventIdFn = int32_t (*)(int32_t*);
    using PendingCountFn = uint32_t (*)();
    using CloseAllFn = int32_t (*)();
    using ShutdownHostFn = int32_t (*)(uint32_t);
    using CollectClosedFn = int32_t (*)();
    using RequestFinalShutdownFn = int32_t (*)();
    using GetFinalShutdownStatusFn = int32_t (*)(LkgWorkerFinalShutdownStatusV1*);
    using WaitFinalShutdownFn = int32_t (*)(uint32_t, LkgWorkerFinalShutdownStatusV1*);
    using GetSubpixelStatusFn = int32_t (*)(void*, LkgWorkerSubpixelStatusV1*);
    using ConfigureSourceCopyFn = void (*)(uint32_t, uint32_t, uint32_t, bool, bool);
    using GetSourceCopyStateFn = int32_t (*)(void*, uint32_t, uint32_t*);
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
    RequestFinalShutdownFn requestFinalShutdown = nullptr;
    GetFinalShutdownStatusFn getFinalShutdownStatus = nullptr;
    WaitFinalShutdownFn waitFinalShutdown = nullptr;
    GetSubpixelStatusFn getSubpixelStatus = nullptr;
    ConfigureSourceCopyFn configureSourceCopy = nullptr;
    GetSourceCopyStateFn getSourceCopyState = nullptr;
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

static std::wstring WideFromAnsi(const std::string& value) {
    const int length = MultiByteToWideChar(CP_ACP, 0, value.c_str(), -1, nullptr, 0);
    if (length <= 1) return {};
    std::wstring result(static_cast<size_t>(length), L'\0');
    if (!MultiByteToWideChar(CP_ACP, 0, value.c_str(), -1, result.data(), length)) return {};
    result.resize(static_cast<size_t>(length - 1));
    return result;
}

static std::string AnsiFromWide(const std::wstring& value) {
    const int length = WideCharToMultiByte(CP_ACP, 0, value.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (length <= 1) return {};
    std::string result(static_cast<size_t>(length), '\0');
    if (!WideCharToMultiByte(CP_ACP, 0, value.c_str(), -1, result.data(), length, nullptr, nullptr)) return {};
    result.resize(static_cast<size_t>(length - 1));
    return result;
}

static bool CreateSecondShimImageCopy(const std::string& sourcePath, std::wstring* copiedPath) {
    static uint32_t copyIndex = 0;
    if (!copiedPath) return false;
    wchar_t tempDirectory[MAX_PATH]{};
    const DWORD length = GetTempPathW(MAX_PATH, tempDirectory);
    if (!length || length >= MAX_PATH) return false;
    wchar_t fileName[96]{};
    swprintf_s(fileName, L"ViewWeaveShimImage-%lu-%u.dll", static_cast<unsigned long>(GetCurrentProcessId()), ++copyIndex);
    *copiedPath = std::wstring(tempDirectory) + fileName;
    const std::wstring sourceWide = WideFromAnsi(sourcePath);
    return !sourceWide.empty() && CopyFileW(sourceWide.c_str(), copiedPath->c_str(), FALSE) != 0;
}

static bool CreateSecondFakeImageCopy(const std::string& sourcePath, std::wstring* copiedPath) {
    static uint32_t copyIndex = 0;
    if (!copiedPath) return false;
    wchar_t tempDirectory[MAX_PATH]{};
    const DWORD length = GetTempPathW(MAX_PATH, tempDirectory);
    if (!length || length >= MAX_PATH) return false;
    wchar_t fileName[96]{};
    swprintf_s(fileName, L"ViewWeaveFakeSdkImage-%lu-%u.dll", static_cast<unsigned long>(GetCurrentProcessId()), ++copyIndex);
    *copiedPath = std::wstring(tempDirectory) + fileName;
    const std::wstring sourceWide = WideFromAnsi(sourcePath);
    return !sourceWide.empty() && CopyFileW(sourceWide.c_str(), copiedPath->c_str(), FALSE) != 0;
}

static std::string QuoteCommandArgument(const std::string& value) {
    std::string result = "\"";
    size_t slashes = 0;
    for (char c : value) {
        if (c == '\\') { ++slashes; continue; }
        if (c == '\"') result.append(slashes * 2 + 1, '\\');
        else result.append(slashes, '\\');
        slashes = 0;
        result.push_back(c);
    }
    result.append(slashes * 2, '\\');
    result.push_back('\"');
    return result;
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
    fake->setUninitializeMode = LoadSymbol<LkgFakeBridgeSetUninitializeModeFn>(fake->module, "lkg_fake_bridge_set_uninitialize_mode");
    fake->setUninitializeThrow = LoadSymbol<LkgFakeBridgeSetUninitializeThrowFn>(fake->module, "lkg_fake_bridge_set_uninitialize_throw");
    fake->setEmptyUninitialize = LoadSymbol<LkgFakeBridgeSetEmptyUninitializeFn>(fake->module, "lkg_fake_bridge_set_empty_uninitialize");
    fake->waitUninitializeEntered = LoadSymbol<LkgFakeBridgeWaitUninitializeEnteredFn>(fake->module, "lkg_fake_bridge_wait_uninitialize_entered");
    fake->releaseUninitialize = LoadSymbol<LkgFakeBridgeReleaseUninitializeFn>(fake->module, "lkg_fake_bridge_release_uninitialize");
    return fake->reset && fake->setDrawMode && fake->waitDrawEntered && fake->releaseDraw && fake->getEvents &&
        fake->setUninitializeMode && fake->setUninitializeThrow && fake->setEmptyUninitialize &&
        fake->waitUninitializeEntered && fake->releaseUninitialize;
}

static std::vector<LkgFakeBridgeEvent> Events(const FakeApi& fake) {
    const uint32_t count = fake.getEvents(nullptr, 0);
    std::vector<LkgFakeBridgeEvent> events(count);
    if (count) fake.getEvents(events.data(), count);
    return events;
}

static uint32_t CountType(const std::vector<LkgFakeBridgeEvent>& events, uint32_t type) {
    return static_cast<uint32_t>(std::count_if(events.begin(), events.end(), [=](const LkgFakeBridgeEvent& event) {
        return event.type == type;
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
    const LkgWorkerDrawOptionsV1 options{ sizeof(LkgWorkerDrawOptionsV1), 1,
        LKG_WORKER_SOURCE_ENCODING_UNORM, 0 };
    void* token = g_worker.makeDraw(worker, source, 1, 1, 1.0f, 1.0f, &options);
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

static bool DrainAfterFailure(FakeApi& fake) {
    fake.releaseDraw();
    fake.releaseUninitialize();
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
    shim->bridgeLoad = LoadSymbol<ShimApi::BridgeLoadFn>(shim->module, "lkg_bridge_load");
    shim->bridgeUnload = LoadSymbol<ShimApi::BridgeUnloadFn>(shim->module, "lkg_bridge_unload");
    shim->bridgeInitialize = LoadSymbol<ShimApi::BridgeInitializeFn>(shim->module, "lkg_bridge_initialize");
    shim->bridgeUninitialize = LoadSymbol<ShimApi::BridgeUninitializeFn>(shim->module, "lkg_bridge_uninitialize");
    shim->createSession = LoadSymbol<ShimApi::CreateSessionFn>(shim->module, "lkg_bridge_create_d3d11_session");
    shim->destroySession = LoadSymbol<ShimApi::DestroySessionFn>(shim->module, "lkg_bridge_destroy_session");
    shim->makeDrawData = LoadSymbol<ShimApi::MakeDrawDataFn>(shim->module, "lkg_bridge_make_draw_data");
    shim->makeCloseData = LoadSymbol<ShimApi::MakeCloseDataFn>(shim->module, "lkg_bridge_make_close_data");
    shim->renderEventFunc = LoadSymbol<ShimApi::RenderEventFuncFn>(shim->module, "lkg_bridge_render_event_func");
    shim->getSessionCloseStatus = LoadSymbol<ShimApi::GetSessionCloseStatusFn>(shim->module, "lkg_bridge_get_session_close_status_v1");
    shim->ackSessionClose = LoadSymbol<ShimApi::AckSessionCloseFn>(shim->module, "lkg_bridge_ack_session_close_v1");
    return shim->pluginLoad && shim->pluginUnload && shim->bridgeLoad && shim->bridgeUnload && shim->bridgeInitialize &&
        shim->bridgeUninitialize && shim->createSession && shim->destroySession && shim->makeDrawData &&
        shim->makeCloseData && shim->renderEventFunc && shim->getSessionCloseStatus && shim->ackSessionClose;
}

static bool LoadWorkerApi(HMODULE module) {
    g_worker.begin = LoadSymbol<WorkerApi::BeginFn>(module, "lkg_worker_begin");
    g_worker.poll = LoadSymbol<WorkerApi::PollFn>(module, "lkg_worker_poll");
    g_worker.close = LoadSymbol<WorkerApi::CloseFn>(module, "lkg_worker_close");
    g_worker.release = LoadSymbol<WorkerApi::ReleaseFn>(module, "lkg_worker_release");
    g_worker.makeDraw = LoadSymbol<WorkerApi::MakeDrawFn>(module, "lkg_worker_make_draw_data_with_options_v1");
    g_worker.cancelDraw = LoadSymbol<WorkerApi::CancelDrawFn>(module, "lkg_worker_cancel_draw_data");
    g_worker.renderEvent = LoadSymbol<WorkerApi::RenderEventFn>(module, "lkg_worker_render_event_func");
    g_worker.renderEventId = LoadSymbol<WorkerApi::RenderEventIdFn>(module, "lkg_worker_render_event_id");
    g_worker.pendingCount = LoadSymbol<WorkerApi::PendingCountFn>(module, "lkg_worker_pending_count");
    g_worker.closeAll = LoadSymbol<WorkerApi::CloseAllFn>(module, "lkg_worker_close_all");
    g_worker.shutdownHost = LoadSymbol<WorkerApi::ShutdownHostFn>(module, "lkg_worker_shutdown_host");
    g_worker.collectClosed = LoadSymbol<WorkerApi::CollectClosedFn>(module, "lkg_worker_collect_closed");
    g_worker.requestFinalShutdown = LoadSymbol<WorkerApi::RequestFinalShutdownFn>(module, "lkg_worker_request_final_shutdown");
    g_worker.getFinalShutdownStatus = LoadSymbol<WorkerApi::GetFinalShutdownStatusFn>(module, "lkg_worker_get_final_shutdown_status_v1");
    g_worker.waitFinalShutdown = LoadSymbol<WorkerApi::WaitFinalShutdownFn>(module, "lkg_worker_wait_final_shutdown_v1");
    g_worker.getSubpixelStatus = LoadSymbol<WorkerApi::GetSubpixelStatusFn>(module, "lkg_worker_get_subpixel_status_v1");
    g_worker.configureSourceCopy = LoadSymbol<WorkerApi::ConfigureSourceCopyFn>(module, "lkg_worker_test_configure_source_copy");
    g_worker.getSourceCopyState = LoadSymbol<WorkerApi::GetSourceCopyStateFn>(module, "lkg_worker_test_get_source_copy_state");
    return g_worker.begin && g_worker.poll && g_worker.close && g_worker.release && g_worker.makeDraw &&
        g_worker.cancelDraw && g_worker.renderEvent && g_worker.renderEventId && g_worker.pendingCount && g_worker.closeAll &&
        g_worker.shutdownHost && g_worker.collectClosed && g_worker.requestFinalShutdown && g_worker.getFinalShutdownStatus &&
        g_worker.waitFinalShutdown && g_worker.getSubpixelStatus;
}

static bool RunContract(const std::string& fakePath, FakeApi& fake, ID3D11Texture2D* source) {
    int eventId = 0;
    if (!Check(g_reservedEventCount == 1, "Unity callback must reserve exactly one event ID") ||
        !Check(g_worker.renderEventId(&eventId) == 0 && eventId > 0, "Unity callback must reserve a positive event ID")) return false;

    LkgWorkerStatus status{};
    void* worker = StartWorker(fakePath, "shutdown-contract-host");
    if (!Check(worker != nullptr, "native presenter worker should start") ||
        !WaitForState(worker, LKG_WORKER_READY, 5000, &status)) {
        std::fprintf(stderr, "native presenter worker state=%u errorCode=%u error=%s\n",
            status.state, status.lastErrorCode, status.error);
        return Check(false, "native presenter worker should reach Ready");
    }
    if (!fake.getEvents && !Check(LoadFakeApi(fakePath.c_str(), &fake),
        "fake API should resolve after worker SDK initialization")) return false;
    if (!Check(SubmitDraw(worker, source, eventId), "native presenter draw should be submitted") ||
        !Check(WaitForCompleted(worker, 1, 5000, &status), "native presenter frame should complete")) return false;
    LkgWorkerSubpixelStatusV1 output{};
    output.structSize = sizeof(output);
    output.version = 1;
    if (!Check(g_worker.getSubpixelStatus(worker, &output) == 0 && output.valid == 1 &&
        output.requestedMode == LKG_WORKER_SUBPIXEL_AUTOMATIC &&
        output.effectiveMode == LKG_WORKER_SUBPIXEL_AUTOMATIC &&
        output.backend == LKG_WORKER_SUBPIXEL_BACKEND_NATIVE_D3D11 &&
        output.renderCompleted >= 1 && output.presentAccepted >= 1,
        "worker frame must be completed and accepted by the native Automatic presenter")) return false;
    if (!Check(g_worker.close(worker) == 0 && WaitForState(worker, LKG_WORKER_CLOSED, 5000, &status),
        "native presenter worker should drain and close") ||
        !Check(WaitForRelease(worker, 5000), "proven native presenter cleanup should release")) return false;
    const std::vector<LkgFakeBridgeEvent> events = Events(fake);
    if (!Check(CountType(events, LKG_FAKE_EVENT_INITIALIZE) == 1,
        "the worker should initialize the fake SDK once")) return false;
    if (!Check(CountType(events, LKG_FAKE_EVENT_DRAW_ENTER) == 0 &&
        CountType(events, LKG_FAKE_EVENT_INSTANCE_WINDOW) == 0 &&
        CountType(events, LKG_FAKE_EVENT_SHOW_WINDOW) == 0 &&
        CountType(events, LKG_FAKE_EVENT_REGISTER_TEXTURE) == 0,
        "native worker presentation must not create or draw an SDK window")) return false;
    const LkgFakeBridgeEvent* initialize = First(events, events.front().generation, LKG_FAKE_EVENT_INITIALIZE);
    if (!Check(initialize && initialize->threadId != g_harnessThread,
        "SDK initialization must stay on the worker thread")) return false;
    for (const auto& event : events) {
        if (event.type == LKG_FAKE_EVENT_INITIALIZE || event.type == LKG_FAKE_EVENT_UNINITIALIZE ||
            event.type == LKG_FAKE_EVENT_RELEASE_DEVICE) {
            if (!Check(event.threadId == initialize->threadId,
                "SDK ownership calls must remain on the same worker thread")) return false;
        }
    }
    return Check(g_worker.pendingCount() == 0, "native worker draw cleanup must leave no pending work");
}

static bool RunSdkImageOwnershipCase(const std::string& shimPath, const std::string& fakePath, const FakeApi& fake) {
    std::wstring copiedShimPath;
    std::wstring copiedFakePath;
    if (!Check(CreateSecondShimImageCopy(shimPath, &copiedShimPath), "second shim image copy should be created") ||
        !Check(CreateSecondFakeImageCopy(fakePath, &copiedFakePath), "second fake SDK image copy should be created")) return false;
    HMODULE secondImage = LoadLibraryExW(copiedShimPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!Check(secondImage != nullptr, "second shim image should load from a distinct path")) {
        DeleteFileW(copiedShimPath.c_str());
        return false;
    }
    ShimApi second{};
    second.module = secondImage;
    second.bridgeLoad = LoadSymbol<ShimApi::BridgeLoadFn>(secondImage, "lkg_bridge_load");
    second.bridgeUnload = LoadSymbol<ShimApi::BridgeUnloadFn>(secondImage, "lkg_bridge_unload");
    second.bridgeInitialize = LoadSymbol<ShimApi::BridgeInitializeFn>(secondImage, "lkg_bridge_initialize");
    second.bridgeUninitialize = LoadSymbol<ShimApi::BridgeUninitializeFn>(secondImage, "lkg_bridge_uninitialize");
    bool ok = Check(second.bridgeLoad && second.bridgeUnload && second.bridgeInitialize && second.bridgeUninitialize,
        "second image SDK entrypoints should resolve");
    const std::string copiedFakePathAnsi = AnsiFromWide(copiedFakePath);
    if (ok) ok = Check(second.bridgeLoad(copiedFakePathAnsi.c_str()) == 0,
        "second image should load a distinct runtime before ownership claim");
    FakeApi secondFake{};
    if (ok) ok = Check(LoadFakeApi(copiedFakePathAnsi.c_str(), &secondFake),
        "second runtime API should resolve after its owning shim loads it");
    const std::vector<LkgFakeBridgeEvent> before = ok ? Events(fake) : std::vector<LkgFakeBridgeEvent>{};
    const std::vector<LkgFakeBridgeEvent> secondBefore = ok ? Events(secondFake) : std::vector<LkgFakeBridgeEvent>{};
    const uint32_t initBefore = CountType(before, LKG_FAKE_EVENT_INITIALIZE);
    const uint32_t uninitBefore = CountType(before, LKG_FAKE_EVENT_UNINITIALIZE);
    const uint32_t secondInitBefore = CountType(secondBefore, LKG_FAKE_EVENT_INITIALIZE);
    const uint32_t secondUninitBefore = CountType(secondBefore, LKG_FAKE_EVENT_UNINITIALIZE);
    if (ok) ok = Check(second.bridgeInitialize("shutdown-contract-non-owner") == -7,
        "second shim image must reject SDK initialization while another image owns it");
    if (ok) {
        second.bridgeUninitialize();
        const std::vector<LkgFakeBridgeEvent> after = Events(fake);
        const std::vector<LkgFakeBridgeEvent> secondAfter = Events(secondFake);
        ok &= Check(CountType(after, LKG_FAKE_EVENT_INITIALIZE) == initBefore &&
            CountType(after, LKG_FAKE_EVENT_UNINITIALIZE) == uninitBefore,
            "non-owner calls must not reach the current SDK owner");
        ok &= Check(CountType(secondAfter, LKG_FAKE_EVENT_INITIALIZE) == secondInitBefore &&
            CountType(secondAfter, LKG_FAKE_EVENT_UNINITIALIZE) == secondUninitBefore,
            "non-owner calls must not reach the second SDK runtime");
    }
    if (second.bridgeUnload) second.bridgeUnload();
    if (secondFake.module) FreeLibrary(secondFake.module);
    FreeLibrary(secondImage);
    DeleteFileW(copiedShimPath.c_str());
    DeleteFileW(copiedFakePath.c_str());
    return ok;
}

static bool QueryCloseStatus(const ShimApi& shim, uint64_t session, uint32_t expectedState,
    int32_t expectedResult, const char* message) {
    LkgBridgeSessionCloseStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    const int32_t result = shim.getSessionCloseStatus(session, &status);
    return Check(result == (expectedState == LKG_BRIDGE_SESSION_CLOSE_UNKNOWN ? -2 : 0) &&
        status.structSize == sizeof(status) && status.version == 1 && status.sessionId == session &&
        status.state == expectedState && status.lastResult == expectedResult && status.reserved0 == 0 &&
        status.reserved1 == 0, message);
}

static bool CloseAndAcknowledgeSession(const ShimApi& shim, uint64_t session) {
    if (!Check(shim.destroySession(session) == 0, "session close should be accepted")) return false;
    void* data = shim.makeCloseData(session);
    auto callback = reinterpret_cast<void (*)(int, void*)>(shim.renderEventFunc());
    if (!Check(data != nullptr && callback != nullptr, "session close callback data should be created")) return false;
    callback(1, data);
    return QueryCloseStatus(shim, session, LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE, 0,
        "successful close should be published as safe") &&
        Check(shim.ackSessionClose(session) == 0, "safe close record should be acknowledged");
}

static bool RunSessionCloseLedgerCase(ShimApi& ledgerShim, const FakeApi& fake, ID3D11Texture2D* source) {
    ShimApi* shim = &ledgerShim;
    if (!Check(shim->bridgeInitialize("shutdown-contract-ledger") == 0,
        "ledger case should initialize the guarded fake SDK")) return false;
    const uint64_t firstSession = shim->createSession(g_device, 0);
    if (!Check(firstSession != 0, "ledger case should create the first session") ||
        !QueryCloseStatus(*shim, firstSession, LKG_BRIDGE_SESSION_CLOSE_PENDING, 0,
            "new session should be visible as Pending")) return false;

    fake.setDrawMode(0, 1);
    void* drawData = shim->makeDrawData(firstSession, source, 8, 6, 1.0f, 1.0f);
    auto callback = reinterpret_cast<void (*)(int, void*)>(shim->renderEventFunc());
    if (!Check(drawData != nullptr && callback != nullptr, "ledger draw data should be created")) return false;
    std::thread drawThread([callback, drawData]() { callback(0, drawData); });
    const bool entered = fake.waitDrawEntered(5000) == 1;
    std::atomic<bool> queryReturned{false};
    std::atomic<int32_t> queryResult{-99};
    LkgBridgeSessionCloseStatusV1 pendingStatus{};
    pendingStatus.structSize = sizeof(pendingStatus);
    pendingStatus.version = 1;
    std::thread queryThread([&]() {
        queryResult.store(shim->getSessionCloseStatus(firstSession, &pendingStatus), std::memory_order_release);
        queryReturned.store(true, std::memory_order_release);
    });
    const ULONGLONG queryDeadline = GetTickCount64() + 1000;
    while (!queryReturned.load(std::memory_order_acquire) && GetTickCount64() < queryDeadline) Sleep(1);
    const bool queryWasNonBlocking = queryReturned.load(std::memory_order_acquire);
    fake.releaseDraw();
    drawThread.join();
    queryThread.join();
    fake.setDrawMode(0, 0);
    if (!Check(entered, "fake draw should enter while querying the ledger") ||
        !Check(queryWasNonBlocking, "session status query must not wait for the vendor draw mutex") ||
        !Check(queryResult.load(std::memory_order_acquire) == 0 &&
            pendingStatus.state == LKG_BRIDGE_SESSION_CLOSE_PENDING && pendingStatus.sessionId == firstSession,
            "blocked draw query should return the known Pending record")) return false;
    if (!CloseAndAcknowledgeSession(*shim, firstSession) ||
        !QueryCloseStatus(*shim, firstSession, LKG_BRIDGE_SESSION_CLOSE_UNKNOWN, -2,
            "acknowledged session must no longer be reported as proof") ||
        !Check(shim->ackSessionClose(firstSession) == -2, "unknown session must not be acknowledged")) return false;

    if (!Check(shim->bridgeInitialize("shutdown-contract-ledger-capacity") == 0,
        "ledger capacity case should initialize after the first safe ACK")) return false;
    std::vector<uint64_t> sessions;
    sessions.reserve(LKG_BRIDGE_SESSION_CLOSE_LEDGER_CAPACITY);
    for (uint32_t i = 0; i < LKG_BRIDGE_SESSION_CLOSE_LEDGER_CAPACITY; ++i) {
        const uint64_t session = shim->createSession(g_device, 0);
        if (!Check(session != 0, "ledger should admit a session while capacity remains")) return false;
        sessions.push_back(session);
    }
    const uint32_t instanceCallsBeforeFull = CountType(Events(fake), LKG_FAKE_EVENT_INSTANCE_WINDOW);
    if (!Check(shim->createSession(g_device, 0) == 0,
        "full close ledger must reject before creating another session") ||
        !Check(CountType(Events(fake), LKG_FAKE_EVENT_INSTANCE_WINDOW) == instanceCallsBeforeFull,
            "ledger capacity rejection must happen before vendor instance_window")) return false;
    for (uint64_t session : sessions) {
        if (!CloseAndAcknowledgeSession(*shim, session)) return false;
    }
    if (!Check(shim->bridgeInitialize("shutdown-contract-ledger-reuse") == 0,
        "acknowledged ledger capacity should permit a later SDK session")) return false;
    const uint64_t reusedSession = shim->createSession(g_device, 0);
    if (!Check(reusedSession != 0, "acknowledged ledger capacity should be reusable")) return false;
    if (!CloseAndAcknowledgeSession(*shim, reusedSession)) return false;
    shim->bridgeUnload();
    return Check(true, "ledger case completed");
}

static bool RunDirectSdkFinalAdmissionCase(const std::string& shimPath, const std::string& fakePath) {
    std::wstring ownerPath;
    if (!Check(CreateSecondShimImageCopy(shimPath, &ownerPath), "direct-sdk owner image copy should be created")) return false;
    HMODULE ownerModule = LoadLibraryExW(ownerPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!Check(ownerModule != nullptr, "direct-sdk owner image should load")) {
        DeleteFileW(ownerPath.c_str());
        return false;
    }
    ShimApi owner{};
    owner.module = ownerModule;
    owner.bridgeLoad = LoadSymbol<ShimApi::BridgeLoadFn>(ownerModule, "lkg_bridge_load");
    owner.bridgeUnload = LoadSymbol<ShimApi::BridgeUnloadFn>(ownerModule, "lkg_bridge_unload");
    owner.bridgeInitialize = LoadSymbol<ShimApi::BridgeInitializeFn>(ownerModule, "lkg_bridge_initialize");
    owner.bridgeUninitialize = LoadSymbol<ShimApi::BridgeUninitializeFn>(ownerModule, "lkg_bridge_uninitialize");
    using RequestFinalFn = int32_t (*)();
    using GetFinalStatusFn = int32_t (*)(LkgWorkerFinalShutdownStatusV1*);
    RequestFinalFn requestFinal = LoadSymbol<RequestFinalFn>(ownerModule, "lkg_worker_request_final_shutdown");
    GetFinalStatusFn getFinalStatus = LoadSymbol<GetFinalStatusFn>(ownerModule, "lkg_worker_get_final_shutdown_status_v1");
    bool ok = Check(owner.bridgeLoad && owner.bridgeUnload && owner.bridgeInitialize && owner.bridgeUninitialize &&
        requestFinal && getFinalStatus, "direct-sdk final symbols should resolve");
    if (ok) ok = Check(owner.bridgeLoad(fakePath.c_str()) == 0, "direct-sdk owner should load the fake runtime");
    FakeApi fake{};
    if (ok) ok = Check(LoadFakeApi(fakePath.c_str(), &fake), "direct-sdk owner fake API should resolve after guarded load");
    if (ok) fake.setEmptyUninitialize(1);
    if (ok) ok = Check(owner.bridgeInitialize("shutdown-contract-direct-sdk") == 0,
        "direct SDK initialization should acquire process ownership");
    const std::vector<LkgFakeBridgeEvent> beforeFinal = fake.getEvents
        ? Events(fake) : std::vector<LkgFakeBridgeEvent>{};
    const uint32_t initializedCount = CountType(beforeFinal, LKG_FAKE_EVENT_INITIALIZE);
    const uint32_t uninitializedCount = CountType(beforeFinal, LKG_FAKE_EVENT_UNINITIALIZE);
    if (ok) ok = Check(requestFinal() == 0, "final shutdown should close direct SDK admission");

    LkgWorkerFinalShutdownStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    if (ok) ok = Check(getFinalStatus(&status) == 0 && status.state == LKG_WORKER_FINAL_SHUTDOWN_DRAINING &&
        status.registeredContexts == 0 && status.hostJoined == 1,
        "active direct SDK ownership must keep final shutdown incomplete");

    std::wstring nonOwnerPath;
    std::wstring nonOwnerFakePath;
    if (ok) ok = Check(CreateSecondShimImageCopy(shimPath, &nonOwnerPath), "non-owner image copy should be created");
    if (ok) ok = Check(CreateSecondFakeImageCopy(fakePath, &nonOwnerFakePath), "non-owner fake SDK image copy should be created");
    HMODULE nonOwnerModule = ok ? LoadLibraryExW(nonOwnerPath.c_str(), nullptr,
        LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) : nullptr;
    if (ok) ok = Check(nonOwnerModule != nullptr, "non-owner image should load");
    ShimApi nonOwner{};
    if (nonOwnerModule) {
        nonOwner.module = nonOwnerModule;
        nonOwner.bridgeLoad = LoadSymbol<ShimApi::BridgeLoadFn>(nonOwnerModule, "lkg_bridge_load");
        nonOwner.bridgeUnload = LoadSymbol<ShimApi::BridgeUnloadFn>(nonOwnerModule, "lkg_bridge_unload");
        nonOwner.bridgeInitialize = LoadSymbol<ShimApi::BridgeInitializeFn>(nonOwnerModule, "lkg_bridge_initialize");
        nonOwner.bridgeUninitialize = LoadSymbol<ShimApi::BridgeUninitializeFn>(nonOwnerModule, "lkg_bridge_uninitialize");
        const std::string nonOwnerFakePathAnsi = AnsiFromWide(nonOwnerFakePath);
        if (ok) ok = Check(nonOwner.bridgeLoad && nonOwner.bridgeUnload && nonOwner.bridgeInitialize && nonOwner.bridgeUninitialize &&
            nonOwner.bridgeLoad(nonOwnerFakePathAnsi.c_str()) == 0, "non-owner image should load a distinct fake runtime");
        FakeApi nonOwnerFake{};
        if (ok) ok = Check(LoadFakeApi(nonOwnerFakePathAnsi.c_str(), &nonOwnerFake),
            "non-owner fake API should resolve after guarded load");
        const std::vector<LkgFakeBridgeEvent> nonOwnerBefore = nonOwnerFake.getEvents
            ? Events(nonOwnerFake) : std::vector<LkgFakeBridgeEvent>{};
        const uint32_t nonOwnerInitBefore = CountType(nonOwnerBefore, LKG_FAKE_EVENT_INITIALIZE);
        const uint32_t nonOwnerUninitBefore = CountType(nonOwnerBefore, LKG_FAKE_EVENT_UNINITIALIZE);
        if (ok) ok = Check(nonOwner.bridgeInitialize("shutdown-contract-final-non-owner") == -7,
            "non-owner image must not initialize while direct SDK owner is active");
        if (ok) nonOwner.bridgeUninitialize();
        const std::vector<LkgFakeBridgeEvent> afterNonOwner = fake.getEvents
            ? Events(fake) : std::vector<LkgFakeBridgeEvent>{};
        if (ok) ok = Check(fake.getEvents && CountType(afterNonOwner, LKG_FAKE_EVENT_INITIALIZE) == initializedCount &&
            CountType(afterNonOwner, LKG_FAKE_EVENT_UNINITIALIZE) == uninitializedCount,
            "non-owner lifecycle calls must not reach the vendor SDK");
        const std::vector<LkgFakeBridgeEvent> nonOwnerAfter = nonOwnerFake.getEvents
            ? Events(nonOwnerFake) : std::vector<LkgFakeBridgeEvent>{};
        if (ok) ok = Check(nonOwnerFake.getEvents && CountType(nonOwnerAfter, LKG_FAKE_EVENT_INITIALIZE) == nonOwnerInitBefore &&
            CountType(nonOwnerAfter, LKG_FAKE_EVENT_UNINITIALIZE) == nonOwnerUninitBefore,
            "non-owner lifecycle calls must not reach the second vendor SDK");
        if (nonOwner.bridgeUnload) nonOwner.bridgeUnload();
        if (nonOwnerFake.module) FreeLibrary(nonOwnerFake.module);
        FreeLibrary(nonOwnerModule);
        DeleteFileW(nonOwnerPath.c_str());
        DeleteFileW(nonOwnerFakePath.c_str());
    }

    if (ok) owner.bridgeUninitialize();
    status.structSize = sizeof(status);
    status.version = 1;
    if (ok) ok = Check(getFinalStatus(&status) == 0 && status.state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE &&
        status.registeredContexts == 0 && status.hostJoined == 1,
        "owner SDK uninitialize should allow final shutdown to complete");
    if (ok) ok = Check(owner.bridgeInitialize("shutdown-contract-after-final") == -7,
        "final shutdown must permanently reject direct SDK reinitialization");
    if (ok) ok = Check(CountType(Events(fake), LKG_FAKE_EVENT_INITIALIZE) == initializedCount,
        "post-final SDK initialization must not reach the vendor SDK");
    if (fake.setEmptyUninitialize) fake.setEmptyUninitialize(0);
    if (owner.bridgeUnload) owner.bridgeUnload();
    if (fake.module) FreeLibrary(fake.module);
    FreeLibrary(ownerModule);
    DeleteFileW(ownerPath.c_str());
    DeleteFileW(nonOwnerFakePath.c_str());
    return ok;
}

static bool RunFinalShutdownContract(const std::string& shimPath, const std::string& fakePath,
    const FakeApi& fake, ID3D11Texture2D* source) {
    int eventId = 0;
    LkgWorkerStatus workerStatus{};
    if (!Check(g_worker.renderEventId(&eventId) == 0 && eventId > 0,
        "final shutdown should retain the Unity render event")) return false;
    fake.setEmptyUninitialize(1);
    fake.setUninitializeMode(0, 1);
    g_worker.configureSourceCopy(0, 0, LKG_WORKER_TEST_PRESENTER_OCCLUDED, false, true);
    void* pending = StartWorker(fakePath, "shutdown-contract-host");
    if (!Check(pending != nullptr && WaitForState(pending, LKG_WORKER_READY, 5000, &workerStatus),
        "held-copy final shutdown worker should reach Ready") ||
        !Check(SubmitDraw(pending, source, eventId), "held-copy final shutdown draw should submit")) return false;

    uint32_t flags = 0;
    const ULONGLONG copyDeadline = GetTickCount64() + 5000;
    do {
        if (g_worker.getSourceCopyState(pending, 0, &flags) == 0 &&
            (flags & (LKG_WORKER_TEST_SOURCE_UNPROVEN | LKG_WORKER_TEST_SOURCE_MUTEX_HELD |
                LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED)) == (LKG_WORKER_TEST_SOURCE_UNPROVEN |
                LKG_WORKER_TEST_SOURCE_MUTEX_HELD | LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED)) break;
        Sleep(2);
    } while (GetTickCount64() < copyDeadline);
    if (!Check((flags & (LKG_WORKER_TEST_SLOT_IN_USE | LKG_WORKER_TEST_SOURCE_UNPROVEN |
            LKG_WORKER_TEST_SOURCE_MUTEX_HELD | LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED)) ==
            (LKG_WORKER_TEST_SLOT_IN_USE | LKG_WORKER_TEST_SOURCE_UNPROVEN |
            LKG_WORKER_TEST_SOURCE_MUTEX_HELD | LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED),
        "held EVENT query must retain source slot and keyed mutex")) return false;
    LkgWorkerSubpixelStatusV1 pixelStatus{};
    pixelStatus.structSize = sizeof(pixelStatus);
    pixelStatus.version = 1;
    if (!Check(g_worker.getSubpixelStatus(pending, &pixelStatus) == 0 && pixelStatus.valid == 1 &&
        pixelStatus.backend == LKG_WORKER_SUBPIXEL_BACKEND_NATIVE_D3D11 &&
        pixelStatus.presentAccepted == 0,
        "occluded test frame must remain an unaccepted native presentation")) return false;
    if (!RunSdkImageOwnershipCase(shimPath, fakePath, fake)) return false;

    if (!Check(g_worker.requestFinalShutdown() == 0, "final shutdown should accept the held source-copy drain") ||
        !Check(g_worker.release(pending) == -2, "unproven source-copy work must prevent worker release") ||
        !Check(g_worker.pendingCount() > 0, "unproven source-copy work must remain pending")) return false;
    LkgWorkerFinalShutdownStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    const int32_t timeoutResult = g_worker.waitFinalShutdown(25, &status);
    if (!Check(timeoutResult == 1 && status.state == LKG_WORKER_FINAL_SHUTDOWN_TIMED_OUT &&
        status.hostRunning == 1 && status.hostJoined == 0,
        "unproven raw copy must keep final shutdown in an explicit timeout")) return false;
    if (!Check(g_worker.getSourceCopyState(pending, 0, &flags) == 0 &&
        (flags & (LKG_WORKER_TEST_SLOT_IN_USE | LKG_WORKER_TEST_SOURCE_UNPROVEN |
            LKG_WORKER_TEST_SOURCE_MUTEX_HELD | LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED)) ==
            (LKG_WORKER_TEST_SLOT_IN_USE | LKG_WORKER_TEST_SOURCE_UNPROVEN |
            LKG_WORKER_TEST_SOURCE_MUTEX_HELD | LKG_WORKER_TEST_SOURCE_COPY_QUERY_ISSUED),
        "final shutdown timeout must retain the issued copy query and its source ownership")) return false;
    const std::vector<LkgFakeBridgeEvent> beforeProof = Events(fake);
    if (!Check(CountType(beforeProof, LKG_FAKE_EVENT_UNINITIALIZE) == 0,
        "SDK uninitialize must wait for raw-copy completion proof") ||
        !Check(fake.waitUninitializeEntered(0) == 0,
        "SDK uninitialize must not enter its gate before raw-copy completion proof")) return false;

    g_worker.configureSourceCopy(0, 0, LKG_WORKER_TEST_PRESENTER_NORMAL, false, false);
    if (!Check(fake.waitUninitializeEntered(5000) == 1,
        "late real EVENT query proof should allow shutdown to reach SDK uninitialize")) return false;
    status.structSize = sizeof(status);
    status.version = 1;
    const int32_t uninitializeTimeout = g_worker.waitFinalShutdown(25, &status);
    if (!Check(uninitializeTimeout == 1 && status.state == LKG_WORKER_FINAL_SHUTDOWN_TIMED_OUT &&
        status.registeredContexts == 0 && status.hostRunning == 1,
        "blocked SDK uninitialize must remain pending after proven worker cleanup")) return false;
    fake.releaseUninitialize();
    status.structSize = sizeof(status);
    status.version = 1;
    const int32_t complete = g_worker.waitFinalShutdown(5000, &status);
    if (!Check(complete == 0 && status.state == LKG_WORKER_FINAL_SHUTDOWN_COMPLETE &&
        status.registeredContexts == 0 && status.closedUncollectedContexts == 0 &&
        status.hostRunning == 0 && status.hostJoined == 1,
        "proven source retirement and SDK uninitialize should complete final shutdown")) return false;
    if (!Check(g_worker.pendingCount() == 0 && g_worker.requestFinalShutdown() == 0,
        "completed final shutdown should be drained and idempotent")) return false;
    const std::vector<LkgFakeBridgeEvent> events = Events(fake);
    const auto initialize = std::find_if(events.begin(), events.end(), [](const LkgFakeBridgeEvent& event) {
        return event.type == LKG_FAKE_EVENT_INITIALIZE;
    });
    const auto uninitialize = std::find_if(events.begin(), events.end(), [](const LkgFakeBridgeEvent& event) {
        return event.type == LKG_FAKE_EVENT_UNINITIALIZE;
    });
    if (!Check(CountType(events, LKG_FAKE_EVENT_INITIALIZE) == 1 &&
        CountType(events, LKG_FAKE_EVENT_UNINITIALIZE) == 1 &&
        initialize != events.end() && uninitialize != events.end() &&
        initialize->generation == uninitialize->generation && initialize->sequence < uninitialize->sequence,
        "final SDK uninitialize must follow initialization after source proof") ||
        !Check(initialize->threadId == uninitialize->threadId && initialize->threadId != g_harnessThread,
        "SDK initialize and final uninitialize must stay on the owning host thread") ||
        !Check(CountType(events, LKG_FAKE_EVENT_DRAW_ENTER) == 0 &&
            CountType(events, LKG_FAKE_EVENT_INSTANCE_WINDOW) == 0,
        "NativeD3D11 final shutdown must not route presentation through an SDK window")) return false;
    fake.setUninitializeMode(0, 0);
    return true;
}

static bool RunUninitializeFailureContract(ShimApi& faultShim, const std::string& faultShimPath,
    const std::string& fakePath,
    FakeApi& fake, bool throwUninitialize = false) {
    if (!LoadWorkerApi(faultShim.module)) return Check(false, "fault-case final shutdown symbols should resolve");
    LkgWorkerStatus workerStatus{};
    void* worker = StartWorker(fakePath, "shutdown-contract-host");
    bool ok = Check(worker != nullptr, "uninitialize-failure worker should start");
    if (ok && !WaitForState(worker, LKG_WORKER_READY, 5000, &workerStatus)) {
        std::fprintf(stderr, "uninitialize-failure worker state=%u errorCode=%u error=%s\n",
            workerStatus.state, workerStatus.lastErrorCode, workerStatus.error);
        ok = Check(false, "uninitialize-failure worker should reach Ready");
    }
    if (ok && !fake.getEvents) ok = Check(LoadFakeApi(fakePath.c_str(), &fake),
        "fault-case fake API should resolve after the worker owner loads the runtime");
    if (ok) {
        fake.setEmptyUninitialize(1);
        fake.setUninitializeMode(throwUninitialize ? 0 : 1, 0);
        fake.setUninitializeThrow(throwUninitialize ? 1 : 0);
    }
    if (ok) ok = Check(g_worker.close(worker) == 0 &&
        WaitForState(worker, LKG_WORKER_CLOSED, 5000, &workerStatus),
        "offscreen worker cleanup should complete before host SDK uninitialization");
    if (ok) ok = Check(g_worker.release(worker) == 0,
        "proven offscreen cleanup should permit context release before final SDK shutdown");
    if (ok) ok = Check(g_worker.requestFinalShutdown() == 0,
        "final shutdown should start after all offscreen contexts drain");
    LkgWorkerFinalShutdownStatusV1 status{};
    status.structSize = sizeof(status);
    status.version = 1;
    const int32_t waitResult = ok ? g_worker.waitFinalShutdown(5000, &status) : -99;
    if (ok) ok &= Check(waitResult == -2 && status.state == LKG_WORKER_FINAL_SHUTDOWN_FAULT && status.lastResult == -2,
        "false SDK uninitialize must report final shutdown Fault, never Complete");
    if (ok) ok &= Check(status.registeredContexts == 0 && status.closedUncollectedContexts == 0 && status.hostJoined == 1,
        "finalization fault must retain SDK ownership without misreporting a released context as pending");
    const std::vector<LkgFakeBridgeEvent> events = fake.getEvents
        ? Events(fake) : std::vector<LkgFakeBridgeEvent>{};
    const auto initialize = std::find_if(events.begin(), events.end(), [](const LkgFakeBridgeEvent& event) {
        return event.type == LKG_FAKE_EVENT_INITIALIZE;
    });
    const auto uninitializeIt = std::find_if(events.begin(), events.end(), [](const LkgFakeBridgeEvent& event) {
        return event.type == LKG_FAKE_EVENT_UNINITIALIZE;
    });
    const LkgFakeBridgeEvent* uninitialize = uninitializeIt == events.end() ? nullptr : &*uninitializeIt;
    if (ok) ok &= Check(uninitialize && uninitialize->result == (throwUninitialize ? -1 : 0),
        "fake SDK should record the false or throwing final uninitialize result");
    if (ok) ok &= Check(initialize != events.end() && uninitialize &&
        initialize->generation == uninitialize->generation && initialize->sequence < uninitialize->sequence,
        "uninitialize fault must refer to the actual initialized generation");
    if (ok) ok &= Check(CountType(events, LKG_FAKE_EVENT_DRAW_ENTER) == 0 &&
        CountType(events, LKG_FAKE_EVENT_INSTANCE_WINDOW) == 0,
        "offscreen worker shutdown must not create SDK draw or window activity");
    const uint32_t initializeCount = CountType(events, LKG_FAKE_EVENT_INITIALIZE);
    if (ok) ok &= Check(g_worker.begin(fakePath.c_str(), "shutdown-contract-fault-reinitialize", 0, g_device) == nullptr,
        "faulted SDK ownership must reject another worker initialization");
    if (ok) ok &= Check(CountType(Events(fake), LKG_FAKE_EVENT_INITIALIZE) == initializeCount,
        "faulted SDK retry must not call vendor initialize");
    if (ok) ok &= RunSdkImageOwnershipCase(faultShimPath, fakePath, fake);
    g_worker.shutdownHost(5000);
    if (fake.setUninitializeMode) fake.setUninitializeMode(0, 0);
    if (fake.setUninitializeThrow) fake.setUninitializeThrow(0);
    return ok;
}
}

static bool RunChildCase(const std::string& mode, const std::string& productionShimPath,
    const std::string& fakePath, const std::string& workerTestShimPath) {
    FakeApi fake{};
    ShimApi shim{};
    g_harnessThread = GetCurrentThreadId();
    if (!CreateWarpDevice()) return Check(false, "WARP device should be created");
    ID3D11Texture2D* source = CreateSourceTexture();
    if (!source) return Check(false, "source texture should be created");
    const bool directCase = mode == "direct-final";
    const bool workerLoadedCase = mode == "worker-final" || mode == "uninitialize-exception";
    const std::string& shimPath = workerLoadedCase ? workerTestShimPath : productionShimPath;
    if (!LoadShim(shimPath.c_str(), &shim)) return Check(false, "selected shim should load");
    if (!LoadWorkerApi(shim.module)) return Check(false, "worker contract symbols should resolve");
    if (workerLoadedCase && !Check(g_worker.configureSourceCopy && g_worker.getSourceCopyState,
        "offscreen worker test hooks should resolve from the test-only DLL")) return false;
    shim.pluginLoad(&g_interfaces);
    bool ok = Check(g_deviceCallback != nullptr, "UnityPluginLoad should register a device callback") &&
        Check(g_registerCallbackCount == 1 && g_reservedEventCount == 1,
            "Unity device callback should be registered and reserve one event ID");
    if (ok && !directCase && !workerLoadedCase) {
        ok = Check(shim.bridgeLoad(fakePath.c_str()) == 0, "SDK owner shim should guard-load its fake runtime");
        if (ok) ok = Check(LoadFakeApi(fakePath.c_str(), &fake), "fake API should resolve after its owner loads the runtime");
        if (ok) fake.reset();
    }
    if (ok && mode == "ledger") {
        ok = RunSessionCloseLedgerCase(shim, fake, source);
    } else if (ok && mode == "direct-final") {
        ok = RunDirectSdkFinalAdmissionCase(shimPath, fakePath);
    } else if (ok && mode == "worker-final") {
        ok = RunContract(fakePath, fake, source);
        if (ok) ok = RunFinalShutdownContract(workerTestShimPath, fakePath, fake, source);
    } else if (ok && mode == "uninitialize-exception") {
        ok = RunUninitializeFailureContract(shim, workerTestShimPath, fakePath, fake, true);
    } else if (ok) {
        ok = Check(false, "unknown shutdown contract child case");
    }
    if (!ok && fake.getEvents && !DrainAfterFailure(fake)) {
        std::fprintf(stderr, "FAIL: could not confirm worker drain; leaving shim loaded until process exit\n");
        ExitProcess(10);
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
    if (fake.module) FreeLibrary(fake.module);
    return ok;
}

static bool RunChildProcess(const char* executable, const std::string& mode,
    const std::string& shimPath, const std::string& fakePath, const std::string& workerTestShimPath) {
    std::string command = QuoteCommandArgument(executable) + " --case " + QuoteCommandArgument(mode) +
        " " + QuoteCommandArgument(shimPath) + " " + QuoteCommandArgument(fakePath) +
        " " + QuoteCommandArgument(workerTestShimPath);
    STARTUPINFOA startup{};
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessA(executable, command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process)) {
        std::fprintf(stderr, "FAIL: could not start shutdown case %s, error=%lu\n", mode.c_str(), GetLastError());
        return false;
    }
    WaitForSingleObject(process.hProcess, INFINITE);
    DWORD exitCode = 1;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    if (exitCode != 0) std::fprintf(stderr, "FAIL: shutdown case %s exited %lu\n", mode.c_str(), exitCode);
    return exitCode == 0;
}

int main(int argc, char** argv) {
    if (argc == 6 && std::string(argv[1]) == "--case") {
        const std::string shimPath = FullPath(argv[3]);
        const std::string fakePath = FullPath(argv[4]);
        const std::string workerTestShimPath = FullPath(argv[5]);
        if (shimPath.empty() || fakePath.empty() || workerTestShimPath.empty()) return 3;
        const bool ok = RunChildCase(argv[2], shimPath, fakePath, workerTestShimPath);
        if (!ok) return 8;
        std::puts("PASS: native Bridge shutdown case completed");
        return 0;
    }
    const bool exceptionOnly = argc == 5 && std::string(argv[4]) == "--uninitialize-exception";
    if (argc != 4 && !exceptionOnly) return 2;
    const std::string shimPath = FullPath(argv[1]);
    const std::string fakePath = FullPath(argv[2]);
    const std::string workerTestShimPath = FullPath(argv[3]);
    if (shimPath.empty() || fakePath.empty() || workerTestShimPath.empty()) return 3;
    char executable[MAX_PATH]{};
    const DWORD executableLength = GetModuleFileNameA(nullptr, executable, MAX_PATH);
    if (!executableLength || executableLength >= MAX_PATH) return 4;
    const std::vector<std::string> cases = exceptionOnly
        ? std::vector<std::string>{"uninitialize-exception"}
        : std::vector<std::string>{"ledger", "direct-final", "worker-final"};
    for (const std::string& mode : cases) {
        if (!RunChildProcess(executable, mode, shimPath, fakePath, workerTestShimPath)) return 8;
    }
    std::puts("PASS: native Bridge shutdown contract scenarios completed");
    return 0;
}
