# Resumed HDRP VFX performance capture

> Audience: Unity and rendering developers. This is a dated research or validation record limited to the revision, platform, and conditions stated here; it does not establish support for other configurations. For current support and setup, see [Compatibility](compatibility.md) and [Getting started](getting-started.md). Browse the [Technical guide](technical-guide.md) or [User guide](user-guide.md).

This internal diagnostic records a resumed run in the user's HDRP VFX Learning Templates project on Direct3D12, at Balanced quality with a 3840x2160 quilt. The view schedule was Full (48 views), Half (24), Quarter (12), Full (48), Off, then Restored Full (48). Each phase used 60 warmup frames and 120 measured samples. Analysis used the first 732 valid-prefix records from 856 raw records. The package remained pinned to version 5892.

The FPS column is the reciprocal of the median Editor frame time; it is not physical display refresh. CPU submission time is `QuiltRenderer.LastCpuSubmissionTicks`; it covers view preparation, rendering, and copy, and excludes texture allocation/clear, Bridge submission, and final cleanup.

| Phase | Median Editor frame (ms) | Reciprocal FPS | Median CPU submission (ms) | Median GPU frame (ms) |
| --- | ---: | ---: | ---: | ---: |
| Full, 48 views | 117.27 | 8.53 | 107.86 | 92.42 |
| Half, 24 views | 63.13 | 15.84 | 54.26 | 46.48 |
| Quarter, 12 views | 34.88 | 28.67 | 26.21 | 23.85 |
| Full, 48 views | 111.50 | 8.97 | 103.53 | 87.83 |
| Off (`Rig.AutoRender=false`) | 7.22 | 138.55 | N/A | 1.54 |
| Restored Full, 48 views | 112.17 | 8.92 | 103.40 | 87.91 |

For Off, setting `Rig.AutoRender=false` disabled auto-render on both the Rig and renderer. With quilt generation stopped, the completion watchdog reset while the Bridge remained connected. Stale CPU submission samples were excluded, so CPU submission is N/A for Off. GPU frame-time medians were independently calculated from the valid-prefix CSV, not the full 856 raw records; all six phases had 120 positive readings with unique timestamps.

A separate fresh three-sample Profiler capture reported aggregate CPU 121.74 ms, Main Thread 117.00 ms, Render Thread 100.20 ms, and GPU 95.61 ms. `Camera.CustomRender` was 192.94 ms across summed calls. Nested markers were 118.52 ms for `HDRenderPipelineRenderCamera`, 109.17 ms for `ExecuteRenderGraph`, 27.99 ms for `BufferD3D12.InitializeResource`, and 25.41 ms for `BufferD3D12.CreateBufferResource`. These are inclusive/nested marker timings; their sums are not Main Thread time.

The quilt audit found all 48 tiles nonempty and zero magenta pixels. The user physically confirmed that the scene was visible on the Looking Glass. The retained full-quilt PNG was also inspected separately. This is a diagnostic image and audit, not a full optical-quality assessment.

The Editor restart recovered bridge initialization with the existing 5892 package, but the new diagnostic message was not deployed or exercised. No gain at 48 views has been demonstrated.

## Incomplete GRD follow-up

| Phase | Median Editor frame (ms) | Reciprocal FPS | Median CPU submission (ms) | Median GPU frame (ms) |
| --- | ---: | ---: | ---: | ---: |
| InstancedDrawing_A1 | 162.20 | 6.17 | 150.77 | 130.18 |
| Disabled_B | 277.89 | 3.60 | 266.47 | 248.98 |

Both phases used 48 views, 60 warmup frames, and 120 measured samples; each had a worker drop counter that remained zero and 120 positive GPU readings with unique timestamps. Session restarts reset counters, and the VFX phase was separated by about 8,000 frames, so this is not a controlled causal comparison. It does not show that disabling GRD is universally slower or establish a 48-view gain.

The Editor exited unexpectedly after `Disabled_B` `StopSession` returned `Closing`; the next state read lost its command connection. Windows recorded `0xC0000005` in the official Looking Glass Bridge 2.6.3 `bridge_inproc.dll` at RVA `0xF8550` (SHA-256 `3BE0EBA96844688C2EB2E70FC512A5ADA3D53E3ED9C8810454AB0370B8241386`). No SDK shim root cause has been demonstrated. `InstancedDrawing_A2` was not run, and the Disabled_B Profiler output is absent. Further quality, Forward, and 300-second hold checks were not run after a Scene Backup Detected modal blocked recovery.

At the end of that interrupted pass, cleanup restored the known temporary backup to its original path and retained the historical recovery copy. The saved scene was not changed; all six tracked settings/asset hashes match their pre-run values. The Editor was closed at that point, and package revision 5892 remained installed. The user later recovered the project and reopened it for the continuation below. These checks do not convert the interrupted GRD run into a complete validation.

