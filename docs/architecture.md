[日本語](ja/architecture.md)

# Architecture

> Audience: Unity, rendering, and package developers. This page covers the technical design, implementation, or procedure stated below. For user workflows, see the [User guide](user-guide.md); for the complete technical index, see the [Technical guide](technical-guide.md).

ViewWeave is an unofficial Unity SDK developed independently of the official SDK. It separates Core, URP and HDRP adapters, and Bridge output. Its supported targets are URP and HDRP validated on Unity 6000.6.4f1. Built-in is unsupported, and Core requires the Standard RenderRequest path.

## Current data path

The diagram shows the regular Windows URP/HDRP Bridge Worker path. The adapter prepares render state, then Core issues StandardRequests and transfers tiles on the GPU. The Metal experiment and file playback use separate paths.

```mermaid
flowchart TB
    Camera["User Camera"] --> Core["Core controls views"]
    Core --> Adapter["URP/HDRP adapter prepares render state"]
    Adapter --> Request["Core issues one StandardRequest per view"]
    Request --> Copy["Core copies to GPU tiles with CopyTexture"]
    Copy --> Quilt["Completed Quilt texture"]
    Quilt --> Output["IQuiltOutput"]
    Output --> Worker["Bridge Worker"]
    Worker --> Bridge["Official Bridge runtime"]
    Bridge --> Display["Looking Glass"]
```

The adapter prepares view rendering before the Quilt is complete; `IQuiltOutput` passes the completed Quilt to the output path. The Bridge Worker sends it to the official Bridge runtime over GPU sharing. Core performs one render request per view.

Core computes each logical view's translation and off-axis frustum, issues one render request per rendered view, and transfers each result to a GPU Quilt tile with `Graphics.CopyTexture`. HDRP's default `IsolatedViewHistories` mode owns one Camera and `HDAdditionalCameraData` per rendered logical view, copies HDRP camera settings from the Reference Camera, and sets `hasPersistentHistory = true` on each view camera. This isolates HDRP history but does not reduce view renders, culling operations, shadows, or post-processing passes. Quilt images stay on the GPU; HDRP exposure metering separately reads back one float asynchronously.

`IQuiltOutput` passes the public Quilt RenderTexture and `QuiltLayout` to the output path. Bridge input requires a Quilt, so the Quilt alone is not identified as a performance bottleneck. Core does not directly handle a native library, socket, or Bridge protocol; it remains replaceable with Bridge and test outputs.

URP/HDRP use a dedicated Guard and `RenderPipeline.StandardRequest`. URP reuses a `VolumeStack`; URP TAA is disabled during each Quilt view and restored afterward. HDRP defaults to `IsolatedViewHistories`, which gives each rendered logical view its own persistent Camera history. HDRP `SharedCameraCompatibility` is an explicit legacy mode that uses one shared Camera and adapts temporal effects. Game simulation, Physics, and Animation do not repeat per view, but side effects in user render callbacks still require separate validation.

## Output and Bridge

Session selects a device profile dynamically and uses auto-detection as its primary path. Regular use requires the official Bridge runtime separately. `BridgeQuiltOutput` and Legacy Direct support Windows D3D11 only. The standard `BridgeWorkerQuiltOutput` path supports Windows D3D11 and D3D12 in Editor Play and Players. On D3D12, the Quilt is GPU-shared from Unity's D3D12 device to a D3D11 worker on the same GPU, and the worker sends it to the official Bridge. This path does not use CPU image readback. Regular Bridge Sessions do not run in Edit Mode. For authoring, a separate, explicitly started `ViewWeaveRig` Editor preview is available on Windows D3D11/D3D12; it is distinct from regular Sessions and Playback. URP/HDRP local GPU fixtures confirmed layout rebuild and Stop/readback proof with zero pending retention. D3D11 and D3D12 device fixtures confirmed URP/HDRP Start/Stop/Restart at a 3840x2160 display profile and 7680x4320, 8x6/48-view Quilt, with one submission/completion for each Start and Restart and zero native/retention pending after Stop. Manual optical review remains pending. An isolated Editor fixture confirmed cleanup and explicit restart across Domain Reload; see [Graphics backends](graphics-backends.md) for File > Exit results; normal Editor exit remains unverified. Regular Sessions target Windows only. macOS Looking Glass Go uses a separate `BridgeMetalQuiltOutput` path with the arm64 native shim included in the package. Bounded URP/HDRP template Edit Mode device previews were checked on Unity 6000.6.4f1, Apple M3 Max, and macOS 26.6.2; the user accepted orientation, depth, and brightness on the connected Go. This does not establish exhaustive view/color/scale quality or general performance. The older `BridgeMetalExperimentalOutput` is a separate finite diagnostic route. See [macOS Metal](macos-metal.md) and the [dated validation record](macos-metal-validation-20261010.md) for current conditions; [Graphics backends](graphics-backends.md) describes the earlier experiment's ABI and lifetime boundaries.

