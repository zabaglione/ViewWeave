[Japanese](ja/xr-provider-investigation.md)

# Investigation of XR Display Providers and Their Application to ViewWeave

Research date: 2026-10-07

## Current Direction

Under the user's direction, the decision to adopt an XR approach, prototype work, and migration from the existing path are on hold. This page is a research record for future consideration. Work to finish Quilt capture and camera-framing features continues.

## Conclusion

Displaying an ordinary scene object such as a Cube on a Looking Glass does not require adding an individual component to every object. A small display-side component can select which existing scene Camera is the basis for multiview rendering and transform that Camera's pose/projection for each display view. Implementing this as an `XRDisplayProvider` called by Unity's `XRDisplaySubsystem` is a design that could connect without significantly changing existing Cameras or Scene structure.

However, ViewWeave at the time of this research does not use the XR Provider approach. It does not enlarge the ordinary Main Camera image into 48 copies. The Rig uses a dedicated disabled Camera, sends 48 `StandardRequest` calls, renders each view, and assembles the Quilt. Migration to an XR path, performance improvement, and HDRP/VFX compatibility have not been demonstrated.

## Using Existing Cameras

| | Current approach | Proposed XR Display Provider approach |
| --- | --- | --- |
| User setup | Add ViewWeave Bridge Rig to a scene Camera | Proposed small component to select a Camera and assign a display Provider |
| Multiview rendering | 48 render requests from ViewWeave's dedicated Camera | Pass multiview positions/projections to Unity XR render passes and join the existing rendering loop |
| Ordinary objects such as Cubes | Render normally; no dedicated shader needed | Can be designed to render standard Renderers the same way; no XR component needed on each object |
| Render count and performance | Currently renders 48 views individually | Shared work such as culling may be possible; this does not mean that GPU rendering of 48 views automatically becomes one draw |
| Compatibility and risk | Current URP/HDRP paths | Validate URP/HDRP render passes, VFX, Volumes, screen-space/history effects, and native display lifecycle |

A Provider can generate the multiple poses/projections required for display from an existing Camera's pose and projection, then pass them to Unity. The user must be able to specify which Camera to use, the zero-parallax/focus distance corresponding to the display center, the width and units of the view cone, scene scale, and whether to output to a flat monitor or Looking Glass. A practical starting point is a settings component that only bridges a normal `Camera` to `XRDisplaySubsystem`.

This design is not the same as “writing a custom OpenXR runtime.” It is possible to implement a Unity XR rendering Provider and keep the backend that sends the final Quilt to the existing official Looking Glass Bridge. OpenXR is a separate option if tracked headsets or XR input become necessary.

## Boundaries of Unity's Standard Features

Unity XR Origin handles transforms between tracking space and Unity scene space. It is not required for an untracked light-field display; it can help organize the tracking coordinate origin when needed. [Unity XR Origin](https://docs.unity3d.com/6000.0/Documentation/Manual/xr-origin.html)

Unity Single Pass Instanced renders the standard two XR eyes into a texture array. Unity 6000.0 public documentation describes instanced drawing for two eyes, and a two-layer array constraint was also confirmed in the XRSystem of the local Core 17.6 package. This is not evidence that 48 views can be processed directly. ([Unity Single Pass Instanced](https://docs.unity3d.com/6000.0/Documentation/Manual/SinglePassInstancing.html), [Unity Graphics XRSystem source](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/XR/XRSystem.cs))

Unity XR Render Pass has a `cullingPassIndex` that represents the relationship where a different view can use the same culling pass. This is a useful design reference for sharing culling information, but it does not guarantee correct union culling across 48 views or an automatic performance benefit from sharing. [XRRenderPass.cullingPassIndex](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/XR.XRDisplaySubsystem.XRRenderPass-cullingPassIndex.html)

Existing Unity native XR headers and public Provider examples confirm the concept of passing multiple views through render passes. However, the headers bundled with Unity 6000.6 Editor have not established the maximum number of views required by ViewWeave. Therefore, do not claim that “XR Provider supports 48 views by default” or that “the limit is 8 views.” Confirm the actual API for the target Unity version and HDRP path with a probe.

## Public DisplayXR Design Example

[DisplayXR ADR-007](https://github.com/DisplayXR/displayxr-unity/blob/main/docs~/adr/ADR-007-render-path-by-view-count.md), dated 2026-07-03 and marked Accepted, records a proposal to use an XR Provider for a small number of views and a dedicated Quilt path for a larger number. The ADR itself says the Quilt path is not implemented; it is design material, not evidence of HDRP 48-view success or performance. [DisplayXR XR Display Provider architecture](https://github.com/DisplayXR/displayxr-unity/blob/main/docs~/architecture/xr-display-provider.md) is also a useful architecture reference. That repository's documents include older proposals and open items, so distinguish individual diagrams and claims from adopted implementations.

This example supports making the choice to use an XR Provider depend not only on ease of integration with an existing scene, but also on the cost of view count and render pipeline. It does not justify replacing ViewWeave's 48-view path with a Provider wholesale.

## Checks Required Before Resuming

1. Determine how to obtain the native headers/ABI for the Unity 6000.6 XR SDK. Unity's XR SDK documentation describes obtaining native SDK headers; do not assume they ship with the Editor. Confirm the acquisition path for the target version, required view count, render lifecycle, and HDRP connection conditions. ([Unity XR SDK](https://docs.unity3d.com/ja/6000.0/Manual/xr-sdk.html))
2. Build a two-view XR Provider prototype based on an existing Camera and confirm that a normal Cube is displayed.
3. In the same Scene, resolution, and two-view count, compare an XR Provider with two `StandardRequest` calls; measure CPU, GPU, end-to-end time, and image quality. Do not treat the difference between two-view XR and the existing 48 `StandardRequest` calls as an XR Provider performance-improvement percentage.
4. Validate Editor Play and Player separately, then compare Volume, VFX, transparency, screen-space effects, history-dependent appearance, and performance in URP/HDRP.
5. Confirm native API view limits and Render Pipeline constraints, and correctness when sharing culling, shadows, and work submission before deciding whether to extend to 48 views. For a 48-view comparison, use the same Scene, resolution, and view count for both the baseline and candidate.

Neither an XR Provider prototype nor these comparisons have been run. Implementation of existing Quilt capture and gizmo features proceeds independently from selection of an XR approach; this research has not changed the rendering path to XR.
