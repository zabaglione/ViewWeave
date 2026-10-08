[English](README.md)

# Windows URP Litサンプル

Windows D3D11またはD3D12のEditor PlayまたはPlayerで、URPの基本Lit表示を確認する任意のsampleです。Unity 6000.6.4f1のURP projectにViewWeave URP配布packageをGit URLから追加し、`Window > Package Management > Package Manager`のSamplesから`Windows URP Lit`をImportします。配布packageにはCore、URP、Bridgeが含まれます。

## 使い方

1. `GameObject > ViewWeave > Bridge Rig`を作成します。
2. 同じGameObjectへ`BridgeUrpLitSampleGenerator`をAdd Componentします。
3. `Generate On Start`を有効にして、Direct3D11またはDirect3D12を使うWindows Standalone PlayerをBuild and Runします。
4. Player起動後、赤いCube、緑のsphere、directional lightがQuilt表示されることを確認します。

Inspectorの`Generate Lit Scene`は確認用のCube、sphere、lightを生成します。Bridgeへ出力するにはBridge runtimeと機器を用意し、Windows D3D11/D3D12 EditorでPlayするか、Windows D3D11/D3D12 Playerを起動します。通常のruntime discoveryでBridge DLLが見つからない場合だけ、`ViewWeaveBridgeSession`の`Runtime Path`にDLLの絶対パスを設定します。

## Material resourceについて

sampleは`Resources/ViewWeave/UrpLit.mat`を使用します。resourceが見つからない場合は`ViewWeave/Samples/Windows URP Lit/Create Material Resource`を実行してから`Generate Lit Scene`を再実行してください。別shaderへの暗黙のfallbackは行いません。
