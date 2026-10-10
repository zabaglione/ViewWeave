# Bridgeのサブピクセル校正値と公開資料の範囲

**確認日:** 2026-10-11

Unity機能ごとの現在の対応状況は[Unity機能×環境表](ja/unity-feature-platform-matrix.html)を参照してください。この文書は、Bridgeのセル校正値と公開シェーダー資料から確認できる範囲を記録します。

## 確認できたこと

Bridgeの公開ヘッダーは、`LKGCalibration` に `cell_pattern_mode` とサブピクセルセル配列を持ち、各セルは赤・緑・青サブピクセルのX/Yオフセットを表します。Bridgeサンプルにはセル数と配列を取得するコードもあります。これは校正メタデータを取得できる根拠であり、特定の表示機種とmodeの対応や、modeごとの光学出力を保証するレンダラーの根拠ではありません。

ViewWeaveの現在のmacOS Metal校正処理は、[`mode=0`かつセル数0を受け付け、nonzero modeまたはセル配列をエラーにします](../Native/platform/macos/metal_calibration.cpp#L35-L39)。出力側は通常のBridge光学値とRGB/BGR channel indexを使うViewWeave独自shaderで、Bridgeのセル表を扱うvendor optical consumerではありません。

## 公開実装とライセンス境界

- [Looking-Glass/bridge-sdk-samples `bridge.h`（固定SHA `a48414a017c9a93548ccd73cff564b61c5ddcf5e`）](https://github.com/Looking-Glass/bridge-sdk-samples/blob/a48414a017c9a93548ccd73cff564b61c5ddcf5e/BridgeRuntime/bridge.h#L53-L77) は校正構造体を定義し、[取得ヘルパー](https://github.com/Looking-Glass/bridge-sdk-samples/blob/a48414a017c9a93548ccd73cff564b61c5ddcf5e/BridgeRuntime/bridge_utils.hpp#L1605-L1637) はセル数と値を読み取ります。確認した[リポジトリ版](https://github.com/Looking-Glass/bridge-sdk-samples/tree/a48414a017c9a93548ccd73cff564b61c5ddcf5e)にはプロジェクト全体を対象とするライセンス表示が見当たらず、BridgeRuntimeコードの再利用条件は確認できません。Metal側のサンプルにもセル配列を使うshader実装はありません。
- [Looking-Glass/looking-glass-webxr `LookingGlassConfig.ts`（固定SHA `b44ed27fea197f28fbe4cb669bc350d141a9f8b1`）](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts#L25-L50) はBridge校正の `CellPatternMode` とRGBのXY値を別フィールドとして定義します。一方、同版の[設定とshader呼び出し](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts#L172-L235) および[shader bundle](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/dist/bundle/webxr.js#L6751-L6826) では、shaderの `cfg.subpixelMode` は `CellPatternMode` とは別の設定です。リポジトリはApache-2.0 ([固定版LICENSE](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/LICENSE))ですが、shader依存の[`holoplay-core@0.0.11`](https://registry.npmjs.org/holoplay-core/-/holoplay-core-0.0.11.tgz) は独自のHoloPlay Core License Agreementです。WebXRリポジトリのライセンスは依存物のライセンスを変更しないため、そのshader実装は移植していません。
- [LookingGlassCoreSDK `HoloPlayShadersOpen.h`（固定SHA `5f786480e8906396a06a442a18e9091ef9e0aa74`）](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/HoloPlayCore/include/HoloPlayShadersOpen.h#L11-L57) はMIT表示の公開ヘッダーですが、shader文字列を動的ライブラリから得る宣言です。同版の[別shaderヘッダー](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/HoloPlayCore/include/HoloPlayShaders.h#L9-L23) は非公開と明記し、リポジトリの[LICENSE.txt](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/LICENSE.txt) はHoloPlay Core独自ライセンスです。確認した古典shader資料にはBridgeのセルmode/table実装はありません。

## 未確定事項と次に必要な証拠

これらの一次資料から、**機種名と `cell_pattern_mode` / セル数の対応表は確認できません**。したがって、特定modeを旧機種・現行機種専用と判定したり、Bridge値だけで任意modeの出力対応を主張したりできません。WebXRの `cfg.subpixelMode` をBridgeの `CellPatternMode` と同一視することもできません。

対応を調べるには、対象ごとに表示機種のモデル識別情報、Bridgeのバージョン、`cell_pattern_mode`、セル数、RGB各XY値を記録し、同じ構成でパネル上の光学出力を確認する必要があります。複数機種を試験する場合も、観測結果を機種別に記録し、測定前に他機種へ一般化しません。
