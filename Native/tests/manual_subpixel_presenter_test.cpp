#include "../platform/windows/manual_subpixel_presenter.h"

#include <d3d11.h>
#include <dxgi1_2.h>
#include <windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using lkg::subpixel::ManualPresentResult;
using lkg::subpixel::ManualSubpixelPresenter;

namespace {

constexpr uint32_t kClassicRgb = 1;
constexpr uint32_t kCalibratedCells = 2;
constexpr uint32_t kAutomatic = 0;
constexpr double kMaximumChannelError = 0.006;
constexpr float kSdkDisplayAspect = 1.17f;

struct TestCase {
    const char* name;
    uint32_t mode;
    int32_t patternMode;
    int32_t cellCount;
    float flipX;
    int32_t invertedViews;
    bool linearFlag;
    bool sourceIsSrgb;
    float center;
    float pitch;
    float slope;
    float aspect;
    float zoom;
};

struct Rgba8 {
    uint8_t r;
    uint8_t g;
    uint8_t b;
    uint8_t a;
};

struct CpuConstants {
    double pitch;
    double verticalTilt;
    double center;
    bool inverted;
    double direction;
    double displayAspect;
};

bool Require(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
    }
    return condition;
}

double SrgbToLinear(double value) {
    return value <= 0.04045 ? value / 12.92 : std::pow((value + 0.055) / 1.055, 2.4);
}

double LinearToSrgb(double value) {
    value = std::clamp(value, 0.0, 1.0);
    return value <= 0.0031308 ? value * 12.92 : 1.055 * std::pow(value, 1.0 / 2.4) - 0.055;
}

uint8_t ToUnorm8(double value) {
    const double clamped = std::clamp(value, 0.0, 1.0);
    return static_cast<uint8_t>(std::floor(clamped * 255.0 + 0.5));
}

double Lerp(double first, double second, double fraction) {
    return first + (second - first) * fraction;
}

int CellForPixel(int x, int y, int mode) {
    switch (mode) {
    case 1: return (x + y) % 2;
    case 2: return x % 2;
    case 3: return (y + (x % 2) * 2) % 4;
    case 4: return y % 2;
    default: return 0;
    }
}

LkgWorkerSubpixelCalibrationV1 MakeCalibration(const TestCase& test) {
    LkgWorkerSubpixelCalibrationV1 value{};
    value.structSize = sizeof(value);
    value.version = 1;
    value.displayIndex = 0;
    value.nativeWidth = 9;
    value.nativeHeight = 7;
    value.center = test.center;
    value.pitch = test.pitch;
    value.slope = test.slope;
    value.dpi = 40.0f;
    value.flipX = test.flipX;
    value.viewCone = 40.0f;
    value.fringe = 0.0f;
    value.invertedViews = test.invertedViews;
    value.cellPatternMode = test.patternMode;
    value.cellCount = test.cellCount;
    value.windowX = 0;
    value.windowY = 0;
    value.redIndex = std::strcmp(test.name, "automatic-scalar-bgr") == 0 ? 2 : 0;
    value.blueIndex = std::strcmp(test.name, "automatic-scalar-bgr") == 0 ? 0 : 2;
    return value;
}

std::array<LkgWorkerSubpixelCellV1, 4> MakeCells() {
    return {{
        {-1.25f, 0.5f, 0.0f, -0.375f, 1.4f, 0.75f},
        {0.65f, -1.1f, -0.8f, 0.3f, 0.35f, -0.6f},
        {-0.45f, 0.2f, 0.9f, -0.7f, -1.3f, 0.4f},
        {1.1f, -0.5f, -1.0f, 1.25f, 0.55f, -0.9f}
    }};
}

std::vector<Rgba8> MakeQuiltBytes() {
    constexpr uint32_t width = 15;
    constexpr uint32_t height = 10;
    std::vector<Rgba8> result(width * height);
    for (uint32_t y = 0; y < height; ++y) {
        for (uint32_t x = 0; x < width; ++x) {
            const uint32_t tile = (y / 5) * 3 + x / 5;
            const uint32_t localX = x % 5;
            const uint32_t localY = y % 5;
            result[y * width + x] = Rgba8{
                static_cast<uint8_t>(25 + tile * 21 + localX * 3 + localY * 11),
                static_cast<uint8_t>(30 + tile * 17 + localY * 19 + localX * 2),
                static_cast<uint8_t>(45 + tile * 15 + localX * 2 + localY * 13),
                255
            };
        }
    }
    return result;
}