## Recovery continued (2026-10-07)

The user-recovered HDRP project was reopened in Unity 6000.6.4f1 on Direct3D12 with the same `5892baaf` HDRP package pin. The VFX Learning Templates scene remained clean, the original camera and Bridge Rig stayed in Play, and the saved scene plus all six HDRP/Graphics settings hashes matched the new pre-test snapshot. No scene or project settings were saved or changed. The earlier GRD A/B/A was not resumed after its native Bridge crash.

### Quality and rendered dimensions

The same Native → Preview → Balanced → Native sequence used the public quality API without stopping the Bridge worker. Each phase had 60 warmup frames and 120 measured records. All measured records showed Full 48 views, 120 unique positive GPU timestamps, and a zero dropped counter. The first measured record has a frame/attempt delta of 61 because the collector's first interval spans the 60 warmup frames; the remaining 119 measured intervals are contiguous. This is a collector boundary, not 60 lost measurement frames.

| Quality | View texture | Quilt texture | Median Editor frame interval (ms) | Reciprocal FPS | Median CPU frame (ms) | Median GPU frame (ms) | Median CPU submission (ms) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Native | 960×720 | 7680×4320 | 110.97 | 9.01 | 111.03 | 87.60 | 102.89 |
| Preview | 240×180 | 1920×1080 | 112.45 | 8.89 | 112.59 | 89.21 | 103.68 |
| Balanced | 480×360 | 3840×2160 | 116.74 | 8.57 | 116.71 | 92.96 | 107.38 |
| Native return | 960×720 | 7680×4320 | 110.77 | 9.03 | 110.66 | 87.51 | 102.67 |

Lowering per-view render resolution did not produce a material frame-rate gain at 48 views in this scene. These are Editor frame-rate measurements, not physical Looking Glass refresh measurements. Another Unity batchmode process remained present, although its CPU use was negligible in a short pre-run check; the available GPU activity check was only a point-in-time snapshot.

A separate short probe read `QuiltRenderer.viewTexture` and an existing HDRP `HDCamera` via `TryGet` after a fresh completed quilt in each quality. Its `actualWidth`/`actualHeight` matched the view texture dimensions above. It captured one state per quality, not one callback per view, so it does not independently verify all 48 view renders. The separately named `lowResViewport` field is retained as such in the raw CSV; it is not labeled as a final viewport.

### Conditional feature gates

The source camera had occlusion culling enabled, but `StaticOcclusionCulling.umbraDataSize` was zero and there were no manual portals or active bake. No occlusion A/B/A was run because this scene had no baked occlusion data to exercise.

The GPU supports async compute and the live HDRP `HDCamera.frameSettings` reported `AsyncCompute` and `LightListAsync` enabled, with `BuildLightListRunsAsync=True`. SSR, SSAO, contact shadows, and volume voxelization async flags were also enabled; high-quality lines async was disabled. Since LightList async was already effective, no setting override or comparison was made.

### Three-frame D3D12 resource trace

The CPU Profiler was temporarily enabled to collect three fresh consecutive frames on the current Editor session, with Deep Profile off, then all three original profiler flags were restored to false. In one Render Thread frame, the inclusive samples included 143 `BufferD3D12.InitializeResource` calls under `DepthPyramid` (about 3.1 ms), 192 under `BuildLightList` (about 3.7 ms), and 192 under `Push Global Camera Parameters` (about 4.1 ms). These markers are nested; `BufferD3D12.CreateBufferResource` is inside `InitializeResource`, so their times and counts must not be added. The raw capture also contains `self_total_ms` values from a collector bug; those values are invalid and excluded from this report. The raw file was retained unchanged, and no recapture was made. The work is distributed across multiple HDRP Render Graph passes. This trace does not prove that the same buffer is redundantly recreated, and it does not identify a safe local change that would remove the cost.

The worker's dropped counter was zero during all four measured quality phases, but later readbacks in the same Play session reported a cumulative value of 9. The resumed session must therefore not be described as drop-free end to end. After the SDK test, `ViewWeaveRig.AutoRender` and the renderer's linked auto-render flag were restored to true without stopping the session. The final readback showed Native, Rendering, Ready, Full 48, a 7680×4320 quilt, 20,813 completed submissions, and no session, worker, or render error. The Editor remains in Play, and no `StopSession` was issued.

The user's package remained on commit `5892baaf`; the updated native trace DLL was not installed or validated in the user's project. The isolated fixture pass is diagnostic and has not yet produced a product-side performance change.

### SDK one-shot and restored Editor state

