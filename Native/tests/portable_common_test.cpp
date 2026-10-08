#include "bridge_worker_common.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>

using namespace lkg::common;

static void Check(bool condition, const char* name) {
    if (condition) return;
    std::fprintf(stderr, "FAIL %s\n", name);
    std::exit(1);
}

static ImageDescriptor ValidRequest() {
    ImageDescriptor descriptor{};
    descriptor.columns = 8;
    descriptor.rows = 6;
    descriptor.aspect = 1.0f;
    descriptor.zoom = 1.0f;
    return descriptor;
}

static ImageDescriptor ValidD3D11Image() {
    ImageDescriptor descriptor{};
    descriptor.width = 7680;
    descriptor.height = 4320;
    descriptor.columns = 8;
    descriptor.rows = 6;
    descriptor.arraySize = 1;
    descriptor.mipLevels = 1;
    descriptor.sampleCount = 1;
    descriptor.dimension = ImageDimension::Texture2D;
    descriptor.format = ImageFormat::Rgba8Unorm;
    descriptor.producerApi = ProducerApi::D3D11;
    descriptor.generation = 1;
    return descriptor;
}

int main() {
    ImageDescriptor request = ValidRequest();
    Check(ValidateDrawRequest(request) == ValidationResult::Valid, "request accepts unset gpu descriptor");
    request.columns = 1;
    request.rows = 1;
    Check(ValidateDrawRequest(request) == ValidationResult::Valid, "request accepts one by one grid");
    request = ValidRequest();
    request.columns = 0;
    Check(ValidateDrawRequest(request) == ValidationResult::InvalidDrawRequest, "request rejects zero columns");
    request = ValidRequest();
    request.rows = 0;
    Check(ValidateDrawRequest(request) == ValidationResult::InvalidDrawRequest, "request rejects zero rows");
    request = ValidRequest();
    request.aspect = std::numeric_limits<float>::quiet_NaN();
    Check(ValidateDrawRequest(request) == ValidationResult::InvalidDrawRequest, "request rejects nan aspect");
    request = ValidRequest();
    request.zoom = std::numeric_limits<float>::infinity();
    Check(ValidateDrawRequest(request) == ValidationResult::InvalidDrawRequest, "request rejects infinite zoom");
    request = ValidRequest();
    request.aspect = 0.0f;
    Check(ValidateDrawRequest(request) == ValidationResult::InvalidDrawRequest, "request rejects zero aspect");
    request = ValidRequest();
    request.zoom = -1.0f;
    Check(ValidateDrawRequest(request) == ValidationResult::InvalidDrawRequest, "request rejects negative zoom");

    ImageDescriptor image = ValidD3D11Image();
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::Valid, "d3d11 descriptor accepted");
    image.format = ImageFormat::Rgba8Typeless;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::Valid, "typeless rgba8 accepted");
    image.format = ImageFormat::Rgba8UnormSrgb;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::Valid, "srgb rgba8 accepted");
    image.format = ImageFormat::Rgba8Unorm;
    image.producerApi = ProducerApi::D3D12;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::Valid, "d3d12 descriptor accepted");
    image.sampleQuality = 1;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "d3d12 quality rejected");
    image.producerApi = ProducerApi::D3D11;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::Valid, "d3d11 quality acceptance preserved");
    image.width = 0;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "zero width rejected");
    image = ValidD3D11Image();
    image.height = 0;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "zero height rejected");
    image = ValidD3D11Image();
    image.arraySize = 2;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "array texture rejected");
    image = ValidD3D11Image();
    image.mipLevels = 2;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "multiple mips rejected");
    image = ValidD3D11Image();
    image.sampleCount = 2;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "multisampled image rejected");
    image = ValidD3D11Image();
    image.format = ImageFormat::Other;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "unsupported format rejected");
    image = ValidD3D11Image();
    image.dimension = ImageDimension::Unknown;
    Check(ValidateGpuImageDescriptor(image) == ValidationResult::InvalidImageDescriptor, "non-2d image rejected");

    Check(SupportsBridgePath(ProducerApi::D3D11, BridgeConsumerApi::D3D11), "d3d11 bridge path");
    Check(SupportsBridgePath(ProducerApi::D3D12, BridgeConsumerApi::D3D11), "d3d12 bridge path");
    Check(!SupportsBridgePath(ProducerApi::Vulkan, BridgeConsumerApi::D3D11), "vulkan unsupported");
    Check(!SupportsBridgePath(ProducerApi::Metal, BridgeConsumerApi::D3D11), "metal unsupported");
    Check(!SupportsBridgePath(ProducerApi::Unknown, BridgeConsumerApi::D3D11), "unknown producer unsupported");
    Check(!SupportsBridgePath(ProducerApi::D3D11, BridgeConsumerApi::Unknown), "unknown consumer unsupported");
    Check(ProducerApiFromUnityGraphicsApi(2) == ProducerApi::D3D11, "Unity D3D11 value preserved");
    Check(ProducerApiFromUnityGraphicsApi(18) == ProducerApi::D3D12, "Unity D3D12 value preserved");
    Check(ProducerApiFromUnityGraphicsApi(21) == ProducerApi::Vulkan, "Unity Vulkan identified");
    Check(ProducerApiFromUnityGraphicsApi(16) == ProducerApi::Metal, "Unity Metal identified");
    Check(ProducerApiFromUnityGraphicsApi(999) == ProducerApi::Unknown, "unknown Unity API identified");
    std::puts("portable_common_test pass");
    return 0;
}
