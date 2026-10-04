# ViewWeave URP

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package is for repository development and tests. End users should install the self-contained `Distribution/urp` Git package, which bundles Core, Bridge, and URP.

The supported environment is Unity 6000.6.4f1, URP 17.6, Windows, and D3D11. Bridge output works in Windows D3D11 Editor Play and Windows D3D11 Player. Install the Looking Glass Bridge runtime separately and connect a display. Edit Mode, D3D12, and macOS output are unsupported.

Standard URP Cube and Box objects render with their ordinary materials and lights. Create `GameObject > ViewWeave > Bridge Rig`; no sample import or object-specific script is needed. URP supports a Base camera; camera stacks are rejected. TAA is temporarily stopped during quilt rendering.

The optional Windows URP Lit example is available from Package Manager Samples and is not copied into project Assets until explicitly imported. Its generator is only a convenience for creating a demonstration scene.

See [Getting started](../../docs/getting-started.md) and [Compatibility](../../docs/compatibility.md).
