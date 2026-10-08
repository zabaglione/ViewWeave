[日本語](README.ja.md)

# Windows HDRP Lit sample

This optional sample demonstrates basic HDRP Lit rendering in Unity Editor Play or a Player on Windows D3D11 or D3D12. Add the ViewWeave HDRP distribution package to a Unity 6000.6.4f1 HDRP project from its Git URL, then import `Windows HDRP Lit` from Samples under `Window > Package Management > Package Manager`. The distribution package contains Core, HDRP, and Bridge. HDRP requires Linear color space. During Quilt rendering, the adapter applies a shared Fixed Exposure value across all views.

## Usage

1. Create `GameObject > ViewWeave > Bridge Rig`.
2. Add `BridgeHdrpLitSampleGenerator` to the same GameObject.
3. Enable `Generate On Start` and Build and Run a Windows Standalone Player using Direct3D11 or Direct3D12.
4. After the Player starts, verify that the generated Lit scene appears in the Quilt.

`Generate Lit Scene` in the Inspector creates the test Cube, sphere, and light. To output to Bridge, provide the Bridge runtime and device, then Play in the Windows D3D11/D3D12 Editor or start a Windows D3D11/D3D12 Player. Set the absolute DLL path in `ViewWeaveBridgeSession`'s `Runtime Path` only if normal runtime discovery cannot find the Bridge DLL.

## Material and HDRP conditions

The sample uses `Resources/ViewWeave/HdrpLit.mat`. If the resource is missing, run `ViewWeave/Samples/Windows HDRP Lit/Create Material Resource`, then run `Generate Lit Scene` again. Generation uses a red Cube, green sphere, directional light, fixed Exposure, and disables Motion Blur, SSR, SSGI, volumetric fog, and clouds.
