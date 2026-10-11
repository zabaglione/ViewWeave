#import <Cocoa/Cocoa.h>
#import <Metal/Metal.h>
#import <QuartzCore/CAMetalLayer.h>
#include "metal_display.h"
#include "metal_calibration.h"
#include "metal_bridge_cells.h"
#include "metal_provider_api.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsMetal.h"
#include <atomic>
#include <cmath>
#include <dlfcn.h>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>
static_assert(sizeof(LkgMetalDisplayInfo) == 60, "Metal display ABI mismatch");

namespace {
std::atomic<IUnityGraphicsMetal*> unityMetal{nullptr};
IUnityGraphics* displayGraphics = nullptr;
std::mutex sessionMutex;
std::string lastError;
uint64_t nextId = 1, activeId = 0;
bool metadataFault = false;
void* pinnedMetadataModule = nullptr;

struct DisplaySession {
    NSWindow* window = nil;
    CAMetalLayer* layer = nil;
    id<MTLDevice> device = nil;
    id<MTLCommandQueue> queue = nil;
    id<MTLRenderPipelineState> pipeline = nil;
    id<MTLTexture> transfer = nil;
    dispatch_queue_t presentation = dispatch_queue_create("com.viewweave.metal.presentation", DISPATCH_QUEUE_SERIAL);
    CGDirectDisplayID displayId = 0;
    CGRect bounds{};
    std::atomic<bool> busy{false}, closing{false};
    std::atomic<uint32_t> submitted{0}, completed{0}, dropped{0}, error{0};
    uint32_t width = 0, height = 0;
    ~DisplaySession() {
        [transfer release]; [pipeline release]; [queue release]; [device release];
        dispatch_release(presentation);
        // GPU callbacks can own the last reference. AppKit objects retire on the main thread.
        NSWindow* retiredWindow = window;
        CAMetalLayer* retiredLayer = layer;
        auto cleanup = ^{
            [retiredWindow orderOut:nil]; [retiredWindow close];
            [retiredWindow release]; [retiredLayer release];
        };
        if (pthread_main_np()) cleanup(); else dispatch_async(dispatch_get_main_queue(), cleanup);
    }
};
std::shared_ptr<DisplaySession> activeSession;
struct Frame {
    std::shared_ptr<DisplaySession> session;
    id<MTLTexture> source = nil;
    ~Frame() { [source release]; }
};
uintptr_t nextEventId = 1;
std::unordered_map<uintptr_t, std::shared_ptr<Frame>> queuedFrames;
// Called with sessionMutex held. A late Unity event sees a missing token, never a freed pointer.
void CancelQueuedFrames(const std::shared_ptr<DisplaySession>& session) {
    for (auto it = queuedFrames.begin(); it != queuedFrames.end();) {
        if (it->second->session == session) {
            it = queuedFrames.erase(it);
            session->busy.store(false, std::memory_order_release);
        } else ++it;
    }
}
void RetireIfClosed(std::shared_ptr<DisplaySession> session) {
    dispatch_async(dispatch_get_main_queue(), ^{
        std::lock_guard<std::mutex> lock(sessionMutex);
        if (activeSession == session && session->closing.load() && !session->busy.load()) {
            activeSession.reset(); activeId = 0;
        }
    });
}
void ReleaseAdmission(const std::shared_ptr<DisplaySession>& session) {
    session->busy.store(false, std::memory_order_release);
    if (session->closing.load()) RetireIfClosed(session);
}
void UNITY_INTERFACE_API DisplayDeviceEvent(UnityGfxDeviceEventType event) {
    if (event != kUnityGfxDeviceEventShutdown && event != kUnityGfxDeviceEventBeforeReset) return;
    std::shared_ptr<DisplaySession> retired;
    {
        std::lock_guard<std::mutex> lock(sessionMutex);
        retired = activeSession;
        if (retired) {
            retired->error.store(8); retired->closing.store(true);
            CancelQueuedFrames(retired);
        }
    }
    if (retired) dispatch_async(dispatch_get_main_queue(), ^{
        [retired->window orderOut:nil];
        if (!retired->busy.load()) RetireIfClosed(retired);
    });
}
template<class T> T Symbol(void* module, const char* name) { return reinterpret_cast<T>(dlsym(module, name)); }
bool ReadProfile(const char* path, const char* name, uint32_t ordinal, uint32_t requestedMode,
    LkgMetalDisplayInfoV3* profile, long* x, long* y) {
    auto* result = &profile->calibration;
    if (metadataFault) { lastError = "Bridge metadata shutdown is unproven; restart Unity before opening another session"; return false; }
    if (lkg_bridge_is_initialized() != 0) { lastError = "Another Bridge metadata owner is active"; return false; }
    lkg::metal::ProviderStatusV1 provider{};
    provider.structSize = sizeof(provider); provider.apiVersion = 1;
    if (lkg_metal_provider_query_v1(&provider) == 0 && provider.providerState != 1) {
        lastError = "The finite Metal experiment owns this process; restart Unity before using Metal display output"; return false;
    }
    void* module = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!module) { lastError = "Bridge runtime could not be loaded"; return false; }
    auto init = Symbol<bool(*)(const char*)>(module, "initialize_bridge");
    auto stop = Symbol<bool(*)()>(module, "uninitialize_bridge");
    auto displays = Symbol<bool(*)(int*, unsigned long*)>(module, "get_displays");
    auto dimensions = Symbol<bool(*)(unsigned long, unsigned long*, unsigned long*)>(module, "get_dimensions_for_display");
    auto quilt = Symbol<bool(*)(unsigned long, float*, int*, int*, int*, int*)>(module, "get_default_quilt_settings_for_display");
    auto position = Symbol<bool(*)(unsigned long, long*, long*)>(module, "get_window_position_for_display");
    auto redIndex = Symbol<bool(*)(unsigned long, int*)>(module, "get_ri_for_display");
    auto blueIndex = Symbol<bool(*)(unsigned long, int*)>(module, "get_bi_for_display");
    auto calibration = Symbol<bool(*)(unsigned long, float*, float*, float*, int*, int*, float*, float*, int*,
        float*, float*, int*, int*, void*)>(module, "get_calibration_for_display");
    if (!init || !stop || !displays || !dimensions || !quilt || !position || !calibration || !redIndex || !blueIndex) {
        lastError = "Required Bridge metadata symbols are missing"; dlclose(module); return false;
    }
    bool ok = false;
    try {
    if (init(name)) {
        int count = 0;
        if (displays(&count, nullptr) && count > 0 && count <= 256 && ordinal < static_cast<uint32_t>(count)) {
            std::vector<unsigned long> indices(count);
            int capacity = count;
            if (displays(&count, indices.data()) && count > 0 && count <= capacity && ordinal < static_cast<uint32_t>(count)) {
                unsigned long index = indices[ordinal], width = 0, height = 0;
                int cw = 0, ch = 0, mode = 0, cells = 0;
                float fringe = 0;
                result->display.index = static_cast<uint32_t>(index);
                ok = dimensions(index, &width, &height) && position(index, x, y) &&
                    quilt(index, &result->display.aspect, &result->display.quilt_width, &result->display.quilt_height,
                        &result->display.quilt_columns, &result->display.quilt_rows) &&
                    calibration(index, &result->center, &result->pitch, &result->slope, &cw, &ch, &result->dpi,
                        &result->flipX, &result->invertedViews, &result->display.viewcone, &fringe, &mode, &cells, nullptr) &&
                    redIndex(index, &profile->redIndex) && blueIndex(index, &profile->blueIndex);
                if (!ok) lastError = "Bridge display metadata or calibration query failed";
                if (ok && (width != static_cast<unsigned long>(cw) || height != static_cast<unsigned long>(ch))) {
                    ok = false; lastError = "Invalid calibration: Bridge display and calibration dimensions differ";
                }
                if (ok && (width > UINT32_MAX || height > UINT32_MAX || index > UINT32_MAX)) {
                    ok = false; lastError = "Invalid calibration: display dimensions or index exceed the native ABI";
                }
                result->display.width = static_cast<uint32_t>(width); result->display.height = static_cast<uint32_t>(height);
                const bool classicRgb = requestedMode == 1;
                profile->cellPatternMode = classicRgb ? 0 : mode;
                profile->cellCount = classicRgb ? 0 : cells;
                if (ok && !classicRgb) {
                    if (const char* error = lkg::metal::ValidateCellPattern(mode, cells)) { ok = false; lastError = error; }
                    if (ok && cells > 0) ok = lkg::metal::ReadBridgeCellTable(*profile, *x, *y, lastError);
                }
                if (ok) if (const char* error = lkg::metal::ValidateCalibration(*result,
                    profile->cellPatternMode, profile->cellCount, fringe,
                    profile->redIndex, profile->blueIndex, profile->cells, requestedMode)) {
                    ok = false; lastError = error;
                }
            }
        }
        if (!ok && lastError.empty()) lastError = "Looking Glass display was not found; check Bridge and Desktop Mode";
    } else lastError = "Bridge initialization failed";
    } catch (...) { lastError = "Bridge metadata query raised an exception"; ok = false; }
    // The SDK is used only for metadata. It never owns the display texture or window.
    bool stopped = false;
    try { stopped = stop(); } catch (...) {}
    if (!stopped) {
        ok = false; lastError = "Bridge metadata shutdown failed; restart Unity before retrying";
        metadataFault = true; pinnedMetadataModule = module;
    } else dlclose(module);
    return ok;
}

