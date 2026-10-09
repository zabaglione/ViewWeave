#pragma once

#include "bridge_shim.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef enum LkgWorkerState {
    LKG_WORKER_CONNECTING = 0,
    LKG_WORKER_READY = 1,
    LKG_WORKER_FAULT = 2,
    LKG_WORKER_CLOSING = 3,
    LKG_WORKER_CLOSED = 4
} LkgWorkerState;

typedef enum LkgWorkerPreviewPausePhase {
    LKG_WORKER_PREVIEW_RUNNING = 0,
    LKG_WORKER_PREVIEW_PAUSING = 1,
    LKG_WORKER_PREVIEW_PAUSED = 2,
    LKG_WORKER_PREVIEW_RESUMING = 3,
    LKG_WORKER_PREVIEW_FAULTED = 4
} LkgWorkerPreviewPausePhase;

typedef struct LkgWorkerStatus {
    uint32_t state;
    uint32_t displayIndex;
    uint32_t width;
    uint32_t height;
    float aspect;
    float viewcone;
    int32_t quiltWidth;
    int32_t quiltHeight;
    int32_t quiltColumns;
    int32_t quiltRows;
    uint32_t submitted;
    uint32_t completed;
    uint32_t dropped;
    uint32_t lastErrorCode;
    char error[256];
} LkgWorkerStatus;

typedef enum LkgWorkerPreviewLeaseStateV1 {
    LKG_WORKER_PREVIEW_LEASE_ACTIVE = 0,
    LKG_WORKER_PREVIEW_LEASE_RETURNING = 1,
    LKG_WORKER_PREVIEW_LEASE_RETURNED_PROVEN = 2,
    LKG_WORKER_PREVIEW_LEASE_FAULTED = 3
} LkgWorkerPreviewLeaseStateV1;

typedef enum LkgWorkerPreviewLeaseResultV1 {
    LKG_WORKER_PREVIEW_LEASE_RESULT_OK = 0,
    LKG_WORKER_PREVIEW_LEASE_RESULT_BUSY = 1,
    LKG_WORKER_PREVIEW_LEASE_RESULT_INVALID_ARGUMENT = -1,
    LKG_WORKER_PREVIEW_LEASE_RESULT_UNSUPPORTED = -2,
    LKG_WORKER_PREVIEW_LEASE_RESULT_RESTART_REQUIRED = -3,
    LKG_WORKER_PREVIEW_LEASE_RESULT_NATIVE_FAILURE = -4,
    LKG_WORKER_PREVIEW_LEASE_RESULT_STALE = -5
} LkgWorkerPreviewLeaseResultV1;

typedef struct LkgWorkerPreviewLeaseIdentityV1 {
    uint32_t structSize;
    uint32_t version;
    uint64_t endpointId;
    uint64_t leaseGeneration;
} LkgWorkerPreviewLeaseIdentityV1;

typedef struct LkgWorkerPreviewLeaseStatusV1 {
    uint32_t structSize;
    uint32_t version;
    uint32_t state;
    uint32_t reserved0;
    uint64_t endpointId;
    uint64_t leaseGeneration;
    int32_t lastResult;
    uint32_t reserved1;
    LkgWorkerStatus workerStatus;
} LkgWorkerPreviewLeaseStatusV1;

typedef enum LkgWorkerFinalShutdownStateV1 {
    LKG_WORKER_FINAL_SHUTDOWN_NONE = 0,
    LKG_WORKER_FINAL_SHUTDOWN_DRAINING = 1,
    LKG_WORKER_FINAL_SHUTDOWN_JOINING = 2,
    LKG_WORKER_FINAL_SHUTDOWN_COMPLETE = 3,
    LKG_WORKER_FINAL_SHUTDOWN_TIMED_OUT = 4,
    LKG_WORKER_FINAL_SHUTDOWN_FAULT = 5
} LkgWorkerFinalShutdownStateV1;

typedef struct LkgWorkerFinalShutdownStatusV1 {
    uint32_t structSize;
    uint32_t version;
    uint32_t state;
    uint32_t registeredContexts;
    uint32_t closedUncollectedContexts;
    uint32_t hostRunning;
    uint32_t hostJoined;
    int32_t lastResult;
} LkgWorkerFinalShutdownStatusV1;

typedef struct LkgWorkerPhysicalDisplayInfoV1 {
    uint32_t structSize;
    uint32_t version;
    uint32_t pixelWidth;
    uint32_t pixelHeight;
    float dpi;
    uint32_t valid;
} LkgWorkerPhysicalDisplayInfoV1;

typedef struct LkgWorkerDisplaySnapshotStatusV1 {
    uint32_t structSize;
    uint32_t version;
    uint32_t state;
    uint32_t itemCount;
    uint64_t snapshotGeneration;
    uint64_t requestedGeneration;
    uint32_t pending;
    uint32_t cleanupFinished;
    int32_t result;
    char error[256];
} LkgWorkerDisplaySnapshotStatusV1;

typedef struct LkgWorkerDisplaySnapshotItemV1 {
    uint32_t structSize;
    uint32_t version;
    uint32_t displayOrdinal;
    uint32_t reserved0;
    LkgBridgeDisplayInfo profile;
    LkgWorkerPhysicalDisplayInfoV1 physical;
} LkgWorkerDisplaySnapshotItemV1;

