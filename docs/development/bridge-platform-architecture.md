[日本語](ja/bridge-platform-architecture.md)

# Bridge platform boundaries

> Audience: Unity, rendering, and package developers. This page covers the technical design, implementation, or procedure stated below. For user workflows, see the [User guide](../user-guide.md); for the complete technical index, see the [Technical guide](../technical-guide.md).

The Bridge package separates shared data and orchestration from platform transport. Namespaces, assemblies, existing script GUIDs, and public component types are preserved so existing scenes can keep their references.

| Responsibility | Managed source under the Bridge package | Native source |
|---|---|---|
| Shared profile, fixed-width metadata ABI, runtime-path precedence | `Runtime/Common` | `Native/common` |
| macOS discovery, Metal producer/presenter and GPU retirement | `Runtime/Platforms/Metal`, `Editor/Platforms/Metal` | `Native/platform/macos` |
| Windows discovery, D3D11/D3D12 worker, legacy D3D11, playback, preview and recovery | `Runtime/Platforms/Windows`, `Editor/Platforms/Windows` | `Native/platform/windows` |
| Shared Rig creation, Inspector/authoring/playback UI, output dispatch, display resolver, private Editor producer, Quilt texture retention and playback compatibility | `Editor/Common` | CMake selects native sources |

`ViewWeaveRig` is the user-facing configuration entry point on every platform. Camera, focus, framing, rendering, capture, and pipeline settings stay in its common frontend. Quality, connection settings, output actions, and status are also exposed there, with the attached backend providing their implementation and availability. Additional components are used for transport, rendering adapters, or tools that need their own workflow; transport components do not become separate primary settings screens.

The common Inspector and Rig Authoring window share `ViewWeaveRigInspectorUI`. Its common portion owns authoring controls, while the Windows and Metal partials own their output-specific controls. `BridgeEditorDisplaySettings` registers one shared display-profile provider and dispatches to the current output. This preserves the existing assembly boundaries and script GUIDs. Backend selection follows the attached components rather than hiding settings solely by Editor OS. A scene's stored backend is not automatically replaced when moving between operating systems.

Metal currently changes quality and transport while stopped, with GPU retirement completed. Windows retains its live quality reconfiguration. Metal permits live framing, view sampling and Auto Render pause/resume; shared recording leases protect these settings during capture. Output shutdown cancels that renderer's active or paused recording before restoring the local profile. Metal and Windows both provide Edit Mode Preview and Play-mode PNG/sequence playback through the common Rig frontend. Metal uses private Editor producers and asynchronous Quilt texture retirement, alongside its native presentation retirement; Windows retains its Worker/fence/reaper protocol. Metal output shutdown synchronously releases the playback operation lease before restoring the local Rig profile. The independently retained playback texture is released only after GPU proof. Playback resumes live rendering only after a normal stop with unchanged ownership and configuration.

`BridgeRuntimeLocator` shares explicit-path → environment-variable → automatic-discovery precedence. It dispatches by Editor host OS, or by target OS in a Player. DLL validation and registry discovery belong to Windows; dylib/application discovery belongs to macOS. Common code does not implement either transport.

The common native metadata reader accepts function pointers and translates SDK values into fixed-width ViewWeave structs. The platform owns SDK module loading, character encoding, admission, synchronization, initialization, shutdown and fault retention. SDK `long`/`unsigned long` widths follow the platform ABI. The D3D descriptor restrictions in the portable worker validation remain unchanged; they do not authorize Metal to use a Windows worker.

CMake compiles one platform shim and transport implementation, together with common sources. The root `bridge_shim.cpp` and `bridge_worker_transport.cpp` remain small compatibility dispatchers for older source-including native tests; they are not compiled alongside the selected implementations. Unsupported D3D exports in the macOS plugin are compatibility failure stubs under `platform/unsupported`, and contain no D3D implementation.

Managed folder placement expresses responsibility while the existing `ViewWeave.Bridge` and Editor assemblies remain intact. Component types stay available for serialized references and clear unsupported-platform errors. Windows worker recovery hooks activate only in the Windows Editor. Metal uses its own retirement records and reaper. GPU lifetime and shutdown procedures are not shared between the two transports.

The bundled Windows DLL and its provenance remain the previous validated artifact. This source reorganization does not establish a new Windows native build or device result. See [macOS validation](macos-metal-validation-20261010.md) for the current verification boundary.
