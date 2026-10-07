#include "../platform/macos/bridge_metal_api.h"

#include <cstdio>
#include <cstring>

namespace {

char g_calls[16]{};
size_t g_callCount = 0;
bool g_initSuccess = true;
bool g_windowSuccess = true;
bool g_textureSuccess = true;

void Record(char value) { g_calls[g_callCount++] = value; }
bool FakeInitialize(const char*) { Record('I'); return g_initSuccess; }
bool FakeWindow(void*, lkg::metal::WindowHandle* window, unsigned long) {
    Record('W');
    *window = 42;
    return g_windowSuccess;
}
bool FakeTexture(lkg::metal::WindowHandle, void*, void** texture) {
    Record('T');
    *texture = reinterpret_cast<void*>(0x1234);
    return g_textureSuccess;
}
bool FakeDraw(lkg::metal::WindowHandle, void*, unsigned long, unsigned long, float, float) {
    Record('D');
    return true;
}

struct ResolverContext { int missingIndex = -1; };

void* Resolve(void* rawContext, const char* name) {
    ResolverContext* context = static_cast<ResolverContext*>(rawContext);
    if (std::strcmp(name, "initialize_bridge") == 0) return context->missingIndex == 0 ? nullptr : reinterpret_cast<void*>(&FakeInitialize);
    if (std::strcmp(name, "instance_window_metal") == 0) return context->missingIndex == 1 ? nullptr : reinterpret_cast<void*>(&FakeWindow);
    if (std::strcmp(name, "create_metal_texture_with_iosurface") == 0) return context->missingIndex == 2 ? nullptr : reinterpret_cast<void*>(&FakeTexture);
    if (std::strcmp(name, "draw_interop_quilt_texture_metal") == 0) return context->missingIndex == 3 ? nullptr : reinterpret_cast<void*>(&FakeDraw);
    return nullptr;
}

bool Check(bool condition, const char* name) {
    if (!condition) std::fprintf(stderr, "FAIL: %s\n", name);
    return condition;
}

}

int main() {
    int failures = 0;
    for (int missingIndex = 0; missingIndex < 4; ++missingIndex) {
        ResolverContext context{missingIndex};
        lkg::metal::BridgeMetalSymbols symbols{};
        const char* missing = nullptr;
        const bool resolved = lkg::metal::ResolveRequiredBridgeMetalSymbols(Resolve, &context, &symbols, &missing);
        failures += Check(!resolved && missing != nullptr, "required symbol absence blocks startup") ? 0 : 1;
        failures += Check(g_callCount == 0, "symbol validation does not call SDK") ? 0 : 1;
    }

    ResolverContext context{};
    lkg::metal::BridgeMetalSymbols symbols{};
    const char* missing = nullptr;
    failures += Check(lkg::metal::ResolveRequiredBridgeMetalSymbols(Resolve, &context, &symbols, &missing), "all required symbols resolve") ? 0 : 1;

    lkg::metal::WindowHandle window = 0;
    void* texture = nullptr;
    const char* error = nullptr;
    g_callCount = 0;
    g_initSuccess = false;
    failures += Check(!lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error), "init failure is returned") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "I") == 0, "window and texture are not called after init failure") ? 0 : 1;

    g_callCount = 0;
    g_initSuccess = true;
    g_windowSuccess = false;
    failures += Check(!lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error), "window failure is returned") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "IW") == 0, "texture is not called after window failure") ? 0 : 1;

    g_callCount = 0;
    g_windowSuccess = true;
    g_textureSuccess = true;
    failures += Check(lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error), "startup call sequence succeeds") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "IWT") == 0 && window == 42 && texture == reinterpret_cast<void*>(0x1234),
        "init then window then texture ordering") ? 0 : 1;

    if (failures != 0) return 1;
    std::puts("metal_api_contract: PASS");
    return 0;
}
