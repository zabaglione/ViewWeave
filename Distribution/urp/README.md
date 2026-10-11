# ViewWeave URP Git package

Install this folder from the repository using Unity Package Manager:

    https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main

Use Unity 6000.6.4f1 with the matching Unity URP render pipeline. Install and start the official Looking Glass Bridge runtime and connect your display.

This is an experimental development version. Local imports were checked. Current macOS validation passed Native CTest 7/7 and three v3 connection/cancellation cycles. Built-in Linear and Gamma GPU comparisons each passed 28 cases across 64,512 samples, with maximum errors 0.001956 and 0.001961 against a 0.006 threshold; URP and HDRP Linear comparisons each passed 28 cases. The URP Editor flow checked Preview/Play, PNG capture, silent recording/playback, and Stop/Restart. Audio recording is unsupported on macOS, and physical output on 16-inch Spatial Landscape remains untested. See the [current calibration validation record](https://github.com/zabaglione/ViewWeave/blob/main/docs/macos-metal-subpixel-validation-20261011.md) for scope and limits. The current Windows Bridge shim has Native CTest 15/15 and transport-only CTest 11/11 evidence plus selected Editor fixtures and one official-Bridge device Player sample per URP/HDRP for D3D11 Development and D3D12 Release; these are limited routes, not general project or optical guarantees. See the [Windows feature record](https://github.com/zabaglione/ViewWeave/blob/main/docs/windows-current-feature-validation-20261011.md). These test-only Player samples are separate from the Quick Start Sample: Windows Quick Start UPM import, D3D11/D3D12 output and Player builds remain unverified. Anonymous Git installation after public access is enabled is unverified.

## Quick Start

1. In Package Manager, select this package and import **URP Quick Start** from **Samples**.
2. Open **QuickStart.unity**. The scene selects Metal or Windows D3D11/D3D12 automatically.
3. Select **ViewWeave Bridge Rig** and click **Preview > Start Looking Glass Device Preview**. Use Local Preview without a device; enter Play for animation.

All ordinary camera, focus, rendering, preview, capture and output controls are on **ViewWeave Rig**. The sample uses Quarter view density and Balanced resolution; choose Full for smoother viewpoint changes. No sample generator is required. The existing Windows Lit generator remains an optional separate sample.

macOS output uses Bridge-reported calibration on Apple Silicon and requires macOS 26.0 or later, Metal, an unrotated native-resolution extended display, and standard RGB/BGR order. `CellPatternMode` 0–4 accepts up to four cells, with minimum counts of 1 for mode 0, 2 for modes 1/2/4, and 4 for mode 3. A cell table requires a mode key (`CellPatternMode` or `cellPatternMode`); tableless output is mode 0 only. Cell tables are read through the local Bridge HTTP JSON endpoint at 127.0.0.1:33334; tableless mode 0 uses the native SDK only. Tableless mode 0 allows `flipX`/`invertedViews` 0 or 1; profiles with cells require `invertedViews=1`. Support is calibration-driven and does not imply universal hardware support. See the [Bridge subpixel calibration note](https://github.com/zabaglione/ViewWeave/blob/main/docs/bridge-subpixel-calibration.md). Windows Bridge Worker output uses D3D11/D3D12. See [Compatibility](https://github.com/zabaglione/ViewWeave/blob/main/docs/compatibility.md) for tested scope and limitations.

ViewWeave's original code is Apache-2.0. [Native plugin notices](NOTICE.md) and [Third-Party Notices](THIRD_PARTY_NOTICES.md) describe separate Unity PluginAPI and Bridge terms. Full Unity license texts are included in ThirdPartyLicenses. The macOS shim is ad hoc signed; Developer ID signing, notarization and end-user Gatekeeper acceptance are unverified.

[User guide and illustrated setup](https://github.com/zabaglione/ViewWeave/blob/main/docs/user-guide.md) | [Technical guide](https://github.com/zabaglione/ViewWeave/blob/main/docs/technical-guide.md) | [日本語](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/user-guide.md)
