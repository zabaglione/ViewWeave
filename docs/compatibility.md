# Compatibility matrix

現状はUnity 6000.6.4f1、Windows、D3D11での結果です。対応予定は未実装です。公式APIのsource互換は保証しません。

品質変更は転送を静止させ、同一host・windowで資源を再設定してから自動描画へ復帰します。明示的な`StopSession()`/`StartSession()`は独立した再起動手段です。

その後の固定条件URP/HDRP Player検証では、Preview 1920x1080、Balanced 3840x2160、Native 7680x4320、Previewの4段階を各122 completionで完了し、全tile非空、移動、視差、明示Stop/Start後120 draw、pending 0を確認しました。これは固定条件の結果であり、他機器や光学品質の保証ではありません。

別のWindows Player検証ではURP Simple Burstを通常制御Cameraで1/8/48視点、GRD ON/OFFの6条件で確認しました。HDRP primingは通常画像差分0で未通過です。これは限定された素材と制御条件の結果であり、VFX全体の対応やBridge経路の品質保証ではありません。

URP driver対象試験は最新統合で5/5通過しました。これは通常制御Cameraと実験用driver条件に限定した結果で、VFX全体の対応を示しません。

旧82/71/11のWindows D3D11 PlayMode統合に代わる最終実行は115件中101件通過、14件失敗、skip 0でした。Session 15/15、互換性37/37、GRD 27/27、SRP追加6/6、SRP拡張2/2、VFX driver 5/5、Samples 4/4を確認しました。未通過はHDRP priming 1件とVFX compatibility 13件で、未通過条件では十分な全tileの効果を確認できませんでした。Bridge接続のURP/HDRP PlayerはPreview、Balanced、NativeのGRD ON/OFF各6条件を固定条件で確認しています。これらは個別fixtureと固定条件の結果であり、機能全体の対応や一般機器でのPlayer実行の保証を意味しません。品質切替cycleも固定条件で通過しました。性能優位と光学品質の全視点人手確認、配布署名、hotplug、複数機器、Metalは未確定です。

公式4 beta/alpha資料は、確認した範囲ではURP 17.0〜17.1、Unity 6000.0〜6000.1を対象とし、公式HDRPは未対応です。既定のdepth interpolationには透明表現上の制約があり、VFXには追加設定が必要です。公式iOS対象を対象外とは扱いません。本SDKの初期release範囲は別途定義します。

公式3.x資料はBuilt-inを主な比較対象とし、URPには制限があり、HDRPは未対応です。公式4 beta配布物とalpha公開資料では、Unity 6000.0.39〜6000.1.5、URP 17.0.4〜17.1、Render Graphを対象として説明されています。公式4でもHDRPは未対応で、視点補間と透明表現には制約があります。

一次資料: [公式Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)、[公式3.x資料](https://lfdocs.lookingglassfactory.com/software/index)、[公式4.0 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)。

| 機能 | 本SDK現状 | 今後の受入条件 |
| --- | --- | --- |
| Quilt / off-axis | Coreで実装、single CameraをN回描画 | 高解像度profileとRig |
| profile / quality / hotplug / 複数機器 | Preview/Balanced/NativeとGRD ON/OFFの6条件、48 view Playerを確認。hotplug、複数機器、Registryの一般運用は未検証 | profile由来の自動選択、hotplug、複数機器を再現可能にする |
| depthiness / frustum / gizmo / camera conversion | Quilt mathとcamera変換の一部のみ | 統合RigとEditor操作を検証 |
| Editor preview / offline display | Editor previewとPNG明示保存のみ | offline display経路を定義 |
| still capture / video / quilt layer / RGBD | 未実装または未検証 | 保存形式、録画、RGBDの受入試験 |
| 3D cursor / orbit / buttons | 未検証 | 入力と機器差を検証 |
| URP | Unity 6000.6.4f1、SRP 17.6、D3D11でLit/Bloom smoke。Base cameraのみ、stackは拒否。M3の8 fixtureを確認済み | 残る視点別効果を個別に検証 |
| HDRP | Linear/fixed Exposure条件でLit/Bloom smoke。volumetric fog、SSR、SSGI、cloudは拒否 | M3で対応範囲を定義 |
| Built-in / Unity graphics | 製品サポート対象外。CoreはStandard RenderRequest経路を要求 | 対象外として扱う |
| Bridge | Windows D3D11 Direct実機確認。Worker通常threadは同一プロセス再接続、各120 draw、終了保留0、host thread再利用、48 view非空画像と視差・移動を確認。2026-10-04の限定実機確認で奥行き・画質は良好、番号付きデモをユーザーが暫定受入。全視点・全視域の網羅的な光学検証は未実施。配布署名、hotplug、複数機器、Metalは未検証 | 配布署名、Windows/macOS実機、hotplug、複数機器を段階検証 |
| Bloom / Tonemapping / Color Adjustments | Lit/Bloom smokeのみ確認 | per-view処理とtile seamを検証 |
| TAA / temporal / Auto Exposure | TAAは一時停止、履歴独立は未検証 | 履歴とメモリを別研究 |
| Motion Blur / SSR / SSGI / fog / cloud | 現状経路では拒否 | fallbackと条件を検証 |
| AA: FXAA / SMAA / MSAA | FXAA/SMAAは追加fixtureで8視点の差分を確認。URP MSAA 1/1で4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認。一般Player、画質、性能は未検証 | MSAAの一般素材、Player、性能を測定 |
| Particles | M3のLit/Unlit表示fixtureを確認。一般的な粒子機能全体は未確定 | simulation once/frame、billboard、sort、culling、soft depthを検証 |
| VFX | URP driver 5/5は通過。VFX compatibilityは18件中5件通過、13件未通過。HDRP primingは1件未通過。未通過条件では十分な全tileの効果を確認できず、HDRP driverは提供しない | 補助Camera条件、透明処理、sort、公式4資料の追加設定を検証 |
| uGUI | WorldSpaceとScreen Space CameraのM3試験を確認 | 各視点の入力、配置、合成条件を検証 |
| UI Toolkit | Unity 6.6 WorldSpace/targetTexture APIはinstalled XMLで確認済み。M3 targetTexture fixtureを確認 | RT route、ray map、DPI、EventSystemを検証 |
| Overlay / camera stacking | Overlayは通常画面へ描いても自動Quilt captureされない。URP stackは拒否 | 各viewと同じdepthで合成する方式を検証 |
| iOS / Linux | iOSの本SDK初期release範囲外、Linux未実装 | 対象platform方針を確定 |
| cloud Blocks連携 | 未対応 | 認証・terms確認後に別計画 |
| GPU Resident Drawer | Editor 27/27回帰、URP通常thread Player 6ケースを確認。性能優位は未確定 | MeshRenderer、shader、culling、動的更新、透明、fallbackの受入試験 |

uGUI WorldSpaceとScreen Space CameraはM3試験で確認済みです。Overlayは通常画面へ直接Quilt出力できないため、各viewと同じdepthで合成する方式を検証します。UI ToolkitのUnity 6.6 WorldSpace/targetTexture APIとM3 targetTexture fixtureを確認済みですが、入力ray map、DPI、EventSystemは未検証です。ParticlesはLit/Unlitの個別表示fixtureを確認済みで、sort、culling、soft depthなどは未確定です。

FXAA/SMAAは追加fixtureで限定確認しました。URP MSAAは4x view RT、1x resolve、resize/disable lifecycle、中心色一致を確認しました。一般Player、性能、HDRP MSAAは未検証です。TAAは一時的に無効化しています。Auto Exposureとtemporal per-view historyは、履歴とメモリを分離した別研究です。





