#include "manual_subpixel_presenter.h"

#include "../../common/subpixel_calibration.h"

#include <dxgi1_6.h>
#include <windows.h>
#include <wrl/client.h>

#include "manual_subpixel_presenter_vertex_shader.h"
#include "manual_subpixel_presenter_pixel_shader.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstddef>
#include <iterator>
#include <limits>
#include <new>
#include <utility>

using Microsoft::WRL::ComPtr;

namespace lkg::subpixel {
namespace {

constexpr uint32_t kAutomaticMode = 0;
constexpr uint32_t kClassicRgbMode = 1;
constexpr uint32_t kCalibratedCellsMode = 2;
constexpr UINT kCompletionPollCount = 200;
constexpr DWORD kCompletionPollDelayMs = 1;
constexpr wchar_t kWindowClassName[] = L"ViewWeaveManualSubpixelPresenter";

struct PresentConstants {
    float calibration[4];
    float screen[4];
    float grid[4];
    float quilt[4];
    float outputOptions[4];
    float channelOrder[4];
    float cellOffsets[12][4];
};

static_assert(sizeof(PresentConstants) % 16 == 0, "D3D11 constant buffer must be 16-byte aligned");

bool IsValidCalibration(const LkgWorkerSubpixelCalibrationV1& value,
                        const LkgWorkerSubpixelCellV1 cells[4],
                        uint32_t mode,
                        std::string* error) {
    if (value.structSize != sizeof(LkgWorkerSubpixelCalibrationV1) || value.version != 1 ||
        value.windowX == std::numeric_limits<int32_t>::min() ||
        value.windowY == std::numeric_limits<int32_t>::min()) {
        if (error) *error = "Manual subpixel calibration header or window origin is invalid.";
        return false;
    }

    LkgCommonSubpixelCalibration common{};
    common.width = value.nativeWidth;
    common.height = value.nativeHeight;
    common.center = value.center;
    common.pitch = value.pitch;
    common.slope = value.slope;
    common.dpi = value.dpi;
    common.flipX = value.flipX;
    common.viewCone = value.viewCone;
    common.fringe = value.fringe;
    common.invertedViews = value.invertedViews;
    common.cellPatternMode = value.cellPatternMode;
    common.cellCount = value.cellCount;
    common.redIndex = value.redIndex;
    common.blueIndex = value.blueIndex;
    LkgCommonSubpixelCell commonCells[4]{};
    if (cells) {
        for (size_t index = 0; index < std::size(commonCells); ++index) {
            commonCells[index] = { cells[index].redX, cells[index].redY,
                cells[index].greenX, cells[index].greenY, cells[index].blueX, cells[index].blueY };
        }
    }
    if (const char* validationError = lkg_validate_subpixel_calibration(
            &common, mode, cells ? commonCells : nullptr)) {
        if (error) *error = validationError;
        return false;
    }
    return true;
}

bool IsFinite(float value) noexcept {
    return std::isfinite(value);
}

void SetError(std::string* error, const char* message) {
    if (error) {
        *error = message;
    }
}

class ThreadDpiContext final {
public:
    ThreadDpiContext() noexcept {
        previous_ = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        changed_ = previous_ != nullptr;
    }
    ~ThreadDpiContext() {
        if (changed_) {
            SetThreadDpiAwarenessContext(previous_);
        }
    }
    bool IsValid() const noexcept { return changed_; }
private:
    DPI_AWARENESS_CONTEXT previous_ = nullptr;
    bool changed_ = false;
};

struct MonitorSearch {
    RECT expected{};
    MONITORINFOEXW info{};
    bool found = false;
};

struct WindowEvents {
    bool closing = false;
    bool displayChanged = false;
    bool movedOrResized = false;
    bool initializingWindow = false;
};

BOOL CALLBACK FindExactMonitor(HMONITOR monitor, HDC, LPRECT, LPARAM parameter) {
    auto* search = reinterpret_cast<MonitorSearch*>(parameter);
    MONITORINFOEXW info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) {
        return TRUE;
    }
    if (info.rcMonitor.left == search->expected.left && info.rcMonitor.top == search->expected.top &&
        info.rcMonitor.right == search->expected.right && info.rcMonitor.bottom == search->expected.bottom) {
        search->info = info;
        search->found = true;
        return FALSE;
    }
    return TRUE;
}

bool IsUnrotatedDisplay(const MONITORINFOEXW& monitor, std::string* error) {
    DEVMODEW mode{};
    mode.dmSize = sizeof(mode);
    if (!EnumDisplaySettingsW(monitor.szDevice, ENUM_CURRENT_SETTINGS, &mode) ||
        (mode.dmFields & DM_DISPLAYORIENTATION) == 0 || mode.dmDisplayOrientation != DMDO_DEFAULT) {
        SetError(error, "Manual subpixel output requires an unrotated native display mode.");
        return false;
    }
    if (mode.dmPelsWidth != static_cast<DWORD>(monitor.rcMonitor.right - monitor.rcMonitor.left) ||
        mode.dmPelsHeight != static_cast<DWORD>(monitor.rcMonitor.bottom - monitor.rcMonitor.top)) {
        SetError(error, "Manual subpixel output requires the display's current native pixel geometry.");
        return false;
    }
    return true;
}

bool IsValidSdrOutput(IDXGIOutput* output, std::string* error) {
    ComPtr<IDXGIOutput6> output6;
    if (!output || FAILED(output->QueryInterface(IID_PPV_ARGS(&output6)))) {
        SetError(error, "Manual subpixel output could not verify the active display color space.");
        return false;
    }
    DXGI_OUTPUT_DESC1 description{};
    if (FAILED(output6->GetDesc1(&description)) ||
        description.ColorSpace != DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709) {
        SetError(error, "Manual subpixel output requires an active SDR display color space.");
        return false;
    }
    return true;
}

LRESULT CALLBACK PresenterWindowProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    auto* events = reinterpret_cast<WindowEvents*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        const auto* create = reinterpret_cast<const CREATESTRUCTW*>(lParam);
        events = reinterpret_cast<WindowEvents*>(create->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(events));
    }
    if (message == WM_CLOSE) {
        if (events) events->closing = true;
        return 0;
    }
    if (message == WM_DISPLAYCHANGE || message == WM_DPICHANGED) {
        if (events) events->displayChanged = true;
    }
    if ((message == WM_MOVE || message == WM_SIZE) && events && !events->initializingWindow) {
        events->movedOrResized = true;
    }
    return DefWindowProcW(window, message, wParam, lParam);
}

