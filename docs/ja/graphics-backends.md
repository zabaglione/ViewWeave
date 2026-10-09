[English](../graphics-backends.md)

# 描画backend

ViewWeaveのgraphics backendは、Unityが生成したQuiltをnative transportへ渡すproducerと、Looking Glass Bridgeへ引き渡すconsumerを分離します。通常のmanaged Sessionが選ぶ経路と、実験中のnative経路は区別します。

## Backend一覧

| 経路 | Producer | Consumer | 状態 |
| --- | --- | --- | --- |
| Legacy Direct | Windows D3D11 | Windows D3D11 / official Bridge | 既存のWindows D3D11経路 |
| Bridge Worker | Windows D3D11 | worker所有D3D11 / official Bridge | Windows Editor PlayとPlayerの通常経路 |
| Bridge Worker on D3D12 | Windows D3D12 resource | 同じGPU上のD3D11 worker / official Bridge | GPU共有で渡す。画像CPU readbackなし |
| Edit Mode Rig preview | Windows Editor D3D11/D3D12 | LocalはEditor UI、deviceはBridge Worker / official Bridge | 明示開始するEditor専用経路。通常Sessionとは別。Native pause/resumeはCTest 10/10 PASS。実URP D3D12で一度のpause/resume表示を確認。一般化した実機検証は未完了 |
| Metal experiment | Unity Metal texture | official Bridge Metal API via IOSurface | native実験のみ。通常Sessionでは未対応、macOS native artifact未配布 |
| Vulkan | なし | なし | 実装・対応なし |

次図はサポート対象の通常Sessionと、独立したMetal実験を区別します。Metal枝は通常Sessionへ接続せず、Windowsのshutdown gateにも入りません。

```mermaid
flowchart TD
    Session["通常managed Session"] --> Windows["Windows D3D11 / D3D12"]
    Windows --> Worker["Bridge Worker経路"]
    Windows --> Legacy["D3D11 Legacy Direct経路"]
    Worker --> Bridge["公式Bridge runtime"]
    Legacy --> Bridge
    Metal["macOS Metal component"] --> Gate["明示opt-in / 最大3 Quilt"]
    Gate --> Contract["consumerのrelease契約は未確認"]
```

通常Session表示はWindowsのBridge WorkerまたはD3D11 Legacy Directを使います。Edit Mode Rig previewは独立したEditor経路で、明示開始します。Local modeはpreview QuiltをEditor内へ表示し、device modeはWindows EditorのBridge Worker経路を使います。いずれもPlay Modeや対象Rigの通常Sessionを開始しません。Native Release DLLのpause/resume protocolはdrain-before-hide、同一session resource保持、一時停止中からの完全終了を含むCTest 10/10に合格しました。このfake runtime/test adapterによる結果だけでは実機表示や光学的な見え方を検証できません。隔離URP/HDRP local previewのmanaged testはpause/resume、一時停止中のFOV変更、完全Closeを2/2で確認しました。2026-10-09、利用者はWindows D3D12の実URP `test.unity` sceneで一度のPause/Resume後にsceneが再表示され、表示対象の移動とUndoも反映されたことを確認しました。この一つのscene・一回の操作は反復運用やHDRP機器での動作を示しません。実寸scale、画像品質、性能も未検証です。Metal枝は手動artifactと実験専用componentを要し、連続運用やUPM配布を示しません。

Windowsのpersistent Worker hostは、producer adapterが同じ場合に限りrender Session間でD3D11 deviceとimmediate contextを再利用します。Session Stopでsessionごとのresourceは閉じますが、process全体のSDK ownerがhost deviceを保持します。adapter変更またはdevice removalはrestartが必要なerrorとして拒否し、deviceを暗黙に作り直しません。EditorまたはPlayer process終了時のSDK uninitializeが成功した後にだけhost deviceを解放します。

通常のmanaged policyはWindows D3D11/D3D12のWorkerと、D3D11のLegacy Directを対象にします。共通descriptor validationとBridge path判定もD3D11/D3D12 producerからD3D11 consumerへの経路を扱います。Windows backendはD3D11、D3D12、D3D11/D3D12 interopに分かれ、共通worker codeからtransport単位で呼ばれます。非対応platformのtransportはfailure stubです。

## 明示的なEdit Mode Rig preview

