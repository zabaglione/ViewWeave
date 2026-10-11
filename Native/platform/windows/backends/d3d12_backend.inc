static bool IsValidD3D12RenderFormat(const D3D12_RESOURCE_DESC& desc) {
    lkg::common::ImageDescriptor image{};
    image.width = desc.Width;
    image.height = desc.Height;
    image.arraySize = desc.DepthOrArraySize;
    image.mipLevels = desc.MipLevels;
    image.sampleCount = desc.SampleDesc.Count;
    image.sampleQuality = desc.SampleDesc.Quality;
    image.dimension = desc.Dimension == D3D12_RESOURCE_DIMENSION_TEXTURE2D
        ? lkg::common::ImageDimension::Texture2D
        : lkg::common::ImageDimension::Unknown;
    image.format = ToCommonImageFormat(desc.Format);
    image.producerApi = lkg::common::ProducerApi::D3D12;
    return lkg::common::ValidateGpuImageDescriptor(image) == lkg::common::ValidationResult::Valid;
}

static void TraceD3D12SourceDesc(const D3D12_RESOURCE_DESC& desc, ID3D12Device* device, uint64_t generation) {
    const char* path = std::getenv("LKG_NATIVE_TRACE");
    if (!path || !*path) return;
    FILE* file = nullptr;
    if (fopen_s(&file, path, "a") != 0 || !file) return;
    std::fprintf(file, "d3d12-source-desc width=%llu height=%u format=%u mip=%u array=%u sampleCount=%u sampleQuality=%u flags=%u device=%p generation=%llu\n",
                 static_cast<unsigned long long>(desc.Width), desc.Height, static_cast<unsigned>(desc.Format),
                 desc.MipLevels, desc.DepthOrArraySize, desc.SampleDesc.Count, desc.SampleDesc.Quality,
                 static_cast<unsigned>(desc.Flags), device, static_cast<unsigned long long>(generation));
    std::fclose(file);
}

static bool EnsureD3D12Slots(WorkerContext& context, ID3D12Device* device, const D3D12_RESOURCE_DESC& source, uint64_t generation) {
    if (!IsValidD3D12RenderFormat(source)) {
        char message[256]{};
        std::snprintf(message, sizeof(message), "D3D12 source unsupported width=%llu height=%u format=%u mip=%u array=%u sampleCount=%u device=%p",
                      static_cast<unsigned long long>(source.Width), source.Height, static_cast<unsigned>(source.Format),
                      source.MipLevels, source.DepthOrArraySize, source.SampleDesc.Count, device);
        SetRenderFault(context, message);
        return false;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.textureWidth != 0 && (context.textureWidth != source.Width || context.textureHeight != source.Height ||
            context.textureFormat != source.Format || context.slots[0].generation != generation)) {
            context.renderFault = true;
            SetError(context, 10, "D3D12 source resize or device generation change requires reopen");
            context.status.state = LKG_WORKER_FAULT;
            context.closeRequested.store(true, std::memory_order_release);
            context.condition.notify_all();
            return false;
        }
        if (context.textureWidth != 0 || context.slotsInitializing) return context.textureWidth != 0;
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY) return false;
        context.slotsInitializing = true;
    }
    WorkerContext::Slot temporary[3]{};
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    heap.CreationNodeMask = 1;
    heap.VisibleNodeMask = 1;
    D3D12_RESOURCE_DESC sharedDesc = source;
    sharedDesc.Format = SharedRenderFormat(source.Format);
    sharedDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
    sharedDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    for (WorkerContext::Slot& slot : temporary) {
        if (FAILED(device->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &sharedDesc,
                D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&slot.sharedTexture12))) ||
            FAILED(device->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&slot.allocator12))) ||
            FAILED(device->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, slot.allocator12.Get(), nullptr,
                IID_PPV_ARGS(&slot.commandList12)))) {
            {
                std::lock_guard<std::mutex> lock(context.mutex);
                context.slotsInitializing = false;
                context.condition.notify_all();
            }
            for (WorkerContext::Slot& temporarySlot : temporary) ClearD3D12Slot(temporarySlot);
            SetRenderFault(context, "D3D12 shared slot creation failed");
            return false;
        }
        if (FAILED(slot.commandList12->Close()) ||
            FAILED(device->CreateSharedHandle(slot.sharedTexture12.Get(), nullptr, GENERIC_ALL, nullptr, &slot.sharedHandle))) {
            {
                std::lock_guard<std::mutex> lock(context.mutex);
                context.slotsInitializing = false;
                context.condition.notify_all();
            }
            for (WorkerContext::Slot& temporarySlot : temporary) ClearD3D12Slot(temporarySlot);
            SetRenderFault(context, "D3D12 shared handle creation failed");
            return false;
        }
        slot.d3d12Slot = true;
        slot.generation = generation;
    }
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.closeRequested.load(std::memory_order_acquire) || context.status.state != LKG_WORKER_READY || context.textureWidth != 0) {
            context.slotsInitializing = false;
            context.condition.notify_all();
            for (WorkerContext::Slot& temporarySlot : temporary) ClearD3D12Slot(temporarySlot);
            return context.textureWidth != 0;
        }
        for (uint32_t index = 0; index < 3; ++index) {
            context.slots[index] = std::move(temporary[index]);
            temporary[index].sharedHandle = nullptr;
        }
        context.textureWidth = static_cast<uint32_t>(source.Width);
        context.textureHeight = source.Height;
        context.textureFormat = source.Format;
        context.slotsInitializing = false;
    }
    return true;
}


