# Remaining-work validation record - 2026-10-07

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

## Quilt video export CPU smoke

On 2026-10-07, the exporter fake-process validation suite passed after lifecycle fixes and passed Astra review. A separate CPU-only smoke used FFmpeg/ffprobe 7.0.2 and three synthetic 64x48 PNG quilts at 30 fps. It produced H.264/yuv420p with 3 frames; ffprobe matched codec, dimensions, frame rate, and count. The full 8x6/48-view geometry was retained in the JSON sidecar. Input/output paths contained Unicode and spaces, and an existing output was rejected. Evidence: `E:/LookingGlassSDK/.local/remaining-work-validation/mp4-final-20261007T195427/probe-result.txt`.

No Unity quilt render, official Bridge output, or physical display was involved. This process-level smoke does not verify the capture-recording-to-export Editor workflow or MP4 export from a real recording. H.264 is lossy. The exporter does not ship or install FFmpeg.

## Repository validation and CI guard

The repository validator now requires `guid:` and a 32-hex GUID value on one line and reports malformed or unreadable metadata explicitly. Five Python tests cover valid LF/CRLF metadata, split/missing/invalid values, duplicate GUIDs within a profile, duplicates across profiles, and invalid UTF-8. The static CI workflow runs these tests before the repository validator. This is a regression-prevention change; no GitHub CI failure has occurred for this change set. The corrected fixture metadata copy/import check reported zero C# errors, zero YAML/meta errors, and both Folder GUIDs, as described above.

Local static verification passed: Python validator suite 5/5, UPM distribution checks for URP and HDRP, and `git diff --check`. The repository validator reported 0 errors and 47 warnings for links to local evidence absent from this worktree; these are existing evidence-link warnings, not metadata errors.
