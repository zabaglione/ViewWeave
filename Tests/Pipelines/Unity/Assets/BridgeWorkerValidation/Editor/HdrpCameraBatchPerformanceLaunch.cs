using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using ViewWeave;
using ViewWeave.BridgeWorkerValidation;

namespace ViewWeave.BridgeWorkerValidation.Editor
{
    [InitializeOnLoad]
    public static class HdrpCameraBatchPerformanceLaunch
    {
        const string LaunchFlag = "--hdrp-camera-batch-launch";
        const string ScenePath = "Assets/BridgeWorkerValidationGenerated/HdrpCameraBatchPerformance.unity";
        const string RunnerName = "ViewWeave.BridgeWorkerValidation.Editor.HdrpCameraBatchPerformanceLaunch.Run";
        const string Prefix = "ViewWeave.HdrpCameraBatchLaunch.";
        const double PrototypeTimeoutSeconds = 1200.0;
        const double DrainTimeoutSeconds = 90.0;
        const double PipelineSettleTimeoutSeconds = 30.0;
        const int RequiredStableEditorUpdates = 3;
        const int HighLightmapEncodingQuality = 2;

        static readonly BuildTarget[] RequiredLightmapEncodingTargets =
        {
            BuildTarget.StandaloneWindows,
            BuildTarget.StandaloneWindows64,
            BuildTarget.StandaloneLinux64,
            BuildTarget.StandaloneOSX,
            BuildTarget.Android,
            BuildTarget.iOS,
            BuildTarget.Switch,
            BuildTarget.Switch2,
            BuildTarget.WSAPlayer
        };

        [Serializable]
        sealed class PrototypeResult
        {
            public string result;
            public string graphicsApi;
            public string projectScenePath;
            public string prototypePlan;
            public string error;
        }

        [Serializable]
        sealed class SceneSnapshot
        {
            public SceneEntry[] scenes;
        }

        [Serializable]
        sealed class SceneEntry
        {
            public string path;
            public bool isLoaded;
            public bool isActive;
        }

        [Serializable]
        sealed class LightmapEncodingSnapshot
        {
            public LightmapEncodingEntry[] entries;
        }

        [Serializable]
        sealed class LightmapEncodingEntry
        {
            public string target;
            public int quality;
        }

        sealed class LightmapEncodingApi
        {
            public Type qualityType;
            public MethodInfo getter;
            public MethodInfo setter;
            public MethodInfo notify;
        }

        [Serializable]
        sealed class LaunchEvidence
        {
            public int schemaVersion = 1;
            public string result;
            public string startedUtc;
            public string finishedUtc;
            public string unityVersion;
            public string graphicsApi;
            public string scenePath;
            public string prototypeResult;
            public string prototypePlan;
            public string error;
            public int shutdownPending = -1;
            public uint nativePending;
            public bool settingsRestored;
            public bool sceneSetupRestored;
            public int originalQualityLevel = -1;
            public string originalGraphicsPipeline;
            public string originalQualityPipeline;
            public string originalColorSpace;
            public int originalVSyncCount;
            public int originalTargetFrameRate;
            public bool originalRunInBackground;
            public int originalWindows64LightmapEncodingQuality = -1;
            public int runLightmapEncodingQuality = -1;
            public LightmapEncodingEntry[] originalLightmapEncodingQualities;
            public bool gameViewFocused;
            public string renderPipelineAssetType;
            public string renderPipelineInstanceType;
            public bool renderPipelineInstanceAvailable;
            public bool blitMaterialAvailable;
            public string pipelineReadinessError;
            public string prototypeEvidencePath;
        }

