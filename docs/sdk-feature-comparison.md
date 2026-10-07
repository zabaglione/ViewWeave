# 公式機能との比較

この表はLooking Glass Factory公式資料に載る機能とViewWeaveの実装状況を、利用者向けに整理したものです。ViewWeaveは独立した非公式SDKであり、公式SDKとのソース互換性や完全な機能互換性を保証しません。詳細は[公式機能の調査](official-sdk-feature-research.md)、各機能の操作手順は[Quilt保存](quilt-capture.md)と[カメラ調整](quilt-authoring.md)を参照してください。

| 機能 | 公式資料で確認したこと | ViewWeaveの状況 |
| --- | --- | --- |
| Quilt描画・合成 | Hologram Cameraが多視点画像をQuiltとして出力する | 専用Cameraで各視点を個別描画し、GPU上でQuiltタイルへ組み立て、Bridgeへ送る。標準のCube/Rendererに個別スクリプトは不要。現在の経路は48個のStandardRequestで、視点間の描画共有やdepth補間はしない |
| 機器profile・光学calibration | 公式SDKは機器profile/calibration情報を利用する | Bridgeから画面寸法、view cone、Quilt寸法・grid等のprofileを受け取る。Factory optical calibrationを編集するUIや独自補正系は提供しない。任意機器のprofile運用・光学品質は未検証 |
| 静止画Quilt保存 | QuiltCapture APIはQuilt PNGと中央視点の2D画像を保存できる | Quilt PNG、JSON sidecar、中央view PNGを保存。既存D3D11 URP/HDRP suite各2/2。現在の作業branchのDX12 fake-output suiteはURP/HDRP各1/1。Bridge保存と実機表示は未検証 |
| 動画・連番保存 | 公式資料は手動動画、時間区間、VideoPlayer同期、Pause/Resume、Take、codec/metadataを説明。Quilt画像からのH.264作成手順もある | PNG連番とRecorder Pause/Resumeは実装済み。10/25fps各3枚、URP 1〜240fpsの開始/停止とsimulation時間復元を確認し、DX12 fake-output suiteでPause/Resumeも確認。無音MP4後処理exporterのfake-process suiteとFFmpeg/ffprobe 7.0.2 CPU-only smokeは合格（合成PNG 3枚）。実Quilt recordingからexportまでのend-to-endとEditor window操作は未確認。音声、Timeline、VideoPlayer同期、Quilt playbackは未実装 |
| 描画範囲・フォーカス | Hologram Cameraのvolume/focal plane gizmo、FOV、Near/Far、focus距離を調整できる | QuiltRendererのCamera FramingとNear/Far/左右端view表示を実装。Camera Authoring suite通過。Scene GUIハンドルの直接ドラッグは未検証。元source Cameraのposeは保持し、録画中の移動は許可 |
| orbit / cursor / 再生操作 | 公式資料にorbit、drag framing、3D cursor等を記載 | 同じ操作系は未実装。Quilt Capture窓は最新Quilt確認と静止画保存を行い、Quiltファイルの読込・再生機能はない |
| device registry / quality | 公式pluginはprofileに対応した機器設定を提供 | Session/Registry/Locator/Rig menuとPreview/Balanced/Nativeの品質選択を提供。一般のhotplug、複数機器、全profileは未検証 |
| 合成・再生操作 | 公式資料はQuilt表示の合成や、orbit・drag framing・3D cursorなどのカメラ操作を説明 | Bridge profileに沿ったQuilt出力を行う。Quiltファイルの読込・再生やorbit/cursor操作は未実装 |
| head tracking / depth interpolation | 4.0公開資料はdepthを用いた視点補間を説明する。公式版・機能によって条件が異なる | head trackingと画像からの視点補間は未実装。真の多視点描画を行う |
| URP/HDRP・VFX | 公式4.0公開資料はURP/Render Graphを対象としHDRP未対応を明記。透明物とVFXには視点補間上の制約 | ViewWeaveはUnity 6000.6.4f1のURP/HDRP adapterを提供。個々のfeature coverageと既知制約は[Compatibility](compatibility.md)を参照 |
| Editor / Player出力 | 公式Unity PluginがBridgeへ出力 | Windows D3D11/D3D12のEditor PlayとPlayer向け経路あり。captureはD3D11と今回のDX12 fake-output Editor suiteで限定確認済み。公式Bridge captureと光学実機の全視点レビューは未検証。GitHub main反映は別工程。Edit Mode、macOS、全機器hotplugは未検証または対象外 |
| パッケージ導入 | 公式配布物とBridgeを利用 | UPMパッケージ、Rig menu、任意Samplesを用意。導入方法はREADMEを参照 |

### 保存・カメラ調整の検証状況

Unity 6000.6.4f1、既存D3D11 URP/HDRP PlayMode smoke testは各2/2通過しています。今回の変更ではDX12 fake-outputのCore smokeとURP/HDRP capture test各1/1も通過しました。これはBridge outputやGitHub mainの検証ではありません。PNG連番10/25fps各3枚、URP 1〜240fpsの開始・停止、simulation時間復元を別suiteで確認しています。

PNGの向き・中間色などの画素確認は、URP/HDRPを実行した後に診断用patternをRenderTextureへBlitして行いました。実シーン全体の色精度や全48視点の光学品質を示す結果ではありません。Sceneビューでのギズモ直接操作、公式BridgeによるDX12保存、実機表示も未検証です。詳細は[Quilt保存](quilt-capture.md)、[カメラ調整](quilt-authoring.md)、[HDRP性能記録](resumed-hdrp-vfx-performance.md)を参照してください。

調査日: 2026-10-07。

### 2026-10-07 validation boundary

Central 2D capture and Recorder Pause/Resume passed the scoped DX12 fake-output URP/HDRP tests; they do not establish Bridge output. Silent MP4 export passed its fake-process suite and a real FFmpeg/ffprobe 7.0.2 CPU-only smoke using three synthetic quilt PNGs, full layout sidecar, Unicode/space paths, and no-overwrite validation. It did not render or export a Unity quilt; real recording-to-export end-to-end and Editor-window interaction remain unverified. The UI recommends the official Studio filename tag. See [capture guide](quilt-capture.md), [video export](quilt-video-export.md), and [remaining-work validation](benchmarks/remaining-work-validation-20261007.md).
