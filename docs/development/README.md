[日本語](ja/README.md)

# Development records

For implementation, contribution, and evidence review. Start with the [Technical Guide](../technical-guide.md) for common tasks, or the [User Guide](../user-guide.md) for installation and operation. Current support conditions are in [Compatibility](../compatibility.md) and the [feature/platform matrix](../ja/unity-feature-platform-matrix.html).

Dated results apply only to their recorded revision, OS/API, pipeline, Editor/Player, fixture, and device. Failed, skipped, zero-test, and incomplete-gate runs remain non-passing. Earlier overviews are retained as history; their open gates and test counts do not override the current matrix.

## Architecture and integration

- [Architecture](architecture.md)
- [Bridge platform boundaries](bridge-platform-architecture.md)
- [Bridge output lifecycle](bridge-output-lifecycle.md)
- [Graphics backends](graphics-backends.md)
- [Bridgeのサブピクセル校正値](bridge-subpixel-calibration.md)
- [macOS Metal implementation and validation](macos-metal-internals.md)
- [Bridge Editor package update safety](bridge-package-update-safety.md)
- [Native plugin update troubleshooting](native-plugin-update-troubleshooting.md)

## Performance and rendering research

- [Performance Measurements and Reproduction Steps](performance.md)
- [GPU Resident Drawer Validation Status](gpu-resident-drawer.md)
- [DX12 HDRP performance investigation](dx12-performance-investigation.md)
- [URP Sample performance and integration evaluation](urp-sample-performance.md)
- [Resumed HDRP VFX performance capture](resumed-hdrp-vfx-performance.md)
- [URP camera-owned VolumeStack reuse experiment](urp-volume-stack-reuse.md)
- [Multiview Rendering Approaches](multiview-rendering.md)
- [Investigation of the Legacy Looking Glass Render Pipeline](legacy-render-pipeline-investigation.md)
- [Unity Multiview Rendering Optimization Examples](unity-multiview-research.md)
- [Looking Glass Official Unity SDK Feature Research](official-sdk-feature-research.md)
- [Comparison with Official Features](sdk-feature-comparison.md)
- [Investigation of XR Display Providers and Their Application to ViewWeave](xr-provider-investigation.md)
- [D3D12 Bridge worker implementation and validation](d3d12-backend-plan.md)

## Validation summaries and release preparation

- [Windows validation status](windows-validation.md)
- [Native shutdown trace](native-shutdown-trace.md)
- [Unity Feature Coverage](unity-feature-coverage.md)
- [Remaining Work and Acceptance Plan](remaining-work-plan.md)
- [Publication preparation and remaining checks](publication-readiness.md)
- [Earlier compatibility and validation overview](compatibility-history.md)
- [VFX Graph validation and investigation history](vfx-graph-history.md)

## Dated validation records

