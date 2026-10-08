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

#if defined(__cplusplus)
static_assert(sizeof(LkgWorkerFinalShutdownStatusV1) == 8 * sizeof(uint32_t), "final shutdown status ABI must be eight 32-bit fields");
#endif

LKG_EXPORT void* lkg_worker_begin(const char* runtimePath, const char* applicationName, uint32_t displayOrdinal, void* producerDevice);
LKG_EXPORT int32_t lkg_worker_poll(void* handle, LkgWorkerStatus* status);
LKG_EXPORT int32_t lkg_worker_close(void* handle);
LKG_EXPORT int32_t lkg_worker_reset_transfer(void* handle);
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

#ifdef __cplusplus
}
#endif
