[Japanese](README.ja.md)

# Pipeline Smoke Tests

> Audience: Unity and rendering developers. This page describes test setup and execution; test results apply only to their documented run conditions. For current support and setup, see [Compatibility](../../docs/compatibility.md) and [Getting started](../../docs/getting-started.md). Browse the [Technical Guide](../../docs/technical-guide.md) or [User Guide](../../docs/user-guide.md).

`Tests/Pipelines/Unity` is the Unity project used to validate Core, URP, HDRP, and Bridge. On a fresh clone, run `python tools/prepare_sample_tests.py` from the repository root before opening the project in Unity Hub. This copies Bridge package Samples to the ignored path `Tests/Pipelines/Unity/Assets/Samples/ViewWeaveBridge`. In Unity Hub, choose `Add`, open this folder, and select Unity 6000.6.4f1.

## Run from the Test Runner

1. In the Unity Editor, open `Window > General > Test Runner`.
2. Select the `PlayMode` tab.
3. Click `Run All`.

No fixed scene is registered in Build Settings. PlayMode tests create fixture scenes at runtime and destroy them after execution. GPU fixtures require a real graphics device; Windows-specific fixtures also require Windows. The targeted `ViewWeave.GraphicsCompatibility` suite runs on macOS Metal. `Run All` includes known VFX compatibility failures; inspect the breakdown in the result XML.

The main PlayMode test classes are:

| Class | Purpose |
| --- | --- |
| `BridgeSessionValidationTests` | Managed checks for session state, timeout, completion watchdog, and quality reconfiguration |
| `SrpAdditionalValidationTests` | Basic URP/HDRP rendering, MSAA, and post-processing conditions |
| `QuiltRendererMsaaLifecycleTests` | 1x/2x/4x/8x, resolve, resize, disable, and Deferred fallback |
| `GrdValidationPlayModeTests` | GPU Resident Drawer conditions in URP/HDRP |
| `VfxGraphicsCompatibilityTests` | URP/HDRP VFX compatibility matrix |
| `HdrpVfxPrimingValidationTests` | Diagnostics for HDRP same-camera priming |
| `HdrpTransientVolumePreparationTests` | HDRP isolated histories, shared GPU exposure, resource limits, and lifecycle |
| `UrpTaaPlatformTests` | URP Quilt TAA on Metal and supported Windows D3D11/D3D12 Editor configurations, including per-view history and lifecycle cases |
| `WindowsHdrpEffectPixelTests` | Explicit Windows-only HDRP effect pixel characterization for D3D11/D3D12; diagnostic suite, not a default full-suite gate |

To run the same PlayMode suite from the CLI, use this command from the repository root:

```powershell
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --report-format junit --output "$PWD/.local/test-results/playmode.xml" --timeout 1200 -- -force-d3d11
```

A result XML with a test count of 0 is not a successful run. For the result breakdown, known VFX failures, and D3D12 coverage boundaries, see [docs/windows-validation.md](../../docs/windows-validation.md).

### Windows HDRP effect characterization

The Windows-only HDRP fixture is marked NUnit `Explicit`; the ordinary full suite does not execute it. Run this diagnostic fixture explicitly on the Unity Editor with Direct3D 11 or Direct3D 12. Its cases are `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.FxaaChangesDiagonalEdgePixels`, `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.SmaaChangesDiagonalEdgePixels`, `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.TaaAdvancesIndependentViewHistoriesAndChangesPixels`, `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.MotionBlurChangesMovingObjectPixels`, `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.SsrChangesScreenSpaceReflectionPixels`, `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.SsgiChangesIndirectReceiverPixels`, `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.VolumetricFogChangesScatteringPixels`, and `ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.VolumetricCloudsChangeSkyPixels`. Run one API per Editor invocation, for example:

```powershell
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --filter ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests --output "$PWD/.local/hdrp-effects/results.xml" --timeout 1200 -- -force-d3d12
```

