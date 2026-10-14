[English](../bridge-platform-architecture.md)

# Bridgeのプラットフォーム分離

> 対象読者: Unity／描画基盤の技術者。このページは本文記載の技術的な設計・実装・手順を扱います。利用者向けの導入・運用手順は[ユーザーガイド](../../ja/user-guide.md)、技術文書一覧は[技術者向けガイド](../../ja/technical-guide.md)を参照してください。

表示情報と共通の操作手順を、プラットフォーム固有の転送・描画処理から分離しています。既存のnamespace、asmdef、型名、スクリプトのGUIDは維持し、既存シーンの参照を保持します。

| 責務 | Bridgeパッケージ内のC#配置 | ネイティブ配置 |
|---|---|---|
| 表示プロファイル、固定幅の表示情報ABI、探索の優先順位 | `Runtime/Common` | `Native/common` |
| macOS探索、Metal転送・表示、GPUリソースの終了処理 | `Runtime/Platforms/Metal`、`Editor/Platforms/Metal` | `Native/platform/macos` |
| Windows探索、DX11/DX12 Worker、従来のDX11出力、再生・プレビュー・回復 | `Runtime/Platforms/Windows`、`Editor/Platforms/Windows` | `Native/platform/windows` |
| Rig作成、Inspector・編集・再生UI、出力制御、表示設定、Editor用producer、Quilt資源の保持、再生の互換性検証 | `Editor/Common` | CMakeでソースを選択 |

ユーザー向け設定の入口は、すべてのOSで`ViewWeave Rig`にまとめます。カメラ、フォーカス、フレーミング、描画、Capture、描画パイプライン設定は共通UIに置きます。Quality、接続設定、開始・停止、状態表示も同じUIから操作し、接続されたバックエンドが実装と操作可否を提供します。別コンポーネントは転送処理、描画アダプター、独立した作業画面が必要なツールに使います。

InspectorとRig Authoringウィンドウは共通の`ViewWeaveRigInspectorUI`を使用します。共通部分が編集操作を持ち、WindowsとMetalのpartialが出力固有の操作を担当します。`BridgeEditorDisplaySettings`が表示プロファイルの取得先を一本化し、現在の出力へ振り分けます。asmdefと既存GUIDは維持します。UIの振り分けは接続されたコンポーネントを基準にし、EditorのOSだけを理由に設定を隠しません。シーンをOS間で移動しても、保存済みのバックエンドは自動交換されません。

MetalのQualityと接続設定は、出力を停止しGPU資源の回収が終わってから変更します。Windowsの実行中Quality変更は維持します。Metalでも実行中のフレーミング、視点の間引き、Auto Renderの停止・再開を操作できます。録画中は共通の所有権で設定を保護します。Metal出力の停止時には、そのRendererの録画を終了してからローカル設定を復元します。MetalとWindowsの両方で、共通Rig UIからEdit Mode PreviewとPlay中のPNG／連番再生を操作できます。Metalは専用Editor producer、Quiltの非同期GPU回収、native表示資源の回収を組み合わせ、Windowsは既存Worker／fence／reaperの手順を維持します。Metalの出力停止では、再生leaseを同期的に解除してから元のRig設定を復元します。再生textureは別途保持し、GPU完了後に解放します。通常描画への復帰は、正常停止かつ所有権と設定が維持された場合に限ります。

`BridgeRuntimeLocator`は「明示パス → 環境変数 → 自動探索」の優先順位を共通化しています。EditorではホストOS、Playerでは対象OSで処理を選びます。DLLの検証とレジストリ探索はWindows側、dylibとアプリの探索はmacOS側にあります。

共通のネイティブ処理は、関数ポインタを受け取り、SDKの表示情報をViewWeaveの固定幅構造体へ変換します。SDKのロード、文字列形式、排他、初期化・終了、失敗時の保持は各プラットフォームが担当します。SDKの`long`と`unsigned long`はOSごとに幅が異なるため、公開ABIとは分けています。既存WorkerのD3D向け画像検証条件は維持しています。

CMakeは共通ソースと、使用OSの実装だけをコンパイルします。ルートの`bridge_shim.cpp`と`bridge_worker_transport.cpp`は、従来のソースinclude型テスト用の小さな振り分けです。macOSに残すD3Dの互換exportは`platform/unsupported`に配置し、未対応を返します。

C#は責務ごとのフォルダに分け、従来のアセンブリ構成を維持しています。シーン参照や未対応環境でのエラー表示のため、コンポーネントの型自体は保持しています。Windows WorkerのEditor回復処理はWindowsだけで登録し、Metalは専用の終了記録とReaperを使用します。GPUの寿命管理や終了手順は各バックエンドが担当します。

同梱Windows DLLとprovenanceは従来の検証済み成果物を保持しています。今回のソース整理後にWindows版を再ビルド・実機検証したことを意味しません。現在の検証範囲は[macOS検証記録](../macos-metal-validation-20261010.md)を参照してください。
