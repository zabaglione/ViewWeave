using System;
using System.Collections;
using System.IO;
using ViewWeave;
using ViewWeave.URP;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using UnityEngine.VFX;
using Unity.Profiling;

namespace ViewWeave.VfxPlayerValidation
{
    public sealed class VfxPlayerValidationRunner : MonoBehaviour
    {
        [SerializeField] RenderPipelineAsset grdOffPipeline;
        [SerializeField] RenderPipelineAsset grdOnPipeline;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] Camera sourceCamera;
        [SerializeField] UrpVfxCameraDriver driver;
        [SerializeField] VisualEffect effect;
        [SerializeField] VFXRenderer effectRenderer;
        [SerializeField] VfxPlayerValidationOutput output;
        string evidenceDirectory;
        int exitCode;
        bool fatalFailure;
        static readonly ProfilerCategory GrdCategory = new ProfilerCategory("GPU Resident Drawer");

        void Awake()
        {
            evidenceDirectory = Argument("--evidence-dir");
            if (string.IsNullOrEmpty(evidenceDirectory)) evidenceDirectory = Path.Combine(Application.dataPath, "..", "..", ".local", "vfx-player-validation");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            Directory.CreateDirectory(evidenceDirectory);
            StartCoroutine(RunGuarded());
        }

        IEnumerator RunGuarded()
        {
            bool profilerWasEnabled = Profiler.enabled;
            bool categoryWasEnabled = Profiler.IsCategoryEnabled(GrdCategory);
            Profiler.enabled = true;
            Profiler.SetCategoryEnabled(GrdCategory, true);
            try { yield return Drive(RunBody()); }
            finally
            {
                Profiler.SetCategoryEnabled(GrdCategory, categoryWasEnabled);
                Profiler.enabled = profilerWasEnabled;
                if (quilt != null) quilt.autoRender = false;
                if (driver != null) driver.enableDriver = false;
                Write("exit.txt", exitCode.ToString());
                Application.Quit(exitCode);
            }
        }

