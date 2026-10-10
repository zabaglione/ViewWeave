# ViewWeave native plugin notices

ViewWeave's original implementation is licensed under Apache-2.0. See `LICENSE.md` in the source Bridge package or `LICENSE` in a self-contained distribution or CI artifact. This license does not replace the terms of Unity PluginAPI, Unity, or the separately installed Looking Glass Bridge runtime.

## Unity Native Plugin API

Unity Native Plugin API copyright © 2015 Unity Technologies ApS

`IUnityInterface.h`, `IUnityGraphics.h`, and `IUnityGraphicsD3D12.h` in the public NativeRenderingPlugin reference specify the Unity Companion License for Unity-dependent projects. The full Unity Companion License v1.4 (2024-10-29), including its use conditions and disclaimer, is supplied in `ThirdPartyLicenses/Unity-Companion-License.txt`.

The public reference is Unity-Technologies/NativeRenderingPlugin, commit `522254181faf188efa8b50c3e3bf6fce720b26e4`. Its repository-level notice is:

Copyright (c) 2016, Unity Technologies

The full MIT notice is supplied in `ThirdPartyLicenses/Unity-NativeRenderingPlugin-MIT.txt` for the public reference files without an overriding file-specific notice, including `IUnityGraphicsMetal.h` and `IUnityGraphicsD3D11.h`. The MIT notice does not replace the explicit Unity Companion License notices above. The external headers themselves are not included in ViewWeave packages.

The packaged macOS shim was compiled using the pinned public headers; their SHA-256 values are recorded in `Plugins/macOS/native-provenance.json` (under `ViewWeave/Bridge/Plugins` in a distribution). The packaged Windows shim was compiled using the Unity 6000.6.4f1 PluginAPI. Its original header hashes and file-specific notices were not retained, so it is not asserted to use the pinned public files or identical header versions. The Unity notices are supplied separately from the Apache-2.0 license for ViewWeave's own code; this does not establish additional redistribution rights for unverified external components. See `Plugins/native-provenance.json` for that artifact's build and validation limits.

## Looking Glass Bridge

The native shim is ViewWeave's own implementation. Official Looking Glass Bridge binaries, headers, libraries, and SDK source are not bundled. The runtime must be installed separately under the vendor terms applicable to the user. Apache-2.0 for ViewWeave does not grant rights to that runtime or its bundled dependencies.
