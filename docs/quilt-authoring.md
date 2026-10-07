# Quiltのカメラ範囲とフォーカスを調整する

1. `QuiltRenderer`が付いたGameObjectを選択します。
2. Sceneビューで、紫の四角形がゼロ視差面、青緑がNear Clip、橙色がFar Clipです。
3. Inspectorの`Camera Framing`で`Focus Distance`、`Vertical Field Of View`、`Near Clip`、`Far Clip`を調整します。
4. Sceneビューでは、紫の面の中央にあるハンドルをカメラの前後方向へドラッグして`Focus Distance`を調整できます。

ゼロ視差面は、その距離にある被写体が表示面に重なるようにする基準面です。`Focus Distance`は立体視の視差を調整します。被写界深度（Depth of Field）、オートフォーカス、Volumeのフォーカス距離は変更しません。

Near/Farの範囲は`sourceCamera`のクリップ距離を使います。描画する画角と縦横比は`QuiltSettings`の`fieldOfView`と`viewAspect`を使います。カメラ自身のLens FOVだけを変えても、Quiltの画角は変わりません。ハンドルとInspectorの編集はUndoに対応し、Prefabインスタンスではオーバーライドとして記録されます。元カメラの位置・回転は動かしません。

Inspectorの`Show Near / Far`でクリップ面を表示／非表示にできます。クリップ距離が大きく、近くの範囲が見にくいときに切り替えてください。`Show Extreme Views`を有効にすると、左右端の視点のNear/Far面を表示します。これは設定した全視点範囲を示します。Half/Quarterの描画密度を選んだ場合も、ギズモは設定上の左右端を示します。

ギズモはSceneビュー上の調整補助です。表示機器のレンズ補正や視点割り当て、Volume効果、合成後の見え方まではプレビューしません。通常の透視カメラと有効な有限値の設定で表示されます。QuiltRendererはOrthographicカメラに対応しません。
