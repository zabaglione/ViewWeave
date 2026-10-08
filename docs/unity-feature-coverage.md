[Japanese](ja/unity-feature-coverage.md)

# Unity Feature Coverage

This page separates public Unity features from the measured coverage for ViewWeave multiview output on Unity 6000.6.4f1, URP/HDRP 17.6, and Windows D3D11/D3D12. Bridge Worker output covers Windows D3D11/D3D12 Editor Play and Player. Legacy Direct is D3D11 only. Regular Bridge Sessions do not run in Edit Mode; a separate explicitly started `ViewWeaveRig` Editor preview is available. Targeted URP/HDRP local GPU fixtures passed for eight-view rendering, layout rebuild, proof-based texture retirement, and Stop/readback with zero pending retention. D3D11 and D3D12 device fixtures passed URP/HDRP Start/Stop/Restart at a 3840x2160 display profile with a 7680x4320, 8x6/48-view Quilt, one submission/completion for each Start and Restart, and zero native/retention pending after Stop. A targeted Play/Edit transition fixture passed 1/1: entering Play left the preview inactive with zero pending work, and returning to Edit Mode did not restart it. An isolated Core-only Unity project on 6000.6.4f1 imported and compiled the Core package, then passed `QuiltCoreBatchTests.Run` for layout, projection, settings validation, and view sampling; the Editor exited naturally with code 0. Manual optical appearance review remains pending. A targeted hidden-Editor fixture confirmed preview cleanup and explicit restart across Domain Reload; see [Graphics backends](graphics-backends.md) for File > Exit results; normal Editor exit remains unverified. macOS output is out of scope. A pass applies to the stated fixture and does not guarantee every project that uses the same feature.

## Current Validation Summary

Before the current guard was added, the old package snapshot's Windows D3D11 PlayMode suite passed 108 of 123 tests, failed 15, and skipped 0. Targeted Core/SRP/VFX Editor tests passed Gamma 8/8 and Linear 17/17; these are not results for the full matrix. Gamma MSAA and normal URP Bridge Player execution remain untested. Gamma VFX is officially unsupported. The old D3D11 integration had one HDRP priming failure and 14 VFX compatibility failures; a separate VFX matrix from around the same time passed 5 of 20 and failed 15. This history does not establish the result of the current D3D12 project integration or all VFX conditions. See the canonical summary in [docs/compatibility.md](compatibility.md).

The existing DX12 cold driver suite passed 6/7 and retains an `alive=0` failure for FOV50 Simple Burst. URP Particle passed 3/3 and HDRP Particle 2/2. A memory diagnostic suite without synchronous CSV I/O passed 7/7 and observed 128 alive for both FOV50 and FOV90. A separate filtered run including the original fully qualified FOV50 case passed 3/3, but the cold-suite failure is retained as history and is not treated as a permanent fix. The old 15 D3D11 failures are also retained. HDRP Shadow OnDemand was not adopted: the EveryFrame baseline failed its own strict image comparison, so the OnDemand comparison and performance measurement were not reached.

GRD performance measurements cover only a normal multithreaded URP Player. They are not measurements of GRD performance in an HDRP fixture. The automatic-render scheduler runs after scene rendering, but that does not guarantee feature-specific update ordering or general rendering compatibility.

Status meanings:

- **Measured in a limited scope**: passed under the stated pipeline, settings, and fixture.
- **Under investigation**: available as a public Unity/SRP feature, but no passing ViewWeave multiview fixture exists yet.
- **Constrained**: subject to guards, pipeline differences, or view-dependent state.
- **Unsupported**: explicitly rejected or unavailable in the current SDK path.

## Rendering, Materials, and Geometry

