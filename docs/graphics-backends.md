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

GitHub Actionsの2 workflow run ([37705337066](https://github.com/zabaglione/ViewWeave/actions/runs/37705337066), [37705340768](https://github.com/zabaglione/ViewWeave/actions/runs/37705340768))はcommit `054fffeb817ac8daae6ff68ed567ec7e69fe4798`で各5/5 jobs成功です。macOS jobは`macos-14-arm64`上でAppleClang 15.0.0.15000309のCXX/ObjCXXを使い、shimをbuild/linkし、common、Metal API contract、submission lifetimeの3 testsを通過しました。CMakeは`CMAKE_BUILD_TYPE`を指定していないため、Release最適化済みとは記録しません。Windows fake regressionsは4/4、transportは2/2、Linux portable/unsupported ABIは2/2、static jobも成功しました。

CI artifact `native-macos-metal-shim` (artifact 11519112992)は`liblookingglass_bridge_shim.dylib` 179952 bytesです。downloadしたfileはMach-O 64-bit ARM64 (CPU type `0x0100000c`)、SHA-256 `C06464EEEBAA3D3B934437AD9242B6DFA4C5E37E26A3187D3BE131113213980B`で、`UnityPluginLoad`とMetal provider query/begin/submit/pump exportsを含みます。依存はMetal/Foundation/IOSurfaceとmacOS system librariesで、official Bridge dylibは同梱されていません。artifactはUPMに含まれず、CIから手動取得する必要があります。

このjobはnative build/linkとfake contract testsだけを行いました。Unityを起動せず、official SDKをloadせず、実機へquiltを表示していません。物理display、provider full flow、consumer release/thread/shutdown契約も未検証です。normal Metal Sessionはunsupportedのままです。

## Managed finite experiment

`BridgeMetalExperimentalOutput`は、通常Sessionから独立した明示opt-in用componentです。macOS Play mode、64-bit process、Metal graphics device、runtime ABI layout guardを満たさない場合は開始しません。ABI guardは`ProviderStatusV1` 64 bytes (`deviceGeneration` offset 32)、`BeginRequestV1` 56 bytes (`runtimePathUtf8` offset 8、`width` offset 28)、`RenderRequest` 48 bytes (`sourceTexture` offset 8、`generation` offset 16、`columns` offset 24)を確認します。

限定試験の手順は次のとおりです。

1. Metal実験用componentをscene objectへ追加し、`rig`に既存の`ViewWeaveRig`を割り当てます。`runtimePath`にはBridge 2.6.3の`/Applications/Looking Glass Bridge 2.6.3.app/Contents/MacOS/libbridge_inproc.dylib`を指定します。native shimは別途手動で入手する必要があり、UPMには含まれません。
2. macOS EditorのPlay中にcomponentのContext Menuから`Begin Finite Metal Experiment From Rig Profile`を実行します。providerがruntimeを初期化し`RuntimeAvailable`になるまで待ちます。
3. `RuntimeAvailable`になった後、`Start Finite Metal Quilt Rendering`を一度実行します。producer submissionは最大3枚で自動停止し、3枚を超えて継続描画しません。
4. `Close Finite Metal Experiment`を実行してmanaged rigのoutput/profile/Auto Render設定を戻します。同じprocessで実験を再開する用途には使わないでください。

この試験はprocessごとのone-shotで、最大3 quiltを受け付け、Bridge consumerが解放したことを示すacknowledgmentがありません。slot/textureはretainedのままです。`Close`はmanaged rigを復元しprovider admissionを閉じますが、SDK側resourceの安全なretire、thread affinity、正常なprocess shutdownは未確定です。継続運用や繰り返し起動に使わないでください。通常SessionのMetal `interopReady`はfalseのままです。

最初のCIでは古いv5 pinとWindows fake initializer/Metal includeのcompile failureがありました。v7対応pin`522254181faf188efa8b50c3e3bf6fce720b26e4`への更新と修正後、上記2 runが成功しました。ただしCI結果はUnity runtime、official SDK load、Bridge画面、physical display、shader/format品質を検証していません。artifactは手動取得のみで、通常Metal対応を意味しません。

## Scope and release gates

Vulkan実装は今回の範囲外です。性能最適化は保留中です。既存のScene View Gizmoによるcamera authoringは対象に含みますが、Quilt imageへのGizmo overlayは対象外です。

過去のWindows Bridge終了時access violationは未解決のrelease gateとして保持します。根本原因が確定するまでmain/releaseは保留です。Metal実験の進捗はこのWindows gateを解消しません。