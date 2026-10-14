#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;

namespace ViewWeave.WindowsEditorCloseValidation
{
    public static class EditorCloseValidationRunner
    {
        const string Prefix = "VW_EDITOR_CLOSE_";
        const double DeadlineSeconds = 270.0;
        static string runRoot;
        static string eventPath;
        static string readyPath;
        static string mode;
        static string actualResolvedHdrpVersion = "unresolved";
        static double deadline;
        static double nextDiagnosticTime;
        static bool started;
        static bool previewStartInvoked;
        static bool stopRequested;
        static bool ready;
        static bool failed;
        static ViewWeaveRig rig;
        static BridgeWorkerQuiltOutput sourceOutput;
        static RenderPipelineAsset oldDefaultPipeline;
        static RenderPipelineAsset oldQualityPipeline;
        static RenderPipelineAsset hdrp;
        static UnityEngine.SceneManagement.Scene fixtureScene;
        static MethodInfo previewStart;
        static MethodInfo previewStop;
        static MethodInfo previewClose;
        static Type previewType;
        static Process currentProcess;
        static IntPtr[] retainedStdHandles = new IntPtr[3];
        static MethodInfo clearSceneDirtiness;
        static ViewWeaveBridgeSession runtimeSession;
        static bool oldEnterPlayModeOptionsEnabled;
        static EnterPlayModeOptions oldEnterPlayModeOptions;
        static bool playModeOptionsCaptured;
        static bool playModeOptionsRestored;
        static int isolatedExistingSceneRootCount;

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetStdHandle(int id);
        [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetFileType(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
            IntPtr targetProcess, out IntPtr targetHandle, uint access, bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CompareObjectHandles(IntPtr first, IntPtr second);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);

        public static void Start()
        {
            runRoot = Environment.GetEnvironmentVariable(Prefix + "ROOT") ?? string.Empty;
            mode = Environment.GetEnvironmentVariable(Prefix + "MODE") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(runRoot) || (mode != "stop-control" && mode != "active-close" && mode != "play-active-close"))
            {
                Fail("Missing run root or invalid mode.");
                return;
            }

            try
            {
                Directory.CreateDirectory(runRoot);
                eventPath = Path.Combine(runRoot, "editor-events.jsonl");
                readyPath = Path.Combine(runRoot, "close-ready.json");
                currentProcess = Process.GetCurrentProcess();
                CaptureStdHandles("startup");
                WriteRecord("startup", "mode=" + mode + " pid=" + currentProcess.Id +
                    " unity=" + Ascii(Application.unityVersion) + " graphics=" + SystemInfo.graphicsDeviceType +
                    " threading=" + SystemInfo.renderingThreadingMode + " standardHandleTypes=" +
                    string.Join(",", Enumerable.Range(0, 3).Select(i => FileTypeName(GetFileType(GetStdHandle(-10 - i))))));
                if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor ||
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || Application.unityVersion != "6000.6.4f1")
                    throw new InvalidOperationException("Expected Unity 6000.6.4f1 Windows D3D12 Editor.");
                for (int sceneIndex = 0; sceneIndex < EditorSceneManager.sceneCount; sceneIndex++)
                {
                    UnityEngine.SceneManagement.Scene existing = EditorSceneManager.GetSceneAt(sceneIndex);
                    if (existing.isDirty)
                        throw new InvalidOperationException("A pre-existing scene is dirty; preserving it takes precedence over the close test.");
                }

                oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
                oldQualityPipeline = QualitySettings.renderPipeline;
                hdrp = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                GraphicsSettings.defaultRenderPipeline = hdrp;
                QualitySettings.renderPipeline = hdrp;
                fixtureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                EditorSceneManager.SetActiveScene(fixtureScene);
                clearSceneDirtiness = typeof(EditorSceneManager).GetMethod("ClearSceneDirtiness",
                    BindingFlags.Static | BindingFlags.NonPublic, null,
                    new[] { typeof(UnityEngine.SceneManagement.Scene) }, null);
                if (clearSceneDirtiness == null)
                    throw new MissingMethodException("Unity EditorSceneManager.ClearSceneDirtiness(Scene) is unavailable.");
                BuildFixture();
                BindPreviewApi();
                CaptureNativeAndPreview("startup_before_output");
                ClearOwnedSceneDirtiness();
                EditorApplication.wantsToQuit += OnWantsToQuit;
                EditorApplication.quitting += OnQuitting;
                EditorApplication.playModeStateChanged += OnPlayModeChanged;
                EditorApplication.update += Update;
                deadline = EditorApplication.timeSinceStartup + DeadlineSeconds;
                nextDiagnosticTime = EditorApplication.timeSinceStartup;
                started = true;
                ViewWeave.HDRP.HdrpQuiltCamera configuredGuard = rig.GetComponent<ViewWeave.HDRP.HdrpQuiltCamera>();
                UnityEditor.PackageManager.PackageInfo hdrpInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(HDRenderPipelineAsset).Assembly);
                actualResolvedHdrpVersion = hdrpInfo == null ? "unresolved" : Ascii(hdrpInfo.version);
                WriteRecord("fixture_ready", "rig=" + Ascii(rig.name) + " camera=" + Ascii(rig.Renderer.sourceCamera.name) +
                    " sceneDirty=" + fixtureScene.isDirty + " previewApi=" + previewType.FullName +
                    " requestedManifestHdrpVersion=" + Ascii(Environment.GetEnvironmentVariable(Prefix + "REQUESTED_HDRP")) +
                    " lockedHdrpVersion=" + Ascii(Environment.GetEnvironmentVariable(Prefix + "LOCKED_HDRP")) +
                    " lockedHdrpSource=" + Ascii(Environment.GetEnvironmentVariable(Prefix + "LOCKED_HDRP_SOURCE")) +
                    " actualResolvedHdrpVersion=" + actualResolvedHdrpVersion +
                    " exposureMode=" + configuredGuard.exposureMode + " exposureEV=" + configuredGuard.fallbackFixedExposureEV.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
                    " exposureRationale=avoid-follow-scene-history-wait quittingObserver=registered-after-BridgeEditorPreview-type-initialization");
                WriteRecord("preview_start_deferred", "reason=waiting-for-image-guard-recovery-and-discovery-bootstrap");
            }
            catch (Exception exception)
            {
                Fail("Setup failed: " + Ascii(exception.GetBaseException().Message));
            }
        }

        static void BuildFixture()
        {
            GameObject root = new GameObject("Editor Close Diagnostic Rig");
            root.SetActive(false);
            Camera camera = root.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.transform.position = Vector3.zero;
            camera.transform.rotation = Quaternion.identity;
            root.AddComponent<HDAdditionalCameraData>();
            QuiltRenderer renderer = root.AddComponent<QuiltRenderer>();
            rig = root.AddComponent<ViewWeaveRig>();
            rig.ApplyProfile(new QuiltQualityProfile(128, 64, 4, 2, 8, 1f, 20f));
            renderer.settings.cameraDistance = 3f;
            renderer.settings.fieldOfView = 60f;
            ViewWeave.HDRP.HdrpQuiltCamera hdrpGuard = root.AddComponent<ViewWeave.HDRP.HdrpQuiltCamera>();
            hdrpGuard.exposureMode = ViewWeave.HDRP.HdrpQuiltExposureMode.ManualFixed;
            hdrpGuard.fallbackFixedExposure = true;
            hdrpGuard.fallbackFixedExposureEV = 4f;
            rig.SetGuard(hdrpGuard);
            GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            target.name = "Close Diagnostic Target";
            target.transform.position = new Vector3(0f, 0f, 2f);
            Shader shader = Shader.Find("HDRP/Unlit");
            if (shader == null) throw new InvalidOperationException("HDRP/Unlit shader is unavailable.");
            Material material = new Material(shader);
            if (material.HasProperty("_UnlitColor")) material.SetColor("_UnlitColor", Color.green);
            else if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", Color.green);
            target.GetComponent<Renderer>().sharedMaterial = material;

            root.AddComponent<BridgeWorkerQuiltOutput>().initializeOnEnable = false;
            sourceOutput = root.GetComponent<BridgeWorkerQuiltOutput>();
            rig.SetOutput(sourceOutput);
            if (mode == "play-active-close")
            {
                runtimeSession = root.AddComponent<ViewWeaveBridgeSession>();
                runtimeSession.startOnEnable = true;
            }
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("ViewWeaveRig configuration failed.");
            if (!rig.ValidateRig(out string error)) throw new InvalidOperationException("Rig validation failed: " + Ascii(error));
            root.SetActive(true);
            ClearOwnedSceneDirtiness();
        }

        static void ClearOwnedSceneDirtiness()
        {
            if (fixtureScene.IsValid() && fixtureScene.isDirty)
            {
                if (clearSceneDirtiness == null)
                    throw new MissingMethodException("Unity EditorSceneManager.ClearSceneDirtiness(Scene) is unavailable.");
                clearSceneDirtiness.Invoke(null, new object[] { fixtureScene });
            }
        }

        static bool TryWaitForBootstrap()
        {
            Type guard = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeNativeImageGuard", true);
            MethodInfo validate = guard.GetMethod("TryValidateGlobalCalls", BindingFlags.Static | BindingFlags.NonPublic);
            Type identityType = validate.GetParameters()[1].ParameterType.GetElementType();
            object[] args = { false, Activator.CreateInstance(identityType), null };
            bool imageGuardValid = (bool)validate.Invoke(null, args);
            bool moduleLoaded = (bool)args[0];
            string guardError = Ascii(Convert.ToString(args[2]));
            if (!imageGuardValid)
            {
                if (guardError.IndexOf("pending", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    WriteRecord("bootstrap_wait", "stage=image-guard reason=pending moduleLoaded=" + moduleLoaded + " error=" + Escape(guardError));
                    return false;
                }
                throw new InvalidOperationException("Bridge native image guard rejected bootstrap: " + guardError);
            }

            Type lifecycle = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeWorkerLifecycle", true);
            Type discovery = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeDisplayDiscovery", true);
            bool recoveryPending = ReadInternalStaticBool(lifecycle, "RecoveryPending");
            bool discoveryBusy = ReadInternalStaticBool(discovery, "IsBusy");
            if (recoveryPending || discoveryBusy)
            {
                WriteRecord("bootstrap_wait", "stage=worker-or-discovery recoveryPending=" + recoveryPending + " discoveryBusy=" + discoveryBusy +
                    " imageGuardValid=true moduleLoaded=" + moduleLoaded);
                return false;
            }
            WriteRecord("bootstrap_ready", "imageGuardValid=true moduleLoaded=" + moduleLoaded + " recoveryPending=false discoveryBusy=false");
            return true;
        }

        static bool ReadInternalStaticBool(Type type, string propertyName)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null) throw new MissingMemberException(type.FullName, propertyName);
            return (bool)property.GetValue(null);
        }

        static void BindPreviewApi()
        {
            previewType = Type.GetType("ViewWeave.Editor.BridgeEditorPreview, ViewWeave.Bridge.Editor", true);
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            previewStart = previewType.GetMethod("Start", flags);
            previewStop = previewType.GetMethod("Stop", flags);
            previewClose = previewType.GetMethod("CloseForTargetChange", flags);
            if (previewStart == null || previewStop == null || previewClose == null)
                throw new MissingMethodException("BridgeEditorPreview lifecycle methods are unavailable.");
        }

        static void Update()
        {
            if (!started || ready || failed) return;
            if (stopRequested)
            {
                PollStopControl();
                return;
            }
            try
            {
                if (EditorApplication.timeSinceStartup >= nextDiagnosticTime)
                {
                    CaptureNativeAndPreview("poll");
                    CaptureStdHandles("poll");
                    nextDiagnosticTime = EditorApplication.timeSinceStartup + 1.0;
                }
                if (EditorApplication.timeSinceStartup >= deadline)
                    throw new TimeoutException(previewStartInvoked
                        ? "Preview did not reach Ready and five completed frames."
                        : "Bridge startup bootstrap did not become ready before the deadline.");
                if (mode == "play-active-close")
                {
                    if (!EditorApplication.isPlaying)
                    {
                        if (!TryWaitForBootstrap()) return;
                        PreparePlayActiveClose();
                        return;
                    }
                    PollPlayActiveOutput();
                    return;
                }
                if (!previewStartInvoked)
                {
                    if (!TryWaitForBootstrap()) return;
                    previewStartInvoked = true;
                    object[] startArgs = { rig, true, null };
                    if (!(bool)previewStart.Invoke(null, startArgs))
                        throw new InvalidOperationException("BridgeEditorPreview.Start rejected the device output: " + Ascii(Convert.ToString(startArgs[2])));
                    WriteRecord("preview_start_returned", "active=" + ReadPreview("IsActive") + " status=" + Ascii(ReadPreview("Status")));
                    CaptureStdHandles("preview_start_returned");
                    CaptureNativeAndPreview("preview_start_returned");
                    return;
                }
                object output = ReadPreviewField("producerOutput");
                if (output == null) return;
                string workerState = ReadMember(output, "State").ToString();
                string workerError = Ascii(ReadMember(output, "LastError"));
                string previewError = Ascii(ReadPreview("LastError"));
                if (workerState == "Fault" || ReadPreview("Status")?.ToString() == "Error")
                    throw new InvalidOperationException("Bridge preview entered an error state: worker=" + workerState +
                        " workerError=" + workerError + " previewError=" + previewError);
                bool outputReady = (bool)output.GetType().GetProperty("Ready").GetValue(output);
                uint completed = (uint)output.GetType().GetProperty("Completed").GetValue(output);
                if (!Convert.ToBoolean(ReadPreview("IsActive")) || !outputReady || workerState != "Ready" || completed < 5) return;
                int activePending = SafePendingCount(out bool activeGuardValid, out string activeGuardError);
                if (!activeGuardValid || activePending < 0)
                    throw new InvalidOperationException("Bridge image guard did not approve pending-state observation: " + Ascii(activeGuardError));

                WriteRecord("preview_ready", "state=" + Ascii(ReadPreview("State")) + " active=true outputReady=true completed=" + completed +
                    " workerState=" + workerState + " submissions=" + ReadMember(output, "Submissions") +
                    " aggregatePending=" + activePending + " imageGuardValid=true status=" + Ascii(ReadPreview("Status")));
                CaptureStdHandles("preview_ready");
                CaptureNativeAndPreview("preview_ready");
                if (mode == "stop-control")
                {
                    previewClose.Invoke(null, new object[] { "Windows Editor close control: stop preview before WM_CLOSE." });
                    stopRequested = true;
                    deadline = EditorApplication.timeSinceStartup + 90.0;
                    WriteRecord("preview_stop_requested", "active=" + ReadPreview("IsActive") + " stopping=" + ReadPreview("IsStopping"));
                    PollStopControl();
                }
                else PublishCloseReady("active-device-output");
                ready = mode == "active-close";
            }
            catch (Exception exception) { Fail("Preview validation failed: " + Ascii(exception.GetBaseException().Message)); }
        }

        static void PreparePlayActiveClose()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            oldEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            oldEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
            playModeOptionsCaptured = true;

            for (int i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                UnityEngine.SceneManagement.Scene scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.IsValid() || scene == fixtureScene) continue;
                foreach (GameObject sceneRoot in scene.GetRootGameObjects())
                {
                    if (sceneRoot == null || !sceneRoot.activeSelf) continue;
                    sceneRoot.SetActive(false);
                    isolatedExistingSceneRootCount++;
                }
                if (scene.isDirty) ClearSceneDirtinessFor(scene);
            }

            EditorSettings.enterPlayModeOptions = oldEnterPlayModeOptions |
                EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            WriteRecord("play_enter_requested", "defaultOptionsEnabled=" + oldEnterPlayModeOptionsEnabled +
                " defaultOptions=" + oldEnterPlayModeOptions +
                " temporaryOptionsEnabled=true temporaryOptions=" + EditorSettings.enterPlayModeOptions +
                " domainReloadDisabled=true sceneReloadDisabled=true existingSceneRootsIsolated=" +
                isolatedExistingSceneRootCount + " fixtureScene=" + Escape(fixtureScene.name));
            EditorApplication.isPlaying = true;
        }

        static void ClearSceneDirtinessFor(UnityEngine.SceneManagement.Scene scene)
        {
            if (clearSceneDirtiness == null)
                throw new MissingMethodException("Unity EditorSceneManager.ClearSceneDirtiness(Scene) is unavailable.");
            clearSceneDirtiness.Invoke(null, new object[] { scene });
        }

        static void RestorePlayModeOptions()
        {
            if (!playModeOptionsCaptured || playModeOptionsRestored) return;
            EditorSettings.enterPlayModeOptions = oldEnterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = oldEnterPlayModeOptionsEnabled;
            playModeOptionsRestored = true;
            WriteRecord("play_settings_restored", "enterPlayModeOptionsEnabled=" + oldEnterPlayModeOptionsEnabled +
                " enterPlayModeOptions=" + oldEnterPlayModeOptions);
        }

        static void PollPlayActiveOutput()
        {
            if (!EditorApplication.isPlaying) return;
            if (runtimeSession == null || sourceOutput == null)
                throw new InvalidOperationException("Play fixture session or output is unavailable.");
            if (runtimeSession.State == ViewWeaveBridgeSession.SessionState.Fault ||
                sourceOutput.State == BridgeWorkerQuiltOutput.WorkerState.Fault ||
                !string.IsNullOrEmpty(runtimeSession.LastError) || !string.IsNullOrEmpty(sourceOutput.LastError))
                throw new InvalidOperationException("Play fixture entered Fault: session=" + runtimeSession.LastError +
                    " output=" + sourceOutput.LastError);
            uint completed = sourceOutput.Completed;
            if (runtimeSession.State != ViewWeaveBridgeSession.SessionState.Rendering ||
                sourceOutput.State != BridgeWorkerQuiltOutput.WorkerState.Ready || !sourceOutput.Ready || completed < 5) return;
            int pending = SafePendingCount(out bool guardValid, out string guardError);
            if (!guardValid || pending < 0)
                throw new InvalidOperationException("Play output image guard observation failed: " + Ascii(guardError));
            WriteRecord("play_output_ready", "isPlaying=true sessionState=" + runtimeSession.State +
                " outputState=" + sourceOutput.State + " outputReady=true completed=" + completed +
                " submitted=" + sourceOutput.Submissions + " subpixelMode=" + runtimeSession.SubpixelMode +
                " aggregatePending=" + pending + " imageGuardValid=true existingSceneRootsIsolated=" + isolatedExistingSceneRootCount);
            CaptureStdHandles("play_output_ready");
            CaptureNativeAndPreview("play_output_ready");
            PublishCloseReady("play-active-device-output");
            ready = true;
        }

        static void PollStopControl()
        {
            if (!stopRequested || mode != "stop-control") return;
            object activeOutput = ReadPreviewField("producerOutput");
            int pending = SafePendingCount(out bool guardValid, out string guardError);
            bool stopped = !Convert.ToBoolean(ReadPreview("IsActive")) && !Convert.ToBoolean(ReadPreview("IsStopping")) &&
                (activeOutput == null || ReadMember(activeOutput, "State").ToString() == "Closed") && guardValid && pending == 0;
            if (stopped)
            {
                WriteRecord("preview_stopped", "active=false stopping=false outputState=" +
                    (activeOutput == null ? "destroyed" : ReadMember(activeOutput, "State")) +
                    " imageGuardValid=true imageGuardError=" + Ascii(guardError) + " aggregatePending=0");
                CaptureStdHandles("preview_stopped");
                CaptureNativeAndPreview("preview_stopped");
                PublishCloseReady("stop-control-pending-zero");
                ready = true;
            }
            else if (EditorApplication.timeSinceStartup >= deadline)
                Fail("Stop control did not reach inactive, output-closed, image-guard-valid, pending-zero state.");
        }

        static void PublishCloseReady(string condition)
        {
            ClearOwnedSceneDirtiness();
            int pid = Process.GetCurrentProcess().Id;
            string json = "{\"schema\":1,\"ready\":true,\"mode\":\"" + Escape(mode) + "\",\"condition\":\"" + Escape(condition) +
                "\",\"pid\":" + pid + ",\"unityVersion\":\"" + Escape(Application.unityVersion) + "\",\"actualUnityVersion\":\"" +
                Escape(Application.unityVersion) + "\",\"graphicsApi\":\"" + Escape(SystemInfo.graphicsDeviceType.ToString()) +
                "\",\"requestedManifestHdrpVersion\":\"" + Escape(Environment.GetEnvironmentVariable(Prefix + "REQUESTED_HDRP")) +
                "\",\"lockedHdrpVersion\":\"" + Escape(Environment.GetEnvironmentVariable(Prefix + "LOCKED_HDRP")) +
                "\",\"lockedHdrpSource\":\"" + Escape(Environment.GetEnvironmentVariable(Prefix + "LOCKED_HDRP_SOURCE")) +
                "\",\"actualResolvedHdrpVersion\":\"" + Escape(actualResolvedHdrpVersion) +
                "\",\"playModeOptionsEnabledBefore\":" + oldEnterPlayModeOptionsEnabled.ToString().ToLowerInvariant() +
                ",\"playModeOptionsBefore\":\"" + Escape(oldEnterPlayModeOptions.ToString()) +
                "\",\"playModeOptionsRestored\":" + playModeOptionsRestored.ToString().ToLowerInvariant() +
                ",\"isolatedExistingSceneRootCount\":" + isolatedExistingSceneRootCount +
                ",\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\"}";
            WriteFlushed(readyPath, json + "\n");
            WriteRecord("close_ready", "condition=" + condition + " pid=" + pid);
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            WriteRecord("play_mode", "state=" + state + " isPlaying=" + EditorApplication.isPlaying +
                " isPlayingOrWillChange=" + EditorApplication.isPlayingOrWillChangePlaymode +
                " runtimeSessionState=" + (runtimeSession == null ? "none" : runtimeSession.State.ToString()));
            if (state == PlayModeStateChange.EnteredPlayMode) RestorePlayModeOptions();
        }
        static bool OnWantsToQuit()
        {
            WriteRecord("wants_to_quit", "active=" + ReadPreview("IsActive") + " stopping=" + ReadPreview("IsStopping") +
                " state=" + Ascii(ReadPreview("State")) + " isPlaying=" + EditorApplication.isPlaying +
                " runtimeSessionState=" + (runtimeSession == null ? "none" : runtimeSession.State.ToString()) +
                " runtimeOutputState=" + (sourceOutput == null ? "none" : sourceOutput.State.ToString()) +
                " runtimeCompleted=" + (sourceOutput == null ? "unobserved" : sourceOutput.Completed.ToString()));
            CaptureStdHandles("wants_to_quit");
            CaptureNativeAndPreview("wants_to_quit");
            return true;
        }
        static void OnQuitting()
        {
            WriteRecord("quitting", "active=" + ReadPreview("IsActive") + " stopping=" + ReadPreview("IsStopping") +
                " state=" + Ascii(ReadPreview("State")) + " isPlaying=" + EditorApplication.isPlaying +
                " runtimeSessionState=" + (runtimeSession == null ? "none" : runtimeSession.State.ToString()) +
                " runtimeOutputState=" + (sourceOutput == null ? "none" : sourceOutput.State.ToString()) +
                " runtimeCompleted=" + (sourceOutput == null ? "unobserved" : sourceOutput.Completed.ToString()));
            CaptureStdHandles("quitting");
            CaptureNativeAndPreview("quitting");
            for (int i = 0; i < retainedStdHandles.Length; i++)
            {
                if (retainedStdHandles[i] != IntPtr.Zero) CloseHandle(retainedStdHandles[i]);
                retainedStdHandles[i] = IntPtr.Zero;
            }
        }

        static void CaptureNativeAndPreview(string stage)
        {
            string modulePath = string.Empty;
            string moduleHash = string.Empty;
            try
            {
                ProcessModule module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .FirstOrDefault(value => string.Equals(value.ModuleName, "lookingglass_bridge_shim.dll", StringComparison.OrdinalIgnoreCase));
                if (module != null)
                {
                    modulePath = Path.GetFullPath(module.FileName);
                    using (FileStream stream = File.OpenRead(modulePath))
                    using (SHA256 sha = SHA256.Create()) moduleHash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                }
            }
            catch (Exception error) { modulePath = "unavailable:" + Ascii(error.GetType().Name); }

            bool guardValid = false, moduleLoaded = false, aggregatePendingReadPermitted = false;
            string guardError = "not-called";
            object pendingValue = "unobserved";
            if (!string.IsNullOrEmpty(modulePath) && !modulePath.StartsWith("unavailable:", StringComparison.Ordinal))
            {
                try
                {
                    Type guard = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeNativeImageGuard", true);
                    MethodInfo validate = guard.GetMethod("TryValidateGlobalCalls", BindingFlags.Static | BindingFlags.NonPublic);
                    Type identityType = validate.GetParameters()[1].ParameterType.GetElementType();
                    object[] args = { false, Activator.CreateInstance(identityType), null };
                    guardValid = (bool)validate.Invoke(null, args);
                    moduleLoaded = (bool)args[0];
                    guardError = Ascii(Convert.ToString(args[2]));
                    if (guardValid && moduleLoaded)
                    {
                        aggregatePendingReadPermitted = true;
                        pendingValue = BridgeWorkerQuiltOutput.ShutdownPendingCount;
                    }
                }
                catch (Exception error) { guardError = Ascii(error.GetBaseException().Message); }
            }
            object output = ReadPreviewField("producerOutput");
            string worker = output == null ? "none" : Ascii(ReadMember(output, "State").ToString());
            string active = Ascii(ReadPreview("IsActive"));
            WriteRecord("diagnostic", "stage=" + stage + " nativeModulePath=" + Escape(modulePath) + " nativeModuleSha256=" + moduleHash +
                " nativeModuleLoaded=" + moduleLoaded + " imageGuardValid=" + guardValid + " imageGuardError=" + Escape(guardError) +
                " aggregatePendingReadPermitted=" + aggregatePendingReadPermitted + " aggregatePending=" + pendingValue + " previewActive=" + active +
                " previewState=" + Ascii(ReadPreview("State")) + " previewStopping=" + ReadPreview("IsStopping") +
                " workerState=" + worker + " completed=" + (output == null ? "unobserved" : ReadMember(output, "Completed")) +
                " submitted=" + (output == null ? "unobserved" : ReadMember(output, "Submissions")) +
                " submissionAttempts=" + (output == null ? "unobserved" : ReadMember(output, "SubmissionAttempts")) +
                " sessionState=" + ReadSessionState() + " isPlaying=" + EditorApplication.isPlaying +
                " runtimeSessionState=" + (runtimeSession == null ? "none" : runtimeSession.State.ToString()) +
                " runtimeOutputState=" + (sourceOutput == null ? "none" : sourceOutput.State.ToString()) +
                " runtimeCompleted=" + (sourceOutput == null ? "unobserved" : sourceOutput.Completed.ToString()) +
                " enterPlayModeOptionsEnabled=" + EditorSettings.enterPlayModeOptionsEnabled +
                " enterPlayModeOptions=" + EditorSettings.enterPlayModeOptions);
        }

        static int SafePendingCount(out bool guardValid, out string error)
        {
            guardValid = false;
            error = "native-module-not-observed";
            try
            {
                Type guard = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeNativeImageGuard", true);
                MethodInfo validate = guard.GetMethod("TryValidateGlobalCalls", BindingFlags.Static | BindingFlags.NonPublic);
                Type identityType = validate.GetParameters()[1].ParameterType.GetElementType();
                object[] args = { false, Activator.CreateInstance(identityType), null };
                guardValid = (bool)validate.Invoke(null, args) && (bool)args[0];
                error = Ascii(Convert.ToString(args[2]));
                if (!guardValid) return -1;
                return BridgeWorkerQuiltOutput.ShutdownPendingCount;
            }
            catch (Exception exception) { error = Ascii(exception.GetBaseException().Message); return -1; }
        }

        static void CaptureStdHandles(string stage)
        {
            string[] names = { "stdin", "stdout", "stderr" };
            int[] ids = { -10, -11, -12 };
            for (int i = 0; i < ids.Length; i++)
            {
                try
                {
                    IntPtr handle = GetStdHandle(ids[i]);
                    if (retainedStdHandles[i] == IntPtr.Zero && handle != IntPtr.Zero && handle != new IntPtr(-1))
                        DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out retainedStdHandles[i], 0, false, 2);
                    string same = "unavailable";
                    try { same = retainedStdHandles[i] == IntPtr.Zero ? "no-retained-handle" : CompareObjectHandles(retainedStdHandles[i], handle).ToString(); }
                    catch (EntryPointNotFoundException) { same = "api-unavailable"; }
                    uint type = handle == IntPtr.Zero || handle == new IntPtr(-1) ? 0 : GetFileType(handle);
                    WriteRecord("std_handle", "stage=" + stage + " name=" + names[i] + " value=" + handle +
                        " type=" + FileTypeName(type) + " duplicateSameObject=" + same);
                }
                catch (Exception error) { WriteRecord("std_handle", "stage=" + stage + " name=" + names[i] + " error=" + Ascii(error.GetType().Name)); }
            }
        }

        static object ReadPreview(string name)
        {
            try { return previewType.GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); }
            catch { return null; }
        }
        static object ReadPreviewField(string name)
        {
            try { return previewType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic).GetValue(null); }
            catch { return null; }
        }
        static object ReadMember(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? "unavailable" : property.GetValue(target);
        }
        static string ReadSessionState()
        {
            object session = ReadPreviewField("sourceSession");
            return session == null ? "none" : Ascii(ReadMember(session, "State").ToString());
        }

        static string FileTypeName(uint value) => value == 1 ? "disk" : value == 2 ? "char" : value == 3 ? "pipe" : value == 0 ? "unknown" : value.ToString();
        static string Escape(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        static string Ascii(string value) => string.IsNullOrEmpty(value) ? string.Empty : new string(value.Select(c => c >= 32 && c <= 126 ? c : '?').ToArray());
        static string Ascii(object value) => Ascii(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
        static void WriteRecord(string eventName, string fields)
        {
            if (string.IsNullOrEmpty(eventPath)) return;
            string line = "{\"utc\":\"" + DateTime.UtcNow.ToString("o") + "\",\"event\":\"" + Escape(eventName) + "\",\"fields\":\"" + Escape(fields) + "\"}" + "\n";
            try { File.AppendAllText(eventPath, line, Encoding.ASCII); }
            catch { }
        }
        static void WriteFlushed(string path, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }
        static void Fail(string reason)
        {
            failed = true;
            WriteRecord("failed", "reason=" + Escape(Ascii(reason)));
            string path = string.IsNullOrEmpty(runRoot) ? string.Empty : Path.Combine(runRoot, "helper-failed.txt");
            if (!string.IsNullOrEmpty(path)) try { WriteFlushed(path, Ascii(reason) + "\n"); } catch { }
        }
    }
}
#endif
