#if UNITY_EDITOR_WIN
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.VFX;
using ViewWeave;
using ViewWeave.URP;

namespace ViewWeave.SceneComparison.Tests
{
    public sealed class MainSdkHdrDeviceProbeTests
    {
        const string ScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string GreenGuid = "09e4dfff658c863459dc084250a47a15";
        const string CoinGuid = "ccd23b3eb0caff84aab1cd555791352b";
        const int ExpectedSceneEffectCount = 33;
        const int MaximumRunFrames = 18000;
        const long GrowthStopBytes = 512L * 1024L * 1024L;
        const string EvidenceEnvironment = "VW_MAIN_HDR_DEVICE_EVIDENCE_DIR";
        const string DeviceOptInEnvironment = "VW_MAIN_HDR_DEVICE_TEST";

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessMemoryCountersEx
        {
            public uint cb;
            public uint pageFaultCount;
            public UIntPtr peakWorkingSetSize;
            public UIntPtr workingSetSize;
            public UIntPtr quotaPeakPagedPoolUsage;
            public UIntPtr quotaPagedPoolUsage;
            public UIntPtr quotaPeakNonPagedPoolUsage;
            public UIntPtr quotaNonPagedPoolUsage;
            public UIntPtr pagefileUsage;
            public UIntPtr peakPagefileUsage;
            public UIntPtr privateUsage;
        }

        static class ProcessMemoryReader
        {
            [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
            [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);

            internal static bool TryRead(out long privateBytes, out long workingSetBytes)
            {
                privateBytes = 0;
                workingSetBytes = 0;
                try
                {
                    var counters = new ProcessMemoryCountersEx { cb = (uint)Marshal.SizeOf(typeof(ProcessMemoryCountersEx)) };
                    if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.cb)) return false;
                    privateBytes = checked((long)counters.privateUsage.ToUInt64());
                    workingSetBytes = checked((long)counters.workingSetSize.ToUInt64());
                    return privateBytes > 0 && workingSetBytes > 0;
                }
                catch { privateBytes = 0; workingSetBytes = 0; return false; }
            }
        }

        [Serializable]
        sealed class StageRecord
        {
            public string schema = "viewweave-main-sdk-hdr-device-stage-v1";
            public string nonce;
            public string stageId;
            public string graphGuid;
            public string graphName;
            public long generation;
            public string sessionState;
            public bool workerReady;
            public bool profileValid;
            public string profile;
            public string layout;
            public int lastRenderedViewCount;
            public long quiltRevision;
            public long quiltRevisionBeforeActivation;
            public uint completed;
            public uint completedBeforeActivation;
            public uint presentAccepted;
            public uint presentAcceptedBeforeActivation;
            public bool subpixelStatusValid;
            public string graphicsApi;
            public string quality;
            public string viewRenderMode;
            public string antialiasing;
            public bool postProcessing;
            public bool referencePostProcessing;
            public bool sourcePostProcessing;
            public string antialiasingQuality;
            public bool referenceAllowHdr;
            public bool sourceAllowHdr;
            public string evidenceDirectory;
            public string targetDescription;
            public int historyCameraCount;
            public string historyAntialiasing;
            public string viewTextureDescriptor;
            public string resolveTextureDescriptor;
            public string quiltTextureDescriptor;
            public string unityVersion;
            public string urpPackageVersion;
            public string vfxPackageVersion;
            public string viewWeavePackageVersion;
            public string viewWeavePackageName;
            public string viewWeavePackagePath;
            public string bridgePackageName;
            public string bridgePackageVersion;
            public string bridgePackagePath;
            public string urpPackageName;
            public string urpPackagePath;
            public string quiltRendererSourceSha256;
            public string bridgeSessionSourceSha256;
            public string urpGuardSourceSha256;
            public string coreAssemblyPath;
            public string coreAssemblySha256;
            public string bridgeAssemblyPath;
            public string bridgeAssemblySha256;
            public string urpAssemblyPath;
            public string urpAssemblySha256;
            public bool deviceOptIn;
            public string graphicsDeviceName;
            public string graphicsDeviceVersion;
        }

        [Serializable]
        sealed class DeviceResponse
        {
            public string schema;
            public string nonce;
            public string stageId;
            public string graphGuid;
            public long generation;
            public string answer;
        }

        [Serializable]
        sealed class ResultRecord
        {
            public string schema = "viewweave-main-sdk-hdr-device-result-v1";
            public string graphGuid;
            public string graphName;
            public string verdict;
            public string failureCode;
            public string responseAnswer;
            public bool responseNonceMatched;
            public int sceneEffectCount;
            public int activeEffectCountAfterCleanup;
            public string sessionStateAfterCleanup;
            public int shutdownPendingAfterCleanup;
            public bool graphBytesUnchanged;
            public bool taaAndPostProcessingUnchanged;
            public bool historyBudgetUnchanged;
            public int historyCameraCountAfterCleanup;
            public bool viewTextureReleased;
            public bool resolveTextureReleased;
            public bool quiltTextureReleased;
            public long startupGraphicsBytes;
            public long startupPrivateBytes;
            public long prewarmGraphicsBytes;
            public long prewarmPrivateBytes;
            public long effectPeakGraphicsGrowthBytes;
            public long effectPeakPrivateGrowthBytes;
            public int actualRenderedViewCount;
            public string actualLayout;
            public string profile;
            public string nonce;
            public bool deviceOptIn;
        }

        struct CameraSettingsSnapshot
        {
            internal bool valid;
            internal bool allowHdr;
            internal bool allowMsaa;
            internal bool postProcessing;
            internal AntialiasingMode antialiasing;
            internal AntialiasingQuality antialiasingQuality;

            internal static CameraSettingsSnapshot Capture(Camera camera)
            {
                if (camera == null) return default;
                UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (data == null) return default;
                return new CameraSettingsSnapshot
                {
                    valid = true,
                    allowHdr = camera.allowHDR,
                    allowMsaa = camera.allowMSAA,
                    postProcessing = data.renderPostProcessing,
                    antialiasing = data.antialiasing,
                    antialiasingQuality = data.antialiasingQuality
                };
            }

            internal bool Matches(Camera camera)
            {
                CameraSettingsSnapshot other = Capture(camera);
                return valid == other.valid && (!valid ||
                    (allowHdr == other.allowHdr && allowMsaa == other.allowMsaa &&
                     postProcessing == other.postProcessing && antialiasing == other.antialiasing &&
                     antialiasingQuality == other.antialiasingQuality));
            }
        }

