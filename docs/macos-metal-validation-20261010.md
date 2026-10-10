# macOS Metal verification — 2026-10-10

This record covers the initial owned-window Looking Glass Go output and its separation into shared, Metal and Windows responsibilities. Usage is documented in [macOS Metal](macos-metal.md), and source boundaries in [Platform architecture](bridge-platform-architecture.md).

## Configuration

- Unity 6000.6.4f1, URP 17.6, Apple M3 Max / arm64, macOS 26.6.2, Metal.
- Installed Looking Glass Bridge 2.6.3; connected Go in Desktop Mode at native 1440×2560.
- Runtime profile: Quilt 4092×4092, 11 columns × 6 rows = 66 views, aspect 0.5625, view cone 54°. Streaming tests use Preview quality while retaining all views.
- Packaged native dylib: Release / arm64 / AppleClang 21.0.0.21000101, 132208 bytes, SHA-256 `9e4d0343bd36cacd7910dea1cac7dd002943a4033a389d05804f021ac8418e9b`. Its `LC_BUILD_VERSION` has minimum macOS 26.0 and SDK 26.5; the bundled plugin therefore requires macOS 26.0 or later. Source/header hashes and the pinned public Unity header commit are in `Packages/com.zabaglione.lookingglass.viewweave.bridge/Plugins/macOS/native-provenance.json`.

## Results

| Evidence | Result and scope |
|---|---|
| Native build and tests after separation | Release build passed; CTest 4/4: portable worker numeric validation, shared metadata, finite Metal API contract, finite submission lifetime. These are not unit coverage of every owned-presenter GPU callback. |
| Shared metadata tests | Physical display ID mapping, bounded enumeration, growing/shrinking lists, invalid calibration, exceptions and ABI version rejection passed. |
| Managed path dispatch | Five mcs/Mono define configurations, 97 assertions passed. Editor host takes precedence over an opposite standalone target; Windows/macOS Players select their own paths; Linux Editor rejects explicit/env paths. Registry automatic discovery was not executed. |
| Shader GPU calculation | Gamma and Linear each passed 486 RGB samples against an independent numeric reference on Metal. Linear was rerun after file separation and in the latest Player build. Both view directions are exercised. Explicit LOD sampling prevents filtering across Quilt views. |
| Native Go admission/cancellation | Three cycles passed after separation: busy submission is dropped, close cancels an unissued token, a stale event does not affect a new session, and reopening succeeds. Fixed-width display/status sizes are 60/24 bytes. |
| Latest macOS Player | Build succeeded. Live Go run produced 24 reports with empty errors, two manual R restarts and up to 2066 completed frames in one session. Escape waited for `IsClosing=false`, then exited naturally with code 0. Report counters are sampled before stop and need not equal final native retirement counters. |
| Latest Editor lifecycle | Two Play/Stop cycles with Domain Reload passed. Each reached 121 submitted / 120 completed frames before stopping; both ended at retained GPU resource count 0. A different component was rejected while process-wide retirement was pending. |
| Editor GUI | Game View showed the cube, sphere and floor with no pink materials. Hierarchy contained the expected sample objects; the Metal component's Rig reference was valid in Inspector. Console showed zero errors and zero warnings. |
| Physical optics | Before file separation, the user confirmed the corrected upright orientation and natural depth on the connected Go. The separation preserves the shader/presenter calculation and UV orientation. Subsequent software/display runs do not add a new exhaustive physical optical measurement. |
| Native ABI / serialized compatibility | Existing script GUIDs and assembly names preserved. `lkg_*` and `UnityPlugin*` export names match the pre-separation macOS artifact. The Windows selected branch differs only in shared metadata extraction and includes; D3D device/worker flow is retained. |

The test project is `Tests/Metal/Unity`. `MetalValidationGate.Run`, `MacOSMetalValidationBuilder.Build` and `MetalEditorLifecycleValidation.Begin` reproduce the GPU/native, Player and Editor checks. Generated scenes and Players, detailed logs, the dispatch harness and local baseline source comparisons are ignored under `.local` or the generated fixture directory. They are not package content.

A first test attempt after overwriting the already-loaded dylib was terminated by macOS with SIGKILL. The OS log identified a code-signing page modification-time mismatch for that dylib. The build tool now installs using a new inode and atomic replacement. A fresh Editor passed the same native/GPU gate afterward. Restart the Editor after native plugin updates.

## Play-stop assertion investigation

After the final manual Play session, stopping produced `Mismatched state in DefaultScenario, was Stopping but expected Idle`. The saved Editor log places the assertion inside assembly reload following a forced synchronous script recompile. The Editor's General > Script Changes While Playing preference was confirmed as `Recompile After Finished Playing`. These conditions match [Unity UUM-154172](https://issuetracker.unity.com/issues/25291/mismatch-state-in-defaultscenario-error-is-thrown-when-exiting-play-mode-with-a-modified-script-and-recompile-after-finished-playing-enabled), which explicitly lists 6000.6.4f1 as affected. The official report tests Windows; this local occurrence was on macOS. The final source-file move being imported during the manual Play session is the likely trigger, rather than proof of a Metal shutdown defect.

