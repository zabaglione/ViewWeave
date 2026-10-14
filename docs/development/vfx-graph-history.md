[日本語](ja/vfx-graph-history.md)

# VFX Graph validation and investigation history

For setup, see the [VFX Graph guide](../vfx-graph.md). Records below apply only to their stated dates and conditions.

## Dated validation and implementation notes

URP VFX Graph has an experimental control path using a separate 1x1 auxiliary Camera rather than the regular Quilt Camera. `UrpVfxCameraDriver` is the URP implementation. The auxiliary Camera is intended to advance the normal GPU VFX simulation once per frame, rather than repeat simulation for every Quilt view. The URP Simple Burst driver path is within the existing validation scope. No equivalent runtime driver is provided for HDRP. A mixed URP/HDRP VFX matrix passed 5/20 and did not pass 15/20. There are conditions where HDRP's regular Camera showed a difference from StandardRequest and the normal Camera in a standalone canary using a template clone and ForceImport. Non-passing cases include priming where Quilt tile 0 showed no difference; execution-order-dependent observations remain. Do not treat HDRP paused/live, GRD, or 8/48 views as runtime support. Custom VFX Graph content, sorting, soft depth, and complex transparency are unverified.


VFX Graph on HDRP is outside the runtime-driver support scope. To inspect regular-Camera VFX display on HDRP, enable the source Camera and use the dedicated diagnostic fixture that includes a template clone and ForceImport. In one standalone-Camera measurement, 4423 pixels changed, while StandardRequest and the enabled-during-submit variant in the same matrix run both had 0. A separate single run in another Editor measured 4423 for both the regular Camera and StandardRequest. A pass in a standalone canary does not establish multiview runtime support; execution-history dependence remains.

The driver does not clone a Quilt Camera for each view. It applies the source Camera position and orientation to the auxiliary Camera and updates VFX through regular 1x1 rendering. The auxiliary Camera adds rendering cost. The source Camera remains disabled; the driver releases the auxiliary Camera and RenderTexture when it stops. Check state through `IsRunning` and `LastError`.

This path assumes that VFX simulation does not run separately per view. Let a playing effect advance once in the regular frame update; do not repeat manual simulation according to the number of `RenderQuilt()` calls. To reproduce a stopped effect, fix the content seed, bounds, and playback state.

This path does not guarantee support for all VFX. The final D3D11 PlayMode run passed 5 of 19 VFX compatibility tests and did not pass 14. A separate final VFX matrix passed 5/20 and did not pass 15. Non-passing conditions did not show sufficient effect across all tiles, including some with small differences. HDRP priming also had one non-passing test. Do not label HDRP StandardRequest universally unsupported: distinguish the standalone canary pass from the matrix non-passing results. HDRP effects requiring temporal history, including Motion Blur, Screen Space Reflection, Screen Space Global Illumination, Volumetric Fog/Clouds, have Quilt-specific constraints. Validate VFX Graph sorting, transparency, soft depth, GPU simulation, and effects using per-view history individually. No HDRP driver is provided; do not recast non-passing results as passes or skips.

See [Compatibility](../compatibility.md) and [Unity feature coverage](unity-feature-coverage.md) for detailed measurements and unverified areas.
