# ViewWeave Bridge

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package submits completed quilts to an installed Looking Glass Bridge runtime. It does not include Bridge binaries, proprietary headers, or runtime assets. End users should install the self-contained URP or HDRP Git distribution package. Do not install this source package alongside a distribution package because both contain the same runtime assemblies.

## Supported output

Regular Bridge Worker output is supported in Windows D3D11 and D3D12 Unity Editor Play and Windows Player. For authoring, a separate explicitly started `ViewWeaveRig` Editor preview is available in Edit Mode on Windows D3D11/D3D12. Targeted URP/HDRP local GPU fixtures passed for layout rebuild and proof-based Stop/readback with zero pending retention. D3D11 and D3D12 device fixtures passed URP/HDRP Start/Stop/Restart at a 3840x2160 display profile with a 7680x4320, 8x6/48-view Quilt; each Start and Restart submitted/completed once, and Stop left native/retention pending at zero. Manual optical review remains pending. A targeted Editor fixture confirmed Domain Reload cleanup and explicit restart; see [Graphics backends](https://github.com/zabaglione/ViewWeave/blob/main/docs/graphics-backends.md) for File > Exit results; normal Editor exit remains unverified. Vulkan and macOS output are outside the support boundary. Install and run the Looking Glass Bridge runtime separately and connect a display.

The supported entry point is `ViewWeaveBridgeSession` with `ViewWeaveRig` and `BridgeWorkerQuiltOutput` on the same rig. Use `GameObject > ViewWeave > Bridge Rig` to create a ready-to-run rig. Standard scene objects such as Cube and Box render with their existing pipeline materials and lights; they do not need ViewWeave components or sample generators. The menu copies the MainCamera framing and uses the selected Renderer center to set zero-parallax distance when available.

The Bridge Worker follows the Unity Editor's active D3D11 or D3D12 graphics API. Check `SystemInfo.graphicsDeviceType` to confirm it. For D3D12, the worker shares the Unity-rendered Quilt with a D3D11 device on the same GPU adapter and sends it to the official Bridge runtime without CPU image readback. No separate SDK demo project is needed.

The optional Windows URP Lit and Windows HDRP Lit examples are declared as Package Manager Samples. They are copied under `Samples~` and enter the project Assets only after the user explicitly imports them.

`ViewWeaveBridgeSession` exposes `StartSession()`, `StopSession()`, `SetQuality(QuiltQualityPreset)`, `State`, `Quality`, `Profile`, and ASCII `LastError`. The default quality is `Preview`. The Bridge runtime is located from an explicit absolute `runtimePath`, the `LKG_BRIDGE_PATH` environment variable, or Windows Bridge uninstall registry entries.

The legacy Direct route is separate from `ViewWeaveBridgeSession` and remains D3D11-only. It requires the validated single-threaded Direct3D11 configuration, including `-force-gfx-direct` and `runInBackground=true`.

See the repository [Getting started](../../docs/getting-started.md), [Compatibility](../../docs/compatibility.md), and [third-party notices](../../docs/THIRD_PARTY_NOTICES.md).