static void WorkerRenderEventD3D12(WorkerContext& context, WorkerDrawData& data, IUnknown* texture) {
    auto abandon = [&]() {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.pendingTokens) --context.pendingTokens;
        ++context.status.dropped;
        context.condition.notify_all();
    };
    ComPtr<ID3D12Resource> source;
    if (texture->QueryInterface(IID_PPV_ARGS(&source)) != S_OK) {
        SetRenderFault(context, "source texture is not a D3D12 resource");
        abandon();
        return;
    }
    const D3D12_RESOURCE_DESC sourceDesc = source->GetDesc();
    ComPtr<ID3D12Device> sourceDevice;
    if (source->GetDevice(IID_PPV_ARGS(&sourceDevice)) != S_OK) {
        SetRenderFault(context, "source D3D12 device unavailable");
        abandon();
        return;
    }
    LkgD3D12Snapshot snapshot{};
    if (lkg_bridge_d3d12_snapshot(&snapshot) != 0 || !snapshot.device || !snapshot.frameFence) {
        SetRenderFault(context, "Unity D3D12 snapshot unavailable");
        abandon();
        return;
    }
    TraceD3D12SourceDesc(sourceDesc, sourceDevice.Get(), snapshot.generation);
    auto releaseSnapshot = [&]() { lkg_bridge_d3d12_snapshot_release(&snapshot); };
    ComPtr<IUnknown> snapshotIdentity;
    ComPtr<IUnknown> sourceIdentity;
    if (snapshot.generation != context.deviceGeneration || snapshot.generation != lkg_bridge_unity_device_generation() ||
        snapshot.device->QueryInterface(IID_PPV_ARGS(&snapshotIdentity)) != S_OK ||
        sourceDevice->QueryInterface(IID_PPV_ARGS(&sourceIdentity)) != S_OK ||
        snapshotIdentity.Get() != sourceIdentity.Get()) {
        releaseSnapshot();
        SetRenderFault(context, "source D3D12 device or generation does not match Unity");
        abandon();
        return;
    }
    if (!EnsureD3D12Slots(context, snapshot.device, sourceDesc, snapshot.generation)) {
        releaseSnapshot();
        abandon();
        return;
    }
    uint32_t selectedSlot = 3;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        if (context.status.state == LKG_WORKER_READY && !context.closeRequested.load(std::memory_order_acquire)) {
            for (uint32_t index = 0; index < 3; ++index) {
                if (!context.slots[index].inUse && !context.slots[index].quarantined &&
                    !context.slots[index].sourceRetirementUnproven && !context.slots[index].sourceMutexHeld) {
                    selectedSlot = index;
                    context.slots[index].inUse = true;
                    break;
                }
            }
        }
    }
    if (selectedSlot == 3) {
        releaseSnapshot();
        abandon();
        return;
    }
    WorkerContext::Slot& slot = context.slots[selectedSlot];
    if (FAILED(slot.allocator12->Reset()) || FAILED(slot.commandList12->Reset(slot.allocator12.Get(), nullptr))) {
        { std::lock_guard<std::mutex> lock(context.mutex); slot.inUse = false; }
        releaseSnapshot();
        SetRenderFault(context, "D3D12 command allocator reset failed");
        abandon();
        return;
    }
    D3D12_RESOURCE_BARRIER barriers[2]{};
    barriers[0].Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barriers[0].Transition.pResource = slot.sharedTexture12.Get();
    barriers[0].Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_DEST;
    slot.commandList12->ResourceBarrier(1, barriers);
    slot.commandList12->CopyResource(slot.sharedTexture12.Get(), source.Get());
    barriers[0].Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_DEST;
    barriers[0].Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    slot.commandList12->ResourceBarrier(1, barriers);
    if (FAILED(slot.commandList12->Close())) {
        { std::lock_guard<std::mutex> lock(context.mutex); slot.inUse = false; }
        releaseSnapshot();
        SetRenderFault(context, "D3D12 copy command list close failed");
        abandon();
        return;
    }
    UINT64 fenceValue = 0;
    bool submissionAllowed = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        submissionAllowed = context.status.state == LKG_WORKER_READY && !context.closeRequested.load(std::memory_order_acquire);
        if (submissionAllowed) fenceValue = lkg_bridge_d3d12_submit(slot.commandList12.Get(), source.Get());
    }
    if (!submissionAllowed) {
        { std::lock_guard<std::mutex> lock(context.mutex); slot.inUse = false; }
        releaseSnapshot();
        abandon();
        return;
    }
    if (fenceValue == 0 || fenceValue == UINT64_MAX) {
        slot.sourceTexture12 = source;
        slot.producerFence12 = snapshot.frameFence;
        slot.producerDevice12 = snapshot.device;
        slot.producerFenceValue12 = fenceValue;
        slot.unknownSubmission = true;
        slot.submittedAtTick = GetTickCount64();
        releaseSnapshot();
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            EnqueueJob(context, WorkerContext::Job{ selectedSlot, data.columns, data.rows, data.aspect,
                data.zoom, data.sourceEncoding, data.sourceWasTypeless });
            ++context.status.submitted;
            context.condition.notify_one();
        }
        SetRenderFault(context, "D3D12 submission returned an invalid fence value; submission state unknown");
        return;
    }
    slot.sourceTexture12 = source;
    slot.producerFence12 = snapshot.frameFence;
    slot.producerDevice12 = snapshot.device;
    slot.producerFenceValue12 = fenceValue;
    slot.unknownSubmission = false;
    slot.submittedAtTick = GetTickCount64();
    slot.timedOut = false;
    releaseSnapshot();
    bool submitted = false;
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        WorkerContext::Job job{ selectedSlot, data.columns, data.rows, data.aspect, data.zoom,
            data.sourceEncoding, data.sourceWasTypeless };
        if (EnqueueJob(context, job)) {
            ++context.status.submitted;
            submitted = true;
            context.condition.notify_one();
        }
    }
    if (submitted) {
        TraceWorker("d3d12-render-submit");
        return;
    }
    SetRenderFault(context, "D3D12 submission could not enter worker queue");
}

