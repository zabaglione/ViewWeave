# Windows Unity stdout and shutdown crash investigation (2026-10-11)

This note narrows one recurring Unity Editor shutdown access violation identified at `Unity.dll+0x44383df`. It supplements, and does not replace, older Windows validation findings whose causes remain unknown. It does not group other exception codes, modules, or RVAs into the same cause.

## Crash evidence

The historic Editor failures for PIDs 45816, 76952, and 72156 reported `0xC0000005` in `Unity.dll` at RVA `0x44383df`. The pre-Metal baseline PID 76952 and a later HDRP/D3D11 Editor run PID 72156 therefore share this fault location. The 2026-10-10 event for PID 72156 was at 14:57:00 UTC (23:57:00 JST); PID 76952 was at 14:46:38 UTC (23:46:38 JST).

The local dumps for PIDs 76952 and 72156 were analyzed against the installed Unity 6000.6.4f1 symbols. Both resolve to `Unity!dup_nolock+0x173`, a one-byte write to address `0x11f0`. The captured stack is:

```text
Unity!dup_nolock+0x173
Unity!dup+0x84
Unity!RedirectStdout
Unity!Application::Terminate
Unity!Application::Exit
Unity!EditorApplication_CUSTOM_Exit
```

The dump and disassembly correlation shows the internal Unity CRT `DuplicateHandle` path failing, leaving `ESI=-1`. The subsequent failed-descriptor cleanup uses index `-1` through `[r13-8]`; the base is null, and the resulting indexed write reaches `0x11f0`. This identifies the failing cleanup path and faulting instruction. It does not by itself prove that Bridge output caused the crash. The earlier current-workspace HDRP/D3D11 run also had a test assertion failure without an access violation; that is a separate outcome. Do not treat it as another instance of this crash.

Other recorded errors remain separate: PID 76148 reported a CoreMessaging `0xE046...` failure, and PID 78364 reported `KERNELBASE` `0xE0000001`. They are not established as the same fault.

## Bridge load-path evidence

Static inspection of the official Bridge in-process runtime found its console reconnect routine on a DLL initialization path. The routine attempts `AttachConsole` and reopens standard streams. A local native probe then measured the official Bridge 2.6.3 runtime through the rebuilt SDK shim, without launching Unity or stopping the Bridge service.

In the hidden GUI-subsystem run, stdin, stdout, and stderr started as valid redirected pipe objects. After the shim loaded they were unchanged. Immediately after the Bridge load call, before SDK initialize, all three current standard-handle slots referred to `FILE_TYPE_CHAR` objects. `CompareObjectHandles` against independently held duplicates of the original pipe handles returned false (error 1656) for all three streams. The saved raw handle slot values were numerically unchanged, but comparing each raw slot to its retained duplicate also returned false: the original handle objects had been closed and the numeric slots reused for different objects. The process had no console window, while the console process count changed from zero to two. This directly confirms replacement of those standard-handle objects during the Bridge DLL load path.

Two guarded diagnostic runs temporarily set all three process standard handles to `NULL` around Bridge load, then restored the original values. Every assignment and restoration succeeded. The held original pipe objects remained alive and identical in their original slots through DLL load; after restoration, current standard handles again matched those objects. This succeeded in both a hidden GUI launch and a child inheriting the existing console context. Both runs loaded and initialized Bridge 2.6.3, observed one display, and exited normally. The version text was `2.6.3`; return value 5 is its string length, as `Native/common/bridge_metadata.cpp` `ReadVersion` returns `value.size()`. It is not an SDK error code.

## Native load guard

The Windows shim now guards the first vendor runtime load in `lkg_bridge_load`. It checks whether the canonical vendor module is already resident outside shim ownership and refuses that case. For a new load it saves all three `GetStdHandle` values, temporarily sets them to `NULL`, calls `LoadLibraryExW`, and restores every standard handle it changed before returning to the caller. A preparation failure skips the library load and restores prior successful changes. A restoration failure is recorded as a sticky shim error and subsequent loads are rejected. Rebinding an already-owned shim module at the same path does not repeat the detach sequence.

The focused native regression uses a fake DLL whose private CRT static initializer calls `freopen_s` for stdin, stdout, and stderr. Its unguarded control reproduces the standard-handle object replacement; its guarded case checks that the original objects remain identical after load, exercises redirected input/output, and verifies that a module loaded outside the shim is refused. The focused control/fix test passed. The full native CTest run passed 15/15, and the transport-only variant passed 11/11. The rebuilt and shipped package, URP, and HDRP shim DLLs have the same SHA-256 `4D28B54BC09FFCF0BC2E6F9E645E9A04C6EF59210C79BB784872655DA1837F7A`; UPM distribution checks passed. This does not establish safety under concurrent process-global `GetStdHandle` users, nor does it verify an already-resident vendor module outside the tested refusal case.

