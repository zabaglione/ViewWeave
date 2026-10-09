#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif

#include <d3d11.h>
#include <dxgiformat.h>

namespace lkg::stable_input {

enum class UploadState {
    Idle,
    Pending,
    Complete,
    Fault
};

// Keeps one profile-sized RGBA8_UNORM input alive across producer leases.
// All methods must be called on the owning D3D11 worker thread.
class StableSdkInput final {
public:
    static HRESULT Create(ID3D11Device* device,
                          UINT width,
                          UINT height,
                          UINT destinationColumns,
                          UINT destinationRows,
                          StableSdkInput** output);

    StableSdkInput(const StableSdkInput&) = delete;
    StableSdkInput& operator=(const StableSdkInput&) = delete;
    ~StableSdkInput();

    ID3D11Texture2D* GetTexture() const noexcept;

    // The producer must be complete before this call. The source reference is
    // retained until PollUpload returns Complete. expectedFormat must match
    // the source resource's format exactly.
    HRESULT BeginUpload(ID3D11Texture2D* source,
                        UINT sourceColumns,
                        UINT sourceRows,
                        DXGI_FORMAT expectedFormat);

    // Polls an EVENT query without flushing. The caller owns flushing policy.
    UploadState PollUpload() noexcept;

    // Call only after all SDK references to GetTexture() have been released
    // and after PollUpload is not Pending. False leaves all resources retained.
    bool Shutdown(bool finalSdkCleanupProven) noexcept;

private:
    StableSdkInput() = default;
    void ReleaseResources() noexcept;
    bool ValidateSource(ID3D11Texture2D* source,
                        UINT sourceColumns,
                        UINT sourceRows,
                        DXGI_FORMAT expectedFormat,
                        D3D11_TEXTURE2D_DESC& description) const noexcept;
    HRESULT ScalePerTile(ID3D11ShaderResourceView* sourceView,
                         UINT sourceWidth,
                         UINT sourceHeight,
                         UINT sourceColumns,
                         UINT sourceRows,
                         bool sourceIsSrgb);

    ID3D11Device* device_ = nullptr;
    ID3D11DeviceContext* immediateContext_ = nullptr;
    ID3D11DeviceContext* deferredContext_ = nullptr;
    ID3D11Texture2D* stableTexture_ = nullptr;
    ID3D11RenderTargetView* stableRtv_ = nullptr;
    ID3D11VertexShader* vertexShader_ = nullptr;
    ID3D11PixelShader* pixelShader_ = nullptr;
    ID3D11RasterizerState* rasterizerState_ = nullptr;
    ID3D11Buffer* constants_ = nullptr;
    ID3D11Query* completionQuery_ = nullptr;
    ID3D11Texture2D* retainedSource_ = nullptr;
    ID3D11ShaderResourceView* retainedSourceView_ = nullptr;
    UINT width_ = 0;
    UINT height_ = 0;
    UINT destinationColumns_ = 0;
    UINT destinationRows_ = 0;
    bool sourceIsSrgb_ = false;
    bool pending_ = false;
    bool faulted_ = false;
    bool shutdown_ = false;
};

} // namespace lkg::stable_input
