#include "bridge_worker_transport.h"
#include "common/bridge_metadata.h"
#include <dlfcn.h>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

extern "C" bool lkg_metal_metadata_claim();
extern "C" void lkg_metal_metadata_release(bool shutdownProven);

namespace {
struct MetadataWorker {
    std::mutex mutex;
    std::thread thread;
    std::vector<LkgWorkerDisplaySnapshotItemV1> items;
    bool finished = false, closed = false, cleanupProven = false;
    int32_t result = -1;
    std::string error;
};
std::mutex ownerMutex;
MetadataWorker* owner = nullptr;
void* pinnedShim = nullptr;
template<class T> T Symbol(void* module, const char* name) { return reinterpret_cast<T>(dlsym(module, name)); }

void Query(MetadataWorker* worker, std::string path, std::string application) {
    void* module = dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
    bool cleanup = true;
    int32_t result = -2;
    std::string error = "Bridge runtime could not be loaded";
    std::vector<LkgWorkerDisplaySnapshotItemV1> items;
    if (module) {
        auto initialize = Symbol<bool(*)(const char*)>(module, "initialize_bridge");
        auto stop = Symbol<bool(*)()>(module, "uninitialize_bridge");
        lkg::metadata::Symbols symbols{};
        symbols.displays = Symbol<lkg::metadata::GetDisplaysFn>(module, "get_displays");
        symbols.dimensions = Symbol<lkg::metadata::GetDimensionsFn>(module, "get_dimensions_for_display");
        symbols.viewcone = Symbol<lkg::metadata::GetViewconeFn>(module, "get_viewcone_for_display");
        symbols.defaultQuilt = Symbol<lkg::metadata::GetDefaultQuiltFn>(module, "get_default_quilt_settings_for_display");
        symbols.calibration = Symbol<lkg::metadata::GetCalibrationFn>(module, "get_calibration_for_display");
        bool initializeAttempted = false;
        try {
            if (!initialize || !stop || !symbols.displays || !symbols.dimensions || !symbols.defaultQuilt || !symbols.viewcone) {
                error = "Required Bridge geometry symbols are missing";
            } else {
                initializeAttempted = true;
                if (!initialize(application.c_str())) error = "Bridge initialization failed";
                else {
                    uint32_t count = 0;
                    result = lkg::metadata::ReadDisplayCount(symbols, &count);
                    if (result != 0 || count > lkg::metadata::kMaximumBridgeDisplays) {
                        result = -3; error = "Bridge display enumeration failed";
                    } else {
                        items.resize(count);
                        for (uint32_t ordinal = 0; ordinal < count; ++ordinal) {
                            auto& item = items[ordinal];
                            item.structSize = sizeof(item); item.version = 1; item.displayOrdinal = ordinal;
                            result = lkg::metadata::ReadDisplay(symbols, ordinal, &item.profile);
                            if (result != 0) { error = "Bridge display geometry query failed"; break; }
                            LkgBridgePhysicalDisplayInfoV1 physical{};
                            physical.structSize = sizeof(physical); physical.version = 1;
                            lkg::metadata::ReadPhysicalDisplay(symbols, item.profile.index, &physical);
                            static_assert(sizeof(physical) == sizeof(item.physical), "Physical metadata ABI mismatch");
                            std::memcpy(&item.physical, &physical, sizeof(physical));
                        }
                        if (result == 0) error.clear();
                    }
                }
            }
        } catch (...) { result = -4; error = "Bridge geometry query raised an exception"; }
        // A failed initialization may still own SDK state. Never unload an unproven owner.
        if (initializeAttempted) {
            try { cleanup = stop(); } catch (...) { cleanup = false; }
        }
        if (cleanup) dlclose(module);
        else { result = -5; error = "Bridge metadata shutdown failed; restart Unity before retrying"; }
    }
    if (!cleanup) lkg_metal_metadata_release(false);
    std::lock_guard<std::mutex> lock(worker->mutex);
    if (result == 0 && cleanup) worker->items = std::move(items);
    worker->result = result; worker->error = std::move(error);
    worker->cleanupProven = cleanup; worker->finished = true;
}
}

extern "C" LKG_EXPORT void* lkg_worker_begin_metadata_v1(const char* path, const char* application) {
    if (!path || !*path || !application || !*application) return nullptr;
    std::lock_guard<std::mutex> lock(ownerMutex);
    if (!pinnedShim) {
        Dl_info image{};
        if (!dladdr(reinterpret_cast<void*>(&lkg_worker_begin_metadata_v1), &image) || !image.dli_fname) return nullptr;
        // Retained for the process: reload cannot unload code used by a query or recovery handle.
        pinnedShim = dlopen(image.dli_fname, RTLD_NOW | RTLD_LOCAL);
        if (!pinnedShim) return nullptr;
    }
    if (owner || !lkg_metal_metadata_claim()) return nullptr;
    try {
        owner = new MetadataWorker();
        owner->thread = std::thread(Query, owner, std::string(path), std::string(application));
        return owner;
    } catch (...) {
        delete owner; owner = nullptr; lkg_metal_metadata_release(true); return nullptr;
    }
}

extern "C" LKG_EXPORT int32_t lkg_worker_close(void* handle) {
    std::lock_guard<std::mutex> lock(ownerMutex);
    if (!owner || owner != handle) return -1;
    std::lock_guard<std::mutex> stateLock(owner->mutex);
    owner->closed = true;
    return 0;
}

extern "C" LKG_EXPORT int32_t lkg_worker_get_display_snapshot_v1(void* handle,
    LkgWorkerDisplaySnapshotStatusV1* status, LkgWorkerDisplaySnapshotItemV1* items,
    uint32_t capacity, uint32_t* requiredCount) {
    if (!status || status->structSize != sizeof(*status) || status->version != 1 || !requiredCount) return -1;
    std::lock_guard<std::mutex> lock(ownerMutex);
    if (!owner || owner != handle) return -1;
    std::lock_guard<std::mutex> stateLock(owner->mutex);
    status->state = owner->closed && owner->finished ? LKG_WORKER_CLOSED : LKG_WORKER_READY;
    status->pending = owner->finished ? 0 : 1;
    status->cleanupFinished = owner->finished && owner->cleanupProven ? 1 : 0;
    status->requestedGeneration = 1; status->snapshotGeneration = owner->finished ? 1 : 0;
    status->result = owner->finished ? owner->result : 0;
    status->itemCount = *requiredCount = static_cast<uint32_t>(owner->items.size());
    std::snprintf(status->error, sizeof(status->error), "%s", owner->error.c_str());
    if (capacity < *requiredCount || (*requiredCount && !items)) return -1;
    if (*requiredCount) std::memcpy(items, owner->items.data(), *requiredCount * sizeof(*items));
    return 0;
}

extern "C" LKG_EXPORT int32_t lkg_worker_end_metadata_v1(void* handle) {
    std::lock_guard<std::mutex> lock(ownerMutex);
    if (!owner || owner != handle) return -1;
    {
        std::lock_guard<std::mutex> stateLock(owner->mutex);
        if (!owner->closed || !owner->finished || !owner->cleanupProven) return -3;
    }
    owner->thread.join(); delete owner; owner = nullptr;
    lkg_metal_metadata_release(true);
    return 0;
}
