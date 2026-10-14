# Quick Start Play startup validation — 2026-10-14

The URP and HDRP Samples saved both output components' `startOnEnable` as false, while the normal Bridge Rig creation menu enables the active platform's output. Edit Mode Preview closes when entering Play, so the old Samples then stayed idle. This reproduced in both pipelines on macOS. Calling `StartOutput()` manually worked.

Both Sample scenes now save the Metal and Windows startup flags as true. They were saved through the live Unity Editor API. The platform-selection helper and runtime code are unchanged: a user's explicit OFF setting remains respected. Unity also serialized the adapters' current default history/exposure/budget fields, Automatic subpixel mode, and empty Editor identity fields. Existing Sample copies under `Assets/Samples` need reimporting or their startup option enabled; back up edited scenes first.

## Evidence and scope

[Structured command results and hashes](../validation/quick-start-play-startup-20261014.json) contain 26 selected phase payloads, source identities, Console observations, and restoration checks. The code basis was `5a3dd3f64d67161486e46ec335e4d2f4160ee3e3` plus the uncommitted Sample scene changes; the JSON records the actual Git-derived full revision and file hashes. These were local UPM imports on Unity 6000.6.4f1, macOS 26.6.2, Metal, URP/HDRP 17.6.0, Quarter view density and Balanced output. Domain Reload was disabled and Scene Reload enabled; those existing Editor options were unchanged.

| Check | URP | HDRP |
| --- | --- | --- |
| Old Sample in Play after device Preview | Auto-start false; output idle; 0 submitted/completed | Auto-start false; output idle; 0 submitted/completed |
| Fixed Edit Mode device Preview | 9 rendered / 9 presented at checkpoint | 26 rendered / 26 presented at checkpoint |
| Fixed Play without manual Start | Running; 209 → 1,335 submitted; 208 → 1,334 completed | Running; 12 → 319 submitted; 12 → 318 completed |
| Dropped frames at those two Play checkpoints | 194 → 1,219 | 12 → 316 |
| Animation | Cube and Sphere Animator times/transforms progressed | Cube and Sphere Animator times/transforms progressed |
| Explicit auto-start OFF | Output idle; 0 submitted/completed | Output idle; 0 submitted/completed |
| After Play stop | Output stopped; pending Metal/Preview resources 0/0 | Output stopped; pending Metal/Preview resources 0/0 |
| Scene integrity | Valid Rig; missing scripts 0; error shaders/materials 0 | Valid Rig; missing scripts 0; error shaders/materials 0 |

These are live Editor observations, not NUnit test counts or a performance benchmark. Completed work and dropped frames are separate counters; completed work does not mean every frame was presented. The connected display and Bridge geometry were available, but no new physical optical comparison was performed.

The actual Game View, Hierarchy, Rig Inspector and Console were inspected in both Editors. URP's observed current Console had 0 errors/warnings; its historical capture buffer retained 21 warnings. HDRP's saved current Console had 0 errors and 1 warning, with 23 historical buffer warnings: `ScriptableRuntimeReflectionSystemSettings.system is assigned more than once. Only the last instance will be used.` Compilation was not failed. Earlier capture entries are not erased or counted as a clean session. Some CLI eval attempts had corrected C# API/name mistakes; those command failures are not test passes or project compilation failures.

## Restoration and delivery

Both projects returned to their original clean scene. Original imported Sample contents and root metadata were restored from pre-run copies; original scene, manifest, package lock and EditorSettings hashes matched. Both Editors exited normally with output stopped and pending resources zero. The source scene copies differ from their Unity-saved tested files only by trailing whitespace normalization. Generated distributions copy those scenes without further semantic changes.

The committed public JSON is an extraction, with host paths and eval command bodies omitted. Its original command hashes/bytes identify full responses retained in ignored `.local/quickstart-play-20261014/`; they are not hashes of the public extraction. Those local files and project backups are not release assets.

Windows receives the shared scene defaults but was not executed in this run. Quick Start Player builds, other reload options, different graphics APIs/devices, output frame rate, physical optical quality and anonymous Git installation remain outside this check. Use the [current matrix](../ja/unity-feature-platform-matrix.html) for other feature evidence; earlier manual-start results remain historical.
