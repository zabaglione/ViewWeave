# 現行dylibのmacOS機器出力Player検証（2026-10-14）

本記録は`11312b8`時点の結果です。後続のpull（`c4de573`）によるCore変更後の結果は[最新版の再評価](macos-preview-shutdown-validation-20261014.md)を参照してください。

現行の同梱arm64 dylibを使い、URP／HDRP × Development／Releaseの4条件で、接続機器向けMetal出力・停止・再開始・GPU資源回収・Playerの自然終了を確認しました。**4/4 PASS、fail 0、skip 0**です。今回の受入条件では製品コード修正を要する問題は再現していません。終了時のUnity警告・診断は下記に残しています。

## 環境とsource／artifact

- source基準: `11312b8c6cf824d9a7ac7c8940e95cee742fa076`。製品sourceと既存のPlayer fixtureは未変更です。先行調査の文書変更だけが未commitの状態でした。
- Unity 6000.6.4f1、URP／HDRP 17.6.0、macOS 26.6.2、Apple M3 Max、arm64、Metal、Linear。接続した拡張画面は1440×2560、校正はtableなしpattern mode 0です。
- 本体package・URP／HDRP Distribution・4 Player内のdylibは160,016 bytes、SHA-256 `c97856ea3bb910a1456eb044347a18ac8117c140ddd149f8e9ec240600766c5b`で一致しました。新しいdylibのbuild／置換はしていません。
- 各PlayerのPIDに対する`lsof`で、そのapp内のdylibが実ロードされていることを確認しました。Looking Glass Bridge 2.6.3の`libbridge_inproc.dylib`も実ロードされ、SHA-256は`3eff8aa52532e1da0a0162a05645d22d63d0997e9ca44e162be087701f0dc1d8`です。
- fixtureはPreview品質、1023×1020、11×6／66 views、赤いUnlit cubeと緑のUnlit sphereです。URPはMSAA 4設定、HDRPはFixed Exposure EV 0です。TAAを明示有効化した試験ではありません。

source／fixture／log／PNG／runtime結果のhash、PID、構成一致gateは[構造化結果](../validation/macos-current-player-20261014.json)に保存しています。[旧Player 4/4](macos-unified-subpixel-validation-20261011.md)の`f2539810...`とは別の、現行`c97856ea...`の実行結果です。

## 4条件の結果

| Pipeline | 構成 | 初回完了frame | 再開始後完了frame | 録画frame | 最終GPU保持 | Player exit |
|---|---|---:|---:|---:|---:|---:|
| URP | Development | 60 | 31 | 3 | 0 | 0 |
| URP | Release | 60 | 30 | 3 | 0 | 0 |
| HDRP | Development | 60 | 30 | 3 | 0 | 0 |
| HDRP | Release | 60 | 30 | 3 | 0 | 0 |

全件で実pipeline／shader tag、Metal、`editor=false`、Development／Releaseの一致、今回生成されたruntime記録を照合しました。Editor buildとPlayerは自然終了し、timeout／強制終了で成功扱いにしたcaseはありません。初期2件のmonitorには構成・鮮度gateがなかったため、最終集計で明示照合しました。後続2件ではmonitorにもgateを追加しています。

全件でAutomatic出力、稼働中mode変更の拒否、Quilt PNG／JSON sidecar保存とQuilt画像の復号・画素検査、中央view PNG保存、3枚のPNG録画のmanifest・frame数・各PNG header／sidecar検査、明示Stop後のGPU retirement 0、tableなしCalibratedCellsの開始拒否、ClassicRgbへの切替と再開始を確認しました。画像を復号して画素検査したのはcurrent／classic Quilt PNGです。中央viewの復号・画素比較や録画全frameの画像復号はfixtureの対象外です。最終Stopではclosingの完了とGPU retirement 0をfixtureが確認してから`Application.Quit`しました。赤／緑は各1,000画素超、magenta 0で、URP／HDRPそれぞれのDevelopment版の保存Quiltも目視しました。

`CompletedFrames`はNative Metal presenterのGPU command完了を数えます。接続機器向け出力経路の実行証拠ですが、物理panelのscanout、上下方向、立体感、光学品質を人間が確認した結果ではありません。画像gateも全66 tileの個別画質を判定するものではありません。

## 終了ログの警告と診断

4 Playerとも`Thread ... may have been prematurely finalized`が5件あります。Development版には終了時の`MemoryLeaks`／`Immediate`診断もあり、URPは2,388,930 bytes、HDRPは2,674,828 bytesでした。Release版には同診断の記録がありませんが、これをメモリ漏れゼロの証明とは扱いません。

旧`f2539810...`のDevelopment検証ログにも同じthread警告5件と`MemoryLeaks`診断があります（URP 2,488,004 bytes、HDRP 2,672,018 bytes）。これらの観測だけでは原因、ViewWeaveへの帰属、一般的なメモリ解放の正しさを確定できません。今回のGPU retirementとexit codeのgateは通過していますが、「警告なし」「全メモリ解放済み」とは記載しません。原因を推測した製品修正は行っていません。

## 手順と復元

既存`MetalCurrentValidationBuilder.Prepare`を`unity run`で実行し、別のfresh non-batch Editorを`unity projects open --wait`で起動して`BuildWhenReady`を実行しました。要求pipeline／shader tagが実際に有効になってから、`Build`がarm64／MetalのPlayerを生成しています。各appの`Contents/MacOS/PipelineSmoke`を一つずつ起動し、runtime結果と実プロセスの終了codeを別々に確認しました。機器出力の同時実行はしていません。

作業前の検証projectのAssets／ProjectSettings／Packages計334 fileを退避し、終了後に全fileのSHA-256一致を確認しました。Unityが変更した9 fileと追加した2 fileを復元・除去し、製品source、fixture、manifest／lock、既存sceneに追加変更はありません。検証EditorとPlayerは終了し、Bridgeアプリは元の状態で稼働しています。

rawログ、PNG、app、監視script、復元用退避はignoredの`.local/macos-current-player-20261014/`に保管します。共有する本report・構造化結果・[対応表](../ja/unity-feature-platform-matrix.html)だけをローカル更新し、commit／push／releaseは行っていません。

URP機器Editor Preview、両pipelineのPreviewからPlayへの遷移、PlayerのPlayback出力、GRD、各効果の画素正確性、hotplug、複数機器、Intel、長時間、window-close／強制終了は今回の試験対象外です。次の優先項目はURP機器Previewと両pipelineのPreviewからPlayへの遷移です。
