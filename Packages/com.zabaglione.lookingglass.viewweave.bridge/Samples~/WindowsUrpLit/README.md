# Windows URP Lit sample

Windows D3D11 Editor PlayまたはPlayerでURPの基本Lit表示を確認する任意sampleです。Unity 6000.6.4f1のURP projectへViewWeave URP配布packageをGit URLから追加し、`Window > Package Management > Package Manager`のSamplesから`Windows URP Lit`をImportします。配布packageにはCore、URP、Bridgeが含まれます。

## 使い方

1. `GameObject > ViewWeave > Bridge Rig`を作成します。
2. 同じGameObjectへ`BridgeUrpLitSampleGenerator`をAdd Componentします。
3. `Generate On Start`を有効にしてWindows Standalone PlayerをDirect3D11でBuild and Runします。
4. Player起動後、赤いcube、緑のsphere、directional lightがQuilt表示されることを確認します。

Inspectorの`Generate Lit Scene`は確認用のCube、sphere、lightを生成する操作です。Bridgeへの出力は、Bridge runtimeと機器を用意してWindows D3D11 EditorでPlayするか、Windows D3D11 Playerを起動して確認します。通常のruntime discoveryでBridge DLLを見つけられない場合だけ、`ViewWeaveBridgeSession`の`Runtime Path`へDLLの絶対パスを設定します。

## material resource

sampleは`Resources/ViewWeave/UrpLit.mat`を使います。resourceが見つからない場合は`ViewWeave/Samples/Windows URP Lit/Create Material Resource`を実行してから`Generate Lit Scene`を再実行してください。shaderへのsilent fallbackは行いません。
