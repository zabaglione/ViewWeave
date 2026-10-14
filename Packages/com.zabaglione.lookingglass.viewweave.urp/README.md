# ViewWeave URP

> For URP integration developers. Use the [User Guide](../../docs/user-guide.md) for setup and operation, or the [Technical Guide](../../docs/technical-guide.md) for implementation and test records.

This source package provides the ViewWeave URP adapter for repository development and tests. End users should install the self-contained [`Distribution/urp` package](../../Distribution/urp/README.md), which includes Core, Bridge, and URP.

## Quick Start

1. Import **URP Quick Start** from the distribution package's Package Manager **Samples** tab.
2. Open `QuickStart.unity` in the imported sample.
3. Select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. To inspect the Quilt without a display, choose **Start Local Edit Mode Preview**.

Edit Mode Preview shows a static scene. For animation on a connected display, enter Play and explicitly choose **Bridge > Start Output**; output does not start automatically.

macOS uses the separate Metal output route. See [Compatibility](../../docs/compatibility.md) and the [macOS Metal guide](../../docs/macos-metal.md) for current conditions. The optional Windows URP Lit sample remains available as a separate Bridge demonstration.

## Quilt TAA and view histories

The default **Isolated View Histories** mode keeps one disabled Camera, `UniversalAdditionalCameraData`, and persistent URP history for each rendered logical view. The adapter copies the supported public rendering settings from the Reference Camera and renders each view synchronously with `Camera.Render()`, because Standard RenderRequests suppress URP TAA history writes. URP TAA requires Post Processing, a single-sample target, a Universal Renderer, no Camera Stack, and no dynamic resolution. With Post Processing enabled, active Volume Motion Blur is rejected until its Quilt behavior is validated. **Shared Camera Compatibility** uses the shared-camera guard, which temporarily disables and then restores TAA.

Target MSAA selection follows the effective renderer while preserving the Rig Source Camera's `allowMSAA`; Deferred targets remain single-sample.

These controls are in **Rig > Rendering > URP Rendering**. The budget defaults to 4096 MB and the camera limit to 256. The budget compares observed graphics-memory growth with estimated storage for new or enlarged RenderTextures; it is not a complete VRAM cap. See [the URP validation record](../../docs/development/urp-view-history-validation-20261011.md) for the dated runtime evidence and remaining limits.
