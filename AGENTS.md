# Repository instructions

- Windows側のURP/HDRP機能を追加・更新・修正する作業、またはWindowsでの対応・検証結果を変更する作業では、その実装・検証を行う同じ作業内で `docs/ja/unity-feature-platform-matrix.html` のWindows該当列、status、条件、根拠リンク、最終照合日、検証コード基準を更新してください。その他のOSを含むUnity graphics、render pipeline、shader、graphics API、lifecycleの対応または検証結果を変更するときも、影響する列を同じ作業で更新してください。
- 実装の有無、設定保持、限定された検証成否を分け、OS/API/pipeline/Editor・Player/実機の範囲を保ってください。skip、0件、失敗または未通過gateを成功に数えず、未commit修正をrelease済みと記載しないでください。
- 根拠の優先順位、古い結果の扱い、HTML確認方法は [`docs/ja/support-matrix-maintenance.md`](docs/ja/support-matrix-maintenance.md) に従ってください。