Evidence is local under `.local/windows-unity-crash-20261011/native-handle-probe/`, including the unguarded and guarded logs, commands, metadata, stdout/stderr captures, and probe source. Access-token-bearing process command lines are not included in this note or those selector outputs.

## Current interpretation and limits

The native probe proves that loading the official Bridge runtime closes and replaces the host's standard-handle objects; the shim now prevents that tested load-time defect. The historic Unity dumps show stdout duplication failing, followed by Unity CRT cleanup through an invalid descriptor, which is consistent with the Bridge handle-replacement defect. The historic Editor processes did not record the SDK DLL load or close event, and the native probe did not reproduce a Unity crash, so linking those particular historical crashes to the Bridge defect remains an inference. This does not identify the cause of every Editor crash or other exception bucket.

The standard handles are process-global. A temporary `NULL` interval can affect concurrent code reading or replacing them; that race remains open. On repository revision `2630bf4` (before the later fetched macOS changes), the focused `DeviceEditorPreviewPausesAndResumesSameWorkerWhenExplicitlyEnabled` EditMode test passed in three real-device runs on Unity 6000.6.4f1 and an NVIDIA GeForce RTX 3090 Ti:

| Run | Fixture | Graphics API | NUnit | CLI exit | Loaded module capture |
| --- | --- | --- | --- | --- | --- |
| `.local/windows-unity-crash-20261011/fixed-unity/run2-hdrp-d3d11-editmode/` | HDRP | D3D11 | 1/1 passed | Not captured; Editor log reported code 0 | No; Editor exited before sampling |
| `.local/windows-unity-crash-20261011/fixed-unity/hdrp-d3d11-recheck/` | HDRP | D3D11 | 1/1 passed | Not captured; Editor log reported code 0 | Yes |
| `.local/windows-unity-crash-20261011/fixed-unity/urp-d3d12-recheck/` | URP | D3D12 | 1/1 passed | 0 | Yes |

For both rechecks, strict process selection matched the new Editor executable, project path, Editor log PID, and native trace PID. Live module sampling captured the package shim at `REPO_ROOT\Packages\com.zabaglione.lookingglass.viewweave.bridge\Plugins\x86_64\lookingglass_bridge_shim.dll` with SHA-256 `4D28B54BC09FFCF0BC2E6F9E645E9A04C6EF59210C79BB784872655DA1837F7A`, and the official runtime at `C:\Program Files\Looking Glass\Looking Glass Bridge 2.6.3\bridge_inproc.dll` with SHA-256 `3BE0EBA96844688C2EB2E70FC512A5ADA3D53E3ED9C8810454AB0370B8241386`. All three runs recorded one display, successful initial and restart render completions, pause/close with native pending 0, and successful uninitialize. Each selected Editor exited, produced no crash report, and had no matching Windows Application 1000/1001 event. The first run and the HDRP recheck recorded Unity Editor's clean-exit marker; the URP/D3D12 CLI process exit code was captured as 0. The pre-run and recheck package shim hashes matched the synchronized build.

The earlier initial PlayMode invocation returned an XML `Passed` result with zero tests; it is preserved but excluded from the three-test total. The native full CTest suite passed 15/15, the transport-only variant passed 11/11, and UPM distribution checks passed for this revision. Unity-generated lock/settings changes were saved to local run evidence and restored to HEAD after each recheck.

The earlier Windows validation report retains its unknown-AV entries and historical test records. This investigation is limited to the matching `Unity.dll` RVA and stack above; unrelated crash buckets and broad shutdown behavior remain open.

## Windows validation on revision 62d0e0c

The fetched revision `62d0e0cb380bf663e4b33d2ad2095b09e4e872f9` changes shared URP/HDRP rendering paths and Windows Editor Preview shutdown, so four focused runs were performed on Windows after the pull. Unity 6000.6.4f1 ran on an NVIDIA GeForce RTX 3090 Ti. The PlayMode runs did not opt into Bridge device tests.

