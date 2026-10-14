# Bridgeのサブピクセル校正値

**確認日:** 2026-10-11

Unity機能ごとの現在の対応状況は[Unity機能×環境表](../ja/unity-feature-platform-matrix.html)を参照してください。

## 現在の共通出力契約

`BridgeSubpixelMode`は出力方法の選択で、`Automatic=0`を既定値とします。`ClassicRgb=1`と`CalibratedCells=2`は明示選択時だけ使います。これは下記のBridge `CellPatternMode`（hardware calibration pattern）の0〜4とは別のenumです。Automaticは有効な校正cell tableがあればcell補正を使い、tableがなければBridgeの`ri`／`bi` channel indexによる標準RGB/BGR出力を使います。Classic RGBは均等なRGBを強制し、cell取得を必要としません。Calibrated Cellsは実在・検証済みのcell tableを必須とし、取得できなければ開始を失敗させます。機種名のallowlistは使いません。

Windows WorkerはAutomaticを含む全modeをViewWeaveのNative D3D11 interlacerで処理します。D3D12 Unity producerも同じGPU上のD3D11 workerへquiltsを渡します。別経路のLegacy DirectはSDK-managed出力のままです。active preview／Session中はmode変更を拒否します。Windows Native Release CTest 19/19、D3D11 WARP shader oracle 25 casesと校正boundary 6件、Managed/UIの16/16 EditMode、9/9 PlayMode、module enumerator 25/25、guard/discovery 46/46、実機Editorの全12 mode×pipeline×API条件がPASSしました。各実機fixtureはmode status、Native D3D11 backend、render/present counter進行、Preview-to-Play lifecycleとcleanupを確認します。これは検証時の未commit作業treeと指定RTX 3090 Ti機器条件に限られ、光学品質やWindows Playerを証明しません。詳細は[Windows subpixel validation](windows-subpixel-validation-20261011.md)に記録します。

sampling/color実装はNative WARP pixel oracle 25 casesと6校正境界で確認しました。macOSのshader samplingは元の入力`uv`にtexel-center offsetを加えて`tex2Dlod`でsampleし、native最終presentではUVを変更しません。Windowsは既存producer transferのY反転後にNative側でsource寸法のquiltをraw-copyし、physical outputへの最終bilinear scaleを1回だけ行います。共通shaderはLinear／Gamma contentと、source textureの実際のUNORM／sRGB encodingを明示的に扱います。Windows Editor device fixtureでも3 modeすべてのrender/present counter進行を確認しましたが、人手光学評価と全viewの画質は未確認です。新しいmacOS source／shaderは[後続の共通契約検証](macos-unified-subpixel-validation-20261011.md)で確認しています。aspect値はmacOS SDK display profileと同じ定義へ揃えます。

共通入力gateは実texture寸法と`QuiltLayout`寸法の一致、正の完全grid、grid各軸4096以下、paddingなしの割り切れる寸法、実gridから導くtile寸法一致、有限かつ正のview aspectを要求します。部分view countや空layoutを受け付けません。9件のManaged contract PlayMode suiteで非既定15x10 / 3x2 / 6-view layoutの受理、寸法不一致・padding・partial count・default layoutの拒否を確認しました。これは入力規則の検証です。Metal runtime／shaderは別の[macOS実行記録](macos-unified-subpixel-validation-20261011.md)で確認しています。Native品質の描画profileは両OS共通でraw Bridge寸法の完全tile使用領域から導きます（4096×4096 / 8×6 → 4096×4092）。元のBridge profileと光学校正は保持し、Preview／Balancedの縮小規則は維持します。

macOSは共通Native校正validatorとsampling shaderを使い、mode引数付き`open_v4`を必須とします。arm64 shimを再ビルドし、Native CTest 8/8、合成Metal GPU、Goのv4取消・再開始とURP／HDRP回帰を実行しました。旧v3の測定は過去のsource／binaryの証拠として分けます。件数・範囲・失敗は[macOS共通契約検証](macos-unified-subpixel-validation-20261011.md)に記録しています。

