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

#ifdef __cplusplus
}
#endif
