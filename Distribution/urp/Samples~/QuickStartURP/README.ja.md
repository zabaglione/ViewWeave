[English](README.md)

# URP Quick Start

[ユーザーガイド](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/ja/user-guide.md) · [技術者向けガイド](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/ja/technical-guide.md)

設定済みViewWeave Rig、色付きのprimitive、loopするAnimator clipを含むSampleです。生成用scriptやobjectへの追加componentは不要です。

1. Package Managerの**Samples**から**URP Quick Start**をImportします。
2. ImportしたSample内の`QuickStart.unity`を開きます。
3. **ViewWeave Bridge Rig**を選び、**Preview > Start Looking Glass Device Preview**を開始します。displayがない場合は**Start Local Edit Mode Preview**を選びます。

device出力には公式Looking Glass Bridge runtimeと接続displayが必要です。local packageのImport・Editor描画・機器出力・Playerはそれぞれ確認範囲が異なります。現在のOS/API別条件は[対応表](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/ja/compatibility.md)を参照してください。Editorでの成功だけでは機器出力やPlayer対応を示しません。

sceneの**Focus Target**はEditor上だけの一時的な選択欄です。適用したfocus distanceはsceneに保存されます。**Full**はより多くの視点を描画します。**Quarter**はGPU負荷を下げられますが、横へ動いたとき画像変化が大きくなることがあります。**Output Quality**は解像度を別に調整します。Edit Mode Previewではsceneは静止し、**Play**するとAnimator clipが動きます。接続displayへの出力は、メニューから作成したRigと同じく既定で自動開始します。補助スクリプトはplatformの出力を選び、起動設定を維持します。手動運用する場合は**Start Output On Enable**（Metal）／**Start Session On Enable**（Windows）をOFFにし、**Bridge > Start Output**で開始します。以前Importしたコピーは、編集sceneをバックアップして再Importするか、起動設定を有効にしてください。

[画像付きQuick Start手順](https://github.com/zabaglione/ViewWeave/blob/v0.1.0/docs/ja/getting-started.md)
