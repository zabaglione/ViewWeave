# ViewWeave HDRP Git package

Install this folder from the repository using Unity Package Manager:

    https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main

Use Unity 6000.6.4f1 with the matching Unity HDRP render pipeline. Install and start the official Looking Glass Bridge runtime and connect your display.

This is an experimental development version. Local imports were checked. The current macOS calibration-driven Metal path has scoped Apple Silicon / Looking Glass Go Editor, recording/playback and Development/Release device Player evidence; four-view URP/HDRP rendering-only Players also passed history, retirement and budget checks. Historical Go optical observations predate the generalized calibration path, and non-Go physical optical output remains unverified. See the [current macOS record](https://github.com/zabaglione/ViewWeave/blob/main/docs/macos-metal-current-validation-20261011.md) and [calibration generalization record](https://github.com/zabaglione/ViewWeave/blob/main/docs/macos-metal-generalization-validation-20261011.md). The current Windows Bridge shim has Native CTest 15/15 and transport-only CTest 11/11 evidence plus selected Editor fixtures and one official-Bridge device Player sample per URP/HDRP for D3D11 Development and D3D12 Release; these are limited routes, not general project or optical guarantees. See the [Windows feature record](https://github.com/zabaglione/ViewWeave/blob/main/docs/windows-current-feature-validation-20261011.md). These test-only Player samples are separate from the Quick Start Sample: Windows Quick Start UPM import, D3D11/D3D12 output and Player builds remain unverified. Anonymous Git installation after public access is enabled is unverified.

## Quick Start

1. In Package Manager, select this package and import **HDRP Quick Start** from **Samples**.
2. Open **QuickStart.unity**. The scene selects Metal or Windows D3D11/D3D12 automatically.
3. Select **ViewWeave Bridge Rig** and click **Preview > Start Looking Glass Device Preview**. Use Local Preview without a device; enter Play for animation.

All ordinary camera, focus, rendering, preview, capture and output controls are on **ViewWeave Rig**. The sample uses Quarter view density and Balanced resolution; choose Full for smoother viewpoint changes. No sample generator is required. The existing Windows Lit generator remains an optional separate sample.

macOS output uses Bridge-reported calibration on Apple Silicon and requires macOS 26.0 or later, Metal, an unrotated native-resolution extended display, standard RGB/BGR calibration (`mode=0`, `cells=0`), and `flipX`/`invertedViews` values of 0 or 1. Cell tables and nonzero cell modes are unsupported; this does not imply universal hardware support. Windows Bridge Worker output uses D3D11/D3D12. See [Compatibility](https://github.com/zabaglione/ViewWeave/blob/main/docs/compatibility.md) for tested scope and limitations.

ViewWeave's original code is Apache-2.0. [Native plugin notices](NOTICE.md) and [Third-Party Notices](THIRD_PARTY_NOTICES.md) describe separate Unity PluginAPI and Bridge terms. Full Unity license texts are included in ThirdPartyLicenses. The macOS shim is ad hoc signed; Developer ID signing, notarization and end-user Gatekeeper acceptance are unverified.

[User guide and illustrated setup](https://github.com/zabaglione/ViewWeave/blob/main/docs/user-guide.md) | [Technical guide](https://github.com/zabaglione/ViewWeave/blob/main/docs/technical-guide.md) | [日本語](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/user-guide.md)
