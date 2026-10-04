# Windows HDRP Lit sample

Windows D3D11 Editor PlayまたはPlayerでHDRPの基本Lit表示を確認する任意sampleです。Unity 6000.6.4f1のHDRP projectへViewWeave HDRP配布packageをGit URLから追加し、`Window > Package Management > Package Manager`のSamplesから`Windows HDRP Lit`をImportします。配布packageにはCore、HDRP、Bridgeが含まれます。HDRPではLinear color spaceが必要です。quilt描画中のExposureはadapterが全view共通のFixed値に調整します。

## 使い方

1. `GameObject > ViewWeave > Bridge Rig`を作成します。
2. 同じGameObjectへ`BridgeHdrpLitSampleGenerator`をAdd Componentします。
3. `Generate On Start`を有効にしてWindows Standalone PlayerをDirect3D11でBuild and Runします。
4. Player起動後、生成されたLit sceneがQuilt表示されることを確認します。

Inspectorの`Generate Lit Scene`は確認用のCube、sphere、lightを生成する操作です。Bridgeへの出力は、Bridge runtimeと機器を用意してWindows D3D11 EditorでPlayするか、Windows D3D11 Playerを起動して確認します。通常のruntime discoveryでBridge DLLを見つけられない場合だけ、`ViewWeaveBridgeSession`の`Runtime Path`へDLLの絶対パスを設定します。

## materialとHDRP条件

sampleは`Resources/ViewWeave/HdrpLit.mat`を使います。resourceが見つからない場合は`ViewWeave/Samples/Windows HDRP Lit/Create Material Resource`を実行してから`Generate Lit Scene`を再実行してください。生成時は赤いcube、緑のsphere、directional light、固定Exposure、Motion Blur、SSR、SSGI、volumetric fog、cloudの無効設定を使用します。