CpuConstants MakeCpuConstants(const LkgWorkerSubpixelCalibrationV1& calibration) {
    const double direction = calibration.flipX == 1.0f ? -1.0 : 1.0;
    const double pitch = calibration.pitch * calibration.nativeWidth / calibration.dpi *
        std::cos(std::atan(1.0 / calibration.slope));
    const double tilt = direction * calibration.nativeHeight /
        (calibration.nativeWidth * calibration.slope);
    return CpuConstants{ pitch, tilt, calibration.center, calibration.invertedViews != 0,
        direction, static_cast<double>(calibration.nativeWidth) / calibration.nativeHeight };
}

double ReadTextureChannel(const std::vector<Rgba8>& source,
                          int x,
                          int y,
                          int channel,
                          bool sourceIsSrgb) {
    constexpr int width = 15;
    constexpr int height = 10;
    x = std::clamp(x, 0, width - 1);
    y = std::clamp(y, 0, height - 1);
    const Rgba8& pixel = source[static_cast<size_t>(y) * width + x];
    const uint8_t value = channel == 0 ? pixel.r : channel == 1 ? pixel.g : pixel.b;
    const double normalized = value / 255.0;
    return sourceIsSrgb ? SrgbToLinear(normalized) : normalized;
}

double SampleTileChannel(const std::vector<Rgba8>& source,
                         int tileX,
                         int tileY,
                         double withinX,
                         double withinY,
                         int channel,
                         bool sourceIsSrgb) {
    constexpr int tileWidth = 5;
    constexpr int tileHeight = 5;
    const double sourcePixelX = std::clamp(tileX * tileWidth + withinX * tileWidth,
        tileX * tileWidth + 0.5, (tileX + 1) * tileWidth - 0.5);
    const double sourcePixelY = std::clamp(tileY * tileHeight + withinY * tileHeight,
        tileY * tileHeight + 0.5, (tileY + 1) * tileHeight - 0.5);
    const double sampleX = sourcePixelX - 0.5;
    const double sampleY = sourcePixelY - 0.5;
    const int xFloor = static_cast<int>(std::floor(sampleX));
    const int yFloor = static_cast<int>(std::floor(sampleY));
    const int x0 = std::clamp(xFloor, tileX * tileWidth, (tileX + 1) * tileWidth - 1);
    const int y0 = std::clamp(yFloor, tileY * tileHeight, (tileY + 1) * tileHeight - 1);
    const int x1 = std::clamp(xFloor + 1, tileX * tileWidth, (tileX + 1) * tileWidth - 1);
    const int y1 = std::clamp(yFloor + 1, tileY * tileHeight, (tileY + 1) * tileHeight - 1);
    const double fractionX = sampleX - xFloor;
    const double fractionY = sampleY - yFloor;
    const double upper = Lerp(ReadTextureChannel(source, x0, y0, channel, sourceIsSrgb),
                              ReadTextureChannel(source, x1, y0, channel, sourceIsSrgb), fractionX);
    const double lower = Lerp(ReadTextureChannel(source, x0, y1, channel, sourceIsSrgb),
                              ReadTextureChannel(source, x1, y1, channel, sourceIsSrgb), fractionX);
    return Lerp(upper, lower, fractionY);
}

