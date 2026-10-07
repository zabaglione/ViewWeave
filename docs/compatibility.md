# Compatibility matrix

対象はUnity 6000.6.4f1、Windows、URPまたはHDRPです。Bridge Worker経路はD3D11/D3D12のEditor PlayとWindows Playerに対応します。Unity D3D12のQuiltは同じGPU上のD3D11 workerへ共有され、公式Looking Glass Bridge runtimeへ送られます。この経路は画像のCPU readbackを行いません。Legacy Direct経路はD3D11のみです。Edit ModeとmacOS出力は対象外です。対応の検証状況は下表と[Windows validation](windows-validation.md)を参照してください。公式APIのsource互換は保証しません。

品質変更は転送を静止させ、同一host・windowで資源を再設定してから自動描画へ復帰します。明示的な`StopSession()`/`StartSession()`は独立した再起動手段です。

その後の固定条件URP/HDRP Player検証では、Preview 1920x1080、Balanced 3840x2160、Native 7680x4320、Previewの4段階を各122 completionで完了し、全tile非空、移動、視差、明示Stop/Start後120 draw、pending 0を確認しました。これは固定条件の結果であり、他機器や光学品質の保証ではありません。

旧package snapshotの別Windows D3D11 Player検証では、URP Simple Burstを通常制御Cameraで1/8/48視点、GRD ON/OFFの6条件で確認しました。HDRP primingは通常画像差分0で未通過でした。これは限定された素材と制御条件の歴史的結果で、現行D3D12 project統合やVFX全体の対応を示しません。

