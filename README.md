# ViewWeave

Looking Glass向けの非公式Unity SDKです。URPまたはHDRPの通常のシーンを複数視点で描画し、Windows上のLooking Glass Bridge runtimeへ送ります。

Looking Glassに表示するときは、利用者自身のUnity projectをEditor PlayするかPlayerとして起動します。UnityがQuiltを描画し、インストール済みで起動中の公式Looking Glass Bridge runtimeが機器へ届けます。SDK内のTests/Bridge Unityプロジェクトやデモアプリを別途起動する必要はありません。

対応環境はUnity 6000.6.4f1、Windows、URPまたはHDRPです。Bridge Worker経由ならD3D11またはD3D12のUnity Editor PlayとWindows Playerから出力できます。Legacy Direct経路はD3D11のみです。Edit ModeとmacOS出力は対象外です。Bridge runtimeとLooking Glass機器を別途用意してください。

このprivate repositoryからPackage Managerで取得するにはGitHubのアクセス権が必要です。独自コードのライセンスは[Apache-2.0](LICENSE)です。

## 使い始める

1. Unity HubでUnity 6000.6.4f1のURPまたはHDRPテンプレートからプロジェクトを作り、Unity Editorで開きます。
2. `Window > Package Management > Package Manager`の`+ > Install package from git URL`を選び、利用するpipelineのURLを追加します。
   - URP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/urp#main`
   - HDRP: `https://github.com/zabaglione/ViewWeave.git?path=/Distribution/hdrp#main`