bool RegisterWindowClass(HINSTANCE* module, std::wstring* name, ATOM* atom, std::string* error) {
    static std::atomic<uint32_t> nextId{ 1 };
    HMODULE referencedModule = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
            reinterpret_cast<LPCWSTR>(reinterpret_cast<const void*>(&PresenterWindowProc)),
            &referencedModule)) {
        SetError(error, "Manual subpixel output module could not be retained for its native window.");
        return false;
    }
    const uint32_t id = nextId.fetch_add(1, std::memory_order_relaxed);
    *name = std::wstring(kWindowClassName) + L"_" + std::to_wstring(id);
    WNDCLASSEXW value{};
    value.cbSize = sizeof(value);
    value.lpfnWndProc = PresenterWindowProc;
    value.hInstance = referencedModule;
    value.lpszClassName = name->c_str();
    const ATOM registered = RegisterClassExW(&value);
    if (!registered) {
        FreeLibrary(referencedModule);
        SetError(error, "Manual subpixel output window class registration failed.");
        return false;
    }
    *module = referencedModule;
    *atom = registered;
    return true;
}

} // namespace

struct ManualSubpixelPresenter::State {
    ~State() {
        bool releaseModule = true;
        if (window && IsWindow(window)) {
            if (DestroyWindow(window)) window = nullptr;
            else releaseModule = false;
        }
        if (windowClassAtom && windowModule) {
            if (UnregisterClassW(windowClassName.c_str(), windowModule)) windowClassAtom = 0;
            else releaseModule = false;
        }
        if (windowModule && releaseModule) FreeLibrary(windowModule);
        windowModule = nullptr;
    }

    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> immediate;
    ComPtr<ID3D11DeviceContext> deferred;
    ComPtr<IDXGISwapChain1> swapChain;
    ComPtr<ID3D11RenderTargetView> backBufferRtv;
    ComPtr<ID3D11VertexShader> vertexShader;
    ComPtr<ID3D11PixelShader> pixelShader;
    ComPtr<ID3D11Buffer> constants;
    ComPtr<ID3D11Query> completionQuery;
    ComPtr<ID3D11Texture2D> retainedQuilt;
    ComPtr<ID3D11ShaderResourceView> retainedQuiltView;
#if defined(LKG_SUBPIXEL_PRESENTER_TESTS)
    ComPtr<ID3D11Texture2D> testTarget;
    ComPtr<ID3D11Texture2D> testReadback;
#endif
    HWND window = nullptr;
    HINSTANCE windowModule = nullptr;
    std::wstring windowClassName;
    ATOM windowClassAtom = 0;
    RECT bounds{};
    MONITORINFOEXW monitor{};
    WindowEvents events{};
    HMONITOR monitorHandle = nullptr;
    ComPtr<IDXGIOutput> monitorOutput;
    PresentConstants constantValues{};
    uint32_t width = 0;
    uint32_t height = 0;
    float displayAspect = 0.0f;
    bool testMode = false;
    bool pending = false;
    bool faulted = false;
};

