# URP VFX Graph の利用

URPのVFX Graphは、通常のQuilt用Cameraとは別の1x1補助Cameraで制御する実験的な経路を用意しています。`UrpVfxCameraDriver`はURP専用で、HDRP用driverはありません。現在確認したのは、URPのSimple Burst素材を通常制御Cameraで停止状態6条件と通常再生4条件で描画した範囲です。HDRPの診断fixtureは未通過で、SDKのHDRP driverは提供していません。独自のVFX Graph、sorting、soft depth、複雑な透明の対応は確認していません。

## 設定

1. URPのBridge RigまたはQuilt Rigを作成し、Quilt用のsource Cameraを無効にします。source CameraはRigと同じGameObjectに置きます。
2. 同じRig GameObjectへ`UrpVfxCameraDriver`を追加します。`enableDriver`を有効にし、`vfxLayers`をVFXオブジェクトのレイヤーに設定します。VFXレイヤーはsource Cameraのculling maskと重ねます。
3. `driverFieldOfView`をVFXの範囲に合わせます。初期値は90度です。VFXのboundsが補助Cameraの視錐台に入ることを確認してください。
4. 通常の自動描画では`QuiltRenderer.autoRender`を有効にします。`RenderQuilt()`を手動で呼ぶ場合は、`runWhenManualRendering`を有効にしてから呼び出します。

driverは視点ごとのQuilt Cameraを複製しません。source Cameraの位置と向きを補助Cameraへ反映し、VFXを通常の1x1描画で更新します。補助Cameraの生成には追加の描画負荷があります。source Cameraは無効なまま維持され、driverの終了時には補助CameraとRenderTextureを解放します。`IsRunning`と`LastError`で状態を確認できます。

VFXのsimulationは視点ごとに実行しない設計を前提にします。再生中のVFXは通常のフレーム更新で一度だけ進め、`RenderQuilt()`の呼び出し回数に合わせて手動simulationを繰り返さないでください。停止状態の再現性が必要な場合は、素材側のseed、bounds、再生状態を固定して確認します。

この経路はVFX全体の対応を保証しません。HDRPの診断fixtureは未通過で、SDKのHDRP driverは提供していないため、HDRP対応は未確認です。VFX Graphのsorting、透明、soft depth、GPU simulation、視点ごとの履歴を利用する効果は個別に確認してください。

詳細な実測範囲と未検証項目は[互換性表](compatibility.md)と[Unity機能対応表](unity-feature-coverage.md)を参照してください。
