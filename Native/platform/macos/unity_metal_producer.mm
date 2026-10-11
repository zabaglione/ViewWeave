#import <Metal/Metal.h>
#include "unity_metal_producer.h"
#include "metal_submission_lifetime.h"
#include "IUnityGraphics.h"

#include <atomic>
#include <cstddef>
#include <cmath>
#include <dlfcn.h>
#include <limits>
#include <mutex>
#include <string>
#include <utility>
extern "C" bool lkg_metal_metadata_claim();
extern "C" void lkg_metal_metadata_release(bool shutdownProven);

namespace lkg::metal {
namespace {

static_assert(sizeof(void*) == 8, "The experimental Metal provider requires LP64.");
static_assert(sizeof(ProviderStatusV1) == 64, "ProviderStatusV1 ABI mismatch.");
static_assert(offsetof(ProviderStatusV1, deviceGeneration) == 32, "ProviderStatusV1 offset mismatch.");
static_assert(sizeof(BeginRequestV1) == 56, "BeginRequestV1 ABI mismatch.");
static_assert(offsetof(BeginRequestV1, runtimePathUtf8) == 8, "BeginRequestV1 offset mismatch.");
static_assert(sizeof(RenderRequest) == 48, "RenderRequest ABI mismatch.");
static_assert(offsetof(RenderRequest, sourceTexture) == 8, "RenderRequest offset mismatch.");
static_assert(offsetof(RenderRequest, generation) == 16, "RenderRequest generation offset mismatch.");
static_assert(offsetof(RenderRequest, columns) == 24, "RenderRequest columns offset mismatch.");

struct RetainedSlot {
    SourceLease* sourceLease = nullptr;
    id<MTLTexture> transfer = nil;
    RenderRequest request{};
    bool requestQueued = false;
    uint64_t generation = 0;
};

struct CompletionContext {
    uint32_t slotIndex;
    uint64_t generation;
    id<MTLTexture> source;
    id<MTLTexture> transfer;
};

struct OwnedBeginRequest {
    std::string runtimePath;
    std::string applicationName;
    uint32_t displayIndex = 0;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t metalPixelFormat = 0;
    uint32_t columns = 0;
    uint32_t rows = 0;
    float aspect = 0.0f;
    float zoom = 0.0f;
};

constexpr uint32_t kErrorNone = 0;
constexpr uint32_t kErrorPluginNotLoaded = 1;
constexpr uint32_t kErrorInvalidBegin = 2;
constexpr uint32_t kErrorBridgeStart = 3;
constexpr uint32_t kErrorAdditionalTexture = 4;
constexpr uint32_t kErrorSubmitRejected = 5;
constexpr uint32_t kErrorDeviceReset = 6;
constexpr uint32_t kErrorStaleGeneration = 7;

SubmissionLifetime* g_lifetime = new SubmissionLifetime();
RetainedSlot g_slots[kSubmissionSlotCount];
IUnityInterfaces* g_unityInterfaces = nullptr;
IUnityGraphics* g_graphics = nullptr;
IUnityGraphicsMetal* g_metal = nullptr;
id<MTLDevice> g_unityDevice = nil;
id<MTLDevice> g_unpublishedDevice = nil;
id<MTLTexture> g_unpublishedTransferTextures[kSubmissionSlotCount] = {nil, nil, nil};
WindowHandle g_window = 0;
WindowHandle g_unpublishedWindow = 0;
std::atomic<uint64_t> g_deviceGeneration{1};
std::atomic<uint32_t> g_completedNotifications{0};
std::atomic<uint32_t> g_failedNotifications{0};
std::atomic<uint32_t> g_acceptedDrawCalls{0};
std::atomic<bool> g_pluginActive{false};
std::atomic<bool> g_configured{false};
std::atomic<bool> g_runtimeAvailable{false};
std::atomic<uint32_t> g_providerState{static_cast<uint32_t>(ProviderState::Compiled)};
std::atomic<uint32_t> g_lastError{kErrorNone};
std::atomic<int32_t> g_renderEventId{0};
std::atomic<bool> g_renderEventIdReserved{false};
std::mutex g_providerMutex;
BeginLifecycle g_beginLifecycle;
BeginTicket g_beginTicket{};
int g_beginEventToken = 0;
int g_pumpEventToken = 0;
OwnedBeginRequest g_beginRequest;
void* g_pinnedProviderModule = nullptr;

bool PinProviderModule() {
    if (g_pinnedProviderModule != nullptr) return true;
    Dl_info info{};
    if (dladdr(reinterpret_cast<const void*>(&lkg_metal_unity_render_event), &info) != 0 && info.dli_fname != nullptr) {
        g_pinnedProviderModule = dlopen(info.dli_fname, RTLD_NOW | RTLD_LOCAL);
    }
    return g_pinnedProviderModule != nullptr;
}

void UNITY_INTERFACE_API OnDeviceEvent(UnityGfxDeviceEventType eventType) {
    if (eventType == kUnityGfxDeviceEventShutdown || eventType == kUnityGfxDeviceEventBeforeReset) {
        std::lock_guard<std::mutex> lock(g_providerMutex);
        g_beginLifecycle.Cancel();
        g_deviceGeneration.fetch_add(1, std::memory_order_acq_rel);
        g_lifetime->CloseAdmission();
        g_runtimeAvailable.store(false, std::memory_order_release);
        g_providerState.store(static_cast<uint32_t>(ProviderState::Closing), std::memory_order_release);
        g_lastError.store(kErrorDeviceReset, std::memory_order_release);
    }
}

void CompleteTransfer(id<MTLCommandBuffer> commandBuffer, void* rawContext) {
    CompletionContext* context = static_cast<CompletionContext*>(rawContext);
    const bool success = commandBuffer.status == MTLCommandBufferStatusCompleted;
    if (context->generation == g_deviceGeneration.load(std::memory_order_acquire)) {
        if (g_lifetime->CompleteProducer(context->slotIndex, context->generation, success)) {
            (success ? g_completedNotifications : g_failedNotifications).fetch_add(1, std::memory_order_relaxed);
        }
    }
    [context->source release];
    [context->transfer release];
    delete context;
}

void RetainMetalTexture(void* rawTexture) {
    [((__bridge id<MTLTexture>)rawTexture) retain];
}

void ReleaseMetalTexture(void* rawTexture) {
    [((__bridge id<MTLTexture>)rawTexture) release];
}

bool ValidTexturePair(id<MTLTexture> source, id<MTLTexture> destination) {
    return source != nil && destination != nil && source != destination &&
        AreDevicesCompatible((__bridge void*)source.device, (__bridge void*)g_unityDevice) &&
        AreDevicesCompatible((__bridge void*)destination.device, (__bridge void*)g_unityDevice) &&
        source.width == destination.width && source.height == destination.height &&
        source.pixelFormat == destination.pixelFormat && source.sampleCount == 1 &&
        destination.sampleCount == 1 && source.mipmapLevelCount == 1 &&
        destination.mipmapLevelCount == 1 && source.textureType == MTLTextureType2D &&
        destination.textureType == MTLTextureType2D;
}

void SubmitProducerCompleteSlots() {
    uint32_t index = 0;
    uint64_t generation = 0;
    while (g_lifetime->BeginNextBridgeCall(&index, &generation)) {
        RetainedSlot& slot = g_slots[index];
        if (slot.generation != generation) {
            g_lifetime->MarkBridgeCallFailedRetained(index, generation);
            g_lastError.store(kErrorStaleGeneration, std::memory_order_release);
            continue;
        }
        const bool accepted = DrawBridgeMetalQuilt(g_window, (__bridge void*)slot.transfer,
            slot.request.columns, slot.request.rows, slot.request.aspect, slot.request.zoom);
        if (accepted) {
            if (g_lifetime->MarkBridgeSubmitted(index, slot.generation) && g_lifetime->MarkConsumerUnknown(index, slot.generation)) {
                g_acceptedDrawCalls.fetch_add(1, std::memory_order_relaxed);
            }
        } else {
            g_lifetime->MarkBridgeCallFailedRetained(index, slot.generation);
            g_lastError.store(kErrorSubmitRejected, std::memory_order_release);
        }
    }
}

void QueueCopy(uint32_t slotIndex, const RenderRequest& request) {
    if (slotIndex >= kSubmissionSlotCount || !g_configured.load(std::memory_order_acquire)) return;
    const SlotSnapshot snapshot = g_lifetime->Snapshot(slotIndex);
    if (snapshot.state != SlotState::ProducerPending || snapshot.generation != request.generation) return;
    if (request.generation != g_deviceGeneration.load(std::memory_order_acquire)) {
        g_lifetime->CompleteProducer(slotIndex, request.generation, false);
        g_lastError.store(kErrorStaleGeneration, std::memory_order_release);
        return;
    }

    RetainedSlot& slot = g_slots[slotIndex];
    id<MTLTexture> source = (__bridge id<MTLTexture>)slot.sourceLease->Get();
    id<MTLTexture> transfer = slot.transfer;
    if (!ValidTexturePair(source, transfer) || request.columns == 0 || request.rows == 0 ||
        !std::isfinite(request.aspect) || !std::isfinite(request.zoom) || request.aspect <= 0.0f || request.zoom <= 0.0f) {
        g_lifetime->CompleteProducer(slotIndex, request.generation, false);
        g_lastError.store(kErrorInvalidBegin, std::memory_order_release);
        return;
    }

    slot.generation = request.generation;
    slot.request = request;

    id<MTLCommandBuffer> commandBuffer = g_metal->CurrentCommandBuffer();
    if (commandBuffer == nil) {
        g_lifetime->CompleteProducer(slotIndex, request.generation, false);
        g_lastError.store(kErrorSubmitRejected, std::memory_order_release);
        return;
    }
    if (g_metal->CurrentCommandEncoder() != nil) g_metal->EndCurrentCommandEncoder();

    id<MTLBlitCommandEncoder> blit = [commandBuffer blitCommandEncoder];
    if (blit == nil) {
        g_lifetime->CompleteProducer(slotIndex, request.generation, false);
        g_lastError.store(kErrorSubmitRejected, std::memory_order_release);
        return;
    }

    const MTLSize size = MTLSizeMake(source.width, source.height, 1);
    [blit copyFromTexture:source sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0, 0, 0)
        sourceSize:size toTexture:transfer destinationSlice:0 destinationLevel:0 destinationOrigin:MTLOriginMake(0, 0, 0)];
    [blit endEncoding];

