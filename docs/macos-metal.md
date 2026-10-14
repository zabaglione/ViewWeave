[日本語](ja/macos-metal.md)

# macOS Metal output

[User Guide](user-guide.md) · [Technical Guide](technical-guide.md)

## User setup and operation

Use Unity **6000.6.4f1**, URP or HDRP **17.6**, an Apple Silicon Mac, and an installed Looking Glass Bridge runtime. The bundled arm64 plugin requires **macOS 26.0 or later**. ViewWeave uses a dedicated calibration-driven Metal output path. Connect the display as an unrotated, native-resolution extended desktop and run Bridge. Model identifiers are not used as an allowlist, but this does not establish support for every device. Consult [Compatibility](compatibility.md) and the [current calibration validation record](development/macos-unified-subpixel-validation-20261011.md) for tested scope and limits.

Select the scene camera and choose **GameObject > ViewWeave > Bridge Rig**. On macOS this creates a `BridgeMetalQuiltOutput` backend. Configure the rig through the **ViewWeave Rig** component: **Rendering > Output Quality** selects Preview, Balanced, or Native; **Bridge** provides startup, runtime path, application name, display ordinal, output status, and Start/Stop controls. Enter Play mode to start output. Stop output and wait for GPU retirement before changing quality or transport, then start it again. A blank runtime path discovers the newest installed Bridge in `/Applications` or `~/Applications`. An explicit dylib, its directory, or the Bridge `.app` and `LKG_BRIDGE_PATH` are also accepted.

Camera, reference camera, focus, orbit, physical framing, Quilt Capture, and Video Export use the shared Rig frontend. While connected in Play mode, **Use Connected Display Size** copies the validated physical calibration into the aperture fields without enabling physical framing automatically. Physical 1:1 framing requires the aperture aspect to match the display. Stopping Metal output cancels an active or paused recording on that renderer before restoring its local configuration. The Metal component's Inspector is for technical diagnostics.

In Edit Mode, open **Preview** on ViewWeave Rig and choose **Start Local Edit Mode Preview** or **Start Looking Glass Device Preview**. Local preview uses the Rig's local Quilt profile; device preview uses Bridge's validated display profile and **Output Quality**. **Pause Preview** holds the last image, **Resume Preview** reuses the producer, and **Close Preview** stops it. Close and wait for GPU retirement before changing quality or transport. A private producer preserves the source Rig and camera settings. Entering Play, reloading scripts, closing its Authoring window, or changing the window's Rig stops preview.

For saved Quilts, enter Play and start Metal output, then choose **Capture & Playback > Playback**. Select a full Quilt PNG or completed PNG recording folder, validate it, and use Play / Pause / Resume / Stop. Its dimensions, grid, aspect, and available camera geometry must match the active output. Playback pauses normal Quilt rendering; normal Stop or sequence completion restores it only while the same output and Rig configuration remain valid. Output Stop, Play exit, and script reload invalidate playback and retain GPU resources until retirement proof completes. The scene stores the attached backend: moving a scene between macOS and Windows does not automatically replace its output component.

Device selection uses Bridge geometry and calibration. Standard RGB/BGR order and a supported cell format are required. See the [calibration contract](development/bridge-subpixel-calibration.md) for accepted modes, cell counts, and flip conditions. Model-independent selection does not guarantee optical quality on untested displays.

## Subpixel processing

Choose **Subpixel Processing** in the ViewWeave Rig Inspector after stopping output/device preview and waiting for GPU retirement. **Automatic** uses the Bridge calibration and its cell table when present. **ClassicRgb** explicitly selects evenly spaced RGB sampling using the optical calibration, for diagnostics. **CalibratedCells** requires a Bridge cell table and rejects a tableless display such as Go. Pattern mode and XY offsets always come from Bridge.

The `BridgeSubpixelMode` contract is shared by Windows and macOS. Windows Worker applies all three choices through its Native interlacer; Legacy Direct accepts Automatic only. Both platforms reject changes while output is active. Consult the per-platform records for device and optical validation.

## Editor assertion when stopping Play

`Mismatched state in DefaultScenario, was Stopping but expected Idle` is a known Unity issue when script changes made during Play are recompiled on stopping. [UUM-154172](https://issuetracker.unity.com/issues/25291/mismatch-state-in-defaultscenario-error-is-thrown-when-exiting-play-mode-with-a-modified-script-and-recompile-after-finished-playing-enabled) lists 6000.6.4f1 as affected. Stop Play before changing source, then wait for imports and compilation to finish before starting again.

Unity's documented workaround is Unity Settings > General > Script Changes While Playing > `Recompile And Continue Playing`. This Editor preference affects other projects and allows recompilation during Play; change it only if that behavior is desired.

## Distribution and validation scope

The bundled shim is ad hoc signed. Developer ID signing, notarization, and end-user Gatekeeper acceptance are unverified. Audio recording is unsupported. Use [Compatibility](compatibility.md) and the [feature/platform matrix](ja/unity-feature-platform-matrix.html) for checked conditions; see the [implementation notes](development/macos-metal-internals.md) for builds, calibration math, and GPU lifetime.
