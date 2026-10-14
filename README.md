[日本語](README.ja.md)

# ViewWeave

![ViewWeave running on a laptop and Looking Glass display, with a blurred background](docs/images/viewweave-hero.jpg)

ViewWeave is an unofficial Unity SDK for rendering a regular URP or HDRP scene as a multi-view Quilt for a Looking Glass display. The project provides an integrated Rig for camera framing, preview, capture, and output. Install the official Looking Glass Bridge runtime and connect a supported display for device output.

v0.1.0 is an experimental release. Use Unity 6000.6.4f1 with a URP or HDRP project. Choose one self-contained package and install it from **Package Manager > + > Install package from git URL**:

- URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#v0.1.0`
- HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#v0.1.0`

## Quick Start

1. Install the URP or HDRP package above, then import **URP Quick Start** or **HDRP Quick Start** from its Package Manager **Samples** tab.
2. Open that sample's `QuickStart.unity` scene.
3. For a static preview, select **ViewWeave Bridge Rig**, then start **Preview > Start Looking Glass Device Preview**. Without a display, choose **Start Local Edit Mode Preview** instead.
4. For animation on the connected display, press **Play**. Output starts automatically, just like a Rig created from the menu.

See the [illustrated setup](docs/getting-started.md), [User Guide](docs/user-guide.md), or [documentation index](docs/index.md). Use [Compatibility](docs/compatibility.md) for OS, pipeline, Editor/Player, device validation, and known limits.

For implementation design, test procedures, dated verification records, and research, start at the [Technical Guide](docs/technical-guide.md). ViewWeave's original code is licensed under [Apache-2.0](LICENSE); Unity PluginAPI and other external dependencies have separate terms described in [Third-Party Notices](docs/THIRD_PARTY_NOTICES.md). The official Looking Glass Bridge runtime must be installed separately under its own terms.
