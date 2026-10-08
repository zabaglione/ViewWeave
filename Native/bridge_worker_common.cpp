#include "bridge_worker_common.h"

#include <cmath>

namespace lkg::common {

ValidationResult ValidateDrawRequest(const ImageDescriptor& descriptor) noexcept {
    if (descriptor.columns == 0 || descriptor.rows == 0 ||
        !std::isfinite(descriptor.aspect) || !std::isfinite(descriptor.zoom) ||
        descriptor.aspect <= 0.0f || descriptor.zoom <= 0.0f) {
        return ValidationResult::InvalidDrawRequest;
    }
    return ValidationResult::Valid;
}

ValidationResult ValidateGpuImageDescriptor(const ImageDescriptor& descriptor) noexcept {
    if (descriptor.producerApi != ProducerApi::D3D11 &&
        descriptor.producerApi != ProducerApi::D3D12) {
        return ValidationResult::UnsupportedBackend;
    }

    if (descriptor.dimension != ImageDimension::Texture2D ||
        descriptor.width == 0 || descriptor.height == 0 ||
        descriptor.mipLevels != 1 || descriptor.arraySize != 1 ||
        (descriptor.format != ImageFormat::Rgba8Typeless &&
         descriptor.format != ImageFormat::Rgba8Unorm &&
         descriptor.format != ImageFormat::Rgba8UnormSrgb) ||
        descriptor.sampleCount != 1 ||
        (descriptor.producerApi == ProducerApi::D3D12 && descriptor.sampleQuality != 0)) {
        return ValidationResult::InvalidImageDescriptor;
    }
    return ValidationResult::Valid;
}

ProducerApi ProducerApiFromUnityGraphicsApi(uint32_t unityGraphicsApi) noexcept {
    switch (unityGraphicsApi) {
    case 2: return ProducerApi::D3D11;
    case 18: return ProducerApi::D3D12;
    case 21: return ProducerApi::Vulkan;
    case 16: return ProducerApi::Metal;
    default: return ProducerApi::Unknown;
    }
}
bool SupportsBridgePath(ProducerApi producer, BridgeConsumerApi consumer) noexcept {
    if (consumer != BridgeConsumerApi::D3D11) return false;
    return producer == ProducerApi::D3D11 || producer == ProducerApi::D3D12;
}

}
