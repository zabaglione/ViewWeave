#pragma once

#include <stdint.h>

namespace lkg::metal {

constexpr uint32_t kProviderApiVersion = 1;

enum class GraphicsApi : uint32_t {
    Unknown = 0,
    Direct3D11 = 1,
    Direct3D12 = 2,
    Metal = 4
};

enum class ProviderState : uint32_t {
    Compiled = 1,
    BeginQueued = 2,
    RuntimeInitialized = 3,
    Closing = 4,
    Faulted = 5
};

struct ProviderStatusV1 {
    uint32_t structSize;
    uint32_t apiVersion;
    uint32_t compiled;
    uint32_t experimentalPathAvailable;
    uint32_t runtimeAvailable;
    uint32_t interopReady;
    uint32_t providerState;
    uint64_t deviceGeneration;
    uint32_t retainedSlots;
    uint32_t acceptedDrawCalls;
    uint32_t lastError;
    uint32_t producerApi;
    uint32_t bridgeConsumerApi;
};

struct BeginRequestV1 {
    uint32_t structSize;
    uint32_t apiVersion;
    const char* runtimePathUtf8;
    const char* applicationNameUtf8;
    uint32_t displayIndex;
    uint32_t width;
    uint32_t height;
    uint32_t metalPixelFormat;
    uint32_t columns;
    uint32_t rows;
    float aspect;
    float zoom;
};

struct RenderRequest {
    uint32_t structSize;
    uint32_t apiVersion;
    void* sourceTexture;
    uint64_t generation;
    unsigned long columns;
    unsigned long rows;
    float aspect;
    float zoom;
};

}

#if defined(__APPLE__)
#define LKG_METAL_EXPORT __attribute__((visibility("default")))
#else
#define LKG_METAL_EXPORT
#endif

extern "C" {
LKG_METAL_EXPORT void lkg_metal_unity_plugin_load(void* interfaces);
LKG_METAL_EXPORT void lkg_metal_unity_plugin_unload(void);
LKG_METAL_EXPORT void* lkg_metal_provider_render_event_func(void);
LKG_METAL_EXPORT int32_t lkg_metal_provider_render_event_id(int32_t* eventId);
LKG_METAL_EXPORT int32_t lkg_metal_provider_query_v1(lkg::metal::ProviderStatusV1* status);
LKG_METAL_EXPORT int32_t lkg_metal_provider_begin_v1(const lkg::metal::BeginRequestV1* request, void** eventData);
LKG_METAL_EXPORT int32_t lkg_metal_provider_submit_v1(const lkg::metal::RenderRequest* request, void** eventData);
LKG_METAL_EXPORT int32_t lkg_metal_provider_pump_v1(void** eventData);
LKG_METAL_EXPORT int32_t lkg_metal_provider_close(void);
LKG_METAL_EXPORT void lkg_metal_unity_render_event(int eventId, void* eventData);
}
