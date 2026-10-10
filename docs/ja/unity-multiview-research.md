[English](../unity-multiview-research.md)

# Unity多視点描画の最適化事例

> 対象読者: Unity／描画基盤の技術者。このページは本文記載の技術的な設計・実装・手順を扱います。利用者向けの導入・運用手順は[ユーザーガイド](user-guide.md)、技術文書一覧は[技術者向けガイド](technical-guide.md)を参照してください。

調査日: 2026-10-07

調査対象は、Unityの複数視点描画とLooking Glass向けQuilt生成です。今回確認した資料の範囲では、URP/HDRPの通常マテリアル、VFX、透明表現、Volumeを保ったまま真の48視点を高速化する、公開済みUnity実装は見つかりませんでした。以下では、Unityで利用できる機能、研究試作、通常のRender Pipelineを置き換える研究を区別します。

## Unityの機能・実装例

| 事例 | 公開内容 | 48視点/HDRPへの読み替え |
| --- | --- | --- |
| Looking Glass Plugin 4.0 | 公式は3.x比2〜4倍を掲示。公開4.0 alpha資料ではURP/Render Graph、深度による視点補間、独自Core/URP packageを説明。 | HDRP未対応。透明物とVFXに条件がある。性能表示の比較条件と同じ48視点シーンでの結果は未確認。既存HDRPへそのまま導入する候補ではない。 |
| Unity Single Pass Instanced | XRの左右2眼をインスタンス化し、同じシーン描画準備のCPU負荷を抑えるUnity機能。 | 2眼XR向けであり、48個の別視点を1回の同等描画に変える機能ではない。 |
| Unity XR Display Provider / DisplayXR | UnityのProvider経路は既存Camera姿勢から複数viewをRender Passへ渡す統合設計に使える。DisplayXR ADR-007は少数viewをXR Provider、多数viewをQuilt経路に分ける案をAcceptedとして記録。 | 通常のScene Objectを既存Cameraから表示する統合例として有望。ただしDisplayXR資料のQuilt経路は未実装で、HDRP 48視点の動作・性能実績ではない。Unity Single Pass Instanced自体は2眼向け。ViewWeaveでの採用・試作はユーザー方針により保留。 |
| DirectX 12 Split Graphics Jobs | Unityは特定構成でCPU latency最大40%低下を報告。Unity 6のDX12ではEditorもGraphics Jobs設定に対応する。 | 40%はFPS向上率でもViewWeaveの予測でもない。現プロジェクトのPlayerではGraphics Jobs有効だが、Editorの実効スレッドモードは未確認。Editor/Playerそれぞれの実効状態を先に測る。 |
| HDRP Shadow Update Mode | HDRP 17.6はライトのシャドウ更新を毎フレーム、On Enable、On Demandなどに制御できる。 | 同じsimulation状態でSpot/PointライトのshadowをQuiltごとに1回更新し、全視点で使えるか試験候補。動的遮蔽物を各frameで反映した上で、座標cullingと影の正しさを確認する。Directional cascadeに一般化しない。未検証。 |
| GPU Resident Drawer | HDRP/URPで対応RendererをGPU側で処理する機能。 | ViewWeaveのHDRP試験プロジェクトは既に有効。単にONにするだけの改善余地は残っていない。 |

Unity XR Providerは既存Cameraからview pose/projectionを受け渡す統合設計に使えます。Provider経路への変更は、OpenXR runtimeの自作を意味しません。XR culling pass共有APIもありますが、48-view union cullingの正しさや性能は別途検証が必要です。Unity XR Originはtracking空間を使う場合の座標補助であり、非追跡のライトフィールド表示に必須ではありません。現在のViewWeaveは専用cameraから48個のStandardRequestを送り、通常のMain Camera画像を拡大している方式ではありません。現在の実装・検証範囲、DisplayXRの公開設計、XR Provider試作順は[XR Display Provider調査](xr-provider-investigation.md)にまとめています。

