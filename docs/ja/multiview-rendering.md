[English](../multiview-rendering.md)

# 多視点レンダリング方式の検討

現在のCoreはCamera 1台を使い、N回の`StandardRequest`で視点を描画します。Cameraを多数配置する構成ではありませんが、CPUの視錐台判定、影、描画提出、ポストエフェクトが視点ごとに繰り返されます。目標はカメラ数を減らすことではなく、視点間の共通処理を再利用し、高価なフル描画回数を減らすことです。

## 方式比較

| 方式 | 内容 | 効果の見込み | 主な未解決事項 |
| --- | --- | --- | --- |
| A: 現行baseline | 1 Camera、N回の標準要求 | 互換性の基準 | 未最適化。CPU判定、影、描画、ポストエフェクトが反復 |
| B: GRD併用 | 既存SRPへGPU Resident Drawer、CPU並列処理、instancingを適用 | CPU提出と同一meshの処理を削減。ただしN視点は残る | 更新時点、視点間culling、fallback、透明、UI、実機性能 |
| C: 独自multiview | 全視点を包含する保守的視錐台で共通cullingし、視点可視性mask、GPU常駐geometry/material、view IDとVP配列からTexture2DArrayへまとめて描画しQuiltへ組立 | CPU提出と一部共通処理を削減できる可能性 | N視点分の画素・頂点・ポストエフェクトは残る。Lit、影、透明sort、SSR、TAA、Shader Graph、VFX、UIの互換性が未解決 |
| D: 再投影 | 少数anchorのcolor/depthから多数視点を生成 | 8視点から48視点などを試験できるが、6倍fpsを保証しない | 欠損、半透明、鏡面、視点依存反射。必要視点の再描画や透明別描画でも完全解決とは限らない |

C/Dは選択式とし、A/Bのfallbackとの混在も検証します。方式を思いつきだけで公式機能の代替と扱いません。

## 共有処理の注意

影の共有は、point/spot lightのlight依存shadow mapと、directional lightのcascade・LOD・camera依存処理を分けて検討します。中央視点のshadowをそのまま全視点へ流用しません。Unity 6.6のGRDで`Light.shadowMatrixOverride`がshadow caster cullingへ効かない制約も受入条件に含めます。

XRのSingle Pass Instancingを48視点へ設定だけで流用しません。Unityの公開資料はstereo向けで、ローカルSRP 17.6の定数配列も2要素です。Unityのforkや非公開APIの反射呼び出しは採用せず、公開APIで適用性spikeを行います。

Geometry Shaderを一択にしません。D3D11の`SV_RenderTargetArrayIndex`はvertex出力とVPRT機能条件を確認し、Metalは別検証とします。

## 段階ゲート

GRD-MVPを先に実施し、Cの最小Litシーンへ進むための8視点共通culling prototypeを作成しました。限定Lit条件で6判定を通過し、feature実行1回、1回のcullingから8本のRendererListを各1 draw、depthは別attachmentであることを確認しました。固定2 Lit objectのマイクロベンチでは、8 StandardRequestのbaseline p50 1.8023 msに対し、shared cullingのp50は0.3198 msでした。これはCPU submission区間の限定値で、GPU/CPU render thread性能ではありません。48視点、影、透明、post processing、VFXは未確認です。再試験は合格しましたが、量産バックエンドには採用していません。C/DともA/B fallbackと比較し、静止・動的更新、影、透明、SSR、TAA、Shader Graph、VFX、UIの品質を個別に確認します。

参考資料: [Unity Single Pass Instancing](https://docs.unity3d.com/6000.6/Documentation/Manual/SinglePassInstancing.html)、[BatchRendererGroup](https://docs.unity3d.com/6000.6/Documentation/Manual/batch-renderer-group.html)、[Graphics.RenderMeshIndirect](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html)、[HLSL semantics](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-semantics)、[公式4 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)。
