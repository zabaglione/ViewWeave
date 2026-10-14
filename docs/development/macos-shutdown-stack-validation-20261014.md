# macOS終了thread・残留割当stackの切り分け（2026-10-14）

終了警告の発行stackと対象threadを特定しました。Unityのみ、Quiltのみ、metadataのみ、URP／HDRP機器出力の5条件すべてで、警告はUnityのPreloadManager 1 threadとjob worker 4 threadの終了時に、MonoのTLS破棄処理から出ていました。5条件とも診断fixtureが通過し、自然終了code 0を観測しました。

OSが終了時に検出した残留malloc割当の生成stackも取得しました。Monoのportable PDB読み込み、AppleのFoundation／音声初期化、別のsystem allocator条件ではUnityのthread破棄処理などが含まれます。**Unityの`MemoryLeaks / Immediate`全量の所有者は未特定です。ViewWeaveの不正な割当・解放ペアは確認できておらず、製品コードへ推測による修正は加えていません。** Windows終了クラッシュとの因果関係も未確定です。

[構造化結果](../validation/macos-shutdown-stacks-20261014.json)、[thread／割当stack](../validation/macos-shutdown-stacks-20261014)、[共有manifest](../validation/macos-shutdown-stacks-20261014/MANIFEST.json)に根拠を保存しました。これは独自fixtureの計測で、NUnit全suiteの成功件数ではありません。検証時は前段のfixtureを未commitの状態で使用しました。この記録はその実行状態を保存しています。

## 基準と計測方法

`c4de573c4435f0105e62f6125f8d4fedd78786b3`、Unity 6000.6.4f1、URP／HDRP 17.6.0、macOS 26.6.2、Apple M3 Max、arm64／Metal、Bridge 2.6.3、Go単機器です。[前段のPlayer／Preview検証](macos-preview-shutdown-validation-20261014.md)で作ったappを使い、今回C# fixtureと製品sourceの変更・再buildはしていません。Unity-onlyにはViewWeave package／shimを含めず、Quilt-onlyではSDKを呼びません。機器出力は一度に1クライアントです。

LLDBは公開symbolの`PlayerCleanup`／`CleanupEngine`で終了前thread一覧を取得し、`MemoryManager::ThreadCleanup`と終了期だけのlibc log書き込みを観測しました。breakpointは各1 locationへ解決され、警告と報告の実hitを記録しています。ARM64のlibc引数、thread stack、module情報を読むだけで、target内の関数呼び出し、expression、private C++構造体の解析はしていません。Unity installation、Library、decompiled assemblyも読んでいません。

後半のmetadata／HDRPはSDK初期化後にlate attachしています。metadataはfresh runtimeのcleanup gate通過、HDRPは初回出力後のfresh current PNG生成を接続条件にしました。起動stackの検証には数えず、その後のruntimeと終了codeを別に確認しています。

全診断Playerへ`-force-metal -timestamps -native-leak-detection EnabledWithStackTrace -log-memory-performance-stats`を指定しました。指定だけで全allocatorのstack loggingが有効になったとは判定していません。`MemoryManager::EndLeakGuardAndReportSummary`はsymbolを解決しましたがhit 0でした。取得できたImmediate stackは実際のlog書き込みからの**報告場所**で、残留割当の生成場所ではありません。

## 警告対象threadと発行stack

| 条件 | PID | 警告thread | Immediate bytes | 自然終了 |
| --- | ---: | --- | ---: | ---: |
| Unity＋URPのみ | 52790 | Preload 1／job worker 4 | 1,882,440 | 0 |
| URP機器出力 | 57466 | 同上 | 2,534,469 | 0 |
| Quiltのみ／URP | 61900 | 同上 | 1,923,800 | 0 |
| metadataのみ／late attach | 63859 | 同上 | 2,471,848 | 0 |
| HDRP機器出力／late attach | 64228 | 同上 | 2,693,954 | 0 |

各条件の5警告を2つのIO hookで計10回観測しています。thread IDと警告本文で重複排除して5件としました。ImmediateもIOで3〜4回見えますが、PID／time／phaseで照合すると各run 1 recordです。前段の通常runとbytesが少し異なるため、同じrunの値として混ぜていません。

全25警告で、同じnative thread IDについて終了前stackと先行する`MemoryManager::ThreadCleanup`を照合できました。終了前の役割は`PreloadManager::Run`、または`lane_guts → worker_thread_routine`です。thread名の多くは汎用名なので、名前だけで役割を推定していません。警告本文の`Thread 0x...`と数値のnative thread IDも別々に保存しています。

