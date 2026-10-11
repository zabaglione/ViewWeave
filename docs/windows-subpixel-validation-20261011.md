# Windows subpixel output validation

**Validation date:** 2026-10-11
**Source base:** `e777b34763b25e1673e5dc0188c812cda69cbcc0` plus the Native, shared Managed, presenter, shader, and Unity fixture changes that were uncommitted in the validation working tree. These results are not a release claim.

## Current Windows contract

`BridgeSubpixelMode` is shared by Windows and macOS. Serialized values are `Automatic=0` (the default, including older serialized objects with no field), `ClassicRgb=1`, and `CalibratedCells=2`. These output modes are distinct from Bridge `CellPatternMode` calibration values.

All three Windows Bridge Worker modes use the ViewWeave Native D3D11 interlacer. Unity D3D11 and D3D12 producers use the same GPU; D3D12 is consumed through the D3D11 worker route. The status backend is `NativeD3D11` (1), and the render-completed / present-accepted counters advance only after the presenter's GPU completion and accepted DXGI Present. Legacy Direct remains an independent Bridge-managed D3D11 route.

Automatic uses a validated Bridge cell table when available; without a table it uses Bridge `ri` / `bi` channel indices for the RGB/BGR pattern. Classic RGB forces even RGB and needs no cell table. Calibrated Cells requires a real validated table and faults if that data is unavailable; it does not silently fall back. No model-name allowlist is used. The test device reported 3840x2160 native output and two calibrated cells; this does not establish support or optical quality for every display.

The common shader handles Linear and Gamma content and explicit UNORM versus sRGB source encoding. Windows preserves the producer-transfer Y flip, copies the source quilt at its original dimensions, then scales once to native output dimensions. The common input gate requires matching RenderTexture and QuiltLayout dimensions, complete positive grids up to 4096 per axis, exact tile divisibility with no padding, and a finite positive aspect. A non-default 15x10 texture with a 3x2 / six-view grid is valid; mismatched dimensions, a 16x10 padded texture, partial view count, and an empty layout are rejected by focused policy tests.

The Managed Worker requires the V1 output-options and draw-options ABI and has no fallback to legacy begin, lease, or draw entry points. A missing required ABI after ownership is a fault; an unknown shutdown state is not reported as clean. Active Editor Preview and runtime Session mode changes are rejected while retaining the current mode.

The macOS source now requires `open_v4` and uses the common calibration and shader contract, but it was not built or run on macOS. The Mac binary in this Windows environment is still the older v3 artifact. Earlier macOS results remain historical for their recorded source and binary and do not validate the new source, shader, calibration, or ABI.

## Validation results

The Windows x64 Release Native build and all 19 registered CTest executables passed (19/19, 0 failures, 17.42 seconds). D3D12 hardware cases had no adapter skips. The CTest log SHA-256 is `08C82F72AB303C9E7325372862D763344E05F74A8806B621206D36D5CFB786C5`. The presenter executable contains 25 WARP D3D11 pixel-oracle cases and six calibration boundaries; these are nested checks, not additional CTest executables. Each WARP output channel was compared at tolerance 0.006. The shared HLSL SHA-256 is `5a0e0b8ea9bef1e5e44dbe836fdb7b4ec56f0b63015beb6d7b7e2effff0fc0fd`.

Focused Managed/UI validation passed on D3D11: EditMode 16/16 and PlayMode 9/9. The Native module-enumerator diagnostic suite passed 25/25 after its close-only failure fixture was corrected. The discovery/identity focused PlayMode suites passed 46/46: `BridgeNativeImageGuardTests` 26 and `BridgeDisplayDiscoveryTests` 20. All had zero skips and natural Editor exit code 0. The first Unity run failed compilation with CS0221 in the production BridgeWorkerQuiltOutput enum-sentinel cast; that production cast was corrected before the focused 16/16 run.

The final device matrix contains one passing test in each of 12 Windows Editor conditions: URP and HDRP, D3D11 and D3D12, and each of Automatic, ClassicRgb, and CalibratedCells. Every final matrix XML reports 1/1 passed, 0 failed, and 0 skipped. Unity was 6000.6.4f1 on an NVIDIA GeForce RTX 3090 Ti with the official Looking Glass Bridge 2.6.3 runtime. Each run used a fresh Editor process; the actual graphics API, loaded shim path and hash, strict PID cross-check, Native initialization/display/cleanup, natural Editor exit, Windows crash events, and settings restoration gates were checked. The production shim is 302080 bytes with SHA-256 `C864A363AC441C77497D4302FF25CB48FA2D6EA194B6FB8A73BAE49DC522FEE2`; the loaded module matched this package artifact. Native render/present counters progressed in Preview and runtime for every mode. The connected test device returned two cells in the calibrated table.

