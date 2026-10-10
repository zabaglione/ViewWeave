#include "../bridge_shim.cpp"

#include <cstdio>
#include <cstdlib>

static uint32_t g_testDrawCalls = 0;
static WindowHandle g_testWindow = 0;
static IUnknown* g_testTexture = nullptr;
static unsigned long g_testColumns = 0;
static unsigned long g_testRows = 0;
static float g_testAspect = 0.0f;
static float g_testZoom = 0.0f;
static bool g_testDrawResult = true;

static void Check(bool condition, const char* label) {
    if (condition) return;
    std::fprintf(stderr, "FAIL %s\n", label);
    std::exit(1);
}

static Bool TestDrawTexture(WindowHandle window, IUnknown* texture, unsigned long columns,
    unsigned long rows, float aspect, float zoom) {
    ++g_testDrawCalls;
    g_testWindow = window;
    g_testTexture = texture;
    g_testColumns = columns;
    g_testRows = rows;
    g_testAspect = aspect;
    g_testZoom = zoom;
    return g_testDrawResult;
}

static Bool TestUnregisterTexture(WindowHandle, IUnknown*) { return true; }
static Bool TestReleaseTexture(WindowHandle, IUnknown*) { return true; }

extern "C" int32_t lkg_worker_close_all(void) { return 0; }
extern "C" int32_t lkg_worker_collect_closed(void) { return 0; }
extern "C" uint32_t lkg_worker_pending_count(void) { return 0; }
extern "C" int32_t lkg_worker_request_final_shutdown(void) { return 0; }
extern "C" int32_t lkg_worker_wait_final_shutdown_v1(uint32_t, LkgWorkerFinalShutdownStatusV1* status) {
    if (status) status->state = LKG_WORKER_FINAL_SHUTDOWN_COMPLETE;
    return 0;
}

int main() {
    constexpr uint64_t sessionId = 71;
    constexpr uint64_t endpointId = 9;
    constexpr uint64_t generation = 14;
    auto* primary = reinterpret_cast<IUnknown*>(static_cast<uintptr_t>(0x12340));

    g_worker_scope = true;
    g_worker_active.store(true, std::memory_order_release);
    g_sdk_ownership_handle = reinterpret_cast<HANDLE>(static_cast<uintptr_t>(1));
    g_initialized = true;
    g_draw_texture = &TestDrawTexture;
    g_unregister_texture = &TestUnregisterTexture;
    g_release_texture = &TestReleaseTexture;

    Session session{};
    session.window = 27;
    session.bridgeTexture = primary;
    session.registered = true;
    session.visible = true;
    session.drawFrameCount = 6;
    session.retainedDrawGeometryValid = false;
    g_sessions.emplace(sessionId, session);

    Check(lkg_bridge_redraw_retained_session(sessionId, endpointId, generation) == 1 &&
        g_testDrawCalls == 0, "no successful ordinary draw skips static present");

    Session& retained = g_sessions.at(sessionId);
    retained.retainedDrawGeometryValid = true;
    retained.retainedColumns = 8;
    retained.retainedRows = 6;
    retained.retainedAspect = 0.625f;
    retained.retainedZoom = 1.375f;
    Check(lkg_bridge_redraw_retained_session(sessionId, endpointId, generation) == 0 &&
        g_testDrawCalls == 1, "valid retained primary is drawn once");
    Check(g_testWindow == retained.window && g_testTexture == primary && retained.bridgeTexture == primary &&
        g_testColumns == 8 && g_testRows == 6 && g_testAspect == 0.625f && g_testZoom == 1.375f,
        "static present reuses the exact registered primary and cached geometry");
    Check(retained.registered && retained.visible && !retained.closing && !retained.cleanupFailed &&
        retained.drawFrameCount == 6, "static present leaves session ownership and draw accounting unchanged");

    g_testDrawResult = false;
    Check(lkg_bridge_redraw_retained_session(sessionId, endpointId, generation) == -2 &&
        g_testDrawCalls == 2, "SDK draw failure is reported without replacing the primary");
    g_testDrawResult = true;

    g_worker_scope = false;
    Check(lkg_bridge_redraw_retained_session(sessionId, endpointId, generation) < 0 &&
        g_testDrawCalls == 2, "non-owner thread scope is rejected before SDK draw");
    g_worker_scope = true;

    g_sdk_admission_closed = true;
    Check(lkg_bridge_redraw_retained_session(sessionId, endpointId, generation) < 0 &&
        g_testDrawCalls == 2, "closed SDK admission is rejected before SDK draw");
    g_sdk_admission_closed = false;

    retained.registered = true;
    retained.visible = true;
    retained.bridgeTexture = primary;
    retained.retainedDrawGeometryValid = true;
    Check(release_bridge_texture(sessionId, retained, false) && !retained.bridgeTexture &&
        !retained.retainedDrawGeometryValid, "primary release invalidates the retained geometry");
    Check(lkg_bridge_redraw_retained_session(sessionId, endpointId, generation) < 0 &&
        g_testDrawCalls == 2, "released primary cannot be statically presented");

    std::fprintf(stdout, "bridge_static_present_test pass\n");
    return 0;
}
