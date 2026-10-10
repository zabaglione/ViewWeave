[English](../sdk-feature-comparison.md)

# 公式機能との比較

> 対象読者: Unity／描画基盤の技術者。このページは本文記載の技術的な設計・実装・手順を扱います。利用者向けの導入・運用手順は[ユーザーガイド](user-guide.md)、技術文書一覧は[技術者向けガイド](technical-guide.md)を参照してください。

この表はLooking Glass Factory公式資料に載る機能とViewWeaveの実装状況を、利用者向けに整理したものです。ViewWeaveは独立した非公式SDKであり、公式SDKとのソース互換性や完全な機能互換性を保証しません。詳細は[公式機能の調査](official-sdk-feature-research.md)、各機能の操作手順は[Quilt保存](quilt-capture.md)と[カメラ調整](quilt-authoring.md)を参照してください。

| 機能 | 公式資料で確認したこと | ViewWeaveの状況 |
| --- | --- | --- |
| Quilt描画・合成 | Hologram Cameraが多視点画像をQuiltとして出力する | 専用Cameraで各視点を個別描画し、GPU上でQuiltタイルへ組み立て、Bridgeへ送る。標準のCube/Rendererに個別スクリプトは不要。現在の経路は48個のStandardRequestで、視点間の描画共有やdepth補間はしない |
| 機器profile・光学calibration | 公式SDKは機器profile/calibration情報を利用する | Bridgeから画面寸法、view cone、Quilt寸法・grid等のprofileを受け取る。Factory optical calibrationを編集するUIや独自補正系は提供しない。任意機器のprofile運用・光学品質は未検証 |
| 静止画Quilt保存 | QuiltCapture APIはQuilt PNGと中央視点の2D画像を保存できる | Quilt PNG、JSON sidecar、中央view PNGを保存。既存D3D11 URP/HDRP suite各2/2。今回の変更のDX12 fake-output suiteはURP/HDRP各1/1。Bridge保存と実機表示は未検証 |
| 動画・連番保存 | 公式資料は手動動画、時間区間、VideoPlayer同期、Pause/Resume、Take、codec/metadataを説明。Quilt画像からのH.264作成手順もある | PNG連番/Pause-Resumeと無音MP4後処理に加え、今回の変更で実URP DX12 Recorder出力3 frameを30fps H.264/yuv420pへexportするE2E 1/1 PASS（256x192、4x2/8 views、Native/Bridgeなし）。Synthetic 48-view CPU smokeとは別結果。Editor window手操作は未確認。音声統合はprobe限定、Timeline/VideoPlayer同期は未実装 |
| 描画範囲・フォーカス | Hologram Cameraのvolume/focal plane gizmo、FOV、Near/Far、focus距離を調整できる | QuiltRendererのCamera Framing、明示的focus/orbit操作、既定OFFの3D focus cursorを今回の変更で実装。static reviewとDX12 RunCameraAuthoringはPASS。Scene GUIのinteractive dragは未実測。Play中のcamera編集は対象外 |
| orbit / cursor / 再生操作 | 公式資料にorbit、drag framing、3D cursor等を記載 | Orbit/focus cursor authoringは今回の変更にありDX12 targeted test PASS。Quilt playbackはCore実装とDX12 CPU smoke PASS。さらに隔離DX12 fixtureで事前生成PNGの静止画/3枚連番を公式Bridge経路へ送り、playback body gateはPASS（1.25秒保持、Pause/Resume、3 submit/0 skipped、Ready generation維持、pending 0）。Editorは終了時にUnity.dll access violationで終了したため、自然終了gateはFAIL。48 camera live renderingは未測定。限定Editor reload probeはrecord/textureの維持とtest-only handoffまでで、shutdown orderingは未検証 |
| device registry / quality | 公式pluginはprofileに対応した機器設定を提供 | Session/Registry/Locator/Rig menuとPreview/Balanced/Nativeの品質選択を提供。一般のhotplug、複数機器、全profileは未検証 |
| 合成・再生操作 | 公式資料はQuilt表示の合成や、orbit・drag framing・3D cursorなどのカメラ操作を説明 | Bridge profileに沿ったQuilt出力。ファイル再生とcamera authoringは今回の変更で実装。Playback safety 12 scenariosは個別PASS。PNG静止画/連番の隔離Bridge playback body gateもPASSしたが、Unity正常終了gateはFAIL。interactive Scene GUIと48 camera live renderingは未実測。 |
| Audio / sound recording | 公式資料はquilt動画と音声を同期して記録・再生する | 今回の変更でWindows Editor Mono opt-in AudioRenderer経路を追加。fake backend 6/6とactual audio/legacy silent 8 cases（focused rerun後）を個別PASS。URP/HDRP Quilt録画、Pause/Resume、Stop/disable、playing/scheduled AudioSource保全を確認。Stereo captureは非対応で、Native Bridge出力とframe単位sample alignmentは未検証 |
| head tracking / depth interpolation | 4.0公開資料はdepthを用いた視点補間を説明する。公式版・機能によって条件が異なる | head tracking source/capabilityは未確定のためOFF。画像からの視点補間は未実装で、真の多視点描画を行う |
| URP/HDRP・VFX | 公式4.0公開資料はURP/Render Graphを対象としHDRP未対応を明記。透明物とVFXには視点補間上の制約 | ViewWeaveはUnity 6000.6.4f1のURP/HDRP adapterを提供。個々のfeature coverageと既知制約は[Compatibility](compatibility.md)を参照 |
| Editor / Player出力 | 公式Unity PluginがBridgeへ出力 | 通常のWindows D3D11/D3D12経路はEditor PlayとPlayer向けです。別系統として、明示的に開始する`ViewWeaveRig` Editor previewをEdit Modeで利用できます。URP/HDRP local GPU fixtureでlayout再構築とproofに基づくStop/readback、retention pending 0を確認しました。D3D11/D3D12 device fixtureでは3840x2160 display profile、7680x4320・8x6/48 view QuiltでURP/HDRPのStart/Stop/Restartを確認し、Start/Restart各1 submission/1 completion、Stop後native/retention pending 0でした。手動光学確認は保留中です。隔離hidden Editor fixtureでDomain Reload後のpreview cleanupと明示restartを確認しましたが、File > Exit quit gateの資源回収は3 probeで確認済みですが、全Editor processがteardown中に0xC0000005で終了し、正常終了は未確認です。詳細は[Graphics backends](graphics-backends.md)を参照してください。検証時baseline 4230c4cはPR #4を含み、CI 37614129521は成功。authoring/playback/Mono audioは本変更で追加。隔離DX12 fixtureでPNG playback bodyを公式Bridge経路へ送信したが、Editor終了はUnity.dll access violationでexit code -1073741819。Audio Native Bridge delivery、48 camera live render、光学実機の全視点レビューは未検証。macOS、全機器hotplugは未検証または対象外 |
| パッケージ導入 | 公式配布物とBridgeを利用 | UPMパッケージ、Rig menu、任意Samplesを用意。導入方法はREADMEを参照 |

