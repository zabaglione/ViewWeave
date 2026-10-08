#pragma once
#include <stdint.h>

#if defined(_WIN32)
#  define LKG_EXPORT __declspec(dllexport)
#else
#  define LKG_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct LkgBridgeDisplayInfo {
    uint32_t index;
    uint32_t width;
    uint32_t height;
    float aspect;
    float viewcone;
    int32_t quilt_width;
    int32_t quilt_height;
    int32_t quilt_columns;
    int32_t quilt_rows;
} LkgBridgeDisplayInfo;

typedef struct LkgBridgePhysicalDisplayInfoV1 {
    uint32_t structSize;
    uint32_t version;
    uint32_t pixelWidth;
    uint32_t pixelHeight;
    float dpi;
    uint32_t valid;
} LkgBridgePhysicalDisplayInfoV1;

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

LKG_EXPORT int32_t lkg_bridge_load(const char* absolute_path);
LKG_EXPORT void lkg_bridge_unload(void);
LKG_EXPORT int32_t lkg_bridge_initialize(const char* app_name);
LKG_EXPORT void lkg_bridge_uninitialize(void);
LKG_EXPORT int32_t lkg_bridge_is_idle(void);
LKG_EXPORT int32_t lkg_bridge_last_error(char* utf8, uint32_t capacity);
LKG_EXPORT uint32_t lkg_bridge_successful_draw_count(void);
LKG_EXPORT int32_t lkg_bridge_is_initialized(void);
LKG_EXPORT uint32_t lkg_bridge_graphics_api(void);
LKG_EXPORT int32_t lkg_bridge_version(char* utf8, uint32_t capacity);
LKG_EXPORT int32_t lkg_bridge_get_display_count(uint32_t* count);
LKG_EXPORT int32_t lkg_bridge_get_display(uint32_t ordinal, LkgBridgeDisplayInfo* info);
LKG_EXPORT int32_t lkg_bridge_get_physical_display_v1(uint32_t display_index, LkgBridgePhysicalDisplayInfoV1* info);
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
