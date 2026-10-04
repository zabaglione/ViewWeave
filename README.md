# ViewWeave

An unofficial Unity SDK for Looking Glass displays.

Looking Glass向けの非公式・独立したUnity SDKです。設計対象はUnity 6.6系のURP/HDRPで、Windows D3D11実験版として検証しています。独自コードはApache-2.0で公開します。Looking Glass Factoryの公式SDK、商標、ソースコード、バイナリを同梱せず、公式製品との提携や互換性を保証するものではありません。

## 現在の範囲

検証環境はUnity 6000.6.4f1、Windows、D3D11、SRP 17.6です。Coreは専用Camera 1台をN回描画し、視点ごとのRenderTextureを`Graphics.CopyTexture`でQuiltへ転送します。これは現在の実装方式であり、速度優位性を示すものではありません。URP互換性試験は透明alpha比較、Bloom、Particle Lit/Unlit、uGUI World Space、uGUI Screen Space Camera、UI Toolkit targetTexture、live particleの8件を確認済みです。HDRPも透明alpha比較、Bloom、Mesh Particle paused/live、uGUI World Space、uGUI Screen Space Camera、UI Toolkit targetTextureの7件を確認済みです。これは個別fixtureの結果であり、機能全体の対応やPlayer実行の保証を意味しません。

旧82/71/11と前回115件の統合集計に代わる最終Windows D3D11 PlayModeは123件中108件pass、15件fail、skip 0です。Session 18/18、GRD 27/27、SRP追加10/10、従来のURP MSAA基準1/1（4x view RT、1x resolve、lifecycle、中心色一致）、VFX driver 5/5を確認しました。追加のLinear MSAA境界試験は10/10 passで、このRTX GPUでは2x/4x/8x要求が対応倍率と一致しました。1x経路はpipeline 1x、allowMSAA無効、Deferred fallbackで確認し、低MSAA能力GPUは未検証です。resize/cleanup、中心色一致と対応MSAAのedge差分も確認しました。Gammaと専用MSAA Player試験は対象外です。15件はHDRP priming 1件とVFX compatibility 14件で、別実行のVFX matrixは20件中5件pass、15件failでした。未通過条件では十分な全tileの効果を確認できませんでした。詳細な内訳と再現条件は [docs/compatibility.md](docs/compatibility.md) と [docs/windows-validation.md](docs/windows-validation.md) を参照してください。

Sessionの実機PlayerではURP/HDRPのPreview、Balanced、NativeとGRD ON/OFFの各6条件を48 viewで確認しました。各条件は120 completionを完了し、固定条件の品質切替では4段階を各122 completion、明示Stop/Start後120 draw、pending 0を確認しました。Worker Nativeは7680x4320の実機profileで確認しています。2026-10-04の限定実機確認では奥行きと画質は良好で、番号付きデモをユーザーが暫定受入としています。全視点・全視域の網羅的な光学検証は未実施です。

SDK Demoは自動検出したNative 7680x4320、8x6、48 viewで120 completionと全tileの赤緑表示を確認しました。これはWindows D3D11の限定実機結果です。視点番号デバッグ表示は左右の視点確認用で、一般機器や長時間運転の保証ではありません。

`BridgeQuiltOutput`は実験的なWindows D3D11 Direct経路です。`-force-gfx-direct`と`runInBackground=true`を指定した構成で、16インチ実機のLit 48-view表示、向き・前後関係、120フレーム後の終了コード0を確認しています。`BridgeWorkerQuiltOutput`はWindows Player D3D11通常thread向けの実験経路で、同一プロセス再接続を含む120 draw、終了保留0、同一host thread再利用を確認しました。接続・終了・completion watchdogは10秒で、専用fake runtimeによるinit/draw-release遅延のPlayer fault試験も通過しています。この試験は物理hotplugや任意の実機接続ハングを保証しません。Worker Nativeは7680x4320の実機profileで確認し、2026-10-04の限定実機確認で奥行きと画質は良好でした。独立native build、CTest、provenance監査は完了していますが、配布署名は未実施です。MacOS Metalは対象外です。

対応状況、未検証事項、公式機能との比較は [docs/compatibility.md](docs/compatibility.md)、性能計測計画は [docs/performance.md](docs/performance.md)、導入手順は [docs/getting-started.md](docs/getting-started.md) を参照してください。開発計画は [ROADMAP.md](ROADMAP.md) にあります。

