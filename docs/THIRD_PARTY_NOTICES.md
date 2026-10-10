[Japanese](ja/THIRD_PARTY_NOTICES.md)

# Third-Party Notices

ViewWeave is an unofficial implementation developed independently from Looking Glass Factory's official Unity SDK. It does not imply affiliation, endorsement, complete compatibility, or permission to use official branding.

## ViewWeave original code

Original ViewWeave code is licensed under [Apache-2.0](../LICENSE). This does not grant rights to Unity, Looking Glass Bridge, device software, trademarks, third-party patents, or other external dependencies. URP/HDRP reference Unity packages as dependencies; their source is not forked or included.

## Unity Native Plugin API

The repository-owned native shims compile against external Unity PluginAPI declarations. Packages contain the compiled shims and [native plugin notices](../Packages/com.zabaglione.lookingglass.viewweave.bridge/NOTICE.md), not the external headers or the official Unity plugin implementation.

Unity Native Plugin API copyright © 2015 Unity Technologies ApS

The public headers `IUnityInterface.h`, `IUnityGraphics.h`, and `IUnityGraphicsD3D12.h` specify the **Unity Companion License** for Unity-dependent projects. The complete [Unity Companion License v1.4](../Packages/com.zabaglione.lookingglass.viewweave.bridge/ThirdPartyLicenses/Unity-Companion-License.txt) is bundled with the source Bridge package, both pipeline distributions, and newly generated macOS CI artifacts. These conditions remain separate from ViewWeave's Apache-2.0 license. See the [authoritative Unity license](https://unity.com/legal/licenses/unity-companion-license).

Copyright (c) 2016, Unity Technologies

The pinned NativeRenderingPlugin repository also supplies an [MIT license](../Packages/com.zabaglione.lookingglass.viewweave.bridge/ThirdPartyLicenses/Unity-NativeRenderingPlugin-MIT.txt). It is included for reference files without an overriding file-specific notice, including `IUnityGraphicsMetal.h` and `IUnityGraphicsD3D11.h`; it does not replace the explicit Unity Companion License notices. The public reference is [commit 522254181faf188efa8b50c3e3bf6fce720b26e4](https://github.com/Unity-Technologies/NativeRenderingPlugin/tree/522254181faf188efa8b50c3e3bf6fce720b26e4).

The macOS provenance records that reference and hashes of its three build headers. The Windows provenance records the installed Unity 6000.6.4f1 PluginAPI, but the original individual header hashes and notices were not retained. Those installed headers have not been equated to the pinned public reference. The bundled notices identify external Unity rights without extending Apache-2.0 to those headers. Exact Windows header provenance remains unverified.

## Separately installed Looking Glass Bridge

ViewWeave dynamically uses the public ABI of a separately installed Bridge runtime. Official Bridge binaries, headers, libraries, and SDK source are not bundled in the repository or packages. The installed Bridge 2.6.3 notice identifies Bridge as proprietary Looking Glass Factory software and points to vendor Terms; its bundled libraries have separate licenses.

Install and use the runtime under the agreement applicable to you. ViewWeave's Apache-2.0 license does not grant commercial-use or redistribution rights for Bridge. The [native function reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference) describes redistribution in some circumstances; it is not a general grant. Review the [vendor Terms](https://lookingglassfactory.com/terms-and-conditions) and any individual agreement before commercial use or bundling vendor software. This project has not resolved every Bridge agreement and does not provide a legal conclusion.

## API and behavior references

These primary references were used for research, not copied official SDK implementation:

- [Looking Glass Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [Looking Glass software documentation](https://lfdocs.lookingglassfactory.com/software/index)
- [Looking Glass Unity Plugin 4.0 alpha](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)
- [Bridge native function reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)
- [Unity Custom Package Samples](https://docs.unity3d.com/6000.0/Documentation/Manual/cus-samples.html)
- [Unity UPM Git dependencies](https://docs.unity.com/en-us/engine/6000.0/manual/packages-list/upm-git)