static void D3D12WorkerJobDeferred(WorkerContext& context, const WorkerContext::Job& job) {
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        RequeueFront(context, job);
        context.inFlightJob = false;
        context.condition.notify_one();
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
}

static void D3D12WorkerJobQuarantine(WorkerContext& context, const WorkerContext::Job& job, const char* message) {
    WorkerContext::Slot& slot = context.slots[job.slot];
    if (!slot.quarantined) {
        slot.quarantined = true;
        SetFault(context, 21, message);
    }
    D3D12WorkerJobDeferred(context, job);
}

static void D3D12WorkerJobRetire(WorkerContext& context, uint32_t slotIndex, bool completed = false) {
    WorkerContext::Slot& slot = context.slots[slotIndex];
    if (!D3D12OwnershipRetired(slot)) {
        slot.quarantined = true;
        SetFault(context, 29, "D3D12 slot retirement refused before all GPU completion proofs");
        return;
    }
    slot.sourceTexture12.Reset();
    slot.producerFence12.Reset();
    slot.producerDevice12.Reset();
    slot.producerFenceValue12 = 0;
    slot.unknownSubmission = false;
    slot.timedOut = false;
    slot.consumerTimedOut = false;
    slot.consumerCopyPending = false;
    slot.sourceCopyRetirementQuery11.Reset();
    {
        std::lock_guard<std::mutex> lock(context.mutex);
        slot.inUse = false;
        if (context.pendingTokens) --context.pendingTokens;
        if (completed) ++context.status.completed;
        else ++context.status.dropped;
        context.inFlightJob = false;
        context.condition.notify_all();
    }
}

