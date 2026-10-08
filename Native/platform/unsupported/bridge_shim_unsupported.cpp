#include "bridge_shim.h"

#include <algorithm>
#include <cstring>

namespace {
constexpr char kUnsupported[] = "unsupported graphics backend";
}

extern "C" LKG_EXPORT int32_t lkg_bridge_load(const char*) { return -1; }
extern "C" LKG_EXPORT void lkg_bridge_unload(void) {}
extern "C" LKG_EXPORT int32_t lkg_bridge_initialize(const char*) { return -1; }
extern "C" LKG_EXPORT void lkg_bridge_uninitialize(void) {}
extern "C" LKG_EXPORT int32_t lkg_bridge_is_idle(void) { return 1; }
extern "C" LKG_EXPORT int32_t lkg_bridge_last_error(char* utf8, uint32_t capacity) {
    if (utf8 && capacity) {
        const size_t length = std::min(static_cast<size_t>(capacity - 1), sizeof(kUnsupported) - 1);
        std::memcpy(utf8, kUnsupported, length);
        utf8[length] = '\0';
    }
    return -1;
}
extern "C" LKG_EXPORT uint32_t lkg_bridge_successful_draw_count(void) { return 0; }
extern "C" LKG_EXPORT int32_t lkg_bridge_is_initialized(void) { return 0; }
extern "C" LKG_EXPORT uint32_t lkg_bridge_graphics_api(void) { return 0; }
extern "C" LKG_EXPORT int32_t lkg_bridge_version(char* utf8, uint32_t capacity) {
    if (utf8 && capacity) utf8[0] = '\0';
    return -1;
}
extern "C" LKG_EXPORT int32_t lkg_bridge_get_display_count(uint32_t* count) {
    if (count) *count = 0;
    return -1;
}
extern "C" LKG_EXPORT int32_t lkg_bridge_get_display(uint32_t, LkgBridgeDisplayInfo* info) {
    if (info) std::memset(info, 0, sizeof(*info));
    return -1;
}
extern "C" LKG_EXPORT int32_t lkg_bridge_get_physical_display_v1(uint32_t, LkgBridgePhysicalDisplayInfoV1* info) {
    if (!info || info->structSize != sizeof(LkgBridgePhysicalDisplayInfoV1) || info->version != 1) return -1;
    info->pixelWidth = 0; info->pixelHeight = 0; info->dpi = 0.0f; info->valid = 0;
    return -2;
}
extern "C" LKG_EXPORT uint64_t lkg_bridge_create_d3d11_session(void*, uint32_t) { return 0; }
extern "C" LKG_EXPORT int32_t lkg_bridge_get_session_close_status_v1(uint64_t session, LkgBridgeSessionCloseStatusV1* status) {
    if (!status || status->structSize != sizeof(LkgBridgeSessionCloseStatusV1) || status->version != 1) return -1;
    status->structSize = sizeof(LkgBridgeSessionCloseStatusV1);
    status->version = 1;
    status->sessionId = session;
    status->state = LKG_BRIDGE_SESSION_CLOSE_UNKNOWN;
    status->lastResult = -2;
    status->reserved0 = 0;
    status->reserved1 = 0;
    return -2;
}
extern "C" LKG_EXPORT int32_t lkg_bridge_ack_session_close_v1(uint64_t) { return -2; }
extern "C" LKG_EXPORT int32_t lkg_bridge_destroy_session(uint64_t) { return -1; }
extern "C" LKG_EXPORT void* lkg_bridge_render_event_func(void) { return nullptr; }
extern "C" LKG_EXPORT void* lkg_bridge_make_draw_data(uint64_t, void*, uint32_t, uint32_t, float, float) { return nullptr; }
extern "C" LKG_EXPORT void* lkg_bridge_make_close_data(uint64_t) { return nullptr; }
extern "C" LKG_EXPORT int32_t lkg_bridge_smoke_draw(uint64_t, void*, uint32_t, uint32_t, float, float) { return -1; }
