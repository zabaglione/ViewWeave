#include "stable_sdk_input_d3d11.h"

#include <algorithm>
#include <cstdint>
#include <new>
#include <windows.h>

#include "stable_sdk_input_d3d11_pixel_shader.h"
#include "stable_sdk_input_d3d11_vertex_shader.h"

namespace lkg::stable_input {
namespace {

struct ScaleConstants {
    UINT sourceWidth;
    UINT sourceHeight;
    UINT sourceColumns;
    UINT sourceRows;
    UINT outputWidth;
    UINT outputHeight;
    UINT destinationColumns;
    UINT destinationRows;
    UINT sourceIsSrgb;
    UINT padding[3];
};

bool IsRgba8Format(DXGI_FORMAT format) noexcept {
    return format == DXGI_FORMAT_R8G8B8A8_TYPELESS ||
           format == DXGI_FORMAT_R8G8B8A8_UNORM ||
           format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
}

DXGI_FORMAT ShaderViewFormat(DXGI_FORMAT format) noexcept {
    return format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
        ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
        : DXGI_FORMAT_R8G8B8A8_UNORM;
}

bool IsTileGridValid(UINT width, UINT height, UINT columns, UINT rows) noexcept {
    return width != 0 && height != 0 && columns != 0 && rows != 0 &&
           width % columns == 0 && height % rows == 0;
}

template <typename T>
void SafeRelease(T*& value) noexcept {
    if (value) {
        value->Release();
        value = nullptr;
    }
}

} // namespace

HRESULT StableSdkInput::Create(ID3D11Device* device,
                               UINT width,
                               UINT height,
                               UINT destinationColumns,
                               UINT destinationRows,
                               StableSdkInput** output) {
    if (!output) {
        return E_POINTER;
    }
    *output = nullptr;
    if (!device || !IsTileGridValid(width, height, destinationColumns, destinationRows)) {
        return E_INVALIDARG;
    }

    StableSdkInput* value = new (std::nothrow) StableSdkInput();
    if (!value) {
        return E_OUTOFMEMORY;
    }

    value->device_ = device;
    value->device_->AddRef();
    value->width_ = width;
    value->height_ = height;
    value->destinationColumns_ = destinationColumns;
    value->destinationRows_ = destinationRows;

    HRESULT result = S_OK;
    device->GetImmediateContext(&value->immediateContext_);
    if (!value->immediateContext_) {
        result = E_FAIL;
    }
    if (SUCCEEDED(result)) {
        result = device->CreateDeferredContext(0, &value->deferredContext_);
    }

    D3D11_TEXTURE2D_DESC textureDescription{};
    textureDescription.Width = width;
    textureDescription.Height = height;
    textureDescription.MipLevels = 1;
    textureDescription.ArraySize = 1;
    textureDescription.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    textureDescription.SampleDesc.Count = 1;
    textureDescription.SampleDesc.Quality = 0;
    textureDescription.Usage = D3D11_USAGE_DEFAULT;
    textureDescription.BindFlags = D3D11_BIND_RENDER_TARGET;
    textureDescription.CPUAccessFlags = 0;
    textureDescription.MiscFlags = 0;
    if (SUCCEEDED(result)) {
        result = device->CreateTexture2D(&textureDescription, nullptr, &value->stableTexture_);
    }
    if (SUCCEEDED(result)) {
        result = device->CreateRenderTargetView(value->stableTexture_, nullptr, &value->stableRtv_);
    }

    if (SUCCEEDED(result)) {
        result = device->CreateVertexShader(g_stable_sdk_input_vertex_shader,
                                            sizeof(g_stable_sdk_input_vertex_shader),
                                            nullptr,
                                            &value->vertexShader_);
    }
    if (SUCCEEDED(result)) {
        result = device->CreatePixelShader(g_stable_sdk_input_pixel_shader,
                                           sizeof(g_stable_sdk_input_pixel_shader),
                                           nullptr,
                                           &value->pixelShader_);
    }

    D3D11_RASTERIZER_DESC rasterizerDescription{};
    rasterizerDescription.FillMode = D3D11_FILL_SOLID;
    rasterizerDescription.CullMode = D3D11_CULL_NONE;
    rasterizerDescription.DepthClipEnable = TRUE;
    if (SUCCEEDED(result)) {
        result = device->CreateRasterizerState(&rasterizerDescription, &value->rasterizerState_);
    }

    D3D11_BUFFER_DESC bufferDescription{};
    bufferDescription.ByteWidth = static_cast<UINT>(sizeof(ScaleConstants));
    bufferDescription.Usage = D3D11_USAGE_DEFAULT;
    bufferDescription.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (SUCCEEDED(result)) {
        result = device->CreateBuffer(&bufferDescription, nullptr, &value->constants_);
    }

    D3D11_QUERY_DESC queryDescription{};
    queryDescription.Query = D3D11_QUERY_EVENT;
    queryDescription.MiscFlags = 0;
    if (SUCCEEDED(result)) {
        result = device->CreateQuery(&queryDescription, &value->completionQuery_);
    }

    if (FAILED(result)) {
        value->ReleaseResources();
        delete value;
        return result;
    }

    *output = value;
    return S_OK;
}

StableSdkInput::~StableSdkInput() {
    // Resources are released only by Shutdown after final SDK cleanup. If a
    // caller destroys this object without proof, intentionally retain the raw
    // COM references so a faulted asynchronous consumer cannot use freed GPU
    // resources. The owner must retain the helper object on fault paths.
}

ID3D11Texture2D* StableSdkInput::GetTexture() const noexcept {
    return shutdown_ ? nullptr : stableTexture_;
}

bool StableSdkInput::ValidateSource(ID3D11Texture2D* source,
                                   UINT sourceColumns,
                                   UINT sourceRows,
                                   DXGI_FORMAT expectedFormat,
                                   D3D11_TEXTURE2D_DESC& description) const noexcept {
    if (!source || !IsRgba8Format(expectedFormat)) {
        return false;
    }
    ID3D11Device* sourceDevice = nullptr;
    source->GetDevice(&sourceDevice);
    const bool sameDevice = sourceDevice == device_;
    SafeRelease(sourceDevice);
    if (!sameDevice) {
        return false;
    }
    source->GetDesc(&description);
    if (description.Format != expectedFormat || !IsRgba8Format(description.Format) ||
        description.MipLevels != 1 || description.ArraySize != 1 ||
        description.SampleDesc.Count != 1 || description.SampleDesc.Quality != 0 ||
        !IsTileGridValid(description.Width, description.Height, sourceColumns, sourceRows)) {
        return false;
    }
    return true;
}

HRESULT StableSdkInput::BeginUpload(ID3D11Texture2D* source,
                                    UINT sourceColumns,
                                    UINT sourceRows,
                                    DXGI_FORMAT expectedFormat) {
    if (shutdown_ || faulted_ || !device_ || !immediateContext_ || !deferredContext_ ||
        !stableTexture_ || !completionQuery_) {
        return E_UNEXPECTED;
    }
    if (pending_ || retainedSource_ || retainedSourceView_) {
        return HRESULT_FROM_WIN32(ERROR_BUSY);
    }

    D3D11_TEXTURE2D_DESC sourceDescription{};
    if (sourceColumns != destinationColumns_ || sourceRows != destinationRows_ ||
        !ValidateSource(source, sourceColumns, sourceRows, expectedFormat, sourceDescription)) {
        return E_INVALIDARG;
    }

    source->AddRef();
    retainedSource_ = source;
    sourceIsSrgb_ = sourceDescription.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;

    HRESULT result = S_OK;
    if (sourceDescription.Width == width_ && sourceDescription.Height == height_) {
        // All accepted formats share the same RGBA8 storage family. This path
        // preserves the original channel bytes, including SRGB resources.
        immediateContext_->CopyResource(stableTexture_, source);
    } else {
        D3D11_SHADER_RESOURCE_VIEW_DESC viewDescription{};
        viewDescription.Format = ShaderViewFormat(sourceDescription.Format);
        viewDescription.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        viewDescription.Texture2D.MostDetailedMip = 0;
        viewDescription.Texture2D.MipLevels = 1;
        result = device_->CreateShaderResourceView(source, &viewDescription, &retainedSourceView_);
        if (SUCCEEDED(result)) {
            result = ScalePerTile(retainedSourceView_,
                                  sourceDescription.Width,
                                  sourceDescription.Height,
                                  sourceColumns,
                                  sourceRows,
                                  sourceIsSrgb_);
        }
    }

    if (FAILED(result)) {
        SafeRelease(retainedSourceView_);
        SafeRelease(retainedSource_);
        faulted_ = true;
        return result;
    }

    // The caller controls Flush on the immediate context after this call.
    // This query proves only that the helper's source read has completed.
    immediateContext_->End(completionQuery_);
    pending_ = true;
    return S_OK;
}

HRESULT StableSdkInput::ScalePerTile(ID3D11ShaderResourceView* sourceView,
                                    UINT sourceWidth,
                                    UINT sourceHeight,
                                    UINT sourceColumns,
                                    UINT sourceRows,
                                    bool sourceIsSrgb) {
    if (!sourceView || !deferredContext_) {
        return E_INVALIDARG;
    }

    const ScaleConstants constants{
        sourceWidth,
        sourceHeight,
        sourceColumns,
        sourceRows,
        width_,
        height_,
        destinationColumns_,
        destinationRows_,
        sourceIsSrgb ? 1u : 0u,
        {0u, 0u, 0u}
    };
    deferredContext_->UpdateSubresource(constants_, 0, nullptr, &constants, 0, 0);

    D3D11_VIEWPORT viewport{};
    viewport.TopLeftX = 0.0f;
    viewport.TopLeftY = 0.0f;
    viewport.Width = static_cast<float>(width_);
    viewport.Height = static_cast<float>(height_);
    viewport.MinDepth = 0.0f;
    viewport.MaxDepth = 1.0f;
    deferredContext_->RSSetViewports(1, &viewport);
    deferredContext_->RSSetState(rasterizerState_);

    ID3D11RenderTargetView* renderTarget = stableRtv_;
    deferredContext_->OMSetRenderTargets(1, &renderTarget, nullptr);
    deferredContext_->IASetInputLayout(nullptr);
    deferredContext_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    deferredContext_->VSSetShader(vertexShader_, nullptr, 0);
    deferredContext_->PSSetShader(pixelShader_, nullptr, 0);
    deferredContext_->PSSetConstantBuffers(0, 1, &constants_);
    deferredContext_->PSSetShaderResources(0, 1, &sourceView);
    deferredContext_->Draw(3, 0);

    ID3D11ShaderResourceView* nullView = nullptr;
    deferredContext_->PSSetShaderResources(0, 1, &nullView);
    ID3D11CommandList* commandList = nullptr;
    HRESULT result = deferredContext_->FinishCommandList(FALSE, &commandList);
    if (FAILED(result)) {
        deferredContext_->ClearState();
        return result;
    }

    // The immediate context is shared with the long-lived worker. Preserve its
    // previous pipeline state while executing the helper's isolated draw.
    immediateContext_->ExecuteCommandList(commandList, TRUE);
    commandList->Release();
    deferredContext_->ClearState();
    return S_OK;
}

UploadState StableSdkInput::PollUpload() noexcept {
    if (faulted_) {
        return UploadState::Fault;
    }
    if (!pending_) {
        return UploadState::Idle;
    }

    BOOL complete = FALSE;
    const HRESULT result = immediateContext_->GetData(completionQuery_,
                                                       &complete,
                                                       sizeof(complete),
                                                       D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (result == S_FALSE || (SUCCEEDED(result) && complete == FALSE)) {
        return UploadState::Pending;
    }
    if (FAILED(result)) {
        faulted_ = true;
        return UploadState::Fault;
    }

    pending_ = false;
    SafeRelease(retainedSourceView_);
    SafeRelease(retainedSource_);
    return UploadState::Complete;
}

bool StableSdkInput::Shutdown(bool finalSdkCleanupProven) noexcept {
    if (shutdown_) {
        return true;
    }
    if (!finalSdkCleanupProven || pending_ || faulted_ || retainedSource_ || retainedSourceView_) {
        return false;
    }
    ReleaseResources();
    shutdown_ = true;
    return true;
}

void StableSdkInput::ReleaseResources() noexcept {
    SafeRelease(completionQuery_);
    SafeRelease(constants_);
    SafeRelease(rasterizerState_);
    SafeRelease(pixelShader_);
    SafeRelease(vertexShader_);
    SafeRelease(stableRtv_);
    SafeRelease(stableTexture_);
    SafeRelease(deferredContext_);
    SafeRelease(immediateContext_);
    SafeRelease(device_);
}

} // namespace lkg::stable_input
