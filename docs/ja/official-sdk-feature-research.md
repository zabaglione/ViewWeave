[English](../official-sdk-feature-research.md)

# Looking Glass公式Unity SDKの機能調査

調査日: 2026-10-07

この資料は公式SDKと公開ドキュメントの機能を整理し、ViewWeaveで同じ用途を実現する場合の候補と未確認事項を分けて記録します。公式Pluginは現行配布物がプロプライエタリのため、公開API等を参考にViewWeaveで独自実装し、コードは転載しません。公式製品ページではPlugin 3.2.8と4.0.0 Betaが案内されています。一方、詳しい公開4.0ドキュメントは4.0.1 alpha向けです。版の違いがあるため、個別機能や既知不具合を現行Betaの挙動として一律に断定しません。

## 機能とViewWeaveでの扱い

| 公式機能 | 公式資料で確認した内容 | ViewWeaveの実装・検証状況 |
| --- | --- | --- |
| Quilt画像保存 | 3.x `QuiltCapture` APIはQuilt PNGと中央視点の2D画像を保存する。4.0公開資料も画像保存を案内する。 | PNGとJSON sidecarを保存する機能を実装。URP/HDRP D3D11 PlayMode smoke testsは各2/2。中央視点2D保存は未実装。 |
| 動画撮影 | 公式APIは手動の録画開始・終了、時間区間指定、Take、Pause/Resume、VideoPlayerとの同期やコーデック・メタデータを記載する。 | 固定simulation FPSで完成QuiltをPNG連番保存する機能を実装。10/25fpsで各3枚を確認し、URPの1〜240fpsの開始・停止後にsimulation時間が復元することを確認。MP4、音声、Timeline、VideoPlayer同期、既存録画の再開は未実装。 |
| Quiltから動画生成 | 別資料にQuilt画像をH.264動画へ符号化する手順がある。 | 保存と符号化を分離できる。Unity Recorder等による定間隔撮影も選択肢だが、RecorderはViewWeaveや公式SDKとの統合確認が別途必要。 |
| Hologram Cameraの範囲表示 | 緑のVolumeと紫のFocal Planeで描画範囲・焦点面を可視化する。Camera/Volume Modeがあり、縦FOV、Near/Far、焦点距離を調整する。 | Near/Far、左右端view、focus位置のCamera Framing設定とgizmoを実装。Camera Authoring suite通過。Sceneビュー上のhandle直接操作は未検証。focusはDOF/Volume focusとは別設定。 |
| フレーミング操作 | 範囲をドラッグして構図を調整し、orbitや3D cursor等を使う手順がある。 | 公式全入力操作への対応は未実装または未検証。 |
| 深度による視点補間 | 4.0 alpha資料に深度補間がある。透明物は深度を書かず補間が崩れる場合があり、VFX Output Particleにも設定条件がある。 | 将来の任意機能候補。既存の真の多視点描画と同等の画質・一般透明対応は未実証。 |

主な参照: [公式4.0公開資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)、[3.xからの移行資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha/migrating-from-unity-plugin-v3.x-to-v4.0)。

追加Recorder suiteは最終通過しています。PNG向き・中間色の確認は、URP/HDRPの実描画確認後に診断用patternをRenderTextureへBlitして行いました。実シーン全体の色正確性を測ったものではありません。D3D12での保存、実機での保存、Scene GUIの直接操作、48視点HDRP/VFXを含む大型シーンは未検証です。

保存形式について、Quilt PNGのファイル名には `name_qs{columns}x{rows}a{viewAspect}.png` 形式が使われ、view 0は左下タイルです。`viewAspect`はviewの投影・表示アスペクト比を示します。1タイルのpixel width/height比とは別の値です。

指定FPSのPNG連番は「各フレームをPNGとして保存する」方式で、動画エンコードとは別です。ViewWeave方式はsimulation時間を指定間隔で進め、完成済みQuiltを保存するためだけの追加RenderQuiltは行いません。同期readbackとPNG保存が描画を遅らせるため、設定FPSにwall-timeが追いつかない場合があります。その場合もsimulationの時刻間隔を保つ設計です。公式4.0公開資料では画像・動画機能が案内されますが、同資料のalpha版にはH.264失敗やギズモ挙動の版依存報告もあります。移行資料では3.xから4.0で使えなくなる機能も示されています。これらを現行Beta全体に当てはまる不具合とみなさず、利用バージョンを固定して確認します。

## 公式資料の版・互換性に関する注意

公式製品ページは4.0.0 Betaを案内します。詳細な[4.0 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)はUnity 6000.0.39〜6000.1.5とURP 17.0.4〜17.1、Render Graphを記載し、HDRP未対応としています。同資料には独自Core/URPパッケージと深度補間、透明・VFXの条件があります。旧版からの移行資料には機能差や4.0側で未対応の項目もあるため、公式の全機能を最新版で等しく利用できるとは限りません。

同ページの「2〜4倍」は公式による3.x比の性能表示であり、測定用プロジェクト、GPU、視点数、画質、VFX/Volume条件が公開資料から特定できません。ViewWeaveの48視点/HDRPで同じ向上が得られる予測には使えません。公式Pluginはプロプライエタリであり、独自実装をコードごと転載する対象ではありません。

## 参照した一次資料

- [Looking Glass Unity Plugin製品ページ（版と性能表示）](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [Unity Plugin 3.x Script Reference: QuiltCapture](https://lfdocs.lookingglassfactory.com/software/index/script-reference/quiltcapture.cs)
- [Unity Plugin 3.x Script Reference: HologramCamera](https://lfdocs.lookingglassfactory.com/software/index/script-reference/hologramcamera.cs)
- [Hologram Camera prefabと操作説明](https://lfdocs.lookingglassfactory.com/software/index/prefabs/hologram-camera)
- [Unity Plugin 4.0 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)
- [3.xから4.0への移行資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha/migrating-from-unity-plugin-v3.x-to-v4.0)
- [Quilt形式と画像レイアウト](https://lfdocs.lookingglassfactory.com/keyconcepts/quilts)
- [Quilt画像から動画を作る手順](https://lfdocs.lookingglassfactory.com/keyconcepts/quilts/quilt-video-encoding)
- [Unity Recorderの録画間隔設定](https://docs.unity3d.com/Packages/com.unity.recorder@5.1/manual/RecorderWindowRecordingProperties.html)

公式資料は2026-10-07時点で確認した公開内容です。SDKやBridgeの実機動作を個別に再現した報告ではありません。
