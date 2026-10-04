# Windows validation status

This document records the current Windows validation boundary. The final D3D11 PlayMode run covers 115 tests; targeted results and experimental failures are listed separately.

## Confirmed

- Target environment: Unity 6000.6.4f1, Windows D3D11.
- Native transport loader lifecycle: 100-cycle test passed.
- Native legacy close-handle cleanup: removed from the current transport path.
- Native DLL provenance: synchronized with the current bridge shim.
- Independent D3D12-to-D3D11 shared-resource probe: passed. This is a native probe only.
- Bridge connection timeout and soak-watchdog handling: normal 600-second and 30-second Player runs completed successfully. Connection hang or stalled completion fault injection in an actual Player remains unexecuted; the managed timeout unit test passes.
- Bridge Player fixed-condition validation: documented URP and HDRP ranges passed under Windows D3D11; this does not establish general device coverage or long-run soak behavior.
- Limited optical review: depth and image quality were good, and the numbered demo was tentatively accepted by the user. All views and all viewing regions have not been exhaustively reviewed.
- Unity D3D11 targeted regressions: SRP additional 6/6, GRD/VolumeStack 27/27, and URP MSAA 1/1. The MSAA case used a 4x view RenderTexture, a 1x resolve texture, resize/disable lifecycle checks, and a non-background interior color consistency check.
- Core/SRP D3D12 smoke: SRP additional 6/6 passed. This covers Core/SRP rendering only; the production Bridge D3D12 backend remains unimplemented.
- Native transport CTest: 3/3 passed. The loader 100-cycle result and DLL provenance checks are also recorded in native evidence.
- Final D3D11 PlayMode integration: 101/115 passed, 14 failed, 0 skipped. Session 15/15, compatibility 37/37, GRD 27/27, SRP additional 6/6, SRP extension 2/2, VFX driver 5/5, and Samples 4/4 passed. The 14 failures are one HDRP same-camera priming case and 13 VFX compatibility cases; the VFX cases did not establish sufficient all-tile effect coverage.
- Final Bridge Player smoke after the Core color-space fix: 30.0108 seconds, completed delta 1786, quality cycle true, restart 120 true, cleanup pending 0, closed state confirmed. The run used native DLL SHA256 `60DA142C...`.
- Earlier Bridge Player soak reference: 600.013836 seconds, completed delta 36082, same native DLL SHA256 `60DA142C...`, quality cycle and restart 120 passed, cleanup pending 0, closed state confirmed. This run used the pre-fix Core Player.

## Remaining limits

- Fault injection of a hung Player connection and full device coverage.

The production Bridge D3D12 backend is not implemented. Unity/Bridge D3D12 Player execution is therefore out of scope until that backend exists.

## Reproduction boundaries

Run Unity validation with Unity 6000.6.4f1, `-force-d3d11`, and the project at `Tests/Pipelines/Unity`. The final integration command is `unity test E:/LookingGlassSDK/Tests/Pipelines/Unity --editor-version 6000.6.4f1 --mode PlayMode --report-format junit --output E:/LookingGlassSDK/.local/windows-improvement/playmode-final-d3d11-rerun.xml --timeout 1200 -- -force-d3d11`. Run native checks from `Native` with the repository's existing CMake and CTest configuration; enable the optional shared-resource probe with `-DLKG_D3D12_SHARED_PROBE=ON`. The D3D12-to-D3D11 result is a probe and does not establish Unity or Bridge D3D12 support.

The historical 82/71/11 integration total is retained in older records only. The current D3D11 total is 101/115 pass with 14 experimental or priming failures as listed above. Multiple devices, macOS Metal, Vulkan, hotplug, distribution signing, and full optical coverage remain outside this validation pass.
