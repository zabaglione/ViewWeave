#include "bridge_metal_api.h"

#include <dlfcn.h>
#include <mutex>

namespace lkg::metal {
namespace {

constexpr unsigned long kFirstLookingGlassDevice = static_cast<unsigned long>(-1L);
std::mutex g_apiMutex;
void* g_pinnedModule = nullptr;
BridgeMetalSymbols g_symbols{};
WindowHandle g_window = 0;
void* g_transferTexture = nullptr;
bool g_initialized = false;
bool g_lifecycleTouched = false;

void* ResolveDlsym(void* context, const char* symbolName) {
    return dlsym(context, symbolName);
}

}

bool LoadAndInitializeBridgeMetal(const char* absoluteRuntimePath, const char* appName,
    void* metalDevice, unsigned long displayIndex, void* metalTextureDescriptor,
    WindowHandle* window, void** transferTexture, const char** errorCode) noexcept {
    if (errorCode) *errorCode = "invalid_argument";
    if (absoluteRuntimePath == nullptr || absoluteRuntimePath[0] != '/' || appName == nullptr ||
        metalDevice == nullptr || metalTextureDescriptor == nullptr || window == nullptr || transferTexture == nullptr) return false;

    std::lock_guard<std::mutex> lock(g_apiMutex);
    if (g_initialized) {
        if (g_window != 0 && g_transferTexture != nullptr) {
            *window = g_window;
            *transferTexture = g_transferTexture;
            if (errorCode) *errorCode = nullptr;
            return true;
        }
        if (errorCode) *errorCode = "already_initialized_incomplete";
        return false;
    }
    if (g_lifecycleTouched) {
        if (errorCode) *errorCode = "lifecycle_state_pinned";
        return false;
    }

    void* module = dlopen(absoluteRuntimePath, RTLD_NOW | RTLD_LOCAL);
    if (module == nullptr) { if (errorCode) *errorCode = "dlopen_failed"; return false; }

    BridgeMetalSymbols resolved{};
    const char* missing = nullptr;
    if (!ResolveRequiredBridgeMetalSymbols(ResolveDlsym, module, &resolved, &missing)) {
        if (errorCode) *errorCode = "required_symbol_missing";
        dlclose(module);
        return false;
    }

    // Probe evidence shows the SDK reads pixelFormat/width/height/storageMode/usage
    // properties from this opaque pointer, consistent with MTLTextureDescriptor*.
    // Returned texture ownership is undocumented; callers retain and never release it.
    // appName is passed as UTF-8 as required by the non-Windows SDK declaration.
    // Pin before invoking init: a failed init may have partially established SDK state.
    g_pinnedModule = module;
    g_symbols = resolved;
    g_lifecycleTouched = true;
    const unsigned long selectedDisplay = displayIndex == kFirstLookingGlassDevice ? kFirstLookingGlassDevice : displayIndex;
    if (!StartBridgeMetalWithSymbols(resolved, appName, metalDevice, selectedDisplay,
        metalTextureDescriptor, &g_window, &g_transferTexture, errorCode)) return false;
    g_initialized = true;
    *window = g_window;
    *transferTexture = g_transferTexture;
    if (errorCode) *errorCode = nullptr;
    return true;
}

bool DrawBridgeMetalQuilt(WindowHandle window, void* texture, unsigned long columns,
    unsigned long rows, float aspect, float zoom) noexcept {
    std::lock_guard<std::mutex> lock(g_apiMutex);
    if (!g_initialized || g_pinnedModule == nullptr || g_symbols.drawInteropQuiltTextureMetal == nullptr ||
        window == 0 || texture == nullptr || columns == 0 || rows == 0 || aspect <= 0.0f || zoom <= 0.0f) return false;
    // A true result means only that the SDK accepted the call. Consumer completion
    // is unknown; callers must transition the slot to ConsumerUnknownRetained.
    return g_symbols.drawInteropQuiltTextureMetal(window, texture, columns, rows, aspect, zoom);
}

bool CreateBridgeMetalTextureForWindow(WindowHandle window, void* metalTextureDescriptor,
    void** transferTexture, const char** errorCode) noexcept {
    if (errorCode) *errorCode = "invalid_argument";
    if (window == 0 || metalTextureDescriptor == nullptr || transferTexture == nullptr) return false;
    std::lock_guard<std::mutex> lock(g_apiMutex);
    if (!g_initialized || g_symbols.createMetalTextureWithIOSurface == nullptr) {
        if (errorCode) *errorCode = "bridge_metal_not_initialized";
        return false;
    }
    void* createdTexture = nullptr;
    if (!g_symbols.createMetalTextureWithIOSurface(window, metalTextureDescriptor, &createdTexture) || createdTexture == nullptr) {
        if (errorCode) *errorCode = "create_metal_texture_failed_pinned";
        return false;
    }
    *transferTexture = createdTexture;
    if (errorCode) *errorCode = nullptr;
    return true;
}

}
