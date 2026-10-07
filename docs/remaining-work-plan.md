# 残作業と受け入れ計画

基準日: 2026-10-07。対象はUnity 6000.6.4f1、Windows、URP/HDRP、公式Looking Glass Bridge経路です。現在の作業branch、main、限定fixture、実機表示の結果を分けます。作業は完了条件を満たす証拠が揃うまで未完了です。

## 実行順

| 順 | 作業 | 現状 | 完了条件 |
| --- | --- | --- | --- |
| 1 | DX12 captureの独立検証と実sceneの48視点profile | Central 2D capture、Recorder Pause/Resume、静音MP4後処理は実装済み。DX12のCore smoke、URP/HDRP capture各1/1、FFmpeg 7.0.2の3-frame CPU smokeを確認。MP4 fake-process検証は通過したが、実録画からexportまでのend-to-endとEditor window操作、Bridge送信は未確認。ユーザーHDRP EditorはGraphics Jobs Split Threadingを使用中。48視点の実scene性能改善は未達。 | 完成Quilt/連番のpixel・sidecar、simulation時間とcamera状態の復元をDX12で確認し、Bridge stop、pending、natural exitも確認する。実scene profilingは同一pipeline、scene、画質、view数、解像度、Bridge状態で複数回測り、CPU frame、GPU frame、submit、dropを分離して画像品質を維持した改善を示す。実効Graphics Threading Modeを記録する。 |
| 2 | Bridge終了時のaccess violationと資源推移 | URP Editor normal-stopと過去HDRP終了でbridge_inprocの0xC0000005を記録。Native image guardのUnity PlayMode suiteは6/6 passし、多重shim imageを予防するが、vendor crashの原因や修正は証明していない。固定RTを5回送る別のfull-size shutdown contractは自然終了したが48 camera/VFXを描画していない。 | trace有効の隔離実sceneを一度だけ実行し、描画のみ、Bridge接続、Auto Render停止、Stop/drainのOS private bytes、working set、thread ID、handle、warning、pendingを同一時系列で記録する。恒久worker threadと累積thread生成を区別する。crash/timeout時に自動再試行せず証拠を保全する。短時間成功だけでleak-freeやvendor内解放完了と結論しない。 |
| 3 | 測定可能な描画改善とNativeの原因限定修正 | HDRP static fixtureではFull/Half/Quarterを測定済み。Private codex/urp-volume-reuseの7 PlayMode testは通過したがGarden A/B/Aの改善は計測誤差を超えず、mainへ採用していない。Natural Camera Batchの試作は遅くなりEditor終了に至ったため再試行しない。 | 実効threadingが既にSplitならthreading切替仮説を終了する。次候補は必要時のみA/B/A。HDRP Spot/Point Shadow OnDemandとRequestShadowMapRenderingは別の最小fixtureで評価し、動く遮蔽物、view端、light入切、複数Cameraを含む全48画像と計測誤差を上回る改善、drop条件を満たす場合だけ採用する。Directional shadowは対象外。原因traceなしにNative sourceを変更しない。 |
| 4 | VFX/Particleの透明・深度・temporal挙動 | VFX GraphはLinear必須でGammaを拒否する。HDRP VFX一般対応、透明順序、soft depth、sort/cullingは未完。新しい限定driver/particle試験はまだ実行していない。古いD3D11 snapshotの15 failureとHDRP priming failureは履歴として保持する。 | URP/HDRPごとにeffect抑制と復元、透明順序、soft depth、particle billboard/sort/cullingを確認し、全48 tileと画質を確認する。古いfailureを現在の合否へ転用しない。 |
| 5 | Optical確認、camera authoringと新機能 | 人手による一部の光学確認では奥行き・画質が良好だったが、48視点の端・継ぎ目を含む確認は未完了で、現在ユーザーは実機確認できない。Quilt file再生、orbit/3D cursor、head tracking、audioは未実装または調査中。 | 実機利用可能時に番号付き48 viewの左右端、立体感、色、継ぎ目を記録する。Quilt再生、authoring input、tracking、audioは下表のmilestoneを個別に通す。 |
| 6 | 文書同期と配布状態 | 本変更に含むcapture/MP4機能と限定検証を各文書へ反映済み。GitHub mainへの配布反映は進行中。 | README、Compatibility、機能coverage、SDK比較、capture/video手順、validation reportの状態・pipeline・実行条件を揃え、未実施や過去失敗を消さずに更新する。 |

