#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <cstdio>
#include <cstdlib>
#include <string>

static ID3D11Device* g_device = nullptr;
static std::string g_mode;
static DWORD g_delayMs = 0;
static std::string g_releaseFile;

static std::string EnvironmentValue(const char* name)
{
    char buffer[4096]{};
    DWORD length = GetEnvironmentVariableA(name, buffer, static_cast<DWORD>(sizeof(buffer)));
    return length == 0 || length >= sizeof(buffer) ? std::string() : std::string(buffer, length);
}

static void ConfigureFault()
{
    g_mode = EnvironmentValue("LKG_FAKE_BRIDGE_MODE");
    std::string delay = EnvironmentValue("LKG_FAKE_BRIDGE_DELAY_MS");
    long value = delay.empty() ? 0 : std::strtol(delay.c_str(), nullptr, 10);
    if (value < 0) value = 0;
    if (value > 30000) value = 30000;
    g_delayMs = static_cast<DWORD>(value);
    g_releaseFile = EnvironmentValue("LKG_FAKE_BRIDGE_RELEASE_FILE");
}

static void DelayFault(const char* phase)
{
    ConfigureFault();
    if (g_mode == phase && g_delayMs) Sleep(g_delayMs);
    if (std::string(phase) == "draw-delay" && g_mode == "draw-release" && g_delayMs) {
        const DWORD start = GetTickCount();
        while (GetTickCount() - start < g_delayMs) {
            if (!g_releaseFile.empty() && GetFileAttributesA(g_releaseFile.c_str()) != INVALID_FILE_ATTRIBUTES) return;
            Sleep(50);
        }
    }
}

extern "C" __declspec(dllexport) bool initialize_bridge(const wchar_t*) { ConfigureFault(); DelayFault("init-delay"); return true; }
extern "C" __declspec(dllexport) bool uninitialize_bridge() { if (g_device) { g_device->Release(); g_device = nullptr; } return true; }
extern "C" __declspec(dllexport) bool get_bridge_version(unsigned long* major, unsigned long* minor, unsigned long* build, int* postfix, wchar_t*) {
    if (major) *major = 1; if (minor) *minor = 0; if (build) *build = 0; if (postfix) *postfix = 0; return true;
}
extern "C" __declspec(dllexport) bool get_displays(int* count, unsigned long* ids) { if (count) *count = 1; if (ids) ids[0] = 0; return true; }
extern "C" __declspec(dllexport) bool get_dimensions_for_display(unsigned long, unsigned long* width, unsigned long* height) { if (width) *width = 1; if (height) *height = 1; return true; }
extern "C" __declspec(dllexport) bool get_viewcone_for_display(unsigned long, float* value) { if (value) *value = 40.0f; return true; }
extern "C" __declspec(dllexport) bool get_default_quilt_settings_for_display(unsigned long, float* aspect, int* width, int* height, int* columns, int* rows) {
    if (aspect) *aspect = 1.0f; if (width) *width = 1920; if (height) *height = 1080; if (columns) *columns = 8; if (rows) *rows = 6; return true;
}
extern "C" __declspec(dllexport) bool get_window_position_for_display(unsigned long, long* x, long* y) { if (x) *x = 0; if (y) *y = 0; return true; }
extern "C" __declspec(dllexport) bool get_display_for_window(unsigned long, unsigned long* display) { if (display) *display = 0; return true; }
extern "C" __declspec(dllexport) bool release_device_dx(IUnknown* device) { if (device) device->Release(); return true; }
extern "C" __declspec(dllexport) bool instance_window_dx(IUnknown* device, unsigned long* window, unsigned long) {
    if (!device || !window) return false;
    if (g_device) g_device->Release();
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&g_device)))) return false;
    *window = 1;
    return true;
}
extern "C" __declspec(dllexport) bool show_window(unsigned long, bool) { return true; }
extern "C" __declspec(dllexport) bool register_texture_dx(unsigned long, IUnknown*) { return true; }
extern "C" __declspec(dllexport) bool unregister_texture_dx(unsigned long, IUnknown*) { return true; }
extern "C" __declspec(dllexport) bool create_texture_dx(unsigned long, unsigned long width, unsigned long height, IUnknown** texture) {
    if (!g_device || !texture || width == 0 || height == 0) return false;
    D3D11_TEXTURE2D_DESC desc{}; desc.Width = width; desc.Height = height; desc.MipLevels = 1; desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET; desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    ID3D11Texture2D* created = nullptr;
    if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &created))) return false;
    *texture = created; return true;
}
extern "C" __declspec(dllexport) bool copy_texture_dx(unsigned long, IUnknown* source, IUnknown* destination) {
    if (!g_device || !source || !destination) return false;
    ID3D11DeviceContext* context = nullptr; ID3D11Resource* sourceResource = nullptr; ID3D11Resource* destinationResource = nullptr;
    bool ok = SUCCEEDED(source->QueryInterface(IID_PPV_ARGS(&sourceResource))) && SUCCEEDED(destination->QueryInterface(IID_PPV_ARGS(&destinationResource)));
    if (ok) { g_device->GetImmediateContext(&context); if (!context) ok = false; else context->CopyResource(destinationResource, sourceResource); }
    if (context) context->Release(); if (sourceResource) sourceResource->Release(); if (destinationResource) destinationResource->Release(); return ok;
}
extern "C" __declspec(dllexport) bool release_texture_dx(unsigned long, IUnknown* texture) { if (texture) texture->Release(); return true; }
extern "C" __declspec(dllexport) bool draw_interop_quilt_texture_dx(unsigned long, IUnknown*, unsigned long, unsigned long, float, float) { DelayFault("draw-delay"); return true; }
extern "C" __declspec(dllexport) bool save_texture_to_file_dx(unsigned long, wchar_t*, IUnknown*) { return false; }
