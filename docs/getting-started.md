# Getting started

このrepositoryをcheckoutするにはGitHubのprivate repositoryへのアクセス権が必要です。独自コードのライセンスは[Apache-2.0](../LICENSE)です。

この手順はUnity 6000.6.4f1のWindowsプロジェクトを対象にします。URPまたはHDRPのpackageを選び、BridgeなしのEditor previewから始めるか、Bridgeを追加してWindows D3D11 Playerを作ります。

## 1. packageを追加する

1. Unity HubでURPまたはHDRPの空プロジェクトを作成し、Unity Editorで開きます。
2. `Window > Package Management > Package Manager`の`+ > Install package from disk`を選び、次の順に各`package.json`を追加します。
   - 必須: `Packages/com.lookingglass.community.core/package.json`
   - URP: `Packages/com.lookingglass.community.urp/package.json`
   - HDRP: `Packages/com.lookingglass.community.hdrp/package.json`
   - 実機Playerのみ: `Packages/com.lookingglass.community.bridge/package.json`
3. URP previewを試す場合はCore packageのSamplesから`Quilt Preview`をImportします。実機用ならBridge packageの`Windows URP Lit`または`Windows HDRP Lit`もImportします。

## 2. BridgeなしのEditor preview

Coreの`Quilt Preview`はURP専用です。HDRPでこのsampleを生成しないでください。

1. `GameObject > ViewWeave > Quilt Rig`を作成します。
2. Rigへ`QuiltPreviewDemoGenerator`をAdd Componentします。
3. `Preset=Preview`のまま、コンテキストメニューの`Generate Preview Rig`を実行します。
4. `Window > Looking Glass Community > Quilt Preview`を開き、Rigの`QuiltRenderer`をRenderer欄へdragします。
5. `Render Quilt`を押します。Quilt Preview windowに分割された複数視点画像が表示されれば成功です。

生成される名前は`Preview Lit Cube`、`Preview Lit Sphere`、`Preview Directional Light`です。`Balanced`または`Native`を使う場合はGeneratorの`nativeQuiltWidth`、`nativeQuiltHeight`、`columns`、`rows`を実際のprofileに設定します。Bridge、Looking Glass本体、device auto detectionは不要です。

VFX Graphを使う場合はLinear color spaceを設定します。HDRPもLinear color spaceと固定Exposureを必要とします。これはURPの通常描画やMSAAの検証条件とは分けて扱います。

## 3. Windows D3D11 Player

1. Bridge runtimeをWindowsへインストールし、Bridge packageを追加します。
2. `GameObject > ViewWeave > Bridge Rig`を作成します。専用Camera、`QuiltRenderer`、`ViewWeaveRig`、`BridgeWorkerQuiltOutput`、`ViewWeaveBridgeSession`、pipeline adapterが作成されます。
3. URPなら`BridgeUrpLitSampleGenerator`、HDRPなら`BridgeHdrpLitSampleGenerator`を同じRigへAdd Componentします。
4. Rigを置いたシーンを`File > Save As...`でAssetsへ保存します。
5. `File > Build Profiles`を開き、Windows profileのScene Listへ保存したsceneを追加します。`Player Settings > Other Settings`でGraphics APIs for Windowsの自動選択を無効にし、Direct3D11を先頭にします。Playerが非フォーカスでも描画を続けるため`Run In Background`は常時有効を推奨します。
6. `Build and Run`を実行します。Play開始時に`Generate On Start`を有効にしたgeneratorがscene内容を作成します。
7. Playerの画面でQuilt表示を確認します。Bridge runtimeが自動検出されない場合だけ、Sessionの`Runtime Path`へDLLの絶対パスを設定します。

URP sampleは赤い立方体、緑の球、ライトを生成します。HDRP sampleはHDRP Lit条件と固定Exposureを使います。Editorの`Generate Lit Scene`は生成sceneの確認用で、EditorからBridgeへ接続しません。

## 4. sample materialの修復

material resourceが見つからない場合だけ、次のEditor menuを使います。

- `ViewWeave/Samples/Windows URP Lit/Create Material Resource`
- `ViewWeave/Samples/Windows HDRP Lit/Create Material Resource`

その後、generatorの`Generate Lit Scene`を再実行します。

## 5. テストプロジェクト

repository rootで`python tools/prepare_sample_tests.py`を実行してから、Unity Hubの`Add`で`Tests/Pipelines/Unity`を開きます。このコマンドはignoredなBridge sample copyだけを作成します。Editorで`Window > General > Test Runner > PlayMode > Run All`を実行します。Build Settingsには固定sceneを登録していません。PlayMode fixtureは実行時にsceneを作成して終了時に破棄します。Run Allには既知のVFX compatibility未通過条件が含まれます。

Player用のgenerated sceneはEditor methodから作成します。

- `LookingGlass.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.Build`: `Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.unity`
- `LookingGlass.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.BuildDemo`: `Assets/BridgeWorkerValidationGenerated/ViewWeaveDemo.unity`
- `ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp`または`BuildHdrp`: `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`
- `LookingGlass.PlayerValidation.Editor.PlayerValidationBuilder.Build`: `Assets/PlayerValidationGenerated/PlayerValidation.unity`

これらはBuild用の一時sceneです。Projectウィンドウから手動で編集するsceneとして扱いません。

## 次に読む資料

- [Windows validation](windows-validation.md): 実行コマンド、結果、未検証範囲
- [Compatibility](compatibility.md): pipelineと機能の対応表
- [VFX Graph](vfx-graph.md): URP driverとHDRP診断条件
- [Pipeline smoke tests](../Tests/Pipelines/README.md): Editor testとgenerated sceneの入口