        IEnumerator RunBody()
        {
            yield return null;
            if (Application.isEditor || !Debug.isDebugBuild || !SystemInfo.graphicsMultiThreaded) { FailAndReport("development player with graphicsMultiThreaded is required"); yield break; }
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) { FailAndReport("Direct3D11 graphics device is required"); yield break; }
            if (effectRenderer == null && effect != null) effectRenderer = effect.GetComponent<VFXRenderer>();
            if (quilt == null || sourceCamera == null || driver == null || effect == null || effectRenderer == null || output == null) { FailAndReport("VFX player references are incomplete"); yield break; }
            if (sourceCamera.enabled) { FailAndReport("source camera must be disabled"); yield break; }
            var report = new Evidence { buildGUID = Application.buildGUID, unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, development = Debug.isDebugBuild, isEditor = Application.isEditor };
            bool live = Argument("--live") == "1";
            foreach (var grd in new[] { false, true })
                foreach (var views in live ? new[] { 8, 48 } : new[] { 1, 8, 48 })
                {
                    var item = new CaseEvidence { grdEnabled = grd, views = views, liveMode = live };
                    yield return RunCaseSafe(item, live);
                    report.cases.Add(item);
                    if (fatalFailure) yield break;
                    if (!item.ok) exitCode = 1;
                    yield return null;
                }
            report.exitCode = exitCode;
            report.ok = exitCode == 0;
            Write("summary.json", JsonUtility.ToJson(report, true));
        }

        IEnumerator Drive(IEnumerator iterator)
        {
            try
            {
                while (true)
                {
                    bool moved;
                    object current = null;
                    try
                    {
                        moved = iterator.MoveNext();
                        if (moved) current = iterator.Current;
                    }
                    catch (Exception error)
                    {
                        exitCode = 1;
                        fatalFailure = true;
                        string message = error.GetType().Name + ": " + error.Message;
                        Fail(message);
                        Write("summary.json", JsonUtility.ToJson(new Evidence { buildGUID = Application.buildGUID, unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, development = Debug.isDebugBuild, isEditor = Application.isEditor, exitCode = exitCode, ok = false, error = Sanitize(message) }, true));
                        yield break;
                    }
                    if (!moved) yield break;
                    IEnumerator nested = current as IEnumerator;
                    if (nested != null) yield return Drive(nested);
                    else yield return current;
                }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
        }

        IEnumerator RunCaseSafe(CaseEvidence item, bool live)
        {
            IEnumerator iterator = RunCase(item, live);
            Exception failure = null;
            while (true)
            {
                bool moved;
                object current = null;
                try
                {
                    moved = iterator.MoveNext();
                    if (moved) current = iterator.Current;
                }
                catch (Exception error)
                {
                    failure = error;
                    break;
                }
                if (!moved) break;
                yield return current;
            }
            (iterator as IDisposable)?.Dispose();
            if (failure != null)
            {
                item.error = Sanitize(failure.GetType().Name + ": " + failure.Message);
                item.ok = false;
                exitCode = 1;
            }
        }

        IEnumerator RunCase(CaseEvidence item, bool live)
        {
            Texture2D before = null;
            Texture2D off = null;
            Texture2D after = null;
            ProfilerRecorder renderers = default;
            ProfilerRecorder instances = default;
            try
            {
            GraphicsSettings.defaultRenderPipeline = item.grdEnabled ? grdOnPipeline : grdOffPipeline;
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            ConfigureViews(1);
            quilt.autoRender = false;
            driver.runWhenManualRendering = true;
            driver.enableDriver = true;
            double driverDeadline = Time.realtimeSinceStartupAsDouble + 1.0;
            while (!driver.IsRunning && Time.realtimeSinceStartupAsDouble < driverDeadline) yield return null;
            if (!driver.IsRunning) throw new TimeoutException("driver bootstrap timeout");
            effectRenderer.enabled = true;
            effect.resetSeedOnPlay = false;
            effect.startSeed = 17u;
            effect.pause = false;
            effect.Reinit();
            effect.Play();
            yield return null;
            yield return null;
            int bootstrapFrame = Time.frameCount;
            int bootstrapAlive = effect.aliveParticleCount;
            effect.pause = true;
            driver.runWhenManualRendering = false;
            ConfigureViews(item.views);
            quilt.autoRender = true;
            driver.driverFieldOfView = item.views == 1 ? 50f : 90f;
            driver.enableDriver = true;
            if (item.grdEnabled)
            {
                renderers = ProfilerRecorder.StartNew(GrdCategory, "GRD Renderers", 15);
                instances = ProfilerRecorder.StartNew(GrdCategory, "Visible Instances", 15);
            }
            double simulationStart = Time.realtimeSinceStartupAsDouble;
            int simulationStartFrame = Time.frameCount;
            int simulationSteps = live ? 0 : 30;
            if (live)
            {
                effect.pause = false;
                double liveDeadline = Time.realtimeSinceStartupAsDouble + 1.0;
                int liveFrames = 0;
                while (Time.realtimeSinceStartupAsDouble < liveDeadline && (liveFrames < 2 || effect.aliveParticleCount <= 0))
                {
                    ++liveFrames;
                    yield return null;
                }
                if (effect.aliveParticleCount <= 0) throw new TimeoutException("live particle bootstrap timeout");
            }
            else
                for (var i = 0; i < simulationSteps; ++i)
                {
                    effect.Simulate(0.02f, 1);
                    yield return null;
                }
            float simulationWallTimeSeconds = (float)(Time.realtimeSinceStartupAsDouble - simulationStart);
            int simulationEndFrame = Time.frameCount;
            if (!live) effect.pause = true;
            double aliveDeadline = Time.realtimeSinceStartupAsDouble + 1.0;
            while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartupAsDouble < aliveDeadline) yield return null;
            if (live && effect.pause) throw new InvalidOperationException("live capture paused before capture");
            output.RequestCapture();
            double beforeDeadline = Time.realtimeSinceStartupAsDouble + 2.0;
            while (output.CapturedImage == null && Time.realtimeSinceStartupAsDouble < beforeDeadline) yield return null;
            if (output.CapturedImage == null) throw new TimeoutException("before capture timeout");
            before = output.TakeCapture(out int beforeFrame);
            bool beforeFrameMatches = output.LastRenderedFrame == beforeFrame;
            int beforeAlive = effect.aliveParticleCount;
            effectRenderer.enabled = false;
            if (live && effect.pause) throw new InvalidOperationException("live capture paused before off capture");
            output.RequestCapture();
            double offDeadline = Time.realtimeSinceStartupAsDouble + 2.0;
            while (output.CapturedImage == null && Time.realtimeSinceStartupAsDouble < offDeadline) yield return null;
            if (output.CapturedImage == null) throw new TimeoutException("off capture timeout");
            off = output.TakeCapture(out int offFrame);
            bool offFrameMatches = output.LastRenderedFrame == offFrame;
            int offAlive = effect.aliveParticleCount;
            effectRenderer.enabled = true;
            for (var i = 0; i < 3; ++i) yield return null;
            if (live && effect.pause) throw new InvalidOperationException("live capture paused before after capture");
            output.RequestCapture();
            double afterDeadline = Time.realtimeSinceStartupAsDouble + 2.0;
            while (output.CapturedImage == null && Time.realtimeSinceStartupAsDouble < afterDeadline) yield return null;
            if (output.CapturedImage == null) throw new TimeoutException("after capture timeout");
            after = output.TakeCapture(out int afterFrame);
            bool afterFrameMatches = output.LastRenderedFrame == afterFrame;
            int afterAlive = effect.aliveParticleCount;
            item.changedTiles = CountChanged(off, after, quilt.settings.GetLayout(), 20);
            item.dynamicChangedTiles = live ? CountChanged(before, after, quilt.settings.GetLayout(), 20) : Array.Empty<int>();
            item.aliveParticleCount = effect.aliveParticleCount;
            item.simulationSteps = simulationSteps;
            item.simulationSeconds = simulationSteps * 0.02f;
            item.simulationWallTimeSeconds = simulationWallTimeSeconds;
            item.simulationStartFrame = simulationStartFrame;
            item.simulationEndFrame = simulationEndFrame;
            item.bootstrapFrame = bootstrapFrame;
            item.bootstrapAliveParticleCount = bootstrapAlive;
            item.controlNonBlank = CountGreen(off) > 20;
            item.driverRunning = driver.IsRunning;
            item.sourceCameraEnabled = sourceCamera.enabled;
            item.renderFrame = output.LastRenderedFrame;
            item.captureFrame = afterFrame;
            item.beforeCaptureFrame = beforeFrame;
            item.offCaptureFrame = offFrame;
            item.captureFramesMatch = beforeFrameMatches && offFrameMatches && afterFrameMatches;
            item.beforeAliveParticleCount = beforeAlive;
            item.offAliveParticleCount = offAlive;
            item.afterAliveParticleCount = afterAlive;
            item.grdRenderers = renderers.Valid ? renderers.LastValue : 0;
            item.visibleInstances = instances.Valid ? instances.LastValue : 0;
            item.grdCountersAvailable = !item.grdEnabled || (renderers.Valid && instances.Valid);
            item.ok = item.driverRunning && !item.sourceCameraEnabled && item.aliveParticleCount > 0 && item.controlNonBlank && item.renderFrame == item.captureFrame && item.captureFramesMatch && AllAbove(item.changedTiles, 20);
            if (live) item.ok &= !effect.pause && AllAbove(item.dynamicChangedTiles, 20) && item.beforeAliveParticleCount > 0 && item.offAliveParticleCount > 0 && item.afterAliveParticleCount > 0;
            if (item.grdEnabled) item.ok &= item.grdCountersAvailable && item.grdRenderers > 0 && item.visibleInstances > 0;
            if (!item.ok && string.IsNullOrEmpty(item.error))
                item.error = "validation failed; driver=" + item.driverRunning + "; alive=" + item.aliveParticleCount + "; control=" + item.controlNonBlank + "; capture=" + item.captureFrame + "; tiles=" + string.Join(",", item.changedTiles);
            quilt.autoRender = false;
            yield return null;
            item.autoRenderStopped = !driver.IsRunning;
            RenderTexture previousDriverTexture = driver.DriverCamera == null ? null : driver.DriverCamera.targetTexture;
            driver.enabled = false;
            bool releasedImmediately = previousDriverTexture == null || !previousDriverTexture.IsCreated();
            yield return null;
            item.driverStopped = !driver.IsRunning && driver.DriverCamera == null && releasedImmediately;
            driver.enabled = true;
            driver.enableDriver = true;
            quilt.autoRender = true;
            yield return null;
            item.driverRestarted = driver.IsRunning;
            item.ok &= item.autoRenderStopped && item.driverStopped && item.driverRestarted;
            Write("urp-grd-" + item.grdEnabled.ToString().ToLowerInvariant() + "-views-" + item.views + ".json", JsonUtility.ToJson(item, true));
            Save(before, "urp-grd-" + item.grdEnabled.ToString().ToLowerInvariant() + "-views-" + item.views + "-before.png");
            Save(off, "urp-grd-" + item.grdEnabled.ToString().ToLowerInvariant() + "-views-" + item.views + "-off.png");
            Save(after, "urp-grd-" + item.grdEnabled.ToString().ToLowerInvariant() + "-views-" + item.views + "-after.png");
            }
            finally
            {
                if (renderers.Valid) renderers.Dispose();
                if (instances.Valid) instances.Dispose();
                if (before != null) Destroy(before);
                if (off != null) Destroy(off);
                if (after != null) Destroy(after);
                quilt.autoRender = false;
                driver.enableDriver = false;
            }
        }

        void ConfigureViews(int views)
        {
            quilt.settings.views = views;
            quilt.settings.columns = views == 1 ? 1 : views == 8 ? 4 : 8;
            quilt.settings.rows = views == 1 ? 1 : views == 8 ? 2 : 6;
            quilt.settings.quiltWidth = 128 * quilt.settings.columns;
            quilt.settings.quiltHeight = 96 * quilt.settings.rows;
            quilt.settings.viewAspect = 128f / 96f;
            quilt.settings.cameraDistance = 3f;
        }

        Texture2D CaptureQuilt()
        {
            if (quilt.QuiltTexture == null) throw new InvalidOperationException("quilt texture is unavailable");
            var old = RenderTexture.active;
            var image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = quilt.QuiltTexture;
                image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
                image.Apply(false, false);
            }
            finally { RenderTexture.active = old; }
            return image;
        }

        static int[] CountChanged(Texture2D before, Texture2D after, QuiltLayout layout, int threshold)
        {
            var result = new int[layout.ViewCount];
            var a = before.GetPixels(); var b = after.GetPixels();
            for (var view = 0; view < layout.ViewCount; ++view) { var tile = layout.GetTile(view); for (var y = tile.y; y < tile.yMax; ++y) for (var x = tile.x; x < tile.xMax; ++x) { var index = y * before.width + x; if (Mathf.Abs(a[index].r - b[index].r) + Mathf.Abs(a[index].g - b[index].g) + Mathf.Abs(a[index].b - b[index].b) > .01f) result[view]++; } }
            return result;
        }

        static bool AllAbove(int[] values, int threshold) { for (var i = 0; i < values.Length; ++i) if (values[i] <= threshold) return false; return true; }
        static int CountGreen(Texture2D image)
        {
            int count = 0;
            Color32[] pixels = image.GetPixels32();
            for (int i = 0; i < pixels.Length; ++i)
                if (pixels[i].a > 0 && pixels[i].g > 15 && pixels[i].g > pixels[i].r * 1.5f && pixels[i].g > pixels[i].b * 1.5f) count++;
            return count;
        }
        void Save(Texture2D image, string name) { File.WriteAllBytes(Path.Combine(evidenceDirectory, name), image.EncodeToPNG()); }
        void Write(string name, string text) { File.WriteAllText(Path.Combine(evidenceDirectory, name), text); }
        void Fail(string message) { exitCode = 1; Write("fatal-error.txt", Sanitize(message)); }
        void FailAndReport(string message)
        {
            Fail(message);
            Write("summary.json", JsonUtility.ToJson(new Evidence { buildGUID = Application.buildGUID, unityVersion = Application.unityVersion, graphicsDevice = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, development = Debug.isDebugBuild, isEditor = Application.isEditor, exitCode = exitCode, ok = false, error = Sanitize(message) }, true));
        }
        static string Argument(string key) { var args = Environment.GetCommandLineArgs(); for (var i = 0; i < args.Length - 1; ++i) if (args[i] == key) return args[i + 1]; return null; }
        static string Sanitize(string value) { if (string.IsNullOrEmpty(value)) return "unknown error"; var chars = new System.Collections.Generic.List<char>(); foreach (var c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c); return new string(chars.ToArray()); }

        [Serializable] public sealed class CaseEvidence { public bool grdEnabled; public int views; public bool liveMode; public bool ok; public string error; public bool driverRunning; public bool autoRenderStopped; public bool driverStopped; public bool driverRestarted; public bool sourceCameraEnabled; public bool controlNonBlank; public bool captureFramesMatch; public bool grdCountersAvailable; public long grdRenderers; public long visibleInstances; public int simulationSteps; public float simulationSeconds; public float simulationWallTimeSeconds; public int simulationStartFrame = -1; public int simulationEndFrame = -1; public int bootstrapFrame = -1; public int bootstrapAliveParticleCount; public int beforeAliveParticleCount; public int offAliveParticleCount; public int afterAliveParticleCount; public int aliveParticleCount; public int renderFrame = -1; public int beforeCaptureFrame = -1; public int offCaptureFrame = -1; public int captureFrame = -1; public int[] changedTiles = Array.Empty<int>(); public int[] dynamicChangedTiles = Array.Empty<int>(); }
        [Serializable] public sealed class Evidence { public string buildGUID; public string unityVersion; public string graphicsDevice; public string graphicsDeviceVersion; public bool graphicsMultiThreaded; public bool development; public bool isEditor; public int exitCode; public bool ok; public string error; public System.Collections.Generic.List<CaseEvidence> cases = new System.Collections.Generic.List<CaseEvidence>(); }
    }
}
