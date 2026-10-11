# CIのWindows FXC探索修正（2026-10-11）

[HTML対応表](ja/unity-feature-platform-matrix.html)のNative検証条件を補足するCI記録です。測定metadataとログhashは[構造化記録](validation/ci-fxc-20261011.json)にまとめます。

## 失敗と原因

mainの`99cdeea7120e36e89fe6341f61ae3e1979f25e7b`に対する[run 38118840186](https://github.com/zabaglione/ViewWeave/actions/runs/38118840186)は5 job中4 jobが成功し、`native-transport`がCMake configureで失敗しました。直前の`be3cbb7`でも同じtransport configure失敗がありました。

errorは`FXC was not found for the manual subpixel presenter`です。transport-only構成ではmanual presenterのtest targetがstable inputより先に構成されます。manual側のFXC探索はPATHとWindows SDK環境変数のみを使い、stable側にあったWindows Kits registryからのSDK探索を使っていませんでした。通常のWindows full buildはstable側が先に見つけたcacheを共有できたため、19/19 CTestが通過していました。SDK未導入とは断定しません。

直前`be3cbb7`のmacOS build失敗は別件の共通校正validatorリンク漏れです。前回の`99cdeea`で修正済みで、そのrunではmacOS Native 8件が通過しています。transport configure失敗ではbuildとCTestを実行していないため、成功件数に含めません。

## 修正と確認範囲

`4fe83a26a4a49f4a4418d3c9fd242375fc8bbbe9`で`Native/stable_sdk_input_d3d11.cmake`の探索を`lkg_find_fxc`へまとめ、stable inputとmanual presenter両方から呼びます。明示FXC pathの検査、PATH、SDK環境変数、Windows Kits registryとインストール済みSDK directory探索を共通で使い、target構成順への依存を解消しました。探索はWindowsのbuild設定で、shader本体・校正・Quilt描画・同梱Native artifactの変更はありません。

[修正後run 38119458293](https://github.com/zabaglione/ViewWeave/actions/runs/38119458293)はcommit `4fe83a2`に対して**5/5 job成功**です。

- `static`（ubuntu-latest）: validator unit tests 5/5、repository validationとUPM distribution checkが通過。
- `native-portable`（ubuntu-latest）: Native CTest **5/5**。
- `native-transport`（windows-2022）: configure／buildを通過し、Native CTest **15/15**。
- `native-windows-fake-regressions`（windows-2022）: Native CTest **19/19**。
- `native-macos-metal`（macos-14）: Native CTest **8/8**とartifact保存を通過。

いずれもjob終了success、CTest実行結果の失敗・reported skipは0です。Windowsの15件と19件は重なるsuiteで、34の独立scenarioではありません。CIの再build binaryを同梱C864／f253 artifactと同一hashと扱っていません。ActionsのNode.js 20非推奨annotationは残りますが、このrunの実行結果は成功です。

GitHub-hosted CIのNative compileとCTestの結果です。Windowsのfake runtime／WARPを使う契約試験、macOS／LinuxのNative試験であり、Unity Editor／Player、URP／HDRP描画、Bridge実機、光学品質の追加証拠ではありません。macOS runnerのbuild artifactを同梱arm64 shimへ置き換えていません。Goと合成GPUの実行範囲は[macOS検証](macos-unified-subpixel-validation-20261011.md)を参照してください。

ローカルではrepository validatorのunit tests 5/5、repository validation、UPM distribution check、diff whitespaceを確認しました。unit test出力のERROR行は不正metadataを拒否するnegative fixtureで、suite全体は終了code 0です。raw CI jobログ／metadataは無視対象の`.local/ci-fxc-20261011/`等に保存し、commitしません。共有記録ではrun／job URL、結果、試験名とraw hashを保持します。