void Present(const std::shared_ptr<Frame>& frame) {
    auto s = frame->session;
    @autoreleasepool {
        if (s->closing.load()) { ReleaseAdmission(s); return; }
        id<CAMetalDrawable> drawable = [s->layer nextDrawable];
        if (drawable == nil) { s->dropped.fetch_add(1); ReleaseAdmission(s); return; }
        id<MTLCommandBuffer> command = [s->queue commandBuffer];
        MTLRenderPassDescriptor* pass = [MTLRenderPassDescriptor renderPassDescriptor];
        pass.colorAttachments[0].texture = drawable.texture;
        pass.colorAttachments[0].loadAction = MTLLoadActionDontCare;
        pass.colorAttachments[0].storeAction = MTLStoreActionStore;
        id<MTLRenderCommandEncoder> encoder = [command renderCommandEncoderWithDescriptor:pass];
        if (command == nil || encoder == nil) { s->error.store(4); ReleaseAdmission(s); return; }
        [encoder setRenderPipelineState:s->pipeline];
        [encoder setFragmentTexture:s->transfer atIndex:0];
        [encoder drawPrimitives:MTLPrimitiveTypeTriangle vertexStart:0 vertexCount:3];
        [encoder endEncoding]; [command presentDrawable:drawable];
        [command addCompletedHandler:^(id<MTLCommandBuffer> done) {
            if (done.status == MTLCommandBufferStatusCompleted) s->completed.fetch_add(1);
            else s->error.store(5);
            // Consumer completion gates reuse of the private transfer texture.
            ReleaseAdmission(s);
        }];
        [command commit];
    }
}

