[Japanese](README.ja.md)

# Pipeline Smoke Tests

`Tests/Pipelines/Unity` is the Unity project used to validate Core, URP, HDRP, and Bridge. On a fresh clone, run `python tools/prepare_sample_tests.py` from the repository root before opening the project in Unity Hub. This copies Bridge package Samples to the ignored path `Tests/Pipelines/Unity/Assets/Samples/ViewWeaveBridge`. In Unity Hub, choose `Add`, open this folder, and select Unity 6000.6.4f1.

## Run from the Test Runner

1. In the Unity Editor, open `Window > General > Test Runner`.
2. Select the `PlayMode` tab.
3. Click `Run All`.

No fixed scene is registered in Build Settings. PlayMode tests create fixture scenes at runtime and destroy them after execution. Fixtures that require a GPU must run on a Windows machine with a GPU. `Run All` includes known VFX compatibility failures; inspect the breakdown in the result XML.

The main PlayMode test classes are:

| Class | Purpose |
| --- | --- |
| `BridgeSessionValidationTests` | Managed checks for session state, timeout, completion watchdog, and quality reconfiguration |
| `SrpAdditionalValidationTests` | Basic URP/HDRP rendering, MSAA, and post-processing conditions |
| `QuiltRendererMsaaLifecycleTests` | 1x/2x/4x/8x, resolve, resize, disable, and Deferred fallback |
| `GrdValidationPlayModeTests` | GPU Resident Drawer conditions in URP/HDRP |
| `VfxGraphicsCompatibilityTests` | URP/HDRP VFX compatibility matrix |
| `HdrpVfxPrimingValidationTests` | Diagnostics for HDRP same-camera priming |

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

Bridge demo view-index validation uses `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run`. It checks that all 48 tile indices are unique and covered without a Bridge connection, then saves the result under `.local`. For hardware validation, do not assume view 0 or 47 corresponds to the left or right edge; record the indices actually seen from each side.

To create a generated scene and build a Player, pass the Editor method arguments through the Unity CLI. For example, build a URP sample Player with:

```powershell
unity run "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 -- -executeMethod ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp -buildOutput "$PWD/.local/player/SampleUrp.exe"
```

This method temporarily creates `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`, sets Direct3D11, Linear, and Run In Background, and builds the Player. The built Player requires the Bridge runtime to start.

## GPU Resident Drawer Validation

`GrdValidationPlayModeTests` validates 1, 8, and 48 views in URP/HDRP. The GRD ON condition also checks profiler counters. Result PNGs and JSON are written to `.local/grd-validation/`, but do not decide pass/fail from evidence files alone; inspect the Unity test result and log. An Editor result does not guarantee Player threading, long-duration operation, or performance.

## Player and Editor Boundaries

Normal Test Runner execution runs in Unity Editor PlayMode. Hardware Player validation with a Bridge connection is a separate run using a Bridge sample or one of the generated scenes above. There is no procedure for connecting the Bridge from the Editor.
