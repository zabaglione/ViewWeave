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

## メモリ使用量と制限

補助Cameraは通常のフレーム更新でsimulationを一度進めるためのものです。Quiltのview数に合わせて手動simulationを繰り返さないでください。補助Cameraの描画負荷も発生します。

複数viewで重いeffectを使うと、instancing用の予約容量が大きくなる組合せがあります。Output Qualityを下げても十分減るとは限りません。想定同時instance数が分かるeffectでは、effectごとのCustom capacityを検討し、実際のsceneで測定してください。一律のinstancing無効化はCPU・draw負荷を増やす可能性があります。Unityの[Instancingガイド](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/Instancing.html)も参照してください。

SDKの既定4096 MB budgetは割り当て後の検知で、最初の大きな割り当てを防ぐ上限ではありません。実行中のinstancing切替にはsimulationがresetする制約があり、SDKの自動切替は実装していません。budgetの引き上げを解決策と扱わないでください。

全graph・GPU Event・透明表現・TAA・全view・長時間運転の対応保証はありません。現在のpipeline／OS別の確認範囲は[機能別対応表](unity-feature-platform-matrix.html)、個別試験・incident・失敗・sample固有の対策は[検証・調査履歴](../development/ja/vfx-graph-history.md)にまとめています。Windows Editor終了時のcrashは未解決です。
