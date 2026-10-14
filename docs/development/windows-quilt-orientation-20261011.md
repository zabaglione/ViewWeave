# Windows Quilt上下方向の検証（2026-10-11）

Windows WorkerのQuilt転送で上下反転を追加していた処理を除く変更を、Unity生成画像とNativeの実GPU presenterで確認しました。現在の `CopyQuiltToTransferTexture` が出力したrawは、期待する上端赤・下端青のno-flip候補と全204,800 bytesで一致しました。旧current-flip候補は上端青・下端赤です。

## 実行した範囲

Unity 6000.6.4f1のPlayModeで、URP `QuiltRenderer`とHDRP history cameraが8視点を描画した320×160 RGBA8 Quiltを取得しました。URPはD3D11/D3D12 × per-view MSAA 1/4の4条件、HDRPはD3D11/D3D12 × MSAA 1の2条件です。6条件すべてで1/1 PASS、skip 0、CLI exit code 0、Editor natural exit、settings diff 0でした。URP MSAA 1はeffective sample count 1でresolveなし、MSAA 4はeffective sample count 4でresolve textureあり。HDRPは両APIで8 owned history-camera callbacksを確認し、MSAA 1でresolveなしでした。

全6条件で実production helper出力のSHA-256がno-flip候補と同一でした。C++ oracleをmode 0 Automatic、1 Classic RGB、2 Calibrated Cellsで実行し、54回すべてでD3D11 WARPのCreateOffscreenForTest、実 `ManualSubpixelPresenter::Present`、GPU completion、readback、`Close`が成功しました。この実行完了数は候補の向き正否とは分けています。production-copy 18/18出力とno-flip control 18/18出力は上端赤・下端青でbyte一致し、正方向を示しました。current-flipのnegative control 18/18出力は上端青・下端赤となり、逆方向を再現しました。URPはcurrent-flipとno-flipで171 channel、HDRPは189 channel異なりました。orientation分類は出力top/bottomのR/B優勢とsentinelの向きの一致に基づき、view markerも出力に残ることを確認しました。各Native outputの平均値は構造化結果に記録しています。

WARP出力は既存Native presenter testと同じ9×7合成calibrationです。実パネル寸法、接続機器への提示、光学品質の試験ではありません。Unity AsyncGPUReadbackのraw row 0は返却byte順として扱い、物理上端とは仮定しません。producer metadataの `rawTop` サンプル名もbyte-memoryのlow-Y行を示す座標名です。向きの判定はUnity入力の行名やCPU referenceではなく、Native GPU outputの画素sentinelを使いました。Native readbackのrow 0はこのoffscreen viewportの上端です。

Built-in/D3D11と初期URP/D3D11 captureは向き候補の診断controlです。Built-inと初期URPのproducer PlayModeは各1件PASSでしたが、これらのrunは現在のproduction-copy captureを含む後続fixtureより前です。最終production-helper検証の件数には加えません。初期compile failure、XMLなし、all-black captureのXML PASS、およびfailed/未完了producer runは向き判定に使用していません。

失敗・除外診断は6件で、PASS集計には含めていません。HDRP D3D11の初回captureはreadiness未完了、次のcaptureは露出準備cameraも数えて11/8 callbackとなったため失敗し、最終fixtureでは対象history cameraだけを数えています。各failのrun-summary/XML hashとfailure detailsは構造化結果に記録し、全履歴は `.local/windows-quilt-orientation-20261011/orientation-summary.json` にあります。別の接続機器Editor fixtureではURP / D3D12 / Automaticを1/1 PASSで確認し、実ロードされたpackage shimのpath/hashがC864 DLLと一致、Native load / initialize / displayCount=1 / pending=0 / uninitialize、PIDとnatural exit、settings cleanupを確認しました。これは実機への送出・lifecycle証拠であり、パネルの向きや光学品質の目視確認ではありません。

## 実装・成果物の範囲

検証時のC# source基準は `e9232d31afdbb80f2577c12955d9d060dba44441` で、Windows転送修正は当時未commitの作業ツリー変更でした。Native presenter HLSL、Native production DLL、Legacy Direct、macOSは変更していません。Native packageとURP/HDRP配布treeのDLLは検証時に302,080 bytes、SHA-256 `C864A363AC441C77497D4302FF25CB48FA2D6EA194B6FB8A73BAE49DC522FEE2`で一致しました。この試験結果は正式リリースの保証を示しません。

最終6 producer runは実行時に記録したtest source fingerprint（URP 4件は `b04c1ac0...53ba75`、HDRP 2件は `10b2055e...88d767`）を保持します。その後、fixtureのcompile guardだけを `UNITY_INCLUDE_TESTS && UNITY_EDITOR_WIN` に変更しました。現在のsource SHA-256は `422e430854b8609317cfc9d313f2da3ebb1240dde9fdd0217f5fb6ceceee317e` ですが、このhashでUnityを再実行したとは扱いません。Windows Editor試験bodyは不変で、macOS EditorおよびPlayerでのfixture compile対象を外す変更です。run別hashとguard-only差分は構造化結果の `fixtureGuardOnlyDelta` に記録しています。

手動実行用Native targetは `lookingglass_unity_quilt_orientation_oracle` です。raw候補は各runの `producer-orientation.json` にあるfilesとSHA-256を使い、Unityのrun-summary/XML gateを満たす場合のみ `Tests/Pipelines/Unity/Tools/Invoke-UnityQuiltOrientationOracle.ps1` を実行してください。入力rawは `row 0` の物理方向を推定せず、そのままGPUへ渡します。CTestには自動登録していません。

URP / HDRP sampleでは `Client.Add` により同一checkout内の `Distribution/urp` / `Distribution/hdrp` をlocal packageとして適用し、別Editor processでfresh cold VerifyOnlyを実行しました。両方exit 0で、package tree比較は246/246・253/253 entries一致、Assets / ProjectSettingsのadded・changed・missingは全て0、非対象manifest/lock依存関係は不変でした。実際にロードされたDistribution DLLは両方C864 / 302,080 bytes、moduleCount=1、pending=0です。Native initializeは行っていません。HDRPの同一Editor内Client.Add attemptはstrict Native image guardがold Git DLLからlocal DLLへのpath変更を検知しexit 1でした。別のfresh cold verify-only processで通過した結果と分けています。URPのexecuteMethod名不一致attemptもClient.Add前の失敗として成功集計に含めていません。これは検証時の未commit Distribution working-tree内容をlocal UPM参照したimport・file comparison・cold module load確認です。URP/HDRPのClient.Addと別Editorのfresh cold VerifyOnlyを区別し、sample描画、runtime initialize、device output、Player、optical検証を示しません。hashと各attemptの記録は[構造化結果](../validation/windows-quilt-orientation-20261011.json)にあります。

次の条件は未検証です。

- Windows Player出力と実パネルの向き・光学品質

実行hash、個別のraw / XML記録、無効結果の扱いは[構造化結果](../validation/windows-quilt-orientation-20261011.json)に記録しています。




## 検証後の公開状態

記録した実行後にrepositoryを `e9232d3` から `1ac8ae1` へfast-forwardしました。追加commitはMacサンプル更新記録と対応表のdocumentation変更のみで、Windows source・Distribution内容・Native DLLは変わっていません。このpullを含む新しいコード実行結果とは扱いません。URP/HDRP sampleの最終local Distribution cold-load結果は上記のとおり別Editor processで各1/1、HDRP Client.Addの同Editor Native image guard停止は失敗履歴として保持しています。
