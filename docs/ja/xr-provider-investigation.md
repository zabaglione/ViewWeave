[English](../xr-provider-investigation.md)

# XR Display Providerの調査とViewWeave適用案

> 対象読者: Unity／描画基盤の技術者。このページは本文記載の技術的な設計・実装・手順を扱います。利用者向けの導入・運用手順は[ユーザーガイド](user-guide.md)、技術文書一覧は[技術者向けガイド](technical-guide.md)を参照してください。

調査日: 2026-10-07

## 現在の方針

ユーザー方針により、XR方式の採用判断、prototype作成、既存経路からの移行は一旦保留しています。この資料は将来検討用の調査記録です。Quilt保存とカメラ調整機能の仕上げは継続します。

## 結論

通常のシーンに置いたCubeなどをLooking Glassに表示するため、各オブジェクトへ個別コンポーネントを追加する必要はありません。必要なのは、既存シーンのどのCameraを基準に多視点を描くかを選び、そのCameraの姿勢・投影を各表示視点に変換する、小さな表示側コンポーネントです。これをUnityの`XRDisplaySubsystem`から呼ばれる`XRDisplayProvider`として作れば、既存CameraやScene構成を大きく変えずに接続できる設計です。

ただし、調査時点のViewWeaveはXR Provider方式ではありません。通常のMain Cameraの画像を48枚へ拡大しているのでもありません。Rigが保持する専用の無効Cameraに48個の`StandardRequest`を送り、各視点を描いてQuiltを組み立てます。XR経路への移行、性能向上、HDRP/VFXの互換性はまだ実証していません。

## 既存Cameraを利用するイメージ

| | 現在の方式 | XR Display Provider方式の案 |
| --- | --- | --- |
| 利用者の準備 | シーンCameraにViewWeave Bridge Rigを付ける | Cameraを選択し、表示Providerを割り当てる小コンポーネントを追加する案 |
| 多視点描画 | ViewWeaveの専用Cameraから48回のrender request | Unity XRのrender passに多視点のposition/projectionを渡し、既存の描画ループに参加 |
| Cube等の通常オブジェクト | 通常通り表示。専用shader追加不要 | 同様に通常Rendererを描く設計が可能。各オブジェクトにXR用componentを付ける必要はない |
| 描画回数・性能 | 現在は48視点を個別に描画 | cullingなどの共通処理を共有できる可能性。ただし48視点のGPU描画が自動で1回になる意味ではない |
| 互換性・リスク | 現在のURP/HDRP経路 | URP/HDRPのrender pass、VFX、Volume、screen-space/history効果、native display lifecycleを検証する必要がある |

Providerでは、既存Cameraのposeとprojectionを元に、表示に必要な多視点のpose/projectionを生成してUnityへ渡せます。どのCameraを使うか、表示中心に対応するzero-parallax/focus distance、view coneの幅と単位、シーン上のscale、平面モニターとLooking Glassのどちらへ出すかはユーザーが指定できる必要があります。通常の`Camera`と`XRDisplaySubsystem`の橋渡しだけをする設定componentから始めるのが現実的です。

この構成は「OpenXR runtimeを自作する」ことと同じではありません。Unity XRのrendering providerを実装し、最終Quiltを既存の公式Looking Glass Bridgeへ送るバックエンドを維持する構成が可能です。OpenXRは別途、tracked headsetやXR入力を使う必要が生じた場合の選択です。

## Unity標準機能の境界

UnityのXR Originはtracking spaceとUnity scene spaceの変換を扱うものです。非追跡のライトフィールド表示で必須ではなく、tracking座標の原点整理が必要な場合に使う補助になります。[Unity XR Origin](https://docs.unity3d.com/6000.0/Documentation/Manual/xr-origin.html)

UnityのSingle Pass Instancedは標準的なXR左右2眼をtexture arrayへ描く経路です。Unity 6000.0の公開資料は2眼のinstance描画を説明し、導入中のCore 17.6のXRSystemにもarray depth 2の制約が確認されています。これは48視点をそのまま処理できる根拠になりません。([Unity Single Pass Instanced](https://docs.unity3d.com/6000.0/Documentation/Manual/SinglePassInstancing.html), [Unity Graphics XRSystem source](https://github.com/Unity-Technologies/Graphics/blob/master/Packages/com.unity.render-pipelines.core/Runtime/XR/XRSystem.cs))

一方で、UnityのXR Render Passには`cullingPassIndex`があり、別のviewが同じculling passを使う関係を示せます。これはculling情報共有の仕組みを設計する参考になりますが、48視点のunion cullingが正しいことや、共有による性能向上が自動的に得られることを保証しません。[XRRenderPass.cullingPassIndex](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/XR.XRDisplaySubsystem.XRRenderPass-cullingPassIndex.html)

既存のUnity native XR headerと公開Provider例からrender-passに複数viewを渡す概念は確認できました。ただし、Unity 6000.6 Editorに同梱されるヘッダーでViewWeaveが必要とする最大view数を確定できていません。したがって「XR Providerなら48 viewが標準対応」「最大8 view」とは断定せず、対象Unity版の実APIとHDRP経路を使ったprobeで確認します。

## DisplayXRの公開設計例

[DisplayXR ADR-007](https://github.com/DisplayXR/displayxr-unity/blob/main/docs~/adr/ADR-007-render-path-by-view-count.md)は2026-07-03付のAcceptedな設計決定として、少数viewではXR Provider、多数viewではQuilt専用経路を使い分ける案を記録しています。ADR自身がQuilt側はまだ未実装としており、設計資料であって、HDRP 48視点の成功・性能実績ではありません。[DisplayXR XR Display Provider architecture](https://github.com/DisplayXR/displayxr-unity/blob/main/docs~/architecture/xr-display-provider.md)も構成の参考になります。同リポジトリの資料には旧案や未解決項目も含まれるため、個々の図・主張は採用済み実装と区別します。

この例は、XR Providerを使うかどうかを「既存シーンとの統合が簡単か」だけでなく、「視点数とRender Pipelineの費用」で分岐させる設計を支持します。ViewWeaveでの48視点経路をProviderへ一括置換する根拠にはなりません。

## 再開時に必要な確認順

1. Unity 6000.6向けXR SDKのnative header/ABIを取得できるか確認する。UnityのXR SDK資料はnative SDK headersの入手方法を案内しているため、Editorへ同梱されていると仮定せず、対象版の取得経路と必要なview数・render lifecycle・HDRP接続条件を確認する。([Unity XR SDK](https://docs.unity3d.com/ja/6000.0/Manual/xr-sdk.html))
2. 既存Cameraを基準にした2視点XR Provider prototypeを作り、通常のCubeが表示されることを確認する。
3. 同じScene・解像度・2視点数で、XR Providerと2回のStandardRequestを比較し、CPU/GPU/end-to-end時間と画質を測る。2視点XRと既存の48回StandardRequestの差を、XR Providerの性能改善率として扱わない。
4. Editor PlayとPlayerをそれぞれ確認し、その後URP/HDRPでVolume、VFX、透明、screen-space効果、履歴依存の見え方と性能を照合する。
5. native APIのview数上限とRender Pipeline側の制約、culling/shadow/work submissionを共有した場合の正しさを確認してから、48視点へ拡張するか判断する。48視点比較をする場合も、同じScene・解像度・48視点数で基準経路と比較する。

現在のXR Provider prototypeもこの比較も未実施です。既存のQuilt captureとギズモ機能の実装はXR方式の選定と独立して進行し、今回の調査はXR経路へ変更していません。
