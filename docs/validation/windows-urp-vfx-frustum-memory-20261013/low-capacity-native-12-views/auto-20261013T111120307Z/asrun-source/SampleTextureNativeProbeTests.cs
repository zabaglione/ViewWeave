#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
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
    public sealed class SampleTextureNativeProbeTests
    {
        const string ScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string SceneGuid = "ba07c3d71e7f23846a0a2a39ae625622";
        const string SelectedPrefabGuid = "3a08a8ebfe6ab4642b6f075038bdc1c3";
        const string SourceGraphPath = "Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleTexture2D.vfx";
        const string SourceGraphGuid = "e166f36ceb1831f46bea1e17fbc67c5a";
        const string ResultEnvironment = "VW_SAMPLE_TEXTURE_NATIVE_RESULT";
        const string FixtureEnvironment = "VW_SAMPLE_TEXTURE_NATIVE_FIXTURE";
        const string ModeEnvironment = "VW_SAMPLE_TEXTURE_NATIVE_MODE";
        const string RestartCyclesEnvironment = "VW_SAMPLE_TEXTURE_NATIVE_RESTART_CYCLES";
        const string RenderedViewsEnvironment = "VW_SAMPLE_TEXTURE_NATIVE_RENDERED_VIEWS";
        const int TileWidth = 128;
        const int TileHeight = 96;
        const int WarmupRevisionCount = 8;
        const int MeasurementRevisionCount = 32;
        const int MaximumRunFrames = 300;
        const float MaximumRunSeconds = 30f;
        const int MaximumRestartCycles = 2;
        const int RestartWaitFrames = 8;
        const long GrowthStopBytes = 512L * 1024L * 1024L;

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
                catch { return false; }
            }
        }

        [Serializable]
        sealed class FixtureRecord
        {
            public string schema, status, unityVersion, graphicsDevice, sourceAssetPath, sourceAssetGuid;
            public string sourceAssetSha256Before, sourceAssetSha256After;
            public string controlAssetPath, controlAssetGuid, controlAssetSha256;
            public string autoAssetPath, autoAssetGuid, autoAssetSha256;
            public string customAssetPath, customAssetGuid, customAssetSha256;
            public string controlComparableStructureSha256, autoComparableStructureSha256, customComparableStructureSha256;
            public string gridParameterExposedName, gridParameterValueType;
            public long controlGridParameterFileId, controlGridOutputSlotFileId;
            public long autoGridParameterFileId, autoGridOutputSlotFileId;
            public long customGridParameterFileId, customGridOutputSlotFileId;
            public string[] normalizedStructureFields;
            public string customCompilerDisabledReasonName, nunitOutcome;
            public int fixtureParticleCapacity, sourceParticleCapacity;
            public int controlGridResolutionX, controlGridResolutionY;
            public int autoGridResolutionX, autoGridResolutionY;
            public int customGridResolutionX, customGridResolutionY;
            public int controlInstancingModeValue, controlInstancingCapacity;
            public int autoInstancingModeValue, autoInstancingCapacity;
            public int customInstancingModeValue, customInstancingCapacity;
            public bool gridParameterExposed, sourceAssetUnchanged, openSceneFilesUnchanged;
        }

        [Serializable]
        sealed class Checkpoint
        {
            public string phase;
            public int frame;
            public long quiltRevision;
            public int activationRevisionIndex;
            public float elapsedSeconds;
            public int historyCameraCount;
            public int aliveParticles;
            public long graphicsBytes;
            public long privateBytes;
            public long workingSetBytes;
            public long graphicsDeltaBytes;
            public long privateDeltaBytes;
            public int changedPixelCount;
            public int maximumPixelDelta;
            public string batchInfoType;
            public string totalGpuBytes;
            public string totalCpuBytes;
            public string activeBatchCount;
            public string inactiveBatchCount;
            public string activeInstanceCount;
            public string unbatchedInstanceCount;
            public string totalInstanceCapacity;
            public string maxInstancePerBatchCapacity;
            public string batchInfoFailure;
            public string quiltTextureDescriptor;
            public string viewTextureDescriptor;
            public string resolveTextureDescriptor;
        }

        [Serializable]
        sealed class HistoryCycle
        {
            public int cycleIndex;
            public string referenceCameraEntityId;
            public string oldHistoryCameraEntityId;
            public string newHistoryCameraEntityId;
            public int historyCameraCountBeforeRelease;
            public int historyCameraCountAfterRelease;
            public int historyCameraCountBeforeRestart;
            public int historyCameraCountAfterRestart;
            public int framesWaited;
            public int revisionsBeforeRelease;
            public int revisionsAfterRelease;
            public int revisionsAfterRestart;
            public int aliveParticlesAtRevision32;
            public int changedPixelCountAtRevision32;
            public int maximumPixelDeltaAtRevision32;
            public float elapsedSeconds;
            public bool oldCameraDestroyed;
            public bool currentCameraMatchesPriorCreated;
            public bool historyReleasedBeforeRestart;
            public bool newCameraCreated;
            public bool newCameraDiffersFromOld;
            public bool referenceCameraStayedSame;
            public bool activeDrawGate;
            public bool checkpoints8Reached;
            public bool checkpoints16Reached;
            public bool checkpoints32Reached;
            public bool growthStopNotReached;
            public bool descriptorGate;
            public bool passed;
        }

        [Serializable]
        sealed class Result
        {
            public string schema = "viewweave-sample-texture-native-probe-v4-grid40-view-count";
            public string status = "started";
            public string mode, testPhase, failureType, failureMessage;
            public string unityVersion, graphicsApi, graphicsDeviceName, vfxPackageVersion;
            public string fixtureResultSha256, fixtureSourceAssetSha256, fixtureSelectedAssetSha256;
            public string gridParameterExposedName, gridParameterValueType;
            public long controlGridParameterFileId, controlGridOutputSlotFileId;
            public long autoGridParameterFileId, autoGridOutputSlotFileId;
            public long customGridParameterFileId, customGridOutputSlotFileId;
            public long selectedGridParameterFileId, selectedGridOutputSlotFileId;
            public string[] normalizedStructureFields;
            public string selectedAssetPath, selectedAssetGuid, selectedAssetSha256;
            public string sourceGraphGuid, sourceGraphSha256Before, sourceGraphSha256After;
            public string selectedAssetSha256After;
            public string scenePath, sceneGuid, sceneSha256Before, sceneSha256After;
            public string selectedPrefabGuid;
            public string historyCameraEntityIdAfterOff, historyCameraEntityIdAfterActive;
            public string selectedGameObjectPath, selectedEffectBounds;
            public string quiltOutputDescriptor, quiltViewRenderMode;
            public string quiltTextureDescriptorAfterOff, quiltTextureDescriptorAfterActive;
            public float measurementElapsedSeconds;
            public int sourceParticleCapacity, selectedParticleCapacity;
            public int sourceGridResolutionX, sourceGridResolutionY, selectedGridResolutionX, selectedGridResolutionY;
            public int sourceCameraEnabledDuringRun, referenceCameraEnabledDuringRun;
            public int historyCameraCountAfterOff, historyCameraCountAfterActive;
            public int historyCameraCountAfterCleanup;
            public int renderedViewCountAfterOff, renderedViewCountAfterActive;
            public long offRevision, activeRevision;
            public int offAliveParticles, activeAliveParticles;
            public int changedPixelCount, maximumPixelDelta;
            public int[] changedPixelCountPerOutputTile;
            public bool graphicsApiGate, pipelineGate, twoCameraGate, isolatedHistoryGate;
            public bool gridResolutionGate, outputLayoutGate;
            public bool pixelDifferenceEachTileGate;
            public bool offscreenRevisionGate, activeRevisionGate, activeParticlesGate, pixelDifferenceGate;
            public bool measurementComplete, checkpoint8Reached, checkpoint16Reached, checkpoint32Reached;
            public int restartCyclesRequested, maximumTotalMeasurementFrames, totalMeasurementFrames;
            public float maximumTotalMeasurementSeconds, totalMeasurementElapsedSeconds;
            public int renderedViewsRequested, outputColumns, outputRows, outputQuiltWidth, outputQuiltHeight;
            public int effectiveAANoneCountAfterOff, effectiveAANoneCountAfterActive;
            public bool effectiveAANoneAfterOffGate, effectiveAANoneAfterActiveGate;
            public string effectiveAAModesAfterOff, effectiveAAModesAfterActive;
            public bool historyCycleGate;
            public bool rendererReadyBeforeActivation, sourceCullingMaskIncludesEffect, effectMaterialsSupported;
            public bool componentAllowInstancing, componentReleaseInstanceOnDisable, componentPause, componentResetSeedOnPlay;
            public bool componentInstancingGate, componentActivationSettingsGate, componentSettingsUnchanged;
            public string componentInitialEventName;
            public uint componentStartSeed;
            public float componentPlayRate;
            public bool growthStopNotReached, descriptorGate, sourceGraphUnchanged, selectedAssetUnchanged, sceneDiskUnchanged, sceneSavedDuringRun;
            public bool autoRenderErrorFree, noUnityErrorsDuringRun, cleanupPassed;
            public bool rigAutoRenderRestored, profileRestored, cameraStateRestored, sceneEffectsInactiveAfterCleanup;
            public bool selectedRendererStateRestored;
            public bool historyReleased, viewTextureReleased, resolveTextureReleased, quiltTextureReleased;
            public int activeSceneEffectCountAtStart, activeSceneEffectCountAtCleanup;
            public long graphicsBaselineBytes, privateBaselineBytes, workingSetBaselineBytes;
            public long graphicsPeakGrowthBytes, privatePeakGrowthBytes;
            public string lastAutoRenderError;
            public int unityErrorCount;
            public string firstUnityError;
            public Checkpoint[] checkpoints;
            public HistoryCycle[] historyCycles;
        }

        sealed class CameraState
        {
            internal Camera camera;
            internal bool enabled, allowHdr, allowMsaa, allowDynamicResolution;
            internal RenderTexture targetTexture;
            internal float aspect, fieldOfView;
            internal Vector3 position;
            internal Quaternion rotation;
            internal UniversalAdditionalCameraData additionalData;
            internal AntialiasingMode antialiasing;
            internal AntialiasingQuality antialiasingQuality;
            internal bool postProcessing;
        }

        Result result;
        Exception failure;
        string evidencePath;
        string fixturePath;
        string sceneAbsolutePath;
        string sourceGraphAbsolutePath;
        string selectedAssetAbsolutePath;
        string phase = "start";
        string resultMode;
        FixtureRecord fixture;
        Scene loadedScene;
        ViewWeaveRig rig;
        QuiltRenderer quilt;
        UrpQuiltCamera guard;
        QuiltOutputTarget outputTarget;
        MonoBehaviour oldOutputBehaviour;
        MonoBehaviour oldGuardBehaviour;
        Camera referenceCamera;
        Camera sourceCamera;
        VisualEffect selectedEffect;
        VFXRenderer selectedRenderer;
        VisualEffectAsset originalEffectAsset;
        bool originalRendererEnabled;
        bool originalRendererForceRenderingOff;
        bool originalComponentAllowInstancing, originalComponentReleaseOnDisable, originalComponentResetSeedOnPlay;
        bool originalComponentPause;
        uint originalComponentStartSeed;
        float originalComponentPlayRate;
        string originalComponentInitialEventName;
        VisualEffect[] sceneEffects = Array.Empty<VisualEffect>();
        Camera[] sceneCameras = Array.Empty<Camera>();
        bool[] cameraEnabledBefore = Array.Empty<bool>();
        CameraState referenceCameraBefore;
        CameraState sourceCameraBefore;
        QuiltSettings originalSettings;
        QuiltQualityProfile originalProfile;
        QuiltViewRenderMode originalViewMode;
        bool originalQuiltAutoRender;
        bool originalRigAutoRender;
        bool originalUseRenderRequests;
        bool rigConfigured;
        bool sceneSavedDuringRun;
        bool subscribedSceneSave;
        bool subscribedLogMessages;
        int loggedErrorCount;
        string firstLoggedError = "";
        Texture2D readback;
        Color32[] offPixels;
        readonly List<Checkpoint> checkpoints = new List<Checkpoint>();
        readonly List<HistoryCycle> historyCycles = new List<HistoryCycle>();
        int requestedRestartCycles;
        int requestedRenderedViews = 1;
        int outputColumns = 1, outputRows = 1, outputQuiltWidth = TileWidth, outputQuiltHeight = TileHeight;
        string referenceCameraEntityId;
        float totalMeasurementStart;
        int totalMeasurementStartFrame;

        [UnityTest]
        public IEnumerator ComparesLowCapacitySampleTextureOnNaturalAutoRender()
        {
            result = new Result();
            failure = null;
            phase = "initialize";
            result.testPhase = phase;
            IEnumerator core = RunCore();
            var stack = new Stack<IEnumerator>();
            stack.Push(core);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool moved;
                object current = null;
                try { moved = top.MoveNext(); if (moved) current = top.Current; }
                catch (Exception exception) { failure = exception; break; }
                if (!moved) { stack.Pop(); continue; }
                if (current is IEnumerator nested) { stack.Push(nested); continue; }
                yield return current;
            }

            phase = "cleanup";
            CleanupRuntimeState();
            for (int frame = 0; frame < 4; ++frame) yield return null;
            CaptureCleanupState();
            result.componentSettingsUnchanged = ComponentSettingsUnchanged();
            RestoreCameraStates();
            RestoreEffectAssets();
            if (subscribedSceneSave) { EditorSceneManager.sceneSaved -= OnSceneSaved; subscribedSceneSave = false; }
            if (subscribedLogMessages) { Application.logMessageReceived -= OnLogMessage; subscribedLogMessages = false; }
            result.sceneSavedDuringRun = sceneSavedDuringRun;
            result.sceneSha256After = SafeSha(sceneAbsolutePath);
            result.sourceGraphSha256After = SafeSha(sourceGraphAbsolutePath);
            result.selectedAssetSha256After = SafeSha(selectedAssetAbsolutePath);
            result.sceneDiskUnchanged = !string.IsNullOrEmpty(result.sceneSha256Before) && result.sceneSha256Before == result.sceneSha256After;
            result.sourceGraphUnchanged = !string.IsNullOrEmpty(result.sourceGraphSha256Before) && result.sourceGraphSha256Before == result.sourceGraphSha256After;
            result.selectedAssetUnchanged = !string.IsNullOrEmpty(result.fixtureSelectedAssetSha256) && result.fixtureSelectedAssetSha256 == result.selectedAssetSha256After;
            result.noUnityErrorsDuringRun = loggedErrorCount == 0;
            result.unityErrorCount = loggedErrorCount;
            result.firstUnityError = Ascii(firstLoggedError);
            result.lastAutoRenderError = Ascii(quilt == null ? "" : quilt.LastAutoRenderError);
            result.checkpoints = checkpoints.ToArray();
            result.historyCycles = historyCycles.ToArray();
            result.status = failure == null && ComputePassGate() ? "passed" : "not-passed";
            if (failure == null && result.status != "passed") failure = new AssertionException("Native sample texture probe did not satisfy every rendering, memory, and cleanup gate.");
            if (failure != null)
            {
                result.failureType = failure.GetType().Name;
                result.failureMessage = Ascii(failure.Message);
            }
            WriteResult();
            if (failure != null) throw failure;
            Assert.AreEqual("passed", result.status);
        }

        IEnumerator RunCore()
        {
            phase = "validate-environment";
            result.testPhase = phase;
            resultMode = Environment.GetEnvironmentVariable(ModeEnvironment);
            Assert.IsTrue(resultMode == "Auto" || resultMode == "Custom", ModeEnvironment + " must be Auto or Custom.");
            result.mode = resultMode;
            string restartValue = Environment.GetEnvironmentVariable(RestartCyclesEnvironment);
            if (string.IsNullOrWhiteSpace(restartValue)) restartValue = "0";
            Assert.IsTrue(int.TryParse(restartValue, NumberStyles.None, CultureInfo.InvariantCulture, out requestedRestartCycles) &&
                requestedRestartCycles >= 0 && requestedRestartCycles <= MaximumRestartCycles,
                RestartCyclesEnvironment + " must be an integer from 0 through 2.");
            result.restartCyclesRequested = requestedRestartCycles;
            result.historyCycleGate = requestedRestartCycles == 0;
            string renderedViewsValue = Environment.GetEnvironmentVariable(RenderedViewsEnvironment);
            if (string.IsNullOrWhiteSpace(renderedViewsValue)) renderedViewsValue = "1";
            Assert.IsTrue(int.TryParse(renderedViewsValue, NumberStyles.None, CultureInfo.InvariantCulture, out requestedRenderedViews) &&
                (requestedRenderedViews == 1 || requestedRenderedViews == 12),
                RenderedViewsEnvironment + " must be 1 or 12.");
            Assert.IsTrue(requestedRestartCycles == 0 || requestedRenderedViews == 1,
                "History camera restart cycles are only approved for one rendered view.");
            outputColumns = requestedRenderedViews == 12 ? 4 : 1;
            outputRows = requestedRenderedViews == 12 ? 3 : 1;
            outputQuiltWidth = TileWidth * outputColumns;
            outputQuiltHeight = TileHeight * outputRows;
            result.renderedViewsRequested = requestedRenderedViews;
            result.outputColumns = outputColumns;
            result.outputRows = outputRows;
            result.outputQuiltWidth = outputQuiltWidth;
            result.outputQuiltHeight = outputQuiltHeight;
            evidencePath = Environment.GetEnvironmentVariable(ResultEnvironment);
            fixturePath = Environment.GetEnvironmentVariable(FixtureEnvironment);
            Assert.IsFalse(string.IsNullOrWhiteSpace(evidencePath), ResultEnvironment + " is required.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(fixturePath), FixtureEnvironment + " is required.");
            evidencePath = Path.GetFullPath(evidencePath);
            fixturePath = Path.GetFullPath(fixturePath);
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string allowedRoot = Path.GetFullPath(Path.Combine(projectRoot, "TestResults")) + Path.DirectorySeparatorChar;
            Assert.IsTrue(evidencePath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase), "Native evidence must remain under the isolated clone TestResults folder.");
            Assert.IsFalse(File.Exists(Path.Combine(evidencePath, "native-result.json")), "Refusing to overwrite native evidence.");
            Directory.CreateDirectory(evidencePath);
            Assert.IsTrue(File.Exists(fixturePath), "The validated Control Null fixture result is missing.");
            fixture = JsonUtility.FromJson<FixtureRecord>(File.ReadAllText(fixturePath, Encoding.UTF8));
            Assert.IsNotNull(fixture);
            Assert.AreEqual("viewweave-sample-texture-capacity-fixture-v2-grid40", fixture.schema);
            Assert.AreEqual("success", fixture.status);
            Assert.AreEqual("Passed", fixture.nunitOutcome);
            Assert.AreEqual("Null", fixture.graphicsDevice);
            Assert.IsTrue(fixture.sourceAssetUnchanged && fixture.openSceneFilesUnchanged, "The CPU fixture did not preserve its source and scene.");
            Assert.AreEqual(160000, fixture.sourceParticleCapacity);
            Assert.AreEqual(1600, fixture.fixtureParticleCapacity);
            Assert.AreEqual("Grid Resolution", fixture.gridParameterExposedName);
            Assert.IsFalse(fixture.gridParameterExposed, "The Grid Resolution setting is expected to remain a non-exposed graph property.");
            Assert.IsTrue(!string.IsNullOrEmpty(fixture.gridParameterValueType) && fixture.gridParameterValueType.StartsWith("UnityEngine.Vector2", StringComparison.Ordinal), "The Grid Resolution fixture setting must be a Vector2.");
            Assert.AreEqual(400, fixture.controlGridResolutionX);
            Assert.AreEqual(400, fixture.controlGridResolutionY);
            Assert.AreEqual(40, fixture.autoGridResolutionX);
            Assert.AreEqual(40, fixture.autoGridResolutionY);
            Assert.AreEqual(40, fixture.customGridResolutionX);
            Assert.AreEqual(40, fixture.customGridResolutionY);
            CollectionAssert.AreEquivalent(new[] { "capacity", "Grid Resolution Vector2 parameter value", "m_InstancingMode", "m_InstancingCapacity" }, fixture.normalizedStructureFields);
            Assert.AreEqual(fixture.controlGridParameterFileId, fixture.autoGridParameterFileId);
            Assert.AreEqual(fixture.controlGridParameterFileId, fixture.customGridParameterFileId);
            Assert.AreEqual(fixture.controlGridOutputSlotFileId, fixture.autoGridOutputSlotFileId);
            Assert.AreEqual(fixture.controlGridOutputSlotFileId, fixture.customGridOutputSlotFileId);
            Assert.AreEqual(fixture.controlComparableStructureSha256, fixture.autoComparableStructureSha256);
            Assert.AreEqual(fixture.controlComparableStructureSha256, fixture.customComparableStructureSha256);
            Assert.AreEqual(0, fixture.customCompilerDisabledReasonName == "None" ? 0 : 1, "Custom CPU compile was not eligible.");
            result.fixtureResultSha256 = Sha256(fixturePath);
            result.fixtureSourceAssetSha256 = fixture.sourceAssetSha256Before;
            result.sourceParticleCapacity = fixture.sourceParticleCapacity;
            result.selectedParticleCapacity = fixture.fixtureParticleCapacity;
            result.sourceGridResolutionX = fixture.controlGridResolutionX;
            result.sourceGridResolutionY = fixture.controlGridResolutionY;
            result.gridParameterExposedName = fixture.gridParameterExposedName;
            result.gridParameterValueType = fixture.gridParameterValueType;
            result.normalizedStructureFields = fixture.normalizedStructureFields;
            result.controlGridParameterFileId = fixture.controlGridParameterFileId;
            result.controlGridOutputSlotFileId = fixture.controlGridOutputSlotFileId;
            result.autoGridParameterFileId = fixture.autoGridParameterFileId;
            result.autoGridOutputSlotFileId = fixture.autoGridOutputSlotFileId;
            result.customGridParameterFileId = fixture.customGridParameterFileId;
            result.customGridOutputSlotFileId = fixture.customGridOutputSlotFileId;
            if (resultMode == "Auto")
            {
                result.selectedAssetPath = fixture.autoAssetPath;
                result.selectedAssetGuid = fixture.autoAssetGuid;
                result.fixtureSelectedAssetSha256 = fixture.autoAssetSha256;
                Assert.AreEqual(0, fixture.autoInstancingModeValue);
                Assert.AreEqual(64, fixture.autoInstancingCapacity);
                result.selectedGridResolutionX = fixture.autoGridResolutionX;
                result.selectedGridResolutionY = fixture.autoGridResolutionY;
                result.selectedGridParameterFileId = fixture.autoGridParameterFileId;
                result.selectedGridOutputSlotFileId = fixture.autoGridOutputSlotFileId;
            }
            else
            {
                result.selectedAssetPath = fixture.customAssetPath;
                result.selectedAssetGuid = fixture.customAssetGuid;
                result.fixtureSelectedAssetSha256 = fixture.customAssetSha256;
                Assert.AreEqual(1, fixture.customInstancingModeValue);
                Assert.AreEqual(1, fixture.customInstancingCapacity);
                result.selectedGridResolutionX = fixture.customGridResolutionX;
                result.selectedGridResolutionY = fixture.customGridResolutionY;
                result.selectedGridParameterFileId = fixture.customGridParameterFileId;
                result.selectedGridOutputSlotFileId = fixture.customGridOutputSlotFileId;
            }
            result.gridResolutionGate = result.selectedGridResolutionX == 40 && result.selectedGridResolutionY == 40 &&
                (resultMode == "Auto"
                    ? fixture.controlGridParameterFileId == fixture.autoGridParameterFileId && fixture.controlGridOutputSlotFileId == fixture.autoGridOutputSlotFileId
                    : fixture.controlGridParameterFileId == fixture.customGridParameterFileId && fixture.controlGridOutputSlotFileId == fixture.customGridOutputSlotFileId);
            Assert.IsTrue(result.gridResolutionGate, "The selected capacity fixture must use the reviewed 40x40 grid resolution and matching graph slot.");
            selectedAssetAbsolutePath = AbsoluteAssetPath(result.selectedAssetPath);
            result.unityVersion = Application.unityVersion;
            result.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            result.graphicsDeviceName = Ascii(SystemInfo.graphicsDeviceName);
            result.vfxPackageVersion = "17.6.0";
            Assert.AreEqual("6000.6.4f1", Application.unityVersion);
            Assert.AreEqual(RuntimePlatform.WindowsEditor, Application.platform);
            Assert.AreEqual(GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType);
            Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline);
            Assert.IsTrue(File.Exists(selectedAssetAbsolutePath), "The selected low-capacity graph clone is missing.");
            Assert.AreEqual(result.fixtureSelectedAssetSha256, Sha256(selectedAssetAbsolutePath), "Selected fixture asset hash differs from the passed Null gate.");
            Assert.IsFalse(fixture.controlAssetGuid == result.selectedAssetGuid, "The 160000-capacity Control graph must never be activated by this native probe.");
            result.sourceGraphGuid = SourceGraphGuid;
            sourceGraphAbsolutePath = AbsoluteAssetPath(SourceGraphPath);
            Assert.AreEqual(SourceGraphGuid, AssetDatabase.AssetPathToGUID(SourceGraphPath));
            result.sourceGraphSha256Before = Sha256(sourceGraphAbsolutePath);
            Assert.AreEqual(fixture.sourceAssetSha256Before, result.sourceGraphSha256Before, "The original source graph differs from the Control Null fixture's source.");

            phase = "load-template-scene";
            result.testPhase = phase;
            sceneAbsolutePath = AbsoluteAssetPath(ScenePath);
            result.scenePath = ScenePath;
            result.sceneGuid = AssetDatabase.AssetPathToGUID(ScenePath);
            Assert.AreEqual(SceneGuid, result.sceneGuid, "The prepared pilot scene identity differs from the reviewed fixture.");
            Assert.AreEqual(SelectedPrefabGuid, AssetDatabase.AssetPathToGUID("Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Sample Texture2D.prefab"), "The Sample Texture2D prefab identity differs from the reviewed fixture.");
            result.selectedPrefabGuid = SelectedPrefabGuid;
            result.sceneSha256Before = Sha256(sceneAbsolutePath);
            EditorSceneManager.sceneSaved += OnSceneSaved;
            subscribedSceneSave = true;
            Application.logMessageReceived += OnLogMessage;
            subscribedLogMessages = true;
            loadedScene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float sceneDeadline = Time.realtimeSinceStartup + 60f;
            while ((!loadedScene.IsValid() || !loadedScene.isLoaded) && Time.realtimeSinceStartup < sceneDeadline) yield return null;
            Assert.IsTrue(loadedScene.IsValid() && loadedScene.isLoaded, "The URP VFX learning scene did not load.");
            Assert.IsTrue(Application.isPlaying);
            Assert.AreEqual(RuntimePlatform.WindowsEditor, Application.platform);
            Assert.AreEqual(GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType);
            Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline);

            sceneEffects = UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include)
                .Where(value => value != null && value.gameObject.scene == loadedScene).ToArray();
            result.activeSceneEffectCountAtStart = sceneEffects.Count(value => value.gameObject.activeInHierarchy && value.enabled);
            Assert.Greater(sceneEffects.Length, 0, "The VFX learning scene has no VisualEffect components.");
            VisualEffect[] sourceMatches = sceneEffects.Where(value => value.visualEffectAsset != null &&
                string.Equals(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(value.visualEffectAsset)), SourceGraphGuid, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.AreEqual(1, sourceMatches.Length, "Expected exactly one scene SampleTexture2D component linked to the original graph.");
            selectedEffect = sourceMatches[0];
            result.selectedGameObjectPath = HierarchyPath(selectedEffect.transform);
            Assert.AreEqual(SourceGraphGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(selectedEffect.visualEffectAsset)), "The selected pilot prefab does not resolve to the expected SampleTexture2D graph.");
            selectedRenderer = selectedEffect.GetComponent<VFXRenderer>();
            Assert.IsNotNull(selectedRenderer, "The selected SampleTexture2D VFXRenderer is missing.");
            originalRendererEnabled = selectedRenderer.enabled;
            originalRendererForceRenderingOff = selectedRenderer.forceRenderingOff;
            originalEffectAsset = selectedEffect.visualEffectAsset;
            Assert.IsNotNull(originalEffectAsset);
            SerializedObject selectedEffectSerialized = new SerializedObject(selectedEffect);
            SerializedProperty allowInstancingProperty = selectedEffectSerialized.FindProperty("m_AllowInstancing");
            SerializedProperty releaseOnDisableProperty = selectedEffectSerialized.FindProperty("m_ReleaseInstanceOnDisable");
            Assert.IsNotNull(allowInstancingProperty, "The VFX component allow-instancing setting is unavailable.");
            Assert.IsNotNull(releaseOnDisableProperty, "The VFX component release-on-disable setting is unavailable.");
            originalComponentAllowInstancing = allowInstancingProperty.boolValue;
            originalComponentReleaseOnDisable = releaseOnDisableProperty.boolValue;
            originalComponentPause = selectedEffect.pause;
            originalComponentResetSeedOnPlay = selectedEffect.resetSeedOnPlay;
            originalComponentStartSeed = selectedEffect.startSeed;
            originalComponentPlayRate = selectedEffect.playRate;
            originalComponentInitialEventName = selectedEffect.initialEventName;
            result.componentAllowInstancing = originalComponentAllowInstancing;
            result.componentReleaseInstanceOnDisable = originalComponentReleaseOnDisable;
            result.componentPause = originalComponentPause;
            result.componentResetSeedOnPlay = originalComponentResetSeedOnPlay;
            result.componentStartSeed = originalComponentStartSeed;
            result.componentPlayRate = originalComponentPlayRate;
            result.componentInitialEventName = Ascii(originalComponentInitialEventName);
            result.componentInstancingGate = originalComponentAllowInstancing;
            result.componentActivationSettingsGate = originalComponentInitialEventName == "OnPlay" && !originalComponentPause && originalComponentPlayRate > 0f;
            Assert.IsTrue(result.componentInstancingGate, "The selected VFX component must allow instancing for the Auto/Custom comparison.");
            Assert.IsTrue(result.componentActivationSettingsGate, "The selected VFX component activation settings do not match the intended OnPlay case.");
            foreach (VisualEffect effect in sceneEffects)
                if (effect.gameObject.activeSelf) effect.gameObject.SetActive(false);
            Assert.AreEqual(0, CountActiveEffects(), "All scene effects must be inactive before the native comparison.");
            selectedRenderer.enabled = true;
            selectedRenderer.forceRenderingOff = false;
            result.rendererReadyBeforeActivation = selectedRenderer.enabled && !selectedRenderer.forceRenderingOff;
            Assert.IsTrue(result.rendererReadyBeforeActivation, "The selected renderer was not renderable while its GameObject was inactive.");
            selectedEffect.visualEffectAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(result.selectedAssetPath);
            Assert.IsNotNull(selectedEffect.visualEffectAsset);
            Assert.AreEqual(result.selectedAssetGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(selectedEffect.visualEffectAsset)));
            Assert.AreEqual(0, CountActiveEffects(), "Assigning a low-capacity asset activated a scene VFX object.");

            phase = "configure-single-view-renderer";
            result.testPhase = phase;
            rig = Resources.FindObjectsOfTypeAll<ViewWeaveRig>().Single(value => value != null && value.gameObject.scene == loadedScene);
            quilt = rig.GetComponent<QuiltRenderer>();
            Assert.IsNotNull(quilt);
            Assert.IsFalse(rig.AutoRender || quilt.autoRender, "The scene rig must be idle before this isolated comparison.");
            Assert.IsFalse(quilt.IsRendering, "The scene rig is already rendering.");
            originalSettings = quilt.settings;
            originalProfile = rig.Profile;
            originalViewMode = quilt.viewRenderMode;
            originalQuiltAutoRender = quilt.autoRender;
            originalRigAutoRender = rig.AutoRender;
            originalUseRenderRequests = quilt.useRenderRequests;
            oldOutputBehaviour = quilt.OutputBehaviour;
            oldGuardBehaviour = quilt.GuardBehaviour;
            sourceCamera = quilt.sourceCamera;
            referenceCamera = quilt.referenceCamera;
            Assert.IsNotNull(sourceCamera);
            Assert.IsNotNull(referenceCamera);
            Assert.AreSame(rig.GetComponent<Camera>(), sourceCamera, "Quilt source camera must remain the rig camera.");
            guard = sourceCamera.GetComponent<UrpQuiltCamera>();
            Assert.IsNotNull(guard);
            Assert.AreSame(sourceCamera, guard.GetComponent<Camera>());
            Assert.AreEqual(UrpQuiltRenderingMode.IsolatedViewHistories, guard.renderingMode, "This comparison requires isolated per-view histories.");
            Assert.AreEqual(4096, guard.historyMemoryBudgetMB, "The SDK history budget must remain 4096 MiB.");
            Assert.AreEqual(0, guard.HistoryCameraCount, "URP history resources existed before the probe.");
            sceneCameras = Resources.FindObjectsOfTypeAll<Camera>()
                .Where(value => value != null && value.gameObject.scene == loadedScene).Distinct().ToArray();
            cameraEnabledBefore = sceneCameras.Select(value => value.enabled).ToArray();
            referenceCameraBefore = CaptureCamera(referenceCamera);
            sourceCameraBefore = CaptureCamera(sourceCamera);
            foreach (Camera camera in sceneCameras) camera.enabled = camera == referenceCamera;
            Assert.IsTrue(referenceCamera.enabled, "Exactly one normal reference camera must remain enabled.");
            Assert.IsFalse(sourceCamera.enabled, "The dedicated quilt source camera must stay disabled.");
            result.sourceCullingMaskIncludesEffect = (sourceCamera.cullingMask & (1 << selectedEffect.gameObject.layer)) != 0;
            Assert.IsTrue(result.sourceCullingMaskIncludesEffect, "The quilt source camera culling mask excludes the selected VFX layer.");
            var selectedMaterials = new List<Material>();
            selectedRenderer.GetSharedMaterials(selectedMaterials);
            result.effectMaterialsSupported = selectedMaterials.Count > 0 && selectedMaterials.All(material =>
                material != null && material.shader != null && material.shader.isSupported);
            Assert.IsTrue(result.effectMaterialsSupported, "The selected VFX renderer has no supported material shader.");
            referenceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing = AntialiasingMode.None;
            sourceCamera.GetComponent<UniversalAdditionalCameraData>().antialiasing = AntialiasingMode.None;
            referenceCamera.allowMSAA = false;
            sourceCamera.allowMSAA = false;
            referenceCamera.allowDynamicResolution = false;
            sourceCamera.allowDynamicResolution = false;

            Bounds bounds = selectedRenderer.bounds;
            if (!IsFinite(bounds.center) || !IsFinite(bounds.extents) || bounds.extents.sqrMagnitude < 0.0001f)
                bounds = new Bounds(selectedEffect.transform.position + Vector3.up * 0.5f, Vector3.one);
            result.selectedEffectBounds = bounds.center.ToString("F4", CultureInfo.InvariantCulture) + ";size=" + bounds.size.ToString("F4", CultureInfo.InvariantCulture);
            Vector3 target = bounds.center;
            Vector3 forward = referenceCamera.transform.forward.normalized;
            if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
            float distance = Mathf.Max(2f, bounds.extents.magnitude * 3f);
            referenceCamera.transform.position = target - forward * distance;
            referenceCamera.transform.LookAt(target);
            referenceCamera.aspect = TileWidth / (float)TileHeight;
            quilt.settings = new QuiltSettings
            {
                quiltWidth = outputQuiltWidth,
                quiltHeight = outputQuiltHeight,
                columns = outputColumns,
                rows = outputRows,
                views = requestedRenderedViews,
                viewAspect = TileWidth / (float)TileHeight,
                fieldOfView = referenceCamera.fieldOfView,
                cameraDistance = distance,
                viewCone = 0f
            };
            rig.ApplyProfile(new QuiltQualityProfile(outputQuiltWidth, outputQuiltHeight, outputColumns, outputRows,
                requestedRenderedViews, TileWidth / (float)TileHeight, 0f));
            quilt.settings.cameraDistance = distance;
            Assert.IsTrue(quilt.TrySetViewRenderMode(QuiltViewRenderMode.Full, out string modeError), "Could not select Full sampling: " + Ascii(modeError));
            quilt.useRenderRequests = true;
            outputTarget = rig.gameObject.AddComponent<QuiltOutputTarget>();
            rig.SetOutput(outputTarget);
            rig.SetGuard(guard);
            Assert.IsTrue(rig.ConfigureComponents(), "Rig components did not accept the explicit render output and URP guard.");
            Assert.IsTrue(rig.ValidateRig(out string rigError), "Rig validation failed: " + Ascii(rigError));
            rig.AutoRender = true;
            rigConfigured = true;
            result.sourceCameraEnabledDuringRun = sourceCamera.enabled ? 1 : 0;
            result.referenceCameraEnabledDuringRun = referenceCamera.enabled ? 1 : 0;
            referenceCameraEntityId = referenceCamera.GetEntityId().ToString();
            result.graphicsApiGate = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            result.pipelineGate = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            result.twoCameraGate = referenceCamera.enabled && !sourceCamera.enabled;
            result.quiltViewRenderMode = quilt.viewRenderMode.ToString();
            result.quiltOutputDescriptor = outputQuiltWidth.ToString(CultureInfo.InvariantCulture) + "x" + outputQuiltHeight.ToString(CultureInfo.InvariantCulture) +
                ";tile=" + TileWidth.ToString(CultureInfo.InvariantCulture) + "x" + TileHeight.ToString(CultureInfo.InvariantCulture) +
                ";columns=" + outputColumns.ToString(CultureInfo.InvariantCulture) + ";rows=" + outputRows.ToString(CultureInfo.InvariantCulture) +
                ";views=" + requestedRenderedViews.ToString(CultureInfo.InvariantCulture) + ";viewCone=0;mode=" + quilt.viewRenderMode.ToString();
            result.outputLayoutGate = quilt.settings.views == requestedRenderedViews && quilt.settings.columns == outputColumns &&
                quilt.settings.rows == outputRows && quilt.settings.quiltWidth == outputQuiltWidth && quilt.settings.quiltHeight == outputQuiltHeight &&
                Mathf.Approximately(quilt.settings.viewCone, 0f) && quilt.viewRenderMode == QuiltViewRenderMode.Full &&
                rig.Profile.QuiltWidth == outputQuiltWidth && rig.Profile.QuiltHeight == outputQuiltHeight && rig.Profile.Columns == outputColumns &&
                rig.Profile.Rows == outputRows && rig.Profile.Views == requestedRenderedViews && Mathf.Approximately(rig.Profile.ViewCone, 0f);
            Assert.IsTrue(result.outputLayoutGate, "The requested quilt layout or Full zero-view-cone sampling was not applied.");
            Assert.AreEqual(requestedRenderedViews, quilt.settings.views);
            Assert.AreEqual(0, CountActiveEffects());
            Assert.AreEqual(1, CountEnabledCameras());
            readback = new Texture2D(outputQuiltWidth, outputQuiltHeight, TextureFormat.RGBA32, false, true)
            {
                name = "SampleTextureNative.Readback",
                hideFlags = HideFlags.HideAndDontSave
            };

            phase = "auto-render-off-warmup";
            result.testPhase = phase;
            long warmRevision = quilt.QuiltRevision;
            yield return WaitForRevisionCount(warmRevision, WarmupRevisionCount, 180, 20f);
            Assert.AreEqual(requestedRenderedViews, quilt.LastRenderedViewCount, "The VFX-off warmup did not naturally render every requested view.");
            Assert.AreEqual(requestedRenderedViews, guard.HistoryCameraCount, "The isolated URP history camera count does not match the requested view count.");
            result.historyCameraCountAfterOff = guard.HistoryCameraCount;
            Camera warmHistoryCamera = guard.GetHistoryCamera(0);
            result.historyCameraEntityIdAfterOff = warmHistoryCamera == null ? "null" : warmHistoryCamera.GetEntityId().ToString();
            result.renderedViewCountAfterOff = quilt.LastRenderedViewCount;
            result.quiltTextureDescriptorAfterOff = DescribeTexture(quilt.QuiltTexture);
            result.quiltTextureDescriptorAfterOff = DescribeTexture(quilt.QuiltTexture);
            result.isolatedHistoryGate = guard.renderingMode == UrpQuiltRenderingMode.IsolatedViewHistories && guard.HistoryCameraCount == requestedRenderedViews;
            result.effectiveAAModesAfterOff = CaptureHistoryAAModes(out result.effectiveAANoneCountAfterOff);
            result.effectiveAANoneAfterOffGate = result.effectiveAANoneCountAfterOff == requestedRenderedViews;
            Assert.IsTrue(result.effectiveAANoneAfterOffGate, "Every warmup history camera must use effective Antialiasing=None.");
            result.offRevision = quilt.QuiltRevision;
            result.offAliveParticles = 0;
            offPixels = ReadQuiltPixels();
            Assert.IsTrue(TryReadMemory(out result.graphicsBaselineBytes, out result.privateBaselineBytes, out result.workingSetBaselineBytes), "Post-warmup process memory baseline is unavailable.");
            Assert.Greater(result.graphicsBaselineBytes, 0);
            Assert.Greater(result.privateBaselineBytes, 0);
            result.offscreenRevisionGate = quilt.QuiltRevision >= warmRevision + WarmupRevisionCount;
            AddCheckpoint("vfx-off-warmed", 0, 0, 0);

            phase = "activate-selected-low-capacity-asset";
            result.testPhase = phase;
            Assert.AreEqual(0, CountActiveEffects(), "VFX-off baseline must contain no active scene effects.");
            long activationRevision = quilt.QuiltRevision;
            totalMeasurementStart = Time.realtimeSinceStartup;
            totalMeasurementStartFrame = Time.frameCount;
            result.maximumTotalMeasurementFrames = MaximumRunFrames * (1 + requestedRestartCycles) + RestartWaitFrames * requestedRestartCycles;
            result.maximumTotalMeasurementSeconds = MaximumRunSeconds * (1 + requestedRestartCycles) + RestartWaitFrames * requestedRestartCycles;
            float activationStart = Time.realtimeSinceStartup;
            int activationFrame = Time.frameCount;
            selectedEffect.gameObject.SetActive(true);
            selectedEffect.enabled = true;
            selectedEffect.pause = false;
            selectedRenderer.enabled = true;
            selectedRenderer.forceRenderingOff = false;
            Assert.AreEqual(1, CountActiveEffects(), "Exactly one scene VFX component must be active after selected activation.");
            Assert.IsTrue(selectedEffect.enabled);
            Assert.IsFalse(selectedEffect.pause);
            bool renderGateReached = false;
            for (int frame = 0; frame < MaximumRunFrames && Time.realtimeSinceStartup - activationStart < MaximumRunSeconds; ++frame)
            {
                yield return null;
                Assert.IsTrue(quilt.autoRender && rig.AutoRender, "Natural auto-render stopped during the comparison.");
                Assert.IsTrue(string.IsNullOrEmpty(quilt.LastAutoRenderError), "Quilt auto-render failed: " + Ascii(quilt.LastAutoRenderError));
                Assert.AreEqual(requestedRenderedViews, quilt.settings.views, "The bounded output view count changed unexpectedly.");
                if (!TryReadMemory(out long graphics, out long privateBytes, out long workingSet))
                    Assert.Fail("A process-global graphics or process-memory counter became unavailable.");
                result.graphicsPeakGrowthBytes = Math.Max(result.graphicsPeakGrowthBytes, Math.Max(0, graphics - result.graphicsBaselineBytes));
                result.privatePeakGrowthBytes = Math.Max(result.privatePeakGrowthBytes, Math.Max(0, privateBytes - result.privateBaselineBytes));
                if (result.graphicsPeakGrowthBytes >= GrowthStopBytes || result.privatePeakGrowthBytes >= GrowthStopBytes)
                {
                    result.growthStopNotReached = false;
                    result.testPhase = "observed-growth-stop-512MiB";
                    AddCheckpoint("growth-stop", SafeAliveCount(selectedEffect), 0, 0, graphics, privateBytes, workingSet);
                    Assert.Fail("The observed 512 MiB growth stop was reached; it is a stop check, not a hard memory cap.");
                }
                if (quilt.QuiltRevision <= activationRevision) continue;
                activationRevision = quilt.QuiltRevision;
                int revisionIndex = checked((int)(quilt.QuiltRevision - result.offRevision));
                if (revisionIndex <= 0) continue;
                int alive = SafeAliveCount(selectedEffect);
                int changedPixels = 0;
                int maximumDelta = 0;
                int[] changedPerTile = Array.Empty<int>();
                if (alive > 0 && quilt.LastRenderedViewCount == requestedRenderedViews && guard.HistoryCameraCount == requestedRenderedViews)
                {
                    Color32[] currentPixels = ReadQuiltPixels();
                    ComparePixels(offPixels, currentPixels, out changedPixels, out maximumDelta);
                    changedPerTile = CompareQuiltTiles(offPixels, currentPixels);
                }
                float elapsed = Time.realtimeSinceStartup - activationStart;
                AddCheckpoint("selected-graph-revision", alive, changedPixels, maximumDelta, graphics, privateBytes, workingSet, revisionIndex, elapsed);
                bool allTilesChanged = changedPerTile.Length == requestedRenderedViews && changedPerTile.All(value => value > 0);
                if (!renderGateReached && alive > 0 && changedPixels > 0 && allTilesChanged && quilt.QuiltRevision > result.offRevision)
                {
                    renderGateReached = true;
                    result.activeRevision = quilt.QuiltRevision;
                    result.activeAliveParticles = alive;
                    result.changedPixelCount = changedPixels;
                    result.changedPixelCountPerOutputTile = changedPerTile;
                    result.maximumPixelDelta = maximumDelta;
                    result.historyCameraCountAfterActive = guard.HistoryCameraCount;
                    result.renderedViewCountAfterActive = quilt.LastRenderedViewCount;
                    result.quiltTextureDescriptorAfterActive = DescribeTexture(quilt.QuiltTexture);
                    result.activeRevisionGate = true;
                    result.activeParticlesGate = true;
                    result.pixelDifferenceGate = true;
                    result.pixelDifferenceEachTileGate = allTilesChanged;
                }
                if (revisionIndex == 8) result.checkpoint8Reached = true;
                if (revisionIndex == 16) result.checkpoint16Reached = true;
                if (revisionIndex == MeasurementRevisionCount)
                {
                    result.checkpoint32Reached = true;
                    result.measurementElapsedSeconds = elapsed;
                    break;
                }
            }
            result.growthStopNotReached = result.graphicsPeakGrowthBytes < GrowthStopBytes && result.privatePeakGrowthBytes < GrowthStopBytes;
            result.activeRevisionGate = result.activeRevisionGate || (renderGateReached && result.activeRevision > result.offRevision);
            result.activeParticlesGate = result.activeParticlesGate || result.activeAliveParticles > 0;
            result.pixelDifferenceGate = result.pixelDifferenceGate || result.changedPixelCount > 0;
            result.measurementComplete = result.checkpoint8Reached && result.checkpoint16Reached && result.checkpoint32Reached;
            Assert.IsTrue(renderGateReached, "A new automatic quilt revision with active particles and changed pixels was not observed within the bounded run.");
            Assert.IsTrue(result.pixelDifferenceEachTileGate, "Every output tile must differ from the VFX-off quilt at the active draw gate.");
            Assert.IsTrue(result.measurementComplete, "The fixed 8/16/32-revision memory comparison did not complete within the bounded run.");
            Assert.Greater(Time.frameCount - activationFrame, 0);
            Assert.IsTrue(result.growthStopNotReached, "The observed graphics/private growth stop was reached.");
            result.autoRenderErrorFree = string.IsNullOrEmpty(quilt.LastAutoRenderError);
            Assert.IsTrue(result.autoRenderErrorFree);
            Assert.AreEqual(1, CountActiveEffects());
            Assert.AreEqual(requestedRenderedViews, quilt.LastRenderedViewCount);
            Assert.AreEqual(requestedRenderedViews, guard.HistoryCameraCount);
            result.effectiveAAModesAfterActive = CaptureHistoryAAModes(out result.effectiveAANoneCountAfterActive);
            result.effectiveAANoneAfterActiveGate = result.effectiveAANoneCountAfterActive == requestedRenderedViews;
            Assert.IsTrue(result.effectiveAANoneAfterActiveGate, "Every active history camera must use effective Antialiasing=None.");
            Camera initialActiveHistoryCamera = guard.GetHistoryCamera(0);
            result.historyCameraEntityIdAfterActive = initialActiveHistoryCamera == null ? "null" : initialActiveHistoryCamera.GetEntityId().ToString();
            if (requestedRestartCycles > 0)
                Assert.AreEqual(result.historyCameraEntityIdAfterOff, result.historyCameraEntityIdAfterActive,
                    "The existing isolated history camera changed during the initial active render phase.");
            result.componentSettingsUnchanged = ComponentSettingsUnchanged();
            if (requestedRestartCycles > 0)
            {
                phase = "history-camera-stop-restart-cycles";
                result.testPhase = phase;
                yield return RunHistoryRestartCycles(requestedRestartCycles);
                result.historyCycleGate = historyCycles.Count == requestedRestartCycles && historyCycles.All(value => value.passed);
                Assert.IsTrue(result.historyCycleGate, "One or more isolated history camera stop/restart cycles failed their rendering or resource gates.");
            }
            result.totalMeasurementElapsedSeconds = Time.realtimeSinceStartup - totalMeasurementStart;
            result.totalMeasurementFrames = Time.frameCount - totalMeasurementStartFrame;
            Assert.LessOrEqual(result.totalMeasurementElapsedSeconds, result.maximumTotalMeasurementSeconds,
                "The total bounded stop/restart measurement duration was exceeded.");
            Assert.LessOrEqual(result.totalMeasurementFrames, result.maximumTotalMeasurementFrames,
                "The total bounded stop/restart frame count was exceeded.");
            result.testPhase = "active-draw-gates-passed";
            result.status = "draw-gates-passed";
        }

        IEnumerator RunHistoryRestartCycles(int cycleCount)
        {
            for (int cycleIndex = 1; cycleIndex <= cycleCount; ++cycleIndex)
            {
                var cycle = new HistoryCycle { cycleIndex = cycleIndex };
                historyCycles.Add(cycle);
                string cameraEntityId = referenceCamera == null ? "null" : referenceCamera.GetEntityId().ToString();
                cycle.referenceCameraEntityId = cameraEntityId;
                cycle.referenceCameraStayedSame = referenceCamera != null && cameraEntityId == referenceCameraEntityId && referenceCamera.enabled;
                Assert.IsTrue(cycle.referenceCameraStayedSame, "The normal reference camera identity or enabled state changed before restart.");
                Assert.IsTrue(selectedEffect != null && selectedEffect.gameObject.activeInHierarchy && selectedEffect.enabled,
                    "The selected low-capacity VFX must stay active across history camera restarts.");
                Assert.AreEqual(1, CountActiveEffects());
                Assert.IsTrue(rig.AutoRender && quilt.autoRender, "Natural auto render must be active before stopping a history camera.");
                Assert.AreEqual(1, guard.HistoryCameraCount);
                Camera oldHistoryCamera = guard.GetHistoryCamera(0);
                Assert.IsNotNull(oldHistoryCamera, "The current isolated history camera is unavailable before release.");
                cycle.oldHistoryCameraEntityId = oldHistoryCamera.GetEntityId().ToString();
                string priorCreatedCameraId = cycleIndex == 1 ? result.historyCameraEntityIdAfterActive : historyCycles[cycleIndex - 2].newHistoryCameraEntityId;
                cycle.currentCameraMatchesPriorCreated = cycle.oldHistoryCameraEntityId == priorCreatedCameraId;
                Assert.IsTrue(cycle.currentCameraMatchesPriorCreated, "The current history camera does not match the previous phase's created camera.");
                cycle.historyCameraCountBeforeRelease = guard.HistoryCameraCount;
                cycle.revisionsBeforeRelease = checked((int)quilt.QuiltRevision);

                rig.AutoRender = false;
                quilt.autoRender = false;
                guard.ReleaseQuiltRenderResources();
                cycle.historyCameraCountAfterRelease = guard.HistoryCameraCount;
                cycle.historyReleasedBeforeRestart = cycle.historyCameraCountAfterRelease == 0 && guard.GetHistoryCamera(0) == null;
                cycle.revisionsAfterRelease = checked((int)quilt.QuiltRevision);
                Assert.IsTrue(cycle.historyReleasedBeforeRestart, "The isolated history camera remained registered after resource release.");
                Assert.AreEqual(cycle.revisionsBeforeRelease, cycle.revisionsAfterRelease, "Quilt revision advanced while automatic rendering was stopped.");
                Assert.IsTrue(string.IsNullOrEmpty(quilt.LastAutoRenderError), "Auto render reported an error before the stop/restart cycle.");
                Assert.IsTrue(TryReadMemory(out long releaseGraphics, out long releasePrivate, out long releaseWorkingSet),
                    "A process-global graphics or process-memory counter became unavailable after history release.");
                RecordMemoryGrowth(releaseGraphics, releasePrivate);
                Assert.IsTrue(result.growthStopNotReached, "The observed 512 MiB growth stop was reached after history release.");
                AddCheckpoint("history-cycle-" + cycleIndex.ToString(CultureInfo.InvariantCulture) + "-released", SafeAliveCount(selectedEffect), 0, 0,
                    releaseGraphics, releasePrivate, releaseWorkingSet);

                for (int frame = 0; frame < RestartWaitFrames; ++frame)
                {
                    Assert.IsFalse(rig.AutoRender || quilt.autoRender, "Automatic rendering restarted before the history camera release wait completed.");
                    Assert.AreEqual(0, guard.HistoryCameraCount);
                    yield return null;
                    cycle.framesWaited++;
                    Assert.AreEqual(cycle.revisionsAfterRelease, quilt.QuiltRevision, "Quilt revision changed while waiting with automatic rendering disabled.");
                    Assert.AreEqual(0, guard.HistoryCameraCount, "A history camera was recreated while automatic rendering remained disabled.");
                    Assert.IsTrue(TryReadMemory(out long waitGraphics, out long waitPrivate, out long waitWorkingSet),
                        "A process-global graphics or process-memory counter became unavailable while waiting for history release.");
                    RecordMemoryGrowth(waitGraphics, waitPrivate);
                    Assert.IsTrue(result.growthStopNotReached, "The observed 512 MiB growth stop was reached while waiting for history release.");
                    if (frame == RestartWaitFrames - 1)
                        AddCheckpoint("history-cycle-" + cycleIndex.ToString(CultureInfo.InvariantCulture) + "-before-restart", SafeAliveCount(selectedEffect), 0, 0,
                            waitGraphics, waitPrivate, waitWorkingSet);
                }
                cycle.oldCameraDestroyed = oldHistoryCamera == null;
                cycle.historyCameraCountBeforeRestart = guard.HistoryCameraCount;
                cycle.referenceCameraStayedSame = cycle.referenceCameraStayedSame && referenceCamera != null &&
                    referenceCamera.GetEntityId().ToString() == referenceCameraEntityId && referenceCamera.enabled;
                Assert.IsTrue(cycle.oldCameraDestroyed, "The released history camera Unity object remained alive after the bounded wait.");
                Assert.AreEqual(0, cycle.historyCameraCountBeforeRestart);
                Assert.IsTrue(cycle.referenceCameraStayedSame, "The normal reference camera changed during the isolated-history release wait.");
                long restartRevision = quilt.QuiltRevision;
                int restartFrame = Time.frameCount;
                float restartStart = Time.realtimeSinceStartup;
                rig.AutoRender = true;
                quilt.autoRender = true;
                bool cycleDrawGate = false;
                int cycleRevisionIndex = 0;
                while (cycleRevisionIndex < MeasurementRevisionCount && Time.frameCount - restartFrame < MaximumRunFrames &&
                    Time.realtimeSinceStartup - restartStart < MaximumRunSeconds)
                {
                    yield return null;
                    Assert.IsTrue(rig.AutoRender && quilt.autoRender, "Natural auto render stopped during a history restart cycle.");
                    Assert.IsTrue(string.IsNullOrEmpty(quilt.LastAutoRenderError), "Quilt auto-render failed after history restart: " + Ascii(quilt.LastAutoRenderError));
                    Assert.IsTrue(selectedEffect != null && selectedEffect.gameObject.activeInHierarchy && selectedEffect.enabled,
                        "The selected low-capacity VFX did not remain active after history restart.");
                    Assert.AreEqual(1, CountActiveEffects());
                    Assert.AreEqual(1, quilt.settings.views, "One-view bounded output changed during history restart.");
                    Assert.IsTrue(TryReadMemory(out long graphics, out long privateBytes, out long workingSet),
                        "A process-global graphics or process-memory counter became unavailable during history restart.");
                    RecordMemoryGrowth(graphics, privateBytes);
                    cycle.growthStopNotReached = result.growthStopNotReached;
                    if (!cycle.growthStopNotReached)
                    {
                        AddCheckpoint("history-cycle-" + cycleIndex.ToString(CultureInfo.InvariantCulture) + "-growth-stop", SafeAliveCount(selectedEffect), 0, 0, graphics, privateBytes, workingSet);
                        Assert.Fail("The observed 512 MiB growth stop was reached during a history restart cycle; it is not a hard allocation cap.");
                    }
                    if (quilt.QuiltRevision <= restartRevision) continue;
                    restartRevision = quilt.QuiltRevision;
                    cycleRevisionIndex = checked((int)(quilt.QuiltRevision - (cycle.revisionsAfterRelease)));
                    if (cycleRevisionIndex <= 0) continue;
                    int alive = SafeAliveCount(selectedEffect);
                    int changedPixels = 0;
                    int maximumDelta = 0;
                    if (alive > 0 && quilt.LastRenderedViewCount == 1 && guard.HistoryCameraCount == 1)
                        ComparePixels(offPixels, ReadQuiltPixels(), out changedPixels, out maximumDelta);
                    float elapsed = Time.realtimeSinceStartup - restartStart;
                    string checkpointPhase = "history-cycle-" + cycleIndex.ToString(CultureInfo.InvariantCulture) + "-revision";
                    AddCheckpoint(checkpointPhase, alive, changedPixels, maximumDelta, graphics, privateBytes, workingSet, cycleRevisionIndex, elapsed);
                    if (!cycle.newCameraCreated)
                    {
                        Camera newHistoryCamera = guard.GetHistoryCamera(0);
                        cycle.historyCameraCountAfterRestart = guard.HistoryCameraCount;
                        cycle.newCameraCreated = newHistoryCamera != null;
                        cycle.newHistoryCameraEntityId = newHistoryCamera == null ? "null" : newHistoryCamera.GetEntityId().ToString();
                        cycle.newCameraDiffersFromOld = cycle.newCameraCreated && cycle.newHistoryCameraEntityId != cycle.oldHistoryCameraEntityId;
                        Assert.IsTrue(cycle.newCameraCreated && cycle.newCameraDiffersFromOld,
                            "A fresh isolated history camera was not created after automatic rendering restarted.");
                    }
                    if (cycleRevisionIndex == MeasurementRevisionCount && alive > 0 && changedPixels > 0 &&
                        quilt.LastRenderedViewCount == 1 && guard.HistoryCameraCount == 1)
                    {
                        cycleDrawGate = true;
                        cycle.aliveParticlesAtRevision32 = alive;
                        cycle.changedPixelCountAtRevision32 = changedPixels;
                        cycle.maximumPixelDeltaAtRevision32 = maximumDelta;
                    }
                    if (cycleRevisionIndex == 8) cycle.checkpoints8Reached = true;
                    if (cycleRevisionIndex == 16) cycle.checkpoints16Reached = true;
                    if (cycleRevisionIndex == MeasurementRevisionCount)
                    {
                        cycle.checkpoints32Reached = true;
                        cycle.revisionsAfterRestart = checked((int)quilt.QuiltRevision);
                        cycle.elapsedSeconds = elapsed;
                    }
                }
                cycle.activeDrawGate = cycleDrawGate && cycle.aliveParticlesAtRevision32 > 0 && cycle.changedPixelCountAtRevision32 > 0;
                cycle.growthStopNotReached = result.graphicsPeakGrowthBytes < GrowthStopBytes && result.privatePeakGrowthBytes < GrowthStopBytes;
                string cyclePhase = "history-cycle-" + cycleIndex.ToString(CultureInfo.InvariantCulture) + "-revision";
                cycle.descriptorGate = new[] { 8, 16, MeasurementRevisionCount }.All(index => checkpoints.Any(value =>
                    value.phase == cyclePhase && value.activationRevisionIndex == index && value.batchInfoFailure == ""));
                cycle.passed = cycle.currentCameraMatchesPriorCreated && cycle.oldCameraDestroyed && cycle.historyReleasedBeforeRestart &&
                    cycle.historyCameraCountBeforeRestart == 0 && cycle.newCameraCreated && cycle.newCameraDiffersFromOld &&
                    cycle.referenceCameraStayedSame && cycle.activeDrawGate && cycle.checkpoints8Reached && cycle.checkpoints16Reached &&
                    cycle.checkpoints32Reached && cycle.growthStopNotReached && cycle.descriptorGate && cycle.framesWaited == RestartWaitFrames &&
                    cycle.historyCameraCountAfterRestart == 1 && cycle.revisionsAfterRestart > cycle.revisionsAfterRelease;
                Assert.IsTrue(cycle.checkpoints8Reached && cycle.checkpoints16Reached && cycle.checkpoints32Reached,
                    "A stop/restart cycle did not complete the fixed 8/16/32 natural revision checkpoints.");
                Assert.IsTrue(cycle.activeDrawGate, "Active particles and changed quilt pixels were not observed after history restart.");
                Assert.IsTrue(cycle.passed, "The isolated history camera restart lifecycle gates did not pass.");
            }
        }

        void RecordMemoryGrowth(long graphics, long privateBytes)
        {
            result.graphicsPeakGrowthBytes = Math.Max(result.graphicsPeakGrowthBytes, Math.Max(0, graphics - result.graphicsBaselineBytes));
            result.privatePeakGrowthBytes = Math.Max(result.privatePeakGrowthBytes, Math.Max(0, privateBytes - result.privateBaselineBytes));
            result.growthStopNotReached = result.graphicsPeakGrowthBytes < GrowthStopBytes && result.privatePeakGrowthBytes < GrowthStopBytes;
        }

        IEnumerator WaitForRevisionCount(long baseline, int count, int maxFrames, float maxSeconds)
        {
            int startFrame = Time.frameCount;
            float startTime = Time.realtimeSinceStartup;
            while (quilt.QuiltRevision < baseline + count && Time.frameCount - startFrame < maxFrames && Time.realtimeSinceStartup - startTime < maxSeconds)
            {
                Assert.IsTrue(rig.AutoRender && quilt.autoRender, "Automatic quilt scheduler is not enabled during VFX-off warmup.");
                Assert.IsTrue(string.IsNullOrEmpty(quilt.LastAutoRenderError), "Automatic quilt scheduler reported an error: " + Ascii(quilt.LastAutoRenderError));
                yield return null;
            }
            Assert.GreaterOrEqual(quilt.QuiltRevision, baseline + count, "The VFX-off auto-render warmup did not advance enough quilt revisions.");
        }

        void AddCheckpoint(string checkpointPhase, int alive, int changedPixels, int maxPixelDelta, long graphics = 0, long privateBytes = 0, long workingSet = 0,
            int activationRevisionIndex = 0, float elapsedSeconds = 0f)
        {
            if (graphics == 0) TryReadMemory(out graphics, out privateBytes, out workingSet);
            ReadBatchInfo(out string infoType, out string totalGpu, out string totalCpu, out string activeBatch,
                out string inactiveBatch, out string activeInstance, out string unbatched, out string capacity,
                out string maxCapacity, out string failureText);
            checkpoints.Add(new Checkpoint
            {
                phase = checkpointPhase,
                frame = Time.frameCount,
                quiltRevision = quilt == null ? -1 : quilt.QuiltRevision,
                activationRevisionIndex = activationRevisionIndex,
                elapsedSeconds = elapsedSeconds,
                historyCameraCount = guard == null ? -1 : guard.HistoryCameraCount,
                aliveParticles = alive,
                graphicsBytes = graphics,
                privateBytes = privateBytes,
                workingSetBytes = workingSet,
                graphicsDeltaBytes = Math.Max(0, graphics - result.graphicsBaselineBytes),
                privateDeltaBytes = Math.Max(0, privateBytes - result.privateBaselineBytes),
                changedPixelCount = changedPixels,
                maximumPixelDelta = maxPixelDelta,
                batchInfoType = infoType,
                totalGpuBytes = totalGpu,
                totalCpuBytes = totalCpu,
                activeBatchCount = activeBatch,
                inactiveBatchCount = inactiveBatch,
                activeInstanceCount = activeInstance,
                unbatchedInstanceCount = unbatched,
                totalInstanceCapacity = capacity,
                maxInstancePerBatchCapacity = maxCapacity,
                batchInfoFailure = failureText,
                quiltTextureDescriptor = DescribeTexture(quilt == null ? null : quilt.QuiltTexture),
                viewTextureDescriptor = DescribeTexture(ReadRenderTextureField(quilt, "viewTexture")),
                resolveTextureDescriptor = DescribeTexture(ReadRenderTextureField(quilt, "resolveTexture"))
            });
        }

        static RenderTexture ReadRenderTextureField(QuiltRenderer renderer, string fieldName)
        {
            if (renderer == null) return null;
            FieldInfo field = typeof(QuiltRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(renderer) as RenderTexture;
        }

        void ReadBatchInfo(out string infoType, out string totalGpu, out string totalCpu, out string activeBatch,
            out string inactiveBatch, out string activeInstance, out string unbatched, out string capacity,
            out string maxCapacity, out string failureText)
        {
            infoType = totalGpu = totalCpu = activeBatch = inactiveBatch = activeInstance = unbatched = capacity = maxCapacity = "unavailable";
            failureText = "";
            try
            {
                MethodInfo method = typeof(VFXManager).GetMethod("GetBatchedEffectInfo", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(VisualEffectAsset) }, null);
                if (method == null) { failureText = "api-unavailable"; return; }
                object info = method.Invoke(null, new object[] { selectedEffect.visualEffectAsset });
                if (info == null) { failureText = "returned-null"; return; }
                Type type = info.GetType();
                infoType = type.FullName ?? type.Name;
                totalGpu = ReadMember(type, info, "totalGPUSizeInBytes");
                totalCpu = ReadMember(type, info, "totalCPUSizeInBytes");
                activeBatch = ReadMember(type, info, "activeBatchCount");
                inactiveBatch = ReadMember(type, info, "inactiveBatchCount");
                activeInstance = ReadMember(type, info, "activeInstanceCount");
                unbatched = ReadMember(type, info, "unbatchedInstanceCount");
                capacity = ReadMember(type, info, "totalInstanceCapacity");
                maxCapacity = ReadMember(type, info, "maxInstancePerBatchCapacity");
                if (new[] { totalGpu, totalCpu, activeBatch, activeInstance, unbatched, capacity, maxCapacity }.Any(value => value == "unavailable"))
                    failureText = "one-or-more-fields-unavailable";
            }
            catch (Exception exception) { failureText = exception.GetType().Name; }
        }

        static string ReadMember(Type type, object instance, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                return Convert.ToString(property.GetValue(instance, null), CultureInfo.InvariantCulture) ?? "null";
            FieldInfo field = type.GetField(name, flags);
            return field == null ? "unavailable" : Convert.ToString(field.GetValue(instance), CultureInfo.InvariantCulture) ?? "null";
        }

        Color32[] ReadQuiltPixels()
        {
            Assert.IsNotNull(quilt);
            RenderTexture texture = quilt.QuiltTexture;
            Assert.IsNotNull(texture, "The auto-render quilt texture is unavailable.");
            Assert.IsTrue(texture.IsCreated(), "The auto-render quilt texture is not created.");
            Assert.AreEqual(outputQuiltWidth, texture.width);
            Assert.AreEqual(outputQuiltHeight, texture.height);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, outputQuiltWidth, outputQuiltHeight), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels32();
            }
            finally { RenderTexture.active = previous; }
        }

        string CaptureHistoryAAModes(out int noneCount)
        {
            noneCount = 0;
            var modes = new List<string>();
            int count = guard == null ? 0 : guard.HistoryCameraCount;
            for (int index = 0; index < count; ++index)
            {
                Camera camera = guard.GetHistoryCamera(index);
                UniversalAdditionalCameraData data = camera == null ? null : camera.GetComponent<UniversalAdditionalCameraData>();
                string mode = data == null ? "missing" : data.antialiasing.ToString();
                modes.Add(index.ToString(CultureInfo.InvariantCulture) + ":" + mode);
                if (data != null && data.antialiasing == AntialiasingMode.None) noneCount++;
            }
            return string.Join(",", modes);
        }

        static void ComparePixels(Color32[] baseline, Color32[] current, out int changedCount, out int maxDelta)
        {
            Assert.IsNotNull(baseline);
            Assert.AreEqual(baseline.Length, current.Length);
            changedCount = 0;
            maxDelta = 0;
            for (int i = 0; i < baseline.Length; ++i)
            {
                int delta = Math.Max(Math.Max(Math.Abs(baseline[i].r - current[i].r), Math.Abs(baseline[i].g - current[i].g)),
                    Math.Max(Math.Abs(baseline[i].b - current[i].b), Math.Abs(baseline[i].a - current[i].a)));
                if (delta > 0) changedCount++;
                if (delta > maxDelta) maxDelta = delta;
            }
        }

        int[] CompareQuiltTiles(Color32[] baseline, Color32[] current)
        {
            Assert.AreEqual(outputQuiltWidth * outputQuiltHeight, baseline.Length);
            Assert.AreEqual(baseline.Length, current.Length);
            int tileWidth = outputQuiltWidth / outputColumns;
            int tileHeight = outputQuiltHeight / outputRows;
            int[] changedCounts = new int[requestedRenderedViews];
            for (int tileY = 0; tileY < outputRows; ++tileY)
            for (int tileX = 0; tileX < outputColumns; ++tileX)
            {
                int tileIndex = tileY * outputColumns + tileX;
                int left = tileX * tileWidth;
                int bottom = tileY * tileHeight;
                for (int y = 0; y < tileHeight; ++y)
                for (int x = 0; x < tileWidth; ++x)
                {
                    int index = (bottom + y) * outputQuiltWidth + left + x;
                    Color32 a = baseline[index];
                    Color32 b = current[index];
                    if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) changedCounts[tileIndex]++;
                }
            }
            return changedCounts;
        }

        void CleanupRuntimeState()
        {
            if (sceneEffects != null)
                foreach (VisualEffect effect in sceneEffects)
                    if (effect != null && effect.gameObject.activeSelf) effect.gameObject.SetActive(false);
            try
            {
                if (rig != null && rig.AutoRender) rig.AutoRender = false;
                if (quilt != null) quilt.autoRender = false;
                if (quilt != null) quilt.enabled = false;
                if (guard != null) guard.ReleaseQuiltRenderResources();
            }
            catch (Exception exception)
            {
                if (failure == null) failure = exception;
            }
        }

        void CaptureCleanupState()
        {
            result.historyReleased = guard != null && guard.HistoryCameraCount == 0;
            result.historyCameraCountAfterCleanup = guard == null ? -1 : guard.HistoryCameraCount;
            result.viewTextureReleased = IsReleased("viewTexture");
            result.resolveTextureReleased = IsReleased("resolveTexture");
            RenderTexture quiltTexture = quilt == null ? null : quilt.QuiltTexture;
            result.quiltTextureReleased = quiltTexture == null || !quiltTexture.IsCreated();
            result.activeSceneEffectCountAtCleanup = CountActiveEffects();
            result.sceneEffectsInactiveAfterCleanup = result.activeSceneEffectCountAtCleanup == 0;
            result.autoRenderErrorFree = quilt != null && string.IsNullOrEmpty(quilt.LastAutoRenderError);
            result.cleanupPassed = result.historyReleased && result.viewTextureReleased && result.resolveTextureReleased &&
                result.quiltTextureReleased && result.sceneEffectsInactiveAfterCleanup && result.autoRenderErrorFree &&
                rig != null && !rig.AutoRender && quilt != null && !quilt.autoRender && !sceneSavedDuringRun;
            if (!result.historyReleased && failure == null) failure = new AssertionException("URP history camera resources remained after auto-render cleanup.");
            if (!result.viewTextureReleased && failure == null) failure = new AssertionException("Core per-view texture remained allocated after cleanup.");
            if (!result.resolveTextureReleased && failure == null) failure = new AssertionException("Core resolve texture remained allocated after cleanup.");
            if (!result.quiltTextureReleased && failure == null) failure = new AssertionException("Core quilt texture remained allocated after cleanup.");
            if (!result.sceneEffectsInactiveAfterCleanup && failure == null) failure = new AssertionException("A scene VFX object remained active after cleanup.");
        }

        bool ComputePassGate()
        {
            result.descriptorGate = checkpoints.Count >= 2 && checkpoints.Any(value => value.phase == "vfx-off-warmed") &&
                new[] { 8, 16, MeasurementRevisionCount }.All(index => checkpoints.Any(value =>
                    value.phase == "selected-graph-revision" && value.activationRevisionIndex == index && value.batchInfoFailure == ""));
            return result.graphicsApiGate && result.pipelineGate && result.twoCameraGate && result.isolatedHistoryGate &&
                result.gridResolutionGate &&
                result.outputLayoutGate && result.effectiveAANoneAfterOffGate && result.effectiveAANoneAfterActiveGate &&
                result.offscreenRevisionGate && result.activeRevisionGate && result.activeParticlesGate && result.pixelDifferenceGate &&
                result.pixelDifferenceEachTileGate && result.changedPixelCountPerOutputTile != null &&
                result.changedPixelCountPerOutputTile.Length == requestedRenderedViews && result.changedPixelCountPerOutputTile.All(value => value > 0) &&
                result.measurementComplete && result.growthStopNotReached && result.descriptorGate && result.sourceGraphUnchanged && result.selectedAssetUnchanged && result.sceneDiskUnchanged &&
                !result.sceneSavedDuringRun && result.autoRenderErrorFree && result.noUnityErrorsDuringRun && result.cleanupPassed && result.historyCycleGate &&
                result.rendererReadyBeforeActivation && result.sourceCullingMaskIncludesEffect && result.effectMaterialsSupported &&
                result.componentInstancingGate && result.componentActivationSettingsGate && result.componentSettingsUnchanged &&
                result.rigAutoRenderRestored && result.profileRestored && result.cameraStateRestored && result.selectedRendererStateRestored;
        }

        void RestoreCameraStates()
        {
            try
            {
                if (rig != null)
                {
                    rig.AutoRender = false;
                    if (quilt != null) quilt.enabled = false;
                    if (outputTarget != null) rig.SetOutput(oldOutputBehaviour);
                    if (guard != null) rig.SetGuard(oldGuardBehaviour);
                    if (originalProfile.QuiltWidth > 0) rig.ApplyProfile(originalProfile);
                    if (quilt != null)
                    {
                        quilt.settings = originalSettings;
                        quilt.TrySetViewRenderMode(originalViewMode, out _);
                        quilt.useRenderRequests = originalUseRenderRequests;
                        quilt.autoRender = originalQuiltAutoRender;
                        quilt.enabled = true;
                    }
                    if (outputTarget != null) UnityEngine.Object.Destroy(outputTarget);
                    result.rigAutoRenderRestored = rig.AutoRender == originalRigAutoRender && (quilt == null || quilt.autoRender == originalQuiltAutoRender);
                    result.profileRestored = rig.Profile.QuiltWidth == originalProfile.QuiltWidth && rig.Profile.QuiltHeight == originalProfile.QuiltHeight &&
                        rig.Profile.Columns == originalProfile.Columns && rig.Profile.Rows == originalProfile.Rows &&
                        rig.Profile.Views == originalProfile.Views && rig.Profile.ViewCone == originalProfile.ViewCone;
                }
            }
            catch (Exception exception) { if (failure == null) failure = exception; }
            if (sceneCameras != null)
                for (int i = 0; i < sceneCameras.Length; ++i)
                    if (sceneCameras[i] != null) sceneCameras[i].enabled = cameraEnabledBefore[i];
            RestoreCamera(referenceCamera, referenceCameraBefore);
            RestoreCamera(sourceCamera, sourceCameraBefore);
            if (referenceCamera != null && sourceCamera != null)
                result.cameraStateRestored = CameraStateRestored(referenceCamera, referenceCameraBefore) && CameraStateRestored(sourceCamera, sourceCameraBefore) &&
                    sceneCameras.Select((camera, index) => camera == null || camera.enabled == cameraEnabledBefore[index]).All(value => value);
        }

        void RestoreEffectAssets()
        {
            if (selectedEffect != null && originalEffectAsset != null) selectedEffect.visualEffectAsset = originalEffectAsset;
            if (selectedRenderer != null)
            {
                selectedRenderer.enabled = originalRendererEnabled;
                selectedRenderer.forceRenderingOff = originalRendererForceRenderingOff;
                result.selectedRendererStateRestored = selectedRenderer.enabled == originalRendererEnabled &&
                    selectedRenderer.forceRenderingOff == originalRendererForceRenderingOff;
            }
            if (readback != null) UnityEngine.Object.Destroy(readback);
        }

        bool ComponentSettingsUnchanged()
        {
            if (selectedEffect == null) return false;
            var serialized = new SerializedObject(selectedEffect);
            SerializedProperty allowInstancingProperty = serialized.FindProperty("m_AllowInstancing");
            SerializedProperty releaseOnDisableProperty = serialized.FindProperty("m_ReleaseInstanceOnDisable");
            return allowInstancingProperty != null && releaseOnDisableProperty != null &&
                allowInstancingProperty.boolValue == originalComponentAllowInstancing &&
                releaseOnDisableProperty.boolValue == originalComponentReleaseOnDisable &&
                selectedEffect.pause == originalComponentPause && selectedEffect.resetSeedOnPlay == originalComponentResetSeedOnPlay &&
                selectedEffect.startSeed == originalComponentStartSeed && Mathf.Approximately(selectedEffect.playRate, originalComponentPlayRate) &&
                selectedEffect.initialEventName == originalComponentInitialEventName;
        }

        bool IsReleased(string fieldName)
        {
            if (quilt == null) return true;
            FieldInfo field = typeof(QuiltRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return false;
            RenderTexture texture = field.GetValue(quilt) as RenderTexture;
            return texture == null || !texture.IsCreated();
        }

        static string DescribeTexture(RenderTexture texture)
        {
            if (texture == null) return "null";
            RenderTextureDescriptor descriptor = texture.descriptor;
            return descriptor.width.ToString(CultureInfo.InvariantCulture) + "x" + descriptor.height.ToString(CultureInfo.InvariantCulture) +
                ";format=" + descriptor.graphicsFormat.ToString() + ";srgb=" + texture.sRGB.ToString() +
                ";msaa=" + descriptor.msaaSamples.ToString(CultureInfo.InvariantCulture) + ";created=" + texture.IsCreated().ToString();
        }

        static CameraState CaptureCamera(Camera camera)
        {
            if (camera == null) return null;
            UniversalAdditionalCameraData additional = camera.GetComponent<UniversalAdditionalCameraData>();
            return new CameraState
            {
                camera = camera,
                enabled = camera.enabled,
                allowHdr = camera.allowHDR,
                allowMsaa = camera.allowMSAA,
                allowDynamicResolution = camera.allowDynamicResolution,
                targetTexture = camera.targetTexture,
                aspect = camera.aspect,
                fieldOfView = camera.fieldOfView,
                position = camera.transform.position,
                rotation = camera.transform.rotation,
                additionalData = additional,
                antialiasing = additional == null ? AntialiasingMode.None : additional.antialiasing,
                antialiasingQuality = additional == null ? AntialiasingQuality.High : additional.antialiasingQuality,
                postProcessing = additional != null && additional.renderPostProcessing
            };
        }

        static void RestoreCamera(Camera camera, CameraState snapshot)
        {
            if (camera == null || snapshot == null) return;
            camera.allowHDR = snapshot.allowHdr;
            camera.allowMSAA = snapshot.allowMsaa;
            camera.allowDynamicResolution = snapshot.allowDynamicResolution;
            camera.targetTexture = snapshot.targetTexture;
            camera.aspect = snapshot.aspect;
            camera.fieldOfView = snapshot.fieldOfView;
            camera.transform.SetPositionAndRotation(snapshot.position, snapshot.rotation);
            if (snapshot.additionalData != null)
            {
                snapshot.additionalData.antialiasing = snapshot.antialiasing;
                snapshot.additionalData.antialiasingQuality = snapshot.antialiasingQuality;
                snapshot.additionalData.renderPostProcessing = snapshot.postProcessing;
            }
            camera.enabled = snapshot.enabled;
        }

        static bool CameraStateRestored(Camera camera, CameraState snapshot)
        {
            if (camera == null || snapshot == null) return camera == null && snapshot == null;
            UniversalAdditionalCameraData additional = camera.GetComponent<UniversalAdditionalCameraData>();
            return camera.enabled == snapshot.enabled && camera.allowHDR == snapshot.allowHdr && camera.allowMSAA == snapshot.allowMsaa &&
                camera.allowDynamicResolution == snapshot.allowDynamicResolution && camera.targetTexture == snapshot.targetTexture &&
                Mathf.Approximately(camera.aspect, snapshot.aspect) && Mathf.Approximately(camera.fieldOfView, snapshot.fieldOfView) &&
                camera.transform.position == snapshot.position && camera.transform.rotation == snapshot.rotation &&
                (additional == null || (additional.antialiasing == snapshot.antialiasing &&
                    additional.antialiasingQuality == snapshot.antialiasingQuality && additional.renderPostProcessing == snapshot.postProcessing));
        }

        void OnSceneSaved(Scene scene) { if (scene == loadedScene) sceneSavedDuringRun = true; }

        void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            loggedErrorCount++;
            if (string.IsNullOrEmpty(firstLoggedError)) firstLoggedError = message;
        }

        int CountActiveEffects() => sceneEffects == null ? 0 : sceneEffects.Count(value => value != null && value.gameObject.activeInHierarchy && value.enabled);
        int CountEnabledCameras() => sceneCameras == null ? 0 : sceneCameras.Count(value => value != null && value.enabled);
        static int SafeAliveCount(VisualEffect effect) { try { return effect == null ? -1 : effect.aliveParticleCount; } catch { return -1; } }

        static bool TryReadMemory(out long graphics, out long privateBytes, out long workingSet)
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

        void WriteResult()
        {
            if (string.IsNullOrEmpty(evidencePath)) return;
            result.testPhase = phase;
            string path = Path.Combine(evidencePath, "native-result.json");
            string json = Ascii(JsonUtility.ToJson(result, false));
            File.WriteAllText(path, json, new UTF8Encoding(false));
            if (readback != null) UnityEngine.Object.Destroy(readback);
        }

        static bool IsFinite(Vector3 value) => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static string AbsoluteAssetPath(string assetPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToUpperInvariant();
        }

        static string SafeSha(string path)
        {
            try { return File.Exists(path) ? Sha256(path) : ""; }
            catch { return ""; }
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var output = new StringBuilder(value.Length);
            foreach (char character in value) output.Append(character >= 32 && character <= 126 ? character : '?');
            return output.ToString();
        }

        static string HierarchyPath(Transform value)
        {
            if (value == null) return string.Empty;
            var names = new Stack<string>();
            for (Transform current = value; current != null; current = current.parent)
                names.Push(current.name);
            return Ascii(string.Join("/", names));
        }
    }
}
#endif
