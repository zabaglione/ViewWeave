[English](README.md)

# Pipeline smoke tests

> 対象読者: Unity／描画基盤の技術者。このページは試験環境の準備と実行方法を説明します。結果は各runに記載した条件の範囲です。現在の対応条件は[対応表](../../docs/ja/compatibility.md)、導入手順は[使い始める](../../docs/ja/getting-started.md)、技術資料一覧は[技術者向けガイド](../../docs/ja/technical-guide.md)を参照してください。利用者向け操作は[ユーザーガイド](../../docs/ja/user-guide.md)を参照してください。

`Tests/Pipelines/Unity`は、Core、URP、HDRP、Bridgeのvalidationを実行するUnity projectです。fresh cloneではUnity Hubで開く前にrepository rootから`python tools/prepare_sample_tests.py`を実行してください。Bridge packageのSamplesをignoredな`Tests/Pipelines/Unity/Assets/Samples/ViewWeaveBridge`へcopyします。Unity Hubの`Add`からこのフォルダを開き、Unity 6000.6.4f1を選択してください。

## Test Runnerで実行する

1. Unity Editorで`Window > General > Test Runner`を開きます。
2. `PlayMode`タブを選択します。
3. `Run All`を押します。

Build Settingsには固定sceneを登録していません。PlayModeテストは実行時にfixture sceneを作り、終了時に破棄します。GPU試験には実際のgraphics deviceが必要で、Windows固有の試験にはWindowsも必要です。対象を絞った`ViewWeave.GraphicsCompatibility` suiteはmacOS Metalでも実行できます。Run Allには既知のVFX compatibility未通過条件が含まれるため、結果XMLで内訳を確認してください。

主なPlayModeテストクラスは次のとおりです。

| クラス | 目的 |
| --- | --- |
| `BridgeSessionValidationTests` | Session状態、timeout、completion watchdog、quality再設定のmanaged検証 |
| `SrpAdditionalValidationTests` | URP/HDRPの基本描画、MSAA、postprocess条件 |
| `QuiltRendererMsaaLifecycleTests` | 1x/2x/4x/8x、resolve、resize、disable、Deferred fallback |
| `GrdValidationPlayModeTests` | URP/HDRPのGPU Resident Drawer条件 |
| `VfxGraphicsCompatibilityTests` | URP/HDRP VFX compatibility matrix |
| `HdrpVfxPrimingValidationTests` | HDRP same-camera primingの診断 |
| `HdrpTransientVolumePreparationTests` | HDRPの視点別履歴、共通GPU露出、リソース制限、停止時の解放 |
| `UrpTaaPlatformTests` | Metal上の通常URP CameraのTAAと、現行QuiltのTAA停止・復元。Quilt TAAを有効にする試験ではありません |

CLIで同じPlayMode suiteを実行する場合は、repository rootから次を使います。

```powershell
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --report-format junit --output "$PWD/.local/test-results/playmode.xml" --timeout 1200 -- -force-d3d11
```

結果XMLのtest countが0の場合は成功扱いにしません。結果の内訳、既知のVFX未通過条件、D3D12の対象範囲は[docs/windows-validation.md](../../docs/windows-validation.md)を参照してください。

## generated sceneを使うPlayer validation

Playerを作るvalidationだけは、Editor methodが一時的なsceneを作成します。これらはBuild用の入口であり、固定sceneとして手動編集しません。

| Editor method | generated scene | 目的 |
| --- | --- | --- |
| `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.Build` | `Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.unity` | Bridge Worker validation Player |
| `ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerValidationBuilder.BuildDemo` | `Assets/BridgeWorkerValidationGenerated/ViewWeaveDemo.unity` | 48 view demo Player |
| `ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp` | `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity` | URP sample Player |
| `ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildHdrp` | `Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity` | HDRP sample Player |
| `ViewWeave.PlayerValidation.Editor.PlayerValidationBuilder.Build` | `Assets/PlayerValidationGenerated/PlayerValidation.unity` | BridgeなしのURP GRD Player検証 |
| `ViewWeave.VfxPlayerValidation.Editor.VfxPlayerValidationBuilder.Build` | `Assets/VfxPlayerValidationGenerated/VfxPlayerValidation.unity` | VFX Player validation |
| `ViewWeave.MacHdrpHistoryValidation.Editor.MacHdrpHistoryValidationBuilder.Build` | `Assets/SamplePlayerValidationGenerated/MacHdrpHistory/Validation.unity` | Bridgeを使わないmacOS MetalのHDRP履歴・露出試験 |

