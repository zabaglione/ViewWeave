[English](../vfx-graph.md)

# URP/HDRP VFX Graph の利用

[ユーザーガイド](user-guide.md) · [技術者向けガイド](technical-guide.md)

## 現在の利用範囲

URPのVFX Graphには実験的なdriver経路があり、Linear color spaceが必要です。HDRPには同等のruntime driverはありません。あらゆるgraph、effect、透明表現への対応を示すものではないため、利用前に以下の条件を確認してください。

## 設定

VFX GraphはLinear color spaceで使用してください。Unity Editorの`Project Settings > Player > Other Settings > Rendering > Color Space`を`Linear`に設定します。Unity Visual Effect Graph 17.6の公式要件でURPのGamma color spaceは非対応です。HDRPもLinear color spaceが必要です。[Unity Visual Effect Graph 17.6 system requirements](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/System-Requirements.html)、[Unity HDRP asset requirements](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/create-an-hdrp-asset.html)

`UrpVfxCameraDriver`はGammaを黙って許可しません。Gamma時はCameraやRenderTextureを生成する前に停止し、`LastError`へ`VFX Graph requires Linear color space; URP Gamma is unsupported`を設定して同じ内容をASCIIのエラーログへ出力します。Linearへ変更してからdriverを有効にしてください。

1. URPのBridge Rigを作成し、Quilt用のsource Cameraを無効にします。source CameraはRigと同じGameObjectに置きます。
2. 同じRig GameObjectへ`UrpVfxCameraDriver`を追加します。`enableDriver`を有効にし、`vfxLayers`をVFXオブジェクトのレイヤーに設定します。VFXレイヤーはsource Cameraのculling maskと重ねます。
3. `driverFieldOfView`をVFXの範囲に合わせます。初期値は90度です。VFXのboundsが補助Cameraの視錐台に入ることを確認してください。
4. 通常の自動描画では`QuiltRenderer.autoRender`を有効にします。`RenderQuilt()`を手動で呼ぶ場合は、`runWhenManualRendering`を有効にしてから呼び出します。

## 日付付き検証と実装の補足

URPのVFX Graphは、通常のQuilt用Cameraとは別の1x1補助Cameraで制御する実験的な経路を用意しています。`UrpVfxCameraDriver`はURP対応の実装です。補助CameraはVFXの通常のGPU simulationをフレームごとに一度だけ進めることを意図し、Quiltの視点ごとにsimulationを繰り返さない設計です。URPのSimple Burst driver経路は既存の検証範囲に含まれます。HDRPでは同等のruntime driverを提供していません。混在したURP/HDRPのVFX matrixでは5/20通過、15/20未通過でした。HDRPの通常Cameraは、template cloneとForceImportを使った単独canaryでStandardRequestおよび通常Cameraの差分を確認できた条件があります。未通過にはQuilt tile 0の差分が出ないpriming条件が含まれ、実行順に依存する観測結果が残っています。HDRPのpaused/live、GRD、8/48 viewsをruntime対応として扱いません。独自のVFX Graph、sorting、soft depth、複雑な透明の対応は確認していません。


HDRPのVFX Graphはruntime driverの対応範囲外です。HDRP側で通常CameraのVFX表示を確認する場合は、source Cameraを有効にし、template cloneとForceImportを含む専用診断fixtureを使ってください。診断では通常Cameraの単独測定で4423 changed pixelsを記録した一方、同じmatrix実行のStandardRequestとenabled-during-submit variantは0でした。別Editorでの単独再実行では通常CameraとStandardRequestの双方が4423でした。単独canaryのpassは複数視点のruntime対応を意味せず、実行履歴依存が残ります。

driverは視点ごとのQuilt Cameraを複製しません。source Cameraの位置と向きを補助Cameraへ反映し、VFXを通常の1x1描画で更新します。補助Cameraの生成には追加の描画負荷があります。source Cameraは無効なまま維持され、driverの終了時には補助CameraとRenderTextureを解放します。`IsRunning`と`LastError`で状態を確認できます。

VFXのsimulationは視点ごとに実行しない設計を前提にします。再生中のVFXは通常のフレーム更新で一度だけ進め、`RenderQuilt()`の呼び出し回数に合わせて手動simulationを繰り返さないでください。停止状態の再現性が必要な場合は、素材側のseed、bounds、再生状態を固定して確認します。

この経路はVFX全体の対応を保証しません。最終D3D11 PlayModeではVFX compatibility 19件中5件が通過し、14件は未通過でした。別実行の最終VFX matrixは20件中5件通過、15件未通過でした。未通過条件では少数の差分が出る場合を含め、十分な全tileの効果を確認できませんでした。HDRP primingも1件未通過です。HDRP StandardRequestは一律非対応とは断定できず、単独canaryのpassとmatrix未通過を分けて扱います。HDRPではMotion Blur、Screen Space Reflection、Screen Space Global Illumination、Volumetric Fog/Cloudsなど、HDRP側で時間的履歴を必要とする機能はQuilt用の制約対象です。VFX Graphのsorting、透明、soft depth、GPU simulation、視点ごとの履歴を利用する効果は個別に確認してください。HDRP driverは提供せず、未通過結果をpassやskipに置き換えないでください。

詳細な実測範囲と未検証項目は[互換性表](compatibility.md)と[Unity機能対応表](unity-feature-coverage.md)を参照してください。
