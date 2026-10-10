[English](../getting-started.md) · [ユーザーガイド](user-guide.md) · [技術者向けガイド](technical-guide.md)

# Quick Startとシーンの作り方

Unity **6000.6.4f1**のURPまたはHDRPプロジェクトで使います。Looking Glassへの表示には、公式Looking Glass Bridgeのインストール・起動と、接続済みの機器が必要です。macOSはMetal、WindowsはDX11／DX12を使います。対応機器と制約は[対応表](compatibility.md)を確認してください。

## Quick Start：設定済みSampleから始める

SDKを導入済みなら、次の3手順で確認できます。

1. **Package Manager**でViewWeaveを選び、**Samples**の**URP Quick Start**または**HDRP Quick Start**を**Import**します。
2. Importされたフォルダーの**QuickStart.unity**をダブルクリックして開きます。元のシーンに未保存の変更があれば、先に保存してください。
3. Hierarchyの**ViewWeave Bridge Rig**を選び、**View Weave Rig > Preview > Start Looking Glass Device Preview**をクリックします。機器がない場合は**Start Local Edit Mode Preview**でEditor内のQuiltを確認できます。

![Package ManagerのSamplesとImportボタン](../images/quick-start/package-samples.jpg)

Import先は`Assets/Samples/<package表示名>/<version>/<Sample名>/`です。URP／HDRPそれぞれに**1つのシーン**があり、同じファイルをmacOSとWindowsで使います。Sample内の補助スクリプトがGraphics APIに合う出力を選びます。設定・操作の入口はRigです。

![ImportしたSample内のQuickStartシーン](../images/quick-start/imported-scene.jpg)

Sampleには黒い背景、赤いCube、青いSphere、黄色いCapsule、カメラ、設定済みRigが入っています。Edit Mode Previewは静止画です。アニメーションも確認する場合は**Play**し、Rigの**Bridge > Start Output**をクリックします。Cubeが回転し、Sphereが上下します。Sampleの出力は自動開始しません。終了は**Stop Output**、Playの停止の順に行います。

![ImportしたURP SampleのDevice Preview](../images/quick-start/device-preview.jpg)

## Package Managerから追加する

1. Unity HubでURPまたはHDRPプロジェクトを作成し、Editorで開きます。
2. **Window > Package Management > Package Manager**を開き、**+ > Install package from git URL**を選びます。
3. パイプラインに合うURLを**一つ**追加します。

| パイプライン | Git URL |
| --- | --- |
| URP | `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main` |
| HDRP | `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main` |

配布packageにはCore、Bridge、選択したパイプラインが含まれます。同じプロジェクトに両版を入れないでください。公開されたGitHubリポジトリはGitHub認証なしで取得できます。アクセス制限のあるコピーを使う場合はリポジトリの権限とGit認証が必要です。認証tokenをURLへ埋め込まないでください。

更新後はEditorを正常終了し、再起動してnative pluginを読み込み直します。`#main`は開発版です。revisionを固定する場合はcommit hashやtagを指定します。更新方法とnative pluginが切り替わらない場合の対処は[package更新](../bridge-package-update-safety.md)と[更新時のトラブル](../native-plugin-update-troubleshooting.md)を参照してください。

## 自分のシーンを作る：8手順

以下はSampleと同じ構成を、自分で作る手順です。画像は専用プロジェクトのUnity UIを撮影したもので、主にURPを使っています。HDRPでもRigの操作は同じです。

### 1. 新しいシーンを用意する

**File > New Scene**を開き、URPなら**Basic (URP)**を選びます。**Load additively**はオフのまま**Create**し、名前を付けて保存します。HDRPではHDRP向けのシーンを使います。

![新規シーン作成ダイアログ。Load additivelyはオフ](../images/quick-start/new-scene.jpg)

### 2. 背景を黒にする

Hierarchyの**Main Camera**を選びます。URPではCameraの**Environment > Background Type**を**Solid Color**、**Background**を黒にします。黒い背景は表示したいオブジェクトの形を確認しやすくします。

HDRPではCameraの**Environment**で背景を**Color**、色を黒にします。HDRP Sampleの**Black Sky and Fixed Exposure**も参考にしてください。VolumeではSkyとCloudを無効にし、ExposureをFixedにしています。簡単な照明構成なので、直接光が当たらない面は暗く見えます。

![Main Cameraの黒背景とカメラ位置](../images/quick-start/camera-background.jpg)

### 3. 表示するオブジェクトとアニメーションを用意する

**GameObject > 3D Object**からCube、Sphere、Capsuleを追加し、色の異なるLit materialを割り当てます。空の**Focus Objects**を作り、3つをその子にまとめると、後で全体へフォーカスを合わせやすくなります。

Sampleと同じ配置例です。PositionはFocus Objectsからの相対位置、Scaleは各オブジェクトの値です。

| オブジェクト | Position (X, Y, Z) | Scale (X, Y, Z) |
| --- | --- | --- |
| 赤いCube | (-0.48, -0.30, -0.30) | (0.65, 0.65, 0.65) |
| 青いSphere | (0.42, 0.10, 0.35) | (0.60, 0.60, 0.60) |
| 黄色いCapsule | (0, 0.65, 0.70) | (0.32, 0.35, 0.32) |