    CompletionContext* context = new CompletionContext{slotIndex, request.generation, [source retain], [transfer retain]};
    [commandBuffer addCompletedHandler:^(id<MTLCommandBuffer> completedBuffer) {
        CompleteTransfer(completedBuffer, context);
    }];
}

void PreserveUnpublishedResources(id<MTLDevice> device, WindowHandle window,
    void* const transferTextures[kSubmissionSlotCount]) {
    if (g_unpublishedDevice == nil && device != nil) g_unpublishedDevice = [device retain];
    if (window != 0) g_unpublishedWindow = window;
    if (transferTextures == nullptr) return;
    for (uint32_t index = 0; index < kSubmissionSlotCount; ++index) {
        if (g_unpublishedTransferTextures[index] == nil && transferTextures[index] != nullptr) {
            g_unpublishedTransferTextures[index] = [((__bridge id<MTLTexture>)transferTextures[index]) retain];
        }
    }
}

void FinishBeginFailure(const BeginTicket& ticket, uint32_t errorCode) {
    std::lock_guard<std::mutex> lock(g_providerMutex);
    const BeginFinish result = g_beginLifecycle.Finish(ticket,
        g_deviceGeneration.load(std::memory_order_acquire),
        g_pluginActive.load(std::memory_order_acquire), false);
    if (result == BeginFinish::Failed) {
        g_runtimeAvailable.store(false, std::memory_order_release);
        g_providerState.store(static_cast<uint32_t>(ProviderState::Faulted), std::memory_order_release);
        g_lastError.store(errorCode, std::memory_order_release);
    }
}

bool IsBeginCurrent(const BeginTicket& ticket) {
    std::lock_guard<std::mutex> lock(g_providerMutex);
    return g_beginLifecycle.IsExecuting(ticket, g_deviceGeneration.load(std::memory_order_acquire),
        g_pluginActive.load(std::memory_order_acquire));
}

bool ContinueProviderBegin(void* rawTicket) noexcept {
    if (rawTicket == nullptr) return false;
    return IsBeginCurrent(*static_cast<const BeginTicket*>(rawTicket));
}

bool InitializeOnRenderThread(const BeginTicket& ticket) {
    if (!IsBeginCurrent(ticket)) return false;
    if (g_metal == nullptr || g_metal->MetalDevice == nullptr) {
        FinishBeginFailure(ticket, kErrorPluginNotLoaded);
        return false;
    }

    NSAutoreleasePool* pool = [[NSAutoreleasePool alloc] init];
    id<MTLDevice> device = [g_metal->MetalDevice() retain];
    if (device == nil) {
        [pool drain];
        FinishBeginFailure(ticket, kErrorPluginNotLoaded);
        return false;
    }
    MTLTextureDescriptor* descriptor = [[MTLTextureDescriptor alloc] init];
    descriptor.textureType = MTLTextureType2D;
    descriptor.pixelFormat = static_cast<MTLPixelFormat>(g_beginRequest.metalPixelFormat);
    descriptor.width = g_beginRequest.width;
    descriptor.height = g_beginRequest.height;
    descriptor.depth = 1;
    descriptor.mipmapLevelCount = 1;
    descriptor.sampleCount = 1;
    descriptor.arrayLength = 1;
    descriptor.storageMode = MTLStorageModeShared;
    descriptor.usage = MTLTextureUsageShaderRead;

    void* transferTextures[kSubmissionSlotCount]{};
    WindowHandle localWindow = 0;
    const char* error = nullptr;
    bool started = LoadAndInitializeBridgeMetal(g_beginRequest.runtimePath.c_str(),
        g_beginRequest.applicationName.c_str(), (__bridge void*)device,
        static_cast<unsigned long>(g_beginRequest.displayIndex),
        (__bridge void*)descriptor,
        &localWindow, &transferTextures[0], &error, ContinueProviderBegin,
        const_cast<BeginTicket*>(&ticket));
    if (started && !IsBeginCurrent(ticket)) started = false;
    if (started) {
        for (uint32_t index = 1; index < kSubmissionSlotCount; ++index) {
            if (!CreateBridgeMetalTextureForWindow(localWindow, (__bridge void*)descriptor, &transferTextures[index], &error)) {
                started = false;
                break;
            }
            if (!IsBeginCurrent(ticket)) {
                started = false;
                break;
            }
        }
    }
    if (started) {
        for (uint32_t index = 0; index < kSubmissionSlotCount; ++index) {
            id<MTLTexture> texture = (__bridge id<MTLTexture>)transferTextures[index];
            if (texture == nil || !AreDevicesCompatible((__bridge void*)texture.device, (__bridge void*)device) || texture.width != g_beginRequest.width ||
                texture.height != g_beginRequest.height || texture.pixelFormat != g_beginRequest.metalPixelFormat) {
                started = false;
                break;
            }
        }
    }
    [descriptor release];

    bool published = false;
    {
        std::lock_guard<std::mutex> lock(g_providerMutex);
        const uint64_t currentGeneration = g_deviceGeneration.load(std::memory_order_acquire);
        const bool pluginActive = g_pluginActive.load(std::memory_order_acquire);
        if (!g_beginLifecycle.IsExecuting(ticket, currentGeneration, pluginActive)) {
            PreserveUnpublishedResources(device, localWindow, transferTextures);
        } else if (started && ConfigureUnityMetalProducer(g_unityInterfaces, localWindow, transferTextures,
            ticket.generation)) {
            g_unityDevice = [device retain];
            g_window = localWindow;
            const BeginFinish result = g_beginLifecycle.Finish(ticket, currentGeneration, pluginActive, true);
            if (result == BeginFinish::Published) {
                g_runtimeAvailable.store(true, std::memory_order_release);
                g_providerState.store(static_cast<uint32_t>(ProviderState::RuntimeInitialized), std::memory_order_release);
                g_lastError.store(kErrorNone, std::memory_order_release);
                published = true;
            } else {
                PreserveUnpublishedResources(device, localWindow, transferTextures);
            }
        } else {
            PreserveUnpublishedResources(device, localWindow, transferTextures);
            const BeginFinish result = g_beginLifecycle.Finish(ticket, currentGeneration, pluginActive, false);
            if (result == BeginFinish::Failed) {
                g_lifetime->CloseAdmission();
                g_runtimeAvailable.store(false, std::memory_order_release);
                g_providerState.store(static_cast<uint32_t>(ProviderState::Faulted), std::memory_order_release);
                g_lastError.store(started ? kErrorAdditionalTexture : kErrorBridgeStart, std::memory_order_release);
            }
        }
    }
    [device release];
    [pool drain];
    return published;
}

}

