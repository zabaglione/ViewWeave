[English](../macos-metal.md)

# macOS Metal / Looking Glass Go

Unity **6000.6.4f1**、URP **17.6**、Apple Silicon Mac、Looking Glass Bridgeを使用します。同梱arm64プラグインは**macOS 26.0以降**が必要です。HDRPのmacOS実行は未検証です。GoをDesktop Modeで接続し、**1440×2560の拡張画面**として設定してBridgeを起動してください。

SceneのCameraを選び、**GameObject > ViewWeave > Bridge Rig**を実行します。macOSでは`BridgeMetalQuiltOutput`が追加されます。Playで出力が開始し、MetalコンポーネントのInspectorにStart／Stopボタンがあります。runtime pathが空なら`/Applications`と`~/Applications`の最新Bridgeを探索します。dylibそのもの、配置ディレクトリ、Bridgeの`.app`、`LKG_BRIDGE_PATH`も指定できます。

最初の実装はGoの識別子（`LKG-E`）、回転のない画面、subpixel cell tableがない通常RGB校正、cell-pattern mode 0に限定します。未対応の機器・校正・解像度はエラーとして停止します。画質は開始前にPreview／Balanced／Nativeを選びます。Goの66視点を含め、Bridgeが返す全視点のgridを維持します。描画負荷が高いため、まずPreviewで確認してください。

## プラグインのビルド

`python3 tools/build_macos_plugin.py --headers /absolute/path/to/public/UnityPluginAPI/headers`を実行します。`--headers`を省略すると、公式NativeRenderingPluginの固定commitの公開ヘッダーを無視対象のbuildディレクトリへ取得します。自作shimをビルドし、native CTestを実行して、dylib・Unity importer・由来情報をBridgeパッケージへ配置します。macOSの古いコード署名情報を避けるためdylibは原子的に置換します。更新後はUnityを再起動して新しいネイティブ版を読み込んでください。`--universal`はIntel sliceもビルドしますが、Intelでの実行確認を意味しません。公式Bridgeのライブラリやヘッダーは同梱しません。ソースパッケージの変更後は`python3 tools/build_upm_distribution.py`で配布パッケージを再生成します。

検証プロジェクトは`Tests/Metal/Unity`です。`MacOSMetalValidationBuilder.Build`で`.local/macos-validation/ViewWeaveMetal.app`を作成し、`Prepare`でEditor用Sceneを作成します。サンプルの図形が回転し、フレーム数を記録します。Rで停止／再開、Escapeで停止して終了します。生成Scene、Player、詳細ログは検証用ファイルです。

## 描画と終了処理

Bridgeから機器情報と校正を取得した後、SDKを終了します。表示ウィンドウと補正処理はViewWeaveが管理します。UnityのMetalで描画・補正し、render eventで専用Metal textureへコピーします。Unity側の完了通知後に表示queueへ渡し、表示GPUの完了後に次のコピーを許可します。使用中のフレームは間引きます。連続表示中は画像のCPU readbackを行いません。停止時のみ1 pixelのqueue-tail readbackでUnity側の完了を確認し、native側の完了と両方そろってからtextureを解放します。

Stopは受付を閉じてウィンドウを隠します。GPU処理の終了待ちはその後も続く場合があり、`IsClosing`で区別できます。前の処理が終了するまで新規開始を待ちます。保留中のeventとGPU callbackは自身のSession・textureを保持し、AppKit objectはmain threadで解放します。画面切断、位置、解像度の変更を検出したら停止します。画面構成を戻して再接続してください。

Windowsの`ViewWeaveBridgeSession`、保存Quiltの再生、Edit Modeの実機previewはWindows用です。共通・Metal・Windowsの配置と責務は[プラットフォーム分離](bridge-platform-architecture.md)を参照してください。以前の有限実験`BridgeMetalExperimentalOutput`も別経路として残ります。実験開始後のprocessをこの出力と共用せず、Unityを再起動してください。

## Play停止時のEditorエラー

`Mismatched state in DefaultScenario, was Stopping but expected Idle`は、Play中のスクリプト変更を停止時に再コンパイルする設定で発生するUnityの既知不具合です。[UUM-154172](https://issuetracker.unity.com/issues/25291/mismatch-state-in-defaultscenario-error-is-thrown-when-exiting-play-mode-with-a-modified-script-and-recompile-after-finished-playing-enabled)には6000.6.4f1での再現が記載されています。Playを止めてからソースを変更し、インポートとコンパイルの完了を待って再開してください。

公式の回避策はUnity Settings > General > Script Changes While Playingを`Recompile And Continue Playing`へ変更することです。この設定は他のプロジェクトにも影響し、Play中に再コンパイルが走るため、必要な場合だけ変更してください。

## 補正式と出典

補正shaderは独自実装です。RGB subpixelごとのレンズ位相からQuiltの視点を選択します。校正値の単位変換はApache-2.0の[公式WebXR Config](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts)にある数学式を参照しました。HoloPlay shaderのコードは移植・同梱していません。

`shaderPitch = rawPitch × screenWidth / dpi × cos(atan(1 / rawSlope))`

`tilt = screenHeight / (screenWidth × rawSlope)`、`RGB step = 1 / (3 × screenWidth)`です。

[Bridgeの公開API](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)とインストール済みruntimeの公開宣言から機器情報を取得します。producerのcommand bufferは[Unityの公開Metal interface](https://github.com/Unity-Technologies/NativeRenderingPlugin/blob/522254181faf188efa8b50c3e3bf6fce720b26e4/PluginSource/source/Unity/IUnityGraphicsMetal.h)を使用します。ビルド・GPU計算・画面capture・実機の立体表示は、それぞれ別の検証です。現時点の結果は[検証記録](../macos-metal-validation-20261010.md)を参照してください。
