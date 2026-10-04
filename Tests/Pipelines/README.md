# Pipeline smoke tests

`Unity/Assets/Editor/QuiltPipelineSmokeTests.cs` is an Editor test/execute-method helper for a Unity 6.6.4f1 project that imports the core package and either URP or HDRP adapter. It creates a low resolution cube scene, renders a small quilt, and checks that tiles contain pixels and differ across views. The test also checks that the source camera transform and pipeline anti-aliasing setting are restored after the render scope. Run from Unity with `-executeMethod QuiltPipelineSmokeEntryPoints.RunUrp` or `-executeMethod QuiltPipelineSmokeEntryPoints.RunHdrp`.

The BridgeWorker ViewWeave demo can overlay a large `00` through `47` index in each view tile with `--view-index-debug 1`; use `--view-index-debug 0` to restore the normal quilt. Run `LookingGlass.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run` in the Unity Editor to render all 48 tiles without Bridge, validate unique glyph hashes and coverage, and save `.local/windows-sdk-demo/view-index-debug-proof.png`. During optical review, record the index seen from the left and right physical sides, rather than assuming that view 0 or view 47 is a particular side.

The smoke test requires a GPU and is intended for a local Editor batch invocation. macOS execution is unverified on this host.

## GPU Resident Drawer validation

`Unity/Assets/GRDValidation/GrdValidationPlayModeTests.cs` runs 14 URP Forward+ and 6 HDRP PlayMode cases for 1, 8, and 48 views with GPU Resident Drawer disabled and enabled. The combined Editor validation has 20 rendering cases. Three additional Core RenderTexture release regression cases cover release, resize, and external active texture preservation, for 23 Editor cases total. The URP matrix includes callbacks before and after `PostLateUpdate.FinishFrameRendering`, plus `QuiltRenderer.autoRender` Automatic cases; HDRP covers Automatic cases. Each uses a fixed 128x96 tile, disables camera occlusion culling, checks visible geometry and view differences, and requires the public `GPU Resident Drawer` profiler counters for enabled cases. Automatic cases also verify disabled/enabled submission counts and `autoRender=false`. HDRP validation uses a global Volume with fixed Exposure, Motion Blur intensity zero, Screen Space Reflection disabled, screen space global illumination disabled, and fog/clouds disabled, plus an HDRP directional light. The fixture waits two frames after setup, renders to initialize the pipeline, waits for warmup, then moves the green target; after the coroutine resumes on the next frame, it reads the output and checks the frame change.

Run the URP cases on Unity 6000.6.4f1 with a GPU:

From the repository root, run:

```powershell
unity test ./Tests/Pipelines/Unity --editor-version 6000.6.4f1 --mode PlayMode --filter LookingGlass.GRDValidation.GrdValidationPlayModeTests.URP --output ./.local/grd-validation/results.xml --timeout 240 -- -force-d3d11
```

PNG and JSON evidence is written under `.local/grd-validation/`. Evidence records the tested configuration and runtime environment, but the evidence files alone do not determine test success; use the Unity test result and log. These runs execute in the Unity Editor batch test host. The Editor result does not establish Player runtime behavior, threading, or performance; the separate Player validation currently covers URP normal-thread graphics only. HDRP Player, performance, Bridge normal-thread timing, and Metal remain pending. A RenderTexture release warning occurs during Windows Player shutdown and is under investigation.
