#if UNITY_EDITOR
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
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.VFX;
using ViewWeave;
using ViewWeave.URP;

namespace ViewWeave.SceneComparison.Tests
{
    public sealed class FrustumMemoryProbeTests
    {
        const string ScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const int Width = 128;
        const int Height = 96;
        const int StageRevisions = 8;
        const int InsideRevisions = 32;
        const int StageFrameLimit = 180;
        const long StopBytes = 512L * 1024L * 1024L;
        const string EvidenceEnvironment = "VW_FRUSTUM_MEMORY_EVIDENCE_DIR";

        static readonly string[] GraphNames = { "RotationAngle", "AngularVelocity", "OrientAdvanced" };
        static readonly string[] GraphGuids = { "ccd23b3eb0caff84aab1cd555791352b", "0716daebd7b426f478f6dc430926c3b8", "33f68cda04641ea4e8bb4e16fc221947" };
        static readonly string[] GraphPaths =
        {
            "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/RotationAngle.vfx",
            "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/AngularVelocity.vfx",
            "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/OrientAdvanced.vfx"
        };
        static readonly string[] StageNames = { "outside-a", "inside", "outside-b", "reentry" };
        static readonly bool[] ExpectedFrustum = { false, true, false, true };

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

        sealed class SafetyWindow
        {
            internal long rawBaseline;
            internal long privateBaseline;
            internal long workingSetBaseline;
            internal long rawCurrent;
            internal long privateCurrent;
            internal long workingSetCurrent;
            internal long rawPeakDelta;
            internal long privatePeakDelta;
            internal string failure = "";

            internal bool Read()
            {
                rawCurrent = Profiler.GetAllocatedMemoryForGraphicsDriver();
                if (rawCurrent <= 0) { failure = "raw-graphics-counter-unavailable-or-zero"; return false; }
                if (!ProcessMemoryReader.TryRead(out privateCurrent, out workingSetCurrent) || privateCurrent <= 0 || workingSetCurrent <= 0)
                {
                    failure = "win32-process-memory-counter-unavailable-or-zero";
                    return false;
                }
                if (rawBaseline <= 0 || privateBaseline <= 0) { failure = "safety-baseline-unavailable-or-zero"; return false; }
                rawPeakDelta = Math.Max(rawPeakDelta, Math.Max(0, rawCurrent - rawBaseline));
                privatePeakDelta = Math.Max(privatePeakDelta, Math.Max(0, privateCurrent - privateBaseline));
                if (rawPeakDelta >= StopBytes || privatePeakDelta >= StopBytes)
                {
                    failure = rawPeakDelta >= StopBytes ? "raw-growth-stop-512MiB" : "private-growth-stop-512MiB";
                    return false;
                }
                return true;
            }
        }

        [Serializable]
        sealed class PhaseCapture
        {
            public string phase;
            public long revision;
            public int frame;
            public int alive;
            public bool geometryGateApplicable;
            public bool geometricIntersection;
            public bool effectCulled;
            public int quiltNonBlack;
            public int quiltVsOffChanged;
            public string bounds;
            public string cameraId;
            public string historyCameraId;
            public string matrixMatch;
            public string historyAA;
            public string historyPP;
            public string framingViewMatrix;
            public string historyViewMatrix;
            public string framingProjectionMatrix;
            public string historyProjectionMatrix;
            public float framingFov;
            public float framingAspect;
            public float framingNear;
            public float framingFar;
            public float historyFov;
            public float historyAspect;
            public float historyNear;
            public float historyFar;
        }

        readonly StringBuilder rows = new StringBuilder(32768);
        readonly StringBuilder evidence = new StringBuilder(16384);
        readonly StringBuilder batchInfoRows = new StringBuilder(4096);
        readonly List<string> runtimeSystems = new List<string>(32);
        readonly Color32[] offQuiltInside = new Color32[Width * Height];
        Texture2D quiltReadback;

        VisualEffect[] sceneEffects = Array.Empty<VisualEffect>();
        Camera[] authoredCameras = Array.Empty<Camera>();
        VisualEffect effect;
        VFXRenderer effectRenderer;
        QuiltRenderer quilt;
        ViewWeaveRig rig;
        UrpQuiltCamera guard;
        Camera referenceCamera;
        Camera sourceCamera;
        Camera historyCamera;
        RenderTexture originalReferenceTarget;
        SafetyWindow setupSafety;
        SafetyWindow activationSafety;
        Vector3 originalCameraPosition;
        Quaternion originalCameraRotation;
        float originalReferenceAspect;
        bool originalSourceMsaa;
        bool originalSourceDynamicResolution;
        Quaternion probeCameraRotation;
        Vector3 originalCameraForward;
        Vector3 horizontalPan;
        Vector3 insidePosition;
        float outsidePanDistance;
        bool originalReferenceEnabled;
        bool[] authoredCameraEnabledBefore = Array.Empty<bool>();
        int originalQualityAntiAliasing;
        bool originalReferenceHdr;
        bool originalReferenceMsaa;
        bool originalReferenceDynamicResolution;
        bool originalReferencePostProcessing;
        bool originalSourceHdr;
        bool originalSourcePostProcessing;
        AntialiasingMode originalReferenceAA;
        AntialiasingMode originalSourceAA;
        string graphLabel = "";
        string graphGuid = "";
        string graphPath = "";
        string evidenceDirectory = "";
        string graphShaBefore = "";
        string graphShaAfter = "";
        string sceneShaBefore = "";
        string sceneShaAfter = "";
        string sourceSceneShaBefore = "";
        string sourceSceneShaAfter = "";
        string packageSummary = "";
        string phase = "start";
        string failurePhase = "";
        string failureType = "";
        string failureMessage = "";
        string rendererBeginMatrixMatch = "unavailable";
        string referenceFormatAtRun = "unavailable";
        string quiltFormatAtRun = "unavailable";
        string historyAaAtRun = "unavailable";
        string historyPostProcessingAtRun = "unavailable";
        int targetErrorCount;
        int stageSampleCount;
        int activeYieldFrameCount;
        int maxAlive;
        int historyCountAfterCleanup = -1;
        int activeEffectCountAfterCleanup = -1;
        bool sourceAssetUnchanged;
        bool sceneSavedDuringProbe;
        bool sceneDiskUnchanged;
        bool sourceSceneDiskUnchanged;
        bool historyReleased;
        bool quiltTextureReleased;
        bool referenceTextureReleased;
        bool cameraEventPairMatched;
        bool cameraAAOff;
        bool runtimeDataValid;
        bool setupCountersValid;
        bool activationCountersValid;
        bool safetyStopReached;
        bool activeObjectActivatedExactlyOnce;
        bool effectRendererEnabledBeforeActivation;
        bool effectRendererEnabledChangedForProbe;
        bool effectRendererEnabledRestored;
        bool[] observedGeometricStates = new bool[StageNames.Length];
        PhaseCapture[] offCaptures = new PhaseCapture[StageNames.Length];
        PhaseCapture[] onCaptures = new PhaseCapture[StageNames.Length];
        int currentReferenceEndFrame = -1;
        int currentHistoryEndFrame = -1;
        int lastManualRenderFrame = -1;
        Matrix4x4 referenceViewMatrix;
        Matrix4x4 referenceProjectionMatrix;
        Matrix4x4 historyViewMatrix;
        Matrix4x4 historyProjectionMatrix;

        [UnityTest]
        public IEnumerator RotationAngleOriginalSceneOneViewFrustumLifecycle()
            => RunAndValidate(0);

        [UnityTest]
        public IEnumerator AngularVelocityOriginalSceneOneViewFrustumLifecycle()
            => RunAndValidate(1);

        [UnityTest]
        public IEnumerator OrientAdvancedOriginalSceneOneViewFrustumLifecycle()
            => RunAndValidate(2);