`Start Local Edit Mode Preview`と`Start Looking Glass Device Preview`は、利用者が開始を要求した場合だけEditor-owned producerを作ります。一時producerはsource Rig設定を使いますが、通常Bridge Sessionを開始したり所有したりしません。開始・停止によってsource Rigや保存sceneを変更しません。device previewはWindows Editor D3D11/D3D12に限られ、公式Bridge runtimeが必要です。D3D11/D3D12とURP/HDRPのdevice fixtureは各組み合わせで1/1 PASSしました。各fixtureは3840x2160 display profile、7680x4320 native output Quilt、8x6/48 viewsを使い、start/restartで各1 submit/1 completion、Stopでnative/retention pending 0を確認し、test processもexit code 0で終了しました。D3D11はReadbackTail、D3D12はFenceAndReadbackを使います。最終start-guard後の代表D3D12 fixtureも1/1 PASSし、実D3D12 producerからD3D11 consumerへの送信、Start/Restart各1 submission/1 completion、Stop後pending 0（0.4s/0.2s）を確認しました。手動の光学確認は未実施です。local previewのURP/HDRP GPU fixtureは2/2 PASSしました。source-controlled/hidden-producer両modeで8 viewのgreen geometryを描画し、layout変更時にproof後の旧RenderTexture retireと再構築、Stop/readback proof、retention pending 0を確認しています。local MSAA fixture設定はURP view=4/resolve=1、HDRP view=1/resolve=0でした。hidden Editor reload fixtureではDomain Reload前のactive preview texture、reload後のinactive/pending 0、明示restart成功を確認しました。previewなしURP control（同じ一時URP asset設定）、reloadなしactive preview、reload後restartしたactive previewの3本のFile > Exit probeでは、各fixture helperが`Stop()`と同期`RequestStop(true)`を先に呼び、その後`wantsToQuit` gateが`ready=true`、`pending=0`、`nativePending=0`を報告しました。これはhelper介入後のproof/recovery確認で、標準observer単独の自動停止確認ではありません。3本すべてUnity test processがteardown中（末尾の`Cleanup mono`/`CodeReloadManager destroyed`後）に同じ`0xC0000005`で終了し、normal exit code 0は未確認です。previewなしcontrolも同じcodeとなったため、共通症状の原因をpreviewに帰属しません。これらはFile > Exitの検証であり、own-window closeを直接試した証拠ではありません。自動更新は0.2秒周期を目標（5Hz）とします。明示的なrefreshやparameter編集は、それより早いrenderを要求することがあります。これはschedule設定であり、性能測定結果ではありません。

`Pause Preview`は新しいsubmitを止め、進行中の処理をdrainしてからdevice windowを隠します。同じproducer、RenderTexture、Worker session、所有権lockを保持し、`Resume Preview`は同じsessionを再表示します。Local previewは最後の表示画像を保持します。一時停止中も構造設定はlockされます。`Close Preview`はproofを伴う完全なdrainを行って所有権を解放します。preview windowのRig切替・close、Play移行、reload、Editor終了も一時停止ではなく完全終了を使います。Pause/Close分離前のfull-stop fixtureはresource retirementを検証したもので、新しいpause/resume経路は検証していません。2026-10-09、実URP Windows D3D12の`test.unity` sceneで一度のpause acknowledgementとresumeを観測し、同じworker handle/generationを保持、pause中counterは変わらず、resume後にsubmission/completionが進みました。利用者はresume後のscene、表示対象の移動、Undoが表示されたと確認しています。dropとerrorはありませんでした。これは一つのscene・一回の操作で、反復やHDRP機器での挙動、実寸scale、画像品質、性能は未検証です。producer完了と必要なGPU/worker所有権proofが揃うまでGPU resourceを保持します。一時Play/Edit遷移fixtureは1/1 PASSし、Play移行後にpreview inactive・pending 0、Edit Mode復帰時に自動再開しないことを確認しました。Authoring windowのRig切替/close lifecycle-helper fixtureも1/1 PASSし、停止後inactive/pending 0でしたが、実window close callbackやFile > Exitを直接試した証拠ではありません。URP/HDRP local GPU fixtureではretirement、再構築、Stop/readback proof、retention pending 0を確認し、D3D11/D3D12 device fixtureではStop/Restart後のnative/retention pending 0を確認しました。hidden Editor fixtureではDomain Reload cleanup後の明示restartも確認しました。3本のFile > Exit probeではfixture helperが`Stop()`と同期`RequestStop(true)`を先に呼び、gateが`ready=true`、`pending=0`、`nativePending=0`を確認しました。これは標準observer単独の動作確認ではありません。previewなしcontrolを含め全processがteardown時に`0xC0000005`で終了し、正常終了code 0は未確認です。原因をpreviewへ断定せず、own-window closeの直接証拠とも扱いません。これはEditor preview固有のlifetimeで、下記の通常Session shutdown検証とは別です。

## Windowsのshutdownとpackage更新

