# macOS 最新版・Preview遷移・終了診断の再評価（2026-10-14）

`c4de573c4435f0105e62f6125f8d4fedd78786b3`をpullした後、URP／HDRPの機器出力Playerと機器Preview→Play遷移を再検証しました。指定した条件ではすべて自然終了し、製品側の不具合は再現しませんでした。終了時のthread警告とDevelopment版の`MemoryLeaks / Immediate`は、ViewWeaveを取り除いた比較Playerでも再現します。これらの診断とWindows終了クラッシュの共通原因は未確定です。

根拠は[構造化結果](validation/macos-preview-shutdown-20261014.json)と[共有証跡](validation/macos-preview-shutdown-20261014/)に保存しました。これは独自fixtureの実行記録であり、NUnit全suiteの合格件数ではありません。検証時は未commitのfixture修正を使用しました。基準commitと実行時の変更範囲を記録しています。

`runtimeSha256`は非公開raw runtimeのhashです。共有JSONはローカルpathを置換しているため、共有ファイルの照合には[MANIFEST](validation/macos-preview-shutdown-20261014/MANIFEST.json)のhashを使ってください。

## pullによる変更と検証基準

- `11312b8`から`c4de573`へのfast-forward。Coreのview形式選択、MSAA/HDR→RGBA8 resolve、graphics state復元とURPのHDR target選択が変更されています。history予算には診断情報が追加されています。
- Native／Bridge packageの変更はありません。現行arm64 dylibは160,016 bytes、SHA-256 `c97856ea3bb910a1456eb044347a18ac8117c140ddd149f8e9ec240600766c5b`。SDK packageとURP/HDRP Distributionの3ファイルが一致しています。
- 実行環境はUnity `6000.6.4f1`、URP/HDRP `17.6.0`、macOS `26.6.2`、Apple M3 Max、arm64／Metal、Bridge `2.6.3`、Goの単機器接続です。機器出力は一度に1クライアントだけ実行しました。
- Preview品質、1023×1020、11×6／66視点、Linear。URPはHDR有効・MSAA4、HDRPはFixed Exposure EV 0の単純Unlit赤Cube／緑Sphereです。VFX、TAA、任意材質、光学精度の検証ではありません。
- 以前の[Player記録](macos-current-player-validation-20261014.md)と[取得時のNative build記録](macos-latest-intake-validation-20261014.md)は`11312b8`時点の証拠として保持し、新Coreの結果へ流用していません。製品source hashは構造化結果に記録しています。

## 最新Playerの機器出力と正常終了

| Pipeline／構成 | PID | 初回／再開のpresentation完了 | 終了code／GPU pending | 終了thread警告 | Development Immediate bytes |
| --- | ---: | ---: | --- | ---: | ---: |
| URP Development | 25728 | 60／30 | 0／0 | 5 | 2,529,744 |
| URP Release | 26020 | 60／30 | 0／0 | 5 | 診断recordなし |
| HDRP Development | 26728 | 60／30 | 0／0 | 5 | 2,689,598 |
| HDRP Release | 27191 | 60／30 | 0／0 | 5 | 診断recordなし |

全4条件で組み込みdylibのhash一致と、実行中のそのmoduleのロードを確認しました。実際のpipeline、shader tag、Metal、Player／Development flag、新しいruntime結果の生成時刻を照合しています。Automatic出力、所有中のmode変更拒否、通常Stop、cell tableを持たないGoでのCalibratedCells開始拒否、ClassicRgb再開を確認しました。

URPはview=`R16G16B16A16_SFloat`・linear・MSAA4、resolve／quilt=`R8G8B8A8_SRGB`・single sampleを実測しました。HDRPは既定のRGBA8 sRGB・single sampleでresolveなしでした。これは形式・encoding・sample数の検証です。1.0を超えるHDR画素の保持、Bloom、TAA、見た目の一致は証明しません。

current／classicのQuilt PNGをdecodeし、赤／緑が各1,000画素超・magenta 0を確認しました。中央view PNGも保存しています。録画3枚のframe count、PNG headerと寸法を確認しましたが、録画全frameのdecode／画素確認とPlayerからの再生出力は未検証です。presentation完了は実機経路の完了観測であり、光学品質の確認とは分けています。

## 機器PreviewとPreview→Play

| Pipeline／Domain Reload | PID | 反復 | Preview／Play完了frame（各反復） | 実測reload | 自然終了／最終pending |
| --- | ---: | ---: | --- | ---: | --- |
| URP／有効 | 29765 | 2 | 18／30 | 2 | 0／0 |
| URP／無効 | 29906 | 2 | 18／30 | 0 | 0／0 |
| HDRP／有効 | 30007 | 2 | 18／30 | 2 | 0／0 |
| HDRP／無効 | 30134 | 2 | 18／30 | 0 | 0／0 |

Scene Reloadは有効のままです。Previewのpause保持／resume、画像の赤／緑とmagenta 0、新target形式を確認しました。代表URP／HDRPのQuilt画像は直接目視しました。

