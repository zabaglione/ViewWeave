# macOS共通サブピクセル契約の再検証（2026-10-11）

現在の対応状況は[HTML対応表](ja/unity-feature-platform-matrix.html)、測定ソースと結果は[構造化記録](validation/macos-unified-subpixel-20261011.json)を参照してください。

## コード基準と変更

mainを`0b0531d`から`be3cbb7f6967c3090d5e446b01a8e9a3ff3b4920`へfast-forwardしました。新しい共通Native校正validator、`ViewWeaveSubpixelSampling.hlsl`、共通Quilt入力規則、Metal `open_v4`をmacOS上で検証しています。測定時は以下のNative／managed／fixture変更を含む未commit作業ツリーです。正式リリース保証ではありません。

- macOSセル情報テストへの共通校正コードのリンク漏れをCMakeで修正し、arm64 shimを再ビルド・配置しました。製品はmode引数付き`open_v4`を必須とし、180-byteのprofile v3を応答として受け取ります。
- Native metadata fixtureとGo取消fixtureをv4へ更新しました。Automatic／ClassicRgb／CalibratedCells／不正mode／短いbufferを検査します。
- Bridgeが返す余白付きQuiltをNative品質で使うと、raw寸法保持と新しいpadding拒否gateが食い違う回帰を修正しました。Windows／macOS共通helperがBridgeの完全tile使用領域から描画寸法を導きます。4096×4096 / 8×6は4096×4092、整列済み4092×4092 / 11×6は変更しません。元のBridge profile、grid、view数、aspect、viewcone、光学校正とPreview／Balancedの縮小規則を維持します。Coreのローカル用padding対応と入力gateは変更していません。
- 合成GPU fixtureを3入力形式（float UNORM、byte UNORM、byte sRGB）、3 sampling条件（等倍、aspect拡大による黒領域、aspect縮小＋zoom）、28 pattern条件へ広げました。CPU oracleは独立したpattern lookup、sRGB関数とbilinear計算を使い、全pixel・RGBを照合します。

機種名やOSから校正値を作る処理は追加していません。機器情報とcell配列はBridge APIから取得します。レンダリング寸法は取得したQuilt寸法とgridから導く値です。OS固有なのはtexture転送・window・graphics APIです。

## 環境と範囲

Apple M3 Max、arm64、macOS 26.6.2、Unity 6000.6.4f1、URP／HDRP 17.6.0、Metal、Bridge 2.6.3。接続したGoの校正は1440×2560、aspect 0.5625、pattern mode 0、cell count 0です。Goと合成データのみを対象にしました。16 Spatial Landscapeは接続していません。

## 通過した検証

- Release arm64 Native CTest **8/8**、失敗0。共通校正、Metal metadata v4、セルJSON／HTTP、API契約、submission lifetimeを含みます。Mac buildのローカルlinker環境に存在しないQt検索pathのwarningがありますが、build／CTestは正常終了しました。
- Mac EditMode **31/31**、skip 0。Metal Inspector／subpixel policy 16件と共通機器形状15件です。新しい入力規則4件はこの31件に含めません。
- Mac PlayMode **9/9**、skip 0。非既定grid受理、不正寸法・padding・partial/default拒否、Preview／Balanced／Native profile変換、Bridge元情報保持、整列済み／余白付きの3合成profileと4096×4096 / 8×6の受入を確認しました。Windows上の新実行ではありません。
- 合成Metal GPUはBuilt-in Linear、URP Linear、HDRP Linearが各**252 cases / 580,608 channel samples**、Built-in Gammaが**168 / 387,072**。合計**924 cases / 2,128,896 samples**、最大絶対誤差0.002278未満、許容0.006です。mode 0〜4、flip、RGB/BGR、セルなしinvView、非対称・負のセルXY、view／XY sampling、tile境界、拡縮・黒領域、Linearの実sRGB入力を検査しました。pixelをskipしていません。
- GammaではUnityがsRGB形式のTexture2D要求を実UNORMへ変えます。成立しなかった1入力形式の84ケースは通過件数に含めず、Gammaの実sRGB samplingは未検証とします。共通shader計算の検証であり、Gammaシーン／機器出力一般の保証ではありません。
- Goのv4 native取消／再開始 **3 cycle**。未発行frame cancel、busy drop、前generationの遅延event、close後の再開始、1440×2560取得を確認しました。
- Go URP／HDRPのEditor工程は各**1/1**。Local Preview pause/resume、Device Preview提示45以上、Play提示60以上、PNG／中央view保存、3枚録画、静止画の提示・保持と連番の再生pause/resume、全3 frame index、live復帰、停止、Play終了、pending resource／retirement 0を確認しました。稼働中のmode変更拒否、セル表なしCalibratedCellsの開始拒否、ClassicRgb再開始30以上も通過。PNGは赤／緑各1,000画素超、magenta 0です。
- GUIで両pipelineのGame View、Hierarchy、Inspectorを確認し、Missing Script／Referenceとpinkは見つかりませんでした。Consoleはwarning／error各0。停止後の紫のfocus枠はBridgeの9:16です。GUI描画と提示counterは人手のパネル光学評価を意味しません。

Goのarm64 / Metal PlayerはURP／HDRP × Development／Releaseの**4/4**でbuild成功、実pipeline／shader tag一致、初回提示60、ClassicRgb再開始30、PNG／中央view保存とPNG再読込、3枚録画と連番再読込、稼働中mode変更拒否、セル表なしCalibratedCells拒否、GPU retirement 0、自然終了code 0を確認しました。Player fixtureは画像の再読込までで、Player上の再生出力・Pause／Resumeは検査していません。全4 appと同梱shimがarm64で、shimはsource packageと同一SHA-256 `f2539810f3a63cc3998f0a9ef7b3b96a50cf29031ea986e67ca753787c5cca0f`、157,712 bytesです。

## 初回失敗の扱い

初回Native buildはセル情報テストへの共通validatorリンク漏れで失敗しました。v4追加試験の初回は期待したerror文の部分文字列が実装と異なり7/8で、表なしCalibratedCellsを製品が正しく拒否していました。fixture修正後の8/8だけを採用しています。

Gamma GPU初回2 runはsRGB入力が実際には作成できないことをassertして失敗し、完了reportを作成していません。前述の非成立形式を明示して除外した168件だけを採用しています。URP Editor初回は過去の無視対象生成fixtureとclass名が衝突し、compile failureで試験0件でした。生成copyのclass／menu名を分け、正常コンパイル後の結果だけを採用しています。Native品質helper追加前のURP成功runも保管し、最終URPはhelper追加後に再実行しました。

## 保存物と限界

製品ソース、native artifact／provenance、追加fixture、共有用の本report／構造化記録／HTMLは管理対象です。raw XML／JSON、画像、Player、ログ、SRPへ展開した一時fixtureは無視対象の`.local/macos-unified-20261011/`等に保存し、commitしません。共有記録ではrawファイルの位置・SHA-256と結果の範囲を示し、Bridge token／serial／raw responseを含めません。

Windowsでの今回のNative品質helper再実行、Legacy Direct実機、16 Spatial Landscape、特殊セル実機・光学、セルありinvView=0、未知pattern／4超cells、回転、hotplug、複数機器、Intel実行、長時間、window-close／強制終了、任意scene／全効果、性能は未検証です。以前のWindows native／device結果はその時点のコードと条件に限り、今回のMac結果をWindowsへ一般化しません。 この記録後のWindows共通品質helper評価は[Windows検証記録](windows-mac-quality-profile-validation-20261011.md)に分けて記載しています。
