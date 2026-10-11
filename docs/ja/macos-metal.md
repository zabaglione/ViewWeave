[English](../macos-metal.md)

# macOS Metal出力

[ユーザーガイド](user-guide.md) · [技術者向けガイド](technical-guide.md)

## 利用条件と操作

Unity **6000.6.4f1**、URPまたはHDRP **17.6**、Apple Silicon Mac、Looking Glass Bridgeを使用します。同梱arm64プラグインは**macOS 26.0以降**が必要です。ViewWeaveはBridgeの校正値に基づくMetal出力経路を使います。displayを回転なし・native解像度の拡張画面に設定してBridgeを起動してください。機種識別子は対応判定のallowlistに使いませんが、すべての機器の対応を意味するものではありません。[対応表](compatibility.md)と[現行校正出力の検証記録](../macos-metal-subpixel-validation-20261011.md)で確認範囲と制約を参照してください。

SceneのCameraを選び、**GameObject > ViewWeave > Bridge Rig**を実行します。macOSでは出力を担当する`BridgeMetalQuiltOutput`が追加されます。設定の入口は**ViewWeave Rig**コンポーネントです。**Rendering > Output Quality**で画質を選び、**Bridge**欄で自動開始、runtime path、application name、display ordinal、状態表示、Start／Stopを操作します。出力はPlayで開始します。Qualityや接続設定を変更する前にStop Outputで停止し、GPU資源の回収を待ってから変更・再開してください。runtime pathが空なら`/Applications`と`~/Applications`の最新Bridgeを探索します。dylibそのもの、配置ディレクトリ、Bridgeの`.app`、`LKG_BRIDGE_PATH`も指定できます。

カメラ、Reference Camera、フォーカス、Orbit、物理フレーミング、Quilt Capture、Video Exportは共通のRig UIから操作します。Play中の接続後に**Use Connected Display Size**を使うと、物理フレーミングの有効・無効を変えずに、校正済みの画面寸法をコピーします。物理1:1では開口寸法の縦横比と機器の縦横比が一致している必要があります。Metal出力を止めると、そのRendererの通常録画・一時停止中の録画を終了し、元のローカル設定を復元します。MetalコンポーネントのInspectorは技術診断用です。

Playしていない状態でViewWeave Rigの**Preview**を開き、**Start Local Edit Mode Preview**または**Start Looking Glass Device Preview**を選びます。LocalはRigのローカルQuilt解像度・gridと取得済みの機器形状、DeviceはBridgeから取得・検証したdisplay profileと**Output Quality**を使います。**Pause Preview**は最後の画像を保持し、**Resume Preview**で再開します。終了は**Close Preview**です。Qualityや接続設定を変える前に終了し、GPU資源の回収を待ってください。専用の描画オブジェクトを使うため、元のRigやカメラの設定は保持します。Play開始、スクリプト再読み込み、Authoringウィンドウの終了・対象Rigの変更でもPreviewを終了します。

保存したQuiltを再生するには、Play中にMetal出力を開始し、**Capture & Playback > Playback**を開きます。全視点を含むQuilt PNGまたは完了したPNG連番のフォルダを選んで検証し、Play／Pause／Resume／Stopで操作します。寸法、grid、縦横比、利用可能なカメラ情報は現在の出力設定と一致する必要があります。再生中は通常のQuilt描画を停止し、正常なStopまたは連番終了時に、同じ出力とRig設定が維持されていれば復帰します。出力停止、Play終了、スクリプト再読み込みでは再生を無効化し、GPU完了が確認できるまで資源を保持します。シーンには出力コンポーネントが保存されるため、macOSとWindowsの間で移動してもバックエンドは自動交換されません。

