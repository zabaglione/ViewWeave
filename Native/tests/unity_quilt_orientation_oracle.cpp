#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include "platform/windows/manual_subpixel_presenter.h"

#include <d3d11.h>
#include <wrl/client.h>

#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <limits>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
using lkg::subpixel::ManualPresentResult;
using lkg::subpixel::ManualSubpixelPresenter;

namespace {

constexpr uint32_t kOutputWidth = 9;
constexpr uint32_t kOutputHeight = 7;
constexpr float kDisplayAspect = 1.17f;
constexpr uint32_t kAutomaticMode = 0;
constexpr uint32_t kClassicRgbMode = 1;
constexpr uint32_t kCalibratedCellsMode = 2;

bool ParsePositive(const char* text, uint32_t* value) {
    if (!text || !value || !*text) return false;
    char* end = nullptr;
    const unsigned long parsed = std::strtoul(text, &end, 10);
    if (!end || *end != '\0' || parsed == 0 || parsed > std::numeric_limits<uint32_t>::max()) return false;
    *value = static_cast<uint32_t>(parsed);
    return true;
}

bool ParseMode(const char* text, uint32_t* value) {
    if (!text || !value || !*text) return false;
    char* end = nullptr;
    const unsigned long parsed = std::strtoul(text, &end, 10);
    if (!end || *end != '\0' || parsed > kCalibratedCellsMode) return false;
    *value = static_cast<uint32_t>(parsed);
    return true;
}

bool ReadInput(const std::string& path, uint32_t width, uint32_t height,
               std::vector<uint8_t>* bytes) {
    const uint64_t expected = static_cast<uint64_t>(width) * height * 4;
    if (expected > std::numeric_limits<size_t>::max()) return false;
    std::ifstream file(path, std::ios::binary);
    if (!file) return false;
    bytes->assign(static_cast<size_t>(expected), 0);
    file.read(reinterpret_cast<char*>(bytes->data()), static_cast<std::streamsize>(expected));
    if (file.gcount() != static_cast<std::streamsize>(expected)) return false;
    return file.peek() == std::ifstream::traits_type::eof();
}

bool WriteOutput(const std::string& path, const std::vector<uint8_t>& bytes) {
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) return false;
    file.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return file.good();
}

std::string JsonEscape(const std::string& value) {
    std::string escaped;
    escaped.reserve(value.size());
    for (const unsigned char character : value) {
        if (character == '\\' || character == '"') {
            escaped.push_back('\\');
            escaped.push_back(static_cast<char>(character));
        } else if (character < 0x20) {
            escaped.push_back(' ');
        } else {
            escaped.push_back(static_cast<char>(character));
        }
    }
    return escaped;
}

bool WriteMetadata(const std::string& path, const std::string& inputPath,
                   const std::string& outputPath, uint32_t inputWidth,
                   uint32_t inputHeight, uint32_t columns, uint32_t rows,
                   uint32_t mode) {
    const char* modeName = mode == kAutomaticMode ? "Automatic" :
        mode == kClassicRgbMode ? "Classic RGB" : "Calibrated Cells";
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    if (!file) return false;
    file << "{\n"
         << "  \"input\": \"" << JsonEscape(inputPath) << "\",\n"
         << "  \"inputFormat\": \"RGBA8_UNORM raw bytes in supplied AsyncGPUReadback storage order; row 0 does not assert physical top\",\n"
         << "  \"inputWidth\": " << inputWidth << ",\n"
         << "  \"inputHeight\": " << inputHeight << ",\n"
         << "  \"columns\": " << columns << ",\n"
         << "  \"rows\": " << rows << ",\n"
         << "  \"output\": \"" << JsonEscape(outputPath) << "\",\n"
         << "  \"outputFormat\": \"RGBA8_UNORM raw bytes in D3D11 staging texture-Y order; row 0 maps viewport top in this offscreen target\",\n"
         << "  \"outputWidth\": " << kOutputWidth << ",\n"
         << "  \"outputHeight\": " << kOutputHeight << ",\n"
         << "  \"device\": \"D3D11 WARP\",\n"
         << "  \"shader\": \"Native/platform/windows/manual_subpixel_presenter.hlsl compiled by repository FXC helper\",\n"
         << "  \"mode\": \"" << modeName << "\",\n"
         << "  \"calibration\": \"manual_subpixel_presenter_test.cpp fixture: center=0.25 pitch=37 slope=1.3 dpi=40 flipX=0 invertedViews=1 pattern=3 cellCount=4\",\n"
         << "  \"opticalPanelValidation\": false,\n"
         << "  \"cpuReferenceUsedForOrientation\": false,\n"
         << "  \"gpuCompletion\": \"PollGpuCompletion succeeded before readback; Close succeeded\"\n"
         << "}\n";
    return file.good();
}

