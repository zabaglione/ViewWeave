# 起動時のBridge機器情報取得と上段の再取得UI（2026-10-11）

Windows／macOS共通のEditor処理で、sceneを開いた時点でBridge APIから機器形状を取得します。出力・Preview・Quilt生成は不要です。取得した縦横比・視野角はfocus／near／far Gizmoへ、有効な物理寸法はPhysical Display Framingへ自動反映します。機種名から寸法を推測する処理はありません。

Rig Inspectorの最初の展開済み項目を **Looking Glass Display** にしました。取得できない場合は機器接続とBridge Desktop Modeの確認を促し、同じ場所の **Refresh Connected Displays** で再試行できます。従来の奥にあった **Use Connected Display Size** とWindows専用のコピー処理を削除しました。Physical 1:1 Framingの有効／無効、units per meter、FOV、保存されたfocus distanceは自動変更しません。

## 接続変更と情報保持

[Bridge公開Native API一覧](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference)で接続変更通知のcallbackを確認できなかったため、今回は起動時に一度自動取得し、後から接続した時は上段ボタンで再取得する方式です。scene・Rig・接続設定の変更は新しい取得対象になります。取得失敗後の定期的なSDK再初期化や機器一覧のpollingは行いません。非同期requestが完了するまでのstatus確認は機器の定期探索とは区別します。

同じ接続設定の取得失敗・機器未接続では最後の正常geometryを保持し、現在のoutput connectionとは区別して表示します。取得元／Rig／選択ordinal／runtime path／application nameが変わった結果や、失効したownerの結果は採用しません。Prefab asset／Prefab Mode／preview scene／非保存の内部producerは自動取得対象外です。複数Rigのrequestは共通queueで処理します。

## APIとlifecycle

共通managed coordinatorは、idle時の既存metadata ABIを両OSで使います。macOSにmetadata専用workerを追加しました。`get_displays`、`get_dimensions_for_display`、`get_viewcone_for_display`、`get_default_quilt_settings_for_display`と校正のDPIを使用し、GPU・output window・セル配列の取得は行いません。各geometry queryの失敗は拒否し、view cone 0以下のmanaged snapshotも採用しません。

Macのmetadata workerとMetal出力／finite providerは同じSDK admissionを使い、並行SDK所有を拒否します。metadata requestはinitialize・query・uninitializeをbackground threadで実行し、shim自体をprocess内にpinします。READY後もCloseとEndを分け、cleanupが証明された後にだけ所有を解放します。shutdown失敗時は再初期化を拒否します。Playへの移行・reload・Editor終了ではrequestをcancelし、Play終了後には新しい初回取得を許可します。

Windowsの稼働中output／device Previewでは既存workerのrefreshを使い、実際のproducerとtoken完了を照合します。`RequiresReconnect`のsnapshotはGizmoへ流用しません。今回のWindows Native sourceにはgeometry query失敗検査を追加しましたが、同梱Windows DLLは従来のC864 artifactのままです。Windows Nativeの再build・実機実行は今回のMac検証には含みません。

## 検証コードとartifact

実装commitは`64803f6d221965deb56f59b50510a19fe71d3f6e`です。検証時の基準commitは`1ac8ae1a102bd616cf7c966f0ce8f168ac6a8867`です。検証時は本変更のsource／fixture／Distributionが未commitでした。[構造化結果](../validation/bridge-authoring-discovery-20261011.json)に測定source、全test名、result XML hash、sampleの取得結果を保存します。この記録はrelease保証ではありません。

macOS arm64 Release shimは160,016 bytes、SHA-256 `c97856ea3bb910a1456eb044347a18ac8117c140ddd149f8e9ec240600766c5b`です。本体package・URP・HDRP Distributionで一致します。URP実プロセスのloaded imageがURP Distributionの同shimであることも確認しました。以前の`f2539810...`／157,712 bytesの出力結果を、このartifactでのPlayer出力成功として扱いません。

## 自動試験

- Native CTest: **10/10 PASS、skip 0**。metadata ABI、縦長／横長・非連続physical index、重複owner拒否、Close／End順序、取消、再開始、初期化・列挙・個別query失敗、shutdown失敗時の所有保持を含みます。fake SDKはwindow／GPU APIを持たず、セルpayloadの要求も拒否します。
- macOS Metal EditMode: **79/79 PASS、skip 0**。新authoring discovery 23、共通geometry 15、共通authoring Inspector 25、Metal Inspector 16です。両output componentでAPI結果からGizmo・物理寸法への反映、未接続／失敗時保持と接続案内・ボタン再試行、設定変更後の不採用、reload取消、初回一度だけで再pollingしないこと、非保存producerの除外、寸法不足、Undo、mutation owner拒否を確認しました。これはMac上の共通fixtureであり、Windows実行でもURP／HDRPの描画試験でもありません。
- macOS Metal PlayModeの既存discovery lifecycle: **20/20 PASS、skip 0**。これもfake native adapterのcoordinator試験です。

初回はfixtureの誤ったfield名でcompile失敗・0件となり、修正後もfake native adapterにproduction request guardが働いて失敗しました。production guardを維持した専用test入口へ修正した最終runだけを採用しています。削除した旧コピー操作のInspector期待値も新しい共通UIのcontractに置換しました。初期失敗runを上記件数へ加算しません。

