#pragma once

#include <cstdint>

namespace lkg::metal {

using WindowHandle = unsigned long;
using InitializeBridgeFn = bool (*)(const char* appName);
using InstanceWindowMetalFn = bool (*)(void* metalDevice, WindowHandle* window, unsigned long displayIndex);
using CreateMetalTextureWithIOSurfaceFn = bool (*)(WindowHandle window, void* descriptor, void** texture);
using DrawInteropQuiltTextureMetalFn = bool (*)(WindowHandle window, void* texture, unsigned long columns,
    unsigned long rows, float aspect, float zoom);

struct BridgeMetalSymbols {
    InitializeBridgeFn initializeBridge = nullptr;
    InstanceWindowMetalFn instanceWindowMetal = nullptr;
    CreateMetalTextureWithIOSurfaceFn createMetalTextureWithIOSurface = nullptr;
    DrawInteropQuiltTextureMetalFn drawInteropQuiltTextureMetal = nullptr;
};

using SymbolResolver = void* (*)(void* context, const char* symbolName);

bool ResolveRequiredBridgeMetalSymbols(SymbolResolver resolver, void* context,
    BridgeMetalSymbols* symbols, const char** missingSymbol) noexcept;

bool StartBridgeMetalWithSymbols(const BridgeMetalSymbols& symbols, const char* appName,
    void* metalDevice, unsigned long displayIndex, void* metalTextureDescriptor,
    WindowHandle* window, void** transferTexture, const char** errorCode) noexcept;

// Experimental entry point. It pins the SDK module and does not call the
// undocumented uninitialize/release APIs during process lifetime.
bool LoadAndInitializeBridgeMetal(const char* absoluteRuntimePath, const char* appName,
    void* metalDevice, unsigned long displayIndex, void* metalTextureDescriptor,
    WindowHandle* window, void** transferTexture, const char** errorCode) noexcept;

bool DrawBridgeMetalQuilt(WindowHandle window, void* texture, unsigned long columns,
    unsigned long rows, float aspect, float zoom) noexcept;

bool CreateBridgeMetalTextureForWindow(WindowHandle window, void* metalTextureDescriptor,
    void** transferTexture, const char** errorCode) noexcept;

}
