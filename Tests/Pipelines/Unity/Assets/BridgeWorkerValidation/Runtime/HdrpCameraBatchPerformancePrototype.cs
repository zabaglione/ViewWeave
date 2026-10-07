using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;
using ViewWeave.HDRP;

namespace ViewWeave.BridgeWorkerValidation
{
    [DefaultExecutionOrder(100)]
    public sealed class HdrpCameraBatchPerformancePrototype : MonoBehaviour
    {
        const int WarmupFrames = 60;
        const int MeasurementFrames = 120;
        const double LifecycleTimeoutSeconds = 60.0;
        const double NativeCompletionTimeoutSeconds = 10.0;
        const double NaturalFirstReadyTimeoutSeconds = 10.0;
        const int NaturalTraceCapacity = 512;
        const int NaturalTracePrefixCapacity = 320;
        const string EvidenceArgument = "--prototype-evidence-dir";
        const string RuntimeArgument = "--runtime-path";
        const string PlanArgument = "--prototype-plan";
        const string ActiveCameraNamePrefix = "ViewWeave.HDRPBatch.View.";

        static HdrpCameraBatchPerformancePrototype active;

        [SerializeField] Camera sourceCamera;
        [SerializeField] Camera mainCamera;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] HdrpQuiltCamera sourceGuard;
        [SerializeField] ViewWeaveRig rig;
        [SerializeField] ViewWeaveBridgeSession session;
        [SerializeField] BridgeWorkerQuiltOutput output;
        [SerializeField] BridgeWorkerValidationRunner originalRunner;

        readonly List<PhaseEvidence> phases = new List<PhaseEvidence>(3);
        readonly Dictionary<Camera, int> viewIndices = new Dictionary<Camera, int>();
        readonly HashSet<Camera> copiedCameras = new HashSet<Camera>();
        readonly HashSet<int> copiedViewIndices = new HashSet<int>();
        readonly HashSet<int> currentContextExpectedViews = new HashSet<int>();
        readonly HashSet<int> currentContextCopiedViews = new HashSet<int>();
        readonly HashSet<int> endedContextOwnedViews = new HashSet<int>();
        readonly List<Camera> viewCameras = new List<Camera>(48);
        readonly List<GameObject> viewCameraObjects = new List<GameObject>(48);
        readonly List<RenderTexture> viewTargets = new List<RenderTexture>(48);
        readonly List<int> naturalMainCameraCallbacksPerEpoch = new List<int>(WarmupFrames + MeasurementFrames);
        readonly FrameTiming[] latestTiming = new FrameTiming[1];
        readonly NaturalTraceEntry[] naturalTraceEntries = new NaturalTraceEntry[NaturalTraceCapacity];
        byte[] firstFullReferencePng;

        string qualityName = "balanced";
        string evidenceDirectory;
        string failure;
        string stage = "initializing";
        IDisposable sourcePreparation;
        CommandBuffer copyCommand;
        ProfilerRecorder drawCallsRecorder;
        ProfilerRecorder initializeBufferRecorder;
        ProfilerRecorder createBufferRecorder;
        ProfilerRecorder totalMemoryRecorder;
        ProfilerRecorder gfxMemoryRecorder;
        bool recorderSetStarted;
        bool running;
        bool phaseActive;
        bool phaseWindowComplete;
        bool playerLoopInstalled;
        bool callbacksSubscribed;
        int naturalCopyCount;
        int naturalEpochId;
        int naturalRenderFrame = -1;
        int naturalContextId;
        int naturalContextFrame = -1;
        int naturalContextExpectedCount;
        int naturalContextCopyCount;
        int naturalContextSubmitCount;
        int naturalOwnedContextCount;
        int naturalOwnedCameraCallbackCount;
        int naturalEpochCount;
        int naturalMainCameraFrame = -1;
        int naturalMainCameraCallbacksForFrame;
        int naturalRenderToTransferFrameOffsetMin;
        int naturalRenderToTransferFrameOffsetMax;
        bool naturalRenderToTransferOffsetSet;
        bool naturalEpochArmed;
        bool naturalEpochReady;
        bool naturalContextOpen;
        bool naturalMeasurementStarted;
        bool naturalMainCameraCallbacksSnapshotCaptured;
        double naturalFirstReadyDeadline;
        double naturalPhaseArmedAt;
        int naturalTracePrefixCount;
        int naturalTraceTailCount;
        int naturalTraceTailNext;
        int naturalTraceSequence;
        int phaseFrameCount;
        int measuredFrameCount;
        int lastBoundaryFrame = -1;
        int mainCameraCallbacks;
        int mainCameraCallbacksAtPhaseStart;
        uint lastObservedSubmissions;
        uint lastObservedSubmissionAttempts;
        PhaseMode phaseMode;
        PhaseEvidence currentPhase;
        FrameSample[] samples;
        CameraSnapshot mainCameraBefore;
        string outputDirectoryLabel;
        bool sessionLifecycleSnapshotCaptured;
        bool sessionEnabledBeforeRun;
        bool sessionStartOnEnableBeforeRun;
        bool sourceGuardAdaptSnapshotCaptured;
        bool sourceGuardAdaptBeforeRun;
        bool densityPlanSelected;
        bool viewRenderModeSnapshotCaptured;
        QuiltViewRenderMode viewRenderModeBeforeRun;

        enum PhaseMode
        {
            LegacyRequests,
            NaturalCameraBatch
        }

        [Serializable]
        sealed class RunEvidence
        {
            public int schemaVersion = 1;
            public string result = "running";
            public string startedUtc;
            public string finishedUtc;
            public string unityVersion;
            public string graphicsApi;
            public string graphicsDeviceVersion;
            public string prototypePlan;
            public int vSyncCount;
            public int targetFrameRate;
            public string projectScenePath;
            public string projectQuality;
            public string qualityPreset;
            public int quiltWidth;
            public int quiltHeight;
            public int rows;
            public int columns;
            public int views;
            public string quiltGraphicsFormat;
            public string deviceProfile;
            public string deviceProfileError;
            public string initialHdrpPipelineType;
            public string initialHdrpPipelineManagedIdentity;
            public bool initialTex2DBlitMaterialAvailable;
            public bool hdrpPreflightObserved;
            public bool sourceGuardAdaptSnapshotCaptured;
            public bool sourceGuardAdaptBeforeRun;
            public bool sourceGuardAdaptDuringCameraBatch;
            public bool sourceGuardAdaptRestored;
            public string mainCameraConfigurationBefore;
            public string mainCameraConfigurationAfter;
            public string sessionStateAfter;
            public string outputStateAfter;
            public bool cleanupPendingCountsAvailable;
            public int cleanupShutdownPending = -1;
            public uint cleanupNativePending = uint.MaxValue;
            public string[] scopeNotes;
            public string error;
            public PhaseEvidence[] phases;
        }

        [Serializable]
        sealed class PhaseEvidence
        {
            public string mode;
            public string startedUtc;
            public string finishedUtc;
            public int warmupFramesExpected = WarmupFrames;
            public int measuredFramesExpected = MeasurementFrames;
            public int measuredFrames;
            public int phaseStartFrame;
            public int phaseFrameCount;
            public int mainCameraCallbacks;
            public int viewCameraCount;
            public string renderMode;
            public int expectedRenderedViewCount;
            public int actualRenderedViewCount;
            public bool renderedEndpointsRetained;
            public bool nearestReuseValid;
            public long nearestReuseComparedPixels;
            public long nearestReuseMismatchPixels;
            public int uniqueViewCameraCallbacks;
            public int naturalEpochsCompleted;
            public int naturalOwnedCameraCallbacks;
            public int naturalOwnedContexts;
            public int naturalContextSubmits;
            public int naturalEpochsWithOneMainCameraCallback;
            public int naturalMainCameraCallbacksTotal;
            public int naturalMainCameraCallbacksMin;
            public int naturalMainCameraCallbacksMax;
            public int[] naturalMainCameraCallbacksPerEpoch;
            public int renderToTransferFrameOffsetMin;
            public int renderToTransferFrameOffsetMax;
            public double firstReadyWaitMs;
            public NaturalTraceEntry[] naturalTrace;
            public int fullSourceComparedPixels;
            public int fullSourceMismatchPixels;
            public int fullSourceMaxChannelDifference;
            public bool fullSourceComparisonValid;
            public long outputSubmissions;
            public bool outputSubmissionCountValid;
            public uint submissionAttemptsBaseline;
            public uint submissionAttemptsAfter;
            public uint submissionAttemptsDelta;
            public bool submissionAttemptCountValid;
            public uint queuedSubmissionsBaseline;
            public uint queuedSubmissionsAfter;
            public uint queuedSubmissionsDelta;
            public bool queuedSubmissionCountValid;
            public bool nativeDispositionSettled;
            public uint completedBaseline;
            public uint completedAfter;
            public uint completedDelta;
            public uint droppedBaseline;
            public uint droppedAfter;
            public uint droppedDelta;
            public bool nativeCompletionObserved;
            public FrameStats frameDeltaMs;
            public FrameStats cpuFrameMs;
            public FrameStats cpuMainThreadMs;
            public FrameStats cpuRenderThreadMs;
            public FrameStats gpuFrameMs;
            public RawStats drawCalls;
            public RawStats initializeBufferMarkerValueNs;
            public RawStats createBufferMarkerValueNs;
            public string bufferMarkerValueUnit = "ns; count unavailable";
            public RawStats totalMemoryBytes;
            public RawStats gfxMemoryBytes;
            public string graphicsFormat;
            public long estimatedViewColorBytes;
            public int tileCount;
            public int nonBlankTiles;
            public long redDominantPixels;
            public long greenDominantPixels;
            public long magentaPixels;
            public long leftRightChangedPixels;
            public bool allTilesNonBlank;
            public bool hasKnownRedAndGreenEvidence;
            public bool hasNoMagentaEvidence;
            public bool leftRightParallaxObserved;
            public int outputTileWidth;
            public int outputTileHeight;
            public bool imageCaptured;
            public string imagePath;
            public double maxTileMeanChannelDeltaFromFirstLegacy;
            public double[] tileMeanRgb;
            public string mainCameraConfigurationAfter;
            public string error;
        }

        [Serializable]
        struct NaturalTraceEntry
        {
            public int sequence;
            public string eventName;
            public int frame;
            public int epoch;
            public int context;
            public int cameraIndex;
            public int contextOwnedCount;
            public int uniqueViewCount;
            public int copyCount;
            public int contextSubmitCount;
            public bool ready;
            public uint attemptDelta;
        }

        [Serializable]
        struct FrameStats
        {
            public bool available;
            public int samples;
            public double p50;
            public double p95;
            public double worst;
        }

        [Serializable]
        struct RawStats
        {
            public bool available;
            public int samples;
            public long p50;
            public long p95;
            public long worst;
        }

        struct FrameSample
        {
            public double deltaMs;
            public double cpuFrameMs;
            public double cpuMainThreadMs;
            public double cpuRenderThreadMs;
            public double gpuFrameMs;
            public long drawCalls;
            public long initializeBuffer;
            public long createBuffer;
            public long totalMemory;
            public long gfxMemory;
        }

        struct CameraSnapshot
        {
            public bool enabled;
            public bool hasTargetTexture;
            public string targetDisplay;
            public int cullingMask;
            public CameraClearFlags clearFlags;
            public float fieldOfView;
            public int volumeLayerMask;

            public static CameraSnapshot Capture(Camera camera)
            {
                HDAdditionalCameraData data = camera == null ? null : camera.GetComponent<HDAdditionalCameraData>();
                return new CameraSnapshot
                {
                    enabled = camera != null && camera.enabled,
                    hasTargetTexture = camera != null && camera.targetTexture != null,
                    targetDisplay = camera == null ? string.Empty : camera.targetDisplay.ToString(CultureInfo.InvariantCulture),
                    cullingMask = camera == null ? 0 : camera.cullingMask,
                    clearFlags = camera == null ? CameraClearFlags.Nothing : camera.clearFlags,
                    fieldOfView = camera == null ? 0f : camera.fieldOfView,
                    volumeLayerMask = data == null ? 0 : data.volumeLayerMask
                };
            }