        static HdrpCameraBatchPerformanceLaunch()
        {
            if (!SessionState.GetBool(Key("armed"), false)) return;
            if (SessionState.GetString(Key("phase"), string.Empty) == "play-pending")
            {
                if (string.IsNullOrEmpty(SessionState.GetString(Key("playEntryDeadlineUtc"), string.Empty)))
                    SessionState.SetString(Key("playEntryDeadlineUtc"), DateTime.UtcNow.AddSeconds(60.0).ToString("O", CultureInfo.InvariantCulture));
                if (string.IsNullOrEmpty(SessionState.GetString(Key("lastPlayRequestUtc"), string.Empty)))
                    SessionState.SetString(Key("lastPlayRequestUtc"), DateTime.MinValue.ToString("O", CultureInfo.InvariantCulture));
            }
            if (SessionState.GetString(Key("phase"), string.Empty) == "pipeline-settling" &&
                string.IsNullOrEmpty(SessionState.GetString(Key("pipelineSettleDeadlineUtc"), string.Empty)))
                SessionState.SetString(Key("pipelineSettleDeadlineUtc"), DateTime.UtcNow.AddSeconds(PipelineSettleTimeoutSeconds).ToString("O", CultureInfo.InvariantCulture));
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        public static void Run()
        {
            if (!HasArgument(LaunchFlag, "1"))
                throw new InvalidOperationException("explicit SDK prototype launcher opt-in is required");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            if (!IsSdkProject(projectRoot))
                throw new InvalidOperationException("launcher is restricted to Tests/Pipelines/Unity");
            if (SessionState.GetBool(Key("armed"), false))
                throw new InvalidOperationException("a prototype launcher is already active");

            string sceneAssetPath = SceneManager.GetActiveScene().path;
            if (!string.Equals(sceneAssetPath, ScenePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("open the generated prototype scene before launching");

            string runtimePath = Argument("--runtime-path");
            if (string.IsNullOrWhiteSpace(runtimePath) || !File.Exists(runtimePath))
                throw new InvalidOperationException("the official runtime path is missing or invalid");
            string quality = (Argument("--prototype-quality") ?? string.Empty).Trim().ToLowerInvariant();
            if (quality != "balanced" && quality != "native")
                throw new InvalidOperationException("prototype quality must be balanced or native");
            string prototypePlan = (Argument("--prototype-plan") ?? "camera-batch").Trim().ToLowerInvariant();
            if (prototypePlan != "density" && prototypePlan != "camera-batch")
                throw new InvalidOperationException("prototype plan must be density or camera-batch");

            string evidenceDirectory = Path.GetFullPath(Argument("--prototype-evidence-dir") ?? string.Empty);
            string evidenceRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", "..", "..", ".local", "dx12-followup"));
            if (!IsWithin(evidenceDirectory, evidenceRoot) || File.Exists(evidenceDirectory))
                throw new InvalidOperationException("prototype evidence directory must be inside .local/dx12-followup");
            if (File.Exists(Path.Combine(evidenceDirectory, "hdrp-camera-batch-prototype.json")) ||
                File.Exists(Path.Combine(evidenceDirectory, "hdrp-camera-batch-launch.json")) ||
                Directory.Exists(evidenceDirectory) && Directory.EnumerateFiles(evidenceDirectory, "hdrp-camera-batch-*.png").Any())
                throw new InvalidOperationException("prototype runtime evidence already exists in this directory");

            BridgeWorkerValidationRunner runner = UnityEngine.Object.FindFirstObjectByType<BridgeWorkerValidationRunner>(FindObjectsInactive.Include);
            if (runner == null) throw new InvalidOperationException("the generated validation runner is missing");
            SerializedObject runnerData = new SerializedObject(runner);
            RenderPipelineAsset hdrpOff = ReadReference<RenderPipelineAsset>(runnerData, "grdOffPipeline");
            if (hdrpOff == null || hdrpOff.GetType().FullName != "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset")
                throw new InvalidOperationException("the generated HDRP off pipeline asset is missing");

            HdrpCameraBatchPerformancePrototype prototype = UnityEngine.Object.FindFirstObjectByType<HdrpCameraBatchPerformancePrototype>(FindObjectsInactive.Include);
            if (prototype == null) throw new InvalidOperationException("the generated prototype component is missing");

            Directory.CreateDirectory(evidenceDirectory);
            SessionState.SetString(Key("projectRoot"), projectRoot);
            SessionState.SetString(Key("evidenceDirectory"), evidenceDirectory);
            SessionState.SetString(Key("runtimeResultPath"), Path.Combine(evidenceDirectory, "hdrp-camera-batch-prototype.json"));
            SessionState.SetString(Key("startedUtc"), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            SessionState.SetString(Key("deadlineUtc"), DateTime.UtcNow.AddSeconds(PrototypeTimeoutSeconds).ToString("O", CultureInfo.InvariantCulture));
            SessionState.SetString(Key("drainDeadlineUtc"), string.Empty);
            SessionState.SetString(Key("phase"), "starting");
            SessionState.SetString(Key("failure"), string.Empty);
            SessionState.SetString(Key("prototypeResult"), string.Empty);
            SessionState.SetString(Key("prototypePlan"), prototypePlan);
            SessionState.SetString(Key("oldGraphicsPipeline"), AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline));
            SessionState.SetString(Key("oldQualityPipeline"), AssetDatabase.GetAssetPath(QualitySettings.renderPipeline));
            SessionState.SetString(Key("oldColorSpace"), PlayerSettings.colorSpace.ToString());
            SessionState.SetString(Key("oldSceneSetup"), CaptureSceneSetup());
            SessionState.SetString(Key("oldQualityName"), QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : string.Empty);
            SessionState.SetInt(Key("oldQualityLevel"), QualitySettings.GetQualityLevel());
            SessionState.SetInt(Key("oldVSyncCount"), QualitySettings.vSyncCount);
            SessionState.SetInt(Key("oldTargetFrameRate"), Application.targetFrameRate);
            SessionState.SetBool(Key("oldRunInBackground"), Application.runInBackground);
            SessionState.SetString(Key("oldLightmapEncodingSnapshot"), string.Empty);
            SessionState.SetInt(Key("oldWindows64LightmapEncodingQuality"), -1);
            SessionState.SetInt(Key("runLightmapEncodingQuality"), -1);
            SessionState.SetBool(Key("gameViewFocused"), FocusGameView());
            SessionState.SetInt(Key("exitCode"), 1);
            SessionState.SetBool(Key("armed"), true);
            SessionState.SetBool(Key("restorePending"), true);
            SessionState.SetBool(Key("stopRequested"), false);
            SessionState.SetBool(Key("settingsRestored"), false);
            SessionState.SetBool(Key("sceneSetupRestored"), false);

            try
            {
                LightmapEncodingApi lightmapApi = ResolveLightmapEncodingApi();
                LightmapEncodingSnapshot lightmapSnapshot = CaptureLightmapEncodingSnapshot(lightmapApi);
                string lightmapSnapshotJson = JsonUtility.ToJson(lightmapSnapshot);
                SessionState.SetString(Key("oldLightmapEncodingSnapshot"), lightmapSnapshotJson);
                SessionState.SetInt(Key("oldWindows64LightmapEncodingQuality"), FindLightmapEncodingQuality(lightmapSnapshot, BuildTarget.StandaloneWindows64));
                ConfigureHighLightmapEncoding(lightmapApi, lightmapSnapshot);
                SessionState.SetInt(Key("runLightmapEncodingQuality"), HighLightmapEncodingQuality);

                Directory.CreateDirectory(Path.GetDirectoryName(SessionState.GetString(Key("runtimeResultPath"), string.Empty)));
                GraphicsSettings.defaultRenderPipeline = hdrpOff;
                QualitySettings.renderPipeline = hdrpOff;
                if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;
                Application.runInBackground = true;
                SessionState.SetString(Key("phase"), "pipeline-settling");
                EditorApplication.update -= Tick;
                EditorApplication.playModeStateChanged -= OnPlayModeChanged;
                EditorApplication.update += Tick;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                SessionState.SetString(Key("pipelineSettleDeadlineUtc"), DateTime.UtcNow.AddSeconds(PipelineSettleTimeoutSeconds).ToString("O", CultureInfo.InvariantCulture));
                SessionState.SetInt(Key("pipelineStableUpdates"), 0);
                SessionState.SetString(Key("playEntryDeadlineUtc"), DateTime.UtcNow.AddSeconds(60.0).ToString("O", CultureInfo.InvariantCulture));
                SessionState.SetString(Key("lastPlayRequestUtc"), string.Empty);
                SessionState.SetInt(Key("playRequestCount"), 0);
                Debug.Log("hdrp_camera_batch_launch=pipeline-settling");
            }
            catch (Exception error)
            {
                SessionState.SetString(Key("failure"), Sanitize(error.GetType().Name + ": " + error.Message));
                RestoreAndExit();
            }
        }

        static void BeginPlay()
        {
            if (!SessionState.GetBool(Key("armed"), false)) return;
            try
            {
                if (!string.Equals(SceneManager.GetActiveScene().path, ScenePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("prototype scene changed before Play mode");
                SessionState.SetString(Key("phase"), "play-pending");
                SessionState.SetString(Key("startedUtc"), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                SessionState.SetString(Key("deadlineUtc"), DateTime.UtcNow.AddSeconds(PrototypeTimeoutSeconds).ToString("O", CultureInfo.InvariantCulture));
                SessionState.SetString(Key("lastPlayRequestUtc"), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                SessionState.SetInt(Key("playRequestCount"), SessionState.GetInt(Key("playRequestCount"), 0) + 1);
                Debug.Log("hdrp_camera_batch_launch=play-request");
                EditorApplication.isPlaying = true;
            }
            catch (Exception error)
            {
                SessionState.SetString(Key("failure"), Sanitize(error.GetType().Name + ": " + error.Message));
                RestoreAndExit();
            }
        }

        static void Tick()
        {
            if (!SessionState.GetBool(Key("armed"), false)) return;
            string phase = SessionState.GetString(Key("phase"), string.Empty);
            if (phase == "pipeline-settling")
            {
                if (DateTime.UtcNow > ReadUtc(Key("pipelineSettleDeadlineUtc")))
                {
                    SessionState.SetString(Key("failure"), "Editor did not reach a stable pipeline update state before timeout");
                    RestoreAndExit();
                    return;
                }
                int stableUpdates = !EditorApplication.isCompiling && !EditorApplication.isUpdating
                    ? SessionState.GetInt(Key("pipelineStableUpdates"), 0) + 1
                    : 0;
                SessionState.SetInt(Key("pipelineStableUpdates"), stableUpdates);
                if (stableUpdates >= RequiredStableEditorUpdates)
                {
                    Debug.Log("hdrp_camera_batch_launch=editor-stable updates=" + stableUpdates.ToString(CultureInfo.InvariantCulture));
                    BeginPlay();
                }
            }
            else if (phase == "play-pending")
            {
                if (EditorApplication.isPlaying)
                {
                    SessionState.SetString(Key("phase"), "playing");
                    return;
                }
                if (DateTime.UtcNow > ReadUtc(Key("playEntryDeadlineUtc")))
                {
                    SessionState.SetString(Key("failure"), "Editor did not enter Play mode before timeout");
                    RestoreAndExit();
                    return;
                }
                DateTime lastRequest = ReadUtc(Key("lastPlayRequestUtc"));
                int requestCount = SessionState.GetInt(Key("playRequestCount"), 0);
                if (requestCount < 4 && DateTime.UtcNow.Subtract(lastRequest).TotalSeconds >= 10.0)
                    BeginPlay();
            }
            else if (phase == "playing")
            {
                if (!EditorApplication.isPlaying) return;
                if (TryReadPrototypeResult(out PrototypeResult result, out string readError))
                {
                    SessionState.SetString(Key("prototypeResult"), result.result ?? string.Empty);
                    if (!string.Equals(result.result, "completed", StringComparison.OrdinalIgnoreCase))
                        SessionState.SetString(Key("failure"), "runtime prototype result was not completed: " + Sanitize(result.error));
                    else if (!string.Equals(result.graphicsApi, "Direct3D12", StringComparison.OrdinalIgnoreCase))
                        SessionState.SetString(Key("failure"), "runtime graphics API was not Direct3D12");
                    else if (!string.Equals(result.projectScenePath, ScenePath, StringComparison.OrdinalIgnoreCase))
                        SessionState.SetString(Key("failure"), "runtime scene path did not match the generated prototype scene");
                    else if (!string.Equals(result.prototypePlan, SessionState.GetString(Key("prototypePlan"), string.Empty), StringComparison.OrdinalIgnoreCase))
                        SessionState.SetString(Key("failure"), "runtime prototype plan did not match the requested plan");
                    BeginDrain();
                }
                else if (DateTime.UtcNow > ReadUtc(Key("deadlineUtc")))
                {
                    SessionState.SetString(Key("failure"), "prototype result was missing or invalid before timeout: " + Sanitize(readError));
                    BeginDrain();
                }
            }
            else if (phase == "draining")
            {
                if (!EditorApplication.isPlaying) return;
                TryStopWorker();
                if (IsFullyDrained(out int shutdownPending, out uint nativePending, out string drainError))
                {
                    SessionState.SetInt(Key("lastShutdownPending"), shutdownPending);
                    SessionState.SetInt(Key("lastNativePending"), unchecked((int)nativePending));
                    SessionState.SetString(Key("phase"), "stopping-play");
                    SessionState.SetInt(Key("exitCode"), string.IsNullOrEmpty(SessionState.GetString(Key("failure"), string.Empty)) ? 0 : 1);
                    Debug.Log("hdrp_camera_batch_launch=drained shutdownPending=0 nativePending=0");
                    EditorApplication.isPlaying = false;
                }
                else if (DateTime.UtcNow > ReadUtc(Key("drainDeadlineUtc")))
                {
                    SessionState.SetString(Key("failure"), "worker shutdown did not reach Idle/Closed and both pending counts zero: " + Sanitize(drainError));
                    SessionState.SetInt(Key("exitCode"), 1);
                    SessionState.SetString(Key("phase"), "stopping-play");
                    EditorApplication.isPlaying = false;
                }
            }
        }

        static void BeginDrain()
        {
            if (SessionState.GetString(Key("phase"), string.Empty) == "draining") return;
            SessionState.SetString(Key("phase"), "draining");
            SessionState.SetString(Key("drainDeadlineUtc"), DateTime.UtcNow.AddSeconds(DrainTimeoutSeconds).ToString("O", CultureInfo.InvariantCulture));
            TryStopWorker();
        }

        static void TryStopWorker()
        {
            ViewWeaveRig rig = UnityEngine.Object.FindFirstObjectByType<ViewWeaveRig>(FindObjectsInactive.Include);
            if (rig != null) rig.AutoRender = false;
            ViewWeaveBridgeSession session = UnityEngine.Object.FindFirstObjectByType<ViewWeaveBridgeSession>(FindObjectsInactive.Include);
            if (session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle)
                session.StopSession();
        }

        static bool IsFullyDrained(out int shutdownPending, out uint nativePending, out string error)
        {
            if (!BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out shutdownPending, out nativePending, out error)) return false;
            ViewWeaveBridgeSession session = UnityEngine.Object.FindFirstObjectByType<ViewWeaveBridgeSession>(FindObjectsInactive.Include);
            BridgeWorkerQuiltOutput output = UnityEngine.Object.FindFirstObjectByType<BridgeWorkerQuiltOutput>(FindObjectsInactive.Include);
            bool sessionStopped = session != null && session.State == ViewWeaveBridgeSession.SessionState.Idle;
            bool outputStopped = output != null && output.State == BridgeWorkerQuiltOutput.WorkerState.Closed;
            return shutdownPending == 0 && nativePending == 0 && sessionStopped && outputStopped;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Key("armed"), false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                SessionState.SetString(Key("phase"), "playing");
                RecordPipelineReadiness();
                Debug.Log("hdrp_camera_batch_launch=entered_play_mode");
            }
            else if (state == PlayModeStateChange.EnteredEditMode && SessionState.GetString(Key("phase"), string.Empty) == "stopping-play")
            {
                RestoreAndExit();
            }
        }

        static void RestoreAndExit()
        {
            if (!SessionState.GetBool(Key("armed"), false)) return;
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            bool settingsRestored = false;
            bool sceneRestored = false;
            string failure = SessionState.GetString(Key("failure"), string.Empty);
            try
            {
                int oldQualityLevel = SessionState.GetInt(Key("oldQualityLevel"), QualitySettings.GetQualityLevel());
                if (oldQualityLevel >= 0 && oldQualityLevel < QualitySettings.names.Length && QualitySettings.GetQualityLevel() != oldQualityLevel)
                    QualitySettings.SetQualityLevel(oldQualityLevel, false);
                GraphicsSettings.defaultRenderPipeline = LoadPipeline(Key("oldGraphicsPipeline"));
                QualitySettings.renderPipeline = LoadPipeline(Key("oldQualityPipeline"));
                if (Enum.TryParse(SessionState.GetString(Key("oldColorSpace"), string.Empty), out ColorSpace oldColorSpace) && PlayerSettings.colorSpace != oldColorSpace)
                    PlayerSettings.colorSpace = oldColorSpace;
                QualitySettings.vSyncCount = SessionState.GetInt(Key("oldVSyncCount"), QualitySettings.vSyncCount);
                Application.targetFrameRate = SessionState.GetInt(Key("oldTargetFrameRate"), Application.targetFrameRate);
                Application.runInBackground = SessionState.GetBool(Key("oldRunInBackground"), Application.runInBackground);
                settingsRestored = true;
            }
            catch (Exception error)
            {
                failure = JoinFailure(failure, "settings restore failed: " + Sanitize(error.GetType().Name + ": " + error.Message));
            }

            try
            {
                RestoreLightmapEncoding();
            }
            catch (Exception error)
            {
                settingsRestored = false;
                failure = JoinFailure(failure, "lightmap encoding restore failed: " + Sanitize(error.GetType().Name + ": " + error.Message));
            }

            try
            {
                SceneSetup[] snapshot = RestoreSceneSetupData(SessionState.GetString(Key("oldSceneSetup"), string.Empty));
                if (snapshot.Length > 0 && !SameSceneSetup(EditorSceneManager.GetSceneManagerSetup(), snapshot))
                {
                    bool dirty = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Any(scene => scene.isDirty);
                    if (dirty)
                        throw new InvalidOperationException("refusing to replace a dirty SDK scene while restoring scene setup");
                    EditorSceneManager.RestoreSceneManagerSetup(snapshot);
                }
                sceneRestored = true;
            }
            catch (Exception error)
            {
                failure = JoinFailure(failure, "scene setup restore failed: " + Sanitize(error.GetType().Name + ": " + error.Message));
            }

            if (!settingsRestored || !sceneRestored) SessionState.SetInt(Key("exitCode"), 1);
            SessionState.SetBool(Key("settingsRestored"), settingsRestored);
            SessionState.SetBool(Key("sceneSetupRestored"), sceneRestored);
            SessionState.SetString(Key("failure"), failure);
            WriteLaunchEvidence(settingsRestored, sceneRestored, failure);
            EditorApplication.update -= Tick;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            SessionState.SetBool(Key("restorePending"), false);
            SessionState.SetBool(Key("armed"), false);
            int exitCode = SessionState.GetInt(Key("exitCode"), 1);
            Debug.Log("hdrp_camera_batch_launch=restored exitCode=" + exitCode.ToString(CultureInfo.InvariantCulture));
            EditorApplication.Exit(exitCode);
        }

        static bool TryReadPrototypeResult(out PrototypeResult result, out string error)
        {
            result = null;
            error = string.Empty;
            string path = SessionState.GetString(Key("runtimeResultPath"), string.Empty);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = "result file is not present";
                return false;
            }
            try
            {
                string json = File.ReadAllText(path);
                PrototypeResult parsed = JsonUtility.FromJson<PrototypeResult>(json);
                if (parsed == null || (parsed.result != "completed" && parsed.result != "failed") || string.IsNullOrEmpty(parsed.graphicsApi) || string.IsNullOrEmpty(parsed.projectScenePath))
                {
                    error = "result JSON is incomplete";
                    return false;
                }
                result = parsed;
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.GetType().Name + ": " + exception.Message);
                return false;
            }
        }

        static void WriteLaunchEvidence(bool settingsRestored, bool sceneRestored, string failure)
        {
            string path = Path.Combine(SessionState.GetString(Key("evidenceDirectory"), string.Empty), "hdrp-camera-batch-launch.json");
            try
            {
                bool pendingAvailable = BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out int shutdownPending, out uint nativePending, out string pendingError);
                if (!pendingAvailable)
                {
                    shutdownPending = -1;
                    nativePending = 0;
                    failure = JoinFailure(failure, "final pending-count verification failed: " + Sanitize(pendingError));
                    SessionState.SetInt(Key("exitCode"), 1);
                }
                else if (shutdownPending != 0 || nativePending != 0)
                {
                    failure = JoinFailure(failure, "final pending counts were not zero");
                    SessionState.SetInt(Key("exitCode"), 1);
                }
                SessionState.SetString(Key("failure"), failure ?? string.Empty);
                LaunchEvidence evidence = new LaunchEvidence
                {
                    result = SessionState.GetInt(Key("exitCode"), 1) == 0 && string.IsNullOrEmpty(failure) ? "completed" : "failed",
                    startedUtc = SessionState.GetString(Key("startedUtc"), string.Empty),
                    finishedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                    scenePath = SceneManager.GetActiveScene().path,
                    prototypeResult = SessionState.GetString(Key("prototypeResult"), string.Empty),
                    prototypePlan = SessionState.GetString(Key("prototypePlan"), string.Empty),
                    error = failure ?? string.Empty,
                    shutdownPending = shutdownPending,
                    nativePending = nativePending,
                    settingsRestored = settingsRestored,
                    sceneSetupRestored = sceneRestored,
                    originalQualityLevel = SessionState.GetInt(Key("oldQualityLevel"), -1),
                    originalGraphicsPipeline = SessionState.GetString(Key("oldGraphicsPipeline"), string.Empty),
                    originalQualityPipeline = SessionState.GetString(Key("oldQualityPipeline"), string.Empty),
                    originalColorSpace = SessionState.GetString(Key("oldColorSpace"), string.Empty),
                    originalVSyncCount = SessionState.GetInt(Key("oldVSyncCount"), -1),
                    originalTargetFrameRate = SessionState.GetInt(Key("oldTargetFrameRate"), -1),
                    originalRunInBackground = SessionState.GetBool(Key("oldRunInBackground"), false),
                    originalWindows64LightmapEncodingQuality = SessionState.GetInt(Key("oldWindows64LightmapEncodingQuality"), -1),
                    runLightmapEncodingQuality = SessionState.GetInt(Key("runLightmapEncodingQuality"), -1),
                    originalLightmapEncodingQualities = ReadLightmapEncodingSnapshot().entries,
                    gameViewFocused = SessionState.GetBool(Key("gameViewFocused"), false),
                    renderPipelineAssetType = SessionState.GetString(Key("renderPipelineAssetType"), string.Empty),
                    renderPipelineInstanceType = SessionState.GetString(Key("renderPipelineInstanceType"), string.Empty),
                    renderPipelineInstanceAvailable = SessionState.GetBool(Key("renderPipelineInstanceAvailable"), false),
                    blitMaterialAvailable = SessionState.GetBool(Key("blitMaterialAvailable"), false),
                    pipelineReadinessError = SessionState.GetString(Key("pipelineReadinessError"), string.Empty),
                    prototypeEvidencePath = SessionState.GetString(Key("runtimeResultPath"), string.Empty)
                };
                File.WriteAllText(path, JsonUtility.ToJson(evidence, true));
            }
            catch (Exception error)
            {
                Debug.LogError("hdrp_camera_batch_launch evidence write failed: " + Sanitize(error.GetType().Name + ": " + error.Message));
                SessionState.SetInt(Key("exitCode"), 1);
            }
        }

        static string CaptureSceneSetup()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            SceneSnapshot snapshot = new SceneSnapshot
            {
                scenes = setup.Select(item => new SceneEntry { path = item.path, isLoaded = item.isLoaded, isActive = item.isActive }).ToArray()
            };
            return JsonUtility.ToJson(snapshot);
        }

        static SceneSetup[] RestoreSceneSetupData(string json)
        {
            if (string.IsNullOrEmpty(json)) return Array.Empty<SceneSetup>();
            SceneSnapshot snapshot = JsonUtility.FromJson<SceneSnapshot>(json);
            if (snapshot == null || snapshot.scenes == null) return Array.Empty<SceneSetup>();
            return snapshot.scenes.Select(item => new SceneSetup { path = item.path, isLoaded = item.isLoaded, isActive = item.isActive }).ToArray();
        }

        static void RecordPipelineReadiness()
        {
            string assetType = GraphicsSettings.currentRenderPipeline == null ? string.Empty : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            RenderPipeline pipeline = RenderPipelineManager.currentPipeline;
            bool blitMaterialAvailable = false;
            string error = string.Empty;
            try
            {
                blitMaterialAvailable = Blitter.GetBlitMaterial(TextureDimension.Tex2D) != null;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.GetType().Name + ": " + exception.Message);
            }
            string instanceType = pipeline == null ? string.Empty : pipeline.GetType().FullName;
            SessionState.SetString(Key("renderPipelineAssetType"), assetType);
            SessionState.SetString(Key("renderPipelineInstanceType"), instanceType);
            SessionState.SetBool(Key("renderPipelineInstanceAvailable"), pipeline != null);
            SessionState.SetBool(Key("blitMaterialAvailable"), blitMaterialAvailable);
            SessionState.SetString(Key("pipelineReadinessError"), error);
            Debug.Log("hdrp_camera_batch_launch=play_readiness asset=" + Sanitize(assetType) +
                " instance=" + Sanitize(instanceType) +
                " instanceAvailable=" + (pipeline != null).ToString() +
                " blitMaterialAvailable=" + blitMaterialAvailable.ToString() +
                " error=" + Sanitize(error));
        }

