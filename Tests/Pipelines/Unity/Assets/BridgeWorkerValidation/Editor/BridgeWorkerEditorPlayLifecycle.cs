using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using ViewWeave;
using ViewWeave.BridgeWorkerValidation;

namespace ViewWeave.BridgeWorkerValidation.Editor
{
    [InitializeOnLoad]
    public static class BridgeWorkerEditorPlayLifecycle
    {
        const string ScenePath = "Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.unity";
        const string RunKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.running";
        const string OwnerKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.owner";
        const string PhaseKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.phase";
        const string CycleKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.cycle";
        const string StartedKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.started";
        const string EvidenceKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.evidence";
        const string ExpectedApiKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.expectedApi";
        const string PipelineKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.pipeline";
        const string DomainReloadKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.domainReload";
        const string RuntimePathKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.runtimePath";
        const string ExitAfterLifecycleKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.exitAfterLifecycle";
        const string OriginalGraphicsKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.originalGraphics";
        const string OriginalQualityKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.originalQuality";
        const string OriginalScenesKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.originalScenes";
        const string OriginalRunInBackgroundKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.originalRunInBackground";
        const string OriginalEditorOptionsEnabledKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.originalEditorOptionsEnabled";
        const string OriginalEditorOptionsKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.originalEditorOptions";
        const string StateSavedKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.stateSaved";
        const string EditorRecoveryWarningKey = "ViewWeave.BridgeWorkerEditorPlayLifecycle.editorRecoveryWarning";
        const double PlayTimeoutSeconds = 60.0;
        const double RecoveryTimeoutSeconds = 20.0;
        const int RequiredCompletions = 4;
        const int RequiredViews = 48;
        const int CenterView = 23;
        static readonly QuiltQualityPreset[] QualityCycle = { QuiltQualityPreset.Preview, QuiltQualityPreset.Native, QuiltQualityPreset.Preview };

        enum Phase
        {
            WaitingFirstPlay,
            RenderingFirst,
            WaitingFirstEdit,
            RecoveringFirst,
            WaitingSecondPlay,
            RenderingSecond,
            WaitingSecondEdit,
            RecoveringSecond,
            StoppingAfterFailure,
            RecoveringFailure,
            Finished
        }

        static bool running;
        static bool finishing;
        static int cycle;
        static Phase phase;
        static double phaseStarted;
        static string evidencePath;
        static string expectedApi;
        static GraphicsDeviceType expectedGraphicsDeviceType;
        static string pipelineName;
        static string runtimePath;
        static bool exitAfterLifecycle;
        static bool exitSuppressedForUnsafeScene;
        static string failure;
        static Evidence evidence;
        static ViewWeaveBridgeSession session;
        static BridgeWorkerQuiltOutput output;
        static QuiltRenderer quilt;
        static ViewWeaveRig rig;
        static BridgeWorkerValidationRunner runner;
        static bool subscribed;
        static int qualityStageIndex;
        static bool qualityStageStarted;
        static bool qualityStageBaselineSet;
        static double qualityStageStartedAt;
        static uint qualityStageCompletedBaseline;
        static bool cycleCompletedBaselineSet;
        static uint cycleCompletedBaseline;
        static bool cycleCounterReset;
        static int previewWidth;
        static int previewHeight;
        static readonly object warningLock = new object();
        static readonly Queue<WarningTrace> warningQueue = new Queue<WarningTrace>();
        static int traceStage;
        static int traceCycle;
        static int lastShutdownPending = int.MinValue;
        static uint lastNativePending = uint.MaxValue;
        static int warningSequence;
        static bool editorRecoveryWarningWritten;
        static bool nativePendingTenSecondWritten;
        static bool subscribedToTraceEvents;

