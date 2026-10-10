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
| `UrpTaaPlatformTests` | Ordinary URP Camera TAA on Metal, and the existing Quilt TAA guard; this does not enable Quilt TAA |

To run the same PlayMode suite from the CLI, use this command from the repository root:

```powershell
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --report-format junit --output "$PWD/.local/test-results/playmode.xml" --timeout 1200 -- -force-d3d11
```

A result XML with a test count of 0 is not a successful run. For the result breakdown, known VFX failures, and D3D12 coverage boundaries, see [docs/windows-validation.md](../../docs/windows-validation.md).

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

Bridge demo view-index validation uses `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run`. It checks that all 48 tile indices are unique and covered without a Bridge connection, then saves the result under `.local`. For hardware validation, do not assume view 0 or 47 corresponds to the left or right edge; record the indices actually seen from each side.

To create a generated scene and build a Player, pass the Editor method arguments through the Unity CLI. For example, build a URP sample Player with:

```powershell
unity run "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 -- -executeMethod ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp -buildOutput "$PWD/.local/player/SampleUrp.exe"
```

This method temporarily creates `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`, sets Direct3D11, Linear, and Run In Background, and builds the Player. The built Player requires the Bridge runtime to start.

## macOS Metal History and Exposure Validation

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

The fixture checks independent/stable Cameras, retained HDRP TAA settings, Fixed exposure compensation, Automatic Histogram light response, non-magenta red Quilt pixels, owned-resource retirement, and a rejected 1 MB warmup budget. Read the JSON and process exit code together. It uses elapsed-time bounds for GPU completion and a Cube covering most of the metering view: a mostly black histogram cannot substantiate a large EV-change expectation. This does not validate Bridge output, physical optics, HDRP TAA pixel correctness, or Windows. See the [dated record](../../docs/hdrp-view-history-validation-20261010.md) for results and retained warnings.

## GPU Resident Drawer Validation

`GrdValidationPlayModeTests` validates 1, 8, and 48 views in URP/HDRP. The GRD ON condition also checks profiler counters. Result PNGs and JSON are written to `.local/grd-validation/`, but do not decide pass/fail from evidence files alone; inspect the Unity test result and log. An Editor result does not guarantee Player threading, long-duration operation, or performance.

## Player and Editor Boundaries

Normal Test Runner execution runs in Unity Editor PlayMode. Hardware Player validation with a Bridge connection is a separate run using a Bridge sample or one of the generated scenes above. For Edit Mode authoring, an explicitly started `ViewWeaveRig` preview can display locally or send output to a device through the Windows D3D11/D3D12 Bridge route; it is separate from a regular Bridge Session, which still requires Editor Play or a Player. See the [Quilt authoring guide](../../docs/quilt-authoring.md) for its setup and support boundaries. The PlayMode suite does not validate this Editor preview.
