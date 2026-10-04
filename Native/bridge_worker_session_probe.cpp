#include "bridge_worker_transport.h"
#include <windows.h>
#include <d3d11.h>
#include <wrl/client.h>
#include <chrono>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>

using Microsoft::WRL::ComPtr;
using BeginFn = void* (*)(const char*, const char*, uint32_t, void*);
using PollFn = int32_t (*)(void*, LkgWorkerStatus*);
using CloseFn = int32_t (*)(void*);
using ReleaseFn = int32_t (*)(void*);
using LoadFn = int32_t (*)(const char*);
using InitializeFn = int32_t (*)(const char*);

struct Api {
    HMODULE module = nullptr;
    BeginFn begin = nullptr;
    PollFn poll = nullptr;
    CloseFn close = nullptr;
    ReleaseFn release = nullptr;
    LoadFn load = nullptr;
    InitializeFn initialize = nullptr;
};

static FILE* traceFile = nullptr;

static void Trace(const char* format, ...) {
    if (!traceFile) return;
    va_list args;
    va_start(args, format);
    std::vfprintf(traceFile, format, args);
    std::fputc('\n', traceFile);
    std::fflush(traceFile);
    va_end(args);
}

static bool LoadApi(const char* path, Api& api) {
    api.module = LoadLibraryA(path);
    if (!api.module) return false;
    api.begin = reinterpret_cast<BeginFn>(GetProcAddress(api.module, "lkg_worker_begin"));
    api.poll = reinterpret_cast<PollFn>(GetProcAddress(api.module, "lkg_worker_poll"));
    api.close = reinterpret_cast<CloseFn>(GetProcAddress(api.module, "lkg_worker_close"));
    api.release = reinterpret_cast<ReleaseFn>(GetProcAddress(api.module, "lkg_worker_release"));
    api.load = reinterpret_cast<LoadFn>(GetProcAddress(api.module, "lkg_bridge_load"));
    api.initialize = reinterpret_cast<InitializeFn>(GetProcAddress(api.module, "lkg_bridge_initialize"));
    return api.begin && api.poll && api.close && api.release && api.load && api.initialize;
}

static bool CreateProducer(ComPtr<ID3D11Device>& device) {
    ComPtr<IDXGIFactory1> factory;
    ComPtr<IDXGIAdapter1> adapter;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory)))) return false;
    if (factory->EnumAdapters1(0, &adapter) != S_OK) return false;
    const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
    D3D_FEATURE_LEVEL selected{};
    return SUCCEEDED(D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                                       D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2,
                                       D3D11_SDK_VERSION, &device, &selected, nullptr));
}

static bool WaitForState(Api& api, void* handle, uint32_t expected, LkgWorkerStatus& result, bool& sawReady) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (std::chrono::steady_clock::now() < deadline) {
        if (api.poll(handle, &result) != 0) return false;
        if (result.state == LKG_WORKER_READY) sawReady = true;
        if (result.state == expected) return true;
        if (result.state == LKG_WORKER_FAULT && expected != LKG_WORKER_FAULT) return false;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return false;
}

static int32_t ReleaseWhenFinished(Api& api, void* handle) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (std::chrono::steady_clock::now() < deadline) {
        const int32_t result = api.release(handle);
        if (result != -2) return result;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return -2;
}

static bool ValidSession(Api& api, ID3D11Device* producer, const char* runtime, uint32_t ordinal, bool closeImmediately, LkgWorkerStatus& finalStatus) {
    void* handle = api.begin(runtime, "ViewWeaveWorkerSessionProbe", ordinal, producer);
    if (!handle) return false;
    bool sawReady = false;
    if (closeImmediately) {
        if (api.close(handle) != 0) return false;
    } else if (!WaitForState(api, handle, LKG_WORKER_READY, finalStatus, sawReady)) {
        api.close(handle);
        WaitForState(api, handle, LKG_WORKER_CLOSED, finalStatus, sawReady);
        api.release(handle);
        return false;
    }
    if (!closeImmediately && api.close(handle) != 0) {
        api.release(handle);
        return false;
    }
    const bool closed = WaitForState(api, handle, LKG_WORKER_CLOSED, finalStatus, sawReady);
    const int32_t released = closed ? ReleaseWhenFinished(api, handle) : -9;
    Trace("valid ordinal=%u closed=%d release=%d state=%u ready=%d profile=%ux%u", ordinal, closed ? 1 : 0, released, finalStatus.state, sawReady ? 1 : 0, finalStatus.width, finalStatus.height);
    return closed && released == 0 && (!closeImmediately || !sawReady);
}

