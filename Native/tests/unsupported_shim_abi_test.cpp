#include <dlfcn.h>

#include <cstdio>
#include <cstring>
#include <cstdlib>

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
        "lkg_bridge_get_display_count", "lkg_bridge_get_display", "lkg_bridge_create_d3d11_session",
        "lkg_bridge_destroy_session", "lkg_bridge_render_event_func", "lkg_bridge_make_draw_data",
        "lkg_bridge_make_close_data", "lkg_bridge_smoke_draw", "lkg_worker_begin", "lkg_worker_poll",
        "lkg_worker_close", "lkg_worker_reset_transfer", "lkg_worker_release", "lkg_worker_make_draw_data",
        "lkg_worker_cancel_draw_data", "lkg_worker_render_event_func", "lkg_worker_render_event_id",
        "lkg_worker_close_all", "lkg_worker_collect_closed", "lkg_worker_pending_count", "lkg_worker_shutdown_host"
    };
    for (const char* symbol : symbols) Check(dlsym(library, symbol) != nullptr, symbol);
    using LoadFn = int (*)(const char*);
    using IdleFn = int (*)();
    using ApiFn = unsigned int (*)();
    using LastErrorFn = int (*)(char*, unsigned int);
    using PendingFn = unsigned int (*)();
    using CloseAllFn = int (*)();
    auto load = reinterpret_cast<LoadFn>(dlsym(library, "lkg_bridge_load"));
    auto idle = reinterpret_cast<IdleFn>(dlsym(library, "lkg_bridge_is_idle"));
    auto graphicsApi = reinterpret_cast<ApiFn>(dlsym(library, "lkg_bridge_graphics_api"));
    auto lastError = reinterpret_cast<LastErrorFn>(dlsym(library, "lkg_bridge_last_error"));
    auto pending = reinterpret_cast<PendingFn>(dlsym(library, "lkg_worker_pending_count"));
    auto closeAll = reinterpret_cast<CloseAllFn>(dlsym(library, "lkg_worker_close_all"));
    char error[64]{};
    Check(load("unused") < 0, "load reports unsupported");
    Check(idle() == 1, "unsupported shim remains idle");
    Check(graphicsApi() == 0, "unsupported graphics api is unknown");
    Check(lastError(error, sizeof(error)) < 0 && std::strcmp(error, "unsupported graphics backend") == 0,
          "unsupported diagnostic");
    Check(pending() == 0 && closeAll() == 0, "unsupported worker stays empty");
    Check(dlclose(library) == 0, "unsupported shim unloads");
    std::puts("unsupported_shim_abi_test pass");
    return 0;
}
