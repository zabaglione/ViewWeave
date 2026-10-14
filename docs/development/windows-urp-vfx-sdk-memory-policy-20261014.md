# Windows URP VFX instancing policy probe (2026-10-14)

## 判定

この試験はNUnit 1件がfailed、Unity exit code 2であり、probe全体をPASSとはしません。一方、試験中のread-only観測は、pause中に公開 `VisualEffect.allowInstancing` をfalse/trueへ変更すると、両方の遷移で粒子simulationがresetされることを示しました。Unity 6000.6.4f1 / URP 17.6 / VFX Graph 17.6の現在の公開APIでは、paused effectの状態を保ったruntime切替を確認できませんでした。SDKは自動instancing切替を導入せず、author設定と既定TAAを保持します。これは現行API/SDK制約であり、あらゆる将来実装や別APIでの原理的不可能性を主張するものではありません。

## ユーザー向けの使い分け

すべてのVFXに手動調整が必要なわけではありません。重いeffectを複数viewで描き、Auto instancingが同時instance用batch capacityを予約する組合せでは、effect単位またはcomponent単位のcapacity調整が必要になる場合があります。Output Qualityを下げても、view数やbatch reservationに結び付いた全bufferが同じ割合で減るとは限りません。想定同時instance数が分かるeffectには、その数に合わせたCustom capacityを個別に検討してください。UnityのInstancing説明では、Custom capacityは同時に存在するeffect数の見積りに使え、異なるscenarioでprofileするよう案内されています（[Unity VFX Graph 17.6 Instancing](https://docs.unity3d.com/Packages/com.unity.visualeffectgraph@17.6/manual/Instancing.html)）。多数の同一effectがあるsceneでinstancingを一律に切ることは推奨しません。

2026-10-14の元Learning Templates projectでは3つのsample専用対策を限定確認しました。SampleTexture2DとSample Skinned Meshのunique prefab variantは検証済みCustom/1 graph参照、CollisionAdvancedのunique variantはcomponent `allowInstancing=false` です。いずれもparticle capacityを変更していません。これは任意のgraph、scene、instance数への一般設定ではありません。

SDKの4096 MB history budgetは維持しています。budget observerはallocation後の検出・停止要求であり、最初の大きなallocationを硬く防ぐ保証ではありません。budget値を上げることは解決策にしません。GPU Event、複数rig、12-view TAA、Player、長時間運転への保証もこの試験からは導けません。

## 試験範囲と結果

Unity 6000.6.4f1、Windows D3D12、URP/VFX Graph 17.6、RTX 3090 Tiの隔離cloneで、Grid40 / capacity 1600のSampleTexture2Dを1 view、PlayMode、TAA off、Bridge/device outputなしで試しました。初期native proofはAuto modeの選択effectが描画可能なことを示す入力です。source assetは前後同一で、sceneは保存していません。

setter適用前のpause中はframe 32 / revision 19、391 particles、VFX time 0.05でした。false setter直後にalive countが0、timeが0となりeffectはFinished、次frameにSpawnCount 1600、frame 36で391 particlesへ戻りました。true setterでもpaused simulationは同様にresetされ、その後の観測ではtime 0.03333334、revision 35→44と再進行しました。false/true両方で、pause時に保存した画像との一致gateは通過しませんでした。

VFX batch APIでは、warm状態のtotal/max capacityは49/49、GPU bytesは21,345,076でした。false後はtotal/max 50/1、inactive batch 1、GPU bytes 21,782,696、true後は50/49、inactive batch 1でした。reservationの即時回復は見られませんでしたが、内部cacheの仕組みや因果はこの試験で証明していません。

process-global counterはVFX-off warmed baselineから graphics peak growth 34,890,100 bytes、private peak growth 65,536 bytesでした。52 samplesを取得し、memory observerの512 MiB thresholdには達しませんでした。これらはUnity process全体の観測値で、physical VRAMの測定ではありません。cleanup、history release、quilt texture release、component設定復元、source hash不変、scene no-save、clone process residual 0は確認しました。

Raw native JSONのstatusは`not-passed`、classificationは`inconclusive`、NUnitは1 failed / 0 passed、Unity exit 2です。失敗gateはpause retention、false/true pixel match、raw `unpausedContinuationGate`でした。最後のraw gateはpause前のtime 0.05とresume後の0.03333334を比較しており、比較基準が揃っていません。後続sampleにはtime/revisionの進行があるため、このgateのfalseはresume停止の証拠には使いません。raw JSONやNUnit結果は変更せず、setter時のresetは独立した直接観測として解釈しています。

## 実装判断と制限

Unityのpublic setterはcomponent settingを変えますが、この測定ではsimulation resetを伴いました。SDK sourceではURP cameraが内部history rendererを使い、logical viewごとのcamera/historyを保持します（[UrpQuiltCamera.cs](../../Packages/com.zabaglione.lookingglass.viewweave.urp/Runtime/UrpQuiltCamera.cs#L20)、[UrpViewHistoryRenderer.cs](../../Packages/com.zabaglione.lookingglass.viewweave.urp/Runtime/UrpViewHistoryRenderer.cs#L15)）。これはsource architectureの確認で、今回probeでの12-view TAA描画証明ではありません。URPのper-view TAA historyを公開APIで切り替えながらVFXを一つのcamera経路で維持する方法と、runtimeでbatch capacityを安全に直接制御する方法は確認できませんでした。現時点では、SDKがeffectごとにinstancingを自動切替するproduction changeを追加しません。GPU Event、複数rig、12 view TAA、Playerでの安全性や性能は未検証です。別途報告された不安定さがユーザー環境で解消したという確認は尊重しますが、この単一clone probeは長期安定性や全sceneの保証ではありません。

この試験専用の作業ツリーはHEAD `11312b8c6cf824d9a7ac7c8940e95cee742fa076`からのdirty/uncommitted状態でした。現在のSDK runtime correctionやreleaseを示しません。元projectの既適用sample対策はこのprobeで変更していません。Windows D3D12 / URP Editor clone / one-view TAA-offという範囲に限ります。

計5試行はそれぞれprelaunch StrictMode、0-test XML metadata、旧Process方式のmemory-baseline setup不成立、clone file conflict、今回のNUnit failedです。000358816Zでは個別counter値や例外型を保存しておらず、特定counterやAPI非対応を断定しません。全attemptのsafe artifacts、source snapshots、入力proofは[portable archive](../validation/windows-urp-vfx-sdk-memory-policy-20261014)にあります（MANIFEST SHA-256 `b0ae8f87253e14dd011cc0929a75dd43c546f85c3b9934752e8560672e420c2f`）。今回の構造化記録は[JSON](../validation/windows-urp-vfx-sdk-memory-policy-20261014.json)です。元projectでの限定観測は[frustum-memory report](windows-urp-vfx-frustum-memory-20261013.md)を参照してください。