## 残る検証と依存

| 項目 | 現在分かっていること | 次の検証と完了条件 |
| --- | --- | --- |
| DX12 captureとcamera/texture lifetime | CoreBuiltinCaptureSmokeとURP/HDRP各1 capture testがUnity 6000.6.4f1、DX12、RTX 3090 Tiで通過。中央PNG/sidecar、half/quarter mapping、Pause/Resume/time restorationを含む。pipeline testはfake outputでBridge未使用。初回272文字pathはURT/UXML importに失敗し、同じ420 source fileをhash一致の218文字pathへ置くとimport error 0でtest通過。2つのfolder metaは168-byte fixtureへのcopy漏れが原因と特定し、169-byte sourceをfixtureへ再copyして同期import後にCS errors 0、YAML/meta errors 0、Folder GUID確認を通過。 | fake-output testと公式Bridgeの実出力を区別し、capture保存、停止/pending/natural exit、実機表示をBridge接続した隔離runで確認する。 |
| URP/HDRP Bridge終了時crash | URP Editor normal-stopで2026-10-07 09:31:42 UTC、bridge_inproc.dll 2.6.3.0、0xC0000005、RVA F8550、thread 71648を記録。vendor SHA prefix 3BE0...。過去HDRP終了事例もある。因果は未確定。 | user sceneを含むURP/HDRP隔離実行で同一runのOS資源とtraceを記録し、自然終了またはcrash証拠を一度だけ取得する。再試行しない。Native guardはduplicate shim image予防としてのみ扱い、vendor修正の根拠としない。 |
| memory/thread/handle/警告の長時間傾向 | SDK fixed fixtureの600秒Player runは完了したが、HDRP Developmentでbytes/thread/handleが上昇した。実scene leak-freeの証明ではない。GUI Editor実測はGraphics Jobs Split Threading、graphicsJobs true、MTRendering true。別BatchのSingleThreadedとは条件が異なる。 | 先の隔離runで段階別OS countersを採取する。短時間runは傾向の入口に限る。600秒以上の実scene soakと反復Stop/Startを独立に行う。 |
| 48視点の性能 | HDRP VFX Learning Templatesで約126.6 ms/frame、Camera.CustomRender約116.6 ms/48 calls。これは短いEditor測定で、Bridge無し比較や性能改善の証明ではない。GardenのVolumeStack A/B/Aはprivate trialで改善なし。 | 実効threadingがSplitならthread-mode調整を打ち切る。必要時のみ候補ごとのA/B/Aを行い、計測誤差を超えるwall/CPU/GPU改善、dropなし、48 image品質維持を条件とする。Spot/Point shadow案は専用fixtureで別途評価し、以前の2つのURP共有culling試作は再試行しない。 |
| VFX / Particle / Volume | VFX GraphはLinear必須。古いD3D11 15 failuresは現行DX12状態と分けて保持。private VolumeStack trialは7/7 test passでもA/B/Aで性能改善なし。 | 透明、深度、視点間履歴、suppress/restore、volume stateをpipeline別に全view確認する。 |
| 物理display / optical | 過去に一部の実機画像を人手で良好と確認したが、全48視点レビューは未完了。 | ユーザーが実機確認できる時にtile番号、左右端、奥行き、色、継ぎ目を記録し、未確認範囲も明記する。 |
| MP4 / audio | Silent MP4 exporterのfake process suiteはRunAll PASS、Astra review PASS。FFmpeg/ffprobe 7.0.2で3枚の合成Quiltを実書出しし、Unicode/空白path、30fps、H.264/yuv420p、48-view sidecar、no-overwriteを確認。実録画からexportまでのend-to-endとEditor window操作は未確認。AudioRenderer probeはcompileのみでruntime未実行。 | 実録画からの変換とEditor操作をend-to-endで確認する。audioは別milestoneとして25/30/59fps相当phase後に同期と停止/再開を検証する。 |
| Shadow OnDemand | HDRP Spot/Point専用fixtureのdynamic first-capture gateを修正済み（source SHA 800986...、semantic compile PASS）。Astra再監査GO済み。GPU試験は未実行。 | SpotとPointを単独で試し、影がある画素差、動く遮蔽物、light再有効化、auxiliary cameraを確認する。変更後最初の48-view captureをA/B比較し、初回shadow更新も合格条件に含める。 |
| 文書と配布 | 以前のD3D11 108/123に15 failure。capture/MP4機能と今回の限定検証をこのchange setに反映している。GitHub mainへの配布反映は進行中で、実Bridgeは別検証。 | docsは検証対象のpipeline、API、fixture、fake/real outputを必ず明記する。過去履歴や未完了を削除しない。 |