bool ConfigureUnityMetalProducer(IUnityInterfaces* unityInterfaces, WindowHandle window,
    void* const transferTextures[3], uint64_t deviceGeneration) noexcept {
    if (unityInterfaces == nullptr || window == 0 || transferTextures == nullptr || deviceGeneration == 0 ||
        deviceGeneration != g_deviceGeneration.load(std::memory_order_acquire) ||
        g_configured.load(std::memory_order_acquire)) return false;
    IUnityGraphicsMetal* metal = unityInterfaces->Get<IUnityGraphicsMetal>();
    if (metal == nullptr || metal->CurrentCommandBuffer == nullptr || metal->CurrentCommandEncoder == nullptr ||
        metal->EndCurrentCommandEncoder == nullptr) return false;
    for (uint32_t index = 0; index < kSubmissionSlotCount; ++index) {
        if (transferTextures[index] == nullptr) return false;
        g_slots[index].transfer = [((__bridge id<MTLTexture>)transferTextures[index]) retain];
    }
    g_metal = metal;
    g_window = window;
    g_configured.store(true, std::memory_order_release);
    return true;
}

void CloseUnityMetalProducerAdmission() noexcept {
    std::lock_guard<std::mutex> lock(g_providerMutex);
    g_beginLifecycle.Cancel();
    g_lifetime->CloseAdmission();
    g_runtimeAvailable.store(false, std::memory_order_release);
    g_providerState.store(static_cast<uint32_t>(ProviderState::Closing), std::memory_order_release);
}

