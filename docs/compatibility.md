[日本語](ja/compatibility.md)

# Compatibility

ViewWeave v0.1.0 is an experimental release for Unity **6000.6.4f1** and **URP/HDRP 17.6**. Install one [pipeline package](../README.md). Device output requires the official Looking Glass Bridge runtime and a connected display.

| Environment | Output route | Setup |
| --- | --- | --- |
| Windows · URP/HDRP · D3D11/D3D12 | Bridge Worker for Editor Play and Windows Player; explicit Edit Mode Preview is separate. Legacy Direct is D3D11 only. | [Getting Started](getting-started.md) |
| Apple Silicon macOS 26.0+ · URP/HDRP · Metal | Dedicated calibration-driven Metal output; requires an unrotated, native-resolution extended display. | [macOS Metal](macos-metal.md) |
| Without a display | Local Edit Mode Preview for framing and Quilt inspection. | [User Guide](user-guide.md#preview-on-the-editor-or-display) |

Built-in is outside the formal support target. Calibration-based device selection does not guarantee every display model or optical configuration.

## Current development version validation

Use the [current feature/platform matrix](ja/unity-feature-platform-matrix.html) for the checked Windows/macOS, URP/HDRP, API, Editor/Player, and device conditions. Its source links lead to the dated evidence. Implementation, retained settings, fixture results, and physical optical acceptance are separate claims.

Keep these limits in mind before choosing a project configuration:

- **Preview and Player:** a successful Editor fixture does not establish Player or optical support. Quick Start import, device output, and Player checks have separate scopes in the matrix.
- **VFX Graph:** use Linear color space. URP has an experimental driver; HDRP has no equivalent driver. Graph compatibility, memory use, and shutdown remain conditional. See [VFX Graph](vfx-graph.md).
- **Memory and shutdown:** per-view histories add GPU resources. The 4096 MB history budget detects growth after allocation and is not a VRAM cap. Windows Editor shutdown crashes remain unresolved; macOS Player shutdown warnings have separate evidence. See [Rendering quality](user-guide.md#choose-rendering-cost-and-resolution) and the matrix.
- **macOS:** audio recording is unsupported; the bundled shim is ad hoc signed. Developer ID signing, notarization, and end-user Gatekeeper acceptance are unverified.
- **Distribution:** anonymous Git installation after public access is enabled remains unverified. See [publication preparation](development/publication-readiness.md) for outstanding release checks.

ViewWeave's original code is [Apache-2.0](../LICENSE). Unity PluginAPI and the separately installed Bridge runtime have their own terms; Windows header provenance has an open verification item in [Third-Party Notices](THIRD_PARTY_NOTICES.md).

For test counts, hashes, earlier failures, and revision-specific conclusions, use the [development records](development/README.md) or [earlier compatibility overview](development/compatibility-history.md).
