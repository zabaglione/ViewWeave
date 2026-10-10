# ViewWeave 技術者向けガイド

[English](../technical-guide.md)

> Unity、描画基盤、パッケージ開発者向けの技術入口です。設計、実装上の境界、試験、日付・条件を限定した検証記録、調査資料をまとめています。個別の結果は各文書に記載したrevisionと条件に限られます。利用手順は[ユーザーガイド](user-guide.md)、[使い始める](getting-started.md)、現行の対応条件は[対応表](compatibility.md)を参照してください。

## 構成と実行時の境界

- [構成](architecture.md)では、現在のデータ経路とパッケージの役割を説明します。
- [Bridge出力のライフサイクル](bridge-output-lifecycle.md)では、出力の所有権、Previewの切替、lease、GPU resourceの解放を扱います。
- [Bridgeのプラットフォーム分離](bridge-platform-architecture.md)では、共通処理、Windows、Metalの責務を示します。
- [描画backend](graphics-backends.md)では、backendの選択とlifecycle gateを説明します。
- [対応表](compatibility.md)を、現在の対応構成と既知の制限の参照先とします。
- [macOS Metalガイド](macos-metal.md)では、Looking Glass Go専用経路と導入条件を説明します。
- [Bridge package](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/README.md)、[URP package](../../Packages/com.zabaglione.lookingglass.viewweave.urp/README.md)、[HDRP package](../../Packages/com.zabaglione.lookingglass.viewweave.hdrp/README.md)では、ソースパッケージと自己完結型の配布パッケージの関係を説明します。
- [Bridge Editor package更新の安全性](../bridge-package-update-safety.md)では、managed/nativeの更新境界を記録しています。

## 検証と性能の記録

- [公開準備と残る確認（英語）](../publication-readiness.md)には、ライセンス通知の同梱、未確認の来歴・導入、過去のCI artifactについて記載しています。

以下は日付または条件を限定した記録であり、一般的な対応保証ではありません。現在の対応範囲は[対応表](compatibility.md)で確認してください。

- Windows: [Windows検証](../windows-validation.md)、[終了処理の検証](../shutdown-validation-20261008.md)、[Native終了trace](../native-shutdown-trace.md)、[残作業の検証記録](../benchmarks/remaining-work-validation-20261007.md)。
- macOS: [Metal検証記録](../macos-metal-validation-20261010.md)。
- URP: [視点別履歴とTAAの検証記録（英語）](../urp-view-history-validation-20261011.md)。同期view描画経路とMetal PlayMode／Edit Mode／Playerの証跡、残るWindows／Bridge機器／実機光学の確認を記録します。
- HDRP: [視点別履歴と共有露出の検証記録（英語）](../hdrp-view-history-validation-20261010.md)。光源の修正、リソース観測、再読み込み時の解放、Metalの試験結果、未検証の環境・効果を記載しています。
- Sampleと文書: [Quick Start検証記録（英語）](../quick-start-validation-20261010.md)。UPM Import、OS共通シーン、Goでの確認、Editor試験の範囲を記録しています。
- 描画と性能: [性能測定](performance.md)、[DX12 HDRP調査](../dx12-performance-investigation.md)、[URPサンプルの性能](../urp-sample-performance.md)、[HDRP VFXの再開後の記録](../resumed-hdrp-vfx-performance.md)、[GPU Resident Drawerの検証状況](gpu-resident-drawer.md)、[URP VolumeStack再利用の実験](../urp-volume-stack-reuse.md)。
- [残作業と受け入れ計画](remaining-work-plan.md)には、文書内の日付時点で残っていたgateを記載しています。

## 描画調査と機能範囲

- [D3D12 Bridge worker計画](../d3d12-backend-plan.md)は実装・検証の対象範囲を記録します。
- [多視点描画方式](multiview-rendering.md)、[旧Looking Glass pipelineの調査](legacy-render-pipeline-investigation.md)、[Unity多視点描画の調査](unity-multiview-research.md)では描画方式を比較します。
- [公式SDK機能の調査](official-sdk-feature-research.md)と[SDK機能比較](sdk-feature-comparison.md)では、機能と互換性の観察結果をまとめます。
- [Unity feature coverage](unity-feature-coverage.md)には確認した機能領域と証拠範囲を記載します。
- [XR Display Provider調査](xr-provider-investigation.md)には統合の分析と現在の方針を記録します。
- [VFX Graphガイド](vfx-graph.md)では、利用者向け機能のpipeline条件と設定を説明します。

## 試験手順と開発

- Pipelineの試験手順: [スモークテスト](../../Tests/Pipelines/README.ja.md)、[Player検証](../../Tests/Pipelines/PLAYER_VALIDATION.md)、[benchmark手順](../../Tests/Pipelines/BENCHMARK.md)。
- Native相互運用テスト: [D3D12-to-D3D11 WARP capability test](../../Tests/Native/D3D12WarpInterop/README.md)。
- [Native plugin更新のトラブルシューティング](../native-plugin-update-troubleshooting.md)では、package更新後の再読み込みとnative library読み込み時の症状を扱います。
- [Contributing（貢献ガイド）](../../CONTRIBUTING.md)、[開発計画](../../ROADMAP.ja.md)、[変更履歴](../../CHANGELOG.md)では、開発作業と変更を追跡します。英語版[開発計画](../../ROADMAP.md)もあります。

## 利用者向けの操作

Scene設定と操作は[ユーザーガイド](user-guide.md)を参照してください。詳しいページは[Quiltのカメラ設定](quilt-authoring.md)、[保存・録画](quilt-capture.md)、[再生](../quilt-playback.md)、[音声録音](quilt-recording-audio.md)、[動画出力](../quilt-video-export.md)です。
