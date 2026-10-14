# ViewWeave ユーザーガイド

[English](../user-guide.md)

> Unity projectでViewWeaveを利用する方向けの操作入口です。導入、Preview、カメラ構図、描画品質、Capture、再生を案内します。実装詳細、試験手順、日付を限定した証跡は[技術者向けガイド](technical-guide.md)を参照してください。[ドキュメント目次](index.md)には両方の入口と独立したライセンスリンクがあります。

## InstallしてSampleを開く

Unity 6000.6.4f1のURPまたはHDRPプロジェクトを使います。**Package Manager > + > Install package from git URL**から、パイプラインに合うpackageを一つ追加してください。

- URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#v0.1.0`
- HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#v0.1.0`

packageの**Samples**から**URP Quick Start**または**HDRP Quick Start**をImportし、その`QuickStart.unity` sceneを開きます。sceneには設定済みRigとアニメーションするサンプルobjectが含まれます。

画像付きの手順は[使い始める](getting-started.md)を参照してください。Sampleをプレビューするには**ViewWeave Bridge Rig**を選び、Editor内で見る場合は**Preview > Start Local Edit Mode Preview**、接続displayへ送る場合は**Start Looking Glass Device Preview**を選びます。機器出力には公式runtimeと対応displayが必要です。現在の条件は[対応表](compatibility.md)を確認してください。

## Sceneの構図とfocusを調整する

ViewWeave Rigを選び、Inspectorを使うか`Window > ViewWeave > Rig Authoring`を開きます。このwindowはUnityの選択を変更しても対象Rigを保持します。**Camera & Focus**からReference Camera、構図、focusを調整します。

Bridge APIで機器情報を正常に取得すると、その縦横比と視野角を紫のfocus枠・描画範囲とLocal Previewへ反映します。Windows／macOS共通で、Previewや出力の停止、Play終了、スクリプト再読み込み後も最後の取得結果を使います。保存済みsceneではEditor再起動後も保持します。履歴はプロジェクトの`UserSettings`へ保存され、Git管理には含まれません。未保存のsceneでは現在のEditorセッション内だけ保持します。Rigの出力コンポーネント、runtime path、application name、機器番号を変えた場合は、変更後の機器情報を再取得してください。機器出力の開始時にはBridgeから最新の情報を取得します。

**Focus Target**は一時的なEditor選択欄です。scene objectを設定し、**Set Focus From Target Center**をクリックすると、その中心をzero-parallax distanceとして反映します。選択したtarget自体はsceneに保存されませんが、適用したfocus distanceはsceneに保存されます。詳しくは[Quiltカメラの調整](quilt-authoring.md)を参照してください。

Edit Mode PreviewではSampleのsceneは静止しています。**Play**するとAnimatorが動き、Cubeが回転しSphereが上下します。接続displayへの出力は、メニューから作成したRigと同じく既定で自動開始します。手動で運用する場合は**Bridge > Start Output On Enable**（Metal）／**Start Session On Enable**（Windows）をOFFにし、**Start Output**で開始します。補助スクリプトはplatformの出力を選び、この起動設定を維持します。以前Importしたコピーは、編集sceneをバックアップして再Importするか、起動設定を有効にしてください。scene内容の変更や停止中に限られる操作を行う前にPlayを止めてください。

Rigの**Bridge**欄では、通常のPlay Mode出力を**Start Output**／**Stop Output**で操作し、statusを確認できます。インストール済みruntimeを自動検出できない場合だけruntime pathを指定します。これはEdit Modeのdevice Previewとは別です。**Advanced**には低レベルの設定があります。通常の利用では他のRig欄から操作してください。

## 描画負荷と解像度を選ぶ

**Rendering**の**Quilt View Render Mode**は描画する視点数を決めます。**Full**は全視点を描画し、**Half**と**Quarter**は視点数を減らしてGPU負荷を下げられます。その分、横へ動いたときの画像変化が段階的になることがあります。描画負荷より視点の連続性を優先する場合はFullを選びます。実際の性能はsceneとGPUにより異なります。

**Output Quality**は視点数とは別に、各viewの描画解像度を調整します。Preview、Balanced、Nativeの順に、接続display profileに対する解像度が大きくなります。Local Previewの解像度とgridはRigのlocal Quilt profileを使い、機器情報を取得済みの場合は縦横比と視野角をその結果に合わせます。描画が重い場合は低い設定から試し、対象displayで見え方を確認してください。確認済みの範囲は[対応表](compatibility.md)、日付付き測定は[Performance](../development/ja/performance.md)を参照してください。

