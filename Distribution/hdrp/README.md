# ViewWeave HDRP Git package

Install this folder from the repository using Unity Package Manager:

    https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#v0.1.0

Use Unity 6000.6.4f1 with the matching Unity HDRP render pipeline. For device output, install and start the official Looking Glass Bridge runtime and connect your display.

This is an experimental v0.1.0 release. See [Compatibility](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/compatibility.md) for the tested Editor/Player and device conditions, memory and shutdown limits, and outstanding installation checks.

## Quick Start

1. In Package Manager, select this package and import **HDRP Quick Start** from **Samples**.
2. Open **QuickStart.unity**. The scene selects Metal or Windows D3D11/D3D12 automatically.
3. Select **ViewWeave Bridge Rig** and click **Preview > Start Looking Glass Device Preview**. Use Local Preview without a device. For animation, enter Play: output starts automatically by default, matching a menu-created Rig. If you disable automatic startup, use **Bridge > Start Output** manually.

All ordinary camera, focus, rendering, preview, capture and output controls are on **ViewWeave Rig**. The sample uses Quarter view density and Balanced resolution; choose Full for smoother viewpoint changes. No sample generator is required. The existing Windows Lit generator remains an optional separate sample.

Windows device output uses D3D11/D3D12. macOS requires Apple Silicon, macOS 26.0 or later, Metal, and an unrotated native-resolution extended display; see the [macOS setup guide](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/macos-metal.md). Audio recording is unsupported on macOS. For VFX Graph, use Linear color space and review the [VFX guide](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/vfx-graph.md).

ViewWeave's original code is Apache-2.0. [Native plugin notices](NOTICE.md) and [Third-Party Notices](THIRD_PARTY_NOTICES.md) describe separate Unity PluginAPI and Bridge terms. Full Unity license texts are included in ThirdPartyLicenses. The macOS shim is ad hoc signed; Developer ID signing, notarization and end-user Gatekeeper acceptance are unverified.

[User guide and illustrated setup](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/user-guide.md) | [Technical guide](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/technical-guide.md) | [日本語](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/ja/user-guide.md)