警告時の呼び出し順は、次のように共通でした。

```text
_pthread_start
  -> _pthread_exit -> _pthread_tsd_cleanup
  -> thread_info_key_dtor -> unregister_thread
  -> mono_hazard_pointer_get
  -> monoeg_g_log -> mono_log_write_logfile
  -> fflush -> __write_nocancel
```

Mono部分は`libmonobdwgc-2.0.dylib`です。組み込みbinaryのSHA-256は`198c12d2b1a2d00e9c4a7ffc40760d0d1eb880bf1290d647406f48799a16f988`、UUIDは`542F91E5-B702-33BE-86C6-5B39012524EA`。UnityPlayerのSHA-256は`d607996852ac441605d12da5ad4c1af5dfb4de545ad0fc5a975ce624d4a91936`、UUIDは`BE07B931-A7A0-335B-BFDE-6D9331DBD96F`です。6 malloc graphと後半2 cleanup snapshotのlive UUIDを照合しました。前半3 LLDB runはlive UUIDを採取しておらず、組み込みidentityと解決されたmodule stackの記録です。

Unity公開Monoの[`mono_hazard_pointer_get`](https://github.com/Unity-Technologies/mono/blob/unity-6000.1-mbe/mono/utils/hazard-pointer.c#L188-L201)では、threadのsmall IDが取れないとこの警告を出し、緊急用hazard tableへ戻ります。[thread unregister処理](https://github.com/Unity-Technologies/mono/blob/unity-6000.1-mbe/mono/utils/mono-threads.c#L573-L603)にはTLS destructor順序の不安定さへの対処があり、[TLS destructor](https://github.com/Unity-Technologies/mono/blob/unity-6000.1-mbe/mono/utils/mono-threads.c#L837-L850)からunregisterへ進みます。今回の観測はこの終了経路と整合します。ただし公開branchは`unity-6000.1-mbe`で、出荷6000.6.4f1との正確なsource revision一致、small ID喪失の直接原因、警告の無害性は証明していません。

**ViewWeave独自workerの停止漏れが、この5件の警告の必須原因であるという仮説は支持されません。** 警告を消す目的でViewWeaveからMonoのthread解除やTLS操作を追加する根拠もありません。

## 残留割当の生成stack

`leaks --atExit --outputGraph`で終了時memgraphを保存し、そのgraphへ`leaks --noContent --fullStacks`と`malloc_history -allBySize -fullStacks`を実行しました。環境変数は診断childだけに設定し、OSの常設設定は変更していません。`MallocStackLogging`等を要求しましたが、実際にはleaksがlite logging modeを選択しています。

| 条件 | OS検出block数 | OS検出bytes | 同runのUnity Immediate bytes |
| --- | ---: | ---: | ---: |
| Unity-only／通常allocator | 2,221 | 129,440 | 1,882,359 |
| URP機器出力／通常allocator | 2,417 | 139,200 | 2,536,492 |
| HDRP機器出力／通常allocator | 1,927 | 201,408 | 2,689,498 |
| metadata-only／通常allocator | 2,280 | 134,768 | 2,471,704 |
| Quilt-only／通常allocator | 2,276 | 134,272 | 1,923,703 |
| Unity-only／`-systemallocator` | 1,854 | 122,992 | 1,882,407 |

6条件ともfresh runtimeが通過し、full出力では初回60／再開30完了・GPU pending 0、metadataではClose→Endとcleanup gate、Quiltではcamera baseline復帰を確認しました。graph exportのtool exitは0、graph分析のexitは1で、実際に残留を検出しています。**このtool exit 0からPlayerのexit 0やリーク0は導いていません。** malloc系列のchild exit statusは独立採取していないため未観測です。Playerの自然終了codeは上記LLDB系列・前段の通常系列の証拠です。

代表的な残留生成stackは以下です。共有JSONは代表stackのsymbolとgroupを抽出したもので、全groupを含むraw logは`.local/`に保持しています。raw addressとmemgraph内の内容は共有していません。

```text
MonoManager::LoadAssemblyInMemoryAndDomain
  -> mono_debug_open_image -> mono_ppdb_load_file
  -> mono_image_open_from_data_internal -> do_mono_image_load
  -> pe_image_load_cli_data -> mono_image_load_metadata
  -> monoeg_g_calloc -> malloc allocator
```

このportable PDB経路はViewWeaveなしのbaselineにもあり、通常allocatorとsystem allocatorの両方で86 calls／27,520 bytes、31 calls／9,920 bytesのgroupを確認しました。別にFoundationのproperty decode／CFString、FMOD／CoreAudio初期化由来のgroupもあります。

system allocatorのbaselineでは、終了処理の次の生成stackも検出しました。

```text
PlayerCleanup -> ujob_shutdown
  -> Thread::~Thread -> PlatformThread::~PlatformThread
  -> MemoryManager::Allocate -> UnityDefaultAllocator::Allocate
  -> MemoryManager::LowLevelAllocate -> malloc allocator
```

このjob終了経路のgroupは16 blocks／1,792 bytesです。Enlighten worker、AssetGarbageCollector、別のJobSystem終了にも同種のgroupがあります。これはsystem allocator条件のOS検出で、通常allocatorのImmediate全量やthread警告との同一割当を証明しません。

標準allocatorで取得したfull／metadataの残留生成stackには、shimまたはBridge配下29 moduleの**名前付きframeを確認しませんでした**。managed JITの不明symbol、pool内のsuballocation、到達可能な保持領域、Metal resourceはこの不在判定で除外できません。異なるscene・pipeline・処理量のbytes差をViewWeaveのリーク量にもできません。[Appleのleaksの範囲](https://developer.apple.com/library/archive/documentation/Performance/Conceptual/ManagingMemory/Articles/FindingLeaks.html)は未参照のmalloc blockであり、すべての残留memoryを同じ基準で数えるものではありません。

Immediateの発行stackは`DoQuit → RuntimeCleanup → MemoryProfiler::ReportAllocatedMemory → log write`でした。割当生成stackと混同していません。Unity公式の[NativeStackTraces](https://docs.unity3d.com/ja/current/ScriptReference/Unity.Profiling.Memory.CaptureFlags.NativeStackTraces.html)と[TakeSnapshot](https://docs.unity3d.com/ja/current/ScriptReference/Unity.Profiling.Memory.MemoryProfiler.TakeSnapshot.html)は、native allocation site／call stack収集を対応build・source access条件としています。今回の通常PlayerでUnity labelごとの完全な生成stackを取得できたとはしていません。残る所有者特定には、このUnity側の割当情報を取得できるbuild、または対象を絞った所有・解放観測が必要です。

## 計測失敗と後処理

初期のLLDB runには、未解決symbol、IO hookの採取漏れ、SDK CPU機能probeの`EXC_BAD_INSTRUCTION`停止、無効なdebugger設定、stale PNGによるfixture失敗があり、最終5条件へ加算していません。SDK probeのdebugger停止を製品の自然crashとは判定していません。起動時LLDBのHDRP追加runはvendor校正ファイルの`fopen`で待機するsampleを取得し、その後runtimeが通過しましたが、debugger待機期限超過とexitcode／終了stack未採取のため最終gateから除外しました。

metadataの起動時LLDB runはquery 60秒・cleanup 30秒の期限に失敗し、fixtureがPlayerを保持しました。sampleでSDK workerを確認できなかったことは、worker終了証明にはしていません。SIGILL通知との因果も未確定です。この専用診断は終了要求で停止し、強制停止として記録しました。正常終了やcleanup成功に数えません。同じapp・Unity flagsのLLDBなしmalloc診断と、初期化後のlate attachはcleanupを通過しました。

各runのfresh結果を別caseへ退避した後、前段のruntime／capture／build metadataを元のbyte列へ復元しています。raw app、log、memgraph、診断scriptはgitignore対象の`.local/macos-shutdown-stacks-20261014/`に保持しました。共有JSONは派生証跡で、raw hashと共有manifestを分けています。observerのmodule UUID採取を途中で追加したため、script hashは最終版のhashであり、前半runの完全なsource版の証明にはしていません。

[対応表](../ja/unity-feature-platform-matrix.html)と[履歴](ja/unity-feature-platform-matrix-history.html)は、macOSの限定正常終了を保ったまま、警告発行経路の特定と残留全量・Windows共通原因の未特定を反映しました。Windowsでの追加実行、Releaseの追加stack、出力中Editor終了、長期／別機器／光学品質の検証は行っていません。

[最終チェック](../validation/macos-shutdown-stacks-20261014/checks.json)ではrepository validation、diff whitespace、HTMLの6列・閉じタグ・anchor・相対リンク、共有manifestを照合しました。342-file project baselineの差分は前段の意図したfixture 3件だけで、残る339件は一致しています。診断プロセスの残存もありません。Astraの最終レビューで重大な漏れ・誤主張・修正必須箇所なしを確認しました。
