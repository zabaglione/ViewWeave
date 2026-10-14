[日本語](ja/vfx-graph.md)

# Using VFX Graph with URP/HDRP

[User Guide](user-guide.md) · [Technical Guide](technical-guide.md)

## Current user-facing status

URP VFX Graph has an experimental driver path that requires Linear color space. HDRP has no equivalent runtime driver. This does not establish support for every graph, effect, or transparency setup; review the conditions below before using VFX in a project.

## Configure

Use VFX Graph with Linear color space. In the Unity Editor, set `Project Settings > Player > Other Settings > Rendering > Color Space` to `Linear`. Unity Visual Effect Graph 17.6 officially does not support URP Gamma color space. HDRP also requires Linear. See [Unity Visual Effect Graph 17.6 system requirements](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/System-Requirements.html) and [Unity HDRP asset requirements](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/create-an-hdrp-asset.html).

`UrpVfxCameraDriver` does not silently allow Gamma. In Gamma, it stops before creating a Camera or RenderTexture, sets `LastError` to `VFX Graph requires Linear color space; URP Gamma is unsupported`, and writes the same message as an ASCII error log. Change to Linear before enabling the driver.

1. Create a URP Bridge Rig and disable the source Camera used for Quilt rendering. Keep the source Camera on the same GameObject as the Rig.
2. Add `UrpVfxCameraDriver` to the same Rig GameObject. Enable `enableDriver`, and set `vfxLayers` to the VFX object's layer. The VFX layer must overlap the source Camera's culling mask.
3. Set `driverFieldOfView` to fit the VFX. It defaults to 90 degrees. Confirm the VFX bounds lie inside the auxiliary Camera's frustum.
4. For regular automatic rendering, enable `QuiltRenderer.autoRender`. To call `RenderQuilt()` manually, enable `runWhenManualRendering` first.

## Memory use and limits

The auxiliary Camera is intended to advance simulation once per normal frame update. Do not repeat manual simulation according to the number of Quilt views. The auxiliary Camera also adds rendering cost.

Heavy effects rendered from multiple views can reserve large instancing batches. Lowering Output Quality may not reduce those buffers enough. If the expected concurrent instance count is known, consider Custom capacity per effect and profile the actual scene. Disabling instancing everywhere can increase CPU and draw costs. See Unity's [Instancing guide](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/Instancing.html).

The default 4096 MB SDK budget detects growth after allocation; it cannot prevent an initial large allocation. Runtime instancing changes can reset simulation, and SDK automatic switching is not implemented. Raising the budget is not a remedy.

There is no general guarantee for every graph, GPU Event, transparency setup, TAA mode, view count, or long-running session. Use the [feature/platform matrix](ja/unity-feature-platform-matrix.html) for current conditions and the [validation and investigation history](development/vfx-graph-history.md) for individual tests, incidents, failures, and sample-specific remedies. Windows Editor shutdown crashes remain unresolved.