uint32_t UnityMetalProducerRetainedCount() noexcept {
    return g_lifetime->RetainedCount();
}

bool QueryProviderStatus(ProviderStatusV1* status) noexcept {
    if (status == nullptr || status->structSize < sizeof(ProviderStatusV1) || status->apiVersion != kProviderApiVersion) return false;
    status->compiled = 1;
    status->experimentalPathAvailable = 1;
    status->runtimeAvailable = g_runtimeAvailable.load(std::memory_order_acquire) ? 1u : 0u;
    status->interopReady = 0;
    status->producerApi = static_cast<uint32_t>(GraphicsApi::Metal);
    status->bridgeConsumerApi = static_cast<uint32_t>(GraphicsApi::Unknown);
    status->providerState = g_providerState.load(std::memory_order_acquire);
    status->deviceGeneration = g_deviceGeneration.load(std::memory_order_acquire);
    status->retainedSlots = UnityMetalProducerRetainedCount();
    status->acceptedDrawCalls = g_acceptedDrawCalls.load(std::memory_order_acquire);
    status->lastError = g_lastError.load(std::memory_order_acquire);
    return true;
}

bool QueueProviderBegin(const BeginRequestV1* request, void** renderEventData) noexcept {
    if (request == nullptr || renderEventData == nullptr || request->structSize < sizeof(BeginRequestV1) ||
        request->apiVersion != kProviderApiVersion || request->runtimePathUtf8 == nullptr ||
        request->applicationNameUtf8 == nullptr || request->runtimePathUtf8[0] != '/' ||
        !std::isfinite(request->aspect) || !std::isfinite(request->zoom) || request->aspect <= 0.0f || request->zoom <= 0.0f) return false;
    const TextureConfiguration requested{request->width, request->height, request->metalPixelFormat,
        request->columns, request->rows};
    if (!IsValidBeginConfiguration(requested)) return false;
    OwnedBeginRequest owned;
    try {
        owned.runtimePath = request->runtimePathUtf8;
        owned.applicationName = request->applicationNameUtf8;
    } catch (...) {
        return false;
    }
    owned.displayIndex = request->displayIndex;
    owned.width = request->width;
    owned.height = request->height;
    owned.metalPixelFormat = request->metalPixelFormat;
    owned.columns = request->columns;
    owned.rows = request->rows;
    owned.aspect = request->aspect;
    owned.zoom = request->zoom;

    std::lock_guard<std::mutex> lock(g_providerMutex);
    if (!lkg_metal_metadata_claim()) return false;
    if (!g_pluginActive.load(std::memory_order_acquire) || !g_lifetime->IsAccepting() ||
        !g_beginLifecycle.Queue(g_deviceGeneration.load(std::memory_order_acquire), &g_beginTicket)) {
        lkg_metal_metadata_release(true);
        return false;
    }
    g_beginRequest = std::move(owned);
    g_providerState.store(static_cast<uint32_t>(ProviderState::BeginQueued), std::memory_order_release);
    *renderEventData = &g_beginEventToken;
    return true;
}

