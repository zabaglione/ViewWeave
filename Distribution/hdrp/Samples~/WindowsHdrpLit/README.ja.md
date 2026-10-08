[English](README.md)

# Windows HDRP Litサンプル

Windows D3D11またはD3D12のEditor PlayまたはPlayerで、HDRPの基本Lit表示を確認する任意のsampleです。Unity 6000.6.4f1のHDRP projectにViewWeave HDRP配布packageをGit URLから追加し、`Window > Package Management > Package Manager`のSamplesから`Windows HDRP Lit`をImportします。配布packageにはCore、HDRP、Bridgeが含まれます。HDRPではLinear color spaceが必要です。Quilt描画中のExposureはadapterが全view共通のFixed値に調整します。

## 使い方

1. `GameObject > ViewWeave > Bridge Rig`を作成します。
2. 同じGameObjectへ`BridgeHdrpLitSampleGenerator`をAdd Componentします。
3. `Generate On Start`を有効にして、Direct3D11またはDirect3D12を使うWindows Standalone PlayerをBuild and Runします。
4. Player起動後、生成されたLit sceneがQuilt表示されることを確認します。

Inspectorの`Generate Lit Scene`は確認用のCube、sphere、lightを生成します。Bridgeへ出力するにはBridge runtimeと機器を用意し、Windows D3D11/D3D12 EditorでPlayするか、Windows D3D11/D3D12 Playerを起動します。通常のruntime discoveryでBridge DLLが見つからない場合だけ、`ViewWeaveBridgeSession`の`Runtime Path`にDLLの絶対パスを設定します。

## MaterialとHDRP条件

sampleは`Resources/ViewWeave/HdrpLit.mat`を使用します。resourceが見つからない場合は`ViewWeave/Samples/Windows HDRP Lit/Create Material Resource`を実行してから`Generate Lit Scene`を再実行してください。生成時は赤いCube、緑のsphere、directional light、固定Exposure、Motion Blur、SSR、SSGI、volumetric fog、cloudの無効設定を使用します。