std::vector<Rgba8> CpuReference(const TestCase& test,
                                const LkgWorkerSubpixelCalibrationV1& calibration,
                                const std::array<LkgWorkerSubpixelCellV1, 4>& cells,
                                const std::vector<Rgba8>& source) {
    constexpr int outputWidth = 9;
    constexpr int outputHeight = 7;
    constexpr int quiltColumns = 3;
    constexpr int quiltRows = 2;
    const CpuConstants optics = MakeCpuConstants(calibration);
    const double quiltAspect = 1.0;
    const double aspectCorrection = static_cast<double>(test.aspect) / kSdkDisplayAspect;
    std::vector<Rgba8> result(outputWidth * outputHeight);

    for (int y = 0; y < outputHeight; ++y) {
        for (int x = 0; x < outputWidth; ++x) {
            double u = (x + 0.5) / outputWidth;
            const double v = 1.0 - (y + 0.5) / outputHeight;
            const bool useCells = test.mode == kCalibratedCells ||
                (test.mode == kAutomatic && calibration.cellCount > 0);
            if (!useCells && calibration.flipX == 1.0f) {
                u = 1.0 - u;
            }
            const int opticalPixelY = std::min(static_cast<int>(v * outputHeight), outputHeight - 1);
            const int cell = useCells ? CellForPixel(x, opticalPixelY, calibration.cellPatternMode) : 0;
            double withinX = (u - 0.5) * (quiltAspect * aspectCorrection) + 0.5;
            double withinY = v;
            withinX = (withinX - 0.5) / test.zoom + 0.5;
            withinY = (withinY - 0.5) / test.zoom + 0.5;

            Rgba8 output{0, 0, 0, 255};
            if (withinX >= 0.0 && withinX <= 1.0 && withinY >= 0.0 && withinY <= 1.0) {
                uint8_t* channels[] = { &output.r, &output.g, &output.b };
                for (int component = 0; component < 3; ++component) {
                    double subpixelU = u;
                    double subpixelV = v;
                    if (useCells) {
                        const LkgWorkerSubpixelCellV1& offset = cells[cell];
                        if (component == 0) {
                            subpixelU += offset.redX / calibration.nativeWidth;
                            subpixelV += offset.redY / calibration.nativeHeight;
                        } else if (component == 1) {
                            subpixelU += offset.greenX / calibration.nativeWidth;
                            subpixelV += offset.greenY / calibration.nativeHeight;
                        } else {
                            subpixelU += offset.blueX / calibration.nativeWidth;
                            subpixelV += offset.blueY / calibration.nativeHeight;
                        }
                    } else {
                        const int channelIndex = test.mode == kAutomatic
                            ? component == 0 ? calibration.redIndex : component == 1 ? 1 : calibration.blueIndex
                            : component;
                        subpixelU += (channelIndex * optics.direction) / (3.0 * calibration.nativeWidth);
                    }
                    const double phaseInput = (subpixelU + subpixelV * optics.verticalTilt) * optics.pitch - optics.center;
                    const double phase = phaseInput - std::floor(phaseInput);
                    const bool invert = useCells || optics.inverted;
                    const double fraction = invert ? 1.0 - phase : phase;
                    int view = static_cast<int>(std::floor(fraction * quiltColumns * quiltRows));
                    view = std::min(view, quiltColumns * quiltRows - 1);
                    const int tileX = view % quiltColumns;
                    const int tileY = view / quiltColumns;
                    const double sampled = SampleTileChannel(source, tileX, tileY, withinX, withinY,
                        component, test.linearFlag && test.sourceIsSrgb);
                    const double encoded = test.linearFlag ? LinearToSrgb(sampled) : sampled;
                    *channels[component] = ToUnorm8(encoded);
                }
            }
            result[y * outputWidth + x] = output;
        }
    }
    return result;
}

bool MakeCases(std::vector<TestCase>& cases) {
    for (int pattern = 0; pattern <= 4; ++pattern) {
        for (int flip = 0; flip <= 1; ++flip) {
            cases.push_back(TestCase{"calibrated-pattern", kCalibratedCells, pattern, 4,
                static_cast<float>(flip), 1, false, false, 0.375f, 37.0f, 1.3f, 1.0f, 1.0f});
        }
    }
    cases.push_back(TestCase{"classic-no-cells", kClassicRgb, 3, 0, 0.0f, 0,
        false, false, 0.19f, 41.0f, 1.1f, 1.0f, 1.0f});
    cases.push_back(TestCase{"classic-flip-inverted", kClassicRgb, 4, 0, 1.0f, 1,
        false, false, 0.61f, 33.0f, 1.4f, 1.0f, 1.0f});
    cases.push_back(TestCase{"automatic-calibrated-table", kAutomatic, 2, 2, 0.0f, 1,
        false, false, 0.41f, 35.0f, 1.15f, 1.0f, 1.0f});
    cases.push_back(TestCase{"automatic-scalar-bgr", kAutomatic, 0, 0, 1.0f, 1,
        false, false, 0.34f, 39.0f, 1.25f, 1.0f, 1.0f});
    cases.push_back(TestCase{"negative-cell-offsets", kCalibratedCells, 3, 4, 1.0f, 1,
        false, false, 0.5f, 29.0f, 0.9f, 1.0f, 1.0f});
    for (bool linear : {false, true}) {
        for (bool srgb : {false, true}) {
            cases.push_back(TestCase{"color-transfer", kCalibratedCells, 1, 4, 0.0f, 1,
                linear, srgb, 0.4999f, 38.0f, 1.2f, 1.0f, 1.0f});
            cases.push_back(TestCase{"classic-color-transfer", kClassicRgb, 0, 0, 0.0f, 1,
                linear, srgb, 0.5001f, 38.0f, 1.2f, 1.0f, 1.0f});
        }
    }
    cases.push_back(TestCase{"aspect-zoom", kCalibratedCells, 0, 4, 0.0f, 1,
        true, true, 0.25f, 45.0f, 1.05f, 0.72f, 1.2f});
    return !cases.empty();
}

