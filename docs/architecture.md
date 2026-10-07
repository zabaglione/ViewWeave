# Architecture

ViewWeaveは公式SDKとは独立して開発している非公式のUnity SDKです。Core、URP adapter、HDRP adapter、Bridge outputを分離しています。正式な対象はUnity 6000.6.4f1で検証しているURP/HDRPです。Built-inはサポート対象外で、CoreはStandard RenderRequest経路を要求します。

## 現在のデータ経路

Coreは専用Camera 1台を一時的に制御し、各視点をN回レンダリングします。視点ごとに平行移動とオフ軸frustumを設定し、`Graphics.CopyTexture`でGPU上のQuiltタイルへ転送します。現在は視点間でフル描画を共有していないため、1台のCameraでもN回の描画、culling、影、post processingが発生します。CPU readbackは通常経路にありません。

`IQuiltOutput`は公開Quilt RenderTextureと`QuiltLayout`を出力経路へ渡す境界です。QuiltはBridge入力仕様上必須であり、Quilt単独を低速原因とは判断できません。Coreはnative library、socket、Bridge protocolを直接扱わず、Bridgeやテスト用出力を交換できる構成を維持します。

URP/HDRPでは専用Guardと`RenderPipeline.StandardRequest`を使います。`VolumeStack`はGuardで再利用する構成ですが、視点ごとのtemporal history分離やTAAの独立性を保証するものではありません。TAAは現在の描画中だけ無効化し、終了時に復元します。Game simulation、Physics、Animationは視点ごとに繰り返しませんが、利用者のrender callbackの副作用やtemporal処理の共有は別途確認が必要です。

## 出力とBridge

Sessionは機器のprofileを動的に選択し、自動検出を主経路とします。通常の利用では公式Bridge runtimeを別途用意します。`BridgeQuiltOutput`/Legacy DirectはWindows D3D11のみです。標準の`BridgeWorkerQuiltOutput`はWindows D3D11とD3D12のEditor PlayおよびPlayerで使う経路です。D3D12ではUnityのD3D12 deviceから同じGPU上のD3D11 workerへQuiltをGPU共有し、workerが公式Bridgeへ送ります。この経路はCPU画像readbackを使いません。Edit Modeは対象外です。通常のSessionはWindowsのみで、実験中のMetal native pathは通常の出力経路ではありません。graphics backendの境界とMetalの未確定契約は[Graphics backends](graphics-backends.md)を参照してください。

WorkerはUnity render thread側のQuilt copyとworker所有のD3D11 device、3 slot共有GPU ring、worker側の描画、WM message pump、shutdown/reopenを分離します。RTX 3090 Tiでshared-resource handoff、ring reuse、typeless format、timeout、late completion、device-removal seam、generationを含むD3D11 baseline込み349 draw testを通過し、fake Bridge runtimeを使ったloader lifecycle 100 cycleも通過しました。Unity D3D12 Editor PlayはURP/HDRPのDomain Reload enabled/disabledで各2 cycleを確認し、URP/HDRP Windows Development PlayerもPreviewで検証済みです。ユーザーHDRP VFX Learning Templates projectもsnapshot 8adda27cでNative 7680x4320・48 viewsのEditor PlayとIdle/Closed・pending 0の停止を確認しました。このmatrixではGRD設定を切り替えていません。Player Native quality、全48 viewsの人手光学確認、WARP、他GPUは未検証です。Windows CIではhardware D3D12をskipします。

失敗時はworkerを`Fault`にし、submission状態が確定しない間はGPU resource参照をshutdown完了まで保持します。保留中のnative shutdownが解決しない場合はUnity Editor/Playerを再起動して回復します。これは技術的なrecovery境界で、通常のセットアップ手順には不要です。物理hotplugや任意の実機接続hangをカバーしません。

Bridge経路ではUnity render threadからのcopy、Bridge workerの描画、WM message pump、終了時の非同期排出、resizeと再openを分離して扱います。共有GPU ring bufferは3スロットです。queueが満杯の場合は新しいsubmissionをdropし、投入済みsubmissionを保持します。Workerの同一プロセス再接続と終了保留0は実測済みです。全48 viewの人手による光学確認、hotplug、複数機器の受入は未完了です。

## 確認済み範囲と未検証範囲

