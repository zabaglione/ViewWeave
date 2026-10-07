#include "bridge_worker_transport.h"

extern "C" LKG_EXPORT void* lkg_worker_begin(const char*, const char*, uint32_t, void*) { return nullptr; }
extern "C" LKG_EXPORT int32_t lkg_worker_poll(void*, LkgWorkerStatus*) { return -1; }
extern "C" LKG_EXPORT int32_t lkg_worker_close(void*) { return -1; }
extern "C" LKG_EXPORT int32_t lkg_worker_reset_transfer(void*) { return -1; }
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