bool RunCase(ID3D11Device* producerDevice, ID3D11Device* consumerDevice, const TestCase& test,
             const std::array<LkgWorkerSubpixelCellV1, 4>& cells,
             const std::vector<Rgba8>& source) {
    LkgWorkerSubpixelCalibrationV1 calibration = MakeCalibration(test);
    D3D11_TEXTURE2D_DESC sourceDescription{};
    sourceDescription.Width = 15;
    sourceDescription.Height = 10;
    sourceDescription.MipLevels = 1;
    sourceDescription.ArraySize = 1;
    sourceDescription.Format = DXGI_FORMAT_R8G8B8A8_TYPELESS;
    sourceDescription.SampleDesc.Count = 1;
    sourceDescription.Usage = D3D11_USAGE_DEFAULT;
    sourceDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    sourceDescription.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
    D3D11_SUBRESOURCE_DATA sourceData{};
    sourceData.pSysMem = source.data();
    sourceData.SysMemPitch = 15 * sizeof(Rgba8);
    ComPtr<ID3D11Texture2D> producerTexture;
    if (!Require(SUCCEEDED(producerDevice->CreateTexture2D(&sourceDescription, &sourceData, &producerTexture)),
                 "failed to create shared RGBA8 TYPELESS producer texture")) {
        return false;
    }
    ComPtr<IDXGIResource> sharedResource;
    HANDLE sharedHandle = nullptr;
    if (!Require(SUCCEEDED(producerTexture.As(&sharedResource)) &&
                 SUCCEEDED(sharedResource->GetSharedHandle(&sharedHandle)) && sharedHandle != nullptr,
                 "failed to export shared producer texture handle")) {
        return false;
    }
    ComPtr<ID3D11Texture2D> consumerTexture;
    if (!Require(SUCCEEDED(consumerDevice->OpenSharedResource(sharedHandle, IID_PPV_ARGS(&consumerTexture))),
                 "failed to open shared producer texture as its consumer")) {
        return false;
    }
    D3D11_SHADER_RESOURCE_VIEW_DESC sourceViewDescription{};
    sourceViewDescription.Format = test.sourceIsSrgb
        ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB : DXGI_FORMAT_R8G8B8A8_UNORM;
    sourceViewDescription.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    sourceViewDescription.Texture2D.MipLevels = 1;
    ComPtr<ID3D11ShaderResourceView> declaredSourceView;
    if (!Require(SUCCEEDED(consumerDevice->CreateShaderResourceView(consumerTexture.Get(), &sourceViewDescription,
                 &declaredSourceView)), "failed to create explicitly declared UNORM/sRGB source view")) {
        return false;
    }

    D3D11_TEXTURE2D_DESC canonicalDescription = sourceDescription;
    canonicalDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    canonicalDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    canonicalDescription.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> texture;
    if (!Require(SUCCEEDED(consumerDevice->CreateTexture2D(&canonicalDescription, nullptr, &texture)),
                 "failed to create actual-sized canonical RGBA8 UNORM Quilt")) {
        return false;
    }
    ComPtr<ID3D11Query> copyCompletion;
    D3D11_QUERY_DESC queryDescription{};
    queryDescription.Query = D3D11_QUERY_EVENT;
    if (!Require(SUCCEEDED(consumerDevice->CreateQuery(&queryDescription, &copyCompletion)),
                 "failed to create raw-copy completion query")) {
        return false;
    }
    ComPtr<ID3D11DeviceContext> immediate;
    consumerDevice->GetImmediateContext(&immediate);
    immediate->CopyResource(texture.Get(), consumerTexture.Get());
    immediate->End(copyCompletion.Get());
    immediate->Flush();
    bool copyRetired = false;
    for (int attempt = 0; attempt < 2000; ++attempt) {
        const HRESULT copyStatus = immediate->GetData(copyCompletion.Get(), nullptr, 0, 0);
        if (copyStatus == S_OK) {
            copyRetired = true;
            break;
        }
        if (FAILED(copyStatus)) {
            std::fprintf(stderr, "FAIL: raw shared-texture copy completion query failed\n");
            return false;
        }
        Sleep(1);
    }
    if (!Require(copyRetired, "shared producer raw-copy did not retire within the bounded wait")) {
        return false;
    }

    ManualSubpixelPresenter presenter;
    std::string error;
    if (!Require(presenter.CreateOffscreenForTest(consumerDevice, calibration, cells.data(), test.mode,
                     test.linearFlag, kSdkDisplayAspect, &error),
                 error.empty() ? "failed to create offscreen presenter" : error.c_str())) {
        return false;
    }
    const ManualPresentResult present = presenter.Present(texture.Get(), 3, 2, test.sourceIsSrgb,
        test.aspect, test.zoom, &error);
    if (!Require(present == ManualPresentResult::Accepted,
                 error.empty() ? "presenter did not accept test frame" : error.c_str())) {
        presenter.Close(nullptr);
        return false;
    }
    if (!Require(presenter.PresentAccepted() == 1 && presenter.RenderCompleted() == 1 &&
                 !presenter.HasPendingGpuWork(), "present counters or GPU retirement state are incorrect")) {
        presenter.Close(nullptr);
        return false;
    }

    std::vector<uint8_t> actual;
    uint32_t width = 0;
    uint32_t height = 0;
    if (!Require(presenter.ReadbackLastPresentedRGBA8(actual, &width, &height, &error),
                 error.empty() ? "failed to read back presented texture" : error.c_str())) {
        presenter.Close(nullptr);
        return false;
    }
    const std::vector<Rgba8> expected = CpuReference(test, calibration, cells, source);
    if (!Require(width == 9 && height == 7 && actual.size() == expected.size() * 4,
                 "GPU output dimensions are incorrect")) {
        presenter.Close(nullptr);
        return false;
    }
    for (size_t pixel = 0; pixel < expected.size(); ++pixel) {
        const uint8_t expectedChannels[] = { expected[pixel].r, expected[pixel].g, expected[pixel].b, expected[pixel].a };
        for (size_t channel = 0; channel < 4; ++channel) {
            const double difference = std::abs(static_cast<double>(actual[pixel * 4 + channel]) / 255.0 -
                                               expectedChannels[channel] / 255.0);
            if (difference > kMaximumChannelError) {
                std::fprintf(stderr, "FAIL: %s pixel=%zu channel=%zu actual=%u expected=%u error=%.6f\n",
                    test.name, pixel, channel, actual[pixel * 4 + channel], expectedChannels[channel], difference);
                presenter.Close(nullptr);
                return false;
            }
        }
    }
    if (!Require(presenter.Close(&error), error.empty() ? "GPU retirement was not proven during close" : error.c_str())) {
        return false;
    }
    return true;
}

