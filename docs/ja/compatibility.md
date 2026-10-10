[English](../compatibility.md)

# 対応表

[ユーザーガイド](user-guide.md) · [技術者向けガイド](technical-guide.md)

## 現在の利用範囲

- **Windows Bridge Worker:** Unity 6000.6.4f1、URPまたはHDRPのEditor Play／Windows Playerで、D3D11またはD3D12を使う構成を対象にしています。公式Looking Glass Bridge runtimeと接続displayが必要です。Legacy Directは別のD3D11専用経路です。
- **macOS Metal出力:** Apple Silicon向けのBridge校正値ベースの独立経路です。OS、校正profile、機器の条件は[macOS Metalガイド](macos-metal.md)を参照してください。過去のGo実機結果は現行一般化binaryを検証せず、non-Go光学出力は未検証です。Windowsの通常Bridge Sessionとは別です。
- **Edit Mode Preview:** 通常のPlay Mode Sessionとは別に明示開始する制作向けPreviewです。出力方法はplatformと接続displayにより異なります。

Quick StartのSample sceneはWindows上で再検証していません。特定のprojectや機器で利用する前に、以下の経路別条件と[日付付きWindows／macOS記録](technical-guide.md)を確認してください。あらゆるUnity構成、effect、displayへの対応を保証するものではありません。

## 現在の開発版の確認範囲

現在は実験的な開発版です。現在のpackage・Sampleと過去の限定試験を区別してください。

| 範囲 | 確認済み | 未検証・制限 |
| --- | --- | --- |
| URP/HDRP Quick Start Sample | ローカルからUPM Import。旧経路のGoではmacOS Metal Editor Previewと短時間のPlayを観測し、向き・立体感、HDRPの明るさを利用者が確認。 | Goの実機観測は校正一般化前の過去記録であり、現行binaryを検証しません。このSampleのWindowsでのGit/UPM導入、D3D11/D3D12実機出力、Playerビルド。[Quick Start記録](../quick-start-validation-20261010.md)を参照。 |
| 現在同梱するWindows DLL | fake Bridge callbackとWARP/test adapterを用いた限定native test 6件が通過。 | このDLLそのもののUnity runtime統合、公式Bridge接続、実機表示。過去のEditor/Player試験や非公開の比較版は現在のartifactを検証したものではありません。[来歴](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Plugins/native-provenance.json)を参照。 |
| macOS runtime | Apple Silicon上の旧Go経路によるEditor試験とURP診断Playerの限定試験。URP/HDRPの4視点描画専用Development／Release Playerで履歴・解放・予算制限を確認。URPではQuiltのTAA画素差、HDRPでは露出共有も確認。 | これらのGo実機確認は現在の校正一般化binaryより前の記録です。現行版は機種非依存の合成校正検査、Gamma/LinearのGPU計算比較、Go URPの短時間Play／Stopを2回通過。non-Go実機の光学出力は未検証です。Quick StartのPlayer、新しい履歴経路のBridge／機器出力Player、Intel runtime、長時間運用、全視点の光学・色測定、通常のウィンドウ終了やEditor終了時のGPU回収。 |
| 配布・導入 | 生成配布物とローカルpackageのImportを確認。 | 公開後の認証なしのGit導入とクリーンな利用者環境での導入。同梱macOS shimはリンカーによるad hoc署名で、Developer ID署名ではありません。公証と利用者環境のGatekeeper受入は未検証です。 |
| 外部権利・来歴 | 固定した公開Unity PluginAPIのライセンス通知全文を同梱。macOSヘッダのハッシュを記録。公式Bridge runtimeは別途導入。 | インストール版Windows Unity PluginAPIの個別ヘッダのハッシュ・通知は保持されていません。Bridgeの商用利用・再配布条件は利用者の契約によります。[第三者ライセンス表記](THIRD_PARTY_NOTICES.md)を参照。 |

以降の過去の失敗や未解決のVFX・終了処理は、それぞれ記載した試験条件での結果です。ビルド成功やローカルQuilt描画を、実機の光学品質や一般的な対応の証明とは扱いません。

## 技術的な証跡と日付付き記録

以下の実装詳細と個別試験結果は、記載したrevision、fixture、機器、条件に限られます。一般的な対応保証ではなく、個別の証跡として参照してください。