        sealed class ProbeState
        {
            internal string graphGuid;
            internal string stageId;
            internal string graphName;
            internal string evidenceDirectory;
            internal string phase = "start";
            internal string failureCode = "";
            internal string nonce;
            internal string responseAnswer = "";
            internal bool responseNonceMatched;
            internal string graphPath;
            internal string graphShaBefore;
            internal bool graphBytesUnchanged;
            internal int sceneEffectCount;
            internal int activeEffectCountAfterCleanup = -1;
            internal bool taaAndPostProcessingUnchanged;
            internal bool historyBudgetUnchanged;
            internal long startupGraphicsBytes;
            internal long startupPrivateBytes;
            internal long prewarmGraphicsBytes;
            internal long prewarmPrivateBytes;
            internal long graphicsBaseline;
            internal long privateBaseline;
            internal long workingSetBaseline;
            internal long graphicsPeakGrowth;
            internal long privatePeakGrowth;
            internal int actualRenderedViewCount;
            internal string actualLayout = "unavailable";
            internal string profile = "unavailable";
            internal string cleanupSessionState = "unavailable";
            internal int cleanupShutdownPending = -1;
            internal int cleanupHistoryCameraCount = -1;
            internal bool cleanupViewReleased;
            internal bool cleanupResolveReleased;
            internal bool cleanupQuiltReleased;
            internal string verdict = "pending";
            internal string testStartUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            internal int startedFrame;
            internal int endedFrame;
            internal long startRevision;
            internal uint startCompleted;
            internal uint startAccepted;
            internal long generation;
            internal int effectAliveParticles;
            internal bool subpixelStatusValid;
            internal int historyCameraCount;
            internal string historyAntialiasing = "unavailable";
            internal string viewTextureDescriptor = "unavailable";
            internal string resolveTextureDescriptor = "unavailable";
            internal string quiltTextureDescriptor = "unavailable";
            internal string targetDescription = "unavailable";
            internal string viewWeavePackageVersion = "unavailable";
            internal string viewWeavePackageName = "unavailable";
            internal string viewWeavePackagePath = "unavailable";
            internal string quiltRendererSourceSha256 = "unavailable";
            internal string bridgeSessionSourceSha256 = "unavailable";
            internal string urpGuardSourceSha256 = "unavailable";
            internal string urpPackageVersion = "unavailable";
            internal string vfxPackageVersion = "unavailable";
            internal string bridgePackageName = "unavailable";
            internal string bridgePackageVersion = "unavailable";
            internal string bridgePackagePath = "unavailable";
            internal string urpPackageName = "unavailable";
            internal string urpPackagePath = "unavailable";
            internal string coreAssemblyPath = "unavailable";
            internal string coreAssemblySha256 = "unavailable";
            internal string bridgeAssemblyPath = "unavailable";
            internal string bridgeAssemblySha256 = "unavailable";
            internal string urpAssemblyPath = "unavailable";
            internal string urpAssemblySha256 = "unavailable";
            internal bool deviceOptIn;
            internal Vector3 referencePositionBefore;
            internal Quaternion referenceRotationBefore;
            internal bool haveReferenceTransformBefore;
            internal long lastCompletedProgressTimeTicks;
            internal long lastAcceptedProgressTimeTicks;
            internal uint lastCompleted;
            internal uint lastAccepted;
            internal QuiltViewRenderMode viewRenderModeBefore;
        }

        static readonly string[] GreenAnswers = { "green-visible", "green-remains-red", "green-yellow-orange", "not-visible", "uncertain" };
        static readonly string[] CoinAnswers = { "coin-flash-visible", "coin-flash-absent", "not-visible", "uncertain" };

        ProbeState state;
        VisualEffect selectedEffect;
        VisualEffect[] sceneEffects = Array.Empty<VisualEffect>();
        ViewWeaveRig rig;
        QuiltRenderer quilt;
        UrpQuiltCamera guard;
        ViewWeaveBridgeSession session;
        BridgeWorkerQuiltOutput worker;
        Camera referenceCamera;
        Camera sourceCamera;
        CameraSettingsSnapshot referenceSettingsBefore;
        CameraSettingsSnapshot sourceSettingsBefore;
        int historyBudgetBefore;
        bool rigWasActive;
        bool createdWorker;
        bool createdSession;

        [UnityTest]
        public IEnumerator ObserveGreenVfxOnMainSdkAndPhysicalDisplay()
        {
            return ExecuteProbe(GreenGuid, "MultiStripGPUEvents", true);
        }

        [UnityTest]
        public IEnumerator ObserveCoinVfxOnMainSdkAndPhysicalDisplay()
        {
            return ExecuteProbe(CoinGuid, "RotationAngle", false);
        }

        IEnumerator ExecuteProbe(string graphGuid, string graphName, bool green)
        {
            state = new ProbeState { graphGuid = graphGuid, graphName = graphName, stageId = graphGuid == GreenGuid ? "green-multistrip" : "coin-rotation" };
            Exception failure = null;
            string phase = "setup";
            string failurePhase = "setup";
            IEnumerator core = RunProbeCore(green);
            var stack = new Stack<IEnumerator>();
            stack.Push(core);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                object current = null;
                try { moved = top.MoveNext(); if (moved) current = top.Current; }
                catch (Exception exception) { failure = exception; failurePhase = state.phase; break; }
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                phase = state.phase;
                WriteProgress(phase);
                yield return current;
            }

            state.phase = "cleanup-disable-effects";
            DeactivateEffects();
            if (session != null)
            {
                try { session.StopSession(); }
                catch (Exception exception)
                {
                    if (failure == null)
                    {
                        failure = exception;
                        failurePhase = state.phase;
                    }
                }
            }
            if (state.haveReferenceTransformBefore && referenceCamera != null)
            {
                referenceCamera.transform.position = state.referencePositionBefore;
                referenceCamera.transform.rotation = state.referenceRotationBefore;
            }
            state.phase = "cleanup-wait-for-shutdown";
            for (int frame = 0; frame < 900; ++frame)
            {
                yield return null;
                state.cleanupSessionState = session == null ? "unavailable" : session.State.ToString();
                state.cleanupShutdownPending = BridgeWorkerQuiltOutput.ShutdownPendingCount;
                state.cleanupHistoryCameraCount = guard == null ? -1 : guard.HistoryCameraCount;
                if (session == null || (session.State == ViewWeaveBridgeSession.SessionState.Idle && state.cleanupShutdownPending == 0)) break;
            }
            bool sessionStopped = session == null || (session.State == ViewWeaveBridgeSession.SessionState.Idle && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0);
            if (sessionStopped && quilt != null)
            {
                quilt.TrySetViewRenderMode(state.viewRenderModeBefore, out _);
                quilt.enabled = false;
            }
            state.phase = "cleanup-wait-for-core-resources";
            for (int frame = 0; frame < 900; ++frame)
            {
                yield return null;
                state.cleanupSessionState = session == null ? "unavailable" : session.State.ToString();
                state.cleanupShutdownPending = BridgeWorkerQuiltOutput.ShutdownPendingCount;
                state.cleanupHistoryCameraCount = guard == null ? -1 : guard.HistoryCameraCount;
                state.cleanupViewReleased = IsPrivateRenderTextureReleased(quilt, "viewTexture");
                state.cleanupResolveReleased = IsPrivateRenderTextureReleased(quilt, "resolveTexture");
                state.cleanupQuiltReleased = quilt == null || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated();
                if (state.cleanupHistoryCameraCount == 0 && state.cleanupViewReleased && state.cleanupResolveReleased && state.cleanupQuiltReleased) break;
            }
            state.activeEffectCountAfterCleanup = CountActiveEffects();
            state.graphBytesUnchanged = string.IsNullOrEmpty(state.graphPath) ||
                string.Equals(state.graphShaBefore, Sha256(AbsoluteAssetPath(state.graphPath)), StringComparison.OrdinalIgnoreCase);
            state.taaAndPostProcessingUnchanged =
                (referenceCamera == null || referenceSettingsBefore.Matches(referenceCamera)) &&
                (sourceCamera == null || sourceSettingsBefore.Matches(sourceCamera));
            state.historyBudgetUnchanged = guard == null || guard.historyMemoryBudgetMB == historyBudgetBefore;
            state.cleanupHistoryCameraCount = guard == null ? -1 : guard.HistoryCameraCount;
            state.cleanupViewReleased = IsPrivateRenderTextureReleased(quilt, "viewTexture");
            state.cleanupResolveReleased = IsPrivateRenderTextureReleased(quilt, "resolveTexture");
            state.cleanupQuiltReleased = quilt == null || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated();