bool QueueProviderSubmit(const RenderRequest* request, void** renderEventData) noexcept {
    if (request == nullptr || renderEventData == nullptr || request->structSize < sizeof(RenderRequest) ||
        request->apiVersion != kProviderApiVersion || request->sourceTexture == nullptr || request->generation == 0) return false;
    if (!g_runtimeAvailable.load(std::memory_order_acquire) || request->generation != g_deviceGeneration.load(std::memory_order_acquire)) return false;
    id<MTLTexture> source = (__bridge id<MTLTexture>)request->sourceTexture;
    if (source == nil || !AreDevicesCompatible((__bridge void*)source.device, (__bridge void*)g_unityDevice) ||
        request->columns != g_beginRequest.columns || request->rows != g_beginRequest.rows ||
        source.width != g_beginRequest.width || source.height != g_beginRequest.height ||
        !IsCompatibleSubmitConfiguration(
        {g_beginRequest.width, g_beginRequest.height, g_beginRequest.metalPixelFormat,
            g_beginRequest.columns, g_beginRequest.rows},
        static_cast<uint32_t>(source.width), static_cast<uint32_t>(source.height),
        static_cast<uint32_t>(source.pixelFormat), static_cast<uint32_t>(request->columns),
        static_cast<uint32_t>(request->rows))) return false;
    SourceLease* sourceLease = SourceLease::Acquire((__bridge void*)source,
        RetainMetalTexture, ReleaseMetalTexture);
    if (sourceLease == nullptr) return false;
    std::lock_guard<std::mutex> lock(g_providerMutex);
    uint32_t slotIndex = 0;
    if (!g_lifetime->TryReserve(request->generation, sourceLease, &slotIndex)) {
        delete sourceLease;
        return false;
    }
    g_slots[slotIndex].sourceLease = sourceLease;
    g_slots[slotIndex].request = *request;
    g_slots[slotIndex].requestQueued = true;
    *renderEventData = &g_slots[slotIndex].request;
    return true;
}