HDRPでは、**Rendering > HDRP Rendering**の**Isolated View Histories**と**Quilt Exposure > Follow Scene**が既定です。描画する視点ごとに履歴を保持し、中央視点で測った露出をQuilt全体で共有します。シーンのHDRP効果設定を保ち、起動直後は有効な測光結果が届くまで待機します。露出を自分で指定する場合だけ**Manual Fixed**を選んでください。

URPでTAAを使うには、Reference Cameraの**Post Processing**を有効にし、**Anti-aliasing > Temporal Anti-aliasing (TAA)**を選びます。Rigの**Rendering > URP Rendering**は、既定の**View History Mode > Isolated View Histories**と**Follow Reference Rendering Settings**を使ってください。各視点で履歴を保持し、Reference Cameraの描画設定を反映します。構図、クリップ範囲、表示対象はRigで設定します。

TAAにはUniversal Rendererを使い、MSAA、Camera Stack、Dynamic Resolutionを無効にしてください。Post Processing有効時のVolumeのMotion Blurは、Quiltでの見え方を未検証のため出力を停止します。利用時はMotion Blurを無効にしてください。**Shared Camera Compatibility**は従来の互換用モードで、Quilt描画中はTAAを無効にします。

視点別の履歴には追加のGPUメモリが必要です。URP/HDRPとも**History Resource Budget (MB)**は4096、**Maximum History Cameras**は256が既定値で、設定は**Rig > Rendering**にあります。観測したGPUメモリの増加量、または新規・拡大した描画テクスチャの容量推定が予算を超えると出力を停止します。共有資源も含めますが、一部のバッファやドライバの使用量は計測できないため、VRAM使用量の厳密な上限ではありません。予算を増やす前にOutput Qualityを下げるかHalf／Quarterを試してください。互換モードでは効果やシーンの見え方が変わることがあります。詳細と未検証範囲は[対応表](compatibility.md)を参照してください。

## EditorまたはdisplayでPreviewする

**Preview**から**Start Local Edit Mode Preview**を選ぶとEditor内でQuiltを確認でき、**Start Looking Glass Device Preview**を選ぶと接続displayへ出力します。どちらも明示的に開始します。Local Previewにはdisplayは不要です。Device Previewには対応経路と事前条件が必要です。Preview中はPause/Resumeで停止・再開し、終わったら**Advanced > Close Preview**を使います。Previewの状態自体は保存sceneへ書き込まれません。

macOSの校正値ベースMetal出力は[macOS Metalガイド](macos-metal.md)で導入条件と制約を確認してください。他platformの組み合わせは[対応表](compatibility.md)を参照してください。

## Quiltを保存、録画、再生する

Rigの**Capture & Playback**から対象renderer用のtoolを開けます。[Quilt Capture](quilt-capture.md)で静止Quiltを保存するかPNG連番を録画します。音声も必要なら[Quilt PNGと音声の録音](quilt-recording-audio.md)を使います。WAVは別ファイルに保存されます。保存済み画像を確認するには[Quilt Playback（英語）](../quilt-playback.md)、完成した連番から無音MP4を作成するには[Quilt Video Export（英語）](../quilt-video-export.md)を参照してください。

Playbackは既に稼働中のoutputを使い、単独ではdisplay sessionを開始しません。Captureや再生を始める前に各ページの条件を確認してください。

## 任意の機能

- [VFX Graph](vfx-graph.md)に現行pipelineとcolor spaceの条件を記載しています。あらゆるgraphやeffectへの対応を保証するものではありません。
- Quick Startの個別READMEは[URP](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/QuickStartURP/README.ja.md)と[HDRP](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/QuickStartHDRP/README.ja.md)を参照してください。
- 任意の**Windows URP Lit**と**Windows HDRP Lit** SampleからLit sceneを生成できます。Quick Startとは別のSampleです。Import前に[使い始める](getting-started.md#任意のsamplesをimportする)を参照してください。個別手順は[URP Lit](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/WindowsUrpLit/README.ja.md)と[HDRP Lit](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/WindowsHdrpLit/README.ja.md)にあります。

設定時の問題やplatform選択は[対応表](compatibility.md)から確認してください。設計、診断、試験結果は[技術者向けガイド](technical-guide.md)にまとめています。
