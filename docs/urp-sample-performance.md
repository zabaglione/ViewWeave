# URP Sample performance and integration evaluation

## Result

The four URP Sample scenes rendered 7680x4320 8x6 quilts at Full48, Half24, and Quarter12. In the selected sample camera configurations, Full48 measured 54-120 ms per game frame, Half24 31-48 ms, and Quarter12 12-25 ms. Oasis was the heaviest measured scene. Reducing view count lowered render time substantially, but Quarter12 still did not reach a stable 60 fps in every scene.

These are Unity Editor, D3D12, render-only measurements. The performance run used ViewWeave URP commit 62bb05a3226b80bfab97bb24ff40bcee467c80f7; it did not initialize Bridge or send frames to a display. Bridge and detached-Cinemachine checks were run later with commit 044e911940fcba4b2dadfaac65ddd83be5f4f5a4 and are reported separately.

## Performance test setup

- Project: E:/URPSample_6.6.41f1, Unity 6000.6.4f1, Windows Editor.
- Graphics: D3D12, NVIDIA GeForce RTX 3090 Ti (24 GB), AMD Ryzen 9 5950X.
- Quality: sample PC High; render scale 1.0, MSAA 1x, HDR enabled. VFX, scene animation, renderer features, and volumes stayed enabled.
- Package: ViewWeave URP commit 62bb05a…. The manifest requested URP 17.7.0, but Unity resolved URP 17.6.0. The measured package combination is therefore ViewWeave 62bb05a… plus URP 17.6.0.
- Quilt: synthetic 7680x4320, 8x6, cone 40 degrees. No cached physical display profile was used. The evaluation selected each scene's camera distance for framing; these are test configurations, not a guarantee that every scene feature or effect works through every camera path. The source projection aspect and quilt view aspect were both 0.6139456. A stored tile is 960x720 pixels (4:3); that is the quilt's pixel storage shape, not the projection aspect. The ordinary Game View was 361x588, while the full quilt was 7680x4320.
- Each scene ran off_before → full48 → off_after → half24 → quarter12 → full48_repeat. Each phase used 60 warm-up frames, discarded 2 phase-boundary frames, then measured 120 successive Unity game frames. Simulation advanced by Time.captureDeltaTime=1/60; wall time was measured independently. A Unity-frame gap invalidated a run.
- The harness disabled Cinemachine/player camera drivers after caching the camera pose and FOV. It made a temporary camera at the source camera's local zero and copied the source URP camera data, tag, clip planes, culling mask, and clear flags. The copied MainCamera tag is an evaluation-only workaround for this template's callback assumptions; it was not added to the SDK.
- Each timed phase verified its view count and end-camera callback count (48/24/12), frame continuity, camera drift, and automatic-render error. PNG readback and tile audit ran outside the timed samples.

## Measurements

Values are wall-frame milliseconds, with nearest-rank p50 and p95. GPU and CPU submission columns are p50 milliseconds. CPU wall-frame and submission times can include GPU waits; the submission number is not an isolated CPU-only cost. Off is the range bracketed by off_before and off_after. The FPS estimate is 1000 / wall p50; it reflects the fixed-simulation test and is not a real-time Unity FPS setting.

| Scene | Distance | Mode | Wall p50 / p95 (ms) | Approx. wall FPS at p50 | GPU p50 (ms) | CPU submission p50 (ms) |
|---|---:|---|---:|---:|---:|---:|
| Terminal | 10 | Off | 6.41–8.70 / 9.13–12.01 | 115–156 | 0.59–1.08 | — |
| Terminal | 10 | Full48 | 85.82 / 112.16 | 11.7 | 69.83 | 77.43 |
| Terminal | 10 | Half24 | 44.40 / 56.59 | 22.5 | 36.88 | 36.16 |
| Terminal | 10 | Quarter12 | 25.04 / 32.26 | 39.9 | 15.14 | 17.73 |
| Garden | 1.5 | Off | 4.70–4.88 / 5.80–7.33 | 205–213 | 0.24–0.56 | — |
| Garden | 1.5 | Full48 | 68.69 / 85.22 | 14.6 | 54.85 | 62.63 |
| Garden | 1.5 | Half24 | 37.57 / 44.53 | 26.6 | 23.76 | 28.75 |
| Garden | 1.5 | Quarter12 | 19.96 / 27.41 | 50.1 | 15.96 | 13.14 |
| Cockpit | 1.5 | Off | 6.07–6.79 / 7.93–12.32 | 147–165 | 0.61–1.02 | — |
| Cockpit | 1.5 | Full48 | 53.58 / 57.87 | 18.7 | 44.36 | 46.40 |
| Cockpit | 1.5 | Half24 | 30.86 / 35.37 | 32.4 | 14.60 | 23.53 |
| Cockpit | 1.5 | Quarter12 | 11.95 / 14.50 | 83.7 | 2.86 | 6.45 |
| Oasis | 1.5 | Off | 6.41–6.92 / 9.12–9.46 | 145–156 | 0.86–0.92 | — |
| Oasis | 1.5 | Full48 | 120.02 / 165.36 | 8.3 | 108.00 | 112.46 |
| Oasis | 1.5 | Half24 | 47.59 / 59.45 | 21.0 | 44.85 | 40.01 |
| Oasis | 1.5 | Quarter12 | 25.38 / 35.04 | 39.4 | 21.25 | 18.37 |

