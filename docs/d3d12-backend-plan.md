# D3D12 Bridge worker implementation and validation

## Production route

The standard `BridgeWorkerQuiltOutput` route accepts Windows D3D11 and D3D12 Unity graphics devices in Editor Play and Windows Player. With D3D12, Unity renders the Quilt on its D3D12 device. The native worker opens a shared resource on a D3D11 device from the same GPU adapter and sends the rendered views through the installed Looking Glass Bridge runtime. The production path shares GPU resources and does not read image pixels back to the CPU.

`BridgeQuiltOutput` / Legacy Direct is a separate route and remains D3D11-only. Edit Mode, macOS, Vulkan, and other native output APIs are outside this support boundary.

The worker uses a three-slot GPU ring with slot/token ownership and resource-state transitions. Unity-side completion is checked by polling Unity frame fences; D3D11 worker completion is tracked with EVENT queries. The implementation does not use a cross-API shared fence. Resource references remain alive until their GPU work is known to be complete. Timeout, late completion, device removal, and uncertain submission state are handled as worker faults without releasing live GPU references prematurely.

If native shutdown cannot establish that all queued work has retired, Session recovery remains pending. Restart the Unity Editor or Player before trying again. This describes an exceptional recovery condition; it is not part of normal setup.

## Validation recorded

- Native worker validation passed 349 draw cases on an NVIDIA RTX 3090 Ti. It includes the D3D11 baseline and D3D12 GPU sharing, ring reuse, typeless formats, timeout, late completion, device-removal seam, and generation cases.
- Native Bridge loader lifecycle passed 100 cycles using the fake Bridge runtime (1/1); this is not a 100-cycle run against the installed Looking Glass Bridge runtime.
- URP and HDRP Editor Play both passed with Domain Reload enabled and disabled. Each configuration completed two cycles across 48 views, Preview -> Native -> Preview quality, clean Idle/Closed stops, pending 0, and empty errors. URP used Preview 1920x1080 and Native 7680x4320; HDRP Domain Reload enabled produced completion deltas 4/4/4 and red-dominant pixel counts 1736/27859/1736. GRD was not toggled as part of this D3D12 matrix.
- URP and HDRP Windows Development Players passed on actual `Direct3D12` Feature Level 12.2: `isEditor=false`, multithreaded rendering, 48 views, Preview 1920x1080, clean Idle/Closed stops with pending 0, and natural process exit code 0. URP produced 120 completions from 121 submissions and a 73063-pixel movement difference; HDRP produced 120/120 with zero drops and a 67005-pixel movement difference. The HDRP run emitted one `HDRuntimeReflectionSystem` assigned-more-than-once warning without a runtime error. Native Player quality was not tested.
- The user's HDRP VFX Learning Templates project was revalidated with package Git revision `8adda27c18a2eeda04b7f16617fa0623e32c2439`, retaining HDRP 17.7.0 and the VFX Graph 17.6 sample. A fresh Unity 6000.6.4f1 Editor ran on `Direct3D12` with the saved Rig under `VFX_HDRP_Camera`, Native quality, and 48 views at 7680x4320. The Bridge worker reached Ready and Session Rendering; completion advanced from 93 to 252 during the final Play, with no drops or errors. Stop reached Idle/Closed with pending 0, then Play resumed. Standalone Windows graphics APIs are ordered D3D12 then D3D11. This verifies the project/runtime integration; physical image quality across all views still needs human review.

The Unity results are targeted fixtures and project/runtime checks. They are not an all-view human optical review.

## Remaining validation

- Four Windows Development Player runs: URP/HDRP x GRD on/off, each exercising Preview/Balanced/Native.
- Native-quality 600-second URP and HDRP representative soaks, with memory observations reported as trends and no pass threshold assumed.
- Ten Stop/Start iterations per URP and HDRP fixture, with per-iteration clean pending state.
- Non-Development URP/HDRP Player runs on actual D3D12 with profile and cleanup evidence.
- Editor lifecycle warning trace and production Worker operation on software WARP.
- Full visual review across all 48 views and viewing regions, hotplug, and multi-device behavior.

The D3D12 checks above are pending; acceptance evidence and the result table are maintained in [Windows validation](windows-validation.md#pending-d3d12-acceptance-matrix). No result is inferred from a planned run.

CI does not have D3D12 hardware and explicitly skips hardware D3D12 tests. A standalone probe passed 9 D3D12-to-D3D11 shared-resource transfers on Microsoft Basic Render Driver, using three ring textures over three rounds and checking a slot/round-specific top-left sentinel. This capability probe is separate from production Worker or official Bridge support, which remains unverified on WARP.
