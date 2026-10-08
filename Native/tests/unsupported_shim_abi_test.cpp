#include <dlfcn.h>

#include <cstdio>
#include <cstring>
#include <cstdlib>
#include "bridge_shim.h"
#include "bridge_worker_transport.h"

static void Check(bool condition, const char* message) {
    if (condition) return;
    std::fprintf(stderr, "FAIL %s\n", message);
    std::exit(1);
}

int main(int argc, char** argv) {
    Check(argc == 2, "library path argument");
    void* library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    Check(library != nullptr, "unsupported shim loads");
    const char* symbols[] = {
        "lkg_bridge_load", "lkg_bridge_unload", "lkg_bridge_initialize", "lkg_bridge_uninitialize",
        "lkg_bridge_is_idle", "lkg_bridge_last_error", "lkg_bridge_successful_draw_count",
        "lkg_bridge_is_initialized", "lkg_bridge_graphics_api", "lkg_bridge_version",
        "lkg_bridge_get_display_count", "lkg_bridge_get_display", "lkg_bridge_get_physical_display_v1", "lkg_bridge_create_d3d11_session",
        "lkg_bridge_destroy_session", "lkg_bridge_get_session_close_status_v1", "lkg_bridge_ack_session_close_v1",
        "lkg_bridge_render_event_func", "lkg_bridge_make_draw_data",
        "lkg_bridge_make_close_data", "lkg_bridge_smoke_draw", "lkg_worker_begin", "lkg_worker_poll",
        "lkg_worker_close", "lkg_worker_reset_transfer", "lkg_worker_release", "lkg_worker_make_draw_data",
        "lkg_worker_cancel_draw_data", "lkg_worker_render_event_func", "lkg_worker_render_event_id",
        "lkg_worker_close_all", "lkg_worker_collect_closed", "lkg_worker_pending_count", "lkg_worker_shutdown_host",
        "lkg_worker_request_final_shutdown", "lkg_worker_get_final_shutdown_status_v1", "lkg_worker_wait_final_shutdown_v1",
        "lkg_worker_get_physical_display_v1"
    };
    for (const char* symbol : symbols) Check(dlsym(library, symbol) != nullptr, symbol);
    using LoadFn = int (*)(const char*);
    using IdleFn = int (*)();
    using ApiFn = unsigned int (*)();
    using LastErrorFn = int (*)(char*, unsigned int);
    using PendingFn = unsigned int (*)();
    using CloseAllFn = int (*)();
    using RequestFinalFn = int (*)();
    using GetFinalStatusFn = int (*)(LkgWorkerFinalShutdownStatusV1*);
    using WaitFinalFn = int (*)(unsigned int, LkgWorkerFinalShutdownStatusV1*);
    using GetCloseStatusFn = int32_t (*)(uint64_t, LkgBridgeSessionCloseStatusV1*);
    using AckCloseFn = int32_t (*)(uint64_t);
    auto load = reinterpret_cast<LoadFn>(dlsym(library, "lkg_bridge_load"));
    auto idle = reinterpret_cast<IdleFn>(dlsym(library, "lkg_bridge_is_idle"));
    auto graphicsApi = reinterpret_cast<ApiFn>(dlsym(library, "lkg_bridge_graphics_api"));
    auto lastError = reinterpret_cast<LastErrorFn>(dlsym(library, "lkg_bridge_last_error"));
    auto pending = reinterpret_cast<PendingFn>(dlsym(library, "lkg_worker_pending_count"));
    auto closeAll = reinterpret_cast<CloseAllFn>(dlsym(library, "lkg_worker_close_all"));
    auto requestFinal = reinterpret_cast<RequestFinalFn>(dlsym(library, "lkg_worker_request_final_shutdown"));
    auto getFinalStatus = reinterpret_cast<GetFinalStatusFn>(dlsym(library, "lkg_worker_get_final_shutdown_status_v1"));
    auto waitFinal = reinterpret_cast<WaitFinalFn>(dlsym(library, "lkg_worker_wait_final_shutdown_v1"));
    auto getCloseStatus = reinterpret_cast<GetCloseStatusFn>(dlsym(library, "lkg_bridge_get_session_close_status_v1"));
    auto ackClose = reinterpret_cast<AckCloseFn>(dlsym(library, "lkg_bridge_ack_session_close_v1"));
    char error[64]{};
    Check(load("unused") < 0, "load reports unsupported");
    Check(idle() == 1, "unsupported shim remains idle");
    Check(graphicsApi() == 0, "unsupported graphics api is unknown");
    Check(lastError(error, sizeof(error)) < 0 && std::strcmp(error, "unsupported graphics backend") == 0,
          "unsupported diagnostic");
    Check(pending() == 0 && closeAll() == 0, "unsupported worker stays empty");
    LkgWorkerFinalShutdownStatusV1 finalStatus{};
    finalStatus.structSize = sizeof(finalStatus);
    finalStatus.version = 1;
    Check(requestFinal() == -2, "unsupported final shutdown is rejected");
    Check(getFinalStatus(&finalStatus) == 0 && finalStatus.state == LKG_WORKER_FINAL_SHUTDOWN_FAULT && finalStatus.lastResult == -2,
          "unsupported final shutdown status remains explicitly faulted");
    finalStatus.structSize = sizeof(finalStatus);
    finalStatus.version = 1;
    Check(waitFinal(0, &finalStatus) == -2 && finalStatus.state == LKG_WORKER_FINAL_SHUTDOWN_FAULT,
          "unsupported final wait never reports success");
    LkgBridgeSessionCloseStatusV1 closeStatus{};
    closeStatus.structSize = sizeof(closeStatus);
    closeStatus.version = 1;
    Check(getCloseStatus(1, &closeStatus) == -2 && closeStatus.state == LKG_BRIDGE_SESSION_CLOSE_UNKNOWN,
          "unsupported session close query never proves cleanup");
    Check(ackClose(1) == -2, "unsupported session close ACK is rejected");
    using GetPhysicalFn = int32_t (*)(uint32_t, LkgBridgePhysicalDisplayInfoV1*);
    using GetWorkerPhysicalFn = int32_t (*)(void*, LkgWorkerPhysicalDisplayInfoV1*);
    auto getPhysical = reinterpret_cast<GetPhysicalFn>(dlsym(library, "lkg_bridge_get_physical_display_v1"));
    auto getWorkerPhysical = reinterpret_cast<GetWorkerPhysicalFn>(dlsym(library, "lkg_worker_get_physical_display_v1"));
    LkgBridgePhysicalDisplayInfoV1 bridgePhysical{};
    bridgePhysical.structSize = sizeof(bridgePhysical);
    bridgePhysical.version = 1;
    LkgWorkerPhysicalDisplayInfoV1 workerPhysical{};
    workerPhysical.structSize = sizeof(workerPhysical);
    workerPhysical.version = 1;
    Check(getPhysical && getPhysical(0, &bridgePhysical) == -2 && bridgePhysical.valid == 0,
          "unsupported vendor calibration is explicitly unavailable");
    Check(getWorkerPhysical && getWorkerPhysical(nullptr, &workerPhysical) == -2 && workerPhysical.valid == 0,
          "unsupported cached calibration is explicitly unavailable");
    Check(dlclose(library) == 0, "unsupported shim unloads");
    std::puts("unsupported_shim_abi_test pass");
    return 0;
}
