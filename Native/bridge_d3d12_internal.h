#pragma once

#if defined(_WIN32)
#include <stdint.h>
#include <d3d12.h>

struct LkgD3D12Snapshot {
    ID3D12Device* device;
    ID3D12Fence* frameFence;
    uint64_t generation;
};

extern "C" int32_t lkg_bridge_d3d12_snapshot(LkgD3D12Snapshot* snapshot);
extern "C" void lkg_bridge_d3d12_snapshot_release(LkgD3D12Snapshot* snapshot);
extern "C" uint64_t lkg_bridge_d3d12_submit(ID3D12GraphicsCommandList* list, ID3D12Resource* source);
extern "C" uint64_t lkg_bridge_unity_device_generation(void);
extern "C" int32_t lkg_bridge_get_worker_render_event_id(int32_t* eventId);
struct IUnknown;
extern "C" int32_t lkg_bridge_unity_device_snapshot(IUnknown** device, uint64_t* generation, uint32_t* graphicsApi);
#endif