void PrintUsage() {
    std::fprintf(stderr,
        "Usage: lookingglass_unity_quilt_orientation_oracle <input.rgba8> <output.rgba8> <metadata.json> <width> <height> <columns> <rows> [mode: 0=Automatic, 1=Classic RGB, 2=Calibrated Cells]\n");
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 8 && argc != 9) {
        PrintUsage();
        return 2;
    }

    uint32_t inputWidth = 0;
    uint32_t inputHeight = 0;
    uint32_t columns = 0;
    uint32_t rows = 0;
    uint32_t mode = kAutomaticMode;
    if (!ParsePositive(argv[4], &inputWidth) || !ParsePositive(argv[5], &inputHeight) ||
        !ParsePositive(argv[6], &columns) || !ParsePositive(argv[7], &rows) ||
        inputWidth % columns != 0 || inputHeight % rows != 0) {
        std::fprintf(stderr, "FAIL: dimensions must be positive and divisible by the quilt grid\n");
        return 2;
    }
    if (argc == 9 && !ParseMode(argv[8], &mode)) {
        std::fprintf(stderr, "FAIL: mode must be 0, 1, or 2\n");
        return 2;
    }
    std::vector<uint8_t> source;
    if (!ReadInput(argv[1], inputWidth, inputHeight, &source)) {
        std::fprintf(stderr, "FAIL: input size does not exactly match width*height*4\n");
        return 2;
    }

    constexpr D3D_FEATURE_LEVEL featureLevels[] = { D3D_FEATURE_LEVEL_11_0 };
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> immediate;
    D3D_FEATURE_LEVEL featureLevel{};
    const HRESULT deviceResult = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, featureLevels, 1, D3D11_SDK_VERSION,
        &device, &featureLevel, &immediate);
    if (FAILED(deviceResult) || featureLevel < D3D_FEATURE_LEVEL_11_0) {
        std::fprintf(stderr, "FAIL: D3D11 WARP device creation failed\n");
        return 1;
    }

    D3D11_TEXTURE2D_DESC sourceDescription{};
    sourceDescription.Width = inputWidth;
    sourceDescription.Height = inputHeight;
    sourceDescription.MipLevels = 1;
    sourceDescription.ArraySize = 1;
    sourceDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sourceDescription.SampleDesc.Count = 1;
    sourceDescription.Usage = D3D11_USAGE_DEFAULT;
    sourceDescription.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA initialData{};
    initialData.pSysMem = source.data();
    initialData.SysMemPitch = inputWidth * 4;
    ComPtr<ID3D11Texture2D> quilt;
    if (FAILED(device->CreateTexture2D(&sourceDescription, &initialData, &quilt))) {
        std::fprintf(stderr, "FAIL: source quilt texture creation failed\n");
        return 1;
    }

    LkgWorkerSubpixelCalibrationV1 calibration{};
    calibration.structSize = sizeof(calibration);
    calibration.version = 1;
    calibration.nativeWidth = kOutputWidth;
    calibration.nativeHeight = kOutputHeight;
    calibration.center = 0.25f;
    calibration.pitch = 37.0f;
    calibration.slope = 1.3f;
    calibration.dpi = 40.0f;
    calibration.flipX = 0.0f;
    calibration.viewCone = 40.0f;
    calibration.invertedViews = 1;
    calibration.cellPatternMode = 3;
    calibration.cellCount = 4;
    calibration.redIndex = 0;
    calibration.blueIndex = 2;
    LkgWorkerSubpixelCellV1 cells[4] = {
        {-1.25f, 0.5f, 0.0f, -0.375f, 1.4f, 0.75f},
        {0.65f, -1.1f, -0.8f, 0.3f, 0.35f, -0.6f},
        {-0.45f, 0.2f, 0.9f, -0.7f, -1.3f, 0.4f},
        {1.1f, -0.5f, -1.0f, 1.25f, 0.55f, -0.9f}
    };

    ManualSubpixelPresenter presenter;
    std::string error;
    if (!presenter.CreateOffscreenForTest(device.Get(), calibration, cells,
            mode, false, kDisplayAspect, &error)) {
        std::fprintf(stderr, "FAIL: presenter creation: %s\n", error.c_str());
        return 1;
    }
    const ManualPresentResult presentResult = presenter.Present(quilt.Get(), columns, rows,
        false, 1.0f, 1.0f, &error);
    if (presentResult != ManualPresentResult::Accepted || presenter.HasPendingGpuWork() ||
        presenter.RenderCompleted() != 1 || presenter.PresentAccepted() != 1) {
        std::fprintf(stderr, "FAIL: present or GPU completion gate: %s\n", error.c_str());
        presenter.Close(nullptr);
        return 1;
    }
    std::vector<uint8_t> output;
    uint32_t outputWidth = 0;
    uint32_t outputHeight = 0;
    if (!presenter.ReadbackLastPresentedRGBA8(output, &outputWidth, &outputHeight, &error) ||
        outputWidth != kOutputWidth || outputHeight != kOutputHeight ||
        output.size() != static_cast<size_t>(kOutputWidth) * kOutputHeight * 4) {
        std::fprintf(stderr, "FAIL: completed frame readback: %s\n", error.c_str());
        presenter.Close(nullptr);
        return 1;
    }
    if (!WriteOutput(argv[2], output)) {
        std::fprintf(stderr, "FAIL: output write failed\n");
        presenter.Close(nullptr);
        return 1;
    }
    if (!presenter.Close(&error)) {
        std::fprintf(stderr, "FAIL: presenter Close: %s\n", error.c_str());
        return 1;
    }
    if (!WriteMetadata(argv[3], argv[1], argv[2], inputWidth, inputHeight, columns, rows, mode)) {
        std::fprintf(stderr, "FAIL: metadata write failed\n");
        return 1;
    }
    std::printf("PASS: WARP rendered %u input bytes to %u output bytes; GPU completion and Close succeeded\n",
        static_cast<uint32_t>(source.size()), static_cast<uint32_t>(output.size()));
    return 0;
}