void UNITY_INTERFACE_API Render(int, void* data) {
    std::shared_ptr<Frame> frame;
    {
        std::lock_guard<std::mutex> lock(sessionMutex);
        auto ticket = queuedFrames.find(reinterpret_cast<uintptr_t>(data));
        if (ticket == queuedFrames.end()) return;
        frame = ticket->second; queuedFrames.erase(ticket);
    }
    auto s = frame->session;
    IUnityGraphicsMetal* metal = unityMetal.load();
    @autoreleasepool {
        if (s->closing.load() || metal == nullptr || frame->source.device != metal->MetalDevice() ||
            frame->source.device != s->device) { s->error.store(1); ReleaseAdmission(s); return; }
        id<MTLCommandBuffer> producer = metal->CurrentCommandBuffer();
        if (producer == nil) { s->error.store(2); ReleaseAdmission(s); return; }
        metal->EndCurrentCommandEncoder();
        if (s->transfer == nil) {
            MTLTextureDescriptor* descriptor = [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:frame->source.pixelFormat
                width:s->width height:s->height mipmapped:NO];
            descriptor.storageMode = MTLStorageModePrivate; descriptor.usage = MTLTextureUsageShaderRead;
            s->transfer = [s->device newTextureWithDescriptor:descriptor];
        }
        id<MTLBlitCommandEncoder> copy = [producer blitCommandEncoder];
        if (copy == nil || s->transfer == nil) { [copy endEncoding]; s->error.store(3); ReleaseAdmission(s); return; }
        [copy copyFromTexture:frame->source sourceSlice:0 sourceLevel:0 sourceOrigin:MTLOriginMake(0,0,0)
            sourceSize:MTLSizeMake(s->width,s->height,1) toTexture:s->transfer destinationSlice:0 destinationLevel:0
            destinationOrigin:MTLOriginMake(0,0,0)];
        [copy endEncoding];
        [producer addCompletedHandler:^(id<MTLCommandBuffer> done) {
            if (done.status == MTLCommandBufferStatusCompleted) {
                dispatch_async(s->presentation, ^{ Present(frame); });
            }
            else { s->error.store(6); ReleaseAdmission(s); }
        }];
    }
}
}

