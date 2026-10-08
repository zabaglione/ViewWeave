# Compatibility matrix

対象はUnity 6000.6.4f1、Windows、URPまたはHDRPです。Bridge Worker経路はD3D11/D3D12のEditor PlayとWindows Playerに対応します。Unity D3D12のQuiltは同じGPU上のD3D11 workerへ共有され、公式Looking Glass Bridge runtimeへ送られます。この経路は画像のCPU readbackを行いません。Legacy Direct経路はD3D11のみです。Edit ModeとmacOS出力は対象外です。対応の検証状況は下表と[Windows validation](windows-validation.md)を参照してください。公式APIのsource互換は保証しません。macOS Metalは通常Sessionで未対応です。`BridgeMetalExperimentalOutput`は明示opt-inの最大3 quilt実験に限られ、Unity/official Bridge runtimeを使った実機表示検証は未実施です。macOS native shimはUPM未同梱で、取得できるCI artifactは手動配布物です。macOS shimのCI build/linkと3 contract testsは成功していますが、Unity/official SDK/実機実行は未検証です。Windows終了処理にはworker/Legacy D3D11のfinal drain、session close ledger、GPU proofを追加しました。Local Native full CTestはUnity PluginAPIとpinned headerの両方で6/6、Editor/Player shutdown fixturesはNative/managed completion proof後の自然終了を確認しています。Full Bridge fixtureは以前の自動WM_CLOSE候補で通過した履歴で、現在の明示的な2回目終了通知の動作を検証したものではありません。save/cancel GUI試験は未完了で、48-view optical quality、性能、physical displayも未検証です。過去のaccess violationの原因は不明で、自然終了fixtureはそれらの原因を除去した証明ではありません。検証範囲は[shutdown validation](shutdown-validation-20261008.md)を参照してください。手順と制約は[Graphics backends](graphics-backends.md)を参照してください。

品質変更は転送を静止させ、同一host・windowで資源を再設定してから自動描画へ復帰します。明示的な`StopSession()`/`StartSession()`は独立した再起動手段です。

その後の固定条件URP/HDRP Player検証では、Preview 1920x1080、Balanced 3840x2160、Native 7680x4320、Previewの4段階を各122 completionで完了し、全tile非空、移動、視差、明示Stop/Start後120 draw、pending 0を確認しました。これは固定条件の結果であり、他機器や光学品質の保証ではありません。

旧package snapshotの別Windows D3D11 Player検証では、URP Simple Burstを通常制御Cameraで1/8/48視点、GRD ON/OFFの6条件で確認しました。HDRP primingは通常画像差分0で未通過でした。これは限定された素材と制御条件の歴史的結果で、現行D3D12 project統合やVFX全体の対応を示しません。

旧URP driver対象試験の5/5は通常制御Cameraと実験用driver条件に限定された過去結果です。現DX12 cold suiteは6/7で、FOV50 Simple Burstのalive=0 failureを保持しています。後続のmemory診断7/7とfocused filter 3/3は観測の追加であり、恒久修正やVFX全体の対応を示しません。

現行guard後のCore/SRP/VFX Editor targeted試験は、LinearのSRP/VFX 17/17（VFX driver 6件を含む）とGammaのMSAA/VFX 8/8（Gamma VFX拒否1件を含む）でした。これは全件matrixの更新ではなく、旧123件統合の結果とも混同しません。Gamma MSAA/通常URPのBridge Player実行は未検証です。Gamma VFXは公式非対応です。証跡は`.local/windows-improvement/bridge/linear-srp-vfx.xml`と`.local/windows-improvement/bridge/gamma-msaa-8-original-threshold.xml`です。

別のprivate trial `codex/urp-volume-reuse` ではVolumeStack変更のPlayMode testが7/7通過しましたが、Garden A/B/A測定で性能改善は確認されず、採用していません。

