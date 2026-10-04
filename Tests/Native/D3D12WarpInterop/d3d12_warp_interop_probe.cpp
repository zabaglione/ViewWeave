#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include <wrl/client.h>

#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace {

constexpr UINT kWidth = 32;
constexpr UINT kHeight = 32;
constexpr size_t kRingSize = 3;
constexpr DWORD kWaitMilliseconds = 5000;

enum class Verdict { Pass, Unsupported, Fail };

struct Result {
    Verdict verdict = Verdict::Fail;
    const char* stage = "initialization";
    HRESULT hr = E_FAIL;
};

const char* VerdictName(Verdict verdict) {
    switch (verdict) {
    case Verdict::Pass: return "PASS";
    case Verdict::Unsupported: return "UNSUPPORTED";
    default: return "FAIL";
    }
}

std::string AsciiAdapterName(const wchar_t* name) {
    std::string result;
    for (size_t i = 0; name[i] != L'\0'; ++i) {
        const wchar_t ch = name[i];
        result.push_back(ch >= 0x20 && ch <= 0x7e ? static_cast<char>(ch) : '?');
    }
    return result;
}

struct SharedTexture {
    ComPtr<ID3D12Resource> d3d12;
    ComPtr<ID3D11Texture2D> imported;
    HANDLE handle = nullptr;
    std::array<uint8_t, 4> expected{};

    ~SharedTexture() {
        imported.Reset();
        d3d12.Reset();
        if (handle != nullptr) CloseHandle(handle);
    }
};

struct EventHandle {
    HANDLE value = nullptr;
    ~EventHandle() { if (value != nullptr) CloseHandle(value); }
};

Result Unsupported(const char* stage, HRESULT hr) {
    return { Verdict::Unsupported, stage, hr };
}

Result Failed(const char* stage, HRESULT hr) {
    return { Verdict::Fail, stage, hr };
}

std::array<uint8_t, 4> ExpectedColor(size_t slot, unsigned round) {
    return {
        static_cast<uint8_t>(41 + slot * 37 + round * 11),
        static_cast<uint8_t>(53 + slot * 19 + round * 31),
        static_cast<uint8_t>(79 + slot * 13 + round * 43),
        255
    };
}

bool WaitForFence(ID3D12Fence* fence, UINT64 value, HANDLE eventHandle, HRESULT* error) {
    const UINT64 initialCompletedValue = fence->GetCompletedValue();
    if (initialCompletedValue == UINT64_MAX) {
        *error = DXGI_ERROR_DEVICE_REMOVED;
        return false;
    }
    if (initialCompletedValue >= value) return true;
    HRESULT hr = fence->SetEventOnCompletion(value, eventHandle);
    if (FAILED(hr)) {
        *error = hr;
        return false;
    }
    const DWORD waitResult = WaitForSingleObject(eventHandle, kWaitMilliseconds);
    if (waitResult == WAIT_OBJECT_0) {
        const UINT64 completedValue = fence->GetCompletedValue();
        if (completedValue == UINT64_MAX) {
            *error = DXGI_ERROR_DEVICE_REMOVED;
            return false;
        }
        if (completedValue >= value) return true;
        *error = DXGI_ERROR_WAS_STILL_DRAWING;
        return false;
    }
    *error = waitResult == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : DXGI_ERROR_WAS_STILL_DRAWING;
    return false;
}