| Run | Mode and fixture | API | NUnit | CLI exit | Result |
| --- | --- | --- | --- | --- | --- |
| `.local/windows-unity-crash-20261011/latest-unity/playmode-d3d11/` | PlayMode, selected URP/HDRP suites | D3D11 | 20/22 passed, 2 failed, 0 skipped | 8 | Two assertion failures below; no crash event |
| `.local/windows-unity-crash-20261011/latest-unity/playmode-d3d12/` | PlayMode, selected URP/HDRP suites | D3D12 | 20/22 passed, 2 failed, 0 skipped | 8 | The same two tests failed; no crash event |
| `.local/windows-unity-crash-20261011/latest-unity/editmode-hdrp-d3d11-device-pause/` | EditMode, HDRP device pause/resume | D3D11 | 1/1 passed, 0 skipped | 0 | Package shim and official Bridge runtime were captured in the Editor process |
| `.local/windows-unity-crash-20261011/latest-unity/editmode-urp-d3d12-device-transition/` | EditMode, URP Preview-to-Play transition | D3D12 | 1/1 passed, 0 skipped | 0 | Package shim and official Bridge runtime were captured in the Editor process |

Both PlayMode runs failed `HdrpCustomPassRunsForEveryQuiltView`: the fixture observed zero Custom Pass executions where it expected eight. `AutomaticExposureRespondsToLightingChanges` also failed on both APIs. On D3D11, the fixture's finite-value assertion failed because a before/after exposure sample was NaN, so the later luminance-response comparison was not reached. On D3D12, samples were finite, but the observed value was `10.259078` against the assertion threshold `greater than 11.259078`. These fixture results do not establish that the HDRP exposure behavior itself regressed. Each run used Direct3D on the 3090 Ti, ended without Application event 1000/1001, and produced no Unity crash report.

Both device EditMode runs passed with zero skips and clean Editor/CLI exit. Strict process selection and PID cross-checking matched the new Editor, Editor log, and native trace. Live module sampling captured the package shim at the expected package path with SHA-256 `4D28B54BC09FFCF0BC2E6F9E645E9A04C6EF59210C79BB784872655DA1837F7A` and the official Bridge 2.6.3 runtime. Both traces recorded one display, zero pending native tokens, and successful uninitialization. Neither run produced an Application event 1000/1001 or crash report. Unity-generated tracked settings changes were saved as per-run patches and restored to the fetched revision; the test-generated `InitTestSceneafb9a046-96ad-40c0-b43d-e3db97862ef0` scene and metadata were absent after the final run. These focused runs verify the named Windows cases only; they do not verify Metal or establish that every Unity shutdown failure is resolved.

## Post-fix verification on the same fetched revision

The following runs used revision `62d0e0cb380bf663e4b33d2ad2095b09e4e872f9` with the local Windows stdio-guard and HDRP history-renderer changes in the working tree. The HDRP renderer package and its Distribution copy matched SHA-256 `3360985F2213701006009A63DAF705C72076BDA4C57514A70D7A6543DBF56346`; the Bridge shim remained `4D28B54BC09FFCF0BC2E6F9E645E9A04C6EF59210C79BB784872655DA1837F7A`.

| Run | Mode and fixture | API | NUnit | CLI exit | Result |
| --- | --- | --- | --- | --- | --- |
| `.local/windows-unity-crash-20261011/latest-unity-fix/playmode-d3d11/` | PlayMode, same selected URP/HDRP suites | D3D11 | 22/22 passed, 0 failed, 0 skipped | 0 | The prior exposure and HDRP Custom Pass failures did not recur |
| `.local/windows-unity-crash-20261011/latest-unity-fix/playmode-d3d12/` | PlayMode, same selected URP/HDRP suites | D3D12 | 22/22 passed, 0 failed, 0 skipped | 0 | All selected cases passed |
| `.local/windows-unity-crash-20261011/latest-unity-fix/editmode-hdrp-d3d11-device-pause/` | EditMode, HDRP device pause/resume | D3D11 | 1/1 passed, 0 skipped | 0 | Shim/runtime captured in the Editor; native cleanup completed |

The HDRP test fixture now waits for bounded exposure readiness and finite samples before evaluating its unchanged response assertion, and emits ASCII timeout diagnostics. The Custom Pass fixture also checks restoration of its scoped state on normal and exceptional paths. The shared HDRP renderer retargets the source/reference-targeted `CustomPassVolume.targetCamera` to each view during rendering and restores it afterward, including exception paths. The two exposure tests passed after the fixture synchronization change, so no runtime exposure adjustment was needed for these Windows checks. In the real-device run, strict Editor selection and PID cross-checking succeeded, the loaded package shim path/hash matched, the official Bridge 2.6.3 runtime was present, native display count was one, pending tokens were zero, and uninitialization succeeded. The Editor and CLI exited cleanly; no Application 1000/1001 event or crash report was found. Settings generated by Unity were recorded and restored after each run. These results cover the listed Windows cases; they do not verify Metal or resolve unrelated crash buckets.
