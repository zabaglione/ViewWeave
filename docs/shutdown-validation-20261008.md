# Windows shutdown validation (2026-10-08)

This note records the Windows shutdown lifecycle changes and their current evidence. It does not attribute historical access violations to a particular component or claim they are fixed.

## Shutdown behavior

Normal Play Mode Stop/restart uses the recoverable session path and does not set the process-wide final latch. Windows Editor and Player process quit use a separate final gate: close admission, stop active outputs, close and collect native contexts, join the persistent worker host, then release managed resources only after GPU proof. Legacy D3D11 textures additionally require a per-session `ClosedSafe` result, ACK, and GPU fence/readback proof; global SDK idle is insufficient. macOS Metal and unsupported platforms do not enter this Windows final drain.

The Windows quit gate is asynchronous and bounded to 10 seconds. If completion is not proven, Unity cancels the quit, keeps resource references, and leaves admission closed. It does not retry automatically; a later explicit quit request rechecks status. Once final shutdown starts, admission is irreversible. When finalization completes, the Editor logs that Bridge shutdown is complete and asks the user to close the Editor again. It does not post a window close or invoke a menu command. If Unity's save confirmation is canceled after final completion, Bridge cannot restart in that Editor process; restart the Editor before using Bridge again.

After `FinalizationReady`, the Windows Editor sets a one-shot latch, writes a completion message to the Console, and waits for the user to close the Editor again. This preserves Unity's own close/save confirmation path without programmatically advancing it. The automatic WM_CLOSE implementation was removed because its save-confirmation behavior was not established. Asset import worker processes skip Editor lifecycle hook registration. If `wantsToQuit` is bypassed, a separate one-shot fallback waits up to five seconds for native completion and performs synchronous GPU proof; an incomplete fallback is not successful shutdown.

After updating the ViewWeave package, fully close and restart the Unity Editor to load the matching managed assembly and native shim.

## Historical access violations

| Observation | Faulting location | What it establishes |
| --- | --- | --- |
| URP/HDRP Editor-stop observations, including URP stop on 2026-10-07 | Official `bridge_inproc.dll` 2.6.3.0, RVA `0xF8550`, access violation reading address 0 | Reported module and instruction location; cause unknown. |
| Isolated DX12 playback fixture, 2026-10-07 | `Unity.dll` offset `0x44383df`, exception `0xC0000005`, after the functional playback body passed | A separate faulting location from the Bridge DLL incident; cause unknown. |

Corrected baseline2 at revision `454ce38` exited naturally with code 0 and reproduced neither of the older access violations in the table. That is a non-reproduction, not proof either cause was eliminated. The initial pair of Play fixtures passed their functional bodies and observed native completion, but their Editors then exited with `0xC0000005` in batch, deferred, and interactive runs. A separate SDK-absent `File/Exit` composition also ended in an access violation. Their faulting locations and causes were not established here. These are distinct from the older `bridge_inproc.dll` RVA `0xF8550` and `Unity.dll` offset `0x44383df` observations above. Guard1/guard2 harness stalls and timeouts were invalid/incomplete runs, not access violations, and are not counted as AV evidence.

Early production attempts to invoke `File/Exit` from `EditorApplication.update` and via `delayCall` were superseded after they failed to reliably complete the close. The successful full Bridge fixture used the frozen package containing the automatic WM_CLOSE candidate, while its harness initiated the initial user-quit request through `File/Exit`. The Editor exited naturally, but the successful automatic WM_CLOSE target/post was not directly instrumented. This is historical evidence for the final-drain path, not the current manual second-close notification; neither result establishes a causal fix for any earlier access violation.

## Current checks and limits

| Check | Result | Limit |
| --- | --- | --- |
| Local Windows Native full CTest, Unity 6000.6.4f1 PluginAPI | 6/6 passed in 17.71 s for DLL SHA-256 `81CBF29737B1D1A13FBB3C1EEAB6265F3B0B076F41AB8C23B6E9229B17F0AEA9`; transport reported 349 draws. | Local Native/fake-runtime contract tests; not the installed Bridge runtime. |
| Pinned NativeRenderingPlugin `522254181faf188efa8b50c3e3bf6fce720b26e4` header CTest | 6/6 passed in 17.77 s. | Independently validates the pinned v7 header build, separate from the packaged local Unity v8-preferred DLL. |
| Managed semantic compilation against Unity 6000.6.4f1 references | Passed with 0 errors and 5 warnings. | Compile harness only; it is not an Editor lifecycle test. |
| Shutdown policy tests | 7/7 passed. | Policy-state coverage only. |
| Production PNG observer Editor fixture `case01-probeobserver-ep-fix1` | Functional body passed; native final status was complete at Editor quit; natural exit code 0. | Native trace corroboration was absent. Visual/performance checks and fake consumer GPU ACK were not proven. |
| Full Bridge Editor fixture `case02-native-close1` (historical automatic-close candidate) | Two Play/Stop/domain-reload cycles; native ABI v1 reported Complete, host joined, contexts zero; managed proof showed reaper 0, no unproven resources, owned handles 0, and final-ready true. PID 88016 exited naturally with code 0. | Eight views at 512x256. This earlier package snapshot is evidence for final drain and process exit, not for the current manual notification/second-close flow. Optical quality, performance, and fake consumer GPU ACK were not proven. |
| DX12 Player quit-gate fixture `case03-quitgate2` | Native final join and managed finalization proof passed; Editor and Player exited naturally with code 0. | Native trace corroboration, optical quality, performance, and fake consumer GPU ACK were not proven. |
| Current manual second-close and GUI save/cancel interactions | Incomplete. Scene case 1 and window cases 1/2 ended `OWNER_ABORT`; the prefab case was not run and auto-review denied a retry. | No pass is claimed for the current notification flow, save-prompt cancellation, or restarting a session after a canceled prompt. |
| Corrected baseline2 Editor run, revision `454ce38` | Natural exit, code 0; neither historical access violation reproduced. | Does not exercise the current shutdown path or establish a causal fix for either incident. |

The native contract tests use WARP/fake runtimes. Editor and Player fixtures confirm bounded shutdown behavior under their stated scenarios, not 48-view optical quality, performance, physical display output, or the unresolved historical crash causes. Evidence and trace context are also summarized in [Native shutdown trace](native-shutdown-trace.md).