Once compilation finished, the same Editor completed two further Play/Stop cycles with Domain Reload. Both reached 121 submitted / 120 completed frames and stopped with retained GPU resources = 0. The process-wide retirement admission check also passed. The new log segment contained no matching assertion or managed exception, and Console showed zero errors and zero warnings in Edit Mode. The validation controller disarmed itself after completion. Unity remained at 6000.6.4f1 and the Editor preference was unchanged; no production shutdown code or log suppression was added. This verifies ordinary Play/Stop after compilation, not a fix for Unity's deferred-recompile issue.

Evidence remains in ignored `.local/macos-validation/platform-split-editor-lifecycle.log`; `play-stop-recovery.log` isolates the subsequent verification. Avoid source changes during Play, then wait for imports and compilation before starting again. The optional Unity preference workaround is documented in the macOS usage pages.

## Unverified boundaries at the initial output checkpoint

Windows native rebuild and D3D11/D3D12 runtime/device tests after this source separation have not been run on this Mac. The bundled Windows DLL and provenance remain the previous validated artifact. Existing Windows/Linux CI targets select the new source paths, but no new remote CI run is claimed here.

HDRP on macOS, Intel/universal runtime, Balanced/Native performance and optical quality, prolonged soak, disconnect/reconnect scenarios, multiple devices, ordinary Player window-close and Editor-quit GPU proof, exhaustive 66-view inspection and colorimetry remain unverified. Player Escape and Editor Play/Stop are the verified shutdown paths.


## Metal Edit Mode Preview and Quilt Playback follow-up

This later checkpoint uses Unity 6000.6.4f1 and the supplied URP/HDRP 17.6 template projects. It adds managed Editor controllers and shared producer/playback utilities; native binaries, shaders, asmdef boundaries and existing script GUIDs are unchanged. The Playback window moved into `Editor/Common` with its original GUID.

| Evidence | Result and scope |
|---|---|
| Rig tests on macOS Metal | 35/35 Edit Mode tests passed with no skips. Five new cases cover local GPU Preview Pause/Resume/Close and source-camera preservation, invalid layout cleanup, Authoring-window close, ambiguous Windows/Metal component dispatch, and rejection of Playback outside Play. |
| Local and device previews | Both URP and HDRP rendered in Edit Mode. Device Preview used the connected Go profile; Pause/Resume and Close were exercised. Later single-submit samples recorded URP 1228 renders / 1227 presentations / one busy drop, and HDRP 20 renders / 19 presentations / zero drops; these are samples before final queue completion. |
| Physical Go acceptance | The user separately confirmed natural orientation, depth and brightness for URP and HDRP Edit Mode device previews. This is scene-level acceptance, not exhaustive view/color/physical-scale measurement. |
| Preview reload and Play handoff | Explicit script reload stopped URP paused and HDRP running device previews with common/native pending counts zero. URP device-preview-to-Play completed with regular output running, Preview inactive, and both pending counts zero after the template startup helper was corrected and imported. |
| PNG and sequence Playback | HDRP single PNG and an eight-frame sequence exercised Pause/Resume, normal Stop, sequence completion and Auto Render restoration. URP single PNG and a four-frame sequence completed through Metal; the sequence reached its final frame, released its lease and resources, and restored live rendering. Latest HDRP PNG admission was also confirmed after snapshot/retry changes. |
| Invalidation and output shutdown | Paused URP Playback ended synchronously before output profile restoration. A raw framing-setting change invalidated paused Playback without resuming Auto Render. URP and HDRP Play exit stopped Playback and reached common/native pending zero with no operation lease retained. |
| GUI and repository checks | Shared Rig Preview controls and the Rig-target Playback window were visually inspected. Repository validation passed with zero errors and 47 pre-existing documentation warnings; Python validation tests passed 5/5; both generated distributions matched source. |

The initial device-preview-to-Play attempt exposed a validation-helper race: its Start coroutine called output start before native retirement completed. The helper in each supplied project now waits for `PendingRetirementCount == 0` and `!IsClosing`; production automatic startup already waits for retirement. An explicit AssetDatabase refresh was needed before the external helper change was compiled. These project-specific helpers are outside the distributed SDK.

Play exits with deferred compilation, and the HDRP exit after an explicit script-reload request, reproduced Unity's `DefaultScenario` assertion described above. Source/import completion was checked before normal Play tests; this work does not fix or suppress that Unity assertion. Historical errors and failed compilation attempts are preserved in local evidence. The new source code was checked after correction.

An explicit script-reload request during active HDRP Playback did not demonstrate an immediate domain reload under the current Editor setting: the next probes still showed active Playback. Its subsequent Play exit completed safely. Active-Playback domain reload is therefore code-reviewed, but not counted as a successful live reload test. Preview domain reload is verified separately. GPU-proof failure injection, ordinary Editor quit, long-duration use and Windows D3D11/D3D12 execution of this revision remain unverified.

Detailed JSON state samples, test XML/logs, review notes and generated PNG recordings are ignored under `.local/metal-preview-playback`; they are validation artifacts and are not distributed package content. Both supplied project scenes retain their pre-existing unsaved state; verification did not save or revert them.
