[日本語](README.ja.md)

# ViewWeave

ViewWeave is an unofficial Unity SDK for rendering a regular URP or HDRP scene as a multi-view Quilt for a Looking Glass display. The project provides an integrated Rig for camera framing, preview, capture, and output. Install the official Looking Glass Bridge runtime and connect a supported display for device output.

This is an experimental development version. Use Unity 6000.6.4f1 with a URP or HDRP project. Choose one self-contained package and install it from **Package Manager > + > Install package from git URL**:

- URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
- HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`

## Quick Start

1. Install the URP or HDRP package above, then import **URP Quick Start** or **HDRP Quick Start** from its Package Manager **Samples** tab.
2. Open that sample's `QuickStart.unity` scene.
3. Select **ViewWeave Bridge Rig**, then start **Preview > Start Looking Glass Device Preview**. Without a display, choose **Start Local Edit Mode Preview** instead.

For illustrated setup and operating instructions, see the [User Guide](docs/user-guide.md) and [Getting Started](docs/getting-started.md). Both Quick Start Samples were imported locally and checked in the macOS Metal Editor on Looking Glass Go. Windows UPM import, D3D11/D3D12 output, and Player builds of these Samples are unverified. Older Windows fixture results do not validate the currently packaged Windows DLL on a physical display. See [Compatibility](docs/compatibility.md) for the complete tested scope and remaining limits.

For implementation design, test procedures, dated verification records, and research, start at the [Technical Guide](docs/technical-guide.md). ViewWeave's original code is licensed under [Apache-2.0](LICENSE); Unity PluginAPI and other external dependencies have separate terms described in [Third-Party Notices](docs/THIRD_PARTY_NOTICES.md). The official Looking Glass Bridge runtime must be installed separately under its own terms.
