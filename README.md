[日本語](README.ja.md)

# ViewWeave

ViewWeave is an unofficial Unity SDK for rendering a regular URP or HDRP scene as a multi-view Quilt for a Looking Glass display. The project provides an integrated Rig for camera framing, preview, capture, and output. Install the official Looking Glass Bridge runtime and connect a supported display for device output.

Use Unity 6000.6.4f1 with a URP or HDRP project. The repository is private, so GitHub access is required for Package Manager installation. Choose one self-contained package:

- URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
- HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`

## Quick Start

1. Install the URP or HDRP package above, then import **URP Quick Start** or **HDRP Quick Start** from its Package Manager **Samples** tab.
2. Open that sample's `QuickStart.unity` scene.
3. Select **ViewWeave Bridge Rig**, then start **Preview > Start Looking Glass Device Preview**. Without a display, choose **Start Local Edit Mode Preview** instead.

For illustrated setup and operating instructions, see the [User Guide](docs/user-guide.md) and [Getting Started](docs/getting-started.md). Current platform conditions, known limitations, and verification scope are in [Compatibility](docs/compatibility.md). This Quick Start scene has not been validated on Windows; consult Compatibility before choosing a runtime configuration.

For implementation design, test procedures, dated verification records, and research, start at the [Technical Guide](docs/technical-guide.md). The [documentation index](docs/index.md) also links the independent [Apache-2.0 license](LICENSE) and [third-party notices](docs/THIRD_PARTY_NOTICES.md).