Garden's initial 10-unit camera run clipped the room and is retained only as a framing diagnostic. It is not compared causally with Garden at 1.5: camera pose, FOV, timeline start, and visible geometry were not identical. Full48 repeat was similar in Garden but differed in Cockpit (53.58 vs 29.47 ms p50) and Oasis (120.02 vs 99.61 ms p50). Their timelines and/or VFX evolve during sequential phases, so those values describe a workload range rather than a controlled mode speedup. Terminal was static. Scene-to-scene values also reflect different content and framing.

GPU p50 is high in every Full48 phase; CPU submission p50 is also tens to over 100 ms. Both costs are material, but these measurements do not isolate CPU execution from GPU waits. The latest-view CPU sample alone cannot show how work is distributed across the 48 views; that requires per-view profiling.

## Scene and feature gates

All four standalone scene gates passed their basic render checks: 48/48 tiles contained sampled non-black pixels, sampled magenta was zero, expected render callbacks/views were observed, camera drift was zero, and the SDK automatic-render error was empty. This confirms quilt generation for those selected standalone camera configurations, not all features in every integration path.

- **Terminal:** The static sample environment appeared in the 48-tile image. An initial gate logged 384 NullReferenceExceptions because SceneTransitionManager.OnBeginCameraRendering assumed an Untagged render camera was the ScreenCamera and dereferenced OffsetCamera. The evaluation camera copied the source tag for that gate; no SDK tag workaround was added. One off_after CSV row retained a stale rendered-view count while callbacks were zero; it is not another 48-view render.
- **Garden:** At 1.5 units the room interior was visible without the near-wall clipping in the 10-unit diagnostic. A Cinematic Timeline was present; the measured log captured it advancing. Active VFX count was zero.
- **Cockpit:** Renderer index 2 was selected. The 3D skybox showed stars, ships, and horizon in the quilt; the image did not show enough cockpit interior to certify all scene content. The Director advanced during the measurement.
- **Oasis standalone:** The standalone capture showed vegetation, terrain, and fog; the timed run counted 19 active VFX and recorded the Cinematic Timeline PlayableDirector. This direct single-scene result does not certify Oasis through Terminal's additive scene-transition camera path.

### Template multi-camera callback integration check

The template's camera callbacks made assumptions that did not hold for a dedicated quilt render camera, so two sample files were adapted:

- Assets/SharedAssets/Scripts/Runtime/SceneTransitionManager.cs resolves a quilt render camera only when its own QuiltRenderer names itself as sourceCamera and its referenceCamera is the template's MainCamera or ScreenCamera. Main-camera identity and scene lighting use that reference. OffsetCamera is called only for the actual ScreenCamera.
- Assets/Scenes/Oasis/Scripts/Runtime/OasisFog.cs retains its terminal/non-main-camera filter but applies it to the resolved reference camera.

These are template-specific changes; the SDK runtime package was not changed for them. Compilation passed. The no-reference case leaves the render camera unchanged without throwing, and the implementation never offsets the quilt source pose.

