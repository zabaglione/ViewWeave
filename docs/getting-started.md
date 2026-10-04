# Getting started

この手順はUnity 6000.6.4f1のWindows Player構成を対象にします。設計対象はURP/HDRPで、Bridge実行経路は実験的なWindows D3D11専用です。Core、Bridge、使用するURPまたはHDRP packageを各`package.json`から導入してください。Bridge runtimeは別途インストールします。macOS Metalは対象外です。

## Bridge Rigを使う主経路

1. URPまたはHDRPを有効にしたUnity Editorで`GameObject/ViewWeave/Bridge Rig`を選びます。メニューは専用のdisabled Camera、`QuiltRenderer`、`ViewWeaveRig`、`BridgeWorkerQuiltOutput`、`ViewWeaveBridgeSession`、対応するURP/HDRP adapterを同じGameObjectへ作成します。adapterはGuardとして必須です。
2. Bridge runtimeを自動検出できない場合は、`LKG_BRIDGE_PATH`へ絶対パスを設定するか、Sessionの`runtimePath`へ絶対パスを設定します。明示path、環境変数、Windows registryの順で解決し、registry候補はインストール済み`bridge_inproc.dll`だけを検証します。相対path、見つからないDLL、複数候補はASCII診断で停止します。
3. Windows PlayerをDirect3D11で起動します。作成されたSessionはmenu生成時だけ`startOnEnable=true`です。手動構成では`startOnEnable=false`のまま、設定完了後に`StartSession()`を呼びます。EditorからBridgeへ接続することはできません。

Sessionの主なAPIは`StartSession()`、`StopSession()`、`SetQuality(QuiltQualityPreset)`、`State`、`Quality`、`Profile`、`LastError`です。ReadyになるとBridge profileからPreview、Balanced、Nativeのprofileを作り、Rigへ適用して自動描画を開始します。実行中のquality変更は`Reconfiguring`状態で転送を静止させ、`TryResetTransfer`による同一host・windowの再設定を行います。明示的な`StopSession()`/`StartSession()`は別の再起動手段です。

source Cameraは同じRig GameObjectに置き、`enabled=false`にします。`fieldOfView`、`cameraDistance`、near/far clip、background color、clear flagsはアプリケーション側で設定してください。Bridge profileが提供する解像度、grid、aspect、viewconeと混同しないでください。通常描画用Cameraとの共有は避けます。

## Optional Bridge Samples

Bridge packageのSamplesからWindows URP LitまたはWindows HDRP Litを任意でImportできます。対応する`BridgeUrpLitSampleGenerator`または`BridgeHdrpLitSampleGenerator`をBridge Rigへ追加し、`Generate On Start`を有効にしてWindows D3D11 PlayerをBuild/Runしてください。生成物はsample所有のLit material resourceを使用します。Editorの`Generate Lit Scene`は生成物を確認する任意操作で、EditorからBridgeへ接続することはありません。詳細は[Windows URP Lit sample](../Packages/com.lookingglass.community.bridge/Samples~/WindowsUrpLit/README.md)と[Windows HDRP Lit sample](../Packages/com.lookingglass.community.bridge/Samples~/WindowsHdrpLit/README.md)を参照してください。`runInBackground=true`を使うBridge worker検証条件はBridge READMEに従います。

## BridgeなしのEditor preview

Core導入後、Package ManagerのURP専用`Quilt Preview` sampleをImportできます。sampleには利用可能なLit materialが含まれます。`GameObject/ViewWeave/Quilt Rig`はprovider registryから現在のURPまたはHDRP adapterを選びます。Coreの`Quilt Preview` sample自体はURP専用です。`QuiltQualityProfile.Preview`、`Balanced`、`Native`を明示的に選択し、`RenderQuilt()`または`autoRender`を使用します。PreviewはEditorで動作しますが、Bridge runtime接続を意味しません。

URP VFX Graphを試す場合は、Rigへ`UrpVfxCameraDriver`を追加し、`enableDriver`、`vfxLayers`、必要に応じて`runWhenManualRendering`を設定します。補助Cameraが追加されるため、VFXのbounds、FOV、simulation状態を確認してください。HDRPにはこのdriverはありません。詳しい制約は [docs/vfx-graph.md](vfx-graph.md) を参照してください。

自動描画は`QuiltRenderer.autoRender`を使用します。描画はシーン描画後の`PostLateUpdate.FinishFrameRendering`直後に1フレーム1回実行されます。`renderInLateUpdate`は既存serialized scene向け互換propertyです。手動描画では`RenderQuilt()`をrender callbackの外側から呼びます。

## 検証条件とlegacy Direct経路

Worker経路は実験的なWindows Player、D3D11専用経路です。同一プロセス再接続と48 viewの非空画像・視差・移動は確認済みですが、光学品質の人手確認、配布署名、hotplug、複数機器、全機器profile、D3D12、Vulkanは未確認です。固定条件のURP/HDRP品質切替ではPreview、Balanced、Native、Previewを各122 completionで完了し、明示Stop/Start後120 drawとpending 0を確認しました。これを全機器profileや光学品質の保証とは扱いません。HDRP検証ではLinear color space、固定Exposure、TAA/Motion Blur/SSR/SSGI/volumetric fog/cloud無効を使用します。

既存のDirect経路はlegacy diagnostic用に維持しています。Directのvalidated条件はBridge package READMEを参照してください。Direct経路とWorker経路の状態、runtime path、初期化APIは共用しないでください。Bridge runtime、公式binary、headerは同梱・再配布しません。