Metal経路はBridgeの表示機器識別子を許可リストにせず、校正値から出力profileを作ります。機種名と`CellPatternMode`の対応表は公開資料から確認できていません。したがって、個々のBridge値を検証して出力する実装は、全機種の光学対応を意味しません。

## 校正データとmode

Bridgeの校正には`CellPatternMode`とRGB各サブピクセルのraw X/Y pixel offsetを持つ`subpixelCells`があります。現行macOS経路はmode引数付き`open_v4`と180-byte応答`LkgMetalDisplayInfoV3`を使います。各cellは24 bytesでRGBの6値を保持し、最大4個を含めます。

以下のmode別cell index規則は、公式Looking Glass Unity Plugin 3.2.8の数学式を照合してViewWeave側で独自に実装したものです。`x`,`y`はnative display pixel座標で、表のセル数はtableの最小必要数を示します。

| `CellPatternMode` | cell数の下限（tableあり） | native pixelから選ぶcell index |
|---:|---:|---|
| 0 | 1 | 0 |
| 1 | 2 | `(x + y) % 2` |
| 2 | 2 | `x % 2` |
| 3 | 4 | `(y + 2 * (x % 2)) % 4` |
| 4 | 2 | `y % 2` |

tableは最大4 cellです。Bridge `CellPatternMode` 0は1 cell以上、1／2／4は2 cell以上、3は4 cell以上を要求します。下限未満または4を超えるtableは拒否します。JSONのmode keyは`CellPatternMode`と`cellPatternMode`を受け付け、cellがあるのにmodeが欠落している場合は拒否します。cell tableがないprofileではBridge pattern mode 0だけが許可されます。このpattern modeは`BridgeSubpixelMode.Automatic`／`ClassicRgb`／`CalibratedCells`とは異なります。

cell tableのないmode 0は通常のRGB/BGR channel indexに基づく出力で、`flipX`と`invertedViews`をそれぞれ0または1にできます。`flipX=1`では画像X、tilt、RGB方向を反転します。cell tableのあるprofileは`invertedViews=1`が必要です。その`flipX=1`はtiltの符号だけを反転し、表示UVやRGB offset Xの並べ替えはしません。未知のmode、cell数の下限未満または4超過、欠落・非数のoffset、共通校正値との不一致は拒否します。displayは回転なし、native解像度、拡張画面で使います。

SDKの`get_calibration_for_display`はcell数とcell pointerを受け取りますが、公開C API説明にcaller側配列のcapacity契約がありません。このためネイティブAPIでは`cells=nullptr`で共通校正値を取得し、`get_ri_for_display`／`get_bi_for_display`からRGB/BGR indexを読みます。cell tableがある場合だけ`127.0.0.1:33334`のBridge HTTP JSON経路を使い、`PUT /enter_orchestration`、`PUT /available_output_devices`、`PUT /exit_orchestration`の順に取得・終了します。raw JSONのmode・cell配列をネイティブprofileのdisplay index、window位置、寸法、光学値などと照合し、一致しないprofileは使用しません。tableなしのmode 0はnative SDKだけを使います。

## 公開資料とライセンス境界