static void ProcessD3D12WorkerJob(WorkerContext& context, const WorkerContext::Job& job,
                                  ID3D11Device* workerDevice, ID3D11DeviceContext* workerContext, uint64_t session) {
    WorkerContext::Slot& slot = context.slots[job.slot];
    if (slot.unknownSubmission) {
        const UINT64 completed = D3D12FenceCompletedValue(slot.producerFence12.Get());
        const HRESULT removedReason = slot.producerDevice12 ? slot.producerDevice12->GetDeviceRemovedReason() : E_FAIL;
        if (completed == UINT64_MAX || FAILED(removedReason)) {
            if (!slot.quarantined) {
                slot.quarantined = true;
                SetFault(context, 21, "D3D12 unknown submission retired after confirmed device removal");
            }
            slot.sourceTexture12.Reset();
            slot.producerFence12.Reset();
            slot.producerDevice12.Reset();
            slot.producerFenceValue12 = 0;
            slot.unknownSubmission = false;
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        if (!slot.timedOut && GetTickCount64() - slot.submittedAtTick > context.d3d12FenceTimeoutMs.load(std::memory_order_acquire)) {
            slot.timedOut = true;
            D3D12WorkerJobQuarantine(context, job, "D3D12 submission state remains unknown; restart required");
            return;
        }
        D3D12WorkerJobDeferred(context, job);
        return;
    }
    if (!slot.consumerCopyPending) {
        if (!slot.producerFence12 || slot.producerFenceValue12 == 0 || slot.producerFenceValue12 == UINT64_MAX) {
            slot.unknownSubmission = true;
            D3D12WorkerJobQuarantine(context, job, "D3D12 producer fence value is invalid; submission state unknown");
            return;
        }
        const UINT64 completed = D3D12FenceCompletedValue(slot.producerFence12.Get());
        if (completed == UINT64_MAX) {
            if (!slot.quarantined) {
                slot.quarantined = true;
                SetFault(context, 21, "D3D12 producer fence reports device removal");
            }
            slot.sourceTexture12.Reset();
            slot.producerFence12.Reset();
            slot.producerDevice12.Reset();
            slot.producerFenceValue12 = 0;
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        if (completed < slot.producerFenceValue12) {
            if (GetTickCount64() - slot.submittedAtTick > context.d3d12FenceTimeoutMs.load(std::memory_order_acquire) && !slot.timedOut) {
                slot.timedOut = true;
                SetFault(context, 22, "D3D12 producer fence timed out; slot quarantined");
            }
            D3D12WorkerJobDeferred(context, job);
            return;
        }
        if (slot.timedOut) {
            // The producer fence is now observed complete. Drop its ownership proof before
            // the guarded retirement path evaluates the slot predicates.
            slot.sourceTexture12.Reset();
            slot.producerFence12.Reset();
            slot.producerDevice12.Reset();
            slot.producerFenceValue12 = 0;
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        slot.sourceTexture12.Reset();
        slot.producerFence12.Reset();
        slot.producerDevice12.Reset();
        slot.producerFenceValue12 = 0;
    }
    if (!slot.workerTexture) {
        ComPtr<ID3D11Device1> workerDevice1;
        HRESULT openResult = slot.sharedHandle ? workerDevice->QueryInterface(IID_PPV_ARGS(&workerDevice1)) : E_HANDLE;
        if (SUCCEEDED(openResult)) openResult = workerDevice1->OpenSharedResource1(slot.sharedHandle, IID_PPV_ARGS(&slot.workerTexture));
        if (FAILED(openResult)) {
            char message[128]{};
            std::snprintf(message, sizeof(message), "D3D12 NT shared resource open failed hr=0x%08lx", static_cast<unsigned long>(openResult));
            SetFault(context, 23, message);
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        CloseHandle(slot.sharedHandle);
        slot.sharedHandle = nullptr;
        D3D11_TEXTURE2D_DESC workerDesc{};
        workerDesc.Width = context.textureWidth;
        workerDesc.Height = context.textureHeight;
        workerDesc.MipLevels = 1;
        workerDesc.ArraySize = 1;
        workerDesc.Format = SharedRenderFormat(context.textureFormat);
        workerDesc.SampleDesc.Count = 1;
        workerDesc.Usage = D3D11_USAGE_DEFAULT;
        workerDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        if (FAILED(workerDevice->CreateTexture2D(&workerDesc, nullptr, &slot.producerTexture))) {
            SetFault(context, 24, "D3D12 private D3D11 texture creation failed");
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        D3D11_QUERY_DESC queryDesc{ D3D11_QUERY_EVENT, 0 };
        if (FAILED(workerDevice->CreateQuery(&queryDesc, &slot.consumerQuery11))) {
            SetFault(context, 25, "D3D11 consumer query creation failed");
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
    }
    if (!slot.consumerCopyPending) {
        workerContext->CopyResource(slot.producerTexture.Get(), slot.workerTexture.Get());
        workerContext->End(slot.consumerQuery11.Get());
        workerContext->Flush();
        slot.consumerCopyPending = true;
        slot.consumerCopyStartedTick = GetTickCount64();
    }
    if (!slot.consumerTimedOut && GetTickCount64() - slot.consumerCopyStartedTick > context.d3d12FenceTimeoutMs.load(std::memory_order_acquire)) {
        slot.consumerTimedOut = true;
        D3D12WorkerJobQuarantine(context, job, "D3D11 consumer copy timed out; slot quarantined");
        return;
    }
#if defined(LKG_WORKER_TRANSPORT_TESTING)
    uint32_t injectedPolls = testD3D12ConsumerPendingPolls.load(std::memory_order_acquire);
    while (injectedPolls != 0 && !testD3D12ConsumerPendingPolls.compare_exchange_weak(injectedPolls, injectedPolls - 1, std::memory_order_acq_rel)) {}
    if (injectedPolls != 0) {
        D3D12WorkerJobDeferred(context, job);
        return;
    }
#endif
    BOOL consumerComplete = FALSE;
    const HRESULT queryResult = workerContext->GetData(slot.consumerQuery11.Get(), &consumerComplete, sizeof(consumerComplete), D3D11_ASYNC_GETDATA_DONOTFLUSH);
    if (queryResult == S_FALSE || (SUCCEEDED(queryResult) && !consumerComplete)) {
        D3D12WorkerJobDeferred(context, job);
        return;
    }
    if (FAILED(queryResult)) {
        const HRESULT removedReason = workerDevice ? workerDevice->GetDeviceRemovedReason() : E_FAIL;
        if (FAILED(removedReason)) {
            slot.consumerCopyPending = false;
            slot.quarantined = true;
            SetFault(context, 28, "D3D11 consumer device removal confirmed; slot retired");
            D3D12WorkerJobRetire(context, job.slot);
            return;
        }
        if (!slot.quarantined) {
            slot.quarantined = true;
            slot.consumerTimedOut = true;
            SetFault(context, 26, "D3D11 consumer query failed; waiting for safe retirement");
        }
        D3D12WorkerJobDeferred(context, job);
        return;
    }
    slot.consumerCopyPending = false;
    if (slot.consumerTimedOut || slot.quarantined) {
        D3D12WorkerJobRetire(context, job.slot);
        return;
    }
    int32_t drawResult = -1;
    bool sourceRetirementProven = true;
    try {
        drawResult = DrawWorkerImage(context, workerContext, workerDevice, session, slot.producerTexture.Get(), job,
            slot.sourceCopyRetirementQuery11, &sourceRetirementProven);
    } catch (...) {
        sourceRetirementProven = !slot.sourceRetirementUnproven;
        SetFault(context, 27, "D3D12 worker draw threw; source retirement is being verified");
    }
    if (!sourceRetirementProven) {
        slot.quarantined = true;
        slot.inUse = false;
        {
            std::lock_guard<std::mutex> lock(context.mutex);
            if (context.pendingTokens) --context.pendingTokens;
            ++context.status.dropped;
            context.inFlightJob = false;
            context.condition.notify_all();
        }
        return;
    }
    if (drawResult != 0) SetBridgeFault(context, 13, "bridge_draw", drawResult);
    D3D12WorkerJobRetire(context, job.slot, drawResult == 0);
    TraceWorker(drawResult == 0 ? "d3d12-draw-complete" : "d3d12-draw-drop");
}

static bool DrainD3D12Ownership(WorkerContext& context, ID3D11Device* workerDevice,
    ID3D11DeviceContext* workerContext, uint64_t timeoutMs) {
    const ULONGLONG deadline = GetTickCount64() + timeoutMs;
    for (WorkerContext::Slot& slot : context.slots) {
        if (!slot.d3d12Slot) continue;
        for (;;) {
            bool waiting = false;
            if (slot.unknownSubmission) {
                if (!slot.producerFence12 || !slot.producerDevice12) return false;
                const UINT64 completed = D3D12FenceCompletedValue(slot.producerFence12.Get());
                const HRESULT removedReason = slot.producerDevice12->GetDeviceRemovedReason();
                if (completed == UINT64_MAX || FAILED(removedReason)) {
                    // Preserve the existing unknown-submission rule: only confirmed device
                    // removal makes an invalid submission fence safe to retire.
                    slot.sourceTexture12.Reset();
                    slot.producerFence12.Reset();
                    slot.producerDevice12.Reset();
                    slot.producerFenceValue12 = 0;
                    slot.unknownSubmission = false;
                } else {
                    waiting = true;
                }
            } else if (slot.producerFenceValue12 != 0) {
                if (!slot.producerFence12 || !slot.producerDevice12) return false;
                const UINT64 completed = D3D12FenceCompletedValue(slot.producerFence12.Get());
                const HRESULT removedReason = slot.producerDevice12->GetDeviceRemovedReason();
                if (completed == UINT64_MAX || FAILED(removedReason)) {
                    slot.sourceTexture12.Reset();
                    slot.producerFence12.Reset();
                    slot.producerDevice12.Reset();
                    slot.producerFenceValue12 = 0;
                } else if (completed >= slot.producerFenceValue12) {
                    slot.sourceTexture12.Reset();
                    slot.producerFence12.Reset();
                    slot.producerDevice12.Reset();
                    slot.producerFenceValue12 = 0;
                } else {
                    waiting = true;
                }
            } else if (slot.sourceTexture12 || slot.producerFence12 || slot.producerDevice12) {
                // GPU ownership is present without a completion value to prove retirement.
                return false;
            }

            if (slot.consumerCopyPending) {
                if (!workerContext || !slot.consumerQuery11) return false;
                BOOL consumerComplete = FALSE;
                const HRESULT queryResult = workerContext->GetData(slot.consumerQuery11.Get(),
                    &consumerComplete, sizeof(consumerComplete), D3D11_ASYNC_GETDATA_DONOTFLUSH);
                if (queryResult == S_OK && consumerComplete) {
                    slot.consumerCopyPending = false;
                } else if (FAILED(queryResult) && workerDevice &&
                    FAILED(workerDevice->GetDeviceRemovedReason())) {
                    // The consumer device removal is the existing safe-retirement proof.
                    slot.consumerCopyPending = false;
                } else {
                    waiting = true;
                }
            }

            if (slot.unknownSubmission || slot.producerFenceValue12 != 0 || slot.consumerCopyPending) {
                waiting = true;
            }
            if (!waiting) break;
            if (GetTickCount64() >= deadline) return false;
            Sleep(1);
        }
    }
    for (const WorkerContext::Slot& slot : context.slots) {
        if (slot.d3d12Slot && (slot.inUse || slot.unknownSubmission || slot.producerFenceValue12 != 0 ||
            slot.sourceTexture12 || slot.producerFence12 || slot.producerDevice12 || slot.consumerCopyPending))
            return false;
    }
    return true;
}

static void ClearD3D12Slot(WorkerContext::Slot& slot) {
    if (slot.inUse || !D3D12OwnershipRetired(slot)) {
        slot.quarantined = true;
        return;
    }
    slot.sourceTexture12.Reset();
    slot.producerFence12.Reset();
    slot.producerDevice12.Reset();
    slot.sharedTexture12.Reset();
    slot.allocator12.Reset();
    slot.commandList12.Reset();
    slot.consumerQuery11.Reset();
    slot.sourceCopyRetirementQuery11.Reset();
    slot.workerTexture.Reset();
    slot.producerTexture.Reset();
    slot.producerMutex.Reset();
    slot.workerMutex.Reset();
    if (slot.sharedHandle) {
        CloseHandle(slot.sharedHandle);
        slot.sharedHandle = nullptr;
    }
    slot.producerFenceValue12 = 0;
    slot.unknownSubmission = false;
    slot.timedOut = false;
    slot.consumerTimedOut = false;
    slot.consumerCopyStartedTick = 0;
    slot.quarantined = false;
    slot.sourceRetirementUnproven = false;
    slot.sourceCopyCompleted = false;
    slot.sourceMutexHeld = false;
    slot.consumerCopyPending = false;
    slot.d3d12Slot = false;
    slot.generation = 0;
    slot.inUse = false;
}