## 機能milestone

| 機能 | milestone | 完了条件 |
| --- | --- | --- |
| Central 2D capture | 完成Quiltの中心論理viewを追加renderなしでPNGとsidecarへ保存。限定DX12 fake-output URP/HDRP testで確認。 | Bridge送信中の保存と実機表示を確認する。 |
| Pause/Resume | PNG Recorderで実装。既存10/25fps連番testと限定DX12 fake-output URP/HDRP testでframe/time復元を確認。 | 長時間・実sceneでframe番号、scene/capture time、pause/resume/stop/errorを確認し、Bridge出力中の挙動を調べる。 |
| Quilt file load/replay | 未実装。PNG/metadataを読み込みBridgeへ再生する。 | profile/layout検証、連番、pause/resume、stop cleanup、次のstable output復帰を検証する。 |
| Silent MP4 | Editor後処理exporter。FFmpeg/ffprobeは外部依存でSDKへbinaryを含めない。Fake suiteとCPU-only FFmpeg 7.0.2 smokeを確認。 | 実Quilt recordingからexportまでのend-to-endとEditor window操作を確認する。Bridge captureや物理表示の成功とは分ける。 |
| Audio | AudioRenderer offline pathを25/30/59 fps相当phaseで先に調査。 | AudioRendererとPNG frame時刻の対応、duration、sample rate、pause/stop/error復元を確定し、silent exportとは別に同期検証する。 |
| Orbit / 3D cursor / authoring input | 後続milestone。Scene/Game viewでinputを追加する。 | 元cameraを意図せず変更せず、frame/near/far/focus preview、Undo、保存とruntime poseを検証する。 |
| Head tracking | capability-gated調査後に実装可否を決める。 | 対応sourceを特定し、coordinates、latency、失効fallbackを確認する。capability不明のdeviceではoffを維持する。 |

## 保留と判定

XR Plugin、Plug & Play/hotplug、複数機器/display、別GPU vendor、Gamma VFXは保留。Gamma VFXはUnity VFX Graph要件に従い非対応。optical 48-view確認はユーザー不在のため未検証。

VolumeStack reuseの7/7 testsとA/B/Aはprivate trialの成果で、性能改善は計測誤差を超えず採用しない。DX12 capture fake-output testsとMP4 CPU smokeは実施済みだが、Bridge output/実機表示の合格を意味しない。全size fixed-texture Bridge shutdownの1回自然終了は48 camera/VFX shutdownの証明ではない。GitHub mainへの配布反映は進行中。