Unity 6000.6.4f1、Windows、D3D11でURP/HDRPの基本Lit、Bloom、透明Lit、uGUI、粒子、UI Toolkitの試験を行っています。これらの結果は個別のfixture条件に限られ、すべての素材や描画経路への対応を意味しません。HDRPではLinear color spaceが必須です。HDRP adapterはquilt viewのscopeだけ専用Volume overrideを有効にし、Motion Blur、SSR、SSGI、volumetric fog/cloudを抑え、Exposureをquilt全view共通のFixed EVにします。元stackがFixedならEVを保ち、通常Fog、Sky、BloomとMainCamera/元Volume/Project Settingsは変更しません。通常SessionでのmacOS出力は未対応です。Metal native実験は物理Mac/Bridgeで未検証です。長時間動作と性能の一般化も未確認です。

次の項目は対応範囲を決めるための検証対象です。

| 分類 | 状態 |
| --- | --- |
| Animator、SkinnedMeshRenderer、Terrain、LOD、影 | Geometry fixtureを個別確認。Animatorを含む一般素材への対応は未確定 |
| Shader Graph、Renderer Feature、Decal、Reflection Probe、Light Probe | Shader GraphとRenderer Featureは限定fixture。Decal、Reflection Probe、Light Probeの一般利用は未検証 |
| HDRP SSR、SSGI、volumetric fog/cloud、Motion Blur | quilt view中だけadapterの一時overrideで抑制。通常表示と元Volume設定は維持 |
| FXAA、SMAA、MSAA、色調補正、TAA履歴、Auto Exposure | FXAA/SMAAと一部色調補正は限定fixture。URP MSAA 1/1の個別条件を確認。HDRP Auto Exposureはquilt view中に共通Fixed EVへ一時変換。独立TAA履歴、HDRP MSAA、性能は未検証 |
| RenderTextureを使うUI、入力ray map、VideoTexture | uGUIとUI Toolkitの個別fixtureを確認。入力ray mapとVideoTextureは未検証 |
| LineRenderer、TrailRenderer、VFX Graph、複雑な透明ソート | Line/Trailは個別fixtureで限定確認。URP Simple Burstは停止6条件と通常再生4条件をPlayerで確認。HDRP、一般VFX、複雑なsortは未確定 |
| 複数機器、hotplug、RGBD、quilt layer、録画 | 未実装または未検証 |

## 性能計測の扱い

`LastCpuSubmissionTicks`はCPU描画依頼区間だけを計時し、`BridgeSubmit`を含みません。GPU時間、render thread時間、transfer時間、present latencyを表しません。視点数を減らさずにGPU負荷が下がるとは限らないため、同一機器、同一scene、同一Quilt設定で個別に計測します。Quiltのタイル転送だけを理由に全体の遅さを説明しません。

性能最適化は検証待ちのため保留しています。既存のScene View Gizmoによるcamera authoringは維持し、QuiltへのGizmo overlayは対象外です。GPU Resident Drawerや視点間共通処理の再利用は独立した検証項目です。既存の1台Camera・N回描画を置き換えた実装ではなく、結果を確認した後に採用可否を判断します。

## 実装済みの検証と残るゲート

M1の通常thread経路は、call stack、thread ID、device context、fenceの収集、Unity render threadだけのcopy、worker own device、3スロットring buffer、resize、close/reopenの実装とAPI検証まで完了しています。同一プロセス再接続を含む各120 draw、終了保留0、host thread再利用を実測しました。品質、光学、配布署名、hotplug、複数機器はrelease gateとして残ります。通常threadの実機結果をDirect経路の結果と混同しません。

M2のSession、Registry、Locator、Rig menuはコンパイルと18件のSession試験を確認しています。SessionはURP/HDRPのPreview、Balanced、NativeとGRD ON/OFFの単独6条件を48 view Playerで確認しました。固定条件のURP/HDRP品質切替で4段階を各122 completion、明示Stop/Start後120 draw、pending 0まで確認しましたが、Registryの一般運用、hotplug、複数機器、光学品質は未確認です。GRDはMeshRenderer中心の検証対象であり、Coreの1台Camera・N回描画やフルrender回数を削減する実装ではありません。cached VolumeStackは適用済みで、固定条件のCPU submission比較もありますが、GPU時間とrender thread時間は未取得です。


