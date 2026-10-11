# macOS Bridgeセル配列対応の検証（2026-10-11）

現在の対応表は[Unity機能×環境のHTML](ja/unity-feature-platform-matrix.html)、機械可読の結果とコード識別は[構造化記録](validation/macos-metal-subpixel-20261011.json)を参照してください。

## 実装と対応条件

基準commitは`b3cae92a5df06c40f0ac4bad372c3b197186048d`です。この基準にnative V3 ABI、Bridge HTTP JSON取得、managedセル転送、独自interlace shader、検証fixtureを変更した作業ツリーで測定しました。構造化記録のSHA-256が測定した製品コードとartifactを識別します。Native／GPU計算試験後にremote main `9d0309c`の共通Editor UIとWindows fixtureを統合しました。Native／shaderの測定コードを保持し、後続の共通Inspector設定を含むmanaged更新後にGo工程を再実行しました。各測定対象は構造化記録のhashで識別します。正式リリースや全機種の光学保証ではありません。

機種名・serialの許可リストを使わず、Bridgeが返すdisplay寸法、Quilt grid、校正、RGB/BGR順序、セル情報から出力を構成します。

- `lkg_metal_display_open_v3`のみを使います。構造体180 bytes、セル24 bytes、最大4セルです。未公開の旧ABIを維持する互換コードは除きました。
- セルなしmode 0は従来のRGB/BGR計算で、flipX／invViewは0または1です。
- セルありmode 0〜4はinvView=1が必要です。mode 0は1以上、mode 1／2／4は2以上、mode 3は4セルが必要で、どのmodeも最大4セルです。
- RGBそれぞれのraw XY pixel offsetをdisplay幅／高さで正規化し、対応するサブピクセル位相へ加えます。セル選択は反転していないdisplay UVのnative pixel位置で行います。セル経路のflipXはtiltを反転し、UVやRGB offset Xは鏡像化しません。
- 未知mode、足りないセル、4を超えるセル、セルありinvView=0、欠落・非数・非有限値、float計算途中のoverflow、SDKとJSONの不一致は開始時に拒否します。
- Apple Silicon、macOS 26以降、Metal、拡張画面、native解像度、回転なしが必要です。fringeは有限値だけ検査し、補正には使いません。

SDKのC関数にはcaller側セル配列capacity契約がないため、cells引数をnullptrにして共通値・mode・countだけを取得します。セルありの場合だけ、Bridgeの固定loopback `127.0.0.1:33334`へPUT enter_orchestration → available_output_devices → exit_orchestrationを行います。JSONから配列を読み、native display index、座標、寸法、光学値、mode、countを照合します。校正のstring／object形式とCellPatternMode／cellPatternModeの両表記を扱います。最大1 MiB、redirectなし、短いtimeoutを設け、取得・照合・終了のどれかが失敗したら出力を開始しません。セルなしprofileにはHTTP追加依存がありません。

## 環境と検証範囲

Apple M3 Max、arm64、macOS 26.6.2、Unity 6000.6.4f1、URP／HDRP 17.6.0、Metal、Bridge 2.6.3で検証しました。接続実機はLooking Glass Goで、取得したprofileはmode 0／セル数0、1440×2560です。GoではセルありHTTP取得や特殊配列のパネル光学を試せません。ユーザー指定により、16 Spatial Landscapeの実機接続は今回は行っていません。

## 結果

- Release arm64 native buildとCTestは7/7 PASS。5種類のportrait／landscape／8K合成profile、mode 0〜4、不正校正、V3短buffer canary、metadata列挙、submission lifetimeを検査しました。
- JSON parserはmode／string-object／flipの20組合せ、mode alias、不一致、重複index、不足・過大配列、欠落・boolean offset、変更前セルの保持を検査しました。loopback HTTPサーバーの5工程シナリオは正常、取得失敗、不正JSON、1 MiB超過、終了失敗を含み、3要求の順序、body、token再利用、終了要求を確認しました。終了失敗時も関数は失敗を返し、呼び出し元はopenを中止します。
- Metal GPUと独立CPU期待値の比較は各run 28ケース、64,512 channel値、全pixel／RGBを照合。8つのセルなし条件（flip×RGB/BGR×invView）と20のセル条件（mode×flip×RGB/BGR）を含みます。セルごとに非対称・負のXY値を与え、view選択とXY samplingを色で識別しました。pixelをskipせず、phase境界marginを確保しました。
- Built-in Linearは最大絶対誤差0.001956135、Gammaは0.001960814、許容0.006でPASS。URP／HDRPのLinear環境でも同じ28ケースが各PASSです。GPU計算の検証であり、Gamma scene一般や特殊配列実機の光学検証ではありません。
- GoでV3 native sessionの3キャンセルcycleがPASS。未発行frameのcancel、busy drop、前generationの遅延event、終了後の再開始を確認しました。

