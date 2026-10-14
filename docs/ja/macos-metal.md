[English](../macos-metal.md)

# macOS Metal出力

[ユーザーガイド](user-guide.md) · [技術者向けガイド](technical-guide.md)

## 利用条件と操作

Unity **6000.6.4f1**、URPまたはHDRP **17.6**、Apple Silicon Mac、Looking Glass Bridgeを使用します。同梱arm64プラグインは**macOS 26.0以降**が必要です。ViewWeaveはBridgeの校正値に基づくMetal出力経路を使います。displayを回転なし・native解像度の拡張画面に設定してBridgeを起動してください。機種識別子は対応判定のallowlistに使いませんが、すべての機器の対応を意味するものではありません。[対応表](compatibility.md)と[現行校正出力の検証記録](../development/macos-unified-subpixel-validation-20261011.md)で確認範囲と制約を参照してください。

SceneのCameraを選び、**GameObject > ViewWeave > Bridge Rig**を実行します。macOSでは出力を担当する`BridgeMetalQuiltOutput`が追加されます。設定の入口は**ViewWeave Rig**コンポーネントです。**Rendering > Output Quality**で画質を選び、**Bridge**欄で自動開始、runtime path、application name、display ordinal、状態表示、Start／Stopを操作します。出力はPlayで開始します。Qualityや接続設定を変更する前にStop Outputで停止し、GPU資源の回収を待ってから変更・再開してください。runtime pathが空なら`/Applications`と`~/Applications`の最新Bridgeを探索します。dylibそのもの、配置ディレクトリ、Bridgeの`.app`、`LKG_BRIDGE_PATH`も指定できます。

カメラ、Reference Camera、フォーカス、Orbit、物理フレーミング、Quilt Capture、Video Exportは共通のRig UIから操作します。Play中の接続後に**Use Connected Display Size**を使うと、物理フレーミングの有効・無効を変えずに、校正済みの画面寸法をコピーします。物理1:1では開口寸法の縦横比と機器の縦横比が一致している必要があります。Metal出力を止めると、そのRendererの通常録画・一時停止中の録画を終了し、元のローカル設定を復元します。MetalコンポーネントのInspectorは技術診断用です。

Playしていない状態でViewWeave Rigの**Preview**を開き、**Start Local Edit Mode Preview**または**Start Looking Glass Device Preview**を選びます。LocalはRigのローカルQuilt解像度・gridと取得済みの機器形状、DeviceはBridgeから取得・検証したdisplay profileと**Output Quality**を使います。**Pause Preview**は最後の画像を保持し、**Resume Preview**で再開します。終了は**Close Preview**です。Qualityや接続設定を変える前に終了し、GPU資源の回収を待ってください。専用の描画オブジェクトを使うため、元のRigやカメラの設定は保持します。Play開始、スクリプト再読み込み、Authoringウィンドウの終了・対象Rigの変更でもPreviewを終了します。

保存したQuiltを再生するには、Play中にMetal出力を開始し、**Capture & Playback > Playback**を開きます。全視点を含むQuilt PNGまたは完了したPNG連番のフォルダを選んで検証し、Play／Pause／Resume／Stopで操作します。寸法、grid、縦横比、利用可能なカメラ情報は現在の出力設定と一致する必要があります。再生中は通常のQuilt描画を停止し、正常なStopまたは連番終了時に、同じ出力とRig設定が維持されていれば復帰します。出力停止、Play終了、スクリプト再読み込みでは再生を無効化し、GPU完了が確認できるまで資源を保持します。シーンには出力コンポーネントが保存されるため、macOSとWindowsの間で移動してもバックエンドは自動交換されません。

対応判定はBridgeのgeometry・校正値に基づきます。標準RGB/BGR順序と対応するcell形式が必要です。受け付けるmode・cell数・反転条件は[校正仕様](../development/bridge-subpixel-calibration.md)を参照してください。機種名による制限はありませんが、未検証のdisplayの光学品質は保証しません。

## サブピクセル処理の選択

ViewWeave RigのInspectorにある **Subpixel Processing** で選択します。出力・機器Previewを停止し、GPU資源の回収を待ってから変更してください。

- **Automatic**（既定）: Bridgeが返す校正情報に従い、セル表があればその配列を使います。
- **ClassicRgb**: Bridgeの光学校正を使い、等間隔のRGB処理を明示指定する診断用設定です。特殊配列機器の正しい出力を保証する設定ではありません。
- **CalibratedCells**: Bridgeのセル表を必須にします。Goのように表がない場合は、代替配列へ切り替えず開始を拒否します。modeやXYオフセットは手入力せずBridgeから取得します。

`BridgeSubpixelMode`はWindows／macOSで共有します。Windows Workerも3設定をNative interlacerで処理します。Legacy DirectはAutomaticのみです。稼働中の設定変更は両OSで拒否します。特殊配列の実機・光学確認は、OSと機器ごとの検証記録を参照してください。

## Play停止時のEditorエラー

`Mismatched state in DefaultScenario, was Stopping but expected Idle`は、Play中のスクリプト変更を停止時に再コンパイルする設定で発生するUnityの既知不具合です。[UUM-154172](https://issuetracker.unity.com/issues/25291/mismatch-state-in-defaultscenario-error-is-thrown-when-exiting-play-mode-with-a-modified-script-and-recompile-after-finished-playing-enabled)には6000.6.4f1での再現が記載されています。Playを止めてからソースを変更し、インポートとコンパイルの完了を待って再開してください。

公式の回避策はUnity Settings > General > Script Changes While Playingを`Recompile And Continue Playing`へ変更することです。この設定は他のプロジェクトにも影響し、Play中に再コンパイルが走るため、必要な場合だけ変更してください。

## 配布と検証範囲

同梱shimはad hoc署名です。Developer ID署名・公証・利用者環境のGatekeeper受入は未検証です。音声録音は非対応です。現在の確認条件は[対応表](compatibility.md)と[機能別対応表](unity-feature-platform-matrix.html)、build・校正計算・GPU資源の終了処理は[実装・検証資料](../development/ja/macos-metal-internals.md)を参照してください。
