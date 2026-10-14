# Bridge Editor package update safety

> Audience: Unity, rendering, and package developers. This page covers the technical design, implementation, or procedure stated below. For user workflows, see the [User guide](../user-guide.md); for the complete technical index, see the [Technical guide](../technical-guide.md).

The Bridge worker checks the Windows Unity Editor process for loaded modules named `lookingglass_bridge_shim.dll` before the first native event-ID call and again immediately before native begin. If the process contains more than one distinct loaded image base, the worker refuses to begin and asks for a Unity Editor restart. Repeated module entries at the same base address count as one image. Inspection errors also fail closed.

This guard is limited to Windows Editor Play mode. It does not unload modules, stop an existing session, or change native cleanup order. It only prevents a new worker session from starting when the Editor has multiple shim images loaded or their module state cannot be inspected.

The guard addresses a hazardous package-update state where a crash dump showed the shim loaded from multiple UPM paths. The observed coexistence does not establish the cause of the reported vendor crash, and this preventive check is not a root-cause fix. Restart the Unity Editor after updating the Bridge package before starting a worker.
