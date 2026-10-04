# D3D12 Bridge backend plan

The production Bridge worker remains D3D11-only. `BridgeWorkerQuiltOutput` rejects non-D3D11 devices, and the native transport requires an `ID3D11Device`, D3D11 shared textures with keyed mutexes, and D3D11 plugin render events.

Unity 6 PluginAPI provides D3D12 device and command-list access through `IUnityGraphicsD3D12.h`, including versioned interfaces and command-list execution callbacks. That header is sufficient to obtain the Unity D3D12 device context, but it does not provide a Bridge-specific D3D12 resource contract.

A future backend needs the following bounded synchronization contract:

- Create a D3D12 texture with `D3D12_HEAP_FLAG_SHARED` and an NT shared handle.
- Create a shared D3D12 fence, signal it after the render command list, and wait with a bounded event timeout.
- Transition the resource to `COMMON` before another API opens it.
- Open the NT resource handle from D3D11 only when the adapter identity matches.
- Keep the fence and resource handles alive until all queued work has completed; close NT handles exactly once.
- Report device removal, fence timeout, and adapter mismatch as worker faults without releasing in-flight resources from the render thread.

The contract is bidirectional. Unity must publish producer ownership and resource state before a worker consumes a slot, and the worker must publish completion ownership before Unity reuses that slot. A ring slot needs explicit `free`, `ready`, and `inflight` states plus monotonically increasing fence values. The design must cover resource state notifications and queue ownership through the versioned Unity D3D12 interfaces, including the `ExecuteCommandList` and `UnityGraphicsD3D12ResourceState` declarations in the installed PluginAPI header. A production implementation must avoid blocking the render thread on a CPU wait; the bounded CPU wait in the probe is prototype-only.

`Native/tests/d3d12_d3d11_shared_probe.cpp` validates only one isolated resource-sharing path. It clears a known RGBA8 color in D3D12, performs a bounded CPU fence wait, opens the resource through D3D11, and checks staging readback. It does not validate bidirectional synchronization, ring-slot reuse, queue ownership, or resource-state notifications, and it does not enable or represent a production D3D12 Bridge backend.