VFX GraphはLinear color spaceが必須です。Unity Visual Effect Graph 17.6の公式要件ではURPのGamma color spaceは非対応で、HDRPもLinear color spaceを要求します。UrpVfxCameraDriverはGamma時にCameraやRenderTextureを生成せず、`VFX Graph requires Linear color space; URP Gamma is unsupported`で停止します。GammaでのVFX描画は対応範囲に含めません。[Unity Visual Effect Graph 17.6 system requirements](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/System-Requirements.html)、[Unity HDRP asset requirements](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/create-an-hdrp-asset.html)

旧package snapshotのWindows D3D11 PlayMode統合は123件中108件通過、15件失敗、skip 0でした。Session 18/18、互換性37/37、GRD 27/27、SRP追加10/10、SRP拡張2/2、VFX driver 5/5、Samples 4/4を確認しました。未通過はHDRP priming 1件とVFX compatibility 14件で、別の同時期VFX matrixは20件中5件pass、15件failでした。未通過条件では十分な全tileの効果を確認できませんでした。この履歴は現行D3D12 project統合やすべてのVFX条件の成否を示すものではありません。Bridge接続のURP/HDRP PlayerはPreview、Balanced、NativeのGRD ON/OFF各6条件を固定条件で確認しています。これらは個別fixtureと固定条件の結果であり、機能全体の対応や一般機器でのPlayer実行の保証を意味しません。品質切替cycleも固定条件で通過しました。性能優位と光学品質の全視点人手確認、配布署名、hotplug、複数機器、Metalは未確定です。

公式4 beta/alpha資料は、確認した範囲ではURP 17.0〜17.1、Unity 6000.0〜6000.1を対象とし、公式HDRPは未対応です。既定のdepth interpolationには透明表現上の制約があり、VFXには追加設定が必要です。公式iOS対象を対象外とは扱いません。本SDKの初期release範囲は別途定義します。

公式3.x資料はBuilt-inを主な比較対象とし、URPには制限があり、HDRPは未対応です。公式4 beta配布物とalpha公開資料では、Unity 6000.0.39〜6000.1.5、URP 17.0.4〜17.1、Render Graphを対象として説明されています。公式4でもHDRPは未対応で、視点補間と透明表現には制約があります。

