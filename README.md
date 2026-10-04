# ViewWeave

Looking Glass向けの非公式Unity SDKです。専用のQuilt RenderTextureへ複数視点を描画し、Bridgeを使わないEditor preview、またはWindows D3D11 Playerから表示できます。

対象環境はUnity 6000.6.4f1、Windows、URPまたはHDRPです。実機表示には別途Looking Glass Bridge runtimeが必要です。macOS、D3D12 Bridge出力、複数機器の自動運用は対象外です。

このprivate repositoryをcheckoutするにはGitHubのアクセス権が必要です。取得例は`git clone https://github.com/zabaglione/ViewWeave.git`です。独自コードのライセンスは[Apache-2.0](LICENSE)です。

## まず選ぶ使い方

| 目的 | 必要なpackage | 入口 | 結果 |
| --- | --- | --- | --- |
| Bridgeなしで画面を確認 | Core + URP | `GameObject/ViewWeave/Quilt Rig` + `QuiltPreviewDemoGenerator` | `Quilt Preview` windowでQuilt preview |
| URPで実機表示 | Core + URP + Bridge | `GameObject/ViewWeave/Bridge Rig` + `BridgeUrpLitSampleGenerator` | Windows D3D11 Playerで赤い立方体と緑の球 |
| HDRPで実機表示 | Core + HDRP + Bridge | `GameObject/ViewWeave/Bridge Rig` + `BridgeHdrpLitSampleGenerator` | Windows D3D11 PlayerでHDRP Lit scene |

## packageをローカルから追加する

1. Unity HubでUnity 6000.6.4f1の空のURPまたはHDRPプロジェクトを作成し、Unity Editorで開きます。
2. `Window > Package Management > Package Manager`を開き、左上の`+`から`Install package from disk`を選びます。
3. このcheckoutの`Packages/com.lookingglass.community.core/package.json`を先に追加します。
4. URPなら`Packages/com.lookingglass.community.urp/package.json`、HDRPなら`Packages/com.lookingglass.community.hdrp/package.json`を追加します。
5. 実機表示を行う場合だけ`Packages/com.lookingglass.community.bridge/package.json`を追加します。
6. URP previewを試す場合はCoreの`Samples`から`Quilt Preview`を`Import`します。実機用の場合はBridgeの`Windows URP Lit`または`Windows HDRP Lit`も`Import`します。

## Bridgeなしで最短確認する

URPプロジェクトで次の操作を行います。

1. `GameObject > ViewWeave > Quilt Rig`を作成します。専用Camera、`QuiltRenderer`、`ViewWeaveRig`、URP adapterが作成されます。
2. Rigに`QuiltPreviewDemoGenerator`をAdd Componentします。
3. Inspectorの`Preset`を`Preview`にして、コンポーネントのコンテキストメニューから`Generate Preview Rig`を実行します。
4. `Window > Looking Glass Community > Quilt Preview`を開き、Rigの`QuiltRenderer`をRenderer欄へdragします。
5. `Render Quilt`を押します。Bridge runtimeやLooking Glass本体は不要です。

生成されるpreview sceneには`Preview Lit Cube`、`Preview Lit Sphere`、`Preview Directional Light`が含まれます。Quilt Preview windowに分割された複数視点画像が表示されれば成功です。`Balanced`または`Native`を使う場合はGeneratorの解像度とgridを実際のprofileに合わせます。Nativeは機器を自動検出しません。

VFX Graphを使う場合はLinear color spaceを設定してください。HDRPもLinear color spaceと固定Exposureを必要とします。URPのMSAAはGamma/Linearの限定試験を実施しています。範囲と結果は[Windows validation](docs/windows-validation.md)を参照してください。

## Windows D3D11 Playerで実機表示する

