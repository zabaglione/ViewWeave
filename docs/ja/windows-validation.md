# Windows検証状況

[English](../windows-validation.md)

> ここでは記載したrevision、platform、条件に限った検証結果を扱います。他の構成での動作を保証しません。Windowsの過去記録を含む詳細は[English版](../windows-validation.md)、現行のHDRP history pathの正本は[HDRP view-history validation](../hdrp-view-history-validation-20261010.md)を参照してください。

## HDRP isolated view history（2026-10-10）

Unity 6000.6.4f1のMetal batch `GraphicsCompatibility`は52/52 PASS、skip 0でした。これは記載したfixtureと実装検査の範囲です。Windows D3D11/D3D12/Playerは現行のhistory pathで未検証で、個々のHDRP effectの画素正確性も未確認です。

- `FollowScene`はfixture lightを100から1600（fixture上の単位。実測luxではありません）へ変更したとき、共有exposureが1 EV超増加しました。scene Fixed exposure 8.5、compensation 2は有効EV 6.5になりました。中立な初回meter sampleを破棄し、warmup中はQuiltを出力しません。
- 論理viewごとのhistory Camera、Quarter mode、resize、Stop後の状態復元をfixtureで確認しました。別の「66 Full」観測ではgraphics-driver growth 2451 MB、RenderTexture descriptor推定1287 MBでした。この計測は一時的なpreview-copy budget 8192 MBを使っており、製品既定値ではありません。
- 接続Goの実scene確認では、利用者から「光と明るさが戻り、向き・立体感も自然」と評価されました。限定的な実機確認であり、全視点の光学レビューではありません。
- macOS描画専用Development／Release Playerでも、4視点の履歴・共通露出・解放・予算制限を確認しました。Windows PlayerとmacOSのBridge／機器出力Playerは未検証です。
- SSR、SSGI、volumetric fog/cloud、Motion Blur、TAAの画素正確性は未検証です。Volume設定の保持試験は、各effectの視覚品質passを意味しません。

HDRPの既定は`IsolatedViewHistories`と`FollowScene`です。旧shared-camera effect変更は`SharedCameraCompatibility`を明示選択した場合のみ適用されます。History Resource Budgetの既定値は4096 MB、Maximum History Camerasは256です。budgetはgraphics-driver growthの観測値と新規/拡大RenderTexture descriptor推定の大きい方を使います。Releaseではdriver-growth counterが0で、推定には未観測bufferとdriver overheadを含みません。完全なVRAM上限ではありません。camera数の上限はEditorと全Player buildで適用されます。

## Windows実行範囲

新しいHDRP history pathをWindows D3D11、D3D12、Playerで実行した結果はありません。既存のWindows D3D11記録は旧package snapshot/shared-camera behaviorの履歴であり、現在の既定実装の成功証拠として扱いません。過去のtest総数やbenchmark記録は[English版](../windows-validation.md)に記載した原文の条件を維持します。
