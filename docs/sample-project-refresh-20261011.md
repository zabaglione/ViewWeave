# URP/HDRPサンプルプロジェクトの更新 — 2026-10-11

ローカルの `URP_60000604` と `HDRP_60000604` に、mainの `e9232d31afdbb80f2577c12955d9d060dba44441` を反映しました。両プロジェクトは既に `Distribution/urp`／`Distribution/hdrp` をlocal UPM packageとして参照しており、その参照方式を維持しました。production sourceへの追加修正はありません。

Unity 6000.6.4f1、URP/HDRP 17.6.0、macOS Metal、Apple M3 Maxで確認しました。[構造化結果](validation/sample-project-refresh-20261011.json)には各チェックの結果、パッケージ参照先、取り込んだファイルとnative shimのhashを保存しています。

## 更新内容

Package ManagerのSample APIから、現在のURP Quick Start／HDRP Quick Startを `Assets/Samples/` へ取り込みました。新しい `QuickStart.unity` は同一シーン内でMetalまたはWindows D3D11/D3D12の出力を選ぶ構成です。

旧 `Assets/ViewWeaveQuickStart/` と素材のGUIDが重複し、Unityが新素材のGUIDを再生成したため、新しいシーンとAnimator Controller内の参照をUnityのSerializedObject APIで新しい素材へ揃えました。URPは7参照、HDRPは8参照を修正し、Material、Controller、Animation Clipがすべて今回取り込んだフォルダを参照することを確認しました。

既存ファイルのsnapshotとの比較では、変更した既存ファイルは両プロジェクトの `ViewWeaveTemplatePackageInstaller.cs` のみです。古い固定commitを再導入する補助メニューを、現在のlocal package参照へ揃えました。既存シーン・素材・ProjectSettings・manifestはこのsnapshotから変わっていません。snapshotは最初のURP起動後、Sample取り込み前に取得したものです。

## 確認した範囲

両pipelineについて、取り込み後のEdit Mode、Play Mode、Play停止後に以下を確認しました。これはNUnit suiteやQuilt描画の試験ではなく、各サンプルの参照と起動状態のチェックです。

- Rig validation成功、Missing Script 0、非対応material 0、有効Audio Listener 1。
- Metal出力が選択され、Windows Worker／Sessionは無効。サブピクセル選択のserialized propertyが存在。
- 自動出力開始・自動Quilt描画は無効。停止後も未保存scene変更なし、pending retirement 0。
- 最終Consoleのground truthは、compile failureなし、errors 0、warnings 0。
- 実際のGame View、Hierarchy、Rig Inspectorを確認。描画された3つの立体にピンク表示やMissing Script表示は見られませんでした。GUIは画面上で観察し、画像ファイルはこの記録に添付していません。

起動・再コンパイル時には、既存SDKの未使用field（CS0414）と非推奨FindObjectsByType overload（CS0618）の警告がありました。最終Consoleの0件と、session中の警告履歴は分けて構造化結果に残しています。

操作中のHDRP起動1回とURP再起動1回はPipeline ready前に終了しました。プロセス不在を確認して再起動し、最終チェックを完了しました。新しいUnity crash reportは見つかっておらず、終了原因は未特定です。またCLIのquit commandはEdit ModeでPipeline側のDontDestroyOnLoad診断に失敗したため、最終終了にはUnityEditor.EditorApplication.Exitを使いました。これらの試行をSDK成功件数として扱っていません。

## 対象外と保存先

今回はWindows、Standalone Player、Quilt描画、Bridge機器出力、特殊セル配列のGPU試験、パネルの光学品質を再検証していません。通常のMain CameraによるGame Viewの表示を、Looking Glass出力成功の証拠には使いません。既存の[macOS共通契約検証](macos-unified-subpixel-validation-20261011.md)とは別の範囲です。

サンプルプロジェクトはViewWeave repository外にあるlocal projectで、今回の取り込み内容自体はrepositoryへ追加していません。更新記録と[HTML対応表](ja/unity-feature-platform-matrix.html)を管理対象とします。raw CLI応答、Editor log、作業用snippet、変更前の補助installerはignored `.local/sample-project-refresh-20261011/` に保持し、commit対象には含めません。
