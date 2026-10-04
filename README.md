# ViewWeave

Looking Glass向けの非公式Unity SDKです。URPまたはHDRPの通常のシーンを複数視点で描画し、Windows上のLooking Glass Bridge runtimeへ送ります。

Looking Glassに表示するときは、利用者自身のUnity projectをEditor PlayするかPlayerとして起動します。UnityがQuiltを描画し、インストール済みで起動中の公式Looking Glass Bridge runtimeが機器へ届けます。SDK内のTests/Bridge Unityプロジェクトやデモアプリを別途起動する必要はありません。

対応環境はUnity 6000.6.4f1、Windows、URPまたはHDRP、D3D11です。Bridge runtimeとLooking Glass機器を別途用意してください。Windows D3D11のUnity Editor PlayとWindows D3D11 Playerで出力できます。Edit Mode、D3D12、macOS出力は対象外です。

このprivate repositoryからPackage Managerで取得するにはGitHubのアクセス権が必要です。独自コードのライセンスは[Apache-2.0](LICENSE)です。

## 使い始める

1. Unity HubでUnity 6000.6.4f1のURPまたはHDRPテンプレートからプロジェクトを作り、Unity Editorで開きます。
2. `Window > Package Management > Package Manager`の`+ > Install package from git URL`を選び、利用するpipelineのURLを追加します。
   - URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
   - HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`
3. 配布packageにはCore、Bridge、選択したpipelineが含まれます。追加のViewWeave packageは不要です。Package ManagerのSamplesは任意で、ImportするまでAssetsへ入りません。標準Cubeから試す場合はImport不要です。
4. WindowsへLooking Glass Bridge runtimeをインストールして、対象機器を接続します。
5. シーンに通常のCubeまたはBoxを置き、必要なmaterialとlightを設定します。特別なobject scriptやgeneratorは不要です。
6. メニューから`GameObject > ViewWeave > Bridge Rig`を作成します。新しいRigはMainCameraの位置、向き、FOV、near/far、culling maskを引き継ぎます。Rig作成前に選択していたRendererの中心がMainCameraのnear/far範囲内かつ正面にあれば、その正のforward距離をzero-parallax距離にします。範囲外、背面、選択Rendererなしの場合は10m、MainCameraがない場合は3mです。
7. HDRPではPlayer SettingsのColor SpaceをLinearにします。HDRP adapterは既定でquilt描画中の一時的な調整を行います。通常のMainCameraやVolume設定は変更しません。詳細は[Getting started](docs/getting-started.md)を参照してください。
8. Editorから表示するには、Editor自体をD3D11で起動してからPlayします。起動引数`-force-d3d11`を使うか、Player SettingsのWindows Graphics APIsをD3D11優先にしてEditorを再起動してください。実際のAPIはGraphicsDeviceTypeで確認できます。D3D12やEdit ModeではBridge出力しません。

Git packageを更新した後はUnity Editorを正常終了し、`-force-d3d11`を付けて起動し直してください。新しいmanaged assemblyとnative pluginの読み込みに必要です。

Windows Playerで使う場合は、Windows Graphics APIsでD3D11を有効にしてBuild and Runします。Bridge runtimeが自動検出されない場合だけ、RigのSessionにRuntime Pathを設定します。

## 任意のサンプル

付属の`Windows URP Lit`と`Windows HDRP Lit`はPackage ManagerのViewWeave package詳細にあるSamplesから明示的にImportできます。Import後、既存のBridge RigへURPなら`BridgeUrpLitSampleGenerator`、HDRPなら`BridgeHdrpLitSampleGenerator`をAdd Componentし、`Generate On Start`を有効にするかInspectorの`Generate Lit Scene`を実行します。詳しくは[サンプル手順](docs/getting-started.md#任意のsamplesをimportする)を参照してください。packageを追加しただけではサンプルがAssetsへコピーされません。

## HDRPと色空間

HDRPとVFX GraphはLinear color spaceが必須です。HDRP adapterはquiltの描画中だけMotion Blur、SSR、SSGI、volumetric fog、volumetric cloudsを抑え、TAAを一時停止します。Auto Exposureは全quilt viewで同じFixed EV 12にし、元のVolume stackがFixedならそのEVを保ちます。通常のFog、Sky、Bloomなどは維持されます。HDRP adapterの詳細と制約は[Compatibility](docs/compatibility.md)を参照してください。

## テストプロジェクト

テスト環境の準備は[Tests/Pipelines/README.md](Tests/Pipelines/README.md)、実行方法は[Getting startedのテストプロジェクト](docs/getting-started.md#テストプロジェクト)を参照してください。

## 検証資料

- [Getting started](docs/getting-started.md): Editor PlayとPlayerの手順
- [Compatibility](docs/compatibility.md): 対応条件と既知の制約
- [Windows validation](docs/windows-validation.md): 実行済み試験と結果
- [Unity feature coverage](docs/unity-feature-coverage.md): Unity機能ごとの確認範囲
- [VFX Graph](docs/vfx-graph.md): VFX Graph利用時の条件
- [Third-party notices](docs/THIRD_PARTY_NOTICES.md): 外部ライセンス
