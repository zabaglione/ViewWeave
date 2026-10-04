using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using ViewWeave;
using UnityEngine;
using Unity.Profiling;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Process = System.Diagnostics.Process;

namespace ViewWeave.BridgeWorkerValidation
{
    public sealed class BridgeWorkerValidationRunner : MonoBehaviour
    {
        const int RequiredFrames = 120;
        const double TimeoutSeconds = 120.0;
        const double HoldCompletionWatchdogSeconds = 10.0;
        const double ProgressWriteIntervalSeconds = 2.0;
        static readonly object warningLock = new object();
        static readonly Queue<WarningOccurrence> pendingWarnings = new Queue<WarningOccurrence>();
        static string currentStage = "before-scene";
        static int warningSequence;
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
        string expectedGraphicsApi = "d3d11";
        GraphicsDeviceType expectedGraphicsDeviceType = GraphicsDeviceType.Direct3D11;
        string buildKind = "development";
        bool expectedDevelopment = true;
        int restartCountRequested;
        bool qualityCycleRequested;
        string faultMode;
        int faultDelayMs = 15000;
        int exitCode;
        Evidence evidence;
        static readonly ProfilerCategory GrdCategory = new ProfilerCategory("GPU Resident Drawer");
        ProfilerRecorder grdRenderers;
        ProfilerRecorder visibleInstances;
        bool grdRecordersStarted;
        bool profilerWasEnabled;
        bool grdCategoryWasEnabled;
        bool profilerStateSaved;
        ProfilerRecorder totalUsedMemoryRecorder;
        ProfilerRecorder totalReservedMemoryRecorder;
        ProfilerRecorder gfxUsedMemoryRecorder;
        ProfilerRecorder gfxReservedMemoryRecorder;
        double lastProgressWriteTime;
        string effectiveGrdMode;
        bool effectiveGrdModeAvailable;
        string effectiveGrdModeError;
        Vector3 originalSpherePosition;
        bool spherePositionSaved;
        bool captureWarnings = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeginWarningCapture()
        {
            Application.logMessageReceivedThreaded -= CaptureThreadedLog;
            Application.logMessageReceivedThreaded += CaptureThreadedLog;
            lock (warningLock)
            {
                pendingWarnings.Clear();
                warningSequence = 0;
            }
            currentStage = "before-scene";
        }

