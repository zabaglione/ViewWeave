# URP Quilt view histories and TAA validation — 2026-10-11

Audience: ViewWeave developers. This is a bounded validation record for Unity **6000.6.4f1**, URP **17.6.0**, and macOS **Metal**. See [Compatibility](compatibility.md) for current support and [User Guide](user-guide.md) for operation. Older dated records retain their original results.

## Behavior and public API choice

The default URP `IsolatedViewHistories` mode owns one disabled Camera and `UniversalAdditionalCameraData` per **rendered logical view**. Full/Half/Quarter sampling keeps the remaining logical-view identities; removed views are destroyed. Changes to tile dimensions, view count, pipeline, settings-camera identity or renderer selection rebuild the pool. Stop, disable and automatic-output failures retire owned resources.

Core still owns off-axis framing, Canvas remapping, guards, target assignment, tile copies, submission, snapshots and restoration. An optional synchronous `IQuiltViewRenderExecutor` chooses the pipeline draw path. URP uses public `Camera.Render()`; HDRP retains `RenderPipeline.StandardRequest`. Metal and Windows transports remain separate from these common/pipeline responsibilities. No native transport, production assembly boundary or reflection workaround was added.

This distinction matters: [URP TemporalAA's implementation](https://github.com/Unity-Technologies/Graphics/blob/a7e4c051d256a781ab362c64316b125a1e104694/Packages/com.unity.render-pipelines.universal/Runtime/TemporalAA.cs) suppresses history updates and motion vectors for a render request. Splitting Cameras while continuing to submit render requests would not establish temporal accumulation. [Unity's pipeline feature comparison](https://docs.unity.com/en-us/engine/6000.6/manual/render-pipelines/choose-a-render-pipeline/feature-comparison) documents `Camera.Render` support in URP. The tests below verify its actual behavior in the selected Editor version.

Reference Camera rendering settings are copied through public URP APIs. Only authored TAA properties are copied; Camera-specific counters, histories and `resetHistory=false` are not assigned each frame. `resetHistory=true` is forwarded on a rising edge; applications can release the pool explicitly for a camera cut. Framing, clip planes, culling and generic Camera options stay with the Rig Source Camera. Renderer selection is resolved through the public pipeline renderer list.

TAA requires Post Processing, a single-sample target, a Universal Renderer and no Camera Stack or dynamic resolution. Incompatible settings stop output with an actionable diagnostic instead of silently rewriting the authoring camera/pipeline. Active URP Motion Blur remains rejected: its visual behavior has not been validated by this TAA work. Explicit `SharedCameraCompatibility` keeps the old TAA suppression/restoration behavior.

An optional `IQuiltMsaaSettingsProvider` lets Core choose target samples using the same renderer that draws the view: Reference in isolated/follow mode, otherwise Source. Source Camera's `allowMSAA` remains authoritative. Older MSAA providers keep their original API/fallback. Deferred targets remain single-sample, including TAA, even if the pipeline asset requests MSAA; Forward TAA rejects an actual multisampled target.

## Volume and resource ownership

In Play Mode, `ViaScripting` and an asset-level scripted update mode copy the authored VolumeStack to private view stacks. TextureCurve parameters require a deep copy: generic `SetValue` would share mutable curve resources. Public `JsonUtility` serialization preserves serialized curve keys, loop, zero-value and range; `SetDirty` rebuilds nonserialized evaluation state. Serialization is cached per Quilt and private copies are replaced only when the serialized value changes. The ownership map uses reference identity because VolumeParameter hashes depend on their mutable values and override state. Tests check stable copy reuse, looped evaluations, changes and preservation of the Reference curve texture at stop.

Owned stacks are explicitly destroyed **before** clearing UAC's stack property, so they are not returned alive to URP's static stack cache. The prior global VolumeStack is restored after each draw. Edit Mode follows Unity's live Volume evaluation and does not retain ignored scripted view stacks. Normal Camera history disposal remains owned by UAC when the view host is destroyed.

Rig > Rendering exposes View History Mode, Follow Reference Rendering Settings, History Resource Budget (4096 MB by default), and Maximum History Cameras (256). The common budget helper observes the larger of graphics-driver allocation growth and an estimate of new/enlarged RenderTextures. It includes shared pipeline allocations and is **not a complete VRAM cap**: buffers, curve Texture2D storage and driver overhead are not fully observable in Release Players. The camera-count limit applies in every build.

## Verification

Artifacts are under the ignored `.local/urp-taa/` folder. Test fixtures and reproduction commands are kept in [Tests/Pipelines](../Tests/Pipelines/README.md). Generated scenes and Players are validation artifacts, not UPM Samples or user-project changes.

| Check | Evidence and limits |
| --- | --- |
| Initial manual Camera prototype | 1/1 Metal PlayMode pass. A disabled ordinary Camera advanced `TaaHistory.GetAccumulationVersion`; observed changing projection jitter and TAA-on/off pixels. This prototype warmed an enabled Camera first, so it alone did not prove fresh-disabled initialization. |
| Four-view production Quilt | Fresh disabled view Cameras create independent TAA objects and accumulation RenderTextures; identities remain stable; versions advance across real frames. Same-frame redraw keeps the history version. Each view has changing jitter; TAA-off projections are stable; Quilt pixels differ. |
| Pool and invalid settings | Full→Quarter pruning, retained end-view identities, resized-target pool rebuild, explicit stop/restart, maximum-camera limit, Post Processing/dynamic-resolution rejection, compatibility fallback and 1 MB budget retirement. |
| Effective renderer and MSAA | Source Deferred / Reference Forward and the opposite combination; follow disabled; Source MSAA permission disabled; Deferred single-sample TAA; Forward multisample TAA rejection; Shared compatibility's original renderer selection. |
| Scripted Volumes | Independent stacks and ColorCurves, equal looped-curve evaluations, edited-curve propagation, intact Reference texture after stop, explicit stack invalidation, and repeated asset=`ViaScripting`/camera=`UsePipelineSettings` restarts. |
| Metal Edit Mode | A separate Editor-only fixture checks real frame/history progression outside Play, same-frame behavior and stop/restart. It additionally starts the actual public `BridgeMetalEditorPreview` local service: eight frames, pause/resume of the same view history, no unused scripted view stacks, and stop with zero pending resources and destroyed view Cameras. This does not test device transport or human optics. |
| Existing URP MSAA | 4/4 Metal PlayMode lifecycle tests passed: supported samples, resize/resolve, deferred behavior, color-space preservation and disable cleanup. |
| macOS Development/Release Players | Rendering-only fixtures both exit 0 and report pass. Four independent view histories advance from version 5 to 20; automatic Quilt rendering advances history. TAA-on/off changes 483 pixels; moving the bar changes 1,752 pixels. Both retire view Cameras and reject a 1 MB budget. Release has driver counter 0 and still rejects the budget through a 13 MB RenderTexture-growth estimate. These figures describe this small fixture, not performance or optical quality. |

Final results after the ownership fixes:

| Artifact | Result | UTC test interval / details |
| --- | --- | --- |
| `graphics-final.xml` | 58/58 passed, 0 failed/skipped | 2026-10-10 15:43:44–15:44:29; eight URP adapter cases, including renderer-dependent MSAA, plus the existing URP/HDRP compatibility cases. |
| `editmode-final.xml` | 1/1 passed, 0 failed/skipped | 2026-10-10 15:46:36–15:46:49; direct Edit Mode histories and actual Metal local Preview service, including scripted-mode cleanup, after the MSAA fix. |
| `rig-editor.xml` | 36/36 passed, 0 failed/skipped | 2026-10-10 15:34:35–15:34:49; existing Rig authoring/creation, Metal Inspector and Preview checks. |
| `msaa-final.xml` | 4/4 passed, 0 failed/skipped | 2026-10-10 15:45:04–15:45:16; after the effective-renderer MSAA fix. |
| `player-development.json`, `player-release.json` | Both pass; build and Player exit 0 | Rebuilt after the ownership and MSAA fixes. Both reports identify Unity 6000.6.4f1 / Metal and the expected development flag. |

Both final Players report 1,329 green pixels, 483 changed TAA-on/off pixels, 1,752 changed moving-bar pixels, history versions 5→20 for all four views, zero retained views after stop, and a texture estimate of 1,769,472 bytes for the small fixture. The Development startup driver counter is 43,364,097 bytes; Release reports zero. These are fixture observations, not per-view memory costs or a performance result. The final Release Quilt PNG was visually checked for the four green bars and absence of magenta output.

Existing fixture/compiler and upstream shader-import warnings remain in Editor/build logs, including unused-field/reference-comparison diagnostics and HDRP ColorChecker's URP Exposure-node warning. Final Player logs contain no exception/assertion/compiler-error diagnostic. Expected rejection diagnostics in the tests are asserted explicitly. Distribution generation/check, repository validation and whitespace checks passed; repository validation retains its two manual public-identity/rights/device-matrix review gates.

The separate live HDRP Editor reported no compilation failure or Console errors/warnings at 15:40:39 UTC. At the final Distribution sync its connection was absent and the previously observed Editor process had ended; the reason was not established. It was not restarted, so no post-sync user-project import/Console result is claimed. The isolated tests and rebuilt Players above validate the final source; the Distribution check verifies matching generated files.

## GadenScene2 MSAA comparison on Looking Glass Go

On 2026-10-11 JST, the user-provided URP project's `Assets/Scenes/Garden/GadenScene2.unity` was opened in Unity 6000.6.4f1 on Apple M3 Max / Metal. The live Editor resolved the SDK directly from `Distribution/urp`; the Distribution consistency check passed, and the new history/MSAA diagnostic APIs were callable. The existing Rig used `IsolatedViewHistories`, Follow Reference Rendering Settings, Full view sampling and Balanced device output. The Reference used a Universal Renderer with MSAA support, no camera antialiasing and Post Processing disabled; Source `allowMSAA` was enabled. Active `PC_High` initially requested one sample (MSAA off).

Only the active URP asset's in-memory `msaaSampleCount` was changed from 1 to 4 between stopped/restarted device previews. Framing, focus, lighting, sampling and output quality were unchanged. Both outputs used a 2046×2046 Quilt, 11×6 tiles, 66 rendered views and 186×341 pixels per view.

| Check | MSAA off | MSAA 4x |
| --- | --- | --- |
| Actual view target samples | 1 | 4 |
| Separate resolve target samples | No resolve target | 1 |
| Final Quilt samples | 1 | 1 |
| Persistent view Cameras | 66 | 66 |
| Successful native presentations at capture | 122 | 56 |

Central-view PNGs captured at 16:04:55 and 16:05:46 UTC show smoother coverage along the thin branches and hanging scroll frame with 4x. The user physically observed the Go and confirmed that jagged edges decreased. This establishes MSAA's visible effect for this scene and device output; it does not establish TAA optics, other sample counts, Native-resolution quality or comparative performance. The 5,504 changed pixels out of 63,426 central-view pixels are a difference check, not an image-quality score.

Quilt PNGs, central-view PNGs, a nearest-neighbor enlargement comparison and public-API JSON diagnostics are retained under ignored `.local/garden-msaa/`; no user scene or pipeline asset was saved. After comparison, preview stopped with zero pending resources, MSAA returned to its original one-sample setting, the scene and pipeline asset both reported not dirty, and Console reported zero errors/warnings with no compilation failure at 16:07:01 UTC.

## Issues found during development

The first attempt moved the public `SupportsRenderRequest` query after session creation. Fresh SRPs then had an uninitialized VolumeManager and the regression run failed. Core now keeps this query before adapter validation to prepare the selected pipeline; only a session without an executor requires a positive StandardRequest support result. The subsequent regression run passed 56/56 before the ownership-specific test was added.

Review also identified the ColorCurves sharing, mutable ownership-map keys, live-stack cache issues, and Source/Reference renderer mismatch in target MSAA selection described above; these were corrected and receive dedicated regression checks. Changing the fixture's pipeline update mode also invalidated its previously created authored stack through pipeline reconstruction; that test now initializes the rebuilt pipeline before creating the authored stack. The renderer-pair fixture waits for its isolated scene's asynchronous unload before creating the next fixture. These failures were not converted to skips or hidden by tolerances.

## Not established

- Windows D3D11/D3D12 execution of this URP history path, and Windows Players.
- Looking Glass Go transport/optical review with this new TAA path, macOS Bridge/device Players, or other Looking Glass models.
- General 48-view/Native-resolution performance, complete GPU-memory measurement, long-duration soak/hotplug behavior.
- Motion Blur, all custom Volume parameter/resource types, XR, stacks, dynamic resolution, renderer-feature assumptions tied to a particular Camera identity, or exhaustive ghosting/disocclusion/optical quality.

Earlier Go acceptance of URP template orientation/depth and earlier Windows fixtures remain historical evidence for those versions/conditions. They do not validate the new TAA output by themselves.
