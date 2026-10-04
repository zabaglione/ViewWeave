# Player validation benchmark

Use the same Development Windows player without a Bridge. `--mode baseline` renders only the enabled URP source camera for 30 frames and records the active target texture. `--mode validation` is the normal six-case GRD validation. `--mode benchmark --views 1|8|48 --grd off|on` warms up for 120 frames and records 1000 frames for three runs without texture readback or log output in the measured loop.

Benchmark JSON reports CPU submission tick p50/p95, sample count, GPU timing availability, graphics device information, and build GUID. Missing GPU timing remains unavailable and is never interpreted as zero. The existing scene contains approximately 75 shared-mesh renderers; larger geometry sets can be added later through the builder.

`--shutdown pipeline-first` disables the quilt renderer, clears the active render pipelines, waits one frame, and then quits to compare shutdown ordering. Use `--verbose 1` to emit ASCII lifecycle markers.
