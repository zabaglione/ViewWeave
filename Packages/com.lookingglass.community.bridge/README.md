# ViewWeave Bridge

ViewWeave is an independently developed, unofficial Unity SDK for Looking Glass displays. It is not an official Looking Glass Factory product.

This optional package submits completed quilts to an installed Looking Glass Bridge runtime. The package does not include Bridge binaries, proprietary headers, or runtime assets.

## Worker session route

The supported package entry point is `ViewWeaveBridgeSession` with a `ViewWeaveRig` and a `BridgeWorkerQuiltOutput` on the same rig. The output is required and must be assigned to the core renderer. The worker route is experimental and is available only in a Windows Player using Direct3D11. It is unavailable in the Unity Editor and on macOS.

Create a rig from `GameObject/ViewWeave/Bridge Rig`, or construct the components on an inactive GameObject before activating it. The menu-created rig opts into startup. Manually authored rigs default to `startOnEnable=false`.

The session exposes `StartSession()`, `StopSession()`, `SetQuality(QuiltQualityPreset)`, `State`, `Quality`, `Profile`, and ASCII `LastError`. The default quality is `Preview`. During a quality change the session enters `Reconfiguring`, quiesces GPU transfer, applies the new profile through `TryResetTransfer` on the same host and window, recreates transfer resources, and resumes automatic rendering. A texture resize follows the same quiescent reset path. `StopSession()` followed by `StartSession()` remains available as an explicit independent restart operation.

The current Windows D3D11 validation covers 14 session cases. Bridge-connected URP and HDRP Player checks cover Preview, Balanced, and Native with GRD enabled and disabled at 48 views. These are fixed conditions and do not establish support for every display or hotplug scenario.

The runtime path must resolve to an absolute `bridge_inproc.dll` path. Resolution priority is:

1. An explicit absolute `runtimePath`.
2. The `LKG_BRIDGE_PATH` environment variable.
3. The Windows Bridge uninstall registry entries.

The registry route is convenience discovery. Set an explicit path when more than one installation is present. The Unity Editor never connects to Bridge.

Native API initialization, GPU transfer, profile exchange, and bounded worker cleanup have been validated on the Windows D3D11 worker path. Optical display compatibility remains dependent on the installed Bridge runtime and display. Verify the target hardware before shipping. The route is not a production support claim.

On Windows, the native shim keeps a successfully loaded Bridge DLL resident for the process lifetime. `StopSession` still releases the worker session, textures, device references, and calls the Bridge uninitialize entry point, but it does not detach and reload the vendor DLL. A later session may reuse the same canonical absolute DLL path; requesting a different path fails with an ASCII error. This avoids vendor DLL reinitialization failures during same-process session restart.

After initialization, `BridgeQuiltOutput.Profile` exposes the connected display dimensions, quilt dimensions, grid, view aspect, and viewcone. Apply that profile to `QuiltSettings` before rendering.

## Legacy Direct route

The existing direct route remains available for legacy deployments. It is a separate compatibility path and requires the validated single-threaded Direct3D11 configuration, including `-force-gfx-direct` and `runInBackground=true`. It does not use `ViewWeaveBridgeSession` or the WorkerSharedD3D11 path. D3D12, Vulkan, and macOS native output are not implemented by this package.

## Optional native shim build

The repository includes the native shim source under `Native`. Build it on Windows with CMake after installing the Visual Studio C++ workload and opening a Visual Studio developer shell:

```text
set "UNITY_PLUGIN_API_DIR=<UnityEditorPath>\Editor\Data\PluginAPI"
cmake -S Native -B Native\build-windows -G Ninja -DUNITY_PLUGIN_API_DIR="%UNITY_PLUGIN_API_DIR%" -DCMAKE_BUILD_TYPE=Release
cmake --build Native\build-windows
```

`UNITY_PLUGIN_API_DIR` must contain `IUnityInterface.h` and `IUnityGraphicsD3D11.h`. The Unity Editor path is user-specific and is not hardcoded in project files. The shim is a local build artifact and is separate from the user's installed Bridge runtime.

The Bridge runtime and its bundled third-party libraries have their own terms. See the repository [third-party notices](../../docs/THIRD_PARTY_NOTICES.md) and the license files in the user's Bridge installation before redistribution.