Play Modeの通常Stopとsession再起動はfinal shutdown latchを立てません。所有中のworker/sessionをdrainし、GPU完了とnative releaseを確認してから参照を解放します。Windows EditorまたはWindows Playerのprocess終了では別のfinal shutdown gateを使い、admissionを閉じ、active outputを停止し、native contextsの回収とpersistent worker hostのjoinを確認してから終了を許可します。Legacy D3D11 outputはsessionごとのclose ledgerが`ClosedSafe`を返し、ACKが成功してからGPU fence/readback proofと合わせてtransfer textureを解放します。global SDK idleだけでは解放しません。

Windows Editorの通常quit callback内で、Bridge専用の同期gateを実行します。単調時計の10秒polling予算を使いますが、個別のUnity/native API呼び出しは中断できず、絶対的な終了時間上限ではありません。callback自身はCoordinatorのPumpOnce、発行済みGPU readbackのUpdate、1ms sleepだけを行います。Unity全体のEditor updateやUI message pumpは再入しません。Native host join、閉じたcontextの回収、ReaperとPlaybackのGPU proofが揃わない場合はquitをキャンセルし、referencesを保持してadmissionを閉じたままにします。自動再試行はせず、後の明示的quit要求でstatusを再確認します。完了後は同じ終了要求を許可し、window close/menu commandは再発行しません。save確認をCancelした場合、irreversibleなfinal admissionは再開しないためBridge再利用前にEditor再起動が必要です。quit callbackを迂回する終了では従来の5秒bounded fallbackを別に試しますが、完了を証明できなければ成功扱いしません。V6の単一Windows D3D12 Editor fixtureで、作成済みかつGPU proof前のowned textureを持つ状態から実際のGUI終了要求一回を通し、flush 1/1、native/managed completion、Editor自然終了code 0を確認しました。これは一つの8-view 512x256 fixtureであり、save確認cancelや一般的な性能/光学品質の確認ではありません。

Final shutdown hookはWindows Editor/Playerだけに適用し、asset import worker processでは登録しません。macOS Metal実験とunsupported platformには触れません。V6ではarmed後も通常描画を続け、実GUI終了要求一回を通して自然終了しました。これは過去のaccess violation原因を除去した証明ではありません。save確認cancel、48-view optical quality、性能、physical displayは未検証です。現在のnative/managed test結果とEditor fixtureの検証状態は[Shutdown validation](../shutdown-validation-20261008.md)に記録しています。

Native DLLまたはexportが変わるUPM packageを更新する前に`Close Preview`を使い、通常Sessionも停止して所有権を解放してください。Package Managerでpackageを更新した後、managed assemblyとnative shimを同じversionで読み込むためUnityを再起動します。The process-wide SDK owner and loaded native image remain until Editor shutdown. If the loaded DLL prevents replacement, exit and restart Unity before using Bridge again. shutdown traceの使い方とnative test境界は[Native shutdown trace](../native-shutdown-trace.md)を参照してください。

MetalのAPI wrapper、Unity Metal producer、submission lifetime管理は別のnative module群に隔離されています。この実験を通常のSession policyや既存Bridge outputの対応表へ含めません。Metal sourceの存在だけでは利用可能なruntimeや配布物を意味しません。

## Metal実験とresource lifetimeの境界

公式Looking Glass Bridge 2.6.3 ARM64 packageのheader/export調査で、`initialize_bridge`、`instance_window_metal`、`create_metal_texture_with_iosurface`、`draw_interop_quilt_texture_metal`のMetal entry pointsを確認しました。wrapperはこれらのABIへtexture descriptor相当のObjective-C object pointerとIOSurface-backed textureを渡します。Unity側は`IUnityGraphicsMetalV2`を使い、Unity command bufferのcompletion handlerでproducer側のcopy完了を観測します。

現設計は最大3枚のimmutable texture slotをboundedに保持します。Unity command-buffer completionはproducer copyの完了です。SDK drawの受理はBridge consumerがtextureの読み取りを終えた通知ではありません。Bridgeのconsumer acknowledgment、release/reuse時点、drawのthread affinity、window/runloop要件は確認できていません。そのため実験経路はslotを再利用せず、通常SessionはMetalをreadyにしません。

SDKの`copy_metal_texture`内にBridge自身のcommand bufferをcommit後`waitUntilCompleted`する実装はありますが、pluginのdraw後のconsumer read完了を示すものではありません。また、plugin実装がそのcopy APIを呼ぶことも前提にしません。ABIで確認したselectorやexportの存在を、draw完了・release契約の証明として扱いません。

