# Bridgeのサブピクセル校正値

**確認日:** 2026-10-11

Unity機能ごとの現在の対応状況は[Unity機能×環境表](ja/unity-feature-platform-matrix.html)を参照してください。

Metal経路はBridgeの表示機器識別子を許可リストにせず、校正値から出力profileを作ります。機種名と`CellPatternMode`の対応表は公開資料から確認できていません。したがって、個々のBridge値を検証して出力する実装は、全機種の光学対応を意味しません。

## 校正データとmode

Bridgeの校正には`CellPatternMode`とRGB各サブピクセルのraw X/Y pixel offsetを持つ`subpixelCells`があります。現在のMetal APIは`open_v3`のみを使い、180-byteの`LkgMetalDisplayInfoV3`で受け取ります。各cellは24 bytesでRGBの6値を保持し、最大4個を含めます。

以下のmode別cell index規則は、公式Looking Glass Unity Plugin 3.2.8の数学式を照合してViewWeave側で独自に実装したものです。`x`,`y`はnative display pixel座標で、表のセル数はtableの最小必要数を示します。

| `CellPatternMode` | cell数の下限（tableあり） | native pixelから選ぶcell index |
|---:|---:|---|
| 0 | 1 | 0 |
| 1 | 2 | `(x + y) % 2` |
| 2 | 2 | `x % 2` |
| 3 | 4 | `(y + 2 * (x % 2)) % 4` |
| 4 | 2 | `y % 2` |

tableは最大4 cellです。mode 0は1 cell以上、mode 1／2／4は2 cell以上、mode 3は4 cell以上を要求します。下限未満または4を超えるtableは拒否します。JSONのmode keyは`CellPatternMode`と`cellPatternMode`を受け付け、cellがあるのにmodeが欠落している場合は拒否します。tableのない従来profileはmode 0に限ります。

cell tableのないmode 0は通常のRGB/BGR channel indexに基づく出力で、`flipX`と`invertedViews`をそれぞれ0または1にできます。`flipX=1`では画像X、tilt、RGB方向を反転します。cell tableのあるprofileは`invertedViews=1`が必要です。その`flipX=1`はtiltの符号だけを反転し、表示UVやRGB offset Xの並べ替えはしません。未知のmode、cell数の下限未満または4超過、欠落・非数のoffset、共通校正値との不一致は拒否します。displayは回転なし、native解像度、拡張画面で使います。

SDKの`get_calibration_for_display`はcell数とcell pointerを受け取りますが、公開C API説明にcaller側配列のcapacity契約がありません。このためネイティブAPIでは`cells=nullptr`で共通校正値を取得し、`get_ri_for_display`／`get_bi_for_display`からRGB/BGR indexを読みます。cell tableがある場合だけ`127.0.0.1:33334`のBridge HTTP JSON経路を使い、`PUT /enter_orchestration`、`PUT /available_output_devices`、`PUT /exit_orchestration`の順に取得・終了します。raw JSONのmode・cell配列をネイティブprofileのdisplay index、window位置、寸法、光学値などと照合し、一致しないprofileは使用しません。tableなしのmode 0はnative SDKだけを使います。

## 公開資料とライセンス境界

