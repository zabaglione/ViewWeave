[日本語](README.ja.md)

# Windows URP Lit sample

This optional sample demonstrates basic URP Lit rendering in Unity Editor Play or a Player on Windows D3D11 or D3D12. Add the ViewWeave URP distribution package to a Unity 6000.6.4f1 URP project from its Git URL, then import `Windows URP Lit` from Samples under `Window > Package Management > Package Manager`. The distribution package contains Core, URP, and Bridge.

## Usage

1. Create `GameObject > ViewWeave > Bridge Rig`.
2. Add `BridgeUrpLitSampleGenerator` to the same GameObject.
3. Enable `Generate On Start` and Build and Run a Windows Standalone Player using Direct3D11 or Direct3D12.
4. After the Player starts, verify that a red Cube, green sphere, and directional light appear in the Quilt.

`Generate Lit Scene` in the Inspector creates the test Cube, sphere, and light. To output to Bridge, provide the Bridge runtime and device, then Play in the Windows D3D11/D3D12 Editor or start a Windows D3D11/D3D12 Player. Set the absolute DLL path in `ViewWeaveBridgeSession`'s `Runtime Path` only if normal runtime discovery cannot find the Bridge DLL.

## Material resource

The sample uses `Resources/ViewWeave/UrpLit.mat`. If the resource is missing, run `ViewWeave/Samples/Windows URP Lit/Create Material Resource`, then run `Generate Lit Scene` again. There is no silent fallback to another shader.