ManualSubpixelPresenter::~ManualSubpixelPresenter() {
    // An unretired State is intentionally leaked. WorkerContext must retain
    // this object when Close returns false; this fallback prevents COM release
    // while the GPU may still reference submitted resources.
}

bool ManualSubpixelPresenter::Create(ID3D11Device* device,
                                     const LkgWorkerSubpixelCalibrationV1& calibration,
                                     const LkgWorkerSubpixelCellV1 cells[4],
                                     uint32_t mode,
                                     bool linearFlag,
                                     float displayAspect,
                                     std::string* error) {
    return CreateCore(device, calibration, cells, mode, linearFlag, displayAspect, false, error);
}

bool ManualSubpixelPresenter::CreateCore(ID3D11Device* device,
                                         const LkgWorkerSubpixelCalibrationV1& calibration,
                                         const LkgWorkerSubpixelCellV1 cells[4],
                                         uint32_t mode,
                                         bool linearFlag,
                                         float displayAspect,
                                         bool offscreenTest,
                                         std::string* error) {
    if (state_) {
        SetError(error, "Manual subpixel presenter is already initialized.");
        return false;
    }
    if (!device || !IsFinite(displayAspect) || displayAspect <= 0.0f ||
        !IsValidCalibration(calibration, cells, mode, error)) {
        if (!device) {
            SetError(error, "Manual subpixel output requires a D3D11 device.");
        } else if (!IsFinite(displayAspect) || displayAspect <= 0.0f) {
            SetError(error, "Manual subpixel output requires a valid SDK display aspect ratio.");
        }
        return false;
    }

    auto* state = new (std::nothrow) State();
    if (!state) {
        SetError(error, "Manual subpixel presenter allocation failed.");
        return false;
    }
    state->device = device;
    state->device->GetImmediateContext(&state->immediate);
    if (!state->immediate || FAILED(device->CreateDeferredContext(0, &state->deferred))) {
        delete state;
        SetError(error, "Manual subpixel D3D11 contexts could not be created.");
        return false;
    }

    const int64_t right64 = static_cast<int64_t>(calibration.windowX) + calibration.nativeWidth;
    const int64_t bottom64 = static_cast<int64_t>(calibration.windowY) + calibration.nativeHeight;
    if (right64 > std::numeric_limits<LONG>::max() || bottom64 > std::numeric_limits<LONG>::max()) {
        delete state;
        SetError(error, "Manual subpixel native display bounds overflowed.");
        return false;
    }
    const LONG right = static_cast<LONG>(right64);
    const LONG bottom = static_cast<LONG>(bottom64);
    state->bounds = RECT{ calibration.windowX, calibration.windowY, right, bottom };
    state->width = calibration.nativeWidth;
    state->height = calibration.nativeHeight;
    state->displayAspect = displayAspect;
    state->events = {};
    state->constantValues.calibration[0] = calibration.pitch * calibration.nativeWidth / calibration.dpi *
        std::cos(std::atan(1.0f / calibration.slope));
    const float direction = calibration.flipX == 1.0f ? -1.0f : 1.0f;
    state->constantValues.calibration[1] = direction * calibration.nativeHeight /
        (calibration.nativeWidth * calibration.slope);
    state->constantValues.calibration[2] = calibration.center;
    state->constantValues.calibration[3] = calibration.invertedViews != 0 ? 1.0f : 0.0f;
    state->constantValues.screen[0] = static_cast<float>(calibration.nativeWidth);
    state->constantValues.screen[1] = static_cast<float>(calibration.nativeHeight);
    state->constantValues.screen[2] = direction / (3.0f * calibration.nativeWidth);
    const bool automaticUsesCells = mode == kAutomaticMode && calibration.cellCount > 0;
    const bool usesCells = mode == kCalibratedCellsMode || automaticUsesCells;
    state->constantValues.screen[3] = !usesCells && calibration.flipX == 1.0f ? 1.0f : 0.0f;
    state->constantValues.outputOptions[0] = static_cast<float>(calibration.cellPatternMode);
    state->constantValues.outputOptions[1] = usesCells ? 1.0f : 0.0f;
    state->constantValues.outputOptions[2] = linearFlag ? 1.0f : 0.0f;
    state->constantValues.channelOrder[0] = mode == kAutomaticMode && !automaticUsesCells
        ? static_cast<float>(calibration.redIndex) : 0.0f;
    state->constantValues.channelOrder[1] = 1.0f;
    state->constantValues.channelOrder[2] = mode == kAutomaticMode && !automaticUsesCells
        ? static_cast<float>(calibration.blueIndex) : 2.0f;
    if (usesCells) {
        for (int32_t index = 0; index < calibration.cellCount; ++index) {
            const float invWidth = 1.0f / calibration.nativeWidth;
            const float invHeight = 1.0f / calibration.nativeHeight;
            const LkgWorkerSubpixelCellV1& cell = cells[index];
            float* offsets = state->constantValues.cellOffsets[index * 3];
            offsets[0] = cell.redX * invWidth;
            offsets[1] = cell.redY * invHeight;
            offsets = state->constantValues.cellOffsets[index * 3 + 1];
            offsets[0] = cell.greenX * invWidth;
            offsets[1] = cell.greenY * invHeight;
            offsets = state->constantValues.cellOffsets[index * 3 + 2];
            offsets[0] = cell.blueX * invWidth;
            offsets[1] = cell.blueY * invHeight;
        }
    }
    if (!IsFinite(state->constantValues.calibration[0]) ||
        !IsFinite(state->constantValues.calibration[1]) ||
        !IsFinite(state->constantValues.screen[2])) {
        delete state;
        SetError(error, "Manual subpixel calibration produced non-finite shader parameters.");
        return false;
    }

    if (!offscreenTest) {
        ThreadDpiContext dpiContext;
        if (!dpiContext.IsValid()) {
            delete state;
            SetError(error, "Manual subpixel output could not enable thread-local per-monitor DPI awareness.");
            return false;
        }
        MonitorSearch monitorSearch{};
        monitorSearch.expected = state->bounds;
        EnumDisplayMonitors(nullptr, nullptr, FindExactMonitor, reinterpret_cast<LPARAM>(&monitorSearch));
        if (!monitorSearch.found || !IsUnrotatedDisplay(monitorSearch.info, error)) {
            delete state;
            if (!monitorSearch.found) {
                SetError(error, "Manual subpixel native bounds do not match a connected monitor.");
            }
            return false;
        }
        state->monitor = monitorSearch.info;
        if (!RegisterWindowClass(&state->windowModule, &state->windowClassName,
                                 &state->windowClassAtom, error)) {
            delete state;
            return false;
        }
    }

    if (FAILED(device->CreateVertexShader(g_manual_subpixel_presenter_vertex_shader,
                                         sizeof(g_manual_subpixel_presenter_vertex_shader),
                                         nullptr, &state->vertexShader)) ||
        FAILED(device->CreatePixelShader(g_manual_subpixel_presenter_pixel_shader,
                                         sizeof(g_manual_subpixel_presenter_pixel_shader),
                                         nullptr, &state->pixelShader))) {
        delete state;
        SetError(error, "Manual subpixel D3D11 shaders could not be created.");
        return false;
    }

    D3D11_BUFFER_DESC constantDescription{};
    constantDescription.ByteWidth = sizeof(PresentConstants);
    constantDescription.Usage = D3D11_USAGE_DEFAULT;
    constantDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SUBRESOURCE_DATA constantData{};
    constantData.pSysMem = &state->constantValues;
    if (FAILED(device->CreateBuffer(&constantDescription, &constantData, &state->constants))) {
        delete state;
        SetError(error, "Manual subpixel constant buffer could not be created.");
        return false;
    }
    D3D11_QUERY_DESC queryDescription{};
    queryDescription.Query = D3D11_QUERY_EVENT;
    if (FAILED(device->CreateQuery(&queryDescription, &state->completionQuery))) {
        delete state;
        SetError(error, "Manual subpixel GPU completion query could not be created.");
        return false;
    }

    ComPtr<IDXGIDevice> dxgiDevice;
    ComPtr<IDXGIAdapter> adapter;
    ComPtr<IDXGIFactory2> factory;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgiDevice))) ||
        FAILED(dxgiDevice->GetAdapter(&adapter)) ||
        FAILED(adapter->GetParent(IID_PPV_ARGS(&factory)))) {
        delete state;
        SetError(error, "Manual subpixel DXGI adapter could not be resolved.");
        return false;
    }
    bool outputFound = offscreenTest;
    for (UINT outputIndex = 0; !offscreenTest; ++outputIndex) {
        ComPtr<IDXGIOutput> output;
        const HRESULT enumResult = adapter->EnumOutputs(outputIndex, &output);
        if (enumResult == DXGI_ERROR_NOT_FOUND) {
            break;
        }
        if (FAILED(enumResult)) {
            break;
        }
        DXGI_OUTPUT_DESC outputDescription{};
        if (SUCCEEDED(output->GetDesc(&outputDescription)) &&
            outputDescription.DesktopCoordinates.left == state->bounds.left &&
            outputDescription.DesktopCoordinates.top == state->bounds.top &&
            outputDescription.DesktopCoordinates.right == state->bounds.right &&
            outputDescription.DesktopCoordinates.bottom == state->bounds.bottom) {
            if (!IsValidSdrOutput(output.Get(), error)) {
                delete state;
                return false;
            }
            state->monitorOutput = output;
            outputFound = true;
            break;
        }
    }
    if (!outputFound) {
        delete state;
        SetError(error, "The D3D11 device does not own the calibrated native display output.");
        return false;
    }

    if (offscreenTest) {
#if defined(LKG_SUBPIXEL_PRESENTER_TESTS)
        D3D11_TEXTURE2D_DESC targetDescription{};
        targetDescription.Width = calibration.nativeWidth;
        targetDescription.Height = calibration.nativeHeight;
        targetDescription.MipLevels = 1;
        targetDescription.ArraySize = 1;
        targetDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        targetDescription.SampleDesc.Count = 1;
        targetDescription.Usage = D3D11_USAGE_DEFAULT;
        targetDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
        if (FAILED(device->CreateTexture2D(&targetDescription, nullptr, &state->testTarget))) {
            delete state;
            SetError(error, "Manual subpixel offscreen test target could not be created.");
            return false;
        }
        targetDescription.Usage = D3D11_USAGE_STAGING;
        targetDescription.BindFlags = 0;
        targetDescription.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(device->CreateTexture2D(&targetDescription, nullptr, &state->testReadback))) {
            delete state;
            SetError(error, "Manual subpixel offscreen readback texture could not be created.");
            return false;
        }
        state->testMode = true;