static uint64_t OpenDisplay(const char* path, const char* name, uint32_t ordinal, uint32_t requestedMode,
    LkgMetalDisplayInfoV3* profile) {
    std::lock_guard<std::mutex> lock(sessionMutex);
    lastError.clear();
    if (requestedMode > 2) { lastError = "Invalid output mode: expected Automatic, Classic RGB, or Calibrated Cells"; return 0; }
    if (!pthread_main_np() || path == nullptr || path[0] != '/' || name == nullptr) {
        lastError = "Metal display open requires the main thread, an absolute runtime path and application name"; return 0;
    }
    if (!lkg::metal::IsDisplayInfoV3HeaderValid(profile)) {
        lastError = "Metal display ABI mismatch; update the managed package and native plugin together, then restart Unity"; return 0;
    }
    if (activeSession && activeSession->closing.load() && !activeSession->busy.load()) {
        activeSession.reset(); activeId = 0;
    }
    if (activeSession) { lastError = "A Metal display session is already active"; return 0; }
    @autoreleasepool {
        long x = 0, y = 0;
        profile->calibration = {}; profile->redIndex = profile->blueIndex = 0;
        profile->cellPatternMode = profile->cellCount = 0;
        for (auto& cell : profile->cells) cell = {};
        if (!ReadProfile(path, name, ordinal, requestedMode, profile, &x, &y)) return 0;
        auto* info = &profile->calibration;
        NSScreen* selected = nil;
        for (NSScreen* screen in NSScreen.screens) {
            CGDirectDisplayID display = [screen.deviceDescription[@"NSScreenNumber"] unsignedIntValue];
            CGRect bounds = CGDisplayBounds(display);
            if (static_cast<long>(bounds.origin.x) == x && static_cast<long>(bounds.origin.y) == y &&
                CGDisplayPixelsWide(display) == info->display.width && CGDisplayPixelsHigh(display) == info->display.height)
                selected = screen;
        }
        if (selected == nil) { lastError = "Calibrated display does not match an extended macOS screen at native resolution"; return 0; }
        CGDirectDisplayID selectedId = [selected.deviceDescription[@"NSScreenNumber"] unsignedIntValue];
        if (std::abs(CGDisplayRotation(selectedId)) > 0.001) {
            lastError = "Rotated Looking Glass screens are unsupported; set macOS display rotation to zero"; return 0;
        }
        auto s = std::make_shared<DisplaySession>();
        s->width = info->display.width; s->height = info->display.height;
        s->displayId = selectedId;
        s->bounds = CGDisplayBounds(s->displayId);
        s->device = MTLCreateSystemDefaultDevice(); s->queue = [s->device newCommandQueue];
        // Original full-screen presenter: no optical transformation in native code.
        NSString* source = @"#include <metal_stdlib>\nusing namespace metal;\n"
            "struct V{float4 p [[position]]; float2 uv;};\n"
            // Graphics.Blit's Metal RenderTexture is bottom-origin in Unity's logical image.
            // Map that image to AppKit's display instead of applying a second vertical inversion.
            "vertex V vertex_main(uint n [[vertex_id]]){float2 p=float2((n<<1)&2,n&2);return {float4(p*2-1,0,1),p};}\n"
            "fragment float4 fragment_main(V v [[stage_in]], texture2d<float> t [[texture(0)]]){constexpr sampler s(coord::normalized,filter::nearest);return t.sample(s,v.uv);}";
        NSError* error = nil;
        id<MTLLibrary> library = [s->device newLibraryWithSource:source options:nil error:&error];
        MTLRenderPipelineDescriptor* descriptor = [[MTLRenderPipelineDescriptor alloc] init];
        descriptor.vertexFunction = [library newFunctionWithName:@"vertex_main"];
        descriptor.fragmentFunction = [library newFunctionWithName:@"fragment_main"];
        descriptor.colorAttachments[0].pixelFormat = MTLPixelFormatBGRA8Unorm;
        s->pipeline = [s->device newRenderPipelineStateWithDescriptor:descriptor error:&error];
        [descriptor.vertexFunction release]; [descriptor.fragmentFunction release]; [descriptor release]; [library release];
        if (s->pipeline == nil || s->queue == nil) { lastError = "Metal presentation pipeline could not be created"; return 0; }
        NSRect localFrame = NSMakeRect(0, 0, selected.frame.size.width, selected.frame.size.height);
        s->window = [[NSWindow alloc] initWithContentRect:localFrame styleMask:NSWindowStyleMaskBorderless
            backing:NSBackingStoreBuffered defer:NO screen:selected];
        s->window.releasedWhenClosed = NO; s->window.level = NSFloatingWindowLevel;
        s->window.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces | NSWindowCollectionBehaviorFullScreenAuxiliary;
        s->window.ignoresMouseEvents = YES;
        s->layer = [[CAMetalLayer alloc] init]; s->layer.device = s->device;
        s->layer.pixelFormat = MTLPixelFormatBGRA8Unorm; s->layer.framebufferOnly = YES;
        s->layer.drawableSize = CGSizeMake(s->width,s->height);
        s->layer.contentsScale = selected.backingScaleFactor;
        s->window.contentView.wantsLayer = YES; s->window.contentView.layer = s->layer;
        [s->window orderFront:nil];
        activeSession = s; activeId = nextId++;
        return activeId;
    }
}
extern "C" uint64_t lkg_metal_display_open_v3(const char* path, const char* name, uint32_t ordinal,
    LkgMetalDisplayInfoV3* info) { return OpenDisplay(path, name, ordinal, 0, info); }
