[English](README.md)

# Windows URP Litサンプル

[ユーザーガイド](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/user-guide.md) · [技術者向けガイド](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/technical-guide.md)

これは既存のWindows Bridge手順を保つ任意のlegacy sampleです。初回利用ではURP Quick Startを使ってください。

Windows D3D11またはD3D12のEditor PlayまたはPlayerで、URPの基本Lit表示を確認する任意のsampleです。Unity 6000.6.4f1のURP projectにViewWeave URP配布packageをGit URLから追加し、`Window > Package Management > Package Manager`のSamplesから`Windows URP Lit`をImportします。配布packageにはCore、URP、Bridgeが含まれます。

## 使い方

1. `GameObject > ViewWeave > Bridge Rig`を作成します。
2. 同じGameObjectへ`BridgeUrpLitSampleGenerator`をAdd Componentします。
3. `Generate On Start`を有効にして、Direct3D11またはDirect3D12を使うWindows Standalone PlayerをBuild and Runします。
4. Player起動後、赤いCube、緑のsphere、directional lightがQuilt表示されることを確認します。

Inspectorの`Generate Lit Scene`は確認用のCube、sphere、lightを生成します。Bridgeへ出力するにはBridge runtimeと機器を用意し、Windows D3D11/D3D12 EditorでPlayするか、Windows D3D11/D3D12 Playerを起動します。通常のruntime discoveryでBridge DLLが見つからない場合だけ、`ViewWeaveBridgeSession`の`Runtime Path`にDLLの絶対パスを設定します。

## Edit Mode preview

Play Modeへ移行せずに表示するには、Bridge Rigを選び、sample generator componentのメニューから`Generate Lit Scene`を実行します。`Window > ViewWeave > Rig Authoring`を開き、windowの`Rig` fieldへBridge Rigを指定してから、生成したobjectを`Focus Target`に直接指定するかfieldへdragします。必要に応じて`Set Focus From Target Center`を押します。Editor内でQuiltを見る場合は`Start Local Edit Mode Preview`、接続機器へ送る場合は`Start Looking Glass Device Preview`を明示的に開始し、終了時に`Stop Preview`を押します。このEditor previewは通常Bridge Sessionとは別です。通常SessionのBridge出力には引き続きEditor PlayまたはPlayerが必要です。device previewには対応するWindows D3D11/D3D12 GPUと公式Looking Glass Bridge runtimeが必要です。GPU ownership proofを含む手順と対応条件は[Quilt制作ガイド](https://github.com/zabaglione/ViewWeave/blob/main/docs/ja/quilt-authoring.md)を参照してください。

## Material resourceについて

sampleは`Resources/ViewWeave/UrpLit.mat`を使用します。resourceが見つからない場合は`ViewWeave/Samples/Windows URP Lit/Create Material Resource`を実行してから`Generate Lit Scene`を再実行してください。別shaderへの暗黙のfallbackは行いません。
