# Getting started

この手順はUnity 6000.6.4f1、Windows、URPまたはHDRPを対象にします。Looking Glassへの出力にはWindowsのLooking Glass Bridge runtimeと接続済みの機器が必要です。

表示のために起動するのは、自分のUnity projectです。Unity EditorのPlay、またはビルドしたPlayerがsceneからQuiltを生成し、公式Looking Glass Bridge runtimeがそれを接続機器へ送ります。Bridge runtimeはインストールして起動しておきます。SDK内のTests/Bridge Unityプロジェクトやデモアプリを別途起動する必要はありません。

## Package Managerから追加する

1. Unity HubでURPまたはHDRPテンプレートからプロジェクトを作成し、Unity Editorで開きます。
2. GitHub private repositoryへアクセスできるようGit Credential ManagerまたはSSHを設定します。認証tokenをGit URLに埋め込まないでください。
3. `Window > Package Management > Package Manager`を開き、`+ > Install package from git URL`を選びます。
4. 次のいずれか一つを追加します。
   - URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
   - HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`
5. 配布packageにCore、Bridge、選択したpipelineが含まれるため、別のViewWeave packageは追加しません。Samplesは任意です。Package ManagerのSamplesからImportするまでAssetsへ入りません。

同じprojectにURP版とHDRP版を同時に追加しないでください。Git URLの`#main`を更新する際にPackage Manager cacheが残る場合は、Removeしてから同じURLを再Addするか、特定commit hashをrefに指定します。
Package Managerが更新を完了したら、Unity Editorを正常終了して`-force-d3d11`で起動し直してください。更新したmanaged assemblyとnative pluginを読み込むために必要です。

## 既存のCubeやBoxを表示する

