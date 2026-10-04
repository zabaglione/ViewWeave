# 公式機能との比較

本表は、公式資料に記載された機能と、本SDKの現状を利用者向けに比較するものです。公式SDKとのsource互換性、商標上の関係、完全な機能互換性は保証しません。本SDKはUnity 6000.6.4f1のURP/HDRPを設計対象とするWindows D3D11実験版です。

公式3.x資料はBuilt-inを主な対象とし、URPには制限があり、HDRPは未対応と説明されています。公式4 betaの配布物と4.0 alpha公開資料は、Unity 6000.0.39〜6000.1.5、URP 17.0.4〜17.1、Render Graphを対象とし、公式HDRPは未対応です。公式4資料の視点補間と透明表現には条件があり、VFXにも追加設定が必要です。本SDKの対応状況はこれらの資料から推測せず、個別fixtureと受入試験で判断します。

| 機能 | 公式3.x資料 | 公式4 beta/alpha資料 | 本SDK現状と計画 |
| --- | --- | --- | --- |
| Quilt capture / off-axis | 主要機能として説明 | Render Graph経路で説明 | Coreで実装。1台CameraをN回描画 |
| still capture | 公式Unity pluginの機能 | 配布版の機能範囲に含まれる | 開発用test fixtureで診断用PNGを確認。利用者向け保存APIは未確定 |
| video / playback | 公式資料の機能範囲 | 資料の対象外部分を含む | 未実装または未検証 |
| calibration | 公式pluginと機器経路で提供 | 公式pluginの設定を利用 | Coreでper-view render、GPU assemble、Bridge worker drawを接続。Profile、Registry、Rig menuはAPIテスト済み。vendorの光学変換条件と実機光学は未確認 |
| device profile | 機器設定と表示経路を提供 | 機器設定を利用 | Profile、Registry、Locator、Rig menuを実装しAPIテスト済み。Windows D3D11のURP/HDRP PlayerでPreview、Balanced、NativeのGRD ON/OFF各6条件と品質cycleを確認。全機器hotplugは未検証 |
| 複数機器 / hotplug | 資料の条件に依存 | 資料の条件に依存 | 未検証 |
| head tracking / view interpolation | 公式pluginの設定対象 | depth interpolationと透明制約を記載 | 未実装または未検証。深度再投影は別研究 |
| Editor preview | 公式pluginに機能あり | external preview等を記載 | 製品向けEditor previewは提供しない。offline displayは未確定 |
| runtime integration | Unity packageとBridge連携 | Unity 6000.0〜6000.1の条件 | Unity 6000.6.4f1のURP/HDRP adapterとSessionを提供。Windows D3D11の固定条件Sessionと品質切替を確認。機器一般運用は未検証 |
| 入力、cursor、orbit、button | 公式pluginの利用機能 | 公式資料の範囲に依存 | 未実装または未検証 |
| installer / package導入 | 公式配布物で提供 | beta/alpha配布物で提供 | UPM導入、Rig menu、optional Samplesを実装済み。ローカルcloneからURP/HDRP別clean projectで各2/2 API導入チェック済み。GitHub導入は未検証 |
| URP | 3.x資料では制限あり | URP 17.0.4〜17.1を対象 | Lit、Bloom、透明、粒子、uGUI、UI Toolkitを個別検証 |
| HDRP | 未対応 | 未対応 | Lit、Bloom、透明、mesh粒子、uGUI、UI Toolkitを個別検証 |
| Built-in | 主な比較対象 | 公式4対象外 | 製品サポート対象外。CoreはStandard RenderRequest経路を要求 |
| post processing / AA | 公式資料の条件に依存 | Render Graphと視点補間の条件に依存 | Bloom、FXAA、SMAA、DoFは限定fixture。MSAAは未通過、temporal、SSR等は未検証または拒否 |
| VFX / particles | 設定と素材条件に依存 | VFX追加設定と透明制約を記載 | URP Simple Burstの停止6条件と通常再生4条件を確認。HDRP driverはなく、一般対応、sorting、soft depth、同一Camera primingは未確定 |
| cloud Blocks連携 | 公式サービス条件に依存 | 本SDK比較対象外 | 認証・契約確認後の別計画 |
| Bridge runtime | 公式runtimeを利用 | 公式配布条件に依存 | runtimeは別途必要。独自adapterにbinary/headerを同梱しない |

参照資料:

- [公式Unity Plugin](https://lookingglassfactory.com/software/looking-glass-unity-plugin)
- [公式3.x資料](https://lfdocs.lookingglassfactory.com/software/index)
- [公式4.0 alpha資料](https://lfdocs.lookingglassfactory.com/software/index/unity-plugin-4.0-alpha)
- [Unity render thread API](https://docs.unity.com/en-us/engine/6000.6/script-reference/unityengine/gl/issuepluginevent)

公式資料にない機能の有無は、この表だけで断定しません。各機能の採否は、対応Unity、RP、機器profile、素材、画質、性能、Bridge条件を固定した受入試験で判断します。