| Feature | Status | Measured scope and limitations |
| --- | --- | --- |
| MeshRenderer and Lit material | Measured in a limited scope | Basic URP/HDRP Lit fixtures. HDRP uses Linear color space and fixed Exposure. |
| Quilt View Render Mode | Implemented; measured only in static fixtures | Full/Half/Quarter. Keeps a 48-view output Quilt while rendering 48/24/12 views. Unrendered tiles duplicate the nearest view; no interpolation is performed. Each mode was measured in a static HDRP fixture on 2026-10-04. All 48 tiles in Half/Quarter had zero RGBA pixel difference from their assigned source. VFX/user scenes and physical optical quality are unverified. [Measurement scope](dx12-performance-investigation.md). |
| Shader Graph Lit/Unlit | Measured in a limited scope | Individual URP/HDRP Shader Graph fixtures. General shader, keyword, depth, and shadow support is unverified. |
| SkinnedMeshRenderer | Measured in a limited scope | URP/HDRP one-bone pose in the Geometry suite. Complex skinning is unverified. |
| Animator / Transform updates | Under investigation | Animation-state synchronization beyond fixed fixture poses is unverified. |
| LODGroup | Measured in a limited scope | ForceLOD fixture in the Geometry suite. General distance-based selection is unverified. |
| Terrain | Measured in a limited scope | Visibility fixture in the Geometry suite. Terrain features, details, and trees are not fully tested. |
| Directional light intensity | Measured in a limited scope | Fixed light fixture in the Geometry suite. Complex combinations of lights, shadows, and probes are unverified. |
| Shadow mode | Measured in a limited scope | Shadow-mode fixture in the Geometry suite. All shadow cascades and contact shadows are unverified. |
| GPU Resident Drawer | Constrained | GRD measurements cover MeshRenderer in a normal multithreaded URP Player. HDRP performance and other renderer types, transparency, VFX, and Terrain performance are unverified. |
| SRP Batcher / GPU instancing | Under investigation | Batching and performance across multiple views are unverified. |

## Particles, VFX, Lines, Decals, and Probes

| Feature | Status | Measured scope and limitations |
| --- | --- | --- |
| Shuriken ParticleSystem | Measured in a limited scope | URP paused Lit/Unlit/live (3) and HDRP mesh paused/live (2) fixtures. General sorting, soft depth, and culling support is unresolved. |
| VFX Graph | Measured only on DX12; Linear required | Existing cold driver suite passed 6/7 and retains the FOV50 Simple Burst `alive=0` failure. The memory diagnostic suite without synchronous CSV I/O passed 7/7 (128 alive each at FOV50/FOV90). A separate filtered run including the original fully qualified FOV50 case passed 3/3, but this does not prove a permanent fix or general cold/warm success. Gamma is unsupported by official requirements. General transparency, sorting, culling, soft depth, and same-Camera priming remain unresolved. GPU simulation is not considered generally supported. |
| LineRenderer / TrailRenderer | Measured in a limited scope | Image differences checked in individual Line/Trail fixtures. General camera-dependent width, history, ordering, and depth behavior are unverified. |
| BillboardRenderer | Under investigation | Multiview output, including HDRP, is unverified. |
| Decal / Decal Projector | Under investigation | URP Renderer Feature and HDRP Decal projection, depth, and transparency are unverified. |
| Reflection Probe / Planar Reflection | Measured in a limited scope | Image differences checked in individual Probe fixtures. Probe updates, per-view reflections, and realtime history are unverified. |

## Volumes and Temporal History

| Feature | Status | Measured scope and limitations |
| --- | --- | --- |
| HDRP Bloom | Measured in a limited scope | Directional light and explicit Volume fixture in HDRP Linear mode. A temporary adapter override preserves normal Bloom during Quilt rendering. Complex stacks are unverified. |
| URP Bloom | Measured in a limited scope | Per-view image differences checked in an individual Bloom fixture. General Volume inheritance, other effects, and tile seams are unverified. |
| Color Adjustments / Tonemapping / Exposure | Measured in a limited scope | Image differences checked in individual Tone/Color fixtures. HDRP requires Linear. Auto Exposure during Quilt rendering is converted to a shared Fixed EV 12 for all views; if the original stack is Fixed, its EV is preserved. General color management is unverified. |
| Motion Blur | Temporarily suppressed during HDRP Quilt rendering | A default temporary Volume override in the HDRP adapter disables it only during Quilt rendering. Normal rendering and the original Volume settings are preserved. URP configuration is separate. |
| FXAA / SMAA / MSAA | Limited FXAA/SMAA checks; URP MSAA 1/1. Confirmed 4x view RT, 1x resolve, resize/disable lifecycle, and center-color match. An additional Linear boundary suite passed 10/10, checking this RTX GPU's supported 2x/4x/8x levels, pipeline 1x, disabled allowMSAA, Deferred fallback, center-color match, and edge differences at supported MSAA. Seven targeted Gamma MSAA cases were also checked, but no dedicated MSAA Player test was run. Low-MSAA-capability GPUs, HDRP MSAA, and performance are unverified. | General materials, Unity/Player behavior, image quality, and performance under MSAA require testing. |
| TAA / temporal history | Unsupported | TAA is disabled during view rendering and restored afterward. Independent per-view temporal history is not provided. |
| SSAO / SSGI / SSR | HDRP SSGI/SSR temporarily suppressed during Quilt rendering | The HDRP adapter's default override disables these only during Quilt views and restores them when the scope ends. URP and other configurations are unverified. |
| Fog / Volumetric Fog / Clouds | HDRP volumetric effects temporarily suppressed during Quilt rendering | HDRP volumetric fog/clouds are disabled only during Quilt views. Normal Fog and Sky are preserved. |
| Depth of Field / Auto Exposure | Limited Depth of Field checks; HDRP Auto Exposure temporarily converted to a shared Fixed EV | HDRP defaults to EV 12 and uses the original EV if the stack is Fixed. URP Auto Exposure is unverified. |