#else
        delete state;
        SetError(error, "Manual subpixel offscreen test seam is unavailable in this build.");
        return false;
#endif
    } else {
        ThreadDpiContext dpiContext;
        if (!dpiContext.IsValid()) {
            delete state;
            SetError(error, "Manual subpixel output could not preserve thread-local per-monitor DPI awareness.");
            return false;
        }
        state->monitorHandle = MonitorFromRect(&state->bounds, MONITOR_DEFAULTTONULL);
        if (!state->monitorHandle) {
            delete state;
            SetError(error, "Manual subpixel target monitor disappeared during window creation.");
            return false;
        }
        state->events.initializingWindow = true;
        state->window = CreateWindowExW(WS_EX_TOPMOST | WS_EX_NOACTIVATE, state->windowClassName.c_str(),
            L"ViewWeave Manual Subpixel Output", WS_POPUP, state->bounds.left, state->bounds.top,
            calibration.nativeWidth, calibration.nativeHeight, nullptr, nullptr, state->windowModule, &state->events);
        if (!state->window) {
            delete state;
            SetError(error, "Manual subpixel native output window could not be created.");
            return false;
        }
        SetWindowPos(state->window, HWND_TOPMOST, state->bounds.left, state->bounds.top,
            calibration.nativeWidth, calibration.nativeHeight, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        state->events.initializingWindow = false;

        DXGI_SWAP_CHAIN_DESC1 swapDescription{};
        swapDescription.Width = calibration.nativeWidth;
        swapDescription.Height = calibration.nativeHeight;
        swapDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swapDescription.Stereo = FALSE;
        swapDescription.SampleDesc.Count = 1;
        swapDescription.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapDescription.BufferCount = 2;
        swapDescription.Scaling = DXGI_SCALING_STRETCH;
        swapDescription.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swapDescription.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        if (FAILED(factory->CreateSwapChainForHwnd(device, state->window, &swapDescription, nullptr, nullptr,
                                                   &state->swapChain))) {
            DestroyWindow(state->window);
            state->window = nullptr;
            delete state;
            SetError(error, "Manual subpixel native SDR swap chain could not be created.");
            return false;
        }
        ComPtr<ID3D11Texture2D> backBuffer;
        if (FAILED(state->swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer))) ||
            FAILED(device->CreateRenderTargetView(backBuffer.Get(), nullptr, &state->backBufferRtv))) {
            DestroyWindow(state->window);
            state->window = nullptr;
            delete state;
            SetError(error, "Manual subpixel native back buffer could not be created.");
            return false;
        }
    }
    state_ = state;
    if (!state_->testMode && !SetVisible(true, error)) {
        Close(nullptr);
        return false;
    }
    return true;
}