To run a single case, use its fully qualified test name as the filter, for example `--filter ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.FxaaChangesDiagonalEdgePixels`. This is an effect-characterization fixture, not a general HDRP guarantee. Explicit tests reported as skipped or not run are not passes; current results include failed effect pixel gates and must be read from the [dated Windows feature record](../../docs/windows-current-feature-validation-20261011.md). Do not interpret a fixture failure as product incompatibility or report the full fixture as passing.

## Player Validation Using Generated Scenes

Only validations that build a Player use an Editor method to create a temporary scene. These are build entry points; do not edit them manually as fixed scenes.

| Editor method | Generated scene | Purpose |
| --- | --- | --- |
| `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.Build` | `Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.unity` | Bridge Worker validation Player |
| `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.BuildDemo` | `Assets/BridgeWorkerValidationGenerated/ViewWeaveDemo.unity` | 48-view demo Player |
| `ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp` | `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity` | URP sample Player |
| `ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildHdrp` | `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity` | HDRP sample Player |
| `ViewWeave.PlayerValidation.Editor.PlayerValidationBuilder.Build` | `Assets/PlayerValidationGenerated/PlayerValidation.unity` | URP GRD Player validation without Bridge |
| `ViewWeave.VfxPlayerValidation.Editor.VfxPlayerValidationBuilder.Build` | `Assets/VfxPlayerValidationGenerated/VfxPlayerValidation.unity` | VFX Player validation |
| `ViewWeave.MacHdrpHistoryValidation.Editor.MacHdrpHistoryValidationBuilder.Build` | `Assets/SamplePlayerValidationGenerated/MacHdrpHistory/Validation.unity` | macOS Metal HDRP histories/exposure without Bridge |
| `ViewWeave.MacUrpHistoryValidation.Editor.MacUrpHistoryValidationBuilder.Build` | `Assets/SamplePlayerValidationGenerated/MacUrpHistory/Validation.unity` | macOS Metal URP TAA histories without Bridge |

Bridge demo view-index validation uses `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run`. It checks that all 48 tile indices are unique and covered without a Bridge connection, then saves the result under `.local`. For hardware validation, do not assume view 0 or 47 corresponds to the left or right edge; record the indices actually seen from each side.

To create a generated scene and build a Player, pass the Editor method arguments through the Unity CLI. For example, build a URP sample Player with:

```powershell
unity run "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 -- -executeMethod ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp -buildOutput "$PWD/.local/player/SampleUrp.exe"
```

The builder accepts `-buildGraphicsApi d3d11` or `-buildGraphicsApi d3d12`, and `-buildDevelopment true` or `-buildDevelopment false`; omitted values default to D3D11 Development. For example, add `-buildGraphicsApi d3d12 -buildDevelopment false` to build a D3D12 Release sample. Unknown values fail the build method. The builder temporarily creates `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`, sets the requested graphics API, Linear color space, and Run In Background, then restores its settings. The built Player requires the Bridge runtime to start. Current Windows Player evidence and its narrow scope are in the [dated feature record](../../docs/windows-current-feature-validation-20261011.md).

## macOS Metal History, Exposure, and URP TAA Validation

From the repository root, run the focused PlayMode regression with a real Metal device:

```bash
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --filter ViewWeave.GraphicsCompatibility --output "$PWD/.local/hdrp-lighting/graphics.xml" --timeout 1200 -- -force-metal
```

The rendering-only HDRP Player fixture runs without Bridge. Use an isolated batch Editor; the builder restores its temporary pipeline, color-space, graphics-API, and scene settings. Generated assets and Players stay ignored. Build Development first, then repeat with a separate output directory and `Release` in place of `Development`:

```bash
unity build "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --target StandaloneOSX --execute-method ViewWeave.MacHdrpHistoryValidation.Editor.MacHdrpHistoryValidationBuilder.Build --output-path "$PWD/.local/hdrp-lighting/player-development/ViewWeaveHdrpValidation.app" --allow-dirty-build --timeout 1800 --args '-force-metal -validationConfiguration Development'
mkdir -p "$PWD/.local/hdrp-lighting"
"$PWD/.local/hdrp-lighting/player-development/ViewWeaveHdrpValidation.app/Contents/MacOS/PipelineSmoke" -batchmode -force-metal -screen-width 320 -screen-height 240 -validationReport "$PWD/.local/hdrp-lighting/player-development.json" -logFile "$PWD/.local/hdrp-lighting/player-development.log"
```

