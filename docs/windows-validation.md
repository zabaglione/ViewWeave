# Windows validation status

This document records the current Windows validation boundary. The 123-test D3D11 PlayMode run predates the current VFX color-space guard; current targeted results and experimental failures are listed separately.

## Confirmed

- Target environment: Unity 6000.6.4f1, Windows D3D11.
- Native transport loader lifecycle: 100-cycle test passed.
- Native legacy close-handle cleanup: removed from the current transport path.
- Native DLL provenance: synchronized with the current bridge shim.
- Independent D3D12-to-D3D11 shared-resource probe: passed. This is a native probe only.
- Bridge connection timeout and soak-watchdog handling: normal 600-second and 30-second Player runs completed successfully, and the production connection, shutdown, and completion watchdogs use a 10-second limit. Dedicated fault-injection Player runs for init delay and draw/release delay passed with native cleanup and reconnection evidence. These runs use the repository fake runtime to delay native calls; they do not establish behavior for physical hotplug or an arbitrary hardware connection hang.
- Bridge Player fixed-condition validation: documented URP and HDRP ranges passed under Windows D3D11; this does not establish general device coverage or long-run soak behavior.
- Limited optical review: depth and image quality were good, and the numbered demo was tentatively accepted by the user. All views and all viewing regions have not been exhaustively reviewed.
- Previous targeted regressions: SRP additional 6/6, GRD/VolumeStack 27/27, and the original URP MSAA 1/1. The original MSAA case used a 4x view RenderTexture, a 1x resolve texture, resize/disable lifecycle checks, and a non-background interior color consistency check.
- Additional Linear D3D11 MSAA boundary run: 10/10 passed. On this RTX GPU, 2x, 4x, and 8x requests were all supported and matched the device-supported sample count. The 1x paths exercised pipeline sample count 1, `allowMSAA=false`, and Deferred renderer fallback; a lower-capability GPU was not tested. Center-color consistency, supported-MSAA edge differences, resize lifecycle, and cleanup passed. The run logged `colorSpace=Linear`. A dedicated MSAA Player fixture remains unexecuted.
- VFX Graph color-space boundary: Unity Visual Effect Graph 17.6 requires Linear color space. The URP VFX driver rejects Gamma before allocating its driver Camera or RenderTexture and reports an ASCII diagnostic; Gamma VFX rendering is not claimed. See the [official VFX Graph system requirements](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/System-Requirements.html) and [HDRP asset requirements](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/create-an-hdrp-asset.html).
- Current targeted Core/SRP/VFX Editor validation: Gamma evidence `gamma-msaa-8-original-threshold.xml` is 8/8 passed, consisting of seven MSAA lifecycle/pixel cases and one Gamma VFX rejection. Linear evidence `linear-srp-vfx.xml` is 17/17 passed, consisting of 11 SRP cases and six VFX driver cases. These are targeted runs, not a new full-project total; the two evidence files are under `.local/windows-improvement/bridge/`. Gamma MSAA/normal-URP Bridge Player validation remains untested; Gamma VFX is upstream unsupported.
- The first Gamma batch had one unstable MSAA result: the interior RGB absolute-difference sum was 0.960784 against a tolerance of 6/255 (0.0235294), and was followed by an 8/8 rerun at the original threshold. The cause is not established; this record does not attribute it to warmup. The additional cold Gamma pixel check passed 3/3 for 4x, 2x, and 8x in `gamma-urp-msaa-cold.xml`; it covered pixel checks only, without lifecycle or Deferred cases, and did not reproduce the initial large color difference.
- Core/SRP D3D12 targeted validation: 28/28 passed, consisting of SRP additional 10/10 and BridgeSessionValidation 18/18. This covers Core/SRP rendering and managed Session behavior only; the production Bridge D3D12 backend remains unimplemented.
- Native transport CTest: 3/3 passed. The loader 100-cycle result and DLL provenance checks are also recorded in native evidence.
- Historical final D3D11 PlayMode integration before the current guard: 108/123 passed, 15 failed, 0 skipped. Session 18/18, compatibility 37/37, GRD 27/27, SRP additional 10/10, SRP extension 2/2, VFX driver 5/5, and Samples 4/4 passed. The 15 failures are one HDRP same-camera priming case and 14 VFX compatibility cases; the VFX cases did not establish sufficient all-tile effect coverage. The separate final VFX matrix has 20 cases with 5 pass and 15 fail. The clone-request diagnostic recorded 4423 changed pixels for the isolated normal-camera measurement, while the same matrix run recorded 0 for StandardRequest and its enabled-during-submit variant; an isolated rerun passed both normal and StandardRequest at 4423, so the result remains execution-history dependent.
- Final Bridge Player smoke after the production completion watchdog: 30.01309 seconds, completed delta 1787, quality cycle true, restart 120 true, cleanup pending 0, Idle/Closed state confirmed. The run used evidence build GUID `e5a91ffb8d45456aa87aecc2fbd0112f` and native DLL SHA256 `60DA142C...`.
- Earlier Bridge Player soak reference: 600.013836 seconds, completed delta 36082, same native DLL SHA256 `60DA142C...`, quality cycle and restart 120 passed, cleanup pending 0, closed state confirmed. This run used the pre-fix Core Player.

## Remaining limits

- Physical hotplug and arbitrary hardware connection-hang fault injection, plus full device coverage.

The production Bridge D3D12 backend is not implemented. Unity/Bridge D3D12 Player execution is therefore out of scope until that backend exists.

## Reproduction boundaries

Run Unity validation with Unity 6000.6.4f1, `-force-d3d11`, and the project at `Tests/Pipelines/Unity`. The final integration command is `unity test E:/LookingGlassSDK/Tests/Pipelines/Unity --editor-version 6000.6.4f1 --mode PlayMode --report-format junit --output E:/LookingGlassSDK/.local/windows-improvement/final-d3d11-123.xml --timeout 1200 -- -force-d3d11`. Run native checks from `Native` with the repository's existing CMake and CTest configuration; enable the optional shared-resource probe with `-DLKG_D3D12_SHARED_PROBE=ON`. The D3D12-to-D3D11 result is a probe and does not establish Unity or Bridge D3D12 support.

To reproduce the Bridge fault scenarios, first build the dedicated fake runtime after the native full configuration: `cmake --build Native/build-worker-release --config Release --target lkg_fake_bridge_fault_runtime`. Run the existing validation Player with an absolute fake DLL and a unique absolute evidence directory, using one scenario at a time:

```text
BridgeWorkerValidation.exe --runtime-path <absolute fake DLL> --fault-mode init-delay --fault-delay-ms 15000 --evidence-dir <unique absolute dir> --quality preview --grd off --pipeline urp -force-d3d11
BridgeWorkerValidation.exe --runtime-path <absolute fake DLL> --fault-mode draw-release --fault-delay-ms 15000 --evidence-dir <unique absolute dir> --quality preview --grd off --pipeline urp -force-d3d11
```

These commands exercise only the dedicated fake DLL and native-call delays. Their cleanup and reconnection evidence is not evidence of physical display quality, hotplug behavior, or arbitrary hardware connection hangs. Keep the generated evidence under local build output; do not add it to Git.

The historical 82/71/11 and prior 101/115 integration totals are retained in older records only. The current D3D11 total is 108/123 pass with 15 experimental or priming failures as listed above. Multiple devices, macOS Metal, Vulkan, hotplug, distribution signing, and full optical coverage remain outside this validation pass.
