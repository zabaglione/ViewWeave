# DX12 HDRP performance investigation

## Scope

This note records a short, read-only capture of the current HDRP quilt workload and a test-only SDK density fixture run. These are diagnostic snapshots, not a benchmark or a release-performance claim. The profiled scene was the HDRP VFX Learning Templates scene in `E:\My project (34)`, at Balanced quality, a 3840x2160 quilt, and 48 views. The Editor scene was dirty before capture; the capture did not save or modify it.

The user's separate reports are approximately 100-180 FPS without Looking Glass and 15 FPS with it, followed by a current report of 4-5 FPS. These were not measured under the same controlled conditions as this capture and should not be compared as a single time series. The profile covered frames 4404-4406 in one current Editor profiler session. Profiler collection was enabled for the capture so `EditorLoop`, `PlayerLoop`, and quilt scheduler markers were recorded. The `recording=false` and `profileEditor=false` values were read after capture, when the original Profiler flags had been restored; they do not describe the capture interval.

## Capture observations

| Marker or counter | Observation |
| --- | ---: |
| Main-thread frame, frames 4404-4406 | 111.764 / 136.581 / 131.570 ms; mean 126.638 ms (about 7.9 FPS) |
| Quilt scheduler | 119.670 ms, 1 call/frame, inclusive |
| HDRP `Camera.CustomRender` | 116.560 ms, 48 calls/frame, main thread; 92.160 ms, 48 calls/frame, render thread |
| HDRP `HDRenderPipelineRenderCamera` | 89.330 ms, 49 calls/frame, render thread |
| `ExecuteRenderGraph` | 83.146 ms, 49 calls/frame, render thread CPU marker; this is not GPU time |
| `BufferD3D12.InitializeResource` | 25.158 ms, 1,113 calls/frame, render thread |
| `BufferD3D12.CreateBufferResource` | 22.930 ms, 1,113 calls/frame, render thread |
| Deferred lighting / shadow maps | 10.428 / 7.909 ms, 49 calls/frame, render thread |
| Companion performance samples | CPU frame 110.933 / 120.006 / 121.902 ms; GPU frame 100.125 / 101.438 / 108.782 ms |
| Companion render counters | 6,558 draw calls and 12,078,580 triangles per sample |
| Bridge worker draw marker | 1.768 ms over 6 calls across 3 frames (0.589 ms/frame) |

Profiler timings are inclusive and nested; do not add parent and child timings. The buffer-resource marker paths included Push Global Camera Parameters, DepthPyramid, and RenderShadowMaps. The Bridge marker does not prove that all interop work or GPU waits are included, so this capture does not establish the absence or presence of a synchronization stall. The three companion GPU values are separate performance-stat samples, not GPU timings attributed to each profiler frame. The first memory sample reported about 13.54 GB allocated and 19.66 GB reserved; these are whole-process counters, not quilt-only allocations.

## Interpretation and next check

The capture shows a high CPU render-thread cost across 48 HDRP camera renders, alongside repeated buffer-resource initialization. It does not identify which subsystem owns all 1,113 buffer initializations, prove that Render Request frame cleanup causes them, or establish that batching cameras will reduce them. A test-only comparison should hold scene, camera geometry, HDRP settings, resolution, and output constant while comparing 48 Standard Render Requests with 48 regular cameras in one SRP frame. Check frame and render-thread time, buffer creation counts, per-view image equivalence, main-camera output, history/RT memory, and exactly one quilt output submission. Keep GPU timing separate from CPU markers.

## Failed prototype startup attempt

The Balanced HDRP camera-batch run `20261004T115800549Z-balanced` used Windows Editor Play on Direct3D12 but failed before entering a phase: the evidence reports `result=failed` and `phases=[]`. The initial manual `StartSession()` raced the Editor's pending recovery; the session rejected startup while `BridgeWorkerLifecycle.RecoveryPending` was true, returning `Bridge worker requires Windows D3D11 or D3D12 Play mode in the Editor`. This was a lifecycle startup failure, not evidence of an unsupported graphics API. No image was displayed and no performance comparison was measured.

