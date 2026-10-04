#include "bridge_shim.h"
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <windows.h>

int main(int argc, char** argv) {
    if (argc < 3) { std::fprintf(stderr, "usage: bridge_probe shim.dll bridge_inproc.dll [log]\n"); return 2; }
    std::ofstream log(argc > 3 ? argv[3] : "bridge_probe.log", std::ios::trunc);
    auto step = [&](const char* text) { log << text << "\n"; log.flush(); };
    step("start");
    char shimPath[MAX_PATH] = {}; if (!GetFullPathNameA(argv[1], MAX_PATH, shimPath, nullptr)) { std::fprintf(stderr, "shim path resolution failed\n"); return 3; }
    char bridgePath[MAX_PATH] = {}; if (!GetFullPathNameA(argv[2], MAX_PATH, bridgePath, nullptr)) { std::fprintf(stderr, "bridge path resolution failed\n"); return 3; }
    char* slash = strrchr(bridgePath, '\\'); if (slash) *slash = 0; SetDllDirectoryA(bridgePath);
    HMODULE shim = LoadLibraryExA(shimPath, nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!shim) { step("shim_load_failed"); std::fprintf(stderr, "shim load failed error=%lu\n", GetLastError()); return 3; } step("shim_loaded");
    auto load = (int32_t(*)(const char*))GetProcAddress(shim, "lkg_bridge_load");
    auto init = (int32_t(*)(const char*))GetProcAddress(shim, "lkg_bridge_initialize");
    auto version = (int32_t(*)(char*,uint32_t))GetProcAddress(shim, "lkg_bridge_version");
    auto count = (int32_t(*)(uint32_t*))GetProcAddress(shim, "lkg_bridge_get_display_count");
    auto uninit = (void(*)())GetProcAddress(shim, "lkg_bridge_uninitialize");
    auto unload = (void(*)())GetProcAddress(shim, "lkg_bridge_unload");
    if (!load || !init || !version || !count || !uninit || !unload) { std::fprintf(stderr, "shim exports missing\n"); return 4; }
    int rc = load(argv[2]); if (rc) { step("bridge_load_failed"); std::fprintf(stderr, "bridge load failed: %d\n", rc); return 5; } step("bridge_loaded");
    rc = init("LookingGlassCommunityProbe"); if (rc) { step("bridge_init_failed"); std::fprintf(stderr, "bridge init failed: %d\n", rc); unload(); return 6; } step("bridge_initialized");
    char v[64] = {}; version(v, sizeof(v)); uint32_t n = 0; rc = count(&n);
    log << "version=" << v << " displays=" << n << " count_rc=" << rc << "\n"; log.flush(); std::printf("version=%s displays=%u count_rc=%d\n", v, n, rc); std::fflush(stdout);
    uninit(); step("uninitialized"); unload(); step("unloaded"); FreeLibrary(shim); step("cleanexit"); return rc ? 7 : 0;
}