ManualPresentResult ManualSubpixelPresenter::Present(ID3D11Texture2D* quilt,
                                                      uint32_t columns,
                                                      uint32_t rows,
                                                      bool sourceIsSrgb,
                                                      float aspect,
                                                      float zoom,
                                                      std::string* error) {
    if (!state_ || state_->faulted || state_->pending || !quilt || columns == 0 || rows == 0 ||
        !IsFinite(aspect) || aspect <= 0.0f || !IsFinite(zoom) || zoom <= 0.0f) {
        SetError(error, "Manual subpixel frame request is invalid or the previous GPU frame is still pending.");
        return ManualPresentResult::Failed;
    }
    State& state = *state_;
    if (!state.testMode && !PollWindowState(error)) {
        return ManualPresentResult::Failed;
    }
    if (!state.testMode && (IsIconic(state.window) || !IsWindowVisible(state.window))) {
        return ManualPresentResult::Occluded;
    }
    D3D11_TEXTURE2D_DESC textureDescription{};
    quilt->GetDesc(&textureDescription);
    if (rows > std::numeric_limits<uint32_t>::max() / columns ||
        textureDescription.Format != DXGI_FORMAT_R8G8B8A8_UNORM ||
        textureDescription.MipLevels != 1 || textureDescription.ArraySize != 1 ||
        textureDescription.SampleDesc.Count != 1 || textureDescription.Width % columns != 0 ||
        textureDescription.Height % rows != 0) {
        SetError(error, "Manual subpixel output requires a canonical single-sample RGBA8 UNORM Quilt.");
        return ManualPresentResult::Failed;
    }
    ID3D11Device* textureDevice = nullptr;
    quilt->GetDevice(&textureDevice);
    const bool sameDevice = textureDevice == state.device.Get();
    if (textureDevice) {
        textureDevice->Release();
    }
    if (!sameDevice) {
        SetError(error, "Manual subpixel Quilt belongs to a different D3D11 device.");
        return ManualPresentResult::Failed;
    }

    ComPtr<ID3D11ShaderResourceView> quiltView;
    D3D11_SHADER_RESOURCE_VIEW_DESC viewDescription{};
    viewDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    viewDescription.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    viewDescription.Texture2D.MostDetailedMip = 0;
    viewDescription.Texture2D.MipLevels = 1;
    HRESULT result = state.device->CreateShaderResourceView(quilt, &viewDescription, &quiltView);
    if (FAILED(result)) {
        SetError(error, "Manual subpixel Quilt shader view could not be created for its declared transfer encoding.");
        return ManualPresentResult::Failed;
    }

    PresentConstants constants = state.constantValues;
    constants.grid[0] = static_cast<float>(columns);
    constants.grid[1] = static_cast<float>(rows);
    constants.grid[2] = static_cast<float>(textureDescription.Width / columns);
    constants.grid[3] = static_cast<float>(textureDescription.Height / rows);
    constants.quilt[0] = static_cast<float>(textureDescription.Width);
    constants.quilt[1] = static_cast<float>(textureDescription.Height);
    constants.quilt[2] = aspect / state.displayAspect;
    constants.quilt[3] = zoom;
    constants.outputOptions[3] = sourceIsSrgb ? 1.0f : 0.0f;
    state.deferred->UpdateSubresource(state.constants.Get(), 0, nullptr, &constants, 0, 0);

    ComPtr<ID3D11RenderTargetView> target = state.backBufferRtv;
#if defined(LKG_SUBPIXEL_PRESENTER_TESTS)
    if (state.testMode) {
        result = state.device->CreateRenderTargetView(state.testTarget.Get(), nullptr, &target);
        if (FAILED(result)) {
            SetError(error, "Manual subpixel test target view could not be created.");
            return ManualPresentResult::Failed;
        }
    }
#endif
    D3D11_VIEWPORT viewport{};
    viewport.Width = static_cast<float>(state.width);
    viewport.Height = static_cast<float>(state.height);
    viewport.MinDepth = 0.0f;
    viewport.MaxDepth = 1.0f;
    state.deferred->RSSetViewports(1, &viewport);
    state.deferred->OMSetRenderTargets(1, target.GetAddressOf(), nullptr);
    state.deferred->IASetInputLayout(nullptr);
    state.deferred->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    state.deferred->VSSetShader(state.vertexShader.Get(), nullptr, 0);
    state.deferred->PSSetShader(state.pixelShader.Get(), nullptr, 0);
    ID3D11Buffer* constantBuffer = state.constants.Get();
    state.deferred->PSSetConstantBuffers(0, 1, &constantBuffer);
    ID3D11ShaderResourceView* shaderView = quiltView.Get();
    state.deferred->PSSetShaderResources(0, 1, &shaderView);
    state.deferred->Draw(3, 0);
    ID3D11ShaderResourceView* nullView = nullptr;
    state.deferred->PSSetShaderResources(0, 1, &nullView);
    ComPtr<ID3D11CommandList> commandList;
    result = state.deferred->FinishCommandList(FALSE, &commandList);
    if (FAILED(result)) {
        state.faulted = true;
        SetError(error, "Manual subpixel D3D11 render command could not be recorded.");
        return ManualPresentResult::Failed;
    }
    state.immediate->ExecuteCommandList(commandList.Get(), TRUE);
#if defined(LKG_SUBPIXEL_PRESENTER_TESTS)
    if (state.testMode) {
        state.immediate->CopyResource(state.testReadback.Get(), state.testTarget.Get());
    }
#endif
    state.immediate->End(state.completionQuery.Get());
    state.immediate->Flush();
    state.retainedQuilt = quilt;
    state.retainedQuiltView = quiltView;
    state.pending = true;

    if (!PollGpuCompletion(error)) {
        state.faulted = true;
        return ManualPresentResult::Failed;
    }
    for (UINT attempt = 0; state.pending && attempt < kCompletionPollCount; ++attempt) {
        if (!PollGpuCompletion(error)) {
            state.faulted = true;
            return ManualPresentResult::Failed;
        }
        if (state.pending) {
            Sleep(kCompletionPollDelayMs);
        }
    }
    if (state.pending) {
        SetError(error, "Manual subpixel GPU completion did not retire within the bounded wait.");
        return ManualPresentResult::Failed;
    }

#if !defined(LKG_SUBPIXEL_PRESENTER_TESTS)
    HRESULT presentResult = state.swapChain->Present(1, 0);
#else
    HRESULT presentResult = state.testMode ? S_OK : state.swapChain->Present(1, 0);
#endif
    if (presentResult == DXGI_STATUS_OCCLUDED) {
        return ManualPresentResult::Occluded;
    }
    if (FAILED(presentResult)) {
        state.faulted = true;
        SetError(error, "Manual subpixel DXGI Present failed.");
        return ManualPresentResult::Failed;
    }
    ++presentAccepted_;
    return ManualPresentResult::Accepted;
}

