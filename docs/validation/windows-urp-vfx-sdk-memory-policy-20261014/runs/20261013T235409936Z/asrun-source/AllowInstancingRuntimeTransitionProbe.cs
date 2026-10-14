#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
    public sealed class AllowInstancingRuntimeTransitionProbe
    {
        const string AssetPath = "Assets/FrustumMemoryFixturesGrid40/Auto/SampleTexture2D.vfx";
        const string AssetGuid = "186806741105897419284e4bc5055e91";
        const string AssetSha256 = "62A767F8977E0C6125A16878F47520A59B83AF66F8F2F70B4A37BFE1913DE7BD";
        const string InputSchema = "viewweave-sample-texture-native-probe-v2-grid40";
        const string InputResultEnvironment = "VW_ALLOW_INSTANCING_INPUT_NATIVE_RESULT";
        const string OutputResultEnvironment = "VW_ALLOW_INSTANCING_TRANSITION_RESULT";
        const int Width = 128;
        const int Height = 96;
        const int WarmupRevisions = 8;
        const long GrowthLimitBytes = 512L * 1024L * 1024L;

        [Serializable]
        sealed class InputProof
        {
            public string schema, status, mode, selectedAssetPath, selectedAssetGuid, selectedAssetSha256, selectedAssetSha256After;
            public int selectedParticleCapacity, selectedGridResolutionX, selectedGridResolutionY, changedPixelCount;
        }

        [Serializable]
        sealed class Sample
        {
            public string phase;
            public int frame, aliveParticles, historyCameraCount, renderedViews;
            public long quiltRevision, graphicsBytes, privateBytes, workingSetBytes, graphicsDeltaBytes, privateDeltaBytes;
            public int changedPixelsFromVfxOff, changedPixelsFromPrior, maxChannelDeltaFromPrior;
            public string allowInstancing, pause, batchInfoType, totalGpuBytes, totalCpuBytes;
            public string activeBatchCount, inactiveBatchCount, activeInstanceCount, unbatchedInstanceCount;
            public string totalInstanceCapacity, maxInstancePerBatchCapacity, batchInfoFailure;
            public uint startSeed;
            public bool resetSeedOnPlay;
            public float playRate;
            public string initialEventName, particleSystemStates, spawnSystemStates;
        }

        [Serializable]
        sealed class Result
        {
            public string schema = "viewweave-allow-instancing-runtime-transition-v1";
            public string status = "started", classification = "inconclusive", failureType, failureMessage, testPhase;
            public string unityVersion, graphicsApi, graphicsDeviceName, vfxPackageVersion, renderPipeline;
            public string selectedAssetPath, selectedAssetGuid, selectedAssetSha256Before, selectedAssetSha256After;
            public string inputNativeResultPath, inputNativeResultSha256, inputNativeResultStatus;
            public string sceneName, sceneWasCreatedEmpty, sceneSavedDuringProbe;
            public int totalVfxComponentsAtStart, totalVfxComponentsWithProbe, activeVfxComponentsAtWarmup, activeVfxComponentsAtCleanup;
            public int requestedViews, renderedViews, historyCameraCountAtWarmup, historyCameraCountAtCleanup;
            public string quiltDescriptor, lastAutoRenderError;
            public long graphicsBaselineBytes, privateBaselineBytes, workingSetBaselineBytes;
            public long graphicsPeakGrowthBytes, privatePeakGrowthBytes, growthLimitBytes;
            public bool runtimePlatformGate, graphicsApiGate, pipelineGate, inputProofGate, selectedAssetGate;
            public bool freshSceneGate, singletonVfxGate, rendererSetupGate, naturalAutoRenderGate, warmupRevisionGate;
            public bool vfxOnDrawGate, pauseGate, setterFalseGate, setterTrueGate, pauseRetentionGate;
            public bool falseSetterPixelsMatchPause, trueSetterPixelsMatchPause;
            public bool unpausedContinuationGate, memoryGuardNotReached, noUnityErrors, noSceneSave;
            public bool sourceAssetUnchanged, historyReleased, quiltTextureReleased, cleanupPassed;
            public bool componentSettingsRestored, probeEffectInactiveAfterCleanup;
            public int memorySampleCount, memoryReadFailureCount;
            public bool memoryCaptureComplete;
            public string spawnerStateReadSemantics = "Read-only GetSpawnSystemNames/GetSpawnSystemInfo values; missing API/state is recorded unavailable. Disposable native state wrappers are disposed after each read.";
            public string behaviorClassificationBasis;
            public string processMemorySemantics = "Graphics driver allocation and Unity Editor private/process working-set counters are process-global observations sampled after the VFX-off warm baseline; threshold stop is post-allocation and is not a hard cap or physical VRAM measure.";
            public Sample[] samples;
            public int unityErrorCount;
            public string firstUnityError;
        }

        Result result;
        Exception failure;
        string resultPath;
        string inputPath;
        string sceneAssetAbsolutePath;
        VisualEffectAsset selectedAsset;
        VisualEffect effect;
        VFXRenderer effectRenderer;
        GameObject effectObject;
        GameObject rigObject;
        GameObject referenceObject;
        ViewWeaveRig rig;
        QuiltRenderer quilt;
        UrpQuiltCamera guard;
        Camera referenceCamera;
        Camera sourceCamera;
        QuiltOutputTarget output;
        Scene probeScene;
        Scene previousActiveScene;
        bool sceneSaveSubscribed;
        bool logSubscribed;
        bool sceneSaved;
        int errorCount;
        string firstError = "";
        Texture2D readback;
        Color32[] vfxOffPixels;
        Color32[] lastPixels;
        readonly List<Sample> samples = new List<Sample>();
        long graphicsBaseline;
        long privateBaseline;
        long workingSetBaseline;
        long graphicsPeakGrowth;
        long privatePeakGrowth;
        bool memoryStopReached;
        bool originalAllowInstancing;
        bool originalPause;
        bool componentStateCaptured;
        bool memoryBaselineCaptured;

        [UnityTest]
        public IEnumerator MeasuresPublicAllowInstancingTransitionsWithoutReinitializingTheVfx()
        {
            result = new Result();
            failure = null;
            IEnumerator body = RunProbe();
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
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

            IEnumerator cleanup = Cleanup();
            while (cleanup.MoveNext()) yield return cleanup.Current;
            result.failureType = failure == null ? "" : failure.GetType().FullName;
            result.failureMessage = failure == null ? "" : Ascii(failure.Message);
            result.unityErrorCount = errorCount;
            result.firstUnityError = firstError;
            result.noUnityErrors = errorCount == 0;
            result.noSceneSave = !sceneSaved;
            result.sceneSavedDuringProbe = sceneSaved.ToString();
            result.selectedAssetSha256After = File.Exists(sceneAssetAbsolutePath) ? Sha256(sceneAssetAbsolutePath) : "missing";
            result.sourceAssetUnchanged = result.selectedAssetSha256Before == result.selectedAssetSha256After;
            if (guard != null) result.historyCameraCountAtCleanup = guard.HistoryCameraCount;
            if (quilt != null) result.quiltTextureReleased = quilt.QuiltTexture == null;
            result.activeVfxComponentsAtCleanup = CountSceneVfx(true);
            result.cleanupPassed = result.historyReleased && result.quiltTextureReleased && result.componentSettingsRestored &&
                result.probeEffectInactiveAfterCleanup && result.activeVfxComponentsAtCleanup == 0;
            result.memoryGuardNotReached = !memoryStopReached && graphicsPeakGrowth < GrowthLimitBytes && privatePeakGrowth < GrowthLimitBytes;
            result.memoryCaptureComplete = result.memorySampleCount > 0 && result.memoryReadFailureCount == 0;
            result.graphicsPeakGrowthBytes = graphicsPeakGrowth;
            result.privatePeakGrowthBytes = privatePeakGrowth;
            result.status = failure == null && ComputeBehaviorClassification() != "inconclusive" && AllRequiredGates()
                ? "passed" : "not-passed";
            result.classification = failure == null ? ComputeBehaviorClassification() : "inconclusive";
            result.behaviorClassificationBasis = "Setter effects are classified from observed component state, VFX batch API fields, particle counts, and captured quilt pixels. API byte fields and process-global memory counters are separate measures; neither is a physical VRAM attribution.";
            result.samples = samples.ToArray();
            WriteResult();
            if (failure != null) Assert.Fail("Runtime transition probe failed: " + result.failureType + ": " + result.failureMessage);
            Assert.AreEqual("passed", result.status, "The probe did not complete every setup, behavior, memory, and cleanup gate.");
        }

        IEnumerator RunProbe()
        {
            result.testPhase = "input-and-environment";
            Assert.AreEqual("6000.6.4f1", Application.unityVersion);
            Assert.AreEqual(RuntimePlatform.WindowsEditor, Application.platform);
            Assert.AreEqual(GraphicsDeviceType.Direct3D12, SystemInfo.graphicsDeviceType);
            result.unityVersion = Application.unityVersion;
            result.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            result.graphicsDeviceName = Ascii(SystemInfo.graphicsDeviceName);
            result.renderPipeline = GraphicsSettings.currentRenderPipeline == null ? "none" : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            result.runtimePlatformGate = Application.platform == RuntimePlatform.WindowsEditor;
            result.graphicsApiGate = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            result.pipelineGate = GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            Assert.IsTrue(result.graphicsApiGate && result.pipelineGate);

            inputPath = RequiredEnvironment(InputResultEnvironment);
            resultPath = Path.GetFullPath(RequiredEnvironment(OutputResultEnvironment));
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string allowedResultRoot = Path.Combine(projectRoot, "TestResults") + Path.DirectorySeparatorChar;
            Assert.IsTrue(resultPath.StartsWith(allowedResultRoot, StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(File.Exists(resultPath), "Refusing to overwrite a prior result file.");
            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            result.inputNativeResultPath = inputPath;
            result.inputNativeResultSha256 = Sha256(inputPath);
            InputProof input = JsonUtility.FromJson<InputProof>(File.ReadAllText(inputPath, Encoding.UTF8));
            Assert.IsNotNull(input);
            result.inputNativeResultStatus = input.status;
            string provenInputAssetSha = string.IsNullOrEmpty(input.selectedAssetSha256) ? input.selectedAssetSha256After : input.selectedAssetSha256;
            result.inputProofGate = input.schema == InputSchema && input.status == "passed" && input.mode == "Auto" &&
                input.selectedAssetPath == AssetPath && input.selectedAssetGuid == AssetGuid && provenInputAssetSha == AssetSha256 &&
                input.selectedParticleCapacity == 1600 && input.selectedGridResolutionX == 40 && input.selectedGridResolutionY == 40 && input.changedPixelCount > 0;
            Assert.IsTrue(result.inputProofGate, "The prior exact Grid40 Auto native-pass evidence did not match.");
            Assert.AreEqual(AssetGuid, AssetDatabase.AssetPathToGUID(AssetPath));
            selectedAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(AssetPath);
            Assert.IsNotNull(selectedAsset);
            sceneAssetAbsolutePath = Path.GetFullPath(Path.Combine(projectRoot, AssetPath.Replace('/', Path.DirectorySeparatorChar)));
            result.selectedAssetPath = AssetPath;
            result.selectedAssetGuid = AssetDatabase.AssetPathToGUID(AssetPath);
            result.selectedAssetSha256Before = Sha256(sceneAssetAbsolutePath);
            result.selectedAssetGate = result.selectedAssetSha256Before == AssetSha256;
            Assert.IsTrue(result.selectedAssetGate, "The selected graph does not match the frozen native-pass graph SHA.");
            result.vfxPackageVersion = "17.6.0";
            result.growthLimitBytes = GrowthLimitBytes;

            Application.logMessageReceived += OnLogMessage;
            logSubscribed = true;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            sceneSaveSubscribed = true;
            previousActiveScene = SceneManager.GetActiveScene();
            probeScene = SceneManager.CreateScene("AllowInstancingTransitionProbe_" + Guid.NewGuid().ToString("N"));
            Assert.IsTrue(probeScene.IsValid() && probeScene.isLoaded);
            Assert.IsTrue(SceneManager.SetActiveScene(probeScene));
            result.sceneName = probeScene.name;
            result.sceneWasCreatedEmpty = "true";
            Assert.AreEqual(0, CountSceneVfx(false), "The new probe scene must begin without VFX components.");
            Assert.AreEqual(0, CountAllSceneVfx(false), "The probe requires no other loaded-scene VFX components.");
            result.freshSceneGate = true;

            CreateRenderRig();
            Assert.IsTrue(result.rendererSetupGate);
            Assert.IsTrue(result.naturalAutoRenderGate);
            effectObject = new GameObject("Grid40AutoEffect");
            SceneManager.MoveGameObjectToScene(effectObject, probeScene);
            effectObject.SetActive(false);
            effectObject.transform.position = new Vector3(-105f, 0f, 0f);
            effect = effectObject.AddComponent<VisualEffect>();
            effect.visualEffectAsset = selectedAsset;
            effect.enabled = true;
            effect.pause = false;
            effectRenderer = effectObject.GetComponent<VFXRenderer>();
            Assert.IsNotNull(effectRenderer);
            effectRenderer.enabled = true;
            effectRenderer.forceRenderingOff = false;
            Assert.AreEqual(AssetGuid, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(effect.visualEffectAsset)));
            result.totalVfxComponentsWithProbe = CountAllSceneVfx(false);
            Assert.AreEqual(1, result.totalVfxComponentsWithProbe);
            result.singletonVfxGate = true;
            originalAllowInstancing = effect.allowInstancing;
            originalPause = effect.pause;
            componentStateCaptured = true;
            Assert.IsTrue(originalAllowInstancing, "The Grid40 Auto probe must start with allowInstancing=true.");
            Assert.IsFalse(originalPause);
            var materials = new List<Material>();
            effectRenderer.GetSharedMaterials(materials);
            Assert.IsTrue(materials.Count > 0 && materials.All(material => material != null && material.shader != null && material.shader.isSupported),
                "The selected VFX graph has no supported renderer material.");
            result.totalVfxComponentsAtStart = CountAllSceneVfx(false);
            result.quiltDescriptor = "128x96;tile=128x96;layout=1x1;views=1;Full;cone=0";

            result.testPhase = "vfx-off-warmup";
            effectObject.SetActive(false);
            long warmStartRevision = quilt.QuiltRevision;
            yield return WaitForRevisions(warmStartRevision, WarmupRevisions, 180, 20f);
            Assert.AreEqual(1, quilt.LastRenderedViewCount);
            Assert.AreEqual(1, guard.HistoryCameraCount);
            result.historyCameraCountAtWarmup = guard.HistoryCameraCount;
            result.renderedViews = quilt.LastRenderedViewCount;
            result.requestedViews = 1;
            result.activeVfxComponentsAtWarmup = CountSceneVfx(true);
            Assert.AreEqual(0, result.activeVfxComponentsAtWarmup);
            vfxOffPixels = ReadQuiltPixels();
            lastPixels = vfxOffPixels;
            Assert.IsTrue(TryReadMemory(out graphicsBaseline, out privateBaseline, out workingSetBaseline));
            memoryBaselineCaptured = true;
            result.graphicsBaselineBytes = graphicsBaseline;
            result.privateBaselineBytes = privateBaseline;
            result.workingSetBaselineBytes = workingSetBaseline;
            result.warmupRevisionGate = quilt.QuiltRevision >= warmStartRevision + WarmupRevisions && guard.HistoryCameraCount == 1 && result.activeVfxComponentsAtWarmup == 0;
            Capture("vfx-off-warmed");

            result.testPhase = "activate-grid40-auto";
            Assert.IsTrue(effect.allowInstancing && !effect.pause);
            effectObject.SetActive(true);
            long activationRevision = quilt.QuiltRevision;
            yield return WaitForLiveDraw(activationRevision, 180, 20f);
            Assert.IsTrue(result.vfxOnDrawGate, "Grid40 VFX did not produce alive particles and new quilt pixels.");
            Capture("auto-live-before-transition");

            result.testPhase = "pause-and-stabilize";
            effect.pause = true;
            result.pauseGate = effect.pause;
            Assert.IsTrue(result.pauseGate);
            long pauseRevision = quilt.QuiltRevision;
            yield return WaitForRevisions(pauseRevision, 4, 120, 10f);
            Sample pauseStableA = Capture("pause-stable-before-false-a");
            Color32[] pausePixelsA = ReadQuiltPixels();
            yield return WaitForRevisions(quilt.QuiltRevision, 4, 120, 10f);
            Sample pauseStable = Capture("pause-stable-before-false");
            Color32[] pausePixelsB = ReadQuiltPixels();
            bool pauseStablePixels = CountPixelDifference(pausePixelsA, pausePixelsB, 0) == 0;
            bool pauseStableAlive = pauseStable.aliveParticles == pauseStableA.aliveParticles;
            result.pauseRetentionGate = pauseStablePixels && pauseStableAlive;
            Assert.IsTrue(result.pauseRetentionGate, "The paused singleton did not retain its particle/image state before the setter transition.");

            result.testPhase = "public-setter-false-paused";
            effect.allowInstancing = false;
            result.setterFalseGate = !effect.allowInstancing && effect.pause && effect.gameObject.activeInHierarchy;
            Assert.IsTrue(result.setterFalseGate, "The public setter did not leave the live paused component in the requested state.");
            Capture("false-immediate");
            for (int frame = 1; frame <= 4; frame++)
            {
                long before = quilt.QuiltRevision;
                yield return null;
                Capture("false-frame-" + frame.ToString(CultureInfo.InvariantCulture));
                if (frame == 4 && quilt.QuiltRevision <= before) result.naturalAutoRenderGate &= quilt.autoRender && rig.AutoRender;
                if (memoryStopReached) break;
            }
            if (!memoryStopReached)
            {
                yield return WaitForRevisions(quilt.QuiltRevision, 4, 120, 10f);
                Sample falseStable = Capture("false-paused-stable");
                Color32[] falsePausedPixels = ReadQuiltPixels();
                result.falseSetterPixelsMatchPause = CountPixelDifference(pausePixelsB, falsePausedPixels, 0) == 0;
                result.pauseRetentionGate &= falseStable.aliveParticles == pauseStable.aliveParticles && result.falseSetterPixelsMatchPause;
            }

            result.testPhase = "public-setter-true-paused";
            if (!memoryStopReached)
            {
                effect.allowInstancing = true;
                result.setterTrueGate = effect.allowInstancing && effect.pause && effect.gameObject.activeInHierarchy;
                Assert.IsTrue(result.setterTrueGate, "The public setter did not restore allowInstancing=true on the live paused component.");
                Capture("true-immediate");
                for (int frame = 1; frame <= 4; frame++)
                {
                    yield return null;
                    Capture("true-frame-" + frame.ToString(CultureInfo.InvariantCulture));
                    if (memoryStopReached) break;
                }
                if (!memoryStopReached)
                {
                    yield return WaitForRevisions(quilt.QuiltRevision, 4, 120, 10f);
                    Sample trueStable = Capture("true-paused-stable");
                    Color32[] truePausedPixels = ReadQuiltPixels();
                    result.trueSetterPixelsMatchPause = CountPixelDifference(pausePixelsB, truePausedPixels, 0) == 0;
                    result.pauseRetentionGate &= trueStable.aliveParticles == pauseStable.aliveParticles && result.trueSetterPixelsMatchPause;
                }
            }

            result.testPhase = "resume-simulation";
            if (!memoryStopReached)
            {
                Color32[] beforeResumePixels = ReadQuiltPixels();
                effect.pause = false;
                long resumeRevision = quilt.QuiltRevision;
                int aliveBeforeResume = effect.aliveParticleCount;
                yield return WaitForLiveDraw(resumeRevision, 180, 20f);
                int aliveAfterResume = effect.aliveParticleCount;
                result.unpausedContinuationGate = !effect.pause && quilt.QuiltRevision > resumeRevision && aliveAfterResume > 0 &&
                    (aliveAfterResume != aliveBeforeResume || CountPixelDifference(beforeResumePixels, ReadQuiltPixels(), 5) > 0);
                for (int i = 0; i < 8 && !memoryStopReached; i++)
                {
                    long before = quilt.QuiltRevision;
                    yield return WaitForRevisions(before, 1, 30, 3f);
                    Capture("unpaused-continuation-" + (i + 1).ToString(CultureInfo.InvariantCulture));
                }
                result.unpausedContinuationGate &= quilt.QuiltRevision > resumeRevision && effect.aliveParticleCount > 0;
                Assert.IsTrue(result.unpausedContinuationGate, "The VFX did not continue simulation and natural rendering after unpause.");
            }
            Assert.IsFalse(memoryStopReached, "The post-allocation graphics/private observation threshold was reached; the case stopped without automatic budget relaxation.");
            result.lastAutoRenderError = Ascii(quilt.LastAutoRenderError ?? "");
            Assert.IsTrue(string.IsNullOrEmpty(result.lastAutoRenderError), "Natural auto render reported an error.");
            Assert.IsTrue(errorCount == 0, "Unity logged an error or exception during the probe.");
            result.status = "observation-complete";
            result.testPhase = "observation-complete";
        }

        void CreateRenderRig()
        {
            rigObject = new GameObject("AllowInstancingProbeRig");
            SceneManager.MoveGameObjectToScene(rigObject, probeScene);
            rigObject.SetActive(false);
            sourceCamera = rigObject.AddComponent<Camera>();
            sourceCamera.enabled = false;
            sourceCamera.allowHDR = false;
            sourceCamera.allowMSAA = false;
            sourceCamera.allowDynamicResolution = false;
            sourceCamera.gameObject.AddComponent<UniversalAdditionalCameraData>().antialiasing = AntialiasingMode.None;
            quilt = rigObject.AddComponent<QuiltRenderer>();
            guard = rigObject.AddComponent<UrpQuiltCamera>();
            output = rigObject.AddComponent<QuiltOutputTarget>();
            rig = rigObject.AddComponent<ViewWeaveRig>();
            rig.ApplyProfile(new QuiltQualityProfile(Width, Height, 1, 1, 1, Width / (float)Height, 0f));
            quilt.referenceCamera = null;
            quilt.settings = new QuiltSettings { quiltWidth = Width, quiltHeight = Height, columns = 1, rows = 1, views = 1,
                viewAspect = Width / (float)Height, fieldOfView = 40f, cameraDistance = 4f, viewCone = 0f };
            quilt.viewRenderMode = QuiltViewRenderMode.Full;
            quilt.useRenderRequests = true;
            rig.SetOutput(output);
            rig.SetGuard(guard);
            referenceObject = new GameObject("AllowInstancingReferenceCamera");
            SceneManager.MoveGameObjectToScene(referenceObject, probeScene);
            referenceCamera = referenceObject.AddComponent<Camera>();
            referenceCamera.enabled = true;
            referenceCamera.allowHDR = false;
            referenceCamera.allowMSAA = false;
            referenceCamera.allowDynamicResolution = false;
            referenceCamera.aspect = Width / (float)Height;
            referenceCamera.fieldOfView = 40f;
            referenceCamera.transform.position = new Vector3(-105f, 0.6f, -4f);
            referenceCamera.transform.LookAt(new Vector3(-105f, 0.6f, 0f));
            referenceCamera.gameObject.AddComponent<UniversalAdditionalCameraData>().antialiasing = AntialiasingMode.None;
            quilt.referenceCamera = referenceCamera;
            Assert.IsTrue(rig.ConfigureComponents(), "The runtime ViewWeave rig could not bind output and URP guard.");
            Assert.IsTrue(rig.ValidateRig(out string validationError), "The runtime ViewWeave rig is invalid: " + Ascii(validationError));
            rigObject.SetActive(true);
            rig.AutoRender = true;
            result.rendererSetupGate = guard.renderingMode == UrpQuiltRenderingMode.IsolatedViewHistories &&
                guard.HistoryCameraCount == 0 && quilt.sourceCamera == sourceCamera && quilt.referenceCamera == referenceCamera &&
                !sourceCamera.enabled && referenceCamera.enabled && quilt.settings.views == 1 && quilt.settings.quiltWidth == Width &&
                quilt.settings.quiltHeight == Height && quilt.viewRenderMode == QuiltViewRenderMode.Full && Mathf.Approximately(quilt.settings.viewCone, 0f);
            result.naturalAutoRenderGate = rig.AutoRender && quilt.autoRender && quilt.useRenderRequests && !sourceCamera.enabled && referenceCamera.enabled;
            Assert.IsTrue(result.rendererSetupGate && result.naturalAutoRenderGate);
            readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave };
        }

        IEnumerator WaitForRevisions(long baseline, int count, int maximumFrames, float maximumSeconds)
        {
            int startFrame = Time.frameCount;
            float startTime = Time.realtimeSinceStartup;
            while (quilt.QuiltRevision < baseline + count && Time.frameCount - startFrame < maximumFrames &&
                Time.realtimeSinceStartup - startTime < maximumSeconds)
            {
                if (ObserveMemoryStop()) yield break;
                yield return null;
            }
            Assert.GreaterOrEqual(quilt.QuiltRevision, baseline + count, "Natural auto-render did not reach the requested revision count in time.");
        }

        IEnumerator WaitForLiveDraw(long baseline, int maximumFrames, float maximumSeconds)
        {
            int startFrame = Time.frameCount;
            float startTime = Time.realtimeSinceStartup;
            while (Time.frameCount - startFrame < maximumFrames && Time.realtimeSinceStartup - startTime < maximumSeconds)
            {
                if (quilt.QuiltRevision > baseline && quilt.LastRenderedViewCount == 1 && guard.HistoryCameraCount == 1 && effect.aliveParticleCount > 0)
                {
                    Color32[] current = ReadQuiltPixels();
                    int changed = CountPixelDifference(vfxOffPixels, current, 5);
                    if (changed > 0)
                    {
                        result.vfxOnDrawGate = true;
                        lastPixels = current;
                        yield break;
                    }
                }
                if (ObserveMemoryStop()) yield break;
                yield return null;
            }
            Assert.IsTrue(result.vfxOnDrawGate, "No positive VFX pixels, live particles, and fresh quilt revision were observed.");
        }

        Sample Capture(string phaseName)
        {
            var sample = new Sample { phase = phaseName, frame = Time.frameCount,
                quiltRevision = quilt == null ? -1 : quilt.QuiltRevision, historyCameraCount = guard == null ? -1 : guard.HistoryCameraCount,
                renderedViews = quilt == null ? -1 : quilt.LastRenderedViewCount, aliveParticles = SafeAlive(),
                allowInstancing = effect == null ? "missing" : effect.allowInstancing.ToString(), pause = effect == null ? "missing" : effect.pause.ToString() };
            if (effect != null)
            {
                sample.startSeed = effect.startSeed;
                sample.resetSeedOnPlay = effect.resetSeedOnPlay;
                sample.playRate = effect.playRate;
                sample.initialEventName = Ascii(effect.initialEventName);
                CaptureSystemState(sample);
            }
            sample.batchInfoFailure = ReadBatchInfo(sample);
            Color32[] pixels = null;
            if (quilt != null && quilt.QuiltTexture != null && quilt.QuiltTexture.IsCreated())
            {
                pixels = ReadQuiltPixels();
                sample.changedPixelsFromVfxOff = vfxOffPixels == null ? -1 : CountPixelDifference(vfxOffPixels, pixels, 5);
                sample.changedPixelsFromPrior = lastPixels == null ? -1 : CountPixelDifference(lastPixels, pixels, 5);
                sample.maxChannelDeltaFromPrior = lastPixels == null ? 0 : MaxPixelDifference(lastPixels, pixels);
                lastPixels = pixels;
            }
            if (TryReadMemory(out long graphics, out long privateBytes, out long workingSet))
            {
                if (!memoryBaselineCaptured)
                {
                    sample.graphicsBytes = graphics;
                    sample.privateBytes = privateBytes;
                    sample.workingSetBytes = workingSet;
                    samples.Add(sample);
                    return sample;
                }
                result.memorySampleCount++;
                sample.graphicsBytes = graphics;
                sample.privateBytes = privateBytes;
                sample.workingSetBytes = workingSet;
                sample.graphicsDeltaBytes = graphics - graphicsBaseline;
                sample.privateDeltaBytes = privateBytes - privateBaseline;
                graphicsPeakGrowth = Math.Max(graphicsPeakGrowth, sample.graphicsDeltaBytes);
                privatePeakGrowth = Math.Max(privatePeakGrowth, sample.privateDeltaBytes);
                if (sample.graphicsDeltaBytes >= GrowthLimitBytes || sample.privateDeltaBytes >= GrowthLimitBytes) memoryStopReached = true;
            }
            else if (memoryBaselineCaptured) result.memoryReadFailureCount++;
            samples.Add(sample);
            return sample;
        }

        string ReadBatchInfo(Sample sample)
        {
            try
            {
                MethodInfo method = typeof(VFXManager).GetMethod("GetBatchedEffectInfo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(VisualEffectAsset) }, null);
                if (method == null || selectedAsset == null) return "api-unavailable";
                object info = method.Invoke(null, new object[] { selectedAsset });
                if (info == null) return "returned-null";
                Type type = info.GetType();
                sample.batchInfoType = type.FullName ?? type.Name;
                sample.totalGpuBytes = ReadMember(type, info, "totalGPUSizeInBytes");
                sample.totalCpuBytes = ReadMember(type, info, "totalCPUSizeInBytes");
                sample.activeBatchCount = ReadMember(type, info, "activeBatchCount");
                sample.inactiveBatchCount = ReadMember(type, info, "inactiveBatchCount");
                sample.activeInstanceCount = ReadMember(type, info, "activeInstanceCount");
                sample.unbatchedInstanceCount = ReadMember(type, info, "unbatchedInstanceCount");
                sample.totalInstanceCapacity = ReadMember(type, info, "totalInstanceCapacity");
                sample.maxInstancePerBatchCapacity = ReadMember(type, info, "maxInstancePerBatchCapacity");
                return new[] { sample.totalGpuBytes, sample.totalCpuBytes, sample.activeBatchCount, sample.inactiveBatchCount,
                    sample.activeInstanceCount, sample.unbatchedInstanceCount, sample.totalInstanceCapacity, sample.maxInstancePerBatchCapacity }
                    .Any(value => value == "unavailable") ? "incomplete-fields" : "";
            }
            catch (Exception exception) { return exception.GetType().Name; }
        }

        void CaptureSystemState(Sample sample)
        {
            try
            {
                var particleNames = new List<string>();
                effect.GetParticleSystemNames(particleNames);
                var particleStates = new List<string>(particleNames.Count);
                foreach (string name in particleNames)
                {
                    var info = effect.GetParticleSystemInfo(name);
                    particleStates.Add(Ascii(name) + ":capacity=" + info.capacity.ToString(CultureInfo.InvariantCulture) +
                        ";alive=" + info.aliveCount.ToString(CultureInfo.InvariantCulture) + ";sleeping=" + info.sleeping.ToString());
                }
                sample.particleSystemStates = string.Join("|", particleStates);
            }
            catch (Exception exception) { sample.particleSystemStates = "unavailable:" + exception.GetType().Name; }

            try
            {
                var spawnNames = new List<string>();
                effect.GetSpawnSystemNames(spawnNames);
                MethodInfo method = typeof(VisualEffect).GetMethod("GetSpawnSystemInfo", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(string) }, null);
                var spawnStates = new List<string>(spawnNames.Count);
                foreach (string name in spawnNames)
                {
                    if (method == null) { spawnStates.Add(Ascii(name) + ":unavailable:api-missing"); continue; }
                    object state = null;
                    try
                    {
                        state = method.Invoke(effect, new object[] { name });
                        spawnStates.Add(Ascii(name) + ":" + ReadScalarMembers(state, "totalTime", "spawnCount", "playing", "loopState", "deltaTime", "loopCount"));
                    }
                    finally { (state as IDisposable)?.Dispose(); }
                }
                sample.spawnSystemStates = string.Join("|", spawnStates);
            }
            catch (Exception exception) { sample.spawnSystemStates = "unavailable:" + exception.GetType().Name; }
        }

        static string ReadScalarMembers(object instance, params string[] names)
        {
            if (instance == null) return "null";
            Type type = instance.GetType();
            var values = new List<string>(names.Length);
            foreach (string name in names)
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object value = property != null ? property.GetValue(instance, null) : field == null ? null : field.GetValue(instance);
                values.Add(name + "=" + (property == null && field == null ? "unavailable" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"));
            }
            return string.Join(";", values);
        }

        static string ReadMember(Type type, object instance, string name)
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetIndexParameters().Length == 0)
                return Convert.ToString(property.GetValue(instance, null), CultureInfo.InvariantCulture) ?? "null";
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? "unavailable" : Convert.ToString(field.GetValue(instance), CultureInfo.InvariantCulture) ?? "null";
        }

        IEnumerator Cleanup()
        {
            if (effect != null && componentStateCaptured)
            {
                effect.allowInstancing = originalAllowInstancing;
                effect.pause = originalPause;
                if (effect.gameObject.activeSelf) effect.gameObject.SetActive(false);
                result.componentSettingsRestored = effect.allowInstancing == originalAllowInstancing && effect.pause == originalPause;
                result.probeEffectInactiveAfterCleanup = !effect.gameObject.activeInHierarchy;
            }
            if (rig != null) rig.AutoRender = false;
            if (quilt != null) quilt.autoRender = false;
            if (guard != null) guard.ReleaseQuiltRenderResources();
            if (quilt != null) quilt.enabled = false;
            if (guard != null)
            {
                result.historyCameraCountAtCleanup = guard.HistoryCameraCount;
                result.historyReleased = guard.HistoryCameraCount == 0;
            }
            if (quilt != null) result.quiltTextureReleased = quilt.QuiltTexture == null;
            if (effectObject != null) UnityEngine.Object.Destroy(effectObject);
            if (referenceObject != null) UnityEngine.Object.Destroy(referenceObject);
            if (rigObject != null) UnityEngine.Object.Destroy(rigObject);
            yield return null;
            if (guard != null)
            {
                result.historyCameraCountAtCleanup = guard.HistoryCameraCount;
                result.historyReleased = guard.HistoryCameraCount == 0;
            }
            if (quilt != null) result.quiltTextureReleased = quilt.QuiltTexture == null;
            result.activeVfxComponentsAtCleanup = CountSceneVfx(true);
            if (readback != null) UnityEngine.Object.Destroy(readback);
            if (probeScene.IsValid() && probeScene.isLoaded)
            {
                AsyncOperation operation = SceneManager.UnloadSceneAsync(probeScene);
                if (operation != null) while (!operation.isDone) yield return null;
            }
            if (previousActiveScene.IsValid() && previousActiveScene.isLoaded) SceneManager.SetActiveScene(previousActiveScene);
            if (sceneSaveSubscribed) { EditorSceneManager.sceneSaving -= OnSceneSaving; sceneSaveSubscribed = false; }
            if (logSubscribed) { Application.logMessageReceived -= OnLogMessage; logSubscribed = false; }
            yield return null;
        }

        bool AllRequiredGates()
        {
            return result.runtimePlatformGate && result.graphicsApiGate && result.pipelineGate && result.inputProofGate && result.selectedAssetGate &&
                result.freshSceneGate && result.singletonVfxGate && result.rendererSetupGate && result.naturalAutoRenderGate && result.warmupRevisionGate &&
                result.vfxOnDrawGate && result.pauseGate && result.setterFalseGate && result.setterTrueGate && result.pauseRetentionGate &&
                result.unpausedContinuationGate && result.memoryGuardNotReached && result.memoryCaptureComplete && result.noUnityErrors && result.noSceneSave &&
                result.sourceAssetUnchanged && result.componentSettingsRestored && result.probeEffectInactiveAfterCleanup && result.cleanupPassed;
        }

        string ComputeBehaviorClassification()
        {
            Sample live = samples.FirstOrDefault(item => item.phase == "auto-live-before-transition");
            Sample falseNow = samples.FirstOrDefault(item => item.phase == "false-immediate");
            Sample trueNow = samples.FirstOrDefault(item => item.phase == "true-immediate");
            Sample pauseStable = samples.LastOrDefault(item => item.phase == "pause-stable-before-false");
            Sample falseStable = samples.LastOrDefault(item => item.phase == "false-paused-stable");
            Sample trueStable = samples.LastOrDefault(item => item.phase == "true-paused-stable");
            if (live == null || falseNow == null || trueNow == null || pauseStable == null) return "inconclusive";
            Sample falseObserved = falseStable ?? falseNow;
            Sample trueObserved = trueStable ?? trueNow;
            bool batchChangedOnFalse = BatchSignature(pauseStable) != BatchSignature(falseObserved);
            bool batchRestoredOnTrue = BatchSignature(trueObserved) == BatchSignature(pauseStable);
            bool spawnerStateChangedOnFalse = pauseStable.spawnSystemStates != falseObserved.spawnSystemStates;
            bool spawnerStateRestoredOnTrue = trueObserved.spawnSystemStates == pauseStable.spawnSystemStates;
            bool particleStateReset = falseObserved.aliveParticles <= 0 || falseObserved.aliveParticles != pauseStable.aliveParticles ||
                trueObserved.aliveParticles <= 0 || trueObserved.aliveParticles != pauseStable.aliveParticles;
            bool pixelsCleared = falseObserved.changedPixelsFromVfxOff == 0 || trueObserved.changedPixelsFromVfxOff == 0;
            bool pixelsChangedOnFalse = !result.falseSetterPixelsMatchPause;
            bool pixelsRestoredOnTrue = result.trueSetterPixelsMatchPause;
            bool memoryChanged = falseObserved.graphicsDeltaBytes != pauseStable.graphicsDeltaBytes ||
                falseObserved.privateDeltaBytes != pauseStable.privateDeltaBytes ||
                trueObserved.graphicsDeltaBytes != falseObserved.graphicsDeltaBytes || trueObserved.privateDeltaBytes != falseObserved.privateDeltaBytes;
            bool appearedEmptyBatch = falseObserved.activeInstanceCount == "0" || falseObserved.activeBatchCount == "0";
            if (particleStateReset || pixelsCleared) return "state-reset-or-visible-content-changed";
            if (appearedEmptyBatch) return "empty-batch-or-zero-active-api-state";
            if (!batchChangedOnFalse && !spawnerStateChangedOnFalse && batchRestoredOnTrue && spawnerStateRestoredOnTrue &&
                !pixelsChangedOnFalse && !memoryChanged) return "setter-no-measurable-effect";
            if (!batchChangedOnFalse && memoryChanged) return "memory-change-without-batch-api-change";
            if ((batchChangedOnFalse || spawnerStateChangedOnFalse || pixelsChangedOnFalse) && batchRestoredOnTrue &&
                spawnerStateRestoredOnTrue && pixelsRestoredOnTrue)
                return batchChangedOnFalse ? "batch-api-state-changed-and-restored" :
                    spawnerStateChangedOnFalse ? "spawner-state-changed-and-restored" : "render-content-changed-and-restored";
            if (spawnerStateChangedOnFalse) return "spawner-state-changed-on-setter";
            return "other-observed-change";
        }

        static string BatchSignature(Sample sample) => string.Join("|", new[] { sample.batchInfoType, sample.totalGpuBytes, sample.totalCpuBytes,
            sample.activeBatchCount, sample.inactiveBatchCount, sample.activeInstanceCount, sample.unbatchedInstanceCount,
            sample.totalInstanceCapacity, sample.maxInstancePerBatchCapacity });

        bool ObserveMemoryStop()
        {
            if (!memoryBaselineCaptured) return false;
            Sample sample = Capture("memory-observer");
            return memoryStopReached;
        }

        int SafeAlive() { try { return effect == null ? -1 : effect.aliveParticleCount; } catch { return -1; } }

        Color32[] ReadQuiltPixels()
        {
            RenderTexture texture = quilt == null ? null : quilt.QuiltTexture;
            Assert.IsNotNull(texture);
            Assert.IsTrue(texture.IsCreated());
            Assert.AreEqual(Width, texture.width);
            Assert.AreEqual(Height, texture.height);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels32();
            }
            finally { RenderTexture.active = previous; }
        }

        static int CountPixelDifference(Color32[] first, Color32[] second, int threshold)
        {
            if (first == null || second == null || first.Length != second.Length) return -1;
            int changed = 0;
            for (int i = 0; i < first.Length; i++)
                if (Mathf.Abs(first[i].r - second[i].r) > threshold || Mathf.Abs(first[i].g - second[i].g) > threshold ||
                    Mathf.Abs(first[i].b - second[i].b) > threshold || Mathf.Abs(first[i].a - second[i].a) > threshold) changed++;
            return changed;
        }

        static int MaxPixelDifference(Color32[] first, Color32[] second)
        {
            if (first == null || second == null || first.Length != second.Length) return -1;
            int maximum = 0;
            for (int i = 0; i < first.Length; i++)
            {
                maximum = Math.Max(maximum, Math.Abs(first[i].r - second[i].r));
                maximum = Math.Max(maximum, Math.Abs(first[i].g - second[i].g));
                maximum = Math.Max(maximum, Math.Abs(first[i].b - second[i].b));
                maximum = Math.Max(maximum, Math.Abs(first[i].a - second[i].a));
            }
            return maximum;
        }

        bool TryReadMemory(out long graphics, out long privateBytes, out long workingSet)
        {
            graphics = Profiler.GetAllocatedMemoryForGraphicsDriver();
            privateBytes = 0;
            workingSet = 0;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    privateBytes = process.PrivateMemorySize64;
                    workingSet = process.WorkingSet64;
                }
            }
            catch { return false; }
            return graphics > 0 && privateBytes > 0 && workingSet > 0;
        }

        int CountSceneVfx(bool activeOnly) => UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include)
            .Count(value => value != null && value.gameObject.scene == probeScene && (!activeOnly || value.gameObject.activeInHierarchy && value.enabled));

        int CountAllSceneVfx(bool activeOnly) => UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsInactive.Include)
            .Count(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded &&
                (!activeOnly || value.gameObject.activeInHierarchy && value.enabled));

        static string RequiredEnvironment(string name)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(name + " is required.");
            return value;
        }

        void OnSceneSaving(Scene scene, string path) { if (scene == probeScene) sceneSaved = true; }
        void OnLogMessage(string message, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            errorCount++;
            if (string.IsNullOrEmpty(firstError)) firstError = Ascii(message);
        }

        static string Sha256(string path)
        {
            using (SHA256 hash = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }

        static string Ascii(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var builder = new StringBuilder(value.Length);
            foreach (char character in value) builder.Append(character >= 32 && character <= 126 ? character : '?');
            return builder.ToString();
        }

        void WriteResult()
        {
            if (string.IsNullOrWhiteSpace(resultPath)) return;
            string directory = Path.GetDirectoryName(resultPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(resultPath)) throw new IOException("Refusing to overwrite transition result.");
            File.WriteAllText(resultPath, JsonUtility.ToJson(result, true), new UTF8Encoding(false));
        }
    }
}
#endif
