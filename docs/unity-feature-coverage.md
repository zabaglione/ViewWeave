# Unity feature coverage

このページは、Unity 6000.6.4f1、URP/HDRP 17.6、Windows D3D11 の ViewWeave 複数視点出力について、公開機能と実測範囲を分けて記載します。合格は個別 fixture の結果であり、同じ機能を含む全プロジェクトを保証するものではありません。

## 現在の検証集計

最新のWindows D3D11 PlayMode統合は82 cases、71 pass、11 fail、skip 0です。内訳はSession 14/14、追加互換性12/12、Geometry 10/10、HDRP 7/7、URP 8/8、HDRP priming 0/1、SRP追加4/5、SRP拡張2/2、VFX 5/14、URP driver 5/5、Samples 4/4です。SessionはPreview/Balanced/NativeとGRD ON/OFFの条件、48 view Playerを確認しています。VFXの失敗はalive 0を含み、同一Camera HDRP primingとMSAAも未通過です。詳細な正本集計は [docs/compatibility.md](compatibility.md) を参照してください。

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
| VFX Graph | URP限定実測 | URP Simple Burstの通常制御Cameraで停止6条件と通常再生4条件を確認。HDRP driverはなく、一般的なVFX、sorting、culling、soft depth、同一Camera primingは未確定。GPU simulationを一般対応と扱わない。 |
| LineRenderer / TrailRenderer | 限定実測 | 個別Line/Trail fixtureで描画差を確認。camera依存幅、履歴、順序、深度の一般対応は未検証。 |
| BillboardRenderer | 調査対象 | HDRPを含む複数視点表示は未検証。 |
| Decal / Decal Projector | 調査対象 | URP Renderer Feature と HDRP Decal の投影、深度、透明物は未検証。 |
| Reflection Probe / Planar Reflection | 限定実測 | 個別Probe fixtureで描画差を確認。probe更新、視点別反射、realtime履歴は未検証。 |

## Volume と時間履歴

| 機能 | 状態 | 実測範囲と制約 |
| --- | --- | --- |
| HDRP Bloom | 限定実測 | HDRP fixed Exposure、Directional light、明示Volumeの fixture。一般のVolume継承や複雑なstackは未検証。 |
| URP Bloom | 限定実測 | 個別Bloom fixtureで視点ごとの差を確認。一般のVolume継承、他の効果、tile seamは未検証。 |
| Color Adjustments / Tonemapping / Exposure | 限定実測 | 個別Tone/Color fixtureで描画差を確認。HDRPはLinear + Fixed Exposureを要求し、Auto/非固定ExposureはGuardで拒否。一般の色管理は未検証。 |
| Motion Blur | 非対応 | URP/HDRP Guard が temporal Motion Blur を拒否。 |
| FXAA / SMAA / MSAA | FXAA/SMAAは限定確認。MSAAはtile差分0で未確認 | MSAAのRenderTexture経路と性能を検証。 |
| TAA / temporal history | 非対応 | view render 中は TAA を無効化して終了時に復元。独立した per-view temporal history は提供しない。 |
| SSAO / SSGI / SSR | 非対応または未検証 | HDRP の SSGI/SSR は temporal としてGuardが拒否。URP/HDRPの他構成は未検証。 |
| Fog / Volumetric Fog / Clouds | 非対応または未検証 | HDRP volumetric fog/cloud はGuardが拒否。一般の fog は未検証。 |
| Depth of Field / Auto Exposure | Depth of FieldはURP/HDRPの固定条件fixtureで限定確認。Auto Exposureは未検証 | 視点ごとの焦点、履歴、Exposure更新を検証。 |

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
| D3D12 / Vulkan / Metal native output | 非対応または未検証 | 現行の対応範囲はWindows D3D11。 |

## SRP と GRD の注意点

CoreはSRPで標準 RenderRequest を使います。`useRenderRequests=false` はSRP adapter経路で拒否されます。URPはBase camera、Volume、Renderer Feature、camera stateをviewごとに検証します。HDRPはLinear、固定Exposure、temporalなVolume機能のGuardが必要です。

GRDの機能有無はpipelineの公式対応と別に検証します。現在の性能証跡はURP normal multithreaded PlayerのMeshRendererに限定され、HDRPやVFX、ParticleSystem、SkinnedMeshRenderer、Terrain、DecalのGRD性能を意味しません。

公式資料:

- [Unity 6 render pipeline feature comparison](https://docs.unity3d.com/6000.6/Documentation/Manual/render-pipelines-feature-comparison.html)
- [VisualEffect API](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/VFX.VisualEffect.html)
- [VisualEffect.AdvanceOneFrame](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/VFX.VisualEffect.AdvanceOneFrame.html)
- [URP rendering paths](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/rendering-paths-set.html)
- [Active render pipeline](https://docs.unity3d.com/6000.1/Documentation/Manual/srp-setting-render-pipeline-asset.html)