        IEnumerator RunAndValidate(int graphIndex)
        {
            graphLabel = GraphNames[graphIndex];
            graphGuid = GraphGuids[graphIndex];
            graphPath = GraphPaths[graphIndex];
            evidenceDirectory = Environment.GetEnvironmentVariable(EvidenceEnvironment) ?? "";
            Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceDirectory), EvidenceEnvironment + " was not provided by the safe runner.");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            string allowedRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(evidenceDirectory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase), "Evidence must stay below project TestResults.");
            if (Directory.Exists(evidenceDirectory)) Assert.Fail("Refusing to overwrite prior case evidence.");
            Directory.CreateDirectory(evidenceDirectory);
            rows.AppendLine("phase,phaseFrame,unityFrame,quiltRevision,aliveParticles,systemCount,geometryGateApplicable,geometryIntersects,effectCulled,quiltNonBlack,quiltChangedVsOff,rawGraphicsBytes,privateBytes,workingSetBytes,rawGrowthBytes,privateGrowthBytes,historyGraphicsGrowthBytes,historyTextureEstimateBytes,framingCameraId,historyCameraId,framingHistoryMatricesMatch,framingCameraEndFrame,historyEndFrame,bounds,quiltFinalFormat,historyAllowHDR,historyAA,historyPostProcessing");
            Subscribe();
            Exception failure = null;
            var stack = new Stack<IEnumerator>();
            stack.Push(RunCore());
            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                bool moved;
                object yielded = null;
                try { moved = current.MoveNext(); if (moved) yielded = current.Current; }
                catch (Exception exception) { failure = exception; failurePhase = phase; break; }
                if (!moved) { stack.Pop(); continue; }
                if (yielded is IEnumerator nested) { stack.Push(nested); continue; }
                yield return yielded;
            }

            phase = "cleanup";
            CleanupSynchronous();
            for (int frame = 1; frame <= 8; ++frame)
            {
                yield return null;
                if (activationSafety != null && !activationSafety.Read()) safetyStopReached = true;
            }
            RestoreAuthoredCameraEnabledStates();
            historyCountAfterCleanup = guard == null ? -1 : guard.HistoryCameraCount;
            historyReleased = guard != null && historyCountAfterCleanup == 0 && guard.GetHistoryCamera(0) == null;
            quiltTextureReleased = quilt == null || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated();
            referenceTextureReleased = referenceCamera == null || referenceCamera.targetTexture == null;
            activeEffectCountAfterCleanup = sceneEffects.Count(value => value != null && value.gameObject.activeInHierarchy);
            sceneShaAfter = SafeSha(ProjectAssetPath(ScenePath));
            graphShaAfter = SafeSha(ProjectAssetPath(graphPath));
            sourceSceneShaAfter = SafeSha(ProjectAssetPath("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity"));
            sceneDiskUnchanged = !string.IsNullOrEmpty(sceneShaBefore) && sceneShaBefore == sceneShaAfter && !sceneSavedDuringProbe;
            sourceSceneDiskUnchanged = !string.IsNullOrEmpty(sourceSceneShaBefore) && sourceSceneShaBefore == sourceSceneShaAfter;
            sourceAssetUnchanged = !string.IsNullOrEmpty(graphShaBefore) && graphShaBefore == graphShaAfter;
            Unsubscribe();
            DestroyReadbacks();
            if (failure == null)
            {
                phase = "final-gates";
                try { ValidateObservations(); }
                catch (Exception exception) { failure = exception; failurePhase = phase; }
            }
            WriteEvidence(failure);
            if (failure != null) throw failure;
        }

        void ValidateObservations()
        {
            Assert.IsTrue(setupCountersValid, "The 512 MiB setup/control safety window was invalid or exceeded.");
            Assert.IsTrue(activationCountersValid, "The 512 MiB activation safety window was invalid or exceeded.");
            Assert.IsFalse(safetyStopReached, "A 512 MiB observed-growth stop was reached during cleanup.");
            Assert.IsFalse(sceneSavedDuringProbe, "The prepared scene was saved during this runtime-only probe.");
            Assert.IsTrue(sceneDiskUnchanged, "The prepared scene bytes changed during the probe.");
            Assert.IsTrue(sourceSceneDiskUnchanged, "The source learning scene bytes changed during the probe.");
            Assert.IsTrue(sourceAssetUnchanged, "The selected original VFX graph bytes changed during the probe.");
            Assert.IsTrue(effectRendererEnabledRestored, "The selected VFXRenderer enabled state was not restored after observation.");
            Assert.AreEqual(0, targetErrorCount, "Unity logged an error or exception during the active probe.");
            Assert.IsTrue(cameraAAOff, "The actual one-view URP history camera did not use AA Off.");
            Assert.IsTrue(runtimeDataValid, "Runtime particle-system metadata was unavailable.");
            Assert.IsTrue(cameraEventPairMatched, "No manual history-camera render matched the framing camera pose and projection.");
            Assert.IsTrue(historyReleased, "URP history camera did not release after cleanup.");
            Assert.IsTrue(quiltTextureReleased, "SDK quilt texture remained created after cleanup.");
            Assert.IsTrue(referenceTextureReleased, "Unexpected probe-owned framing-camera target remained created after cleanup.");
            Assert.AreEqual(0, activeEffectCountAfterCleanup, "A scene VFX object remained active after cleanup.");
            for (int i = 0; i < StageNames.Length; ++i)
            {
                Assert.IsNotNull(offCaptures[i], "Missing VFX-off control capture for " + StageNames[i]);
                Assert.IsNotNull(onCaptures[i], "Missing VFX-on capture for " + StageNames[i]);
                Assert.IsFalse(offCaptures[i].geometryGateApplicable, "Off-control must record bounds intersection as reference only at " + StageNames[i]);
                Assert.IsTrue(onCaptures[i].geometryGateApplicable, "Active VFX geometry gate was not enabled at " + StageNames[i]);
                Assert.AreEqual(ExpectedFrustum[i], onCaptures[i].geometricIntersection, "Active frustum state mismatch at " + StageNames[i]);
            }
            Assert.Greater(onCaptures[1].alive, 0, "The selected graph produced no live particles while inside the frustum.");
            Assert.Greater(onCaptures[3].alive, 0, "The selected graph had no live particles on frustum re-entry.");
            Assert.Greater(onCaptures[1].quiltVsOffChanged, 20, "The quilt output showed no meaningful selected-effect change inside the frustum.");
            Assert.Greater(onCaptures[3].quiltVsOffChanged, 20, "The quilt output showed no meaningful selected-effect change on re-entry.");
        }

        IEnumerator RunCore()
        {
            phase = "scene-load-and-preconditions";
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float loadDeadline = Time.realtimeSinceStartup + 60f;
            while ((!scene.IsValid() || !scene.isLoaded) && Time.realtimeSinceStartup < loadDeadline) yield return null;
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "Prepared scene failed to load.");
            Assert.IsTrue(Application.isPlaying, "This probe requires Play Mode.");
            Assert.AreEqual(GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType, "Expected the incident-matching D3D12 backend.");
            Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline, "Expected URP.");
            quiltReadback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true) { name = "FrustumMemory.Quilt.Readback", hideFlags = HideFlags.HideAndDontSave };
            packageSummary = CaptureMainPackages();
            sceneShaBefore = SafeSha(ProjectAssetPath(ScenePath));
            sourceSceneShaBefore = SafeSha(ProjectAssetPath("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity"));
            graphShaBefore = SafeSha(ProjectAssetPath(graphPath));
            Assert.IsNotEmpty(sceneShaBefore);
            Assert.IsNotEmpty(sourceSceneShaBefore);
            Assert.IsNotEmpty(graphShaBefore);
            Assert.AreEqual(graphGuid, AssetDatabase.AssetPathToGUID(graphPath), "The selected graph GUID changed.");
            Assert.AreEqual("UnityEditor.VFX.URP.VFXURPBinder", CurrentVfxBinderType(), "VFX binder does not match URP.");

            sceneEffects = UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded).ToArray();
            Assert.AreEqual(33, sceneEffects.Length, "Expected all original VisualEffect components, including inactive entries.");
            Assert.AreEqual(0, sceneEffects.Count(value => value.gameObject.activeInHierarchy), "All copied VFX objects must begin inactive.");
            VisualEffect[] matches = sceneEffects.Where(value => value.visualEffectAsset != null &&
                string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value.visualEffectAsset)), graphGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, matches.Length, "Expected exactly one original scene instance for the selected graph GUID.");
            effect = matches[0];
            Assert.IsFalse(effect.gameObject.activeInHierarchy);
            Assert.IsTrue(effect.enabled, "The original VisualEffect component must remain enabled.");
            Assert.IsFalse(effect.pause, "The original effect must not be paused.");
            Assert.Greater(effect.playRate, 0f, "The original play rate must be positive.");
            Assert.IsNotNull(effect.visualEffectAsset);
            Assert.AreEqual(graphGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect.visualEffectAsset)));
            effectRenderer = effect.GetComponent<VFXRenderer>();
            Assert.IsNotNull(effectRenderer);
            effectRendererEnabledBeforeActivation = effectRenderer.enabled;

            rig = Resources.FindObjectsOfTypeAll<ViewWeaveRig>().Single(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded);
            quilt = rig.GetComponent<QuiltRenderer>();
            Assert.IsNotNull(quilt);
            Assert.IsFalse(rig.AutoRender, "Rig autorender must be off during setup.");
            Assert.IsFalse(quilt.autoRender, "Quilt autorender must be off during setup.");
            Assert.IsNotNull(quilt.referenceCamera);
            Assert.IsNotNull(quilt.sourceCamera);
            referenceCamera = quilt.referenceCamera;
            sourceCamera = quilt.sourceCamera;
            guard = sourceCamera.GetComponent<UrpQuiltCamera>();
            Assert.IsNotNull(guard);
            Assert.AreEqual(UrpQuiltRenderingMode.IsolatedViewHistories, guard.renderingMode);
            Assert.AreEqual(4096, guard.historyMemoryBudgetMB, "The SDK default history budget must remain 4096 MiB.");
            Assert.AreEqual(0, CountActiveSceneEffects());

            authoredCameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(camera => camera != null && camera.gameObject.scene.IsValid() && camera.gameObject.scene.isLoaded && camera != sourceCamera)
                .Distinct().ToArray();
            Assert.IsTrue(authoredCameras.Contains(referenceCamera), "Reference camera was not enumerated as a scene camera.");
            originalReferenceEnabled = referenceCamera.enabled;
            authoredCameraEnabledBefore = authoredCameras.Select(camera => camera.enabled).ToArray();
            Assert.IsTrue(originalReferenceEnabled, "Saved framing camera must begin enabled before in-memory isolation.");
            foreach (Camera camera in authoredCameras) camera.enabled = false;
            Assert.IsTrue(authoredCameras.All(camera => !camera.enabled), "Every authored camera, including the framing camera, must be disabled.");
            originalQualityAntiAliasing = QualitySettings.antiAliasing;
            originalReferenceHdr = referenceCamera.allowHDR;
            originalReferenceMsaa = referenceCamera.allowMSAA;
            originalReferenceDynamicResolution = referenceCamera.allowDynamicResolution;
            UniversalAdditionalCameraData referenceData = referenceCamera.GetComponent<UniversalAdditionalCameraData>();
            UniversalAdditionalCameraData sourceData = sourceCamera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsNotNull(referenceData);
            Assert.IsNotNull(sourceData);
            originalReferencePostProcessing = referenceData.renderPostProcessing;
            originalSourceHdr = sourceCamera.allowHDR;
            originalSourcePostProcessing = sourceData.renderPostProcessing;
            originalReferenceAA = referenceData.antialiasing;
            originalSourceAA = sourceData.antialiasing;
            originalCameraPosition = referenceCamera.transform.position;
            originalCameraRotation = referenceCamera.transform.rotation;
            originalCameraForward = referenceCamera.transform.forward;
            originalReferenceAspect = referenceCamera.aspect;
            originalSourceMsaa = sourceCamera.allowMSAA;
            originalSourceDynamicResolution = sourceCamera.allowDynamicResolution;
            Assert.IsFalse(sourceCamera.enabled, "The dedicated source camera must not render independently.");

            phase = "camera-and-one-view-setup";
            float cameraDistance = graphLabel == "RotationAngle" ? 4f : 8f;
            Vector3 target = graphLabel == "RotationAngle"
                ? effect.transform.position + Vector3.up * 1.5f
                : effectRenderer.bounds.center;
            insidePosition = target - originalCameraForward.normalized * cameraDistance;
            Quaternion aimedRotation = Quaternion.LookRotation(target - insidePosition, Vector3.up);
            probeCameraRotation = aimedRotation;
            Vector3 horizontalRight = Vector3.ProjectOnPlane(aimedRotation * Vector3.right, Vector3.up).normalized;
            Assert.Greater(horizontalRight.sqrMagnitude, 0.9f, "Could not determine a horizontal camera pan axis.");
            referenceCamera.transform.SetPositionAndRotation(insidePosition, aimedRotation);
            referenceCamera.aspect = Width / (float)Height;
            referenceCamera.allowDynamicResolution = false;
            referenceCamera.allowMSAA = false;
            referenceData.antialiasing = AntialiasingMode.None;
            sourceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing = AntialiasingMode.None;
            cameraAAOff = true;
            horizontalPan = horizontalRight;
            float halfWidth = cameraDistance * Mathf.Tan(referenceCamera.fieldOfView * Mathf.Deg2Rad * 0.5f) * referenceCamera.aspect;
            Bounds startBounds = effectRenderer.bounds;
            float projectedRadius = Mathf.Abs(horizontalRight.x) * startBounds.extents.x + Mathf.Abs(horizontalRight.y) * startBounds.extents.y + Mathf.Abs(horizontalRight.z) * startBounds.extents.z;
            outsidePanDistance = halfWidth + projectedRadius + 1f;
            originalReferenceTarget = referenceCamera.targetTexture;
            Assert.IsNull(originalReferenceTarget, "The prepared framing camera must not own an existing render target.");
            Assert.IsFalse(referenceCamera.enabled, "Framing camera must remain disabled; only the isolated history camera renders.");
            sourceCamera.allowMSAA = false;
            sourceCamera.allowDynamicResolution = false;
            QualitySettings.antiAliasing = 0;
            rig.SetOutput(null);
            rig.SetGuard(guard);
            rig.ApplyProfile(new QuiltQualityProfile(Width, Height, 1, 1, 1, Width / (float)Height, 0f));
            quilt.settings.fieldOfView = referenceCamera.fieldOfView;
            quilt.settings.cameraDistance = cameraDistance;
            quilt.settings.viewCone = 0f;
            quilt.viewRenderMode = QuiltViewRenderMode.Full;
            quilt.followReferenceFieldOfView = true;
            quilt.useRenderRequests = true;
            quilt.autoRender = false;
            Assert.IsTrue(rig.ValidateRig(out string validationError), validationError ?? "Rig validation failed.");
            Assert.AreSame(referenceCamera, quilt.referenceCamera);
            Assert.AreSame(sourceCamera, quilt.sourceCamera);
            Assert.AreEqual(4096, guard.historyMemoryBudgetMB);
            rig.AutoRender = false;
            Assert.IsFalse(rig.AutoRender, "Rig auto-render must remain disabled for explicit single-render observations.");
            Assert.IsFalse(quilt.autoRender, "Quilt auto-render must remain disabled for explicit single-render observations.");

            phase = "pipeline-warmup";
            long warmupStart = quilt.QuiltRevision;
            yield return WaitForRevisions(warmupStart + StageRevisions, StageFrameLimit, null, "warmup");
            Assert.AreEqual(1, guard.HistoryCameraCount, "Expected a single isolated history camera.");
            Assert.AreEqual(1, quilt.LastRenderedViewCount, "Expected one rendered quilt view.");
            Assert.IsNotNull(quilt.QuiltTexture);
            Assert.AreEqual(Width, quilt.QuiltTexture.width);
            Assert.AreEqual(Height, quilt.QuiltTexture.height);
            historyCamera = guard.GetHistoryCamera(0);
            Assert.IsNotNull(historyCamera);
            Assert.AreEqual(AntialiasingMode.None, historyCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing, "Effective history AA must be Off.");
            Assert.AreEqual(originalReferenceHdr, referenceCamera.allowHDR, "Probe changed the framing-camera HDR setting.");
            Assert.AreEqual(originalReferencePostProcessing, referenceData.renderPostProcessing, "Probe changed the framing-camera post-processing setting.");
            Assert.AreEqual(originalSourceHdr, sourceCamera.allowHDR, "Probe changed the source-camera HDR setting.");
            Assert.AreEqual(originalSourcePostProcessing, sourceData.renderPostProcessing, "Probe changed the source-camera post-processing setting.");
            referenceFormatAtRun = "not-rendered-framing-camera-disabled";
            quiltFormatAtRun = quilt.QuiltTexture.graphicsFormat.ToString();
            UniversalAdditionalCameraData effectiveHistoryData = historyCamera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsNotNull(effectiveHistoryData);
            historyAaAtRun = effectiveHistoryData.antialiasing.ToString();
            historyPostProcessingAtRun = effectiveHistoryData.renderPostProcessing.ToString();
            cameraAAOff = effectiveHistoryData.antialiasing == AntialiasingMode.None;

            setupSafety = CreateSafetyWindow("off-control-baseline");
            phase = "off-control-four-phase-path";
            for (int stage = 0; stage < StageNames.Length; ++stage)
            {
                SetCameraStage(stage);
                yield return WaitForRevisions(quilt.QuiltRevision + StageRevisions, StageFrameLimit, setupSafety, "off-" + StageNames[stage], stage, false);
                PhaseCapture capture = CaptureStage("off-" + StageNames[stage], stage, false);
                offCaptures[stage] = capture;
                Assert.IsFalse(capture.geometryGateApplicable, "VFX-off bounds intersection is reference data, not an effect-frustum gate.");
            }
            Assert.IsTrue(setupSafety.Read(), setupSafety.failure);
            activationSafety = CreateSafetyWindow("activation-baseline-after-off-control");

            phase = "selected-effect-activation-outside-a";
            SetCameraStage(0);
            RecordImmediateSample("activation-before-renderer-enable", 0, false);
            Assert.AreEqual(0, CountActiveSceneEffects(), "All VFX objects must be inactive immediately before activation.");
            Assert.IsTrue(effect.enabled && !effect.pause && effect.playRate > 0f, "Original selected VFX playback state changed before activation.");
            int activatedBefore = CountActiveSceneEffects();
            effect.gameObject.SetActive(true);
            activeObjectActivatedExactlyOnce = activatedBefore == 0 && effect.gameObject.activeInHierarchy && CountActiveSceneEffects() == 1;
            Assert.IsTrue(activeObjectActivatedExactlyOnce, "The original selected VFX object did not activate exactly once.");
            RecordImmediateSample("activation-after-setactive", 0, true);
            if (!effectRenderer.enabled)
            {
                effectRendererEnabledChangedForProbe = true;
                effectRenderer.enabled = true;
                Assert.IsTrue(effectRenderer.enabled, "The selected VFXRenderer could not be enabled for the isolated probe.");
                RecordImmediateSample("activation-after-probe-renderer-enable", 0, true);
            }
            ReadRuntimeParticleSystems();
            AssertOnlySelectedActive();

            phase = "active-four-phase-path";
            for (int stage = 0; stage < StageNames.Length; ++stage)
            {
                SetCameraStage(stage);
                int requested = stage == 1 ? InsideRevisions : StageRevisions;
                yield return WaitForRevisions(quilt.QuiltRevision + requested, StageFrameLimit * 2, activationSafety, StageNames[stage], stage, true);
                PhaseCapture capture = CaptureStage(StageNames[stage], stage, true);
                onCaptures[stage] = capture;
                Assert.AreEqual(ExpectedFrustum[stage], capture.geometricIntersection, "Active geometry did not reach the expected frustum phase.");
            }
            Assert.IsTrue(activationSafety.Read(), activationSafety.failure);
            Assert.IsTrue(sourceAssetUnchanged || graphShaBefore == SafeSha(ProjectAssetPath(graphPath)), "Selected graph source changed during runtime observation.");
            Assert.IsTrue(cameraEventPairMatched, "No manual history-camera render matched the framing camera matrices.");
        }

        IEnumerator WaitForRevisions(long targetRevision, int maxFrames, SafetyWindow safety, string stageName, int stageIndex = -1, bool active = false)
        {
            long startRevision = quilt.QuiltRevision;
            int frames = 0;
            int requestedRevisions = checked((int)(targetRevision - startRevision));
            Assert.Greater(requestedRevisions, 0, "Explicit render request must contain at least one revision.");
            for (int requested = 0; requested < requestedRevisions; ++requested)
            {
                Assert.IsNotNull(quilt);
                Assert.Less(frames, maxFrames, "Normal PlayerLoop frame limit reached before an explicit quilt render.");
                if (active) AssertOnlySelectedActive();
                frames++;
                activeYieldFrameCount += active ? 1 : 0;
                yield return null;
                if (!SampleSafety(stageName + "-before-render", stageIndex, active, safety)) throw new AssertionException(safety.failure);
                Assert.IsNull(Camera.current, "Manual quilt rendering must run outside camera callbacks.");
                Assert.AreNotEqual(lastManualRenderFrame, Time.frameCount, "More than one manual quilt render was requested in a single PlayerLoop frame.");
                lastManualRenderFrame = Time.frameCount;
                long revisionBeforeRender = quilt.QuiltRevision;
                quilt.RenderQuilt();
                Assert.IsFalse(quilt.IsRendering, "Explicit quilt render did not leave the renderer idle.");
                Assert.AreEqual(revisionBeforeRender + 1, quilt.QuiltRevision, "Explicit quilt render did not complete exactly one revision.");
                Assert.AreEqual(1, quilt.LastRenderedViewCount, "Explicit render did not render exactly one logical view.");
                if (!SampleSafety(stageName + "-after-render", stageIndex, active, safety)) throw new AssertionException(safety.failure);
                frames++;
                Assert.LessOrEqual(frames, maxFrames, "Normal PlayerLoop frame limit reached after an explicit quilt render.");
                activeYieldFrameCount += active ? 1 : 0;
                yield return null;
                if (!SampleSafety(stageName + "-player-loop", stageIndex, active, safety)) throw new AssertionException(safety.failure);
            }
            Assert.IsNotNull(quilt);
            Assert.GreaterOrEqual(quilt.QuiltRevision, targetRevision, "Manual RenderQuilt calls did not complete the required phase revisions.");
            Assert.AreEqual(targetRevision - startRevision, quilt.QuiltRevision - startRevision, "Quilt revisions skipped between manual frustum observations.");
        }

        bool SampleSafety(string label, int stageIndex, bool active, SafetyWindow safety)
        {
            if (safety == null) return true;
            bool valid = safety.Read();
            RecordFrame(label, stageIndex, active, safety);
            if (!valid) safetyStopReached = true;
            return valid;
        }

        void SetCameraStage(int stage)
        {
            Vector3 offset = stage == 0 ? horizontalPan * outsidePanDistance : stage == 2 ? -horizontalPan * outsidePanDistance : Vector3.zero;
            referenceCamera.transform.position = insidePosition + offset;
            referenceCamera.transform.rotation = probeCameraRotation;
            phase = StageNames[stage];
        }

        SafetyWindow CreateSafetyWindow(string label)
        {
            long raw = Profiler.GetAllocatedMemoryForGraphicsDriver();
            bool processValid = ProcessMemoryReader.TryRead(out long privateBytes, out long workingSetBytes);
            Assert.IsTrue(raw > 0 && processValid && privateBytes > 0 && workingSetBytes > 0, "A raw graphics or process-memory safety counter is unavailable at " + label + ".");
            return new SafetyWindow { rawBaseline = raw, privateBaseline = privateBytes, workingSetBaseline = workingSetBytes };
        }

        void RecordImmediateSample(string label, int stage, bool active)
        {
            bool valid = activationSafety.Read();
            AppendMemoryRow(label, stage, active, activationSafety);
            Assert.IsTrue(valid, activationSafety.failure);
        }

        void RecordFrame(string stageName, int stageIndex, bool active, SafetyWindow safety)
        {
            bool intersect = GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(referenceCamera), effectRenderer.bounds);
            int alive = active && effect != null ? effect.aliveParticleCount : 0;
            bool culled = active && effect != null && effect.culled;
            long raw = safety == null ? Profiler.GetAllocatedMemoryForGraphicsDriver() : safety.rawCurrent;
            long privateBytes = safety == null ? 0 : safety.privateCurrent;
            long workingSet = safety == null ? 0 : safety.workingSetCurrent;
            long rawDelta = safety == null ? 0 : Math.Max(0, raw - safety.rawBaseline);
            long privateDelta = safety == null ? 0 : Math.Max(0, privateBytes - safety.privateBaseline);
            rows.Append(Ascii(stageName)).Append(',').Append(stageIndex).Append(',').Append(Time.frameCount).Append(',')
                .Append(quilt == null ? -1 : quilt.QuiltRevision).Append(',').Append(alive).Append(',').Append(runtimeSystems.Count).Append(',')
                .Append(active ? 1 : 0).Append(',').Append(intersect ? 1 : 0).Append(',').Append(culled ? 1 : 0).Append(",-1,-1,")
                .Append(raw).Append(',').Append(privateBytes).Append(',').Append(workingSet).Append(',').Append(rawDelta).Append(',').Append(privateDelta).Append(',')
                .Append(guard == null ? -1 : guard.HistoryGraphicsMemoryGrowthBytes).Append(',').Append(guard == null ? -1 : guard.HistoryTextureMemoryEstimateBytes).Append(',')
                .Append(referenceCamera == null ? "unavailable" : referenceCamera.GetEntityId().ToString()).Append(',')
                .Append(historyCamera == null ? "unavailable" : historyCamera.GetEntityId().ToString()).Append(',')
                .Append(rendererBeginMatrixMatch).Append(',').Append(currentReferenceEndFrame).Append(',').Append(currentHistoryEndFrame).Append(',')
                .Append(Ascii(BoundsText(effectRenderer.bounds))).Append(',')
                .Append(quilt == null || quilt.QuiltTexture == null ? "unavailable" : quilt.QuiltTexture.graphicsFormat.ToString()).Append(',')
                .Append(historyCamera == null ? "unavailable" : historyCamera.allowHDR.ToString()).Append(',')
                .Append(historyCamera == null ? "unavailable" : historyCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing.ToString()).Append(',')
                .Append(historyCamera == null ? "unavailable" : historyCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing.ToString()).AppendLine();
            if (active && safety != null) activationCountersValid = true;
            else if (!active && safety != null) setupCountersValid = true;
            stageSampleCount++;
            maxAlive = Math.Max(maxAlive, alive);
        }

        void AppendMemoryRow(string sampleLabel, int stage, bool active, SafetyWindow safety)
        {
            rows.Append(Ascii(sampleLabel)).Append(',').Append(stage).Append(',').Append(Time.frameCount).Append(',')
                .Append(quilt == null ? -1 : quilt.QuiltRevision).Append(',').Append(!active || effect == null ? 0 : effect.aliveParticleCount).Append(',').Append(runtimeSystems.Count)
                .Append(',').Append(active ? 1 : 0).Append(',').Append(effectRenderer != null && GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(referenceCamera), effectRenderer.bounds) ? 1 : 0)
                .Append(',').Append(active && effect != null && effect.culled ? 1 : 0).Append(",-1,-1,").Append(safety.rawCurrent).Append(',').Append(safety.privateCurrent).Append(',').Append(safety.workingSetCurrent)
                .Append(',').Append(Math.Max(0, safety.rawCurrent - safety.rawBaseline)).Append(',').Append(Math.Max(0, safety.privateCurrent - safety.privateBaseline))
                .Append(',').Append(guard == null ? -1 : guard.HistoryGraphicsMemoryGrowthBytes).Append(',').Append(guard == null ? -1 : guard.HistoryTextureMemoryEstimateBytes)
                .Append(',').Append(referenceCamera.GetEntityId().ToString()).Append(',').Append(historyCamera == null ? "unavailable" : historyCamera.GetEntityId().ToString())
                .Append(',').Append(rendererBeginMatrixMatch).Append(',').Append(currentReferenceEndFrame).Append(',').Append(currentHistoryEndFrame)
                .Append(',').Append(Ascii(BoundsText(effectRenderer.bounds))).Append(',')
                .Append(quilt.QuiltTexture == null ? "unavailable" : quilt.QuiltTexture.graphicsFormat.ToString()).Append(',').Append(historyCamera == null ? "unavailable" : historyCamera.allowHDR.ToString())
                .Append(',').Append(historyCamera == null ? "unavailable" : historyCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing.ToString())
                .Append(',').Append(historyCamera == null ? "unavailable" : historyCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing.ToString()).AppendLine();
            if (active) activationCountersValid = true; else setupCountersValid = true;
            stageSampleCount++;
        }

        PhaseCapture CaptureStage(string stageName, int stage, bool active)
        {
            Assert.IsNotNull(historyCamera);
            Assert.AreEqual(1, guard.HistoryCameraCount);
            Assert.AreEqual(1, quilt.LastRenderedViewCount);
            ReadPixels(quilt.QuiltTexture, quiltReadback);
            Unity.Collections.NativeArray<Color32> quiltPixels = quiltReadback.GetRawTextureData<Color32>();
            int quiltNonBlack = CountNonBlack(quiltPixels);
            bool intersect = GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(referenceCamera), effectRenderer.bounds);
            int alive = active ? effect.aliveParticleCount : 0;
            int quiltChanged = 0;
            if (!active && stage == 1)
            {
                quiltPixels.CopyTo(offQuiltInside);
            }
            if (active && (stage == 1 || stage == 3))
            {
                quiltChanged = CountChanged(quiltPixels, offQuiltInside);
            }
            if (active && alive > 0) maxAlive = Math.Max(maxAlive, alive);
            if (active && (stage == 0 || stage == 1 || stage == 3))
            {
                ReadRuntimeParticleSystems();
                CaptureBatchedEffectInfo(stageName);
            }
            string name = graphLabel + "-" + (active ? "on-" : "off-") + stageName;
            if (stage == 1) SavePng(quiltReadback, name + "-quilt.png");
            var capture = new PhaseCapture
            {
                phase = stageName,
                revision = quilt.QuiltRevision,
                frame = Time.frameCount,
                alive = alive,
                geometryGateApplicable = active,
                geometricIntersection = intersect,
                effectCulled = active && effect.culled,
                quiltNonBlack = quiltNonBlack,
                quiltVsOffChanged = quiltChanged,
                bounds = BoundsText(effectRenderer.bounds),
                cameraId = referenceCamera.GetEntityId().ToString(),
                historyCameraId = historyCamera.GetEntityId().ToString(),
                matrixMatch = rendererBeginMatrixMatch,
                historyAA = historyCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing.ToString(),
                historyPP = historyCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing.ToString(),
                framingViewMatrix = MatrixText(referenceViewMatrix),
                historyViewMatrix = MatrixText(historyViewMatrix),
                framingProjectionMatrix = MatrixText(referenceProjectionMatrix),
                historyProjectionMatrix = MatrixText(historyProjectionMatrix),
                framingFov = referenceCamera.fieldOfView,
                framingAspect = referenceCamera.aspect,
                framingNear = referenceCamera.nearClipPlane,
                framingFar = referenceCamera.farClipPlane,
                historyFov = historyCamera.fieldOfView,
                historyAspect = historyCamera.aspect,
                historyNear = historyCamera.nearClipPlane,
                historyFar = historyCamera.farClipPlane
            };
            return capture;
        }

        void ReadRuntimeParticleSystems()
        {
            runtimeSystems.Clear();
            effect.GetParticleSystemNames(runtimeSystems);
            Assert.IsNotEmpty(runtimeSystems, "Selected graph exposes no runtime particle systems after its original OnEnable event.");
            for (int i = 0; i < runtimeSystems.Count; ++i)
            {
                string systemName = runtimeSystems[i];
                VFXParticleSystemInfo info = effect.GetParticleSystemInfo(systemName);
                Assert.Greater(info.capacity, 0, "Runtime particle system reported a nonpositive capacity.");
                runtimeSystems[i] = systemName + ":capacity=" + info.capacity.ToString(CultureInfo.InvariantCulture) + ":alive=" + info.aliveCount.ToString(CultureInfo.InvariantCulture);
            }
            runtimeDataValid = true;
        }

        void AssertOnlySelectedActive()
        {
            Assert.AreEqual(1, CountActiveSceneEffects(), "Exactly one scene VisualEffect must be active during the selected phase.");
            foreach (VisualEffect candidate in sceneEffects)
            {
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                Assert.AreSame(effect, candidate, "An unrelated VisualEffect became active during the probe.");
                Assert.IsTrue(candidate.enabled, "The selected VisualEffect component was disabled.");
            }
            Assert.IsNotNull(effectRenderer);
            Assert.IsTrue(effectRenderer.enabled, "The selected VFXRenderer was disabled during an observation phase.");
        }

        void CaptureBatchedEffectInfo(string checkpoint)
        {
            VisualEffect[] selected = sceneEffects.Where(value => value != null && value.visualEffectAsset != null &&
                string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value.visualEffectAsset)), graphGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            int activeSelected = selected.Count(value => value.gameObject.activeInHierarchy && value.enabled);
            string resultType = "unavailable";
            string failure = "";
            string gpu = "unavailable";
            string cpu = "unavailable";
            string activeBatches = "unavailable";
            string inactiveBatches = "unavailable";
            string activeInstances = "unavailable";
            string unbatchedInstances = "unavailable";
            string totalCapacity = "unavailable";
            string maximumBatchCapacity = "unavailable";
            try
            {
                if (selected.Length != 1 || activeSelected != 1)
                {
                    failure = "selected-component-count-not-one";
                }
                else
                {
                    MethodInfo method = typeof(VFXManager).GetMethod("GetBatchedEffectInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null, new[] { typeof(VisualEffectAsset) }, null);
                    if (method == null) failure = "api-unavailable";
                    else
                    {
                        object info = method.Invoke(null, new object[] { effect.visualEffectAsset });
                        if (info == null) failure = "returned-null";
                        else
                        {
                            Type infoType = info.GetType();
                            resultType = infoType.FullName ?? infoType.Name;
                            gpu = ReadInfoMember(infoType, info, "totalGPUSizeInBytes");
                            cpu = ReadInfoMember(infoType, info, "totalCPUSizeInBytes");
                            activeBatches = ReadInfoMember(infoType, info, "activeBatchCount");
                            inactiveBatches = ReadInfoMember(infoType, info, "inactiveBatchCount");
                            activeInstances = ReadInfoMember(infoType, info, "activeInstanceCount");
                            unbatchedInstances = ReadInfoMember(infoType, info, "unbatchedInstanceCount");
                            totalCapacity = ReadInfoMember(infoType, info, "totalInstanceCapacity");
                            maximumBatchCapacity = ReadInfoMember(infoType, info, "maxInstancePerBatchCapacity");
                            if (gpu == "unavailable" || cpu == "unavailable" || activeBatches == "unavailable" || inactiveBatches == "unavailable" ||
                                activeInstances == "unavailable" || unbatchedInstances == "unavailable" || totalCapacity == "unavailable" || maximumBatchCapacity == "unavailable")
                                failure = "one-or-more-fields-unavailable";
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception.GetType().Name;
            }
            batchInfoRows.Append("checkpoint=").Append(Ascii(checkpoint))
                .Append(";frame=").Append(Time.frameCount.ToString(CultureInfo.InvariantCulture))
                .Append(";revision=").Append(quilt == null ? -1 : quilt.QuiltRevision)
                .Append(";assetGuid=").Append(graphGuid)
                .Append(";selectedComponents=").Append(selected.Length.ToString(CultureInfo.InvariantCulture))
                .Append(";activeSelectedComponents=").Append(activeSelected.ToString(CultureInfo.InvariantCulture))
                .Append(";infoType=").Append(Ascii(resultType))
                .Append(";totalGpuBytes=").Append(Ascii(gpu))
                .Append(";totalCpuBytes=").Append(Ascii(cpu))
                .Append(";activeBatchCount=").Append(Ascii(activeBatches))
                .Append(";inactiveBatchCount=").Append(Ascii(inactiveBatches))
                .Append(";activeInstanceCount=").Append(Ascii(activeInstances))
                .Append(";unbatchedInstanceCount=").Append(Ascii(unbatchedInstances))
                .Append(";totalInstanceCapacity=").Append(Ascii(totalCapacity))
                .Append(";maxInstancePerBatchCapacity=").Append(Ascii(maximumBatchCapacity))
                .Append(";auxiliaryFailure=").Append(Ascii(failure)).AppendLine();
        }

        static string ReadInfoMember(Type type, object instance, string memberName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                object value = property.GetValue(instance, null);
                return value == null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            FieldInfo field = type.GetField(memberName, flags);
            if (field == null) return "unavailable";
            object fieldValue = field.GetValue(instance);
            return fieldValue == null ? "null" : Convert.ToString(fieldValue, CultureInfo.InvariantCulture);
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == historyCamera)
            {
                if (referenceCamera != null)
                {
                    referenceViewMatrix = referenceCamera.worldToCameraMatrix;
                    referenceProjectionMatrix = referenceCamera.projectionMatrix;
                }
                historyViewMatrix = camera.worldToCameraMatrix;
                historyProjectionMatrix = camera.projectionMatrix;
                rendererBeginMatrixMatch = (MatrixNear(referenceViewMatrix, historyViewMatrix) && MatrixNear(referenceProjectionMatrix, historyProjectionMatrix)).ToString();
            }
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == historyCamera)
            {
                currentHistoryEndFrame = Time.frameCount;
                cameraEventPairMatched |= rendererBeginMatrixMatch == "True";
            }
        }

        void Subscribe()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            Application.logMessageReceived += OnLogMessage;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        void Unsubscribe()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Application.logMessageReceived -= OnLogMessage;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
        }

        void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) targetErrorCount++;
        }

        void OnSceneSaved(Scene savedScene)
        {
            if (savedScene.IsValid() && string.Equals((savedScene.path ?? "").Replace('\\', '/'), ScenePath, StringComparison.Ordinal)) sceneSavedDuringProbe = true;
        }

        void CleanupSynchronous()
        {
            Unsubscribe();
            try { if (rig != null) rig.AutoRender = false; } catch { }
            try { if (quilt != null) { quilt.autoRender = false; quilt.enabled = false; } } catch { }
            try { if (effect != null) { effect.Stop(); effect.gameObject.SetActive(false); } } catch { }
            try { if (effectRenderer != null) { effectRenderer.enabled = effectRendererEnabledBeforeActivation; effectRendererEnabledRestored = effectRenderer.enabled == effectRendererEnabledBeforeActivation; } } catch { }
            try { if (sourceCamera != null) sourceCamera.enabled = false; } catch { }
            try
            {
                if (referenceCamera != null)
                {
                    referenceCamera.enabled = false;
                    referenceCamera.targetTexture = originalReferenceTarget;
                    referenceCamera.transform.SetPositionAndRotation(originalCameraPosition, originalCameraRotation);
                    referenceCamera.aspect = originalReferenceAspect;
                    referenceCamera.allowHDR = originalReferenceHdr;
                    referenceCamera.allowMSAA = originalReferenceMsaa;
                    referenceCamera.allowDynamicResolution = originalReferenceDynamicResolution;
                    UniversalAdditionalCameraData data = referenceCamera.GetComponent<UniversalAdditionalCameraData>();
                    if (data != null) { data.antialiasing = originalReferenceAA; data.renderPostProcessing = originalReferencePostProcessing; }
                }
            }
            catch { }
            try
            {
                if (sourceCamera != null)
                {
                    sourceCamera.allowHDR = originalSourceHdr;
                    sourceCamera.allowMSAA = originalSourceMsaa;
                    sourceCamera.allowDynamicResolution = originalSourceDynamicResolution;
                    UniversalAdditionalCameraData data = sourceCamera.GetComponent<UniversalAdditionalCameraData>();
                    if (data != null) { data.antialiasing = originalSourceAA; data.renderPostProcessing = originalSourcePostProcessing; }
                }
            }
            catch { }
            try { QualitySettings.antiAliasing = originalQualityAntiAliasing; } catch { }
            try { if (guard != null) guard.ReleaseQuiltRenderResources(); } catch { }
        }

        void RestoreAuthoredCameraEnabledStates()
        {
            for (int i = 0; i < authoredCameras.Length && i < authoredCameraEnabledBefore.Length; ++i)
                if (authoredCameras[i] != null) authoredCameras[i].enabled = authoredCameraEnabledBefore[i];
        }

        void DestroyReadbacks()
        {
            try { if (quiltReadback != null) UnityEngine.Object.DestroyImmediate(quiltReadback); } catch { }
            quiltReadback = null;
        }

        void WriteEvidence(Exception failure)
        {
            failureType = failure == null ? "" : failure.GetType().Name;
            failureMessage = failure == null ? "" : Ascii(failure.Message);
            Add("schema", "viewweave-frustum-memory-probe-v1");
            Add("status", failure == null ? "observed" : "failed");
            Add("failurePhase", failurePhase);
            Add("failureType", failureType);
            Add("failureMessage", failureMessage);
            Add("graphName", graphLabel);
            Add("graphGuid", graphGuid);
            Add("graphPath", graphPath);
            Add("graphSha256Before", graphShaBefore);
            Add("graphSha256After", graphShaAfter);
            Add("graphSourceUnchanged", sourceAssetUnchanged);
            Add("scene", ScenePath);
            Add("sceneSha256Before", sceneShaBefore);
            Add("sceneSha256After", sceneShaAfter);
            Add("sourceLearningSceneSha256Before", sourceSceneShaBefore);
            Add("sourceLearningSceneSha256After", sourceSceneShaAfter);
            Add("sourceLearningSceneDiskUnchanged", sourceSceneDiskUnchanged);
            Add("sceneDiskUnchanged", sceneDiskUnchanged);
            Add("sceneSavedDuringProbe", sceneSavedDuringProbe);
            Add("graphicsDevice", SystemInfo.graphicsDeviceType.ToString());
            Add("pipeline", GraphicsSettings.currentRenderPipeline == null ? "null" : GraphicsSettings.currentRenderPipeline.GetType().FullName);
            Add("registeredPackages", packageSummary);
            Add("vfxCount", sceneEffects.Length);
            Add("activeVfxAfterCleanup", activeEffectCountAfterCleanup);
            Add("unrelatedAuthoredCameraCount", authoredCameras.Length - 1);
            Add("referenceCameraOriginalEnabled", originalReferenceEnabled);
            Add("referenceCameraAllowHDR", originalReferenceHdr);
            Add("referenceCameraAllowMSAAOriginal", originalReferenceMsaa);
            Add("referenceCameraDynamicResolutionOriginal", originalReferenceDynamicResolution);
            Add("referenceCameraPostProcessingOriginal", originalReferencePostProcessing);
            Add("sourceCameraAllowHDR", originalSourceHdr);
            Add("sourceCameraPostProcessing", originalSourcePostProcessing);
            Add("referenceCameraAAOriginal", originalReferenceAA.ToString());
            Add("sourceCameraAAOriginal", originalSourceAA.ToString());
            Add("referenceCameraOriginalPosition", VectorText(originalCameraPosition));
            Add("referenceCameraOriginalRotation", QuaternionText(originalCameraRotation));
            Add("referenceCameraOriginalForward", VectorText(originalCameraForward));
            Add("referenceCameraInsidePosition", VectorText(insidePosition));
            Add("referenceCameraProbeRotation", QuaternionText(probeCameraRotation));
            Add("horizontalPanAxis", VectorText(horizontalPan));
            Add("outsidePanDistance", outsidePanDistance.ToString("R", CultureInfo.InvariantCulture));
            Add("quiltViews", 1);
            Add("quiltDimensions", Width + "x" + Height);
            Add("quiltViewRenderMode", quilt == null ? "unavailable" : quilt.viewRenderMode.ToString());
            Add("effectiveHistoryCameraCountAfterCleanup", historyCountAfterCleanup);
            Add("historyReleased", historyReleased);
            Add("quiltFinalGraphicsFormatAtRender", quiltFormatAtRun);
            Add("quiltTextureReleased", quiltTextureReleased);
            Add("referenceTargetGraphicsFormatAtRender", referenceFormatAtRun);
            Add("referenceTargetReleased", referenceTextureReleased);
            Add("historyAAOff", cameraAAOff);
            Add("historyAAAtRender", historyAaAtRun);
            Add("historyPostProcessingAtRender", historyPostProcessingAtRun);
            Add("historyCameraFramingMatricesMatched", cameraEventPairMatched);
            Add("framingCameraRenderingEnabled", referenceCamera != null && referenceCamera.enabled);
            Add("targetErrorCount", targetErrorCount);
            Add("runtimeParticleSystems", string.Join("|", runtimeSystems.ToArray()));
            Add("batchedEffectInfoCheckpoints", batchInfoRows.ToString());
            Add("runtimeDataValid", runtimeDataValid);
            Add("activeObjectActivatedExactlyOnce", activeObjectActivatedExactlyOnce);
            Add("effectRendererEnabledBeforeActivation", effectRendererEnabledBeforeActivation);
            Add("effectRendererEnabledChangedForProbe", effectRendererEnabledChangedForProbe);
            Add("effectRendererEnabledRestored", effectRendererEnabledRestored);
            Add("stageNames", string.Join(",", StageNames));
            Add("expectedGeometricFrustumStates", "false,true,false,true");
            Add("setupBaselineRawGraphicsBytes", setupSafety == null ? 0 : setupSafety.rawBaseline);
            Add("setupBaselinePrivateBytes", setupSafety == null ? 0 : setupSafety.privateBaseline);
            Add("setupPeakRawGrowthBytes", setupSafety == null ? 0 : setupSafety.rawPeakDelta);
            Add("setupPeakPrivateGrowthBytes", setupSafety == null ? 0 : setupSafety.privatePeakDelta);
            Add("activationBaselineRawGraphicsBytes", activationSafety == null ? 0 : activationSafety.rawBaseline);
            Add("activationBaselinePrivateBytes", activationSafety == null ? 0 : activationSafety.privateBaseline);
            Add("activationPeakRawGrowthBytes", activationSafety == null ? 0 : activationSafety.rawPeakDelta);
            Add("activationPeakPrivateGrowthBytes", activationSafety == null ? 0 : activationSafety.privatePeakDelta);
            Add("observedGrowthStopBytes", StopBytes);
            Add("setupCountersValid", setupCountersValid);
            Add("activationCountersValid", activationCountersValid);
            Add("safetyStopReached", safetyStopReached);
            Add("activeYieldFrameCount", activeYieldFrameCount);
            Add("stageSampleCount", stageSampleCount);
            Add("effectMaxAlive", maxAlive);
            Add("offCaptures", JsonUtility.ToJson(new CaptureArray { values = offCaptures }));
            Add("onCaptures", JsonUtility.ToJson(new CaptureArray { values = onCaptures }));
            Add("normalCameraRendering", false);
            Add("quiltInsideChangedPixels", onCaptures[1] == null ? -1 : onCaptures[1].quiltVsOffChanged);
            Add("quiltReentryChangedPixels", onCaptures[3] == null ? -1 : onCaptures[3].quiltVsOffChanged);
            File.WriteAllText(Path.Combine(evidenceDirectory, "evidence.txt"), evidence.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(evidenceDirectory, "result.json"), BuildResultJson(failure), new UTF8Encoding(false));
        }

        [Serializable]
        sealed class CaptureArray { public PhaseCapture[] values; }

        string BuildResultJson(Exception failure)
        {
            var data = new ResultSummary
            {
                schema = "viewweave-frustum-memory-result-v1",
                status = failure == null ? "observed" : "failed",
                failurePhase = failure == null ? "" : failurePhase,
                failureType = failure == null ? "" : failure.GetType().Name,
                failureCode = failure == null ? "" : ClassifyFailure(failure),
                graphName = graphLabel,
                graphGuid = graphGuid,
                graphPath = graphPath,
                graphSha256Before = graphShaBefore,
                graphSha256After = graphShaAfter,
                graphSourceUnchanged = sourceAssetUnchanged,
                sceneSha256Before = sceneShaBefore,
                sceneSha256After = sceneShaAfter,
                sceneDiskUnchanged = sceneDiskUnchanged,
                sourceSceneSha256Before = sourceSceneShaBefore,
                sourceSceneSha256After = sourceSceneShaAfter,
                sourceSceneDiskUnchanged = sourceSceneDiskUnchanged,
                sceneSavedDuringProbe = sceneSavedDuringProbe,
                graphicsDevice = SystemInfo.graphicsDeviceType.ToString(),
                unityVersion = Application.unityVersion,
                registeredPackages = packageSummary,
                effectCount = sceneEffects.Length,
                historyCameraCountAfterCleanup = historyCountAfterCleanup,
                historyReleased = historyReleased,
                quiltTextureReleased = quiltTextureReleased,
                referenceTextureReleased = referenceTextureReleased,
                targetErrorCount = targetErrorCount,
                maxAlive = maxAlive,
                effectRendererEnabledBeforeActivation = effectRendererEnabledBeforeActivation,
                effectRendererEnabledChangedForProbe = effectRendererEnabledChangedForProbe,
                effectRendererEnabledRestored = effectRendererEnabledRestored,
                stageSampleCount = stageSampleCount,
                activeYieldFrameCount = activeYieldFrameCount,
                observedGrowthStopBytes = StopBytes,
                setupCountersValid = setupCountersValid,
                activationCountersValid = activationCountersValid,
                safetyStopReached = safetyStopReached,
                setupRawBaseline = setupSafety == null ? 0 : setupSafety.rawBaseline,
                setupPrivateBaseline = setupSafety == null ? 0 : setupSafety.privateBaseline,
                setupRawPeakGrowth = setupSafety == null ? 0 : setupSafety.rawPeakDelta,
                setupPrivatePeakGrowth = setupSafety == null ? 0 : setupSafety.privatePeakDelta,
                activationRawBaseline = activationSafety == null ? 0 : activationSafety.rawBaseline,
                activationPrivateBaseline = activationSafety == null ? 0 : activationSafety.privateBaseline,
                activationRawPeakGrowth = activationSafety == null ? 0 : activationSafety.rawPeakDelta,
                activationPrivatePeakGrowth = activationSafety == null ? 0 : activationSafety.privatePeakDelta,
                referenceTargetGraphicsFormatAtRender = referenceFormatAtRun,
                quiltFinalGraphicsFormatAtRender = quiltFormatAtRun,
                historyAaAtRender = historyAaAtRun,
                historyPostProcessingAtRender = historyPostProcessingAtRun,
                batchInfoCheckpoints = batchInfoRows.ToString(),
                frameSamples = rows.ToString(),
                offCaptures = offCaptures,
                onCaptures = onCaptures
            };
            return JsonUtility.ToJson(data, true);
        }

        [Serializable]
        sealed class ResultSummary
        {
            public string schema, status, failurePhase, failureType, failureCode, graphName, graphGuid, graphPath, graphSha256Before, graphSha256After, graphicsDevice, unityVersion, registeredPackages;
            public string referenceTargetGraphicsFormatAtRender, quiltFinalGraphicsFormatAtRender, historyAaAtRender, historyPostProcessingAtRender;
            public string batchInfoCheckpoints;
            public string frameSamples;
            public bool graphSourceUnchanged, sceneDiskUnchanged, sourceSceneDiskUnchanged, sceneSavedDuringProbe, historyReleased, quiltTextureReleased, referenceTextureReleased, setupCountersValid, activationCountersValid, safetyStopReached;
            public bool effectRendererEnabledBeforeActivation, effectRendererEnabledChangedForProbe, effectRendererEnabledRestored;
            public string sceneSha256Before, sceneSha256After, sourceSceneSha256Before, sourceSceneSha256After;
            public int effectCount, historyCameraCountAfterCleanup, targetErrorCount, maxAlive, stageSampleCount, activeYieldFrameCount;
            public long observedGrowthStopBytes, setupRawBaseline, setupPrivateBaseline, setupRawPeakGrowth, setupPrivatePeakGrowth, activationRawBaseline, activationPrivateBaseline, activationRawPeakGrowth, activationPrivatePeakGrowth;
            public PhaseCapture[] offCaptures, onCaptures;
        }

        static string ClassifyFailure(Exception exception)
        {
            string message = exception == null ? "" : exception.Message;
            if (message.IndexOf("512MiB", StringComparison.OrdinalIgnoreCase) >= 0) return "observed-growth-stop";
            if (message.IndexOf("frustum", StringComparison.OrdinalIgnoreCase) >= 0) return "frustum-phase-gate";
            return exception == null ? "" : "probe-exception";
        }

        void Add(string key, object value) => evidence.Append(key).Append('=').Append(Ascii(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")).AppendLine();

        static int CountActiveSceneEffects() => UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Count(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded && value.gameObject.activeInHierarchy);

        static int CountNonBlack(Unity.Collections.NativeArray<Color32> pixels)
        {
            int count = 0;
            for (int i = 0; i < pixels.Length; ++i)
            {
                Color32 value = pixels[i];
                if (value.r > 2 || value.g > 2 || value.b > 2) count++;
            }
            return count;
        }

        static int CountChanged(Unity.Collections.NativeArray<Color32> current, Color32[] control)
        {
            Assert.AreEqual(current.Length, control.Length);
            int count = 0;
            for (int i = 0; i < current.Length; ++i)
            {
                Color32 a = current[i];
                Color32 b = control[i];
                int delta = Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
                if (delta > 20) count++;
            }
            return count;
        }

        static void ReadPixels(RenderTexture source, Texture2D target)
        {
            Assert.IsNotNull(source);
            Assert.IsTrue(source.IsCreated());
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                target.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            }
            finally { RenderTexture.active = previous; }
        }

        void SavePng(Texture2D texture, string fileName)
        {
            File.WriteAllBytes(Path.Combine(evidenceDirectory, fileName), texture.EncodeToPNG());
        }

        static string CaptureMainPackages()
        {
            UnityEditor.PackageManager.PackageInfo[] all = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            string[] names = { "com.zabaglione.lookingglass.viewweave.core", "com.zabaglione.lookingglass.viewweave.urp", "com.zabaglione.lookingglass.viewweave.bridge", "com.unity.visualeffectgraph" };
            var output = new List<string>(names.Length);
            foreach (string name in names)
            {
                UnityEditor.PackageManager.PackageInfo[] matching = all.Where(value => value != null && value.name == name).ToArray();
                Assert.AreEqual(1, matching.Length, "Expected exactly one registered package: " + name);
                UnityEditor.PackageManager.PackageInfo package = matching[0];
                if (name.StartsWith("com.zabaglione.", StringComparison.Ordinal))
                {
                    string expected = Path.GetFullPath(Path.Combine("REPO_ROOT/Packages", name));
                    Assert.IsTrue(string.Equals(expected.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(package.resolvedPath ?? "").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), "Package was not resolved from the main SDK source.");
                }
                if (name == "com.unity.visualeffectgraph") Assert.AreEqual("17.6.0", package.version);
                output.Add(name + "@" + package.version + ";source=" + package.source + ";path=" + (package.resolvedPath ?? ""));
            }
            output.Sort(StringComparer.Ordinal);
            return string.Join("|", output.ToArray());
        }

        static string CurrentVfxBinderType()
        {
            Type library = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("UnityEditor.VFX.VFXLibrary", false)).FirstOrDefault(type => type != null);
            if (library == null) return "";
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = library.GetProperty("currentSRPBinder", flags);
            FieldInfo field = library.GetField("currentSRPBinder", flags);
            object binder = property != null ? property.GetValue(null, null) : field == null ? null : field.GetValue(null);
            return binder == null ? "" : binder.GetType().FullName;
        }

        static string ProjectAssetPath(string assetPath) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        static string SafeSha(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
                using (FileStream stream = File.OpenRead(path))
                using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
            catch { return ""; }
        }

        static bool MatrixNear(Matrix4x4 left, Matrix4x4 right)
        {
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                    if (Mathf.Abs(left[row, column] - right[row, column]) > 0.0001f) return false;
            return true;
        }

        static string MatrixText(Matrix4x4 value)
        {
            var builder = new StringBuilder(160);
            for (int row = 0; row < 4; ++row)
            {
                if (row != 0) builder.Append(';');
                for (int column = 0; column < 4; ++column)
                {
                    if (column != 0) builder.Append(',');
                    builder.Append(value[row, column].ToString("R", CultureInfo.InvariantCulture));
                }
            }
            return builder.ToString();
        }

        static string BoundsText(Bounds bounds) => VectorText(bounds.center) + ";" + VectorText(bounds.extents);
        static string VectorText(Vector3 value) => value.x.ToString("R", CultureInfo.InvariantCulture) + "," + value.y.ToString("R", CultureInfo.InvariantCulture) + "," + value.z.ToString("R", CultureInfo.InvariantCulture);
        static string QuaternionText(Quaternion value) => value.x.ToString("R", CultureInfo.InvariantCulture) + "," + value.y.ToString("R", CultureInfo.InvariantCulture) + "," + value.z.ToString("R", CultureInfo.InvariantCulture) + "," + value.w.ToString("R", CultureInfo.InvariantCulture);
        static string Ascii(string value)
        {
            if (value == null) return "";
            var chars = value.Select(character => character >= 32 && character <= 126 ? character : '?').ToArray();
            return new string(chars);
        }
    }
}
#endif
