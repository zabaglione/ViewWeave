# ViewWeave HDRP Git package

Install this folder from the repository using Unity Package Manager:

    https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main

Use Unity 6000.6.4f1 with the matching Unity HDRP render pipeline. Install and start the official Looking Glass Bridge runtime and connect your display.

This is an experimental development version. Local imports and macOS Metal Editor output on Looking Glass Go were checked. Windows UPM import, D3D11/D3D12 output and Player builds of the Quick Start Sample are unverified. The current Windows DLL has focused native-test evidence only; older Windows fixtures do not validate this artifact's physical output. Anonymous Git installation after public access is enabled is unverified.

## Quick Start

1. In Package Manager, select this package and import **HDRP Quick Start** from **Samples**.
2. Open **QuickStart.unity**. The scene selects Metal or Windows D3D11/D3D12 automatically.
3. Select **ViewWeave Bridge Rig** and click **Preview > Start Looking Glass Device Preview**. Use Local Preview without a device; enter Play for animation.

All ordinary camera, focus, rendering, preview, capture and output controls are on **ViewWeave Rig**. The sample uses Quarter view density and Balanced resolution; choose Full for smoother viewpoint changes. No sample generator is required. The existing Windows Lit generator remains an optional separate sample.

macOS output targets Apple Silicon, macOS 26.0 or later, Metal and Looking Glass Go in Desktop Mode. Windows Bridge Worker output uses D3D11/D3D12. See [Compatibility](https://github.com/zabaglione/ViewWeave/blob/main/docs/compatibility.md) for tested scope and limitations.

ViewWeave's original code is Apache-2.0. [Native plugin notices](NOTICE.md) and [Third-Party Notices](THIRD_PARTY_NOTICES.md) describe separate Unity PluginAPI and Bridge terms. Full Unity license texts are included in ThirdPartyLicenses. The macOS shim is ad hoc signed; Developer ID signing, notarization and end-user Gatekeeper acceptance are unverified.

[User guide and illustrated setup](https://github.com/zabaglione/ViewWeave/blob/main/docs/user-guide.md) | [Technical guide](https://github.com/zabaglione/ViewWeave/blob/main/docs/technical-guide.md) | [日本語](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/user-guide.md)