        static bool SameSceneSetup(SceneSetup[] left, SceneSetup[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; ++i)
                if (!string.Equals(left[i].path, right[i].path, StringComparison.OrdinalIgnoreCase) || left[i].isLoaded != right[i].isLoaded || left[i].isActive != right[i].isActive)
                    return false;
            return true;
        }

        static T ReadReference<T>(SerializedObject serializedObject, string propertyName) where T : UnityEngine.Object
        {
            SerializedProperty property = serializedObject.FindProperty(propertyName);
            return property == null ? null : property.objectReferenceValue as T;
        }

        static RenderPipelineAsset LoadPipeline(string key)
        {
            string path = SessionState.GetString(key, string.Empty);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
        }

        static LightmapEncodingApi ResolveLightmapEncodingApi()
        {
            Type qualityType = typeof(PlayerSettings).Assembly.GetType("UnityEditor.LightmapEncodingQuality");
            if (qualityType == null || !qualityType.IsEnum ||
                Enum.GetName(qualityType, Enum.ToObject(qualityType, 0)) != "Low" ||
                Enum.GetName(qualityType, Enum.ToObject(qualityType, 1)) != "Normal" ||
                Enum.GetName(qualityType, Enum.ToObject(qualityType, HighLightmapEncodingQuality)) != "High")
                throw new MissingMemberException("Unity lightmap encoding quality API is unavailable or changed");

            Type buildTargetType = typeof(BuildTarget);
            MethodInfo getter = typeof(PlayerSettings).GetMethod("GetLightmapEncodingQualityForPlatform", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { buildTargetType }, null);
            MethodInfo setter = typeof(PlayerSettings).GetMethod("SetLightmapEncodingQualityForPlatform", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { buildTargetType, qualityType }, null);
            Type lightmappingType = typeof(PlayerSettings).Assembly.GetType("UnityEditor.Lightmapping");
            MethodInfo notify = lightmappingType == null ? null : lightmappingType.GetMethod("OnUpdateLightmapEncoding", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(BuildTargetGroup) }, null);
            if (getter == null || getter.ReturnType != qualityType || setter == null || setter.ReturnType != typeof(void) || notify == null || notify.ReturnType != typeof(void))
                throw new MissingMemberException("Unity lightmap encoding methods are unavailable or changed");