簡単に動かすには、ImportしたSampleの**Animations/CubeRotation.controller**をCubeへ、**SphereBounce.controller**をSphereへドラッグします。Animatorが追加され、Playすると動きます。自分で作る場合は**Window > Animation > Animation**でclipを作り、CubeのY回転やSphereのY位置にkeyframeを付けてループさせます。

![プリミティブとCubeRotationを設定したAnimator](../images/quick-start/primitives-animation.jpg)

### 4. カメラの位置を調整する

**Main Camera**のPositionを**(0, 0.1, -5)**、Rotationを**(0, 0, 0)**、**Field of View**を**35**にすると、上の配置を見渡せます。**Game**ビューで構図を確認してください。オブジェクトの大きさに応じて位置や画角を調整します。手順2の画像では設定値と構図を同時に確認できます。

### 5. Bridge Rigを追加する

Main Cameraを選択し、**GameObject > ViewWeave > Bridge Rig**を実行します。Hierarchyに**ViewWeave Bridge Rig**が追加されます。その**View Weave Rig**コンポーネントに、カメラ・フォーカス・Rendering・Preview・Capture・Bridgeのユーザー向けUIがまとまっています。

**Camera & Focus > Reference Camera**がMain Cameraになっていることを確認してください。Main Cameraの位置と向きは描画時に追従します。追加されたRigのCameraはQuilt描画用で、通常の画面描画は無効です。Audio ListenerをRigへ追加する必要はありません。

### 6. フォーカスを合わせる

**Camera & Focus > Focus Target**へ、フォーカスを合わせたいオブジェクトをドラッグするか、右端の選択ボタンから指定します。ここでは**Focus Objects**を指定し、**Set Focus From Target Center**をクリックします。Sceneビューの紫の面が、設定したフォーカス位置です。

![Focus TargetとSet Focus From Target Center](../images/quick-start/rig-focus.jpg)

Focus Targetは一時的なEditorの選択欄です。対象そのものは保存されませんが、ボタンで反映した**Focus Distance**はシーンに保存されます。画面へ収めたい場合は**Fit View To Target**も使えます。詳しくは[Quiltカメラの調整](quilt-authoring.md)を参照してください。

### 7. PreviewでLooking Glassへ表示する

Playせずに、Rigの**Preview > Start Looking Glass Device Preview**をクリックします。Editor内のQuiltとLooking Glassの表示を確認します。Main CameraのGameビューは通常の単眼表示です。

![Previewの開始ボタンとRenderingの画質設定](../images/quick-start/preview-quality.jpg)

一時停止は**Pause Preview**、再開は**Resume Preview**です。接続と描画資源を解放して終了する場合は**Advanced > Close Preview**を使います。アニメーションを実機へ送るにはPlayし、**Bridge**の出力状態を確認してください。Sampleでは**Start Output**を押します。メニューから新規作成したRigは自動起動が既定で有効です。

### 8. Renderingで描画品質を調整する

まず**Quilt View Render Mode = Quarter**、**Output Quality = Balanced**から試すと、描画負荷と解像度を調整しやすくなります。画質設定を変更するときはPreviewや出力を終了してください。MetalではOutput Qualityを変更後に再開します。

| 設定 | 変わるもの | 選び方 |
| --- | --- | --- |
| Quilt View Render Mode | 実際に描画する視点数 | Fullは全視点、Halfは約1/2、Quarterは約1/4。減らすとGPU負荷を下げられます。 |
| Output Quality | 各視点の描画解像度 | Preview、Balanced、Nativeの順に解像度と描画負荷が増えます。 |

FullからQuarterへ変えると、重いシーンではフレームレートが改善することがあります。ただし、視点間を補間せず近い視点を使うため、左右へ動いたときの変化が段階的に見えます。**Quarterは解像度を上げる設定ではありません。** 視点の滑らかさを優先するならFull、解像度を優先するならOutput Qualityを調整してください。

## 任意のSamplesをImportする

**URP Quick Start／HDRP Quick Start**は、保存済みシーンを含む導入用Sampleです。既存の**Windows URP Lit／Windows HDRP Lit**は、テストオブジェクトを生成する別のSampleで、必須ではありません。手順は[URP Lit](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/WindowsUrpLit/README.ja.md)と[HDRP Lit](../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Samples~/WindowsHdrpLit/README.ja.md)にあります。

## Playerをbuildする

シーンを保存し、**File > Build Profiles**へ追加します。macOSはMetal、WindowsはDX11またはDX12でビルドします。Sampleは自動起動をオフにしているため、Playerへ表示する場合は、Rigの**Bridge**で**Start Output On Enable**（Metal）または**Start Session On Enable**（Windows）を有効にしてから保存・ビルドしてください。OSを変えたときは、選択された出力側の起動設定とOutput Qualityを再確認します。

これはビルド手順であり、Quick Start SampleのPlayer動作を確認した結果ではありません。このSampleのPlayerビルド、WindowsでのUPM導入と実機出力は未検証です。macOSの確認はローカルpackageをImportしたEditorで行っており、公開後の認証なしのGit導入は未確認です。ネイティブ署名や実機の制限を含む現在の確認範囲は、[対応表](compatibility.md#現在の開発版の確認範囲)を参照してください。

表示できない場合は[対応表](compatibility.md)と[macOS Metalガイド](macos-metal.md)を確認してください。設計、性能測定、テストプロジェクト、検証記録は[技術者向けガイド](technical-guide.md)にまとめています。