static bool FaultSession(Api& api, ID3D11Device* producer, const char* runtime, uint32_t ordinal, const char* name) {
    void* handle = api.begin(runtime, "ViewWeaveWorkerSessionProbe", ordinal, producer);
    if (!handle) return false;
    LkgWorkerStatus status{};
    bool sawReady = false;
    const bool fault = WaitForState(api, handle, LKG_WORKER_FAULT, status, sawReady);
    const int32_t released = fault ? ReleaseWhenFinished(api, handle) : -9;
    Trace("%s fault=%d release=%d state=%u error=%u text=%s", name, fault ? 1 : 0, released, status.state, status.lastErrorCode, status.error);
    return fault && released == 0;
}

static bool DoubleBeginSession(Api& api, ID3D11Device* producer, const char* runtime) {
    void* first = api.begin(runtime, "ViewWeaveWorkerSessionProbe", 0, producer);
    void* second = api.begin(runtime, "ViewWeaveWorkerSessionProbe", 0, producer);
    const int32_t directLoad = api.load("Z:\\missing\\bridge_inproc.dll");
    const int32_t directInitialize = api.initialize("ViewWeaveWorkerSessionProbe");
    const int32_t earlyRelease = first ? api.release(first) : -9;
    const bool doubleBegin = first && !second;
    if (!first) return false;
    api.close(first);
    LkgWorkerStatus closeStatus{};
    bool sawReady = false;
    const bool closed = WaitForState(api, first, LKG_WORKER_CLOSED, closeStatus, sawReady);
    const int32_t releaseResult = closed ? ReleaseWhenFinished(api, first) : -9;
    Trace("doublebegin rejected=%d direct_load=%d direct_initialize=%d early_release=%d closed=%d final_release=%d", doubleBegin ? 1 : 0, directLoad, directInitialize, earlyRelease, closed ? 1 : 0, releaseResult);
    return doubleBegin && directLoad == -6 && directInitialize == -6 && earlyRelease == -2 && closed && releaseResult == 0;
}

