# Quilt Preview sample

BridgeなしでURPのQuilt RenderTextureをEditorから確認するsampleです。Unity 6000.6.4f1のURP projectへViewWeave URP配布packageをGit URLから追加し、`Window > Package Management > Package Manager`のSamplesから`Quilt Preview`をImportします。このsampleはURP専用です。配布packageにはCoreとURPが含まれます。

## 使い方

1. `GameObject > ViewWeave > Quilt Rig`を作成します。
2. Rigへ`QuiltPreviewDemoGenerator`をAdd Componentします。
3. `Preset=Preview`のまま、コンテキストメニュー`Generate Preview Rig`を実行します。
4. `Window > ViewWeave > Quilt Preview`を開きます。
5. Rigの`QuiltRenderer`をRenderer欄へdragし、`Render Quilt`を押します。

Quilt Preview windowにQuilt画像が表示されることが確認結果です。SceneViewに生成物が見えるだけでは表示確認になりません。生成物は`Preview Lit Cube`、`Preview Lit Sphere`、`Preview Directional Light`です。Bridge runtimeやLooking Glass本体は不要です。

`Balanced`または`Native`を選ぶ場合は`nativeQuiltWidth`、`nativeQuiltHeight`、`columns`、`rows`を実際のprofileに合わせます。Nativeは機器を自動検出しません。VFX Graphを使う場合はLinear color spaceを設定してください。
