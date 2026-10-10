[English](../bridge-output-lifecycle.md)

# Bridge出力のライフサイクル

Edit Modeの実機previewとPlay出力は、process単位のLooking Glass Bridge endpointを共有します。各modeはquilt送信のため、generation単位で排他的なleaseを取得します。modeを閉じるとleaseを返しますが、通常はprocess単位のSDK session、登録済みprimary texture、endpointを破棄しません。persistent preview endpointではPauseやlease返却後も最後のSDK primary imageを表示したままにし、producer処理がdrainした後にNative owner threadがcached geometryを使って最大5Hzで同じprimaryを再描画できます。このstatic redraw経路は追加のsource texture copyやSDK texture-copy API callを行わず、texture登録やSDK visibilityも変更しません。これはplugin側の呼び出しを説明するもので、vendor内部の実装については断定しません。

このページでは公開済みNative artifact、private static-present比較candidate、実機で確認できた動作を区別します。現在のsourceには明示的なBusy admission結果が実装され、生成済みdistribution packageに含まれています。private URP比較candidateではPause/owner間のstatic redrawを試しました。結果は後述の日付付き観察に限られ、広範なlifecycleの信頼性や一般的な修正を証明しません。

## Process ownershipとmode lease

Process ownerはNative Bridge session、worker thread/window、SDK登録済みprimary texture、固定profileのSDK inputをmode間で保持します。persistent preview leaseがPauseまたは返却された後もendpointは最後に成功したprimary imageを表示します。producer処理のdrain後、owner threadはcached primaryを最大5Hzで再描画できます。Edit PreviewまたはPlayのleaseは、そのgenerationのUnity producer、camera状態、可変サイズのquilt/transfer resource、GPU処理を所有します。同時にendpointを所有できるmodeは一つです。

```mermaid
flowchart LR
    subgraph Process[Unity process lifetime]
        subgraph Lease[One exclusive mode lease]
            Mode[Play or Edit Mode preview]
            Producer[Unity producer and quality-sized quilt]
        end
        Stable[Fixed-profile SDK input]
        Primary[SDK-registered primary texture]
        Endpoint[Native Bridge session, worker, and retained-visible window]
        Static[Cached primary redraw while paused or returned, max 5 Hz]
    end
    Mode --> Producer
    Producer -->|GPU copy or per-tile scale| Stable
    Stable --> Primary
    Endpoint --> Primary
    Static --> Primary
    Primary --> Bridge[Looking Glass Bridge runtime]
    Bridge --> Display[Looking Glass display]
```

登録済みprimary textureとSDK向けinputはlease generation間で同じidentityを保ちます。producer quiltの寸法はqualityで変わる場合がありますが、logical device gridは固定です。process shutdownは別の境界です。一致するmodule/registryとUnity GPU cleanupのproofを確認した場合だけ、lease admissionを閉じてNative/SDKを最終cleanupします。

## Pause、Resume、Close、handoff

Inspectorの`Close Preview`は初期状態で閉じた`Advanced` section内にあります。通常のpreview操作はStart、Pause、Resumeです。

| 操作 | Mode producerとUnity texture | Lease | Process Bridge endpoint |
| --- | --- | --- | --- |
| Pause Preview | submit drain後も保持 | 同じgenerationを保持 | 最後のimageを表示。cached primaryを最大5Hzで再描画可能 |
| Resume Preview | 再利用 | 同じgenerationを再開 | static redrawを止め、live producer submitを再開 |
| Close Preview | Native source使用終了とmanaged/Core GPU proof後に破棄 | return proof、resource破棄、その後ACK | 最後のimageを表示したままpark。返却/ACK後に次leaseを許可 |
| Edit/Play handoff | 次modeが所有する前に旧producerとtextureを破棄 | 旧generationのhandoff後に新しい排他的generation | 同じendpointと最後のimageを維持し、新producerが更新 |
| Process shutdown | 必要なUnity GPU proof後に破棄 | admissionを閉じる。証明済み最終teardownが通常handoff ACKの代わりになる | 最終cleanup時にSDK endpoint、登録texture、workerを解放 |

