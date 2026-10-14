[English](../macos-metal-internals.md)

# macOS Metalの実装・検証資料

導入・操作は[macOS Metalガイド](../../ja/macos-metal.md)を参照してください。

対応判定は機種識別子ではなく、Bridgeのmetadataと校正値に基づきます。Bridgeからdisplay／Quilt geometry、aspect、viewcone、DPI、raw `center`／`pitch`／`slope`校正値を取得し、`get_ri_for_display`と`get_bi_for_display`でRGB/BGRのchannel indexを読みます。Metal経路は`CellPatternMode` 0～4を受け付けます。cell tableは最大4 cellで、mode 0は1 cell以上、mode 1／2／4は2 cell以上、mode 3は4 cell以上を要求します。tableなしはmode 0だけで、tableがあるのにJSONのmode key（`CellPatternMode`または`cellPatternMode`）が欠落している場合、または必要数未満・4超過の場合は拒否します。tableなしmode 0はRGB/BGRと、`flipX`／`invertedViews`それぞれ0または1を使えます。cell tableありのprofileは`invertedViews=1`が必要です。cellなしmode 0で`flipX=1`にすると画像X、tilt、RGB方向を反転します。cell tableありではtiltの符号だけを反転し、表示UVやRGB offset Xは鏡像化・並べ替えしません。cell tableの取得には`127.0.0.1:33334`のBridge HTTP JSON経路が必要で、tableなしmode 0はnative SDKだけを使います。未知のmode、不正なtable、非有限値、native校正値との不一致は拒否します。cell offsetはRGB各channelのraw X/Y pixel値です。`fringe`は有限値であることだけを検査し、光学補正には使いません。displayは回転なし・native解像度・拡張画面が必要です。[Bridgeサブピクセル校正の説明](../bridge-subpixel-calibration.md)にmode別index規則とmetadata取得方法を記載しています。画質は開始前にPreview／Balanced／Nativeを選びます。Bridgeが返すQuilt全gridを維持します。Native品質では、余白付き寸法から完全tileの使用領域を描画寸法として導きます。元のBridge校正情報は変更しません。この寸法処理はWindowsとmacOSで共通です。view数が多い場合は描画負荷が高いため、まずPreviewで確認してください。

## 実装と検証の補足

共通校正validator・sampling shader・Metal API v4を使うarm64版を再ビルドしました。Native CTest 8/8とGoのv4取消・再開始3 cycle、UNORM／sRGB・拡縮・mode 0〜4の合成Metal GPU照合、URP／HDRPのGo回帰を確認しています。GammaでUnityがsRGB要求をUNORMへ変える形式は成功件数から除外します。特殊配列実機、16 Spatial Landscape、光学品質、Intel実行、音声録画は未確認です。検証の件数とEditor／Playerの範囲は[共通契約のmacOS検証記録](../macos-unified-subpixel-validation-20261011.md)を参照してください。

## プラグインのビルド

`python3 tools/build_macos_plugin.py --headers /absolute/path/to/public/UnityPluginAPI/headers`を実行します。`--headers`を省略すると、公式NativeRenderingPluginの固定commitの公開ヘッダーを無視対象のbuildディレクトリへ取得します。自作shimをビルドし、native CTestを実行して、dylib・Unity importer・由来情報をBridgeパッケージへ配置します。現行経路はmode引数付き`open_v4`を使い、応答の`LkgMetalDisplayInfoV3`は180 bytesです。最大4個のraw RGB XY cellを含み、1 cellは24 bytesです。macOSの古いコード署名情報を避けるためdylibは原子的に置換します。更新後はUnityを再起動して新しいネイティブ版を読み込んでください。`--universal`はIntel sliceもビルドしますが、Intelでの実行確認を意味しません。公式Bridgeのライブラリやヘッダーは同梱しません。公式Looking Glass Unity Plugin 3.2.8はmode数式の数学的意味を確認する参照元で、ViewWeaveは独自に実装し、vendorコードを移植・同梱していません。ソースパッケージの変更後は`python3 tools/build_upm_distribution.py`で配布パッケージを再生成します。

