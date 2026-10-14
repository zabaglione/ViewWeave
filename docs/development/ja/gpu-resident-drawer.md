[English](../gpu-resident-drawer.md)

# GPU Resident Drawerの検証状況

> 対象読者: Unity／描画基盤の技術者。このページは本文記載の技術的な設計・実装・手順を扱います。利用者向けの導入・運用手順は[ユーザーガイド](../../ja/user-guide.md)、技術文書一覧は[技術者向けガイド](../../ja/technical-guide.md)を参照してください。

本SDKでは、Unity 6.6のGPU Resident Drawer（GRD）を独自multiview高速化とは別に検証します。URPでは、旧LateUpdate自動描画とGRDを組み合わせた条件で移動反映の遅延を確認し、現在の自動描画タイミングで修正しました。これは旧描画タイミングとの組み合わせに関する結果であり、GRD自体の一般的な遅延を示すものではありません。GRDのdraw call最適化と、Bridge interopのスレッド停止は別問題です。GRD有効化による速度優位やBridge停止の解決は確認していません。

Unity `6000.6.4f1`、Windows D3D11でEditor GRD/VolumeStack回帰27/27、URP通常thread PlayerのGRD OFF/ON・1/8/48視点6ケースを確認しました。HDRP PlayerでもPreview、Balanced、NativeのGRD OFF/ON各6条件を48 viewで確認し、GRD ONの76 renderer、3,648 visible instancesを記録しました。これは描画正確性の確認であり、HDRP性能の測定ではありません。別の固定条件の性能比較では48視点CPU submission p50がGRD Off 17.3311 ms、On 15.0732 msで、Onが約13.0%低くなりました。GPU時間とrender thread時間は欠測で、一般的な速度優位は未確定です。MetalとHDRPのGRD性能は未検証です。Bridge Worker通常threadは別のGPU preview継続確認を行いましたが、GRDとの組み合わせた性能値はありません。

## Unity公式機能との関係

最新のGRD/VolumeStack回帰は27/27 passed、skip 0です。URP/HDRPのvalidation stack reuseとMotion Blur toggleを含みます。これは正確性ゲートであり、速度保証ではありません。Player benchmarkの固定条件と実測値は [performance.md](performance.md) に記載します。paired tableはVolumeStack cache変更後の条件を含みますが、キャッシュ変更単独の因果やGPU速度を示すものではありません。

Unity 6.6の選択表では、GRDとBatchRendererGroup（BRG）はmultithreaded対応で、SRP Batcherと併用できます。GRDはMeshRendererのデータをGPU常駐させ、BRG、GPU instancing、CPU並列culling jobsを使う仕組みです。48視点を自動的に1 drawへ変換する機能ではなく、Camera APIを`Task.Run`で実行できる仕組みでもありません。

URP 6.6ではForward+とDeferred+の両方を対象候補とします。使用するRendererがいずれかであること、SRP Batcherを有効化し、GRDのInstanced Drawingを使い、BRG VariantsをKeep All、static batchingを無効化することを確認します。compute対応はD3D11、D3D12、Vulkan、Metalを機種ごとに確認します。これはWindows D3D11 Directに限定したBridge経路とは別の条件です。

HDRP 17.6にもGRDがあります。HDRP AssetのInstanced DrawingとBRG Keep All、compute対応、MeshRenderer条件を確認します。

参考資料: [Unityの方式選択](https://docs.unity3d.com/6000.6/Documentation/Manual/optimizing-draw-calls-choose-method.html)、[URP GRD](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/gpu-resident-drawer.html)、[URP互換オブジェクト](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/make-object-compatible-gpu-rendering.html)、[URP GRD性能](https://docs.unity3d.com/6000.6/Documentation/Manual/urp/gpu-resident-drawer-performance.html)、[HDRP 17.6 GRD](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/gpu-resident-drawer.html)。

## 適用条件と制約

対象はMeshRendererとDOTS対応shaderです。MaterialPropertyBlock、per-instance描画callback、プローブなどには制約があります。視点の描画中に対象オブジェクトを動かさない条件を確認します。フレーム間の移動を一律禁止する意味ではありません。

SkinnedMeshRenderer、ParticleSystemRenderer、uGUI、UI ToolkitはGRDのMeshRenderer対象とは別経路です。標準描画fallbackとの共存、画質、生成・破棄、透明のsortを検証します。GRD自体はdepth interpolationではないため、透明を一律拒否するとは決めません。

GRDはCPU draw bound、多数の同一meshとvariantで効果が期待されます。GPU boundでは追加GPU負荷により遅くなる場合があります。比較はGRD OFF/ONと実際のcounter・Burst worker・Profilerで行い、toggleだけでは適用成功としません。

## 本SDKでの仮説と検証ゲート

17.6のローカル依存コードから、Coreの`GPUResidentDrawer.InsertIntoPlayerLoop`が`PostLateUpdate.FinishFrameRendering`直前に`WorldProcessor.Update`を行うことを確認しました。旧来のLateUpdate自動描画ではこの更新より前に描画されるため、URPの8視点・GRD有効条件で動的対象の移動が反映されない結果を確認しました。現在の自動描画は`PostLateUpdate.FinishFrameRendering`直後にスケジュールされます。`renderInLateUpdate`は互換propertyとして残りますが、LateUpdateでの自動描画には使用しません。

Editorの`EditorFrameUpdate`は同じCamera IDの再登場をframe境界と推定するため、48回の`StandardRequest`でper-view処理が増える可能性があります。PlayerとEditorを分けて計測します。URPのUniversalRendererRenderGraphがcamera entity IDを使うocclusion設定を参照するため、1 Camera 48視点ではocclusion view履歴共有の可能性も仮説として扱います。

初期試験は、A:通常SRP・GRD OFF、B:GRD ON・occlusion OFF、C:履歴安全確認後のGRD ON・occlusion ONです。Bridge出力を切り離し、1/8/48視点、静止同一mesh多数、異なるmesh、動的spawn/despawn、移動、遮蔽開閉、影、transparent、Skinned、particles、UI、postprocessを比較します。現時点ではURPのBeforeFinish/AfterFinish条件と48視点のGRD ON/OFFを確認済みです。速度比較は別途、実際のcounter・Burst worker・Profilerで行い、toggleだけでは適用成功としません。最終確認はUnity `6000.6.4f1`のWindows D3D11 Editor batchによる描画正確性チェックです。WindowsのURP Forward+/Deferred+とHDRP、通常graphicsとDirectを分け、Mac Metalは別hostで検証します。

受入条件は、動的更新時点が正しいこと、視点間cullingが正当であること、callbackが1 frame 1回のsimulationを保つこと、誤消失がないことです。高解像度、独自multiview、reprojectionの優先度はGRD試験後に判断します。M1のBridge修正とは同時進行でき、依存しません。Unity依存forkや非公開メソッドの反射呼び出しは採用しません。
