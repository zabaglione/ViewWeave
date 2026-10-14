# HDRP view histories and shared exposure validation — 2026-10-10

> Audience: rendering developers. This record covers the lighting/history change set following `2630bf421b02aaf470e421992fc23d2947b82140`, Unity 6000.6.4f1, HDRP 17.6, and macOS Metal. It does not establish Windows support for the new path. For operation, see the [User Guide](../user-guide.md); for current limits, see [Compatibility](../compatibility.md).

## Cause and implementation

The HDRP template plant case was bright in Game View but dark on Looking Glass Go. The previous common HDRP adapter replaced non-Fixed exposure with EV 12, disabled volumetric fog, disabled TAA, and suppressed Motion Blur, SSR, SSGI, and volumetric clouds. These were SDK rendering changes, rather than evidence of a Metal transport failure. Copying only the reference camera's background also left other HDRP camera settings behind.

The new default, `IsolatedViewHistories`, retains one disabled Camera and persistent HDRP history per rendered logical view. The camera pool survives successive Quilts, preserves surviving logical-view identities when sampling changes, and resets when the required render dimensions or pipeline change. The adapter copies the Reference Camera's HDRP settings using public `HDAdditionalCameraData.CopyTo`. Core owns the render-session contract and camera/state restoration; HDRP owns history and exposure resources; Metal and Windows retain their separate output transports. This does not change assembly boundaries or native binaries. See the [public HDRP camera API](https://docs.unity3d.com/Packages/com.unity.render-pipelines.high-definition@17.6/api/UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData.html).

`FollowScene` measures the central view with a persistent HDRP camera and a scoped public Custom Pass before post processing. A shader samples HDRP's exposure multiplier, then an owned asynchronous GPU readback supplies a compensated EV shared by all Quilt views. An authored Fixed exposure uses `fixedExposure - compensation`; the temporary per-view override sets compensation to zero. The initial neutral exposure sample is discarded, and no Quilt is submitted while metering warms up. Exposure conversion uses the [public ColorUtils API](https://docs.unity3d.com/Packages/com.unity.render-pipelines.core@17.6/api/UnityEngine.Rendering.ColorUtils.html). No private C# exposure API or reflection is used.

The temporary exposure Volume affects only the current view camera and is restored afterward. Screen Space Camera canvases assigned to the source or reference camera are scoped to the current view camera and restored afterward. Stopping output, disabling the renderer, or an automatic-render failure explicitly releases the history pool. Exposure resources retire after owned readback completion; Editor assembly reload waits only for the owned requests. An uncertain GPU completion retains its ownership record instead of immediately destroying potentially in-use resources.

## Explicit settings and remaining adaptations

All user controls remain under **ViewWeave Rig > Rendering > HDRP Rendering**.

| Setting or path | Current behavior |
| --- | --- |
| Isolated View Histories / Follow Scene | Default. Keeps authored HDRP effect settings and shares central measured exposure. |
| Manual Fixed | Explicit exposure override. EV 12 is a selectable default value, not a fallback in Follow Scene. |
| Shared Camera Compatibility | Explicit legacy option. Disables TAA; adapts Motion Blur, SSR, SSGI, and clouds; volumetric fog uses Gaussian filtering. Its non-Fixed exposure fallback is separately selectable. This changes scene appearance. |
| Full / Half / Quarter | Explicit view-count tradeoff. Reduced modes reuse the nearest rendered view for missing tiles and can produce larger changes when moving sideways. |
| Output Quality | Explicit per-view resolution tradeoff. |
| URP temporal effects and camera stacking | The existing URP path still disables TAA, rejects Motion Blur in strict validation, and rejects camera stacking. The HDRP changes do not resolve these URP limits. |
| Looking Glass Go Metal transport | Existing Go calibration/layout constraints remain; other models and rotated layouts are not established by this validation. |

## Automated validation

