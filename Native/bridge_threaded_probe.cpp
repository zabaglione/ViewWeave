#include "bridge_shim.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <condition_variable>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <deque>
#include <filesystem>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;

struct Slot {
    ComPtr<ID3D11Texture2D> producerTexture;
    ComPtr<IDXGIKeyedMutex> producerMutex;
    HANDLE sharedHandle = nullptr;
    ComPtr<ID3D11RenderTargetView> producerView;
};

struct Job {
    uint32_t slot = 0;
    uint32_t frame = 0;
};

struct Queue {
    std::mutex mutex;
    std::condition_variable condition;
    std::deque<Job> jobs;
    bool closing = false;
    bool workerReady = false;
    int workerResult = -1;
    uint32_t produced = 0;
    uint32_t consumed = 0;
    uint32_t drawDelta = 0;
    int idleState = -1;
    int initializedState = -1;
};

static std::mutex traceMutex;

static void Trace(const char* stage) {
    const char* path = std::getenv("LKG_THREADED_TRACE");
    if (!path || !path[0]) return;
    std::lock_guard<std::mutex> lock(traceMutex);
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file, "thread=%lu stage=%s\n", static_cast<unsigned long>(GetCurrentThreadId()), stage);
    std::fclose(file);
}

static std::string AsciiPath(const std::string& path) {
    std::string result = path;
    for (char& value : result)
        if (static_cast<unsigned char>(value) < 32 || static_cast<unsigned char>(value) > 126) value = '?';
    return result;
}

using LoadFn = int32_t (*)(const char*);
using UnloadFn = void (*)();
using InitializeFn = int32_t (*)(const char*);
using UninitializeFn = void (*)();
using CountFn = int32_t (*)(uint32_t*);
using DisplayFn = int32_t (*)(uint32_t, LkgBridgeDisplayInfo*);
using CreateSessionFn = uint64_t (*)(void*, uint32_t);
using DestroySessionFn = int32_t (*)(uint64_t);
using DrawFn = int32_t (*)(uint64_t, void*, uint32_t, uint32_t, float, float);
using CloseDataFn = void* (*)(uint64_t);
using GetEventFn = void* (*)();
using EventFn = void (*)(int, void*);
using IdleFn = int32_t (*)();
using InitializedFn = int32_t (*)();
using DrawCountFn = uint32_t (*)();
using ErrorFn = int32_t (*)(char*, uint32_t);

struct BridgeApi {
    HMODULE module = nullptr;
    LoadFn load = nullptr;
    UnloadFn unload = nullptr;
    InitializeFn initialize = nullptr;
    UninitializeFn uninitialize = nullptr;
    CountFn count = nullptr;
    DisplayFn display = nullptr;
    CreateSessionFn createSession = nullptr;
    DestroySessionFn destroySession = nullptr;
    DrawFn draw = nullptr;
    CloseDataFn closeData = nullptr;
    GetEventFn getEvent = nullptr;
    ErrorFn error = nullptr;
    IdleFn idle = nullptr;
    InitializedFn initialized = nullptr;
    DrawCountFn drawCount = nullptr;
};

static void TraceApiFailure(const char* stage, int32_t result, const BridgeApi& api, const std::string& path) {
    char error[512] = {};
    if (api.error) api.error(error, sizeof(error));
    const auto safePath = AsciiPath(path);
    const auto safeError = AsciiPath(error);
    char line[1200] = {};
    std::snprintf(line, sizeof(line), "%s rc=%d last_error=%s win32=%lu path=%s", stage, result, safeError.c_str(), static_cast<unsigned long>(GetLastError()), safePath.c_str());
    Trace(line);
}

static std::string ErrorText(const BridgeApi& api) {
    char text[512] = {};
    if (api.error) api.error(text, sizeof(text));
    return text;
}

static std::string FindBridgePath(int argc, char** argv) {
    if (argc > 2 && argv[2] && argv[2][0]) return argv[2];
    if (argc == 2 && argv[1] && argv[1][0]) return argv[1];
    const char* overridePath = std::getenv("LKG_BRIDGE_DLL");
    if (overridePath && overridePath[0]) return overridePath;
    const char* programFiles = std::getenv("ProgramFiles");
    if (!programFiles) return {};
    std::error_code error;
    const auto root = std::filesystem::path(programFiles) / "Looking Glass";
    for (const auto& entry : std::filesystem::recursive_directory_iterator(root, error)) {
        if (error) break;
        if (!entry.is_regular_file(error)) continue;
        const auto name = entry.path().filename().string();
        if (name == "bridge_inproc.dll") return entry.path().string();
    }
    return {};
}