- 公式[Looking Glass Unity Plugin 3.2.8](https://lookingglassfactory.com/software/looking-glass-unity-plugin)の数学的なmode規則を照合したパッケージ識別SHA-256は`0c0628d6d8ea2fbfe54006392abcdfd5702d61f2bc5ab0e7329696674a8c3f31`です。公式ページはPluginとその上に作るアプリをopen sourceではなくライセンス提供としています。ViewWeaveは式の意味を参照し、vendorコードを移植・同梱していません。
- [Looking-Glass/LKG-Toolkit `Calibration.cs`（固定SHA `0b6209bfa83c8eda5dd862666d4af83bd9e1ec67`）](https://github.com/Looking-Glass/LKG-Toolkit/blob/0b6209bfa83c8eda5dd862666d4af83bd9e1ec67/Toolkit-API/Device/Calibration.cs#L93-L107)はmode 0–4を定義し、第三世代より前に作られたdisplayはmode 0と仮定すると記しています。これはToolkitの一般的な仮定で、特定モデルとのmode対応表ではありません。GitHub READMEのopen-source表現にかかわらず、同じ固定版の[LICENSE.txt](https://github.com/Looking-Glass/LKG-Toolkit/blob/0b6209bfa83c8eda5dd862666d4af83bd9e1ec67/LICENSE.txt)はLKG-Toolkit License Agreementです。OSSライセンスや再利用許可があるとは扱いません。
- [Bridge Native Function Reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)は共通校正値、`cell_pattern_mode`、cell数・pointer、RGB/BGR用index getterを記載しますが、modeに対応する機種一覧やcell配列のcapacity規則は示していません。
- [Looking-Glass/bridge-sdk-samples `bridge.h`（固定SHA `a48414a017c9a93548ccd73cff564b61c5ddcf5e`）](https://github.com/Looking-Glass/bridge-sdk-samples/blob/a48414a017c9a93548ccd73cff564b61c5ddcf5e/BridgeRuntime/bridge.h#L53-L77)と[取得ヘルパー](https://github.com/Looking-Glass/bridge-sdk-samples/blob/a48414a017c9a93548ccd73cff564b61c5ddcf5e/BridgeRuntime/bridge_utils.hpp#L1605-L1637)はcell配列を公開しますが、確認した版にリポジトリ全体のOSSライセンス表示はありません。Metal用cell shaderの実装は含まれていません。
- [Looking-Glass/looking-glass-webxr `LookingGlassConfig.ts`（固定SHA `b44ed27fea197f28fbe4cb669bc350d141a9f8b1`）](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts#L25-L50)はBridgeの`CellPatternMode`とshaderの`cfg.subpixelMode`を別値として扱います。同repoはApache-2.0ですが、参照先の`holoplay-core@0.0.11`は独自ライセンスなので、そのshaderコードは移植していません。
- [LookingGlassCoreSDK `HoloPlayShadersOpen.h`（固定SHA `5f786480e8906396a06a442a18e9091ef9e0aa74`）](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/HoloPlayCore/include/HoloPlayShadersOpen.h#L11-L57)はMIT表示の公開宣言ですが、shader本体は動的ライブラリから取得します。同版の[別shader header](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/HoloPlayCore/include/HoloPlayShaders.h#L9-L23)は非公開と明記し、repoの[LICENSE.txt](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/LICENSE.txt)はHoloPlay Core独自ライセンスです。

## 確認範囲

現在の一般化経路はNative CTest 7/7とv3接続・取消の3 cycleを通過しました。Built-inのLinear／Gamma GPU比較は各28 cases、各64,512 samplesで通過し、最大誤差はそれぞれ0.001956／0.001961（許容値0.006）です。URP／HDRPのLinear GPU比較も各28 casesで通過しました。現行URP EditorではPreview／Play、PNG保存、無音録画・再生、Stop／Restartを確認しました。これらは機種一般の光学確認ではなく、16-inch Spatial Landscapeの実機出力も未検証です。確定した範囲と境界は[Metal subpixel calibration validation record](macos-metal-subpixel-validation-20261011.md)を参照してください。

## Inspector設定の共通契約

`BridgeSubpixelMode`はAutomatic／ClassicRgb／CalibratedCellsを定義します。MetalではBridge校正の自動選択、セル表を使わない診断、セル表必須の選択として適用します。Windowsでは同じ設定をserialized fieldに保持し、現在のBridge管理出力はAutomatic以外を開始前に拒否します。Rig InspectorとEdit Mode device previewに設定を伝え、稼働中の変更を拒否します。将来のWindows手動補正がこの契約を使えるよう、enum／判定policyはRuntime/Commonに置きます。