The final PlayMode batch ran with Unity 6000.6.4f1 and `-force-metal` in the project-owned pipeline fixture. After the original 50/50 history/graphics pass, two macOS URP TAA checks were added. `ViewWeave.GraphicsCompatibility` passed **52/52**, with **zero skips**, from 2026-10-10 14:25:45 to 14:26:30 UTC. The final compilation log contained no CS0618 warnings or C# compilation errors. This includes bounded URP regression coverage and the following HDRP checks:

- Separate persistent view cameras, stable identities, Quarter pruning, resize resets, and camera/render-target/Volume state restoration.
- Authored temporal-effect settings retained in the isolated path; the temporary Volume changes only exposure. This asserts settings, not pixel correctness of every effect.
- Screen Space Camera uGUI rendering with isolated cameras.
- Actual GPU measurement: authored Fixed EV 8.5 with compensation 2 produces effective EV 6.5 within 0.05 EV.
- Automatic Histogram exposure follows a fixture light intensity change from 100 to 1600 with an increase greater than 1 EV. These are fixture values, not calibrated lux.
- Warmup produces no Quilt submission. The camera-count limit rejects excess views before allocation; a 1 MB resource budget stops a 512×512 metering warmup and releases owned resources.
- Disposing while readback is pending retires the owned records. Automatic-render failure turns rendering off and releases an existing history pool.

Evidence is retained locally in ignored `.local/hdrp-lighting/graphics-precommit.xml` and `graphics-precommit.log`; the preceding 50/50 result remains in `graphics-reviewed.xml`. Deprecated `FormatUsage` and sorted object-search overloads were replaced in SDK and project-owned validation code. Validation light intensity uses the current `Light.intensity` property.

### URP TAA on Metal

The two added tests separately checked ordinary URP rendering and the existing SDK guard. A normal enabled URP Camera rendered multiple real frames with MSAA disabled, no camera stack, and no dynamic resolution. A public Renderer Feature observed changing TAA projection jitter through `UniversalCameraData.GetProjectionMatrix`; the non-black image differed from a TAA-off control, whose projection stayed stable. Both tests passed on macOS Metal. These are ordinary-camera TAA results, not Quilt TAA support.