static std::string FindShimPath(int argc, char** argv) {
    if (argc > 2 && argv[1] && argv[1][0]) return argv[1];
    const char* overridePath = std::getenv("LKG_BRIDGE_SHIM");
    if (overridePath && overridePath[0]) return overridePath;
    char modulePath[MAX_PATH] = {};
    if (GetModuleFileNameA(nullptr, modulePath, MAX_PATH) == 0) return "lookingglass_bridge_shim.dll";
    std::string result = modulePath;
    const auto slash = result.find_last_of("\\/");
    if (slash == std::string::npos) return "lookingglass_bridge_shim.dll";
    result.resize(slash + 1);
    return result + "lookingglass_bridge_shim.dll";
}

static bool LoadApi(BridgeApi& api, const std::string& path) {
    api.module = LoadLibraryA(path.c_str());
    if (!api.module) return false;
    api.load = reinterpret_cast<LoadFn>(GetProcAddress(api.module, "lkg_bridge_load"));
    api.unload = reinterpret_cast<UnloadFn>(GetProcAddress(api.module, "lkg_bridge_unload"));
    api.initialize = reinterpret_cast<InitializeFn>(GetProcAddress(api.module, "lkg_bridge_initialize"));
    api.uninitialize = reinterpret_cast<UninitializeFn>(GetProcAddress(api.module, "lkg_bridge_uninitialize"));
    api.count = reinterpret_cast<CountFn>(GetProcAddress(api.module, "lkg_bridge_get_display_count"));
    api.display = reinterpret_cast<DisplayFn>(GetProcAddress(api.module, "lkg_bridge_get_display"));
    api.createSession = reinterpret_cast<CreateSessionFn>(GetProcAddress(api.module, "lkg_bridge_create_d3d11_session"));
    api.destroySession = reinterpret_cast<DestroySessionFn>(GetProcAddress(api.module, "lkg_bridge_destroy_session"));
    api.draw = reinterpret_cast<DrawFn>(GetProcAddress(api.module, "lkg_bridge_smoke_draw"));
    api.closeData = reinterpret_cast<CloseDataFn>(GetProcAddress(api.module, "lkg_bridge_make_close_data"));
    api.getEvent = reinterpret_cast<GetEventFn>(GetProcAddress(api.module, "lkg_bridge_render_event_func"));
    api.error = reinterpret_cast<ErrorFn>(GetProcAddress(api.module, "lkg_bridge_last_error"));
    api.idle = reinterpret_cast<IdleFn>(GetProcAddress(api.module, "lkg_bridge_is_idle"));
    api.initialized = reinterpret_cast<InitializedFn>(GetProcAddress(api.module, "lkg_bridge_is_initialized"));
    api.drawCount = reinterpret_cast<DrawCountFn>(GetProcAddress(api.module, "lkg_bridge_successful_draw_count"));
    return api.load && api.unload && api.initialize && api.uninitialize && api.count && api.display && api.createSession && api.destroySession && api.draw && api.closeData && api.getEvent && api.idle && api.initialized && api.drawCount;
}

static bool CreateDevices(ComPtr<ID3D11Device>& producer, ComPtr<ID3D11DeviceContext>& producerContext, ComPtr<ID3D11Device>& worker, ComPtr<ID3D11DeviceContext>& workerContext) {
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
    ComPtr<IDXGIAdapter1> adapter;
    if (factory->EnumAdapters1(0, &adapter) != S_OK) return false;
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL selected{};
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, levels, 2, D3D11_SDK_VERSION, &producer, &selected, &producerContext))) return false;
    if (FAILED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, levels, 2, D3D11_SDK_VERSION, &worker, &selected, &workerContext))) return false;
    return true;
}

static bool CreateSlots(ID3D11Device* device, std::vector<Slot>& slots) {
    for (auto& slot : slots) {
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = 256;
        desc.Height = 192;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &slot.producerTexture))) return false;
        if (FAILED(slot.producerTexture.As(&slot.producerMutex))) return false;
        ComPtr<IDXGIResource> resource;
        if (FAILED(slot.producerTexture.As(&resource))) return false;
        if (FAILED(resource->GetSharedHandle(&slot.sharedHandle))) return false;
        if (FAILED(device->CreateRenderTargetView(slot.producerTexture.Get(), nullptr, &slot.producerView))) return false;
    }
    return true;
}

