#include "platform/windows/stable_sdk_input_d3d11.h"

#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

#include "stable_sdk_input_d3d11_pixel_shader.h"
#include "stable_sdk_input_d3d11_vertex_shader.h"

namespace {

constexpr UINT kColumns = 8;
constexpr UINT kRows = 6;
constexpr UINT kOutputWidth = 64;
constexpr UINT kOutputHeight = 48;

struct Rgba {
    uint8_t r;
    uint8_t g;
    uint8_t b;
    uint8_t a;
};

bool Check(bool condition, const char* message) {
    if (!condition) {
        std::fprintf(stderr, "FAIL: %s\n", message);
        return false;
    }
    return true;
}

Rgba TileColour(UINT x, UINT y) {
    return Rgba{
        static_cast<uint8_t>(20 + x * 25),
        static_cast<uint8_t>(15 + y * 31),
        static_cast<uint8_t>(30 + (x + y) * 13),
        255};
}

ID3D11Texture2D* CreateTexture(ID3D11Device* device,
                               UINT width,
                               UINT height,
                               DXGI_FORMAT format,
                               bool gradient = false) {
    std::vector<Rgba> pixels(static_cast<size_t>(width) * height);
    const UINT tileWidth = width / kColumns;
    const UINT tileHeight = height / kRows;
    for (UINT y = 0; y < height; ++y) {
        for (UINT x = 0; x < width; ++x) {
            const Rgba base = TileColour(x / tileWidth, y / tileHeight);
            const uint8_t delta = gradient
                ? static_cast<uint8_t>(((x % tileWidth) + (y % tileHeight)) % 16)
                : 0;
            pixels[static_cast<size_t>(y) * width + x] = Rgba{
                static_cast<uint8_t>(base.r + delta),
                static_cast<uint8_t>(base.g + delta),
                static_cast<uint8_t>(base.b + delta),
                255};
        }
    }

    D3D11_TEXTURE2D_DESC description{};
    description.Width = width;
    description.Height = height;
    description.MipLevels = 1;
    description.ArraySize = 1;
    description.Format = format;
    description.SampleDesc.Count = 1;
    description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA initialData{};
    initialData.pSysMem = pixels.data();
    initialData.SysMemPitch = width * sizeof(Rgba);
    ID3D11Texture2D* texture = nullptr;
    if (FAILED(device->CreateTexture2D(&description, &initialData, &texture))) {
        return nullptr;
    }
    return texture;
}

bool WaitForUpload(lkg::stable_input::StableSdkInput* helper, ID3D11DeviceContext* context) {
    context->Flush();
    const ULONGLONG start = GetTickCount64();
    while (GetTickCount64() - start < 10000) {
        const auto state = helper->PollUpload();
        if (state == lkg::stable_input::UploadState::Complete) {
            return true;
        }
        if (state == lkg::stable_input::UploadState::Fault ||
            state == lkg::stable_input::UploadState::Idle) {
            return false;
        }
        Sleep(1);
    }
    return false;
}

std::vector<Rgba> ReadTexture(ID3D11Device* device,
                              ID3D11DeviceContext* context,
                              ID3D11Texture2D* texture,
                              UINT width,
                              UINT height) {
    D3D11_TEXTURE2D_DESC description{};
    texture->GetDesc(&description);
    description.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    description.Usage = D3D11_USAGE_STAGING;
    description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    description.MiscFlags = 0;
    ID3D11Texture2D* staging = nullptr;
    std::vector<Rgba> output;
    if (FAILED(device->CreateTexture2D(&description, nullptr, &staging))) {
        return output;
    }
    context->CopyResource(staging, texture);
    context->Flush();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (SUCCEEDED(context->Map(staging, 0, D3D11_MAP_READ, 0, &mapped))) {
        output.resize(static_cast<size_t>(width) * height);
        for (UINT y = 0; y < height; ++y) {
            std::memcpy(output.data() + static_cast<size_t>(y) * width,
                        static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch,
                        static_cast<size_t>(width) * sizeof(Rgba));
        }
        context->Unmap(staging, 0);
    }
    staging->Release();
    return output;
}

bool Near(uint8_t actual, uint8_t expected, uint8_t tolerance = 1) {
    return actual > expected ? actual - expected <= tolerance : expected - actual <= tolerance;
}

bool CheckSolidTiles(const std::vector<Rgba>& pixels,
                     UINT width,
                     UINT height,
                     uint8_t tolerance) {
    if (pixels.size() != static_cast<size_t>(width) * height) {
        return Check(false, "stable texture readback failed");
    }
    const UINT tileWidth = width / kColumns;
    const UINT tileHeight = height / kRows;
    for (UINT y = 0; y < height; ++y) {
        for (UINT x = 0; x < width; ++x) {
            const Rgba expected = TileColour(x / tileWidth, y / tileHeight);
            const Rgba actual = pixels[static_cast<size_t>(y) * width + x];
            if (!Near(actual.r, expected.r, tolerance) ||
                !Near(actual.g, expected.g, tolerance) ||
                !Near(actual.b, expected.b, tolerance) || actual.a != 255) {
                std::fprintf(stderr,
                             "FAIL: tile bleed or color mismatch at %u,%u expected=%u,%u,%u actual=%u,%u,%u\n",
                             x, y, expected.r, expected.g, expected.b, actual.r, actual.g, actual.b);
                return false;
            }
        }
    }
    return true;
}

bool CheckCopyBytes(const std::vector<Rgba>& actual, const std::vector<Rgba>& expected) {
    return Check(actual.size() == expected.size() &&
                 (actual.empty() || std::memcmp(actual.data(), expected.data(), actual.size() * sizeof(Rgba)) == 0),
                 "same-size copy did not preserve RGBA8 bytes");
}

struct PipelineStateSeed {
    ID3D11VertexShader* vertexShader = nullptr;
    ID3D11PixelShader* pixelShader = nullptr;
    ID3D11Buffer* constantBuffer = nullptr;
    ID3D11ShaderResourceView* shaderResourceView = nullptr;
    D3D11_VIEWPORT viewport{};
};

bool SeedPipelineState(ID3D11Device* device,
                       ID3D11DeviceContext* context,
                       ID3D11Texture2D* source,
                       PipelineStateSeed& seed) {
    HRESULT result = device->CreateVertexShader(g_stable_sdk_input_vertex_shader,
                                                sizeof(g_stable_sdk_input_vertex_shader),
                                                nullptr,
                                                &seed.vertexShader);
    if (SUCCEEDED(result)) {
        result = device->CreatePixelShader(g_stable_sdk_input_pixel_shader,
                                           sizeof(g_stable_sdk_input_pixel_shader),
                                           nullptr,
                                           &seed.pixelShader);
    }
    D3D11_BUFFER_DESC bufferDescription{};
    bufferDescription.ByteWidth = 48;
    bufferDescription.Usage = D3D11_USAGE_DEFAULT;
    bufferDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (SUCCEEDED(result)) {
        result = device->CreateBuffer(&bufferDescription, nullptr, &seed.constantBuffer);
    }
    D3D11_SHADER_RESOURCE_VIEW_DESC viewDescription{};
    D3D11_TEXTURE2D_DESC sourceDescription{};
    source->GetDesc(&sourceDescription);
    viewDescription.Format = sourceDescription.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS
        ? DXGI_FORMAT_R8G8B8A8_UNORM
        : sourceDescription.Format;
    viewDescription.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
    viewDescription.Texture2D.MostDetailedMip = 0;
    viewDescription.Texture2D.MipLevels = 1;
    if (SUCCEEDED(result)) {
        result = device->CreateShaderResourceView(source, &viewDescription, &seed.shaderResourceView);
    }
    if (FAILED(result)) {
        return false;
    }

    seed.viewport.TopLeftX = 3.0f;
    seed.viewport.TopLeftY = 5.0f;
    seed.viewport.Width = 19.0f;
    seed.viewport.Height = 23.0f;
    seed.viewport.MinDepth = 0.2f;
    seed.viewport.MaxDepth = 0.8f;
    context->RSSetViewports(1, &seed.viewport);
    context->VSSetShader(seed.vertexShader, nullptr, 0);
    context->PSSetShader(seed.pixelShader, nullptr, 0);
    context->PSSetConstantBuffers(0, 1, &seed.constantBuffer);
    context->PSSetShaderResources(0, 1, &seed.shaderResourceView);
    return true;
}

bool CheckPipelineState(ID3D11DeviceContext* context, const PipelineStateSeed& expected) {
    D3D11_VIEWPORT actualViewport{};
    UINT viewportCount = 1;
    context->RSGetViewports(&viewportCount, &actualViewport);
    ID3D11VertexShader* actualVertexShader = nullptr;
    ID3D11PixelShader* actualPixelShader = nullptr;
    ID3D11Buffer* actualConstantBuffer = nullptr;
    ID3D11ShaderResourceView* actualShaderResourceView = nullptr;
    context->VSGetShader(&actualVertexShader, nullptr, nullptr);
    context->PSGetShader(&actualPixelShader, nullptr, nullptr);
    context->PSGetConstantBuffers(0, 1, &actualConstantBuffer);
    context->PSGetShaderResources(0, 1, &actualShaderResourceView);

    const bool matches = viewportCount == 1 &&
        actualViewport.TopLeftX == expected.viewport.TopLeftX &&
        actualViewport.TopLeftY == expected.viewport.TopLeftY &&
        actualViewport.Width == expected.viewport.Width &&
        actualViewport.Height == expected.viewport.Height &&
        actualViewport.MinDepth == expected.viewport.MinDepth &&
        actualViewport.MaxDepth == expected.viewport.MaxDepth &&
        actualVertexShader == expected.vertexShader &&
        actualPixelShader == expected.pixelShader &&
        actualConstantBuffer == expected.constantBuffer &&
        actualShaderResourceView == expected.shaderResourceView;
    if (actualVertexShader) actualVertexShader->Release();
    if (actualPixelShader) actualPixelShader->Release();
    if (actualConstantBuffer) actualConstantBuffer->Release();
    if (actualShaderResourceView) actualShaderResourceView->Release();
    return Check(matches, "D3D11 immediate-context pipeline state changed during upload");
}

void ReleasePipelineStateSeed(PipelineStateSeed& seed) {
    if (seed.shaderResourceView) seed.shaderResourceView->Release();
    if (seed.constantBuffer) seed.constantBuffer->Release();
    if (seed.pixelShader) seed.pixelShader->Release();
    if (seed.vertexShader) seed.vertexShader->Release();
    seed = {};
}

bool CheckGradientTileEdges(const std::vector<Rgba>& pixels, UINT width, UINT height) {
    if (!Check(pixels.size() == static_cast<size_t>(width) * height, "gradient readback failed")) {
        return false;
    }
    const UINT tileWidth = width / kColumns;
    const UINT tileHeight = height / kRows;
    for (UINT tileY = 0; tileY < kRows; ++tileY) {
        for (UINT tileX = 0; tileX < kColumns; ++tileX) {
            const UINT right = (tileX + 1) * tileWidth - 1;
            const UINT down = (tileY + 1) * tileHeight - 1;
            const Rgba base = TileColour(tileX, tileY);
            const Rgba topLeft = pixels[static_cast<size_t>(tileY * tileHeight) * width + tileX * tileWidth];
            const Rgba bottomRight = pixels[static_cast<size_t>(down) * width + right];
            if (topLeft.r < base.r || topLeft.g < base.g || topLeft.b < base.b ||
                bottomRight.r <= topLeft.r || bottomRight.g <= topLeft.g || bottomRight.b <= topLeft.b) {
                return Check(false, "scaled gradient tile bounds were not preserved");
            }
        }
    }
    return true;
}

bool Run() {
    D3D_FEATURE_LEVEL featureLevel{};
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;
    HRESULT result = D3D11CreateDevice(nullptr,
                                       D3D_DRIVER_TYPE_WARP,
                                       nullptr,
                                       0,
                                       nullptr,
                                       0,
                                       D3D11_SDK_VERSION,
                                       &device,
                                       &featureLevel,
                                       &context);
    if (!Check(SUCCEEDED(result) && device && context, "could not create WARP D3D11 device")) {
        return false;
    }

    lkg::stable_input::StableSdkInput* helper = nullptr;
    result = lkg::stable_input::StableSdkInput::Create(device,
                                                       kOutputWidth,
                                                       kOutputHeight,
                                                       kColumns,
                                                       kRows,
                                                       &helper);
    bool ok = Check(SUCCEEDED(result) && helper, "stable SDK input creation failed");
    ID3D11Texture2D* stableIdentity = ok ? helper->GetTexture() : nullptr;

    // Exercise the fixed native endpoint while the producer texture dimensions
    // change as Preview, Balanced, Native, and Preview quality would.
    const std::array<UINT, 4> widths{64, 128, 256, 32};
    const std::array<UINT, 4> heights{48, 96, 192, 24};
    const std::array<DXGI_FORMAT, 4> formats{
        DXGI_FORMAT_R8G8B8A8_UNORM,
        DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        DXGI_FORMAT_R8G8B8A8_TYPELESS,
        DXGI_FORMAT_R8G8B8A8_UNORM};

    for (size_t index = 0; ok && index < widths.size(); ++index) {
        ID3D11Texture2D* source = CreateTexture(device, widths[index], heights[index], formats[index]);
        ok &= Check(source != nullptr, "could not create quality-profile source texture");
        if (source) {
            if (index == 0) {
                const auto expected = ReadTexture(device, context, source, widths[index], heights[index]);
                PipelineStateSeed pipelineState;
                ok &= Check(SeedPipelineState(device, context, source, pipelineState),
                            "could not seed immediate-context state for copy regression");
                result = helper->BeginUpload(source, kColumns, kRows, formats[index]);
                ok &= Check(SUCCEEDED(result), "same-size upload rejected");
                if (SUCCEEDED(result)) {
                    ok &= CheckPipelineState(context, pipelineState);
                    ok &= Check(WaitForUpload(helper, context), "same-size upload did not complete");
                    ok &= Check(helper->GetTexture() == stableIdentity, "stable texture identity changed after copy");
                    ok &= CheckCopyBytes(ReadTexture(device, context, stableIdentity, kOutputWidth, kOutputHeight), expected);
                }
                ReleasePipelineStateSeed(pipelineState);
                context->ClearState();
            } else {
                PipelineStateSeed pipelineState;
                ok &= Check(SeedPipelineState(device, context, source, pipelineState),
                            "could not seed immediate-context state for scale regression");
                result = helper->BeginUpload(source, kColumns, kRows, formats[index]);
                ok &= Check(SUCCEEDED(result), "scaled quality upload rejected");
                if (SUCCEEDED(result)) {
                    ok &= CheckPipelineState(context, pipelineState);
                    const HRESULT busy = helper->BeginUpload(source, kColumns, kRows, formats[index]);
                    ok &= Check(busy == HRESULT_FROM_WIN32(ERROR_BUSY), "second outstanding upload was not rejected");
                    ok &= Check(WaitForUpload(helper, context), "scaled quality upload did not complete");
                    ok &= Check(helper->GetTexture() == stableIdentity, "stable texture identity changed after scaling");
                    ok &= CheckSolidTiles(ReadTexture(device, context, stableIdentity, kOutputWidth, kOutputHeight),
                                          kOutputWidth,
                                          kOutputHeight,
                                          1);
                }
                ReleasePipelineStateSeed(pipelineState);
                context->ClearState();
            }
            source->Release();
        }
    }

    if (ok) {
        ID3D11Texture2D* gradient = CreateTexture(device, 128, 96, DXGI_FORMAT_R8G8B8A8_UNORM, true);
        ok &= Check(gradient != nullptr, "gradient source creation failed");
        if (gradient) {
            result = helper->BeginUpload(gradient, kColumns, kRows, DXGI_FORMAT_R8G8B8A8_UNORM);
            ok &= Check(SUCCEEDED(result) && WaitForUpload(helper, context), "gradient scale upload failed");
            if (ok) {
                ok &= CheckGradientTileEdges(ReadTexture(device, context, stableIdentity, kOutputWidth, kOutputHeight),
                                             kOutputWidth,
                                             kOutputHeight);
            }
            gradient->Release();
        }
    }

    if (ok) {
        const std::array<DXGI_FORMAT, 3> copyFormats{
            DXGI_FORMAT_R8G8B8A8_TYPELESS,
            DXGI_FORMAT_R8G8B8A8_UNORM,
            DXGI_FORMAT_R8G8B8A8_UNORM_SRGB};
        for (DXGI_FORMAT format : copyFormats) {
            ID3D11Texture2D* source = CreateTexture(device, kOutputWidth, kOutputHeight, format);
            ok &= Check(source != nullptr, "same-size format source creation failed");
            if (!source) {
                break;
            }
            const auto expected = ReadTexture(device, context, source, kOutputWidth, kOutputHeight);
            result = helper->BeginUpload(source, kColumns, kRows, format);
            ok &= Check(SUCCEEDED(result) && WaitForUpload(helper, context),
                        "same-size supported RGBA8 format upload failed");
            if (ok) {
                ok &= CheckCopyBytes(ReadTexture(device, context, stableIdentity, kOutputWidth, kOutputHeight),
                                     expected);
            }
            source->Release();
            if (!ok) {
                break;
            }
        }
    }

    if (ok) {
        ID3D11Texture2D* source = CreateTexture(device, 128, 96, DXGI_FORMAT_R8G8B8A8_UNORM);
        ok &= Check(source != nullptr, "validation source creation failed");
        if (source) {
            const auto before = ReadTexture(device, context, stableIdentity, kOutputWidth, kOutputHeight);
            ok &= Check(helper->BeginUpload(source, 4, 6, DXGI_FORMAT_R8G8B8A8_UNORM) == E_INVALIDARG,
                        "mismatched logical grid was accepted");
            ok &= Check(helper->BeginUpload(source, kColumns, kRows, DXGI_FORMAT_R8G8B8A8_UNORM_SRGB) == E_INVALIDARG,
                        "source format mismatch was accepted");
            const auto after = ReadTexture(device, context, stableIdentity, kOutputWidth, kOutputHeight);
            ok &= CheckCopyBytes(after, before);
            source->Release();
        }
    }

    if (helper) {
        ok &= Check(helper->Shutdown(true), "final cleanup did not release helper resources");
        delete helper;
    }
    if (context) {
        context->Release();
    }
    if (device) {
        device->Release();
    }
    return ok;
}

} // namespace

int main() {
    return Run() ? 0 : 1;
}
