# 開発計画

日付を固定せず、各phaseの受入基準を満たした時点で次へ進みます。以下は計画であり、未実装機能を現在対応とは扱いません。

## M0: 配布・権利・計測基盤

パッケージ境界、rights確認、再現可能な性能ベンチ、テストログとスクリーンショットの保存形式を定めます。独自コードのApache-2.0採用は完了しています。Bridge、Unity、商標、第三者特許など外部権利の監査は継続します。受入基準は、URP/HDRPプロジェクトへCoreと対応adapterを導入して基本Quiltを描画でき、依存と外部コンポーネントが文書化されることです。

GPU Resident Drawerの適用性試験をM0/M1付近へ前倒しします。Bridge出力から切り離し、GRD OFF、GRD ON・occlusion OFF、履歴確認後のGRD ON・occlusion ONを比較します。これはBridge停止修正とは別作業です。

GRD-MVP後に、視点間の共通cullingとGPU常駐geometryを使う独自multiviewの最小Lit検証を行いました。8視点の限定条件で6判定、feature実行1回、8本のRendererListを確認しています。再投影方式はその後の選択式研究とします。方式の詳細と未解決事項は [docs/multiview-rendering.md](docs/multiview-rendering.md) に記載します。量産経路への採用や性能達成とは扱いません。

## M1: Bridge通常thread経路（実装済み、受入ゲート継続）

call stack、thread ID、device context、fenceの収集、Unity render threadだけのcopy、Bridge own D3D11 device、3スロットの共有GPU ring buffer、WM message pump、resize、close/reopenを実装し、API検証を行いました。queue満杯時は新しいsubmissionをdropし、投入済みsubmissionを保持します。CPU readback常用は採用しません。Direct guardは別経路の受入まで維持します。Worker通常threadはWindows D3D11 Playerで同一プロセス再接続を含む実機確認を行い、新WM pump DLLのCTestも通過しています。光学、長時間lifecycle、正式配布署名、終了処理、macOS経路は別のrelease gateです。

## M2: profile・Rig・導入性（限定通過、実機範囲を継続確認）

高解像度profile、統合Rig、機器自動選択、installer、通常利用時のmanual DLL path不要化を整備します。Quality profile（Preview/Balanced/Native）は登録済みです。ViewWeave Rigはprovider registryから対応URP/HDRP adapterを追加します。M0のempty projectからUPM導入、Bridge runtime設定、Lit表示まで5分を目標とします。URP Sample PlayerはNative 48 view、全tile非空、赤緑の奥行き、移動、視差、124 draw、pending 0を確認しました。HDRP Sample PlayerもNative 48 view、全tile非空、赤緑の奥行き、移動、視差、125 draw、pending 0を確認しました。受入基準は、対応機器のprofile由来設定が一貫し、未対応機器を誤って対応表示しないことです。

## M3: per-view互換性（限定通過、未通過条件あり）

per-view postprocessing、UI、particles、VFXの互換性を検証します。consumer向けの経路はper-view処理、assemble、calibrateの順序を明示し、Quilt全体Bloomによるtile seamを避けます。受入基準は機能matrixと再現手順が揃うことです。

VFX GraphはLinear color spaceを必須条件とします。Unity Visual Effect Graph 17.6のURP Gamma非対応に従い、URP VFX driverはGamma時にresource生成前に停止します。Gamma VFX描画を対応範囲へ含めるには別のUnity公式対応変更が必要です。

現行guard追加前のprevious full Windows D3D11 PlayModeは123件中108件が通過、15件が未通過、skip 0です。Session 18/18、GRD 27/27、SRP追加10/10、URP MSAA 1/1、VFX driver 5/5を確認しました。追加のLinear MSAA境界試験は10/10で、このRTX GPUでは2x/4x/8x要求が対応倍率と一致しました。1x経路はpipeline 1x、allowMSAA無効、Deferred fallbackで確認し、低MSAA能力GPUは未検証です。resize/cleanup、中心色一致と対応MSAAのedge差分も確認しました。専用MSAA Player試験は未実施です。今回のCore/SRP/VFX Editor targeted試験はGamma 8/8（MSAA 7件とGamma VFX拒否1件）、Linear 17/17（SRP 11件とVFX driver 6件）で、full125件の結果ではありません。Gamma MSAA/通常URPのBridge Player実行は未検証です。Gamma VFXは公式非対応です。未通過はHDRP priming 1件とVFX compatibility 14件で、別実行のVFX matrixは20件中5件pass、15件failでした。未通過条件では十分な全tileの効果を確認できませんでした。D3D12 Player、網羅的な光学確認、署名付き正式配布は別のrelease gateとして残ります。個別fixtureの通過結果をすべての素材や効果への対応宣言には使いません。

## M4: 高速化研究（評価継続、production採用なし）

URP公開extensionを基礎に、culling/shadow共有の成立条件、pooling、command reuse、GPU-instanced multiview、depth reprojection（transparent、occlusion、reflection fallbackを含む）を測定します。共有culling prototypeは限定Lit条件で合格し、8視点マイクロベンチのCPU submission p50はbaseline 1.8023 ms、shared culling 0.3198 msでした。GPU性能、48視点、影、透明、post processing、VFXは未確認です。独自開発目標として同品質20% GPU/frame time改善を置くことはできますが、実績や保証として記載しません。受入基準は固定条件3-run以上のベンチと画質確認です。現時点でproduction経路へ採用しません。

## M5: alpha / beta / stable（実験版の公開準備、正式タグ未発行）

現在はWindows実験版のローカル導入再現を確認した段階で、正式なrelease tagは発行していません。alphaは実験的なWindows経路を明示でき、通常threadや実機の未解決事項を公開文書に残すことをゲートとします。betaは通常threadの再現性、Windows実機、互換性matrix、性能計測、権利確認をゲートとします。stableは通常threadの安定化とWindows・macOS実機確認、配布再現性、権利確認、release blocker解消を必須とします。各段階で未達項目がrelease blockerとして残る場合は次段階へ進めません。

SamplesはUPMの`Samples~`ディレクトリと`package.json`の`samples`定義によるoptional方式で登録します。利用者向けには、Core、Bridge、選択したrender pipelineを含む自己完結したURPまたはHDRP配布packageをGit URLから導入します。開発用のsource packageはテストproject内のローカル参照として維持します。

公式機能との差は、代替可能性を断定せず、機能matrixの検証結果で示します。
