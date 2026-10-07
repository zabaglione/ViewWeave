# Unity feature coverage

このページは、Unity 6000.6.4f1、URP/HDRP 17.6、Windows D3D11/D3D12のViewWeave複数視点出力について、公開機能と実測範囲を分けて記載します。Bridge Worker出力はWindows D3D11/D3D12 Editor PlayおよびPlayerが対象です。Legacy DirectはD3D11のみで、Edit ModeとmacOS出力は対象外です。合格は個別 fixture の結果であり、同じ機能を含む全プロジェクトを保証するものではありません。

## 現在の検証集計

旧package snapshotのWindows D3D11 PlayModeは、現行guard追加前に123件中108件pass、15件fail、skip 0でした。Core/SRP/VFX Editor targeted試験はGamma 8/8、Linear 17/17で、全件matrixの結果ではありません。Gamma MSAA/通常URPのBridge Player実行は未検証です。Gamma VFXは公式非対応です。旧D3D11統合の未通過はHDRP priming 1件とVFX compatibility 14件で、別の同時期VFX matrixは20件中5件pass、15件failでした。これらの履歴は現行D3D12 project統合や全VFX条件の成否を示しません。詳細な正本集計は [docs/compatibility.md](compatibility.md) を参照してください。

既存のDX12 VFX/Particle validation suite（driver 7件、URP Particle 3件、HDRP Particle 2件）はまだ実行していません。旧15 failureは歴史的な結果として保持し、今回の未実行testの結果とは混同しません。Shadow OnDemandの動的初回capture sourceは修正済みでAstra review GO、semantic compile PASS（source SHA 800986...）ですが、GPU gateは未実行です。

GRD の性能実測は URP normal multithreaded Player の範囲だけです。HDRP fixture で GRD 性能を測定した結果とは扱いません。自動描画 scheduler は scene rendering 後に実行しますが、機能固有の更新順序や一般的な描画対応を保証するものではありません。

判定は次の意味です。

- **限定実測**: 記載した pipeline、設定、fixture で合格を確認済みです。
- **調査対象**: Unity/SRP の公開機能として利用できますが、ViewWeave の複数視点 fixture が未合格です。
- **制約あり**: Guard、pipeline差、視点依存状態などの制限があります。
- **非対応**: 現在のSDK経路で明示的に拒否または提供していません。

## 描画、マテリアル、ジオメトリ

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| MeshRenderer と Lit material | 限定実測 | URP/HDRP の基本 Lit fixture。HDRP は Linear color space と固定 Exposure 条件。 |
| Quilt View Render Mode | 実装済み、static fixture限定実測 | Full/Half/Quarter。48-view出力Quiltを維持し、実描画数は48/24/12。未描画tileは最近傍viewを複製し、補間しない。2026-10-04 static HDRP fixtureで各modeを計測し、Half/Quarterの全48 tileは割当sourceとのRGBA画素差0。VFX/user sceneと実機光学品質は未確認。[測定範囲](dx12-performance-investigation.md)。 |
| Shader Graph Lit/Unlit | 限定実測 | URP/HDRPの個別Shader Graph fixture。shader、keyword、depth、shadowの一般対応は未検証。 |
| SkinnedMeshRenderer | 限定実測 | Geometry suite の URP/HDRP skinned 1-bone pose。複雑な skinning は未検証。 |
| Animator / Transform 更新 | 調査対象 | 固定 fixture の pose 以外の animation state 同期は未検証。 |
| LODGroup | 限定実測 | Geometry suite の ForceLOD fixture。距離による一般的な選択は未検証。 |
| Terrain | 限定実測 | Geometry suite の visibility fixture。地形の全機能、detail、tree は未検証。 |
| Directional light intensity | 限定実測 | Geometry suite の固定 light fixture。複雑な light、shadow、probe 組み合わせは未検証。 |
| Shadow mode | 限定実測 | Geometry suite の shadow mode fixture。全 shadow cascade、contact shadow は未検証。 |
| GPU Resident Drawer | 制約あり | GRD 実測は URP normal multithreaded Player の MeshRenderer 範囲。HDRP性能、他Renderer種別、透明、VFX、Terrain性能は未検証。 |
| SRP Batcher / GPU instancing | 調査対象 | 複数 view の batching と性能は未検証。 |