Split Graphics JobsのCPU latency最大40%は[Unity 6の公式説明](https://unity.com/blog/unity-6-features-announcement)の値です。DX12でEditorが対応することは[DirectX 12改善資料](https://unity.com/blog/directx-12-improvements-in-unity-6)にあります。Editor Preferencesの設定とPlayer Settingsは別です。実効モードを読み取ってから、設定変更が意味を持つ環境だけ比較するのが安全です。今回の調査ではUnity APIから実効Graphics Threading Modeを取得する方法があることを確認していますが、ユーザーのEditorの実効値は未確認です。

## 研究として有望だが、そのまま統合できない例

| 研究 | 実測・実装 | ViewWeaveへの示唆と限界 |
| --- | --- | --- |
| Image-Based Spatio-Temporal Interpolation (HPG 2025) | Unity URPで元画像・深度等を取得し、補間部は公開C++/CUDA実装。RTX 4090で1440pの補間処理約2ms。 | 48視点全体の描画時間ではなく補間部分の値。ボリューメトリック照明とCausticsは対象外。深度合成の小規模試験の参考。 |
| CREAL STALF (2022) | Unity統合のライトフィールドHMD研究。1枚のカラー/深度画像から約100の近接視点を生成。 | 近眼・狭い視点間隔向けで、Looking Glassの広い視点範囲や表示品質へ数値を外挿できない。 |
| Light Field Display Point Rendering (ACM TOG 2024) | Unityではなく独自OpenGL。共有点群で48視点を描き、RTX 3070で非最適化MVR比2〜8倍、Sponzaで38.92msから12.81msと報告。 | 通常のURP/HDRPマテリアル/VFX/Volume描画を保つ手法ではない。描画方式を置き換える研究プロトタイプとしての比較対象。 |

- [HPG 2025論文](https://onlinelibrary.wiley.com/doi/full/10.1111/cgf.70215) / [公開コード](https://github.com/DerThomy/ImageBasedSpatioTemporalInterpolation)
- [CREAL STALF論文](https://sid.onlinelibrary.wiley.com/doi/full/10.1002/jsid.1112) / [技術資料](https://creal.com/app/uploads/2023/03/CREAL-light-field-rendering-2.pdf)
- [Light Field Display Point Rendering論文](https://arxiv.org/html/2601.19901v1)

## ViewWeaveでの実測と次の比較

ユーザーのHDRP VFX Learning Templatesで行った48視点測定は、Native設定でEditor約9fps、GPU時間約88ms、CPU submission約103msでした。CPU submissionは描画準備・描画・コピーの壁時計時間で、Render ThreadやGPU待ちを含み得ます。CPUだけが原因とは断定できません。品質解像度を下げてもFPSはほぼ変わらず、24/12視点へ減らすと速度は上がりました。測定条件と注意点は[HDRP VFX性能記録](../resumed-hdrp-vfx-performance.md)にあります。試験プロジェクトではGPU Resident Drawerが既にONでした。これはその試験設定での状態であり、全プロジェクトの保証ではありません。

優先する検証は次の順です。

1. Split Graphics Jobsの実効Editor/Playerモードを読み、既にsplitなら比較対象から外す。
2. 静的ローカルライトを使う最小シーンでHDRP Shadow Update Modeの結果・コストを比較する。
3. 機能を保つ範囲で、共有できる視点準備処理を2視点試作し、CPU/GPU/end-to-endを個別計測する。
4. 任意の近似モードとして、12/24視点の画像から48枚を深度合成する品質・速度比較を行う。これは真の48視点描画とは別の品質設定として扱う。

これらは検証候補であり、今回の調査だけで速度向上を実証したものではありません。視点合成は透明VFX、遮蔽境界、反射、ボリューメトリック効果を含むシーンで品質の確認が必要です。

参照: [Unity Single Pass Instanced](https://docs.unity3d.com/6000.0/Documentation/Manual/SinglePassInstancing.html)、[HDRP 17.6 Shadow Update Mode](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/shadow-update-mode.html)、[HDRP GPU Resident Drawer](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/manual/gpu-resident-drawer.html)。