        static BridgeWorkerEditorPlayLifecycle()
        {
            if (!SessionState.GetBool(RunKey, false)) return;
            int owner;
            if (!int.TryParse(SessionState.GetString(OwnerKey, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out owner) || owner != Process.GetCurrentProcess().Id)
            {
                ClearState();
                return;
            }
            try { RestoreContext(); }
            catch (Exception error)
            {
                evidencePath = SessionState.GetString(EvidenceKey, string.Empty);
                expectedApi = SessionState.GetString(ExpectedApiKey, "d3d11");
                expectedGraphicsDeviceType = expectedApi == "d3d12" ? GraphicsDeviceType.Direct3D12 : GraphicsDeviceType.Direct3D11;
                pipelineName = SessionState.GetString(PipelineKey, "urp");
                runtimePath = SessionState.GetString(RuntimePathKey, string.Empty);
                exitAfterLifecycle = SessionState.GetBool(ExitAfterLifecycleKey, false);
                evidence = new Evidence { expectedApi = expectedApi, expectedGraphicsDeviceType = expectedGraphicsDeviceType.ToString(), pipeline = pipelineName, processId = Process.GetCurrentProcess().Id, scenePath = ScenePath, unityVersion = Application.unityVersion };
                running = true;
                cycle = SessionState.GetInt(CycleKey, 0);
                phase = Phase.StoppingAfterFailure;
                SetEvidenceFailure("context recovery failed: " + error.Message);
            }
            SetTraceStage(phase == Phase.RecoveringFirst || phase == Phase.RecoveringSecond || phase == Phase.RecoveringFailure ? "recovering" : phase == Phase.StoppingAfterFailure ? "stopping" : phase == Phase.RenderingFirst || phase == Phase.RenderingSecond ? "rendering" : "waiting-play");
            editorRecoveryWarningWritten = SessionState.GetBool(EditorRecoveryWarningKey, false);
            Subscribe();
        }

        public static void Run()
        {
            if (running || SessionState.GetBool(RunKey, false)) throw new InvalidOperationException("Bridge editor lifecycle is already running");
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Editor must be in edit mode before starting lifecycle validation");
            try
            {
                exitSuppressedForUnsafeScene = false;
                exitAfterLifecycle = Argument("-exitAfterLifecycle") == "1";
                SessionState.SetBool(StateSavedKey, false);
                string requestedEvidencePath = Argument("-evidencePath") ?? Argument("--evidence-path");
                if (!string.IsNullOrWhiteSpace(requestedEvidencePath) && Path.IsPathFullyQualified(requestedEvidencePath)) evidencePath = Path.GetFullPath(requestedEvidencePath);
                Configuration configuration = ReadConfiguration();
                PrepareContext(configuration);
                ValidateOpenScenes();
                SaveEditorState();
                ApplyEditorSettings(configuration);
                OpenValidationScene();
                ConfigureValidationScene(configuration);
                phase = Phase.WaitingFirstPlay;
                phaseStarted = EditorApplication.timeSinceStartup;
                SetTraceStage("waiting-play");
                PersistContext();
                Subscribe();
                Log("run-start");
                EditorApplication.isPlaying = true;
            }
            catch (Exception error)
            {
                if (!running) PrepareConfigurationFailureContext();
                FailBeforePlay(error);
            }
        }

        static Configuration ReadConfiguration()
        {
            string api = (Argument("-expectedApi") ?? Argument("--graphics-api") ?? string.Empty).ToLowerInvariant();
            string pipeline = (Argument("-pipeline") ?? string.Empty).ToLowerInvariant();
            string domainReload = (Argument("-domainReload") ?? string.Empty).ToLowerInvariant();
            string path = Argument("-bridgeRuntimePath");
            string outputPath = Argument("-evidencePath") ?? Argument("--evidence-path");
            string exitAfter = Argument("-exitAfterLifecycle");
            exitAfterLifecycle = exitAfter == "1";
            if (api != "d3d11" && api != "d3d12") throw new ArgumentException("-expectedApi must be d3d11 or d3d12");
            if (pipeline != "urp" && pipeline != "hdrp") throw new ArgumentException("-pipeline must be urp or hdrp");
            if (domainReload != "on" && domainReload != "off") throw new ArgumentException("-domainReload must be on or off");
            if (exitAfter != null && exitAfter != "0" && exitAfter != "1") throw new ArgumentException("-exitAfterLifecycle must be 0 or 1");
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("-bridgeRuntimePath must be an absolute path");
            if (string.IsNullOrWhiteSpace(outputPath) || !Path.IsPathFullyQualified(outputPath)) throw new ArgumentException("-evidencePath must be an absolute path");
            return new Configuration { api = api, pipeline = pipeline, domainReload = domainReload, exitAfterLifecycle = exitAfter == "1", runtimePath = Path.GetFullPath(path), evidencePath = Path.GetFullPath(outputPath) };
        }

        static void PrepareConfigurationFailureContext()
        {
            running = true;
            finishing = false;
            cycle = 0;
            phase = Phase.WaitingFirstPlay;
            phaseStarted = EditorApplication.timeSinceStartup;
            expectedApi = (Argument("-expectedApi") ?? Argument("--graphics-api") ?? string.Empty).ToLowerInvariant();
            expectedGraphicsDeviceType = expectedApi == "d3d12" ? GraphicsDeviceType.Direct3D12 : GraphicsDeviceType.Direct3D11;
            pipelineName = (Argument("-pipeline") ?? string.Empty).ToLowerInvariant();
            runtimePath = Argument("-bridgeRuntimePath") ?? string.Empty;
            string rawEvidencePath = Argument("-evidencePath") ?? Argument("--evidence-path");
            evidencePath = !string.IsNullOrWhiteSpace(rawEvidencePath) && Path.IsPathFullyQualified(rawEvidencePath) ? Path.GetFullPath(rawEvidencePath) : string.Empty;
            exitAfterLifecycle = Argument("-exitAfterLifecycle") == "1";
            failure = null;
            evidence = new Evidence { expectedApi = expectedApi, expectedGraphicsDeviceType = expectedGraphicsDeviceType.ToString(), pipeline = pipelineName, domainReload = Argument("-domainReload") ?? string.Empty, exitAfterLifecycle = exitAfterLifecycle, processId = Process.GetCurrentProcess().Id, scenePath = ScenePath, unityVersion = Application.unityVersion };
            SessionState.SetBool(StateSavedKey, false);
        }

        static void PrepareContext(Configuration configuration)
        {
            running = true;
            finishing = false;
            cycle = 0;
            phase = Phase.WaitingFirstPlay;
            phaseStarted = 0.0;
            expectedApi = configuration.api;
            expectedGraphicsDeviceType = expectedApi == "d3d12" ? GraphicsDeviceType.Direct3D12 : GraphicsDeviceType.Direct3D11;
            pipelineName = configuration.pipeline;
            runtimePath = configuration.runtimePath;
            exitAfterLifecycle = configuration.exitAfterLifecycle;
            evidencePath = configuration.evidencePath;
            failure = null;
            evidence = new Evidence { expectedApi = expectedApi, expectedGraphicsDeviceType = expectedGraphicsDeviceType.ToString(), pipeline = pipelineName, domainReload = configuration.domainReload, exitAfterLifecycle = exitAfterLifecycle, processId = Process.GetCurrentProcess().Id, scenePath = ScenePath, unityVersion = Application.unityVersion };
            PersistContext();
            WriteEvidence();
        }

        static void SaveEditorState()
        {
            RenderPipelineAsset graphics = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset quality = QualitySettings.renderPipeline;
            SessionState.SetString(OriginalGraphicsKey, graphics == null ? string.Empty : AssetDatabase.GetAssetPath(graphics));
            SessionState.SetString(OriginalQualityKey, quality == null ? string.Empty : AssetDatabase.GetAssetPath(quality));
            SessionState.SetString(OriginalScenesKey, JsonUtility.ToJson(SceneSetupState.From(EditorSceneManager.GetSceneManagerSetup())));
            SessionState.SetBool(OriginalRunInBackgroundKey, Application.runInBackground);
            SessionState.SetBool(OriginalEditorOptionsEnabledKey, EditorSettings.enterPlayModeOptionsEnabled);
            SessionState.SetInt(OriginalEditorOptionsKey, (int)EditorSettings.enterPlayModeOptions);
            SessionState.SetBool(StateSavedKey, true);
        }

        static void ApplyEditorSettings(Configuration configuration)
        {
            EditorSettings.enterPlayModeOptionsEnabled = configuration.domainReload == "off";
            if (configuration.domainReload == "off") EditorSettings.enterPlayModeOptions = (EditorSettings.enterPlayModeOptions & ~EnterPlayModeOptions.DisableSceneReload) | EnterPlayModeOptions.DisableDomainReload;
            Application.runInBackground = true;
            PersistContext();
        }

        static void OpenValidationScene()
        {
            if (!File.Exists(Path.Combine(Directory.GetParent(Application.dataPath).FullName, ScenePath.Replace('/', Path.DirectorySeparatorChar)))) throw new FileNotFoundException("generated validation scene is missing", ScenePath);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        static void ValidateOpenScenes()
        {
            for (int index = 0; index < SceneManager.sceneCount; ++index)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded) continue;
                if (string.IsNullOrEmpty(scene.path))
                {
                    if (exitAfterLifecycle) { exitSuppressedForUnsafeScene = true; if (evidence != null) evidence.exitSuppressed = true; }
                    throw new InvalidOperationException("cannot run lifecycle validation with an untitled scene open");
                }
                if (scene.isDirty)
                {
                    if (exitAfterLifecycle) { exitSuppressedForUnsafeScene = true; if (evidence != null) evidence.exitSuppressed = true; }
                    throw new InvalidOperationException("cannot run lifecycle validation with unsaved scene changes: " + scene.path);
                }
            }
        }

        static void ConfigureValidationScene(Configuration configuration)
        {
            runner = UnityEngine.Object.FindAnyObjectByType<BridgeWorkerValidationRunner>();
            if (runner == null) throw new InvalidOperationException("generated validation runner is missing");
            Transform owner = runner.transform;
            session = owner.GetComponent<ViewWeaveBridgeSession>();
            output = owner.GetComponent<BridgeWorkerQuiltOutput>();
            quilt = owner.GetComponent<QuiltRenderer>();
            ViewWeaveRig rig = owner.GetComponent<ViewWeaveRig>();
            BridgeWorkerEditorPlayLifecycle.rig = rig;
            Camera source = owner.GetComponent<Camera>();
            if (session == null || output == null || quilt == null || rig == null || source == null) throw new InvalidOperationException("generated Bridge rig components are incomplete");
            runner.enabled = false;
            session.runtimePath = runtimePath;
            session.startOnEnable = true;
            source.enabled = false;
            string pipelinePath = configuration.pipeline == "urp" ? "Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.Off.asset" : "Assets/BridgeWorkerValidationGenerated/BridgeWorkerValidation.HDRP.Off.asset";
            RenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(pipelinePath);
            if (pipeline == null) throw new InvalidOperationException("generated pipeline asset is missing: " + pipelinePath);
            GraphicsSettings.defaultRenderPipeline = pipeline;
            QualitySettings.renderPipeline = pipeline;
            evidence.quality = session.Quality.ToString();
            evidence.grid = rig.Profile.Columns.ToString(CultureInfo.InvariantCulture) + "x" + rig.Profile.Rows.ToString(CultureInfo.InvariantCulture);
            evidence.runtimePathExists = File.Exists(runtimePath);
        }

        static void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += PlayModeStateChanged;
            EditorApplication.quitting += EditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            subscribedToTraceEvents = true;
            Application.logMessageReceivedThreaded += LogMessageReceivedThreaded;
        }

