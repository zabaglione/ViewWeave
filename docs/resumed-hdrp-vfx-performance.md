# Resumed HDRP VFX performance capture

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

Final cleanup restored the known temporary backup to its original path and retained the historical recovery copy. The saved scene was not changed; all six tracked settings/asset hashes match their pre-run values. The Editor is closed, and package revision 5892 remains installed. These checks do not convert the interrupted run into a complete validation.

## Retained evidence

- [Density capture, valid prefix](../.local/dx12-followup/resumed-20261007/balanced-density-valid.csv) and [raw density capture](../.local/dx12-followup/resumed-20261007/balanced-density.csv)
- [Fresh warmed Profiler capture](../.local/dx12-followup/resumed-20261007/fresh-full-profiler-warmed.csv)
- [Balanced full quilt image](../.local/dx12-followup/resumed-20261007/balanced-full-quilt.png) and [tile audit](../.local/dx12-followup/resumed-20261007/balanced-full-quilt-audit.csv)
- [GRD phase capture](../.local/dx12-followup/resumed-20261007/grd-aba.csv), [crash summary](../.local/dx12-followup/resumed-20261007/grd-aba-crash-summary.txt), and [final cleanup summary](../.local/dx12-followup/resumed-20261007/grd-aba-final-cleanup.json)
- [Verified pre/post settings hashes](../.local/dx12-followup/resumed-20261007/grd-aba-asset-hashes-after-crash.json)
- [Prior HDRP/DX12 investigation context](dx12-performance-investigation.md)