            return new LightmapEncodingApi { qualityType = qualityType, getter = getter, setter = setter, notify = notify };
        }

        static LightmapEncodingSnapshot CaptureLightmapEncodingSnapshot(LightmapEncodingApi api)
        {
            LightmapEncodingEntry[] entries = new LightmapEncodingEntry[RequiredLightmapEncodingTargets.Length];
            for (int i = 0; i < RequiredLightmapEncodingTargets.Length; ++i)
            {
                BuildTarget target = RequiredLightmapEncodingTargets[i];
                object quality = api.getter.Invoke(null, new object[] { target });
                entries[i] = new LightmapEncodingEntry { target = target.ToString(), quality = Convert.ToInt32(quality, CultureInfo.InvariantCulture) };
            }
            return new LightmapEncodingSnapshot { entries = entries };
        }

        static void ConfigureHighLightmapEncoding(LightmapEncodingApi api, LightmapEncodingSnapshot snapshot)
        {
            object highQuality = Enum.ToObject(api.qualityType, HighLightmapEncodingQuality);
            for (int i = 0; i < snapshot.entries.Length; ++i)
            {
                BuildTarget target = ParseLightmapEncodingTarget(snapshot.entries[i]);
                api.setter.Invoke(null, new[] { (object)target, highQuality });
            }
            NotifyActiveLightmapEncoding(api);
            for (int i = 0; i < snapshot.entries.Length; ++i)
            {
                BuildTarget target = ParseLightmapEncodingTarget(snapshot.entries[i]);
                int actual = Convert.ToInt32(api.getter.Invoke(null, new object[] { target }), CultureInfo.InvariantCulture);
                if (actual != HighLightmapEncodingQuality)
                    throw new InvalidOperationException("Unity lightmap encoding quality did not become High for " + target);
            }
        }

