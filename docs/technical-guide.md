# ViewWeave Technical Guide

[日本語](ja/technical-guide.md)

> For Unity, rendering, and package developers. This index collects design notes, implementation boundaries, tests, dated validation records, and research. Results apply only to the revisions and conditions stated in each document. For current setup and user workflows, see the [User Guide](user-guide.md), [Getting Started](getting-started.md), and [Compatibility](compatibility.md).

## Architecture and runtime boundaries

- [Architecture](architecture.md) describes the current data path and package roles.
- [Bridge output lifecycle](bridge-output-lifecycle.md) covers output ownership, preview transitions, leases, and retirement.
- [Bridge platform boundaries](bridge-platform-architecture.md) describes shared, Windows, and Metal responsibilities.
- [Graphics backends](graphics-backends.md) documents backend selection and lifecycle gates.
- [Compatibility matrix](compatibility.md) is the current reference for supported configurations and known limits.
- [macOS Metal guide](macos-metal.md) describes the dedicated Looking Glass Go path and its setup boundaries.
- [Bridge package](../Packages/com.zabaglione.lookingglass.viewweave.bridge/README.md), [URP package](../Packages/com.zabaglione.lookingglass.viewweave.urp/README.md), and [HDRP package](../Packages/com.zabaglione.lookingglass.viewweave.hdrp/README.md) explain the source packages and their relationship to the self-contained distributions.
- [Bridge Editor package update safety](bridge-package-update-safety.md) records the managed/native update boundary.
- [Publication preparation](publication-readiness.md) lists the license-notice packaging and remaining provenance, installation, and artifact checks before a public release.

## Validation and performance records

These are dated or condition-specific records, not general support guarantees. Use [Compatibility](compatibility.md) for the current support boundary.

- Windows: [Windows validation](windows-validation.md), [shutdown validation](shutdown-validation-20261008.md), [native shutdown trace](native-shutdown-trace.md), and [remaining-work validation record](benchmarks/remaining-work-validation-20261007.md).
- macOS: [Metal validation record](macos-metal-validation-20261010.md).
- Samples and documentation: [Quick Start validation](quick-start-validation-20261010.md) records UPM import, shared scenes, Go checks, and the Editor test scope.
- Rendering and performance: [performance measurements](performance.md), [DX12 HDRP investigation](dx12-performance-investigation.md), [URP sample performance](urp-sample-performance.md), [resumed HDRP VFX capture](resumed-hdrp-vfx-performance.md), [GPU Resident Drawer status](gpu-resident-drawer.md), and the [URP VolumeStack reuse experiment](urp-volume-stack-reuse.md).
- [Remaining work and acceptance plan](remaining-work-plan.md) tracks open gates as of the date stated in that record.

## Rendering research and feature coverage

- [D3D12 Bridge worker plan](d3d12-backend-plan.md) records its implementation and validation scope.
- [Multiview rendering approaches](multiview-rendering.md), [legacy Looking Glass pipeline investigation](legacy-render-pipeline-investigation.md), and [Unity multiview research](unity-multiview-research.md) compare rendering approaches.
- [Official SDK feature research](official-sdk-feature-research.md) and [SDK feature comparison](sdk-feature-comparison.md) document feature and compatibility observations.
- [Unity feature coverage](unity-feature-coverage.md) lists checked feature areas and evidence boundaries.
- [XR Display Provider investigation](xr-provider-investigation.md) records the integration analysis and current direction.
- [VFX Graph guide](vfx-graph.md) describes pipeline conditions and setup for that user-facing feature.

## Test procedures and development

- Pipeline procedures: [smoke tests](../Tests/Pipelines/README.md), [Player validation](../Tests/Pipelines/PLAYER_VALIDATION.md), and the [benchmark procedure](../Tests/Pipelines/BENCHMARK.md).
- Native interop probe: [D3D12-to-D3D11 WARP capability test](../Tests/Native/D3D12WarpInterop/README.md).
- [Native plugin update troubleshooting](native-plugin-update-troubleshooting.md) covers package refresh and native library loading symptoms.
- [Contributing](../CONTRIBUTING.md), [development roadmap](../ROADMAP.md), and [changelog](../CHANGELOG.md) track project work and changes. The [Japanese roadmap](../ROADMAP.ja.md) is also available.

## Related user workflows

For scene setup and operation, use the [User Guide](user-guide.md). Detailed pages cover [Quilt authoring](quilt-authoring.md), [capture](quilt-capture.md), [playback](quilt-playback.md), [audio recording](quilt-recording-audio.md), and [video export](quilt-video-export.md).
