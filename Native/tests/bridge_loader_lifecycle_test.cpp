#include <windows.h>
#include <cstdio>
#include <string>
#include "bridge_shim.h"

using LoadFn = int32_t (*)(const char*);
using UnloadFn = void (*)();
using InitializeFn = int32_t (*)(const char*);
using UninitializeFn = void (*)();
using LastErrorFn = int32_t (*)(char*, uint32_t);
using GetPhysicalDisplayFn = int32_t (*)(uint32_t, LkgBridgePhysicalDisplayInfoV1*);

static std::string full_path(const char* path) {
    char buffer[MAX_PATH]{};
    const DWORD length = GetFullPathNameA(path, MAX_PATH, buffer, nullptr);
    return length && length < MAX_PATH ? std::string(buffer, length) : std::string();
}

static bool load_exports(HMODULE module, LoadFn* load, UnloadFn* unload, InitializeFn* initialize, UninitializeFn* uninitialize, LastErrorFn* lastError) {
    *load = reinterpret_cast<LoadFn>(GetProcAddress(module, "lkg_bridge_load"));
    *unload = reinterpret_cast<UnloadFn>(GetProcAddress(module, "lkg_bridge_unload"));
    *initialize = reinterpret_cast<InitializeFn>(GetProcAddress(module, "lkg_bridge_initialize"));
    *uninitialize = reinterpret_cast<UninitializeFn>(GetProcAddress(module, "lkg_bridge_uninitialize"));
    *lastError = reinterpret_cast<LastErrorFn>(GetProcAddress(module, "lkg_bridge_last_error"));
    return *load && *unload && *initialize && *uninitialize && *lastError;
}

static bool has_error(LastErrorFn lastError, const char* needle) {
    char buffer[512]{};
    lastError(buffer, sizeof(buffer));
    return std::string(buffer).find(needle) != std::string::npos;
}

int main(int argc, char** argv) {
    if (argc != 5) return 2;
    const std::string shim = full_path(argv[1]);
    const std::string good = full_path(argv[2]);
    const std::string other = full_path(argv[3]);
    const std::string missing = full_path(argv[4]);
    if (shim.empty() || good.empty() || other.empty() || missing.empty()) return 3;
    HMODULE module = LoadLibraryExA(shim.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (!module) return 4;
    LoadFn load = nullptr; UnloadFn unload = nullptr; InitializeFn initialize = nullptr; UninitializeFn uninitialize = nullptr; LastErrorFn lastError = nullptr;
    if (!load_exports(module, &load, &unload, &initialize, &uninitialize, &lastError)) { FreeLibrary(module); return 5; }
    if (load(missing.c_str()) != -4) { FreeLibrary(module); return 6; }
    if (GetModuleHandleA("lkg_fake_bridge_missing.dll")) { FreeLibrary(module); return 10; }
    for (int cycle = 0; cycle < 100; ++cycle) {
        if (load(good.c_str()) != 0) { FreeLibrary(module); return 7; }
        if (initialize("loader-lifecycle-test") != 0) { unload(); FreeLibrary(module); return 11; }
        uninitialize();
        unload();
    }
    if (load(good.c_str()) != 0) { FreeLibrary(module); return 8; }
    if (initialize("loader-lifecycle-test") != 0) { unload(); FreeLibrary(module); return 12; }
    GetPhysicalDisplayFn getPhysical = reinterpret_cast<GetPhysicalDisplayFn>(GetProcAddress(module, "lkg_bridge_get_physical_display_v1"));
    LkgBridgePhysicalDisplayInfoV1 physical{};
    physical.structSize = sizeof(physical);
    physical.version = 1;
    if (!getPhysical || getPhysical(0, &physical) != -2 || physical.valid != 0)
    { uninitialize(); unload(); FreeLibrary(module); return 15; }
    uninitialize();
    if (!GetModuleHandleA("lkg_fake_bridge_runtime.dll")) { unload(); FreeLibrary(module); return 13; }
    if (load(other.c_str()) != -5 || !has_error(lastError, "different resident path")) { unload(); FreeLibrary(module); return 9; }
    unload();
    FreeLibrary(module);
    if (!GetModuleHandleA("lkg_fake_bridge_runtime.dll")) return 14;
    return 0;
}