        static void RestoreLightmapEncoding()
        {
            LightmapEncodingSnapshot snapshot = ReadLightmapEncodingSnapshot();
            if (snapshot.entries == null || snapshot.entries.Length != RequiredLightmapEncodingTargets.Length)
                throw new InvalidOperationException("Unity lightmap encoding snapshot is missing or incomplete");
            LightmapEncodingApi api = ResolveLightmapEncodingApi();
            System.Collections.Generic.List<string> errors = new System.Collections.Generic.List<string>();

            for (int i = 0; i < snapshot.entries.Length; ++i)
            {
                LightmapEncodingEntry entry = snapshot.entries[i];
                try
                {
                    BuildTarget target = ParseLightmapEncodingTarget(entry);
                    api.setter.Invoke(null, new[] { (object)target, Enum.ToObject(api.qualityType, entry.quality) });
                }
                catch (Exception error) { errors.Add("restore " + SafeLightmapTarget(entry) + ": " + Sanitize(error.GetType().Name)); }
            }

            try { NotifyActiveLightmapEncoding(api); }
            catch (Exception error) { errors.Add("notify active target group: " + Sanitize(error.GetType().Name)); }

            for (int i = 0; i < snapshot.entries.Length; ++i)
            {
                LightmapEncodingEntry entry = snapshot.entries[i];
                try
                {
                    BuildTarget target = ParseLightmapEncodingTarget(entry);
                    int actual = Convert.ToInt32(api.getter.Invoke(null, new object[] { target }), CultureInfo.InvariantCulture);
                    if (actual != entry.quality) errors.Add("verify " + target + " expected " + entry.quality + " actual " + actual);
                }
                catch (Exception error) { errors.Add("verify " + SafeLightmapTarget(entry) + ": " + Sanitize(error.GetType().Name)); }
            }

            if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors.ToArray()));
        }

        static void NotifyActiveLightmapEncoding(LightmapEncodingApi api)
        {
            BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            if (group == BuildTargetGroup.Unknown)
                throw new NotSupportedException("Unity active lightmap encoding target group is unavailable");
            api.notify.Invoke(null, new object[] { group });
        }

        static BuildTarget ParseLightmapEncodingTarget(LightmapEncodingEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.target) ||
                !Enum.TryParse(entry.target, out BuildTarget target) || !Enum.IsDefined(typeof(BuildTarget), target) ||
                !RequiredLightmapEncodingTargets.Contains(target))
                throw new InvalidOperationException("Unity lightmap encoding snapshot has an invalid target");
            return target;
        }

        static string SafeLightmapTarget(LightmapEncodingEntry entry)
        {
            return entry == null || string.IsNullOrEmpty(entry.target) ? "unknown target" : Sanitize(entry.target);
        }

        static int FindLightmapEncodingQuality(LightmapEncodingSnapshot snapshot, BuildTarget target)
        {
            for (int i = 0; i < snapshot.entries.Length; ++i)
                if (string.Equals(snapshot.entries[i].target, target.ToString(), StringComparison.Ordinal)) return snapshot.entries[i].quality;
            throw new InvalidOperationException("Unity lightmap encoding snapshot is missing " + target);
        }

        static LightmapEncodingSnapshot ReadLightmapEncodingSnapshot()
        {
            string json = SessionState.GetString(Key("oldLightmapEncodingSnapshot"), string.Empty);
            if (string.IsNullOrEmpty(json)) return new LightmapEncodingSnapshot { entries = Array.Empty<LightmapEncodingEntry>() };
            LightmapEncodingSnapshot snapshot = JsonUtility.FromJson<LightmapEncodingSnapshot>(json);
            return snapshot ?? new LightmapEncodingSnapshot { entries = Array.Empty<LightmapEncodingEntry>() };
        }

        static bool FocusGameView()
        {
            try
            {
                Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (gameViewType == null) return false;
                EditorWindow window = EditorWindow.GetWindow(gameViewType, false, "Game", false);
                if (window == null) return false;
                window.Show();
                window.Focus();
                return EditorWindow.focusedWindow == window;
            }
            catch (Exception error)
            {
                Debug.LogWarning("hdrp_camera_batch_launch game view focus unavailable: " + Sanitize(error.GetType().Name));
                return false;
            }
        }


        static bool IsSdkProject(string projectRoot)
        {
            string normalized = projectRoot.Replace('\\', '/').TrimEnd('/');
            return normalized.EndsWith("/Tests/Pipelines/Unity", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsWithin(string path, string root)
        {
            string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
        }

        static bool HasArgument(string key, string expected)
        {
            return string.Equals(Argument(key), expected, StringComparison.Ordinal);
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i)
                if (string.Equals(args[i], key, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }

        static DateTime ReadUtc(string key)
        {
            return DateTime.TryParse(SessionState.GetString(key, string.Empty), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime value) ? value : DateTime.MaxValue;
        }

        static string Key(string key) => Prefix + key;

        static string JoinFailure(string existing, string next)
        {
            return string.IsNullOrEmpty(existing) ? next : existing + "; " + next;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; ++i)
                if (characters[i] < 0x20 || characters[i] > 0x7e) characters[i] = '?';
            return new string(characters);
        }
    }
}
