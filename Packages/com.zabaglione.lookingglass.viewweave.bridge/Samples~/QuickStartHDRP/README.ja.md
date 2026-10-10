[English](README.md)

# HDRP Quick Start

[ユーザーガイド](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/user-guide.md) · [技術者向けガイド](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/technical-guide.md)

設定済みViewWeave Rig、色付きのprimitive、loopするAnimator clipを含むSampleです。生成用scriptやobjectへの追加componentは不要です。HDRP projectではLinear color spaceを使います。

1. Package Managerの**Samples**から**HDRP Quick Start**をImportします。
2. ImportしたSample内の`QuickStart.unity`を開きます。
3. **ViewWeave Bridge Rig**を選び、**Preview > Start Looking Glass Device Preview**を開始します。displayがない場合は**Start Local Edit Mode Preview**を選びます。

device出力には公式Looking Glass Bridge runtimeと接続displayが必要です。platform別の条件と確認範囲は[対応表](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/compatibility.md)を参照してください。このSampleはローカルからUPM Importし、macOS MetalのEditorとGoで確認しました。このSampleのWindowsでのUPM導入、D3D11/D3D12出力、Playerビルドは未検証です。

sceneの**Focus Target**はEditor上だけの一時的な選択欄です。適用したfocus distanceはsceneに保存されます。**Full**はより多くの視点を描画します。**Quarter**はGPU負荷を下げられますが、横へ動いたとき画像変化が大きくなることがあります。**Output Quality**は解像度を別に調整します。Edit Mode Previewではsceneは静止し、**Play**するとAnimator clipが動きます。接続displayへアニメーションを出すには、**Bridge > Start Output**を明示的に選びます。出力は自動で始まらず、Sample helperはplatform用output componentを選ぶだけです。

[画像付きQuick Start手順](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/getting-started.md)
