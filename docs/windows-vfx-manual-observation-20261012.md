# Windows HDRP VFX manual display observation (2026-10-12)

The user reported that the `HDRP_VFX_Learning_Templates` scene's VFX appeared in both the Unity Editor view and on the Looking Glass device. This is a user-reported, manual limited display observation, not an automated fixture or a counted test pass.

The scene path is `E:/My project (34)/Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/HDRP_VFX_Learning_Templates.unity`. The project currently uses Unity 6000.6.4f1 and has HDRP 17.7.0 in its manifest, while the imported sample folder is version 17.6.0. These current project facts do not establish the exact package resolution, graphics API, or setup at the time of the observation.

The report establishes only that the user saw this scene in the Editor view and on the physical Looking Glass display. The triggering operation and exact Editor mode were not recorded. The chosen graph, view count, complete Quilt evidence, display settings, and timing/synchronization details were not captured. This observation does not establish general support for arbitrary HDRP VFX Graphs, simulation behavior, view sorting or culling, soft depth, or Player output.

The absence of a dedicated HDRP runtime driver does not invalidate this particular reported display. It remains a narrow manual observation and does not change prior automated canary/matrix results or their counts. No Unity run, device rendering, project asset change, runtime modification, or new validation test was performed for this record. Documentation source basis is revision `11312b8c6cf824d9a7ac7c8940e95cee742fa076` with documentation-only local changes.
