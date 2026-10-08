static lkg::common::ImageFormat ToCommonImageFormat(DXGI_FORMAT format) {
    switch (format) {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return lkg::common::ImageFormat::Rgba8Typeless;
    case DXGI_FORMAT_R8G8B8A8_UNORM: return lkg::common::ImageFormat::Rgba8Unorm;
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return lkg::common::ImageFormat::Rgba8UnormSrgb;
    default: return lkg::common::ImageFormat::Other;
    }
}

static bool IsValidRenderFormat(const D3D11_TEXTURE2D_DESC& desc) {
    lkg::common::ImageDescriptor image{};
    image.width = desc.Width;
    image.height = desc.Height;
    image.arraySize = desc.ArraySize;
    image.mipLevels = desc.MipLevels;
    image.sampleCount = desc.SampleDesc.Count;
    image.sampleQuality = desc.SampleDesc.Quality;
    image.dimension = lkg::common::ImageDimension::Texture2D;
    image.format = ToCommonImageFormat(desc.Format);
    image.producerApi = lkg::common::ProducerApi::D3D11;
    return lkg::common::ValidateGpuImageDescriptor(image) == lkg::common::ValidationResult::Valid;
}

static DXGI_FORMAT SharedRenderFormat(DXGI_FORMAT sourceFormat) {
    return sourceFormat == DXGI_FORMAT_R8G8B8A8_TYPELESS ? DXGI_FORMAT_R8G8B8A8_UNORM : sourceFormat;
}

static void SetRenderFault(WorkerContext& context, const char* message);
static void ClearD3D12Slot(WorkerContext::Slot& slot);