            public override string ToString()
            {
                return "enabled=" + enabled + ";hasTargetTexture=" + hasTargetTexture + ";targetDisplay=" + targetDisplay + ";cullingMask=" + cullingMask + ";clearFlags=" + clearFlags + ";fieldOfView=" + fieldOfView.ToString("R", CultureInfo.InvariantCulture) + ";volumeLayerMask=" + volumeLayerMask;
            }
        }

        void Start()
        {
            StartCoroutine(Run());
        }

        IEnumerator Run()
        {
            active = this;
            RunEvidence evidence = new RunEvidence
            {
                startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                vSyncCount = QualitySettings.vSyncCount,
                targetFrameRate = Application.targetFrameRate,
                projectScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path,
                projectQuality = QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "unknown",
                qualityPreset = qualityName,
                scopeNotes = new[]
                {
                    "SDK test-only prototype; no package, distribution, native, or user-project changes.",
                    "Generated fixture uses a global HDRP Volume and static red/green geometry only; no Local Volume and no VFX Graph.",
                    "No numeric FPS improvement threshold is imposed; report measurements, spread, image differences, and available counters.",
                    "Dedicated camera history and render-target memory may differ from the single-camera Render Request path."
                }
            };
            try
            {
                evidenceDirectory = ResolveEvidenceDirectory();
                Directory.CreateDirectory(evidenceDirectory);
                string prototypePlan = ParsePlan();
                densityPlanSelected = prototypePlan == "density";
                evidence.prototypePlan = prototypePlan;
                evidence.scopeNotes = densityPlanSelected
                    ? new[]
                    {
                        "SDK test-only density plan; no package, distribution, native, or user-project changes.",
                        "Generated fixture uses a static HDRP scene and existing Legacy Render Requests; it does not compare the natural-camera batch path.",
                        "Full/Half/Quarter retain the same output layout and copy skipped tiles from their nearest rendered source view.",
                        "No numeric FPS improvement threshold is imposed; report measured timing, image differences, and queue outcomes."
                    }
                    : new[]
                    {
                        "SDK test-only A/B/A plan; no package, distribution, native, or user-project changes.",
                        "Generated fixture uses one global fixed-exposure HDRP Volume and static red/green geometry; Local Volumes and VFX components are rejected by preflight.",
                        "Legacy and natural-camera phases keep adaptUnsupportedEffects disabled; the original guard value is restored after the run.",
                        "Image equivalence is limited to this static fixture and does not establish Local Volume, VFX, temporal-history, or general HDRP equivalence.",
                        "Dedicated camera history and render-target memory may differ from the single-camera Render Request path."
                    };
                if (!ValidateFixture(!densityPlanSelected, out string validationError)) throw new InvalidOperationException(validationError);
                viewRenderModeBeforeRun = quilt.viewRenderMode;
                viewRenderModeSnapshotCaptured = true;
                if (!TryReadPending(out int shutdownPending, out uint nativePending, out string pendingError)) throw new InvalidOperationException("pending-count diagnostics unavailable: " + pendingError);
                if (shutdownPending != 0 || nativePending != 0) throw new InvalidOperationException("worker shutdown is pending before prototype start");
                if (session.State != ViewWeaveBridgeSession.SessionState.Idle || output.State != BridgeWorkerQuiltOutput.WorkerState.Closed)
                    throw new InvalidOperationException("worker session must be idle and closed before prototype start");

                if (!densityPlanSelected)
                {
                    sourceGuardAdaptBeforeRun = sourceGuard.adaptUnsupportedEffects;
                    sourceGuardAdaptSnapshotCaptured = true;
                    sourceGuard.adaptUnsupportedEffects = false;
                    evidence.sourceGuardAdaptSnapshotCaptured = true;
                    evidence.sourceGuardAdaptBeforeRun = sourceGuardAdaptBeforeRun;
                    evidence.sourceGuardAdaptDuringCameraBatch = sourceGuard.adaptUnsupportedEffects;
                }
                qualityName = ParseQuality();
                evidence.qualityPreset = qualityName;
                session.runtimePath = Argument(RuntimeArgument) ?? session.runtimePath;
                QuiltQualityPreset selectedQuality = qualityName == "native" ? QuiltQualityPreset.Native : QuiltQualityPreset.Balanced;
                if (!session.startOnEnable || !session.SetQuality(selectedQuality))
                    throw new InvalidOperationException("initial session must use startOnEnable and accept the selected quality");
                sessionEnabledBeforeRun = session.enabled;
                sessionStartOnEnableBeforeRun = session.startOnEnable;
                sessionLifecycleSnapshotCaptured = true;
                session.enabled = false;
                yield return WaitForHdrpBlitterReady(evidence);
                if (!string.IsNullOrEmpty(failure)) yield break;
                session.enabled = true;
                mainCameraBefore = CameraSnapshot.Capture(mainCamera);
                evidence.mainCameraConfigurationBefore = mainCameraBefore.ToString();
                evidence.projectQuality = QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : "unknown";
                evidence.deviceProfile = string.Empty;
                evidence.deviceProfileError = string.Empty;

                PhaseMode[] plan = densityPlanSelected
                    ? new[] { PhaseMode.LegacyRequests, PhaseMode.LegacyRequests, PhaseMode.LegacyRequests, PhaseMode.LegacyRequests }
                    : new[] { PhaseMode.LegacyRequests, PhaseMode.NaturalCameraBatch, PhaseMode.LegacyRequests };
                QuiltViewRenderMode[] densityModes =
                {
                    QuiltViewRenderMode.Full,
                    QuiltViewRenderMode.Half,
                    QuiltViewRenderMode.Quarter,
                    QuiltViewRenderMode.Full
                };
                running = true;
                for (int i = 0; i < plan.Length; ++i)
                {
                    if (i > 0 && !StartSession(qualityName)) throw new InvalidOperationException(failure ?? "could not start the bridge worker session");
                    uint primingSubmissionBaseline = output.Submissions;
                    yield return WaitUntilReady();
                    if (!string.IsNullOrEmpty(failure)) break;

                    yield return WaitForAutoRenderPriming(primingSubmissionBaseline);
                    if (!string.IsNullOrEmpty(failure)) break;
                    rig.AutoRender = false;
                    if (!ValidateActiveProfile(out string profileError))
                    {
                        SetFailure(profileError);
                        break;
                    }
                    if (i == 0)
                    {
                        PopulateRunProfile(evidence, out string profileErrorAtStart);
                        if (!string.IsNullOrEmpty(profileErrorAtStart))
                        {
                            SetFailure(profileErrorAtStart);
                            break;
                        }
                    }

                    if (plan[i] == PhaseMode.NaturalCameraBatch && !PrepareNaturalCameras(out string setupError))
                    {
                        SetFailure(setupError);
                        break;
                    }

                    if (densityPlanSelected) quilt.viewRenderMode = densityModes[i];
                    StartPhase(plan[i]);
                    while (phaseActive && !phaseWindowComplete && string.IsNullOrEmpty(failure)) yield return null;
                    phaseActive = false;
                    if (!string.IsNullOrEmpty(failure)) break;
                    yield return WaitForPhaseCompletion(currentPhase);
                    StopRecorders();
                    if (!string.IsNullOrEmpty(failure)) break;

                    CapturePhaseImage(currentPhase);
                    currentPhase.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    currentPhase.mainCameraConfigurationAfter = CameraSnapshot.Capture(mainCamera).ToString();
                    CapturePhaseSubmissionCounts(currentPhase);
                    currentPhase.submissionAttemptCountValid = currentPhase.submissionAttemptsDelta == WarmupFrames + MeasurementFrames;
                    currentPhase.queuedSubmissionCountValid = currentPhase.queuedSubmissionsDelta <= WarmupFrames + MeasurementFrames;
                    currentPhase.outputSubmissionCountValid = currentPhase.queuedSubmissionCountValid;
                    if (!currentPhase.submissionAttemptCountValid)
                        SetFailure("phase submission-attempt count did not equal 180: " + PhaseSubmissionDiagnostic(currentPhase));
                    if (!currentPhase.queuedSubmissionCountValid)
                        SetFailure("phase queued more than 180 submissions: " + PhaseSubmissionDiagnostic(currentPhase));
                    currentPhase.mainCameraCallbacks = mainCameraCallbacks - mainCameraCallbacksAtPhaseStart;
                    if (!MainCameraUnchanged()) SetFailure("main camera output settings changed during a phase");
                    if (!currentPhase.nativeCompletionObserved)
                        SetFailure("phase produced no fresh native worker completion");
                    if (plan[i] == PhaseMode.NaturalCameraBatch && currentPhase.uniqueViewCameraCallbacks != 48)
                        SetFailure("natural camera phase did not observe all 48 unique view cameras");
                    if (plan[i] == PhaseMode.NaturalCameraBatch)
                    {
                        currentPhase.naturalEpochsCompleted = naturalEpochCount;
                        currentPhase.naturalOwnedCameraCallbacks = naturalOwnedCameraCallbackCount;
                        currentPhase.naturalOwnedContexts = naturalOwnedContextCount;
                        currentPhase.renderToTransferFrameOffsetMin = naturalRenderToTransferFrameOffsetMin;
                        currentPhase.renderToTransferFrameOffsetMax = naturalRenderToTransferFrameOffsetMax;
                        SnapshotNaturalMainCameraCallbacks();
                        currentPhase.naturalTrace = SnapshotNaturalTrace();
                    }

                    phases.Add(currentPhase);
                    StopSessionForDrain();
                    yield return StopAndDrainCoroutine();
                    CleanupNaturalCamerasAfterDrain();
                    if (!string.IsNullOrEmpty(failure)) break;
                }

                if (session != null && (session.State != ViewWeaveBridgeSession.SessionState.Idle || output.State != BridgeWorkerQuiltOutput.WorkerState.Closed))
                {
                    StopSessionForDrain();
                    yield return StopAndDrainCoroutine();
                    CleanupNaturalCamerasAfterDrain();
                }

                if (string.IsNullOrEmpty(failure))
                    ValidatePhaseImages();
                evidence.result = string.IsNullOrEmpty(failure) ? "completed" : "failed";
                evidence.error = failure ?? string.Empty;
                evidence.phases = phases.ToArray();
                evidence.mainCameraConfigurationAfter = CameraSnapshot.Capture(mainCamera).ToString();
                if (!MainCameraUnchanged())
                {
                    evidence.result = "failed";
                    evidence.error = "main camera configuration changed during prototype";
                }
            }
            finally
            {
                if (evidence.result == "running")
                {
                    SetFailure("prototype aborted before completing all phases");
                    evidence.result = "failed";
                    evidence.error = failure;
                }
                phaseActive = false;
                running = false;
                StopRecorders();
                DisableNaturalCameras();
                UnsubscribeCallbacks();
                RemovePlayerLoopNode();
                if (session != null)
                {
                    session.StopSession();
                    bool restoreEnabled = sessionLifecycleSnapshotCaptured ? sessionEnabledBeforeRun : session.enabled;
                    bool restoreStartOnEnable = sessionLifecycleSnapshotCaptured ? sessionStartOnEnableBeforeRun : session.startOnEnable;
                    session.startOnEnable = false;
                    session.enabled = restoreEnabled;
                    session.startOnEnable = restoreStartOnEnable;
                }
                if (quilt != null && viewRenderModeSnapshotCaptured) quilt.viewRenderMode = viewRenderModeBeforeRun;
                if (TryReadPending(out int shutdownPending, out uint nativePending, out _) && shutdownPending == 0 && nativePending == 0)
                    CleanupNaturalCamerasAfterDrain();
                if (sourcePreparation != null && TryReadPending(out shutdownPending, out nativePending, out _) && shutdownPending == 0 && nativePending == 0)
                {
                    sourcePreparation.Dispose();
                    sourcePreparation = null;
                }
                if (sourceGuard != null && sourceGuardAdaptSnapshotCaptured)
                {
                    sourceGuard.adaptUnsupportedEffects = sourceGuardAdaptBeforeRun;
                    evidence.sourceGuardAdaptRestored = sourceGuard.adaptUnsupportedEffects == sourceGuardAdaptBeforeRun;
                }
                if (currentPhase != null && currentPhase.mode == PhaseMode.NaturalCameraBatch.ToString() && !phases.Contains(currentPhase))
                {
                    CapturePhaseSubmissionCounts(currentPhase);
                    currentPhase.naturalEpochsCompleted = naturalEpochCount;
                    currentPhase.naturalOwnedCameraCallbacks = naturalOwnedCameraCallbackCount;
                    currentPhase.naturalOwnedContexts = naturalOwnedContextCount;
                    currentPhase.renderToTransferFrameOffsetMin = naturalRenderToTransferFrameOffsetMin;
                    currentPhase.renderToTransferFrameOffsetMax = naturalRenderToTransferFrameOffsetMax;
                    SnapshotNaturalMainCameraCallbacks();
                    currentPhase.naturalTrace = SnapshotNaturalTrace();
                    currentPhase.error = failure ?? string.Empty;
                    if (string.IsNullOrEmpty(currentPhase.finishedUtc)) currentPhase.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    phases.Add(currentPhase);
                }
                evidence.sessionStateAfter = session == null ? "missing" : session.State.ToString();
                evidence.outputStateAfter = output == null ? "missing" : output.State.ToString();
                if (TryReadPending(out shutdownPending, out nativePending, out _))
                {
                    evidence.cleanupPendingCountsAvailable = true;
                    evidence.cleanupShutdownPending = shutdownPending;
                    evidence.cleanupNativePending = nativePending;
                }
                else
                {
                    evidence.cleanupPendingCountsAvailable = false;
                    evidence.cleanupShutdownPending = -1;
                    evidence.cleanupNativePending = uint.MaxValue;
                }
                evidence.finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                evidence.result = string.IsNullOrEmpty(failure) && evidence.result != "failed" ? "completed" : "failed";
                evidence.error = failure ?? evidence.error ?? string.Empty;
                evidence.phases = phases.ToArray();
                try { WriteEvidence(evidence); }
                catch (Exception error) { Debug.LogError("HDRP batch prototype evidence write failed: " + Sanitize(error.Message)); }
                if (string.IsNullOrEmpty(failure)) Debug.Log("HDRP batch prototype completed: " + Sanitize(Path.Combine(evidenceDirectory, "hdrp-camera-batch-prototype.json")));
                else Debug.LogError("HDRP batch prototype failed: " + Sanitize(failure));
            }
        }