static bool OpenWorkerSlots(ID3D11Device* device, const std::vector<Slot>& source, std::vector<ComPtr<ID3D11Texture2D>>& workerTextures, std::vector<ComPtr<IDXGIKeyedMutex>>& workerMutexes) {
    for (const auto& slot : source) {
        ComPtr<ID3D11Texture2D> texture;
        if (FAILED(device->OpenSharedResource(slot.sharedHandle, IID_PPV_ARGS(&texture)))) return false;
        ComPtr<IDXGIKeyedMutex> mutex;
        if (FAILED(texture.As(&mutex))) return false;
        workerTextures.push_back(texture);
        workerMutexes.push_back(mutex);
    }
    return true;
}

static int Worker(BridgeApi api, const std::string& bridgePath, ID3D11Device* workerDevice, const std::vector<ComPtr<ID3D11Texture2D>>& textures, const std::vector<ComPtr<IDXGIKeyedMutex>>& mutexes, Queue& queue, uint32_t columns, uint32_t rows, float aspect) {
    Trace("worker_start");
    std::error_code pathError;
    const std::string normalizedBridgePath = std::filesystem::absolute(std::filesystem::path(bridgePath), pathError).make_preferred().string();
    const std::string loadPath = pathError ? bridgePath : normalizedBridgePath;
    auto fail = [&](int code, const char* stage) {
        TraceApiFailure(stage, code, api, bridgePath);
        std::lock_guard<std::mutex> lock(queue.mutex);
        queue.workerReady = true;
        queue.workerResult = code;
        queue.closing = true;
        queue.condition.notify_all();
        return code;
    };
    uint32_t displayCount = 0;
    const int loadResult = api.load(loadPath.c_str());
    if (loadResult) return fail(loadResult, "bridge_load_failed");
    Trace("bridge_loaded");
    const int initializeResult = api.initialize("ViewWeaveThreadedProbe");
    if (initializeResult) { const int result = fail(initializeResult, "bridge_initialize_failed"); api.unload(); return result; }
    Trace("bridge_initialized");
    const int countResult = api.count(&displayCount);
    if (countResult || displayCount == 0) { const int result = fail(countResult ? countResult : 12, "display_count_failed"); api.uninitialize(); api.unload(); return result; }
    LkgBridgeDisplayInfo display{};
    const int displayResult = api.display(0, &display);
    if (displayResult) { const int result = fail(displayResult, "display_profile_failed"); api.uninitialize(); api.unload(); return result; }
    uint64_t session = api.createSession(workerDevice, display.index);
    if (!session) { const int result = fail(14, "session_create_failed"); api.uninitialize(); api.unload(); return result; }
    Trace("session_created");
    const uint32_t startDraws = api.drawCount();
    {
        std::lock_guard<std::mutex> lock(queue.mutex);
        queue.workerReady = true;
        queue.workerResult = 0;
    }
    queue.condition.notify_all();
    int result = 0;
    while (true) {
        Job job{};
        {
            std::unique_lock<std::mutex> lock(queue.mutex);
            queue.condition.wait(lock, [&] { return queue.closing || !queue.jobs.empty(); });
            if (queue.jobs.empty() && queue.closing) break;
            job = queue.jobs.front();
            queue.jobs.pop_front();
        }
        if (mutexes[job.slot]->AcquireSync(1, 100) != WAIT_OBJECT_0) { result = 20; break; }
        Trace("acquire_worker");
        if (api.draw(session, textures[job.slot].Get(), columns, rows, aspect, 1.0f)) { result = 21; mutexes[job.slot]->ReleaseSync(0); break; }
        mutexes[job.slot]->ReleaseSync(0);
        Trace("draw_release_worker");
        {
            std::lock_guard<std::mutex> lock(queue.mutex);
            ++queue.consumed;
        }
    }
    void* closeData = api.closeData(session);
    EventFn event = reinterpret_cast<EventFn>(api.getEvent());
    if (closeData && event) event(1, closeData);
    api.destroySession(session);
    api.uninitialize();
    const uint32_t drawDelta = api.drawCount() - startDraws;
    const int idleState = api.idle();
    const int initializedState = api.initialized();
    if (idleState != 1 || initializedState != 0 || drawDelta != 120) result = result == 0 ? 22 : result;
    Trace("worker_stop");
    {
        std::lock_guard<std::mutex> lock(queue.mutex);
        queue.workerResult = result;
        queue.drawDelta = drawDelta;
        queue.idleState = idleState;
        queue.initializedState = initializedState;
        queue.closing = true;
    }
    queue.condition.notify_all();
    api.unload();
    return result;
}