最新の統合互換性集計は [docs/compatibility.md](docs/compatibility.md) を正本とし、公式機能との比較は [docs/sdk-feature-comparison.md](docs/sdk-feature-comparison.md) に整理しています。

視点間の共通処理再利用を検証する8視点の共有culling試作は、限定Lit条件で画像差分と8本のRendererList実行を確認しています。固定マイクロベンチではCPU submission p50がbaseline 1.8023 ms、shared culling 0.3198 msでした。GPU性能、48視点、影、透明、post processing、VFXの対応は未確認で、量産経路へは採用していません。方式比較と条件は [docs/multiview-rendering.md](docs/multiview-rendering.md) に整理しています。

最新のEditor GRD/VolumeStack回帰は27/27合格、skip 0です。URP通常thread PlayerのGRD OFF/ON・1/8/48視点6ケースも通過しています。CPU submissionとGCの参考計測はありますが、GPU時間とrender thread時間は取得できていません。別の固定条件の48視点測定では、GRD ONのCPU submission p50がOFFより約13.0%低い結果でした。GPU速度や公式実装との比較値ではないため、速度保証には使いません。詳細は [docs/gpu-resident-drawer.md](docs/gpu-resident-drawer.md) と [docs/performance.md](docs/performance.md) を参照してください。

## パッケージ

現在は次の4 UPMパッケージを分離しています。導入順はCore、Bridge、必要に応じてURPまたはHDRPです。通常利用で利用者が別途用意するのはBridge runtimeです。native shimをビルドする場合だけ、Unity Plugin API headersを開発環境から参照します。

- `Packages/com.lookingglass.community.core` (`0.1.0`)
- `Packages/com.lookingglass.community.bridge` (`0.1.0`)
- `Packages/com.lookingglass.community.urp` (`0.1.0`)
- `Packages/com.lookingglass.community.hdrp` (`0.1.0`)

Coreには`Quilt Preview` optional Sampleを登録しています。Package ManagerでCoreのSamplesからImportすると、Bridgeなしのruntime Lit previewを作成できます。権利とライセンスの確定前に、公式Bridgeや外部SDKをリポジトリへ追加しません。詳細は [docs/THIRD_PARTY_NOTICES.md](docs/THIRD_PARTY_NOTICES.md) を参照してください。

## 主要な制約

- HDRPはLinear color spaceと固定Exposureが必要です。TAAは一時的に無効化し、Motion Blur、SSR、SSGI、volumetric fog、cloudは未対応経路として扱います。
- URPはBase cameraのみ対応し、camera stackは拒否します。FXAA/SMAAは限定fixtureで確認しました。MSAAは4x view RT、1x resolve、resize/disable lifecycle、中心色一致を限定URP fixtureで確認しました。一般素材、Player、性能は未検証です。TAAの視点間履歴は未検証です。
- Bloomは個別fixtureで視点ごとの差を確認しています。Quilt全体へ一括適用した場合のtile seamや、他のpostprocessの視点間履歴は未検証です。
- URP VFX Graphには`UrpVfxCameraDriver`による補助Camera経路があります。source Cameraは無効なままにし、VFXレイヤーとculling maskを合わせてください。追加の1x1描画負荷があり、HDRP driverはありません。詳細は [docs/vfx-graph.md](docs/vfx-graph.md) を参照してください。
- 3840x2160の表示パネルとQuilt解像度は別物です。解像度プロファイルは機器由来であり、全機種共通の固定値ではありません。

ローカルcheckoutでは、Unity Package Managerから必要なpackageフォルダを順に追加します。導入順はCore、Bridge、使用するURPまたはHDRPです。Bridge runtimeは別途インストールし、通常利用では手動DLL pathを設定しません。Git URLやregistryを使う配布手順は、公開時のタグと依存関係に合わせて別途提示します。

## 参考資料

- [Looking Glass Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [Looking Glass software documentation](https://lfdocs.lookingglassfactory.com/software/index)
- [Unity Graphics plugin event](https://docs.unity.com/en-us/engine/6000.6/script-reference/unityengine/gl/issuepluginevent)
