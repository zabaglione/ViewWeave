# ViewWeave HDRP

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package is for repository development and tests. End users should install the self-contained `Distribution/hdrp` Git package, which bundles Core, Bridge, and HDRP.

The supported environment is Unity 6000.6.4f1, HDRP 17.6, Windows, and D3D11. Bridge output works in Windows D3D11 Editor Play and Windows D3D11 Player. Install the Looking Glass Bridge runtime separately and connect a display. Edit Mode, D3D12, and macOS output are unsupported.

Set Player Settings Color Space to Linear. Standard HDRP template Cube and Box objects can be displayed using their ordinary HDRP materials and lights. Create `GameObject > ViewWeave > Bridge Rig`; no sample import or object-specific script is needed.

`HdrpQuiltCamera` adapts effects for quilt rendering with a temporary Volume override while each quilt is rendered. Its default adaptation suppresses Motion Blur, SSR, SSGI, volumetric fog, and volumetric clouds in that scope. TAA is temporarily disabled and restored. Auto Exposure is converted to one fixed exposure across all quilt views, default EV 12; an existing Fixed exposure value is retained. Normal Fog, Sky, Bloom, the MainCamera, original Volume Profiles, and Project Settings remain unchanged.

Optional examples are available from Package Manager Samples and are not copied into project Assets until explicitly imported. The HDRP sample generator is only a convenience for creating a demonstration scene.

See [Getting started](../../docs/getting-started.md) and [Compatibility](../../docs/compatibility.md).
