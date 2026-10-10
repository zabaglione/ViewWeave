# Unity対応表の保守

[現在の対応状況](unity-feature-platform-matrix.html)は、Windows / macOSとURP / HDRPのUnity機能をまとめる単一のHTMLです。HTMLを直接更新し、別の表データや生成器は作りません。技術者向けの詳細は本ページではなく、リンク先の試験記録に記載します。

## 証拠の優先順位

1. 対象runのXML/structured resultと、Unity version、source revision、graphics API、pipeline、GPU、Editor/Player、device opt-in、native hashなどの実行metadataを確認します。
2. それらを説明する日付付き検証レポートとログを照合します。結果の範囲や失敗、skip、cleanup gateが読めることを確認します。
3. sourceを調べ、実装の有無、明示拒否、設定保持、resource/lifecycle動作を区別します。コードの存在だけで描画成功や画質を推定しません。

raw XML/structured recordを最も具体的な実行証拠とし、日付付きレポートを人が読める説明として使います。失敗、skip、test 0件、自然終了やdevice cleanupなどの未通過gateをPASSに数えません。集計値だけでfixtureの対象や条件を広げません。

## 状態と検証範囲

- 「実装あり」はその組み合わせでの実行成功を示しません。
- 「設定保持」は設定値やfeature enable stateの維持だけを示し、画素差・視点ごとの履歴・画質を証明しません。
- 「限定確認」は記録されたfixtureと条件に限ります。対象OS、D3D11/D3D12/Metal、URP/HDRP、Editor/Player、Bridge device output、光学確認の各軸を分けて記載します。
- macOSの結果はmacOS上のrunとして記載します。Windowsで実行したという表現や、Windowsへ結果を一般化する記述を避けます。新しいmacOS Metal記録がpushされたら、同じHTMLのmacOS列、条件、照合日、根拠リンクを更新します。
- 22件のWindows選択suiteなど、subsetの合格を全suite合格と扱いません。Player、実機、optical、未実行featureは別セル・別条件のままにします。

## Revisionと古い結果

コードのcommitと文書のcheckout revisionを分けて記載します。作業ツリー修正を使ったrunには、基準commit、未commit修正の有無、対象範囲を明示し、release済み・push済みの対応と誤認させません。パッケージDLLの現在状態は必要に応じてSHA-256とサイズで示し、provenanceに記録された旧artifactを現在のbinary identityに流用しません。

関連コードが変わった場合、古い証拠を新しい実装に自動適用しません。どの行・条件が影響を受けるかを確認し、新証拠がなければセルを「実装あり」または「未検証」に保ちます。対象機能・根拠・判定に変化がなければ、日付だけを更新しません。

## HTML更新と検証

表はWindows URP、Windows HDRP、macOS URP、macOS HDRPの4列を維持します。CSSのstatus classとlegendを合わせ、変更した各status行の条件文・根拠リンクも更新します。冒頭の要約、検証件数、対応範囲の要点、source一覧、最終照合日とコード基準も同じ証拠に合わせます。特定のOSやAPIで未確認の事項を総論で「未検証」と言い切らない一方、検証したfixtureより広い主張にしません。

Markdown内に一時的なlocal pathだけのリンクを置かず、共有可能な日付付きreportへリンクします。reportは可能ならraw resultの保存場所と対象条件を示します。Status表示とsource anchorは日本語ページ上で読みやすく保ちます。

編集後は次を実行します。

```powershell
python tools/validate_repository.py
git diff --check
```

加えてPython `html.parser.HTMLParser`等の小さな読み取り専用checkで、全table行の列数（group rowを除いて6列）、tableの閉じタグ、ページ内anchorと相対リンク先を確認します。追加の恒久validation toolやdata fileは、繰り返し必要になり別途合意された場合にだけ検討します。HTMLやcheckのプログラム出力はASCIIにし、日本語はページ本文に使います。

定期・毎時の照合は行いません。Windows側のURP/HDRP機能を追加・更新・修正する作業、またはWindowsの対応・検証結果を変える作業の中で、該当するHTML列と根拠を更新します。新しいpushやmacOS記録は、それを取り込む作業で影響する部分だけ反映します。バックグラウンドpollやUnityの定期実行は行いません。
