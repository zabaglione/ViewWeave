# ViewWeave Bridge

> For Unity and package developers integrating output. Start with the [User Guide](../../docs/user-guide.md) for setup and operation or the [Technical Guide](../../docs/technical-guide.md) for design, tests, and dated records.

This source package supports repository development. End users should install one self-contained pipeline package from `Distribution/urp` or `Distribution/hdrp`; do not install this source package beside a distribution package because they include the same runtime assemblies. The official Looking Glass Bridge runtime is provided separately.

## Output routes

- **Windows Bridge Worker:** regular Editor Play and Windows Player output on D3D11 or D3D12. See [Compatibility](../../docs/compatibility.md) for current conditions and limits.
- **macOS Looking Glass Go:** a separate `BridgeMetalQuiltOutput` route using Metal on Apple Silicon. See the [macOS Metal guide](../../docs/macos-metal.md) and [Compatibility](../../docs/compatibility.md).
- **Legacy Direct:** a separate Windows D3D11-only route.

## Quick Start samples

Import **URP Quick Start** or **HDRP Quick Start** from the matching distribution package's Package Manager **Samples** tab, open that sample's `QuickStart.unity`, then select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. Use **Start Local Edit Mode Preview** to inspect output without a device. The Windows Quick Start scene has not been revalidated; check [Compatibility](../../docs/compatibility.md) before selecting a runtime configuration.

Edit Mode Preview is static. To show the animated sample during Play on a connected display, explicitly choose **Bridge > Start Output**. The sample helper selects the platform output component but does not start it.

The optional **Windows URP Lit** and **Windows HDRP Lit** samples remain available for their existing Bridge demonstration workflow. For installation and everyday operation, use the repository [README](../../README.md) and [User Guide](../../docs/user-guide.md).
