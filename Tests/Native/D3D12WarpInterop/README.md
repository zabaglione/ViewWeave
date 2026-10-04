# D3D12 to D3D11 WARP interop capability probe

This standalone Windows test checks whether the local WARP adapter supports the small shared-texture path used by the native bridge. It explicitly selects `IDXGIFactory4::EnumWarpAdapter`; it never selects a hardware adapter or silently falls back. It creates D3D12 and D3D11 devices for the same adapter LUID, shares three 32 by 32 `R8G8B8A8_UNORM` default-heap textures through temporary NT handles, fills them on a D3D12 queue, waits on a bounded D3D12 fence, and checks the top-left pixel of each D3D11 private-copy readback over three reuse rounds. Each texture receives a different color in each round to detect stale prior-round contents.

Configure and build with CMake and a Visual Studio 2022 C++ toolchain:

```powershell
cmake -S Tests/Native/D3D12WarpInterop -B .local/dx12-followup/warp/build -G "Visual Studio 17 2022" -A x64
cmake --build .local/dx12-followup/warp/build --config Release
& .local/dx12-followup/warp/build/Release/d3d12_warp_interop_probe.exe
```

The executable writes one ASCII JSON object to stdout. `PASS` proves only this small WARP capability path; it does not exercise the production bridge transport. `UNSUPPORTED` records the first unsupported stage and HRESULT. A timeout, bad pixel, or operational error is `FAIL`.