通常runtime Sessionの対象はUnity 6000.6.4f1、Windows、URPまたはHDRPです。Bridge Worker経路はD3D11/D3D12のEditor PlayとWindows Playerに対応します。Unity D3D12のQuiltは同じGPU上のD3D11 workerへ共有され、公式Looking Glass Bridge runtimeへ送られます。この経路は画像のCPU readbackを行いません。Legacy Direct経路はD3D11のみです。別機能として`ViewWeaveRig`から明示開始するWindows Editor D3D11/D3D12のEdit Mode previewがありますが、これは通常SessionをPlay Mode外で利用可能にするものではありません。URP/HDRP local GPU fixtureで8 view描画、layout再構築、proof後の旧texture retirement、Stop/readback、retention pending 0を確認しました。D3D11/D3D12 deviceのURP/HDRP fixtureは3840x2160 display profile、7680x4320・8x6/48 viewでStart/Stop/Restartを確認し、Start/Restart各1 submission/1 completion、Stop後native/retention pending 0でした。手動光学確認は保留中です。隔離hidden Editor fixtureでDomain Reload後のpreview cleanupと明示restartを確認しましたが、File > Exit quit gateの資源回収は3 probeで確認済みですが、全Editor processがteardown中に0xC0000005で終了し、正常終了は未確認です。詳細は[Graphics backends](graphics-backends.md)を参照してください。macOSに通常managed Sessionの表示経路はありません。対応の検証状況は下表と[Windows validation](../windows-validation.md)を参照してください。公式APIのsource互換は保証しません。macOS MetalはApple Silicon上の専用`BridgeMetalQuiltOutput`を使い、Bridge metadataと校正値に基づいてRGB/BGR出力を判定します。回転なし・native解像度・拡張画面が必要で、cell tableとnonzero cell-pattern modeは未対応です。Windowsの通常Sessionから独立し、arm64 native shimをpackageに含めます。過去のGo実機結果と現行校正一般化の範囲は[macOS Metal](macos-metal.md)を参照してください。non-Go光学出力は未検証です。従来の有限`BridgeMetalExperimentalOutput`は別の診断経路です。Windows終了処理にはworker/Legacy D3D11のfinal drain、session close ledger、GPU proofを追加しました。Local Native full CTestはUnity PluginAPIとpinned headerの両方で6/6、Editor/Player shutdown fixturesはNative/managed completion proof後の自然終了を確認しています。以前のfull Bridge fixtureでは自動WM_CLOSE候補を含むpackageでnative/managed drain後の自然終了を確認しました。V6ではWindows D3D12 Editorの実GUI File > Exit一回から、GPU proof前のowned textureをgate内でretireし、native host join、managed ownership zero、Editor自然終了code 0までを確認しました。過去のaccess violation原因は不明で、V6はその原因を除去した証明ではありません。save/cancel GUI試験は未完了です。新Edit Mode previewの手動光学確認も未実施で、Domain Reload後のpreview cleanupと明示restartは隔離fixtureで確認済みですが、File > Exit quit gateの資源回収は3 probeで確認済みですが、全Editor processがteardown中に0xC0000005で終了し、正常終了は未確認です。詳細は[Graphics backends](graphics-backends.md)を参照してください。通常Bridgeの48-view光学品質と性能は引き続き未検証です。検証範囲は[shutdown validation](../shutdown-validation-20261008.md)を参照してください。手順と制約は[Graphics backends](graphics-backends.md)を参照してください。

品質変更は転送を静止させ、同一host・windowで資源を再設定してから自動描画へ復帰します。明示的な`StopSession()`/`StartSession()`は独立した再起動手段です。

その後の固定条件URP/HDRP Player検証では、Preview 1920x1080、Balanced 3840x2160、Native 7680x4320、Previewの4段階を各122 completionで完了し、全tile非空、移動、視差、明示Stop/Start後120 draw、pending 0を確認しました。これは固定条件の結果であり、他機器や光学品質の保証ではありません。

旧package snapshotの別Windows D3D11 Player検証では、URP Simple Burstを通常制御Cameraで1/8/48視点、GRD ON/OFFの6条件で確認しました。HDRP primingは通常画像差分0で未通過でした。これは限定された素材と制御条件の歴史的結果で、現行D3D12 project統合やVFX全体の対応を示しません。

旧URP driver対象試験の5/5は通常制御Cameraと実験用driver条件に限定された過去結果です。現DX12 cold suiteは6/7で、FOV50 Simple Burstのalive=0 failureを保持しています。後続のmemory診断7/7とfocused filter 3/3は観測の追加であり、恒久修正やVFX全体の対応を示しません。

