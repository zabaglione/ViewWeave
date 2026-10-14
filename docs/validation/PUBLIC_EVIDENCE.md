# Public evidence copies

[Redaction manifest](PUBLIC_REDACTIONS.json) · [Development records](../development/README.md)

The publication preparation dated 2026-10-14 replaces machine-specific repository, external-project, package, and user-home roots with stable labels. These copies are path-redacted representations, not byte-exact raw/as-run files. Labels identify distinct roots; they are not runnable paths and must be replaced for a local reproduction. System installation paths and intentional test/example paths can remain.

The recorded outcomes, failed/skipped/zero-test cases, timings, gates, OS/API/pipeline, and existing source/binary hashes are retained. Existing manifests' hashes and byte counts describe the original captured artifacts, not necessarily the redacted files beside them. Do not replace those historical values with hashes of a public copy or treat path cleanup as a new test run.

`PUBLIC_REDACTIONS.json` records each changed file's current repository-relative path, SHA-256 and byte count immediately before redaction, and SHA-256 and byte count of its public copy. Where available, `base` separately records the original repository-relative path, hash and size at commit `66930a2483fd929e2bd3ade61df96f9d653cf6c0`. Moved or edited prose may already differ from that base before redaction. For an extracted section, `derivedFrom` identifies its complete source document at the base commit; it does not assert that the split file is byte-identical to that source. Existing malformed captured text remains malformed and is explicitly identified; it is not repaired or counted as successful structured evidence.

To check an entry, hash the file at `path` and compare it with `publicSha256` and `publicBytes`. For original as-run identity, consult its existing run metadata and the base/original artifact. This manifest is a preparation snapshot; later evidence edits need a new recorded public hash rather than reuse of this snapshot's values.

This work changes the current tree only. Original local paths and older commit metadata remain in Git history. Repository visibility is kept private; a future visibility change would also expose that history and needs a separate review. No history rewrite, new runtime/hardware validation, or release is implied. The separately edited README photograph is presentation media, described in [its own provenance note](../development/readme-image.md).

## 日本語

2026年10月14日の公開準備では、個人のホーム・リポジトリ・外部projectなどのローカルパスを識別用ラベルへ置換しました。公開コピーは実行時ファイルと完全に同じbytesではありません。ラベルは元projectと隔離環境などを区別するためのもので、再実行時には自分のパスへ置き換えてください。

結果・件数・時刻・gate・既存hashは保持しています。従来manifest内のhashとbytesは元の実行時artifactの値です。公開コピーの照合には、別一覧の`publicSha256`と`publicBytes`を使ってください。`base`、置換直前、公開コピーの値を分け、失敗・skip・0件・不正な保存テキストを成功へ変更していません。

整理は現行ツリーに限られ、元のローカルパスや過去のcommit metadataはGit履歴に残ります。非公開設定は維持します。将来の公開設定変更時には履歴も公開対象となるため別途確認が必要です。READMEの編集写真は検証証跡に含めません。
