# ViewWeave HDRP

> For HDRP integration developers. Use the [User Guide](../../docs/user-guide.md) for setup and operation, or the [Technical Guide](../../docs/technical-guide.md) for implementation and test records.

This source package provides the ViewWeave HDRP adapter for repository development and tests. End users should install the self-contained [`Distribution/hdrp` package](../../Distribution/hdrp/README.md), which includes Core, Bridge, and HDRP.

HDRP projects require Linear color space.

## HDRP view rendering

By default, each rendered logical view has its own persistent HDRP Camera history. ViewWeave copies HDRP camera settings from the Reference Camera. `FollowScene` meters the central view on the GPU and shares its exposure across the Quilt as Fixed exposure, including scene compensation; output waits until the first neutral meter sample is discarded and exposure history is ready. A Fixed scene uses `fixedExposure - compensation`. Choose `ManualFixed` explicitly to enter one Quilt-wide EV value.

The default isolated-history mode retains the scene's TAA, SSR, SSGI, volumetric fog/cloud, and Motion Blur settings. The opt-in `SharedCameraCompatibility` mode applies the legacy changes: Motion Blur, SSR, SSGI, and clouds off; TAA off; volumetric fog uses Gaussian filtering; non-Fixed exposure uses Manual EV, defaulting to 12. All HDRP controls are under **Rig > Rendering**. History Resource Budget defaults to 4096 MB and Maximum History Cameras to 256. The budget uses the larger of observed graphics-driver growth and estimated new or enlarged RenderTextures; Release driver readings are zero, and unobserved buffers and driver overhead are outside the estimate, so it is not a complete VRAM cap. See [Compatibility](../../docs/compatibility.md) and the [HDRP view-history validation](../../docs/development/hdrp-view-history-validation-20261010.md) for current evidence and limits.

## Quick Start

1. Import **HDRP Quick Start** from the distribution package's Package Manager **Samples** tab.
2. Open `QuickStart.unity` in the imported sample.
3. Select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. To inspect the Quilt without a display, choose **Start Local Edit Mode Preview**.

Edit Mode Preview shows a static scene. For animation on a connected display, enter Play. The Sample starts output automatically by default, matching a Rig created from the menu. A user-disabled startup option remains respected; use **Bridge > Start Output** for manual control.

macOS uses the separate Metal output route. See [Compatibility](../../docs/compatibility.md) and the [macOS Metal guide](../../docs/macos-metal.md) for current conditions. The optional Windows HDRP Lit sample remains available as a separate Bridge demonstration.