A follow-up used the registered Oasis SceneLoader and the normal Terminal portal path. Before Play, the FlythroughDirector was paused and idle flythrough was disabled; at runtime the Player was aligned to the Oasis loader ReferencePoint, then SceneTransitionManager.StartTransition() was called once. The transition later reached currentScene=OasisScene and ActiveScene=OasisScene. The immediate post-teleport frame was not captured. At a later observation with timeScale still 1, Player and MainCamera had fallen far below the Oasis scene and the Main Game View showed only a flat sky/sun gradient. That image is not evidence of how a correctly positioned post-transition camera renders, and the fall cause remains undetermined.

To test a valid Oasis camera pose without changing the Scene asset, the run was frozen with timeScale=0; CharacterController, FirstPersonController, and CinemachineBrain were disabled for the runtime-only probe; then Player was moved to the Oasis SpawnTransform. MainCamera followed the Player to the spawn viewpoint and rendered Oasis rocks, foliage, and terrain. A temporary disabled source camera referenced MainCamera with matching position and rotation, FOV 60, aspect 0.6139456, near/far 0.2/3000, and culling mask 133177151. With QuiltRenderer.autoRender enabled through normal Editor frame scheduling, the Quilt completed 48 views at revision 1404 with LastAutoRenderError empty. The full quilt and corrected adjacent views 23 and 24 contain the same Oasis scene content as Main. View 23/24 are neighboring views, not an exact optical center view. The Quilt capture used cameraDistance=1.5 and viewCone=50 degrees; this differs from the synthetic benchmark's viewCone=40 degrees. It is a render-only check and does not claim Bridge transport or immediate post-teleport behavior.

A direct CLI eval call to QuiltRenderer.RenderQuilt() failed with System.InvalidOperationException: "RenderQuilt must be called outside render callbacks" at QuiltRenderer.cs:220. This was an invocation-context constraint, not a content/render failure; the method produced zero views. The successful capture used the normal autoRender frame path instead. In the earlier arbitrary-position API-transition test, matching only the source culling mask to MainCamera did not improve the flat quilt; its cause was not established. Separately, the follow-up above observed Player/MainCamera far below the Oasis scene later in the run and saved `main-after-unpositioned.png`. That later fallen-camera observation does not explain the earlier flat quilt or establish the immediate post-transition result. The transition-path result before spawn correction remains unknown. A separate image from the standalone Oasis scene must not be taken as proof of additive transition behavior.

One earlier manual test called SceneTransitionManager.Teleport() while the Timeline-driven MediaSceneLoader transition was already pending. That invalid mixed-path attempt produced a NullReferenceException in the sample's CinemachineTeleport at SceneTransitionManager.cs:300. It was excluded. The controlled loader test began from m_MediaSceneLoader=null, InTransition=false.

## Bridge and detached Cinemachine check

After performance runs, the URP project package was updated to ViewWeave commit 044e911940fcba4b2dadfaac65ddd83be5f4f5a4 and Unity Editor was restarted after the native worker initially returned render-event ID -2. A transient Unity SceneView ArgumentNullException was logged during package removal; after restart the new Editor Console had zero errors and the Bridge test initialized successfully.

The Garden scene now has a root-level ViewWeave Bridge Rig saved with Reference Camera MainCamera, FOV follow enabled, and focus distance 1.5. Its dedicated source camera is disabled for ordinary Game View rendering; the Rig object itself is not parented to MainCamera. The explicit detached condition was verified: sourceCamera.transform.IsChildOf(referenceCamera.transform) == false. After switching the active Cinemachine virtual camera to Pond Start and changing its FOV to 72, MainCamera, source camera, and quilt settings converged to FOV 72 and identical position/rotation (0 position delta, 0 rotation delta). Bridge stayed Rendering, frame counters advanced, and dropped frames stayed at zero. The user confirmed that the Garden room appeared on the Looking Glass. This is the physical display check; the synthetic performance measurements above did not use the display profile.

The Bridge reported a device profile of 3840x2160, quilt 7680x4320, grid 8x6, aspect 1.77778, and view cone 50 degrees. Those physical display settings differ from the synthetic benchmark's cone 40 degrees and projection aspect 0.6139456, so the Bridge check is not an extra performance result.

## Evidence and reproduction

