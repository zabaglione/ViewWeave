# ViewWeave HDRP

> For HDRP integration developers. Use the [User Guide](../../docs/user-guide.md) for setup and operation, or the [Technical Guide](../../docs/technical-guide.md) for implementation and test records.

This source package provides the ViewWeave HDRP adapter for repository development and tests. End users should install the self-contained [`Distribution/hdrp` package](../../Distribution/hdrp/README.md), which includes Core, Bridge, and HDRP.

HDRP projects require Linear color space.

## Quick Start

1. Import **HDRP Quick Start** from the distribution package's Package Manager **Samples** tab.
2. Open `QuickStart.unity` in the imported sample.
3. Select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. To inspect the Quilt without a display, choose **Start Local Edit Mode Preview**.

Edit Mode Preview shows a static scene. For animation on a connected display, enter Play and explicitly choose **Bridge > Start Output**; output does not start automatically.

The macOS Looking Glass Go route uses the separate Metal output. The Windows Quick Start scene has not been revalidated. See [Compatibility](../../docs/compatibility.md) and the [macOS Metal guide](../../docs/macos-metal.md) for current conditions. The optional Windows HDRP Lit sample remains available as a separate Bridge demonstration.