int main(int argc, char** argv) {
    Trace("producer_start");
    const auto path = FindBridgePath(argc, argv);
    if (path.empty()) { std::fprintf(stderr, "bridge dll path not found\n"); return 2; }
    const std::string shimPath = FindShimPath(argc, argv);
    ComPtr<ID3D11Device> producer;
    ComPtr<ID3D11DeviceContext> producerContext;
    ComPtr<ID3D11Device> worker;
    ComPtr<ID3D11DeviceContext> workerContext;
    if (!CreateDevices(producer, producerContext, worker, workerContext)) { std::fprintf(stderr, "d3d11 device creation failed\n"); return 3; }
    std::vector<Slot> slots(3);
    if (!CreateSlots(producer.Get(), slots)) { std::fprintf(stderr, "shared slot creation failed\n"); return 4; }
    std::vector<ComPtr<ID3D11Texture2D>> workerTextures;
    std::vector<ComPtr<IDXGIKeyedMutex>> workerMutexes;
    if (!OpenWorkerSlots(worker.Get(), slots, workerTextures, workerMutexes)) { std::fprintf(stderr, "shared slot open failed\n"); return 5; }
    Queue queue;
    BridgeApi api;
    if (!LoadApi(api, shimPath)) { std::fprintf(stderr, "bridge api load failed\n"); return 6; }
    int workerResult = -1;
    std::thread workerThread([&] { workerResult = Worker(api, path, worker.Get(), workerTextures, workerMutexes, queue, 4, 2, 4.0f / 3.0f); });
    {
        std::unique_lock<std::mutex> lock(queue.mutex);
        if (!queue.condition.wait_for(lock, std::chrono::seconds(5), [&] { return queue.workerReady || queue.closing; })) { std::fprintf(stderr, "worker readiness timeout\n"); queue.closing = true; }
        if (queue.closing && queue.workerResult != 0) { lock.unlock(); queue.condition.notify_all(); workerThread.join(); return queue.workerResult; }
    }
    for (uint32_t frame = 0; frame < 120; ++frame) {
        const uint32_t slotIndex = frame % 3;
        {
            std::unique_lock<std::mutex> lock(queue.mutex);
            if (!queue.condition.wait_for(lock, std::chrono::seconds(5), [&] { return queue.jobs.size() < 3 || queue.closing; })) { std::fprintf(stderr, "queue capacity timeout\n"); queue.closing = true; break; }
            if (queue.closing) break;
        }
        bool acquired = false;
        for (int attempt = 0; attempt < 50 && !acquired; ++attempt)
        {
            if (slots[slotIndex].producerMutex->AcquireSync(0, 100) == WAIT_OBJECT_0) acquired = true;
            std::lock_guard<std::mutex> lock(queue.mutex);
            if (queue.closing) break;
        }
        if (!acquired) { std::fprintf(stderr, "producer acquire timeout\n"); std::lock_guard<std::mutex> lock(queue.mutex); queue.closing = true; break; }
        Trace("acquire_producer");
        const float color[4] = { (frame % 2) ? 0.1f : 0.8f, 0.2f, 0.3f, 1.0f };
        producerContext->ClearRenderTargetView(slots[slotIndex].producerView.Get(), color);
        slots[slotIndex].producerMutex->ReleaseSync(1);
        Trace("release_producer");
        {
            std::lock_guard<std::mutex> lock(queue.mutex);
            queue.jobs.push_back(Job{ slotIndex, frame });
            ++queue.produced;
        }
        queue.condition.notify_one();
    }
    {
        std::lock_guard<std::mutex> lock(queue.mutex);
        queue.closing = true;
    }
    queue.condition.notify_one();
    workerThread.join();
    const int finalResult = workerResult || queue.produced != 120 || queue.consumed != 120 ? 30 : 0;
    std::printf("threaded_probe_frames=%u consumed=%u draw_delta=%u idle=%d initialized=%d slots=3 exit=%d\n", queue.produced, queue.consumed, queue.drawDelta, queue.idleState, queue.initializedState, finalResult);
    const char* resultPath = std::getenv("LKG_THREADED_RESULT");
    if (!resultPath || !resultPath[0]) resultPath = "threaded_probe_result.log";
    FILE* resultFile = nullptr;
    if (fopen_s(&resultFile, resultPath, "w") == 0 && resultFile) {
        std::fprintf(resultFile, "produced=%u consumed=%u draw_delta=%u idle=%d initialized=%d worker_result=%d exit=%d\n", queue.produced, queue.consumed, queue.drawDelta, queue.idleState, queue.initializedState, workerResult, finalResult);
        std::fclose(resultFile);
    }
    return finalResult;
}
