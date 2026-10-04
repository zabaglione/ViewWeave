using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using LookingGlass.Community;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace LookingGlass.BridgeWorkerValidation
{
    public sealed class BridgeWorkerValidationRunner : MonoBehaviour
    {
        const int RequiredFrames = 120;
        const double TimeoutSeconds = 120.0;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] BridgeWorkerQuiltOutput output;
        [SerializeField] ViewWeaveRig rig;
        [SerializeField] ViewWeaveBridgeSession session;
        [SerializeField] RenderPipelineAsset grdOffPipeline;
        [SerializeField] RenderPipelineAsset grdOnPipeline;
        [SerializeField] Camera sourceCamera;
        [SerializeField] Transform dynamicSphere;
        string evidenceDirectory;
        string runtimePath;
        double holdSeconds;
        bool argumentsValid = true;
        string argumentError;
        string qualityName = "preview";
        string grdName = "off";
        string expectedPipeline = "urp";
        bool exerciseRestart;
        bool qualityCycleRequested;
        int exitCode;
        Evidence evidence;
        static readonly ProfilerCategory GrdCategory = new ProfilerCategory("GPU Resident Drawer");
        ProfilerRecorder grdRenderers;
        ProfilerRecorder visibleInstances;
        bool grdRecordersStarted;
        bool profilerWasEnabled;
        bool grdCategoryWasEnabled;
        bool profilerStateSaved;

        void Awake()
        {
            Application.runInBackground = true;
            evidenceDirectory = Argument("--evidence-dir") ?? Path.Combine(Application.dataPath, "..", "..", ".local", "bridge-worker-validation");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            runtimePath = Argument("--runtime-path") ?? Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
            string holdText = Argument("--hold-seconds");
            if (!string.IsNullOrEmpty(holdText) && (!double.TryParse(holdText, NumberStyles.Float, CultureInfo.InvariantCulture, out holdSeconds) || double.IsNaN(holdSeconds) || double.IsInfinity(holdSeconds) || holdSeconds < 0.0 || holdSeconds > 300.0))
            {
                argumentsValid = false;
                argumentError = "invalid --hold-seconds; expected 0..300 seconds";
            }
            qualityName = (Argument("--quality") ?? "preview").ToLowerInvariant();
            grdName = (Argument("--grd") ?? "off").ToLowerInvariant();
            expectedPipeline = (Argument("--pipeline") ?? "urp").ToLowerInvariant();
            string restartText = Argument("--exercise-restart");
            exerciseRestart = restartText == "1";
            string qualityCycleText = Argument("--quality-cycle");
            qualityCycleRequested = qualityCycleText == "1";
            if (qualityName != "preview" && qualityName != "balanced" && qualityName != "native") { argumentsValid = false; argumentError = "invalid --quality"; }
            if (grdName != "off" && grdName != "on") { argumentsValid = false; argumentError = "invalid --grd"; }
            if (expectedPipeline != "urp" && expectedPipeline != "hdrp") { argumentsValid = false; argumentError = "invalid --pipeline"; }
            if (restartText != null && restartText != "0" && restartText != "1") { argumentsValid = false; argumentError = "invalid --exercise-restart"; }
            if (qualityCycleText != null && qualityCycleText != "0" && qualityCycleText != "1") { argumentsValid = false; argumentError = "invalid --quality-cycle"; }
            evidence = new Evidence { buildGUID = Application.buildGUID, quiltBuildGUID = Application.buildGUID, quality = qualityName, grd = grdName, isEditor = Application.isEditor, development = Debug.isDebugBuild, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion, unityVersion = Application.unityVersion };
            Directory.CreateDirectory(evidenceDirectory);
        }

        void Start()
        {
            StartCoroutine(RunGuarded());
        }

        IEnumerator RunGuarded()
        {
            yield return Drive(RunBody());
            PrepareCleanup();
            yield return CleanupWait();
            evidence.cleanupPending = BridgeWorkerReaperPending();
            evidence.cleanupState = output.State.ToString();
            evidence.sessionState = session.State.ToString();
            evidence.outputState = output.State.ToString();
            if (evidence.cleanupPending != 0 || session.State != ViewWeaveBridgeSession.SessionState.Idle || output.State != BridgeWorkerQuiltOutput.WorkerState.Closed)
            {
                AppendFailure("worker cleanup did not complete");
            }
            if (!evidence.ok) exitCode = 1;
            WriteEvidence();
            Application.Quit(exitCode);
        }

        IEnumerator RunBody()
        {
            if (Application.isEditor || !Debug.isDebugBuild || !SystemInfo.graphicsMultiThreaded || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new InvalidOperationException("Windows Development Player with D3D11 graphicsMultiThreaded is required");
            if (!argumentsValid) throw new ArgumentException(argumentError);
            GraphicsSettings.defaultRenderPipeline = grdName == "on" ? grdOnPipeline : grdOffPipeline;
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
            string activePipelineType = activePipeline == null ? "none" : activePipeline.GetType().FullName;
            string actualPipeline = activePipelineType.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ? "hdrp" : activePipelineType.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ? "urp" : "unknown";
            evidence.pipelineFamily = actualPipeline;
            evidence.pipelineAssetName = activePipeline == null ? "" : activePipeline.name;
            evidence.colorSpace = QualitySettings.activeColorSpace.ToString();
            if (actualPipeline != expectedPipeline) throw new InvalidOperationException("pipeline mismatch expected=" + expectedPipeline + " actual=" + actualPipeline);
            QuiltQualityPreset requestedQuality = qualityName == "native" ? QuiltQualityPreset.Native : qualityName == "balanced" ? QuiltQualityPreset.Balanced : QuiltQualityPreset.Preview;
            session.runtimePath = runtimePath;
            session.startOnEnable = false;
            if (!session.SetQuality(requestedQuality)) throw new InvalidOperationException(FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session quality setup failed"));
            evidence.outputIsActiveAndEnabled = output.isActiveAndEnabled;
            sourceCamera.enabled = false;
            quilt.autoRender = false;
            if (!session.StartSession()) throw new InvalidOperationException(FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session begin failed"));
            double readyStarted = Time.realtimeSinceStartupAsDouble;
            while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && !TimedOut(readyStarted))
            {
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    RecordTextureDiagnostics();
                    throw new InvalidOperationException(FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session stopped before rendering"));
                }
                yield return null;
            }
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering) throw new TimeoutException("Bridge session did not become rendering");
            yield return null;
            yield return null;
            StartGrdRecorders();
            BridgeDisplayProfile displayProfile = session.Profile;
            QuiltQualityProfile profile = ViewWeaveBridgeSession.CreateQualityProfile(displayProfile, requestedQuality);
            evidence.profile = displayProfile.ToString();
            evidence.nativeQuiltWidth = displayProfile.QuiltWidth;
            evidence.nativeQuiltHeight = displayProfile.QuiltHeight;
            evidence.nativeColumns = displayProfile.Columns;
            evidence.nativeRows = displayProfile.Rows;
            evidence.nativeAspect = displayProfile.Aspect;
            evidence.nativeViewcone = displayProfile.Viewcone;
            evidence.actualQuiltWidth = profile.QuiltWidth;
            evidence.actualQuiltHeight = profile.QuiltHeight;
            evidence.actualColumns = profile.Columns;
            evidence.actualRows = profile.Rows;
            evidence.actualViews = profile.Views;
            evidence.actualAspect = profile.Aspect;
            evidence.actualViewcone = profile.ViewCone;
            quilt.autoRender = true;
            double renderStarted = Time.realtimeSinceStartupAsDouble;
            for (int frame = 0; frame < RequiredFrames / 2 && !TimedOut(renderStarted); ++frame)
            {
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    RecordTextureDiagnostics();
                    throw new InvalidOperationException(FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "Bridge worker stopped during render"));
                }
                yield return null;
            }
            FrameDigest before = CaptureDigest(quilt.QuiltTexture, "before.png", quilt.settings.GetLayout());
            evidence.beforeChecksum = before.checksum;
            evidence.beforeNonBlank = before.nonBlank;
            evidence.beforeAllTilesNonBlank = before.allTilesNonBlank;
            evidence.beforeAllTilesColorEvidence = before.allTilesColorEvidence;
            evidence.beforeEdgeDifference = before.edgeDifference;
            evidence.beforeEdgeChangedPixels = before.edgeChangedPixels;
            uint completionBeforeMove = output.Completed;
            dynamicSphere.localPosition += Vector3.right * 1.2f;
            int moveFrames = 0;
            while ((moveFrames < 2 || output.Completed < completionBeforeMove + 2 || output.Completed < RequiredFrames) && !TimedOut(renderStarted))
            {
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    RecordTextureDiagnostics();
                    throw new InvalidOperationException(FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "Bridge worker stopped after movement"));
                }
                moveFrames++;
                yield return null;
            }
            RecordTextureDiagnostics();
            if (output.Completed < RequiredFrames || moveFrames < 2) throw new TimeoutException("Bridge worker completion timeout");
            FrameDigest after = CaptureDigest(quilt.QuiltTexture, "after.png", quilt.settings.GetLayout());
            evidence.afterChecksum = after.checksum;
            evidence.afterNonBlank = after.nonBlank;
            evidence.afterAllTilesNonBlank = after.allTilesNonBlank;
            evidence.afterAllTilesColorEvidence = after.allTilesColorEvidence;
            evidence.afterEdgeDifference = after.edgeDifference;
            evidence.afterEdgeChangedPixels = after.edgeChangedPixels;
            evidence.changedPixelCount = CountChangedPixels(before.pixels, after.pixels);
            evidence.movementDifference = evidence.changedPixelCount > 20;
            evidence.viewDifference = before.edgeDifference && after.edgeDifference;
            evidence.submissions = output.Submissions;
            evidence.completed = output.Completed;
            evidence.dropped = output.Dropped;
            evidence.quiltWidth = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.width;
            evidence.quiltHeight = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.height;
            evidence.grdRenderers = grdRenderers.Valid ? grdRenderers.LastValue : 0;
            evidence.visibleInstances = visibleInstances.Valid ? visibleInstances.LastValue : 0;
            evidence.grdCounterAvailable = !grdName.Equals("on", StringComparison.Ordinal) || (grdRecordersStarted && evidence.grdRenderers > 0 && evidence.visibleInstances > 0);
            evidence.ok = evidence.submissions >= RequiredFrames && evidence.completed >= RequiredFrames && evidence.beforeNonBlank && evidence.afterNonBlank && evidence.beforeAllTilesNonBlank && evidence.afterAllTilesNonBlank && evidence.viewDifference && evidence.movementDifference && evidence.grdCounterAvailable && (!expectedPipeline.Equals("hdrp", StringComparison.Ordinal) || (evidence.beforeAllTilesColorEvidence && evidence.afterAllTilesColorEvidence));
            if (!evidence.ok) evidence.error = "insufficient submissions, completions, or view difference";
            WriteProgress();
            double holdStarted = Time.unscaledTimeAsDouble;
            while (Time.unscaledTimeAsDouble - holdStarted < holdSeconds)
            {
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    RecordTextureDiagnostics();
                    throw new InvalidOperationException(FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "Bridge worker stopped during hold"));
                }
                yield return null;
            }
            evidence.submissions = output.Submissions;
            evidence.completed = output.Completed;
            evidence.dropped = output.Dropped;
            if (qualityCycleRequested)
            {
                yield return Drive(RunQualityCycle());
                if (!evidence.qualityCycleOk) throw new InvalidOperationException(evidence.error ?? "Bridge quality cycle failed");
            }
            if (exerciseRestart)
            {
                yield return Drive(RestartSession(requestedQuality));
                if (!evidence.restartOk || evidence.restartPending != 0)
                {
                    evidence.ok = false;
                    evidence.error = evidence.error ?? "Bridge session restart failed";
                    throw new InvalidOperationException(evidence.error);
                }
            }
        }

        IEnumerator RestartSession(QuiltQualityPreset quality)
        {
            quilt.autoRender = false;
            session.StopSession();
            double stopStarted = Time.realtimeSinceStartupAsDouble;
            while ((session.State != ViewWeaveBridgeSession.SessionState.Idle || BridgeWorkerReaperPending() != 0) && !TimedOut(stopStarted))
            {
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                {
                    evidence.restartOk = false;
                    evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session restart stop fault");
                    yield break;
                }
                yield return null;
            }
            if (session.State != ViewWeaveBridgeSession.SessionState.Idle || BridgeWorkerReaperPending() != 0) { evidence.restartOk = false; evidence.error = "Bridge session restart stop timeout"; yield break; }
            if (!session.SetQuality(quality) || !session.StartSession()) { evidence.restartOk = false; evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session restart start failed"); yield break; }
            double startStarted = Time.realtimeSinceStartupAsDouble;
            while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && !TimedOut(startStarted))
            {
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    evidence.restartOk = false;
                    evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session restart start fault");
                    yield break;
                }
                yield return null;
            }
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering) { evidence.restartOk = false; evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session restart start timeout"); yield break; }
            double renderStarted = Time.realtimeSinceStartupAsDouble;
            while (output.Completed < RequiredFrames && !TimedOut(renderStarted))
            {
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    evidence.restartOk = false;
                    evidence.error = FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "Bridge worker restart render fault");
                    yield break;
                }
                yield return null;
            }
            evidence.restartCompleted = output.Completed;
            evidence.restartPending = BridgeWorkerQuiltOutput.ShutdownPendingCount;
            evidence.restartOk = output.Completed >= RequiredFrames;
        }

        IEnumerator RunQualityCycle()
        {
            QuiltQualityPreset[] stages = { QuiltQualityPreset.Preview, QuiltQualityPreset.Balanced, QuiltQualityPreset.Native, QuiltQualityPreset.Preview };
            evidence.qualityCycle = new QualityCycleStage[stages.Length];
            evidence.qualityCycleOk = true;
            for (int index = 0; index < stages.Length; ++index)
            {
                QuiltQualityPreset quality = stages[index];
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || !session.SetQuality(quality))
                {
                    evidence.qualityCycleOk = false;
                    evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "quality cycle quality change failed");
                    yield break;
                }
                double startStarted = Time.realtimeSinceStartupAsDouble;
                while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && !TimedOut(startStarted))
                {
                    if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                    {
                        evidence.qualityCycleOk = false;
                        evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "quality cycle start fault");
                        yield break;
                    }
                    yield return null;
                }
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering)
                {
                    evidence.qualityCycleOk = false;
                    evidence.error = "quality cycle rendering timeout";
                    yield break;
                }
                quilt.autoRender = true;
                BridgeDisplayProfile queriedProfile = session.Profile;
                QuiltQualityProfile expectedProfile = ViewWeaveBridgeSession.CreateQualityProfile(queriedProfile, quality);
                yield return null;
                if (quilt.settings.quiltWidth != expectedProfile.QuiltWidth || quilt.settings.quiltHeight != expectedProfile.QuiltHeight || quilt.settings.columns != expectedProfile.Columns || quilt.settings.rows != expectedProfile.Rows || quilt.settings.views != expectedProfile.Views)
                {
                    evidence.qualityCycleOk = false;
                    evidence.error = "quality cycle profile mismatch";
                    yield break;
                }
                uint startCompleted = output.Completed;
                double renderStarted = Time.realtimeSinceStartupAsDouble;
                while (output.Completed < startCompleted + RequiredFrames && !TimedOut(renderStarted))
                {
                    if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                    {
                        evidence.qualityCycleOk = false;
                        evidence.error = FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "quality cycle render fault");
                        yield break;
                    }
                    yield return null;
                }
                if (output.Completed < startCompleted + RequiredFrames)
                {
                    evidence.qualityCycleOk = false;
                    evidence.error = "quality cycle completion timeout";
                    yield break;
                }
                string label = quality.ToString().ToLowerInvariant() + "-" + index;
                FrameDigest before = CaptureDigest(quilt.QuiltTexture, "quality-cycle-" + label + "-before.png", quilt.settings.GetLayout());
                Vector3 originalPosition = dynamicSphere.localPosition;
                dynamicSphere.localPosition = originalPosition + Vector3.right * .1f;
                uint moveStart = output.Completed;
                int moveFrames = 0;
                while ((moveFrames < 2 || output.Completed < moveStart + 2) && !TimedOut(renderStarted)) { moveFrames++; yield return null; }
                FrameDigest after = CaptureDigest(quilt.QuiltTexture, "quality-cycle-" + label + "-after.png", quilt.settings.GetLayout());
                dynamicSphere.localPosition = originalPosition;
                int actualWidth = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.width;
                int actualHeight = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.height;
                QualityCycleStage stage = new QualityCycleStage { quality = quality.ToString(), expectedWidth = expectedProfile.QuiltWidth, expectedHeight = expectedProfile.QuiltHeight, actualWidth = actualWidth, actualHeight = actualHeight, actualViews = quilt.settings.views, actualColumns = quilt.settings.columns, actualRows = quilt.settings.rows, completedDelta = output.Completed - startCompleted, beforeAllTilesNonBlank = before.allTilesNonBlank, afterAllTilesNonBlank = after.allTilesNonBlank, movementDifference = CountChangedPixels(before.pixels, after.pixels) > 20, viewDifference = before.edgeDifference && after.edgeDifference };
                evidence.qualityCycle[index] = stage;
                if (stage.actualWidth != stage.expectedWidth || stage.actualHeight != stage.expectedHeight || stage.actualViews != stage.actualColumns * stage.actualRows || stage.completedDelta < RequiredFrames || !stage.beforeAllTilesNonBlank || !stage.afterAllTilesNonBlank || !stage.movementDifference || !stage.viewDifference) evidence.qualityCycleOk = false;
                if (!evidence.qualityCycleOk) { evidence.error = "quality cycle image/profile validation failed"; yield break; }
            }
        }

        IEnumerator Drive(IEnumerator iterator)
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
                        evidence.ok = false;
                        AppendFailure(Sanitize(error.GetType().Name + ": " + error.Message));
                        break;
                    }
                    if (!moved) break;
                    object current;
                    try { current = iterator.Current; }
                    catch (Exception error)
                    {
                        exitCode = 1;
                        evidence.ok = false;
                        AppendFailure(Sanitize(error.GetType().Name + ": " + error.Message));
                        break;
                    }
                    IEnumerator nested = current as IEnumerator;
                    if (nested != null) yield return Drive(nested);
                    else yield return current;
                }
            }
            finally
            {
                IDisposable disposable = iterator as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        void PrepareCleanup()
        {
            try { quilt.autoRender = false; DisposeGrdRecorders(); session.StopSession(); }
            catch (Exception error) { AppendFailure(Sanitize(error.GetType().Name + ": " + error.Message)); }
        }

        IEnumerator CleanupWait()
        {
            double started = Time.realtimeSinceStartupAsDouble;
            while ((BridgeWorkerReaperPending() > 0 || session.State != ViewWeaveBridgeSession.SessionState.Idle) && Time.realtimeSinceStartupAsDouble - started < 10.0) yield return null;
        }

        static int BridgeWorkerReaperPending() { return BridgeWorkerQuiltOutput.ShutdownPendingCount; }

        void AppendFailure(string message)
        {
            exitCode = 1;
            evidence.ok = false;
            if (string.IsNullOrEmpty(evidence.error)) evidence.error = message;
            else if (!evidence.error.Contains(message, StringComparison.Ordinal)) evidence.error += "; " + message;
        }

        static string FirstError(string first, string second, string third, string fallback)
        {
            if (!string.IsNullOrEmpty(first)) return first;
            if (!string.IsNullOrEmpty(second)) return second;
            if (!string.IsNullOrEmpty(third)) return third;
            return fallback;
        }

        void StartGrdRecorders()
        {
            if (!grdName.Equals("on", StringComparison.Ordinal)) return;
            try
            {
                profilerWasEnabled = Profiler.enabled;
                grdCategoryWasEnabled = Profiler.IsCategoryEnabled(GrdCategory);
                profilerStateSaved = true;
                Profiler.enabled = true;
                Profiler.SetCategoryEnabled(GrdCategory, true);
                grdRenderers = ProfilerRecorder.StartNew(GrdCategory, "GRD Renderers", 15);
                visibleInstances = ProfilerRecorder.StartNew(GrdCategory, "Visible Instances", 15);
                grdRecordersStarted = grdRenderers.Valid && visibleInstances.Valid;
            }
            catch (Exception error)
            {
                grdRecordersStarted = false;
                AppendFailure(Sanitize(error.GetType().Name + ": " + error.Message));
            }
        }

        void DisposeGrdRecorders()
        {
            if (grdRenderers.Valid) grdRenderers.Dispose();
            if (visibleInstances.Valid) visibleInstances.Dispose();
            if (profilerStateSaved)
            {
                Profiler.SetCategoryEnabled(GrdCategory, grdCategoryWasEnabled);
                Profiler.enabled = profilerWasEnabled;
                profilerStateSaved = false;
            }
            grdRecordersStarted = false;
        }

        FrameDigest CaptureDigest(RenderTexture source, string fileName, QuiltLayout layout)
        {
            if (source == null || !source.IsCreated()) return default;
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                readback.Apply(false, false);
                Color32[] pixels = readback.GetPixels32();
                bool nonBlank = false;
                for (int i = 0; i < pixels.Length; ++i)
                {
                    if (pixels[i].r > 2 || pixels[i].g > 2 || pixels[i].b > 2) { nonBlank = true; break; }
                }
                bool allTilesNonBlank = true;
                bool allTilesColorEvidence = true;
                for (int view = 0; view < layout.ViewCount; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    bool tileHasPixel = false;
                    int colorPixels = 0;
                    for (int y = tile.y; y < tile.yMax; ++y)
                        for (int x = tile.x; x < tile.xMax; ++x)
                        {
                            Color32 pixel = pixels[y * source.width + x];
                            if (pixel.r > 2 || pixel.g > 2 || pixel.b > 2) tileHasPixel = true;
                            if ((pixel.r > pixel.g + 12 && pixel.r > pixel.b + 12 && pixel.r > 20) || (pixel.g > pixel.r + 12 && pixel.g > pixel.b + 12 && pixel.g > 20)) colorPixels++;
                        }
                    allTilesNonBlank &= tileHasPixel;
                    allTilesColorEvidence &= colorPixels > 20;
                }
                RectInt left = layout.GetTile(0);
                RectInt right = layout.GetTile(layout.ViewCount - 1);
                int edgeChangedPixels = 0;
                for (int y = 0; y < left.height; ++y)
                    for (int x = 0; x < left.width; ++x)
                    {
                        Color32 lp = pixels[(left.y + y) * source.width + left.x + x];
                        Color32 rp = pixels[(right.y + y) * source.width + right.x + x];
                        if (Mathf.Abs(lp.r - rp.r) + Mathf.Abs(lp.g - rp.g) + Mathf.Abs(lp.b - rp.b) > 3) edgeChangedPixels++;
                    }
                bool edgeDifference = edgeChangedPixels > 20;
                using (SHA256 hash = SHA256.Create())
                {
                    byte[] png = readback.EncodeToPNG();
                    File.WriteAllBytes(Path.Combine(evidenceDirectory, fileName), png);
                    byte[] digest = hash.ComputeHash(png);
                    int result = 17;
                    for (int i = 0; i < digest.Length; ++i) result = result * 31 + digest[i];
                    return new FrameDigest { checksum = result, nonBlank = nonBlank, allTilesNonBlank = allTilesNonBlank, allTilesColorEvidence = allTilesColorEvidence, edgeDifference = edgeDifference, edgeChangedPixels = edgeChangedPixels, pixels = pixels };
                }
            }
            finally { RenderTexture.active = previous; Destroy(readback); }
        }

        int CountChangedPixels(Color32[] before, Color32[] after)
        {
            if (before == null || after == null || before.Length != after.Length) return 0;
            int changed = 0;
            for (int i = 0; i < before.Length; ++i)
            {
                if (Mathf.Abs(before[i].r - after[i].r) + Mathf.Abs(before[i].g - after[i].g) + Mathf.Abs(before[i].b - after[i].b) > 3) changed++;
            }
            return changed;
        }

        void RecordTextureDiagnostics()
        {
            RenderTexture source = quilt.QuiltTexture;
            if (source != null)
            {
                evidence.sourceFormat = source.format.ToString();
                evidence.sourceGraphicsFormat = source.graphicsFormat.ToString();
                evidence.sourceSrgb = source.sRGB;
                evidence.sourceAntiAliasing = source.antiAliasing;
                evidence.sourceDimension = source.dimension.ToString();
            }
            evidence.transferFormat = output.TransferFormat;
            evidence.transferGraphicsFormat = output.TransferGraphicsFormat;
            evidence.transferSrgb = output.TransferIsSrgb;
            evidence.transferAntiAliasing = output.TransferAntiAliasing;
            evidence.transferDimension = output.TransferDimension;
        }

        bool TimedOut(double started) { return Time.realtimeSinceStartupAsDouble - started > TimeoutSeconds; }
        void WriteEvidence() { File.WriteAllText(Path.Combine(evidenceDirectory, "summary.json"), JsonUtility.ToJson(evidence, true)); }
        void WriteProgress() { File.WriteAllText(Path.Combine(evidenceDirectory, "progress.json"), JsonUtility.ToJson(evidence, true)); }
        static string Argument(string key) { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1]; return null; }
        static string Sanitize(string value) { if (string.IsNullOrEmpty(value)) return "unknown error"; var chars = new System.Collections.Generic.List<char>(); foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c); return new string(chars.ToArray()); }

        struct FrameDigest { public int checksum; public bool nonBlank; public bool allTilesNonBlank; public bool allTilesColorEvidence; public bool edgeDifference; public int edgeChangedPixels; public Color32[] pixels; }

        [Serializable]
        sealed class Evidence
        {
            public string buildGUID, quiltBuildGUID, quality, grd, unityVersion, graphicsDeviceType, graphicsDeviceVersion, profile, error, cleanupState, sessionState, outputState, pipelineFamily, pipelineAssetName, colorSpace, sourceFormat, sourceGraphicsFormat, sourceDimension, transferFormat, transferGraphicsFormat, transferDimension;
            public bool isEditor, development, graphicsMultiThreaded, outputIsActiveAndEnabled, ok, viewDifference, movementDifference, beforeNonBlank, afterNonBlank, beforeAllTilesNonBlank, afterAllTilesNonBlank, beforeAllTilesColorEvidence, afterAllTilesColorEvidence, beforeEdgeDifference, afterEdgeDifference;
            public bool sourceSrgb, transferSrgb;
            public bool grdCounterAvailable;
            public int beforeChecksum, afterChecksum, quiltWidth, quiltHeight, cleanupPending;
            public int sourceAntiAliasing, transferAntiAliasing;
            public int nativeQuiltWidth, nativeQuiltHeight, nativeColumns, nativeRows, actualQuiltWidth, actualQuiltHeight, actualColumns, actualRows, actualViews, changedPixelCount, beforeEdgeChangedPixels, afterEdgeChangedPixels;
            public float nativeAspect, nativeViewcone, actualAspect, actualViewcone;
            public uint submissions, completed, dropped;
            public long grdRenderers, visibleInstances;
            public uint restartCompleted;
            public int restartPending;
            public bool restartOk, qualityCycleOk;
            public QualityCycleStage[] qualityCycle;
        }

        [Serializable]
        sealed class QualityCycleStage
        {
            public string quality;
            public int expectedWidth, expectedHeight, actualWidth, actualHeight, actualViews, actualColumns, actualRows;
            public uint completedDelta;
            public bool beforeAllTilesNonBlank, afterAllTilesNonBlank, movementDifference, viewDifference;
        }
    }
}
