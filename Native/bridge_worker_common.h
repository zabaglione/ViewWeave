#pragma once

#include <cstdint>

namespace lkg::common {

enum class ProducerApi : uint32_t {
    Unknown = 0,
    D3D11 = 1,
    D3D12 = 2,
    Vulkan = 3,
    Metal = 4
};

enum class BridgeConsumerApi : uint32_t {
    Unknown = 0,
    D3D11 = 1
};

enum class ImageFormat : uint32_t {
    Unknown = 0,
    Rgba8Typeless = 1,
    Rgba8Unorm = 2,
    Rgba8UnormSrgb = 3,
    Other = 4
};

enum class ImageDimension : uint32_t {
    Unknown = 0,
    Texture2D = 1
};

enum class ImageOrigin : uint32_t {
    Unknown = 0,
    TopLeft = 1,
    BottomLeft = 2
};

enum class ColorSpace : uint32_t {
    Unknown = 0,
    Linear = 1,
    Srgb = 2
};

enum class ValidationResult : uint32_t {
    Valid = 0,
    InvalidDrawRequest = 1,
    InvalidImageDescriptor = 2,
    UnsupportedBackend = 3
};

struct ImageDescriptor {
    uint64_t width = 0;
    uint32_t height = 0;
    uint32_t columns = 0;
    uint32_t rows = 0;
    uint32_t arraySize = 0;
    uint32_t mipLevels = 0;
    uint32_t sampleCount = 0;
    uint32_t sampleQuality = 0;
    ImageDimension dimension = ImageDimension::Unknown;
    ImageFormat format = ImageFormat::Unknown;
    ColorSpace colorSpace = ColorSpace::Unknown;
    ImageOrigin origin = ImageOrigin::Unknown;
    ProducerApi producerApi = ProducerApi::Unknown;
    uint64_t generation = 0;
    float aspect = 0.0f;
    float zoom = 0.0f;
};

ValidationResult ValidateDrawRequest(const ImageDescriptor& descriptor) noexcept;
ValidationResult ValidateGpuImageDescriptor(const ImageDescriptor& descriptor) noexcept;
ProducerApi ProducerApiFromUnityGraphicsApi(uint32_t unityGraphicsApi) noexcept;
bool SupportsBridgePath(ProducerApi producer, BridgeConsumerApi consumer) noexcept;

}