The full performance PNG, .image-audit.json, .csv, .summary.csv, and .status.txt files are under .local/urp-sample-evaluation/results/. Representative images are [Terminal gate](../.local/urp-sample-evaluation/results/TerminalScene_20261007_024902.png), [Garden focus 1.5](../.local/urp-sample-evaluation/results/GardenScene_20261007_032041_preview.jpg), [Cockpit focus 1.5](../.local/urp-sample-evaluation/results/CockpitScene_20261007_031505.png), and [Oasis standalone focus 1.5](../.local/urp-sample-evaluation/results/OasisScene_20261007_031742_preview.jpg). The additive callback images before/after the mask-only change are [before](../.local/urp-sample-evaluation/results/terminal-to-oasis-mask-before.png) and [after](../.local/urp-sample-evaluation/results/terminal-to-oasis-mask-after.png); the earlier callback gate is recorded in [terminal-to-oasis-callback-gate.txt](../.local/urp-sample-evaluation/results/terminal-to-oasis-callback-gate.txt). The spawn-corrected transition comparison, captured Main images, adjacent quilt tiles, camera coordinates, and crop mapping are in [oasis-transition-20261007](../.local/urp-sample-evaluation/results/oasis-transition-20261007/): [Main before](../.local/urp-sample-evaluation/results/oasis-transition-20261007/main-before.png), [Main at the fallen position](../.local/urp-sample-evaluation/results/oasis-transition-20261007/main-after-unpositioned.png), [Main at Spawn](../.local/urp-sample-evaluation/results/oasis-transition-20261007/main-after-spawn-correction.png), [Quilt view 23](../.local/urp-sample-evaluation/results/oasis-transition-20261007/quilt-tile-23-after-spawn-correction.png), [Quilt view 24](../.local/urp-sample-evaluation/results/oasis-transition-20261007/quilt-tile-24-after-spawn-correction.png), and [comparison record](../.local/urp-sample-evaluation/results/oasis-transition-20261007/comparison-summary.txt). The 51 MB full quilt is retained in the sample project's local diagnostic folder rather than duplicated into the repository; a small 8x6 [quilt preview](../.local/urp-sample-evaluation/results/oasis-transition-20261007/quilt-preview.png) is included.

The evaluation source is [UrpSamplePerformanceHarness.cs](../tools/urp-sample-evaluation/UrpSamplePerformanceHarness.cs). Copy it into Assets/Editor/UrpSampleEvaluation/ in the URP Sample project and compile. In Edit Mode, open one of TerminalScene, GardenScene, CockpitScene, or OasisScene. Before entering Play Mode, set the saved Garden Rig's `ViewWeaveBridgeSession.startOnEnable` to false (or deactivate the entire Bridge Rig GameObject), and set `autoRender` to false on every `QuiltRenderer`, including the saved Garden Rig. This prevents an automatically started Bridge session or duplicate quilt rendering from contaminating the render-only run. Enter Play Mode, confirm the Bridge session and worker are idle, then wait until scene initialization is complete and the ordinary MainCamera image is visible; confirm no other quilt renderer is active. Run the gate, inspect its PNG/audit, and only then collect timings:

~~~csharp
UrpSamplePerformanceHarness.StartGate("E:/LookingGlassSDK/.local/urp-sample-evaluation/results", 1.5f);
UrpSamplePerformanceHarness.Start("E:/LookingGlassSDK/.local/urp-sample-evaluation/results", 1.5f);
~~~

Use cameraDistance=10f for the original Terminal run. The harness defaults to 10 only for backward compatibility and never reads a physical display profile. It restores capture time, time scale, quality, camera-driver states, event subscriptions, and its temporary camera after completion or start failure. A forced start-failure restoration probe passed; see [the probe record](../.local/urp-sample-evaluation/results/harness-start-failure-restore.txt). Terminal has a SceneTransitionManager, while standalone Garden was evaluated without one; the sample camera paths therefore differ. Absolute scene-to-scene timings are not strict comparisons, and camera pose/configuration must match the recorded test before attempting a close reproduction. Once the render-only evaluation is complete, restore the Rig's startup setting and the intended autoRender configuration before testing Bridge output.

The raw .csv contains frame rows and additional summary rows; select only the exact six frame phase names for raw-frame analysis. .summary.csv has its own schema, with _p50 and _p95 suffixes denoting nearest-rank percentiles. Its legacy wall_p50_ms and wall_p95_ms fields contain the same wall percentile on each row; do not read the p50 row's wall_p95_ms as a p95 result. Missing GPU samples are identified by valid/unique counts and are N/A, not zero-cost work. Bridge transport counters are intentionally absent from these render-only runs.