        bool ValidateFixture(bool requireStaticCameraBatchFixture, out string error)
        {
            error = string.Empty;
            if (!Application.isPlaying || !Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
                error = "prototype requires Windows Editor Play mode";
            else if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
                error = "prototype requires Direct3D12";
            else if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                error = "prototype requires Linear color space";
            else if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset))
                error = "prototype requires an active HDRP asset";
            else if (sourceCamera == null || sourceCamera.enabled)
                error = "prototype requires a disabled source camera";
            else if (mainCamera == null || !mainCamera.enabled || mainCamera.targetTexture != null || mainCamera.tag != "MainCamera")
                error = "prototype requires an enabled MainCamera on the default display output";
            else if (quilt == null || quilt.sourceCamera != sourceCamera || quilt.useRenderRequests != true)
                error = "prototype requires the fixture QuiltRenderer and Standard Render Requests";
            else if (requireStaticCameraBatchFixture && quilt.viewRenderMode != QuiltViewRenderMode.Full)
                error = "camera-batch A/B/A requires Full view render mode";
            else if (sourceGuard == null || sourceGuard.GetComponent<Camera>() != sourceCamera || quilt.RenderGuard != sourceGuard)
                error = "prototype requires the fixture HDRP quilt guard";
            else if (rig == null || rig.Renderer != quilt)
                error = "prototype fixture rig is missing or references another renderer";
            else if (!rig.ValidateRig(out string rigError))
                error = "prototype fixture rig is invalid: " + rigError;
            else if (session == null || output == null || session.GetComponent<BridgeWorkerQuiltOutput>() != output)
                error = "prototype requires the fixture bridge session and output";
            else if (originalRunner == null || originalRunner.enabled)
                error = "the original validation runner must be present and disabled";
            else if (quilt.settings == null || quilt.settings.views != 48 || quilt.settings.columns != 8 || quilt.settings.rows != 6)
                error = "prototype requires an 8x6, 48-view fixture";
            else if (GraphicsSettings.currentRenderPipeline == null)
                error = "active render pipeline is unavailable";
            if (!string.IsNullOrEmpty(error)) return false;