- [起動時のBridge機器情報取得と上段の再取得UI（2026-10-11）](bridge-authoring-discovery-validation-20261011.md)
- [Bridge機器情報の共通保持とGizmo形状（2026-10-11）](bridge-display-geometry-validation-20261011.md)
- [HDRP SampleSceneのEditor機器Previewとprofile一致修正（2026-10-11）](bridge-editor-preview-profile-validation-20261011.md)
- [CIのWindows FXC探索修正（2026-10-11）](ci-fxc-validation-20261011.md)
- [HDRP view histories and shared exposure validation — 2026-10-10](hdrp-view-history-validation-20261010.md)
- [現行dylibのmacOS機器出力Player検証（2026-10-14）](macos-current-player-validation-20261014.md)
- [最新mainのmacOS影響確認（2026-10-14）](macos-latest-intake-validation-20261014.md)
- [現行macOS校正出力の再検証](macos-metal-current-validation-20261011.md)
- [macOS Metal calibration generalization — 2026-10-11](macos-metal-generalization-validation-20261011.md)
- [macOS Bridgeセル配列対応の検証（2026-10-11）](macos-metal-subpixel-validation-20261011.md)
- [macOS Metal verification — 2026-10-10](macos-metal-validation-20261010.md)
- [macOS 最新版・Preview遷移・終了診断の再評価（2026-10-14）](macos-preview-shutdown-validation-20261014.md)
- [macOS終了thread・残留割当stackの切り分け（2026-10-14）](macos-shutdown-stack-validation-20261014.md)
- [macOS共通サブピクセル契約の再検証（2026-10-11）](macos-unified-subpixel-validation-20261011.md)
- [Quick Start Sample validation — 2026-10-10](quick-start-validation-20261010.md)
- [Reference Camera Inspector validation (2026-10-11)](rig-reference-camera-validation-20261011.md)
- [URP/HDRPサンプルプロジェクトの更新 — 2026-10-11](sample-project-refresh-20261011.md)
- [Windows shutdown validation (2026-10-08)](shutdown-validation-20261008.md)
- [URP Quilt view histories and TAA validation — 2026-10-11](urp-view-history-validation-20261011.md)
- [Windows current graphics feature validation (2026-10-11)](windows-current-feature-validation-20261011.md)
- [Windows generic display metadata validation (2026-10-11)](windows-display-generic-validation-20261011.md)
- [Windows manual Editor close crash dump and control diagnostics (2026-10-11 to 2026-10-12)](windows-editor-close-crash-20261011.md)
- [Windows environment and output path validation (2026-10-11)](windows-environment-output-validation-20261011.md)
- [Windows latest Native integration validation (2026-10-11)](windows-latest-integration-20261011.md)
- [Windows connected-display quality-profile validation](windows-mac-quality-profile-validation-20261011.md)
- [Windows Native mock shutdown and stdio regression tests (2026-10-12)](windows-native-mock-shutdown-validation-20261012.md)
- [Windows Quilt上下方向の検証（2026-10-11）](windows-quilt-orientation-20261011.md)
- [Earlier Windows URP / HDRP Git-package refresh](windows-sample-refresh-20261011.md)
- [Windows Unity stdout and shutdown crash investigation (2026-10-11)](windows-stdout-crash-20261011.md)
- [Windows subpixel output validation](windows-subpixel-validation-20261011.md)
- [Windows URP VFX history-budget diagnostic follow-up (2026-10-12)](windows-urp-vfx-budget-diagnostic-20261012.md)
- [Windows URP quilt history-budget spike while moving VFX view (2026-10-13)](windows-urp-vfx-frustum-memory-20261013.md)
- [Windows URP VFX HDR intermediate fix validation (2026-10-13)](windows-urp-vfx-hdr-fix-20261013.md)
- [URP VFX color and HDR output path investigation (2026-10-13)](windows-urp-vfx-hdr-rootcause-20261013.md)
- [Windows URP VFX rendering and Editor crash observation (2026-10-13)](windows-urp-vfx-rendering-incident-20261013.md)
- [Windows URP VFX instancing policy probe (2026-10-14)](windows-urp-vfx-sdk-memory-policy-20261014.md)
- [Windows URP VFX follow-up validation (2026-10-13)](windows-urp-vfx-validation-20261013.md)
- [Windows HDRP VFX manual display observation (2026-10-12)](windows-vfx-manual-observation-20261012.md)
- [Windows URP/HDRP VFX Learning Templates comparison (2026-10-13)](windows-vfx-scene-comparison-20261013.md)

## Evidence and maintenance

- [README image and editing provenance](readme-image.md).

- [Validation evidence](../validation/): results, XML, images, run metadata, and manifests. Text copies have [public path redactions](../validation/PUBLIC_EVIDENCE.md); original run hashes and public-copy hashes have separate meanings.
- [Benchmark records](../benchmarks/).
- [Matrix history](ja/unity-feature-platform-matrix-history.html) and [maintenance rules](../ja/support-matrix-maintenance.md).
- [Pipeline test procedures](../../Tests/Pipelines/README.md), [Player validation](../../Tests/Pipelines/PLAYER_VALIDATION.md), and [benchmark procedure](../../Tests/Pipelines/BENCHMARK.md).

Keep new implementation notes, investigations, and dated reports in this directory. Put setup, operating instructions, and actionable limits in the user guides. Preserve run outcomes and original as-run identities. For publication copies with path redactions, follow the linked policy and record current-copy hashes; update inbound links when moving a report.
