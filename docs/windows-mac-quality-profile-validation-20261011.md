# Windows connected-display quality-profile validation

This record covers the Windows re-evaluation of the shared connected-display quality-profile path at checkout `fd5f29e1eac499ca0b14bed4f1b925ef693277f3`. The common nine-case suite ran before the device-fixture edit. The four device runs used one test-only working-tree change to `BridgeEditorPreviewGpuTests.cs`, recorded below. These are validation results for the checkout and fixture state, not a release claim.

## Results

The common input and quality PlayMode selection passed 9/9 tests on Unity 6000.6.4f1 with D3D11. The nine cases were `GenericConnectedDisplayProfilesPreserveDeviceMetadata` (3 synthetic metadata cases), `InvalidProfilePresetIsRejectedBeforeProfileUse` (1), `QualityPresetKeepsPreviewBalancedAndNativeDimensionsDistinct` (1), and the four `SharedQuiltInputPolicy` acceptance/rejection cases. This selection validates synthetic profile conversion and complete-grid input rules; it does not use a connected display or claim URP/HDRP rendering. The preflight recorded the relevant source-test hash as `7297076E9E5EDB07F5F71C1F325D0B4112E7220A46E3906F4466C7C481AB3FC2`; the per-run source manifest covers only the GPU fixture file.

The device Preview-to-Play fixture passed once for each of these independent conditions:

| Pipeline | API | Quality preset | Result |
| --- | --- | --- | --- |
| URP | D3D11 | Native | 1/1, skip 0 |
| HDRP | D3D11 | Native | 1/1, skip 0 |
| URP | D3D12 | Native | 1/1, skip 0 |
| HDRP | D3D12 | Native | 1/1, skip 0 |

All four runs used subpixel mode Automatic, Unity 6000.6.4f1, and an NVIDIA GeForce RTX 3090 Ti. The Bridge returned a 7680x4320 raw quilt with an 8x6 grid and 48 views for the connected display. The test derives expected Native output dimensions from each run's connected profile rather than hard-coding that device geometry. It checked the Editor Preview and runtime RenderTexture dimensions, full layout dimensions/grid/view count, aspect and view cone. It also checked that the source Rig profile remains unchanged during Editor Preview, persisted the Bridge profile's primitive metadata across the Play Mode reload, and compared the restored metadata with the fresh Native runtime profile before deriving the runtime quality expectation.

For every device run, the actual graphics API matched the requested API. The exact Editor PID matched the Unity log, native trace, module sampler, and loaded-module record. The loaded shim came from the package path and matched SHA-256 `C864A363AC441C77497D4302FF25CB48FA2D6EA194B6FB8A73BAE49DC522FEE2`. NUnit passed 1/1 with zero skips; Unity CLI and Editor exited naturally with code 0. Native load, initialization, one-display discovery, zero pending work after Stop, and uninitialization passed. Windows Application events 1000/1001 and crash reports were absent. Unity-generated tracked settings were restored to their clean-at-start state.

The separate Native fresh transport configuration/build at the same source head passed 15/15 CTest cases with zero failures and zero skips. This Native result is a separate scope from the nine common PlayMode cases and four Unity device conditions; counts are not combined. The packaged shim was unchanged by this validation. Package, URP distribution, and HDRP distribution copies are each 302,080 bytes and have the same SHA-256 shown above.

## Failure history and fixture correction

The first URP/D3D11 device run is preserved separately and is not counted among the four passing conditions. It used the pre-correction fixture source hash `0A3CA279FFB6089FBB8D62326F5FCF807E8D4F5D7DD70748434112910C24009F`. Its Preview phase passed the profile-derived RenderTexture and layout checks. After `EnterPlayMode`, Unity reloaded the test domain and reset the iterator's local readonly `BridgeDisplayProfile`; constructing the expected quality profile from that default value raised `ArgumentOutOfRangeException` (`profile`). The run reported one failed test, zero skips, and Unity CLI exit code 8. Native shutdown, process identity, module hash, crash-event, and settings-cleanup gates passed.

The fixture now serializes a schema-versioned primitive DTO to `SessionState` before entering Play Mode. It rejects a missing/invalid snapshot, reconstructs the readonly profile through `BridgeDisplayProfile.TryCreateCached`, compares every saved profile field with the fresh runtime Native profile, and derives the runtime expected quality profile from the restored metadata. This is a test-fixture correction; the production quality helper and Native DLL were not changed for it.

## Boundaries

The four connected-device tests cover only Automatic subpixel output with the Native quality preset, for one connected device on Windows URP/HDRP and D3D11/D3D12. The synthetic nine-case PlayMode suite covers the common profile helper's Preview, Balanced, and Native calculations, but those cases do not exercise a connected device. This work does not establish Windows Player behavior, other devices or profile geometries, manual subpixel modes, image or optical quality, or every project configuration. The macOS `open_v4` artifact and Mac validation remain the separate evidence in [the macOS unified subpixel report](macos-unified-subpixel-validation-20261011.md); these Windows runs do not extend or replace that result.

The four passing device runs used the corrected test-only `Tests/Pipelines/Unity/Assets/BridgeSessionValidation/Editor/BridgeEditorPreviewGpuTests.cs` change (SHA-256 `A173BFEBA723789E00C6D24AC6AA3EBE1356B57EC0A9C9C01043F640B0B58ED6`). The earlier failed attempt used the distinct pre-correction hash above. The common PlayMode run preceded both device-fixture versions; its preflight hash is stated separately. A portable selected-metadata record and six NUnit XML result files are available in [`validation/windows-mac-quality-profile-20261011.json`](validation/windows-mac-quality-profile-20261011.json). That JSON records hashes and local evidence paths for the source manifest, launch metadata, summary, Editor logs, native traces, module samples, and XML. Raw Editor logs, launch commands, native traces, and module samples remain under `.local/windows-mac-quality-validation-20261011/`; only their paths and SHA-256 values are shared, avoiding raw command-line or device-identity data. The portable NUnit XML copies remove the _PID property and replace the user-profile path prefix with <USER_PROFILE>; case names, counts, and outcomes were checked unchanged. Trailing padding was also removed from three stack-trace lines in the shared failed-run XML; the local raw copy is unchanged. The local raw XML copies remain unchanged and are separately hashed.