            if (failure == null) failurePhase = state.phase;
            if (state.activeEffectCountAfterCleanup != 0 && failure == null) failure = new AssertionException("A scene VFX object remained active after cleanup.");
            if (session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle && failure == null)
                failure = new AssertionException("Bridge Session did not reach Idle after StopSession.");
            if (BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 && failure == null)
                failure = new AssertionException("Bridge worker shutdown remained pending after cleanup.");
            if (!state.graphBytesUnchanged && failure == null) failure = new AssertionException("Selected original VFX graph bytes changed during the probe.");
            if (!state.taaAndPostProcessingUnchanged && failure == null) failure = new AssertionException("Camera TAA or post-processing settings changed during the probe.");
            if (!state.historyBudgetUnchanged && failure == null) failure = new AssertionException("URP history resource budget changed during the probe.");
            if (state.cleanupHistoryCameraCount != 0 && failure == null) failure = new AssertionException("URP history cameras remained after Session shutdown.");
            if (!state.cleanupViewReleased && failure == null) failure = new AssertionException("Core view target remained allocated after Session shutdown.");
            if (!state.cleanupResolveReleased && failure == null) failure = new AssertionException("Core resolve target remained allocated after Session shutdown.");
            if (!state.cleanupQuiltReleased && failure == null) failure = new AssertionException("Core quilt target remained allocated after Session shutdown.");

