#include "bridge_metal_api.h"

namespace lkg::metal {

bool ResolveRequiredBridgeMetalSymbols(SymbolResolver resolver, void* context,
    BridgeMetalSymbols* symbols, const char** missingSymbol) noexcept {
    if (symbols == nullptr || resolver == nullptr) return false;
    BridgeMetalSymbols resolved{};
    resolved.initializeBridge = reinterpret_cast<InitializeBridgeFn>(resolver(context, "initialize_bridge"));
    resolved.instanceWindowMetal = reinterpret_cast<InstanceWindowMetalFn>(resolver(context, "instance_window_metal"));
    resolved.createMetalTextureWithIOSurface = reinterpret_cast<CreateMetalTextureWithIOSurfaceFn>(resolver(context, "create_metal_texture_with_iosurface"));
    resolved.drawInteropQuiltTextureMetal = reinterpret_cast<DrawInteropQuiltTextureMetalFn>(resolver(context, "draw_interop_quilt_texture_metal"));

    if (resolved.initializeBridge == nullptr) { if (missingSymbol) *missingSymbol = "initialize_bridge"; return false; }
    if (resolved.instanceWindowMetal == nullptr) { if (missingSymbol) *missingSymbol = "instance_window_metal"; return false; }
    if (resolved.createMetalTextureWithIOSurface == nullptr) { if (missingSymbol) *missingSymbol = "create_metal_texture_with_iosurface"; return false; }
    if (resolved.drawInteropQuiltTextureMetal == nullptr) { if (missingSymbol) *missingSymbol = "draw_interop_quilt_texture_metal"; return false; }

    *symbols = resolved;
    if (missingSymbol) *missingSymbol = nullptr;
    return true;
}

bool StartBridgeMetalWithSymbols(const BridgeMetalSymbols& symbols, const char* appName,
    void* metalDevice, unsigned long displayIndex, void* metalTextureDescriptor,
    WindowHandle* window, void** transferTexture, const char** errorCode,
    ContinueCallback continueCallback, void* continueContext) noexcept {
    if (errorCode) *errorCode = "invalid_argument";
    if (symbols.initializeBridge == nullptr || symbols.instanceWindowMetal == nullptr ||
        symbols.createMetalTextureWithIOSurface == nullptr || symbols.drawInteropQuiltTextureMetal == nullptr ||
        appName == nullptr || metalDevice == nullptr || metalTextureDescriptor == nullptr ||
        window == nullptr || transferTexture == nullptr || continueCallback == nullptr) return false;
    if (!continueCallback(continueContext)) {
        if (errorCode) *errorCode = "begin_cancelled";
        return false;
    }
    if (!symbols.initializeBridge(appName)) {
        if (errorCode) *errorCode = "initialize_bridge_failed_pinned";
        return false;
    }
    if (!continueCallback(continueContext)) {
        if (errorCode) *errorCode = "begin_cancelled";
        return false;
    }
    WindowHandle createdWindow = 0;
    const bool windowCreated = symbols.instanceWindowMetal(metalDevice, &createdWindow, displayIndex);
    if (createdWindow != 0) *window = createdWindow;
    if (!windowCreated || createdWindow == 0) {
        if (errorCode) *errorCode = "instance_window_metal_failed_pinned";
        return false;
    }
    if (!continueCallback(continueContext)) {
        if (errorCode) *errorCode = "begin_cancelled";
        return false;
    }
    void* createdTexture = nullptr;
    const bool textureCreated = symbols.createMetalTextureWithIOSurface(createdWindow, metalTextureDescriptor, &createdTexture);
    if (createdTexture != nullptr) *transferTexture = createdTexture;
    if (!textureCreated || createdTexture == nullptr) {
        if (errorCode) *errorCode = "create_metal_texture_failed_pinned";
        return false;
    }
    if (!continueCallback(continueContext)) {
        if (errorCode) *errorCode = "begin_cancelled";
        return false;
    }
    if (errorCode) *errorCode = nullptr;
    return true;
}

}