現行guard後のCore/SRP/VFX Editor targeted試験は、LinearのSRP/VFX 17/17（VFX driver 6件を含む）とGammaのMSAA/VFX 8/8（Gamma VFX拒否1件を含む）でした。これは全件matrixの更新ではなく、旧123件統合の結果とも混同しません。Gamma MSAA/通常URPのBridge Player実行は未検証です。Gamma VFXは公式非対応です。証跡は`.local/windows-improvement/bridge/linear-srp-vfx.xml`と`.local/windows-improvement/bridge/gamma-msaa-8-original-threshold.xml`です。

別のprivate trial `codex/urp-volume-reuse` ではVolumeStack変更のPlayMode testが7/7通過しましたが、Garden A/B/A測定で性能改善は確認されず、採用していません。

VFX GraphはLinear color spaceが必須です。Unity Visual Effect Graph 17.6の公式要件ではURPのGamma color spaceは非対応で、HDRPもLinear color spaceを要求します。UrpVfxCameraDriverはGamma時にCameraやRenderTextureを生成せず、`VFX Graph requires Linear color space; URP Gamma is unsupported`で停止します。GammaでのVFX描画は対応範囲に含めません。[Unity Visual Effect Graph 17.6 system requirements](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/System-Requirements.html)、[Unity HDRP asset requirements](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/create-an-hdrp-asset.html)

旧package snapshotのWindows D3D11 PlayMode統合は123件中108件通過、15件失敗、skip 0でした。Session 18/18、互換性37/37、GRD 27/27、SRP追加10/10、SRP拡張2/2、VFX driver 5/5、Samples 4/4を確認しました。未通過はHDRP priming 1件とVFX compatibility 14件で、別の同時期VFX matrixは20件中5件pass、15件failでした。未通過条件では十分な全tileの効果を確認できませんでした。この履歴は現行D3D12 project統合やすべてのVFX条件の成否を示すものではありません。Bridge接続のURP/HDRP PlayerはPreview、Balanced、NativeのGRD ON/OFF各6条件を固定条件で確認しています。これらは個別fixtureと固定条件の結果であり、機能全体の対応や一般機器でのPlayer実行の保証を意味しません。品質切替cycleも固定条件で通過しました。性能優位と光学品質の全視点人手確認、配布署名、hotplug、複数機器は未確定です。過去のGo実機確認と現行校正一般化の検証範囲は[macOS Metal](macos-metal.md)を参照してください。

公式4 beta/alpha資料は、確認した範囲ではURP 17.0〜17.1、Unity 6000.0〜6000.1を対象とし、公式HDRPは未対応です。既定のdepth interpolationには透明表現上の制約があり、VFXには追加設定が必要です。公式iOS対象を対象外とは扱いません。本SDKの初期release範囲は別途定義します。

公式3.x資料はBuilt-inを主な比較対象とし、URPには制限があり、HDRPは未対応です。公式4 beta配布物とalpha公開資料では、Unity 6000.0.39〜6000.1.5、URP 17.0.4〜17.1、Render Graphを対象として説明されています。公式4でもHDRPは未対応で、視点補間と透明表現には制約があります。