## パーティクル、VFX、線、デカール、プローブ

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| Shuriken ParticleSystem | 限定実測 | URP paused Lit/Unlit/live 3 と HDRP mesh paused/live 2 の fixture。sorting、soft depth、culling の一般対応は未確定。 |
| VFX Graph | URP限定実測、Linear必須 | URP Simple Burstの通常制御Cameraで停止6条件と通常再生4条件を確認。Unity VFX Graph 17.6の要件に従いGammaは非対応で、UrpVfxCameraDriverはGamma時にresource生成前に停止する。HDRP driverはなく、一般的なVFX、sorting、culling、soft depth、同一Camera primingは未確定。GPU simulationを一般対応と扱わない。 |
| LineRenderer / TrailRenderer | 限定実測 | 個別Line/Trail fixtureで描画差を確認。camera依存幅、履歴、順序、深度の一般対応は未検証。 |
| BillboardRenderer | 調査対象 | HDRPを含む複数視点表示は未検証。 |
| Decal / Decal Projector | 調査対象 | URP Renderer Feature と HDRP Decal の投影、深度、透明物は未検証。 |
| Reflection Probe / Planar Reflection | 限定実測 | 個別Probe fixtureで描画差を確認。probe更新、視点別反射、realtime履歴は未検証。 |

## Volume と時間履歴

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| HDRP Bloom | 限定実測 | HDRP Linear条件のDirectional light、明示Volume fixture。adapterの一時overrideでquilt中も通常Bloomを維持。複雑なstackは未検証。 |
| URP Bloom | 限定実測 | 個別Bloom fixtureで視点ごとの差を確認。一般のVolume継承、他の効果、tile seamは未検証。 |
| Color Adjustments / Tonemapping / Exposure | 限定実測 | 個別Tone/Color fixtureで描画差を確認。HDRPはLinear必須。quilt中のAuto Exposureは全view共通Fixed EV 12に変換し、元stackがFixedなら元EVを保つ。一般の色管理は未検証。 |
| Motion Blur | HDRPはquilt中に一時抑制 | HDRP adapter既定の一時Volume overrideでquilt中だけ無効化。通常renderと元Volume設定は維持。URPの構成は別途。 |
| FXAA / SMAA / MSAA | FXAA/SMAAは限定確認。URP MSAA 1/1で4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認。追加のLinear境界試験10/10で、このRTX GPUの2x/4x/8x対応倍率、pipeline 1x、allowMSAA無効、Deferred fallback、中心色一致と対応MSAAのedge差分を確認。Gamma側targeted MSAA 7件も確認したが専用MSAA Playerは未実施。低MSAA能力GPU、HDRP MSAA、性能は未検証 | MSAAの一般素材、Unity/Player、画質、性能を検証。 |
| TAA / temporal history | 非対応 | view render 中は TAA を無効化して終了時に復元。独立した per-view temporal history は提供しない。 |
| SSAO / SSGI / SSR | HDRP SSGI/SSRはquilt中に一時抑制 | HDRP adapter既定のoverrideでquilt view中だけ無効化し、scope終了時に戻す。URP/他構成は未検証。 |
| Fog / Volumetric Fog / Clouds | HDRP volumetric effectsはquilt中に一時抑制 | HDRP volumetric fog/cloudはquilt view中だけ無効化。通常のFogとSkyは維持する。 |
| Depth of Field / Auto Exposure | Depth of Fieldは限定確認。HDRP Auto Exposureはquilt共通Fixed EVへ一時変換 | HDRPはEV 12を既定にし、元stackがFixedならそのEVを使う。URP Auto Exposureは未検証。 |