1. UnityをWindows D3D11で起動します。Editor Play出力にもD3D11が必要です。起動引数に`-force-d3d11`を指定する方法、またはPlayer SettingsのWindows Graphics APIsでD3D11を優先してEditorを再起動する方法があります。実行中のGraphicsDeviceTypeを確認してください。
2. WindowsへLooking Glass Bridge runtimeをインストールし、Looking Glassを接続します。
3. 通常のCubeやBoxをシーンに置き、標準のURP/HDRP materialとlightを使います。CubeへViewWeave componentを追加する必要はありません。
4. Rig作成前に被写体のRendererを選択してから`GameObject > ViewWeave > Bridge Rig`を実行します。作成される専用cameraはMainCameraの位置、rotation、FOV、near/far、culling maskを引き継ぎます。選択Renderer中心がMainCameraのnear/far範囲内かつ正面にある場合、その正のcamera forward距離をQuiltRendererのzero-parallax距離`cameraDistance`に設定します。範囲外、背面、Renderer未選択なら10mです。MainCameraがない場合は既定Rig位置を保ち、3mにします。既存のMain Cameraの子としてRigを作る手順は[Cameraを親にする](#既存cameraの子にrigを作る)を参照してください。
5. Rendererを選択せず作成した場合、MainCameraがあればzero-parallax距離は10mです。MainCameraがない場合はRigの既定位置を維持し、距離は3mです。必要に応じてRigのcameraDistanceをInspectorで調整します。
6. Rigのあるsceneを保存し、Unity EditorのPlayを押します。Unity Editorがsceneを描画し、Bridge runtime接続後にBridge profileに応じたQuiltがLooking Glassへ送られます。EditorのGame viewは通常のMainCamera表示のままです。

EditorがD3D12で起動している場合は、Player SettingsのGraphics APIsを変更してUnity Editorを再起動するか、`-force-d3d11`でEditorを起動してください。Edit Mode、macOS、D3D12ではBridge出力しません。Windows Playerで確認する場合はWindows向けGraphics APIsでD3D11を選び、Build and Runします。

## Qualityを変更する

1. Unity EditorのPlayを停止します。
2. Rig上の`ViewWeaveBridgeSession` Inspectorにある`Quality`を`Balanced`または`Native`へ変更します。
3. Playを押し、Bridgeへ送られる表示を確認します。

Qualityは接続機器から取得したBridge profileの解像度を基準にします。`Preview`は1 viewあたりの縦横をprofileの1/4、`Balanced`は1/2にし、`Native`はprofile本来の解像度を使います。どのQualityでもview数はprofileの値のままです。たとえば、直前に確認した機器profileが7680x4320、8x6 viewsなら、1 viewあたりはPreviewが240x180、Balancedが480x360、Nativeが960x720です。この数値はそのprofileの例で、他の機器に共通する仕様値ではありません。48 viewsの場合、NativeはPreviewと比べて1 viewあたり縦横4倍、総pixel数16倍を描画するため、GPU負荷が増えます。

Sessionは起動時に選択したQualityからquilt解像度とgridを適用します。`QuiltSettings`を直接編集してもSessionのprofileで上書きされるため、表示解像度を変える場合は`ViewWeaveBridgeSession`の`Quality`を使ってください。

## 既存Cameraの子にRigを作る

既存のMain Cameraに追従させるには、HierarchyでそのCameraを右クリックし、`ViewWeave > Bridge Rig`を選びます。生成されたRigは右クリックしたGameObjectの子になり、Cameraが`MainCamera`タグ付きなら、そのworld positionとrotationを基準にframingをコピーします。そのため、Main Cameraを右クリックした場合はRigのlocal positionとrotationが0になり、親Cameraに追従します。既存Main Cameraは有効なままです。Rig上の専用CameraはQuilt描画用sourceCameraとして自動的に無効化されます。

MainCameraタグのないCameraを右クリックしても、そのCameraのframingが選ばれるわけではありません。現在のメニューは親を右クリックしたGameObjectにしますが、framingは`Camera.main`からコピーします。

## HDRPテンプレート

HDRPはLinear color spaceが必須です。`Project Settings > Player > Other Settings > Color Space`でLinearを選んでください。HDRPのquilt描画には専用adapterが働き、既定でquilt viewを妨げる一部temporal効果を一時的に抑えます。HDRP Auto Exposureは全view共通のFixed EV 12に変換されます。元のExposureがFixedならその値を保ちます。元のMainCamera、Volume Profile、Project Settingsは変更しません。通常Fog、Sky、Bloomなどは維持されます。

VFX Graphを使う場合もLinear color spaceにします。HDRP adapterの制約は[Compatibility](compatibility.md)を参照してください。

## 任意のSamplesをImportする

確認用オブジェクトをgeneratorで作る場合だけ、Package ManagerでViewWeave packageを選び、Samples欄からpipelineに合うものをImportします。この操作はsceneをAssetsへコピーせず、generatorとmaterial resourceをImportします。

1. シーンに`GameObject > ViewWeave > Bridge Rig`を作成します。
2. RigにURPなら`BridgeUrpLitSampleGenerator`、HDRPなら`BridgeHdrpLitSampleGenerator`をAdd Componentします。
3. Inspectorの`Generate On Start`を有効にしてPlayすると、起動時に確認用のCube、sphere、lightが生成されます。手動で作成する場合は、generatorのInspectorメニューから`Generate Lit Scene`を実行します。
4. Bridge出力を確認するにはWindows D3D11でPlayします。HDRPはLinear color spaceが必要です。HDRP adapterと色空間の説明は上記[HDRPテンプレート](#hdrpテンプレート)を参照してください。

標準Cubeや自分のsceneを表示するときはSamplesのImportもgeneratorも必要ありません。

## Player build

1. sceneを保存します。
2. `File > Build Profiles`でWindows profileにsceneを追加します。
3. Windows Graphics APIsでDirect3D11を有効にし、必要なら自動選択を無効にします。
4. `Build and Run`します。Bridge runtimeが自動検出されない場合だけ、Bridge SessionのRuntime Pathに`bridge_inproc.dll`の絶対パスを指定します。

## テストプロジェクト

repository rootで`python tools/prepare_sample_tests.py`を実行してから、Unity Hubの`Add`で`Tests/Pipelines/Unity`を開きます。GPUを使うfixtureが含まれます。Editorで`Window > General > Test Runner`から対象testを実行します。既知の未通過条件と試験結果は[Windows validation](windows-validation.md)を参照してください。
