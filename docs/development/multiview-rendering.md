[Japanese](ja/multiview-rendering.md)

# Multiview Rendering Approaches

> Audience: Unity and rendering developers. This is a dated research or validation record limited to the revision, platform, and conditions stated here; it does not establish support for other configurations. For current support and setup, see [Compatibility](../compatibility.md) and [Getting started](../getting-started.md). Browse the [Technical guide](../technical-guide.md) or [User guide](../user-guide.md).

Core currently uses 1 Camera and draws views through N `StandardRequest` calls. It does not place many Cameras in the scene, but CPU frustum tests, shadows, draw submission, and post-processing are repeated per view. The goal is not to reduce the Camera count; it is to reuse work common across views and reduce the number of expensive full renders.

## Approach Comparison

| Approach | Description | Expected benefit | Main open questions |
| --- | --- | --- | --- |
| A: Current baseline | One Camera, N standard requests | Compatibility baseline | Not optimized; CPU tests, shadows, rendering, and post-processing repeat |
| B: With GRD | Apply GPU Resident Drawer, CPU parallel processing, and instancing to the existing SRP | May reduce CPU submission and work for identical meshes, but N views remain | Update timing, culling across views, fallback, transparency, UI, hardware performance |
| C: Custom multiview | Use conservative culling bounds covering all views, view-visibility masks, GPU-resident geometry/materials, and view ID and VP arrays to render together into a Texture2DArray and assemble the Quilt | May reduce CPU submission and some shared work | Pixel, vertex, and post-processing work for N views remains. Lit, shadows, transparent sorting, SSR, TAA, Shader Graph, VFX, and UI compatibility are unresolved |
| D: Reprojection | Generate many views from color/depth rendered at a small number of anchor views | Can test, for example, 48 output views from 8 rendered views, but does not guarantee 6x FPS | Holes, transparency, mirrors, and view-dependent reflections. Re-rendering required views or rendering transparency separately may not fully solve these issues |

C and D are optional paths and must also be tested alongside A/B fallback. Do not treat an approach as a replacement for an official feature based only on an idea.

## Shared Processing Considerations

Evaluate shared shadows separately for light-dependent point/spot shadow maps and directional-light processing that depends on cascades, LOD, and Camera. Do not reuse a center-view shadow for all views as-is. Include Unity 6.6 GRD's limitation where `Light.shadowMatrixOverride` does not affect shadow-caster culling in acceptance conditions.

Do not assume that XR Single Pass Instancing can be reused for 48 views by changing settings. Unity's public documentation covers stereo, and the local SRP 17.6 constant array has 2 elements. Do not fork Unity or call non-public APIs through reflection; run an applicability spike using public APIs.

Do not select Geometry Shader by default. For D3D11, validate `SV_RenderTargetArrayIndex` at vertex output and the VPRT capability requirement. Validate Metal separately.

## Staged Gates

The GRD MVP was performed first, and a shared-culling prototype for 8 views was built as a prerequisite to a minimal C-path Lit scene. Under limited Lit conditions, 6 checks passed. 1 feature execution produced 8 RendererLists, 1 draw for each, from a single culling pass; depth used a separate attachment. In a microbenchmark with 2 fixed Lit objects, baseline p50 for 8 `StandardRequest` calls was 1.8023 ms, versus 0.3198 ms with shared culling. This is a limited CPU submission measurement, not GPU or CPU render-thread performance. 48 views, shadows, transparency, post-processing, and VFX are unverified. The retest passed, but the approach was not adopted for the production backend. Compare C/D with A/B fallback and separately inspect image quality for static and dynamic updates, shadows, transparency, SSR, TAA, Shader Graph, VFX, and UI.

References: [Unity Single Pass Instancing](https://docs.unity3d.com/6000.6/Documentation/Manual/SinglePassInstancing.html), [BatchRendererGroup](https://docs.unity3d.com/6000.6/Documentation/Manual/batch-renderer-group.html), [Graphics.RenderMeshIndirect](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html), [HLSL semantics](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics), [official Plugin 4 alpha documentation](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha).
