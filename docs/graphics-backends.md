# Graphics backends

ViewWeaveのgraphics backendは、Unityが生成したQuiltをnative transportへ渡すproducerと、Looking Glass Bridgeへ引き渡すconsumerを分離します。通常のmanaged Sessionが選ぶ経路と、実験中のnative経路は区別します。

## Backend map

| 経路 | Producer | Consumer | 状態 |
| --- | --- | --- | --- |
| Legacy Direct | Windows D3D11 | Windows D3D11 / official Bridge | 既存のWindows D3D11経路 |
| Bridge Worker | Windows D3D11 | worker所有D3D11 / official Bridge | Windows Editor PlayとPlayerの通常経路 |
| Bridge Worker on D3D12 | Windows D3D12 resource | 同じGPU上のD3D11 worker / official Bridge | GPU共有で渡す。画像CPU readbackなし |
| Metal experiment | Unity Metal texture | official Bridge Metal API via IOSurface | native実験のみ。通常Sessionでは未対応、macOS native artifact未配布 |
| Vulkan | なし | なし | 実装・対応なし |

通常のmanaged policyはWindows D3D11/D3D12のWorkerと、D3D11のLegacy Directを対象にします。共通descriptor validationとBridge path判定もD3D11/D3D12 producerからD3D11 consumerへの経路を扱います。Windows backendはD3D11、D3D12、D3D11/D3D12 interopに分かれ、共通worker codeからtransport単位で呼ばれます。非対応platformのtransportはfailure stubです。

MetalのAPI wrapper、Unity Metal producer、submission lifetime管理は別のnative module群に隔離されています。この実験を通常のSession policyや既存Bridge outputの対応表へ含めません。Metal sourceの存在だけでは利用可能なruntimeや配布物を意味しません。

## Metal experiment and lifetime boundary

公式Looking Glass Bridge 2.6.3 ARM64 packageのheader/export調査で、`initialize_bridge`、`instance_window_metal`、`create_metal_texture_with_iosurface`、`draw_interop_quilt_texture_metal`のMetal entry pointsを確認しました。wrapperはこれらのABIへtexture descriptor相当のObjective-C object pointerとIOSurface-backed textureを渡します。Unity側は`IUnityGraphicsMetalV2`を使い、Unity command bufferのcompletion handlerでproducer側のcopy完了を観測します。

現設計は最大3枚のimmutable texture slotをboundedに保持します。Unity command-buffer completionはproducer copyの完了です。SDK drawの受理はBridge consumerがtextureの読み取りを終えた通知ではありません。Bridgeのconsumer acknowledgment、release/reuse時点、drawのthread affinity、window/runloop要件は確認できていません。そのため実験経路はslotを再利用せず、通常SessionはMetalをreadyにしません。

SDKの`copy_metal_texture`内にBridge自身のcommand bufferをcommit後`waitUntilCompleted`する実装はありますが、pluginのdraw後のconsumer read完了を示すものではありません。また、plugin実装がそのcopy APIを呼ぶことも前提にしません。ABIで確認したselectorやexportの存在を、draw完了・release契約の証明として扱いません。

Unityの公式[IUnityGraphicsMetal.h](https://github.com/Unity-Technologies/NativeRenderingPlugin/blob/master/PluginSource/source/Unity/IUnityGraphicsMetal.h)はMetal interfaceのrender-thread利用制約を記載しています。pluginはUnity render eventからMetal producerを実行します。Bridge API側のthread契約は不明です。公式Bridgeの製品情報は[Looking Glass Bridge](https://lookingglassfactory.com/software/looking-glass-bridge)を参照してください。

このnative実験は、物理Mac、Bridge runtime、実機表示、長時間のslot再利用または正常終了を検証した結果ではありません。最大3枚のbounded実験、fake contract試験、macOS CI compileが通過しても実機PASSを意味しません。macOS native artifactは配布しておらず、利用者向けsetup手順もありません。

## Scope and release gates

Vulkan実装は今回の範囲外です。性能最適化は保留中です。既存のScene View Gizmoによるcamera authoringは対象に含みますが、Quilt imageへのGizmo overlayは対象外です。

過去のWindows Bridge終了時access violationは未解決のrelease gateとして保持します。根本原因が確定するまでmain/releaseは保留です。Metal実験の進捗はこのWindows gateを解消しません。