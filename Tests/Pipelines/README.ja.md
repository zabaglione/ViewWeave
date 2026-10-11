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
| `UrpTaaPlatformTests` | Metalおよび対応Windows D3D11/D3D12 Editor上のURP Quilt TAA。view別historyとlifecycleを確認 |
| `WindowsHdrpEffectPixelTests` | 明示実行するWindows D3D11/D3D12専用HDRP effect画素characterization。既定full suiteのgateではなく診断用 |

CLIで同じPlayMode suiteを実行する場合は、repository rootから次を使います。

```powershell
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --report-format junit --output "$PWD/.local/test-results/playmode.xml" --timeout 1200 -- -force-d3d11
```

結果XMLのtest countが0の場合は成功扱いにしません。結果の内訳、既知のVFX未通過条件、D3D12の対象範囲は[docs/windows-validation.md](../../docs/windows-validation.md)を参照してください。

### Windows HDRP effect characterization

Windows専用HDRP fixtureにはNUnit `Explicit`を付けているため、通常のfull suiteでは実行されません。Unity EditorのDirect3D 11またはDirect3D 12で明示実行する診断fixtureです。caseは`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.FxaaChangesDiagonalEdgePixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.SmaaChangesDiagonalEdgePixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.TaaAdvancesIndependentViewHistoriesAndChangesPixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.MotionBlurChangesMovingObjectPixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.SsrChangesScreenSpaceReflectionPixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.SsgiChangesIndirectReceiverPixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.VolumetricFogChangesScatteringPixels`、`ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.VolumetricCloudsChangeSkyPixels`です。APIごとにEditor invocationを分け、例として次を実行します。

```powershell
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode PlayMode --filter ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests --output "$PWD/.local/hdrp-effects/results.xml" --timeout 1200 -- -force-d3d12
```

個別caseは完全修飾名をfilterに指定します。例: `--filter ViewWeave.GraphicsCompatibility.WindowsHdrpEffectPixelTests.FxaaChangesDiagonalEdgePixels`。これはeffect characterisation用fixtureであり、HDRP一般の保証ではありません。Explicit testのskipや未実行はPASSではありません。現在の結果にはeffect画素gateの失敗が含まれるため、[Windows日付付きfeature record](../../docs/windows-current-feature-validation-20261011.md)でrunごとの結果と境界を確認してください。fixture失敗を製品非対応の証明にしたり、全case成功と報告したりしません。

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
| `ViewWeave.MacUrpHistoryValidation.Editor.MacUrpHistoryValidationBuilder.Build` | `Assets/SamplePlayerValidationGenerated/MacUrpHistory/Validation.unity` | Bridgeを使わないmacOS MetalのURP TAA履歴試験 |

Bridge demoの視点番号検証は`ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run`です。Bridge接続なしで48 tileの番号一意性とcoverageを確認し、結果を`.local`へ保存します。実機確認ではview 0/47を左右と仮定せず、左右から実際に見えた番号を記録します。

generated sceneを作ってPlayerをBuildする場合は、Unity CLIからEditor methodへ引数を渡します。例えばURP sample Playerは次のように実行します。

```powershell
unity run "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 -- -executeMethod ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp -buildOutput "$PWD/.local/player/SampleUrp.exe" -buildGraphicsApi d3d11 -buildDevelopment true
```

builderは`-buildGraphicsApi d3d11` / `d3d12`と`-buildDevelopment true` / `false`を受け付けます。省略時はD3D11 Developmentです。例えばD3D12 Releaseでは`-buildGraphicsApi d3d12 -buildDevelopment false`を渡します。不明な値はbuild methodが失敗します。builderは`Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`を一時作成し、指定API、Linear、Run In BackgroundでPlayerをBuildした後に設定を復元します。Build後のPlayer起動にはBridge runtimeが必要です。現在のWindows Player結果と限定範囲は[日付付きfeature record](../../docs/windows-current-feature-validation-20261011.md)を参照してください。

## macOS Metalの履歴・露出・URP TAA試験

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

