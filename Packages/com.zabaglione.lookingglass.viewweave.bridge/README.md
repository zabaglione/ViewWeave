# ViewWeave Bridge

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package submits completed quilts to an installed Looking Glass Bridge runtime. It does not include Bridge binaries, proprietary headers, or runtime assets. End users should install the self-contained URP or HDRP Git distribution package. Do not install this source package alongside a distribution package because both contain the same runtime assemblies.

## Supported output

Bridge output is supported in Windows D3D11 Unity Editor Play and Windows D3D11 Player. Edit Mode, D3D12, and macOS output are not supported. Install the Looking Glass Bridge runtime separately and connect a display.

The supported entry point is `ViewWeaveBridgeSession` with `ViewWeaveRig` and `BridgeWorkerQuiltOutput` on the same rig. Use `GameObject > ViewWeave > Bridge Rig` to create a ready-to-run rig. Standard scene objects such as Cube and Box render with their existing pipeline materials and lights; they do not need ViewWeave components or sample generators. The menu copies the MainCamera framing and uses the selected Renderer center to set zero-parallax distance when available.

The Editor must run on Direct3D11 for Play output. Start Unity with `-force-d3d11`, or configure Windows Graphics APIs with D3D11 first and restart the Editor. Check `SystemInfo.graphicsDeviceType` to confirm the active API.

The optional Windows URP Lit and Windows HDRP Lit examples are declared as Package Manager Samples. They are copied under `Samples~` and enter the project Assets only after the user explicitly imports them.

`ViewWeaveBridgeSession` exposes `StartSession()`, `StopSession()`, `SetQuality(QuiltQualityPreset)`, `State`, `Quality`, `Profile`, and ASCII `LastError`. The default quality is `Preview`. The Bridge runtime is located from an explicit absolute `runtimePath`, the `LKG_BRIDGE_PATH` environment variable, or Windows Bridge uninstall registry entries.

The legacy Direct route is separate from `ViewWeaveBridgeSession` and requires the validated single-threaded Direct3D11 configuration, including `-force-gfx-direct` and `runInBackground=true`. D3D12, Vulkan, and macOS native output are not implemented.

See the repository [Getting started](../../docs/getting-started.md), [Compatibility](../../docs/compatibility.md), and [third-party notices](../../docs/THIRD_PARTY_NOTICES.md).