Result RunProbe(std::string* adapterName, LUID* adapterLuid, bool* softwareAdapter) {
    ComPtr<IDXGIFactory4> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return Unsupported("factory", hr);

    ComPtr<IDXGIAdapter> warpAdapter;
    hr = factory->EnumWarpAdapter(IID_PPV_ARGS(&warpAdapter));
    if (FAILED(hr)) return Unsupported("enum-warp-adapter", hr);

    ComPtr<IDXGIAdapter1> adapter;
    hr = warpAdapter.As(&adapter);
    if (FAILED(hr)) return Unsupported("warp-adapter-query", hr);
    DXGI_ADAPTER_DESC1 adapterDesc{};
    hr = adapter->GetDesc1(&adapterDesc);
    if (FAILED(hr)) return Unsupported("warp-adapter-description", hr);
    *adapterName = AsciiAdapterName(adapterDesc.Description);
    *adapterLuid = adapterDesc.AdapterLuid;
    *softwareAdapter = (adapterDesc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
    if (!*softwareAdapter) return Failed("warp-adapter-not-software", E_UNEXPECTED);

    ComPtr<ID3D12Device> device12;
    hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&device12));
    if (FAILED(hr)) return Unsupported("d3d12-device", hr);

    ComPtr<ID3D11Device> device11;
    ComPtr<ID3D11DeviceContext> context11;
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
        &device11, nullptr, &context11);
    if (FAILED(hr)) return Unsupported("d3d11-same-adapter-device", hr);
    ComPtr<ID3D11Device1> device11_1;
    hr = device11.As(&device11_1);
    if (FAILED(hr)) return Unsupported("d3d11-device1", hr);

    D3D12_COMMAND_QUEUE_DESC queueDesc{};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> queue;
    hr = device12->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue));
    if (FAILED(hr)) return Unsupported("command-queue", hr);
    ComPtr<ID3D12CommandAllocator> allocator;
    hr = device12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
    if (FAILED(hr)) return Unsupported("command-allocator", hr);
    ComPtr<ID3D12GraphicsCommandList> commandList;
    hr = device12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr,
        IID_PPV_ARGS(&commandList));
    if (FAILED(hr)) return Unsupported("command-list", hr);
    hr = commandList->Close();
    if (FAILED(hr)) return Unsupported("command-list-initial-close", hr);

    D3D12_DESCRIPTOR_HEAP_DESC rtvHeapDesc{};
    rtvHeapDesc.NumDescriptors = static_cast<UINT>(kRingSize);
    rtvHeapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    hr = device12->CreateDescriptorHeap(&rtvHeapDesc, IID_PPV_ARGS(&rtvHeap));
    if (FAILED(hr)) return Unsupported("rtv-heap", hr);

    std::array<SharedTexture, kRingSize> ring;
    const UINT descriptorIncrement = device12->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
    for (size_t index = 0; index < kRingSize; ++index) {
        D3D12_RESOURCE_DESC textureDesc{};
        textureDesc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        textureDesc.Width = kWidth;
        textureDesc.Height = kHeight;
        textureDesc.DepthOrArraySize = 1;
        textureDesc.MipLevels = 1;
        textureDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        textureDesc.SampleDesc.Count = 1;
        textureDesc.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN;
        textureDesc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
        D3D12_HEAP_PROPERTIES heapProperties{};
        heapProperties.Type = D3D12_HEAP_TYPE_DEFAULT;
        hr = device12->CreateCommittedResource(&heapProperties, D3D12_HEAP_FLAG_SHARED,
            &textureDesc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(&ring[index].d3d12));
        if (FAILED(hr)) return Unsupported("shared-texture", hr);
        hr = device12->CreateSharedHandle(ring[index].d3d12.Get(), nullptr, GENERIC_ALL, nullptr,
            &ring[index].handle);
        if (FAILED(hr)) return Unsupported("create-nt-handle", hr);
        hr = device11_1->OpenSharedResource1(ring[index].handle, IID_PPV_ARGS(&ring[index].imported));
        if (FAILED(hr)) return Unsupported("open-shared-resource1", hr);
        device12->CreateRenderTargetView(ring[index].d3d12.Get(), nullptr, rtv);
        rtv.ptr += descriptorIncrement;
    }

    D3D11_TEXTURE2D_DESC copyDesc{};
    copyDesc.Width = kWidth;
    copyDesc.Height = kHeight;
    copyDesc.MipLevels = 1;
    copyDesc.ArraySize = 1;
    copyDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    copyDesc.SampleDesc.Count = 1;
    copyDesc.Usage = D3D11_USAGE_DEFAULT;
    D3D11_TEXTURE2D_DESC stagingDesc = copyDesc;
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    std::array<ComPtr<ID3D11Texture2D>, kRingSize> privateCopies;
    std::array<ComPtr<ID3D11Texture2D>, kRingSize> stagingCopies;
    for (size_t index = 0; index < kRingSize; ++index) {
        hr = device11->CreateTexture2D(&copyDesc, nullptr, &privateCopies[index]);
        if (FAILED(hr)) return Unsupported("d3d11-private-copy", hr);
        hr = device11->CreateTexture2D(&stagingDesc, nullptr, &stagingCopies[index]);
        if (FAILED(hr)) return Unsupported("d3d11-test-readback", hr);
    }

    ComPtr<ID3D12Fence> fence;
    hr = device12->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
    if (FAILED(hr)) return Unsupported("d3d12-fence", hr);
    EventHandle fenceEvent{ CreateEventW(nullptr, FALSE, FALSE, nullptr) };
    if (fenceEvent.value == nullptr) return Failed("fence-event", HRESULT_FROM_WIN32(GetLastError()));
    ComPtr<ID3D11Query> query;
    D3D11_QUERY_DESC queryDesc{ D3D11_QUERY_EVENT, 0 };
    hr = device11->CreateQuery(&queryDesc, &query);
    if (FAILED(hr)) return Unsupported("d3d11-copy-query", hr);

    UINT64 fenceValue = 0;
    for (unsigned round = 0; round < 3; ++round) {
        hr = allocator->Reset();
        if (FAILED(hr)) return Failed("allocator-reset", hr);
        hr = commandList->Reset(allocator.Get(), nullptr);
        if (FAILED(hr)) return Failed("command-list-reset", hr);
        for (size_t index = 0; index < kRingSize; ++index) {
            ring[index].expected = ExpectedColor(index, round);
            D3D12_RESOURCE_BARRIER toRenderTarget{};
            toRenderTarget.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            toRenderTarget.Transition.pResource = ring[index].d3d12.Get();
            toRenderTarget.Transition.StateBefore = D3D12_RESOURCE_STATE_COMMON;
            toRenderTarget.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
            toRenderTarget.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            commandList->ResourceBarrier(1, &toRenderTarget);
            const auto& color = ring[index].expected;
            const float rgba[4] = {
                static_cast<float>(color[0]) / 255.0f,
                static_cast<float>(color[1]) / 255.0f,
                static_cast<float>(color[2]) / 255.0f,
                1.0f
            };
            D3D12_CPU_DESCRIPTOR_HANDLE target = rtvHeap->GetCPUDescriptorHandleForHeapStart();
            target.ptr += descriptorIncrement * index;
            commandList->ClearRenderTargetView(target, rgba, 0, nullptr);
            D3D12_RESOURCE_BARRIER toCommon{};
            toCommon.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
            toCommon.Transition.pResource = ring[index].d3d12.Get();
            toCommon.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
            toCommon.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
            toCommon.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            commandList->ResourceBarrier(1, &toCommon);
        }
        hr = commandList->Close();
        if (FAILED(hr)) return Failed("command-list-close", hr);
        ID3D12CommandList* lists[] = { commandList.Get() };
        queue->ExecuteCommandLists(1, lists);
        hr = queue->Signal(fence.Get(), ++fenceValue);
        if (FAILED(hr)) return Failed("fence-signal", hr);
        if (!WaitForFence(fence.Get(), fenceValue, fenceEvent.value, &hr)) return Failed("fence-wait", hr);

        for (size_t index = 0; index < kRingSize; ++index) {
            context11->CopyResource(privateCopies[index].Get(), ring[index].imported.Get());
            context11->CopyResource(stagingCopies[index].Get(), privateCopies[index].Get());
        }
        context11->End(query.Get());
        context11->Flush();
        BOOL complete = FALSE;
        const ULONGLONG start = GetTickCount64();
        while (!complete && GetTickCount64() - start < kWaitMilliseconds) {
            hr = context11->GetData(query.Get(), &complete, sizeof(complete), 0);
            if (FAILED(hr)) return Failed("d3d11-copy-query", hr);
            if (!complete) Sleep(1);
        }
        if (!complete) return Failed("d3d11-copy-timeout", DXGI_ERROR_WAS_STILL_DRAWING);

        for (size_t index = 0; index < kRingSize; ++index) {
            D3D11_MAPPED_SUBRESOURCE mapped{};
            hr = context11->Map(stagingCopies[index].Get(), 0, D3D11_MAP_READ, 0, &mapped);
            if (FAILED(hr)) return Failed("d3d11-readback-map", hr);
            const auto* pixel = static_cast<const uint8_t*>(mapped.pData);
            const std::array<uint8_t, 4> actual{ pixel[0], pixel[1], pixel[2], pixel[3] };
            context11->Unmap(stagingCopies[index].Get(), 0);
            if (actual != ring[index].expected) return Failed("pixel-mismatch", E_FAIL);
        }
    }
    return { Verdict::Pass, "complete", S_OK };
}

void PrintResult(const Result& result, const std::string& adapterName, const LUID& luid, bool software) {
    std::printf("{\"verdict\":\"%s\",\"stage\":\"%s\",\"hresult\":\"0x%08lX\","
        "\"adapter_name\":\"%s\",\"adapter_luid\":\"%08lX:%08lX\",\"software\":%s,"
        "\"width\":%u,\"height\":%u,\"ring_textures\":%zu,\"reuse_rounds\":3}\n",
        VerdictName(result.verdict), result.stage, static_cast<unsigned long>(result.hr), adapterName.c_str(),
        static_cast<unsigned long>(luid.HighPart), static_cast<unsigned long>(luid.LowPart),
        software ? "true" : "false", kWidth, kHeight, kRingSize);
}

} // namespace

int main() {
    std::string adapterName;
    LUID luid{};
    bool softwareAdapter = false;
    Result result = RunProbe(&adapterName, &luid, &softwareAdapter);
    PrintResult(result, adapterName, luid, softwareAdapter);
    return result.verdict == Verdict::Pass ? 0 : (result.verdict == Verdict::Unsupported ? 2 : 1);
}