bool RunCalibrationBoundaryCases(ID3D11Device* device,
                                 const std::array<LkgWorkerSubpixelCellV1, 4>& cells) {
    struct InvalidCase {
        int32_t pattern;
        int32_t count;
        int32_t inverted;
    };
    const InvalidCase rejected[] = {
        {0, 0, 1},
        {1, 1, 1},
        {3, 3, 1},
        {4, 2, 0}
    };
    for (const InvalidCase& invalid : rejected) {
        TestCase base{"invalid-calibration", kCalibratedCells, invalid.pattern, invalid.count,
            0.0f, invalid.inverted, false, false, 0.25f, 37.0f, 1.3f, 1.0f, 1.0f};
        const auto calibration = MakeCalibration(base);
        ManualSubpixelPresenter presenter;
        std::string error;
        if (!Require(!presenter.CreateOffscreenForTest(device, calibration, cells.data(),
                base.mode, base.linearFlag, kSdkDisplayAspect, &error), "invalid calibrated-cell boundary was accepted")) {
            presenter.Close(nullptr);
            return false;
        }
        if (!Require(!error.empty(), "invalid calibrated-cell boundary had no diagnostic")) {
            return false;
        }
    }

    TestCase invalidAutomatic{"invalid-automatic-channel-order", kAutomatic, 0, 0,
        0.0f, 0, false, false, 0.25f, 37.0f, 1.3f, 1.0f, 1.0f};
    auto invalidAutomaticCalibration = MakeCalibration(invalidAutomatic);
    invalidAutomaticCalibration.blueIndex = invalidAutomaticCalibration.redIndex;
    ManualSubpixelPresenter invalidPresenter;
    std::string invalidError;
    if (!Require(!invalidPresenter.CreateOffscreenForTest(device, invalidAutomaticCalibration, cells.data(),
            invalidAutomatic.mode, invalidAutomatic.linearFlag, kSdkDisplayAspect, &invalidError),
            "invalid Automatic scalar channel order was accepted")) {
        invalidPresenter.Close(nullptr);
        return false;
    }

    auto ignoredCells = cells;
    ignoredCells[0].redX = std::numeric_limits<float>::quiet_NaN();
    TestCase classic{"classic-ignores-cell-table", kClassicRgb, 3, 4,
        0.0f, 0, false, false, 0.25f, 37.0f, 1.3f, 1.0f, 1.0f};
    const auto calibration = MakeCalibration(classic);
    ManualSubpixelPresenter presenter;
    std::string error;
    if (!Require(presenter.CreateOffscreenForTest(device, calibration, ignoredCells.data(),
            classic.mode, classic.linearFlag, kSdkDisplayAspect, &error),
            error.empty() ? "Classic RGB incorrectly validated unused cells" : error.c_str())) {
        return false;
    }
    if (!Require(presenter.Close(&error), error.empty() ? "Classic RGB boundary cleanup failed" : error.c_str())) {
        return false;
    }
    return true;
}

} // namespace

