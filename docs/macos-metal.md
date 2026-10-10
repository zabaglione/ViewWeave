[日本語](ja/macos-metal.md)

# macOS Metal / Looking Glass Go

Use Unity **6000.6.4f1**, URP **17.6**, an Apple Silicon Mac, and an installed Looking Glass Bridge runtime. The bundled arm64 plugin requires **macOS 26.0 or later**. HDRP has not been runtime-verified on macOS. Connect the Go in Desktop Mode, extend the desktop at its native **1440 × 2560** resolution, and run Bridge.

Select the scene camera and choose **GameObject > ViewWeave > Bridge Rig**. On macOS this creates a `BridgeMetalQuiltOutput` instead of the Windows Worker Session. Enter Play mode to start output; the Metal component's Inspector provides Start/Stop controls. A blank runtime path discovers the newest installed Bridge in `/Applications` or `~/Applications`. An explicit dylib, its directory, or the Bridge `.app` and `LKG_BRIDGE_PATH` are also accepted.

The initial implementation accepts Go device identifiers (`LKG-E`), unrotated native screens, conventional RGB calibration with no subpixel cell table, and zero cell-pattern mode. Other devices and calibration patterns fail with an error. Choose Preview, Balanced, or Native before starting. The full grid is retained at every quality level, including the Go's 66-view grid when returned by Bridge. Rendering 66 views can be expensive; start with Preview.

## Native plugin build

Run `python3 tools/build_macos_plugin.py --headers /absolute/path/to/public/UnityPluginAPI/headers` with public Unity NativeRenderingPlugin headers. Without `--headers`, the tool obtains pinned upstream headers into its ignored build directory. It builds the repository-owned shim, runs native CTests, and installs the dylib/importer metadata and provenance to the Bridge package. The dylib is replaced atomically to avoid stale macOS code-signing pages; restart Unity after updating it to load the new native version. `--universal` also compiles the Intel slice; Intel runtime execution is not implied by a successful build. Official Bridge libraries and headers are never bundled. Run `python3 tools/build_upm_distribution.py` after changing the source package to regenerate both self-contained distributions.

The verification project is `Tests/Metal/Unity`. `MacOSMetalValidationBuilder.Build` creates `.local/macos-validation/ViewWeaveMetal.app`; `Prepare` creates its Editor scene. The application rotates its sample geometry and records frame counters. R stops/restarts output, and Escape stops and exits. Generated scenes, Players, and detailed logs are verification artifacts rather than package content.

## Rendering and lifetime

Bridge supplies metadata and calibration, then its SDK is uninitialized. The display window and optical sampling are owned by ViewWeave. Unity renders/interlaces on Metal; a Unity render event copies the result into one private Metal texture. Unity producer completion schedules the presentation queue. Completion of ViewWeave's own consumer command buffer permits the next copy. Busy frames are dropped; no image is read back to the CPU during streaming. Teardown uses a one-pixel queue-tail readback to prove Unity completion before releasing the texture, together with native completion.

Stop immediately closes admission and hides the window. GPU retirement can continue afterward; `IsClosing` distinguishes this from full retirement. A new session waits for the previous session to retire. Pending event data and GPU callbacks retain their own session and textures. AppKit objects are released on the main thread. Screen disconnect, movement, or resolution changes stop output rather than continuing with stale pixel placement. Reconnect and start again after restoring the display arrangement.

The Windows `ViewWeaveBridgeSession`, playback, and Edit Mode device preview remain Windows-specific. Shared, Metal and Windows responsibilities are described in [Platform boundaries](bridge-platform-architecture.md). The earlier finite `BridgeMetalExperimentalOutput` remains a separate diagnostic route. It must not share a process with this output after beginning its experiment; restart Unity between routes.

## Editor assertion when stopping Play

`Mismatched state in DefaultScenario, was Stopping but expected Idle` is a known Unity issue when script changes made during Play are recompiled on stopping. [UUM-154172](https://issuetracker.unity.com/issues/25291/mismatch-state-in-defaultscenario-error-is-thrown-when-exiting-play-mode-with-a-modified-script-and-recompile-after-finished-playing-enabled) lists 6000.6.4f1 as affected. Stop Play before changing source, then wait for imports and compilation to finish before starting again.

Unity's documented workaround is Unity Settings > General > Script Changes While Playing > `Recompile And Continue Playing`. This Editor preference affects other projects and allows recompilation during Play; change it only if that behavior is desired.

## Optical calculation and provenance

The interlace shader is an original implementation, not an import or port of the HoloPlay shader. It computes the lenticular phase for each RGB subpixel, selects a Quilt view, and samples that view. The calibration conversion is the mathematical relationship in the Apache-2.0 [official WebXR configuration](https://github.com/Looking-Glass/looking-glass-webxr/blob/b44ed27fea197f28fbe4cb669bc350d141a9f8b1/src/LookingGlassConfig.ts):

`shaderPitch = rawPitch × screenWidth / dpi × cos(atan(1 / rawSlope))`

`tilt = screenHeight / (screenWidth × rawSlope)`; `RGB step = 1 / (3 × screenWidth)`.

The [Bridge native reference](https://lfdocs.lookingglassfactory.com/software/looking-glass-bridge-sdk/native-function-reference) and the installed runtime's public declarations provide the metadata ABI. Unity's [public native Metal interface](https://github.com/Unity-Technologies/NativeRenderingPlugin/blob/522254181faf188efa8b50c3e3bf6fce720b26e4/PluginSource/source/Unity/IUnityGraphicsMetal.h) supplies the producer command buffer. GPU/unit/build proof, screen capture, and physical optical acceptance are different evidence; consult the [verification record](macos-metal-validation-20261010.md) for the current result.
