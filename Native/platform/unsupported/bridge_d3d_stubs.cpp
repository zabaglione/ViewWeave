#include "platform/windows/bridge_d3d_api.h"

// macOS compatibility exports; this file contains no D3D implementation.
LKG_EXPORT uint64_t lkg_bridge_create_d3d11_session(void*, uint32_t) { return 0; }
LKG_EXPORT int32_t lkg_bridge_destroy_session(uint64_t) { return -2; }
LKG_EXPORT int32_t lkg_bridge_smoke_draw(uint64_t, void*, uint32_t, uint32_t, float, float) { return -2; }
LKG_EXPORT int32_t lkg_bridge_get_session_close_status_v1(uint64_t id, LkgBridgeSessionCloseStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgBridgeSessionCloseStatusV1) || status->version != 1) return -1;
    status->structSize = sizeof(LkgBridgeSessionCloseStatusV1);
    status->version = 1;
    status->sessionId = id;
    status->state = LKG_BRIDGE_SESSION_CLOSE_UNKNOWN;
    status->lastResult = -2;
    status->reserved0 = 0;
    status->reserved1 = 0;
    return -2;
}
LKG_EXPORT int32_t lkg_bridge_ack_session_close_v1(uint64_t) { return -2; }
static void lkg_render_event(int, void*) {}

LKG_EXPORT void* lkg_bridge_render_event_func(void) { return reinterpret_cast<void*>(&lkg_render_event); }
LKG_EXPORT void* lkg_bridge_make_draw_data(uint64_t, void*, uint32_t, uint32_t, float, float) { return nullptr; }
LKG_EXPORT void* lkg_bridge_make_close_data(uint64_t) { return nullptr; }