The Worker separates Quilt copies on Unity's render thread from its owned D3D11 device, a three-slot shared-GPU ring, worker-side rendering, the WM message pump, and shutdown/reopen. A D3D11 baseline with 349 draw tests passed on RTX 3090 Ti, covering shared-resource handoff, ring reuse, typeless formats, timeouts, late completion, device-removal seams, and generations. Loader lifecycle tests using a fake Bridge runtime passed 100 cycles. Windows D3D12 Editor Play was checked for two cycles per URP/HDRP pipeline with Domain Reload enabled and disabled. In the fixed-fixture D3D12 Player matrix, URP/HDRP Development and Release runs completed Preview, Balanced, Native, then Preview; Native-quality Player execution is confirmed within that matrix. Development performed 10 Native restarts per pipeline; Release performed one. The user's HDRP VFX Learning Templates project at snapshot `8adda27c` also completed Native 7680x4320, 48-view Editor Play and stopped with Idle/Closed and zero pending work. This matrix did not toggle GRD. Optical quality for all 48 views in arbitrary user projects, WARP, and other GPUs remain unverified. Windows CI skips hardware D3D12.

On failure, the Worker enters `Fault` and retains GPU resource references until shutdown completes while submission state is uncertain. If a pending native shutdown does not resolve, recovery requires restarting the Unity Editor/Player. This is a technical recovery boundary, not part of normal setup. It does not cover physical hotplug or hangs with arbitrary device connections.

The Bridge path separates copies from the Unity render thread, Bridge Worker rendering, the WM message pump, asynchronous drain on exit, resize, and reopen. The shared GPU ring buffer has three slots. If the queue is full, a new submission is dropped while queued submissions are retained. Same-process Worker reconnect and zero pending work on shutdown have been measured. Manual optical review of all 48 views, hotplug, and acceptance with multiple devices remain incomplete.

## Verified and unverified scope

Older Windows/D3D11 fixtures cover Basic Lit, Bloom, transparent Lit, uGUI, particles, and UI Toolkit with URP/HDRP; they do not validate the current HDRP isolated-history path or every material/render path. HDRP requires Linear color space. Its default `FollowScene` exposure meters the central view on the GPU, includes exposure compensation, and shares the resulting Fixed exposure across the Quilt with compensation set to zero. An authored Fixed scene exposure uses `fixedExposure - compensation`; `ManualFixed` is an explicit choice. The initial neutral meter sample is discarded, so no Quilt is output while exposure history warms up. The default isolated-history path preserves authored HDRP effects, including TAA, SSR, SSGI, volumetric fog/clouds, and Motion Blur; effect-specific pixel correctness is not established. The explicit shared-camera compatibility mode retains the older adaptations. The controls are grouped under **Rig > Rendering**. Its default History Resource Budget is 4096 MB and Maximum History Cameras is 256. The budget uses the larger of observed graphics-driver growth and estimated new or enlarged RenderTexture storage, and separately limits camera count; the driver counter is zero in Release Players, and the estimate omits unobserved buffers and driver overhead, so it is not a complete VRAM cap. See the [HDRP view-history validation](hdrp-view-history-validation-20261010.md) for the measured scope. Regular macOS Sessions remain unsupported. The separate Looking Glass Go Metal preview path was checked in bounded URP/HDRP template conditions on a Mac; the user accepted orientation, depth, and brightness, while exhaustive optical quality, colorimetry, long-duration behavior, and general performance remain unverified. See the [dated macOS validation](macos-metal-validation-20261010.md). The earlier finite `BridgeMetalExperimentalOutput` experiment is not the current Go route.