The current change adds camera authoring, Quilt playback, and Mono AudioRenderer recording. The Camera Authoring DX12 targeted test and static review passed, but interactive Scene GUI dragging is unmeasured. The 12 Playback safety scenarios passed individually, as did the Core CPU boundary test. An isolated DX12 official Bridge fixture sent a pre-generated 7680x4320/8x6 PNG with still-image hold/Pause/Resume and a three-frame, 1 fps sequence. The playback body gate confirmed three submissions, zero skipped, preserved Ready generation, released lease, and pending 0. However, the Editor exited with an access violation at Unity.dll offset `0x44383df` (exit code `-1073741819`), so the natural-exit gate did not pass. This does not demonstrate live 48-camera rendering or physical display. A limited Editor reload probe confirmed retention of the serialized playback recovery record and 4x4 RenderTexture reference, post-reload fence/readback, and test-only handoff; it does not prove production shutdown ordering or natural exit. The Audio fake suite passed 6/6 and the actual audio/legacy silent cases each passed after a focused rerun (14 cases total). Mono URP/HDRP recording, Pause/Resume, stop/disable, and preservation of AudioSource/scheduled clips were confirmed. Native Bridge audio delivery and sample-accurate frame alignment were not measured. The validation baseline revision was 4230c4c. The final integration revision is undetermined.

## Quilt Capture and Recording

| Feature | Status | Measured scope and limitations |
| --- | --- | --- |
| Camera framing / focus / orbit / cursor | Implemented in the current change; DX12 targeted test and static review | Confirmed focus-distance settings, orbit that preserves the pivot, and a 3D cursor that is OFF by default. Direct Scene GUI dragging is unmeasured. |
| Quilt file playback | Core implementation; playback body gate measured in an isolated D3D12 official Bridge fixture | Checked still-image hold/Pause/Resume for a pre-generated 7680x4320/8x6 PNG and a three-frame, 1 fps sequence. Sequence had three submissions/zero skipped, preserved Ready generation, released lease, and pending 0. Unity Editor exited with a Unity.dll access violation, code `-1073741819`; the exit gate did not pass. Live 48-camera rendering and physical display are unmeasured. The 12 Playback safety scenarios passed individually. |
| AudioRenderer recording | Windows Editor Mono opt-in implemented in the current change; limited fake and actual integration measurements | Fake suite passed 6/6. Actual audio/legacy silent cases all passed individually after the initial 7/8 run and a focused test-only fix (8 cases total). Confirmed URP/HDRP Quilt recording, Pause/Resume, Stop/disable, and preserving playing/scheduled AudioSources. Stereo capture is unsupported; Native Bridge output and per-frame sample-accurate alignment are unverified. |
| Still Quilt PNG and metadata | Implemented on main; limited D3D11 and DX12 fake-output measurements | Saves Quilt PNG, JSON sidecar, and center-view PNG. Existing D3D11 URP/HDRP PlayMode tests passed 2/2 each. DX12 Core smoke and one capture test per URP/HDRP confirmed center metadata/view mapping; an actual URP recording-to-MP4 E2E also passed 1/1. Bridge transport and saving from a physical device are unverified. |
| Quilt PNG sequence | Implemented on main; limited D3D11/DX12 fake-output measurements | Recorder suite confirmed three images each at 10/25 fps and time restoration after 1–240 fps start/stop in URP. DX12 URP/HDRP capture tests checked Pause/Resume. An actual URP DX12 recording-to-silent-MP4 E2E passed 1/1 with three frames at 30 fps. Separately, a pre-rendered three-image, 1 fps sequence passed the body gate in an isolated DX12 official Bridge playback fixture; the Unity exit gate failed with an access violation. |
| Center-view 2D PNG | Implemented; measured only with DX12 fake output | Saves the center logical view of the completed Quilt; sidecar/mapping checked in URP/HDRP tests. Saving during actual Bridge output and physical display are unverified. |
| Silent MP4 export | Implemented on main; limited fake-process suite, synthetic CPU smoke, and actual recording E2E | In addition to synthetic export of three 64x48 8x6/48-view PNGs at 30 fps, an E2E passed 1/1 converting three 256x192 4x2/8-view PNGs from an actual URP DX12 QuiltRecorder recording to 30 fps H.264/yuv420p. No Native/Bridge was used. Manual Editor-window operation and physical output are unverified. |