通常の返却順序は固定です。まずNativeが旧source textureの参照終了を`ReturnedProven`として通知します。managed側は次にCore/transferとproducer GPU完了を待ち、旧mode textureとownershipを破棄してからgeneration ACKを送ります。NativeはACK受信後にだけ次leaseを許可します。Native status queryは履歴metadataであり、managed ACKでも現在のoutput ownerや表示frameの証明でもありません。producer/GPU完了counterもBridge scanoutを証明しません。

```mermaid
sequenceDiagram
    participant Old as Current mode
    participant Unity as Unity producer and GPU
    participant Worker as Native worker
    participant Endpoint as Process Bridge endpoint
    participant Next as Next mode
    Old->>Worker: Gate submissions and request return for generation N
    Worker->>Endpoint: Drain source work; retain primary visible
    Endpoint-->>Worker: Native source-use proof
    Worker-->>Old: ReturnedProven for generation N
    Endpoint->>Endpoint: Redraw cached primary while parked, at most 5 Hz
    Old->>Unity: Wait for Core/transfer and producer GPU proofs
    Unity-->>Old: GPU work complete
    Old->>Unity: Retire mode textures and ownership
    Old->>Worker: ACK generation N after Unity retirement
    Worker-->>Next: Permit next generation acquisition
    Next->>Endpoint: Acquire exclusive generation N+1
    Endpoint-->>Next: Grant same visible endpoint
    Next->>Worker: Submit with new mode resources
```

Unity reloadと通常Stopはこの経路でactive mode leaseを返しますが、process SDK sessionはuninitializeせず、retained primary imageも隠しません。static redrawはpersistent endpointがPause中またはlease返却後だけ動き、次leaseが再開/取得されると停止します。Process shutdownではadmissionを閉じます。一致するNative module/registry teardownと必要なUnity GPU proofを確認した後だけ最終cleanupします。process終了がSDK endpointとretained imageを解放する境界です。最終teardown自体を証明できた場合、通常handoff ACKは不要です。

## Native admissionの結果

現在のNative source candidateはlease専用draw-data admission callを追加します。outcomeを返す前にlease identity、lifecycle状態、quilt descriptor、固定logical gridを検証します。

```mermaid
flowchart TD
    Submit[Submit quilt for current lease] --> Valid{Valid active Ready lease and request?}
    Valid -->|No| Reject[Reject as invalid, stale, or Native failure]
    Valid -->|Yes| Queue{Three-token admission queue full?}
    Queue -->|No| Accept[Accept draw token; retain source texture]
    Queue -->|Yes| Busy[Return Busy; allocate no draw payload or texture reference]
    Busy --> Policy{Managed caller policy}
    Policy -->|Edit Preview| Retry[Retry while keeping current owner and generation]
    Policy -->|Playback| Skip[Skip this frame and continue playback]
    Reject --> Stop[Stop or clean up the failed mode; keep resources until proven safe]
```

`Accepted`はNative queueがdraw tokenを受け取ったという意味です。GPU完了やdevice表示を証明しません。`Busy`は有効・Ready・running状態のleaseで、3 tokenのadmission queueが満杯のときに限って返します。このとき送信textureへのAddRefやdraw payloadの割り当てはありません。managed Edit Previewはproducer、endpoint、generationを保持したまま再試行します。Quilt playbackは該当frameだけをskipして続行します。invalid argument、stale generation、lifecycle conflict、Native faultはerrorであり、retry対象ではありません。

現在のsource candidateは新しいlease generationの取得時にNative workerのsubmitted/completed/dropped統計をresetします。これらは診断用counterであり、source retirement、Unity GPU release、ACK、display scanoutのproofではありません。managed submit試行、Nativeが受理したsubmit、worker処理完了は別々のeventなので、同じcounterのように比較できません。特にsampleに入っている旧A6A DLLのcounter観察はprocess scopeであり、generationごとの証拠ではありません。