Previewを明示Stopせず、機器Previewがactiveな状態で`EnterPlaymode`を要求しています。Play時にはPreview内部producerが1→0となり、Editor retentionとMetal retirementが0、runtime outputが30 frame完了しました。Stop後、**Playを出る前**に元profileとrenderer設定の復元を確認し、その後Editへ戻っています。Scene Reloadで復元を見せかける判定にはしていません。source sceneの実行前後hashも一致しています。

この4 Editor runではlive moduleのpath採取が間に合わず、実ロードdylib identityは未観測です。設定packageのdylib hash、現行Native APIを用いたpresentation／retirement、最新Core sourceと実行metadataは照合しています。Player4条件の実ロード確認をEditorの実ロード証明へ広げていません。機器出力をactiveのままEditorを閉じる試験は未実施です。

## 終了診断の比較

| 比較Player | thread警告 | Immediate bytes | 確認した境界 |
| --- | ---: | ---: | --- |
| Unity＋URPのみ／Development | 6 | 1,882,391 | ViewWeave packageなし、組み込みshimなし、取得成功したmodule snapshotとdyld traceにSDK／shimなし |
| Unity＋URPのみ／Release | 5 | recordなし | 同じ隔離構成、通常camera 60 frame、正常終了 |
| Quiltのみ／URP Development | 5 | 1,923,079 | SDKなし、66視点×60Quilt、camera 2→68→2、Quilt target解放 |
| Quiltのみ／HDRP Development | 5 | 2,084,165 | SDKなし、同じ視点数・Quilt数・camera baseline復帰 |
| SDK metadataのみ／Development | 5 | 2,474,636 | Quiltなし、display 1件取得、cleanupFinished、Close→End成功 |

全5比較はarm64／Metal Playerで、fresh runtime、要求mode、Development flag、正常終了code 0を確認しました。Quilt比較では実際のpipeline／shader tagとtarget形式も照合しています。Metadata比較の`pipeline=URP`は準備sceneのラベルであり、URP描画試験の成功とは数えません。module snapshotは取得終了codeと対象PIDをgate化し、取得失敗や空出力を非ロード証明にはしていません。Quilt比較にはshimが組み込まれていますが、取得成功したmodule snapshotとdyld traceではshim／vendor SDKともロード未観測です。組み込みファイルの存在と実ロードを分けています。

Unity-onlyの隔離projectは`11312b8`時点の設定・packageから作り、公開Package Manager APIでViewWeave 4 packageを除去しました。最新commitでもUnity／pipeline versionは同じで、ViewWeaveのコード変更から独立した比較です。初回比較も保存した上でmodule取得gateを強めて同じappを再実行しました。script／manifest／lock確認は[隔離記録](validation/macos-preview-shutdown-20261014/unity-only-isolation.json)を参照してください。

したがって、ViewWeaveの共通Quilt処理やSDK終了処理は、**このmacOS診断が発生するための必須原因ではありません**。一方、metadata比較にも追加のImmediate割当が記録されています。scene、package集合、処理量、実行時間が揃っていないため、差分をSDKまたは共通コードのリーク量とはみなせません。割当stackや時間推移を取得しておらず、追加残留の所有者は未特定です。Releaseにrecordがないこともリーク0の証拠にはなりません。

## Windowsとの照合と残る範囲

追加された[Windows終了ダンプ調査](windows-editor-close-crash-20261011.md)では、解析済み終了ダンプの`Unity!dup_nolock`書き込みアクセス違反が記録されています。停止後／HDRP D3D12 Preview／Playの4 controlled runは個別gateを通過しましたが、報告された事故原因の確定や一般的な終了保証には至っていません。現在の`-1`警告はmanaged image guardの拒否でも発生するため、それだけでNative workerの稼働継続とは判定できません。

[Native 19件のmock試験](windows-native-mock-shutdown-validation-20261012.md)はUnity外のfake／loopback等の限定試験です。vendor runtime、実機、実際のEditor crashの検証とは分けます。今回Windowsでの追加実行はありません。macOSのthread警告／Immediate診断とWindowsのCRTアクセス違反を対応付けるstack・thread・descriptor・allocation根拠はありません。

今回製品コードの変更は加えていません。検証fixtureのtarget記録漏れ、失敗保持時のRunner再有効化、control構成とmodule取得gateを修正しました。初期compile失敗、Play要求前のfixture判定失敗、旧Coreでの成功、target記録欠落runは最終結果へ混ぜず、構造化結果に除外理由を残しています。

新Coreに変更された個別graphics featureの旧結果は履歴へ保持し、今回未実行のLit／Shader Graph／VFX／TAA／Volume等を最新版の成功へ流用しません。[対応表](ja/unity-feature-platform-matrix.html)と[履歴](ja/unity-feature-platform-matrix-history.html)を同時更新しています。

## 後片付けとチェック

検証用のUnity設定・生成scene変更はpull後の342-file baselineへ戻しました。意図したfixture以外はhash一致、Editor／Playerの残存なしを確認しています。最新のQualitySettings変更も保持しています。raw log／appはgitignore対象の`.local/`に残し、共有証跡には結果、hash、fixtureと単純画像だけを含めています。

repository validationとURP/HDRP Distribution照合、diff whitespace、対応表の列数／anchor／相対リンクをチェックしています。実機光学品質、長時間、別profile／GPU、出力中Editor終了、Windows事故再現、割当の所有者と完全解放は未検証です。
