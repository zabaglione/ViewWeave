#include "../platform/macos/bridge_metal_api.h"
#include "../platform/macos/metal_submission_lifetime.h"

#include <cstdio>
#include <cstring>

namespace {

char g_calls[16]{};
size_t g_callCount = 0;
bool g_initSuccess = true;
bool g_windowSuccess = true;
bool g_textureSuccess = true;
unsigned long g_displayIndex = 0;
enum class CancelAt { None, Initialize, Window, Texture };
CancelAt g_cancelAt = CancelAt::None;
lkg::metal::BeginLifecycle* g_lifecycle = nullptr;

void Record(char value) {
    if (g_callCount + 1 < sizeof(g_calls)) {
        g_calls[g_callCount++] = value;
        g_calls[g_callCount] = '\0';
    }
}
void MaybeCancel(CancelAt stage) {
    if (g_cancelAt == stage && g_lifecycle != nullptr) g_lifecycle->Cancel();
}
bool FakeInitialize(const char*) { Record('I'); MaybeCancel(CancelAt::Initialize); return g_initSuccess; }
bool FakeWindow(void*, lkg::metal::WindowHandle* window, unsigned long displayIndex) {
    Record('W');
    g_displayIndex = displayIndex;
    *window = 42;
    MaybeCancel(CancelAt::Window);
    return g_windowSuccess;
}
bool FakeTexture(lkg::metal::WindowHandle, void*, void** texture) {
    Record('T');
    *texture = reinterpret_cast<void*>(0x1234);
    MaybeCancel(CancelAt::Texture);
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

bool ContinueAlways(void*) noexcept { return true; }

struct BeginContext {
    lkg::metal::BeginLifecycle* lifecycle = nullptr;
    lkg::metal::BeginTicket ticket{};
    uint64_t generation = 0;
    bool pluginActive = true;
};

bool ContinueBegin(void* rawContext) noexcept {
    BeginContext* context = static_cast<BeginContext*>(rawContext);
    return context->lifecycle->IsExecuting(context->ticket, context->generation, context->pluginActive);
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
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueAlways, nullptr), "init failure is returned") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "I") == 0, "window and texture are not called after init failure") ? 0 : 1;

    g_callCount = 0;
    g_initSuccess = true;
    g_windowSuccess = false;
    failures += Check(!lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueAlways, nullptr), "window failure is returned") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "IW") == 0, "texture is not called after window failure") ? 0 : 1;

    g_callCount = 0;
    g_windowSuccess = true;
    g_textureSuccess = true;
    failures += Check(lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueAlways, nullptr), "startup call sequence succeeds") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "IWT") == 0 && window == 42 && texture == reinterpret_cast<void*>(0x1234),
        "init then window then texture ordering") ? 0 : 1;

    g_callCount = 0;
    g_cancelAt = CancelAt::Initialize;
    lkg::metal::BeginLifecycle cancelledDuringInit;
    BeginContext beginContext{};
    beginContext.lifecycle = &cancelledDuringInit;
    beginContext.generation = 91;
    failures += Check(cancelledDuringInit.Queue(91, &beginContext.ticket) &&
        cancelledDuringInit.Start(beginContext.ticket, 91, true), "begin execution starts with captured generation") ? 0 : 1;
    g_lifecycle = &cancelledDuringInit;
    window = 0;
    texture = nullptr;
    failures += Check(!lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueBegin, &beginContext),
        "cancel during SDK initialize prevents later SDK calls") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "I") == 0 && window == 0 && texture == nullptr,
        "cancel after initialize does not create window or transfer texture") ? 0 : 1;
    failures += Check(cancelledDuringInit.Finish(beginContext.ticket, 92, false, true) ==
        lkg::metal::BeginFinish::Cancelled, "cancelled begin cannot publish or rebind generation") ? 0 : 1;

    g_callCount = 0;
    g_cancelAt = CancelAt::Window;
    lkg::metal::BeginLifecycle cancelledDuringWindow;
    beginContext = {};
    beginContext.lifecycle = &cancelledDuringWindow;
    beginContext.generation = 101;
    failures += Check(cancelledDuringWindow.Queue(101, &beginContext.ticket) &&
        cancelledDuringWindow.Start(beginContext.ticket, 101, true), "window cancellation begin starts") ? 0 : 1;
    g_lifecycle = &cancelledDuringWindow;
    failures += Check(!lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueBegin, &beginContext),
        "cancel during window creation prevents transfer texture call") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "IW") == 0,
        "no SDK texture call occurs after close cancellation") ? 0 : 1;

    g_callCount = 0;
    g_cancelAt = CancelAt::Texture;
    lkg::metal::BeginLifecycle cancelledDuringTexture;
    beginContext = {};
    beginContext.lifecycle = &cancelledDuringTexture;
    beginContext.generation = 106;
    failures += Check(cancelledDuringTexture.Queue(106, &beginContext.ticket) &&
        cancelledDuringTexture.Start(beginContext.ticket, 106, true), "texture cancellation begin starts") ? 0 : 1;
    g_lifecycle = &cancelledDuringTexture;
    window = 0;
    texture = nullptr;
    failures += Check(!lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), 7,
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueBegin, &beginContext),
        "cancel during texture creation prevents begin publication") ? 0 : 1;
    failures += Check(std::strcmp(g_calls, "IWT") == 0 && window == 42 && texture == reinterpret_cast<void*>(0x1234),
        "created unknown resources remain available for conservative retention") ? 0 : 1;
    failures += Check(cancelledDuringTexture.Finish(beginContext.ticket, 107, false, true) ==
        lkg::metal::BeginFinish::Cancelled, "texture cancellation does not publish a new generation") ? 0 : 1;

    g_callCount = 0;
    g_cancelAt = CancelAt::None;
    lkg::metal::BeginLifecycle defaultDisplay;
    beginContext = {};
    beginContext.lifecycle = &defaultDisplay;
    beginContext.generation = 111;
    failures += Check(defaultDisplay.Queue(111, &beginContext.ticket) && defaultDisplay.Start(beginContext.ticket, 111, true),
        "default display begin starts") ? 0 : 1;
    g_lifecycle = &defaultDisplay;
    const unsigned long sdkDefaultDisplay = static_cast<unsigned long>(UINT32_MAX);
    failures += Check(lkg::metal::StartBridgeMetalWithSymbols(symbols, "test", reinterpret_cast<void*>(1), sdkDefaultDisplay,
        reinterpret_cast<void*>(2), &window, &texture, &error, ContinueBegin, &beginContext),
        "SDK default display startup succeeds") ? 0 : 1;
    failures += Check(g_displayIndex == sdkDefaultDisplay,
        "uint32 SDK default display value is preserved on LP64") ? 0 : 1;
    g_lifecycle = nullptr;
    g_cancelAt = CancelAt::None;

    if (failures != 0) return 1;
    std::puts("metal_api_contract: PASS");
    return 0;
}