1. URPまたはHDRP packageを追加したプロジェクトへBridge packageを追加し、Bridge runtimeをWindowsへインストールします。
2. `GameObject > ViewWeave > Bridge Rig`を作成します。専用Camera、`QuiltRenderer`、`ViewWeaveRig`、`BridgeWorkerQuiltOutput`、`ViewWeaveBridgeSession`、対応adapterが同じGameObjectに作成されます。
3. URPなら`BridgeUrpLitSampleGenerator`、HDRPなら`BridgeHdrpLitSampleGenerator`を同じRigへAdd Componentします。`Generate On Start`は有効のままにします。
4. Rigを配置したシーンを`File > Save As...`でAssetsへ保存します。
5. `File > Build Profiles`を開き、Windows profileのScene Listへ保存したsceneを追加します。`Player Settings > Other Settings`でGraphics APIs for Windowsの自動選択を無効にし、Direct3D11を先頭にします。Playerが非フォーカスでも描画を続けるため`Run In Background`は常時有効を推奨します。
6. `Build and Run`を実行します。通常はBridge runtimeを自動検出します。検出できない場合だけSessionの`Runtime Path`へDLLの絶対パスを設定します。

URP sampleは赤い立方体、緑の球、ライトを生成します。HDRP sampleも基本形状に加えて固定ExposureとHDRPの制約設定を生成します。Editorの`Generate Lit Scene`は生成物確認用で、EditorからBridgeへ接続する操作ではありません。

## Samplesの比較

| Sample | pipeline | Bridge | 生成操作 | 主な確認内容 |
| --- | --- | --- | --- | --- |
| `Quilt Preview` | URP | 不要 | `QuiltPreviewDemoGenerator > Generate Preview Rig` | EditorのQuilt Preview windowで立方体、球、Quilt描画を確認 |
| `Windows URP Lit` | URP | 必要 | `BridgeUrpLitSampleGenerator > Generate Lit Scene` または`Generate On Start` | Windows D3D11 Playerの赤い立方体、緑の球、視差 |
| `Windows HDRP Lit` | HDRP | 必要 | `BridgeHdrpLitSampleGenerator > Generate Lit Scene` または`Generate On Start` | Windows D3D11 PlayerのHDRP Lit表示 |

Bridge sampleのmaterial resourceが見つからない場合は、`ViewWeave/Samples/Windows URP Lit/Create Material Resource`または`ViewWeave/Samples/Windows HDRP Lit/Create Material Resource`を実行してから再生成します。

## テストプロジェクトを開く

repository rootで`python tools/prepare_sample_tests.py`を実行してから、Unity Hubの`Add`で`Tests/Pipelines/Unity`を開きます。GPUを使うfixtureが含まれるため、GPUのあるWindows環境で実行します。Editorで`Window > General > Test Runner`を開き、`PlayMode`タブの`Run All`を実行してください。Run Allには既知のVFX compatibility未通過条件が含まれます。

このテストプロジェクトのBuild Settingsには固定のtest sceneを登録していません。通常のPlayModeテストは実行時にfixture sceneを作成し、終了時に破棄します。

Player確認用のgenerated scene入口とTest Runnerの対象は[Pipeline smoke tests](Tests/Pipelines/README.md)にまとめています。詳細な結果と未検証範囲は[Windows validation](docs/windows-validation.md)、対応表は[compatibility](docs/compatibility.md)、導入補足は[getting started](docs/getting-started.md)を参照してください。

## 知っておく制約

- URPはBase cameraを使います。camera stack、一般的なtemporal history、VFX全体の対応は保証しません。
- HDRPはLinear color spaceと固定Exposureを使います。VFX Graphを使う場合もLinear color spaceを設定します。詳細は[VFX Graph guide](docs/vfx-graph.md)を参照してください。
- 実機の視点番号、奥行き、画質は限定条件で確認済みです。全視点と全視域の網羅的な光学確認は未実施です。

公式SDKとの関係、ライセンス、詳細な制約は[compatibility](docs/compatibility.md)と[third-party notices](docs/THIRD_PARTY_NOTICES.md)を参照してください。