## Quilt保存と録画

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| 静止Quilt PNGとmetadata | 実装済み、D3D11とDX12 fake-outputで限定実測 | Quilt PNG、JSON sidecar、中央view PNGを保存。既存D3D11 URP/HDRP PlayMode各2/2。DX12 Core smokeとURP/HDRP各1 capture testでcentral metadataとview mappingを確認。Bridge transport/実機保存は未検証。 |
| Quilt PNG連番 | 実装済み、D3D11とDX12 fake-outputで限定実測 | Recorder suiteで10/25 fps各3枚、URP 1〜240 fpsの開始/停止とsimulation時間復元を確認。DX12 URP/HDRP各1 capture testではPause/Resumeを確認。追加renderは行わず、PNG読戻し/圧縮中はUnity frameが停止。Quilt file再生は未実装。 |
| 中央2D PNG | 実装済み、DX12 fake-output限定実測 | 完成quiltの中央論理viewを保存し、sidecar/mappingをURP/HDRP testで確認。実Bridge中の保存と実機表示は未検証。 |
| 無音MP4 export | 実装済み、fake process suite + FFmpeg 7.0.2 CPU-only smoke | 64x48の合成8x6/48-view quilt PNG 3枚を30fps H.264/yuv420pへ出力。ffprobe、Unicode/空白path、full-layout sidecar、no-overwriteを確認。実Quilt recordingからexportまでのend-to-endとEditor window操作は未確認。 |
## UI とカメラ

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| uGUI World Space | 限定実測 | 統合 fixture で合格。入力、複雑なCanvas、深度競合は未検証。 |
| uGUI Screen Space Camera | 限定実測 | 統合 fixture で合格。cameraごとのviewport、入力は未検証。 |
| uGUI Screen Space Overlay | 制約あり | 通常画面のOverlayは自動Quilt capture対象ではない。各viewと同じdepthで合成する方式は未検証。 |
| UI Toolkit target texture | 限定実測 | Unity 6.6 targetTexture fixture で合格。ray map、DPI、EventSystem、入力は未検証。 |
| Camera.Render | 非対応 | SRP経路では使用しない。Coreは `useRenderRequests=false` を拒否する。 |
| SDK RenderRequest | 限定実測 | SRPでは Standard RenderRequest を必須とし、pipeline adapter/guardで検証する。1 CameraでN回要求するコストは残る。 |
| URP camera stack | 非対応 | Base cameraのみ。Overlay camera stackはGuardが拒否。 |

## SRP拡張、XR、特殊経路

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| URP Renderer Feature / Render Graph | 限定実測 | 検証用marker passで8視点の差分と8回実行を確認。一般のresource lifetime、pass順、camera filteringは未検証。 |
| HDRP Custom Pass / Render Graph | 限定実測 | 検証用marker passで8視点の差分と8回実行を確認。一般のCustom Pass camera filteringとresource管理は未検証。 |
| Custom post-processing | 調査対象 | view間の履歴共有と順序は未検証。 |
| XR single-pass / multipass | 未検証 | XRのstereo viewとquilt gridは別モデル。 |
| DXR ray tracing / HDRP path tracing | 未検証 | ray/path tracingの履歴とN回render requestを保証しない。 |
| D3D12 Bridge Worker / Vulkan / Metal output | D3D12はWindows Bridge WorkerのEditor Play/Player経路に対応。VulkanとMetalは対象外または未検証 | D3D12 Workerは同一GPU上のD3D11 workerとGPU資源を共有する。Legacy DirectはD3D11のみ。 |

## SRP と GRD の注意点

CoreはSRPで標準 RenderRequest を使います。`useRenderRequests=false` はSRP adapter経路で拒否されます。URPはBase camera、Volume、Renderer Feature、camera stateをviewごとに検証します。HDRPはLinear必須です。adapterはquilt scope中に一時Volume overrideを使い、Motion Blur、SSR、SSGI、volumetric fog/cloudを抑えてAuto Exposureを全view共通Fixed EVにします。scope外のMainCameraとVolume設定は保たれます。

GRDの機能有無はpipelineの公式対応と別に検証します。現在の性能証跡はURP normal multithreaded PlayerのMeshRendererに限定され、HDRPやVFX、ParticleSystem、SkinnedMeshRenderer、Terrain、DecalのGRD性能を意味しません。

公式資料:

- [Unity 6 render pipeline feature comparison](https://docs.unity3d.com/6000.6/Documentation/Manual/render-pipelines-feature-comparison.html)
- [VisualEffect API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/VFX.VisualEffect.html)
- [VisualEffect.AdvanceOneFrame](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/VFX.VisualEffect.AdvanceOneFrame.html)
- [URP rendering paths](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/rendering-paths-set.html)
- [Active render pipeline](https://docs.unity3d.com/6000.1/Documentation/Manual/srp-setting-render-pipeline-asset.html)