            Volume[] volumes = FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; ++i)
                if (volumes[i] != null && !volumes[i].isGlobal)
                {
                    error = "prototype fixture contains a Local Volume; only the global-volume fixture is supported";
                    return false;
                }
            if (requireStaticCameraBatchFixture && !ValidateStaticCameraBatchFixture(volumes, out error)) return false;
            return InstallPlayerLoopNode(out error);
        }

        static bool ValidateStaticCameraBatchFixture(Volume[] volumes, out string error)
        {
            error = string.Empty;
            if (volumes == null || volumes.Length != 1 || volumes[0] == null || !volumes[0].isGlobal)
            {
                error = "camera-batch plan requires exactly one global HDRP Volume";
                return false;
            }

            VolumeProfile profile = volumes[0].sharedProfile;
            if (profile == null || !profile.TryGet<Exposure>(out Exposure exposure) || !exposure.mode.overrideState || exposure.mode.value != ExposureMode.Fixed ||
                !exposure.fixedExposure.overrideState)
            {
                error = "camera-batch plan requires an overridden Fixed Exposure global Volume";
                return false;
            }

            if (!profile.TryGet<MotionBlur>(out MotionBlur motionBlur) || !motionBlur.intensity.overrideState || motionBlur.intensity.value != 0f ||
                !profile.TryGet<ScreenSpaceReflection>(out ScreenSpaceReflection reflections) || !reflections.enabled.overrideState || reflections.enabled.value ||
                !profile.TryGet<GlobalIllumination>(out GlobalIllumination gi) || !gi.enable.overrideState || gi.enable.value ||
                !profile.TryGet<VolumetricClouds>(out VolumetricClouds clouds) || !clouds.enable.overrideState || clouds.enable.value ||
                !profile.TryGet<Fog>(out Fog fog) || !fog.enabled.overrideState || fog.enabled.value || !fog.enableVolumetricFog.overrideState || fog.enableVolumetricFog.value)
            {
                error = "camera-batch plan requires the static non-temporal HDRP Volume fixture";
                return false;
            }

            GameObject[] objects = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < objects.Length; ++i)
            {
                if (objects[i] == null) continue;
                Component[] components = objects[i].GetComponents<Component>();
                for (int componentIndex = 0; componentIndex < components.Length; ++componentIndex)
                    if (components[componentIndex] != null && components[componentIndex].GetType().FullName == "UnityEngine.VFX.VisualEffect")
                    {
                        error = "camera-batch plan does not support Visual Effect components";
                        return false;
                    }
            }
            return true;
        }

        IEnumerator WaitForHdrpBlitterReady(RunEvidence evidence)
        {
            const double timeoutSeconds = 10.0;
            const string expectedPipelineType = "UnityEngine.Rendering.HighDefinition.HDRenderPipeline";
            double started = Time.realtimeSinceStartupAsDouble;
            RenderPipeline lastPipeline = null;
            bool lastBlitMaterialAvailable = false;
            bool diagnosticWritten = false;
            while (Time.realtimeSinceStartupAsDouble - started < timeoutSeconds)
            {
                RenderPipeline pipeline = RenderPipelineManager.currentPipeline;
                bool isHdrp = pipeline != null && pipeline.GetType().FullName == expectedPipelineType;
                Material blitMaterial = Blitter.GetBlitMaterial(TextureDimension.Tex2D);
                bool blitMaterialAvailable = blitMaterial != null;
                lastPipeline = pipeline;
                lastBlitMaterialAvailable = blitMaterialAvailable;

                if (isHdrp && !diagnosticWritten)
                {
                    evidence.hdrpPreflightObserved = true;
                    evidence.initialHdrpPipelineType = Sanitize(pipeline.GetType().FullName);
                    evidence.initialHdrpPipelineManagedIdentity = RuntimeHelpers.GetHashCode(pipeline).ToString(CultureInfo.InvariantCulture);
                    evidence.initialTex2DBlitMaterialAvailable = blitMaterialAvailable;
                    Debug.Log("HDRP preflight: pipeline=" + evidence.initialHdrpPipelineType + ";managedIdentity=" + evidence.initialHdrpPipelineManagedIdentity + ";tex2dBlitMaterialAvailable=" + blitMaterialAvailable.ToString().ToLowerInvariant());
                    diagnosticWritten = true;
                }

                if (isHdrp && blitMaterialAvailable) yield break;
                yield return null;
            }

            if (!diagnosticWritten)
            {
                evidence.hdrpPreflightObserved = lastPipeline != null;
                evidence.initialHdrpPipelineType = lastPipeline == null ? "unavailable" : Sanitize(lastPipeline.GetType().FullName);
                evidence.initialHdrpPipelineManagedIdentity = lastPipeline == null ? string.Empty : RuntimeHelpers.GetHashCode(lastPipeline).ToString(CultureInfo.InvariantCulture);
                evidence.initialTex2DBlitMaterialAvailable = lastBlitMaterialAvailable;
                Debug.Log("HDRP preflight: pipeline=" + evidence.initialHdrpPipelineType + ";managedIdentity=" + evidence.initialHdrpPipelineManagedIdentity + ";tex2dBlitMaterialAvailable=" + lastBlitMaterialAvailable.ToString().ToLowerInvariant());
            }
            SetFailure("HDRP preflight timed out: an HDRP pipeline instance and shared Tex2D Blit material were not both available; bridge startup was not enabled");
        }

        bool StartSession(string selectedQuality)
        {
            stage = "start-session";
            if (!TryReadPending(out int shutdownPending, out uint nativePending, out string error))
            {
                SetFailure("pending-count diagnostics unavailable before start: " + error);
                return false;
            }
            if (shutdownPending != 0 || nativePending != 0)
            {
                SetFailure("worker shutdown is pending before phase start");
                return false;
            }
            if (!session.SetQuality(selectedQuality == "native" ? QuiltQualityPreset.Native : QuiltQualityPreset.Balanced))
            {
                SetFailure("could not set bridge worker quality: " + Sanitize(session.LastError));
                return false;
            }
            string runtimePath = Argument(RuntimeArgument);
            if (!string.IsNullOrWhiteSpace(runtimePath)) session.runtimePath = runtimePath;
            if (!session.StartSession())
            {
                SetFailure("bridge worker session start failed: " + Sanitize(session.LastError));
                return false;
            }
            return true;
        }

        IEnumerator WaitUntilReady()
        {
            stage = "wait-ready";
            double started = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - started < LifecycleTimeoutSeconds)
            {
                if (session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.Ready)
                {
                    if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                        SetFailure("bridge worker entered ready state with an error");
                    yield break;
                }
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                {
                    SetFailure("bridge worker startup failed: " + Sanitize(string.IsNullOrEmpty(session.LastError) ? output.LastError : session.LastError));
                    yield break;
                }
                yield return null;
            }
            SetFailure("bridge worker did not become ready before timeout");
        }

        IEnumerator WaitForAutoRenderPriming(uint baseline)
        {
            double started = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - started < LifecycleTimeoutSeconds)
            {
                RenderTexture atlas = quilt == null ? null : quilt.QuiltTexture;
                if (rig != null && rig.AutoRender && atlas != null && atlas.IsCreated() &&
                    output.SubmissionAttempts > 0 && output.Submissions > baseline)
                {
                    rig.AutoRender = false;
                    uint attemptsTarget = output.SubmissionAttempts;
                    double drainStarted = Time.realtimeSinceStartupAsDouble;
                    while (Time.realtimeSinceStartupAsDouble - drainStarted < NativeCompletionTimeoutSeconds)
                    {
                        if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !output.Ready ||
                            !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                        {
                            SetFailure("worker failed while settling the priming submission: " + Sanitize(string.IsNullOrEmpty(session.LastError) ? output.LastError : session.LastError));
                            yield break;
                        }
                        ulong settled = (ulong)output.Completed + output.Dropped;
                        if (attemptsTarget > 0 && settled >= attemptsTarget)
                        {
                            Debug.Log("HDRP prime settled attempts=" + attemptsTarget.ToString(CultureInfo.InvariantCulture) +
                                " completed=" + output.Completed.ToString(CultureInfo.InvariantCulture) +
                                " dropped=" + output.Dropped.ToString(CultureInfo.InvariantCulture));
                            yield break;
                        }
                        yield return null;
                    }
                    SetFailure("priming submission disposition did not settle: attempts=" + attemptsTarget.ToString(CultureInfo.InvariantCulture) +
                        " completed=" + output.Completed.ToString(CultureInfo.InvariantCulture) +
                        " dropped=" + output.Dropped.ToString(CultureInfo.InvariantCulture));
                    yield break;
                }
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                {
                    SetFailure("worker failed before quilt priming: " + Sanitize(string.IsNullOrEmpty(session.LastError) ? output.LastError : session.LastError));
                    yield break;
                }
                yield return null;
            }
            SetFailure("automatic quilt render did not prime an atlas before timeout");
        }

        IEnumerator WaitForPhaseCompletion(PhaseEvidence phase)
        {
            double started = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - started < NativeCompletionTimeoutSeconds)
            {
                CapturePhaseSubmissionCounts(phase);
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !output.Ready ||
                    !string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError))
                {
                    SetFailure("worker failed while settling phase submissions: " + Sanitize(string.IsNullOrEmpty(session.LastError) ? output.LastError : session.LastError) + "; " + PhaseSubmissionDiagnostic(phase));
                    yield break;
                }
                ulong settled = (ulong)phase.completedDelta + phase.droppedDelta;
                if (settled >= phase.submissionAttemptsDelta) break;
                yield return null;
            }
            CapturePhaseSubmissionCounts(phase);
            phase.nativeCompletionObserved = phase.completedDelta > 0;
            phase.nativeDispositionSettled = (ulong)phase.completedDelta + phase.droppedDelta >= phase.submissionAttemptsDelta;
            if (!phase.nativeDispositionSettled)
                SetFailure("phase submission disposition did not settle before timeout: " + PhaseSubmissionDiagnostic(phase));
            else if (!phase.nativeCompletionObserved)
                SetFailure("phase produced no fresh native worker completion: " + PhaseSubmissionDiagnostic(phase));
        }

        void CapturePhaseSubmissionCounts(PhaseEvidence phase)
        {
            phase.submissionAttemptsAfter = output.SubmissionAttempts;
            phase.submissionAttemptsDelta = unchecked(phase.submissionAttemptsAfter - phase.submissionAttemptsBaseline);
            phase.queuedSubmissionsAfter = output.Submissions;
            phase.queuedSubmissionsDelta = unchecked(phase.queuedSubmissionsAfter - phase.queuedSubmissionsBaseline);
            phase.outputSubmissions = phase.queuedSubmissionsDelta;
            phase.completedAfter = output.Completed;
            phase.completedDelta = unchecked(phase.completedAfter - phase.completedBaseline);
            phase.droppedAfter = output.Dropped;
            phase.droppedDelta = unchecked(phase.droppedAfter - phase.droppedBaseline);
            phase.phaseFrameCount = phaseFrameCount;
        }

        string PhaseSubmissionDiagnostic(PhaseEvidence phase)
        {
            return "frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                " phaseStartFrame=" + phase.phaseStartFrame.ToString(CultureInfo.InvariantCulture) +
                " phaseFrameCount=" + phaseFrameCount.ToString(CultureInfo.InvariantCulture) +
                " lastBoundaryFrame=" + lastBoundaryFrame.ToString(CultureInfo.InvariantCulture) +
                " attemptDelta=" + phase.submissionAttemptsDelta.ToString(CultureInfo.InvariantCulture) +
                " queuedDelta=" + phase.queuedSubmissionsDelta.ToString(CultureInfo.InvariantCulture) +
                " cachedDropped=" + output.Dropped.ToString(CultureInfo.InvariantCulture) +
                " completed=" + output.Completed.ToString(CultureInfo.InvariantCulture) +
                " outputState=" + output.State.ToString() +
                " sessionState=" + session.State.ToString();
        }

        string BoundarySubmissionDiagnostic(uint attemptDelta, uint queuedDelta)
        {
            return "frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) +
                " phaseStartFrame=" + (currentPhase == null ? -1 : currentPhase.phaseStartFrame).ToString(CultureInfo.InvariantCulture) +
                " phaseFrameCount=" + phaseFrameCount.ToString(CultureInfo.InvariantCulture) +
                " lastBoundaryFrame=" + lastBoundaryFrame.ToString(CultureInfo.InvariantCulture) +
                " attemptDelta=" + attemptDelta.ToString(CultureInfo.InvariantCulture) +
                " queuedDelta=" + queuedDelta.ToString(CultureInfo.InvariantCulture) +
                " cachedDropped=" + output.Dropped.ToString(CultureInfo.InvariantCulture) +
                " completed=" + output.Completed.ToString(CultureInfo.InvariantCulture) +
                " outputState=" + output.State.ToString() +
                " sessionState=" + session.State.ToString();
        }

        bool ValidateActiveProfile(out string error)
        {
            error = string.Empty;
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || !output.Ready)
                error = "bridge worker is not rendering and ready";
            else if (session.Profile.Columns * session.Profile.Rows != 48 || session.Profile.Columns != 8 || session.Profile.Rows != 6)
                error = "bridge display must provide 48 views in an 8x6 quilt";
            else
            {
                QuiltQualityPreset preset = qualityName == "native" ? QuiltQualityPreset.Native : QuiltQualityPreset.Balanced;
                QuiltQualityProfile selected = ViewWeaveBridgeSession.CreateQualityProfile(session.Profile, preset);
                if (qualityName == "balanced" && (selected.QuiltWidth != 3840 || selected.QuiltHeight != 2160))
                    error = "Balanced profile must resolve to 3840x2160 for the initial prototype";
                else if (quilt.settings.quiltWidth != selected.QuiltWidth || quilt.settings.quiltHeight != selected.QuiltHeight || quilt.settings.columns != selected.Columns || quilt.settings.rows != selected.Rows || quilt.settings.views != selected.Views)
                    error = "active quilt settings do not match the selected bridge profile";
            }
            return string.IsNullOrEmpty(error);
        }

        void PopulateRunProfile(RunEvidence evidence, out string error)
        {
            error = string.Empty;
            QuiltQualityPreset preset = qualityName == "native" ? QuiltQualityPreset.Native : QuiltQualityPreset.Balanced;
            QuiltQualityProfile selected = ViewWeaveBridgeSession.CreateQualityProfile(session.Profile, preset);
            evidence.quiltWidth = selected.QuiltWidth;
            evidence.quiltHeight = selected.QuiltHeight;
            evidence.columns = selected.Columns;
            evidence.rows = selected.Rows;
            evidence.views = selected.Views;
            evidence.deviceProfile = session.Profile.ToString();
            evidence.quiltGraphicsFormat = quilt.QuiltTexture == null ? string.Empty : quilt.QuiltTexture.graphicsFormat.ToString();
            evidence.deviceProfileError = string.Empty;
            if (quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated()) error = "quilt atlas was not created before profile capture";
        }

        bool PrepareNaturalCameras(out string error)
        {
            error = string.Empty;
            try
            {
                if (quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated()) throw new InvalidOperationException("legacy warmup did not create a quilt atlas");
                if (sourcePreparation != null) throw new InvalidOperationException("source HDRP preparation lease is already active");
                sourcePreparation = sourceGuard.PrepareForQuilt();
                HDAdditionalCameraData sourceData = sourceCamera.GetComponent<HDAdditionalCameraData>();
                if (sourceData == null) throw new InvalidOperationException("source HDRP camera data is missing");

                QuiltLayout layout = quilt.settings.GetLayout();
                if (layout.ViewCount != 48) throw new InvalidOperationException("natural camera batch requires 48 views");
                RenderTexture atlas = quilt.QuiltTexture;
                if (atlas.antiAliasing != 1) throw new InvalidOperationException("quilt atlas must use MSAA 1");
                if ((SystemInfo.copyTextureSupport & CopyTextureSupport.Basic) == 0) throw new NotSupportedException("basic texture copy is not supported");
                viewIndices.Clear();
                viewCameras.Clear();
                viewCameraObjects.Clear();
                viewTargets.Clear();
                copiedViewIndices.Clear();
                for (int index = 0; index < layout.ViewCount; ++index)
                {
                    GameObject viewObject = new GameObject(ActiveCameraNamePrefix + index.ToString("D2", CultureInfo.InvariantCulture)) { hideFlags = HideFlags.HideAndDontSave };
                    viewCameraObjects.Add(viewObject);
                    Camera viewCamera = viewObject.AddComponent<Camera>();
                    viewCameras.Add(viewCamera);
                    viewCamera.enabled = false;
                    viewCamera.CopyFrom(sourceCamera);
                    HDAdditionalCameraData viewData = viewObject.AddComponent<HDAdditionalCameraData>();
                    sourceData.CopyTo(viewData);
                    viewObject.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);

                    RectInt tile = layout.GetTile(index);
                    float offset = QuiltMath.ViewOffset(index, layout.ViewCount, quilt.settings.viewCone, quilt.settings.cameraDistance);
                    viewCamera.transform.position = sourceCamera.transform.position + sourceCamera.transform.rotation * Vector3.right * offset;
                    viewCamera.transform.rotation = QuiltMath.ParallelViewRotation(sourceCamera.transform.rotation);
                    viewCamera.projectionMatrix = QuiltMath.OffAxisProjection(quilt.settings.fieldOfView, quilt.settings.viewAspect, sourceCamera.nearClipPlane, sourceCamera.farClipPlane, offset, quilt.settings.cameraDistance);
                    viewCamera.aspect = quilt.settings.viewAspect;
                    viewCamera.rect = new Rect(0f, 0f, 1f, 1f);
                    RenderTexture target = CreateViewTarget(atlas, tile.width, tile.height, index);
                    viewTargets.Add(target);
                    viewCamera.targetTexture = target;
                    viewIndices.Add(viewCamera, index);
                }
                currentPhase = null;
                SubscribeCallbacks();
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        static RenderTexture CreateViewTarget(RenderTexture atlas, int width, int height, int index)
        {
            RenderTextureDescriptor descriptor = atlas.descriptor;
            descriptor.width = width;
            descriptor.height = height;
            descriptor.depthBufferBits = 24;
            descriptor.msaaSamples = 1;
            descriptor.volumeDepth = 1;
            descriptor.dimension = TextureDimension.Tex2D;
            descriptor.useMipMap = false;
            descriptor.autoGenerateMips = false;
            descriptor.graphicsFormat = atlas.graphicsFormat;
            RenderTexture target = new RenderTexture(descriptor)
            {
                name = ActiveCameraNamePrefix + index.ToString("D2", CultureInfo.InvariantCulture) + ".RT",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            target.Create();
            if (!target.IsCreated() || target.graphicsFormat != atlas.graphicsFormat || target.antiAliasing != 1)
            {
                target.Release();
                Destroy(target);
                throw new InvalidOperationException("per-view target format or MSAA does not match the quilt atlas");
            }
            return target;
        }

        void StartPhase(PhaseMode mode)
        {
            phaseMode = mode;
            rig.AutoRender = mode == PhaseMode.LegacyRequests;
            currentPhase = new PhaseEvidence
            {
                mode = mode.ToString(),
                renderMode = quilt.viewRenderMode.ToString(),
                expectedRenderedViewCount = QuiltViewSampling.GetRenderCount(quilt.settings.views, quilt.viewRenderMode),
                actualRenderedViewCount = 0,
                renderedEndpointsRetained = QuiltViewSampling.GetRenderedViewIndex(quilt.settings.views, quilt.viewRenderMode, 0) == 0
                    && QuiltViewSampling.GetRenderedViewIndex(quilt.settings.views, quilt.viewRenderMode, QuiltViewSampling.GetRenderCount(quilt.settings.views, quilt.viewRenderMode) - 1) == quilt.settings.views - 1,
                startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                graphicsFormat = quilt.QuiltTexture.graphicsFormat.ToString(),
                tileCount = quilt.settings.views,
                viewCameraCount = mode == PhaseMode.NaturalCameraBatch ? viewCameras.Count : 0,
                estimatedViewColorBytes = mode == PhaseMode.NaturalCameraBatch
                    ? (long)quilt.settings.quiltWidth / quilt.settings.columns * (quilt.settings.quiltHeight / quilt.settings.rows) * quilt.settings.views * 4L
                    : 0L,
                completedBaseline = output.Completed,
                droppedBaseline = output.Dropped,
                submissionAttemptsBaseline = output.SubmissionAttempts,
                queuedSubmissionsBaseline = output.Submissions,
                phaseStartFrame = Time.frameCount
            };
            QuiltLayout outputLayout = quilt.settings.GetLayout();
            currentPhase.tileCount = outputLayout.ViewCount;
            currentPhase.outputTileWidth = outputLayout.GetTile(0).width;
            currentPhase.outputTileHeight = outputLayout.GetTile(0).height;
            phaseFrameCount = 0;
            measuredFrameCount = 0;
            phaseWindowComplete = false;
            phaseActive = true;
            lastObservedSubmissions = output.Submissions;
            lastObservedSubmissionAttempts = output.SubmissionAttempts;
            mainCameraCallbacksAtPhaseStart = mainCameraCallbacks;
            samples = new FrameSample[MeasurementFrames];
            if (mode == PhaseMode.NaturalCameraBatch)
            {
                ResetNaturalTrace();
                naturalMeasurementStarted = false;
                naturalOwnedContextCount = 0;
                naturalOwnedCameraCallbackCount = 0;
                naturalEpochCount = 0;
                naturalEpochId = 0;
                naturalContextSubmitCount = 0;
                naturalMainCameraCallbacksPerEpoch.Clear();
                naturalMainCameraCallbacksSnapshotCaptured = false;
                naturalRenderToTransferOffsetSet = false;
                naturalContextId = 0;
                naturalPhaseArmedAt = Time.realtimeSinceStartupAsDouble;
                naturalFirstReadyDeadline = naturalPhaseArmedAt + NaturalFirstReadyTimeoutSeconds;
                naturalRenderToTransferFrameOffsetMin = 0;
                naturalRenderToTransferFrameOffsetMax = 0;
                currentPhase.startedUtc = string.Empty;
                ArmNaturalEpoch();
            }
            StartRecorders();
            stage = mode == PhaseMode.NaturalCameraBatch ? "wait-first-natural-epoch" : "warmup";
        }

        void StartRecorders()
        {
            StopRecorders();
            drawCallsRecorder = TryStartRecorder(ProfilerCategory.Render, "Draw Calls Count");
            initializeBufferRecorder = TryStartRecorder(ProfilerCategory.Render, "BufferD3D12.InitializeResource");
            createBufferRecorder = TryStartRecorder(ProfilerCategory.Render, "BufferD3D12.CreateBufferResource");
            totalMemoryRecorder = TryStartRecorder(ProfilerCategory.Memory, "Total Used Memory");
            gfxMemoryRecorder = TryStartRecorder(ProfilerCategory.Memory, "Gfx Used Memory");
            recorderSetStarted = true;
        }

        static ProfilerRecorder TryStartRecorder(ProfilerCategory category, string marker)
        {
            try { return ProfilerRecorder.StartNew(category, marker, 1, ProfilerRecorderOptions.SumAllSamplesInFrame); }
            catch { return default; }
        }

        void StopRecorders()
        {
            if (!recorderSetStarted) return;
            drawCallsRecorder.Dispose();
            initializeBufferRecorder.Dispose();
            createBufferRecorder.Dispose();
            totalMemoryRecorder.Dispose();
            gfxMemoryRecorder.Dispose();
            recorderSetStarted = false;
        }

        void SubscribeCallbacks()
        {
            if (callbacksSubscribed) return;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            callbacksSubscribed = true;
        }

        void UnsubscribeCallbacks()
        {
            if (!callbacksSubscribed) return;
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
            callbacksSubscribed = false;
        }

        bool IsNaturalCaptureActive()
        {
            return phaseActive && phaseMode == PhaseMode.NaturalCameraBatch && string.IsNullOrEmpty(failure);
        }

        void ResetNaturalTrace()
        {
            naturalTracePrefixCount = 0;
            naturalTraceTailCount = 0;
            naturalTraceTailNext = 0;
            naturalTraceSequence = 0;
        }

        void ArmNaturalEpoch()
        {
            naturalEpochId++;
            naturalEpochArmed = true;
            naturalEpochReady = false;
            naturalContextOpen = false;
            naturalRenderFrame = -1;
            naturalContextFrame = -1;
            naturalContextExpectedCount = 0;
            naturalContextCopyCount = 0;
            naturalMainCameraFrame = -1;
            naturalMainCameraCallbacksForFrame = 0;
            naturalCopyCount = 0;
            copiedCameras.Clear();
            copiedViewIndices.Clear();
            currentContextExpectedViews.Clear();
            currentContextCopiedViews.Clear();
            int enabled = 0;
            for (int i = 0; i < viewCameras.Count; ++i)
            {
                if (viewCameras[i] == null) continue;
                viewCameras[i].enabled = true;
                if (viewCameras[i].enabled) enabled++;
            }
            if (enabled != 48)
                SetFailure("natural epoch did not enable all 48 owned cameras");
            else
                TraceNatural("epoch-armed", -1, 48, 0, false, 0);
        }

        void DisableNaturalCameras()
        {
            for (int i = 0; i < viewCameras.Count; ++i)
                if (viewCameras[i] != null) viewCameras[i].enabled = false;
        }

        void TraceNatural(string eventName, int cameraIndex, int contextOwnedCount, int contextSubmitCount, bool ready, uint attemptDelta, int contextId = -1)
        {
            NaturalTraceEntry entry = new NaturalTraceEntry
            {
                sequence = naturalTraceSequence++,
                eventName = eventName,
                frame = Time.frameCount,
                epoch = naturalEpochId,
                context = contextId >= 0 ? contextId : naturalContextId,
                cameraIndex = cameraIndex,
                contextOwnedCount = contextOwnedCount,
                uniqueViewCount = copiedViewIndices.Count,
                copyCount = naturalCopyCount,
                contextSubmitCount = contextSubmitCount,
                ready = ready,
                attemptDelta = attemptDelta
            };
            if (naturalEpochId <= 2 && naturalTracePrefixCount < NaturalTracePrefixCapacity)
            {
                naturalTraceEntries[naturalTracePrefixCount++] = entry;
                return;
            }

            int tailCapacity = NaturalTraceCapacity - NaturalTracePrefixCapacity;
            int destination = NaturalTracePrefixCapacity + naturalTraceTailNext;
            naturalTraceEntries[destination] = entry;
            naturalTraceTailNext = (naturalTraceTailNext + 1) % tailCapacity;
            if (naturalTraceTailCount < tailCapacity) naturalTraceTailCount++;
        }

        NaturalTraceEntry[] SnapshotNaturalTrace()
        {
            int tailCapacity = NaturalTraceCapacity - NaturalTracePrefixCapacity;
            NaturalTraceEntry[] snapshot = new NaturalTraceEntry[naturalTracePrefixCount + naturalTraceTailCount];
            Array.Copy(naturalTraceEntries, 0, snapshot, 0, naturalTracePrefixCount);
            int tailStart = naturalTraceTailCount == tailCapacity ? naturalTraceTailNext : 0;
            for (int i = 0; i < naturalTraceTailCount; ++i)
            {
                int source = NaturalTracePrefixCapacity + ((tailStart + i) % tailCapacity);
                snapshot[naturalTracePrefixCount + i] = naturalTraceEntries[source];
            }
            return snapshot;
        }

        void SnapshotNaturalMainCameraCallbacks()
        {
            if (naturalMainCameraCallbacksSnapshotCaptured || currentPhase == null || currentPhase.mode != PhaseMode.NaturalCameraBatch.ToString()) return;
            currentPhase.naturalMainCameraCallbacksPerEpoch = naturalMainCameraCallbacksPerEpoch.ToArray();
            naturalMainCameraCallbacksSnapshotCaptured = true;
        }

        void BeginNaturalMeasurementWindow()
        {
            naturalMeasurementStarted = true;
            currentPhase.startedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            currentPhase.phaseStartFrame = Time.frameCount;
            currentPhase.firstReadyWaitMs = Math.Max(0.0, (Time.realtimeSinceStartupAsDouble - naturalPhaseArmedAt) * 1000.0);
            currentPhase.submissionAttemptsBaseline = output.SubmissionAttempts;
            currentPhase.queuedSubmissionsBaseline = output.Submissions;
            currentPhase.completedBaseline = output.Completed;
            currentPhase.droppedBaseline = output.Dropped;
            lastObservedSubmissionAttempts = output.SubmissionAttempts;
            lastObservedSubmissions = output.Submissions;
            naturalRenderToTransferOffsetSet = false;
            stage = "warmup";
        }

        void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!IsNaturalCaptureActive() || cameras == null) return;
            currentContextExpectedViews.Clear();
            for (int i = 0; i < cameras.Count; ++i)
                if (cameras[i] != null && viewIndices.TryGetValue(cameras[i], out int viewIndex))
                    currentContextExpectedViews.Add(viewIndex);
            if (currentContextExpectedViews.Count == 0) return;
            if (!naturalEpochArmed || naturalEpochReady)
            {
                SetFailure("owned HDRP context began without an armed natural epoch");
                return;
            }
            if (naturalContextOpen)
            {
                SetFailure("nested owned HDRP contexts are unsupported");
                return;
            }
            if (naturalRenderFrame < 0) naturalRenderFrame = Time.frameCount;
            else if (naturalRenderFrame != Time.frameCount)
            {
                SetFailure("natural epoch mixed render frames");
                return;
            }

            naturalContextOpen = true;
            naturalContextFrame = Time.frameCount;
            naturalContextExpectedCount = currentContextExpectedViews.Count;
            naturalContextCopyCount = 0;
            currentContextCopiedViews.Clear();
            naturalOwnedContextCount++;
            naturalContextId++;
            TraceNatural("context-begin", -1, currentContextExpectedViews.Count, naturalContextSubmitCount, false, 0);
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!phaseActive || phaseMode != PhaseMode.NaturalCameraBatch || camera == null) return;
            if (camera == mainCamera)
            {
                mainCameraCallbacks++;
                if (naturalEpochArmed || (naturalEpochReady && naturalRenderFrame == Time.frameCount))
                {
                    if (naturalMainCameraFrame != Time.frameCount)
                    {
                        naturalMainCameraFrame = Time.frameCount;
                        naturalMainCameraCallbacksForFrame = 0;
                    }
                    naturalMainCameraCallbacksForFrame++;
                }
                TraceNatural("main-camera-end", -1, naturalContextOpen ? naturalContextExpectedCount : 0, naturalContextSubmitCount, naturalEpochReady, 0,
                    naturalContextOpen ? naturalContextId : 0);
                return;
            }
            if (!viewIndices.TryGetValue(camera, out int index)) return;
            if (!naturalEpochArmed || naturalEpochReady || !naturalContextOpen || naturalContextFrame != Time.frameCount || naturalRenderFrame != Time.frameCount ||
                !currentContextExpectedViews.Contains(index) || !copiedViewIndices.Add(index) || !copiedCameras.Add(camera))
            {
                SetFailure("duplicate, unexpected, or out-of-frame natural camera callback");
                return;
            }
            try
            {
                RectInt tile = quilt.settings.GetLayout().GetTile(index);
                if (copyCommand == null) copyCommand = new CommandBuffer { name = "ViewWeave HDRP Batch Tile Copy" };
                copyCommand.Clear();
                copyCommand.CopyTexture(viewTargets[index], 0, 0, 0, 0, tile.width, tile.height, quilt.QuiltTexture, 0, 0, tile.x, tile.y);
                context.ExecuteCommandBuffer(copyCommand);
                naturalCopyCount++;
                naturalContextCopyCount++;
                naturalOwnedCameraCallbackCount++;
                currentContextCopiedViews.Add(index);
                TraceNatural("tile-copy", index, currentContextExpectedViews.Count, naturalContextSubmitCount, false, 0);
            }
            catch (Exception error)
            {
                SetFailure("natural camera tile copy failed: " + error.GetType().Name + ": " + error.Message);
            }
        }

        void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!IsNaturalCaptureActive() || cameras == null) return;
            int contextOwnedCount = 0;
            endedContextOwnedViews.Clear();
            for (int i = 0; i < cameras.Count; ++i)
                if (cameras[i] != null && viewIndices.TryGetValue(cameras[i], out int viewIndex))
                {
                    contextOwnedCount++;
                    endedContextOwnedViews.Add(viewIndex);
                }
            if (contextOwnedCount == 0)
            {
                if (naturalContextOpen) SetFailure("owned HDRP context ended with an unexpected camera list");
                return;
            }
            if (!naturalContextOpen || naturalContextFrame != Time.frameCount || naturalRenderFrame != Time.frameCount ||
                contextOwnedCount != naturalContextExpectedCount || currentContextCopiedViews.Count != naturalContextExpectedCount ||
                naturalContextCopyCount != naturalContextExpectedCount || !endedContextOwnedViews.SetEquals(currentContextExpectedViews))
            {
                SetFailure("owned HDRP context ended before all of its tile copies completed");
                return;
            }

            try
            {
                context.Submit();
                naturalContextSubmitCount++;
                currentPhase.naturalContextSubmits++;
                TraceNatural("context-submit", -1, naturalContextExpectedCount, naturalContextSubmitCount, false, 0);
                naturalContextOpen = false;
                naturalContextExpectedCount = 0;
                naturalContextCopyCount = 0;
                currentContextExpectedViews.Clear();
                currentContextCopiedViews.Clear();

                if (naturalCopyCount == 48 && copiedViewIndices.Count == 48)
                {
                    naturalEpochArmed = false;
                    naturalEpochReady = true;
                    naturalEpochCount++;
                    DisableNaturalCameras();
                    TraceNatural("epoch-ready", -1, 48, naturalContextSubmitCount, true, 0);
                }
                else if (naturalCopyCount > 48 || copiedViewIndices.Count > 48)
                {
                    SetFailure("natural epoch copied more than 48 unique view tiles");
                }
            }
            catch (Exception error)
            {
                SetFailure("owned HDRP context submit failed: " + error.GetType().Name + ": " + error.Message);
            }
        }

        static bool TryInsertAfterAutoRenderScheduler(ref PlayerLoopSystem system, PlayerLoopSystem node)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
            {
                if (children[i].type != null && children[i].type.FullName == "ViewWeave.QuiltAutoRenderScheduler")
                {
                    PlayerLoopSystem[] expanded = new PlayerLoopSystem[children.Length + 1];
                    Array.Copy(children, 0, expanded, 0, i + 1);
                    expanded[i + 1] = node;
                    Array.Copy(children, i + 1, expanded, i + 2, children.Length - i - 1);
                    system.subSystemList = expanded;
                    return true;
                }
                if (TryInsertAfterAutoRenderScheduler(ref children[i], node)) return true;
            }
            return false;
        }

        bool InstallPlayerLoopNode(out string error)
        {
            error = string.Empty;
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (ContainsPlayerLoopNode(loop, typeof(HdrpCameraBatchPerformancePrototype)))
            {
                error = "prototype PlayerLoop node is already installed";
                return false;
            }
            PlayerLoopSystem node = new PlayerLoopSystem { type = typeof(HdrpCameraBatchPerformancePrototype), updateDelegate = RunAtFinishFrameBoundary };
            if (!TryInsertAfterAutoRenderScheduler(ref loop, node))
            {
                error = "could not find ViewWeave.QuiltAutoRenderScheduler after frame rendering";
                return false;
            }
            PlayerLoop.SetPlayerLoop(loop);
            playerLoopInstalled = true;
            return true;
        }

        static bool ContainsPlayerLoopNode(PlayerLoopSystem system, Type nodeType)
        {
            if (system.type == nodeType) return true;
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
                if (ContainsPlayerLoopNode(children[i], nodeType)) return true;
            return false;
        }

        static bool RemovePlayerLoopNode(ref PlayerLoopSystem system, Type nodeType)
        {
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            int keep = 0;
            bool removed = false;
            for (int i = 0; i < children.Length; ++i)
            {
                if (children[i].type == nodeType) removed = true;
                else keep++;
            }
            if (removed)
            {
                PlayerLoopSystem[] compact = new PlayerLoopSystem[keep];
                int destination = 0;
                for (int i = 0; i < children.Length; ++i)
                    if (children[i].type != nodeType) compact[destination++] = children[i];
                system.subSystemList = compact;
                return true;
            }
            for (int i = 0; i < children.Length; ++i)
                if (RemovePlayerLoopNode(ref children[i], nodeType)) return true;
            return false;
        }

        void RemovePlayerLoopNode()
        {
            if (!playerLoopInstalled) return;
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (RemovePlayerLoopNode(ref loop, typeof(HdrpCameraBatchPerformancePrototype))) PlayerLoop.SetPlayerLoop(loop);
            playerLoopInstalled = false;
        }

        void RunAtFinishFrameBoundary()
        {
            HdrpCameraBatchPerformancePrototype instance = active;
            if (instance == null || instance != this || !running || !phaseActive || !string.IsNullOrEmpty(failure)) return;
            try
            {
                if (phaseMode == PhaseMode.NaturalCameraBatch && !naturalMeasurementStarted)
                {
                    if (!naturalEpochReady)
                    {
                        if (naturalRenderFrame >= 0 && naturalRenderFrame != Time.frameCount)
                            throw new InvalidOperationException("initial natural epoch did not become ready within one render frame");
                        if (Time.realtimeSinceStartupAsDouble >= naturalFirstReadyDeadline)
                            throw new TimeoutException("natural camera batch did not produce its first ready epoch within 10 seconds");
                        lastBoundaryFrame = Time.frameCount;
                        return;
                    }
                    BeginNaturalMeasurementWindow();
                }

                if (phaseMode == PhaseMode.LegacyRequests)
                {
                    currentPhase.actualRenderedViewCount = quilt.LastRenderedViewCount;
                    if (!rig.AutoRender || currentPhase.actualRenderedViewCount != currentPhase.expectedRenderedViewCount)
                        throw new InvalidOperationException("legacy scheduler rendered " + currentPhase.actualRenderedViewCount.ToString(CultureInfo.InvariantCulture) + " views; expected " + currentPhase.expectedRenderedViewCount.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    if (!naturalEpochReady || naturalEpochArmed || naturalContextOpen || naturalRenderFrame < 0 || copiedViewIndices.Count != 48 ||
                        copiedCameras.Count != 48 || naturalCopyCount != 48 || Time.frameCount < naturalRenderFrame)
                        throw new InvalidOperationException("natural camera phase reached a boundary without one complete ready epoch");
                    int frameOffset = Time.frameCount - naturalRenderFrame;
                    if (!naturalRenderToTransferOffsetSet)
                    {
                        naturalRenderToTransferOffsetSet = true;
                        naturalRenderToTransferFrameOffsetMin = frameOffset;
                        naturalRenderToTransferFrameOffsetMax = frameOffset;
                    }
                    else
                    {
                        naturalRenderToTransferFrameOffsetMin = Math.Min(naturalRenderToTransferFrameOffsetMin, frameOffset);
                        naturalRenderToTransferFrameOffsetMax = Math.Max(naturalRenderToTransferFrameOffsetMax, frameOffset);
                        if (naturalRenderToTransferFrameOffsetMin != naturalRenderToTransferFrameOffsetMax)
                            throw new InvalidOperationException("natural render-to-transfer frame offset varied during the phase");
                    }

                    output.Submit(quilt.QuiltTexture, quilt.settings.GetLayout());
                    currentPhase.actualRenderedViewCount = 48;
                    currentPhase.uniqueViewCameraCallbacks = copiedViewIndices.Count;
                    currentPhase.mainCameraCallbacks = mainCameraCallbacks - mainCameraCallbacksAtPhaseStart;
                        int mainCallbacksForEpoch = naturalMainCameraFrame == naturalRenderFrame ? naturalMainCameraCallbacksForFrame : 0;
                        naturalMainCameraCallbacksPerEpoch.Add(mainCallbacksForEpoch);
                        currentPhase.naturalMainCameraCallbacksTotal += mainCallbacksForEpoch;
                        if (naturalMainCameraCallbacksPerEpoch.Count == 1)
                        {
                            currentPhase.naturalMainCameraCallbacksMin = mainCallbacksForEpoch;
                            currentPhase.naturalMainCameraCallbacksMax = mainCallbacksForEpoch;
                        }
                        else
                        {
                            currentPhase.naturalMainCameraCallbacksMin = Math.Min(currentPhase.naturalMainCameraCallbacksMin, mainCallbacksForEpoch);
                            currentPhase.naturalMainCameraCallbacksMax = Math.Max(currentPhase.naturalMainCameraCallbacksMax, mainCallbacksForEpoch);
                        }
                        if (mainCallbacksForEpoch == 1)
                            currentPhase.naturalEpochsWithOneMainCameraCallback++;
                        if (mainCallbacksForEpoch != 1)
                            throw new InvalidOperationException("natural render epoch observed " + mainCallbacksForEpoch.ToString(CultureInfo.InvariantCulture) + " MainCamera callbacks; expected exactly one");
                }

                uint afterAttempts = output.SubmissionAttempts;
                uint afterQueued = output.Submissions;
                uint attemptDelta = unchecked(afterAttempts - lastObservedSubmissionAttempts);
                uint queuedDelta = unchecked(afterQueued - lastObservedSubmissions);
                if (attemptDelta != 1 || queuedDelta > 1)
                    throw new InvalidOperationException("phase submission boundary invariant failed: " +
                        BoundarySubmissionDiagnostic(attemptDelta, queuedDelta));
                if (phaseMode == PhaseMode.NaturalCameraBatch)
                    TraceNatural("output-submit", -1, 48, naturalContextSubmitCount, true, attemptDelta);
                lastObservedSubmissionAttempts = afterAttempts;
                lastObservedSubmissions = afterQueued;

                if (output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !string.IsNullOrEmpty(output.LastError) || session.State != ViewWeaveBridgeSession.SessionState.Rendering)
                    throw new InvalidOperationException("worker output faulted during a prototype phase");

                int frameInPhase = phaseFrameCount++;
                if (frameInPhase >= WarmupFrames)
                {
                    if (measuredFrameCount >= MeasurementFrames) throw new InvalidOperationException("prototype exceeded the measurement window");
                    samples[measuredFrameCount++] = CaptureSample();
                    currentPhase.measuredFrames = measuredFrameCount;
                }
                if (phaseFrameCount >= WarmupFrames + MeasurementFrames)
                {
                    phaseWindowComplete = true;
                    phaseActive = false;
                    if (rig != null) rig.AutoRender = false;
                    if (phaseMode == PhaseMode.NaturalCameraBatch)
                    {
                        naturalEpochReady = false;
                        naturalEpochArmed = false;
                        DisableNaturalCameras();
                    }
                    FinalizeStats(currentPhase);
                }
                else if (phaseMode == PhaseMode.NaturalCameraBatch)
                {
                    naturalEpochReady = false;
                    ArmNaturalEpoch();
                }
                if (lastBoundaryFrame >= 0 && Time.frameCount <= lastBoundaryFrame)
                    throw new InvalidOperationException("finish-frame callback did not advance with engine frames");
                lastBoundaryFrame = Time.frameCount;
            }
            catch (Exception error)
            {
                phaseActive = false;
                phaseWindowComplete = true;
                SetFailure(error.GetType().Name + ": " + error.Message);
                if (rig != null) rig.AutoRender = false;
                if (phaseMode == PhaseMode.NaturalCameraBatch)
                {
                    naturalEpochReady = false;
                    naturalEpochArmed = false;
                    DisableNaturalCameras();
                }
            }
        }

        FrameSample CaptureSample()
        {
            FrameSample sample = new FrameSample { deltaMs = Time.unscaledDeltaTime * 1000.0 };
            FrameTimingManager.CaptureFrameTimings();
            uint timingCount = FrameTimingManager.GetLatestTimings(1, latestTiming);
            if (timingCount > 0)
            {
                FrameTiming timing = latestTiming[0];
                sample.cpuFrameMs = FinitePositive(timing.cpuFrameTime);
                sample.cpuMainThreadMs = FinitePositive(timing.cpuMainThreadFrameTime);
                sample.cpuRenderThreadMs = FinitePositive(timing.cpuRenderThreadFrameTime);
                sample.gpuFrameMs = FinitePositive(timing.gpuFrameTime);
            }
            if (drawCallsRecorder.Valid) sample.drawCalls = drawCallsRecorder.LastValue;
            if (initializeBufferRecorder.Valid) sample.initializeBuffer = initializeBufferRecorder.LastValue;
            if (createBufferRecorder.Valid) sample.createBuffer = createBufferRecorder.LastValue;
            if (totalMemoryRecorder.Valid) sample.totalMemory = totalMemoryRecorder.LastValue;
            if (gfxMemoryRecorder.Valid) sample.gfxMemory = gfxMemoryRecorder.LastValue;
            return sample;
        }

        static double FinitePositive(double value) => value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value) ? value : 0.0;

        void FinalizeStats(PhaseEvidence phase)
        {
            phase.measuredFrames = measuredFrameCount;
            phase.frameDeltaMs = SummarizeSamples(samples, measuredFrameCount, value => value.deltaMs);
            phase.cpuFrameMs = SummarizeSamples(samples, measuredFrameCount, value => value.cpuFrameMs);
            phase.cpuMainThreadMs = SummarizeSamples(samples, measuredFrameCount, value => value.cpuMainThreadMs);
            phase.cpuRenderThreadMs = SummarizeSamples(samples, measuredFrameCount, value => value.cpuRenderThreadMs);
            phase.gpuFrameMs = SummarizeSamples(samples, measuredFrameCount, value => value.gpuFrameMs);
            phase.drawCalls = SummarizeRaw(samples, measuredFrameCount, value => value.drawCalls, drawCallsRecorder.Valid);
            phase.initializeBufferMarkerValueNs = SummarizeRaw(samples, measuredFrameCount, value => value.initializeBuffer, initializeBufferRecorder.Valid);
            phase.createBufferMarkerValueNs = SummarizeRaw(samples, measuredFrameCount, value => value.createBuffer, createBufferRecorder.Valid);
            phase.totalMemoryBytes = SummarizeRaw(samples, measuredFrameCount, value => value.totalMemory, totalMemoryRecorder.Valid);
            phase.gfxMemoryBytes = SummarizeRaw(samples, measuredFrameCount, value => value.gfxMemory, gfxMemoryRecorder.Valid);
            if (measuredFrameCount != MeasurementFrames) SetFailure("prototype phase did not collect exactly 120 measurement frames");
            if (phaseMode == PhaseMode.NaturalCameraBatch && phase.uniqueViewCameraCallbacks != 48)
                SetFailure("natural camera phase observed fewer than 48 unique cameras");
        }

        delegate double DoubleSelector(FrameSample sample);
        delegate long LongSelector(FrameSample sample);

        static FrameStats SummarizeSamples(FrameSample[] values, int count, DoubleSelector selector)
        {
            double[] selected = new double[count];
            int available = 0;
            for (int i = 0; i < count; ++i)
            {
                double value = selector(values[i]);
                if (value > 0.0 && !double.IsNaN(value) && !double.IsInfinity(value)) selected[available++] = value;
            }
            Array.Sort(selected, 0, available);
            return new FrameStats
            {
                available = available > 0,
                samples = available,
                p50 = Percentile(selected, available, .50),
                p95 = Percentile(selected, available, .95),
                worst = available > 0 ? selected[available - 1] : 0.0
            };
        }

        static RawStats SummarizeRaw(FrameSample[] values, int count, LongSelector selector, bool available)
        {
            long[] selected = new long[count];
            int length = 0;
            if (available)
                for (int i = 0; i < count; ++i) selected[length++] = selector(values[i]);
            Array.Sort(selected, 0, length);
            return new RawStats
            {
                available = available && length > 0,
                samples = length,
                p50 = Percentile(selected, length, .50),
                p95 = Percentile(selected, length, .95),
                worst = length > 0 ? selected[length - 1] : 0
            };
        }

        static double Percentile(double[] values, int count, double percentile)
        {
            return count <= 0 ? 0.0 : values[(int)Math.Floor((count - 1) * percentile)];
        }

        static long Percentile(long[] values, int count, double percentile)
        {
            return count <= 0 ? 0L : values[(int)Math.Floor((count - 1) * percentile)];
        }

        void CapturePhaseImage(PhaseEvidence phase)
        {
            if (phase == null || quilt.QuiltTexture == null) throw new InvalidOperationException("quilt atlas unavailable for image capture");
            RenderTexture atlas = quilt.QuiltTexture;
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new Texture2D(atlas.width, atlas.height, TextureFormat.RGBA32, false, false)
            {
                name = "ViewWeave.HDRPBatch.ImageReadback",
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                RenderTexture.active = atlas;
                readback.ReadPixels(new Rect(0, 0, atlas.width, atlas.height), 0, 0, false);
                readback.Apply(false, false);
                Color32[] pixels = readback.GetPixels32();
                QuiltLayout layout = quilt.settings.GetLayout();
                bool allNonBlank = true;
                long red = 0;
                long green = 0;
                long magenta = 0;
                long changed = 0;
                double[] tileMeans = new double[layout.ViewCount * 3];
                for (int view = 0; view < layout.ViewCount; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    long sumR = 0, sumG = 0, sumB = 0;
                    long nonBlank = 0;
                    for (int y = tile.y; y < tile.yMax; ++y)
                    {
                        int row = y * atlas.width;
                        for (int x = tile.x; x < tile.xMax; ++x)
                        {
                            Color32 pixel = pixels[row + x];
                            sumR += pixel.r; sumG += pixel.g; sumB += pixel.b;
                            if (pixel.r > 2 || pixel.g > 2 || pixel.b > 2) nonBlank++;
                            if (pixel.r > pixel.g + 12 && pixel.r > pixel.b + 12 && pixel.r > 20) red++;
                            if (pixel.g > pixel.r + 12 && pixel.g > pixel.b + 12 && pixel.g > 20) green++;
                            if (pixel.r > 40 && pixel.b > 40 && pixel.g < 20) magenta++;
                        }
                    }
                    allNonBlank &= nonBlank > 0;
                    int meanIndex = view * 3;
                    long area = (long)tile.width * tile.height;
                    tileMeans[meanIndex] = area == 0 ? 0 : sumR / (double)area;
                    tileMeans[meanIndex + 1] = area == 0 ? 0 : sumG / (double)area;
                    tileMeans[meanIndex + 2] = area == 0 ? 0 : sumB / (double)area;
                }
                RectInt left = layout.GetTile(0);
                RectInt right = layout.GetTile(layout.ViewCount - 1);
                for (int y = 0; y < left.height; ++y)
                    for (int x = 0; x < left.width; ++x)
                    {
                        Color32 a = pixels[(left.y + y) * atlas.width + left.x + x];
                        Color32 b = pixels[(right.y + y) * atlas.width + right.x + x];
                        if (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) > 3) changed++;
                    }

                string name = "hdrp-camera-batch-" + phase.mode.ToLowerInvariant() + "-" + phases.Count.ToString("D2", CultureInfo.InvariantCulture) + ".png";
                string path = Path.Combine(evidenceDirectory, name);
                byte[] encoded = readback.EncodeToPNG();
                File.WriteAllBytes(path, encoded);
                phase.imageCaptured = true;
                phase.imagePath = Sanitize(path);
                phase.nonBlankTiles = allNonBlank ? layout.ViewCount : 0;
                phase.allTilesNonBlank = allNonBlank;
                phase.redDominantPixels = red;
                phase.greenDominantPixels = green;
                phase.hasKnownRedAndGreenEvidence = red > 20 && green > 20;
                phase.magentaPixels = magenta;
                phase.hasNoMagentaEvidence = magenta == 0;
                phase.leftRightChangedPixels = changed;
                phase.leftRightParallaxObserved = changed > 20;
                phase.tileMeanRgb = tileMeans;
                phase.maxTileMeanChannelDeltaFromFirstLegacy = CompareFirstLegacyMeans(tileMeans);
                if (densityPlanSelected) ValidateNearestReuseAssignments(pixels, layout, phase);
                else ValidateFullSourceReference(pixels, atlas.width, atlas.height, phase, encoded);
                if (!phase.allTilesNonBlank) SetFailure("image validation found a blank view tile");
                if (!phase.hasKnownRedAndGreenEvidence) SetFailure("image validation found no red and green fixture evidence");
                if (!phase.hasNoMagentaEvidence) SetFailure("image validation found magenta fixture pixels");
                if (!phase.leftRightParallaxObserved) SetFailure("image validation found no left-to-right parallax evidence");
                if (densityPlanSelected && !phase.renderedEndpointsRetained) SetFailure("density mode did not retain the first and last rendered views");
                if (densityPlanSelected && !phase.nearestReuseValid) SetFailure("density mode output tile did not exactly match its nearest rendered source tile");
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(readback);
            }
        }

        void ValidateFullSourceReference(Color32[] pixels, int width, int height, PhaseEvidence phase, byte[] encoded)
        {
            if (quilt.viewRenderMode != QuiltViewRenderMode.Full)
            {
                phase.fullSourceComparisonValid = false;
                SetFailure("camera-batch A/B/A requires Full view render mode in every phase");
                return;
            }
            if (phases.Count == 0)
            {
                firstFullReferencePng = encoded;
                return;
            }
            if (phases.Count > 2 || firstFullReferencePng == null)
            {
                phase.fullSourceComparisonValid = false;
                SetFailure("camera-batch A/B/A Full reference image is unavailable");
                return;
            }

            Texture2D reference = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = "ViewWeave.HDRPBatch.FullReference",
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                if (!reference.LoadImage(firstFullReferencePng, false) || reference.width != width || reference.height != height)
                    throw new InvalidOperationException("camera-batch Full reference image could not be decoded at the expected dimensions");
                Color32[] referencePixels = reference.GetPixels32();
                if (referencePixels.Length != pixels.Length)
                    throw new InvalidOperationException("camera-batch Full reference pixel count did not match the current image");

                phase.fullSourceComparedPixels = pixels.Length;
                for (int i = 0; i < pixels.Length; ++i)
                {
                    Color32 expected = referencePixels[i];
                    Color32 actual = pixels[i];
                    int deltaR = Math.Abs(expected.r - actual.r);
                    int deltaG = Math.Abs(expected.g - actual.g);
                    int deltaB = Math.Abs(expected.b - actual.b);
                    int deltaA = Math.Abs(expected.a - actual.a);
                    if (deltaR != 0 || deltaG != 0 || deltaB != 0 || deltaA != 0)
                        phase.fullSourceMismatchPixels++;
                    phase.fullSourceMaxChannelDifference = Math.Max(phase.fullSourceMaxChannelDifference, Math.Max(Math.Max(deltaR, deltaG), Math.Max(deltaB, deltaA)));
                }
                phase.fullSourceComparisonValid = phase.fullSourceMismatchPixels == 0;
                if (!phase.fullSourceComparisonValid)
                    SetFailure("camera-batch image differed from the first Full phase at " + phase.fullSourceMismatchPixels.ToString(CultureInfo.InvariantCulture) + " pixels");
            }
            catch (Exception error)
            {
                phase.fullSourceComparisonValid = false;
                SetFailure("camera-batch Full image comparison failed: " + error.GetType().Name + ": " + error.Message);
            }
            finally
            {
                Destroy(reference);
            }
        }

        double CompareFirstLegacyMeans(double[] current)
        {
            PhaseEvidence first = phases.Count > 0 && phases[0].mode == PhaseMode.LegacyRequests.ToString() ? phases[0] : null;
            if (first == null || first.tileMeanRgb == null || first.tileMeanRgb.Length != current.Length) return 0.0;
            double max = 0.0;
            for (int i = 0; i < current.Length; ++i) max = Math.Max(max, Math.Abs(current[i] - first.tileMeanRgb[i]));
            return max;
        }

        void ValidateNearestReuseAssignments(Color32[] pixels, QuiltLayout layout, PhaseEvidence phase)
        {
            phase.nearestReuseValid = true;
            phase.nearestReuseComparedPixels = 0;
            phase.nearestReuseMismatchPixels = 0;
            for (int outputView = 0; outputView < layout.ViewCount; ++outputView)
            {
                int sourceView = QuiltViewSampling.GetSourceViewIndex(layout.ViewCount, quilt.viewRenderMode, outputView);
                if (sourceView == outputView) continue;

                RectInt outputTile = layout.GetTile(outputView);
                RectInt sourceTile = layout.GetTile(sourceView);
                if (outputTile.width != sourceTile.width || outputTile.height != sourceTile.height)
                {
                    phase.nearestReuseValid = false;
                    phase.nearestReuseMismatchPixels++;
                    continue;
                }

                phase.nearestReuseComparedPixels += (long)outputTile.width * outputTile.height;
                for (int y = 0; y < outputTile.height; ++y)
                {
                    int outputRow = (outputTile.y + y) * quilt.QuiltTexture.width + outputTile.x;
                    int sourceRow = (sourceTile.y + y) * quilt.QuiltTexture.width + sourceTile.x;
                    for (int x = 0; x < outputTile.width; ++x)
                    {
                        Color32 outputPixel = pixels[outputRow + x];
                        Color32 sourcePixel = pixels[sourceRow + x];
                        if (outputPixel.r != sourcePixel.r || outputPixel.g != sourcePixel.g || outputPixel.b != sourcePixel.b || outputPixel.a != sourcePixel.a)
                            phase.nearestReuseMismatchPixels++;
                    }
                }
            }
            phase.nearestReuseValid &= phase.nearestReuseMismatchPixels == 0;
        }

        void ValidatePhaseImages()
        {
            if (densityPlanSelected)
            {
                if (phases.Count != 4)
                {
                    SetFailure("density prototype did not complete four phases");
                    return;
                }
                QuiltViewRenderMode[] expectedModes = { QuiltViewRenderMode.Full, QuiltViewRenderMode.Half, QuiltViewRenderMode.Quarter, QuiltViewRenderMode.Full };
                int[] expectedRenderCounts = { 48, 24, 12, 48 };
                for (int i = 0; i < phases.Count; ++i)
                {
                    if (phases[i].mode != PhaseMode.LegacyRequests.ToString() || phases[i].renderMode != expectedModes[i].ToString())
                        SetFailure("density prototype phase order is invalid");
                    if (phases[i].expectedRenderedViewCount != expectedRenderCounts[i] || phases[i].actualRenderedViewCount != expectedRenderCounts[i])
                        SetFailure("density prototype rendered view count did not match the expected mode count");
                    if (phases[i].tileCount != 48)
                        SetFailure("density prototype changed the 48-tile output layout");
                    if (!phases[i].renderedEndpointsRetained || !phases[i].nearestReuseValid)
                        SetFailure("density prototype failed endpoint or nearest-reuse validation");
                    long expectedReusedPixels = (long)(48 - expectedRenderCounts[i]) * phases[i].outputTileWidth * phases[i].outputTileHeight;
                    if (phases[i].nearestReuseComparedPixels != expectedReusedPixels)
                        SetFailure("density prototype did not compare every skipped output tile pixel");
                }
            }
            else
            {
                if (phases.Count != 3)
                {
                    SetFailure("prototype did not complete the Legacy-Natural-Legacy phase order");
                    return;
                }
                if (phases[0].mode != PhaseMode.LegacyRequests.ToString() || phases[1].mode != PhaseMode.NaturalCameraBatch.ToString() || phases[2].mode != PhaseMode.LegacyRequests.ToString())
                    SetFailure("prototype phase order is invalid");
                PhaseEvidence batchPhase = phases[1];
                if (batchPhase.actualRenderedViewCount != 48 || batchPhase.uniqueViewCameraCallbacks != 48 ||
                    batchPhase.naturalEpochsCompleted != WarmupFrames + MeasurementFrames ||
                    batchPhase.naturalOwnedCameraCallbacks != 48 * (WarmupFrames + MeasurementFrames) ||
                    batchPhase.naturalContextSubmits != batchPhase.naturalOwnedContexts ||
                    batchPhase.naturalEpochsWithOneMainCameraCallback != WarmupFrames + MeasurementFrames ||
                    batchPhase.naturalMainCameraCallbacksTotal != WarmupFrames + MeasurementFrames ||
                    batchPhase.naturalMainCameraCallbacksMin != 1 || batchPhase.naturalMainCameraCallbacksMax != 1 ||
                    batchPhase.naturalMainCameraCallbacksPerEpoch == null ||
                    batchPhase.naturalMainCameraCallbacksPerEpoch.Length != WarmupFrames + MeasurementFrames ||
                    batchPhase.renderToTransferFrameOffsetMin != batchPhase.renderToTransferFrameOffsetMax ||
                    batchPhase.submissionAttemptsDelta != WarmupFrames + MeasurementFrames)
                    SetFailure("camera-batch phase failed epoch, callback, context-submit, offset, or attempt invariants");
                if (batchPhase.naturalMainCameraCallbacksPerEpoch != null)
                    for (int i = 0; i < batchPhase.naturalMainCameraCallbacksPerEpoch.Length; ++i)
                        if (batchPhase.naturalMainCameraCallbacksPerEpoch[i] != 1)
                            SetFailure("camera-batch phase did not observe exactly one MainCamera callback per rendered epoch");
                for (int i = 0; i < phases.Count; ++i)
                {
                    if (phases[i].renderMode != QuiltViewRenderMode.Full.ToString() || phases[i].tileCount != 48)
                        SetFailure("camera-batch A/B/A must retain Full mode and all 48 output tiles");
                    int expectedPixels = phases[i].outputTileWidth * phases[i].outputTileHeight * phases[i].tileCount;
                    if (i > 0 && (phases[i].fullSourceComparedPixels != expectedPixels ||
                                  phases[i].fullSourceMismatchPixels != 0 || !phases[i].fullSourceComparisonValid))
                        SetFailure("camera-batch phase did not match the first Full reference image");
                }
            }
            for (int i = 0; i < phases.Count; ++i)
            {
                if (!phases[i].imageCaptured || !phases[i].allTilesNonBlank || !phases[i].hasKnownRedAndGreenEvidence || !phases[i].hasNoMagentaEvidence || !phases[i].leftRightParallaxObserved)
                    SetFailure("a prototype phase failed image validity checks");
            }
        }

        void StopSessionForDrain()
        {
            stage = "stop-and-drain";
            phaseActive = false;
            if (rig != null) rig.AutoRender = false;
            DisableNaturalCameras();
            UnsubscribeCallbacks();
            session.StopSession();
        }

        IEnumerator StopAndDrainCoroutine()
        {
            double started = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - started < LifecycleTimeoutSeconds)
            {
                if (!TryReadPending(out int shutdownPending, out uint nativePending, out string error))
                {
                    SetFailure("pending-count diagnostics unavailable during stop: " + error);
                    yield break;
                }
                if (shutdownPending == 0 && nativePending == 0 && session.State == ViewWeaveBridgeSession.SessionState.Idle && output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                    yield break;
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                {
                    SetFailure("bridge worker faulted while stopping: " + Sanitize(string.IsNullOrEmpty(session.LastError) ? output.LastError : session.LastError));
                    yield break;
                }
                yield return null;
            }
            SetFailure("native worker shutdown did not drain before timeout");
        }

        void CleanupNaturalCamerasAfterDrain()
        {
            DisableNaturalCameras();
            UnsubscribeCallbacks();
            if (viewCameras.Count == 0 && viewCameraObjects.Count == 0 && sourcePreparation == null && copyCommand == null) return;
            if (!TryReadPending(out int shutdownPending, out uint nativePending, out string pendingError) || shutdownPending != 0 || nativePending != 0)
            {
                SetFailure("refusing to release prototype textures before native shutdown reaches zero: " + Sanitize(pendingError));
                return;
            }
            for (int i = 0; i < viewCameras.Count; ++i)
            {
                Camera camera = viewCameras[i];
                if (camera != null)
                {
                    camera.enabled = false;
                    camera.targetTexture = null;
                }
            }
            for (int i = 0; i < viewCameraObjects.Count; ++i)
                if (viewCameraObjects[i] != null) Destroy(viewCameraObjects[i]);
            for (int i = 0; i < viewTargets.Count; ++i)
            {
                RenderTexture target = viewTargets[i];
                if (target == null) continue;
                if (target.IsCreated()) target.Release();
                Destroy(target);
            }
            viewCameras.Clear();
            viewCameraObjects.Clear();
            viewTargets.Clear();
            viewIndices.Clear();
            copiedCameras.Clear();
            copiedViewIndices.Clear();
            currentContextExpectedViews.Clear();
            currentContextCopiedViews.Clear();
            if (sourcePreparation != null)
            {
                sourcePreparation.Dispose();
                sourcePreparation = null;
            }
            if (copyCommand != null)
            {
                copyCommand.Release();
                copyCommand = null;
            }
        }

        void SetFailure(string message)
        {
            if (!string.IsNullOrEmpty(failure)) return;
            if (phaseMode == PhaseMode.NaturalCameraBatch && currentPhase != null)
            {
                TraceNatural("failure", -1, naturalContextExpectedCount, naturalContextSubmitCount, naturalEpochReady, 0);
                currentPhase.naturalTrace = SnapshotNaturalTrace();
                SnapshotNaturalMainCameraCallbacks();
            }
            failure = Sanitize(message);
            stage = "failed";
            phaseActive = false;
            naturalEpochArmed = false;
            naturalEpochReady = false;
            DisableNaturalCameras();
            Debug.LogError("HDRP batch prototype failed: " + failure);
        }

        bool MainCameraUnchanged()
        {
            CameraSnapshot after = CameraSnapshot.Capture(mainCamera);
            return after.enabled == mainCameraBefore.enabled && after.hasTargetTexture == mainCameraBefore.hasTargetTexture && after.targetDisplay == mainCameraBefore.targetDisplay && after.cullingMask == mainCameraBefore.cullingMask && after.clearFlags == mainCameraBefore.clearFlags && Mathf.Abs(after.fieldOfView - mainCameraBefore.fieldOfView) < 0.0001f && after.volumeLayerMask == mainCameraBefore.volumeLayerMask;
        }

        static bool TryReadPending(out int shutdownPending, out uint nativePending, out string error)
        {
            return BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out shutdownPending, out nativePending, out error);
        }

        string ResolveEvidenceDirectory()
        {
            string argument = Argument(EvidenceArgument);
            if (!string.IsNullOrWhiteSpace(argument)) return Path.GetFullPath(argument);
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "dx12-followup", "camera-batch-prototype"));
        }

        string ParsePlan()
        {
            string value = null;
            bool found = false;
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; ++i)
            {
                if (!string.Equals(args[i], PlanArgument, StringComparison.Ordinal)) continue;
                if (found || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidOperationException("invalid --prototype-plan; expected one value: camera-batch or density");
                found = true;
                value = args[++i];
            }
            value = value ?? "camera-batch";
            value = value.ToLowerInvariant();
            if (value != "camera-batch" && value != "density")
                throw new InvalidOperationException("invalid --prototype-plan; expected camera-batch or density");
            return value;
        }

        string ParseQuality()
        {
            string value = Argument("--prototype-quality") ?? "balanced";
            value = value.ToLowerInvariant();
            if (value != "balanced" && value != "native") throw new InvalidOperationException("invalid --prototype-quality; expected balanced or native");
            return value;
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i)
                if (string.Equals(args[i], key, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; ++i)
                if (characters[i] < 0x20 || characters[i] > 0x7e) characters[i] = '?';
            return new string(characters);
        }

        static void WriteEvidence(RunEvidence evidence)
        {
            string path = Path.Combine(active.evidenceDirectory, "hdrp-camera-batch-prototype.json");
            File.WriteAllText(path, JsonUtility.ToJson(evidence, true));
        }

        void OnDestroy()
        {
            if (active == this) active = null;
            phaseActive = false;
            running = false;
            DisableNaturalCameras();
            UnsubscribeCallbacks();
            RemovePlayerLoopNode();
            StopRecorders();
            if (session != null)
            {
                session.startOnEnable = false;
                session.StopSession();
            }
            if (TryReadPending(out int shutdownPending, out uint nativePending, out _) && shutdownPending == 0 && nativePending == 0)
                CleanupNaturalCamerasAfterDrain();
        }
    }
}