検証プロジェクトは`Tests/Metal/Unity`です。`MacOSMetalValidationBuilder.Build`で`.local/macos-validation/ViewWeaveMetal.app`を作成し、`Prepare`でEditor用Sceneを作成します。サンプルの図形が回転し、フレーム数を記録します。Rで停止／再開、Escapeで停止して終了します。生成Scene、Player、詳細ログは検証用ファイルです。

## 描画と終了処理

Bridgeから機器情報と校正を取得した後、SDKを終了します。表示ウィンドウと補正処理はViewWeaveが管理します。UnityのMetalで描画・補正し、render eventで専用Metal textureへコピーします。Unity側の完了通知後に表示queueへ渡し、表示GPUの完了後に次のコピーを許可します。使用中のフレームは間引きます。連続表示中は画像のCPU readbackを行いません。停止時のみ1 pixelのqueue-tail readbackでUnity側の完了を確認し、native側の完了と両方そろってからtextureを解放します。

Stopは受付を閉じてウィンドウを隠します。GPU処理の終了待ちはその後も続く場合があり、`IsClosing`で区別できます。前の処理が終了するまで新規開始を待ちます。保留中のeventとGPU callbackは自身のSession・textureを保持し、AppKit objectはmain threadで解放します。画面切断、位置、解像度の変更を検出したら停止します。画面構成を戻して再接続してください。

Windowsの`ViewWeaveBridgeSession`とD3D WorkerはWindows固有です。MetalのPreview／Playbackは専用コントローラーを使い、Rig UI、入力検証、Editor用producerと資源管理を共通化しています。共通・Metal・Windowsの配置と責務は[プラットフォーム分離](bridge-platform-architecture.md)を参照してください。以前の有限実験`BridgeMetalExperimentalOutput`も別経路として残ります。実験開始後のprocessをこの出力と共用せず、Unityを再起動してください。

## Play停止時のEditorエラー

`Mismatched state in DefaultScenario, was Stopping but expected Idle`は、Play中のスクリプト変更を停止時に再コンパイルする設定で発生するUnityの既知不具合です。[UUM-154172](https://issuetracker.unity.com/issues/25291/mismatch-state-in-defaultscenario-error-is-thrown-when-exiting-play-mode-with-a-modified-script-and-recompile-after-finished-playing-enabled)には6000.6.4f1での再現が記載されています。Playを止めてからソースを変更し、インポートとコンパイルの完了を待って再開してください。

公式の回避策はUnity Settings > General > Script Changes While Playingを`Recompile And Continue Playing`へ変更することです。この設定は他のプロジェクトにも影響し、Play中に再コンパイルが走るため、必要な場合だけ変更してください。

## 補正式と出典

補正shaderは独自実装です。RGB subpixelごとのレンズ位相からQuiltの視点を選択します。tableなしmode 0で`flipX=1`の場合は画像を水平反転し、tiltとRGB方向の符号を反転します。cell tableがある場合はtiltの符号だけを反転し、表示UVやRGB offset Xは鏡像化・並べ替えしません。RGB/BGRのchannel indexに応じて位相を選びます。`fringe`は有限値の検査だけに使い、補正には使いません。校正値の単位変換はApache-2.0の[公式WebXR Config](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts)を、mode別cell選択規則は[Bridgeサブピクセル校正の説明](../bridge-subpixel-calibration.md)を参照してください。HoloPlay shaderと公式Unity Pluginのコードは移植・同梱していません。

`shaderPitch = rawPitch × screenWidth / dpi × cos(atan(1 / rawSlope))`

`tilt = screenHeight / (screenWidth × rawSlope)`、`RGB step = 1 / (3 × screenWidth)`です。

[Bridgeの公開API](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)とインストール済みruntimeの公開宣言から機器情報を取得します。producerのcommand bufferは[Unityの公開Metal interface](https://github.com/Unity-Technologies/NativeRenderingPlugin/blob/522254181faf188efa8b50c3e3bf6fce720b26e4/PluginSource/source/Unity/IUnityGraphicsMetal.h)を使用します。ビルド・GPU計算・画面capture・実機の立体表示は、それぞれ別の検証です。確認済み範囲と制約は[現行校正出力の検証記録](../macos-unified-subpixel-validation-20261011.md)を参照してください。