bool ManualSubpixelPresenter::PollGpuCompletion(std::string* error) {
    if (!state_) {
        SetError(error, "Manual subpixel presenter is not initialized.");
        return false;
    }
    if (!state_->pending) {
        return true;
    }
    const HRESULT result = state_->immediate->GetData(state_->completionQuery.Get(), nullptr, 0, 0);
    if (result == S_FALSE) {
        return true;
    }
    if (FAILED(result)) {
        state_->faulted = true;
        SetError(error, "Manual subpixel GPU completion query failed or the D3D11 device was removed.");
        return false;
    }
    state_->pending = false;
    state_->retainedQuiltView.Reset();
    state_->retainedQuilt.Reset();
    ++renderCompleted_;
    return true;
}

bool ManualSubpixelPresenter::PollWindowState(std::string* error) {
    if (!state_ || state_->testMode || state_->faulted) {
        if (state_ && state_->testMode) return true;
        SetError(error, "Manual subpixel native output window is unavailable or faulted.");
        return false;
    }
    State& state = *state_;
    ThreadDpiContext dpiContext;
    if (!dpiContext.IsValid()) {
        state.faulted = true;
        SetError(error, "Manual subpixel output could not preserve per-monitor DPI awareness during geometry validation.");
        return false;
    }
    if (!state.window || !IsWindow(state.window)) {
        state.faulted = true;
        SetError(error, "Manual subpixel output window is unavailable.");
        return false;
    }
    MSG message{};
    while (PeekMessageW(&message, state.window, 0, 0, PM_REMOVE)) {
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    if (state.events.closing || state.events.displayChanged || state.events.movedOrResized) {
        state.faulted = true;
        SetError(error, "Manual subpixel output display geometry changed; restart output after reconnecting the display.");
        return false;
    }
    RECT windowBounds{};
    if (!GetWindowRect(state.window, &windowBounds) || windowBounds.left != state.bounds.left ||
        windowBounds.top != state.bounds.top || windowBounds.right != state.bounds.right ||
        windowBounds.bottom != state.bounds.bottom) {
        state.faulted = true;
        SetError(error, "Manual subpixel output window moved or resized away from native pixel geometry.");
        return false;
    }
    RECT clientBounds{};
    if (!GetClientRect(state.window, &clientBounds) || clientBounds.left != 0 || clientBounds.top != 0 ||
        clientBounds.right != static_cast<LONG>(state.width) || clientBounds.bottom != static_cast<LONG>(state.height)) {
        state.faulted = true;
        SetError(error, "Manual subpixel output client area no longer maps one-to-one to native display pixels.");
        return false;
    }
    if (MonitorFromWindow(state.window, MONITOR_DEFAULTTONULL) != state.monitorHandle) {
        state.faulted = true;
        SetError(error, "Manual subpixel target display was removed or changed.");
        return false;
    }
    if (!IsValidSdrOutput(state.monitorOutput.Get(), error)) {
        state.faulted = true;
        return false;
    }
    return true;
}

bool ManualSubpixelPresenter::HasPendingGpuWork() const noexcept {
    return state_ && state_->pending;
}

uint32_t ManualSubpixelPresenter::RenderCompleted() const noexcept {
    return renderCompleted_;
}

uint32_t ManualSubpixelPresenter::PresentAccepted() const noexcept {
    return presentAccepted_;
}

bool ManualSubpixelPresenter::SetVisible(bool visible, std::string* error) {
    if (!state_) {
        return !visible;
    }
    if (state_->testMode) {
        return true;
    }
    if (!state_->window || !IsWindow(state_->window)) {
        SetError(error, "Manual subpixel output window is unavailable.");
        return false;
    }
    ThreadDpiContext dpiContext;
    if (!dpiContext.IsValid()) {
        SetError(error, "Manual subpixel visibility could not preserve thread DPI context.");
        return false;
    }
    ShowWindow(state_->window, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    return true;
}

bool ManualSubpixelPresenter::Close(std::string* error) {
    if (!state_) {
        return true;
    }
    State& state = *state_;
    for (UINT attempt = 0; state.pending && attempt < kCompletionPollCount; ++attempt) {
        if (!PollGpuCompletion(error)) {
            return false;
        }
        if (state.pending) {
            Sleep(kCompletionPollDelayMs);
        }
    }
    if (state.pending) {
        SetError(error, "Manual subpixel resources remain retained because GPU completion is unproven.");
        return false;
    }
    if (state.window && IsWindow(state.window)) {
        if (!DestroyWindow(state.window)) {
            SetError(error, "Manual subpixel output window could not be destroyed on its owner thread.");
            return false;
        }
        state.window = nullptr;
    }
    if (state.windowClassAtom && state.windowModule) {
        if (!UnregisterClassW(state.windowClassName.c_str(), state.windowModule)) {
            SetError(error, "Manual subpixel output window class could not be unregistered on its owner thread.");
            return false;
        }
        state.windowClassAtom = 0;
    }
    delete state_;
    state_ = nullptr;
    return true;
}

#if defined(LKG_SUBPIXEL_PRESENTER_TESTS)
bool ManualSubpixelPresenter::CreateOffscreenForTest(ID3D11Device* device,
                                                      const LkgWorkerSubpixelCalibrationV1& calibration,
                                                      const LkgWorkerSubpixelCellV1 cells[4],
                                                      uint32_t mode,
                                                      bool linearFlag,
                                                      float displayAspect,
                                                      std::string* error) {
    return CreateCore(device, calibration, cells, mode, linearFlag, displayAspect, true, error);
}

bool ManualSubpixelPresenter::ReadbackLastPresentedRGBA8(std::vector<uint8_t>& pixels,
                                                          uint32_t* width,
                                                          uint32_t* height,
                                                          std::string* error) {
    if (!state_ || !state_->testMode || state_->pending || !width || !height) {
        SetError(error, "Manual subpixel offscreen readback requires a completed test frame.");
        return false;
    }
    D3D11_MAPPED_SUBRESOURCE mapped{};
    const HRESULT result = state_->immediate->Map(state_->testReadback.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(result)) {
        SetError(error, "Manual subpixel offscreen output could not be mapped for readback.");
        return false;
    }
    const size_t rowBytes = static_cast<size_t>(state_->width) * 4;
    pixels.resize(rowBytes * state_->height);
    const auto* source = static_cast<const uint8_t*>(mapped.pData);
    for (uint32_t y = 0; y < state_->height; ++y) {
        std::copy_n(source + static_cast<size_t>(y) * mapped.RowPitch, rowBytes,
                    pixels.data() + static_cast<size_t>(y) * rowBytes);
    }
    state_->immediate->Unmap(state_->testReadback.Get(), 0);
    *width = state_->width;
    *height = state_->height;
    return true;
}
#endif

} // namespace lkg::subpixel