一次資料: [公式Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)、[公式3.x資料](https://lfdocs.lookingglassfactory.com/software/index)、[公式4.0 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)。

| 機能 | 本SDK現状 | 今後の受入条件 |
| --- | --- | --- |
| Quilt / off-axis / View Render Mode | Coreで実装。Fullは全viewを描画し、Half/Quarterは均等に選んだ視点をそれぞれ24/12回だけ描画して、全出力tileを最近傍画像で埋める。48 viewの出力Quiltとprofile解像度は維持する。補間は行わず、描画数を減らすと角度方向の滑らかさが下がる。2026-10-04のstatic HDRP fixtureでFull/Half/Quarter/Fullを計測し、全48出力tileのHalf/Quarter最近傍sourceとのRGBA画素差0を確認。Frame p50は254.76/53.17/23.31/254.68 ms。一般sceneの性能や実機光学品質は未確認。詳細は[DX12 HDRP performance investigation](dx12-performance-investigation.md)。 | HDRP/VFX user sceneと実機の視認品質、一般機器での性能を確認 |
| profile / quality / hotplug / 複数機器 | Preview/Balanced/NativeとGRD ON/OFFの6条件、48 view Playerを確認。hotplug、複数機器、Registryの一般運用は未検証 | profile由来の自動選択、hotplug、複数機器を再現可能にする |
| depthiness / frustum / gizmo / camera conversion | 今回の変更でfocus距離、near/far/framing表示、focus pivot orbit、3D cursorを実装。DX12 targeted authoring testとstatic review PASS。Scene GUI direct dragは未実測 | Editor interactive操作と48-view opticalを確認 |
| Editor / offline display | Bridge Worker経路はWindows D3D11/D3D12 Editor Playに対応。Edit Modeとoffline displayは提供しない。D3D12 EditorはURP/HDRPのDomain Reload enabled/disabledで各2 cycleを確認。HDRP VFX Learning Templates projectもsnapshot 8adda27cでNative/48 viewsのPlayと停止cleanupを確認。GRDはこのmatrixで切り替えていない | 全48 viewsの人手光学レビュー、Editor再起動と一般機器条件を確認 |
| still capture / video / quilt layer / RGBD | mainにCentral 2D PNG、Quilt PNG連番/Pause-Resume、無音MP4 exporterあり。DX12の実URP QuiltRecorder→FFmpeg/ffprobe E2Eは3 frame、256x192、4x2/8 views、30fps H.264/yuv420pでPASS（Native/Bridgeなし）。今回の変更のPlayback safety 12 scenariosとMono audio fake 6/6・actual/silent 8 casesは個別PASS。隔離DX12 official Bridge fixtureで事前生成Quilt PNGの静止/連番playback body gateはPASS（3 submit/0 skipped、同Ready generation、pending 0）したが、Unity終了はUnity.dll access violation、exit code -1073741819で未合格。48 camera live render、audio Bridge delivery、production reload/shutdown orderingの正常終了、Editor window手操作と実機保存は未確認。限定Editor reload probeはtest-only handoffまで。RGBD未検証。 | 実scene 48視点Bridge経路と正常終了、audio Bridge delivery、実機保存、Editor UI、RGBDを受入試験 |
| 3D cursor / orbit / buttons | 今回の変更で実装、DX12 targeted testとstatic review PASS。Scene GUI interactive drag未実測 | Unity Editor上の直接操作を確認 |
| URP | Unity 6000.6.4f1、SRP 17.6、D3D11でLit/Bloom smoke。Base cameraのみ、stackは拒否。M3の8 fixtureを確認済み | 残る視点別効果を個別に検証 |
| HDRP | Linear必須。quilt view中はadapter既定設定でMotion Blur、SSR、SSGI、volumetric fog/cloudを一時抑制。Auto Exposureは全view共通Fixed EV 12、元がFixedならその値を保持。通常Fog/Sky/Bloomと元設定は維持 | HDRP標準templateのCubeとEditor Playで起動・再起動を確認 |
| Built-in / Unity graphics | 製品サポート対象外。CoreはStandard RenderRequest経路を要求 | 対象外として扱う |
| Bridge | WorkerはWindows D3D11/D3D12 Editor PlayとPlayerに対応。D3D12経路はUnity D3D12から同一GPU上のD3D11 workerへ共有して公式Bridgeへ送る。Native ring testはD3D11 baseline込み349 draw、loader 100-cycleはfake runtimeで確認。URP/HDRP Editor PlayのDomain Reload enabled/disabled各2 cycle、両pipelineのD3D12 Development/Release PlayerでPreview/Balanced/Native/Previewを通る固定fixture matrix、DevelopmentのNative 10-cycle restartとGRD-on 600-second holdを確認。HDRP user project snapshot 8adda27cのNative/48 views Editor Playも確認。GRD設定を含む一般projectの性能・光学を保証しない。Legacy Direct実機結果はD3D11のみ。全48 viewの人手確認、配布署名、hotplug、複数機器、Unity/official Bridge上のMetal実行は未実施。Metal native shim自体はCI build/linkとfake contract tests 3/3を確認したが、UPM未同梱の手動CI artifactであり、通常Sessionでは未対応 | 実scene光学品質、VFX長時間、hotplug、複数機器を検証 |
| Bloom / Tonemapping / Color Adjustments | Lit/Bloom smokeのみ確認 | per-view処理とtile seamを検証 |
| TAA / temporal / Auto Exposure | TAAはquilt view中のみ停止して復元。HDRP Auto Exposureはquilt全view共通Fixed exposureへ一時変換。一般的なview別履歴分離は未検証 | temporal履歴の独立は別研究 |
| HDRP Motion Blur / SSR / SSGI / volumetric fog / clouds | adapter既定の一時overrideでquilt view中のみ抑制。通常render scopeでは元stackの設定を使う | 別のtemporal効果や複雑なVolume構成は個別検証 |
| AA: FXAA / SMAA / MSAA | FXAA/SMAAは追加fixtureで8視点の差分を確認。URP MSAA 1/1で4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認。追加のLinear境界試験10/10で、このRTX GPUの2x/4x/8x対応倍率、pipeline 1x、allowMSAA無効、Deferred fallback、中心色一致と対応MSAAのedge差分を確認。Gamma側のtargeted MSAA 7件も通過したが、専用MSAA Playerは未実施。性能は未検証 | MSAAの一般素材、Player、性能を測定 |
| Particles | URP Particle 3/3、HDRP Particle 2/2のDX12限定fixtureを確認（Bridgeなし） | simulation once/frame、billboard、sort、culling、soft depthの一般条件を検証 |
| VFX | 既存cold DX12 driver suite 6/7 PASSでFOV50 Simple Burstのalive=0 failureを保持。追加memory診断7/7 PASS、FOV50/FOV90は各128 alive。original fully-qualified FOV50を含むfilter run 3/3 PASSはあるが、恒久fixの証明ではない。古いD3D11 failureも歴史保持。VFX GraphはLinear必須、Gammaはresource生成前に拒否。HDRP driverは提供しない | 透明処理、sorting/culling、soft depth、temporal、全tileを検証し、元cold failureを別途解消 |
| uGUI | WorldSpaceとScreen Space CameraのM3試験を確認 | 各視点の入力、配置、合成条件を検証 |
| UI Toolkit | Unity 6.6 WorldSpace/targetTexture APIはinstalled XMLで確認済み。M3 targetTexture fixtureを確認 | RT route、ray map、DPI、EventSystemを検証 |
| Overlay / camera stacking | Overlayは通常画面へ描いても自動Quilt captureされない。URP stackは拒否 | 各viewと同じdepthで合成する方式を検証 |
| iOS / Linux | iOSの本SDK初期release範囲外、Linux未実装 | 対象platform方針を確定 |
| cloud Blocks連携 | 未対応 | 認証・terms確認後に別計画 |
| GPU Resident Drawer | Editor 27/27回帰、URP通常thread Player 6ケースを確認。性能優位は未確定 | MeshRenderer、shader、culling、動的更新、透明、fallbackの受入試験 |

uGUI WorldSpaceとScreen Space CameraはM3試験で確認済みです。Overlayは通常画面へ直接Quilt出力できないため、各viewと同じdepthで合成する方式を検証します。UI ToolkitのUnity 6.6 WorldSpace/targetTexture APIとM3 targetTexture fixtureを確認済みですが、入力ray map、DPI、EventSystemは未検証です。ParticlesはLit/Unlitの個別表示fixtureを確認済みで、sort、culling、soft depthなどは未確定です。

FXAA/SMAAは追加fixtureで限定確認しました。URP MSAAは4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認しました。一般Player、性能、HDRP MSAAは未検証です。TAAは一時的に無効化しています。Auto Exposureとtemporal per-view historyは、履歴とメモリを分離した別研究です。

## DX12 capture and shutdown evidence (2026-10-07)

At the time of these tests, baseline revision `4230c4c` included PR #4 and CI run `37614129521` had passed. This change set passed the Playback safety 12 scenarios individually and the Mono Audio fake 6/6 plus actual/silent 8 cases individually (the latter after a focused test-only correction). The isolated DX12 official Bridge PNG playback fixture passed its static/sequence body checks (3 sequence submissions, 0 skipped, same Ready generation, pending 0), but the Unity process exited with `0xC0000005` in `Unity.dll` at offset `0x44383df`; clean natural Editor exit is not established. The fixture used pre-rendered PNGs and did not exercise live 48-camera rendering. Audio Bridge delivery and production reload/shutdown ordering remain open; a limited test-only Editor reload probe does not prove natural exit. The original cold DX12 VFX driver suite remains 6/7 with FOV50 `alive=0`; separate 7/7 memory diagnostics and a 3/3 focused filter run do not prove a permanent fix. URP and earlier HDRP Editor-stop `0xC0000005` observations remain unresolved and are separate from this isolated fixture event. The Native image guard is preventive and does not establish the vendor-crash cause or fix. These targeted results do not establish physical 48-view quality. See [Windows validation](windows-validation.md) and [the evidence report](benchmarks/remaining-work-validation-20261007.md).