### 保存・カメラ調整の検証状況

Unity 6000.6.4f1のD3D11 URP/HDRP PlayMode smokeは各2/2。DX12 fake-output Core smokeとURP/HDRP capture test各1/1も通過し、別途実URP DX12 Recorderから無音MP4へのE2Eが1/1通過しました。検証時baseline 4230c4cはPR #4を含みCI成功。camera authoring/playback/Mono audioは今回の変更で追加しました。Audioのfake6/6とactual/silent8 casesはfocused rerun後に個別PASSし、Playback safety 12 scenariosも個別PASSしました。隔離DX12 official Bridge fixtureでは事前生成7680x4320/8x6 PNGの静止保持/Pause/Resumeと3枚・1fps連番のbody gateがPASSし、Start/Stop各1、pending 0でした。ただしUnity Editorは終了時にUnity.dll offset 0x44383dfのアクセス違反（exit code -1073741819）となり、正常な自然終了は未合格です。これは48 camera live renderや実機表示を確認した結果ではありません。限定Editor reload probeはserialized playback recovery recordとRenderTexture referenceの維持、post-reload fence/readback、test-only handoffに限られます。

PNGの向き・中間色などの画素確認は、URP/HDRPを実行した後に診断用patternをRenderTextureへBlitして行いました。今回のD3D12 Bridge fixtureは事前生成PNGの再生であり、capture保存機能、実シーンの全48 camera render、光学品質を示す結果ではありません。Sceneビューでのギズモ直接操作、公式Bridge送信中のQuilt保存、実機表示も未検証です。詳細は[Quilt保存](quilt-capture.md)、[カメラ調整](quilt-authoring.md)、[HDRP性能記録](../resumed-hdrp-vfx-performance.md)を参照してください。

調査日: 2026-10-08（追加検証のevidenceは2026-10-07 UTC）。

### 2026-10-07 validation boundary

Central 2D capture and Recorder Pause/Resume passed the scoped DX12 fake-output URP/HDRP tests; they do not establish Bridge output. Silent MP4 export passed its fake-process suite and a real FFmpeg/ffprobe 7.0.2 CPU-only synthetic smoke. Separately, an actual URP DX12 recording-to-export test passed 1/1 for three 256x192 4x2/8-view frames at 30 fps, H.264/yuv420p, without Native or Bridge. This does not establish physical output or manual Editor-window interaction. The UI recommends the official Studio filename tag. Camera authoring passed its targeted DX12 test; playback Core CPU smoke passed, while Bridge/Native playback remains untested. See [capture guide](quilt-capture.md), [video export](../quilt-video-export.md), and [remaining-work validation](../benchmarks/remaining-work-validation-20261007.md).
