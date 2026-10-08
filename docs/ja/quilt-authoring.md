[English](../quilt-authoring.md)

# Quiltのカメラ範囲とフォーカスを調整する

1. `QuiltRenderer`が付いたGameObjectを選択します。
2. Sceneビューで、紫の四角形がゼロ視差面、青緑がNear Clip、橙色がFar Clipです。
3. Inspectorの`Camera Framing`で`Focus Distance`、`Vertical Field Of View`、`Near Clip`、`Far Clip`を調整します。
4. Sceneビューでは、紫の面の中央にあるハンドルをカメラの前後方向へドラッグして`Focus Distance`を調整できます。

ゼロ視差面は、その距離にある被写体が表示面に重なるようにする基準面です。`Focus Distance`（`Quilt Settings > Camera Distance`）は、実効フレーミングカメラのワールド位置からカメラ前方へ測るUnity unitsです。紫の面がその地点に置かれ、ギズモの線と距離ラベルで位置を確認できます。単位はUnity sceneのスケールに従うため、project側で1 unitを1mとしていない場合は実メートルとは一致しません。`Focus Distance`は立体視の視差を調整します。被写界深度（Depth of Field）、オートフォーカス、Volumeのフォーカス距離は変更しません。

Near/Farの範囲は専用`sourceCamera`のクリップ距離を使います。`Reference Camera`が設定されている場合、実行時の位置と向きはそのCameraのworld poseをQuilt描画前に専用カメラへ反映します。`Follow Reference Field Of View`が有効なら、`Vertical Field Of View`も各Quilt描画前に参照カメラから同期されます。したがってInspectorで入力したFOVは次の描画で上書きされます。無効にするとQuilt側で指定したFOVを使います。`viewAspect`、`Camera Distance`、`viewCone`、Near/Farは自動同期しません。元の参照カメラの設定やTransformは変更しません。

Bridge Rig作成メニューは、解決したframing Cameraを`Reference Camera`に設定します。CinemachineではVirtual Cameraではなく、Cinemachine Brainが出力する実Cameraを参照してください。標準の自動Quilt描画はscene rendering後に行われるため、通常はそのframeのBrain出力を読み取ります。録画は開始時にQuilt設定を固定し、録画中にFOVや他のQuilt設定が変わると停止します。動く参照カメラで連番録画する場合も、FOVとQuilt geometryは録画中固定にしてください。

Inspectorの`Show Near / Far`でクリップ面を表示／非表示にできます。クリップ距離が大きく、近くの範囲が見にくいときに切り替えてください。`Show Extreme Views`を有効にすると、左右端の視点のNear/Far面を表示します。これは設定した全視点範囲を示します。Half/Quarterの描画密度を選んだ場合も、ギズモは設定上の左右端を示します。

## 明示的なカメラ制作操作

`Camera Authoring`には、操作対象の`Effective Camera`と`Effective Camera Transform`が表示されます。`Reference Camera`が設定されていれば参照カメラが対象で、未設定なら`Source Camera`が対象です。参照カメラのTransformはQuilt描画前にSource Cameraへコピーされるため、手動操作も実際のフレーミングカメラに対して行います。

`Set Focus From Selected Object Center`を使うには、先にQuiltRendererのInspectorをロックし、その後でシーン内のGameObjectを1つ選択します。ロックしたInspectorのボタンを押すと、Renderer boundsの中心、RendererがなければCollider boundsの中心をカメラ前方軸へ投影し、その距離をzero-parallaxの`Focus Distance`へ設定します。どちらのboundsもない場合はTransform位置を使います。カメラ後方や無効な値は拒否します。設定を変えるだけでCameraの位置・向きは変わりません。これは被写界深度のfocusではありません。

`Frame Focus Plane Center in Scene View`は、Scene Viewの表示をzero-parallax面中央へ移動します。`Horizontal Orbit`または`Vertical Orbit`へ角度を入力し対応するApplyボタンを押すと、Effective Cameraの位置と向きをzero-parallax点をpivotとして一緒に回転します。pivot位置は維持され、水平角は±180度、垂直角は±89度に制限されます。これらの手動操作はUndoに記録され、Prefab instance上の変更も記録します。

`Show 3D Focus Cursor`は既定で無効です。有効にすると従来の前後スライダーの代わりにfocus面中央へPosition Handleを表示します。ドラッグ位置をカメラ前方へ投影して距離だけを更新するため、横方向の移動は適用されずfocus面中央へ戻ります。CameraのTransformは変更しません。

Camera authoring操作はPlay Mode中とPlay開始遷移中は無効です。CinemachineやAnimation previewが参照カメラを駆動している場合、後続更新で手動Transformを上書きすることがあるため、停止した状態で編集してください。複数の`QuiltRenderer`選択中はこれらの操作を表示・実行しません。

ギズモはSceneビュー上の調整補助です。参照カメラが設定されていれば、その現在のworld poseと追従FOVを使って面を表示します。Edit Modeの表示更新ではカメラや設定値を書き換えません。表示機器のレンズ補正や視点割り当て、Volume効果、合成後の見え方まではプレビューしません。通常の透視カメラと有効な有限値の設定で表示されます。QuiltRendererはOrthographicカメラに対応しません。