The final matrix run directories are:

- Automatic: `device-urp-d3d12-automatic-discovery-order`, `device-hdrp-d3d12-automatic-finalorder`, `device-urp-d3d11-automatic-finalorder`, `device-hdrp-d3d11-automatic-finalorder`.
- ClassicRgb: `device-urp-d3d12-classicrgb-finalorder-fix1`, `device-hdrp-d3d12-classicrgb-finalorder`, `device-urp-d3d11-classicrgb-finalorder`, `device-hdrp-d3d11-classicrgb-finalorder`.
- CalibratedCells: `device-urp-d3d12-calibratedcells-finalorder`, `device-hdrp-d3d12-calibratedcells-finalorder`, `device-urp-d3d11-calibratedcells-finalorder`, `device-hdrp-d3d11-calibratedcells-finalorder`.

Structured per-run results and the combined condition list are in `.local/windows-subpixel-validation-20261011/final-summary.json`; raw XML, Editor logs, native traces, module samples, event checks, and settings cleanup records are retained in sibling run directories. The four Automatic runs preceded a test-only fix that persists the expected mode through Unity's Play Mode domain reload. Automatic's value remains zero, and the corrected fixture passed in all four ClassicRgb conditions; no production code or Native artifact changed for that fixture fix.

## Failed attempts retained

Failures are separate from the final passing matrix and were not overwritten:

- `unit-ui-editmode-d3d11` failed to compile with CS0221; the corrected focused EditMode run passed 16/16.
- `device-urp-d3d11-automatic` failed before mode selection while Native image validation was pending. The bounded fixture wait was added without bypassing the real validation or shutdown-pending gates. `device-urp-d3d11-automatic-admissionwait` later passed, but it preceded the final discovery-order change and is historical.
- Initial D3D12 Automatic runs reported a generic module-snapshot error. After metadata Begin triggered SDK load/init, a subsequent image-identity capture failed after five CreateToolhelp32Snapshot attempts returned Win32 error 24 (ERROR_BAD_LENGTH); this was before render-worker/device startup. The Managed discovery order was then changed to validate the editor, load only the side-effect-free render-event export, capture a fresh validated image identity, and verify that export's owning module base before beginning metadata. No cached or partial module list is accepted. The final D3D12/URP/Automatic run passed after that ordering change.
- The first D3D12/URP/ClassicRgb run reached runtime rendering but failed because the fixture's iterator-local expected mode returned to `Automatic` after Play Mode domain reload while the actual mode remained `ClassicRgb`. The fixture now persists and validates the expected enum through `SessionState`; the failed run remains at `device-urp-d3d12-classicrgb-finalorder`, and the corrected run is `device-urp-d3d12-classicrgb-finalorder-fix1`.
- An initial Native CTest run failed 5/19 before Native fixes and fixture migrations. The first metadata loopback success-path failure remains unexplained despite five independent later repetitions and a passing final suite. The preview-lease baseline comparison, source-generation retirement, pause behavior, V1 shutdown fixtures, and D3D12 option propagation have their own corrective changes and passed in the final Native suite; these are not evidence that the unexplained metadata event was fixed.

## Scope and limits

These tests establish the selected modes, device metadata, requested/effective status, Native D3D11 backend, Preview-to-Play lifecycle, render/present counter progression, and cleanup on the recorded Windows device and APIs. They do not establish optical quality, all-view visual inspection, all display models, Windows Player behavior for the new output path, broad scene rendering quality, or macOS behavior. Earlier Windows Player, color, and device results used a different shim and remain historical. The package changes were uncommitted in the validation working tree; these results are not a release claim.

See the [calibration contract](bridge-subpixel-calibration.md) and [platform matrix](ja/unity-feature-platform-matrix.html). The machine-readable rollup is retained at `.local/windows-subpixel-validation-20261011/final-summary.json` alongside the raw per-run evidence.
