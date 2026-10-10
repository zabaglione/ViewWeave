#pragma once
#include <stdint.h>
#include "../../common/bridge_runtime_api.h"

// Unity owns the producer; this window owns the Metal consumer and its completion.
struct LkgMetalDisplayInfo {
    LkgBridgeDisplayInfo display;
    float center, pitch, slope, dpi, flipX;
    int32_t invertedViews;
};
// Keep the original 60-byte export intact for already-loaded managed clients.
struct LkgMetalDisplayInfoV2 {
    uint32_t structSize, apiVersion;
    LkgMetalDisplayInfo calibration;
    int32_t redIndex, blueIndex;
};
struct LkgMetalDisplayStatus {
    uint32_t submitted, completed, dropped, error, pending, closing;
};
extern "C" {
LKG_EXPORT uint64_t lkg_metal_display_open(const char* path, const char* applicationName,
    uint32_t ordinal, LkgMetalDisplayInfo* info);
LKG_EXPORT uint64_t lkg_metal_display_open_v2(const char* path, const char* applicationName,
    uint32_t ordinal, LkgMetalDisplayInfoV2* info);
LKG_EXPORT int32_t lkg_metal_display_submit(uint64_t session, void* texture, void** eventData);
LKG_EXPORT int32_t lkg_metal_display_status(uint64_t session, LkgMetalDisplayStatus* status);
LKG_EXPORT void lkg_metal_display_close(uint64_t session);
LKG_EXPORT const char* lkg_metal_display_error(void);
LKG_EXPORT void* lkg_metal_display_render_event(void);
void lkg_metal_display_plugin_load(void* interfaces);
void lkg_metal_display_plugin_unload(void);
}
