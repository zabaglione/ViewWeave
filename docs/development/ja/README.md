[English](../README.md)

# 開発資料・検証記録

実装・開発参加・根拠確認のための資料です。目的別の入口は[技術者向けガイド](../../ja/technical-guide.md)、導入・操作は[ユーザーガイド](../../ja/user-guide.md)です。現在の対応判定は[対応表](../../ja/compatibility.md)と[機能別対応表](../../ja/unity-feature-platform-matrix.html)を参照してください。

日付付き記録は、そのrevision・OS/API・pipeline・Editor/Player・fixture・機器に限る結果です。失敗・skip・0件・未通過gateは成功に含めません。過去の概要は履歴として保持しており、現在の判定を上書きしません。

## 日本語の技術資料

- [構成](architecture.md)
- [Bridge出力のライフサイクル](bridge-output-lifecycle.md)
- [Bridgeのプラットフォーム分離](bridge-platform-architecture.md)
- [対応・検証記録の旧概要](compatibility-history.md)
- [GPU Resident Drawerの検証状況](gpu-resident-drawer.md)
- [描画backend](graphics-backends.md)
- [旧Looking Glass専用Render Pipelineの調査](legacy-render-pipeline-investigation.md)
- [macOS Metalの実装・検証資料](macos-metal-internals.md)
- [多視点レンダリング方式の検討](multiview-rendering.md)
- [Looking Glass公式Unity SDKの機能調査](official-sdk-feature-research.md)
- [性能の実測と再現手順](performance.md)
- [残作業と受け入れ計画](remaining-work-plan.md)
- [公式機能との比較](sdk-feature-comparison.md)
- [Unity feature coverage](unity-feature-coverage.md)
- [Unity多視点描画の最適化事例](unity-multiview-research.md)
- [VFX Graphの検証・調査履歴](vfx-graph-history.md)
- [Windows検証状況](windows-validation.md)
- [XR Display Providerの調査とViewWeave適用案](xr-provider-investigation.md)

## 日付付き記録と証跡

- [英語の全資料目次](../README.md#dated-validation-records): Windows／macOSの個別検証、incident、修正結果。
- [対応表の作業・検証履歴](unity-feature-platform-matrix-history.html)。
- [検証証跡](../../validation/): result・XML・画像・実行metadata・manifest。[公開コピーのパスとhashの扱い](../../validation/PUBLIC_EVIDENCE.md)に従い、元の実行時hashと公開コピーのhashを区別しています。
- [benchmark記録](../../benchmarks/)と[対応表の保守ルール](../../ja/support-matrix-maintenance.md)。
- [pipeline試験手順](../../../Tests/Pipelines/README.ja.md)、[Player検証（英語）](../../../Tests/Pipelines/PLAYER_VALIDATION.md)、[benchmark手順（英語）](../../../Tests/Pipelines/BENCHMARK.md)。

新しい実装資料・調査・日付付きreportはこの開発階層へ置きます。導入・操作・対処できる制限は利用者ガイドへ記載し、公開用のパス整理では実行結果と元の実行時identityを保持し、公開コピーのhashも記録してください。reportの移動時は参照元のリンクも更新します。
