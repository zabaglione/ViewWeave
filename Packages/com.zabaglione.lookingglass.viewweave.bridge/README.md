# ViewWeave Bridge

[Japanese lifecycle guide](../../docs/ja/bridge-output-lifecycle.md)

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package submits completed quilts to an installed Looking Glass Bridge runtime. It does not include Bridge binaries, proprietary headers, or runtime assets. End users should install the self-contained URP or HDRP Git distribution package. Do not install this source package alongside a distribution package because both contain the same runtime assemblies.

## Supported output

Regular Bridge Worker output is supported in Windows D3D11 and D3D12 Unity Editor Play and Windows Player. The `ViewWeaveRig` Editor preview is a separate Edit Mode authoring route. `Pause Preview` drains work and hides the device window while retaining that preview's producer, Unity textures, and output lease; `Resume Preview` continues the same lease. `Close Preview` requests lease return. Native first reports that it no longer references the old source textures; managed then waits for Core/transfer and producer GPU completion, retires those textures, and sends an ACK. Native permits the next generation only after that ACK. The process-level SDK endpoint stays hidden and parked between owners; only process shutdown releases it. Play and Edit Mode device preview take turns owning that endpoint. See [Bridge output lifecycle](../../docs/bridge-output-lifecycle.md) for the flow and quality behavior.

The reviewed Native release candidate has SHA-256 `A6A062343710AF2362DAF7B923A9BB98BDD5D381D896C68AE3190CA55655FBC7`. Native fake/WARP CTest passed 12/12, and managed Editor/Player code compiled against seven Unity assemblies with Roslyn; Unity licensing-dependent tests were not run. The URP sample still uses its older 6a297a8 package until Package Manager updates it and Unity restarts. The only physical observation is historical: one URP Pause/Resume cycle with a visible object move and Undo succeeded on the old package and Native DLL SHA-256 `4E5E1FF81C7C1C227D45E5D38E23FB84F1AAB8A8444EF327D639E1AEB619532B`. Black output after `Close Preview` followed by Start and after switching between Play and device preview in the reverse direction remains pending physical validation. HDRP hardware handoffs, repeated device operation, optical scale, image quality, and performance remain unverified. The Native tests cover fake/WARP GPU copy/scale behavior and immediate-context preservation; they do not load the official Bridge SDK or verify device presentation. See [Graphics backends](https://github.com/zabaglione/ViewWeave/blob/main/docs/graphics-backends.md) for the support boundary. Vulkan and macOS output are outside the support boundary. Install and run the Looking Glass Bridge runtime separately and connect a display.

The supported entry point is `ViewWeaveBridgeSession` with `ViewWeaveRig` and `BridgeWorkerQuiltOutput` on the same rig. Use `GameObject > ViewWeave > Bridge Rig` to create a ready-to-run rig. Standard scene objects such as Cube and Box render with their existing pipeline materials and lights; they do not need ViewWeave components or sample generators. The menu copies the MainCamera framing and uses the selected Renderer center to set zero-parallax distance when available.

The Bridge Worker follows the Unity Editor's active D3D11 or D3D12 graphics API. Check `SystemInfo.graphicsDeviceType` to confirm it. For D3D12, the worker shares the Unity-rendered Quilt with a D3D11 device on the same GPU adapter and sends it to the official Bridge runtime without CPU image readback. No separate SDK demo project is needed.

The optional Windows URP Lit and Windows HDRP Lit examples are declared as Package Manager Samples. They are copied under `Samples~` and enter the project Assets only after the user explicitly imports them.

`ViewWeaveBridgeSession` exposes `StartSession()`, `StopSession()`, `SetQuality(QuiltQualityPreset)`, `State`, `Quality`, `Profile`, and ASCII `LastError`. The default quality is `Balanced`. The Bridge runtime is located from an explicit absolute `runtimePath`, the `LKG_BRIDGE_PATH` environment variable, or Windows Bridge uninstall registry entries.

The legacy Direct route is separate from `ViewWeaveBridgeSession` and remains D3D11-only. It requires the validated single-threaded Direct3D11 configuration, including `-force-gfx-direct` and `runInBackground=true`.

See the repository [Getting started](../../docs/getting-started.md), [Compatibility](../../docs/compatibility.md), and [third-party notices](../../docs/THIRD_PARTY_NOTICES.md).
