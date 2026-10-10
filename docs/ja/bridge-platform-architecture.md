[English](../bridge-platform-architecture.md)

# Bridgeのプラットフォーム分離

表示情報と共通の操作手順を、プラットフォーム固有の転送・描画処理から分離しています。既存のnamespace、asmdef、型名、スクリプトのGUIDは維持し、既存シーンの参照を保持します。

| 責務 | Bridgeパッケージ内のC#配置 | ネイティブ配置 |
|---|---|---|
| 表示プロファイル、固定幅の表示情報ABI、探索の優先順位 | `Runtime/Common` | `Native/common` |
| macOS探索、Metal転送・表示、GPUリソースの終了処理 | `Runtime/Platforms/Metal`、`Editor/Platforms/Metal` | `Native/platform/macos` |
| Windows探索、DX11/DX12 Worker、従来のDX11出力、再生・プレビュー・回復 | `Runtime/Platforms/Windows`、`Editor/Platforms/Windows` | `Native/platform/windows` |
| Rig作成と使用するバックエンドの選択 | `Editor/Common` | CMakeでソースを選択 |

`BridgeRuntimeLocator`は「明示パス → 環境変数 → 自動探索」の優先順位を共通化しています。EditorではホストOS、Playerでは対象OSで処理を選びます。DLLの検証とレジストリ探索はWindows側、dylibとアプリの探索はmacOS側にあります。

共通のネイティブ処理は、関数ポインタを受け取り、SDKの表示情報をViewWeaveの固定幅構造体へ変換します。SDKのロード、文字列形式、排他、初期化・終了、失敗時の保持は各プラットフォームが担当します。SDKの`long`と`unsigned long`はOSごとに幅が異なるため、公開ABIとは分けています。既存WorkerのD3D向け画像検証条件は維持しています。

CMakeは共通ソースと、使用OSの実装だけをコンパイルします。ルートの`bridge_shim.cpp`と`bridge_worker_transport.cpp`は、従来のソースinclude型テスト用の小さな振り分けです。macOSに残すD3Dの互換exportは`platform/unsupported`に配置し、未対応を返します。

C#は責務ごとのフォルダに分け、従来のアセンブリ構成を維持しています。シーン参照や未対応環境でのエラー表示のため、コンポーネントの型自体は保持しています。Windows WorkerのEditor回復処理はWindowsだけで登録し、Metalは専用の終了記録とReaperを使用します。GPUの寿命管理や終了手順は各バックエンドが担当します。

同梱Windows DLLとprovenanceは従来の検証済み成果物を保持しています。今回のソース整理後にWindows版を再ビルド・実機検証したことを意味しません。現在の検証範囲は[macOS検証記録](../macos-metal-validation-20261010.md)を参照してください。