The current `UrpQuiltCamera` still temporarily disables TAA during Quilt views and restores it afterward. This is an SDK history-sharing limitation, not a Metal prohibition. Supporting Quilt TAA requires persistent histories for each rendered URP view, with lifecycle and pixel tests. It was not implemented as part of this HDRP change set. See the [Unity 6.6 TAA requirements](https://docs.unity.com/en-us/engine/6000.6/manual/render-pipelines/universal-render-pipeline/anti-aliasing) and [public URP camera-data API](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@17.6/api/UnityEngine.Rendering.Universal.UniversalCameraData.html).

## macOS Development and Release Players

Both configurations built and exited with code 0 on Unity 6000.6.4f1 / Metal. The dedicated `MacHdrpHistoryValidation` fixture uses Linear, a 256×192 four-view Quilt, a red HDRP/Lit Cube, a directional light, and an authored Exposure Volume. It does not load Bridge or send output to a display. Build/run commands are in the [pipeline test guide](../../Tests/Pipelines/README.md#macos-metal-history-exposure-and-urp-taa-validation).

| Check | Development | Release |
| --- | ---: | ---: |
| Separate persistent view Cameras, stable identities, retained TAA settings | 4 | 4 |
| Fixed EV 8.5 minus compensation 2 | 6.5 | 6.5 |
| Automatic Histogram EV, light 100 → 1600 | 9.1339 → 10.2446 | 9.1339 → 10.2430 |
| Red Quilt pixels / magenta error pixels | 49152 / 0 | 49152 / 0 |
| New/grown RenderTexture estimate after automatic exposure | 152.3 MB | 151.1 MB |
| View Cameras / meter ownership records after stop and after budget failure | 0 / 0 | 0 / 0 |
| 1 MB warmup budget | Rejected: driver growth 28 MB, texture estimate 16 MB | Rejected: driver growth 0 MB, texture estimate 15 MB |

Fixed-EV controls confirmed that ordinary-camera half-float pixels and LDR Quilt pixels both respond to changing light intensity. The first, smaller Cube left most of the histogram black, so its automatic EV change did not satisfy the fixture's greater-than-1-EV expectation. Enlarging the fixture's metering subject resolved that condition without an SDK workaround. The final fixture waits for asynchronous GPU work using bounded elapsed time rather than a fixed number of fast CPU frames. Each passing run checked actual zero resource counts before exit; it does not prove arbitrary window-close cleanup. TAA retention above asserts settings and camera identity, not the pixel correctness of HDRP TAA.

Both Players retained the known HDRP `ScriptableRuntimeReflectionSystemSettings.system is assigned more than once` initialization warning. It is not marked fixed. The builds also emitted upstream Shader Graph and Null-device shader-limit diagnostics; successful Metal execution and the zero-magenta image checks are separate evidence. Logs, JSON, and the captured fixed-exposure PNG remain in ignored `.local/hdrp-lighting/player-{development,release}*`; the reproducible fixture source is committed. The generated scene, material, pipeline assets, and `.app` bundles are not committed.

## Editor and physical-device observations

The plant-case center-view capture at the same 186×341 device tile dimensions regained the case lighting and light shafts. The user inspected the connected Looking Glass Go and reported that light and brightness returned, with natural orientation and depth. This is a physical-device acceptance for this scene; it is not a color measurement or exhaustive optical review. Local captures are `baseline-center.png` and `device-fixed-center.png` under `.local/hdrp-lighting/`.

The following point observations use the same Go scene and Balanced tile dimensions. They are resource observations, not GPU timing or general performance benchmarks.

| Sampling | History cameras | Observed graphics-driver allocation growth | New/grown RenderTexture estimate |
| --- | ---: | ---: | ---: |
| Full | 66 | 2451.2 MB | 1287.1 MB |
| Quarter | 17 | 625.0 MB | 357.0 MB |

The Full diagnostic used an 8192 MB budget on a temporary preview copy, so it was not a test of the product's default threshold. The product default is **4096 MB**, with **Maximum History Cameras = 256**. The budget compares the larger of graphics-driver growth and estimated new/grown RenderTexture storage. It checks warmup as well as view rendering and stops rather than silently switching to shared histories. These observations can include shared or unrelated new allocations; they are not a precise attribution to the history pool.

Release Players return zero for the graphics-driver counter, so the RenderTexture descriptor estimate and separate camera-count limit remain active. The macOS Release fixture above confirmed budget rejection with a zero driver counter. Unobserved buffers and driver overhead are outside the estimate: **this is not a strict VRAM cap**. See the [Unity graphics-driver counter documentation](https://docs.unity3d.com/6000.6/Documentation/ScriptReference/Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver.html).

An actual Editor script reload was requested with a meter readback pending: before reload, `Pending=True`, `Ready=False`, and one ownership record; afterward, meter cameras and ownership records were both zero. A separate Preview stop left zero view cameras, meter cameras, and meter records. The final Editor was stopped, the scene was not dirty, and the Console contained zero errors and warnings after archived earlier diagnostics were cleared. This does not prove a GPU failure or long-duration shutdown case. Records: `.local/hdrp-lighting/reload-pending-start.txt`, `reload-pending-end.txt`, `quarter-metrics.txt`, and `warnings-before-final.json`.

## Unverified scope

- The new history/exposure path on Windows DX11/DX12, Windows Players, and macOS Bridge/device Players. The four-view macOS rendering-only Players above do not validate arbitrary scenes, larger view counts, Quick Start Players, or other build configurations.
- Pixel correctness of each HDRP temporal effect, VFX history behavior, all views, and arbitrary scenes. Preserving settings alone is not visual validation.
- Forced GPU/readback failure recovery, prolonged resource trends, hotplug, and general performance. An uncertain retirement completion retains resources by design.
- Precise VRAM accounting, GPU timing, all Looking Glass models, colorimetry, and general optical quality.
- The existing URP temporal-effect and camera-stack limits listed above.

The bounded architecture review found no remaining P1/P2 blockers in the reviewed implementation after the automatic-failure release and warmup-budget fixes. This review does not replace the unexecuted platform and visual checks.