一次資料: [公式Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)、[公式3.x資料](https://lfdocs.lookingglassfactory.com/software/index)、[公式4.0 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)。

| 機能 | 本SDK現状 | 今後の受入条件 |
| --- | --- | --- |
| Quilt / off-axis / View Render Mode | Coreで実装。Fullは全viewを描画し、Half/Quarterは均等に選んだ視点をそれぞれ24/12回だけ描画して、全出力tileを最近傍画像で埋める。48 viewの出力Quiltとprofile解像度は維持する。補間は行わず、描画数を減らすと角度方向の滑らかさが下がる。2026-10-04のstatic HDRP fixtureでFull/Half/Quarter/Fullを計測し、全48出力tileのHalf/Quarter最近傍sourceとのRGBA画素差0を確認。Frame p50は254.76/53.17/23.31/254.68 ms。一般sceneの性能や実機光学品質は未確認。詳細は[DX12 HDRP performance investigation](../dx12-performance-investigation.md)。 | HDRP/VFX user sceneと実機の視認品質、一般機器での性能を確認 |
| profile / quality / hotplug / 複数機器 | Preview/Balanced/NativeとGRD ON/OFFの6条件、48 view Playerを確認。hotplug、複数機器、Registryの一般運用は未検証 | profile由来の自動選択、hotplug、複数機器を再現可能にする |
| depthiness / frustum / gizmo / camera conversion | 今回の変更でfocus距離、near/far/framing表示、focus pivot orbit、3D cursorを実装。DX12 targeted authoring testとstatic review PASS。Scene GUI direct dragは未実測 | Editor interactive操作と48-view opticalを確認 |
| Editor / offline display | 通常Bridge Worker SessionはWindows D3D11/D3D12 Editor Playに対応。別経路の`ViewWeaveRig` Edit Mode previewはLocal表示またはWindows Workerでdevice送信できます。URP/HDRP local GPU fixtureで8 view描画、layout再構築、proof後の旧texture retirement、Stop/readback、retention pending 0を確認。D3D11/D3D12 device fixtureはURP/HDRPで3840x2160、7680x4320、8x6/48 viewのStart/Stop/Restartを確認し、Start/Restart各1 submission/1 completion、Stop後native/retention pending 0でした。新経路は既存Play fixtureに含まれず、手動の光学確認は未検証です。隔離hidden Editor fixtureでDomain Reload後のcleanupと明示restartを確認しましたが、File > Exit quit gateの資源回収は3 probeで確認済みですが、全Editor processがteardown中に0xC0000005で終了し、正常終了は未確認です。詳細は[Graphics backends](graphics-backends.md)を参照してください。D3D12 Editor PlayはURP/HDRPのDomain Reload enabled/disabledで各2 cycleを確認。HDRP VFX Learning Templates projectもsnapshot 8adda27cでNative/48 viewsのPlayと停止cleanupを確認。GRDはこのmatrixで切り替えていない | active previewのquit gateとown-window closeを確認し、全48 viewsの人手光学レビューと一般機器条件を確認 |
| still capture / video / quilt layer / RGBD | mainにCentral 2D PNG、Quilt PNG連番/Pause-Resume、無音MP4 exporterあり。DX12の実URP QuiltRecorder→FFmpeg/ffprobe E2Eは3 frame、256x192、4x2/8 views、30fps H.264/yuv420pでPASS（Native/Bridgeなし）。今回の変更のPlayback safety 12 scenariosとMono audio fake 6/6・actual/silent 8 casesは個別PASS。隔離DX12 official Bridge fixtureで事前生成Quilt PNGの静止/連番playback body gateはPASS（3 submit/0 skipped、同Ready generation、pending 0）したが、Unity終了はUnity.dll access violation、exit code -1073741819で未合格。48 camera live render、audio Bridge delivery、production reload/shutdown orderingの正常終了、Editor window手操作と実機保存は未確認。限定Editor reload probeはtest-only handoffまで。RGBD未検証。 | 実scene 48視点Bridge経路と正常終了、audio Bridge delivery、実機保存、Editor UI、RGBDを受入試験 |
| 3D cursor / orbit / buttons | 今回の変更で実装、DX12 targeted testとstatic review PASS。Scene GUI interactive drag未実測 | Unity Editor上の直接操作を確認 |
| URP | Unity 6000.6.4f1、SRP 17.6、D3D11でLit/Bloom smoke。Base cameraのみ、stackは拒否。既定のhistory modeは描画論理viewごとに独立したpersistent Camera historyを持つ | 残る視点別効果を個別に検証 |
| HDRP | Linear必須。既定の`IsolatedViewHistories`は描画する論理viewごとにpersistent Camera historyを持ち、Reference CameraからHDRP設定をコピーします。`FollowScene`は中央viewをGPU測光し、結果をquilt全体で共有します。SceneのFixed exposureは`fixedExposure - compensation`を使用します。中立な初回meter sampleを捨て、warmup中は出力しません。`ManualFixed`は明示選択です。既定ではTAA、SSR、SSGI、fog/cloud、Motion Blurをscene設定のまま使います | 6000.6.4f1のMetal batch、Goの植物ケースのEditor表示、4視点のmacOS描画専用Development／Release Playerを確認。Windows D3D11/D3D12/Player、macOSのBridge／機器出力Player、effect別画素正確性は未検証。[HDRP view-history validation](../hdrp-view-history-validation-20261010.md)を参照 |
| Built-in / Unity graphics | 製品サポート対象外。対応するSRP adapterが必要 | 対象外として扱う |
| Bridge | WorkerはWindows D3D11/D3D12 Editor PlayとPlayerに対応。D3D12経路はUnity D3D12から同一GPU上のD3D11 workerへ共有して公式Bridgeへ送る。Native ring testはD3D11 baseline込み349 draw、loader 100-cycleはfake runtimeで確認。URP/HDRP Editor PlayのDomain Reload enabled/disabled各2 cycle、両pipelineのD3D12 Development/Release PlayerでPreview/Balanced/Native/Previewを通る固定fixture matrix、DevelopmentのNative 10-cycle restartとGRD-on 600-second holdを確認。HDRP user project snapshot 8adda27cのNative/48 views Editor Playも確認。GRD設定を含む一般projectの性能・光学を保証しない。Legacy Direct実機結果はD3D11のみ。全48 viewの人手確認、配布署名、hotplug、複数機器は未実施。macOSのBridge校正値に基づくMetal出力とarm64 shimをpackageに含めます。[macOS Metal](macos-metal.md)に現在の検証範囲を記載。Windowsの通常SessionはMetal未対応 | 実scene光学品質、VFX長時間、hotplug、複数機器を検証 |
| Bloom / Tonemapping / Color Adjustments | Lit/Bloom smokeのみ確認 | per-view処理とtile seamを検証 |
| TAA / temporal / Auto Exposure | URP既定のisolated-history modeは描画論理viewごとの独立historyと同期public `Camera.Render()`を使います。Standard RenderRequestはURP TAA historyの書き込みを抑制します。Post Processing、single-sample target、Universal Renderer、Camera Stackなし、dynamic resolutionなしが必要です。URP `SharedCameraCompatibility`だけが描画中TAAを停止・復元します。2026-10-11のMetal Editor PlayMode `GraphicsCompatibility`は58/58 PASS、skip 0（URP adapterは8 test）でした。Metal上のbounded Edit Mode／描画専用Player runは日付付き記録に示しています。Windows／実機光学のTAA検証は未実施です。HDRP既定のisolated modeも描画viewごとにpersistent historyを作り、`FollowScene`で中央viewのGPU測光結果をFixed exposureとして共有。個々のeffect画素は未検証。[URP検証記録](../urp-view-history-validation-20261011.md)を参照 | Windows／実機光学とeffect別画素を検証 |
| HDRP Motion Blur / URP Motion Blur / SSR / SSGI / volumetric fog / clouds | HDRP既定のisolated modeはscene設定を維持。HDRP `SharedCameraCompatibility`だけがMotion Blur、SSR、SSGI、cloudsを無効化し、TAAを停止、volumetric fogをGaussian filteringへ変更。non-Fixed exposureはManual EV（既定12）へ変換。URPはPost Processing有効時にactiveなVolume Motion Blurを未検証のため拒否 | 各modeと複雑なVolume構成の視覚的正しさを検証 |
| AA: FXAA / SMAA / MSAA | FXAA/SMAAは追加fixtureで8視点の差分を確認。URP MSAA 1/1で4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認。追加のLinear境界試験10/10で、このRTX GPUの2x/4x/8x対応倍率、pipeline 1x、allowMSAA無効、Deferred fallback、中心色一致と対応MSAAのedge差分を確認。Gamma側のtargeted MSAA 7件も通過したが、専用MSAA Playerは未実施。性能は未検証 | MSAAの一般素材、Player、性能を測定 |
| Particles | URP Particle 3/3、HDRP Particle 2/2のDX12限定fixtureを確認（Bridgeなし） | simulation once/frame、billboard、sort、culling、soft depthの一般条件を検証 |
| VFX | 既存cold DX12 driver suite 6/7 PASSでFOV50 Simple Burstのalive=0 failureを保持。追加memory診断7/7 PASS、FOV50/FOV90は各128 alive。original fully-qualified FOV50を含むfilter run 3/3 PASSはあるが、恒久fixの証明ではない。古いD3D11 failureも歴史保持。VFX GraphはLinear必須、Gammaはresource生成前に拒否。HDRP driverは提供しない | 透明処理、sorting/culling、soft depth、temporal、全tileを検証し、元cold failureを別途解消 |
| uGUI | WorldSpaceとScreen Space CameraのM3試験を確認 | 各視点の入力、配置、合成条件を検証 |
| UI Toolkit | Unity 6.6 WorldSpace/targetTexture APIはinstalled XMLで確認済み。M3 targetTexture fixtureを確認 | RT route、ray map、DPI、EventSystemを検証 |
| Overlay / camera stacking | Overlayは通常画面へ描いても自動Quilt captureされない。URP stackは拒否 | 各viewと同じdepthで合成する方式を検証 |
| macOS Metal | 専用`BridgeMetalQuiltOutput`、Bridge metadataの寸法・quilt/grid・aspect/viewcone/DPI/raw `center`／`pitch`／`slope`値と`get_ri_for_display`／`get_bi_for_display` channel order、独自合成shader、専用Metalウィンドウ。通常RGB/BGR（`mode=0`、`cells=0`）、`flipX`／`invertedViews`は0/1のみ。回転なし・native解像度・拡張画面が必要。cell tableとnonzero modeは拒否し、`fringe`は有限性だけ検査して補正には不使用。共通Rig UIからEdit ModeのLocal／Device Preview、Play中のQuilt PNG／連番再生を実装。 | URP/HDRP templateのGo Previewによる向き・立体感・明るさ確認は校正一般化前の過去記録で、現行binaryを検証しません。機種非依存の合成校正検査とGamma/LinearのGPU計算テストは通過し、現行URP/Goの短時間Play／Stopも確認しました。non-Go光学出力、長時間動作、Intel、全視点の光学測定は未検証。詳細は[macOS Metal](macos-metal.md)と[校正一般化記録](../macos-metal-generalization-validation-20261011.md)。 |
| iOS / Linux | iOSの本SDK初期release範囲外、Linux未実装 | 対象platform方針を確定 |
| cloud Blocks連携 | 未対応 | 認証・terms確認後に別計画 |
| GPU Resident Drawer | Editor 27/27回帰、URP通常thread Player 6ケースを確認。性能優位は未確定 | MeshRenderer、shader、culling、動的更新、透明、fallbackの受入試験 |

uGUI WorldSpaceとScreen Space CameraはM3試験で確認済みです。Overlayは通常画面へ直接Quilt出力できないため、各viewと同じdepthで合成する方式を検証します。UI ToolkitのUnity 6.6 WorldSpace/targetTexture APIとM3 targetTexture fixtureを確認済みですが、入力ray map、DPI、EventSystemは未検証です。ParticlesはLit/Unlitの個別表示fixtureを確認済みで、sort、culling、soft depthなどは未確定です。

FXAA/SMAAは追加fixtureで限定確認しました。URP MSAAは4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認しました。一般Player、性能、HDRP MSAAは未検証です。URP/HDRPのhistory設定は**Rig > Rendering**にあり、History Resource Budgetの既定値は4096 MB、Maximum History Camerasは256です。graphics-driver growth実測と新規・拡大RenderTexture推定の大きい方がbudgetを超えると出力を停止し、camera数も別途制限します。共有allocationを含みますが、未観測bufferやdriver overheadを含まないため、完全なVRAM上限ではありません。従来の範囲は[Windows validation](../windows-validation.md)、新しいURP history pathは[日付付き記録](../urp-view-history-validation-20261011.md)を参照してください。

## DX12 captureとshutdownの検証記録（2026-10-07）

この試験時点のbaseline revision `4230c4c`にはPR #4が含まれ、CI run `37614129521`は成功していました。この変更ではPlayback safetyの12 scenarioを個別に通過し、Mono Audio fake 6/6とactual/silent 8 caseも個別に通過しました（後者はtestだけに限定した修正後の結果です）。隔離したDX12 official Bridge PNG playback fixtureは、静的確認とsequence本体gate（sequence submission 3、skip 0、同じReady generation、pending 0）を通過しましたが、Unity processは`Unity.dll` offset `0x44383df`で`0xC0000005`終了しました。Editorが自然終了することは確認できていません。このfixtureは事前描画PNGを使い、48 Cameraのlive renderingを試していません。Audio Bridge deliveryとproduction reload/shutdown順序は未解決です。限定的なtest-only Editor reload probeは自然終了を証明しません。元のcold DX12 VFX driver suiteはFOV50 `alive=0`を含む6/7のままです。別のmemory diagnostic 7/7とfocused filter 3/3は恒久修正を証明しません。URPと以前のHDRP Editor-stopで観測した`0xC0000005`は未解決で、この隔離fixtureの事象とは別です。Native image guardは予防策で、vendor crashの原因特定や修正を示しません。これらの限定結果は、実機の48視点品質を証明しません。[Windows validation](../windows-validation.md)と[検証記録](../benchmarks/remaining-work-validation-20261007.md)を参照してください.