GoでのEditor／Player工程は、URP／HDRPのEdit Mode local/device Preview、Play、保存・3枚録画・再生、停止と再開始を対象にします。サブピクセル設定はAutomatic開始、稼働中の変更拒否、表なし機器でCalibratedCells開始拒否、ClassicRgb再開始と画像gateを確認しました。両Editorは各1/1、arm64／MetalのDevelopment／Release PlayerはURP／HDRPの計4/4でPASSでした。各Playerは実際のpipeline／shader tag、赤・緑それぞれ1,000画素超、magenta 0、提示60・再開始30フレーム、3枚録画、設定保持・拒否、pending 0、自然終了code 0を確認しました。両EditorのGUIではGame View、Hierarchy、Inspector、Consoleの警告・error 0を確認しました。具体的な値は構造化記録に記載します。提示完了counterはGPU完了の証拠で、パネル上の立体感・向き・光学alignmentを人間が判定した証拠ではありません。

## 初回の失敗と検証gate

初回URP GPU runは旧一時fixtureとのclass名衝突でcompile failure、試験0件でした。名前を分けて再実行した結果だけをGPU PASSに数えています。

初回URP Development Playerは提示counter・終了条件を通過した一方、画像はred 0／green 0／magenta 247,052でした。distinct color数だけを条件とした旧fixtureのPASSは描画成功として採用しません。BuildログではfixtureのURP Unlitに対しRenderPipeline tag不一致による全SubShader削除を確認しました。切替直後の準備状態が原因という判断は推定です。赤・緑の各画素数1,000超とmagenta 0のgateをrunnerへ追加し、準備したpipelineを別Editor processで読み直すbuild手順で再検証した4/4のPlayer結果だけを採用します。初回raw結果は無視対象のinitial-device-validationへ保持します。

Build前には保存したpipeline asset、Standaloneで設定した各quality asset、実際のpipeline instance、`Shader.globalRenderPipeline`をpublic APIで照合します。Playerでも実際のpipeline typeとtagを記録します。[UnityのShader tag仕様](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Shader-globalRenderPipeline.html)と[対象platformのquality asset API](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/QualitySettings.GetAllRenderPipelineAssetsForPlatform.html)に基づくguardです。

Inspector／共通policyのEdit Mode試験は15/15、Windows向け設定UIの選択・serialized保持はMac上で1/1、手動指定のSession開始拒否はMac上のPlay Modeで1/1です。Windows上での実行やnative機器出力を示しません。初回Windows UI試験は未接続panelでChangeEventが届かず1件失敗し、実際のInspector windowへ接続するfixtureに直した結果だけを採用しています。再実行先に前回PNGが残ったURP Editor工程も上書き防止で失敗し、別の新規保存先で通過しました。一時fixtureの重複menu警告も除きました。

## 一次資料と実装由来

[公式Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin) 3.2.8のLenticular.shader（114–138行）、MultiViewRendering.cs（979、1014行）、SubpixelCell.cs（27行）でmode 0〜4の数学的規則、flip処理、pixel単位を照合しました。取得unitypackage SHA-256は`0c0628d6d8ea2fbfe54006392abcdfd5702d61f2bc5ab0e7329696674a8c3f31`。vendor shaderコードのコピー・翻訳・移植・同梱は行っていません。独自ライセンスのSDKをOSS扱いしません。

HTTP lifecycleは[公式Bridge.js固定版](https://github.com/Looking-Glass/Bridge.js/tree/f09f7f7510b09bb502d602c26907c958ed77075f)、セルmode定義とpixel単位は[公式Toolkit固定版](https://github.com/Looking-Glass/LKG-Toolkit/blob/0b6209bfa83c8eda5dd862666d4af83bd9e1ec67/Toolkit-API/Device/Calibration.cs#L93-L107)を照合しました。Toolkitの「第三世代前はmode 0」という仮定から、特殊配列が旧機種専用とは判断できません。機種→modeの対応表は確認できていません。詳細は[校正資料](bridge-subpixel-calibration.md)へまとめています。

## 保存物と未確認

共有用には本report、構造化結果、製品ソース・fixture・native provenanceをcommitします。raw response、session token、serialを含みうるBridge情報、公式SDK package、Player、画像、詳細ログ、一時GPU fixtureは無視対象`.local/metal-subpixel-20261011/`等へ保存し、commitしません。構造化記録はrawの保存場所とSHA-256を示します。

特殊配列機器の実機／光学、16 Spatial Landscape、セルありinvView=0、4超のセル、未知mode、回転、hotplug、複数機器、Intel runtime、長時間、Player window-close／強制終了、任意scene／全効果は未検証です。

## Inspectorの共通設定

共通`BridgeSubpixelMode`のAutomatic／ClassicRgb／CalibratedCellsをRig Inspectorから選びます。Metalでのみ手動補正を適用し、Windowsは同じ設定を保持しますが、現在のBridge管理出力ではAutomatic以外を開始前に拒否します。Windows上の新設定の実行・特殊配列機器の光学は未検証です。セルpattern modeとoffsetはBridgeが提供する校正に従います。