3. 配布packageにはCore、Bridge、選択したpipelineが含まれます。追加のViewWeave packageは不要です。Package ManagerのSamplesは任意で、ImportするまでAssetsへ入りません。標準Cubeから試す場合はImport不要です。
4. WindowsへLooking Glass Bridge runtimeをインストールして、対象機器を接続します。
5. シーンに通常のCubeまたはBoxを置き、必要なmaterialとlightを設定します。特別なobject scriptやgeneratorは不要です。
6. Hierarchyで使用中Camera、またはCameraを祖先に持つGameObjectを右クリックして`ViewWeave > Bridge Rig`を選びます。対象Cameraのposeと基本設定はRig作成時にコピーされます。`MainCamera`タグは不要です。
7. CubeをCameraの前に置き、必要に応じてRigの`QuiltRenderer > Settings > Camera Distance`（`cameraDistance`）を調整します。詳しい焦点距離の決まり方は[Getting started](docs/getting-started.md#既存のcubeやboxを表示する)を参照してください。
8. HDRPではPlay前に`Window > Rendering > HDRP Wizard`を開き、HDRP/Global checksの`Lightmap Encoding`が赤い場合は`Fix All Platforms`を実行して`High Quality`にします。`Color Space`も`Linear`であることを確認し、必要ならWizardの`Fix`を実行してください。`Default Render Pipeline`、`Volume`、`Diffusion Profile`、`SRP Batcher`など他の項目が赤い場合も`Fix All`または各項目の`Fix`で設定を整えます。HDRP標準テンプレートでは通常これらの設定は済んでいます。Unityの[HDRP Wizard](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/Render-Pipeline-Wizard.html)の手順を参照してください。ViewWeaveは利用者projectのこれらの設定を変更しません。HDRP設定の詳細は[Getting started](docs/getting-started.md)を参照してください。
9. Unity EditorでPlayを押します。

Git packageを更新した後はUnity Editorを正常終了して起動し直し、選択したGraphics APIでPlayします。新しいmanaged assemblyとnative pluginを読み込むために必要です。

Windows Playerで使う場合は、Windows Graphics APIsでD3D11またはD3D12を有効にしてBuild and Runします。Legacy Direct経路を明示的に使う場合はD3D11が必要です。Bridge runtimeが自動検出されない場合だけ、RigのSessionにRuntime Pathを設定します。

## 視点描画数を調整する

Hierarchyで`ViewWeave Bridge Rig`を選び、Inspectorの`QuiltRenderer > View Render Mode`で`Full`、`Half`、`Quarter`を選べます。Fullが既定です。48視点の機器では、どのモードも48タイルのQuiltを出力し、実際に描画する視点数はFullが48、Halfが24、Quarterが12です。描画しないtileには最も近い描画済み視点の画像を使います。視点間の補間は行わないため、HalfとQuarterでは視点角度方向の滑らかさが下がることがあります。この設定はQuilt解像度やPreview/Balanced/Native profileとは別です。速度向上はシーンやGPUで変わるため、数値を保証しません。限定HDRP fixtureの測定と制約は[DX12 HDRP performance investigation](docs/dx12-performance-investigation.md)を参照してください。

## Quilt保存とカメラ調整

Play中にHierarchyでViewWeave Bridge Rigを選び、Window > ViewWeave > Quilt Captureから完成Quilt PNG、JSON sidecar、中央view PNGを保存できます。RecorderはPNG連番のPause/Resumeに対応します。DX12 fake-output capture testはCore smokeとURP/HDRP各1/1通過し、中央PNG/sidecar、half/quarter view mapping、Pause/ResumeとUnity時間復元を確認しました。このtestはBridgeを使わず、実機表示の合格を意味しません。既存D3D11 test結果は別に記録しています。PNG圧縮は同期処理のため指定FPSに追いつかない場合があります。操作は[Quilt Capture](docs/quilt-capture.md)を参照してください。

カメラ範囲はQuiltRendererのCamera FramingとSceneビューで確認できます。紫の面がzero-parallax/focus plane、青緑と橙の面がNear/Farです。Camera Authoring test suiteは通過しています。Sceneビューでハンドルを直接ドラッグする操作と実機の光学表示は未検証です。capture suiteのDX12 fake-output試験は通過していますが、Bridge captureは未検証です。カメラのfocus/FOV/clip設定を変えても元カメラのposeは維持されます。録画中のカメラ移動は可能です。詳しくはdocs/quilt-authoring.mdを参照してください。

対応条件と既知の制約は[Compatibility](docs/compatibility.md)、検証結果は[Windows validation](docs/windows-validation.md)を参照してください。

## 動画出力

PNG連番からの無音MP4書き出しはEditor専用の後処理機能です。FFmpeg/ffprobe 7.0.2を別途用意する必要があり、SDKはbinaryを同梱・downloadしません。空白・Unicodeを含むpathから3枚のQuilt PNGをH.264/yuv420p、30fpsで書き出し、ffprobeとfull-layout JSONを確認しました。これはCPU-onlyの合成素材テストで、Unity NUnitや実Quilt描画を使った検証ではありません。詳しくは[Quilt Video Export](docs/quilt-video-export.md)を参照してください。

## 任意のサンプル

付属の`Windows URP Lit`と`Windows HDRP Lit`はPackage ManagerのViewWeave package詳細にあるSamplesから明示的にImportできます。Import後、既存のBridge RigへURPなら`BridgeUrpLitSampleGenerator`、HDRPなら`BridgeHdrpLitSampleGenerator`をAdd Componentし、`Generate On Start`を有効にするかInspectorの`Generate Lit Scene`を実行します。詳しくは[サンプル手順](docs/getting-started.md#任意のsamplesをimportする)を参照してください。packageを追加しただけではサンプルがAssetsへコピーされません。

## HDRPと色空間

HDRPとVFX GraphはLinear color spaceが必須です。HDRP Wizardの`Lightmap Encoding`はHDRPで`High Quality`を使うための設定です。HDRP adapterはquiltの描画中だけMotion Blur、SSR、SSGI、volumetric fog、volumetric cloudsを抑え、TAAを一時停止します。Auto Exposureは全quilt viewで同じFixed EV 12にし、元のVolume stackがFixedならそのEVを保ちます。通常のFog、Sky、Bloomなどは維持されます。HDRP adapterの詳細と制約は[Compatibility](docs/compatibility.md)を参照してください。

## テストプロジェクト

テスト環境の準備は[Tests/Pipelines/README.md](Tests/Pipelines/README.md)、実行方法は[Getting startedのテストプロジェクト](docs/getting-started.md#テストプロジェクト)を参照してください。

## 検証資料

- [Getting started](docs/getting-started.md): Editor PlayとPlayerの手順
- [Compatibility](docs/compatibility.md): 対応条件と既知の制約
- [Windows validation](docs/windows-validation.md): 実行済み試験と結果
- [残作業と受け入れ計画](docs/remaining-work-plan.md): 次に行う作業と完了条件
- [残作業の検証記録](docs/benchmarks/remaining-work-validation-20261007.md): 2026-10-07の限定DX12 captureと終了診断
- [DX12 HDRP performance investigation](docs/dx12-performance-investigation.md): Profiler調査と視点数削減の実測
- [公式SDK機能調査](docs/official-sdk-feature-research.md): Quilt保存、連番録画、カメラ操作の比較
- [Unity多視点最適化調査](docs/unity-multiview-research.md): Unityと研究での描画最適化
- [XR Display Provider調査](docs/xr-provider-investigation.md): 既存Cameraを使うXR連携方式の調査（採用・試作は保留）
- [Unity feature coverage](docs/unity-feature-coverage.md): Unity機能ごとの確認範囲
- [VFX Graph](docs/vfx-graph.md): VFX Graph利用時の条件
- [Third-party notices](docs/THIRD_PARTY_NOTICES.md): 外部ライセンス