The first SDK one-shot's hardware transport check passed 1/1, but its display-selection path used ordinal 2. It returned `TryBegin=True`, then the worker profile read returned `rc-4` before the worker became Ready; it completed zero quilts. Cleanup called Dispose and uninit once and reached cleanup end. The process exited naturally with code 1, with no crash. A corrected one-shot then used the live session's zero-based ordinal 0 and passed: `get_display_count` returned 1, the worker became Ready with a 3840×2160 display and 7680×4320 8×6 quilt, and five fresh completions advanced from 1 through 5 with zero worker drops. The runner stopped once and exited naturally with code 0, with no timeout or crash. The trace recorded unregister, texture release, device release, and uninit calls, each returning 1; cleanup ended with zero pending work and clear slot flags. These are diagnostic return values, not independent proof of vendor resource release. This validates the corrected selector and teardown path in the isolated SDK fixture; physical Looking Glass output was not rechecked. The detailed trace is in [native shutdown trace](native-shutdown-trace.md).

The still-running user's Bridge session reports `displayOrdinal=0` on both its Session and Worker components. Its cached, valid profile reports `DisplayIndex=2`, 3840×2160, and a 7680×4320 8×6 quilt. `DisplayIndex` is a separate profile field and must not be read as the zero-based selector ordinal. No safe public profile-count enumeration API was found in the loaded ViewWeave assembly. The initial SDK request used ordinal 2, outside the available ordinal range; the corrected fixture used ordinal 0. No native init/uninit was called while reading the existing user session.

At edit-mode preflight, the saved scene and host position were `(-51.04, 1.13, 4.68)`. During Play, the source camera and its `VFX_HDRP_Camera` host both read `(-51.04, 1.17, 3.46)`. The saved scene SHA-256 still matched its pre-test value, so the runtime transform observation is kept separate from saved scene state; no cause for the runtime position difference is inferred. All six settings/asset hashes also still match. The physical display was not rechecked after the SDK one-shot.

### Continued-run evidence

- Quality samples (`../.local/dx12-followup/continued-20261007/quality-native-preview-balanced-native.csv`)
- Rendered viewport sizes (`../.local/dx12-followup/continued-20261007/viewport-sizes.csv`)
- Occlusion gate (`../.local/dx12-followup/continued-20261007/occlusion-gate.txt`) and effective async-compute gate (`../.local/dx12-followup/continued-20261007/async-gate-final.txt`)
- Three-frame BufferD3D12 trace with parent paths and call counts (`../.local/dx12-followup/continued-20261007/buffer-resource-profiler-3frames.txt`) and restored Profiler flags (`../.local/dx12-followup/continued-20261007/profiler-final-flags.txt`)
- Post-profiler runtime state (`../.local/dx12-followup/continued-20261007/runtime-closeout.txt`), restored runtime state (`../.local/dx12-followup/continued-20261007/runtime-restored-final.txt`), and final scene/settings hash verification (`../.local/dx12-followup/continued-20261007/final-integrity-20261007.txt`)
- Initial SDK one-shot result (`../.local/dx12-followup/continued-20261007/sdk-one-shot-result.txt`), ordinal-zero SDK pass summary (`../.local/dx12-followup/continued-20261007/sdk-ordinal0-pass-summary.txt`), live display ordinal/profile (`../.local/dx12-followup/continued-20261007/bridge-display-ordinal.txt`), and pre-test recovery snapshot (`../.local/dx12-followup/continued-20261007/recovery-preflight.json`)
- Final runtime readback after the ordinal-zero test (`../.local/dx12-followup/continued-20261007/runtime-post-ordinal0-final.txt`), final asset hashes and Profiler state (`../.local/dx12-followup/continued-20261007/final-integrity-post-ordinal0-20261007.txt`), and post-test static checks (`../.local/dx12-followup/continued-20261007/static-checks-post-ordinal0-20261007.txt`)

## Retained evidence

- Density capture, valid prefix (`../.local/dx12-followup/resumed-20261007/balanced-density-valid.csv`) and raw density capture (`../.local/dx12-followup/resumed-20261007/balanced-density.csv`)
- Fresh warmed Profiler capture (`../.local/dx12-followup/resumed-20261007/fresh-full-profiler-warmed.csv`)
- Balanced full quilt image (`../.local/dx12-followup/resumed-20261007/balanced-full-quilt.png`) and tile audit (`../.local/dx12-followup/resumed-20261007/balanced-full-quilt-audit.csv`)
- GRD phase capture (`../.local/dx12-followup/resumed-20261007/grd-aba.csv`), crash summary (`../.local/dx12-followup/resumed-20261007/grd-aba-crash-summary.txt`), and final cleanup summary (`../.local/dx12-followup/resumed-20261007/grd-aba-final-cleanup.json`)
- Verified pre/post settings hashes (`../.local/dx12-followup/resumed-20261007/grd-aba-asset-hashes-after-crash.json`)
- [Prior HDRP/DX12 investigation context](dx12-performance-investigation.md)