URP driver対象試験は最新統合で5/5通過しました。これは通常制御Cameraと実験用driver条件に限定した結果で、VFX全体の対応を示しません。

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
| depthiness / frustum / gizmo / camera conversion | Quilt mathとcamera変換の一部のみ | 統合RigとEditor操作を検証 |
| Editor / offline display | Bridge Worker経路はWindows D3D11/D3D12 Editor Playに対応。Edit Modeとoffline displayは提供しない。D3D12 EditorはURP/HDRPのDomain Reload enabled/disabledで各2 cycleを確認。HDRP VFX Learning Templates projectもsnapshot 8adda27cでNative/48 viewsのPlayと停止cleanupを確認。GRDはこのmatrixで切り替えていない | 全48 viewsの人手光学レビュー、Editor再起動と一般機器条件を確認 |
| still capture / video / quilt layer / RGBD | Central 2D PNGを含むQuilt PNG/PNG連番は実装済み。D3D11 URP/HDRP静止画suite各2/2。DX12 Core smoke、URP/HDRP各1 capture testはfake outputでpassし、central metadata、mapping、Pause/Resumeを確認。Bridge output未検証。静音MP4 exporterのfake suiteと実FFmpeg/ffprobe 7.0.2 CPU smokeはpass（合成3-frame input）。実録画からのexport end-to-endとEditor window操作は未確認。音声とQuilt playbackは未実装。RGBDは未検証。 | DX12 Bridge経路、実QuiltからMP4へのend-to-end、Editor window操作、音声、Quilt playback、RGBDの受入試験 |
| 3D cursor / orbit / buttons | 未検証 | 入力と機器差を検証 |
| URP | Unity 6000.6.4f1、SRP 17.6、D3D11でLit/Bloom smoke。Base cameraのみ、stackは拒否。M3の8 fixtureを確認済み | 残る視点別効果を個別に検証 |
| HDRP | Linear必須。quilt view中はadapter既定設定でMotion Blur、SSR、SSGI、volumetric fog/cloudを一時抑制。Auto Exposureは全view共通Fixed EV 12、元がFixedならその値を保持。通常Fog/Sky/Bloomと元設定は維持 | HDRP標準templateのCubeとEditor Playで起動・再起動を確認 |
| Built-in / Unity graphics | 製品サポート対象外。CoreはStandard RenderRequest経路を要求 | 対象外として扱う |
| Bridge | WorkerはWindows D3D11/D3D12 Editor PlayとPlayerに対応。D3D12経路はUnity D3D12から同一GPU上のD3D11 workerへ共有して公式Bridgeへ送る。Native ring testはD3D11 baseline込み349 draw、loader 100-cycleはfake runtimeで確認。URP/HDRP Editor PlayのDomain Reload enabled/disabled各2 cycle、両pipelineのD3D12 Development/Release PlayerでPreview/Balanced/Native/Previewを通る固定fixture matrix、DevelopmentのNative 10-cycle restartとGRD-on 600-second holdを確認。HDRP user project snapshot 8adda27cのNative/48 views Editor Playも確認。GRD設定を含む一般projectの性能・光学を保証しない。Legacy Direct実機結果はD3D11のみ。全48 viewの人手確認、配布署名、hotplug、複数機器、Metalは未実施 | 実scene光学品質、VFX長時間、hotplug、複数機器を検証 |
| Bloom / Tonemapping / Color Adjustments | Lit/Bloom smokeのみ確認 | per-view処理とtile seamを検証 |
| TAA / temporal / Auto Exposure | TAAはquilt view中のみ停止して復元。HDRP Auto Exposureはquilt全view共通Fixed exposureへ一時変換。一般的なview別履歴分離は未検証 | temporal履歴の独立は別研究 |
| HDRP Motion Blur / SSR / SSGI / volumetric fog / clouds | adapter既定の一時overrideでquilt view中のみ抑制。通常render scopeでは元stackの設定を使う | 別のtemporal効果や複雑なVolume構成は個別検証 |
| AA: FXAA / SMAA / MSAA | FXAA/SMAAは追加fixtureで8視点の差分を確認。URP MSAA 1/1で4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認。追加のLinear境界試験10/10で、このRTX GPUの2x/4x/8x対応倍率、pipeline 1x、allowMSAA無効、Deferred fallback、中心色一致と対応MSAAのedge差分を確認。Gamma側のtargeted MSAA 7件も通過したが、専用MSAA Playerは未実施。性能は未検証 | MSAAの一般素材、Player、性能を測定 |
| Particles | M3のLit/Unlit表示fixtureを確認。一般的な粒子機能全体は未確定 | simulation once/frame、billboard、sort、culling、soft depthを検証 |
| VFX | URP driver 5/5は通過。19件中5件通過・14件未通過、および別matrix 20件中5件通過・15件未通過は旧package snapshotのWindows D3D11結果で、HDRP priming 1件未通過も同じ歴史的検証範囲。これらは現行D3D12 projectや全VFX条件の成否を示さない。VFX GraphはLinear必須で、URP Gammaはdriverがresource生成前に拒否する。HDRP driverは提供しない | 補助Camera条件、透明処理、sort、公式4資料の追加設定を検証 |
| uGUI | WorldSpaceとScreen Space CameraのM3試験を確認 | 各視点の入力、配置、合成条件を検証 |
| UI Toolkit | Unity 6.6 WorldSpace/targetTexture APIはinstalled XMLで確認済み。M3 targetTexture fixtureを確認 | RT route、ray map、DPI、EventSystemを検証 |
| Overlay / camera stacking | Overlayは通常画面へ描いても自動Quilt captureされない。URP stackは拒否 | 各viewと同じdepthで合成する方式を検証 |
| iOS / Linux | iOSの本SDK初期release範囲外、Linux未実装 | 対象platform方針を確定 |
| cloud Blocks連携 | 未対応 | 認証・terms確認後に別計画 |
| GPU Resident Drawer | Editor 27/27回帰、URP通常thread Player 6ケースを確認。性能優位は未確定 | MeshRenderer、shader、culling、動的更新、透明、fallbackの受入試験 |

uGUI WorldSpaceとScreen Space CameraはM3試験で確認済みです。Overlayは通常画面へ直接Quilt出力できないため、各viewと同じdepthで合成する方式を検証します。UI ToolkitのUnity 6.6 WorldSpace/targetTexture APIとM3 targetTexture fixtureを確認済みですが、入力ray map、DPI、EventSystemは未検証です。ParticlesはLit/Unlitの個別表示fixtureを確認済みで、sort、culling、soft depthなどは未確定です。

FXAA/SMAAは追加fixtureで限定確認しました。URP MSAAは4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認しました。一般Player、性能、HDRP MSAAは未検証です。TAAは一時的に無効化しています。Auto Exposureとtemporal per-view historyは、履歴とメモリを分離した別研究です。

## DX12 capture and shutdown evidence (2026-10-07)

The current change set passed a DX12 Core capture smoke, one URP plus one HDRP capture pipeline test using fake output, and a CPU-only synthetic MP4 export with FFmpeg/ffprobe 7.0.2. These results do not verify official Bridge transport or physical display output. The exporter smoke did not render an actual Unity quilt; real recording-to-export end-to-end and Editor-window interaction remain unverified. A URP and earlier HDRP Editor-stop 0xC0000005 observation remains unresolved. The Native image guard PlayMode suite passed 6/6; it is preventive and does not establish the cause or fix of the vendor crash. GitHub distribution integration is in progress. See [Windows validation](windows-validation.md) and [the evidence report](benchmarks/remaining-work-validation-20261007.md).
