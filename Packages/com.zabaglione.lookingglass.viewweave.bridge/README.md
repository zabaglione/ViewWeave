# ViewWeave Bridge

> For Unity and package developers integrating output. Start with the [User Guide](../../docs/user-guide.md) for setup and operation or the [Technical Guide](../../docs/technical-guide.md) for design, tests, and dated records.

This source package supports repository development. End users should install one self-contained pipeline package from `Distribution/urp` or `Distribution/hdrp`; do not install this source package beside a distribution package because they include the same runtime assemblies. The official Looking Glass Bridge runtime is provided separately.

## Output routes

- **Windows Bridge Worker:** regular Editor Play and Windows Player output on D3D11 or D3D12. See [Compatibility](../../docs/compatibility.md) for current conditions and limits.
- **macOS Looking Glass output:** `BridgeMetalQuiltOutput` uses Bridge calibration and cell-array metadata with Metal on Apple Silicon. See the [macOS Metal guide](../../docs/macos-metal.md) and [Compatibility](../../docs/compatibility.md) for validated conditions.
- **Legacy Direct:** a separate Windows D3D11-only route.

## Quick Start samples

Import **URP Quick Start** or **HDRP Quick Start** from the matching distribution package's Package Manager **Samples** tab, open that sample's `QuickStart.unity`, then select **ViewWeave Bridge Rig** and start **Preview > Start Looking Glass Device Preview**. Use **Start Local Edit Mode Preview** to inspect output without a device. Check [Compatibility](../../docs/compatibility.md) before selecting a runtime configuration.

Edit Mode Preview is static. Enter Play to run the animated sample and start device output automatically, matching the menu-created Rig. The helper selects the platform output and preserves the saved startup preference. If you disable automatic startup, use **Bridge > Start Output** manually.

The optional **Windows URP Lit** and **Windows HDRP Lit** samples remain available for their existing Bridge demonstration workflow. For installation and everyday operation, use the repository [README](../../README.md) and [User Guide](../../docs/user-guide.md).

## License scope

Original ViewWeave code is Apache-2.0. The compiled native shims use external Unity PluginAPI declarations; [NOTICE.md](NOTICE.md) and the full texts in ThirdPartyLicenses distinguish those rights. The official Bridge runtime is installed separately under its own agreement. [Compatibility](../../docs/compatibility.md#current-development-version-validation) separates current artifact evidence from older fixtures.
