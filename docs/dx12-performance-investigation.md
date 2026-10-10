# DX12 HDRP performance investigation

> Audience: Unity and rendering developers. This is a dated research or validation record limited to the revision, platform, and conditions stated here; it does not establish support for other configurations. For current support and setup, see [Compatibility](compatibility.md) and [Getting started](getting-started.md). Browse the [Technical guide](technical-guide.md) or [User guide](user-guide.md).

## Scope

This note records a short, read-only capture of the current HDRP quilt workload and test-only SDK density and camera-batch fixture runs. These are diagnostic snapshots, not a benchmark or a release-performance claim. The profiled scene was the HDRP VFX Learning Templates scene in `E:\My project (34)`, at Balanced quality, a 3840x2160 quilt, and 48 views. The Editor scene was dirty before capture; the capture did not save or modify it.

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

The capture shows a high CPU render-thread cost across 48 HDRP camera renders, alongside repeated buffer-resource initialization. It does not identify which subsystem owns all 1,113 buffer initializations or prove that Render Request frame cleanup causes them. A later fixed-fixture A/B/A comparison measured 48 Standard Render Requests against a natural camera batch with scene, camera geometry, HDRP settings, resolution, and output held constant; see the result below. The comparison retained per-view image and epoch/submission checks, but the Editor crash means it is not an accepted end-to-end result. Keep GPU timing separate from CPU markers.

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

Treat the timing values only as measurements of this static SDK fixture, Unity version, Direct3D12 path, and machine. They do not predict FPS in a user's HDRP/VFX scene, prove physical-device playback smoothness, or establish a general performance gain. The natural-camera batch comparison below did not produce an accepted end-to-end result, and the production renderer continues to use the existing Legacy Requests path. Human visual confirmation on the target Looking Glass remains outstanding.

## Natural camera-batch comparison and Editor crash

The fixed HDRP fixture case `20261004T140722565Z-camera-batch-balanced` ran an A/B/A sequence: Legacy Requests, Natural Camera Batch, then Legacy Requests. Each phase used 60 warmup and 120 measured frames, with 180 submission attempts. The 48-tile output was held constant. The Natural Camera Batch phase completed 180 epochs and 8,640 owned camera callbacks; each epoch recorded exactly one MainCamera callback, and the render-to-transfer frame offset was one throughout. It queued and completed 180 submissions with zero drops. Its Full-source image comparison covered all 48 tiles (8,294,400 pixels) and reported zero RGBA mismatches and zero maximum channel difference.

| Phase | CPU frame p50 (ms) | GPU frame p50 (ms) | Attempts / queued / completed / dropped |
| --- | ---: | ---: | ---: |
| Legacy Requests A | 245.7466 | 231.556096 | 180 / 180 / 180 / 0 |
| Natural Camera Batch B | 342.6780 | 328.307712 | 180 / 180 / 180 / 0 |
| Legacy Requests A return | 243.7782 | 230.283264 | 180 / 180 / 180 / 0 |

Against the mean of the two Legacy phases, this single run measured Natural Camera Batch about 40% slower at CPU frame p50 and 42% slower at GPU frame p50. The static fixture and image equivalence do not offset the measured regression. Natural Camera Batch is not adopted for production; the existing Legacy Requests path remains in use.

The runtime summary recorded its phases and the worker drained to `Idle`/`Closed` with both pending counts at zero. However, Unity Editor exited with code `-1073741819` (`0xC0000005`) after the runtime result, and `hdrp-camera-batch-launch.json` was not written. The crash evidence identifies `bridge_inproc` and the symbol `get_calibration_template_config_version`; dump inspection places the null-read at RVA `0xF8550` on thread 83204. The symbol may be a nearest-export label rather than the exact call name. Available evidence does not establish whether the fault came from SDK versus official Bridge call ordering, and Editor settings/lightmap/scene restoration remain unverified for this case. Do not count the runtime phase completion as an overall pass.

A later diagnostic run, `20261004T143237618Z-density-balanced`, used the density plan with a 10-second post-drain Play hold and native trace enabled. Its diagnostic log records zero pending counts through the hold and stop request; launch evidence reports `completed`, `settingsRestored=true`, `sceneSetupRestored=true`, pending counts zero, and the owned Editor process exited with code 0. This confirms one successful shutdown and restoration sequence under that run's conditions. It is not a performance comparison and does not prove the crash race is absent.

A read-only source review found that non-XR Standard Render Request tile dimensions (480x360 in this fixture) flow to the HDCamera's actual dimensions and then its viewport. RTHandle maximum allocation is a separate quantity from the viewport. This source observation does not establish why the reported Quality FPS stayed unchanged; that cause remains unresolved.

Internal evidence: `.local/dx12-followup/hdrp-camera-batch/20261004T140722565Z-camera-batch-balanced/runtime-summary.json`, `process-observation.json`, and `crash-evidence/crash-observation.json`; `.local/dx12-followup/hdrp-camera-batch/20261004T143237618Z-density-balanced/hdrp-camera-batch-launch.json`, `process-observation.json`, and `post-drain-diagnostic.log`.

The separate user project `E:/My project (34)` declares HDRP 17.7.0 in its manifest while its package lock and local cache resolve HDRP/Core 17.6.0. That discrepancy is specific to the inspected user project. The SDK validation fixture manifest and lock both specify HDRP/Core 17.6.0; the source-level lifecycle observations and matrix below are limited to Unity 6000.6.4f1 and that package version.

