#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdint>

using Microsoft::WRL::ComPtr;

static int Fail(const char* stage, HRESULT result) {
    std::printf("FAIL stage=%s hr=0x%08lx\n", stage, static_cast<unsigned long>(result));
    return 1;
}

int main() {
    ComPtr<IDXGIFactory6> factory;
    HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(hr)) return Fail("factory", hr);
    ComPtr<IDXGIAdapter1> adapter;
    for (UINT index = 0; factory->EnumAdapters1(index, &adapter) != DXGI_ERROR_NOT_FOUND; ++index) {
        DXGI_ADAPTER_DESC1 desc{};
        if (FAILED(adapter->GetDesc1(&desc))) continue;
        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0) break;
        adapter.Reset();
    }
    if (!adapter) return Fail("adapter", E_NOINTERFACE);

    ComPtr<ID3D12Device> d3d12;
    hr = D3D12CreateDevice(adapter.Get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&d3d12));
    if (FAILED(hr)) return Fail("d3d12-device", hr);
    ComPtr<ID3D11Device> d3d11;
    ComPtr<ID3D11DeviceContext> context11;
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    hr = D3D11CreateDevice(adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, flags, nullptr, 0,
                           D3D11_SDK_VERSION, &d3d11, nullptr, &context11);
    if (FAILED(hr)) return Fail("d3d11-device", hr);

    D3D12_COMMAND_QUEUE_DESC queueDesc{};
    queueDesc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ComPtr<ID3D12CommandQueue> queue;
    hr = d3d12->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue));
    if (FAILED(hr)) return Fail("queue", hr);
    ComPtr<ID3D12CommandAllocator> allocator;
    hr = d3d12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&allocator));
    if (FAILED(hr)) return Fail("allocator", hr);
    ComPtr<ID3D12GraphicsCommandList> list;
    hr = d3d12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, allocator.Get(), nullptr, IID_PPV_ARGS(&list));
    if (FAILED(hr)) return Fail("command-list", hr);

    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
    desc.Width = 1;
    desc.Height = 1;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    D3D12_HEAP_PROPERTIES heap{};
    heap.Type = D3D12_HEAP_TYPE_DEFAULT;
    D3D12_CLEAR_VALUE clear{};
    clear.Format = desc.Format;
    clear.Color[0] = 0.125f;
    clear.Color[1] = 0.25f;
    clear.Color[2] = 0.5f;
    clear.Color[3] = 1.0f;
    ComPtr<ID3D12Resource> resource;
    hr = d3d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_SHARED, &desc,
                                        D3D12_RESOURCE_STATE_RENDER_TARGET, &clear,
                                        IID_PPV_ARGS(&resource));
    if (FAILED(hr)) return Fail("shared-resource", hr);
    ComPtr<ID3D12DescriptorHeap> rtvHeap;
    D3D12_DESCRIPTOR_HEAP_DESC heapDesc{};
    heapDesc.NumDescriptors = 1;
    heapDesc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    hr = d3d12->CreateDescriptorHeap(&heapDesc, IID_PPV_ARGS(&rtvHeap));
    if (FAILED(hr)) return Fail("rtv-heap", hr);
    D3D12_CPU_DESCRIPTOR_HANDLE rtv = rtvHeap->GetCPUDescriptorHandleForHeapStart();
    d3d12->CreateRenderTargetView(resource.Get(), nullptr, rtv);
    const float color[4] = { 0.125f, 0.25f, 0.5f, 1.0f };
    list->ClearRenderTargetView(rtv, color, 0, nullptr);
    D3D12_RESOURCE_BARRIER barrier{};
    barrier.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
    barrier.Transition.pResource = resource.Get();
    barrier.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
    barrier.Transition.StateAfter = D3D12_RESOURCE_STATE_COMMON;
    barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
    list->ResourceBarrier(1, &barrier);
    hr = list->Close();
    if (FAILED(hr)) return Fail("command-close", hr);
    ID3D12CommandList* lists[] = { list.Get() };
    queue->ExecuteCommandLists(1, lists);
    ComPtr<ID3D12Fence> fence;
    hr = d3d12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(&fence));
    if (FAILED(hr)) return Fail("fence", hr);
    HANDLE fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!fenceEvent) return Fail("fence-event", HRESULT_FROM_WIN32(GetLastError()));
    const UINT64 fenceValue = 1;
    hr = queue->Signal(fence.Get(), fenceValue);
    if (FAILED(hr)) { CloseHandle(fenceEvent); return Fail("fence-signal", hr); }
    if (fence->GetCompletedValue() < fenceValue) {
        hr = fence->SetEventOnCompletion(fenceValue, fenceEvent);
        if (FAILED(hr)) { CloseHandle(fenceEvent); return Fail("fence-wait-setup", hr); }
        if (WaitForSingleObject(fenceEvent, 5000) != WAIT_OBJECT_0) { CloseHandle(fenceEvent); return Fail("fence-wait", DXGI_ERROR_WAS_STILL_DRAWING); }
    }
    CloseHandle(fenceEvent);

    HANDLE sharedHandle = nullptr;
    hr = d3d12->CreateSharedHandle(resource.Get(), nullptr, GENERIC_ALL, nullptr, &sharedHandle);
    if (FAILED(hr)) return Fail("create-nt-handle", hr);
    ComPtr<ID3D11Texture2D> texture11;
    ComPtr<ID3D11Device1> d3d11_1;
    hr = d3d11.As(&d3d11_1);
    if (SUCCEEDED(hr)) hr = d3d11_1->OpenSharedResource1(sharedHandle, IID_PPV_ARGS(&texture11));
    CloseHandle(sharedHandle);
    if (FAILED(hr)) return Fail("open-shared-resource", hr);
    D3D11_TEXTURE2D_DESC stagingDesc{};
    texture11->GetDesc(&stagingDesc);
    stagingDesc.Usage = D3D11_USAGE_STAGING;
    stagingDesc.BindFlags = 0;
    stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    stagingDesc.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    hr = d3d11->CreateTexture2D(&stagingDesc, nullptr, &staging);
    if (FAILED(hr)) return Fail("staging", hr);
    context11->CopyResource(staging.Get(), texture11.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = context11->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr)) return Fail("map", hr);
    const auto* pixel = static_cast<const uint8_t*>(mapped.pData);
    const uint8_t red = pixel[0];
    const uint8_t green = pixel[1];
    const uint8_t blue = pixel[2];
    const uint8_t alpha = pixel[3];
    const bool match = red >= 31 && red <= 33 && green >= 63 && green <= 65 && blue >= 127 && blue <= 129 && alpha == 255;
    context11->Unmap(staging.Get(), 0);
    if (!match) {
        std::printf("FAIL stage=readback rgba=%u,%u,%u,%u\n", red, green, blue, alpha);
        return 1;
    }
    std::printf("PASS d3d12_to_d3d11_shared_rgba=%u,%u,%u,%u expected=32,64,128,255 tolerance=1\n", red, green, blue, alpha);
    return 0;
}