互換性のないadapter/device、logical quilt grid、RGBA8 typeless/UNORM/SRGB family外のoutput format、Native profileに変更する場合、outputを閉じてUnity Editorを再起動する必要がある場合があります。Native profile/deviceのlive hot-swapは保証しません。

## Qualityと描画view密度

`Preview`、`Balanced`、`Native` quality profileは、以前のGPU処理をdrainした後にproducer quiltの寸法を変更できます。`Full`、`Half`、`Quarter` view densityは完成quiltのgridを保ったまま描画するview数を変更します。Nativeは同じ寸法ならcopyし、それ以外では固定profileのSDK inputへview単位でGPU scalingします。sampling範囲は各tile内にclampするため、隣のviewからpixelを読み込みません。

固定inputへのcopy/scalingでGPU負荷が増える場合があります。描画viewやsource quiltを減らしても、transport frame rateが同じ割合で向上するとは限りません。最新Nativeのcopy/scale試験はfake/WARPによるもので、性能benchmarkではありません。内部mode leaseはpublic SDK componentやpublic control APIを追加しません。

## Candidateと導入済みbuild

2026-10-09、両sample projectをpackage commit `eb527e1ec126ec99d35e9990d8dce0119b855b4d`、Native DLL SHA-256 `A6A062343710AF2362DAF7B923A9BB98BDD5D381D896C68AE3190CA55655FBC7`でcold verificationしました。同日後刻、両sampleをpackage commit `5234b834381afd91a896714ad6d8f9dcbd9f6f95`、Native DLL SHA-256 `655FDB898CF7B9F9733A1880828B740B62FB74E0C3ECDF9D825C07794F6388CD`（208,896 bytes、51 exports）でcold verificationしました。これは各日付時点のidentityであり、その後のpackage更新後も同一という意味ではありません。現在のNative artifactはSHA-256 `F1B06893404D75582FF600BA733AE3C531ED0C0A7EA40E29CE56DD7A91B0A000`（214,016 bytes、51 exports）で、Windows fake/WARP CTestのfocused 6件に成功しました。2026-10-10、両official sampleをpackage commit `c09fdcce30e77d95f3aa451c3642b00522edc255`へ更新し、このDLLをcold verificationしました。URP cacheは`496c1dc45e56`、HDRP cacheは`e04b2f9bb328`です。各fresh owned Editorでcompile/loadを確認し、moduleは期待した1件、native pending countは0でした。保護した`Assets`と`ProjectSettings`はchanged/missing/addedが各0です。F1Bの限定的なURP実機観察を以下に記録します。反復hardware matrixとUnity Test Runnerは未実施です。Native testはfake/WARP resourceを使用し、official Looking Glass Bridge SDKをloadしません。

## URP実機での限定観察

以下はWindows Unity 6000.6.4f1、URP package commit `5234b834381afd91a896714ad6d8f9dcbd9f6f95`、Native DLL SHA-256 `655FDB898CF7B9F9733A1880828B740B62FB74E0C3ECDF9D825C07794F6388CD`での限定観察です。HDRPの実機出力は試していません。

| 遷移 | 実機での観察 | managed/leaseでの観察 | 判定範囲 |
| --- | --- | --- | --- |
| Edit Preview Close/Start | 利用者が再開始後の映像と新たな移動/Undoを確認 | 同じprocess endpoint、新しいlease generation | 日付付き候補でURPの1 cycleを確認 |
| active Edit Preview → Play | 利用者がPlayで映像と新たな移動/Undoを確認 | 同じendpointで新generationがReady | 同日候補でURPの1 handoffを確認 |
| active Play → Edit Preview | 最初は黒。後に映像と新たな移動/Undoが見えたと利用者が報告 | managed verification前にpreview ownerが消え、workerはIdle/error停止 | live復帰は検証できず、逆handoffは未解決 |

