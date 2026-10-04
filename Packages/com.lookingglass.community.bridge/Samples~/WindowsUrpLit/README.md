# Windows URP Lit sample

Windows D3D11 PlayerでURPの基本Lit表示を確認するsampleです。Unity 6000.6.4f1のURP projectへCore、URP、Bridge packageを追加し、`Window > Package Management > Package Manager`のBridge package Samplesから`Windows URP Lit`をImportします。

## 使い方

1. `GameObject > ViewWeave > Bridge Rig`を作成します。
2. 同じGameObjectへ`BridgeUrpLitSampleGenerator`をAdd Componentします。
3. `Generate On Start`を有効にしてWindows Standalone PlayerをDirect3D11でBuild and Runします。
4. Player起動後、赤いcube、緑のsphere、directional lightがQuilt表示されることを確認します。

Editorで生成物だけを確認する場合は、generatorのコンテキストメニュー`Generate Lit Scene`を実行します。これはBridgeへ接続しません。通常のruntime discoveryでBridge DLLを見つけられない場合だけ、`ViewWeaveBridgeSession`の`Runtime Path`へDLLの絶対パスを設定します。

## material resource

sampleは`Resources/ViewWeave/UrpLit.mat`を使います。resourceが見つからない場合は`ViewWeave/Samples/Windows URP Lit/Create Material Resource`を実行してから`Generate Lit Scene`を再実行してください。shaderへのsilent fallbackは行いません。