bool QueueProviderPump(void** renderEventData) noexcept {
    if (renderEventData == nullptr || !g_runtimeAvailable.load(std::memory_order_acquire) ||
        !g_pluginActive.load(std::memory_order_acquire) || !g_lifetime->IsAccepting()) return false;
    *renderEventData = &g_pumpEventToken;
    return true;
}

}

extern "C" void UNITY_INTERFACE_API lkg_metal_unity_render_event(int, void* eventData) {
    if (!lkg::metal::g_pluginActive.load(std::memory_order_acquire) || eventData == nullptr) return;
    if (eventData == &lkg::metal::g_beginEventToken) {
        bool valid = false;
        lkg::metal::BeginTicket ticket{};
        {
            std::lock_guard<std::mutex> lock(lkg::metal::g_providerMutex);
            ticket = lkg::metal::g_beginTicket;
            valid = lkg::metal::g_beginLifecycle.Start(ticket,
                lkg::metal::g_deviceGeneration.load(std::memory_order_acquire),
                lkg::metal::g_pluginActive.load(std::memory_order_acquire));
        }
        if (valid) lkg::metal::InitializeOnRenderThread(ticket);
        return;
    }
    if (eventData == &lkg::metal::g_pumpEventToken) {
        lkg::metal::SubmitProducerCompleteSlots();
        return;
    }
    lkg::metal::SubmitProducerCompleteSlots();
    for (uint32_t index = 0; index < lkg::metal::kSubmissionSlotCount; ++index) {
        if (eventData == &lkg::metal::g_slots[index].request) {
            lkg::metal::RenderRequest request{};
            {
                std::lock_guard<std::mutex> lock(lkg::metal::g_providerMutex);
                if (!lkg::metal::g_slots[index].requestQueued) return;
                request = lkg::metal::g_slots[index].request;
                lkg::metal::g_slots[index].requestQueued = false;
            }
            lkg::metal::QueueCopy(index, request);
            return;
        }
    }
}