        static void CaptureThreadedLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Warning) return;
            string stageSnapshot = CurrentStage();
            string utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            lock (warningLock)
            {
                pendingWarnings.Enqueue(new WarningOccurrence
                {
                    sequence = ++warningSequence,
                    utc = utc,
                    stage = stageSnapshot,
                    message = condition ?? string.Empty,
                    stackTrace = stackTrace ?? string.Empty
                });
            }
        }

        void Awake()
        {
            SetStage("awake");
            Application.runInBackground = true;
            evidenceDirectory = Argument("--evidence-dir") ?? Path.Combine(Application.dataPath, "..", "..", ".local", "bridge-worker-validation");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            runtimePath = Argument("--runtime-path") ?? Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
            string holdText = Argument("--hold-seconds");
            if (!string.IsNullOrEmpty(holdText) && (!double.TryParse(holdText, NumberStyles.Float, CultureInfo.InvariantCulture, out holdSeconds) || double.IsNaN(holdSeconds) || double.IsInfinity(holdSeconds) || holdSeconds < 0.0 || holdSeconds > 600.0))
            {
                argumentsValid = false;
                argumentError = "invalid --hold-seconds; expected 0..600 seconds";
            }
            qualityName = (Argument("--quality") ?? "preview").ToLowerInvariant();
            grdName = (Argument("--grd") ?? "off").ToLowerInvariant();
            expectedPipeline = (Argument("--pipeline") ?? "urp").ToLowerInvariant();
            expectedGraphicsApi = (Argument("--graphics-api") ?? "d3d11").ToLowerInvariant();
            buildKind = (Argument("--build-kind") ?? "development").ToLowerInvariant();
            expectedDevelopment = buildKind == "development";
            string restartText = Argument("--exercise-restart");
            string restartCountText = Argument("--restart-count");
            int legacyRestartCount = restartText == "1" ? 1 : 0;
            restartCountRequested = legacyRestartCount;
            if (restartCountText != null)
            {
                if (!int.TryParse(restartCountText, NumberStyles.None, CultureInfo.InvariantCulture, out restartCountRequested) || restartCountRequested < 0 || restartCountRequested > 10)
                {
                    argumentsValid = false;
                    argumentError = "invalid --restart-count; expected 0..10";
                }
                else if (restartText != null && restartCountRequested != legacyRestartCount)
                {
                    argumentsValid = false;
                    argumentError = "--restart-count conflicts with --exercise-restart";
                }
            }
            string qualityCycleText = Argument("--quality-cycle");
            qualityCycleRequested = qualityCycleText == "1";
            faultMode = Argument("--fault-mode");
            string faultDelayText = Argument("--fault-delay-ms");
            if (!string.IsNullOrEmpty(faultDelayText) && (!int.TryParse(faultDelayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out faultDelayMs) || faultDelayMs < 10000 || faultDelayMs > 30000)) { argumentsValid = false; argumentError = "invalid --fault-delay-ms; expected 10000..30000"; }
            if (qualityName != "preview" && qualityName != "balanced" && qualityName != "native") { argumentsValid = false; argumentError = "invalid --quality"; }
            if (grdName != "off" && grdName != "on") { argumentsValid = false; argumentError = "invalid --grd"; }
            if (expectedPipeline != "urp" && expectedPipeline != "hdrp") { argumentsValid = false; argumentError = "invalid --pipeline"; }
            if (buildKind != "development" && buildKind != "release") { argumentsValid = false; argumentError = "invalid --build-kind; expected development or release"; }
            if (expectedGraphicsApi == "d3d11") expectedGraphicsDeviceType = GraphicsDeviceType.Direct3D11;
            else if (expectedGraphicsApi == "d3d12") expectedGraphicsDeviceType = GraphicsDeviceType.Direct3D12;
            else { argumentsValid = false; argumentError = "invalid --graphics-api; expected d3d11 or d3d12"; }
            if (restartText != null && restartText != "0" && restartText != "1") { argumentsValid = false; argumentError = "invalid --exercise-restart"; }
            if (qualityCycleText != null && qualityCycleText != "0" && qualityCycleText != "1") { argumentsValid = false; argumentError = "invalid --quality-cycle"; }
            if (faultMode != null && faultMode != "init-delay" && faultMode != "draw-release") { argumentsValid = false; argumentError = "invalid --fault-mode"; }
            evidence = new Evidence { buildGUID = Application.buildGUID, quiltBuildGUID = Application.buildGUID, quality = qualityName, grd = grdName, buildKind = buildKind, expectedDevelopment = expectedDevelopment, restartCountRequested = restartCountRequested, qualityCycleRequested = qualityCycleRequested, expectedGraphicsApi = expectedGraphicsApi, expectedGraphicsDeviceType = expectedGraphicsDeviceType.ToString(), isEditor = Application.isEditor, development = Debug.isDebugBuild, graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded, graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(), graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion, unityVersion = Application.unityVersion, warnings = new List<WarningEvidence>() };
            Directory.CreateDirectory(evidenceDirectory);
            evidence.restartIterations = new List<RestartIteration>();
            evidence.memory = new List<MemorySample>();
            evidence.startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
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
            try { CaptureMemorySample("cleanup-complete"); }
            catch (Exception error) { AppendFailure("memory evidence failed: " + Sanitize(error.GetType().Name + ": " + error.Message)); }
            int shutdownPending;
            uint nativePending;
            string pendingError;
            evidence.cleanupPendingAvailable = BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out shutdownPending, out nativePending, out pendingError);
            evidence.cleanupShutdownPending = evidence.cleanupPendingAvailable ? shutdownPending : -1;
            evidence.cleanupNativePending = evidence.cleanupPendingAvailable ? nativePending : uint.MaxValue;
            evidence.cleanupPending = evidence.cleanupPendingAvailable ? Math.Max(shutdownPending, nativePending > int.MaxValue ? int.MaxValue : (int)nativePending) : -1;
            if (!evidence.cleanupPendingAvailable) AppendFailure("pending count unavailable: " + Sanitize(pendingError));
            evidence.cleanupState = output.State.ToString();
            evidence.sessionState = session.State.ToString();
            evidence.outputState = output.State.ToString();
            evidence.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            try { FlushWarnings(); }
            catch (Exception error) { AppendFailure("warning evidence failed: " + Sanitize(error.GetType().Name + ": " + error.Message)); }
            try { DisposeMemoryRecorders(); }
            catch (Exception error) { AppendFailure("memory recorder cleanup failed: " + Sanitize(error.GetType().Name + ": " + error.Message)); }
            try { DisposeGrdRecorders(); }
            catch (Exception error) { AppendFailure("GRD recorder cleanup failed: " + Sanitize(error.GetType().Name + ": " + error.Message)); }
            if (evidence.cleanupPending != 0 || session.State != ViewWeaveBridgeSession.SessionState.Idle || output.State != BridgeWorkerQuiltOutput.WorkerState.Closed)
            {
                AppendFailure("worker cleanup did not complete");
            }
            if (!evidence.ok) exitCode = 1;
            try { WriteEvidence(); }
            catch (Exception error)
            {
                AppendFailure("summary write failed: " + Sanitize(error.GetType().Name + ": " + error.Message));
                try { File.WriteAllText(Path.Combine(evidenceDirectory, "summary.json"), JsonUtility.ToJson(evidence, true)); }
                catch { }
            }
            if (!evidence.ok) exitCode = 1;
            Application.Quit(exitCode);
        }

        IEnumerator RunBody()
        {
            if (!argumentsValid) throw new ArgumentException(argumentError);
            if (Application.isEditor || Debug.isDebugBuild != expectedDevelopment || !SystemInfo.graphicsMultiThreaded || SystemInfo.graphicsDeviceType != expectedGraphicsDeviceType) throw new InvalidOperationException("Windows Player build-kind or graphics API mismatch");
            evidence.actualDevelopment = Debug.isDebugBuild;
            if (evidence.actualDevelopment != expectedDevelopment) throw new InvalidOperationException("Player build-kind mismatch expected=" + buildKind);
            GraphicsSettings.defaultRenderPipeline = grdName == "on" ? grdOnPipeline : grdOffPipeline;
            QualitySettings.renderPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
            string activePipelineType = activePipeline == null ? "none" : activePipeline.GetType().FullName;
            string actualPipeline = activePipelineType.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ? "hdrp" : activePipelineType.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ? "urp" : "unknown";
            evidence.pipelineFamily = actualPipeline;
            evidence.pipelineAssetName = activePipeline == null ? "" : activePipeline.name;
            evidence.colorSpace = QualitySettings.activeColorSpace.ToString();
            if (actualPipeline != expectedPipeline) throw new InvalidOperationException("pipeline mismatch expected=" + expectedPipeline + " actual=" + actualPipeline);
            CaptureEffectiveGrdMode(activePipeline);
            StartMemoryRecorders();
            QuiltQualityPreset requestedQuality = qualityName == "native" ? QuiltQualityPreset.Native : qualityName == "balanced" ? QuiltQualityPreset.Balanced : QuiltQualityPreset.Preview;
            session.runtimePath = runtimePath;
            session.startOnEnable = false;
            if (!session.SetQuality(requestedQuality)) throw new InvalidOperationException(FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "Bridge session quality setup failed"));
            evidence.outputIsActiveAndEnabled = output.isActiveAndEnabled;
            sourceCamera.enabled = false;
            quilt.autoRender = false;
            originalSpherePosition = dynamicSphere.localPosition;
            spherePositionSaved = true;
            SetStage("initial-render");
            if (!string.IsNullOrEmpty(faultMode))
            {
                yield return Drive(RunFaultScenario(requestedQuality));
                yield break;
            }
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
            output.Poll();
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError) || !ProfileMatches(profile))
                throw new InvalidOperationException(FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "initial profile/readiness validation failed"));
            uint initialCompletedBaseline = output.Completed;
            double renderStarted = Time.realtimeSinceStartupAsDouble;
            while (output.Completed < initialCompletedBaseline + (uint)(RequiredFrames / 2) && !TimedOut(renderStarted))
            {
                output.Poll();
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed || !string.IsNullOrEmpty(output.LastError) || !string.IsNullOrEmpty(session.LastError))
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
            while ((moveFrames < 2 || output.Completed < completionBeforeMove + 2 || output.Completed < initialCompletedBaseline + (uint)RequiredFrames) && !TimedOut(renderStarted))
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
            if (output.Completed < initialCompletedBaseline + (uint)RequiredFrames || moveFrames < 2) throw new TimeoutException("Bridge worker completion timeout");
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
            before.pixels = null;
            after.pixels = null;
            evidence.submissions = output.Submissions;
            evidence.completed = output.Completed;
            evidence.dropped = output.Dropped;
            evidence.quiltWidth = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.width;
            evidence.quiltHeight = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.height;
            evidence.grdRenderers = grdRenderers.Valid ? grdRenderers.LastValue : 0;
            evidence.visibleInstances = visibleInstances.Valid ? visibleInstances.LastValue : 0;
            evidence.grdCounterAvailable = !grdName.Equals("on", StringComparison.Ordinal) || (grdRecordersStarted && evidence.grdRenderers > 0 && evidence.visibleInstances > 0);
            evidence.ok = evidence.submissions >= RequiredFrames && output.Completed - initialCompletedBaseline >= RequiredFrames && evidence.beforeNonBlank && evidence.afterNonBlank && evidence.beforeAllTilesNonBlank && evidence.afterAllTilesNonBlank && evidence.beforeAllTilesColorEvidence && evidence.afterAllTilesColorEvidence && evidence.viewDifference && evidence.movementDifference && evidence.grdCounterAvailable;
            if (!evidence.ok) evidence.error = "insufficient submissions, completions, or view difference";
            if (!evidence.ok) throw new InvalidOperationException(evidence.error);
            CaptureMemorySample("post-render-warmup");
            WriteProgress();
            double holdStarted = Time.unscaledTimeAsDouble;
            double lastCompletionProgress = holdStarted;
            uint holdStartCompleted = output.Completed;
            uint lastCompleted = holdStartCompleted;
            uint lastProgressWrittenCompleted = holdStartCompleted;
            evidence.holdStartedCompleted = holdStartCompleted;
            evidence.holdWatchdogOk = true;
            while (Time.unscaledTimeAsDouble - holdStarted < holdSeconds)
            {
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    RecordTextureDiagnostics();
                    throw new InvalidOperationException(FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "Bridge worker stopped during hold"));
                }
                uint completed = output.Completed;
                double now = Time.unscaledTimeAsDouble;
                if (completed != lastCompleted)
                {
                    lastCompleted = completed;
                    lastCompletionProgress = now;
                    evidence.holdElapsedSeconds = now - holdStarted;
                    evidence.holdCompleted = completed;
                    evidence.holdCompletedDelta = completed - holdStartCompleted;
                    evidence.submissions = output.Submissions;
                    evidence.completed = completed;
                    evidence.dropped = output.Dropped;
                }
                if (completed != lastProgressWrittenCompleted) lastProgressWrittenCompleted = completed;
                WriteProgressSafely();
                if (holdSeconds > HoldCompletionWatchdogSeconds && now - lastCompletionProgress > HoldCompletionWatchdogSeconds)
                {
                    evidence.holdWatchdogOk = false;
                    evidence.holdWatchdogSeconds = now - lastCompletionProgress;
                    throw new TimeoutException("Bridge worker completion stopped during hold");
                }
                yield return null;
            }
            evidence.holdElapsedSeconds = Time.unscaledTimeAsDouble - holdStarted;
            evidence.holdCompleted = output.Completed;
            evidence.holdCompletedDelta = output.Completed - holdStartCompleted;
            evidence.submissions = output.Submissions;
            evidence.completed = output.Completed;
            evidence.dropped = output.Dropped;
            if (qualityCycleRequested)
            {
                yield return Drive(RunQualityCycle());
                if (!evidence.qualityCycleOk) throw new InvalidOperationException(evidence.error ?? "Bridge quality cycle failed");
            }
            if (effectiveGrdModeAvailable && !evidence.grdModeMatchesRequested) throw new InvalidOperationException("GPU Resident Drawer configured mode mismatch expected=" + grdName + " actual=" + effectiveGrdMode);
            if (restartCountRequested > 0)
            {
                yield return Drive(RunRestartCycles(requestedQuality));
                if (!evidence.restartOk)
                {
                    evidence.ok = false;
                    evidence.error = evidence.error ?? "Bridge session restart failed";
                    throw new InvalidOperationException(evidence.error);
                }
            }
        }

        IEnumerator RunRestartCycles(QuiltQualityPreset quality)
        {
            evidence.restartOk = true;
            for (int iteration = 0; iteration < restartCountRequested; ++iteration)
            {
                RestartIteration result = new RestartIteration { index = iteration + 1, startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), result = "running" };
                evidence.restartIterations.Add(result);
                SetStage("restart-" + result.index.ToString(CultureInfo.InvariantCulture));
                CaptureMemorySample("restart-before-stop");
                quilt.autoRender = false;
                session.StopSession();
                double stopStarted = Time.realtimeSinceStartupAsDouble;
                bool pendingAvailable = false;
                int shutdownPending = -1;
                uint nativePending = uint.MaxValue;
                string pendingError = string.Empty;
                while (!TimedOut(stopStarted))
                {
                    pendingAvailable = BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out shutdownPending, out nativePending, out pendingError);
                    if (pendingAvailable && session.State == ViewWeaveBridgeSession.SessionState.Idle && output.State == BridgeWorkerQuiltOutput.WorkerState.Closed && shutdownPending == 0 && nativePending == 0) break;
                    if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                    {
                        result.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "restart stop fault");
                        break;
                    }
                    yield return null;
                }
                result.stopShutdownPending = pendingAvailable ? shutdownPending : -1;
                result.stopNativePending = pendingAvailable ? nativePending : uint.MaxValue;
                result.stopPendingVerified = pendingAvailable && shutdownPending == 0 && nativePending == 0;
                result.stopSessionState = session.State.ToString();
                result.stopOutputState = output.State.ToString();
                if (!pendingAvailable) result.error = "pending count unavailable: " + Sanitize(pendingError);
                if (result.stopPendingVerified) CaptureMemorySample("restart-stopped");
                if (!string.IsNullOrEmpty(result.error) || !result.stopPendingVerified || session.State != ViewWeaveBridgeSession.SessionState.Idle || output.State != BridgeWorkerQuiltOutput.WorkerState.Closed)
                {
                    result.ok = false;
                    if (string.IsNullOrEmpty(result.error)) result.error = "restart stop did not reach Idle/Closed with zero pending counts";
                    evidence.restartOk = false;
                    evidence.error = result.error;
                    result.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    yield break;
                }

                if (!session.SetQuality(quality) || !session.StartSession())
                {
                    result.ok = false;
                    result.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "restart start failed");
                    evidence.restartOk = false;
                    evidence.error = result.error;
                    result.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    yield break;
                }
                quilt.autoRender = true;
                double readyStarted = Time.realtimeSinceStartupAsDouble;
                QuiltQualityProfile expectedProfile = default;
                while (!TimedOut(readyStarted))
                {
                    output.Poll();
                    if (session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.State == BridgeWorkerQuiltOutput.WorkerState.Ready && string.IsNullOrEmpty(session.LastError) && string.IsNullOrEmpty(output.LastError))
                    {
                        BridgeDisplayProfile display = session.Profile;
                        expectedProfile = ViewWeaveBridgeSession.CreateQualityProfile(display, quality);
                        if (ProfileMatches(expectedProfile)) break;
                    }
                    if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                    {
                        result.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "restart render fault");
                        break;
                    }
                    yield return null;
                }
                if (!string.IsNullOrEmpty(result.error) || !ProfileMatches(expectedProfile))
                {
                    result.ok = false;
                    if (string.IsNullOrEmpty(result.error)) result.error = "restart profile did not become ready";
                    evidence.restartOk = false;
                    evidence.error = result.error;
                    result.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    yield break;
                }
                output.Poll();
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                {
                    result.ok = false;
                    result.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "restart readiness changed before baseline");
                    evidence.restartOk = false;
                    evidence.error = result.error;
                    result.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    yield break;
                }
                result.expectedWidth = expectedProfile.QuiltWidth;
                result.expectedHeight = expectedProfile.QuiltHeight;
                result.actualWidth = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.width;
                result.actualHeight = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.height;
                result.views = quilt.settings.views;
                result.completedBaseline = output.Completed;
                CaptureMemorySample("restart-ready");
                double renderStarted = Time.realtimeSinceStartupAsDouble;
                double lastRestartMemory = renderStarted;
                while (output.Completed < result.completedBaseline + (uint)RequiredFrames && !TimedOut(renderStarted))
                {
                    if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || session.State != ViewWeaveBridgeSession.SessionState.Rendering || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                    {
                        result.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "restart completion fault");
                        break;
                    }
                    if (Time.realtimeSinceStartupAsDouble - lastRestartMemory >= ProgressWriteIntervalSeconds)
                    {
                        CaptureMemorySample("restart-render");
                        WriteProgressSafely();
                        lastRestartMemory = Time.realtimeSinceStartupAsDouble;
                    }
                    yield return null;
                }
                result.completed = output.Completed;
                result.completedDelta = result.completed - result.completedBaseline;
                if (string.IsNullOrEmpty(result.error) && result.completedDelta >= RequiredFrames)
                {
                    FrameDigest first = CaptureDigest(quilt.QuiltTexture, "restart-" + result.index.ToString(CultureInfo.InvariantCulture) + "-before.png", quilt.settings.GetLayout());
                    Vector3 originalPosition = dynamicSphere.localPosition;
                    dynamicSphere.localPosition = originalPosition + Vector3.right * .1f;
                    uint movementBaseline = output.Completed;
                    double movementStarted = Time.realtimeSinceStartupAsDouble;
                    while (output.Completed < movementBaseline + 2 && !TimedOut(movementStarted)) yield return null;
                    FrameDigest second = CaptureDigest(quilt.QuiltTexture, "restart-" + result.index.ToString(CultureInfo.InvariantCulture) + "-after.png", quilt.settings.GetLayout());
                    dynamicSphere.localPosition = originalPosition;
                    result.allTilesNonBlank = first.allTilesNonBlank && second.allTilesNonBlank;
                    result.allTilesColorEvidence = first.allTilesColorEvidence && second.allTilesColorEvidence;
                    result.viewDifference = first.edgeDifference && second.edgeDifference;
                    result.movementDifference = CountChangedPixels(first.pixels, second.pixels) > 20;
                    first.pixels = null;
                    second.pixels = null;
                    if (!result.allTilesNonBlank || !result.allTilesColorEvidence || !result.viewDifference || !result.movementDifference || result.actualWidth != result.expectedWidth || result.actualHeight != result.expectedHeight || result.views != 48)
                        result.error = "restart image/profile validation failed";
                }
                else if (string.IsNullOrEmpty(result.error)) result.error = "restart completion timeout";
                result.ok = string.IsNullOrEmpty(result.error);
                result.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                CaptureMemorySample("restart-complete");
                if (!result.ok)
                {
                    evidence.restartOk = false;
                    evidence.error = result.error;
                    yield break;
                }
                WriteProgressSafely();
            }
        }

        bool ProfileMatches(QuiltQualityProfile expected)
        {
            RenderTexture texture = quilt.QuiltTexture;
            return session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.State == BridgeWorkerQuiltOutput.WorkerState.Ready && texture != null && texture.IsCreated() && texture.width == expected.QuiltWidth && texture.height == expected.QuiltHeight && quilt.settings.quiltWidth == expected.QuiltWidth && quilt.settings.quiltHeight == expected.QuiltHeight && quilt.settings.columns == expected.Columns && quilt.settings.rows == expected.Rows && quilt.settings.views == 48 && quilt.settings.views == expected.Views;
        }

        IEnumerator RunFaultScenario(QuiltQualityPreset quality)
        {
            string releaseFile = Path.Combine(evidenceDirectory, "fault-release.marker");
            if (File.Exists(releaseFile)) File.Delete(releaseFile);
            try
            {
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_DELAY_MS", faultDelayMs.ToString(CultureInfo.InvariantCulture));
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_MODE", faultMode);
                if (faultMode == "draw-release") Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_RELEASE_FILE", releaseFile);
            if (faultMode == "init-delay")
            {
                if (!session.StartSession()) throw new InvalidOperationException("fault session begin failed");
                double started = Time.realtimeSinceStartupAsDouble;
                while (session.State != ViewWeaveBridgeSession.SessionState.Fault && Time.realtimeSinceStartupAsDouble - started < 20.0) yield return null;
                if (session.State != ViewWeaveBridgeSession.SessionState.Fault) throw new TimeoutException("fault init delay did not reach Fault");
                if (string.IsNullOrEmpty(session.LastError) || session.LastError.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("fault init delay did not produce timeout error");
                if (rig.AutoRender) throw new InvalidOperationException("fault init delay did not stop auto render");
                evidence.faultPendingBeforeRelease = BridgeWorkerReaperPending();
                if (evidence.faultPendingBeforeRelease == 0) throw new InvalidOperationException("fault init delay did not retain pending cleanup");
            }
            else
            {
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_MODE", "");
                if (!session.StartSession()) throw new InvalidOperationException("fault baseline begin failed");
                double ready = Time.realtimeSinceStartupAsDouble;
                while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && Time.realtimeSinceStartupAsDouble - ready < 20.0) yield return null;
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering) throw new TimeoutException("fault baseline did not render");
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_MODE", "draw-release");
                double faultStarted = Time.realtimeSinceStartupAsDouble;
                while (session.State != ViewWeaveBridgeSession.SessionState.Fault && Time.realtimeSinceStartupAsDouble - faultStarted < 20.0) yield return null;
                if (session.State != ViewWeaveBridgeSession.SessionState.Fault) throw new TimeoutException("draw stall did not reach Fault");
                if (string.IsNullOrEmpty(session.LastError) || session.LastError.IndexOf("completion timed out", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("draw stall did not produce completion timeout error");
                if (rig.AutoRender) throw new InvalidOperationException("draw stall did not stop auto render");
                evidence.faultPendingBeforeRelease = BridgeWorkerReaperPending();
                if (evidence.faultPendingBeforeRelease == 0) throw new InvalidOperationException("draw stall did not retain pending cleanup");
                File.WriteAllText(releaseFile, "release");
            }
            Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_MODE", "");
            Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_RELEASE_FILE", "");
            double drained = Time.realtimeSinceStartupAsDouble;
            while (BridgeWorkerReaperPending() != 0 && Time.realtimeSinceStartupAsDouble - drained < 20.0) yield return null;
            if (BridgeWorkerReaperPending() != 0) throw new TimeoutException("fault cleanup remained pending");
            if (!session.StartSession()) throw new InvalidOperationException("fault reconnect begin failed");
            double reconnect = Time.realtimeSinceStartupAsDouble;
            while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && Time.realtimeSinceStartupAsDouble - reconnect < 20.0) yield return null;
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering) throw new TimeoutException("fault reconnect did not render");
            double completedStarted = Time.realtimeSinceStartupAsDouble;
            while (output.Completed == 0 && Time.realtimeSinceStartupAsDouble - completedStarted < 10.0) yield return null;
            evidence.completed = output.Completed;
            evidence.submissions = output.Submissions;
            evidence.faultMode = faultMode;
            evidence.faultInjectionOk = evidence.completed > 0 && BridgeWorkerReaperPending() == 0;
                evidence.ok = evidence.faultInjectionOk;
                if (!evidence.ok) throw new InvalidOperationException("fault injection reconnect validation failed");
            }
            finally
            {
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_MODE", null);
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_DELAY_MS", null);
                Environment.SetEnvironmentVariable("LKG_FAKE_BRIDGE_RELEASE_FILE", null);
            }
        }

        IEnumerator RunQualityCycle()
        {
            QuiltQualityPreset[] stages = { QuiltQualityPreset.Preview, QuiltQualityPreset.Balanced, QuiltQualityPreset.Native, QuiltQualityPreset.Preview };
            evidence.qualityCycle = new QualityCycleStage[stages.Length];
            evidence.qualityCycleOk = true;
            for (int index = 0; index < stages.Length; ++index)
            {
                SetStage("quality-" + index.ToString(CultureInfo.InvariantCulture));
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
                QuiltQualityProfile expectedProfile = ViewWeaveBridgeSession.CreateQualityProfile(session.Profile, quality);
                bool profileReady = false;
                double profileStarted = Time.realtimeSinceStartupAsDouble;
                while (!TimedOut(profileStarted))
                {
                    output.Poll();
                    if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError) || session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault) break;
                    if (ProfileMatches(expectedProfile)) { profileReady = true; break; }
                    yield return null;
                }
                if (!profileReady || session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                {
                    evidence.qualityCycleOk = false;
                    evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "quality cycle profile/readiness mismatch");
                    yield break;
                }
                output.Poll();
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                {
                    evidence.qualityCycleOk = false;
                    evidence.error = FirstError(session.LastError, output.LastError, quilt.LastAutoRenderError, "quality cycle readiness changed before baseline");
                    yield break;
                }
                uint startCompleted = output.Completed;
                double renderStarted = Time.realtimeSinceStartupAsDouble;
                while (output.Completed < startCompleted + (uint)RequiredFrames && !TimedOut(renderStarted))
                {
                    if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || session.State != ViewWeaveBridgeSession.SessionState.Rendering || !string.IsNullOrEmpty(output.LastError) || !string.IsNullOrEmpty(session.LastError))
                    {
                        evidence.qualityCycleOk = false;
                        evidence.error = FirstError(output.LastError, session.LastError, quilt.LastAutoRenderError, "quality cycle render fault");
                        yield break;
                    }
                    yield return null;
                }
                if (output.Completed < startCompleted + (uint)RequiredFrames)
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
                double moveStarted = Time.realtimeSinceStartupAsDouble;
                while ((moveFrames < 2 || output.Completed < moveStart + 2) && !TimedOut(moveStarted)) { moveFrames++; yield return null; }
                FrameDigest after = CaptureDigest(quilt.QuiltTexture, "quality-cycle-" + label + "-after.png", quilt.settings.GetLayout());
                dynamicSphere.localPosition = originalPosition;
                int actualWidth = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.width;
                int actualHeight = quilt.QuiltTexture == null ? 0 : quilt.QuiltTexture.height;
                QualityCycleStage stage = new QualityCycleStage { quality = quality.ToString(), expectedWidth = expectedProfile.QuiltWidth, expectedHeight = expectedProfile.QuiltHeight, actualWidth = actualWidth, actualHeight = actualHeight, actualViews = quilt.settings.views, actualColumns = quilt.settings.columns, actualRows = quilt.settings.rows, completedBaseline = startCompleted, completedAfter = output.Completed, completedDelta = output.Completed - startCompleted, beforeAllTilesNonBlank = before.allTilesNonBlank, afterAllTilesNonBlank = after.allTilesNonBlank, beforeAllTilesColorEvidence = before.allTilesColorEvidence, afterAllTilesColorEvidence = after.allTilesColorEvidence, movementDifference = CountChangedPixels(before.pixels, after.pixels) > 20, viewDifference = before.edgeDifference && after.edgeDifference };
                before.pixels = null;
                after.pixels = null;
                evidence.qualityCycle[index] = stage;
                if (stage.actualWidth != stage.expectedWidth || stage.actualHeight != stage.expectedHeight || stage.actualViews != 48 || stage.actualViews != stage.actualColumns * stage.actualRows || stage.completedDelta < RequiredFrames || !stage.beforeAllTilesNonBlank || !stage.afterAllTilesNonBlank || !stage.beforeAllTilesColorEvidence || !stage.afterAllTilesColorEvidence || !stage.movementDifference || !stage.viewDifference) evidence.qualityCycleOk = false;
                CaptureMemorySample("quality-stage-complete");
                WriteProgressSafely();
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
            if (spherePositionSaved && dynamicSphere != null) dynamicSphere.localPosition = originalSpherePosition;
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

        void CaptureEffectiveGrdMode(RenderPipelineAsset pipeline)
        {
            effectiveGrdModeAvailable = false;
            effectiveGrdMode = string.Empty;
            effectiveGrdModeError = string.Empty;
            evidence.requestedGrdMode = grdName;
            try
            {
                if (pipeline == null) throw new InvalidOperationException("active render pipeline is null");
                Type type = pipeline.GetType();
                PropertyInfo property = type.GetProperty("gpuResidentDrawerMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object mode = property != null ? property.GetValue(pipeline, null) : null;
                if (mode == null)
                {
                    FieldInfo field = type.GetField("m_GPUResidentDrawerMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null) mode = field.GetValue(pipeline);
                }
                if (mode == null) throw new MissingMemberException(type.FullName, "GPUResidentDrawerMode");
                effectiveGrdMode = mode.ToString();
                effectiveGrdModeAvailable = true;
            }
            catch (Exception error) { effectiveGrdModeError = Sanitize(error.GetType().Name + ": " + error.Message); }
            evidence.configuredGrdMode = effectiveGrdModeAvailable ? effectiveGrdMode : string.Empty;
            evidence.configuredGrdModeAvailable = effectiveGrdModeAvailable;
            evidence.configuredGrdModeError = effectiveGrdModeError;
            evidence.grdModeMatchesRequested = effectiveGrdModeAvailable && (grdName == "on" ? effectiveGrdMode == "InstancedDrawing" : effectiveGrdMode == "Disabled");
            evidence.grdRuntimeEvidence = grdName == "on" ? "GRD profiler counters required" : "configured mode only; runtime disabled counter unavailable";
        }

        void StartMemoryRecorders()
        {
            StartMemoryRecorder(ref totalUsedMemoryRecorder, "Total Used Memory", out evidence.totalUsedMemoryAvailable, out evidence.totalUsedMemoryUnavailableReason);
            StartMemoryRecorder(ref totalReservedMemoryRecorder, "Total Reserved Memory", out evidence.totalReservedMemoryAvailable, out evidence.totalReservedMemoryUnavailableReason);
            StartMemoryRecorder(ref gfxUsedMemoryRecorder, "Gfx Used Memory", out evidence.gfxUsedMemoryAvailable, out evidence.gfxUsedMemoryUnavailableReason);
            StartMemoryRecorder(ref gfxReservedMemoryRecorder, "Gfx Reserved Memory", out evidence.gfxReservedMemoryAvailable, out evidence.gfxReservedMemoryUnavailableReason);
        }

        static void StartMemoryRecorder(ref ProfilerRecorder recorder, string marker, out bool available, out string reason)
        {
            available = false;
            reason = string.Empty;
            try
            {
                recorder = ProfilerRecorder.StartNew(ProfilerCategory.Memory, marker, 1);
                available = recorder.Valid;
                if (!available) reason = "marker unavailable";
            }
            catch (Exception error) { reason = Sanitize(error.GetType().Name + ": " + error.Message); }
        }

        void CaptureMemorySample(string label)
        {
            long totalUsed = totalUsedMemoryRecorder.Valid ? totalUsedMemoryRecorder.LastValue : -1;
            long totalReserved = totalReservedMemoryRecorder.Valid ? totalReservedMemoryRecorder.LastValue : -1;
            long gfxUsed = gfxUsedMemoryRecorder.Valid ? gfxUsedMemoryRecorder.LastValue : -1;
            long gfxReserved = gfxReservedMemoryRecorder.Valid ? gfxReservedMemoryRecorder.LastValue : -1;
            int shutdownPending;
            uint nativePending;
            string pendingError;
            bool pendingAvailable = BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out shutdownPending, out nativePending, out pendingError);
            long privateBytes = -1, workingSetBytes = -1, handles = -1;
            int threadCount = -1;
            bool processAvailable = false;
            string processError = string.Empty;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    process.Refresh();
                    privateBytes = process.PrivateMemorySize64;
                    workingSetBytes = process.WorkingSet64;
                    handles = process.HandleCount;
                    threadCount = process.Threads.Count;
                    processAvailable = true;
                }
            }
            catch (Exception error) { processError = Sanitize(error.GetType().Name + ": " + error.Message); }
            evidence.memory.Add(new MemorySample { utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), stage = CurrentStage(), label = label, totalUsedBytes = totalUsed, totalReservedBytes = totalReserved, gfxUsedBytes = gfxUsed, gfxReservedBytes = gfxReserved, playerPrivateBytes = privateBytes, playerWorkingSetBytes = workingSetBytes, playerHandleCount = handles, playerThreadCount = threadCount, playerProcessStatsAvailable = processAvailable, playerProcessStatsError = processError, shutdownPending = pendingAvailable ? shutdownPending : -1, nativePending = pendingAvailable ? nativePending : uint.MaxValue, pendingAvailable = pendingAvailable, pendingError = pendingAvailable ? string.Empty : Sanitize(pendingError) });
        }

        void DisposeMemoryRecorders()
        {
            if (totalUsedMemoryRecorder.Valid) totalUsedMemoryRecorder.Dispose();
            if (totalReservedMemoryRecorder.Valid) totalReservedMemoryRecorder.Dispose();
            if (gfxUsedMemoryRecorder.Valid) gfxUsedMemoryRecorder.Dispose();
            if (gfxReservedMemoryRecorder.Valid) gfxReservedMemoryRecorder.Dispose();
        }

        void SetStage(string stage)
        {
            lock (warningLock) currentStage = stage ?? string.Empty;
            if (evidence != null) evidence.stage = stage ?? string.Empty;
        }

        static string CurrentStage()
        {
            lock (warningLock) return currentStage;
        }

        void FlushWarnings()
        {
            if (evidence == null || evidence.warnings == null) return;
            List<WarningOccurrence> copy = new List<WarningOccurrence>();
            lock (warningLock) while (pendingWarnings.Count > 0) copy.Add(pendingWarnings.Dequeue());
            foreach (WarningOccurrence warning in copy)
            {
                evidence.warnings.Add(new WarningEvidence { sequence = warning.sequence, utc = warning.utc, stage = warning.stage, message = warning.message, stackTrace = warning.stackTrace });
            }
            evidence.warningCount = evidence.warnings.Count;
        }

        void WriteProgressSafely()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - lastProgressWriteTime < ProgressWriteIntervalSeconds) return;
            lastProgressWriteTime = now;
            FlushWarnings();
            CaptureMemorySample("progress");
            WriteProgress();
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
        void WriteEvidence() { FlushWarnings(); File.WriteAllText(Path.Combine(evidenceDirectory, "summary.json"), JsonUtility.ToJson(evidence, true)); }
        void WriteProgress() { FlushWarnings(); File.WriteAllText(Path.Combine(evidenceDirectory, "progress.json"), JsonUtility.ToJson(evidence, true)); }
        static string Argument(string key) { string[] args = Environment.GetCommandLineArgs(); for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1]; return null; }
        static string Sanitize(string value) { if (string.IsNullOrEmpty(value)) return "unknown error"; var chars = new System.Collections.Generic.List<char>(); foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c); return new string(chars.ToArray()); }

        struct FrameDigest { public int checksum; public bool nonBlank; public bool allTilesNonBlank; public bool allTilesColorEvidence; public bool edgeDifference; public int edgeChangedPixels; public Color32[] pixels; }

        sealed class WarningOccurrence
        {
            public int sequence;
            public string utc, stage, message, stackTrace;
        }

        [Serializable]
        sealed class Evidence
        {
            public string buildGUID, quiltBuildGUID, quality, grd, buildKind, expectedGraphicsApi, expectedGraphicsDeviceType, unityVersion, graphicsDeviceType, graphicsDeviceVersion, profile, error, cleanupState, sessionState, outputState, pipelineFamily, pipelineAssetName, colorSpace, sourceFormat, sourceGraphicsFormat, sourceDimension, transferFormat, transferGraphicsFormat, transferDimension, faultMode, requestedGrdMode, configuredGrdMode, configuredGrdModeError, grdRuntimeEvidence, stage, startedUtc, finishedUtc;
            public bool isEditor, development, expectedDevelopment, actualDevelopment, graphicsMultiThreaded, outputIsActiveAndEnabled, ok, viewDifference, movementDifference, beforeNonBlank, afterNonBlank, beforeAllTilesNonBlank, afterAllTilesNonBlank, beforeAllTilesColorEvidence, afterAllTilesColorEvidence, beforeEdgeDifference, afterEdgeDifference;
            public bool sourceSrgb, transferSrgb;
            public bool grdCounterAvailable, grdModeMatchesRequested, configuredGrdModeAvailable, cleanupPendingAvailable;
            public bool totalUsedMemoryAvailable, totalReservedMemoryAvailable, gfxUsedMemoryAvailable, gfxReservedMemoryAvailable;
            public string totalUsedMemoryUnavailableReason, totalReservedMemoryUnavailableReason, gfxUsedMemoryUnavailableReason, gfxReservedMemoryUnavailableReason;
            public int beforeChecksum, afterChecksum, quiltWidth, quiltHeight, cleanupPending, cleanupShutdownPending, faultPendingBeforeRelease, restartCountRequested, warningCount;
            public uint cleanupNativePending;
            public int sourceAntiAliasing, transferAntiAliasing;
            public int nativeQuiltWidth, nativeQuiltHeight, nativeColumns, nativeRows, actualQuiltWidth, actualQuiltHeight, actualColumns, actualRows, actualViews, changedPixelCount, beforeEdgeChangedPixels, afterEdgeChangedPixels;
            public float nativeAspect, nativeViewcone, actualAspect, actualViewcone;
            public uint submissions, completed, dropped;
            public uint holdStartedCompleted, holdCompleted, holdCompletedDelta;
            public double holdElapsedSeconds, holdWatchdogSeconds;
            public bool holdWatchdogOk;
            public long grdRenderers, visibleInstances;
            public uint restartCompleted;
            public int restartPending;
            public bool restartOk, qualityCycleOk, faultInjectionOk, qualityCycleRequested;
            public QualityCycleStage[] qualityCycle;
            public List<RestartIteration> restartIterations;
            public List<WarningEvidence> warnings;
            public List<MemorySample> memory;
        }

        [Serializable]
        sealed class QualityCycleStage
        {
            public string quality;
            public int expectedWidth, expectedHeight, actualWidth, actualHeight, actualViews, actualColumns, actualRows;
            public uint completedBaseline, completedAfter, completedDelta;
            public bool beforeAllTilesNonBlank, afterAllTilesNonBlank, beforeAllTilesColorEvidence, afterAllTilesColorEvidence, movementDifference, viewDifference;
        }

        [Serializable]
        sealed class RestartIteration
        {
            public int index, stopShutdownPending, expectedWidth, expectedHeight, actualWidth, actualHeight, views;
            public uint stopNativePending, completedBaseline, completed, completedDelta;
            public bool stopPendingVerified, allTilesNonBlank, allTilesColorEvidence, movementDifference, viewDifference, ok;
            public string startedUtc, finishedUtc, result, stopSessionState, stopOutputState, error;
        }

        [Serializable]
        sealed class WarningEvidence
        {
            public int sequence;
            public string utc, stage, message, stackTrace;
        }

        [Serializable]
        sealed class MemorySample
        {
            public string utc, stage, label, pendingError;
            public long totalUsedBytes, totalReservedBytes, gfxUsedBytes, gfxReservedBytes;
            public long playerPrivateBytes, playerWorkingSetBytes, playerHandleCount;
            public int playerThreadCount;
            public bool playerProcessStatsAvailable;
            public string playerProcessStatsError;
            public int shutdownPending;
            public uint nativePending;
            public bool pendingAvailable;
        }
    }
}