Bridge demoの視点番号検証は`ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run`です。Bridge接続なしで48 tileの番号一意性とcoverageを確認し、結果を`.local`へ保存します。実機確認ではview 0/47を左右と仮定せず、左右から実際に見えた番号を記録します。

generated sceneを作ってPlayerをBuildする場合は、Unity CLIからEditor methodへ引数を渡します。例えばURP sample Playerは次のように実行します。

```powershell
unity run "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 -- -executeMethod ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp -buildOutput "$PWD/.local/player/SampleUrp.exe"
```

このmethodは`Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`を一時作成し、Direct3D11、Linear、Run In Backgroundを設定してPlayerをBuildします。Build後のPlayer起動にはBridge runtimeが必要です。

## macOS Metalの履歴・露出試験

実際のMetal GPUを使い、repository rootから対象を絞ったPlayMode試験を実行します。

```bash
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --filter ViewWeave.GraphicsCompatibility --output "$PWD/.local/hdrp-lighting/graphics.xml" --timeout 1200 -- -force-metal
```

HDRP描画専用PlayerはBridgeを使いません。隔離したbatch Editorでビルドします。builderは一時変更したpipeline、color space、graphics API、scene設定を復元します。generated assetとPlayerはGit対象外です。まずDevelopmentを作り、別の出力先で`Development`を`Release`に替えて再実行します。

```bash
unity build "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --target StandaloneOSX --execute-method ViewWeave.MacHdrpHistoryValidation.Editor.MacHdrpHistoryValidationBuilder.Build --output-path "$PWD/.local/hdrp-lighting/player-development/ViewWeaveHdrpValidation.app" --allow-dirty-build --timeout 1800 --args '-force-metal -validationConfiguration Development'
mkdir -p "$PWD/.local/hdrp-lighting"
"$PWD/.local/hdrp-lighting/player-development/ViewWeaveHdrpValidation.app/Contents/MacOS/PipelineSmoke" -batchmode -force-metal -screen-width 320 -screen-height 240 -validationReport "$PWD/.local/hdrp-lighting/player-development.json" -logFile "$PWD/.local/hdrp-lighting/player-development.log"
```

Cameraの視点別分離・維持、HDRP TAA設定の保持、Fixed exposureのcompensation、自動露出の光量追従、赤いQuilt画素とmagenta画素の不在、所有リソースの解放、1 MBのwarmup予算超過を確認します。JSONと終了codeを両方確認してください。GPU完了待ちは実時間で制限し、Cubeが測光画面の大部分を占めるようにしています。黒背景が大半のHistogramから大きなEV変化を期待することはできません。Bridge出力、実機光学、HDRP TAA画素正確性、Windowsの検証ではありません。結果と残る警告は[日付付き記録](../../docs/hdrp-view-history-validation-20261010.md)を参照してください。

## GPU Resident Drawer validation

`GrdValidationPlayModeTests`はURP/HDRPの1、8、48 viewsを検証します。GRDのON条件はprofiler counterも確認します。結果PNGとJSONは`.local/grd-validation/`へ出力されますが、証跡ファイルだけで合否を決めず、Unity test resultとlogを確認してください。Editor結果はPlayerのthreading、長時間運転、性能を保証しません。

## PlayerとEditorの境界

通常のTest Runner実行はUnity EditorのPlayModeです。Bridge接続を使う実機Player確認は、Bridge sampleまたは上記generated sceneをBuildして別途実行します。制作時はEdit Modeで`ViewWeaveRig` previewを明示的に開始し、Editor内表示またはWindows D3D11/D3D12 Bridge経路での機器表示ができます。これは通常Sessionとは別機能で、通常SessionのBridge出力にはEditor PlayまたはPlayerが必要です。設定と対応条件は[Quilt制作ガイド](../../docs/ja/quilt-authoring.md)を参照してください。PlayMode suiteはこのEditor previewを検証しません。