対応判定は機種識別子ではなく、Bridgeのmetadataと校正値に基づきます。Bridgeからdisplay／Quilt geometry、aspect、viewcone、DPI、raw `center`／`pitch`／`slope`校正値を取得し、`get_ri_for_display`と`get_bi_for_display`でRGB/BGRのchannel indexを読みます。Metal経路は`CellPatternMode` 0～4を受け付けます。cell tableは最大4 cellで、mode 0は1 cell以上、mode 1／2／4は2 cell以上、mode 3は4 cell以上を要求します。tableなしはmode 0だけで、tableがあるのにJSONのmode key（`CellPatternMode`または`cellPatternMode`）が欠落している場合、または必要数未満・4超過の場合は拒否します。tableなしmode 0はRGB/BGRと、`flipX`／`invertedViews`それぞれ0または1を使えます。cell tableありのprofileは`invertedViews=1`が必要です。cellなしmode 0で`flipX=1`にすると画像X、tilt、RGB方向を反転します。cell tableありではtiltの符号だけを反転し、表示UVやRGB offset Xは鏡像化・並べ替えしません。cell tableの取得には`127.0.0.1:33334`のBridge HTTP JSON経路が必要で、tableなしmode 0はnative SDKだけを使います。未知のmode、不正なtable、非有限値、native校正値との不一致は拒否します。cell offsetはRGB各channelのraw X/Y pixel値です。`fringe`は有限値であることだけを検査し、光学補正には使いません。displayは回転なし・native解像度・拡張画面が必要です。[Bridgeサブピクセル校正の説明](../bridge-subpixel-calibration.md)にmode別index規則とmetadata取得方法を記載しています。画質は開始前にPreview／Balanced／Nativeを選びます。Bridgeが返すQuilt全gridを維持します。view数が多い場合は描画負荷が高いため、まずPreviewで確認してください。

## サブピクセル処理の選択

ViewWeave RigのInspectorにある **Subpixel Processing** で選択します。出力・機器Previewを停止し、GPU資源の回収を待ってから変更してください。

- **Automatic**（既定）: Bridgeが返す校正情報に従い、セル表があればその配列を使います。
- **ClassicRgb**: Bridgeの光学校正を使い、等間隔のRGB処理を明示指定する診断用設定です。特殊配列機器の正しい出力を保証する設定ではありません。
- **CalibratedCells**: Bridgeのセル表を必須にします。Goのように表がない場合は、代替配列へ切り替えず開始を拒否します。modeやXYオフセットは手入力せずBridgeから取得します。

`BridgeSubpixelMode`はWindows／macOSで共有する設定契約です。Windows Inspectorでも設定を保持できますが、現在のWindows出力はBridge側の自動補正を使うため、適用できるのはAutomaticだけです。手動指定は開始前に明確に拒否します。Windowsでの手動処理と特殊配列実機は未検証です。

## 実装と検証の補足

現在の一般化経路はNative CTest 7/7とv3接続・取消の3 cycleを通過しました。Built-in Linear／Gamma GPU比較は各28 cases、各64,512 samplesで通過し、最大誤差は0.001956／0.001961です。URP／HDRPのLinear GPU比較も各28 casesで通過しました。現行URP／HDRP EditorではPreview／Play、PNG保存、無音録画・再生、Stop／Restartを確認しました。URP／HDRP × Development／ReleaseのGo機器Player計4件も、描画・設定拒否・保持資源0・自然終了のgateを通過しました。これらは機種一般の光学確認ではなく、16-inch Spatial Landscapeの実機出力も未検証です。macOSは音声録画に対応しません。確定した範囲と制約は[現行校正出力の検証記録](../macos-metal-subpixel-validation-20261011.md)を参照してください。

## プラグインのビルド

`python3 tools/build_macos_plugin.py --headers /absolute/path/to/public/UnityPluginAPI/headers`を実行します。`--headers`を省略すると、公式NativeRenderingPluginの固定commitの公開ヘッダーを無視対象のbuildディレクトリへ取得します。自作shimをビルドし、native CTestを実行して、dylib・Unity importer・由来情報をBridgeパッケージへ配置します。現行経路は`open_v3`のみを使い、`LkgMetalDisplayInfoV3`は180 bytesです。最大4個のraw RGB XY cellを含み、1 cellは24 bytesです。macOSの古いコード署名情報を避けるためdylibは原子的に置換します。更新後はUnityを再起動して新しいネイティブ版を読み込んでください。`--universal`はIntel sliceもビルドしますが、Intelでの実行確認を意味しません。公式Bridgeのライブラリやヘッダーは同梱しません。公式Looking Glass Unity Plugin 3.2.8はmode数式の数学的意味を確認する参照元で、ViewWeaveは独自に実装し、vendorコードを移植・同梱していません。ソースパッケージの変更後は`python3 tools/build_upm_distribution.py`で配布パッケージを再生成します。

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

[Bridgeの公開API](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)とインストール済みruntimeの公開宣言から機器情報を取得します。producerのcommand bufferは[Unityの公開Metal interface](https://github.com/Unity-Technologies/NativeRenderingPlugin/blob/522254181faf188efa8b50c3e3bf6fce720b26e4/PluginSource/source/Unity/IUnityGraphicsMetal.h)を使用します。ビルド・GPU計算・画面capture・実機の立体表示は、それぞれ別の検証です。確認済み範囲と制約は[現行校正出力の検証記録](../macos-metal-subpixel-validation-20261011.md)を参照してください。