655F artifactは2026-10-09のURP実機で限定的に試し、そのrunでは逆handoffが未解決でした。手動のStart/Pause/CloseおよびPlay cycleでも断続的な黒画面が報告されました。これらは655Fの日付付き観察であり、後述するprivate static-present candidateの結果とは別です。output counterやGPU completion値はdisplay scanoutを証明しません。HDRP実機handoff、device hot-plug、物理寸法、光学画質、runtime性能は未検証です。別の日付のA6A試験でproducer readback後に映像が出た事象は、655F candidateや黒画面の原因の証拠ではありません。

## F1B production URPでの限定観察

2026-10-10、公式F1B package commit `c09fdcce30e77d95f3aa451c3642b00522edc255`を、一つの通常URP Editor session（Unity 6000.6.4f1、D3D12、`Assets/test.unity`）で試しました。利用者は通常Play直後の映像と新たな移動/Undoを確認し、StopからEdit Device Previewへの遷移でも映像と移動/Undoを確認しました。またPause中は映像が残り、scene objectの移動に追従せず、Resume後に移動とUndoが反映されることを確認しました。このproduction sessionでの各遷移は1回ずつで、反復信頼性matrixではありません。

Pause前後のsnapshotは同じrenderer、quilt texture、output identityを保持し、連続したPaused読取でrevisionとcompleted counterは変化しませんでした。Resume後も同じidentityを保ち、新しいcompleted frameが再開しました。Editorのloaded-module検査でF1Bの一致module/hashを1件確認しました。Native traceではSDK session/window 1を確認しました。image readbackやcaptureは行っていません。これは限定的なURP利用者観察であり、HDRP、Player、他graphics API、光学品質、性能、広い反復性を証明しません。後述のprivate 00CE比較とは別build・別試験です。

## Private static-present candidateによるURP比較

2026-10-10、private Windows x64 candidate（DLL SHA-256 `00CEFDA953D695202F195E43F32D2C8EB260E18356F147643E441CCEE50208C2`、241,152 bytes）を、package commit `d913f9912ad2c983fcdbbdc7ed319b97d5d07354`にpinされたURP sampleへ導入しました。これは公開package releaseではなくcandidate DLLの比較です。Native focused testは6/6件passしました。Engine testは未実行、HDRP/Player検証も未実施です。

利用者は4回の通常Play、最初のEdit Device Preview、Resume、Close/Start、Pause中のPreviewからPlay、Play StopからEdit Previewで、映像と新たなmove/Undoを確認しました。2回のPauseでは最後の映像が残ることを確認し、最初のPauseではscene targetを動かしても表示が追従しないことも確認しました。2回目のPauseではsceneを変更していません。通常Stop後およびPause中、producer処理のdrain後に同じ登録済みSDK primaryへ約200ms間隔（最大5Hz）でstatic-present callが行われました。同じprocess endpoint、SDK session/window、primaryを使用し、SDK create/registerの追加はありません。比較中にimage readbackやcaptureは行っていません。

これは単一URP project、Unity version、D3D12構成での限定的な利用者観察です。HDRP、Player、他graphics API、光学画質、一般的な信頼性を証明しません。candidateのtestやcounterもscanoutを証明しません。上記のphysicalな記述は利用者の直接確認に限ります。公開済み655F artifactの過去の逆handoff結果は別の日付の履歴です。

## Native traceと表示確認の限界

Editor起動前にprocess環境変数`LKG_NATIVE_TRACE`を設定すると、Native trace recordを収集できます。traceはlifecycleやdraw callの相関に役立ちますが、submit受理、worker completion counter、producer GPU completionはBridge表示やscanoutを確認しません。trace記録はdiagnostic path上で行われるため、観測タイミングに影響することがあります。後から映像が見えたことだけでは、復帰させた操作やlive ownerを証明できません。