## Windows D3D12 Player matrix

The matrix run `20261004T151420937Z-fed703a5` passed four builds and six serial Player runs in the SDK validation project on Unity 6000.6.4f1. Each build and Player used Direct3D12. The combinations were URP and HDRP, Development and Release builds, with Development GRD off/on and Release GRD off. Each run passed the four-stage Preview, Balanced, Native, Preview quality cycle at 48 views in an 8x6 quilt, with output sizes of 1920x1080, 3840x2160, 7680x4320, and 1920x1080. Development cases completed 10 Native restarts; Release cases completed one. Both Development GRD-on cases completed a 600-second hold and reported 76 renderers and 3,648 visible instances. The run retained 144 PNG artifacts across the six cases.

All six Players exited naturally with code 0. Their runtime summaries ended in `Idle` with output `Closed`, and reported zero native and shutdown pending work. These are worker shutdown counters, not proof that vendor resources were released or that the process is leak-free. The external process observer recorded rising private-byte, thread, and handle counts during the HDRP Development runs, including across restart stages. The cause is unresolved. Runtime-reported process memory counters were zero despite being marked available, so they are not used as memory evidence. The two HDRP Development cases each emitted one Graphics Ring Buffer warning (`Ran out of Graphics Ring Buffer space`); the option's units and a numeric recommendation are unknown. URP Development and both Release cases had no warnings.

The matrix establishes functional behavior for this fixed SDK fixture, Unity/package version, and machine. It does not establish behavior in arbitrary VFX scenes, physical display quality, or leak-free operation. The 600-second holds apply only to the two Development GRD-on cases; they are not a general release soak. Natural Camera Batch remains unadopted after the slower fixed-fixture comparison and the separate Editor crash described above. The original crash cause remains unresolved.

The runner verified the four tracked `ProjectSettings` files against their snapshots. It did not capture a pre-run raw snapshot of every Unity asset. During cleanup, one `HDRenderPipelineGlobalSettings.asset` change was identified as a single added existing `GPUResidentDrawerResources` reference and restored to the repository baseline; preserved pre/post evidence is under `.local/dx12-followup/player-matrix/20261004T151420937Z-fed703a5/`. Because no matrix-start raw copy of that asset exists, the four-file settings gate should not be read as proof that every project asset was restored byte-for-byte.

Internal evidence: `.local/dx12-followup/player-matrix/20261004T151420937Z-fed703a5/matrix-summary.json`, per-case `runs/*/summary.json`, `runs/*/external-process-observations.json`, and `global-settings-head-cleanup.json`.

## Post-drain density diagnostic

The follow-up case `20261004T161234027Z-density-balanced` completed the Full/Half/Quarter/Full sequence in Unity 6000.6.4f1 on Direct3D12. Each phase measured 120 frames and rendered 48/24/12/48 views. Frame p50/p95 was 270.79/319.96, 61.28/70.56, 25.66/29.50, and 270.98/315.27 ms; Quarter dropped two submissions and the other phases dropped none. Reused-source comparisons reported zero mismatches, but the runtime full-source comparison was invalid with zero pixels compared. The first and last Full PNG files were byte-identical; this is not an independent pixel-level source comparison. These are diagnostic measurements, not an overall validation pass or a performance acceptance claim.

The post-drain Play hold lasted 30.0038 seconds with native and shutdown pending counts at zero. The runtime completed, its owned Editor exited naturally with code 0, and the launch evidence reports `settingsRestored=true` and `sceneSetupRestored=true`. The launcher restored all nine HDRP Wizard Lightmap Encoding target values through the Editor API. Unity had serialized six Lightmap defaults into `ProjectSettings.asset`; after the Editor exited, cleanup restored that file byte-for-byte from the preserved preflight snapshot and removed the newly generated, previously absent `PackageManagerSettings.asset`. The HDRP global settings asset remained byte-identical. Cleanup evidence is `.local/dx12-followup/hdrp-camera-batch/20261004T161234027Z-density-balanced/density-diagnostic-cleanup.json`; the original `density-diagnostic-final-evidence.json` was left unchanged.

The only OS observation was one mid-run sample during Half: 282 of 284 thread records mapped, with two unmapped and helper availability false. It provides no post-drain time series and cannot establish a memory/thread trend, leak status, or source. The Editor log recorded an unavailable licensing-token update followed by a successful update, and a D3D12 upload-buffer-too-small warning (16,777,216 bytes available; 33,177,600 requested); neither is attributed to a phase. This single completed shutdown does not resolve the earlier Editor crash or prove that its race is absent.

Internal evidence: `.local/dx12-followup/hdrp-camera-batch/20261004T161234027Z-density-balanced/density-diagnostic-final-evidence.json`, `hdrp-camera-batch-launch.json`, `process-observation.json`, `post-drain-diagnostic.log`, and `density-diagnostic-cleanup.json`.

Internal evidence: `.local/dx12-followup/profiler-fresh-3frames.json`, `.local/dx12-followup/profiler-self-3frames.json`, `.local/dx12-followup/profiler-marker-paths.json`, `.local/dx12-followup/performance-stats-3samples.txt`, and the `hdrp-camera-batch/20261004T130532660Z-density-balanced/` launch, runtime, PNG, and Editor log artifacts.