2026-10-11のMetal PlayMode `GraphicsCompatibility` runは58/58 PASS、skip 0でした。URP adapterの8 testを含み、4視点のhistory蓄積、view別RenderTextureと画素比較、frame versionの安定性、view削除、resize/restart、budget cleanup、scripted Volume設定の保持を確認しました。Editor PlayModeの証跡です。Metal上のbounded Edit Modeと描画専用Development／Release Playerのrunも以下に記載します。Windowsと実機光学TAAは対象外です。[URPの日付付き記録](../../docs/urp-view-history-validation-20261011.md)を参照してください。

### URP Edit ModeとPlayerの試験

Editor専用Edit Mode fixtureはMetal hostが必要です。無効化Camera、TAA history蓄積、Pause/Resume、public local-preview serviceを確認します。repository rootで実行してください。

```bash
mkdir -p "$PWD/.local/urp-history"
unity test "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --mode EditMode --filter ViewWeave.UrpHistoryEditor.UrpHistoryEditModeTests.DisabledViewsAccumulateOutsidePlayModeAndRestartAfterPause --report-format junit --output "$PWD/.local/urp-history/editmode.xml" --timeout 1200 -- -force-metal
```

macOS描画専用Playerは隔離したbatch Editorでbuildします。builderはMetalを選び、一時sceneを作ってEditorのgraphics／scene設定を復元します。まずDevelopmentを実行します。

```bash
mkdir -p "$PWD/.local/urp-history/player-development"
unity build "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 --target StandaloneOSX --execute-method ViewWeave.MacUrpHistoryValidation.Editor.MacUrpHistoryValidationBuilder.Build --output-path "$PWD/.local/urp-history/player-development/ViewWeaveUrpValidation.app" --allow-dirty-build --timeout 1200 --args '-force-metal -validationConfiguration Development'
"$PWD/.local/urp-history/player-development/ViewWeaveUrpValidation.app/Contents/MacOS/PipelineSmoke" -force-metal -screen-width 640 -screen-height 480 -validationReport "$PWD/.local/urp-history/player-development.json" -logFile "$PWD/.local/urp-history/player-development.log"
```

Releaseを試す場合は、両command内の`player-development`を`player-release`に置き換え、builderの設定を`-validationConfiguration Release`にします。Playerの終了code、JSON report、logを合わせて確認してください。reportと一緒にTAA on/offおよび移動geometryの比較に使うPNGも保存します。描画専用fixtureはBridge出力や実機光学を検証しません。条件と結果は[URPの日付付き記録](../../docs/urp-view-history-validation-20261011.md)を参照してください。

HDRP fixtureではCameraの視点別分離・維持、HDRP TAA設定の保持、Fixed exposureのcompensation、自動露出の光量追従、赤いQuilt画素とmagenta画素の不在、所有リソースの解放、1 MBのwarmup予算超過を確認します。JSONと終了codeを両方確認してください。GPU完了待ちは実時間で制限し、Cubeが測光画面の大部分を占めるようにしています。黒背景が大半のHistogramから大きなEV変化を期待することはできません。Bridge出力、実機光学、HDRP TAA画素正確性、Windowsの検証ではありません。結果と残る警告は[HDRPの日付付き記録](../../docs/hdrp-view-history-validation-20261010.md)を参照してください。

## GPU Resident Drawer validation

`GrdValidationPlayModeTests`はURP/HDRPの1、8、48 viewsを検証します。GRDのON条件はprofiler counterも確認します。結果PNGとJSONは`.local/grd-validation/`へ出力されますが、証跡ファイルだけで合否を決めず、Unity test resultとlogを確認してください。Editor結果はPlayerのthreading、長時間運転、性能を保証しません。

## PlayerとEditorの境界

通常のTest Runner実行はUnity EditorのPlayModeです。Bridge接続を使う実機Player確認は、Bridge sampleまたは上記generated sceneをBuildして別途実行します。制作時はEdit Modeで`ViewWeaveRig` previewを明示的に開始し、Editor内表示またはWindows D3D11/D3D12 Bridge経路での機器表示ができます。これは通常Sessionとは別機能で、通常SessionのBridge出力にはEditor PlayまたはPlayerが必要です。設定と対応条件は[Quilt制作ガイド](../../docs/ja/quilt-authoring.md)を参照してください。PlayMode suiteはこのEditor previewを検証しません。
