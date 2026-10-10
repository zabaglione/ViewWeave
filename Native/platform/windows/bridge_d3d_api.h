#pragma once
#include "common/bridge_runtime_api.h"

// Fixed-width compatibility ABI for Windows D3D output. Other platforms expose failure stubs.
#ifdef __cplusplus
extern "C" {
#endif
typedef struct LkgBridgeDrawData {
    uint64_t session;
    void* texture;
    uint32_t columns;
    uint32_t rows;
    float aspect;
    float zoom;
} LkgBridgeDrawData;

typedef enum LkgBridgeSessionCloseStateV1 {
    LKG_BRIDGE_SESSION_CLOSE_PENDING = 0,
    LKG_BRIDGE_SESSION_CLOSE_CLOSED_SAFE = 1,
    LKG_BRIDGE_SESSION_CLOSE_FAILED_RETAINED = 2,
    LKG_BRIDGE_SESSION_CLOSE_UNKNOWN = 3
} LkgBridgeSessionCloseStateV1;

/* All fields are fixed-width ABI data. Querying a known session is nonblocking
   with respect to vendor rendering; unknown IDs return -2 and state Unknown. */
typedef struct LkgBridgeSessionCloseStatusV1 {
    uint32_t structSize;
    uint32_t version;
    uint64_t sessionId;
    uint32_t state;
    int32_t lastResult;
    uint32_t reserved0;
    uint32_t reserved1;
} LkgBridgeSessionCloseStatusV1;

#define LKG_BRIDGE_SESSION_CLOSE_LEDGER_CAPACITY 64u

#if defined(__cplusplus)
static_assert(sizeof(LkgBridgeSessionCloseStatusV1) == 32, "session close status ABI must be 32 bytes");
#endif

LKG_EXPORT uint64_t lkg_bridge_create_d3d11_session(void* d3d_device, uint32_t display_index);
LKG_EXPORT int32_t lkg_bridge_destroy_session(uint64_t session);
/* Query returns 0 for a ledger record, -2 for unknown/acknowledged IDs, and -1
   for an invalid size/version. ACK accepts only ClosedSafe records. */
LKG_EXPORT int32_t lkg_bridge_get_session_close_status_v1(uint64_t session, LkgBridgeSessionCloseStatusV1* status);
LKG_EXPORT int32_t lkg_bridge_ack_session_close_v1(uint64_t session);
LKG_EXPORT void* lkg_bridge_render_event_func(void);
LKG_EXPORT void* lkg_bridge_make_draw_data(uint64_t session, void* texture, uint32_t columns, uint32_t rows, float aspect, float zoom);
LKG_EXPORT void* lkg_bridge_make_close_data(uint64_t session);
LKG_EXPORT int32_t lkg_bridge_smoke_draw(uint64_t session, void* d3d_texture, uint32_t columns, uint32_t rows, float aspect, float zoom);

#ifdef __cplusplus
}
#endif
