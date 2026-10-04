# Pipeline smoke tests

`Tests/Pipelines/Unity`は、Core、URP、HDRP、Bridgeのvalidationを実行するUnity projectです。fresh cloneではUnity Hubで開く前にrepository rootから`python tools/prepare_sample_tests.py`を実行してください。Bridge packageのSamplesをignoredな`Tests/Pipelines/Unity/Assets/Samples/ViewWeaveBridge`へcopyします。Unity Hubの`Add`からこのフォルダを開き、Unity 6000.6.4f1を選択してください。

## Test Runnerで実行する

1. Unity Editorで`Window > General > Test Runner`を開きます。
2. `PlayMode`タブを選択します。
3. `Run All`を押します。

Build Settingsには固定sceneを登録していません。PlayModeテストは実行時にfixture sceneを作り、終了時に破棄します。GPUが必要なfixtureはGPUのあるWindows環境で実行してください。Run Allには既知のVFX compatibility未通過条件が含まれるため、結果XMLで内訳を確認してください。

主なPlayModeテストクラスは次のとおりです。

| クラス | 目的 |
| --- | --- |
| `BridgeSessionValidationTests` | Session状態、timeout、completion watchdog、quality再設定のmanaged検証 |
| `SrpAdditionalValidationTests` | URP/HDRPの基本描画、MSAA、postprocess条件 |
| `QuiltRendererMsaaLifecycleTests` | 1x/2x/4x/8x、resolve、resize、disable、Deferred fallback |
| `GrdValidationPlayModeTests` | URP/HDRPのGPU Resident Drawer条件 |
| `VfxGraphicsCompatibilityTests` | URP/HDRP VFX compatibility matrix |
| `HdrpVfxPrimingValidationTests` | HDRP same-camera primingの診断 |

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

Bridge demoの視点番号検証は`ViewWeave.BridgeWorkerValidation.Editor.BridgeWorkerViewIndexDebugVerification.Run`です。Bridge接続なしで48 tileの番号一意性とcoverageを確認し、結果を`.local`へ保存します。実機確認ではview 0/47を左右と仮定せず、左右から実際に見えた番号を記録します。

generated sceneを作ってPlayerをBuildする場合は、Unity CLIからEditor methodへ引数を渡します。例えばURP sample Playerは次のように実行します。

```powershell
unity run "$PWD/Tests/Pipelines/Unity" --editor-version 6000.6.4f1 -- -executeMethod ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp -buildOutput "$PWD/.local/player/SampleUrp.exe"
```

このmethodは`Assets/SamplePlayerValidationGenerated/SamplePlayerValidation.unity`を一時作成し、Direct3D11、Linear、Run In Backgroundを設定してPlayerをBuildします。Build後のPlayer起動にはBridge runtimeが必要です。

## GPU Resident Drawer validation

`GrdValidationPlayModeTests`はURP/HDRPの1、8、48 viewsを検証します。GRDのON条件はprofiler counterも確認します。結果PNGとJSONは`.local/grd-validation/`へ出力されますが、証跡ファイルだけで合否を決めず、Unity test resultとlogを確認してください。Editor結果はPlayerのthreading、長時間運転、性能を保証しません。

## PlayerとEditorの境界

通常のTest Runner実行はUnity EditorのPlayModeです。Bridge接続を使う実機Player確認は、Bridge sampleまたは上記generated sceneをBuildして別途実行します。EditorからBridgeへ接続する手順はありません。