Unityの公式[IUnityGraphicsMetal.h](https://github.com/Unity-Technologies/NativeRenderingPlugin/blob/master/PluginSource/source/Unity/IUnityGraphicsMetal.h)はMetal interfaceのrender-thread利用制約を記載しています。pluginはUnity render eventからMetal producerを実行します。Bridge API側のthread契約は不明です。公式Bridgeの製品情報は[Looking Glass Bridge](https://lookingglassfactory.com/software/looking-glass-bridge)を参照してください。

GitHub Actionsの2 workflow run ([37705337066](https://github.com/zabaglione/ViewWeave/actions/runs/37705337066), [37705340768](https://github.com/zabaglione/ViewWeave/actions/runs/37705340768))はcommit `054fffeb817ac8daae6ff68ed567ec7e69fe4798`で各5/5 jobs成功です。macOS jobは`macos-14-arm64`上でAppleClang 15.0.0.15000309のCXX/ObjCXXを使い、shimをbuild/linkし、common、Metal API contract、submission lifetimeの3 testsを通過しました。CMakeは`CMAKE_BUILD_TYPE`を指定していないため、Release最適化済みとは記録しません。Windows fake regressionsは4/4、transportは2/2、Linux portable/unsupported ABIは2/2、static jobも成功しました。

CI artifact `native-macos-metal-shim` (artifact 11519112992)は`liblookingglass_bridge_shim.dylib` 179952 bytesです。downloadしたfileはMach-O 64-bit ARM64 (CPU type `0x0100000c`)、SHA-256 `C06464EEEBAA3D3B934437AD9242B6DFA4C5E37E26A3187D3BE131113213980B`で、`UnityPluginLoad`とMetal provider query/begin/submit/pump exportsを含みます。依存はMetal/Foundation/IOSurfaceとmacOS system librariesで、official Bridge dylibは同梱されていません。artifactはUPMに含まれず、CIから手動取得する必要があります。

このjobはnative build/linkとfake contract testsだけを行いました。Unityを起動せず、official SDKをloadせず、実機へquiltを表示していません。物理display、provider full flow、consumer release/thread/shutdown契約も未検証です。normal Metal Sessionはunsupportedのままです。

## Managedによる有限実験

`BridgeMetalExperimentalOutput`は、通常Sessionから独立した明示opt-in用componentです。macOS Play mode、64-bit process、Metal graphics device、runtime ABI layout guardを満たさない場合は開始しません。ABI guardは`ProviderStatusV1` 64 bytes (`deviceGeneration` offset 32)、`BeginRequestV1` 56 bytes (`runtimePathUtf8` offset 8、`width` offset 28)、`RenderRequest` 48 bytes (`sourceTexture` offset 8、`generation` offset 16、`columns` offset 24)を確認します。

限定試験の手順は次のとおりです。

1. Metal実験用componentをscene objectへ追加し、`rig`に既存の`ViewWeaveRig`を割り当てます。`runtimePath`にはBridge 2.6.3の`/Applications/Looking Glass Bridge 2.6.3.app/Contents/MacOS/libbridge_inproc.dylib`を指定します。native shimは別途手動で入手する必要があり、UPMには含まれません。
2. macOS EditorのPlay中にcomponentのContext Menuから`Begin Finite Metal Experiment From Rig Profile`を実行します。providerがruntimeを初期化し`RuntimeAvailable`になるまで待ちます。
3. `RuntimeAvailable`になった後、`Start Finite Metal Quilt Rendering`を一度実行します。producer submissionは最大3枚で自動停止し、3枚を超えて継続描画しません。
4. `Close Finite Metal Experiment`を実行してmanaged rigのoutput/profile/Auto Render設定を戻します。同じprocessで実験を再開する用途には使わないでください。

この試験はprocessごとのone-shotで、最大3 quiltを受け付け、Bridge consumerが解放したことを示すacknowledgmentがありません。slot/textureはretainedのままです。`Close`はmanaged rigを復元しprovider admissionを閉じますが、SDK側resourceの安全なretire、thread affinity、正常なprocess shutdownは未確定です。継続運用や繰り返し起動に使わないでください。通常SessionのMetal `interopReady`はfalseのままです。

最初のCIでは古いv5 pinとWindows fake initializer/Metal includeのcompile failureがありました。v7対応pin`522254181faf188efa8b50c3e3bf6fce720b26e4`への更新と修正後、上記2 runが成功しました。ただしCI結果はUnity runtime、official SDK load、Bridge画面、physical display、shader/format品質を検証していません。artifactは手動取得のみで、通常Metal対応を意味しません。

## 対象範囲と既知の制約

Vulkan実装は今回の範囲外です。性能最適化は保留中です。既存のScene View Gizmoによるcamera authoringは対象に含みますが、Quilt imageへのGizmo overlayは対象外です。

過去のWindows Bridge終了時access violationは根本原因未確定のまま履歴として保持します。訂正済みbaseline2は自然終了しましたが、旧事象は再現されておらず、修正の因果的な解消も証明されていません。Metal実験は通常Sessionに未対応で、このWindows shutdown変更の検証には含みません。
