# Getting started

このprivate repositoryからUnity Package Managerで取得するにはGitHubへのアクセス権が必要です。独自コードのライセンスは[Apache-2.0](../LICENSE)です。

この手順はUnity 6000.6.4f1のWindowsプロジェクトを対象にします。URPまたはHDRPのpackageを1つ選び、Looking Glass Bridge runtimeを使ってWindows D3D11 Playerを作ります。

## 1. packageをGit URLから追加する

1. Unity HubでURPまたはHDRPの空プロジェクトを作成し、Unity Editorで開きます。
2. GitHub private repositoryを読めるGit Credential Managerを設定します。SSHを使う場合はSSH鍵をGitHubへ登録してください。認証tokenをURLへ埋め込まないでください。
3. `Window > Package Management > Package Manager`の`+ > Install package from git URL`を選びます。
4. HTTPSではURPに`https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`、HDRPに`https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`を入力します。SSHではURPに`ssh://git@github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`、HDRPに`ssh://git@github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`を入力します。
5. 配布packageにCore、Bridge、選択したpipelineが含まれるため、別packageを追加しません。Package Managerの`Samples`からURPでは`Windows URP Lit`、HDRPでは`Windows HDRP Lit`を明示的にImportします。Samplesは自動的にAssetsへimportされません。

Package ManagerではURP版を`ViewWeave for Looking Glass - URP (Unofficial)`、HDRP版を`ViewWeave for Looking Glass - HDRP (Unofficial)`として表示します。

URPとHDRPの配布packageは同じprojectへ同時に追加せず、projectのpipelineに合う方を1つ選びます。`#main`を使うGit packageはPackage Managerのcacheが残ることがあるため、更新を確認するときは一度Removeしてから同じGit URLを再Addするか、検証用のcommit hashをURLのrefに指定します。

## 2. Windows D3D11 Player

1. Bridge runtimeをWindowsへインストールします。Bridge codeは配布packageに含まれます。
2. `GameObject > ViewWeave > Bridge Rig`を作成します。専用Camera、`QuiltRenderer`、`ViewWeaveRig`、`BridgeWorkerQuiltOutput`、`ViewWeaveBridgeSession`、pipeline adapterが作成されます。
3. URPなら`BridgeUrpLitSampleGenerator`、HDRPなら`BridgeHdrpLitSampleGenerator`を同じRigへAdd Componentします。
HDRPまたはVFX Graphを使う場合は、Build操作の前にColor SpaceをLinearに設定してください。HDRP sampleは固定Exposureを生成します。
4. Rigを置いたシーンを`File > Save As...`でAssetsへ保存します。
5. `File > Build Profiles`を開き、Windows profileのScene Listへ保存したsceneを追加します。`Player Settings > Other Settings`でGraphics APIs for Windowsの自動選択を無効にし、Direct3D11を先頭にします。Playerが非フォーカスでも描画を続けるため`Run In Background`は常時有効を推奨します。
6. `Build and Run`を実行します。Play開始時に`Generate On Start`を有効にしたgeneratorがscene内容を作成します。
7. Playerの画面でQuilt表示を確認します。Bridge runtimeが自動検出されない場合だけ、Sessionの`Runtime Path`へDLLの絶対パスを設定します。

URP sampleは赤い立方体、緑の球、ライトを生成します。HDRP sampleはHDRP Lit条件と固定Exposureを使います。Editorの`Generate Lit Scene`は生成sceneの確認用で、EditorからBridgeへ接続しません。

## 3. sample materialの修復

material resourceが見つからない場合だけ、次のEditor menuを使います。

- `ViewWeave/Samples/Windows URP Lit/Create Material Resource`
- `ViewWeave/Samples/Windows HDRP Lit/Create Material Resource`

その後、generatorの`Generate Lit Scene`を再実行します。

## 4. テストプロジェクト

repository rootで`python tools/prepare_sample_tests.py`を実行してから、Unity Hubの`Add`で`Tests/Pipelines/Unity`を開きます。このコマンドはignoredなBridge sample copyだけを作成します。Editorで`Window > General > Test Runner > PlayMode > Run All`を実行します。Build Settingsには固定sceneを登録していません。PlayMode fixtureは実行時にsceneを作成して終了時に破棄します。Run Allには既知のVFX compatibility未通過条件が含まれます。

Player用のgenerated sceneはEditor methodから作成します。

- `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.Build`: `Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.unity`
- `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.BuildDemo`: `Assets/BridgeWorkerValidationGenerated/ViewWeaveDemo.unity`
- `ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp`または`BuildHdrp`: `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`
- `ViewWeave.PlayerValidation.Editor.PlayerValidationBuilder.Build`: `Assets/PlayerValidationGenerated/PlayerValidation.unity`

これらはBuild用の一時sceneです。Projectウィンドウから手動で編集するsceneとして扱いません。

## 次に読む資料

- [Windows validation](windows-validation.md): 実行コマンド、結果、未検証範囲
- [Compatibility](compatibility.md): pipelineと機能の対応表
- [VFX Graph](vfx-graph.md): URP driverとHDRP診断条件
- [Pipeline smoke tests](../Tests/Pipelines/README.md): Editor testとgenerated sceneの入口