## 実サンプルとGo

Unity 6000.6.4f1、URP／HDRP 17.6.0、Apple M3 Max、macOS 26.6.2、Metal、Bridge 2.6.3、接続したLooking Glass Goで実行しました。ローカルのURP_60000604／HDRP_60000604は、それぞれ更新したDistributionをfile UPM参照しています。

両projectの個人用geometry cacheを退避した起動で、QuickStartの保存aspect 1に対し、Bridge取得後の実効aspect・focus／near／far枠はすべて **0.5625（9:16）**でした。物理寸法は **74.4928741 × 132.431763 mm、DPI 491**です。上段ボタンの実操作後も同じ値でした。全captureでEdit Mode、Metal output停止、worker Closed、submitted／presented 0、Preview無効、QuiltTexture未生成、pending retirement 0です。`isConnected=false`は出力connectionがないという意味で、機器情報を取得できなかったという意味ではありません。

Scene Viewの紫の縦長focus枠、Game View、Hierarchy、Rig Inspector、上段の再取得ボタンを両pipelineで目視確認しました。最終Consoleは両projectともerror 0・warning 11です。warningは既存packageの廃止予定API等で、warning 0とは記載しません。URPではRig validation成功・Missing Script 0も読み取りました。

サンプルsceneを保存する前に検証用コピーとbaselineを比較し、機器寸法の変更とURP／HDRP標準の追加Lightデータ補完を確認しました。ユーザーがその後sceneを編集してEditorを閉じたため、以降は元sceneへの保存を行いません。検証用コピーでの出力確認は機器情報だけの試験と分けます。

CLI経由の一部起動はMetal初期化中に終了し、成功に数えていません。ready状態の別起動で上記を確認しました。検証snippetのnamespace不足によるcompile失敗も製品compile／試験成功とは区別します。

## 検証用コピーでのGo出力回帰

自動取得だけの確認後、ユーザーが元sceneを編集してEditorを閉じました。それ以降は元sceneを保存せず、作業前baselineのQuickStartから作った一時コピーだけでPlay時の自動出力を有効にしました。最終URPは提示／submit 933／934、HDRPは327／328で、両方とも出力errorは空でした。Quiltは2046 × 2046で、16画素間隔の261,633 sample中、URPはnon-black 46,455・red 22,785・blue 11,445、HDRPはnon-black 46,130・red 29,553・blue 4,421、両方ともmagenta 0でした。パネル上の立体感を人間が判断した証拠ではありません。

両方でStop後のoutput／closing停止・GPU retirement 0、Play終了後の9:16保持、Console error 0を確認し、Editor processはcode 0で終了しました。Preview、pause/resume、再開始、録画・画像保存、Playerはこの回帰試験に含めません。通常QuickStartは`startOnEnable=false`で、自動出力を有効にしていなければPlayだけではGo表示は始まりません。今回そのサンプル既定を変更していません。

最終レビューで、Bridge履歴のないLocal Previewを「Last successful Bridge geometry」と表示する問題を修正しました。Bridge由来の4 sourceだけを許可しました。LocalRendererSettings／LocalPreviewの共通UI 2 caseと、接続中のprofileが残っていても再取得失敗の案内を隠さないcaseを追加した最終EditMode 79件で確認しました。active outputの成功表示は現在の取得requestがないときだけ使い、処理中・失敗statusを保持します。初回機器情報・HDRP出力のcaptureはsource表示制限の修正前、URP出力はその修正後です。再取得statusの修正は両出力測定後のUI変更です。Native・geometry・output sourceとmetadata query処理は測定時から変更していません。URP初回のCLI Play終了後captureはまだplaying／outputRunning=trueであり、終了証拠に採用しません。最終runでは検証コピーのPlay内で再自動開始を無効にしてStopし、GUIのPlay buttonでEditへ戻した後、playing／outputRunning／outputClosing=falseとretirement 0を別captureでassertしています。

## 実装commitのCI

[GitHub Actions run 38128398151](https://github.com/zabaglione/ViewWeave/actions/runs/38128398151)は実装commit `64803f6d221965deb56f59b50510a19fe71d3f6e`で5/5 job成功です。static検査、portable Native 5/5、Windows transport 15/15、Windows fake-runtime regressions 19/19、macOS Metal Native 10/10を確認しました。CTestのskipは0です。これはCIでのsource buildとNative fixtureであり、Windows Unity Editorの自動取得UI・接続実機の検証ではありません。CIでbuildしたWindows artifactによる同梱C864 DLLの置換も行っていません。

## 未検証と管理範囲

Windows上の起動時取得／上段UI／実機refresh、新しいmacOS artifactのPlayer出力、実機の抜き差し、16 Spatial Landscape、複数接続機器、callback通知、光学品質、長時間運用は未検証です。合成データの縦長／横長成功を全機種の実機成功へ広げません。

製品source・fixture・native artifact／provenance・両Distribution・本report／構造化結果・HTML対応表はcommit対象です。raw XML、詳細ログ、Editor状態、検証コピー、元scene/cacheの退避は無視対象`.local/metadata-discovery-20261011/`へ保管し、commitしません。機器serial・認証情報・Unity起動のsession引数を共有記録に含めません。HTMLはcolumn数・anchor・相対link・tagの読み取り検査で確認し、ブラウザ描画を確認したとは扱いません。
