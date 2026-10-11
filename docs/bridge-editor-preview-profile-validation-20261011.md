# HDRP SampleSceneのEditor機器Previewとprofile一致修正（2026-10-11）

HDRPの`Assets/Scenes/SampleScene.unity`で、Playでは出力できる一方、Editor機器Previewの開始が`Invalid ViewWeaveRig: QuiltRenderer settings do not match the rig profile`で失敗する問題を修正しました。

元Rigは`ValidateRig=true`で、保存profileとRendererはaspect 1でした。内部Editor producerを作る際、取得済みBridge geometryをRendererのaspect／view coneだけへ反映し、private Rigのprofileが元の値に残るため、Native出力開始前の整合性検証で拒否されていました。起動時の機器情報取得により、初回Editor Previewからこの条件が発生します。

共通`QuiltEditorProducer.Sync`で、取得後のRenderer設定からprivate Rigのprofileも同期します。Coreの検証条件・owner制約を維持し、機種名やOSでの例外は加えていません。機器出力を開始した後の同期では、Bridgeから新たに取得したdevice profileを保持します。元Rig・Renderer・sceneの保存値は変更しません。URP／HDRPのDistributionにも同じC#修正を反映しました。

## コード基準と自動試験

検証時の基準commitは`b7140a59fa025df3169584615e01e1381dfd7bae`で、本修正のsource／fixture／Distributionは未commitでした。[構造化記録](validation/bridge-editor-preview-profile-20261011.json)にsource hash、XML hash、全test名と実Editor結果を保存します。

macOS Metal EditModeは**27/27 PASS、skip 0**です。新private producer 6件、既存geometry 15件、既存Metal Preview／ownership／playback 6件を実行しました。新6件は両output componentと縦長／横長の合成Bridge値でprofile一致・source不変を確認し、device同期で保存geometryが取得済みdevice profileを上書きしないことを確認します。fixtureはURP adapterを使う共通コード試験で、Windows実行やHDRP描画の証拠ではありません。

初回の新fixtureは12セルに対して11 viewsという無効なRig profileを指定し、producer作成前に6件失敗しました。完全gridの12 viewsへ訂正した最終runだけを成功件数へ採用します。初回21 PASS／6 FAILを合算しません。

Nativeは変更・再buildしていません。既存macOS arm64 shimは160,016 bytes、SHA-256 `c97856ea3bb910a1456eb044347a18ac8117c140ddd149f8e9ec240600766c5b`で、packageと両Distributionで一致します。Windows transport・runtime・同梱DLLも変更していません。

## 実HDRP SampleSceneの確認

Unity 6000.6.4f1、HDRP 17.6.0、macOS 26.6.2、Apple M3 Max、Metal、Bridge 2.6.3、Looking Glass Goで確認しました。開いていた元のSampleSceneを保存せず、公開`BridgeMetalEditorPreview.Start(rig, true)`をUnity CLIから実行しています。Inspectorボタンのクリック試験とは区別します。

修正前は元Rigが有効なのに同じprofile不一致で開始失敗し、sceneはdirty=falseでした。修正後はEdit Modeのまま開始に成功し、内部Rigも有効、profile／Rendererのaspectはともに0.5625、view coneはともに54でした。継続描画中に90 rendered／65 presented、停止直前には565 rendered／216 presentedを取得し、Preview errorは空でした。

GPUから読んだQuiltは4092 × 4092です。16画素間隔の1,046,529 sampleはすべてnon-black、magenta 0でした。raw Quiltを目視し、Game Viewと同じ庭園の複数viewが描かれていることも確認しました。このGPU画像確認はパネル上の光学品質を証明しません。Consoleのground truthはerror 0・既存warning 11、compile failure=falseです。

Stop後はactive／stopping=false、Editor Preview record 0、Metal GPU retirement 0、元Rigは有効でした。開始前後のRig／Renderer serializationが一致し、元sceneファイルのSHA-256も不変、dirty=falseを確認しました。Editorは開いたままです。Play成功はユーザー報告であり、今回のagentによる再実行には数えません。

Windowsの新revisionでの機器Preview、URPサンプルの新revisionでの機器Preview、pause/resume・PreviewからPlayへの遷移、Player、長時間運用、光学品質は今回未検証です。

## 管理範囲

共通source、回帰fixture、両Distribution、本report・構造化結果・HTML対応表をcommit対象とします。詳細CLI結果・XML・GPU画像は無視対象`.local/metadata-preview-profile-20261011/`に保管し、commitしません。元SampleSceneや個人用Editor設定は登録しません。