        static void Unsubscribe()
        {
            if (!subscribed) return;
            subscribed = false;
            EditorApplication.update -= Update;
            EditorApplication.playModeStateChanged -= PlayModeStateChanged;
            EditorApplication.quitting -= EditorQuitting;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            Application.logMessageReceivedThreaded -= LogMessageReceivedThreaded;
            subscribedToTraceEvents = false;
        }

        static void LogMessageReceivedThreaded(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Warning || !subscribedToTraceEvents) return;
            WarningTrace item = new WarningTrace
            {
                utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                cycle = Volatile.Read(ref traceCycle),
                stage = Volatile.Read(ref traceStage),
                raw = condition ?? string.Empty,
                stack = stackTrace ?? string.Empty
            };
            lock (warningLock)
            {
                item.count = ++warningSequence;
                warningQueue.Enqueue(item);
            }
        }

        static void BeforeAssemblyReload()
        {
            TraceLifecycleEvent("before-assembly-reload");
        }

        static void EditorQuitting()
        {
            TraceLifecycleEvent("quitting");
        }

        static void TraceLifecycleEvent(string eventName)
        {
            if (!running) return;
            RecordPendingSnapshot(eventName);
            DrainWarnings();
        }

        static void SetTraceStage(string stage)
        {
            Volatile.Write(ref traceStage, StageId(stage));
            Volatile.Write(ref traceCycle, cycle);
        }

        static int StageId(string stage)
        {
            if (stage == "waiting-play") return 1;
            if (stage == "rendering") return 2;
            if (stage == "stopping") return 3;
            if (stage == "waiting-edit") return 4;
            if (stage == "recovering") return 5;
            if (stage == "finished") return 6;
            return 0;
        }

        static string StageName(int stage)
        {
            switch (stage)
            {
                case 1: return "waiting-play";
                case 2: return "rendering";
                case 3: return "stopping";
                case 4: return "waiting-edit";
                case 5: return "recovering";
                case 6: return "finished";
                default: return "starting";
            }
        }

        static void DrainWarnings()
        {
            if (string.IsNullOrEmpty(evidencePath)) return;
            while (true)
            {
                WarningTrace item;
                lock (warningLock)
                {
                    if (warningQueue.Count == 0) break;
                    item = warningQueue.Dequeue();
                }
                string line = item.utc + " event=warning domainLocalSeq=" + item.count.ToString(CultureInfo.InvariantCulture) + " cycle=" + item.cycle.ToString(CultureInfo.InvariantCulture) + " stage=" + StageName(item.stage) + " raw=" + TraceEscape(item.raw) + " stack=" + TraceEscape(item.stack);
                AppendTrace(line);
                if (evidence != null) evidence.warningCount++;
            }
            if (evidence != null) evidence.traceFile = "lifecycle-trace.log";
        }

        static string TraceEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            StringBuilder result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (character >= 0x20 && character <= 0x7e && character != '\\') result.Append(character);
                else if (character == '\\') result.Append("\\\\");
                else result.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        static void AppendTrace(string line)
        {
            if (string.IsNullOrEmpty(evidencePath)) return;
            try
            {
                Directory.CreateDirectory(evidencePath);
                File.AppendAllText(Path.Combine(evidencePath, "lifecycle-trace.log"), line + Environment.NewLine, Encoding.ASCII);
            }
            catch (Exception error)
            {
                SetEvidenceFailure("lifecycle trace write failed: " + error.Message);
            }
        }

        static bool RecordPendingSnapshot(string eventName)
        {
            int shutdownPending;
            uint nativePending;
            string error;
            if (!BridgeWorkerValidationDiagnostics.TryReadPendingCounts(out shutdownPending, out nativePending, out error))
            {
                SetEvidenceFailure("pending count unavailable at " + eventName + ": " + error);
                AppendTrace(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " event=pending-unavailable source=" + eventName + " error=" + TraceEscape(error));
                return false;
            }
            string utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            if (eventName != "recovery-poll") AppendTrace(utc + " event=" + eventName + " cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " stage=" + StageName(traceStage));
            if (shutdownPending != lastShutdownPending || nativePending != lastNativePending)
            {
                AppendTrace(utc + " event=pending-count source=" + eventName + " cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " shutdownPending=" + shutdownPending.ToString(CultureInfo.InvariantCulture) + " nativePending=" + nativePending.ToString(CultureInfo.InvariantCulture));
                lastShutdownPending = shutdownPending;
                lastNativePending = nativePending;
            }
            CycleEvidence cycleEvidence = cycle > 0 && evidence != null && evidence.cycles != null && evidence.cycles.Length >= cycle ? evidence.cycles[cycle - 1] : null;
            if (cycleEvidence != null && eventName == "stop-start")
            {
                cycleEvidence.pendingBeforeStop = shutdownPending;
                cycleEvidence.shutdownPendingBeforeStop = shutdownPending;
                cycleEvidence.nativePendingBeforeStop = nativePending;
            }
            bool afterStop = phase == Phase.RecoveringFirst || phase == Phase.RecoveringSecond || phase == Phase.RecoveringFailure;
            if (cycleEvidence != null && afterStop && shutdownPending == 0 && nativePending == 0 && string.IsNullOrEmpty(cycleEvidence.zeroCountsUtc))
            {
                cycleEvidence.zeroCountsUtc = utc;
                cycleEvidence.zeroCountsAfterStop = true;
                AppendTrace(utc + " event=pending-zero cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " shutdownPending=0 nativePending=0");
            }
            if (cycleEvidence != null && eventName == "stop-start" && shutdownPending == 0 && nativePending == 0) cycleEvidence.zeroCountsBeforeStop = true;
            return true;
        }

        static void PlayModeStateChanged(PlayModeStateChange state)
        {
            if (!running) return;
            try
            {
                if (state == PlayModeStateChange.EnteredPlayMode)
                {
                    if (phase != Phase.WaitingFirstPlay && phase != Phase.WaitingSecondPlay) return;
                    cycle = phase == Phase.WaitingFirstPlay ? 1 : 2;
                    phase = cycle == 1 ? Phase.RenderingFirst : Phase.RenderingSecond;
                    SetTraceStage("rendering");
                    phaseStarted = EditorApplication.timeSinceStartup;
                    TraceLifecycleEvent("entered-playmode");
                    qualityStageIndex = 0;
                    qualityStageStarted = false;
                    qualityStageBaselineSet = false;
                    cycleCompletedBaselineSet = false;
                    cycleCounterReset = false;
                    RefreshReferences();
                    if (Application.platform != RuntimePlatform.WindowsEditor || SystemInfo.graphicsDeviceType != expectedGraphicsDeviceType) { Fail("play environment mismatch"); return; }
                    evidence.actualGraphicsDeviceTypes.Add(SystemInfo.graphicsDeviceType.ToString());
                    evidence.cycles[cycle - 1].isEditor = Application.isEditor;
                    evidence.cycles[cycle - 1].isPlaying = Application.isPlaying;
                    evidence.cycles[cycle - 1].platform = Application.platform.ToString();
                    Log("entered-play cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " quality=" + evidence.quality + " grid=" + evidence.grid);
                    PersistContext();
                }
                else if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    Log("exiting-play cycle=" + cycle.ToString(CultureInfo.InvariantCulture));
                    TraceLifecycleEvent("exiting-playmode");
                }
                else if (state == PlayModeStateChange.EnteredEditMode && (phase == Phase.WaitingFirstEdit || phase == Phase.WaitingSecondEdit || phase == Phase.StoppingAfterFailure))
                {
                    phase = phase == Phase.StoppingAfterFailure ? Phase.RecoveringFailure : cycle == 1 ? Phase.RecoveringFirst : Phase.RecoveringSecond;
                    phaseStarted = EditorApplication.timeSinceStartup;
                    SetTraceStage("recovering");
                    editorRecoveryWarningWritten = false;
                    nativePendingTenSecondWritten = false;
                    SessionState.SetBool(EditorRecoveryWarningKey, false);
                    RefreshReferences();
                    Log("entered-edit cycle=" + cycle.ToString(CultureInfo.InvariantCulture));
                    TraceLifecycleEvent("entered-editmode");
                    PersistContext();
                }
            }
            catch (Exception callbackError)
            {
                FailDuringRun(callbackError);
            }
        }

        static void Update()
        {
            if (!running || finishing) return;
            try
            {
                DrainWarnings();
                if (phase == Phase.StoppingAfterFailure)
                {
                    if (Application.isPlaying) { SetTraceStage("stopping"); RecordPendingSnapshot("stop-start"); EditorApplication.isPlaying = false; }
                    else { phase = Phase.RecoveringFailure; phaseStarted = EditorApplication.timeSinceStartup; SetTraceStage("recovering"); }
                }
                if (phase == Phase.RenderingFirst || phase == Phase.RenderingSecond) UpdateRendering();
                else if (phase == Phase.WaitingFirstEdit || phase == Phase.WaitingSecondEdit) CheckTimeout(PlayTimeoutSeconds, "play exit timeout");
                else if (phase == Phase.RecoveringFirst || phase == Phase.RecoveringSecond || phase == Phase.RecoveringFailure) UpdateRecovery();
                else if (phase == Phase.WaitingFirstPlay || phase == Phase.WaitingSecondPlay) CheckTimeout(PlayTimeoutSeconds, "play entry timeout");
            }
            catch (Exception error)
            {
                FailDuringRun(error);
            }
        }

        static void UpdateRendering()
        {
            RefreshReferences();
            if (!Application.isEditor || !Application.isPlaying) return;
            if (session == null || output == null) { Fail("Bridge session references disappeared during Play"); return; }
            if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault) { Fail("Bridge session fault: " + FirstError(session.LastError, output.LastError)); return; }
            if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError)) { Fail("Bridge worker reported an error during rendering"); return; }
            if (cycle == 1) UpdateQualityCycle();
            else UpdateStandardCycle();
        }

        static void UpdateQualityCycle()
        {
            if (qualityStageIndex >= QualityCycle.Length) { Fail("Bridge quality cycle stage index is invalid"); return; }
            if (evidence.qualityStages == null || evidence.qualityStages.Length != QualityCycle.Length) evidence.qualityStages = new[] { new QualityStageEvidence(), new QualityStageEvidence(), new QualityStageEvidence() };
            QuiltQualityPreset requestedQuality = QualityCycle[qualityStageIndex];
            QualityStageEvidence stage = evidence.qualityStages[qualityStageIndex];
            if (!qualityStageStarted)
            {
                qualityStageStarted = true;
                qualityStageBaselineSet = false;
                qualityStageStartedAt = EditorApplication.timeSinceStartup;
                stage.quality = requestedQuality.ToString();
                if (qualityStageIndex == 0)
                {
                    if (session.Quality != requestedQuality) { Fail("initial Bridge quality is not Preview"); return; }
                }
                else if (!session.SetQuality(requestedQuality))
                {
                    Fail(FirstError(session.LastError, output.LastError));
                    return;
                }
                stage.requested = true;
            }

            if (output.Completed < qualityStageCompletedBaseline)
            {
                qualityStageCompletedBaseline = 0;
                stage.counterResetObserved = true;
            }
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready)
            {
                CheckQualityStageTimeout(requestedQuality, "readiness timeout");
                return;
            }
            QuiltQualityProfile expected = ViewWeaveBridgeSession.CreateQualityProfile(session.Profile, requestedQuality);
            if (!RenderedSettingsMatch(expected))
            {
                CheckQualityStageTimeout(requestedQuality, "profile application timeout");
                return;
            }

            if (!qualityStageBaselineSet)
            {
                output.Poll();
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault) { Fail("Bridge session fault while establishing quality baseline: " + FirstError(session.LastError, output.LastError)); return; }
                if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError)) { Fail("Bridge worker reported an error while establishing quality baseline"); return; }
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !RenderedSettingsMatch(expected))
                {
                    CheckQualityStageTimeout(requestedQuality, "baseline readiness timeout");
                    return;
                }
                qualityStageCompletedBaseline = output.Completed;
                stage.startCompleted = qualityStageCompletedBaseline;
                qualityStageBaselineSet = true;
            }

            if (output.Completed - qualityStageCompletedBaseline < RequiredCompletions)
            {
                CheckQualityStageTimeout(requestedQuality, "completion timeout");
                return;
            }
            if (!ValidateRenderedProfile(expected, stage)) return;
            stage.completed = output.Completed;
            stage.completedDelta = output.Completed - qualityStageCompletedBaseline;
            stage.sessionState = session.State.ToString();
            stage.outputState = output.State.ToString();
            string captureName = qualityStageIndex == 0 ? "preview-initial" : qualityStageIndex == 1 ? "native" : "preview-reset";
            stage.redDominantPixels = CaptureCenterView(1, captureName, expected, stage);
            if (stage.redDominantPixels <= 20) { Fail("center view did not contain red Cube pixels: " + requestedQuality.ToString()); return; }
            stage.ok = true;
            if (qualityStageIndex == 0)
            {
                previewWidth = stage.actualWidth;
                previewHeight = stage.actualHeight;
            }
            else if (qualityStageIndex == 1 && stage.actualWidth == previewWidth && stage.actualHeight == previewHeight)
            {
                Fail("Native quality dimensions did not differ from Preview");
                return;
            }
            else if (qualityStageIndex == 2 && (stage.actualWidth != previewWidth || stage.actualHeight != previewHeight))
            {
                Fail("Preview quality reset did not restore the initial dimensions");
                return;
            }

            Log("quality-stage cycle=1 quality=" + requestedQuality.ToString() + " completed=" + stage.completed.ToString(CultureInfo.InvariantCulture) + " delta=" + stage.completedDelta.ToString(CultureInfo.InvariantCulture) + " size=" + stage.actualWidth.ToString(CultureInfo.InvariantCulture) + "x" + stage.actualHeight.ToString(CultureInfo.InvariantCulture) + " red=" + stage.redDominantPixels.ToString(CultureInfo.InvariantCulture));
            qualityStageIndex++;
            qualityStageStarted = false;
            qualityStageBaselineSet = false;
            if (qualityStageIndex == QualityCycle.Length) CompleteRenderCycle();
        }

        static void UpdateStandardCycle()
        {
            QuiltQualityPreset quality = session.Quality;
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready)
            {
                CheckTimeout(PlayTimeoutSeconds, "Bridge rendering readiness timeout");
                return;
            }
            QuiltQualityProfile expected = ViewWeaveBridgeSession.CreateQualityProfile(session.Profile, quality);
            if (!RenderedSettingsMatch(expected))
            {
                CheckTimeout(PlayTimeoutSeconds, "Bridge rendering profile application timeout");
                return;
            }
            if (!cycleCompletedBaselineSet)
            {
                output.Poll();
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault) { Fail("Bridge session fault while establishing cycle baseline: " + FirstError(session.LastError, output.LastError)); return; }
                if (!string.IsNullOrEmpty(session.LastError) || !string.IsNullOrEmpty(output.LastError)) { Fail("Bridge worker reported an error while establishing cycle baseline"); return; }
                if (session.State != ViewWeaveBridgeSession.SessionState.Rendering || output.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !RenderedSettingsMatch(expected))
                {
                    CheckTimeout(PlayTimeoutSeconds, "Bridge rendering baseline readiness timeout");
                    return;
                }
                cycleCompletedBaseline = output.Completed;
                cycleCompletedBaselineSet = true;
            }
            if (output.Completed < cycleCompletedBaseline)
            {
                cycleCounterReset = true;
                cycleCompletedBaseline = 0;
            }
            if (output.Completed - cycleCompletedBaseline < RequiredCompletions)
            {
                CheckTimeout(PlayTimeoutSeconds, "Bridge rendering timeout");
                return;
            }
            CycleEvidence cycleEvidence = evidence.cycles[cycle - 1];
            if (!ValidateRenderedProfile(expected, cycleEvidence)) return;
            cycleEvidence.completedDelta = output.Completed - cycleCompletedBaseline;
            cycleEvidence.redDominantPixels = CaptureCenterView(cycle, quality.ToString().ToLowerInvariant(), expected, cycleEvidence);
            if (cycleEvidence.redDominantPixels <= 20) { Fail("center view did not contain red Cube pixels in cycle " + cycle.ToString(CultureInfo.InvariantCulture)); return; }
            cycleEvidence.counterResetObserved = cycleCounterReset;
            cycleEvidence.quality = quality.ToString();
            CompleteRenderCycle();
        }

        static bool ValidateRenderedProfile(QuiltQualityProfile expected, QualityStageEvidence stage)
        {
            int width, height, tileWidth, tileHeight, views;
            if (!GetRenderedProfile(expected, out width, out height, out tileWidth, out tileHeight, out views))
            {
                Fail("rendered quilt does not match the expected 48-view quality profile");
                return false;
            }
            stage.expectedWidth = expected.QuiltWidth;
            stage.expectedHeight = expected.QuiltHeight;
            stage.actualWidth = width;
            stage.actualHeight = height;
            stage.tileWidth = tileWidth;
            stage.tileHeight = tileHeight;
            stage.views = views;
            return true;
        }

        static bool ValidateRenderedProfile(QuiltQualityProfile expected, CycleEvidence cycleEvidence)
        {
            int width, height, tileWidth, tileHeight, views;
            if (!GetRenderedProfile(expected, out width, out height, out tileWidth, out tileHeight, out views))
            {
                Fail("rendered quilt does not match the expected 48-view quality profile");
                return false;
            }
            cycleEvidence.expectedWidth = expected.QuiltWidth;
            cycleEvidence.expectedHeight = expected.QuiltHeight;
            cycleEvidence.actualWidth = width;
            cycleEvidence.actualHeight = height;
            cycleEvidence.tileWidth = tileWidth;
            cycleEvidence.tileHeight = tileHeight;
            cycleEvidence.views = views;
            return true;
        }

        static bool GetRenderedProfile(QuiltQualityProfile expected, out int width, out int height, out int tileWidth, out int tileHeight, out int views)
        {
            width = height = tileWidth = tileHeight = views = 0;
            if (rig == null || quilt == null || quilt.settings == null) return false;
            RenderTexture texture = quilt.QuiltTexture;
            if (texture == null || !texture.IsCreated() || texture.width <= 0 || texture.height <= 0) return false;
            QuiltQualityProfile actualRig = rig.Profile;
            QuiltLayout layout = quilt.settings.GetLayout();
            width = texture.width;
            height = texture.height;
            tileWidth = layout.TileWidth;
            tileHeight = layout.TileHeight;
            views = layout.ViewCount;
            return expected.Views == RequiredViews && actualRig.Views == RequiredViews && actualRig.Columns * actualRig.Rows == RequiredViews &&
                quilt.settings.views == RequiredViews && quilt.settings.columns == actualRig.Columns && quilt.settings.rows == actualRig.Rows &&
                layout.ViewCount == RequiredViews && texture.width == expected.QuiltWidth && texture.height == expected.QuiltHeight &&
                quilt.settings.quiltWidth == expected.QuiltWidth && quilt.settings.quiltHeight == expected.QuiltHeight &&
                tileWidth > 0 && tileHeight > 0;
        }

        static bool RenderedSettingsMatch(QuiltQualityProfile expected)
        {
            if (rig == null || quilt == null || quilt.settings == null) return false;
            QuiltQualityProfile actual = rig.Profile;
            return expected.Views == RequiredViews && actual.Views == RequiredViews && actual.QuiltWidth == expected.QuiltWidth && actual.QuiltHeight == expected.QuiltHeight &&
                actual.Columns == expected.Columns && actual.Rows == expected.Rows && actual.Views == expected.Views &&
                quilt.settings.quiltWidth == expected.QuiltWidth && quilt.settings.quiltHeight == expected.QuiltHeight &&
                quilt.settings.columns == expected.Columns && quilt.settings.rows == expected.Rows && quilt.settings.views == expected.Views;
        }

        static int CaptureCenterView(int cycleNumber, string qualityName, QuiltQualityProfile expected, QualityStageEvidence stage)
        {
            int width, height, tileWidth, tileHeight, views;
            if (!GetRenderedProfile(expected, out width, out height, out tileWidth, out tileHeight, out views)) throw new InvalidOperationException("quilt profile became invalid before center view readback");
            return CaptureCenterView(cycleNumber, qualityName, tileWidth, tileHeight, stage);
        }

        static int CaptureCenterView(int cycleNumber, string qualityName, QuiltQualityProfile expected, CycleEvidence cycleEvidence)
        {
            int width, height, tileWidth, tileHeight, views;
            if (!GetRenderedProfile(expected, out width, out height, out tileWidth, out tileHeight, out views)) throw new InvalidOperationException("quilt profile became invalid before center view readback");
            return CaptureCenterView(cycleNumber, qualityName, tileWidth, tileHeight, cycleEvidence);
        }

        static int CaptureCenterView(int cycleNumber, string qualityName, int tileWidth, int tileHeight, QualityStageEvidence stage)
        {
            return CaptureCenterView(cycleNumber, qualityName, tileWidth, tileHeight, delegate(int redPixels, string fileName) { stage.redDominantPixels = redPixels; stage.image = fileName; });
        }

        static int CaptureCenterView(int cycleNumber, string qualityName, int tileWidth, int tileHeight, CycleEvidence cycleEvidence)
        {
            return CaptureCenterView(cycleNumber, qualityName, tileWidth, tileHeight, delegate(int redPixels, string fileName) { cycleEvidence.redDominantPixels = redPixels; cycleEvidence.image = fileName; });
        }

        static int CaptureCenterView(int cycleNumber, string qualityName, int tileWidth, int tileHeight, Action<int, string> record)
        {
            if (string.IsNullOrEmpty(evidencePath)) throw new InvalidOperationException("evidence path is unavailable for center view PNG");
            RenderTexture source = quilt.QuiltTexture;
            RectInt tile = quilt.settings.GetLayout().GetTile(CenterView);
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = null;
            try
            {
                readback = new Texture2D(tileWidth, tileHeight, TextureFormat.RGBA32, false);
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(tile.x, tile.y, tile.width, tile.height), 0, 0, false);
                readback.Apply(false, false);
                Color32[] pixels = readback.GetPixels32();
                int redPixels = 0;
                for (int index = 0; index < pixels.Length; ++index)
                {
                    Color32 pixel = pixels[index];
                    if (pixel.r > 13 && pixel.r > pixel.g * 1.2f && pixel.r > pixel.b * 1.2f) redPixels++;
                }
                string fileName = "cycle-" + cycleNumber.ToString(CultureInfo.InvariantCulture) + "-" + qualityName + "-center.png";
                Directory.CreateDirectory(evidencePath);
                File.WriteAllBytes(Path.Combine(evidencePath, fileName), readback.EncodeToPNG());
                record(redPixels, fileName);
                return redPixels;
            }
            finally
            {
                RenderTexture.active = previous;
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }

        static void CompleteRenderCycle()
        {
            CycleEvidence cycleEvidence = evidence.cycles[cycle - 1];
            cycleEvidence.readyObserved = true;
            cycleEvidence.completed = output.Completed;
            cycleEvidence.submissions = output.Submissions;
            cycleEvidence.state = session.State.ToString();
            if (cycle == 1 && evidence.qualityStages != null)
            {
                for (int index = 0; index < evidence.qualityStages.Length; ++index) cycleEvidence.completedDelta += evidence.qualityStages[index].completedDelta;
                QualityStageEvidence finalPreview = evidence.qualityStages[evidence.qualityStages.Length - 1];
                cycleEvidence.quality = finalPreview.quality;
                cycleEvidence.expectedWidth = finalPreview.expectedWidth;
                cycleEvidence.expectedHeight = finalPreview.expectedHeight;
                cycleEvidence.actualWidth = finalPreview.actualWidth;
                cycleEvidence.actualHeight = finalPreview.actualHeight;
                cycleEvidence.tileWidth = finalPreview.tileWidth;
                cycleEvidence.tileHeight = finalPreview.tileHeight;
                cycleEvidence.views = finalPreview.views;
                cycleEvidence.redDominantPixels = finalPreview.redDominantPixels;
                cycleEvidence.image = finalPreview.image;
                cycleEvidence.tile = finalPreview.tileWidth.ToString(CultureInfo.InvariantCulture) + "x" + finalPreview.tileHeight.ToString(CultureInfo.InvariantCulture);
            }
            Log("rendering cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " completed=" + output.Completed.ToString(CultureInfo.InvariantCulture) + " views=" + cycleEvidence.views.ToString(CultureInfo.InvariantCulture) + " tile=" + cycleEvidence.tileWidth.ToString(CultureInfo.InvariantCulture) + "x" + cycleEvidence.tileHeight.ToString(CultureInfo.InvariantCulture) + " red=" + cycleEvidence.redDominantPixels.ToString(CultureInfo.InvariantCulture));
            phase = cycle == 1 ? Phase.WaitingFirstEdit : Phase.WaitingSecondEdit;
            phaseStarted = EditorApplication.timeSinceStartup;
            SetTraceStage("waiting-edit");
            RecordPendingSnapshot("stop-start");
            PersistContext();
            EditorApplication.isPlaying = false;
        }

        static void CheckQualityStageTimeout(QuiltQualityPreset quality, string reason)
        {
            if (EditorApplication.timeSinceStartup - qualityStageStartedAt > PlayTimeoutSeconds) Fail("Bridge quality stage " + quality.ToString() + " " + reason);
        }

        static void UpdateRecovery()
        {
            RefreshReferences();
            if (session == null || output == null) { Fail("Bridge session references disappeared after Play"); return; }
            if (!RecordPendingSnapshot("recovery-poll")) { Fail("Bridge pending counts unavailable during Editor recovery"); return; }
            if (lastShutdownPending != 0 || lastNativePending != 0)
            {
                double recoveryElapsed = EditorApplication.timeSinceStartup - phaseStarted;
                if (recoveryElapsed >= 1.0 && !editorRecoveryWarningWritten)
                {
                    editorRecoveryWarningWritten = true;
                    SessionState.SetBool(EditorRecoveryWarningKey, true);
                    AppendTrace(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " event=shutdown-pending-over-1s thresholdSeconds=1 shutdownPending=" + lastShutdownPending.ToString(CultureInfo.InvariantCulture) + " nativePending=" + lastNativePending.ToString(CultureInfo.InvariantCulture));
                }
                if (recoveryElapsed >= 10.0 && lastNativePending > 0 && !nativePendingTenSecondWritten)
                {
                    nativePendingTenSecondWritten = true;
                    AppendTrace(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " event=native-pending-over-10s thresholdSeconds=10 nativePending=" + lastNativePending.ToString(CultureInfo.InvariantCulture));
                }
                CheckTimeout(RecoveryTimeoutSeconds, "Bridge cleanup timeout");
                return;
            }
            if (session.State != ViewWeaveBridgeSession.SessionState.Idle || output.State != BridgeWorkerQuiltOutput.WorkerState.Closed) { CheckTimeout(RecoveryTimeoutSeconds, "Bridge cleanup timeout"); return; }
            CycleEvidence recoveredCycle = evidence.cycles[cycle - 1];
            recoveredCycle.pendingAfterStop = lastShutdownPending;
            recoveredCycle.shutdownPendingAfterStop = lastShutdownPending;
            recoveredCycle.nativePendingAfterStop = lastNativePending;
            evidence.cycles[cycle - 1].cleanupVerified = true;
            evidence.cycles[cycle - 1].stoppedSessionState = session.State.ToString();
            evidence.cycles[cycle - 1].stoppedOutputState = output.State.ToString();
            Log("recovered cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " pending=0");
            AppendTrace(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " event=recovered cycle=" + cycle.ToString(CultureInfo.InvariantCulture) + " shutdownPending=0 nativePending=0 zeroCountsUtc=" + TraceEscape(recoveredCycle.zeroCountsUtc));
            if (phase == Phase.RecoveringFailure) { Finish(false, failure); return; }
            if (cycle == 1)
            {
                phase = Phase.WaitingSecondPlay;
                phaseStarted = EditorApplication.timeSinceStartup;
                SetTraceStage("waiting-play");
                PersistContext();
                EditorApplication.isPlaying = true;
            }
            else Finish(true, null);
        }

        static void RefreshReferences()
        {
            if (runner == null) runner = UnityEngine.Object.FindAnyObjectByType<BridgeWorkerValidationRunner>();
            if (runner == null) return;
            Transform owner = runner.transform;
            session = owner.GetComponent<ViewWeaveBridgeSession>();
            output = owner.GetComponent<BridgeWorkerQuiltOutput>();
            quilt = owner.GetComponent<QuiltRenderer>();
            rig = owner.GetComponent<ViewWeaveRig>();
        }

        static void CheckTimeout(double timeout, string message)
        {
            if (EditorApplication.timeSinceStartup - phaseStarted > timeout) Fail(message);
        }

        static void FailBeforePlay(Exception error)
        {
            failure = Sanitize(error.GetType().Name + ": " + error.Message);
            SetEvidenceFailure(failure);
            SafeLog("failed-before-play " + failure);
            Finish(false, failure);
        }

        static void FailDuringRun(Exception error)
        {
            Fail(Sanitize(error.GetType().Name + ": " + error.Message));
        }

        static void Fail(string message)
        {
            if (!running || finishing) return;
            SetEvidenceFailure(message);
            SafeLog("failed " + failure);
            if (Application.isPlaying)
            {
                phase = Phase.StoppingAfterFailure;
                phaseStarted = EditorApplication.timeSinceStartup;
                SetTraceStage("stopping");
                RecordPendingSnapshot("stop-start");
                SafePersistContext();
                EditorApplication.isPlaying = false;
            }
            else Finish(false, failure);
        }

        static void Finish(bool success, string error)
        {
            if (finishing) return;
            if (Application.isPlaying)
            {
                if (!string.IsNullOrEmpty(error)) SetEvidenceFailure(error);
                phase = Phase.StoppingAfterFailure;
                phaseStarted = EditorApplication.timeSinceStartup;
                SetTraceStage("stopping");
                RecordPendingSnapshot("stop-start");
                SafePersistContext();
                try { EditorApplication.isPlaying = false; }
                catch (Exception stopError) { SetEvidenceFailure("play stop failed: " + stopError.Message); }
                return;
            }
            finishing = true;
            SetTraceStage("finished");
            if (!string.IsNullOrEmpty(error)) SetEvidenceFailure(error);
            bool validationPassed = success && EvidenceHasRenderProof();
            bool exitWhenFinished = exitAfterLifecycle;
            if (evidence != null) evidence.ok = false;
            try
            {
                RestoreEditorState();
            }
            catch (Exception restoreError)
            {
                if (evidence != null) evidence.ok = false;
                SetEvidenceFailure("restore failed: " + restoreError.Message);
            }
            finally
            {
                phase = Phase.Finished;
                DrainWarnings();
                Unsubscribe();
                DrainWarnings();
                try { WriteEvidence(); }
                catch (Exception writeError) { SetEvidenceFailure("evidence write failed: " + writeError.Message); }
                SafeLog(validationPassed && evidence != null && string.IsNullOrEmpty(evidence.error) ? "pass" : "fail");
                if (evidence != null) evidence.ok = validationPassed && string.IsNullOrEmpty(evidence.error);
                try { WriteEvidence(); }
                catch (Exception writeError) { SetEvidenceFailure("evidence write failed: " + writeError.Message); }
                running = false;
                ClearState();
                exitAfterLifecycle = false;
                if (exitWhenFinished)
                {
                    int exitCode = evidence != null && evidence.ok && string.IsNullOrEmpty(evidence.error) ? 0 : 1;
                    TraceExit("exit-requested", exitCode);
                    ExitEditorWhenSafe(exitCode);
                }
            }
        }

        static void ExitEditorWhenSafe(int exitCode)
        {
            TraceExit("safety-check-entered", exitCode);
            if (exitSuppressedForUnsafeScene || HasDirtyOrUntitledOpenScene())
            {
                TraceExit("suppressed-dirty-or-untitled", exitCode);
                exitSuppressedForUnsafeScene = true;
                if (evidence != null) evidence.exitSuppressed = true;
                string previousError = evidence == null ? string.Empty : evidence.error;
                SetEvidenceFailure(string.IsNullOrEmpty(previousError) ? "auto exit suppressed because an open scene is dirty or untitled" : previousError + "; auto exit suppressed because an open scene is dirty or untitled");
                SafeLog("auto-exit-suppressed-dirty-or-untitled-scene");
                try { WriteEvidence(); }
                catch (Exception writeError) { SetEvidenceFailure("evidence write failed after auto exit suppression: " + writeError.Message); }
                return;
            }
            TraceExit("before-editor-exit", exitCode);
            EditorApplication.Exit(exitCode);
        }

        static void TraceExit(string eventName, int exitCode)
        {
            string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " exitCode=" + exitCode.ToString(CultureInfo.InvariantCulture) + " event=" + eventName;
            try { UnityEngine.Debug.Log("bridge-worker-exit-trace " + line); }
            catch { }
            try
            {
                if (!string.IsNullOrEmpty(evidencePath))
                {
                    Directory.CreateDirectory(evidencePath);
                    File.AppendAllText(Path.Combine(evidencePath, "exit-trace.txt"), line + Environment.NewLine, Encoding.ASCII);
                }
            }
            catch { }
        }

        static bool HasDirtyOrUntitledOpenScene()
        {
            for (int index = 0; index < SceneManager.sceneCount; ++index)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (scene.isLoaded && (string.IsNullOrEmpty(scene.path) || scene.isDirty)) return true;
            }
            return false;
        }

        static bool EvidenceHasRenderProof()
        {
            if (evidence == null || !string.IsNullOrEmpty(evidence.error) || evidence.cycles == null || evidence.cycles.Length < 2) return false;
            for (int cycleIndex = 0; cycleIndex < 2; ++cycleIndex)
            {
                CycleEvidence cycleEvidence = evidence.cycles[cycleIndex];
                if (cycleEvidence == null || cycleEvidence.completed < RequiredCompletions || cycleEvidence.completedDelta < RequiredCompletions || !cycleEvidence.readyObserved || cycleEvidence.views != RequiredViews ||
                    cycleEvidence.actualWidth <= 0 || cycleEvidence.actualHeight <= 0 || cycleEvidence.tileWidth <= 0 || cycleEvidence.tileHeight <= 0 ||
                    cycleEvidence.redDominantPixels <= 20 || string.IsNullOrEmpty(cycleEvidence.image)) return false;
            }
            QualityStageEvidence[] stages = evidence.qualityStages;
            if (stages == null || stages.Length != QualityCycle.Length) return false;
            for (int stageIndex = 0; stageIndex < stages.Length; ++stageIndex)
            {
                QualityStageEvidence stage = stages[stageIndex];
                if (stage == null || !stage.ok || !stage.requested || stage.completedDelta < RequiredCompletions || stage.views != RequiredViews ||
                    stage.expectedWidth <= 0 || stage.expectedHeight <= 0 || stage.actualWidth != stage.expectedWidth || stage.actualHeight != stage.expectedHeight ||
                    stage.tileWidth <= 0 || stage.tileHeight <= 0 || stage.redDominantPixels <= 20 || string.IsNullOrEmpty(stage.image) ||
                    stage.sessionState != ViewWeaveBridgeSession.SessionState.Rendering.ToString() || stage.outputState != BridgeWorkerQuiltOutput.WorkerState.Ready.ToString()) return false;
            }
            return (stages[1].actualWidth != stages[0].actualWidth || stages[1].actualHeight != stages[0].actualHeight) &&
                stages[2].actualWidth == stages[0].actualWidth && stages[2].actualHeight == stages[0].actualHeight;
        }

        static void SaveEvidenceState()
        {
            SessionState.SetString(EvidenceKey, evidencePath ?? string.Empty);
            SessionState.SetString(ExpectedApiKey, expectedApi ?? string.Empty);
            SessionState.SetString(PipelineKey, pipelineName ?? string.Empty);
            SessionState.SetString(DomainReloadKey, evidence == null ? string.Empty : evidence.domainReload);
            SessionState.SetString(RuntimePathKey, runtimePath ?? string.Empty);
            SessionState.SetBool(ExitAfterLifecycleKey, exitAfterLifecycle);
        }

        static void PersistContext()
        {
            if (!running) return;
            SessionState.SetBool(RunKey, true);
            SessionState.SetString(OwnerKey, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
            SessionState.SetInt(PhaseKey, (int)phase);
            SessionState.SetInt(CycleKey, cycle);
            SessionState.SetString(StartedKey, phaseStarted.ToString("R", CultureInfo.InvariantCulture));
            SaveEvidenceState();
        }

        static void RestoreContext()
        {
            running = true;
            phase = (Phase)SessionState.GetInt(PhaseKey, (int)Phase.WaitingFirstPlay);
            cycle = SessionState.GetInt(CycleKey, 0);
            double.TryParse(SessionState.GetString(StartedKey, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out phaseStarted);
            evidencePath = SessionState.GetString(EvidenceKey, string.Empty);
            expectedApi = SessionState.GetString(ExpectedApiKey, "d3d11");
            expectedGraphicsDeviceType = expectedApi == "d3d12" ? GraphicsDeviceType.Direct3D12 : GraphicsDeviceType.Direct3D11;
            pipelineName = SessionState.GetString(PipelineKey, "urp");
            runtimePath = SessionState.GetString(RuntimePathKey, string.Empty);
            exitAfterLifecycle = SessionState.GetBool(ExitAfterLifecycleKey, false);
            editorRecoveryWarningWritten = SessionState.GetBool(EditorRecoveryWarningKey, false);
            if (!string.IsNullOrEmpty(evidencePath) && File.Exists(Path.Combine(evidencePath, "summary.json"))) evidence = JsonUtility.FromJson<Evidence>(File.ReadAllText(Path.Combine(evidencePath, "summary.json")));
            if (evidence == null) evidence = new Evidence { expectedApi = expectedApi, expectedGraphicsDeviceType = expectedGraphicsDeviceType.ToString(), pipeline = pipelineName, exitAfterLifecycle = exitAfterLifecycle, processId = Process.GetCurrentProcess().Id, scenePath = ScenePath, unityVersion = Application.unityVersion };
            if (evidence.cycles == null || evidence.cycles.Length < 2) evidence.cycles = new[] { new CycleEvidence(), new CycleEvidence() };
            if (evidence.actualGraphicsDeviceTypes == null) evidence.actualGraphicsDeviceTypes = new List<string>();
        }

        static void RestoreEditorState()
        {
            if (!SessionState.GetBool(StateSavedKey, false)) return;
            RestoreAction("graphics pipeline", delegate { GraphicsSettings.defaultRenderPipeline = LoadPipeline(SessionState.GetString(OriginalGraphicsKey, string.Empty)); });
            RestoreAction("quality pipeline", delegate { QualitySettings.renderPipeline = LoadPipeline(SessionState.GetString(OriginalQualityKey, string.Empty)); });
            RestoreAction("run in background", delegate { Application.runInBackground = SessionState.GetBool(OriginalRunInBackgroundKey, false); });
            RestoreAction("enter play mode options enabled", delegate { EditorSettings.enterPlayModeOptionsEnabled = SessionState.GetBool(OriginalEditorOptionsEnabledKey, false); });
            RestoreAction("enter play mode options", delegate { EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions)SessionState.GetInt(OriginalEditorOptionsKey, 0); });
            RestoreAction("scene setup", delegate
            {
                SceneSetupState saved = JsonUtility.FromJson<SceneSetupState>(SessionState.GetString(OriginalScenesKey, string.Empty));
                if (saved != null && saved.items != null && saved.items.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(saved.ToSceneSetup());
            });
        }

        static void RestoreAction(string name, Action action)
        {
            try { action(); }
            catch (Exception error) { SetEvidenceFailure("restore " + name + " failed: " + error.Message); }
        }

        static RenderPipelineAsset LoadPipeline(string path)
        {
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
        }

        static void WriteEvidence()
        {
            if (evidence == null || string.IsNullOrEmpty(evidencePath)) return;
            Directory.CreateDirectory(evidencePath);
            File.WriteAllText(Path.Combine(evidencePath, "summary.json"), JsonUtility.ToJson(evidence, true), Encoding.ASCII);
        }

        static void Log(string message)
        {
            if (string.IsNullOrEmpty(evidencePath)) return;
            Directory.CreateDirectory(evidencePath);
            string line = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " pid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + " scene=" + Sanitize(SceneManager.GetActiveScene().path) + " " + Sanitize(message) + Environment.NewLine;
            File.AppendAllText(Path.Combine(evidencePath, "lifecycle.log"), line, Encoding.ASCII);
            WriteEvidence();
        }

        static void SafeLog(string message)
        {
            try { Log(message); }
            catch (Exception error) { SetEvidenceFailure("evidence log failed: " + error.Message); }
        }

        static void SafePersistContext()
        {
            try { PersistContext(); }
            catch (Exception error) { SetEvidenceFailure("context persistence failed: " + error.Message); }
        }

        static void SetEvidenceFailure(string message)
        {
            failure = Sanitize(message);
            if (evidence != null)
            {
                evidence.error = failure;
                evidence.ok = false;
            }
        }

        static void ClearState()
        {
            SessionState.EraseBool(RunKey); SessionState.EraseString(OwnerKey); SessionState.EraseInt(PhaseKey); SessionState.EraseInt(CycleKey); SessionState.EraseString(StartedKey); SessionState.EraseString(EvidenceKey); SessionState.EraseString(ExpectedApiKey); SessionState.EraseString(PipelineKey); SessionState.EraseString(DomainReloadKey); SessionState.EraseString(RuntimePathKey); SessionState.EraseBool(ExitAfterLifecycleKey); SessionState.EraseBool(EditorRecoveryWarningKey); SessionState.EraseString(OriginalGraphicsKey); SessionState.EraseString(OriginalQualityKey); SessionState.EraseString(OriginalScenesKey); SessionState.EraseBool(OriginalRunInBackgroundKey); SessionState.EraseBool(OriginalEditorOptionsEnabledKey); SessionState.EraseInt(OriginalEditorOptionsKey); SessionState.EraseBool(StateSavedKey);
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (string.Equals(args[i], key, StringComparison.Ordinal)) return args[i + 1];
            return null;
        }

        static string FirstError(string first, string second)
        {
            return !string.IsNullOrEmpty(first) ? first : !string.IsNullOrEmpty(second) ? second : "unknown bridge error";
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            StringBuilder result = new StringBuilder(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) result.Append(character);
            return result.ToString();
        }

        sealed class Configuration
        {
            public string api, pipeline, domainReload, runtimePath, evidencePath;
            public bool exitAfterLifecycle;
        }

        sealed class WarningTrace
        {
            public string utc, raw, stack;
            public int count, cycle, stage;
        }

        [Serializable]
        sealed class Evidence
        {
            public bool ok, runtimePathExists, exitAfterLifecycle, exitSuppressed;
            public int warningCount;
            public int processId;
            public string expectedApi, expectedGraphicsDeviceType, pipeline, domainReload, scenePath, unityVersion, quality, grid, error, traceFile;
            public List<string> actualGraphicsDeviceTypes = new List<string>();
            public CycleEvidence[] cycles = { new CycleEvidence(), new CycleEvidence() };
            public QualityStageEvidence[] qualityStages = { new QualityStageEvidence(), new QualityStageEvidence(), new QualityStageEvidence() };
        }

        [Serializable]
        sealed class CycleEvidence
        {
            public bool isEditor, isPlaying, readyObserved, cleanupVerified;
            public bool zeroCountsBeforeStop, zeroCountsAfterStop;
            public string state, platform, tile, stoppedSessionState, stoppedOutputState, quality, image;
            public string zeroCountsUtc;
            public uint completed, submissions;
            public uint completedDelta;
            public int pendingAfterStop, pendingBeforeStop, shutdownPendingBeforeStop, shutdownPendingAfterStop, expectedWidth, expectedHeight, actualWidth, actualHeight, tileWidth, tileHeight, views, redDominantPixels;
            public uint nativePendingBeforeStop, nativePendingAfterStop;
            public bool counterResetObserved;
        }

        [Serializable]
        sealed class QualityStageEvidence
        {
            public bool requested, counterResetObserved, ok;
            public string quality, sessionState, outputState, image;
            public uint startCompleted, completed, completedDelta;
            public int expectedWidth, expectedHeight, actualWidth, actualHeight, tileWidth, tileHeight, views, redDominantPixels;
        }

        [Serializable]
        sealed class SceneSetupState
        {
            public SceneSetupItem[] items;
            public static SceneSetupState From(SceneSetup[] setup)
            {
                var result = new SceneSetupState { items = new SceneSetupItem[setup == null ? 0 : setup.Length] };
                for (int i = 0; i < result.items.Length; ++i) result.items[i] = SceneSetupItem.From(setup[i]);
                return result;
            }
            public SceneSetup[] ToSceneSetup()
            {
                var result = new SceneSetup[items == null ? 0 : items.Length];
                for (int i = 0; i < result.Length; ++i) result[i] = items[i].ToSceneSetup();
                return result;
            }
        }

        [Serializable]
        sealed class SceneSetupItem
        {
            public string path;
            public bool isLoaded, isActive, isSubScene;
            public static SceneSetupItem From(SceneSetup setup) => new SceneSetupItem { path = setup.path, isLoaded = setup.isLoaded, isActive = setup.isActive, isSubScene = setup.isSubScene };
            public SceneSetup ToSceneSetup() => new SceneSetup { path = path, isLoaded = isLoaded, isActive = isActive, isSubScene = isSubScene };
        }
    }
}
