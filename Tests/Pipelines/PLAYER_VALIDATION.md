# URP Player GRD validation

> Audience: Unity and rendering developers. This is a bounded validation procedure; results apply only to the stated setup and run. For current support and user setup, see [Compatibility](../../docs/compatibility.md) and [Getting started](../../docs/getting-started.md). Browse the [Technical Guide](../../docs/technical-guide.md) or [User Guide](../../docs/user-guide.md).

`PlayerValidationBuilder.Build()` creates a generated scene and a Development Windows Player without a Bridge or device display. The scene contains serialized URP Lit material and mesh references. BRG KeepAll retains the required BRG shader variants; serialized material and mesh references are retained separately by the generated scene. The build settings enable GRD so its resources are not stripped from the Player validation build.

The Player runs URP with GPU Resident Drawer disabled and enabled at 1, 8, and 48 views, waits ten frames before each mutation, checks green sphere tile centroids and `mutationFrame == output.LastRenderedFrame`, records GRD counters, and checks automatic rendering stop/restart. It writes per-case JSON, before/after PNG files, and `summary.json`; exit code 0 means all checks passed.

From the repository root, build into gitignored local artifacts with:

```powershell
unity build ./Tests/Pipelines/Unity --target StandaloneWindows64 --execute-method ViewWeave.PlayerValidation.Editor.PlayerValidationBuilder.Build --output-path "$PWD/.local/grd-player-validation/build/ViewWeaveValidation.exe" --editor-version 6000.6.4f1 --allow-dirty-build --timeout 900 --log-file "$PWD/.local/grd-player-validation/build.log" --no-tail --format json --args '-force-d3d11'
```

Run the Player normally, without `-batchmode`, `-force-gfx-direct`, or `-nographics`, so threaded graphics are selected:

```powershell
& './.local/grd-player-validation/build/ViewWeaveValidation.exe' -force-d3d11 -screen-fullscreen 0 --evidence-dir "$PWD/.local/grd-player-validation/run" -logFile "$PWD/.local/grd-player-validation/player.log"
```

The current Windows D3D11 Development Player run passed URP GRD off/on at 1, 8, and 48 views with normal threaded graphics. HDRP Player, performance, Bridge normal-thread timing, and Metal remain unverified. A RenderTexture release warning remains during Windows Player shutdown; its cause is under investigation, and the ownership fix is not claimed to have caused or resolved it. The generated evidence is gitignored local artifact data and can be regenerated without modifying the validation scenes.