The following items remain validation targets for defining support:

| Area | Status |
| --- | --- |
| Animator, SkinnedMeshRenderer, Terrain, LOD, shadows | Individual geometry fixtures checked. Support for general content including Animator remains undetermined. |
| Shader Graph, Renderer Feature, Decal, Reflection Probe, Light Probe | Limited Shader Graph and Renderer Feature fixtures. General use of Decal, Reflection Probe, and Light Probe is unverified. |
| HDRP SSR, SSGI, volumetric fog/clouds, Motion Blur, TAA | The default isolated-history mode keeps authored settings. Only explicit `SharedCameraCompatibility` disables SSR, SSGI, clouds, and Motion Blur, disables TAA, and changes volumetric fog filtering to Gaussian. Per-effect pixel correctness is unverified. |
| FXAA, SMAA, MSAA, color grading, temporal history, Auto Exposure | Limited FXAA/SMAA and some color-grading fixtures. URP MSAA 1/1 checked under one condition; URP TAA remains disabled during view rendering. HDRP creates separate persistent histories per rendered logical view. HDRP exposure follows the scene by default; the Windows history path and per-effect visual correctness remain unverified. |
| UI using RenderTexture, input ray map, VideoTexture | Individual uGUI and UI Toolkit fixtures checked. Input ray maps and VideoTexture are unverified. |
| LineRenderer, TrailRenderer, VFX Graph, complex transparent sorting | Line/Trail checked in individual fixtures. URP Simple Burst checked in a Player for six stop conditions and four regular-play conditions. HDRP, general VFX, and complex sorting remain undetermined. |
| Multiple devices, hotplug, RGBD, Quilt layers | Unimplemented or unverified. |
| Quilt saving, sequence recording, silent MP4 | Capture PNG/sequence and silent MP4 export are implemented and checked in bounded fixtures. Mono audio is saved to a separate WAV. Bridge delivery, audio muxing, and physical-device quality are unverified. |

## How to interpret performance measurements

`LastCpuSubmissionTicks` times only the CPU render-submission interval and excludes `BridgeSubmit`. It does not represent GPU time, render-thread time, transfer time, or present latency. Reducing GPU load without reducing view count is not guaranteed; measure each scene on the same device with the same Quilt settings. Do not attribute overall slowness to Quilt tile transfer alone.

Performance optimization is deferred pending validation. Existing Scene View Gizmo camera authoring is retained; Gizmo overlays in the Quilt are out of scope. GPU Resident Drawer and reuse of work shared between views are independent validation items. HDRP's isolated histories add one persistent Camera per rendered logical view but still render each view separately. Decide whether to adopt an optimization only after its results are checked.

## Implemented validation and remaining gates

The M1 regular-thread path completed implementation and API validation for call-stack, thread-ID, and device-context collection; Unity-render-thread-only copying; the Worker-owned device; the three-slot ring buffer; resize; and close/reopen. Measurements covered 120 draws per case including same-process reconnect, zero pending work at shutdown, and host-thread reuse. Quality, optics, distribution signing, hotplug, and multiple devices remain release gates. Do not conflate regular-thread results with the Direct path.

M2 Session, Registry, Locator, and Rig menu compile, and 18 Session tests passed. Session was checked in 48-view Player for the six individual combinations of URP/HDRP Preview, Balanced, Native, and GRD on/off. Under fixed URP/HDRP quality-switch conditions, four stages completed 122 times each; explicit Stop/Start was followed by 120 draws and zero pending work. General Registry operation, hotplug, multiple devices, and optical quality remain unverified. GRD is evaluated for MeshRenderer-focused cases; it does not reduce the number of rendered views or render requests. VolumeStack caching is applied and fixed-condition CPU submission comparisons exist, but GPU and render-thread times have not been collected.
