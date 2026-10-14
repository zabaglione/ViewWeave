# 最新mainのmacOS影響確認（2026-10-14）

本記録は`11312b8`時点の結果です。後続のpull（`c4de573`）によるCore変更後の結果は[最新版の再評価](macos-preview-shutdown-validation-20261014.md)を参照してください。

`main`を`a3fcd1d6408eea786acc91fd2e3140e0f17e18ec`から`11312b8c6cf824d9a7ac7c8940e95cee742fa076`へfast-forwardし、追加4 commitを確認しました。正確な旧revision、実行環境、source／artifact／試験ログのSHA-256は[構造化結果](../validation/macos-latest-intake-20261014.json)に保存しています。製品コードの追加修正は行っていません。

後続の[現行dylib機器Player検証](macos-current-player-validation-20261014.md)では、URP／HDRP × Development／Release 4/4、GPU retirement 0、自然終了code 0を確認しました。以下の「今回実行した検証」は先行取込調査の範囲で、後続のPlayer結果と区別します。

## 更新による影響

- 製品変更はWindows Workerの転送時に追加していたY反転の除去とWindows DLLの更新です。`CopyQuiltToTransferTexture`は通常の`Graphics.Blit`を使います。macOSの`BridgeMetalQuiltOutput`、Metal interlace shader、Native Metal presenter、共通Core／URP／HDRPの製品sourceは今回の差分にありません。
- macOSはinterlace shaderから専用Metal windowへ送る別経路です。WindowsのWARP／D3D producer結果を理由にMetalのUVを変更する根拠はありません。既存のMetal描画・提示処理に同じ修正が必要な問題は、今回のsource確認では見つかりませんでした。
- Native CMakeの追加oracle targetは`WIN32 AND BUILD_TESTING`内です。共通metadata fixtureへの追加失敗caseはmacOSでも実行しました。
- 新しいQuilt orientation fixtureは`UNITY_INCLUDE_TESTS && UNITY_EDITOR_WIN`で限定され、Windows用runnerもD3D11／D3D12を指定します。Metalの向きの検証には使えません。共通Editor fixtureの変更については、この作業ではUnityを再実行していません。

## 今回実行した検証

```sh
python3 tools/build_macos_plugin.py --build-dir Native/build-macos-intake-20261014 --no-install
python3 tools/validate_repository.py --release
python3 tools/build_upm_distribution.py --check
git diff --check
```

- macOS arm64 ReleaseのNative build成功、CTest **10/10 PASS、fail 0、skip 0**。metadata workerの取消／所有／shutdown failure、校正、セル情報、Metal API契約とsubmission lifetimeを含むNative fixtureです。実Bridge SDK、Unity Editor／Player、接続機器の描画は実行していません。
- 静的repository検査はerror 0、warning 2。warningはpublic identity／device matrixとrights／Unity-render確認のmanual reviewを求める既存release gateです。配布版URP／HDRPの生成物照合とdiff checkも通過しました。この結果をrelease承認やUnity動作成功とは扱いません。
- 本体packageとURP／HDRP DistributionのmacOS shimはすべて160,016 bytes、SHA-256 `c97856ea3bb910a1456eb044347a18ac8117c140ddd149f8e9ec240600766c5b`でprovenanceと一致し、更新前から不変です。今回buildした検証用dylibはpackageへ配置していません。そのbuild成功を同梱binaryのUnity実行成功に読み替えません。
- linkerはローカルの`/opt/homebrew/opt/qt@5/lib`がないwarningを出しました。build／CTestは成功しており、警告ゼロとは記載しません。

## macOSで優先して追加確認する内容

1. **現行shimでの機器出力Player**（後続検証で4/4確認、上記report参照）。URP／HDRP × Development／Releaseを、出力・停止・再開始・pending 0・自然終了まで再確認します。[旧Player 4/4](macos-unified-subpixel-validation-20261011.md)は別shim `f2539810...`の結果です。現在の`c97856ea...`では[起動時取得の記録](bridge-authoring-discovery-validation-20261011.md)にPlayer実行がありません。
2. **URPのEdit Mode機器PreviewとPreviewからPlayへの遷移**。共通profile修正後の[HDRP SampleSceneのPreview](bridge-editor-preview-profile-validation-20261011.md)は限定確認済みですが、URP機器Previewと両pipelineの遷移・pause/resumeは同記録の対象外です。今回変わった共通Editor fixtureも、Metal EditModeで再実行して現行sourceの結果を残す対象です。
3. **Metalの向きの独立検証**。今回のWindows専用fixtureとWARP結果を流用せず、非対称なview／上下markerを使って実Metal producer、interlace、Native presenterを通す試験が候補です。合成shaderの[924 cases](macos-unified-subpixel-validation-20261011.md)と、Native window／実パネルの物理方向・光学確認は分けます。現時点でMetalの上下反転不具合を確認したという意味ではありません。
4. **Windowsで検証が進んだ機能のMetal確認**。GRD、一般的なURP Renderer Feature／HDRP Custom Pass、HDRP TAA・SSR・SSGI等の画素効果は対応表上の未検証／設定保持の境界を維持します。Windowsの新しいPlayer成功をMacの成功に読み替えません。機能の実装追加が必要かは、Metalで実行してから判断します。

hotplug、複数機器、Intel、長時間運用、一般的な光学品質は従来からの未確認事項です。今回の更新で発生した回帰とは区別します。

## 管理範囲

本report、構造化結果、[対応表](../ja/unity-feature-platform-matrix.html)をローカル更新しました。commit／push／releaseは行っていません。Nativeのbuild生成物と`Testing/Temporary/LastTest.log`はignoredの`Native/build-macos-intake-20261014/`にあります。今回の結果はNative build／fixtureと静的確認の範囲に限定します。
