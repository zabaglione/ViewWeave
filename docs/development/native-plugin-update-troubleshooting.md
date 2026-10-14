# Native plugin update troubleshooting

> Audience: Unity, rendering, and package developers. This page covers the technical design, implementation, or procedure stated below. For user workflows, see the [User guide](../user-guide.md); for the complete technical index, see the [Technical guide](../technical-guide.md).

Unity cannot unload native plug-ins during a managed domain reload. After updating a package that contains native binaries, or replacing a native plug-in while the Unity Editor is running, restart the Editor before retrying.

Before a manual restart, close Play mode and let pending rendering and worker operations drain. Save project changes and recover any unsaved user work before restarting.

Observed during this investigation: adding package version 5892 through UPM left two cache paths resident, although both contained the same Git blob for the native DLL. The render event ID error was consistent with an unreserved event ID; the bridge entered the fault state and the render attempt count remained zero. After restarting the Editor, the bridge initialized successfully in a new process using the single current cache DLL with existing package version 5892. This confirms recovery after restart in this investigation, but does not establish the original cause, prove that an older DLL was responsible, or show that DX12 is unsupported. The newly added diagnostic message was not separately exercised.

A later Editor failure followed `StopSession` returning `Closing`; the next session-state read lost its command connection. Windows recorded an access violation (`0xC0000005`: read from address 0; faulting instruction at module RVA `0xF8550`) in the official Looking Glass Bridge 2.6.3 `bridge_inproc.dll` (SHA-256 `3BE0EBA96844688C2EB2E70FC512A5ADA3D53E3ED9C8810454AB0370B8241386`). This identifies the faulting module in that incident but does not establish a cause or implicate the SDK shim. The new diagnostic message was not deployed in that run.

The diagnostic source compiled in the SDK's Bridge fixture using Unity 6000.6.4f1 with batch mode and no graphics (exit code 0, no C# compiler errors). This compile-only check did not activate the native runtime or exercise the new message. Evidence: compile log (`../.local/bridge-eventid-compile/unity-compile.log`) and incident summary (`../.local/dx12-followup/resumed-20261007/grd-aba-crash-summary.txt`).

For Unity's native plug-in loading and reload behavior, see the [official native plug-in overview](https://docs.unity.com/en-us/engine/6000.0/manual/scripting/compilation-and-code-reload/plug-ins/native/overview).
