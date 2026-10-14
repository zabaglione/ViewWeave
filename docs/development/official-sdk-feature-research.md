[Japanese](ja/official-sdk-feature-research.md)

# Looking Glass Official Unity SDK Feature Research

> Audience: Unity and rendering developers. This is a dated research or validation record limited to the revision, platform, and conditions stated here; it does not establish support for other configurations. For current support and setup, see [Compatibility](../compatibility.md) and [Getting started](../getting-started.md). Browse the [Technical guide](../technical-guide.md) or [User guide](../user-guide.md).

Research date: 2026-10-07

This page summarizes features documented for the official SDK and separates ViewWeave alternatives from items that remain unverified. The official Plugin is proprietary in its current distribution, so ViewWeave uses public APIs and documentation as references for independent implementation and does not reproduce its code. The official product page lists Plugin 3.2.8 and 4.0.0 Beta. Detailed public 4.0 documentation is for 4.0.1 alpha. Because these versions differ, individual features or known issues are not universally attributed to the current Beta.

## Features and How ViewWeave Handles Them

| Official feature | Confirmed in official documentation | ViewWeave implementation and validation |
| --- | --- | --- |
| Save Quilt images | The 3.x `QuiltCapture` API saves a Quilt PNG and a 2D center-view image. Public 4.0 documentation also describes image capture. | Implements PNG and JSON sidecar saving. URP/HDRP D3D11 PlayMode smoke tests passed 2/2 each. Saving a 2D center view is not implemented. |
| Record video | Official APIs describe manual recording start/stop, time-range capture, Take, Pause/Resume, synchronization with VideoPlayer, codecs, and metadata. | Implements sequential PNG output of completed Quilts at a fixed simulation FPS. Confirmed three frames each at 10/25 fps and restoration of simulation time after start/stop from 1–240 fps in URP. MP4, audio, Timeline, VideoPlayer synchronization, and resuming an existing recording are not implemented. |
| Create video from Quilt | Separate documentation describes encoding Quilt images into H.264 video. | Saving and encoding can be separate steps. Fixed-interval capture with Unity Recorder is another option, but its integration with ViewWeave or the official SDK requires separate confirmation. |
| Hologram Camera range visualization | Green Volume and purple Focal Plane visualize the rendering range and focal plane. Camera/Volume modes are available, with vertical FOV, Near/Far, and focal distance controls. | Implemented Camera Framing settings and gizmos for Near/Far, left/right edge views, and focus position. The Camera Authoring suite passed. Direct manipulation of handles in the Scene view is unverified. Focus is separate from DOF/Volume focus. |
| Framing controls | Documentation describes dragging the range to adjust composition, orbit, 3D cursor, and related operations. | Support for all official input interactions is unimplemented or unverified. |
| Depth-based view interpolation | The 4.0 alpha documentation describes depth-based view interpolation. Transparent objects may not write depth and can break interpolation; VFX Output Particle also has configuration requirements. | Candidate optional feature for the future. Equivalent image quality to true multiview rendering and general transparency support have not been demonstrated. |

Main references: [public official 4.0 documentation](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha), [migration guide from 3.x](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha/migrating-from-unity-plugin-v3.x-to-v4.0).

The additional Recorder suite passed. PNG orientation and intermediate-color checks were performed by blitting a diagnostic pattern to a RenderTexture after URP/HDRP rendering. They do not measure color accuracy for a complete real scene. Saving with D3D12, saving on physical hardware, direct Scene GUI interaction, and large scenes including 48-view HDRP/VFX remain unverified.

For the save format, Quilt PNG filenames use the form `name_qs{columns}x{rows}a{viewAspect}.png`, and view 0 is the lower-left tile. `viewAspect` is the projection/display aspect ratio of a view, not the pixel width/height ratio of one tile.

PNG sequences at a specified FPS save each frame as a PNG and are separate from video encoding. The ViewWeave method advances simulation time by the specified interval and saves the completed Quilt without an extra `RenderQuilt`. Synchronous readback and PNG saving slow rendering, so wall-clock time may not keep up with the configured FPS. The design preserves the simulation time interval in that case. Public 4.0 documentation describes image/video features, but its alpha documentation also reports version-specific H.264 failures and gizmo behavior. The migration guide lists features unavailable after moving from 3.x to 4.0. These reports are not treated as issues in all current Beta versions; validate against a pinned version.

## Notes on Official Documentation Versions and Compatibility

The official product page lists 4.0.0 Beta. Detailed [4.0 alpha documentation](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha) specifies Unity 6000.0.39–6000.1.5 and URP 17.0.4–17.1, uses Render Graph, and states that HDRP is unsupported. It describes dedicated Core/URP packages, depth interpolation, and conditions for transparency and VFX. The migration guide also lists feature differences and items unsupported in 4.0, so not every official feature can be assumed available in the latest version.

The “2–4x” figure on the same page is an official performance statement relative to 3.x; the public materials do not identify the measurement project, GPU, view count, image quality, or VFX/Volume conditions. It cannot be used to predict the same improvement for ViewWeave at 48 views/HDRP. The official Plugin is proprietary and is not code to reproduce in an independent implementation.

## Primary Sources Reviewed

- [Looking Glass Unity Plugin product page (versions and performance statement)](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [Unity Plugin 3.x Script Reference: QuiltCapture](https://lfdocs.lookingglassfactory.com/software/index/script-reference/quiltcapture.cs)
- [Unity Plugin 3.x Script Reference: HologramCamera](https://lfdocs.lookingglassfactory.com/software/index/script-reference/hologramcamera.cs)
- [Hologram Camera prefab and controls](https://lfdocs.lookingglassfactory.com/software/index/prefabs/hologram-camera)
- [Unity Plugin 4.0 alpha documentation](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)
- [Migration guide from 3.x to 4.0](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha/migrating-from-unity-plugin-v3.x-to-v4.0)
- [Quilt format and image layout](https://lfdocs.lookingglassfactory.com/keyconcepts/quilts)
- [Create a video from Quilt images](https://lfdocs.lookingglassfactory.com/keyconcepts/quilts/quilt-video-encoding)
- [Unity Recorder capture interval settings](https://docs.unity3d.com/Packages/com.unity.recorder@5.1/manual/RecorderWindowRecordingProperties.html)

The official documentation reflects public material reviewed as of 2026-10-07. This is not a report of reproducing SDK or Bridge behavior on hardware.
