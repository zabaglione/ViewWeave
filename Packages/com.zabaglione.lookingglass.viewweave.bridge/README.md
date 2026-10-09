# ViewWeave Bridge

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This source package submits completed quilts to an installed Looking Glass Bridge runtime. It does not include Bridge binaries, proprietary headers, or runtime assets. End users should install the self-contained URP or HDRP Git distribution package. Do not install this source package alongside a distribution package because both contain the same runtime assemblies.

## Supported output

Regular Bridge Worker output is supported in Windows D3D11 and D3D12 Unity Editor Play and Windows Player. The `ViewWeaveRig` Editor preview is a separate Edit Mode authoring route. `Pause Preview` hides the device window after draining in-flight work while retaining the producer, textures, Worker, and ownership locks; `Resume Preview` reuses that session. `Close Preview` performs the full drain and releases preview ownership; the process-wide SDK owner and loaded native image remain until Editor shutdown. Window retarget/close and Play/reload/Editor shutdown also use full close. Earlier URP/HDRP and D3D11/D3D12 fixtures validated local rendering and full-close cleanup, not pause visibility. The Native pause protocol passes Release CTest 10/10. Local URP/HDRP managed pause/resume, FOV-change, and Close tests passed 2/2; official device pause visibility remains unverified, and manual optical review is pending. See [Graphics backends](https://github.com/zabaglione/ViewWeave/blob/main/docs/graphics-backends.md) for scope. Vulkan and macOS output are outside the support boundary. Install and run the Looking Glass Bridge runtime separately and connect a display.

The supported entry point is `ViewWeaveBridgeSession` with `ViewWeaveRig` and `BridgeWorkerQuiltOutput` on the same rig. Use `GameObject > ViewWeave > Bridge Rig` to create a ready-to-run rig. Standard scene objects such as Cube and Box render with their existing pipeline materials and lights; they do not need ViewWeave components or sample generators. The menu copies the MainCamera framing and uses the selected Renderer center to set zero-parallax distance when available.

The Bridge Worker follows the Unity Editor's active D3D11 or D3D12 graphics API. Check `SystemInfo.graphicsDeviceType` to confirm it. For D3D12, the worker shares the Unity-rendered Quilt with a D3D11 device on the same GPU adapter and sends it to the official Bridge runtime without CPU image readback. No separate SDK demo project is needed.

The optional Windows URP Lit and Windows HDRP Lit examples are declared as Package Manager Samples. They are copied under `Samples~` and enter the project Assets only after the user explicitly imports them.

`ViewWeaveBridgeSession` exposes `StartSession()`, `StopSession()`, `SetQuality(QuiltQualityPreset)`, `State`, `Quality`, `Profile`, and ASCII `LastError`. The default quality is `Preview`. The Bridge runtime is located from an explicit absolute `runtimePath`, the `LKG_BRIDGE_PATH` environment variable, or Windows Bridge uninstall registry entries.

The legacy Direct route is separate from `ViewWeaveBridgeSession` and remains D3D11-only. It requires the validated single-threaded Direct3D11 configuration, including `-force-gfx-direct` and `runInBackground=true`.

See the repository [Getting started](../../docs/getting-started.md), [Compatibility](../../docs/compatibility.md), and [third-party notices](../../docs/THIRD_PARTY_NOTICES.md).