            DetachAndDestroyRuntimeComponents();
            if (failure == null && state.verdict == "optical-mismatch")
                failure = new AssertionException("Physical output did not match the expected visible result: " + state.responseAnswer + ".");
            if (failure == null && state.verdict == "optical-inconclusive")
                failure = new InconclusiveException("Physical output response was uncertain.");
            WriteResult(failure, failurePhase);
            if (failure != null) throw failure;
            Assert.AreEqual("accepted", state.verdict, "The physical display response was not accepted.");
        }

        IEnumerator RunProbeCore(bool green)
        {
            state.phase = "require-explicit-device-opt-in";
            Assert.AreEqual("1", Environment.GetEnvironmentVariable(DeviceOptInEnvironment),
                DeviceOptInEnvironment + "=1 is required before the physical-device test can start.");
            state.deviceOptIn = true;
            state.phase = "validate-evidence-directory";
            string evidenceRoot = Environment.GetEnvironmentVariable(EvidenceEnvironment);
            if (string.IsNullOrWhiteSpace(evidenceRoot)) Assert.Fail(EvidenceEnvironment + " was not provided by the safe runner.");
            state.evidenceDirectory = Path.GetFullPath(evidenceRoot);
            string projectRoot = Directory.GetParent(Path.GetFullPath(Application.dataPath)).FullName;
            string allowedRoot = Path.GetFullPath(Path.Combine(projectRoot, "TestResults")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(state.evidenceDirectory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase), "Evidence output must remain below project TestResults.");
            Directory.CreateDirectory(state.evidenceDirectory);
            foreach (string name in new[] { "progress.json", "stageReady.json", "device-response.json", "samples.csv", "result.json" })
                Assert.IsFalse(File.Exists(Path.Combine(state.evidenceDirectory, name)), "Refusing to overwrite existing device evidence: " + name);

            ReadAbsoluteMemory(out state.startupGraphicsBytes, out state.startupPrivateBytes);
            state.phase = "load-prepared-scene";
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float loadDeadline = Time.realtimeSinceStartup + 60f;
            while ((!scene.IsValid() || !scene.isLoaded) && Time.realtimeSinceStartup < loadDeadline) yield return null;
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "Prepared URP scene did not load.");
            Assert.IsTrue(Application.isPlaying, "Physical output observation must run in Play Mode.");
            Assert.AreEqual(RuntimePlatform.WindowsEditor, Application.platform, "Expected Windows Editor Play Mode.");
            Assert.AreEqual(GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType, "Expected the incident-matching D3D12 Unity graphics API.");
            Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline, "Expected the prepared URP pipeline.");

            sceneEffects = UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded).ToArray();
            state.sceneEffectCount = sceneEffects.Length;
            Assert.AreEqual(ExpectedSceneEffectCount, sceneEffects.Length, "Expected all 33 prepared scene VisualEffect components.");
            Assert.AreEqual(0, CountActiveEffects(), "All copied VFX components must be inactive before output setup.");
            string graphPath = AssetDatabase.GUIDToAssetPath(state.graphGuid);
            Assert.IsFalse(string.IsNullOrEmpty(graphPath), "Selected VFX graph GUID is missing from the clone.");
            state.graphPath = graphPath;
            state.graphShaBefore = Sha256(AbsoluteAssetPath(graphPath));
            VisualEffect[] matches = sceneEffects.Where(value => value.visualEffectAsset != null &&
                string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value.visualEffectAsset)), state.graphGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, matches.Length, "Expected exactly one copied scene effect with the selected graph GUID.");
            selectedEffect = matches[0];
            Assert.IsFalse(selectedEffect.gameObject.activeInHierarchy, "Selected VFX must be inactive before bridge warmup.");
            Assert.IsTrue(selectedEffect.enabled, "Selected VisualEffect component must remain enabled.");
            Assert.IsFalse(selectedEffect.pause, "Selected VisualEffect must not be paused.");
            Assert.Greater(selectedEffect.playRate, 0f, "Selected VisualEffect must retain a positive play rate.");
            Assert.AreEqual("OnPlay", selectedEffect.initialEventName, "Selected original graph must retain natural OnPlay activation.");

            state.phase = "resolve-rig-and-preserve-settings";
            rig = Resources.FindObjectsOfTypeAll<ViewWeaveRig>().Single(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded);
            quilt = rig.GetComponent<QuiltRenderer>();
            Assert.IsNotNull(quilt);
            sourceCamera = quilt.sourceCamera;
            Assert.IsNotNull(sourceCamera);
            Assert.AreSame(rig.GetComponent<Camera>(), sourceCamera, "The prepared source camera must be the Rig camera before registering its guard.");
            guard = quilt.sourceCamera == null ? null : quilt.sourceCamera.GetComponent<UrpQuiltCamera>();
            Assert.IsNotNull(guard, "Prepared scene is missing the URP quilt history guard.");
            Assert.AreSame(sourceCamera, guard.GetComponent<Camera>(), "The selected URP guard must belong to the Rig source camera.");
            rig.SetGuard(guard);
            Assert.AreSame(guard, quilt.GuardBehaviour, "The URP guard was not assigned to the QuiltRenderer before Bridge startup.");
            Assert.AreEqual(UrpQuiltRenderingMode.IsolatedViewHistories, guard.renderingMode, "Expected isolated URP histories.");
            historyBudgetBefore = guard.historyMemoryBudgetMB;
            Assert.AreEqual(4096, historyBudgetBefore, "Expected the incident's unchanged 4096 MiB SDK guard budget.");
            Assert.IsFalse(rig.AutoRender, "Rig must remain stopped until the Bridge Session owns it.");
            Assert.IsFalse(quilt.autoRender, "Quilt autorender must remain stopped until the Bridge Session owns it.");
            referenceCamera = quilt.referenceCamera;
            Assert.IsNotNull(referenceCamera);
            Assert.IsNotNull(sourceCamera);
            referenceSettingsBefore = CameraSettingsSnapshot.Capture(referenceCamera);
            sourceSettingsBefore = CameraSettingsSnapshot.Capture(sourceCamera);
            state.referencePositionBefore = referenceCamera.transform.position;
            state.referenceRotationBefore = referenceCamera.transform.rotation;
            state.haveReferenceTransformBefore = true;
            Assert.IsTrue(referenceSettingsBefore.valid && sourceSettingsBefore.valid, "Source/reference camera URP settings must be available.");
            string initialTaa = referenceSettingsBefore.antialiasing.ToString();
            string initialSourceTaa = sourceSettingsBefore.antialiasing.ToString();
            state.viewRenderModeBefore = quilt.viewRenderMode;
            Assert.IsTrue(quilt.TrySetViewRenderMode(QuiltViewRenderMode.Quarter, out string modeError), "Could not select Quarter output: " + modeError);
            Assert.AreEqual(QuiltViewRenderMode.Quarter, quilt.viewRenderMode);

            Vector3 viewTarget = selectedEffect.transform.position + Vector3.up * (green ? 1f : 1.5f);
            float cameraDistance = green ? 3f : 4f;
            Vector3 cameraForward = referenceCamera.transform.forward.normalized;
            if (cameraForward.sqrMagnitude < 0.5f) cameraForward = Vector3.forward;
            referenceCamera.transform.position = viewTarget - cameraForward * cameraDistance;
            referenceCamera.transform.LookAt(viewTarget);
            state.targetDescription = "target=" + FormatVector(viewTarget) + ";distance=" + cameraDistance.ToString("F2", CultureInfo.InvariantCulture);
            CapturePackageProvenance();

            state.phase = "prewarm-with-all-vfx-disabled";
            rigWasActive = rig.gameObject.activeSelf;
            if (rigWasActive) rig.gameObject.SetActive(false);
            worker = rig.gameObject.AddComponent<BridgeWorkerQuiltOutput>();
            createdWorker = true;
            worker.initializeOnEnable = false;
            worker.enabled = true;
            session = rig.gameObject.AddComponent<ViewWeaveBridgeSession>();
            createdSession = true;
            session.startOnEnable = false;
            session.applicationName = Application.productName;
            session.displayOrdinal = 0;
            if (rigWasActive) rig.gameObject.SetActive(true);
            Assert.IsTrue(session.SetQuality(QuiltQualityPreset.Preview), "Could not select Preview output quality: " + Ascii(session.LastError));
            Assert.IsTrue(session.StartSession(), "Could not start Bridge Session: " + Ascii(session.LastError));
            float connectDeadline = Time.realtimeSinceStartup + 60f;
            while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && Time.realtimeSinceStartup < connectDeadline)
            {
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault) Assert.Fail("Bridge Session faulted during connect: " + Ascii(session.LastError));
                yield return null;
            }
            Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Rendering, session.State, "Bridge Session did not reach Rendering.");
            Assert.IsTrue(worker.Ready, "Bridge worker did not become Ready.");
            Assert.IsTrue(session.Profile.IsValid, "The connected Bridge display profile is invalid.");
            Assert.IsTrue(worker.SubpixelStatusValid, "Native subpixel present/completion status is unavailable.");
            long warmRevision = quilt.QuiltRevision;
            uint warmCompleted = worker.Completed;
            uint warmAccepted = worker.SubpixelPresentAccepted;
            float warmDeadline = Time.realtimeSinceStartup + 60f;
            while ((quilt.QuiltRevision < warmRevision + 8 || worker.Completed <= warmCompleted || worker.SubpixelPresentAccepted <= warmAccepted) &&
                Time.realtimeSinceStartup < warmDeadline)
            {
                AssertSessionReady();
                yield return null;
            }
            Assert.GreaterOrEqual(quilt.QuiltRevision, warmRevision + 8, "All-VFX-off Bridge quilt prewarm did not render 8 revisions.");
            Assert.Greater(worker.Completed, warmCompleted, "Native completion counter did not advance during all-VFX-off prewarm.");
            Assert.Greater(worker.SubpixelPresentAccepted, warmAccepted, "Native present-accepted counter did not advance during all-VFX-off prewarm.");
            Assert.AreEqual(QuiltViewRenderMode.Quarter, quilt.viewRenderMode, "Bridge setup changed Quarter output selection.");
            Assert.IsTrue(referenceSettingsBefore.Matches(referenceCamera), "Bridge setup changed reference camera TAA/HDR/MSAA/post-processing settings.");
            Assert.IsTrue(sourceSettingsBefore.Matches(sourceCamera), "Bridge setup changed source camera TAA/HDR/MSAA/post-processing settings.");
            Assert.AreEqual(historyBudgetBefore, guard.historyMemoryBudgetMB, "Bridge setup changed the URP history budget.");
            Assert.IsTrue(ReadMemory(out state.graphicsBaseline, out state.privateBaseline, out state.workingSetBaseline), "Post-prewarm memory baseline is unavailable.");
            state.prewarmGraphicsBytes = state.graphicsBaseline;
            state.prewarmPrivateBytes = state.privateBaseline;
            state.startRevision = quilt.QuiltRevision;
            state.startCompleted = worker.Completed;
            state.startAccepted = worker.SubpixelPresentAccepted;
            state.generation = worker.SessionGeneration;
            Assert.Greater(state.generation, 0);
            state.lastCompleted = worker.Completed;
            state.lastAccepted = worker.SubpixelPresentAccepted;
            state.lastCompletedProgressTimeTicks = CurrentRealtimeTicks();
            state.lastAcceptedProgressTimeTicks = CurrentRealtimeTicks();
            WriteSamplesHeader();

            state.phase = "activate-selected-original-ongraph";
            int startFrame = Time.frameCount;
            float startTime = Time.realtimeSinceStartup;
            long startRevision = quilt.QuiltRevision;
            state.startedFrame = startFrame;
            selectedEffect.gameObject.SetActive(true);
            Assert.AreEqual(1, CountActiveEffects(), "Exactly one selected VFX must be active after natural activation.");
            float stageMinimumSeconds = green ? 12f : 6f;
            bool stageWritten = false;
            float lastSampleWrite = 0f;
            while (Time.frameCount - startFrame < MaximumRunFrames)
            {
                AssertHealthyDuringRender();
                TrackCounterProgress();
                if (!ReadGrowthSafety())
                {
                    state.failureCode = "observed-growth-stop-512MiB";
                    Assert.Fail("The selected-effect 512 MiB growth stop was reached. Raw delta=" + state.graphicsPeakGrowth.ToString(CultureInfo.InvariantCulture) + ", private delta=" + state.privatePeakGrowth.ToString(CultureInfo.InvariantCulture) + ".");
                }
                state.effectAliveParticles = SafeAliveCount(selectedEffect);
                float elapsed = Time.realtimeSinceStartup - startTime;
                if (Time.realtimeSinceStartup - lastSampleWrite >= 1f)
                {
                    AppendSample(elapsed);
                    lastSampleWrite = Time.realtimeSinceStartup;
                    WriteProgress("selected-graph-running");
                }
                if (!stageWritten && elapsed >= stageMinimumSeconds && quilt.QuiltRevision > startRevision &&
                    quilt.LastRenderedViewCount > 0 && worker.Completed > state.startCompleted &&
                    worker.SubpixelPresentAccepted > state.startAccepted)
                {
                    StageRecord stage = CreateStageRecord(initialTaa, initialSourceTaa);
                    WriteStageReady(stage);
                    stageWritten = true;
                    state.phase = "await-optical-acknowledgement";
                    WriteProgress(state.phase);
                }
                if (stageWritten && TryReadResponse(green))
                {
                    string expectedAnswer = green ? "green-visible" : "coin-flash-visible";
                    state.verdict = state.responseAnswer == expectedAnswer
                        ? "accepted"
                        : state.responseAnswer == "uncertain" ? "optical-inconclusive" : "optical-mismatch";
                    break;
                }
                if (stageWritten && Time.realtimeSinceStartup - startTime > stageMinimumSeconds + 240f)
                {
                    state.failureCode = "device-response-timeout-240s";
                    Assert.Inconclusive("No nonce-matched physical display response arrived within 240 seconds.");
                }
                yield return null;
            }
            if (!stageWritten) Assert.Fail("A physically acknowledged-ready stage could not be reached before the bounded run frame limit.");
            if (state.verdict != "accepted") Assert.Fail("The optical response was not accepted.");
            state.endedFrame = Time.frameCount;
            state.graphBytesUnchanged = string.Equals(state.graphShaBefore, Sha256(AbsoluteAssetPath(state.graphPath)), StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(state.graphBytesUnchanged, "Selected original graph bytes changed during the device probe.");
        }

        void AssertHealthyDuringRender()
        {
            AssertSessionReady();
            Assert.AreEqual(state.generation, worker.SessionGeneration, "Bridge worker generation changed during the observation.");
            Assert.IsTrue(worker.SubpixelStatusValid, "Native subpixel status became invalid.");
        }

        void AssertSessionReady()
        {
            Assert.IsNotNull(session);
            Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Rendering, session.State, "Bridge Session left Rendering.");
            Assert.IsTrue(worker != null && worker.Ready, "Bridge worker left Ready.");
            Assert.IsTrue(session.Profile.IsValid, "Connected display profile became invalid.");
        }

        void TrackCounterProgress()
        {
            long nowTicks = CurrentRealtimeTicks();
            if (worker.Completed != state.lastCompleted)
            {
                state.lastCompleted = worker.Completed;
                state.lastCompletedProgressTimeTicks = nowTicks;
            }
            if (worker.SubpixelPresentAccepted != state.lastAccepted)
            {
                state.lastAccepted = worker.SubpixelPresentAccepted;
                state.lastAcceptedProgressTimeTicks = nowTicks;
            }
            long progressTimeout = 30L * TimeSpan.TicksPerSecond;
            Assert.Less(nowTicks - state.lastCompletedProgressTimeTicks, progressTimeout, "Native completion counter did not make progress for 30 seconds.");
            Assert.Less(nowTicks - state.lastAcceptedProgressTimeTicks, progressTimeout, "Native accepted-present counter did not make progress for 30 seconds.");
        }

        bool ReadGrowthSafety()
        {
            if (!ReadMemory(out long graphics, out long privateBytes, out long workingSet)) return false;
            long graphicsDelta = Math.Max(0, graphics - state.graphicsBaseline);
            long privateDelta = Math.Max(0, privateBytes - state.privateBaseline);
            state.graphicsPeakGrowth = Math.Max(state.graphicsPeakGrowth, graphicsDelta);
            state.privatePeakGrowth = Math.Max(state.privatePeakGrowth, privateDelta);
            return state.graphicsPeakGrowth < GrowthStopBytes && state.privatePeakGrowth < GrowthStopBytes && workingSet > 0;
        }

        StageRecord CreateStageRecord(string referenceTaa, string sourceTaa)
        {
            BridgeDisplayProfile profile = session.Profile;
            QuiltLayout layout = worker.Layout;
            Assert.IsTrue(profile.IsValid, "Stage requires a valid display profile.");
            Assert.IsTrue(worker.Ready && session.State == ViewWeaveBridgeSession.SessionState.Rendering, "Stage requires a Ready Rendering session.");
            Assert.AreEqual(state.generation, worker.SessionGeneration, "Stage worker generation changed.");
            Assert.Greater(worker.Completed, state.startCompleted, "Stage requires native rendering completion after activation.");
            Assert.Greater(worker.SubpixelPresentAccepted, state.startAccepted, "Stage requires an accepted native physical-output submission after activation.");
            Assert.Greater(quilt.QuiltRevision, state.startRevision, "Stage requires a new quilt revision after activation.");
            Assert.Greater(quilt.LastRenderedViewCount, 0, "Stage requires actual rendered quilt views.");
            Assert.Greater(layout.ViewCount, 0, "Stage requires the actual output layout.");
            Assert.AreEqual(QuiltViewRenderMode.Quarter, quilt.viewRenderMode, "Stage requires the explicit Quarter output mode.");
            Assert.AreEqual(QuiltQualityPreset.Preview, session.Quality, "Stage requires Preview output quality.");
            Assert.IsTrue(referenceSettingsBefore.Matches(referenceCamera), "Stage detected changed reference TAA/HDR/MSAA/post-processing settings.");
            Assert.IsTrue(sourceSettingsBefore.Matches(sourceCamera), "Stage detected changed source TAA/HDR/MSAA/post-processing settings.");
            Assert.AreEqual(historyBudgetBefore, guard.historyMemoryBudgetMB, "Stage detected a changed URP history budget.");
            state.historyCameraCount = guard.HistoryCameraCount;
            Camera historyCamera = guard.GetHistoryCamera(0);
            UniversalAdditionalCameraData historyData = historyCamera == null ? null : historyCamera.GetComponent<UniversalAdditionalCameraData>();
            Assert.AreEqual(quilt.LastRenderedViewCount, guard.HistoryCameraCount, "History camera count does not match actual rendered views.");
            Assert.IsNotNull(historyData, "URP history camera additional settings are unavailable.");
            UniversalAdditionalCameraData referenceData = referenceCamera.GetComponent<UniversalAdditionalCameraData>();
            Assert.AreEqual(referenceData.antialiasing, historyData.antialiasing, "History TAA mode differs from the original reference camera.");
            Assert.AreEqual(referenceData.antialiasingQuality, historyData.antialiasingQuality, "History TAA quality differs from the original reference camera.");
            Assert.AreEqual(referenceData.renderPostProcessing, historyData.renderPostProcessing, "History post-processing state differs from the original reference camera.");
            Assert.AreEqual(referenceCamera.allowHDR, historyCamera.allowHDR, "History HDR setting differs from the reference camera.");
            Assert.AreEqual(referenceCamera.allowMSAA, historyCamera.allowMSAA, "History MSAA setting differs from the reference camera.");
            state.historyAntialiasing = "mode=" + historyData.antialiasing.ToString() + ";postProcessing=" + historyData.renderPostProcessing.ToString() + ";allowHDR=" + historyCamera.allowHDR.ToString();
            RenderTexture viewTexture = GetPrivateRenderTexture(quilt, "viewTexture");
            RenderTexture resolveTexture = GetPrivateRenderTexture(quilt, "resolveTexture");
            RenderTexture quiltTexture = quilt.QuiltTexture;
            Assert.IsNotNull(viewTexture, "Core per-view target is unavailable at stage time.");
            Assert.IsNotNull(resolveTexture, "Core resolve target is unavailable at stage time.");
            Assert.IsNotNull(quiltTexture, "Final quilt target is unavailable at stage time.");
            Assert.AreEqual(GraphicsFormat.R16G16B16A16_SFloat, viewTexture.graphicsFormat, "Core view target is not a 16-bit floating-point HDR texture.");
            Assert.IsFalse(viewTexture.sRGB, "Core view target must be linear.");
            Assert.AreEqual(GraphicsFormat.R8G8B8A8_SRGB, resolveTexture.graphicsFormat, "Core resolve target is not RGBA8 sRGB.");
            Assert.IsTrue(resolveTexture.sRGB, "Core resolve target must be sRGB.");
            Assert.AreEqual(1, resolveTexture.antiAliasing, "Core resolve target must be single-sample.");
            Assert.AreEqual(GraphicsFormat.R8G8B8A8_SRGB, quiltTexture.graphicsFormat, "Final quilt target is not RGBA8 sRGB.");
            Assert.IsTrue(quiltTexture.sRGB, "Final quilt target must be sRGB.");
            state.viewTextureDescriptor = DescribeTexture(viewTexture);
            state.resolveTextureDescriptor = DescribeTexture(resolveTexture);
            state.quiltTextureDescriptor = DescribeTexture(quiltTexture);
            state.nonce = Guid.NewGuid().ToString("N");
            state.actualRenderedViewCount = quilt.LastRenderedViewCount;
            state.actualLayout = layout.ToString();
            state.profile = profile.ToString();
            state.subpixelStatusValid = worker.SubpixelStatusValid;
            return new StageRecord
            {
                nonce = state.nonce,
                stageId = state.stageId,
                graphGuid = state.graphGuid,
                graphName = state.graphName,
                generation = worker.SessionGeneration,
                sessionState = session.State.ToString(),
                workerReady = worker.Ready,
                profileValid = profile.IsValid,
                profile = profile.ToString(),
                layout = layout.ToString(),
                lastRenderedViewCount = quilt.LastRenderedViewCount,
                quiltRevision = quilt.QuiltRevision,
                quiltRevisionBeforeActivation = state.startRevision,
                completed = worker.Completed,
                completedBeforeActivation = state.startCompleted,
                presentAccepted = worker.SubpixelPresentAccepted,
                presentAcceptedBeforeActivation = state.startAccepted,
                subpixelStatusValid = worker.SubpixelStatusValid,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                quality = session.Quality.ToString(),
                viewRenderMode = quilt.viewRenderMode.ToString(),
                antialiasing = "reference=" + referenceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing.ToString() + ";source=" + sourceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing.ToString() + ";saved-reference=" + referenceTaa + ";saved-source=" + sourceTaa,
                postProcessing = referenceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing && sourceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing,
                referencePostProcessing = referenceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing,
                sourcePostProcessing = sourceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing,
                antialiasingQuality = "reference=" + referenceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasingQuality.ToString() + ";source=" + sourceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasingQuality.ToString(),
                referenceAllowHdr = referenceCamera.allowHDR,
                sourceAllowHdr = sourceCamera.allowHDR,
                evidenceDirectory = state.evidenceDirectory,
                targetDescription = state.targetDescription,
                historyCameraCount = state.historyCameraCount,
                historyAntialiasing = state.historyAntialiasing,
                viewTextureDescriptor = state.viewTextureDescriptor,
                resolveTextureDescriptor = state.resolveTextureDescriptor,
                quiltTextureDescriptor = state.quiltTextureDescriptor,
                unityVersion = Application.unityVersion,
                urpPackageVersion = state.urpPackageVersion,
                vfxPackageVersion = state.vfxPackageVersion,
                viewWeavePackageVersion = state.viewWeavePackageVersion,
                viewWeavePackageName = state.viewWeavePackageName,
                viewWeavePackagePath = state.viewWeavePackagePath,
                quiltRendererSourceSha256 = state.quiltRendererSourceSha256,
                bridgeSessionSourceSha256 = state.bridgeSessionSourceSha256,
                urpGuardSourceSha256 = state.urpGuardSourceSha256,
                bridgePackageName = state.bridgePackageName,
                bridgePackageVersion = state.bridgePackageVersion,
                bridgePackagePath = state.bridgePackagePath,
                urpPackageName = state.urpPackageName,
                urpPackagePath = state.urpPackagePath,
                coreAssemblyPath = state.coreAssemblyPath,
                coreAssemblySha256 = state.coreAssemblySha256,
                bridgeAssemblyPath = state.bridgeAssemblyPath,
                bridgeAssemblySha256 = state.bridgeAssemblySha256,
                urpAssemblyPath = state.urpAssemblyPath,
                urpAssemblySha256 = state.urpAssemblySha256,
                deviceOptIn = state.deviceOptIn,
                graphicsDeviceName = Ascii(SystemInfo.graphicsDeviceName),
                graphicsDeviceVersion = Ascii(SystemInfo.graphicsDeviceVersion)
            };
        }

        bool TryReadResponse(bool green)
        {
            string path = Path.Combine(state.evidenceDirectory, "device-response.json");
            if (!File.Exists(path)) return false;
            string json = File.ReadAllText(path, Encoding.UTF8);
            DeviceResponse response = JsonUtility.FromJson<DeviceResponse>(json);
            Assert.IsNotNull(response, "Device response JSON is invalid.");
            Assert.AreEqual("viewweave-main-sdk-hdr-device-response-v1", response.schema, "Device response schema mismatch.");
            Assert.AreEqual(state.nonce, response.nonce, "Device response nonce mismatch.");
            Assert.AreEqual(state.stageId, response.stageId, "Device response stage ID mismatch.");
            Assert.AreEqual(state.graphGuid, response.graphGuid, "Device response graph GUID mismatch.");
            Assert.AreEqual(state.generation, response.generation, "Device response worker generation mismatch.");
            string[] allowed = green ? GreenAnswers : CoinAnswers;
            Assert.IsTrue(allowed.Contains(response.answer), "Device response answer is not in the allowed set.");
            state.responseAnswer = response.answer;
            state.responseNonceMatched = true;
            return true;
        }

        void WriteStageReady(StageRecord stage)
        {
            string path = Path.Combine(state.evidenceDirectory, "stageReady.json");
            string temporary = path + ".tmp";
            WriteNewAsciiFile(temporary, JsonUtility.ToJson(stage, true));
            File.Move(temporary, path);
            WriteProgress("stage-ready");
        }

        void WriteProgress(string phase)
        {
            if (state == null || string.IsNullOrEmpty(state.evidenceDirectory)) return;
            string path = Path.Combine(state.evidenceDirectory, "progress.json");
            string text = "{\"schema\":\"viewweave-main-sdk-hdr-device-progress-v1\",\"graphGuid\":\"" + state.graphGuid + "\",\"phase\":\"" + Ascii(phase) + "\",\"frame\":" + Time.frameCount.ToString(CultureInfo.InvariantCulture) + ",\"generation\":" + state.generation.ToString(CultureInfo.InvariantCulture) + "}";
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        void WriteSamplesHeader()
        {
            string path = Path.Combine(state.evidenceDirectory, "samples.csv");
            File.WriteAllText(path, "elapsed_seconds,frame,quilt_revision,alive_particles,graphics_growth_bytes,private_growth_bytes,completed,present_accepted,generation\n", Encoding.ASCII);
        }

        void AppendSample(float elapsed)
        {
            ReadMemory(out long graphics, out long privateBytes, out _);
            string row = elapsed.ToString("F3", CultureInfo.InvariantCulture) + "," + Time.frameCount.ToString(CultureInfo.InvariantCulture) + "," +
                quilt.QuiltRevision.ToString(CultureInfo.InvariantCulture) + "," + state.effectAliveParticles.ToString(CultureInfo.InvariantCulture) + "," +
                Math.Max(0, graphics - state.graphicsBaseline).ToString(CultureInfo.InvariantCulture) + "," +
                Math.Max(0, privateBytes - state.privateBaseline).ToString(CultureInfo.InvariantCulture) + "," +
                worker.Completed.ToString(CultureInfo.InvariantCulture) + "," + worker.SubpixelPresentAccepted.ToString(CultureInfo.InvariantCulture) + "," +
                worker.SessionGeneration.ToString(CultureInfo.InvariantCulture) + "\n";
            File.AppendAllText(Path.Combine(state.evidenceDirectory, "samples.csv"), row, Encoding.ASCII);
        }

        void WriteResult(Exception failure, string failurePhase)
        {
            if (string.IsNullOrEmpty(state.evidenceDirectory)) return;
            var record = new ResultRecord
            {
                graphGuid = state.graphGuid,
                graphName = state.graphName,
                verdict = failure == null ? state.verdict : "failed-or-inconclusive",
                failureCode = state.failureCode + (failure == null ? "" : (string.IsNullOrEmpty(state.failureCode) ? "" : ";") + "phase=" + Ascii(failurePhase) + ";type=" + failure.GetType().Name),
                responseAnswer = state.responseAnswer,
                responseNonceMatched = state.responseNonceMatched,
                sceneEffectCount = state.sceneEffectCount,
                activeEffectCountAfterCleanup = state.activeEffectCountAfterCleanup,
                sessionStateAfterCleanup = state.cleanupSessionState,
                shutdownPendingAfterCleanup = state.cleanupShutdownPending,
                historyCameraCountAfterCleanup = state.cleanupHistoryCameraCount,
                viewTextureReleased = state.cleanupViewReleased,
                resolveTextureReleased = state.cleanupResolveReleased,
                quiltTextureReleased = state.cleanupQuiltReleased,
                graphBytesUnchanged = state.graphBytesUnchanged,
                taaAndPostProcessingUnchanged = state.taaAndPostProcessingUnchanged,
                historyBudgetUnchanged = state.historyBudgetUnchanged,
                startupGraphicsBytes = state.startupGraphicsBytes,
                startupPrivateBytes = state.startupPrivateBytes,
                prewarmGraphicsBytes = state.prewarmGraphicsBytes,
                prewarmPrivateBytes = state.prewarmPrivateBytes,
                effectPeakGraphicsGrowthBytes = state.graphicsPeakGrowth,
                effectPeakPrivateGrowthBytes = state.privatePeakGrowth,
                actualRenderedViewCount = state.actualRenderedViewCount,
                actualLayout = state.actualLayout,
                profile = state.profile,
                nonce = state.nonce,
                deviceOptIn = state.deviceOptIn
            };
            File.WriteAllText(Path.Combine(state.evidenceDirectory, "result.json"), JsonUtility.ToJson(record, true), new UTF8Encoding(false));
        }

        void DeactivateEffects()
        {
            if (sceneEffects == null) return;
            foreach (VisualEffect effect in sceneEffects)
                if (effect != null && effect.gameObject.activeSelf) effect.gameObject.SetActive(false);
        }

        void DetachAndDestroyRuntimeComponents()
        {
            bool clean = session != null && session.State == ViewWeaveBridgeSession.SessionState.Idle &&
                BridgeWorkerQuiltOutput.ShutdownPendingCount == 0 && state.cleanupHistoryCameraCount == 0 &&
                state.cleanupViewReleased && state.cleanupResolveReleased && state.cleanupQuiltReleased;
            if (!clean) return;
            try { rig.SetOutput(null); }
            catch { }
            if (createdSession && session != null) UnityEngine.Object.Destroy(session);
            if (createdWorker && worker != null) UnityEngine.Object.Destroy(worker);
        }

        static RenderTexture GetPrivateRenderTexture(QuiltRenderer renderer, string fieldName)
        {
            if (renderer == null) return null;
            FieldInfo field = typeof(QuiltRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(renderer) as RenderTexture;
        }

        static bool IsPrivateRenderTextureReleased(QuiltRenderer renderer, string fieldName)
        {
            RenderTexture texture = GetPrivateRenderTexture(renderer, fieldName);
            return texture == null || !texture.IsCreated();
        }

        static string DescribeTexture(RenderTexture texture)
        {
            if (texture == null) return "null";
            return texture.width.ToString(CultureInfo.InvariantCulture) + "x" + texture.height.ToString(CultureInfo.InvariantCulture) +
                ";format=" + texture.graphicsFormat.ToString() + ";srgb=" + texture.sRGB.ToString() +
                ";msaa=" + texture.antiAliasing.ToString(CultureInfo.InvariantCulture) + ";dimension=" + texture.dimension.ToString() +
                ";volumeDepth=" + texture.volumeDepth.ToString(CultureInfo.InvariantCulture) + ";created=" + texture.IsCreated().ToString();
        }

        void CapturePackageProvenance()
        {
            Assembly coreAssembly = typeof(QuiltRenderer).Assembly;
            Assembly bridgeAssembly = typeof(BridgeWorkerQuiltOutput).Assembly;
            Assembly urpAssembly = typeof(UrpQuiltCamera).Assembly;
            UnityEditor.PackageManager.PackageInfo coreInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(coreAssembly);
            UnityEditor.PackageManager.PackageInfo bridgeInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(bridgeAssembly);
            UnityEditor.PackageManager.PackageInfo urpInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(urpAssembly);
            UnityEditor.PackageManager.PackageInfo vfxInfo = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.unity.visualeffectgraph");
            AssertPackageInfo(coreInfo, "ViewWeave Core");
            AssertPackageInfo(bridgeInfo, "ViewWeave Bridge");
            AssertPackageInfo(urpInfo, "ViewWeave URP guard");
            Assert.IsNotNull(vfxInfo, "Unity VFX Graph PackageInfo is unavailable.");

            state.viewWeavePackageVersion = coreInfo.version ?? "embedded-or-local";
            state.viewWeavePackageName = coreInfo.name ?? "unavailable";
            state.viewWeavePackagePath = Ascii(coreInfo.resolvedPath);
            state.bridgePackageName = bridgeInfo.name ?? "unavailable";
            state.bridgePackageVersion = bridgeInfo.version ?? "embedded-or-local";
            state.bridgePackagePath = Ascii(bridgeInfo.resolvedPath);
            state.urpPackageName = urpInfo.name ?? "unavailable";
            state.urpPackageVersion = urpInfo.version ?? "embedded-or-local";
            state.urpPackagePath = Ascii(urpInfo.resolvedPath);
            state.vfxPackageVersion = vfxInfo.version ?? "unavailable";

            state.quiltRendererSourceSha256 = RequirePackageFileHash(coreInfo.resolvedPath, "Runtime/QuiltRenderer.cs");
            state.bridgeSessionSourceSha256 = RequirePackageFileHash(bridgeInfo.resolvedPath, "Runtime/Platforms/Windows/ViewWeaveBridgeSession.cs");
            state.urpGuardSourceSha256 = RequirePackageFileHash(urpInfo.resolvedPath, "Runtime/UrpQuiltCamera.cs");
            CaptureAssemblyHash(coreAssembly, out state.coreAssemblyPath, out state.coreAssemblySha256);
            CaptureAssemblyHash(bridgeAssembly, out state.bridgeAssemblyPath, out state.bridgeAssemblySha256);
            CaptureAssemblyHash(urpAssembly, out state.urpAssemblyPath, out state.urpAssemblySha256);
        }

        static void AssertPackageInfo(UnityEditor.PackageManager.PackageInfo info, string label)
        {
            Assert.IsNotNull(info, label + " PackageInfo is unavailable.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.name), label + " package name is unavailable.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.resolvedPath), label + " resolved package path is unavailable.");
            Assert.IsTrue(Directory.Exists(info.resolvedPath), label + " resolved package path does not exist.");
        }

        static string RequirePackageFileHash(string packageRoot, string relativePath)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(packageRoot), "Package root is unavailable for " + relativePath + ".");
            string path = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
            Assert.IsTrue(File.Exists(path), "Package source provenance file is missing: " + Ascii(path));
            return Sha256(path);
        }

        static void CaptureAssemblyHash(Assembly assembly, out string path, out string hash)
        {
            path = assembly == null ? string.Empty : assembly.Location;
            Assert.IsFalse(string.IsNullOrWhiteSpace(path), "Compiled package assembly location is unavailable.");
            path = Path.GetFullPath(path);
            Assert.IsTrue(File.Exists(path), "Compiled package assembly file is missing: " + Ascii(path));
            hash = Sha256(path);
        }

        static string FormatVector(Vector3 value)
        {
            return value.x.ToString("F3", CultureInfo.InvariantCulture) + ";" +
                value.y.ToString("F3", CultureInfo.InvariantCulture) + ";" +
                value.z.ToString("F3", CultureInfo.InvariantCulture);
        }

        int CountActiveEffects()
        {
            if (sceneEffects == null) return 0;
            int count = 0;
            foreach (VisualEffect effect in sceneEffects)
                if (effect != null && effect.gameObject.activeInHierarchy && effect.enabled) count++;
            return count;
        }

        static int SafeAliveCount(VisualEffect effect)
        {
            try { return effect == null ? 0 : effect.aliveParticleCount; }
            catch { return -1; }
        }

        static long CurrentRealtimeTicks()
        {
            return (long)(Time.realtimeSinceStartupAsDouble * TimeSpan.TicksPerSecond);
        }

        static bool ReadMemory(out long graphics, out long privateBytes, out long workingSet)
        {
            graphics = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (graphics <= 0 || !ProcessMemoryReader.TryRead(out privateBytes, out workingSet))
            {
                privateBytes = 0;
                workingSet = 0;
                return false;
            }
            return true;
        }

        static void ReadAbsoluteMemory(out long graphics, out long privateBytes)
        {
            graphics = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (!ProcessMemoryReader.TryRead(out privateBytes, out _)) privateBytes = 0;
        }

        static string AbsoluteAssetPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Path.GetFullPath(Application.dataPath)).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        static void WriteNewAsciiFile(string path, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                stream.Write(bytes, 0, bytes.Length);
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var result = new StringBuilder(value.Length);
            foreach (char character in value) result.Append(character >= 32 && character <= 126 ? character : '?');
            return result.ToString();
        }
    }
}
#endif