- 公式[Looking Glass Unity Plugin 3.2.8](https://lookingglassfactory.com/software/looking-glass-unity-plugin)の数学的なmode規則を照合したパッケージ識別SHA-256は`0c0628d6d8ea2fbfe54006392abcdfd5702d61f2bc5ab0e7329696674a8c3f31`です。公式ページはPluginとその上に作るアプリをopen sourceではなくライセンス提供としています。ViewWeaveは式の意味を参照し、vendorコードを移植・同梱していません。
- [Looking-Glass/LKG-Toolkit `Calibration.cs`（固定SHA `0b6209bfa83c8eda5dd862666d4af83bd9e1ec67`）](https://github.com/Looking-Glass/LKG-Toolkit/blob/0b6209bfa83c8eda5dd862666d4af83bd9e1ec67/Toolkit-API/Device/Calibration.cs#L93-L107)はmode 0–4を定義し、第三世代より前に作られたdisplayはmode 0と仮定すると記しています。これはToolkitの一般的な仮定で、特定モデルとのmode対応表ではありません。GitHub READMEのopen-source表現にかかわらず、同じ固定版の[LICENSE.txt](https://github.com/Looking-Glass/LKG-Toolkit/blob/0b6209bfa83c8eda5dd862666d4af83bd9e1ec67/LICENSE.txt)はLKG-Toolkit License Agreementです。OSSライセンスや再利用許可があるとは扱いません。
- [Bridge Native Function Reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)は共通校正値、`cell_pattern_mode`、cell数・pointer、RGB/BGR用index getterを記載しますが、modeに対応する機種一覧やcell配列のcapacity規則は示していません。
- [Looking-Glass/bridge-sdk-samples `bridge.h`（固定SHA `a48414a017c9a93548ccd73cff564b61c5ddcf5e`）](https://github.com/Looking-Glass/bridge-sdk-samples/blob/a48414a017c9a93548ccd73cff564b61c5ddcf5e/BridgeRuntime/bridge.h#L53-L77)と[取得ヘルパー](https://github.com/Looking-Glass/bridge-sdk-samples/blob/a48414a017c9a93548ccd73cff564b61c5ddcf5e/BridgeRuntime/bridge_utils.hpp#L1605-L1637)はcell配列を公開しますが、確認した版にリポジトリ全体のOSSライセンス表示はありません。Metal用cell shaderの実装は含まれていません。
- [Looking-Glass/looking-glass-webxr `LookingGlassConfig.ts`（固定SHA `b44ed27fea197f28fbe4cb669bc350d141a9f8b1`）](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts#L25-L50)はBridgeの`CellPatternMode`とshaderの`cfg.subpixelMode`を別値として扱います。同repoはApache-2.0ですが、参照先の`holoplay-core@0.0.11`は独自ライセンスなので、そのshaderコードは移植していません。
- [LookingGlassCoreSDK `HoloPlayShadersOpen.h`（固定SHA `5f786480e8906396a06a442a18e9091ef9e0aa74`）](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/HoloPlayCore/include/HoloPlayShadersOpen.h#L11-L57)はMIT表示の公開宣言ですが、shader本体は動的ライブラリから取得します。同版の[別shader header](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/HoloPlayCore/include/HoloPlayShaders.h#L9-L23)は非公開と明記し、repoの[LICENSE.txt](https://github.com/Looking-Glass/LookingGlassCoreSDK/blob/5f786480e8906396a06a442a18e9091ef9e0aa74/LICENSE.txt)はHoloPlay Core独自ライセンスです。

## 旧macOS実装の確認範囲（歴史的結果）

旧一般化経路はNative CTest 7/7とv3接続・取消の3 cycleを通過しました。Built-inのLinear／Gamma GPU比較は各28 cases、各64,512 samplesで通過し、最大誤差はそれぞれ0.001956／0.001961（許容値0.006）です。URP／HDRPのLinear GPU比較も各28 casesで通過しました。旧URP EditorではPreview／Play、PNG保存、無音録画・再生、Stop／Restartを確認しました。これらは旧コード／binaryに対する歴史的結果であり、新しいshared shader、`open_v4`、共通校正経路の検証ではありません。機種一般の光学確認ではなく、16-inch Spatial Landscapeの実機出力も未検証です。旧記録の正確な範囲は[Metal subpixel calibration validation record](macos-metal-subpixel-validation-20261011.md)を参照してください。

## Inspector設定の共通契約

`BridgeSubpixelMode`の3値はRig InspectorとSession／output設定で共有され、mode未指定のserialized objectはAutomaticになります。Windows Workerは3値をNative D3D11 interlacerへ渡す実装済みで、Unity Editorの指定device fixtureでは12条件を限定確認しました。Legacy Directは独立したSDK-managed pathです。macOSも同じmode意味を`open_v4`で処理します。GoではAutomatic／ClassicRgbの出力と、セル表なしCalibratedCellsの開始拒否を確認しています。共通UIは稼働中のmode変更を拒否します。
