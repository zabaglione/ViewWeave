#pragma once

#if !defined(__OBJC__)
#error unity_metal_producer.h must be included from Objective-C++.
#endif

#include "metal_provider_api.h"
#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"
#include "bridge_metal_api.h"

// Uses the legacy IUnityGraphicsMetal interface present in the pinned
// NativeRenderingPlugin header at 4c65c1ca19585b8b447693f7e577e51a58e8ef02.
// The actual 6000.6 registry query has not been verified on a Metal runtime.
namespace lkg::metal {

bool ConfigureUnityMetalProducer(IUnityInterfaces* unityInterfaces, WindowHandle window,
    void* const transferTextures[3], uint64_t deviceGeneration) noexcept;
void CloseUnityMetalProducerAdmission() noexcept;
uint32_t UnityMetalProducerRetainedCount() noexcept;
bool QueryProviderStatus(ProviderStatusV1* status) noexcept;
bool QueueProviderBegin(const BeginRequestV1* request, void** renderEventData) noexcept;
bool QueueProviderSubmit(const RenderRequest* request, void** renderEventData) noexcept;
bool QueueProviderPump(void** renderEventData) noexcept;

}