The 2026-10-11 Metal PlayMode `GraphicsCompatibility` run passed 58/58 with zero skips, including eight URP adapter tests. Those tests cover four-view history accumulation, separate per-view RenderTextures and pixel comparisons, stable frame-version checks, view pruning, resize/restart, budget cleanup, and scripted Volume preservation. It is Editor PlayMode evidence. Separate bounded Metal Edit Mode and rendering-only Development/Release Player runs are covered below; neither covers Windows or physical-device optical TAA. See the [URP dated record](../../docs/urp-view-history-validation-20261011.md).

### URP Edit Mode and Player checks

The Editor-only Edit Mode fixture requires a Metal host and exercises disabled view cameras, TAA accumulation, pause/resume, and the public local-preview service. Run it from the repository root:

```bash
mkdir -p "$PWD/.local/urp-history"
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode EditMode --filter ViewWeave.UrpHistoryEditor.UrpHistoryEditModeTests.DisabledViewsAccumulateOutsidePlayModeAndRestartAfterPause --report-format junit --output "$PWD/.local/urp-history/editmode.xml" --timeout 1200 -- -force-metal
```

Build the rendering-only macOS Player in an isolated batch Editor. The builder selects Metal, creates a temporary scene, and restores the Editor's graphics and scene settings afterward. Start with Development:

```bash
mkdir -p "$PWD/.local/urp-history/player-development"
unity build "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --target StandaloneOSX --execute-method ViewWeave.MacUrpHistoryValidation.Editor.MacUrpHistoryValidationBuilder.Build --output-path "$PWD/.local/urp-history/player-development/ViewWeaveUrpValidation.app" --allow-dirty-build --timeout 1200 --args '-force-metal -validationConfiguration Development'
"$PWD/.local/urp-history/player-development/ViewWeaveUrpValidation.app/Contents/MacOS/PipelineSmoke" -force-metal -screen-width 640 -screen-height 480 -validationReport "$PWD/.local/urp-history/player-development.json" -logFile "$PWD/.local/urp-history/player-development.log"
```

Repeat the Player commands after replacing every `player-development` path with `player-release` and changing the builder option to `-validationConfiguration Release`. Read the Player exit code, JSON report, and log together; the report also writes PNGs used for TAA-on/off and moving-geometry comparisons. These rendering-only fixtures do not test Bridge output or physical-device optics. See the [URP dated record](../../docs/urp-view-history-validation-20261011.md) for run conditions and results.

The HDRP fixture checks independent/stable Cameras, retained HDRP TAA settings, Fixed exposure compensation, Automatic Histogram light response, non-magenta red Quilt pixels, owned-resource retirement, and a rejected 1 MB warmup budget. Read the JSON and process exit code together. It uses elapsed-time bounds for GPU completion and a Cube covering most of the metering view: a mostly black histogram cannot substantiate a large EV-change expectation. This does not validate Bridge output, physical optics, HDRP TAA pixel correctness, or Windows. See the [dated record](../../docs/hdrp-view-history-validation-20261010.md) for results and retained warnings.

## GPU Resident Drawer Validation

`GrdValidationPlayModeTests` validates 1, 8, and 48 views in URP/HDRP. The GRD ON condition also checks profiler counters. Result PNGs and JSON are written to `.local/grd-validation/`, but do not decide pass/fail from evidence files alone; inspect the Unity test result and log. An Editor result does not guarantee Player threading, long-duration operation, or performance.

## Player and Editor Boundaries

Normal Test Runner execution runs in Unity Editor PlayMode. Hardware Player validation with a Bridge connection is a separate run using a Bridge sample or one of the generated scenes above. For Edit Mode authoring, an explicitly started `ViewWeaveRig` preview can display locally or send output to a device through the Windows D3D11/D3D12 Bridge route; it is separate from a regular Bridge Session, which still requires Editor Play or a Player. See the [Quilt authoring guide](../../docs/quilt-authoring.md) for its setup and support boundaries. The PlayMode suite does not validate this Editor preview.