extern "C" uint64_t lkg_metal_display_open_v4(const char* path, const char* name, uint32_t ordinal,
    uint32_t requestedMode, LkgMetalDisplayInfoV3* info) {
    return OpenDisplay(path, name, ordinal, requestedMode, info);
}
extern "C" int32_t lkg_metal_display_submit(uint64_t sessionId, void* texture, void** data) {
    if (texture == nullptr || data == nullptr) return -1;
    std::lock_guard<std::mutex> lock(sessionMutex);
    auto s = activeSession;
    if (!s || sessionId != activeId || s->closing.load() || s->error.load()) return -2;
    id<MTLTexture> source = (__bridge id<MTLTexture>)texture;
    if (source.width != s->width || source.height != s->height || source.sampleCount != 1 || source.mipmapLevelCount != 1 ||
        (source.pixelFormat != MTLPixelFormatRGBA8Unorm && source.pixelFormat != MTLPixelFormatBGRA8Unorm)) return -3;
    bool expected = false;
    if (!s->busy.compare_exchange_strong(expected, true)) { s->dropped.fetch_add(1); return 1; }
    auto frame = std::make_shared<Frame>(); frame->session = s; frame->source = [source retain];
    uintptr_t token = nextEventId++;
    queuedFrames.emplace(token, frame);
    *data = reinterpret_cast<void*>(token); s->submitted.fetch_add(1);
    return 0;
}
extern "C" int32_t lkg_metal_display_status(uint64_t id, LkgMetalDisplayStatus* status) {
    std::lock_guard<std::mutex> lock(sessionMutex);
    if (!status || !activeSession || id != activeId) return -1;
    auto s = activeSession;
    if (!CGDisplayIsActive(s->displayId) || std::abs(CGDisplayRotation(s->displayId)) > 0.001 || CGDisplayPixelsWide(s->displayId) != s->width ||
        CGDisplayPixelsHigh(s->displayId) != s->height || !CGRectEqualToRect(CGDisplayBounds(s->displayId), s->bounds))
        s->error.store(7);
    *status = {s->submitted.load(),s->completed.load(),s->dropped.load(),s->error.load(),
        s->busy.load() ? 1u : 0u, s->closing.load() ? 1u : 0u}; return 0;
}
extern "C" void lkg_metal_display_close(uint64_t id) {
    if (!pthread_main_np()) return;
    std::lock_guard<std::mutex> lock(sessionMutex);
    if (!activeSession || activeId != id) return;
    activeSession->closing.store(true); [activeSession->window orderOut:nil];
    CancelQueuedFrames(activeSession);
    if (!activeSession->busy.load()) { activeSession.reset(); activeId = 0; }
}
extern "C" const char* lkg_metal_display_error() { return lastError.c_str(); }
extern "C" void* lkg_metal_display_render_event() { return reinterpret_cast<void*>(&Render); }
extern "C" void lkg_metal_display_plugin_load(void* interfaces) {
    auto registry = static_cast<IUnityInterfaces*>(interfaces);
    unityMetal.store(registry->Get<IUnityGraphicsMetal>());
    displayGraphics = registry->Get<IUnityGraphics>();
    if (displayGraphics) displayGraphics->RegisterDeviceEventCallback(DisplayDeviceEvent);
}
extern "C" void lkg_metal_display_plugin_unload() {
    DisplayDeviceEvent(kUnityGfxDeviceEventShutdown);
    lkg_metal_display_close(activeId); unityMetal.store(nullptr);
    if (displayGraphics) displayGraphics->UnregisterDeviceEventCallback(DisplayDeviceEvent);
    displayGraphics = nullptr;
}