## UI and Camera

| Feature | Status | Measured scope and limitations |
| --- | --- | --- |
| uGUI World Space | Measured in a limited scope | Passed in an integration fixture. Input, complex Canvas, and depth conflicts are unverified. |
| uGUI Screen Space Camera | Measured in a limited scope | Passed in an integration fixture. Per-camera viewport and input are unverified. |
| uGUI Screen Space Overlay | Constrained | Normal-screen Overlay is not automatically included in Quilt capture. Compositing at the same depth as each view is unverified. |
| UI Toolkit target texture | Measured in a limited scope | Passed in a Unity 6.6 targetTexture fixture. Ray mapping, DPI, EventSystem, and input are unverified. |
| Camera.Render | Unsupported | Not used in the SRP path. Core rejects `useRenderRequests=false`. |
| SDK RenderRequest | Measured in a limited scope | SRP requires Standard RenderRequest and validates it in the pipeline adapter/guard. The cost of N requests from one Camera remains. |
| URP camera stack | Unsupported | Base camera only. The Guard rejects an Overlay camera stack. |

## SRP Extensions, XR, and Special Paths

| Feature | Status | Measured scope and limitations |
| --- | --- | --- |
| URP Renderer Feature / Render Graph | Measured in a limited scope | A validation marker pass confirmed differences across eight views and eight executions. General resource lifetime, pass order, and camera filtering are unverified. |
| HDRP Custom Pass / Render Graph | Measured in a limited scope | A validation marker pass confirmed differences across eight views and eight executions. General Custom Pass camera filtering and resource management are unverified. |
| Custom post-processing | Under investigation | Shared history and ordering across views are unverified. |
| XR single-pass / multipass | Unverified | XR stereo views and a Quilt grid are different models. |
| DXR ray tracing / HDRP path tracing | Unverified | History behavior and N render requests are not guaranteed. |
| D3D12 Bridge Worker / Vulkan / Metal output | D3D12 supports the Windows Bridge Worker Editor Play/Player path. Vulkan is out of scope; Metal is unverified. | D3D12 Worker shares GPU resources with a D3D11 worker on the same GPU. Legacy Direct is D3D11 only. |

## SRP and GRD Notes

Core uses standard RenderRequest in SRP. `useRenderRequests=false` is rejected in the SRP adapter path. URP validates Base camera, Volume, Renderer Feature, and Camera state per view. HDRP requires Linear. During the Quilt scope, adapters use temporary Volume overrides to suppress Motion Blur, SSR, SSGI, volumetric fog/clouds, and convert Auto Exposure to a shared Fixed EV for all views. The Main Camera and Volume settings outside that scope are preserved.

GRD availability is validated separately from official pipeline support. Current performance evidence is limited to MeshRenderer in a normal multithreaded URP Player; it does not represent GRD performance for HDRP, VFX, ParticleSystem, SkinnedMeshRenderer, Terrain, or Decal.

Official references:

- [Unity 6 render pipeline feature comparison](https://docs.unity3d.com/6000.6/Documentation/Manual/render-pipelines-feature-comparison.html)
- [VisualEffect API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/VFX.VisualEffect.html)
- [VisualEffect.AdvanceOneFrame](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/VFX.VisualEffect.AdvanceOneFrame.html)
- [URP rendering paths](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/rendering-paths-set.html)
- [Active render pipeline](https://docs.unity3d.com/6000.1/Documentation/Manual/srp-setting-render-pipeline-asset.html)
