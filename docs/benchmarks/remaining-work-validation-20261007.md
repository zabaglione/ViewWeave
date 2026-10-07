# Remaining-work validation record - 2026-10-08 JST (test evidence captured 2026-10-07 UTC)

## Capture tests

On Unity 6000.6.4f1 with URP 17.6.0 / HDRP 17.6.0, DX12 (-force-d3d12), and an RTX 3090 Ti, the current change set passed:

| Test | Result | Scope |
| --- | --- | --- |
| CoreBuiltinCaptureSmoke | PASS | DX12 capture smoke |
| URP QuiltCapturePipelineTests | 1/1 PASS | Central PNG and sidecar, half/quarter source mapping, Recorder Pause/Resume and time restoration |
| HDRP QuiltCapturePipelineTests | 1/1 PASS | Same targeted capture cases |

The Editor had graphics enabled; runs did not use -nographics. The pipeline tests use fake output and do not initialize official Bridge or display to a Looking Glass. These results apply to this change set, not to GitHub main, Bridge transport, or optical quality. The evidence is retained under E:/LookingGlassSDK/.local/cdx12/wvk6/.

## Fixture import path and metadata

The initial fixture path was 272 characters and Unity failed during URT/UXML import with a missing-directory error. A shortened 218-character fixture was built from the same 420 source files with matching hashes. URT compute/ray-tracing shader assets and both URP/HDRP Global Settings imported without errors, and the capture tests then passed. This supports a path-length limitation in that tested setup, not a general Unity path limit.

Two fixture folder .meta files were initially copied as 168-byte snapshots without the source EOF fix, producing two YAML/meta-pattern diagnostics. The corrected 169-byte source files were copied into the MP4 short fixture; `ForceSynchronousImport`, `ForceUpdate`, and `Refresh` then reported zero C# errors, zero YAML/meta errors, and both folder GUIDs. Source and imported fixture hashes matched. This confirms repaired metadata in that fixture only; it does not establish that other Unity projects have no import diagnostics.

## Shutdown observations

A new URP Editor normal-stop observation for PID 72436 recorded 0xC0000005 in bridge_inproc.dll 2.6.3.0 at RVA F8550, thread 71648, on 2026-10-07 09:31:42 UTC. Vendor SHA prefix: 3BE0...; WER report: a63c009c-242b-447b-afc3-8200ccc86996. Evidence is under E:/LookingGlassSDK/.local/remaining-work-validation/user-urp-stop-observation/. Earlier HDRP shutdown observations are retained; the issue is not scoped to HDRP/VFX. Root cause is unknown. Three UPM shim images were present in the dump observation; one known current shim hash begins 4C..., while two actual binary hashes are unavailable.

The Native image guard Unity PlayMode suite passed 6/6, and a child-process check observed two distinct shim image bases. The evidence folder suffix 112000Z is an arbitrary run identifier, not a UTC timestamp. Its purpose is to prevent duplicate shim loading; it does not identify or fix the vendor access violation. Separate pure .NET 8 guard tests passed. This targeted Unity PlayMode result is not a broader NUnit integration total. Separately, the full-size official Bridge shutdown contract passed one isolated DX12 run with five fresh fixed-texture sends, zero drops, one Stop, pending zero before texture release, and natural process exit. That test did not render 48 scene cameras or VFX and does not prove vendor-internal resource release completion.

## Threading and performance

A read-only query of the user's HDRP Editor (PID 65332) reported NativeGraphicsJobsSplitThreading, graphicsJobs=true, and MTRendering=true. A separate batch measurement reported effective SingleThreaded; these are distinct conditions. The user's Editor is already using split graphics threading, so the batch result does not motivate a threading-mode change in that Editor. Garden VolumeStack A/B/A remains a private trial with no improvement above measurement noise and is not adopted in main.

## Related limits

The older D3D11 108/123 run with 15 failures remains historical and is not replaced by these targeted DX12 tests. The physical 48-view optical review is still unverified. XR, Plug & Play, multiple devices, and Gamma VFX remain held.

## Quilt video export and real-recording E2E

On 2026-10-07, the exporter fake-process validation suite passed after lifecycle fixes and passed Astra review. A separate CPU-only smoke used FFmpeg/ffprobe 7.0.2 and three synthetic 64x48 PNG quilts at 30 fps. It produced H.264/yuv420p with 3 frames; ffprobe matched codec, dimensions, frame rate, and count. The full 8x6/48-view geometry was retained in the JSON sidecar. Input/output paths contained Unicode and spaces, and an existing output was rejected. Evidence: `E:/LookingGlassSDK/.local/remaining-work-validation/mp4-final-20261007T195427/probe-result.txt`.

No Unity quilt render, official Bridge output, or physical display was involved in this synthetic CPU smoke. Separately, on 2026-10-07T13:28:41Z, an actual URP DX12 QuiltRecorder output was exported through the real FFmpeg/ffprobe path: 256x192, 4x2/8 views, three frames at 30 fps, H.264/yuv420p; the Editor end-to-end test passed 1/1. It used no Native/official Bridge and is not an optical or Bridge transport test. This actual-recording result is distinct from the synthetic full 8x6/48-view smoke above. Manual Editor-window interaction remains unverified. H.264 is lossy. The exporter does not ship or install FFmpeg.

## Repository validation and CI guard

