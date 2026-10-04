using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using ViewWeave;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace ViewWeave.PlayerValidation
{
    public sealed class PlayerValidationRunner : MonoBehaviour
    {
        const int TileWidth = 128;
        const int TileHeight = 96;
        const double CaseTimeout = 120.0;
        static readonly ProfilerCategory GrdCategory = new ProfilerCategory("GPU Resident Drawer");

        [SerializeField] RenderPipelineAsset grdOffPipeline;
        [SerializeField] RenderPipelineAsset grdOnPipeline;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] Camera sourceCamera;
        [SerializeField] Transform dynamicSphere;
        [SerializeField] PlayerValidationOutput output;

        readonly List<CaseEvidence> cases = new List<CaseEvidence>();
        string evidenceDirectory;
        string mode;
        string shutdown;
        bool verbose;
        bool captureEnabled;
        ProfilerRecorder renderers;
        ProfilerRecorder instances;
        ProfilerRecorder gcAllocated;
        bool profilerWasEnabled;
        bool categoryWasEnabled;
        int exitCode;
        LifecycleEvidence lifecycle = new LifecycleEvidence();
        BaselineEvidence baseline;
        readonly List<BenchmarkEvidence> benchmarks = new List<BenchmarkEvidence>();
        readonly FrameTiming[] timingBuffer = new FrameTiming[1];

        void Awake()
        {
            Application.runInBackground = true;
            evidenceDirectory = Argument("--evidence-dir");
            mode = Argument("--mode") ?? "validation";
            shutdown = Argument("--shutdown") ?? "normal";
            verbose = Argument("--verbose") == "1";
            var capture = Argument("--profiler-capture");
            if (!string.IsNullOrEmpty(capture) && capture != "on" && capture != "off") { Fatal("invalid profiler-capture"); return; }
            captureEnabled = capture != "off";
            if (string.IsNullOrEmpty(evidenceDirectory)) evidenceDirectory = Path.Combine(Application.dataPath, "..", "..", ".local", "grd-player-validation");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            Directory.CreateDirectory(evidenceDirectory);
            StartCoroutine(RunGuarded());
        }

        IEnumerator RunGuarded()
        {
            profilerWasEnabled = Profiler.enabled;
            categoryWasEnabled = Profiler.IsCategoryEnabled(GrdCategory);
            Profiler.enabled = captureEnabled;
            Profiler.SetCategoryEnabled(GrdCategory, true);
            if (Application.isEditor || !Debug.isDebugBuild || !SystemInfo.graphicsMultiThreaded)
            {
                exitCode = 1;
                WriteText("fatal-error.txt", "development player with graphicsMultiThreaded is required");
                Profiler.SetCategoryEnabled(GrdCategory, categoryWasEnabled);
                Profiler.enabled = profilerWasEnabled;
                WriteSummary();
                Marker("quit");
                Application.Quit(exitCode);
                yield break;
            }
            if (mode != "validation" && mode != "baseline" && mode != "benchmark") { Fatal("invalid mode"); yield break; }
            if (shutdown != "normal" && shutdown != "pipeline-first") { Fatal("invalid shutdown"); yield break; }
            try
            {
                if (mode == "baseline")
                {
                yield return Drive(RunBaseline(), null);
                }
                else if (mode == "benchmark")
                {
                    yield return Drive(RunBenchmark(), null);
                }
                else foreach (var grd in new[] { false, true }) foreach (var views in new[] { 1, 8, 48 })
                {
                    var evidence = new CaseEvidence { views = views, grdEnabled = grd, buildGUID = Application.buildGUID, isEditor = Application.isEditor, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion };
                    yield return Drive(RunCase(evidence), evidence);
                    cases.Add(evidence);
                    if (!evidence.ok) exitCode = 1;
                    yield return null;
                }
                if (mode == "validation")
                {
                    yield return Drive(RunLifecycle(), null);
                    if (!lifecycle.ok) exitCode = 1;
                }
                if (shutdown == "pipeline-first")
                {
                    Marker("shutdown.pipeline-first.begin");
                    quilt.enabled = false;
                    GraphicsSettings.defaultRenderPipeline = null;
                    QualitySettings.renderPipeline = null;
                    yield return null;
                    Marker("shutdown.pipeline-first.end");
                }
            }
            finally
            {
                DisposeRecorders();
                Profiler.SetCategoryEnabled(GrdCategory, categoryWasEnabled);
                Profiler.enabled = profilerWasEnabled;
                WriteSummary();
                Application.Quit(exitCode);
            }
        }

        IEnumerator Drive(IEnumerator iterator, CaseEvidence evidence)
        {
            try
            {
                while (true)
                {
                    bool moved;
                    try { moved = iterator.MoveNext(); }
                    catch (Exception error)
                    {
                        exitCode = 1;
                        if (evidence != null) { evidence.error = error.GetType().Name + ": " + error.Message; evidence.ok = false; WriteCase(evidence); }
                        else lifecycle.error = error.GetType().Name + ": " + error.Message;
                        yield break;
                    }
                    if (!moved) yield break;
                    yield return iterator.Current;
                }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
        }

        IEnumerator RunCase(CaseEvidence evidence)
        {
            var started = Time.realtimeSinceStartupAsDouble;
            Marker("case.begin." + evidence.grdEnabled + "." + evidence.views);
            try
            {
                GraphicsSettings.defaultRenderPipeline = evidence.grdEnabled ? grdOnPipeline : grdOffPipeline;
                QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
                sourceCamera.enabled = false;
                ConfigureViews(evidence.views);
                dynamicSphere.localPosition = new Vector3(-0.6f, 0.5f, -0.5f);
                quilt.autoRender = false;
                DisposeRecorders();
                yield return null;
                yield return null;
                quilt.RenderQuilt();
                yield return null;
                if (evidence.grdEnabled) StartRecorders();
                quilt.autoRender = true;
                for (var i = 0; i < 10; ++i) yield return null;
                if (TimedOut(started)) throw new TimeoutException("case timeout");
                var before = Capture(quilt.QuiltTexture);
                var beforeCentroids = GreenCentroids(before, quilt.QuiltTexture.width, quilt.settings.GetLayout());
                SavePng(evidence, before, "before");
                dynamicSphere.localPosition += Vector3.right * 1.2f;
                evidence.mutationFrame = Time.frameCount;
                yield return null;
                var after = Capture(quilt.QuiltTexture);
                var afterCentroids = GreenCentroids(after, quilt.QuiltTexture.width, quilt.settings.GetLayout());
                SavePng(evidence, after, "after");
                evidence.renderFrame = output.LastRenderedFrame;
                evidence.grdRenderers = renderers.Valid ? renderers.LastValue : 0;
                evidence.visibleInstances = instances.Valid ? instances.LastValue : 0;
                evidence.beforeCentroids = beforeCentroids;
                evidence.afterCentroids = afterCentroids;
                evidence.ok = !TimedOut(started) && evidence.renderFrame == evidence.mutationFrame && AllMoved(beforeCentroids, afterCentroids) && (!evidence.grdEnabled || (evidence.grdRenderers > 0 && evidence.visibleInstances > 0));
                if (!evidence.ok) evidence.error = "frame, centroid, timeout, or GRD counter validation failed";
                WriteCase(evidence);
            }
            finally
            {
                quilt.autoRender = false;
                DisposeRecorders();
                Marker("case.end." + evidence.grdEnabled + "." + evidence.views);
            }
        }

        IEnumerator RunBaseline()
        {
            var evidence = new BaselineEvidence { mode = mode, buildGUID = Application.buildGUID, graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion };
            baseline = evidence;
            Marker("baseline.begin");
            quilt.enabled = false;
            var baselineGrd = Argument("--grd");
            if (!string.IsNullOrEmpty(baselineGrd) && baselineGrd != "off" && baselineGrd != "on") { Fatal("invalid grd"); yield break; }
            var baselinePipeline = baselineGrd == "on" ? grdOnPipeline : grdOffPipeline;
            GraphicsSettings.defaultRenderPipeline = baselinePipeline;
            QualitySettings.renderPipeline = baselinePipeline;
            sourceCamera.enabled = true;
            for (var i = 0; i < 30; ++i) yield return null;
            evidence.cameraEnabled = sourceCamera.enabled;
            evidence.activeTextureName = sourceCamera.targetTexture == null ? "null" : sourceCamera.targetTexture.name;
            evidence.renderTextureActiveName = RenderTexture.active == null ? "null" : RenderTexture.active.name;
            evidence.actualActiveName = evidence.renderTextureActiveName;
            evidence.quiltTextureOwned = quilt.QuiltTexture != null;
            evidence.activeTextureIsQuilt = RenderTexture.active != null && quilt.QuiltTexture != null && ReferenceEquals(RenderTexture.active, quilt.QuiltTexture);
            evidence.frame = Time.frameCount;
            evidence.ok = evidence.cameraEnabled && evidence.activeTextureName == "null";
            if (!evidence.ok) exitCode = 1;
            WriteText("baseline.json", JsonUtility.ToJson(evidence, true));
            Marker("baseline.end");
        }

        IEnumerator RunBenchmark()
        {
            var requestedViewsText = Argument("--views");
            var requestedViews = 48;
            int parsedViews = 48;
            if (!string.IsNullOrEmpty(requestedViewsText) && (!int.TryParse(requestedViewsText, out parsedViews) || (parsedViews != 1 && parsedViews != 8 && parsedViews != 48))) { Fatal("invalid views"); yield break; }
            if (!string.IsNullOrEmpty(requestedViewsText)) requestedViews = parsedViews;
            var requestedGrd = Argument("--grd");
            if (!string.IsNullOrEmpty(requestedGrd) && requestedGrd != "off" && requestedGrd != "on") { Fatal("invalid grd"); yield break; }
            var oldVsync = QualitySettings.vSyncCount;
            var oldTargetRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            try
            {
            var raw = new RawSample[3000];
            foreach (var grd in new[] { false, true })
            {
                if (requestedGrd == "off" && grd) continue;
                if (requestedGrd == "on" && !grd) continue;
                var evidence = new BenchmarkEvidence { views = requestedViews, grdEnabled = grd, warmupFrames = 120, runs = 3, buildGUID = Application.buildGUID, graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion };
                GraphicsSettings.defaultRenderPipeline = grd ? grdOnPipeline : grdOffPipeline;
                QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
                ConfigureViews(requestedViews);
                quilt.autoRender = false;
                gcAllocated = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame", 15);
                yield return null;
                yield return null;
                if (grd) StartRecorders();
                quilt.RenderQuilt();
                quilt.autoRender = true;
                for (var i = 0; i < evidence.warmupFrames; ++i) yield return null;
                var lastFrame = -1;
                for (var run = 0; run < evidence.runs; ++run)
                {
                    var runStart = Time.realtimeSinceStartupAsDouble;
                    for (var frame = 0; frame < 1000; ++frame)
                    {
                        if (Time.realtimeSinceStartupAsDouble - runStart > 120.0) throw new TimeoutException("benchmark run timeout");
                        yield return null;
                        var renderedFrame = output.LastRenderedFrame;
                        if (lastFrame >= 0 && renderedFrame != lastFrame + 1) evidence.duplicateFrames++;
                        if (renderedFrame != Time.frameCount - 1) evidence.frameMismatch++;
                        lastFrame = renderedFrame;
                        var tick = quilt.LastCpuSubmissionTicks;
                        raw[run * 1000 + frame] = default;
                        raw[run * 1000 + frame].submissionTicks = tick;
                        raw[run * 1000 + frame].frame = renderedFrame;
                        raw[run * 1000 + frame].gcBytes = gcAllocated.Valid ? gcAllocated.LastValue : 0;
                        FrameTimingManager.CaptureFrameTimings();
                        if (FrameTimingManager.GetLatestTimings(1, timingBuffer) > 0)
                        {
                            evidence.frameTimingAvailable = true;
                            evidence.cpuMainMs = timingBuffer[0].cpuMainThreadFrameTime;
                            evidence.cpuRenderMs = timingBuffer[0].cpuRenderThreadFrameTime;
                            evidence.gpuMs = timingBuffer[0].gpuFrameTime;
                            raw[run * 1000 + frame].cpuMainMs = evidence.cpuMainMs;
                            raw[run * 1000 + frame].cpuRenderMs = evidence.cpuRenderMs;
                            raw[run * 1000 + frame].gpuMs = evidence.gpuMs;
                            raw[run * 1000 + frame].timingTimestamp = timingBuffer[0].frameStartTimestamp;
                            raw[run * 1000 + frame].timing = true;
                        }
                    }
                }
                var samples = new long[1000];
                evidence.distributions = new RunDistribution[3];
                evidence.sampleCount = 3000;
                evidence.rawSamples = (RawSample[])raw.Clone();
                for (var run = 0; run < 3; ++run) { var distribution = new RunDistribution(); distribution.submissionMs = new double[1000]; distribution.gcBytes = new long[1000]; for (var frame = 0; frame < 1000; ++frame) { samples[frame] = raw[run * 1000 + frame].submissionTicks; distribution.submissionMs[frame] = raw[run * 1000 + frame].submissionTicks * 1000.0 / Stopwatch.Frequency; distribution.gcBytes[frame] = raw[run * 1000 + frame].gcBytes; } Array.Sort(samples); evidence.runP50Ticks[run] = samples[500]; evidence.runP95Ticks[run] = samples[950]; evidence.runSampleCounts[run] = 1000; evidence.distributions[run] = distribution; }
                var allSamples = new long[3000];
                for (var frame = 0; frame < 3000; ++frame) allSamples[frame] = raw[frame].submissionTicks;
                Array.Sort(allSamples);
                evidence.p50Ticks = allSamples[1500];
                evidence.p95Ticks = allSamples[2850];
                evidence.millisecondsP50 = evidence.p50Ticks * 1000.0 / Stopwatch.Frequency;
                evidence.millisecondsP95 = evidence.p95Ticks * 1000.0 / Stopwatch.Frequency;
                evidence.gcAllocatedAvailable = gcAllocated.Valid;
                evidence.gcAllocatedBytes = gcAllocated.Valid ? gcAllocated.LastValue : 0;
                evidence.grdRenderers = renderers.Valid ? renderers.LastValue : 0;
                evidence.visibleInstances = instances.Valid ? instances.LastValue : 0;
                evidence.grdCounterAvailable = !grd || (evidence.grdRenderers > 0 && evidence.visibleInstances > 0);
                evidence.gpuTimingAvailable = evidence.frameTimingAvailable && evidence.gpuMs > 0.0 && !double.IsNaN(evidence.gpuMs) && !double.IsInfinity(evidence.gpuMs);
                evidence.ok = evidence.sampleCount == 3000 && evidence.duplicateFrames == 0 && evidence.frameMismatch == 0 && evidence.grdCounterAvailable;
                if (!evidence.ok) exitCode = 1;
                benchmarks.Add(evidence);
                WriteText("benchmark-grd-" + grd.ToString().ToLowerInvariant() + "-views-" + requestedViews + ".json", JsonUtility.ToJson(evidence, true));
            }
            }
            finally
            {
                QualitySettings.vSyncCount = oldVsync;
                Application.targetFrameRate = oldTargetRate;
                quilt.autoRender = false;
                DisposeRecorders();
            }
        }

        IEnumerator RunLifecycle()
        {
            Marker("lifecycle.begin");
            try
            {
                ConfigureViews(8);
                quilt.autoRender = false;
                quilt.RenderQuilt();
                var initial = output.SubmissionCount;
                quilt.autoRender = true;
                yield return null;
                lifecycle.startedCount = output.SubmissionCount;
                quilt.enabled = false;
                yield return null;
                lifecycle.disabledCount = output.SubmissionCount;
                quilt.enabled = true;
                yield return null;
                lifecycle.restartedCount = output.SubmissionCount;
                quilt.autoRender = false;
                yield return null;
                lifecycle.autoStoppedCount = output.SubmissionCount;
                lifecycle.ok = lifecycle.startedCount > initial && lifecycle.disabledCount == lifecycle.startedCount && lifecycle.restartedCount == lifecycle.disabledCount + 1 && lifecycle.autoStoppedCount == lifecycle.restartedCount;
                if (!lifecycle.ok) lifecycle.error = "automatic render lifecycle failed";
            }
            finally { quilt.autoRender = false; Marker("lifecycle.end"); }
            WriteText("lifecycle.json", JsonUtility.ToJson(lifecycle, true));
        }

        void ConfigureViews(int views)
        {
            quilt.settings.columns = views == 1 ? 1 : views == 8 ? 4 : 8;
            quilt.settings.rows = views == 1 ? 1 : views == 8 ? 2 : 6;
            quilt.settings.views = views;
            quilt.settings.quiltWidth = TileWidth * quilt.settings.columns;
            quilt.settings.quiltHeight = TileHeight * quilt.settings.rows;
            quilt.settings.viewAspect = 4f / 3f;
            quilt.settings.cameraDistance = 4f;
        }

        void StartRecorders() { renderers = ProfilerRecorder.StartNew(GrdCategory, "GRD Renderers", 15); instances = ProfilerRecorder.StartNew(GrdCategory, "Visible Instances", 15); }
        void DisposeRecorders() { if (renderers.Valid) renderers.Dispose(); if (instances.Valid) instances.Dispose(); if (gcAllocated.Valid) gcAllocated.Dispose(); renderers = default; instances = default; gcAllocated = default; }
        bool TimedOut(double start) { return Time.realtimeSinceStartupAsDouble - start > CaseTimeout; }

        static Color32[] Capture(RenderTexture source) { var old = RenderTexture.active; var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false); try { RenderTexture.active = source; texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); texture.Apply(false, false); return texture.GetPixels32(); } finally { RenderTexture.active = old; Destroy(texture); } }
        static Vector2[] GreenCentroids(Color32[] image, int width, QuiltLayout layout) { var result = new Vector2[layout.ViewCount]; for (var i = 0; i < result.Length; ++i) { var tile = layout.GetTile(i); var sum = Vector2.zero; var count = 0; for (var y = tile.y; y < tile.yMax; ++y) for (var x = tile.x; x < tile.xMax; ++x) { var p = image[y * width + x]; if (p.a > 0 && p.g > 15 && p.g > p.r * 1.5f && p.g > p.b * 1.5f) { sum += new Vector2(x - tile.x, y - tile.y); count++; } } result[i] = count == 0 ? new Vector2(-1f, -1f) : sum / count; } return result; }
        static bool AllMoved(Vector2[] before, Vector2[] after) { if (before.Length != after.Length) return false; for (var i = 0; i < before.Length; ++i) if (before[i].x < 0f || after[i].x - before[i].x <= 1f) return false; return true; }
        void SavePng(CaseEvidence evidence, Color32[] pixels, string suffix) { var width = evidence.views == 1 ? TileWidth : TileWidth * (evidence.views == 8 ? 4 : 8); var height = TileHeight * (evidence.views == 1 ? 1 : evidence.views == 8 ? 2 : 6); var texture = new Texture2D(width, height, TextureFormat.RGBA32, false); texture.SetPixels32(pixels); texture.Apply(false, false); File.WriteAllBytes(Path.Combine(evidenceDirectory, CaseName(evidence) + "-" + suffix + ".png"), texture.EncodeToPNG()); Destroy(texture); }
        void WriteCase(CaseEvidence evidence) { WriteText(CaseName(evidence) + ".json", JsonUtility.ToJson(evidence, true)); }
        void WriteSummary() { var summary = new SummaryEvidence { mode = mode, profilerCaptureEnabled = captureEnabled, stopwatchFrequency = Stopwatch.Frequency, tileWidth = TileWidth, tileHeight = TileHeight, meshRendererCount = FindObjectsOfType<Renderer>().Length, qualityLevel = QualitySettings.GetQualityLevel(), targetFrameRate = Application.targetFrameRate, vSyncCount = QualitySettings.vSyncCount, buildGUID = Application.buildGUID, unityVersion = Application.unityVersion, gpu = SystemInfo.graphicsDeviceName, graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, isEditor = Application.isEditor, development = Debug.isDebugBuild, exitCode = exitCode, lifecycle = lifecycle, baseline = baseline, cases = cases.ToArray(), benchmarks = benchmarks.ToArray() }; var modeOk = mode == "validation" ? lifecycle.ok && cases.Count == 6 : mode == "benchmark" ? benchmarks.Count > 0 : baseline != null && baseline.ok; summary.overallOk = exitCode == 0 && modeOk && summary.development && !summary.isEditor && summary.graphicsMultiThreaded; foreach (var item in cases) summary.overallOk &= item.ok; foreach (var item in benchmarks) summary.overallOk &= item.ok; WriteText("summary.json", JsonUtility.ToJson(summary, true)); }
        void WriteText(string name, string text) { File.WriteAllText(Path.Combine(evidenceDirectory, name), text); }
        static string CaseName(CaseEvidence e) { return "urp-grd-" + e.grdEnabled.ToString().ToLowerInvariant() + "-views-" + e.views; }
        static string Argument(string key) { var args = Environment.GetCommandLineArgs(); for (var i = 0; i < args.Length - 1; ++i) if (args[i] == key) return args[i + 1]; return null; }
        static int ParseInt(string value, int fallback) { int result; return int.TryParse(value, out result) && result > 0 ? result : fallback; }
        void Marker(string value) { if (verbose) Debug.Log("PLAYER_VALIDATION_MARKER " + value); }
        void Fatal(string message) { exitCode = 1; WriteText("fatal-error.txt", message); WriteSummary(); Application.Quit(exitCode); }
        void OnDisable() { Marker("runner.OnDisable"); }
        void OnDestroy() { Marker("runner.OnDestroy"); }

        [Serializable] public sealed class CaseEvidence { public int views; public bool grdEnabled; public bool ok; public string error; public int mutationFrame = -1; public int renderFrame = -1; public long grdRenderers; public long visibleInstances; public Vector2[] beforeCentroids = Array.Empty<Vector2>(); public Vector2[] afterCentroids = Array.Empty<Vector2>(); public string buildGUID; public bool isEditor; public bool graphicsMultiThreaded; public string graphicsDeviceType; public string graphicsDeviceVersion; }
        [Serializable] public sealed class LifecycleEvidence { public bool ok; public string error; public int startedCount; public int disabledCount; public int restartedCount; public int autoStoppedCount; }
        [Serializable] public sealed class SummaryEvidence { public string mode; public bool profilerCaptureEnabled; public long stopwatchFrequency; public int tileWidth; public int tileHeight; public int meshRendererCount; public int qualityLevel; public int targetFrameRate; public int vSyncCount; public string buildGUID; public string unityVersion; public string gpu; public string graphicsDeviceType; public string graphicsDeviceVersion; public bool graphicsMultiThreaded; public bool isEditor; public bool development; public int exitCode; public bool overallOk; public LifecycleEvidence lifecycle; public BaselineEvidence baseline; public CaseEvidence[] cases; public BenchmarkEvidence[] benchmarks; }
        [Serializable] public sealed class BaselineEvidence { public string mode; public string buildGUID; public string graphicsDeviceType; public string graphicsDeviceVersion; public bool cameraEnabled; public string activeTextureName; public string actualActiveName; public string renderTextureActiveName; public bool quiltTextureOwned; public bool activeTextureIsQuilt; public int frame; public bool ok; }
        [Serializable] public struct RawSample { public long submissionTicks; public int frame; public long gcBytes; public double cpuMainMs; public double cpuRenderMs; public double gpuMs; public bool timing; public ulong timingTimestamp; }
        [Serializable] public sealed class RunDistribution { public double[] submissionMs; public long[] gcBytes; }
        [Serializable] public sealed class BenchmarkEvidence { public int views; public bool grdEnabled; public int warmupFrames; public int runs; public int sampleCount; public long p50Ticks; public long p95Ticks; public double millisecondsP50; public double millisecondsP95; public long[] runP50Ticks = new long[3]; public long[] runP95Ticks = new long[3]; public int[] runSampleCounts = new int[3]; public RawSample[] rawSamples; public RunDistribution[] distributions; public int duplicateFrames; public int frameMismatch; public long grdRenderers; public long visibleInstances; public bool grdCounterAvailable; public bool gpuTimingAvailable; public bool frameTimingAvailable; public double cpuMainMs; public double cpuRenderMs; public double gpuMs; public bool gcAllocatedAvailable; public long gcAllocatedBytes; public string buildGUID; public string graphicsDeviceType; public string graphicsDeviceVersion; public bool ok; }
    }
}
