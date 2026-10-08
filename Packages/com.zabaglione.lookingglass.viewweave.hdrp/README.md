# ViewWeave HDRP

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package is for repository development and tests. End users should install the self-contained `Distribution/hdrp` Git package, which bundles Core, Bridge, and HDRP.

The supported regular Bridge output environment is Unity 6000.6.4f1, HDRP 17.6, Windows, and D3D11; it works in Windows D3D11 Editor Play and Windows D3D11 Player. A separate explicitly started `ViewWeaveRig` Editor preview is available in Edit Mode on Windows D3D11/D3D12. URP/HDRP local GPU fixtures passed for layout rebuild and proof-based Stop/readback with zero pending retention. D3D11 and D3D12 device fixtures passed URP/HDRP Start/Stop/Restart at a 3840x2160 display profile with a 7680x4320, 8x6/48-view Quilt; each Start and Restart submitted/completed once, and Stop left native/retention pending at zero. Manual optical review remains pending. A targeted Editor fixture confirmed Domain Reload cleanup and explicit restart; see [Graphics backends](https://github.com/zabaglione/ViewWeave/blob/main/docs/graphics-backends.md) for File > Exit results; normal Editor exit remains unverified. Regular D3D12 Bridge output and macOS output are unsupported. Install the Looking Glass Bridge runtime separately and connect a display.

Set Player Settings Color Space to Linear. Standard HDRP template Cube and Box objects can be displayed using their ordinary HDRP materials and lights. Create `GameObject > ViewWeave > Bridge Rig`; no sample import or object-specific script is needed.

`HdrpQuiltCamera` adapts effects for quilt rendering with a temporary Volume override while each quilt is rendered. Its default adaptation suppresses Motion Blur, SSR, SSGI, volumetric fog, and volumetric clouds in that scope. TAA is temporarily disabled and restored. Auto Exposure is converted to one fixed exposure across all quilt views, default EV 12; an existing Fixed exposure value is retained. Normal Fog, Sky, Bloom, the MainCamera, original Volume Profiles, and Project Settings remain unchanged.

Optional examples are available from Package Manager Samples and are not copied into project Assets until explicitly imported. The HDRP sample generator is only a convenience for creating a demonstration scene.

See [Getting started](../../docs/getting-started.md) and [Compatibility](../../docs/compatibility.md).
