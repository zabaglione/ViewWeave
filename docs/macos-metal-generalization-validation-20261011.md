# macOS Metal calibration generalization — 2026-10-11

This change removes the Go serial allowlist from the owned-window output. Bridge supplies the selected display's dimensions, position, Quilt grid, aspect, view cone, DPI, optical calibration, and RGB/BGR order. The same output component serves URP and HDRP. Usage and supported calibration conditions are in [macOS Metal](macos-metal.md); the [2026-10-10 Go record](macos-metal-validation-20261010.md) remains historical evidence.

## Implemented scope

- Model names and serial prefixes are not admission criteria. `get_device_serial_for_display` is no longer a required symbol.
- Conventional RGB or BGR calibration, `flipX` and inverted views in {0, 1}, no subpixel cell table, and cell-pattern mode zero are accepted. An unrotated, native-resolution extended macOS display matching Bridge's position is required.
- Unsupported cell tables/modes, invalid geometry, flags, nonfinite optical values, converted pitch/tilt overflow and phase-range overflow produce explicit errors. `fringe` is checked for finite values but is not applied as an optical correction. This does not establish compatibility with every Looking Glass product.
- The versioned `lkg_metal_display_open_v2` takes a 76-byte structure with a checked size/version header. The original 60-byte export remains available and rejects calibration that its old shader cannot represent. Update managed code and the native plugin together, then restart Unity.
- Bridge is used for metadata and uninitialized afterward. The owned consumer window, producer completion, consumer completion, frame admission and resource retirement contracts are preserved. Streaming does not read frames back to the CPU.

## Verification

Environment: Unity 6000.6.4f1, URP/HDRP 17.6.0, Apple Silicon arm64, Metal, Looking Glass Bridge 2.6.3. Source baseline is commit `b185d5ba63933a7a18c6ac971865a5397e97510a` plus this generalization change. The bundled native provenance records exact source and binary hashes. Final arm64 dylib SHA-256: `150e3a01ca3acae31e5f25d6d7f60d2dbc21d926078d0cc068c0e5ef4d23cb1b`. The current Go URP Editor test used this installed artifact; the earlier cancellation run preceded only the added phase-overflow rejection guard.

| Check | Result and boundary |
| --- | --- |
| Native Release build / CTest | arm64 build passed, 6/6 tests. Includes existing metadata/worker and finite-experiment tests plus the new calibration and owned-display metadata cases. A host linker warning references an absent Qt library search directory; the plugin does not link Qt. |
| Synthetic calibration acceptance | Five profiles span portrait, landscape and 8K dimensions with 45/48/66/72-view grids. RGB/BGR, both horizontal flip flags and both view directions pass. No device identity is supplied. These are synthetic profiles, not measurements of those products. |
| Invalid and unsupported calibration | Unknown cell mode/table, invalid dimensions/grid/flags, nonfinite values and conversion/phase overflow are rejected. The phase overflow regression uses rawPitch=2e35, slope=0.1 and DPI=1. |
| Native metadata integration | A fake SDK without a serial export reaches the screen-match check for RGB and BGR/flip profiles. Unknown mode stops before screen selection. Legacy client rejection and unchanged short-buffer canaries pass. No fake profile is presented to a physical screen. |
| GPU interlace calculation | Gamma and Linear each passed 1,944 RGB-channel samples (8 combinations × 243) against an independent double-precision CPU phase/view reference with asymmetric per-tile colors. Exercises RGB/BGR, image flip and inverted views; the first case also checks the shader's default RGB order for legacy material clients. RGB/flip=0 preserves the previous formula. This proves calculation, not physical lenticular alignment. |
| HDRP project compilation / common shader | The same GPU calculation passed 1,944 Linear samples in `Tests/Pipelines/Unity`, which contains HDRP and URP packages. This is compilation and common-shader proof, not HDRP device output. |
| Connected Go native session | Versioned managed/native metadata ABI passed three 1440×2560 open/admit/busy-drop/cancel/close/restart cycles, including a late cancelled event. No valid frame is presented in this test. |
| Current Go URP Editor continuous output / GUI | Two Play/Stop cycles with Domain Reload each reached 121 submissions / 120 completed presentations. Process-wide retirement admission was exercised; each Stop reached retained=0. Rig validation passed, Missing Script count=0, and output error was empty. Game View showed the cube/sphere/floor without pink materials; Hierarchy and Inspector references were checked; Console displayed 0 errors and 0 warnings. The test Editor exited after a scripted request. This is short URP runtime/GUI evidence, not physical lenticular acceptance or HDRP device-output proof. |
| Non-Go physical optical output | Unverified. Synthetic acceptance and shader calculations do not prove a non-Go display's physical orientation, depth, brightness or optical alignment. |
| Repository / distribution / HTML | Repository validation passed with 0 errors and 0 warnings. Both generated distributions passed `--check`; the native binary, managed component and shader match the source package. All 60 native source hashes match provenance. The HTML matrix passed structural, local-link, anchor and numbered-source checks (3 tables, 53 rows, 14 sources). |

## Reproduction and artifacts

Run `python3 tools/build_macos_plugin.py` for the native build/tests and provenance. In `Tests/Metal/Unity`, run `MetalShaderValidation.UseGamma` or `UseLinear`, restart the Editor for the selected color space, and run `MetalShaderValidation.Run`. `MetalNativeSessionValidation.Run` exercises the versioned connected-device cancellation path. Restore the original color-space setting afterward.

Raw logs and downloaded primary references are ignored under `.local/metal-generalization`; generated fixture scenes and copied validation scripts are ignored test artifacts. Package-native binaries/provenance, source tests and this record are managed repository content. Regenerate both distributions with `python3 tools/build_upm_distribution.py` after the final source/native update.

Final GPU logs are `shader-linear-final.log`, `shader-gamma-final.log` and `hdrp-shader-final.log` under that ignored directory. These runs include the final default RGB material property. The preceding Go runtime/GUI check used the same native artifact and explicit channel-order setup. Unity-generated package-lock and test-project settings changes were restored after the runs.

Calibration conversion follows the mathematical relationships in the Apache-2.0 [official WebXR configuration at b44ed27](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts). `flipX` negates tilt and subpixel step and mirrors image X; the Bridge [native reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference) specifies `flip_x` and the 0/2 red/blue indices. No HoloPlay shader source or Bridge SDK binary is bundled or ported.