int main(int argc, char** argv) {
    if (argc < 3) {
        std::fprintf(stderr, "usage: bridge_worker_session_probe shim.dll bridge_inproc.dll\n");
        return 2;
    }
    const char* tracePath = std::getenv("LKG_WORKER_SESSION_TRACE");
    if (tracePath && tracePath[0]) fopen_s(&traceFile, tracePath, "w");
    Api api;
    if (!LoadApi(argv[1], api)) return 3;
    ComPtr<ID3D11Device> producer;
    if (!CreateProducer(producer)) return 4;
    const char* childCase = std::getenv("LKG_WORKER_SESSION_CASE");
    if (childCase && childCase[0]) {
        const int caseNumber = std::atoi(childCase);
        LkgWorkerStatus status{};
        bool passed = false;
        if (caseNumber == 1) passed = ValidSession(api, producer.Get(), argv[2], 0, false, status);
        if (caseNumber == 2) passed = FaultSession(api, producer.Get(), argv[2], 9999, "invalidordinal");
        if (caseNumber == 3) passed = ValidSession(api, producer.Get(), argv[2], 0, true, status);
        if (caseNumber == 4) passed = DoubleBeginSession(api, producer.Get(), argv[2]);
        if (caseNumber == 5) passed = FaultSession(api, producer.Get(), "Z:\\missing\\bridge_inproc.dll", 0, "badpath");
        if (traceFile) std::fclose(traceFile);
        FreeLibrary(api.module);
        return passed ? 0 : 1;
    }
    char executablePath[MAX_PATH]{};
    if (!GetModuleFileNameA(nullptr, executablePath, MAX_PATH)) return 5;
    if (traceFile) {
        std::fclose(traceFile);
        traceFile = nullptr;
    }
    const char* traceBase = std::getenv("LKG_WORKER_SESSION_TRACE");
    int childFailures = 0;
    for (int caseNumber = 1; caseNumber <= 5; ++caseNumber) {
        char commandLine[32768]{};
        std::snprintf(commandLine, sizeof(commandLine), "\"%s\" \"%s\" \"%s\"", executablePath, argv[1], argv[2]);
        char caseValue[16]{};
        std::snprintf(caseValue, sizeof(caseValue), "%d", caseNumber);
        _putenv_s("LKG_WORKER_SESSION_CASE", caseValue);
        if (traceBase && traceBase[0]) {
            char caseTrace[1024]{};
            std::snprintf(caseTrace, sizeof(caseTrace), "%s.case%d", traceBase, caseNumber);
            _putenv_s("LKG_WORKER_SESSION_TRACE", caseTrace);
        }
        STARTUPINFOA startup{};
        startup.cb = sizeof(startup);
        PROCESS_INFORMATION process{};
        if (!CreateProcessA(nullptr, commandLine, nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process)) {
            ++childFailures;
            continue;
        }
        const DWORD waitResult = WaitForSingleObject(process.hProcess, 10000);
        if (waitResult != WAIT_OBJECT_0) {
            TerminateProcess(process.hProcess, 1);
            ++childFailures;
        } else {
            DWORD exitCode = 1;
            GetExitCodeProcess(process.hProcess, &exitCode);
            std::printf("case=%d exit=%lu\n", caseNumber, static_cast<unsigned long>(exitCode));
            if (exitCode != 0) ++childFailures;
        }
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
    }
    _putenv_s("LKG_WORKER_SESSION_CASE", "");
    if (traceBase && traceBase[0]) {
        FILE* resultTrace = nullptr;
        if (fopen_s(&resultTrace, traceBase, "w") == 0 && resultTrace) {
            std::fprintf(resultTrace, "result failures=%d\n", childFailures);
            std::fclose(resultTrace);
        }
    }
    FreeLibrary(api.module);
    std::printf("worker_session_probe failures=%d exit=%d\n", childFailures, childFailures ? 1 : 0);
    return childFailures ? 1 : 0;
#if 0
    int failures = 0;
    LkgWorkerStatus status{};
    failures += ValidSession(api, producer.Get(), argv[2], 0, false, status) ? 0 : 1;
    failures += FaultSession(api, producer.Get(), argv[2], 9999, "invalidordinal") ? 0 : 1;
    failures += ValidSession(api, producer.Get(), argv[2], 0, true, status) ? 0 : 1;
    void* first = api.begin(argv[2], "ViewWeaveWorkerSessionProbe", 0, producer.Get());
    void* second = api.begin(argv[2], "ViewWeaveWorkerSessionProbe", 0, producer.Get());
    const int32_t directLoad = api.load("Z:\\missing\\bridge_inproc.dll");
    const int32_t directInitialize = api.initialize("ViewWeaveWorkerSessionProbe");
    const int32_t earlyRelease = first ? api.release(first) : -9;
    bool doubleBegin = first && !second;
    if (first) {
        api.close(first);
        LkgWorkerStatus closeStatus{};
        bool sawReady = false;
        const bool closed = WaitForState(api, first, LKG_WORKER_CLOSED, closeStatus, sawReady);
        const int32_t releaseResult = closed ? api.release(first) : -9;
        Trace("doublebegin rejected=%d direct_load=%d direct_initialize=%d early_release=%d closed=%d final_release=%d", doubleBegin ? 1 : 0, directLoad, directInitialize, earlyRelease, closed ? 1 : 0, releaseResult);
        if (!doubleBegin || directLoad != -6 || directInitialize != -6 || earlyRelease != -2 || !closed || releaseResult != 0) ++failures;
    } else {
        ++failures;
    }
    failures += FaultSession(api, producer.Get(), "Z:\\missing\\bridge_inproc.dll", 0, "badpath") ? 0 : 1;
    if (traceFile) {
        std::fprintf(traceFile, "result failures=%d\n", failures);
        std::fclose(traceFile);
    }
    if (api.module) FreeLibrary(api.module);
    std::printf("worker_session_probe failures=%d exit=%d\n", failures, failures ? 1 : 0);
    return failures ? 1 : 0;
#endif
}
