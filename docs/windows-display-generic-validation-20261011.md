# Windows generic display metadata validation (2026-10-11)

## Scope

This record checks that Windows Bridge display metadata is consumed by display ordinal and then retained in connected-display profiles and quality settings. It does not claim real-device coverage for every Looking Glass model. The native and managed fixtures are synthetic and are not named after physical products.

The source base is `62d0e0cb380bf663e4b33d2ad2095b09e4e872f9`; the documentation checkout is `b185d5ba63933a7a18c6ac971865a5397e97510a` (documentation-only). The validation used local, uncommitted source/test changes. No production runtime or packaged DLL change was made for this metadata check.

## Generic implementation paths

- `Native/common/bridge_metadata.cpp` obtains the SDK display ID at the requested ordinal, then queries dimensions, view cone, and default Quilt metadata using that ID. `ReadPhysicalDisplay` passes the requested display ID to the calibration callback. Neither path selects a model by name or resolution.
- `Packages/com.zabaglione.lookingglass.viewweave.bridge/Runtime/Platforms/Windows/BridgeDisplayDiscovery.cs` builds profiles from each snapshot item's SDK `profile.index` and physical calibration metadata. `BridgeDisplayProfile` validates geometry and converts pixel dimensions plus DPI to physical dimensions.
- `ViewWeaveBridgeSession.CreateQualityProfile` passes the connected profile's Quilt dimensions, grid, aspect, and view cone to Preview, Balanced, and Native profiles. `QuiltQualityProfile.ApplyTo` copies those settings to `QuiltSettings`. The Unity fixture checks device-specific preset dimensions as independent expected constants for Preview and Balanced and checks the original Quilt dimensions for Native.

These paths support the conclusion that Windows Bridge metadata is generic rather than restricted to 16 Spatial. The fixture results verify the metadata flow and settings behavior; they do not establish operation on any other physical model.

## Synthetic fixtures

The native metadata fixture uses nonsequential IDs and distinct metadata:

| Synthetic ID | Screen | Quilt | Grid | View cone | DPI |
| --- | --- | --- | --- | --- | --- |
| 7 | 1920 x 1080 | 4000 x 4000 | 5 x 9 | 35 | 120 |
| 42 | 1440 x 2560 | 4092 x 4092 | 11 x 6 | 54 | 300 |
| 301 | 5120 x 2880 | 8192 x 8192 | 8 x 6 | 50 | 190 |

The native regression iterates by ordinal, checks that every metadata callback receives the associated SDK ID, and validates the returned calibration, including failure-output clearing and the existing ABI and changing-count checks.

The managed `GenericConnectedDisplayProfilesPreserveDeviceMetadata` test uses the same three synthetic profiles. For each it checks calibration and DPI-derived physical dimensions, then applies Preview, Balanced, and Native to a default 4096 x 4096, 8 x 6, 48-view `QuiltSettings`. It verifies that aspect, view cone, grid, view count, and preset Quilt dimensions are retained or replace the defaults as appropriate. These cases allocate no large render targets.

## Results

- Native: `lookingglass_metadata_test` rebuilt in Release; CTest `-R ^lookingglass_metadata_test$` passed 1/1.
- Unity: 6000.6.4f1, PlayMode test assembly, URP, `-force-d3d11`, NVIDIA GeForce RTX 3090 Ti. The selected nine test names expand to 11 NUnit cases because the new test has three synthetic-profile cases. Result: 11 passed, 0 failed, 0 skipped; CLI exit 0; the Editor log reports code 0; the Editor exited naturally. No Application event 1000/1001 was observed, and tracked Unity settings were clean before and after the run.
- Unity test result: `.local/windows-display-generic-20261011/metadata-profile-tests-d3d11-playmode/test-results.xml`. Run metadata and Editor log are in the same evidence directory.
- A prior EditMode invocation returned zero cases and is not counted as validation. The test asmdef has no Editor-only platform restriction and runs in PlayMode; the separate Editor test assembly explicitly includes only `Editor`.

The Unity run did not opt into device output. It verifies synthetic profile construction and settings application, not Bridge runtime loading, display enumeration on a physical device, rendered output, or optical quality. Other physical display models remain unverified.
