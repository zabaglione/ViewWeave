[English](README.md)

# ViewWeave

ViewWeaveは、通常のURPまたはHDRP sceneを多視点Quiltとして描画し、Looking Glassへ出力する非公式Unity SDKです。カメラ構図、Preview、Capture、出力をまとめて操作するRigを提供します。機器へ出力する場合は、公式Looking Glass Bridge runtimeと対応するdisplayを別途用意してください。

Unity 6000.6.4f1のURPまたはHDRP projectを使います。このrepositoryはprivateのため、Package Managerから取得するにはGitHubのアクセス権が必要です。利用するpipelineのpackageを一つ選んでください。

- URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
- HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`

## Quick Start

1. 上記からURPまたはHDRP packageを追加し、Package Managerの**Samples**から**URP Quick Start**または**HDRP Quick Start**をImportします。
2. Sample内の`QuickStart.unity`を開きます。
3. **ViewWeave Bridge Rig**を選び、**Preview > Start Looking Glass Device Preview**を開始します。displayがない場合は**Start Local Edit Mode Preview**を選びます。

画像付き導入と操作手順は[ユーザーガイド](docs/ja/user-guide.md)と[使い始める](docs/ja/getting-started.md)を参照してください。現在の対応条件、既知の制限、検証範囲は[対応表](docs/ja/compatibility.md)に記載しています。このQuick Start sceneはWindows上での動作は未検証です。runtime構成を選ぶ前に対応表を確認してください。

実装設計、試験手順、日付を限定した検証記録、調査資料は[技術者向けガイド](docs/ja/technical-guide.md)から辿れます。[ドキュメント目次](docs/ja/index.md)には、独立した[Apache-2.0ライセンス](LICENSE)と[第三者ライセンス表記](docs/ja/THIRD_PARTY_NOTICES.md)も掲載しています。
