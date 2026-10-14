[Japanese](ja/unity-multiview-research.md)

# Unity Multiview Rendering Optimization Examples

> Audience: Unity and rendering developers. This is a dated research or validation record limited to the revision, platform, and conditions stated here; it does not establish support for other configurations. For current support and setup, see [Compatibility](../compatibility.md) and [Getting started](../getting-started.md). Browse the [Technical guide](../technical-guide.md) or [User guide](../user-guide.md).

Research date: 2026-10-07

This research covers Unity multiview rendering and Quilt generation for Looking Glass displays. In the sources reviewed, no published Unity implementation was found that accelerates true 48-view rendering while preserving ordinary URP/HDRP materials, VFX, transparency, and Volumes. The examples below distinguish Unity features, research prototypes, and research that replaces the normal Render Pipeline.

## Unity Features and Implementation Examples

| Example | Published information | Implications for 48 views/HDRP |
| --- | --- | --- |
| Looking Glass Plugin 4.0 | The official product claims 2–4x over 3.x. Public 4.0 alpha material describes URP/Render Graph, depth-based view interpolation, and dedicated Core/URP packages. | HDRP is unsupported. Transparency and VFX have conditions. Measurement conditions for the performance claim and results for the same 48-view scene are unknown. It is not a direct candidate for an existing HDRP project. |
| Unity Single Pass Instanced | Unity feature that instantiates the two XR eyes and reduces CPU load for shared scene-render preparation. | Intended for two-eye XR; it does not turn 48 distinct views into one equivalent draw. |
| Unity XR Display Provider / DisplayXR | Unity's Provider path can pass views derived from an existing Camera pose into Render Passes as an integration design. DisplayXR ADR-007 records an Accepted proposal to use an XR Provider for a small number of views and a dedicated Quilt path for many views. | Promising as an integration example for displaying ordinary Scene Objects from an existing Camera. However, the Quilt path in DisplayXR material is unimplemented and is not evidence of HDRP 48-view behavior or performance. Unity Single Pass Instanced itself is for two eyes. Adoption or prototyping in ViewWeave is on hold under the user's direction. |
| DirectX 12 Split Graphics Jobs | Unity reports up to 40% lower CPU latency for specific configurations. Unity 6 DX12 also supports the Graphics Jobs setting in the Editor. | 40% is neither an FPS increase nor a ViewWeave prediction. Graphics Jobs is enabled in the current project's Player, but the Editor's effective threading mode has not been checked. Measure the effective state separately in Editor and Player first. |
| HDRP Shadow Update Mode | HDRP 17.6 can control light-shadow updates as Every Frame, On Enable, On Demand, and other modes. | A candidate test is to update Spot/Point shadows once per Quilt in the same simulation state and reuse them across views. First verify dynamic occluders are reflected each frame, and validate bounds culling and shadow correctness. Do not generalize to directional cascades. Untested. |
| GPU Resident Drawer | Feature that processes supported renderers on the GPU in HDRP/URP. | It was already enabled in the ViewWeave HDRP test project. There is no remaining opportunity from simply turning it on. |

Unity XR Provider can pass view poses/projections from an existing Camera. Changing to a Provider path does not mean creating a custom OpenXR runtime. Unity XR also has a culling-pass sharing API, but correctness and performance of union culling across 48 views require separate validation. Unity XR Origin assists with coordinates when using a tracked space; it is not required for an untracked light-field display. ViewWeave currently sends 48 `StandardRequest` calls from a dedicated Camera; it does not upscale the ordinary Main Camera image. Current implementation and validation scope, public DisplayXR design, and the proposed XR Provider spike order are summarized in [XR Display Provider Investigation](xr-provider-investigation.md).

The maximum 40% CPU latency figure for Split Graphics Jobs is from [Unity 6's official announcement](https://unity.com/blog/unity-6-features-announcement). Editor support on DX12 is described in [DirectX 12 improvements](https://unity.com/blog/directx-12-improvements-in-unity-6). Editor Preferences and Player Settings are separate. Read the effective mode first and compare only environments where changing the setting is meaningful. This research confirmed that a Unity API can provide the effective Graphics Threading Mode, but did not confirm the effective mode in the user's Editor.

## Promising Research That Cannot Be Integrated As-Is

| Research | Measurement or implementation | Implications and limits for ViewWeave |
| --- | --- | --- |
| Image-Based Spatio-Temporal Interpolation (HPG 2025) | Captures source images/depth in Unity URP; interpolation is a public C++/CUDA implementation. Reports about 2 ms for interpolation at 1440p on RTX 4090. | This is interpolation time, not total rendering time for 48 views. Volumetric lighting and caustics are out of scope. A reference for a small depth-compositing experiment. |
| CREAL STALF (2022) | Unity-integrated light-field HMD research. Generates about 100 nearby views from one color/depth image. | Intended for near-eye displays with narrow view spacing. The values cannot be extrapolated to the wide viewing range or image quality of a Looking Glass display. |
| Light Field Display Point Rendering (ACM TOG 2024) | Independent OpenGL rather than Unity. Renders 48 views using shared point clouds and reports 2–8x versus non-optimized MVR on RTX 3070, and 38.92 ms to 12.81 ms on Sponza. | Does not preserve normal URP/HDRP materials, VFX, or Volume rendering. A comparison for research prototypes that replace the rendering method. |

- [HPG 2025 paper](https://onlinelibrary.wiley.com/doi/full/10.1111/cgf.70215) / [public code](https://github.com/DerThomy/ImageBasedSpatioTemporalInterpolation)
- [CREAL STALF paper](https://sid.onlinelibrary.wiley.com/doi/full/10.1002/jsid.1112) / [technical material](https://creal.com/app/uploads/2023/03/CREAL-light-field-rendering-2.pdf)
- [Light Field Display Point Rendering paper](https://arxiv.org/html/2601.19901v1)

## ViewWeave Measurements and Next Comparisons

A 48-view measurement in the user's HDRP VFX Learning Templates recorded about 9 FPS in the Editor at Native settings, about 88 ms GPU time, and about 103 ms CPU submission. CPU submission is wall-clock time for render preparation, rendering, and copying; it may include Render Thread or GPU waits. CPU alone cannot be identified as the cause. Lowering quality resolution barely changed FPS, while reducing to 24/12 views increased speed. Conditions and limitations are in [HDRP VFX Performance Record](resumed-hdrp-vfx-performance.md). GPU Resident Drawer was already ON in the test project; that is specific to this test and is not a guarantee for all projects.

The next validation candidates are prioritized as follows:

1. Read the effective Editor/Player mode for Split Graphics Jobs; if already split, remove it from the comparison.
2. Compare HDRP Shadow Update Mode output and cost in a minimal scene with static local lights.
3. Prototype shared view-preparation work for two views while retaining features, and measure CPU, GPU, and end-to-end time separately.
4. As an optional approximate mode, compare quality and speed when depth-compositing 48 images from 12/24 rendered views. Treat this as a separate quality setting from true 48-view rendering.

These are candidates for validation; this research does not demonstrate a performance improvement. View synthesis requires image-quality checks in scenes with transparent VFX, occlusion boundaries, reflections, and volumetric effects.

References: [Unity Single Pass Instanced](https://docs.unity3d.com/6000.0/Documentation/Manual/SinglePassInstancing.html), [HDRP 17.6 Shadow Update Mode](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/shadow-update-mode.html), [HDRP GPU Resident Drawer](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/gpu-resident-drawer.html).
