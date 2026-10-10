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

## Dated validation and implementation notes

URP VFX Graph has an experimental control path using a separate 1x1 auxiliary Camera rather than the regular Quilt Camera. `UrpVfxCameraDriver` is the URP implementation. The auxiliary Camera is intended to advance the normal GPU VFX simulation once per frame, rather than repeat simulation for every Quilt view. The URP Simple Burst driver path is within the existing validation scope. No equivalent runtime driver is provided for HDRP. A mixed URP/HDRP VFX matrix passed 5/20 and did not pass 15/20. There are conditions where HDRP's regular Camera showed a difference from StandardRequest and the normal Camera in a standalone canary using a template clone and ForceImport. Non-passing cases include priming where Quilt tile 0 showed no difference; execution-order-dependent observations remain. Do not treat HDRP paused/live, GRD, or 8/48 views as runtime support. Custom VFX Graph content, sorting, soft depth, and complex transparency are unverified.


VFX Graph on HDRP is outside the runtime-driver support scope. To inspect regular-Camera VFX display on HDRP, enable the source Camera and use the dedicated diagnostic fixture that includes a template clone and ForceImport. In one standalone-Camera measurement, 4423 pixels changed, while StandardRequest and the enabled-during-submit variant in the same matrix run both had 0. A separate single run in another Editor measured 4423 for both the regular Camera and StandardRequest. A pass in a standalone canary does not establish multiview runtime support; execution-history dependence remains.

The driver does not clone a Quilt Camera for each view. It applies the source Camera position and orientation to the auxiliary Camera and updates VFX through regular 1x1 rendering. The auxiliary Camera adds rendering cost. The source Camera remains disabled; the driver releases the auxiliary Camera and RenderTexture when it stops. Check state through `IsRunning` and `LastError`.

This path assumes that VFX simulation does not run separately per view. Let a playing effect advance once in the regular frame update; do not repeat manual simulation according to the number of `RenderQuilt()` calls. To reproduce a stopped effect, fix the content seed, bounds, and playback state.

This path does not guarantee support for all VFX. The final D3D11 PlayMode run passed 5 of 19 VFX compatibility tests and did not pass 14. A separate final VFX matrix passed 5/20 and did not pass 15. Non-passing conditions did not show sufficient effect across all tiles, including some with small differences. HDRP priming also had one non-passing test. Do not label HDRP StandardRequest universally unsupported: distinguish the standalone canary pass from the matrix non-passing results. HDRP effects requiring temporal history, including Motion Blur, Screen Space Reflection, Screen Space Global Illumination, Volumetric Fog/Clouds, have Quilt-specific constraints. Validate VFX Graph sorting, transparency, soft depth, GPU simulation, and effects using per-view history individually. No HDRP driver is provided; do not recast non-passing results as passes or skips.

See [Compatibility](compatibility.md) and [Unity feature coverage](unity-feature-coverage.md) for detailed measurements and unverified areas.
