# 開発計画

日付を固定せず、各phaseの受入基準を満たした時点で次へ進みます。以下は計画であり、未実装機能を現在対応とは扱いません。

## M0: 配布・権利・計測基盤

パッケージ境界、rights確認、再現可能な性能ベンチ、テストログとスクリーンショットの保存形式を定めます。独自コードのApache-2.0採用は完了しています。Bridge、Unity、商標、第三者特許など外部権利の監査は継続します。受入基準は、URP/HDRPプロジェクトへCoreと対応adapterを導入して基本Quiltを描画でき、依存と外部コンポーネントが文書化されることです。

GPU Resident Drawerの適用性試験をM0/M1付近へ前倒しします。Bridge出力から切り離し、GRD OFF、GRD ON・occlusion OFF、履歴確認後のGRD ON・occlusion ONを比較します。これはBridge停止修正とは別作業です。

GRD-MVP後に、視点間の共通cullingとGPU常駐geometryを使う独自multiviewの最小Lit検証を行いました。8視点の限定条件で6判定、feature実行1回、8本のRendererListを確認しています。再投影方式はその後の選択式研究とします。方式の詳細と未解決事項は [docs/multiview-rendering.md](docs/multiview-rendering.md) に記載します。量産経路への採用や性能達成とは扱いません。

## M1: Bridge通常thread経路（実装済み、受入ゲート継続）

call stack、thread ID、device context、fenceの収集、Unity render threadだけのcopy、Bridge own D3D11 device、3スロットの共有GPU ring buffer、WM message pump、resize、close/reopenを実装し、API検証を行いました。queue満杯時は新しいsubmissionをdropし、投入済みsubmissionを保持します。CPU readback常用は採用しません。Direct guardは別経路の受入まで維持します。Worker通常threadはWindows D3D11 Playerで同一プロセス再接続を含む実機確認を行い、新WM pump DLLのCTestも通過しています。光学、長時間lifecycle、正式配布署名、終了処理、macOS経路は別のrelease gateです。

## M2: profile・Rig・導入性（限定通過、実機範囲を継続確認）

高解像度profile、統合Rig、機器自動選択、installer、通常利用時のmanual DLL path不要化を整備します。Coreの`Quilt Preview` optional SampleとQuality profile（Preview/Balanced/Native）は登録済みです。ViewWeave RigはGameObjectメニューのprovider registryから対応URP/HDRP adapterを追加します。RigMenu providerは`ViewWeaveRigProviderRegistry.TryAddAdapter(target, out adapter)`を使用し、profile factoryは`QuiltQualityProfile.Preview(...)`など必要な引数を渡して呼び出します。M0のempty projectからUPM導入、setup wizard、Lit表示まで5分を目標とします。URP Sample PlayerはNative 48 view、全tile非空、赤緑の奥行き、移動、視差、124 draw、pending 0を確認しました。HDRP Sample PlayerもNative 48 view、全tile非空、赤緑の奥行き、移動、視差、125 draw、pending 0を確認しました。受入基準は、対応機器のprofile由来設定が一貫し、未対応機器を誤って対応表示しないことです。

## M3: per-view互換性（限定通過、未通過条件あり）

per-view postprocessing、UI、particles、VFXの互換性を検証します。consumer向けの経路はper-view処理、assemble、calibrateの順序を明示し、Quilt全体Bloomによるtile seamを避けます。受入基準は機能matrixと再現手順が揃うことです。

最新のWindows D3D11 PlayMode統合は82件中71件が通過、11件が未通過、skip 0です。Session 14/14、追加互換性12/12、Geometry 10/10、HDRP 7/7、URP 8/8、HDRP priming 0/1、SRP追加4/5、SRP拡張2/2、VFX 5/14、URP driver 5/5、Samples 4/4を確認しました。HDRP priming、MSAA、VFXには未通過条件が残っています。個別fixtureの通過結果をすべての素材や効果への対応宣言には使いません。Bridge通常threadの光学確認、正式配布用native再構築は別のrelease gateです。

## M4: 高速化研究（評価継続、production採用なし）

URP公開extensionを基礎に、culling/shadow共有の成立条件、pooling、command reuse、GPU-instanced multiview、depth reprojection（transparent、occlusion、reflection fallbackを含む）を測定します。共有culling prototypeは限定Lit条件で合格しましたが、量産経路の性能は未計測です。独自開発目標として同品質20% GPU/frame time改善を置くことはできますが、実績や保証として記載しません。受入基準は固定条件3-run以上のベンチと画質確認です。現時点でproduction経路へ採用しません。

## M5: alpha / beta / stable（未開始、release gate待ち）

alphaは実験的なWindows経路を明示でき、通常threadや実機の未解決事項を公開文書に残すことをゲートとします。betaは通常threadの再現性、Windows実機、互換性matrix、性能計測、権利確認をゲートとします。stableは通常threadの安定化とWindows・macOS実機確認、配布再現性、権利確認、release blocker解消を必須とします。各段階で未達項目がrelease blockerとして残る場合は次段階へ進めません。

SamplesはUPMの`Samples~`ディレクトリと`package.json`の`samples`定義によるoptional方式でCoreに登録済みです。monorepoのGit URLでは兄弟パッケージを`package.json`の依存関係へGit URLとして書けないため、初期導入ではproject manifestへ必要な各Git packageを列挙します。将来はregistryで依存解決します。

公式機能との差は、代替可能性を断定せず、機能matrixの検証結果で示します。