The SDK's tracked Graphics and Quality pipeline defaults are null, and the color space is Linear. Those null pipeline defaults are the intentional multi-pipeline test baseline. After the failed run restored that baseline, delayed Editor exit left the HDRP wizard showing the unconfigured-pipeline status in red; while HDRP was active, the wizard had shown only the Lightmap item in red. This reflects the restored test-project state and does not establish that HDRP assets were missing.

The follow-up source change defers the first session to the existing `startOnEnable` path so Editor recovery can finish, and stops a faulted session during cleanup before synchronous exit. The change compiled, and the later density run completed through startup, cleanup, and Editor exit. That run uses Legacy Render Requests; it does not validate the natural-camera batch path.

## HDRP density fixture run

The test-only run `20261004T130532660Z-density-balanced` completed in Unity 6000.6.4f1 on Direct3D12. It used the generated static HDRP fixture at Balanced quality with a 3840x2160 output quilt (8x6, 48 tiles), a global Volume, and static red/green geometry. It had no Local Volume or VFX Graph. Each phase had 60 warmup frames followed by 120 measured frames. The order was Full, Half, Quarter, then Full again. The device output grid remained 48 tiles; only the number of rendered views changed.

| Mode | Rendered views | Frame p50 / p95 (ms) | GPU frame p50 / p95 (ms) | Submit attempts / queued / completed / dropped |
| --- | ---: | ---: | ---: | ---: |
| Full | 48 | 254.76 / 295.81 | 241.28 / 278.36 | 180 / 180 / 180 / 0 |
| Half | 24 | 53.17 / 63.01 | 45.46 / 53.54 | 180 / 179 / 179 / 1 |
| Quarter | 12 | 23.31 / 27.57 | 17.31 / 19.42 | 180 / 171 / 171 / 9 |
| Full (return) | 48 | 254.68 / 300.69 | 241.08 / 290.92 | 180 / 180 / 180 / 0 |

The image checks retained the first and last views and verified the nearest-view reuse path. An independent raw-pixel audit compared all 48 tiles in the Half and Quarter captures against their assigned rendered source tile in the Full reference: both had zero mismatching RGBA pixels; the returned Full capture also matched the first Full capture pixel-for-pixel. This supports the fixture's tile-copy and selected-source image equivalence. It does not establish image quality for arbitrary scenes or perceptual quality on a physical display. The repeated Full measurements were close but are one short sequential run, not a controlled benchmark.

The submission counters distinguish eligible attempts from accepted queue entries and native completions. Queue pressure dropped one Half and nine Quarter frames; all queued entries completed, and cleanup reported zero native/shutdown pending work, `Idle` session state, and closed output. This is a diagnostic observation of the current worker's backpressure behavior, not a claim of lossless output at those modes.

The fixture run also temporarily set HDRP Wizard Lightmap Encoding to High for all nine Wizard targets. The launcher recorded each original enum value, restored it through the Editor API, and reported `settingsRestored=true`; the original Windows 64-bit value was High. The project's Graphics and Quality pipeline defaults remained null, its color space remained Linear, and the scene setup was restored. This temporary test-project setup is not part of the user workflow; the README describes the HDRP Wizard check for a user's own project.

Treat the timing values only as measurements of this static SDK fixture, Unity version, Direct3D12 path, and machine. They do not predict FPS in a user's HDRP/VFX scene, prove physical-device playback smoothness, or establish a general performance gain. The natural-camera batch comparison has not produced an accepted result, and the production renderer continues to use the existing Legacy Requests path. Human visual confirmation on the target Looking Glass remains outstanding.

The project manifest declares HDRP 17.7.0, while the inspected package lock and local package cache resolve HDRP/Core 17.6.0. The source-level lifecycle observations are therefore limited to the cached 17.6.0 package until that version discrepancy is resolved. The density fixture does not resolve this version discrepancy. The Windows acceptance matrix remains pending; native long-soak and release compatibility remain unverified.

Internal evidence: `.local/dx12-followup/profiler-fresh-3frames.json`, `.local/dx12-followup/profiler-self-3frames.json`, `.local/dx12-followup/profiler-marker-paths.json`, `.local/dx12-followup/performance-stats-3samples.txt`, and the `hdrp-camera-batch/20261004T130532660Z-density-balanced/` launch, runtime, PNG, and Editor log artifacts.
