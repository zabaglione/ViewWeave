# 現行macOS校正出力の再検証

確認日: 2026-10-11。対応表は[現在の対応状況](../ja/unity-feature-platform-matrix.html)、数値と検証コードのSHA-256は[構造化結果](../validation/macos-metal-current-20261011.json)を参照。

## コードと試験条件

- 基準commit: `abf33b357637b43d5aa135c3f086910b8dff53b5`。この作業で追加した`MetalCurrentValidation` fixtureとcapture試験の待機修正を使用。製品のnative／managed出力コードは変更していない。
- Unity 6000.6.4f1、URP／HDRP 17.6.0、macOS 26.6.2、Apple M3 Max、arm64、Metal、Linear、Looking Glass Bridge 2.6.3。
- 接続機器はLooking Glass Go。機種名・serialで限定する経路を使わず、Bridge校正に基づくV2 native APIを使用。Preview品質は1023×1020、11列×6行、66視点。パネル出力は1440×2560。
- URPは4x MSAA設定。HDRPはFollowSceneとFixed Exposure Volume（EV 0）の小規模Unlit cube／sphere scene。今回の機器試験はTAA、動的露出、Fogなど各効果の画素品質を証明しない。
- macOS native dylibはSHA-256 `150e3a01ca3acae31e5f25d6d7f60d2dbc21d926078d0cc068c0e5ef4d23cb1b`。本体・URP・HDRP配布版と各Playerに同じartifactを使用。

## 現行版で確認した項目

- EditModeローカルPreviewの既存試験: 6/6 PASS、失敗0、skip 0。Pause／Close、所有権と不正layoutの解放、authoring window／Inspector lifetime、Playbackの開始条件を含む。
- PlayMode capture／録画: URP・HDRP各1件、2/2 PASS、skip 0。PNG／JSON、寸法・向き、中央tile一致、カメラ復元、Full／Half／Quarter、10／25 FPSの3枚録画、Pause／Resume、manifestと時刻を確認。これはfake-output fixtureであり、機器転送の試験とは分ける。
- URP MSAA lifecycle: 4/4 PASS、skip 0。Gamma／Linearの色、GPU対応倍率、resolve、resize、停止を既存fixtureで確認。パネル上のギザギザ改善を測定したものではない。
- GUI EditorでURP・HDRPそれぞれ、ローカルPreview 9フレーム、機器Preview 45フレーム、双方のPause／Resumeを確認。停止後Editor resource／native retirementは0。
- Editor Playで各pipelineの60フレーム提示完了、PNG／中央view保存、3枚の無音録画、停止・再開始後30フレーム、最終停止・retirement 0を確認。
- Editor Playの静止Quiltは1回提示と保持を確認。1 FPSの3枚連番は全index（0／1／2）、Pause／Resume、skip 0、EOF後のlive復帰を確認。再生資源とnative保持は最後に0。
- GUIでGame Viewの赤cube／緑sphere、HierarchyとQuilt Rig Inspectorの参照、Consoleのerror 0／warning 0を確認。EditorはGUIのQuitで自然終了した。

URP／HDRPそれぞれのDevelopment／Release Player、計4/4でビルド・実行・自然終了code 0を確認。各60フレーム提示、保存・3枚録画、停止・再開始後30フレーム以上、最終GPU保持0。保存PNGの赤／緑画素が存在し、magenta画素0を閾値による読み取り検査でも確認した。具体値と画像hashは構造化結果の`players`に記録する。Playerの再生UIはEditor専用APIのため今回のPlayer fixtureには含めない。

## 判定を厳密にするfixture修正

既存HDRP capture試験の初回実行は、単一の`RenderQuilt()`直後にcompletion 1を要求して失敗した。現行FollowScene露出は非同期測光の準備中にQuiltを完成させないため、15秒の上限まで実際のcompletionを待つよう修正した。タイムアウトをPASSにせず、完了後の既存の画素・復元・保存・録画assertionは維持している。

新しいEditor fixtureの初期再生判定は静止画・3枚連番に15提示を要求していたため、完了結果には使っていない。静止画は先行するlive submissionの総数を越えた提示完了、連番は全frame index・EOF・live復帰を判定するよう修正した。レビューで停止時の`LastError`を再開始前に確認する必要も見つかり、停止直前／完了後と再生中のエラーをFAILにした最終コードでEditor／Playerを実行した。

## 証拠の保存と再実行

raw XML、runtime／Editor JSON、PNG／sidecar／録画、build／Playerログとappはignoredの`.local/metal-current-validation-20261011/`に保存した。これらの大きい生成物と個人環境を含むrawログはcommitしない。集計値、各試験名、source hash、artifact hash、結果JSONのhashと自然終了コードを共有可能な構造化結果に保存する。追加fixture、検証記録、HTMLは管理対象。

Unity Editorを閉じた状態で、各runに新しい出力directoryを指定する。CLIの`unity`は公式Unity CLIを使用する。

```sh
metal_validation_root="$PWD/.local/metal-current-rerun"
unity test Tests/Pipelines/Unity --mode EditMode --filter MetalEditorPreviewTests --output "$metal_validation_root/local.xml" --timeout 300 --format json -- -force-metal
unity test Tests/Pipelines/Unity --mode PlayMode --filter CaptureAndThreeFrameRecording --output "$metal_validation_root/capture.xml" --timeout 300 --format json -- -force-metal
unity test Tests/Pipelines/Unity --mode PlayMode --filter QuiltRendererMsaaLifecycleTests --output "$metal_validation_root/msaa.xml" --timeout 300 --format json -- -force-metal
unity projects open Tests/Pipelines/Unity --wait --args "-force-metal -executeMethod MetalCurrentEditorValidation.Begin -validationPipeline URP -validationRoot $metal_validation_root/urp-editor" --format json
```

HDRPは`-validationPipeline HDRP`と別directoryで実行する。Editor結果の`passed`、resource／retirement 0とConsoleを確認してからQuitする。機器出力開始にはBridgeの起動、native解像度の拡張画面が必要。

Playerは同じprojectで`unity run`の`-executeMethod MetalCurrentValidationBuilder.Build`、`-validationPipeline URP|HDRP`、`-validationConfiguration Development|Release`、`-validationRoot`、`-buildOutput`を指定して生成する。生成appの`Contents/MacOS/PipelineSmoke`を起動するとfixtureが出力・保存・録画・停止・再開始を実行し、GPU解放後に`Application.Quit`で終了する。runtime JSONだけでなく終了code 0も必要。

## 未確認の境界

他モデルの実機出力、パネルを人が見た立体感・向き・光学品質、回転表示、Intel、全scene・効果、長時間負荷、Player window-close／強制終了は未確認。Bridgeから取得できるセル配列／非0 modeの解釈は未実装のまま明示拒否する。[機種とセル校正の調査](bridge-subpixel-calibration.md)では、公開資料から旧機種・現行機種の対応表を特定できていない。

Windowsの結果はこのmacOS runに含めず、既存のWindows記録に従う。旧Go経路の光学確認を現在の光学確認に流用しない。