extern "C" void UNITY_INTERFACE_API lkg_metal_unity_plugin_load(void* rawInterfaces) {
    IUnityInterfaces* interfaces = static_cast<IUnityInterfaces*>(rawInterfaces);
    if (interfaces == nullptr) return;
    if (!lkg::metal::PinProviderModule()) {
        lkg::metal::g_lastError.store(lkg::metal::kErrorPluginNotLoaded, std::memory_order_release);
        lkg::metal::g_providerState.store(static_cast<uint32_t>(lkg::metal::ProviderState::Faulted), std::memory_order_release);
        return;
    }
    lkg::metal::g_unityInterfaces = interfaces;
    lkg::metal::g_graphics = interfaces->Get<IUnityGraphics>();
    lkg::metal::g_metal = interfaces->Get<IUnityGraphicsMetal>();
    if (lkg::metal::g_graphics == nullptr || lkg::metal::g_metal == nullptr) {
        lkg::metal::g_lastError.store(lkg::metal::kErrorPluginNotLoaded, std::memory_order_release);
        lkg::metal::g_providerState.store(static_cast<uint32_t>(lkg::metal::ProviderState::Faulted), std::memory_order_release);
        return;
    }
    lkg::metal::g_renderEventId.store(lkg::metal::g_graphics->ReserveEventIDRange(1), std::memory_order_release);
    lkg::metal::g_renderEventIdReserved.store(true, std::memory_order_release);
    lkg::metal::g_graphics->RegisterDeviceEventCallback(lkg::metal::OnDeviceEvent);
    lkg::metal::g_pluginActive.store(true, std::memory_order_release);
}

extern "C" void UNITY_INTERFACE_API lkg_metal_unity_plugin_unload(void) {
    lkg::metal::CloseUnityMetalProducerAdmission();
    {
        std::lock_guard<std::mutex> lock(lkg::metal::g_providerMutex);
        lkg::metal::g_pluginActive.store(false, std::memory_order_release);
    }
    if (lkg::metal::g_graphics != nullptr) {
        lkg::metal::g_graphics->UnregisterDeviceEventCallback(lkg::metal::OnDeviceEvent);
    }
}

extern "C" void* lkg_metal_provider_render_event_func(void) {
    return reinterpret_cast<void*>(&lkg_metal_unity_render_event);
}

extern "C" int32_t lkg_metal_provider_render_event_id(int32_t* eventId) {
    if (eventId == nullptr) return -1;
    const int32_t reserved = lkg::metal::g_renderEventId.load(std::memory_order_acquire);
    if (!lkg::metal::g_renderEventIdReserved.load(std::memory_order_acquire)) return -2;
    *eventId = reserved;
    return 0;
}

extern "C" int32_t lkg_metal_provider_query_v1(lkg::metal::ProviderStatusV1* status) {
    return lkg::metal::QueryProviderStatus(status) ? 0 : -1;
}

extern "C" int32_t lkg_metal_provider_begin_v1(const lkg::metal::BeginRequestV1* request, void** eventData) {
    return lkg::metal::QueueProviderBegin(request, eventData) ? 0 : -1;
}

extern "C" int32_t lkg_metal_provider_submit_v1(const lkg::metal::RenderRequest* request, void** eventData) {
    return lkg::metal::QueueProviderSubmit(request, eventData) ? 0 : -1;
}

extern "C" int32_t lkg_metal_provider_pump_v1(void** eventData) {
    return lkg::metal::QueueProviderPump(eventData) ? 0 : -1;
}

extern "C" int32_t lkg_metal_provider_close(void) {
    lkg::metal::CloseUnityMetalProducerAdmission();
    lkg::metal::g_providerState.store(static_cast<uint32_t>(lkg::metal::ProviderState::Closing), std::memory_order_release);
    return 0;
}
