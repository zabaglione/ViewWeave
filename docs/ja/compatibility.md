[English](../compatibility.md)

# 対応環境と制限

ViewWeaveは、Unity **6000.6.4f1**、**URP/HDRP 17.6**向けの実験的な開発版です。[利用するパイプラインのパッケージ](../../README.ja.md)を一つ導入してください。機器への出力には公式Looking Glass Bridge runtimeと接続displayが必要です。

| 環境 | 出力経路 | 導入手順 |
| --- | --- | --- |
| Windows · URP/HDRP · D3D11/D3D12 | Editor Play／Windows PlayerのBridge Worker。明示開始するEdit Mode Previewは別機能です。Legacy DirectはD3D11のみです。 | [使い始める](getting-started.md) |
| Apple Silicon macOS 26.0以降 · URP/HDRP · Metal | 校正値を使う独立したMetal出力。回転なし・native解像度・拡張画面が必要です。 | [macOS Metal](macos-metal.md) |
| displayなし | Local Edit Mode Previewで構図とQuiltを確認できます。 | [ユーザーガイド](user-guide.md#editorまたはdisplayでpreviewする) |

Built-inは正式な対応対象外です。校正値による機器選択は、すべての機種・光学条件への対応を保証するものではありません。

## 現在の開発版の確認範囲

Windows／macOS、URP／HDRP、API、Editor／Player、機器別の確認条件は[現在の機能別対応表](unity-feature-platform-matrix.html)にまとめています。表の根拠リンクから日付付き証跡を確認できます。実装、設定保持、限定fixtureの成功、実機の光学確認はそれぞれ別の判定です。

利用する構成を選ぶ際は、次の制限も確認してください。

- **PreviewとPlayer:** Editorの試験成功からPlayerや光学対応を推定できません。Quick StartのImport・機器出力・Playerも、対応表で確認範囲を分けています。
- **VFX Graph:** Linear color spaceが必要です。URPには実験的なdriverがあり、HDRPには同等のdriverがありません。graph互換性・メモリ使用量・終了処理には条件が残ります。[VFX Graphガイド](vfx-graph.md)を参照してください。
- **メモリと終了処理:** 視点別の履歴はGPU資源を追加で使います。既定4096 MBの監視は割り当て後の検知で、VRAMの上限ではありません。Windows Editorの終了crashは未解決です。macOS Playerの終了警告は別の証跡で扱っています。[描画品質](user-guide.md#描画負荷と解像度を選ぶ)と対応表を参照してください。
- **macOS:** 音声録音は非対応です。同梱shimはad hoc署名で、Developer ID署名・公証・利用者環境のGatekeeper受入は未検証です。
- **配布:** 公開後の認証なしGit導入は未検証です。公開前の残る確認は[公開準備資料（英語）](../development/publication-readiness.md)を参照してください。

独自コードは[Apache-2.0](../../LICENSE)です。Unity PluginAPIと別途導入するBridge runtimeには別の条件があります。Windowsヘッダーの来歴に残る確認事項を含め、[第三者ライセンス表記](THIRD_PARTY_NOTICES.md)に記載しています。

試験件数・hash・過去の失敗・revisionごとの結論は[開発記録](../development/ja/README.md)と[整理前の対応・検証概要](../development/ja/compatibility-history.md)に残しています。
