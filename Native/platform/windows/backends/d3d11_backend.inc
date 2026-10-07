static void SetRenderFault(WorkerContext& context, const char* message) {
    std::lock_guard<std::mutex> lock(context.mutex);
    context.renderFault = true;
    SetError(context, 9, message);
    context.status.state = LKG_WORKER_FAULT;
    context.closeRequested.store(true, std::memory_order_release);
    context.condition.notify_all();
}

static bool EnsureSharedSlots(WorkerContext& context, ID3D11Device* device, const D3D11_TEXTURE2D_DESC& source) {
    if (!IsValidRenderFormat(source)) {
        char message[256]{};
        std::snprintf(message, sizeof(message), "source texture unsupported width=%u height=%u mip=%u array=%u format=%u(%s) sampleCount=%u sampleQuality=%u",
                      source.Width, source.Height, source.MipLevels, source.ArraySize,
                      static_cast<unsigned>(source.Format), FormatName(source.Format),
                      source.SampleDesc.Count, source.SampleDesc.Quality);
        SetRenderFault(context, message);
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.textureWidth != 0 && (context.textureWidth != source.Width || context.textureHeight != source.Height || context.textureFormat != source.Format)) {
            context.renderFault = true;
            SetError(context, 10, "resize requires reopen");
            context.status.state = LKG_WORKER_FAULT;
            context.closeRequested.store(true, std::memory_order_release);
            context.condition.notify_all();
            return false;
        }
        if (context.textureWidth != 0 || context.slotsInitializing) return context.textureWidth != 0;
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY) return false;
        context.slotsInitializing = true;
    }
    D3D11_TEXTURE2D_DESC desc = source;
    desc.Format = SharedRenderFormat(source.Format);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.CPUAccessFlags = 0;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
    WorkerContext::Slot temporary[3]{};
    for (WorkerContext::Slot& slot : temporary) {
        ComPtr<IDXGIResource> resource;
        if (FAILED(device->CreateTexture2D(&desc, nullptr, &slot.producerTexture)) ||
            FAILED(slot.producerTexture.As(&slot.producerMutex)) ||
            FAILED(slot.producerTexture.As(&resource)) ||
            FAILED(resource->GetSharedHandle(&slot.sharedHandle))) {
            {
                std::lock_guard<std::mutex> lock(context.mutex);
                context.slotsInitializing = false;
                context.condition.notify_all();
            }
            SetRenderFault(context, "shared texture creation failed");
            return false;
        }
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY || context.textureWidth != 0) {
            context.slotsInitializing = false;
            context.condition.notify_all();
            return context.textureWidth != 0;
        }
        for (uint32_t index = 0; index < 3; ++index) {
            context.slots[index].producerTexture = std::move(temporary[index].producerTexture);
            context.slots[index].producerMutex = std::move(temporary[index].producerMutex);
            context.slots[index].sharedHandle = temporary[index].sharedHandle;
            temporary[index].sharedHandle = nullptr;
        }
        context.textureWidth = source.Width;
        context.textureHeight = source.Height;
        context.textureFormat = source.Format;
        context.slotsInitializing = false;
    }
    return true;
}
