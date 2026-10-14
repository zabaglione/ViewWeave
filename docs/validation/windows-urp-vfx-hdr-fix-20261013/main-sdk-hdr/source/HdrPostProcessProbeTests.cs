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
using UnityEngine.Experimental.Rendering;
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
    public sealed class HdrPostProcessProbeTests
    {
        const string ScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string GreenGuid = "09e4dfff658c863459dc084250a47a15";
        const string GreenPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/MultiStripGPUEvents.vfx";
        const string CoinGuid = "ccd23b3eb0caff84aab1cd555791352b";
        const string CoinPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/RotationAngle.vfx";
        const int Width = 480;
        const int Height = 360;
        const int MaxFrames = 1200;
        const int MaxSystems = 32;
        const long StopBytes = 512L * 1024L * 1024L;
        const string HdrEvidenceEnvironment = "VW_VFX_HDR_PP_PROBE_EVIDENCE_DIR";

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

        struct Observation
        {
            internal int frame;
            internal long revision;
            internal float elapsed;
            internal int aliveTotal;
            internal int systemCount;
            internal int greenLightsAlive;
            internal int normalLuma;
            internal int quiltLuma;
            internal int normalGreen;
            internal int quiltGreen;
            internal int normalGold;
            internal int quiltGold;
            internal int pairedChanged;
            internal long raw;
            internal long privateBytes;
            internal long workingSet;
            internal float normalFloatPeak;
            internal float quiltFloatPeak;
            internal int normalFloatOverOne;
            internal int quiltFloatOverOne;
        }

        sealed class ImagePair
        {
            internal string label;
            internal int observationIndex;
            internal bool captured;
            internal Color32[] normal;
            internal Color32[] quilt;
        }

        sealed class HdrDescriptorFeature : ScriptableRendererFeature
        {
            internal Camera normalCamera;
            internal Camera[] quiltCameras = Array.Empty<Camera>();
            internal ScriptableRenderer ownerRenderer;
            internal readonly List<string> rows = new List<string>(1024);
            internal bool normalSeen;
            internal readonly bool[] quiltSeen = new bool[1];

            public override void Create() { }

            public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
            {
                Camera camera = renderingData.cameraData.camera;
                bool matched = camera == normalCamera;
                string role = matched ? "normal" : "";
                if (!matched)
                {
                    for (int i = 0; i < quiltCameras.Length; ++i)
                    {
                        if (camera == quiltCameras[i]) { matched = true; role = "quilt-" + i.ToString(CultureInfo.InvariantCulture); break; }
                    }
                }
                if (!matched) return;
                if (role == "normal")
                {
                    if (normalSeen) return;
                    normalSeen = true;
                }
                else
                {
                    int separator = role.LastIndexOf('-');
                    int viewIndex = int.Parse(role.Substring(separator + 1), CultureInfo.InvariantCulture);
                    if (quiltSeen[viewIndex]) return;
                    quiltSeen[viewIndex] = true;
                }
                RenderTextureDescriptor descriptor = renderingData.cameraData.cameraTargetDescriptor;
                UniversalAdditionalCameraData additionalCameraData = camera.GetComponent<UniversalAdditionalCameraData>();
                string volumeSnapshot = DescribeVolumeStack();
                rows.Add("frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) + ";role=" + role +
                    ";camera=" + camera.GetEntityId().ToString() + ";width=" + descriptor.width.ToString(CultureInfo.InvariantCulture) +
                    ";height=" + descriptor.height.ToString(CultureInfo.InvariantCulture) + ";graphicsFormat=" + descriptor.graphicsFormat.ToString() +
                    ";depthBits=" + descriptor.depthBufferBits.ToString(CultureInfo.InvariantCulture) + ";msaa=" + descriptor.msaaSamples.ToString(CultureInfo.InvariantCulture) +
                    ";sRGB=" + descriptor.sRGB.ToString(CultureInfo.InvariantCulture) + ";hdr=" + renderingData.cameraData.isHdrEnabled.ToString() +
                    ";postProcessEnabled=" + renderingData.cameraData.postProcessEnabled.ToString() +
                    ";rendererType=" + renderer.GetType().FullName + ";rendererIndex=" + RendererIndex(additionalCameraData) +
                    ";volume=" + volumeSnapshot);
            }

            internal static string RendererIndex(UniversalAdditionalCameraData data)
            {
                if (data == null) return "unavailable";
                try
                {
                    PropertyInfo property = data.GetType().GetProperty("rendererIndex", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    object value = property == null ? null : property.GetValue(data);
                    return value == null ? "unavailable" : Convert.ToString(value, CultureInfo.InvariantCulture);
                }
                catch { return "unavailable"; }
            }

            static string DescribeVolumeStack()
            {
                try
                {
                    VolumeStack stack = VolumeManager.instance == null ? null : VolumeManager.instance.stack;
                    if (stack == null || !stack.isValid) return "managerStack=unavailable";
                    Bloom bloom = stack.GetComponent<Bloom>();
                    Tonemapping tonemapping = stack.GetComponent<Tonemapping>();
                    return "source=VolumeManager.instance.stack,valid,bloomActive=" + (bloom != null && bloom.IsActive()).ToString() +
                        ",bloomIntensity=" + (bloom == null ? "unavailable" : bloom.intensity.value.ToString("R", CultureInfo.InvariantCulture)) +
                        ",bloomThreshold=" + (bloom == null ? "unavailable" : bloom.threshold.value.ToString("R", CultureInfo.InvariantCulture)) +
                        ",bloomScatter=" + (bloom == null ? "unavailable" : bloom.scatter.value.ToString("R", CultureInfo.InvariantCulture)) +
                        ",bloomClamp=" + (bloom == null ? "unavailable" : bloom.clamp.value.ToString("R", CultureInfo.InvariantCulture)) +
                        ",tonemappingActive=" + (tonemapping != null && tonemapping.IsActive()).ToString() +
                        ",tonemappingMode=" + (tonemapping == null ? "unavailable" : tonemapping.mode.value.ToString());
                }
                catch { return "managerStack=read-error"; }
            }
        }

        readonly Observation[] observations = new Observation[MaxFrames];
        readonly Dictionary<string, ImagePair> representatives = new Dictionary<string, ImagePair>(StringComparer.Ordinal);
        readonly List<string> particleNames = new List<string>(MaxSystems);
        readonly List<string> missingSrvWarnings = new List<string>(8);
        readonly StringBuilder evidence = new StringBuilder(32768);
        readonly int[,] aliveRows = new int[MaxFrames, MaxSystems];
        VisualEffect[] sceneEffects;

        VisualEffect effect;
        VFXRenderer effectRenderer;
        QuiltRenderer quilt;
        ViewWeaveRig rig;
        UrpQuiltCamera guard;
        Camera sourceCamera;
        Camera referenceCamera;
        RenderTexture referenceOutput;
        RenderTexture originalReferenceTarget;
        RenderTexture previousActive;
        Vector3 savedReferencePosition;
        Quaternion savedReferenceRotation;
        Vector3 adjustedReferencePosition;
        Quaternion adjustedReferenceRotation;
        Vector3 savedReferenceForward;
        Texture2D normalReadback;
        Texture2D quiltReadback;
        Texture2D normalFloatReadback;
        Texture2D quiltFloatReadback;
        Color[] bestNormalFloat;
        Color[] bestQuiltFloat;
        Color[] firstGreenAliveNormalFloat;
        Color[] firstGreenAliveQuiltFloat;
        Color[] peakGreenAliveNormalFloat;
        Color[] peakGreenAliveQuiltFloat;
        Color[] minCoinNormalFloat;
        Color[] minCoinQuiltFloat;
        int currentGreenLightsAlive;
        int firstGreenAliveObservation = -1;
        int firstGreenAliveUnityFrame = -1;
        int peakGreenAliveObservation = -1;
        int peakGreenAliveUnityFrame = -1;
        int peakGreenLightsAlive;
        bool firstGreenAliveCaptured;
        float minCoinNormalHdrPeak = float.PositiveInfinity;
        float minCoinQuiltHdrPeak;
        int minCoinNormalOverOnePixels;
        int minCoinQuiltOverOnePixels;
        int minCoinObservation = -1;
        int minCoinUnityFrame = -1;
        int bestFloatIndex = -1;
        int bestFloatUnityFrame = -1;
        float bestNormalHdrPeak;
        float bestQuiltHdrPeak;
        long normalFloatOverOnePixels;
        long quiltFloatOverOnePixels;
        int bestNormalOverOnePixels;
        int bestQuiltOverOnePixels;
        GraphicsFormat normalTargetFormat;
        GraphicsFormat quiltTargetFormat;
        bool normalTargetSrgb;
        bool quiltTargetSrgb;
        bool normalAllowHdrBefore;
        bool sourceAllowHdrBefore;
        bool normalPostProcessingBefore;
        bool sourcePostProcessingBefore;
        int normalVolumeLayerMaskBefore;
        int sourceVolumeLayerMaskBefore;
        string normalVolumeTriggerBefore = "unavailable";
        string sourceVolumeTriggerBefore = "unavailable";
        readonly List<HdrDescriptorFeature> descriptorFeatures = new List<HdrDescriptorFeature>(4);
        readonly List<string> rendererPerturbationRows = new List<string>(8);
        readonly List<string> observedDescriptorRows = new List<string>(1024);
        bool descriptorObserverCleanupSucceeded = true;
        string activeGraphLabel = "";
        string activeGraphGuid = "";
        string activeGraphPath = "";
        string evidenceDirectory = "";
        string originalGraphShaBefore = "";
        string originalGraphShaAfter = "";
        string phase = "start";
        string visualConclusion = "inconclusive";
        string hdrTargetConclusion = "inconclusive";
        string observationValidity = "inconclusive-not-evaluated";
        string volumeObservationValidity = "not-observed";
        string legacyColorMaskConclusion = "not-evaluated";
        int observationCount;
        int referenceEndFrame = -1;
        int quiltViewEndFrame = -1;
        Matrix4x4 referenceViewAtBegin;
        Matrix4x4 referenceProjectionAtBegin;
        Matrix4x4 quiltViewAtBegin;
        Matrix4x4 quiltProjectionAtBegin;
        int referenceBeginFrame = -1;
        int quiltViewBeginFrame = -1;
        bool normalReferenceRendered;
        bool quiltViewRendered;
        bool cameraMatricesMatch;
        int targetLogErrorCount;
        long rawBaseline;
        long privateBaseline;
        long workingSetBaseline;
        long peakRawDelta;
        long peakPrivateDelta;
        string safetyFailure = "";
        bool countersValid = true;
        int allVfxInactiveAfterCleanup = -1;
        int historyCountAfterCleanup = -1;
        bool quiltTextureReleased;
        bool referenceTargetReleased;
        bool selectedGraphSourceUnchanged;
        string packageRegistrationSummary = "not-captured";
        bool normalCameraEndEventSeen;
        bool quiltCameraEndEventSeen;
        float observationSeconds;
        int greenSignalFramesNormal;
        int greenSignalFramesQuilt;
        int normalGoldMinimum = int.MaxValue;
        int normalGoldMaximum;
        int quiltGoldMinimum = int.MaxValue;
        int quiltGoldMaximum;
        int representativeNormalGreenMaximum = -1;
        int representativeQuiltGreenMaximum = -1;
        int representativeNormalGoldMaximum = -1;
        int representativeQuiltGoldMaximum = -1;
        int observationStartFrame = -1;
        int normalLumaMinimum = int.MaxValue;
        int normalLumaMaximum;
        int quiltLumaMinimum = int.MaxValue;
        int quiltLumaMaximum;
        int bestNormalLuma = int.MaxValue;
        int bestNormalLumaMaximum = int.MinValue;
        int bestNormalGreen = -1;
        int bestQuiltGreen = -1;
        int bestNormalGold = -1;
        int bestQuiltGold = -1;
        bool subscribed;
        string matchedLogSummary = "";

        [UnityTest]
        public IEnumerator ObserveMultiStripGpuEventsOnOriginalSceneWithPostProcessing()
        {
            return ObserveGraph(GreenGuid, GreenPath, "green-multistrip-gpu-events", true);
        }

        [UnityTest]
        public IEnumerator ObserveRotationAngleOnOriginalSceneWithPostProcessing()
        {
            return ObserveGraph(CoinGuid, CoinPath, "rotation-angle-coin", false);
        }

        IEnumerator ObserveGraph(string graphGuid, string graphPath, string label, bool greenTarget)
        {
            activeGraphGuid = graphGuid;
            activeGraphPath = graphPath;
            activeGraphLabel = label;
            evidenceDirectory = Environment.GetEnvironmentVariable(HdrEvidenceEnvironment);
            if (string.IsNullOrWhiteSpace(evidenceDirectory)) Assert.Fail(HdrEvidenceEnvironment + " was not provided by the safe runner.");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            string allowedRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(evidenceDirectory.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase), "Evidence output must remain below project TestResults.");
            Directory.CreateDirectory(evidenceDirectory);
            string evidencePath = Path.Combine(evidenceDirectory, "observations.txt");
            string rowsPath = Path.Combine(evidenceDirectory, "frames.csv");
            if (File.Exists(evidencePath) || File.Exists(rowsPath)) Assert.Fail("Refusing to overwrite prior paired-view evidence.");
            Exception failure = null;
            string failurePhase = "";
            var stack = new Stack<IEnumerator>();
            stack.Push(ObserveGraphCore(graphGuid, graphPath));
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                object current = null;
                try { moved = top.MoveNext(); if (moved) current = top.Current; }
                catch (Exception exception) { failure = exception; failurePhase = phase; break; }
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current;
            }

            phase = "cleanup";
            CleanupSynchronous();
            for (int frame = 0; frame < 8; ++frame)
            {
                yield return null;
                if (rawBaseline > 0) ReadSafetyOnly();
            }
            historyCountAfterCleanup = guard == null ? -1 : guard.HistoryCameraCount;
            quiltTextureReleased = quilt == null || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated();
            referenceTargetReleased = referenceOutput == null || !referenceOutput.IsCreated();
            allVfxInactiveAfterCleanup = CountActiveSceneEffects();
            SaveRepresentatives();
            WriteRows(rowsPath);
            WriteEvidence(evidencePath, failure, observationStartFrame, failurePhase);
            UnsubscribeRenderAndLogEvents();
            DestroyReadbackTextures();

            if (failure != null) throw new AssertionException("Visual probe failed during " + failurePhase + ": " + failure.GetType().Name + ": " + Ascii(failure.Message));
            Assert.AreEqual(0, historyCountAfterCleanup, "Expected the bounded cleanup path to release the isolated history camera.");
            Assert.IsTrue(quiltTextureReleased, "Quilt texture remained created after cleanup.");
            Assert.IsTrue(referenceTargetReleased, "The probe-owned normal-camera RenderTexture remained created after cleanup.");
            Assert.AreEqual(0, allVfxInactiveAfterCleanup, "A VFX GameObject remained active after cleanup.");
            Assert.IsTrue(countersValid, safetyFailure);
            Assert.IsTrue(descriptorObserverCleanupSucceeded, "A descriptor observer remained attached or failed disposal.");
            Assert.Less(peakRawDelta, StopBytes, "The raw graphics growth stop was reached.");
            Assert.Less(peakPrivateDelta, StopBytes, "The process-private growth stop was reached.");
            Assert.AreEqual(0, targetLogErrorCount);
            if (observationValidity.StartsWith("inconclusive-", StringComparison.Ordinal)) Assert.Inconclusive(observationValidity);
        }

        IEnumerator ObserveGraphCore(string graphGuid, string graphPath)
        {
            phase = "load-prepared-scene";
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float loadDeadline = Time.realtimeSinceStartup + 60f;
            while ((!scene.IsValid() || !scene.isLoaded) && Time.realtimeSinceStartup < loadDeadline) yield return null;
            Assert.IsTrue(scene.IsValid() && scene.isLoaded, "Prepared URP scene did not load.");
            Assert.IsTrue(Application.isPlaying, "The visual observation must run in Play Mode.");
            Assert.AreEqual(UnityEngine.Rendering.GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType, "Expected the incident-matching D3D12 graphics API.");
            Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline, "Expected the prepared URP pipeline.");
            packageRegistrationSummary = CaptureMainPackageRegistrations();
            previousActive = RenderTexture.active;

            sceneEffects = UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded).ToArray();
            Assert.AreEqual(33, sceneEffects.Length, "Expected all 33 prepared scene VisualEffect components, including inactive objects.");
            Assert.AreEqual(0, CountActiveSceneEffects(), "All copied VFX components must be inactive before the probe.");
            VisualEffect[] matches = sceneEffects.Where(value =>
            {
                string path = AssetDatabase.GetAssetPath(value.visualEffectAsset);
                return !string.IsNullOrEmpty(path) && string.Equals(AssetDatabase.AssetPathToGUID(path), activeGraphGuid, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            Assert.AreEqual(1, matches.Length, "Expected exactly one prepared effect with the selected graph GUID.");
            effect = matches[0];
            Assert.IsFalse(effect.gameObject.activeInHierarchy, "The selected effect must be inactive before import and warmup.");
            Assert.AreEqual(activeGraphGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect.visualEffectAsset)));
            effectRenderer = effect.GetComponent<VFXRenderer>();
            Assert.IsNotNull(effectRenderer, "The selected effect has no VFXRenderer.");
            Assert.IsTrue(effect.enabled, "The original selected VisualEffect component is not enabled in the prepared scene.");
            Assert.IsFalse(effect.pause, "The original selected VisualEffect is paused in the prepared scene.");
            Assert.Greater(effect.playRate, 0f, "The original selected VisualEffect play rate is not positive.");

            rig = Resources.FindObjectsOfTypeAll<ViewWeaveRig>().Single(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded);
            quilt = rig.GetComponent<QuiltRenderer>();
            Assert.IsNotNull(quilt);
            Assert.IsFalse(rig.AutoRender, "Rig autorender must start off during selected import.");
            Assert.IsFalse(quilt.autoRender, "Quilt autorender must start off during selected import.");
            Assert.AreEqual("UnityEditor.VFX.URP.VFXURPBinder", CurrentVfxBinderType(), "VFX binder does not match URP.");
            guard = quilt.sourceCamera.GetComponent<UrpQuiltCamera>();
            sourceCamera = quilt.sourceCamera;
            referenceCamera = quilt.referenceCamera;
            Assert.IsNotNull(sourceCamera);
            Assert.IsFalse(sourceCamera.enabled, "The dedicated quilt source camera must stay disabled outside quilt rendering.");
            Assert.IsNotNull(referenceCamera);
            Assert.IsTrue(referenceCamera.enabled, "The normal reference camera must remain enabled for the paired view.");
            Assert.IsNotNull(guard);
            Assert.AreEqual(UrpQuiltRenderingMode.IsolatedViewHistories, guard.renderingMode);
            Assert.AreEqual(4096, guard.historyMemoryBudgetMB, "The SDK default history budget must remain 4096 MiB.");

            originalGraphShaBefore = Sha256(AbsoluteAssetPath(activeGraphPath));
            Assert.IsTrue(RegexGuidMatches(activeGraphPath, activeGraphGuid), "Selected graph path/GUID mismatch.");
            AssetDatabase.ImportAsset(activeGraphPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            VisualEffectAsset selectedAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(activeGraphPath);
            Assert.IsNotNull(selectedAsset, "Selected VFX asset did not reload after the bounded synchronous import.");
            Assert.AreEqual(activeGraphGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(selectedAsset)));
            Assert.AreEqual(activeGraphGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect.visualEffectAsset)), "Import changed the effect's selected graph GUID.");
            originalGraphShaAfter = Sha256(AbsoluteAssetPath(activeGraphPath));
            selectedGraphSourceUnchanged = string.Equals(originalGraphShaBefore, originalGraphShaAfter, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(selectedGraphSourceUnchanged, "Import changed the selected original graph bytes.");

            phase = "paired-camera-setup";
            ConfigureCamerasAndQuilt();
            PreallocateRepresentativeImages(activeGraphGuid == GreenGuid);
            SubscribeRenderAndLogEvents();
            rig.AutoRender = true;
            long warmupStart = quilt.QuiltRevision;
            yield return WaitForRevision(warmupStart + 8, 60);
            Assert.AreEqual(1, quilt.LastRenderedViewCount, "Warmup did not render exactly one quilt view.");
            Assert.AreEqual(1, guard.HistoryCameraCount, "Warmup did not create exactly one isolated URP history camera.");
            Assert.AreEqual(Width, referenceOutput.width);
            Assert.AreEqual(Height, referenceOutput.height);
            Assert.AreEqual(Width, quilt.QuiltTexture.width);
            Assert.AreEqual(Height, quilt.QuiltTexture.height);
            normalTargetFormat = referenceOutput.graphicsFormat;
            quiltTargetFormat = quilt.QuiltTexture.graphicsFormat;
            normalTargetSrgb = referenceOutput.sRGB;
            quiltTargetSrgb = quilt.QuiltTexture.sRGB;
            normalAllowHdrBefore = referenceCamera.allowHDR;
            Assert.AreEqual(AntialiasingMode.None, referenceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing);
            Assert.IsTrue(referenceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing);
            Assert.AreEqual(AntialiasingMode.None, guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>().antialiasing);
            Assert.IsTrue(guard.GetHistoryCamera(0).GetComponent<UniversalAdditionalCameraData>().renderPostProcessing);
            InstallDescriptorObservers();
            long descriptorStart = quilt.QuiltRevision;
            yield return WaitForRevision(descriptorStart + 2, 60);
            AssertDescriptorObservation("normal");
            AssertDescriptorObservation("quilt-0");
            Assert.IsTrue(DescriptorPostProcessingEnabled("normal"), "URP did not report post-processing enabled for the normal reference camera.");
            Assert.IsTrue(DescriptorPostProcessingEnabled("quilt-0"), "URP did not report post-processing enabled for the quilt history camera.");
            volumeObservationValidity = ValidateVolumeObservation("normal", "quilt-0");
            if (!normalAllowHdrBefore || !DescriptorHdrEnabled("normal"))
            {
                hdrTargetConclusion = !normalAllowHdrBefore
                    ? "normal-camera-allowHDR-was-disabled-before-probe"
                    : "normal-camera-target-descriptor-reported-hdr-disabled";
                visualConclusion = hdrTargetConclusion;
                observationValidity = "inconclusive-normal-HDR-target-unavailable";
                yield break;
            }
            normalReadback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true) { name = "VfxProbe.Normal.Readback" };
            quiltReadback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true) { name = "VfxProbe.Quilt.Readback" };
            normalFloatReadback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { name = "VfxProbe.Normal.FloatReadback", hideFlags = HideFlags.HideAndDontSave };
            quiltFloatReadback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true) { name = "VfxProbe.Quilt.FloatReadback", hideFlags = HideFlags.HideAndDontSave };
            bestNormalFloat = new Color[Width * Height];
            bestQuiltFloat = new Color[Width * Height];
            firstGreenAliveNormalFloat = new Color[Width * Height];
            firstGreenAliveQuiltFloat = new Color[Width * Height];
            peakGreenAliveNormalFloat = new Color[Width * Height];
            peakGreenAliveQuiltFloat = new Color[Width * Height];
            minCoinNormalFloat = new Color[Width * Height];
            minCoinQuiltFloat = new Color[Width * Height];
            bool baselineOk = ReadCounters(out rawBaseline, out privateBaseline, out workingSetBaseline);
            Assert.IsTrue(baselineOk, "Raw graphics or Win32 process-memory baseline was zero or unavailable.");
            Assert.Greater(rawBaseline, 0);
            Assert.Greater(privateBaseline, 0);
            Assert.Greater(workingSetBaseline, 0);

            phase = "selected-graph-activation";
            Assert.IsTrue(effect.enabled && !effect.pause && effect.playRate > 0f, "Original VFX playback properties changed before activation.");
            Assert.IsTrue(effect.initialEventName == "OnPlay", "The original graph activation event must remain OnPlay.");
            observationStartFrame = Time.frameCount;
            effect.gameObject.SetActive(true);
            Assert.IsTrue(effect.gameObject.activeInHierarchy && effect.enabled, "The selected VFX did not remain enabled after activation.");
            Assert.IsTrue(effectRenderer.enabled, "The selected VFXRenderer is disabled after activation.");
            CaptureParticleSystemNames();
            AssertOnlySelectedVfxActive();
            float startTime = Time.realtimeSinceStartup;
            long lastRevision = quilt.QuiltRevision;
            phase = "paired-observation-window";
            float observationTimeLimit = activeGraphGuid == GreenGuid ? 20f : 6f;
            int observationFrameLimit = activeGraphGuid == GreenGuid ? 1200 : 360;
            while (observationCount < observationFrameLimit && Time.realtimeSinceStartup - startTime < observationTimeLimit)
            {
                AssertSafety();
                AssertOnlySelectedVfxActive();
                yield return null;
                if (quilt.QuiltRevision <= lastRevision) continue;
                Assert.AreEqual(lastRevision + 1, quilt.QuiltRevision, "Quilt revisions advanced by more than one between paired observations.");
                lastRevision = quilt.QuiltRevision;
                RecordPairedObservation(lastRevision, Time.realtimeSinceStartup - startTime, activeGraphGuid == GreenGuid);
            }
            observationSeconds = Time.realtimeSinceStartup - startTime;
            Assert.Greater(observationCount, 0, "No completed paired camera/quilt frames were observed.");
            Assert.IsTrue(normalReferenceRendered && quiltViewRendered, "A normal or quilt camera end-render event was not observed.");
            Assert.IsTrue(cameraMatricesMatch, "The one-view quilt camera view/projection matrices did not match the normal reference camera.");
            Assert.IsTrue(selectedGraphSourceUnchanged);
            Assert.AreEqual(0, targetLogErrorCount, "A logged error/exception occurred while the selected graph was active.");
            Assert.IsEmpty(missingSrvWarnings, "A missing-SRV warning matched while the selected graph was active.");
            legacyColorMaskConclusion = ClassifyVisualObservation(activeGraphGuid == GreenGuid);
            if (bestNormalHdrPeak <= 1f && bestQuiltHdrPeak <= 1f)
                hdrTargetConclusion = "both-PP-on-targets-remained-within-unit-range-HDR-over-one-not-applicable";
            else if (bestNormalHdrPeak <= 1f)
                hdrTargetConclusion = "normal-PP-on-target-remained-within-unit-range-quilt-retained-rgb-over-one";
            else if (bestQuiltHdrPeak <= 1f)
                hdrTargetConclusion = "normal-PP-on-target-retained-rgb-over-one-quilt-did-not";
            else
                hdrTargetConclusion = "both-PP-on-targets-retained-rgb-over-one-values";
            visualConclusion = hdrTargetConclusion;
            observationValidity = GetObservationValidity(activeGraphGuid == GreenGuid);
        }

        void ConfigureCamerasAndQuilt()
        {
            Assert.IsNotNull(referenceCamera.GetComponent<UniversalAdditionalCameraData>());
            Assert.IsNotNull(sourceCamera.GetComponent<UniversalAdditionalCameraData>());
            UniversalAdditionalCameraData originalReferenceData = referenceCamera.GetComponent<UniversalAdditionalCameraData>();
            UniversalAdditionalCameraData originalSourceData = sourceCamera.GetComponent<UniversalAdditionalCameraData>();
            normalAllowHdrBefore = referenceCamera.allowHDR;
            sourceAllowHdrBefore = sourceCamera.allowHDR;
            normalPostProcessingBefore = originalReferenceData.renderPostProcessing;
            sourcePostProcessingBefore = originalSourceData.renderPostProcessing;
            Assert.IsTrue(normalPostProcessingBefore, "The original normal reference camera must have post-processing enabled before this comparison.");
            Assert.IsTrue(sourcePostProcessingBefore, "The original source camera must have post-processing enabled before this comparison.");
            normalVolumeLayerMaskBefore = originalReferenceData.volumeLayerMask.value;
            sourceVolumeLayerMaskBefore = originalSourceData.volumeLayerMask.value;
            normalVolumeTriggerBefore = originalReferenceData.volumeTrigger == null ? "null" : originalReferenceData.volumeTrigger.GetEntityId().ToString();
            sourceVolumeTriggerBefore = originalSourceData.volumeTrigger == null ? "null" : originalSourceData.volumeTrigger.GetEntityId().ToString();
            savedReferencePosition = referenceCamera.transform.position;
            savedReferenceRotation = referenceCamera.transform.rotation;
            savedReferenceForward = referenceCamera.transform.forward;
            Vector3 target = effect.transform.position + Vector3.up;
            referenceCamera.transform.position = target - savedReferenceForward.normalized * 3f;
            referenceCamera.transform.LookAt(target);
            referenceCamera.transform.localScale = Vector3.one;
            adjustedReferencePosition = referenceCamera.transform.position;
            adjustedReferenceRotation = referenceCamera.transform.rotation;
            referenceCamera.clearFlags = CameraClearFlags.SolidColor;
            referenceCamera.backgroundColor = Color.black;
            referenceCamera.allowMSAA = false;
            referenceCamera.allowDynamicResolution = false;
            referenceCamera.aspect = 4f / 3f;
            referenceCamera.fieldOfView = 50f;
            referenceCamera.nearClipPlane = sourceCamera.nearClipPlane;
            referenceCamera.farClipPlane = sourceCamera.farClipPlane;
            referenceCamera.cullingMask = sourceCamera.cullingMask;
            referenceCamera.usePhysicalProperties = sourceCamera.usePhysicalProperties;
            referenceCamera.lensShift = sourceCamera.lensShift;
            var referenceData = referenceCamera.GetComponent<UniversalAdditionalCameraData>();
            referenceData.antialiasing = AntialiasingMode.None;
            referenceData.renderPostProcessing = true;
            referenceData.allowXRRendering = false;
            referenceData.allowHDROutput = false;
            originalReferenceTarget = referenceCamera.targetTexture;
            referenceOutput = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "HdrTargetProbe.Reference480x360",
                antiAliasing = 1,
                useDynamicScale = false
            };
            Assert.IsTrue(referenceOutput.Create(), "Could not create the probe-owned 480x360 linear ARGBHalf target.");
            referenceCamera.targetTexture = referenceOutput;
            referenceCamera.enabled = true;
            Assert.AreEqual(normalAllowHdrBefore, referenceCamera.allowHDR, "The probe changed the reference camera allowHDR setting.");

            sourceCamera.clearFlags = CameraClearFlags.SolidColor;
            sourceCamera.backgroundColor = Color.black;
            sourceCamera.allowMSAA = false;
            sourceCamera.allowDynamicResolution = false;
            sourceCamera.aspect = 4f / 3f;
            sourceCamera.fieldOfView = 50f;
            sourceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing = AntialiasingMode.None;
            sourceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing = true;
            Assert.AreEqual(sourceAllowHdrBefore, sourceCamera.allowHDR, "The probe changed the source camera allowHDR setting.");
            QualitySettings.antiAliasing = 0;
            rig.SetOutput(null);
            rig.SetGuard(guard);
            rig.ApplyProfile(new QuiltQualityProfile(Width, Height, 1, 1, 1, 4f / 3f, 0f));
            quilt.settings.fieldOfView = 50f;
            quilt.settings.cameraDistance = 3f;
            quilt.settings.viewCone = 0f;
            quilt.viewRenderMode = QuiltViewRenderMode.Full;
            quilt.followReferenceFieldOfView = true;
            quilt.useRenderRequests = true;
            quilt.autoRender = false;
            Assert.IsTrue(rig.ValidateRig(out string error), error ?? "Prepared rig failed validation.");
            Assert.AreSame(referenceCamera, quilt.referenceCamera);
            Assert.AreSame(sourceCamera, quilt.sourceCamera);
            Assert.IsFalse(sourceCamera.enabled);
            Assert.IsTrue(referenceCamera.enabled);
            Assert.IsNotNull(referenceCamera.targetTexture);
            Assert.AreEqual(Width, referenceCamera.targetTexture.width);
            Assert.AreEqual(Height, referenceCamera.targetTexture.height);
        }

        void InstallDescriptorObservers()
        {
            Camera historyCamera = guard.GetHistoryCamera(0);
            Assert.IsNotNull(historyCamera, "The quilt history camera is unavailable for the target-descriptor probe.");
            var targets = new[] { referenceCamera, historyCamera };
            var renderers = new List<ScriptableRenderer>(2);
            for (int i = 0; i < targets.Length; ++i)
            {
                UniversalAdditionalCameraData data = targets[i].GetComponent<UniversalAdditionalCameraData>();
                Assert.IsNotNull(data, "A probed camera has no UniversalAdditionalCameraData.");
                ScriptableRenderer renderer = data.scriptableRenderer;
                Assert.IsNotNull(renderer, "A probed camera has no live ScriptableRenderer.");
                if (!renderers.Contains(renderer)) renderers.Add(renderer);
            }

            foreach (ScriptableRenderer renderer in renderers)
            {
                IList<ScriptableRendererFeature> features = GetLiveRendererFeatures(renderer);
                int activeBefore = features.Count(value => value != null && value.isActive);
                string intermediateMode = GetIntermediateTextureMode(renderer);
                rendererPerturbationRows.Add("renderer=" + renderer.GetType().FullName + ";intermediateTextureMode=" + intermediateMode + ";activeFeaturesBefore=" + activeBefore.ToString(CultureInfo.InvariantCulture));
                Assert.IsFalse(string.Equals(intermediateMode, "Always", StringComparison.OrdinalIgnoreCase) && activeBefore == 0,
                    "Observer injection would activate the Always-intermediate-texture predicate for a renderer with no existing active features.");

                var observer = ScriptableObject.CreateInstance<HdrDescriptorFeature>();
                descriptorFeatures.Add(observer);
                observer.name = "HdrTargetDescriptorObserver";
                observer.hideFlags = HideFlags.HideAndDontSave;
                observer.normalCamera = referenceCamera;
                observer.quiltCameras = new[] { historyCamera };
                observer.ownerRenderer = renderer;
                observer.Create();
                features.Add(observer);
                int activeAfter = features.Count(value => value != null && value.isActive);
                bool alwaysPredicateBefore = string.Equals(intermediateMode, "Always", StringComparison.OrdinalIgnoreCase) && activeBefore > 0;
                bool alwaysPredicateAfter = string.Equals(intermediateMode, "Always", StringComparison.OrdinalIgnoreCase) && activeAfter > 0;
                rendererPerturbationRows.Add("renderer=" + renderer.GetType().FullName + ";activeFeaturesAfter=" + activeAfter.ToString(CultureInfo.InvariantCulture) + ";alwaysIntermediatePredicateBefore=" + alwaysPredicateBefore.ToString() + ";alwaysIntermediatePredicateAfter=" + alwaysPredicateAfter.ToString());
                rendererPerturbationRows.Add("rendererIdentity=" + renderer.GetType().FullName + ";normalRendererIndex=" + HdrDescriptorFeature.RendererIndex(referenceCamera.GetComponent<UniversalAdditionalCameraData>()) + ";historyRendererIndex=" + HdrDescriptorFeature.RendererIndex(historyCamera.GetComponent<UniversalAdditionalCameraData>()));
                Assert.AreEqual(alwaysPredicateBefore, alwaysPredicateAfter, "Observer changed the Always-intermediate-texture predicate.");
            }
        }

        static IList<ScriptableRendererFeature> GetLiveRendererFeatures(ScriptableRenderer renderer)
        {
            PropertyInfo property = typeof(ScriptableRenderer).GetProperty("rendererFeatures", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(property, "Could not resolve the live ScriptableRenderer.rendererFeatures property.");
            var features = property.GetValue(renderer) as IList<ScriptableRendererFeature>;
            Assert.IsNotNull(features, "The live renderer feature collection is not mutable as expected.");
            return features;
        }

        static string GetIntermediateTextureMode(ScriptableRenderer renderer)
        {
            for (Type type = renderer.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField("m_IntermediateTextureMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (field != null) return Convert.ToString(field.GetValue(renderer), CultureInfo.InvariantCulture) ?? "null";
                PropertyInfo property = type.GetProperty("intermediateTextureMode", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (property != null) return Convert.ToString(property.GetValue(renderer), CultureInfo.InvariantCulture) ?? "null";
            }
            Assert.Fail("Could not inspect the active renderer intermediate-texture mode.");
            return "unavailable";
        }

        void AssertDescriptorObservation(string role)
        {
            bool found = descriptorFeatures.Any(feature => feature.rows.Any(row => row.IndexOf(";role=" + role + ";", StringComparison.Ordinal) >= 0));
            Assert.IsTrue(found, "The live descriptor observer did not see " + role + " camera rendering.");
        }

        bool DescriptorPostProcessingEnabled(string role)
        {
            string marker = ";role=" + role + ";";
            for (int i = 0; i < descriptorFeatures.Count; ++i)
            {
                HdrDescriptorFeature feature = descriptorFeatures[i];
                for (int row = 0; row < feature.rows.Count; ++row)
                {
                    string value = feature.rows[row];
                    if (value.IndexOf(marker, StringComparison.Ordinal) < 0) continue;
                    int start = value.IndexOf(";postProcessEnabled=", StringComparison.Ordinal);
                    if (start < 0) return false;
                    start += ";postProcessEnabled=".Length;
                    int end = value.IndexOf(';', start);
                    return string.Equals(end < 0 ? value.Substring(start) : value.Substring(start, end - start), "True", StringComparison.Ordinal);
                }
            }
            return false;
        }

        string ValidateVolumeObservation(string firstRole, string secondRole)
        {
            string first = DescriptorVolumeSnapshot(firstRole);
            string second = DescriptorVolumeSnapshot(secondRole);
            if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return "inconclusive-volume-stack-row-missing";
            if (!first.StartsWith("source=VolumeManager.instance.stack,valid,", StringComparison.Ordinal) ||
                !second.StartsWith("source=VolumeManager.instance.stack,valid,", StringComparison.Ordinal))
                return "inconclusive-volume-manager-stack-unavailable";
            string[] requiredFields = { "bloomActive", "bloomIntensity", "bloomThreshold", "bloomScatter", "bloomClamp", "tonemappingActive", "tonemappingMode" };
            foreach (string field in requiredFields)
                if (VolumeField(first, field) == "unavailable" || VolumeField(second, field) == "unavailable")
                    return "inconclusive-volume-stack-field-unavailable-" + field;
            Assert.AreEqual("True", VolumeField(first, "bloomActive"), "Bloom was not active for the first camera observation.");
            Assert.AreEqual("True", VolumeField(second, "bloomActive"), "Bloom was not active for the second camera observation.");
            Assert.AreEqual("True", VolumeField(first, "tonemappingActive"), "Tonemapping was not active for the first camera observation.");
            Assert.AreEqual("True", VolumeField(second, "tonemappingActive"), "Tonemapping was not active for the second camera observation.");
            Assert.AreEqual("ACES", VolumeField(first, "tonemappingMode"), "The first camera did not report ACES tonemapping.");
            Assert.AreEqual("ACES", VolumeField(second, "tonemappingMode"), "The second camera did not report ACES tonemapping.");
            string[] matchingFields = { "bloomIntensity", "bloomThreshold", "bloomScatter", "bloomClamp", "tonemappingMode" };
            foreach (string field in matchingFields)
            {
                Assert.AreEqual(VolumeField(first, field), VolumeField(second, field), "Volume stack values differed between the paired cameras for " + field + ".");
            }
            Assert.Greater(float.Parse(VolumeField(first, "bloomIntensity"), CultureInfo.InvariantCulture), 0f, "Observed Bloom intensity was not positive.");
            return "observed-valid-volume-manager-stacks-and-matching-bloom-ACES-values";
        }

        string DescriptorVolumeSnapshot(string role)
        {
            string marker = ";role=" + role + ";";
            for (int i = 0; i < descriptorFeatures.Count; ++i)
            {
                HdrDescriptorFeature feature = descriptorFeatures[i];
                for (int row = 0; row < feature.rows.Count; ++row)
                {
                    string value = feature.rows[row];
                    if (value.IndexOf(marker, StringComparison.Ordinal) < 0) continue;
                    int start = value.IndexOf(";volume=", StringComparison.Ordinal);
                    return start < 0 ? null : value.Substring(start + ";volume=".Length);
                }
            }
            return null;
        }

        static string VolumeField(string snapshot, string field)
        {
            string marker = field + "=";
            int start = snapshot.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return "unavailable";
            start += marker.Length;
            int end = snapshot.IndexOf(',', start);
            return end < 0 ? snapshot.Substring(start) : snapshot.Substring(start, end - start);
        }

        string GetObservationValidity(bool greenTarget)
        {
            if (observationCount < 2) return "inconclusive-too-few-paired-frame-observations";
            if (!normalReferenceRendered || !quiltViewRendered || !cameraMatricesMatch)
                return "inconclusive-paired-camera-render-or-matrix-evidence-incomplete";
            if (targetLogErrorCount != 0 || missingSrvWarnings.Count != 0)
                return "inconclusive-render-log-or-missing-SRV-diagnostic-present";
            if (volumeObservationValidity.StartsWith("inconclusive-", StringComparison.Ordinal)) return volumeObservationValidity;
            if (greenTarget)
            {
                return firstGreenAliveCaptured
                    ? "usable-observation-native-green-particles-and-paired-targets"
                    : "inconclusive-no-native-green-particle-sample";
            }
            for (int i = 0; i < observationCount; ++i)
                if (observations[i].aliveTotal > 0) return "usable-observation-native-particles-and-paired-targets";
            return "inconclusive-no-native-particle-sample";
        }

        bool DescriptorHdrEnabled(string role)
        {
            string marker = ";role=" + role + ";";
            for (int i = 0; i < descriptorFeatures.Count; ++i)
            {
                HdrDescriptorFeature feature = descriptorFeatures[i];
                for (int row = 0; row < feature.rows.Count; ++row)
                    if (feature.rows[row].IndexOf(marker, StringComparison.Ordinal) >= 0)
                    {
                        string value = feature.rows[row];
                        int start = value.IndexOf(";hdr=", StringComparison.Ordinal);
                        if (start < 0) return false;
                        start += 5;
                        int end = value.IndexOf(';', start);
                        return string.Equals(end < 0 ? value.Substring(start) : value.Substring(start, end - start), "True", StringComparison.Ordinal);
                    }
            }
            return false;
        }

        IEnumerator WaitForRevision(long target, int timeoutFrames)
        {
            int frames = 0;
            while (quilt != null && quilt.QuiltRevision < target && frames++ < timeoutFrames) yield return null;
            Assert.IsNotNull(quilt);
            Assert.GreaterOrEqual(quilt.QuiltRevision, target, "Quilt scheduler did not reach the required warmup revision.");
        }

        void SubscribeRenderAndLogEvents()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            Application.logMessageReceived += OnLogMessage;
            subscribed = true;
        }

        void UnsubscribeRenderAndLogEvents()
        {
            if (!subscribed) return;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            Application.logMessageReceived -= OnLogMessage;
            subscribed = false;
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == referenceCamera)
            {
                referenceBeginFrame = Time.frameCount;
                referenceViewAtBegin = camera.worldToCameraMatrix;
                referenceProjectionAtBegin = camera.projectionMatrix;
            }
            Camera historyCamera = guard == null ? null : guard.GetHistoryCamera(0);
            if (camera == historyCamera)
            {
                quiltViewBeginFrame = Time.frameCount;
                quiltViewAtBegin = camera.worldToCameraMatrix;
                quiltProjectionAtBegin = camera.projectionMatrix;
                cameraMatricesMatch = MatrixNear(referenceViewAtBegin, quiltViewAtBegin, 0.0001f) && MatrixNear(referenceProjectionAtBegin, quiltProjectionAtBegin, 0.0001f);
            }
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == referenceCamera)
            {
                referenceEndFrame = Time.frameCount;
                normalReferenceRendered = true;
                normalCameraEndEventSeen = true;
            }
            Camera historyCamera = guard == null ? null : guard.GetHistoryCamera(0);
            if (camera == historyCamera)
            {
                quiltViewEndFrame = Time.frameCount;
                quiltViewRendered = true;
                quiltCameraEndEventSeen = true;
            }
        }

        void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            bool selectedActive = effect != null && effect.gameObject.activeInHierarchy;
            if (!selectedActive) return;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) targetLogErrorCount++;
            if (condition.IndexOf("missing SRV", StringComparison.OrdinalIgnoreCase) >= 0 || condition.IndexOf("MissingSRV", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string row = "frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) + ";graph=" + activeGraphGuid + ";type=" + type.ToString() + ";message=" + Ascii(condition);
                missingSrvWarnings.Add(row);
                if (missingSrvWarnings.Count <= 8) matchedLogSummary += (matchedLogSummary.Length == 0 ? "" : "|") + row;
            }
        }

        void RecordPairedObservation(long revision, float elapsed, bool greenTarget)
        {
            Assert.Less(observationCount, MaxFrames);
            int pairedFrame = referenceEndFrame;
            Assert.AreEqual(Time.frameCount - 1, pairedFrame, "Normal camera output was not from the immediately previous completed frame.");
            Assert.AreEqual(pairedFrame, quiltViewEndFrame, "Quilt camera render did not complete in the same Unity frame as the normal camera.");
            Assert.AreEqual(pairedFrame, referenceBeginFrame);
            Assert.AreEqual(pairedFrame, quiltViewBeginFrame);
            Assert.IsTrue(cameraMatricesMatch, "Normal/reference and actual one-view quilt camera matrices diverged.");
            Assert.IsNotNull(referenceCamera.targetTexture);
            Assert.IsNotNull(quilt.QuiltTexture);
            Assert.AreEqual(referenceCamera.targetTexture.width, quilt.QuiltTexture.width);
            Assert.AreEqual(referenceCamera.targetTexture.height, quilt.QuiltTexture.height);
            ReadPixels(referenceCamera.targetTexture, normalReadback);
            ReadPixels(quilt.QuiltTexture, quiltReadback);
            var normalNative = normalReadback.GetRawTextureData<Color32>();
            var quiltNative = quiltReadback.GetRawTextureData<Color32>();
            Assert.AreEqual(normalNative.Length, quiltNative.Length);
            int normalLuma = 0, quiltLuma = 0, normalGreen = 0, quiltGreen = 0, normalGold = 0, quiltGold = 0, pairedChanged = 0;
            for (int i = 0; i < normalNative.Length; ++i)
            {
                Color32 n = normalNative[i];
                Color32 q = quiltNative[i];
                normalLuma += Luma(n);
                quiltLuma += Luma(q);
                if (IsGreen(n)) normalGreen++;
                if (IsGreen(q)) quiltGreen++;
                if (IsGold(n)) normalGold++;
                if (IsGold(q)) quiltGold++;
                if (Math.Abs(n.r - q.r) > 20 || Math.Abs(n.g - q.g) > 20 || Math.Abs(n.b - q.b) > 20) pairedChanged++;
            }
            normalLuma /= normalNative.Length;
            quiltLuma /= quiltNative.Length;
            normalLumaMinimum = Math.Min(normalLumaMinimum, normalLuma);
            normalLumaMaximum = Math.Max(normalLumaMaximum, normalLuma);
            quiltLumaMinimum = Math.Min(quiltLumaMinimum, quiltLuma);
            quiltLumaMaximum = Math.Max(quiltLumaMaximum, quiltLuma);
            normalGoldMinimum = Math.Min(normalGoldMinimum, normalGold);
            normalGoldMaximum = Math.Max(normalGoldMaximum, normalGold);
            quiltGoldMinimum = Math.Min(quiltGoldMinimum, quiltGold);
            quiltGoldMaximum = Math.Max(quiltGoldMaximum, quiltGold);
            if (normalGreen >= 12) greenSignalFramesNormal++;
            if (quiltGreen >= 12) greenSignalFramesQuilt++;
            AssertSafety();
            bool memoryOk = ProcessMemoryReader.TryRead(out long privateBytes, out long workingSetBytes);
            Assert.IsTrue(memoryOk, "Win32 process-memory counters became unavailable during the observation.");
            int aliveTotal = ReadAliveCounts(observationCount, out int systemCount);
            float normalFloatPeak = 0f, quiltFloatPeak = 0f;
            int normalOverOne = 0, quiltOverOne = 0;
            ReadHdrFloatMetrics(out normalFloatPeak, out normalOverOne, out quiltFloatPeak, out quiltOverOne);
            if (greenTarget && currentGreenLightsAlive > 0)
            {
                if (!firstGreenAliveCaptured)
                {
                    firstGreenAliveCaptured = true;
                    firstGreenAliveObservation = observationCount;
                    firstGreenAliveUnityFrame = pairedFrame;
                    normalFloatReadback.GetRawTextureData<Color>().CopyTo(firstGreenAliveNormalFloat);
                    quiltFloatReadback.GetRawTextureData<Color>().CopyTo(firstGreenAliveQuiltFloat);
                }
                if (currentGreenLightsAlive > peakGreenLightsAlive)
                {
                    peakGreenLightsAlive = currentGreenLightsAlive;
                    peakGreenAliveObservation = observationCount;
                    peakGreenAliveUnityFrame = pairedFrame;
                    normalFloatReadback.GetRawTextureData<Color>().CopyTo(peakGreenAliveNormalFloat);
                    quiltFloatReadback.GetRawTextureData<Color>().CopyTo(peakGreenAliveQuiltFloat);
                }
            }
            if (!greenTarget && normalFloatPeak < minCoinNormalHdrPeak)
            {
                minCoinNormalHdrPeak = normalFloatPeak;
                minCoinQuiltHdrPeak = quiltFloatPeak;
                minCoinNormalOverOnePixels = normalOverOne;
                minCoinQuiltOverOnePixels = quiltOverOne;
                minCoinObservation = observationCount;
                minCoinUnityFrame = pairedFrame;
                normalFloatReadback.GetRawTextureData<Color>().CopyTo(minCoinNormalFloat);
                quiltFloatReadback.GetRawTextureData<Color>().CopyTo(minCoinQuiltFloat);
            }
            if (Math.Max(normalFloatPeak, quiltFloatPeak) > Math.Max(bestNormalHdrPeak, bestQuiltHdrPeak))
            {
                bestFloatIndex = observationCount;
                bestFloatUnityFrame = pairedFrame;
                bestNormalHdrPeak = normalFloatPeak;
                bestQuiltHdrPeak = quiltFloatPeak;
                bestNormalOverOnePixels = normalOverOne;
                bestQuiltOverOnePixels = quiltOverOne;
                normalFloatReadback.GetRawTextureData<Color>().CopyTo(bestNormalFloat);
                quiltFloatReadback.GetRawTextureData<Color>().CopyTo(bestQuiltFloat);
            }
            normalFloatOverOnePixels = Math.Max(normalFloatOverOnePixels, normalOverOne);
            quiltFloatOverOnePixels = Math.Max(quiltFloatOverOnePixels, quiltOverOne);
            var observation = new Observation
            {
                frame = pairedFrame, revision = revision, elapsed = elapsed, aliveTotal = aliveTotal, systemCount = systemCount, greenLightsAlive = currentGreenLightsAlive,
                normalLuma = normalLuma, quiltLuma = quiltLuma, normalGreen = normalGreen, quiltGreen = quiltGreen,
                normalGold = normalGold, quiltGold = quiltGold, pairedChanged = pairedChanged,
                raw = Profiler.GetAllocatedMemoryForGraphicsDriver(), privateBytes = privateBytes, workingSet = workingSetBytes,
                normalFloatPeak = normalFloatPeak, quiltFloatPeak = quiltFloatPeak,
                normalFloatOverOne = normalOverOne, quiltFloatOverOne = quiltOverOne
            };
            observations[observationCount] = observation;
            UpdateRepresentative("first", observationCount, normalNative, quiltNative, observationCount == 0);
            UpdateRepresentative("normal-luma-min", observationCount, normalNative, quiltNative, normalLuma < bestNormalLuma);
            UpdateRepresentative("normal-luma-max", observationCount, normalNative, quiltNative, normalLuma > bestNormalLumaMaximum);
            bestNormalLuma = Math.Min(bestNormalLuma, normalLuma);
            bestNormalLumaMaximum = Math.Max(bestNormalLumaMaximum, normalLuma);
            if (greenTarget)
            {
                UpdateRepresentative("normal-green-peak", observationCount, normalNative, quiltNative, normalGreen > bestNormalGreen);
                UpdateRepresentative("quilt-green-peak", observationCount, normalNative, quiltNative, quiltGreen > bestQuiltGreen);
                bestNormalGreen = Math.Max(bestNormalGreen, normalGreen);
                bestQuiltGreen = Math.Max(bestQuiltGreen, quiltGreen);
            }
            else
            {
                UpdateRepresentative("normal-gold-peak", observationCount, normalNative, quiltNative, normalGold > bestNormalGold);
                UpdateRepresentative("quilt-gold-peak", observationCount, normalNative, quiltNative, quiltGold > bestQuiltGold);
                bestNormalGold = Math.Max(bestNormalGold, normalGold);
                bestQuiltGold = Math.Max(bestQuiltGold, quiltGold);
            }
            observationCount++;
        }

        void ReadHdrFloatMetrics(out float normalPeak, out int normalOverOne, out float quiltPeak, out int quiltOverOne)
        {
            ReadPixels(referenceCamera.targetTexture, normalFloatReadback);
            ReadPixels(quilt.QuiltTexture, quiltFloatReadback);
            var normal = normalFloatReadback.GetRawTextureData<Color>();
            var quiltPixels = quiltFloatReadback.GetRawTextureData<Color>();
            Assert.AreEqual(normal.Length, quiltPixels.Length);
            normalPeak = 0f;
            quiltPeak = 0f;
            normalOverOne = 0;
            quiltOverOne = 0;
            for (int i = 0; i < normal.Length; ++i)
            {
                Color n = normal[i];
                Color q = quiltPixels[i];
                normalPeak = Math.Max(normalPeak, Math.Max(n.r, Math.Max(n.g, n.b)));
                quiltPeak = Math.Max(quiltPeak, Math.Max(q.r, Math.Max(q.g, q.b)));
                if (n.r > 1f || n.g > 1f || n.b > 1f) normalOverOne++;
                if (q.r > 1f || q.g > 1f || q.b > 1f) quiltOverOne++;
            }
        }

        int ReadAliveCounts(int row, out int systemCount)
        {
            systemCount = particleNames.Count;
            int total = 0;
            currentGreenLightsAlive = 0;
            for (int index = 0; index < particleNames.Count; ++index)
            {
                VFXParticleSystemInfo info = effect.GetParticleSystemInfo(particleNames[index]);
                int alive = checked((int)info.aliveCount);
                aliveRows[row, index] = alive;
                if (particleNames[index].IndexOf("Green Lights", StringComparison.OrdinalIgnoreCase) >= 0)
                    currentGreenLightsAlive = alive;
                total = checked(total + alive);
            }
            return total;
        }

        void PreallocateRepresentativeImages(bool greenTarget)
        {
            string[] labels = greenTarget
                ? new[] { "first", "normal-luma-min", "normal-luma-max", "normal-green-peak", "quilt-green-peak" }
                : new[] { "first", "normal-luma-min", "normal-luma-max", "normal-gold-peak", "quilt-gold-peak" };
            foreach (string label in labels)
                representatives.Add(label, new ImagePair { label = label, observationIndex = -1, normal = new Color32[Width * Height], quilt = new Color32[Width * Height] });
        }

        void UpdateRepresentative(string label, int index, Unity.Collections.NativeArray<Color32> normal, Unity.Collections.NativeArray<Color32> quilt, bool update)
        {
            if (!update) return;
            ImagePair pair = representatives[label];
            pair.observationIndex = index;
            pair.captured = true;
            normal.CopyTo(pair.normal);
            quilt.CopyTo(pair.quilt);
        }

        string ClassifyVisualObservation(bool greenTarget)
        {
            if (greenTarget)
            {
                if (greenSignalFramesNormal < 2) return "inconclusive-normal-green-trigger-not-observed-in-multiple-frames";
                return greenSignalFramesQuilt == 0 ? "normal-green-observed-quilt-green-not-observed" : "green-observed-in-both-normal-and-quilt";
            }
            if (observationCount < 2 || normalGoldMaximum - normalGoldMinimum < 12)
                return "inconclusive-normal-coin-brightness-did-not-vary-enough";
            if (quiltGoldMaximum - quiltGoldMinimum < 12) return "normal-coin-varied-quilt-coin-did-not-vary-enough";
            return "coin-brightness-varied-in-both-normal-and-quilt";
        }

        void AssertOnlySelectedVfxActive()
        {
            int activeCount = 0;
            for (int i = 0; i < sceneEffects.Length; ++i)
            {
                VisualEffect candidate = sceneEffects[i];
                if (candidate == null || !candidate.gameObject.activeInHierarchy) continue;
                activeCount++;
                if (candidate != effect) throw new AssertionException("Another copied VFX GameObject became active during the isolated probe.");
            }
            if (activeCount != 1) throw new AssertionException("Expected only the selected original VisualEffect to be active.");
        }

        void AssertSafety()
        {
            long raw = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (raw <= 0) { countersValid = false; safetyFailure = "raw-graphics-counter-unavailable-or-zero"; Assert.Fail(safetyFailure); }
            if (!ProcessMemoryReader.TryRead(out long privateBytes, out long workingSetBytes) || privateBytes <= 0 || workingSetBytes <= 0)
            {
                countersValid = false; safetyFailure = "win32-process-memory-unavailable-or-zero"; Assert.Fail(safetyFailure);
            }
            peakRawDelta = Math.Max(peakRawDelta, Math.Max(0, raw - rawBaseline));
            peakPrivateDelta = Math.Max(peakPrivateDelta, Math.Max(0, privateBytes - privateBaseline));
            if (peakRawDelta >= StopBytes || peakPrivateDelta >= StopBytes)
            {
                countersValid = false;
                safetyFailure = peakRawDelta >= StopBytes ? "raw-growth-stop-512MiB" : "private-growth-stop-512MiB";
                Assert.Fail(safetyFailure);
            }
        }

        bool ReadCounters(out long raw, out long privateBytes, out long workingSet)
        {
            raw = Profiler.GetAllocatedMemoryForGraphicsDriver();
            bool valid = ProcessMemoryReader.TryRead(out privateBytes, out workingSet);
            return raw > 0 && valid && privateBytes > 0 && workingSet > 0;
        }

        void ReadSafetyOnly()
        {
            try { AssertSafety(); }
            catch (Exception exception) { if (string.IsNullOrEmpty(safetyFailure)) safetyFailure = exception.GetType().Name; countersValid = false; }
        }

        void CleanupSynchronous()
        {
            UnsubscribeRenderAndLogEvents();
            DetachDescriptorObservers();
            try { if (rig != null) rig.AutoRender = false; } catch { }
            try { if (quilt != null) { quilt.autoRender = false; quilt.enabled = false; } } catch { }
            try { if (effect != null) effect.gameObject.SetActive(false); } catch { }
            try { if (guard != null) guard.ReleaseQuiltRenderResources(); } catch { }
            try { if (referenceCamera != null) { referenceCamera.enabled = false; referenceCamera.targetTexture = originalReferenceTarget; } } catch { }
            try { if (referenceOutput != null) { referenceOutput.Release(); UnityEngine.Object.Destroy(referenceOutput); } } catch { }
            try { if (normalReadback != null) UnityEngine.Object.Destroy(normalReadback); } catch { }
            try { if (quiltReadback != null) UnityEngine.Object.Destroy(quiltReadback); } catch { }
            try { if (normalFloatReadback != null) UnityEngine.Object.Destroy(normalFloatReadback); } catch { }
            try { if (quiltFloatReadback != null) UnityEngine.Object.Destroy(quiltFloatReadback); } catch { }
            try { RenderTexture.active = previousActive; } catch { }
        }

        void DetachDescriptorObservers()
        {
            foreach (HdrDescriptorFeature observer in descriptorFeatures)
            {
                if (observer == null) continue;
                try
                {
                    if (observer.ownerRenderer != null)
                    {
                        IList<ScriptableRendererFeature> features = GetLiveRendererFeatures(observer.ownerRenderer);
                        bool removed = features.Remove(observer);
                        if (!removed || features.Contains(observer)) descriptorObserverCleanupSucceeded = false;
                    }
                    else descriptorObserverCleanupSucceeded = false;
                }
                catch { descriptorObserverCleanupSucceeded = false; }
                try { observedDescriptorRows.AddRange(observer.rows); } catch { }
                try { observer.Dispose(); } catch { descriptorObserverCleanupSucceeded = false; }
                try { UnityEngine.Object.DestroyImmediate(observer); } catch { descriptorObserverCleanupSucceeded = false; }
            }
            descriptorFeatures.Clear();
        }

        void DestroyReadbackTextures()
        {
            if (normalReadback != null) UnityEngine.Object.Destroy(normalReadback);
            if (quiltReadback != null) UnityEngine.Object.Destroy(quiltReadback);
            if (normalFloatReadback != null) UnityEngine.Object.Destroy(normalFloatReadback);
            if (quiltFloatReadback != null) UnityEngine.Object.Destroy(quiltFloatReadback);
            normalReadback = null;
            quiltReadback = null;
            normalFloatReadback = null;
            quiltFloatReadback = null;
        }

        void SaveRepresentatives()
        {
            foreach (ImagePair pair in representatives.Values.OrderBy(value => value.label, StringComparer.Ordinal))
            {
                if (!pair.captured) continue;
                SavePng(pair.normal, pair.label + "-normal.png");
                SavePng(pair.quilt, pair.label + "-quilt.png");
            }
            if (bestFloatIndex >= 0)
            {
                string normalPath = Path.Combine(evidenceDirectory, "hdr-float-normal.bin");
                string quiltPath = Path.Combine(evidenceDirectory, "hdr-float-quilt.bin");
                WriteFloatPixels(normalPath, bestNormalFloat);
                WriteFloatPixels(quiltPath, bestQuiltFloat);
                File.WriteAllText(Path.Combine(evidenceDirectory, "hdr-float-metadata.txt"),
                    "encoding=float32-ieee754-little-endian-rgba;readbackTextureLinear=true;observationRowIndex=" + bestFloatIndex.ToString(CultureInfo.InvariantCulture) + ";pairedUnityFrame=" + bestFloatUnityFrame.ToString(CultureInfo.InvariantCulture) +
                    ";normalTargetGraphicsFormat=" + normalTargetFormat + ";normalTargetSrgb=" + normalTargetSrgb.ToString() +
                    ";normalCameraAllowHdrBeforeProbe=" + normalAllowHdrBefore.ToString() +
                    ";quiltTargetGraphicsFormat=" + quiltTargetFormat + ";quiltTargetSrgb=" + quiltTargetSrgb.ToString() +
                    ";normalFloatPeak=" + bestNormalHdrPeak.ToString("R", CultureInfo.InvariantCulture) +
                    ";quiltFloatPeak=" + bestQuiltHdrPeak.ToString("R", CultureInfo.InvariantCulture) +
                    ";normalPixelsAnyRgbOverOneAtRepresentative=" + bestNormalOverOnePixels.ToString(CultureInfo.InvariantCulture) +
                    ";quiltPixelsAnyRgbOverOneAtRepresentative=" + bestQuiltOverOnePixels.ToString(CultureInfo.InvariantCulture) +
                    ";pngsAreDiagnosticRGBA32ReadbacksNotRawFloat=true" + Environment.NewLine, new UTF8Encoding(false));
            }
            if (firstGreenAliveCaptured)
            {
                WriteFloatPixels(Path.Combine(evidenceDirectory, "green-lights-first-alive-normal.bin"), firstGreenAliveNormalFloat);
                WriteFloatPixels(Path.Combine(evidenceDirectory, "green-lights-first-alive-quilt.bin"), firstGreenAliveQuiltFloat);
                WriteFloatPixels(Path.Combine(evidenceDirectory, "green-lights-peak-alive-normal.bin"), peakGreenAliveNormalFloat);
                WriteFloatPixels(Path.Combine(evidenceDirectory, "green-lights-peak-alive-quilt.bin"), peakGreenAliveQuiltFloat);
                File.WriteAllText(Path.Combine(evidenceDirectory, "green-lights-float-metadata.txt"),
                    "firstAliveObservation=" + firstGreenAliveObservation.ToString(CultureInfo.InvariantCulture) + ";firstAliveUnityFrame=" + firstGreenAliveUnityFrame.ToString(CultureInfo.InvariantCulture) +
                    ";peakAlive=" + peakGreenLightsAlive.ToString(CultureInfo.InvariantCulture) + ";peakAliveObservation=" + peakGreenAliveObservation.ToString(CultureInfo.InvariantCulture) +
                    ";peakAliveUnityFrame=" + peakGreenAliveUnityFrame.ToString(CultureInfo.InvariantCulture) + ";encoding=float32-ieee754-little-endian-rgba;readbackTextureLinear=true" + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            if (minCoinObservation >= 0)
            {
                WriteFloatPixels(Path.Combine(evidenceDirectory, "coin-min-normal-float-peak.bin"), minCoinNormalFloat);
                WriteFloatPixels(Path.Combine(evidenceDirectory, "coin-min-quilt-paired-float-peak.bin"), minCoinQuiltFloat);
                File.WriteAllText(Path.Combine(evidenceDirectory, "coin-min-float-metadata.txt"),
                    "observation=" + minCoinObservation.ToString(CultureInfo.InvariantCulture) + ";unityFrame=" + minCoinUnityFrame.ToString(CultureInfo.InvariantCulture) +
                    ";normalFloatPeak=" + minCoinNormalHdrPeak.ToString("R", CultureInfo.InvariantCulture) + ";quiltFloatPeak=" + minCoinQuiltHdrPeak.ToString("R", CultureInfo.InvariantCulture) +
                    ";normalPixelsAnyRgbOverOne=" + minCoinNormalOverOnePixels.ToString(CultureInfo.InvariantCulture) + ";quiltPixelsAnyRgbOverOne=" + minCoinQuiltOverOnePixels.ToString(CultureInfo.InvariantCulture) +
                    ";encoding=float32-ieee754-little-endian-rgba;readbackTextureLinear=true" + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }

        static void WriteFloatPixels(string path, Color[] pixels)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
                for (int i = 0; i < pixels.Length; ++i)
                {
                    writer.Write(pixels[i].r); writer.Write(pixels[i].g); writer.Write(pixels[i].b); writer.Write(pixels[i].a);
                }
        }

        void CaptureParticleSystemNames()
        {
            particleNames.Clear();
            effect.GetParticleSystemNames(particleNames);
            if (particleNames.Count > MaxSystems) throw new AssertionException("VFX particle system count exceeds the fixed diagnostic capacity.");
            if (particleNames.Count == 0) throw new AssertionException("The selected VFX reported no particle systems after its original activation event.");
        }

        int CountActiveSceneEffects()
        {
            if (sceneEffects == null) return -1;
            int count = 0;
            for (int i = 0; i < sceneEffects.Length; ++i)
                if (sceneEffects[i] != null && sceneEffects[i].gameObject.activeInHierarchy) count++;
            return count;
        }

        void SavePng(Color32[] pixels, string filename)
        {
            var texture = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(Path.Combine(evidenceDirectory, filename), texture.EncodeToPNG());
            UnityEngine.Object.Destroy(texture);
        }

        void WriteRows(string path)
        {
            var rows = new StringBuilder(64 + observationCount * 160);
            rows.AppendLine("frame,quiltRevision,elapsedSeconds,aliveTotal,systemCount,greenLightsAlive,systemNames,aliveBySystem,normalLuma,quiltLuma,normalGreenPixels,quiltGreenPixels,normalGoldPixels,quiltGoldPixels,pairedChangedPixels,rawGraphicsBytes,privateBytes,workingSetBytes,normalFloatPeak,quiltFloatPeak,normalPixelsAnyRgbOverOne,quiltPixelsAnyRgbOverOne");
            for (int i = 0; i < observationCount; ++i)
            {
                Observation item = observations[i];
                rows.Append(item.frame).Append(',').Append(item.revision).Append(',').Append(item.elapsed.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                    .Append(item.aliveTotal).Append(',').Append(item.systemCount).Append(',').Append(item.greenLightsAlive).Append(',');
                for (int system = 0; system < item.systemCount; ++system)
                {
                    if (system != 0) rows.Append('|');
                    rows.Append(Ascii(particleNames[system]));
                }
                rows.Append(',');
                for (int system = 0; system < item.systemCount; ++system)
                {
                    if (system != 0) rows.Append('|');
                    rows.Append(aliveRows[i, system]);
                }
                rows.Append(',').Append(item.normalLuma).Append(',').Append(item.quiltLuma).Append(',').Append(item.normalGreen).Append(',').Append(item.quiltGreen)
                    .Append(',').Append(item.normalGold).Append(',').Append(item.quiltGold).Append(',').Append(item.pairedChanged).Append(',')
                    .Append(item.raw).Append(',').Append(item.privateBytes).Append(',').Append(item.workingSet).Append(',')
                    .Append(item.normalFloatPeak.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(item.quiltFloatPeak.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(item.normalFloatOverOne).Append(',').Append(item.quiltFloatOverOne).AppendLine();
            }
            File.WriteAllText(path, rows.ToString(), new UTF8Encoding(false));
        }

        void WriteEvidence(string path, Exception failure, int firstFrame, string failurePhase)
        {
            evidence.Clear();
            Add("status", failure == null ? "observed" : "failed");
            Add("visualConclusion", visualConclusion);
            Add("observationValidity", observationValidity);
            Add("volumeObservationValidity", volumeObservationValidity);
            Add("observationValidityMeaning", "Capture validity is separate from the HDR-range comparison; PP-on values at or below one are not a failed observation.");
            Add("coinFramingScope", "The selected original effect was paired at its prepared camera pose; complete coverage of all three coins was not asserted.");
            Add("legacyColorMaskConclusion", legacyColorMaskConclusion);
            Add("hdrTargetConclusion", hdrTargetConclusion);
            Add("featureCompatibilityPass", "not-claimed");
            Add("phase", failure == null ? phase : failurePhase);
            Add("failureType", failure == null ? "" : failure.GetType().Name);
            Add("failureMessageAscii", failure == null ? "" : Ascii(failure.Message));
            Add("graphLabel", activeGraphLabel);
            Add("graphGuid", activeGraphGuid);
            Add("graphPath", activeGraphPath);
            Add("graphSha256Before", originalGraphShaBefore);
            Add("graphSha256After", originalGraphShaAfter);
            Add("graphSourceUnchanged", selectedGraphSourceUnchanged);
            Add("selectedEffectEnabledAtCleanup", effect != null && effect.enabled);
            Add("selectedEffectPause", effect == null ? "unavailable" : effect.pause.ToString());
            Add("selectedEffectPlayRate", effect == null ? "unavailable" : effect.playRate.ToString("R", CultureInfo.InvariantCulture));
            Add("selectedInitialEvent", effect == null ? "unavailable" : effect.initialEventName);
            Add("graphicsApi", SystemInfo.graphicsDeviceType.ToString());
            Add("pipeline", GraphicsSettings.currentRenderPipeline == null ? "null" : GraphicsSettings.currentRenderPipeline.GetType().Name);
            Add("registeredPackages", packageRegistrationSummary);
            Add("scene", ScenePath);
            Add("testPlayMode", Application.isPlaying);
            Add("quiltViews", 1);
            Add("quiltLayout", "1x1");
            Add("quiltDimensions", Width + "x" + Height);
            Add("normalReferenceDimensions", referenceOutput == null ? "unavailable" : referenceOutput.width + "x" + referenceOutput.height);
            Add("normalTargetGraphicsFormat", normalTargetFormat);
            Add("normalTargetSrgb", normalTargetSrgb);
            Add("normalCameraAllowHdrBeforeProbe", normalAllowHdrBefore);
            Add("sourceCameraAllowHdrBeforeProbe", sourceAllowHdrBefore);
            Add("normalCameraAllowHdrDuringProbe", referenceCamera == null ? "unavailable" : referenceCamera.allowHDR.ToString());
            Add("sourceCameraAllowHdrDuringProbe", sourceCamera == null ? "unavailable" : sourceCamera.allowHDR.ToString());
            Add("normalPostProcessingBeforeProbe", normalPostProcessingBefore);
            Add("sourcePostProcessingBeforeProbe", sourcePostProcessingBefore);
            Add("normalPostProcessingDuringProbe", referenceCamera == null ? "unavailable" : referenceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing.ToString());
            Add("sourcePostProcessingDuringProbe", sourceCamera == null ? "unavailable" : sourceCamera.GetComponent<UniversalAdditionalCameraData>().renderPostProcessing.ToString());
            Add("normalVolumeLayerMaskBeforeProbe", normalVolumeLayerMaskBefore);
            Add("sourceVolumeLayerMaskBeforeProbe", sourceVolumeLayerMaskBefore);
            Add("normalVolumeTriggerEntityIdBeforeProbe", normalVolumeTriggerBefore);
            Add("sourceVolumeTriggerEntityIdBeforeProbe", sourceVolumeTriggerBefore);
            Add("normalVolumeTriggerEntityIdDuringProbe", referenceCamera == null ? "unavailable" : (referenceCamera.GetComponent<UniversalAdditionalCameraData>().volumeTrigger == null ? "null" : referenceCamera.GetComponent<UniversalAdditionalCameraData>().volumeTrigger.GetEntityId().ToString()));
            Add("sourceVolumeTriggerEntityIdDuringProbe", sourceCamera == null ? "unavailable" : (sourceCamera.GetComponent<UniversalAdditionalCameraData>().volumeTrigger == null ? "null" : sourceCamera.GetComponent<UniversalAdditionalCameraData>().volumeTrigger.GetEntityId().ToString()));
            Add("normalVolumeLayerMaskDuringProbe", referenceCamera == null ? "unavailable" : referenceCamera.GetComponent<UniversalAdditionalCameraData>().volumeLayerMask.value.ToString(CultureInfo.InvariantCulture));
            Add("sourceVolumeLayerMaskDuringProbe", sourceCamera == null ? "unavailable" : sourceCamera.GetComponent<UniversalAdditionalCameraData>().volumeLayerMask.value.ToString(CultureInfo.InvariantCulture));
            Add("quiltTargetGraphicsFormat", quiltTargetFormat);
            Add("quiltTargetSrgb", quiltTargetSrgb);
            Add("floatReadbackFormat", "RGBAFloat;Texture2D.linear=true;sourceEncodingRecordedSeparately");
            Add("normalFloatPeak", bestNormalHdrPeak.ToString("R", CultureInfo.InvariantCulture));
            Add("quiltFloatPeak", bestQuiltHdrPeak.ToString("R", CultureInfo.InvariantCulture));
            Add("normalMaxObservedPixelsAnyRgbOverOne", normalFloatOverOnePixels);
            Add("quiltMaxObservedPixelsAnyRgbOverOne", quiltFloatOverOnePixels);
            Add("normalPixelsAnyRgbOverOneAtRepresentative", bestNormalOverOnePixels);
            Add("quiltPixelsAnyRgbOverOneAtRepresentative", bestQuiltOverOnePixels);
            Add("floatRepresentativeFrameIndex", bestFloatIndex);
            Add("floatRepresentativeUnityFrame", bestFloatUnityFrame);
            Add("greenLightsFirstAliveCaptured", firstGreenAliveCaptured);
            Add("greenLightsFirstAliveObservation", firstGreenAliveObservation);
            Add("greenLightsFirstAliveUnityFrame", firstGreenAliveUnityFrame);
            Add("greenLightsPeakAlive", peakGreenLightsAlive);
            Add("greenLightsPeakAliveObservation", peakGreenAliveObservation);
            Add("greenLightsPeakAliveUnityFrame", peakGreenAliveUnityFrame);
            Add("coinMinimumNormalFloatPeak", float.IsPositiveInfinity(minCoinNormalHdrPeak) ? "unavailable" : minCoinNormalHdrPeak.ToString("R", CultureInfo.InvariantCulture));
            Add("coinMinimumObservation", minCoinObservation);
            Add("coinMinimumUnityFrame", minCoinUnityFrame);
            Add("normalDescriptorObserverRowCount", observedDescriptorRows.Count(row => row.Contains(";role=normal;")));
            Add("quiltDescriptorObserverRowCount", observedDescriptorRows.Count(row => row.Contains(";role=quilt-0;")));
            Add("rendererPerturbationCheck", string.Join("|", rendererPerturbationRows.ToArray()));
            Add("descriptorObserverCleanupSucceeded", descriptorObserverCleanupSucceeded);
            File.WriteAllLines(Path.Combine(evidenceDirectory, "camera-target-descriptors.txt"), observedDescriptorRows, new UTF8Encoding(false));
            Add("savedReferencePosition", VectorText(savedReferencePosition));
            Add("savedReferenceRotation", QuaternionText(savedReferenceRotation));
            Add("savedReferenceForward", VectorText(savedReferenceForward));
            Add("adjustedReferencePosition", VectorText(adjustedReferencePosition));
            Add("adjustedReferenceRotation", QuaternionText(adjustedReferenceRotation));
            Add("normalCameraAA", "Off");
            Add("quiltCameraAA", "Off");
            Add("postProcessing", "On");
            Add("volumeStackReadbackScope", "VolumeManager.instance.stack snapshot from AddRenderPasses; row includes rendering camera identity and cameraData.postProcessEnabled");
            Add("configuredVolumeComponentsApplied", "not-claimed; stack values and PP flags are observations, not proof of pixel contribution");
            Add("viewCone", 0);
            Add("quiltAutoRender", "scheduler");
            Add("sdkDefaultHistoryBudgetMiB", 4096);
            Add("observedGrowthStopBytes", StopBytes);
            Add("rawGraphicsBaselineBytes", rawBaseline);
            Add("privateBaselineBytes", privateBaseline);
            Add("workingSetBaselineBytes", workingSetBaseline);
            Add("peakRawGrowthBytes", peakRawDelta);
            Add("peakPrivateGrowthBytes", peakPrivateDelta);
            Add("countersValid", countersValid);
            Add("safetyFailure", safetyFailure);
            Add("observationCount", observationCount);
            Add("observationSeconds", observationSeconds.ToString("F4", CultureInfo.InvariantCulture));
            Add("observationFirstFrame", firstFrame);
            Add("observationArrayCapacity", MaxFrames);
            Add("effectiveFrameLimit", activeGraphGuid == GreenGuid ? 1200 : 360);
            Add("secondsLimit", activeGraphGuid == GreenGuid ? 20 : 6);
            Add("normalRenderEndEventObserved", normalCameraEndEventSeen);
            Add("quiltRenderEndEventObserved", quiltCameraEndEventSeen);
            Add("normalEndFrame", referenceEndFrame);
            Add("quiltViewEndFrame", quiltViewEndFrame);
            Add("cameraMatricesMatched", cameraMatricesMatch);
            Add("greenSignalFramesNormal", greenSignalFramesNormal);
            Add("greenSignalFramesQuilt", greenSignalFramesQuilt);
            Add("normalGoldRange", normalGoldMinimum == int.MaxValue ? "unavailable" : normalGoldMinimum + ".." + normalGoldMaximum);
            Add("quiltGoldRange", quiltGoldMinimum == int.MaxValue ? "unavailable" : quiltGoldMinimum + ".." + quiltGoldMaximum);
            Add("normalLumaRange", normalLumaMinimum == int.MaxValue ? "unavailable" : normalLumaMinimum + ".." + normalLumaMaximum);
            Add("quiltLumaRange", quiltLumaMinimum == int.MaxValue ? "unavailable" : quiltLumaMinimum + ".." + quiltLumaMaximum);
            Add("targetErrorCount", targetLogErrorCount);
            Add("matchedMissingSrvWarnings", missingSrvWarnings.Count == 0 ? "none" : string.Join("|", missingSrvWarnings.ToArray()));
            Add("targetWarningSummary", matchedLogSummary);
            Add("completedFrameRows", "frames.csv");
            int capturedPairs = 0;
            foreach (ImagePair pair in representatives.Values) if (pair.captured) capturedPairs++;
            Add("representativeImageCount", capturedPairs * 2);
            Add("cleanupHistoryCameraCount", historyCountAfterCleanup);
            Add("cleanupQuiltTextureReleased", quiltTextureReleased);
            Add("cleanupReferenceTargetReleased", referenceTargetReleased);
            Add("cleanupActiveVisualEffectCount", allVfxInactiveAfterCleanup);
            Add("nunitResultMeaning", "bounded-observation-harness-only");
            File.WriteAllText(path, evidence.ToString(), new UTF8Encoding(false));
        }

        void Add(string key, object value) => evidence.Append(key).Append('=').Append(Ascii(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "")).AppendLine();

        static string CaptureMainPackageRegistrations()
        {
            UnityEditor.PackageManager.PackageInfo[] packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            string[] names =
            {
                "com.zabaglione.lookingglass.viewweave.core",
                "com.zabaglione.lookingglass.viewweave.urp",
                "com.zabaglione.lookingglass.viewweave.bridge",
                "com.unity.visualeffectgraph"
            };
            var rows = new List<string>(names.Length);
            foreach (string name in names)
            {
                UnityEditor.PackageManager.PackageInfo[] matches = packages.Where(value => value != null && value.name == name).ToArray();
                Assert.AreEqual(1, matches.Length, "Expected exactly one registered package: " + name);
                UnityEditor.PackageManager.PackageInfo package = matches[0];
                if (name.StartsWith("com.zabaglione.", StringComparison.Ordinal))
                {
                    string expected = Path.GetFullPath(Path.Combine("REPO_ROOT/Packages", name));
                    Assert.IsFalse(string.IsNullOrEmpty(package.resolvedPath), "Resolved path is missing for " + name);
                    string actual = Path.GetFullPath(package.resolvedPath ?? "");
                    Assert.IsTrue(string.Equals(expected.TrimEnd(Path.DirectorySeparatorChar), actual.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                        "Expected the main SDK package source path for " + name + "; actual=" + actual);
                }
                if (name == "com.unity.visualeffectgraph")
                    Assert.AreEqual("17.6.0", package.version, "Expected the resolved VFX Graph version.");
                rows.Add(name + "@" + package.version + ";source=" + package.source + ";resolvedPath=" + (package.resolvedPath ?? "unavailable"));
            }
            rows.Sort(StringComparer.Ordinal);
            return string.Join("|", rows);
        }

        static void ReadPixels(RenderTexture source, Texture2D target)
        {
            Assert.IsNotNull(source);
            Assert.IsNotNull(target);
            Assert.IsTrue(source.IsCreated());
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = source;
            target.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            RenderTexture.active = previous;
        }

        static int Luma(Color32 value) => (value.r * 299 + value.g * 587 + value.b * 114) / 1000;
        static string VectorText(Vector3 value) => value.x.ToString("R", CultureInfo.InvariantCulture) + "," + value.y.ToString("R", CultureInfo.InvariantCulture) + "," + value.z.ToString("R", CultureInfo.InvariantCulture);
        static string QuaternionText(Quaternion value) => value.x.ToString("R", CultureInfo.InvariantCulture) + "," + value.y.ToString("R", CultureInfo.InvariantCulture) + "," + value.z.ToString("R", CultureInfo.InvariantCulture) + "," + value.w.ToString("R", CultureInfo.InvariantCulture);
        static bool IsGreen(Color32 value) => value.g >= 45 && value.g > value.r + 24 && value.g > value.b + 24;
        static bool IsGold(Color32 value) => value.r >= 145 && value.g >= 75 && value.r > value.g && value.b < value.g;

        static bool MatrixNear(Matrix4x4 left, Matrix4x4 right, float tolerance)
        {
            for (int row = 0; row < 4; ++row)
                for (int column = 0; column < 4; ++column)
                    if (Mathf.Abs(left[row, column] - right[row, column]) > tolerance) return false;
            return true;
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

        static bool RegexGuidMatches(string path, string guid) => string.Equals(AssetDatabase.AssetPathToGUID(path), guid, StringComparison.OrdinalIgnoreCase);
        static string AbsoluteAssetPath(string path) => Path.GetFullPath(Path.Combine(Application.dataPath, "..", path));

        static string Sha256(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var chars = value.Select(character => character >= 32 && character <= 126 ? character : '?').ToArray();
            return new string(chars);
        }
    }
}
#endif