int main() {
    constexpr D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_0 };
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11Device> consumerDevice;
    ComPtr<ID3D11DeviceContext> consumerContext;
    D3D_FEATURE_LEVEL featureLevel{};
    const HRESULT createResult = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, featureLevels, static_cast<UINT>(std::size(featureLevels)),
        D3D11_SDK_VERSION, &device, &featureLevel, &context);
    if (!Require(SUCCEEDED(createResult) && featureLevel >= D3D_FEATURE_LEVEL_11_0,
                 "D3D11 WARP device creation failed")) {
        return 1;
    }
    D3D_FEATURE_LEVEL consumerFeatureLevel{};
    const HRESULT consumerCreateResult = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, featureLevels, static_cast<UINT>(std::size(featureLevels)),
        D3D11_SDK_VERSION, &consumerDevice, &consumerFeatureLevel, &consumerContext);
    if (!Require(SUCCEEDED(consumerCreateResult) && consumerFeatureLevel >= D3D_FEATURE_LEVEL_11_0,
                 "D3D11 WARP consumer device creation failed")) {
        return 1;
    }

    const auto cells = MakeCells();
    const auto source = MakeQuiltBytes();
    std::vector<TestCase> cases;
    MakeCases(cases);
    uint32_t passed = 0;
    for (const TestCase& test : cases) {
        if (!RunCase(device.Get(), consumerDevice.Get(), test, cells, source)) {
            std::fprintf(stderr, "FAIL: case %s\n", test.name);
            return 1;
        }
        ++passed;
    }
    auto nonFiniteUnusedCells = cells;
    for (LkgWorkerSubpixelCellV1& cell : nonFiniteUnusedCells) {
        cell.redX = std::numeric_limits<float>::quiet_NaN();
        cell.redY = std::numeric_limits<float>::quiet_NaN();
        cell.greenX = std::numeric_limits<float>::quiet_NaN();
        cell.greenY = std::numeric_limits<float>::quiet_NaN();
        cell.blueX = std::numeric_limits<float>::quiet_NaN();
        cell.blueY = std::numeric_limits<float>::quiet_NaN();
    }
    const TestCase classicWithoutCells{"classic-ignores-cell-table", kClassicRgb, 3, 0,
        0.0f, 0, false, false, 0.25f, 37.0f, 1.3f, 1.0f, 1.0f};
    if (!RunCase(device.Get(), consumerDevice.Get(), classicWithoutCells, nonFiniteUnusedCells, source)) {
        std::fprintf(stderr, "FAIL: case %s\n", classicWithoutCells.name);
        return 1;
    }
    ++passed;
    if (!RunCalibrationBoundaryCases(consumerDevice.Get(), cells)) {
        return 1;
    }

    std::printf("PASS: %u GPU oracle cases; 6 calibration boundary checks passed; compared every output pixel and RGBA channel; tolerance %.3f\n",
        passed, kMaximumChannelError);
    return 0;
}