#if defined(__cplusplus)
static_assert(sizeof(LkgWorkerFinalShutdownStatusV1) == 8 * sizeof(uint32_t), "final shutdown status ABI must be eight 32-bit fields");
static_assert(sizeof(LkgWorkerDisplaySnapshotStatusV1) == 304, "display snapshot status V1 ABI size must remain stable");
static_assert(sizeof(LkgWorkerDisplaySnapshotItemV1) == 76, "display snapshot item V1 ABI size must remain stable");
static_assert(sizeof(LkgWorkerStatus) == 312, "worker status ABI must remain stable");
static_assert(sizeof(LkgWorkerPreviewLeaseIdentityV1) == 24, "preview lease identity V1 ABI size must remain stable");
static_assert(sizeof(LkgWorkerPreviewLeaseStatusV1) == 352, "preview lease status V1 ABI size must remain stable");
#endif

LKG_EXPORT void* lkg_worker_begin(const char* runtimePath, const char* applicationName, uint32_t displayOrdinal, void* producerDevice);
LKG_EXPORT int32_t lkg_worker_poll(void* handle, LkgWorkerStatus* status);
LKG_EXPORT int32_t lkg_worker_close(void* handle);
LKG_EXPORT int32_t lkg_worker_reset_transfer(void* handle);
/* Pause/resume are asynchronous requests. Poll get_preview_pause_state until the requested phase is acknowledged. */
LKG_EXPORT int32_t lkg_worker_set_preview_paused(void* handle, uint32_t paused);
LKG_EXPORT int32_t lkg_worker_get_preview_pause_state(void* handle, uint32_t* phase);
/*
 * Preview lease handles are opaque registry tokens and are accepted by the existing
 * worker operations while active. Acquire returns 0 when accepted (the worker can
 * still be Connecting), 1 for retryable busy/return/ACK pending, and a negative
 * LkgWorkerPreviewLeaseResultV1 value for a hard error. On every nonzero acquire
 * result, handle and identity endpoint/generation are zero. Return fences new
 * submissions immediately. ReturnedProven means producer work drained, stable-input
 * transfer proof completed, and SDK hide acknowledged; it is not Unity retention or
 * consumer GPU proof. ACK is required after those external proofs and gates the next
 * acquire. Unknown or stale tokens/generations are rejected.
 */
LKG_EXPORT int32_t lkg_worker_acquire_preview_lease_v1(const char* runtimePath, const char* applicationName,
    uint32_t displayOrdinal, void* producerDevice, void** leaseHandle, LkgWorkerPreviewLeaseIdentityV1* identity);
LKG_EXPORT int32_t lkg_worker_request_preview_lease_return_v1(void* leaseHandle, const LkgWorkerPreviewLeaseIdentityV1* identity);
LKG_EXPORT int32_t lkg_worker_get_preview_lease_status_v1(void* leaseHandle, const LkgWorkerPreviewLeaseIdentityV1* identity,
    LkgWorkerPreviewLeaseStatusV1* status);
LKG_EXPORT int32_t lkg_worker_ack_preview_lease_return_v1(void* leaseHandle, const LkgWorkerPreviewLeaseIdentityV1* identity);
/*
 * Lease-only draw admission API. drawData is cleared on every nonzero result.
 * BUSY is reserved for a valid, ready, running lease with all request fields valid
 * when the three-token admission queue is full; that result does not AddRef texture
 * or allocate a draw payload. Other lifecycle failures return STALE or NATIVE_FAILURE,
 * and invalid identity/request arguments return INVALID_ARGUMENT.
 */
LKG_EXPORT int32_t lkg_worker_try_make_preview_draw_data_v1(void* leaseHandle,
    const LkgWorkerPreviewLeaseIdentityV1* identity, void* texture, uint32_t columns, uint32_t rows,
    float aspect, float zoom, void** drawData);
LKG_EXPORT int32_t lkg_worker_release(void* handle);
LKG_EXPORT void* lkg_worker_make_draw_data(void* handle, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom);
LKG_EXPORT int32_t lkg_worker_cancel_draw_data(void* drawData);
LKG_EXPORT void* lkg_worker_render_event_func(void);
LKG_EXPORT int32_t lkg_worker_render_event_id(int32_t* event_id);
LKG_EXPORT int32_t lkg_worker_close_all(void);
LKG_EXPORT int32_t lkg_worker_collect_closed(void);
LKG_EXPORT uint32_t lkg_worker_pending_count(void);
LKG_EXPORT int32_t lkg_worker_request_final_shutdown(void);
LKG_EXPORT int32_t lkg_worker_get_final_shutdown_status_v1(LkgWorkerFinalShutdownStatusV1* status);
LKG_EXPORT int32_t lkg_worker_wait_final_shutdown_v1(uint32_t timeoutMilliseconds, LkgWorkerFinalShutdownStatusV1* status);
LKG_EXPORT int32_t lkg_worker_get_physical_display_v1(void* handle, LkgWorkerPhysicalDisplayInfoV1* info);
LKG_EXPORT void* lkg_worker_begin_metadata_v1(const char* runtimePath, const char* applicationName);
LKG_EXPORT int32_t lkg_worker_request_display_refresh_v1(void* handle, uint64_t* requestedGeneration);
LKG_EXPORT int32_t lkg_worker_get_display_snapshot_v1(void* handle, LkgWorkerDisplaySnapshotStatusV1* status,
    LkgWorkerDisplaySnapshotItemV1* items, uint32_t capacity, uint32_t* requiredCount);
LKG_EXPORT int32_t lkg_worker_end_metadata_v1(void* handle);

#ifdef __cplusplus
}
#endif
