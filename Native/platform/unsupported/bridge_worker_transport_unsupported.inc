#include "bridge_worker_transport.h"

static bool LkgPreviewLeaseIdentityValid(const LkgWorkerPreviewLeaseIdentityV1* identity) {
    return identity && identity->structSize == sizeof(LkgWorkerPreviewLeaseIdentityV1) && identity->version == 1 &&
        identity->endpointId != 0 && identity->leaseGeneration != 0;
}

extern "C" LKG_EXPORT void* lkg_worker_begin(const char*, const char*, uint32_t, void*) { return nullptr; }
extern "C" LKG_EXPORT int32_t lkg_worker_poll(void*, LkgWorkerStatus*) { return -1; }
extern "C" LKG_EXPORT int32_t lkg_worker_close(void*) { return -1; }
extern "C" LKG_EXPORT int32_t lkg_worker_reset_transfer(void*) { return -1; }
extern "C" LKG_EXPORT int32_t lkg_worker_set_preview_paused(void*, uint32_t) { return -2; }
extern "C" LKG_EXPORT int32_t lkg_worker_get_preview_pause_state(void*, uint32_t* phase) {
    if (phase) *phase = LKG_WORKER_PREVIEW_FAULTED;
    return -2;
}
extern "C" LKG_EXPORT int32_t lkg_worker_acquire_preview_lease_v1(const char* runtimePath, const char* applicationName, uint32_t, void*, void** leaseHandle,
    LkgWorkerPreviewLeaseIdentityV1* identity) {
    if (leaseHandle) *leaseHandle = nullptr;
    if (!leaseHandle || !identity || !runtimePath || !runtimePath[0] || !applicationName || !applicationName[0])
        return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    identity->structSize = sizeof(LkgWorkerPreviewLeaseIdentityV1);
    identity->version = 1;
    identity->endpointId = 0;
    identity->leaseGeneration = 0;
    return LKG_WORKER_PREVIEW_LEASE_RESULT_UNSUPPORTED;
}
extern "C" LKG_EXPORT int32_t lkg_worker_request_preview_lease_return_v1(void* handle, const LkgWorkerPreviewLeaseIdentityV1* identity) {
    if (!handle || !LkgPreviewLeaseIdentityValid(identity)) return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    return LKG_WORKER_PREVIEW_LEASE_RESULT_UNSUPPORTED;
}
extern "C" LKG_EXPORT int32_t lkg_worker_get_preview_lease_status_v1(void* handle, const LkgWorkerPreviewLeaseIdentityV1* identity,
    LkgWorkerPreviewLeaseStatusV1* status) {
    if (!handle || !LkgPreviewLeaseIdentityValid(identity) || !status) return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    status->structSize = sizeof(LkgWorkerPreviewLeaseStatusV1);
    status->version = 1;
    status->state = LKG_WORKER_PREVIEW_LEASE_FAULTED;
    status->endpointId = 0;
    status->leaseGeneration = 0;
    status->lastResult = -2;
    status->reserved1 = 0;
    status->workerStatus = {};
    return LKG_WORKER_PREVIEW_LEASE_RESULT_UNSUPPORTED;
}
extern "C" LKG_EXPORT int32_t lkg_worker_ack_preview_lease_return_v1(void* handle, const LkgWorkerPreviewLeaseIdentityV1* identity) {
    if (!handle || !LkgPreviewLeaseIdentityValid(identity)) return LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT;
    return LKG_WORKER_PREVIEW_LEASE_RESULT_UNSUPPORTED;
}
extern "C" LKG_EXPORT int32_t lkg_worker_release(void*) { return -1; }
extern "C" LKG_EXPORT void* lkg_worker_make_draw_data(void*, void*, uint32_t, uint32_t, float, float) { return nullptr; }
extern "C" LKG_EXPORT int32_t lkg_worker_cancel_draw_data(void*) { return -1; }
extern "C" LKG_EXPORT void* lkg_worker_render_event_func(void) { return nullptr; }
extern "C" LKG_EXPORT int32_t lkg_worker_render_event_id(int32_t* eventId) {
    if (eventId) *eventId = 0;
    return -1;
}
extern "C" LKG_EXPORT int32_t lkg_worker_close_all(void) { return 0; }
extern "C" LKG_EXPORT int32_t lkg_worker_collect_closed(void) { return 0; }
extern "C" LKG_EXPORT uint32_t lkg_worker_pending_count(void) { return 0; }
extern "C" LKG_EXPORT int32_t lkg_worker_shutdown_host(uint32_t) { return 0; }
extern "C" LKG_EXPORT int32_t lkg_worker_request_final_shutdown(void) { return -2; }
extern "C" LKG_EXPORT int32_t lkg_worker_get_final_shutdown_status_v1(LkgWorkerFinalShutdownStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgWorkerFinalShutdownStatusV1) || status->version != 1) return -1;
    status->state = LKG_WORKER_FINAL_SHUTDOWN_FAULT;
    status->registeredContexts = 0;
    status->closedUncollectedContexts = 0;
    status->hostRunning = 0;
    status->hostJoined = 0;
    status->lastResult = -2;
    return 0;
}
extern "C" LKG_EXPORT int32_t lkg_worker_wait_final_shutdown_v1(uint32_t, LkgWorkerFinalShutdownStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgWorkerFinalShutdownStatusV1) || status->version != 1) return -1;
    status->state = LKG_WORKER_FINAL_SHUTDOWN_FAULT;
    status->registeredContexts = 0;
    status->closedUncollectedContexts = 0;
    status->hostRunning = 0;
    status->hostJoined = 0;
    status->lastResult = -2;
    return -2;
}
extern "C" LKG_EXPORT int32_t lkg_worker_get_physical_display_v1(void*, LkgWorkerPhysicalDisplayInfoV1* info) {
    if (!info || info->structSize != sizeof(LkgWorkerPhysicalDisplayInfoV1) || info->version != 1) return -1;
    info->pixelWidth = 0;
    info->pixelHeight = 0;
    info->dpi = 0.0f;
    info->valid = 0;
    return -2;
}
extern "C" LKG_EXPORT void* lkg_worker_begin_metadata_v1(const char*, const char*) { return nullptr; }
extern "C" LKG_EXPORT int32_t lkg_worker_request_display_refresh_v1(void*, uint64_t*) { return -2; }
extern "C" LKG_EXPORT int32_t lkg_worker_get_display_snapshot_v1(void*, LkgWorkerDisplaySnapshotStatusV1* status,
    LkgWorkerDisplaySnapshotItemV1*, uint32_t, uint32_t* requiredCount) {
    if (requiredCount) *requiredCount = 0;
    if (!status || status->structSize != sizeof(LkgWorkerDisplaySnapshotStatusV1) || status->version != 1) return -1;
    status->state = LKG_WORKER_FAULT;
    status->itemCount = 0;
    status->snapshotGeneration = 0;
    status->requestedGeneration = 0;
    status->pending = 0;
    status->cleanupFinished = 1;
    status->result = -2;
    status->error[0] = '\0';
    return -2;
}
extern "C" LKG_EXPORT int32_t lkg_worker_end_metadata_v1(void*) { return -2; }