The repository validator now requires `guid:` and a 32-hex GUID value on one line and reports malformed or unreadable metadata explicitly. Five Python tests cover valid LF/CRLF metadata, split/missing/invalid values, duplicate GUIDs within a profile, duplicates across profiles, and invalid UTF-8. The static CI workflow runs these tests before the repository validator. This is a regression-prevention change; no GitHub CI failure has occurred for this change set. The corrected fixture metadata copy/import check reported zero C# errors, zero YAML/meta errors, and both Folder GUIDs, as described above.

Local static verification passed: Python validator suite 5/5, UPM distribution checks for URP and HDRP, and `git diff --check`. The repository validator reported 0 errors and 47 warnings for links to local evidence absent from this worktree; these are existing evidence-link warnings, not metadata errors.
## Follow-up validation (reviewed 2026-10-08 JST; runs captured 2026-10-07 UTC)

At the time of these test runs, the validation base was `4230c4c` (PR #4 merged; CI run `37614129521` successful), which included capture controls, the silent MP4 exporter, and the Native image guard. The results below apply to the additional features and tests in this change set. They do not identify a final integration revision or grant release approval.

- Camera authoring: static Astra review and the DX12 `RunCameraAuthoring` test passed. Interactive Scene GUI dragging remains unmeasured.
- Quilt playback safety: the suite's 12 scenarios were individually passed after an initial 11/12 run exposed a fixture-only setup failure; the focused correction passed 1/1 without product-code changes. This does not prove Native file playback or official Bridge transport. A separate Editor reload probe passed for a 4x4 RenderTexture and serialized playback recovery record surviving reload, post-reload fence/readback, and a test-only handoff; the recovery guard was unproven and Native Begin/Stop calls were zero. Graceful close failed with exit code 6. After verifying fixture identity, the probe fixture was force-closed once; the cleanup command exited 0 and reported `closed=true`, with owned PIDs 88544, 112172, and 116276 absent afterward. Natural Editor exit remains unconfirmed, so this is not shutdown-order proof. Evidence: `E:/LookingGlassSDK/.local/remaining-work-validation/cdx12/wvk6-results/reload-run-20261008/wvk6-editor.log`. Safety-suite evidence IDs: `playback-safety-valid-20261007T142043Z/safety-tests.xml` and `safety-focused-run-20261007T150304Z/safety-focused.xml`.
- VFX: the existing cold DX12 driver suite's 6/7 result and FOV50 `alive=0` failure remain historical and unresolved. A no-synchronous-CSV-I/O diagnostic suite passed 7/7; FOV50 and FOV90 diagnostic outputs each contain 79 rows and report 128 alive particles. A separate filter run passed 3/3, including the fully qualified original FOV50 case, but does not establish a permanent fix or general cold/warm success. URP Particle 3/3 and HDRP Particle 2/2 remain limited fixtures. Evidence IDs: `vfx-memory-run-20261007T150431Z/` and `vfx-original-fov50-focused-20261007T151119Z/vfx-fov50-focused.xml`.
- Audio: fake backend tests passed 6/6. Actual audio and legacy silent tests passed 7/8 initially; the sole failure was an unexpected exact Unity error prefix in `UrpAudioStartRejectPreservesExistingRendererOwner`. After the test-only expectation/owned-Stop check change (SHA-256 `66D173D080A0DEE487EC4EFCE27FD75B0DB466392F2F02382CB952F86356BD28`), the focused case passed 1/1. All 14 cases across fake, actual audio, and legacy silent runs have individually passed across the recorded runs. Actual URP/HDRP Mono Quilt recording, Pause/Resume, Stop/disable cleanup, and preservation of playing and scheduled `AudioSource` state passed. The foreign-owner case confirmed its renderer remained usable and the test-owned `AudioRenderer.Stop()` returned true. Stereo declaration with Mono capture and a normal GUI 48 kHz Stereo / DSP 1024x4 configuration with Mono capture both failed channel separation; audio capture remains Mono-only. The 150-frame 30 fps probe measured 4.992 s of audio for 5 s simulated video (about -8 ms), not sample-accurate per-frame alignment. Evidence IDs: `audio-fake-mono-20261007T145153Z/audio-fake-tests.xml`, `audio-actual-silent-20261007T145520Z/audio-actual-tests.xml`, and `audio-owner-focused-20261007T150954Z/owner-focused.xml`.
- HDRP shadow: EV12 exposure, 48 callbacks, and stack restoration passed; clipping improved. The repeated EveryFrame baseline self-comparison differed at 170 pixels (maximum per-channel delta 1) across 47 of 48 tiles, so the strict image-equivalence gate failed. OnDemand comparison and performance measurement were not reached; it is not adopted.
- Bridge shutdown: the URP Editor `0xC0000005` at `bridge_inproc.dll` RVA `F8550` remains unresolved; no new crash was observed in this snapshot. A clean main cold start and 600-second OS/Bridge watch remain pending. No Native production source or vendor DLL was changed.

The prior D3D11 integration failures remain historical and are not replaced by these targeted results. Full 48-view optical review remains unverified. Native Bridge playback/transport, production reload/shutdown ordering and natural exit, and the user-project cold-start/600-second watch remain pending. The isolated Editor reload probe is only the limited evidence described above. XR, Plug & Play, multiple devices, and head tracking remain deferred or disabled.
