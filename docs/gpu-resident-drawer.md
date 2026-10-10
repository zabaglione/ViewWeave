[Japanese](ja/gpu-resident-drawer.md)

# GPU Resident Drawer Validation Status

> Audience: Unity and rendering developers. This is a dated research or validation record limited to the revision, platform, and conditions stated here; it does not establish support for other configurations. For current support and setup, see [Compatibility](compatibility.md) and [Getting started](getting-started.md). Browse the [Technical guide](technical-guide.md) or [User guide](user-guide.md).

This SDK evaluates Unity 6.6 GPU Resident Drawer (GRD) separately from custom multiview optimization. In URP, delayed movement updates were observed when GRD was combined with the former LateUpdate-based automatic rendering path; the issue was corrected for the current automatic-render timing. This finding applies to the interaction with the former render timing and does not indicate a general delay in GRD itself. GRD draw-call optimization and Bridge interop thread shutdown are separate issues. Neither a performance advantage from enabling GRD nor a fix for Bridge shutdown has been confirmed.

With Unity `6000.6.4f1` on Windows D3D11, Editor GRD/VolumeStack regression tests passed 27/27. The URP normal-thread Player was checked in 6 cases: GRD OFF/ON at 1/8/48 views. The HDRP Player was also checked at 48 views for Preview, Balanced, and Native with GRD OFF/ON, recording 76 renderers and 3,648 visible instances with GRD ON. These checks establish rendering correctness, not HDRP performance. In a separate fixed-condition performance comparison, 48-view CPU submission p50 was 17.3311 ms with GRD Off and 15.0732 ms with GRD On, about 13.0% lower with On. GPU and render-thread time are missing, so a general performance advantage remains undetermined. Metal and HDRP GRD performance are untested. The Bridge Worker normal-thread path had a separate GPU-preview continuity check, but no performance measurement combining it with GRD.

## Relationship to Unity's Official Feature

The latest GRD/VolumeStack regression suite passed 27/27 with 0 skipped. It includes URP/HDRP validation stack reuse and Motion Blur toggling. This is a correctness gate, not a performance guarantee. Fixed conditions and Player benchmark measurements are in [performance.md](performance.md). The paired table includes conditions after the VolumeStack cache change, but does not establish causality for the cache change alone or GPU performance.

Unity 6.6's selection guide lists both GRD and BatchRendererGroup (BRG) as multithreaded and compatible with the SRP Batcher. GRD keeps MeshRenderer data resident on the GPU and uses BRG, GPU instancing, and CPU-parallel culling jobs. It does not automatically combine 48 views into one draw, nor does it allow Camera APIs to run in `Task.Run`.

For URP 6.6, both Forward+ and Deferred+ are candidates. Confirm that the active Renderer uses one of these paths, enable the SRP Batcher and GRD Instanced Drawing, set BRG Variants to Keep All, and disable static batching. Validate compute support per device for D3D11, D3D12, Vulkan, and Metal. These conditions are separate from the Bridge path limited to Windows D3D11 Direct.

HDRP 17.6 also has GRD. Check Instanced Drawing and BRG Keep All in the HDRP Asset, compute support, and MeshRenderer conditions.

References: [Unity's method selection](https://docs.unity3d.com/6000.6/Documentation/Manual/optimizing-draw-calls-choose-method.html), [URP GRD](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/gpu-resident-drawer.html), [URP compatible objects](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/make-object-compatible-gpu-rendering.html), [URP GRD performance](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/gpu-resident-drawer-performance.html), [HDRP 17.6 GRD](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/gpu-resident-drawer.html).

## Applicability Conditions and Limitations

GRD applies to MeshRenderer and DOTS-compatible shaders. It has constraints for MaterialPropertyBlock, per-instance rendering callbacks, and probes. Confirm that target objects are not moved while their views are being rendered. This does not mean that movement between frames is universally prohibited.

SkinnedMeshRenderer, ParticleSystemRenderer, uGUI, and UI Toolkit follow paths separate from GRD's MeshRenderer support. Validate coexistence with standard rendering fallback, image quality, creation/destruction, and transparent sorting. GRD itself is not depth interpolation, so transparency is not rejected universally.

GRD may help when CPU draw submission is the bottleneck and many instances share a mesh and variant. It can be slower when GPU-bound due to added GPU work. Compare GRD OFF/ON using actual counters, Burst workers, and the Profiler; a toggle alone does not prove successful applicability.

## ViewWeave Hypotheses and Validation Gates

Inspection of the local 17.6 dependency confirmed that Core's `GPUResidentDrawer.InsertIntoPlayerLoop` calls `WorldProcessor.Update` immediately before `PostLateUpdate.FinishFrameRendering`. The former LateUpdate automatic render ran before this update; under an eight-view URP condition with GRD enabled, movement of dynamic objects was not reflected. The current automatic render is scheduled immediately after `PostLateUpdate.FinishFrameRendering`. `renderInLateUpdate` remains as a compatibility property, but is no longer used for automatic rendering in LateUpdate.

The Editor's `EditorFrameUpdate` infers a frame boundary when the same Camera ID reappears, so per-view processing may increase across 48 `StandardRequest` calls. Measure Player and Editor separately. Because URP's `UniversalRendererRenderGraph` references occlusion settings using a camera entity ID, shared occlusion view history across 48 views from 1 Camera is also a hypothesis.

Initial test cases are A: normal SRP with GRD OFF; B: GRD ON with occlusion OFF; and C: GRD ON with occlusion ON after history safety is confirmed. Disconnect Bridge output. Compare 1/8/48 views, many identical static meshes, different meshes, dynamic spawn/despawn, movement, opening/closing occlusion, shadows, transparency, skinned meshes, particles, UI, and post-processing. The current URP BeforeFinish/AfterFinish timing and GRD ON/OFF at 48 views have been checked. Measure performance separately using actual counters, Burst workers, and the Profiler; a toggle alone does not prove applicability. Final validation is a rendering-correctness check using Unity `6000.6.4f1` Windows D3D11 Editor batch. Separate Windows URP Forward+/Deferred+ and HDRP, normal graphics and Direct, and validate Mac Metal on a separate host.

Acceptance requires correct dynamic-update timing, valid culling across views, callbacks preserving one simulation per frame, and no false object disappearance. Decide the priority of high resolution, custom multiview, and reprojection after GRD testing. This can proceed alongside the M1 Bridge fix and does not depend on it. Do not adopt a Unity dependency fork or reflection calls to non-public methods.
