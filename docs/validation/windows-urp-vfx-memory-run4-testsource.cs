#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using UnityEngine.VFX;
using Object = UnityEngine.Object;
using ViewWeave;
using ViewWeave.URP;

namespace ViewWeave.VfxDriverValidation
{
    public sealed class UrpVfxMemoryScaleTests
    {
        const string SourceAssetPath = "Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx";
        const string MotionOffAssetPath = "Assets/VfxDriverValidation/Generated/MemoryProbe_Capacity512_MotionOff_Run4.vfx";
        const string MotionOnAssetPath = "Assets/VfxDriverValidation/Generated/MemoryProbe_Capacity512_MotionOn_Run4.vfx";
        const string EvidencePath = "TestResults/vfx-memory-scale-run4.txt";
        const string PixelDirectory = "TestResults/vfx-memory-scale-run4-pixels";
        const long SafetyLimitBytes = 512L * 1024L * 1024L;
        const long CleanupGrowthStopBytes = 8L * 1024L * 1024L;

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
            [DllImport("kernel32.dll")]
            static extern IntPtr GetCurrentProcess();

            [DllImport("psapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
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
                catch
                {
                    privateBytes = 0;
                    workingSetBytes = 0;
                    return false;
                }
            }
        }

        sealed class MemorySafety
        {
            internal long rawStart;
            internal long privateStart;
            internal long peakRawDelta;
            internal long peakPrivateDelta;
            internal long lastRawDelta;
            internal long lastPrivateDelta;
            internal bool processMemoryAvailable = true;
            internal bool rawGraphicsMemoryAvailable = true;

            internal bool Sample(out long raw, out long rawDelta, out long privateBytes, out long privateDelta, out long workingSet)
            {
                raw = Profiler.GetAllocatedMemoryForGraphicsDriver();
                if (raw <= 0 || rawStart <= 0)
                {
                    rawGraphicsMemoryAvailable = false;
                    rawDelta = 0;
                    privateBytes = 0;
                    privateDelta = 0;
                    workingSet = 0;
                    return false;
                }
                rawDelta = raw > 0 && rawStart > 0 ? Math.Max(0, raw - rawStart) : 0;
                if (!ProcessMemoryReader.TryRead(out privateBytes, out workingSet))
                {
                    processMemoryAvailable = false;
                    privateBytes = 0;
                    workingSet = 0;
                    privateDelta = 0;
                    return false;
                }
                privateDelta = Math.Max(0, privateBytes - privateStart);
                lastRawDelta = rawDelta;
                lastPrivateDelta = privateDelta;
                peakRawDelta = Math.Max(peakRawDelta, rawDelta);
                peakPrivateDelta = Math.Max(peakPrivateDelta, privateDelta);
                return peakRawDelta < SafetyLimitBytes && peakPrivateDelta < SafetyLimitBytes;
            }
        }

        struct MeasurementCondition
        {
            internal int views;
            internal bool vfx;
            internal bool motion;
            internal string name;
        }

        readonly StringBuilder evidence = new StringBuilder(8192);
        RenderPipelineAsset oldDefaultPipeline;
        RenderPipelineAsset oldQualityPipeline;
        UniversalRenderPipelineAsset testPipeline;
        UniversalRendererData testRenderer;
        Camera[] savedCameras;
        bool[] savedCameraEnabled;
        int oldAntiAliasing;

        [UnityTest]
        public IEnumerator SimpleBurstCapacity512MemoryScaleNineConditions()
        {
            Assert.IsFalse(File.Exists(Path.GetFullPath(Path.Combine(Application.dataPath, "..", EvidencePath))), "Refusing to overwrite prior memory-sweep evidence");
            var safety = new MemorySafety
            {
                rawStart = Profiler.GetAllocatedMemoryForGraphicsDriver()
            };
            try
            {
                Assert.Greater(safety.rawStart, 0, "Graphics driver memory counter is unavailable; refusing to run the memory sweep");
                long startPrivate;
                long startWorkingSet;
                Assert.IsTrue(ProcessMemoryReader.TryRead(out startPrivate, out startWorkingSet), "Win32 GetProcessMemoryInfo baseline is unavailable; refusing to run the memory sweep");
                safety.privateStart = startPrivate;
                evidence.AppendLine("processMemoryApi=Win32 GetProcessMemoryInfo; baselinePrivateBytes=" + startPrivate + "; baselineWorkingSetBytes=" + startWorkingSet + "; monitorRequired=true");
                PrepareUrpPipeline();
                yield return null;
                Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline, "URP pipeline is required");
                Assert.AreEqual(ColorSpace.Linear, QualitySettings.activeColorSpace, "Linear color space is required");
                Assert.AreEqual(1, testPipeline.msaaSampleCount, "MSAA must remain disabled");

                VisualEffectAsset motionOffAsset = CreateGraphAsset(MotionOffAssetPath, false);
                VisualEffectAsset motionOnAsset = CreateGraphAsset(MotionOnAssetPath, true);
                yield return WaitForCompilation(60.0);
                Assert.IsTrue(safety.Sample(out _, out _, out _, out _, out _), "Global safety limit exceeded while preparing cloned VFX graphs");
                foreach (int views in new[] { 1, 2, 8 })
                {
                    yield return MeasureCondition(new MeasurementCondition { views = views, vfx = false, motion = false, name = "vfx-off" }, null, safety);
                    yield return MeasureCondition(new MeasurementCondition { views = views, vfx = true, motion = false, name = "motion-off" }, motionOffAsset, safety);
                    yield return MeasureCondition(new MeasurementCondition { views = views, vfx = true, motion = true, name = "motion-on" }, motionOnAsset, safety);
                }
                evidence.AppendLine("result=all-nine-conditions-completed");
            }
            finally
            {
                RestorePipeline();
                if (evidence.Length == 0) evidence.AppendLine("result=failed-before-first-condition");
                WriteEvidence();
            }
        }

        IEnumerator MeasureCondition(MeasurementCondition condition, VisualEffectAsset vfxAsset, MemorySafety safety)
        {
            double conditionStart = Time.realtimeSinceStartupAsDouble;
            long conditionRawStart = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long conditionPrivateStart;
            long conditionWorkingSetStart;
            if (!ProcessMemoryReader.TryRead(out conditionPrivateStart, out conditionWorkingSetStart))
                Assert.Fail("Win32 GetProcessMemoryInfo condition baseline is unavailable");
            GameObject host = null;
            Camera source = null;
            QuiltRenderer quilt = null;
            UrpQuiltCamera guard = null;
            UrpVfxCameraDriver driver = null;
            UniversalAdditionalCameraData cameraData = null;
            GameObject effectObject = null;
            VisualEffect effect = null;
            VFXRenderer vfxRenderer = null;
            string failure = string.Empty;
            int aliveGateFrame = -1;
            long measurementRawBaseline = 0;
            long measurementPrivateBaseline = 0;
            int cleanupHistoryCount = -1;
            long lastRaw = 0, lastRawDelta = 0, lastPrivate = 0, lastPrivateDelta = 0, lastWorkingSet = 0;
            RenderTexture ownedQuiltTexture = null;
            Camera ownedDriverCamera = null;
            RenderTexture ownedDriverTexture = null;
            GameObject ownedDriverObject = null;
            GameObject ownedEffectObject = null;
            Camera[] ownedHistoryCameras = Array.Empty<Camera>();
            Texture2D enabledPixels = null;
            Texture2D disabledPixels = null;

            evidence.AppendLine("condition=" + condition.name + "; views=" + condition.views + "; requestedVfx=" + condition.vfx + "; motionVector=" + (condition.vfx ? condition.motion.ToString() : "not-applicable") + "; cleanupStopRule=continuousRawOrPrivateGrowthAt2_4_8FramesAndCumulativeDeltaOver8MiB");
            evidence.AppendLine("fixture=cap512; views=" + condition.views + "; tile=128x96; motionVector=" + (condition.vfx ? condition.motion.ToString() : "not-applicable") + "; msaa=1; cameraAA=None; sourceClear=SolidColorBlack; memorySweep=true");
            evidence.AppendLine("testStartRawGraphicsBytes=" + safety.rawStart + "; testStartPrivateBytes=" + safety.privateStart + "; conditionStartRawGraphicsBytes=" + conditionRawStart + "; conditionStartPrivateBytes=" + conditionPrivateStart + "; safetyLimitBytes=" + SafetyLimitBytes + "; budgetMB=" + 4096);
            try
            {
                host = CreateQuiltHost(condition, out source, out quilt, out guard, out cameraData, out driver);

                if (condition.vfx)
                {
                    effectObject = new GameObject("UrpVfxMemoryScale.SimpleBurst");
                    effectObject.layer = 31;
                    effectObject.transform.SetParent(host.transform, false);
                    effectObject.transform.localPosition = new Vector3(0f, 0.5f, 2.2f);
                    effect = effectObject.AddComponent<VisualEffect>();
                    effect.visualEffectAsset = vfxAsset;
                    effect.resetSeedOnPlay = false;
                    effect.startSeed = 17u;
                    effect.pause = false;
                    effect.Reinit();
                    vfxRenderer = effectObject.GetComponent<VFXRenderer>();
                }

                host.SetActive(true);
                yield return null;
                for (int warmup = 1; warmup <= 8; ++warmup)
                {
                    yield return null;
                    quilt.RenderQuilt();
                    if (!safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet))
                    {
                        failure = "safety-limit-during-pipeline-warmup";
                        break;
                    }
                    if (Time.realtimeSinceStartupAsDouble - conditionStart > 60.0)
                    {
                        failure = "condition-exceeded-60-seconds-during-warmup";
                        break;
                    }
                }

                if (failure.Length == 0)
                {
                    if (!driver.IsRunning || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated() || quilt.LastRenderedViewCount != condition.views || guard.HistoryCameraCount != condition.views)
                        failure = "pipeline-warmup-did-not-create-requested-view-histories";
                }
                if (failure.Length == 0 && !HistoryAaIsNone(guard, condition.views))
                    failure = "history-camera-aa-was-not-none";

                if (failure.Length == 0 && condition.vfx)
                {
                    effect.Play();
                    for (int frame = 1; frame <= 30 && aliveGateFrame < 0; ++frame)
                    {
                        yield return null;
                        quilt.RenderQuilt();
                        if (!safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet))
                        {
                            failure = "safety-limit-during-effect-alive-gate";
                            break;
                        }
                        if (effect.aliveParticleCount > 0 && !effect.pause && !effect.culled && vfxRenderer != null && quilt.LastRenderedViewCount == condition.views)
                            aliveGateFrame = frame;
                        if (Time.realtimeSinceStartupAsDouble - conditionStart > 60.0)
                        {
                            failure = "condition-exceeded-60-seconds-during-effect-alive-gate";
                            break;
                        }
                    }
                    if (failure.Length == 0 && aliveGateFrame < 0) failure = "effect-did-not-produce-visible-alive-particles-within-30-frames";
                    if (failure.Length == 0)
                    {
                        for (int aliveFrame = 1; aliveFrame <= 8; ++aliveFrame)
                        {
                            yield return null;
                            quilt.RenderQuilt();
                            if (!safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet))
                            {
                                failure = "safety-limit-during-eight-post-alive-frames";
                                break;
                            }
                            if (effect.aliveParticleCount <= 0 || effect.pause || effect.culled || quilt.LastRenderedViewCount != condition.views)
                            {
                                failure = "vfx-alive-or-render-gate-lost-during-eight-post-alive-frames";
                                break;
                            }
                            if (Time.realtimeSinceStartupAsDouble - conditionStart > 60.0)
                            {
                                failure = "condition-exceeded-60-seconds-during-post-alive-frames";
                                break;
                            }
                        }
                        if (failure.Length == 0)
                        {
                            enabledPixels = ReadQuiltPixels(quilt.QuiltTexture);
                            evidence.AppendLine("vfxOnAtPixelCapture=alive:" + effect.aliveParticleCount + "; culled:" + effect.culled + "; paused:" + effect.pause + "; rendererEnabled:" + vfxRenderer.enabled);
                            vfxRenderer.enabled = false;
                            quilt.RenderQuilt();
                            disabledPixels = ReadQuiltPixels(quilt.QuiltTexture);
                            string imageStem = "views-" + condition.views + "-" + (condition.motion ? "motion-on" : "motion-off");
                            WritePixelImage(PixelDirectory + "/" + imageStem + "-on.png", enabledPixels);
                            WritePixelImage(PixelDirectory + "/" + imageStem + "-off.png", disabledPixels);
                            int[] changedPixelsPerTile = CountChangedPixelsPerTile(enabledPixels, disabledPixels, condition.views, quilt.settings.columns, quilt.settings.rows);
                            int[] nonBlackPixelsPerTile = CountNonBlackPixelsPerTile(disabledPixels, condition.views, quilt.settings.columns, quilt.settings.rows);
                            evidence.Append("enabledToBlackOffChangedPixelsPerTile=");
                            evidence.AppendLine(string.Join(",", changedPixelsPerTile));
                            evidence.Append("offFrameNonBlackPixelsPerTile=");
                            evidence.AppendLine(string.Join(",", nonBlackPixelsPerTile));
                            evidence.AppendLine("rendererOffBlackCapture=rendererEnabled:" + vfxRenderer.enabled + "; alive:" + effect.aliveParticleCount + "; culled:" + effect.culled + "; paused:" + effect.pause);
                            if (changedPixelsPerTile.Any(count => count <= 20)) failure = "renderer-on-vs-black-off-difference-did-not-exceed-20-in-every-view-tile";
                            if (nonBlackPixelsPerTile.Any(count => count != 0)) failure = "renderer-off-output-was-not-black-in-every-view-tile";
                            Object.DestroyImmediate(enabledPixels);
                            enabledPixels = null;
                            Object.DestroyImmediate(disabledPixels);
                            disabledPixels = null;
                            vfxRenderer.enabled = true;
                            yield return null;
                            quilt.RenderQuilt();
                            if (!safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet))
                                failure = "safety-limit-after-pixel-gate-reactivation";
                            if (failure.Length == 0 && (effect.aliveParticleCount <= 0 || effect.pause || effect.culled || quilt.LastRenderedViewCount != condition.views))
                                failure = "vfx-did-not-remain-active-after-pixel-gate";
                        }
                    }
                    if (failure.Length == 0)
                    {
                        if (!safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet) && failure.Length == 0)
                            failure = "safety-limit-during-pixel-gate";
                    }
                }
                else if (failure.Length == 0 && (quilt.LastRenderedViewCount != condition.views || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated()))
                {
                    failure = "vfx-off-control-did-not-render-requested-views";
                }

                if (failure.Length == 0)
                {
                    measurementRawBaseline = Profiler.GetAllocatedMemoryForGraphicsDriver();
                    long measurementWorkingSetBaseline;
                    if (!ProcessMemoryReader.TryRead(out measurementPrivateBaseline, out measurementWorkingSetBaseline))
                        failure = "Win32-GetProcessMemoryInfo-measurement-baseline-unavailable";
                    evidence.AppendLine("measureBaselineWorkingSetBytes=" + measurementWorkingSetBaseline);
                    evidence.AppendLine("aliveGateFrame=" + aliveGateFrame + "; aliveAtGate=" + (effect != null ? effect.aliveParticleCount : 0) + "; effectCulled=" + (effect != null && effect.culled) + "; effectPaused=" + (effect != null && effect.pause));
                    evidence.AppendLine("measureBaselineRawGraphicsBytes=" + measurementRawBaseline + "; measureBaselinePrivateBytes=" + measurementPrivateBaseline + "; warmupHistoryCameraCount=" + guard.HistoryCameraCount);
                    int observationFrame = 0;
                    foreach (int sampleFrame in failure.Length == 0 ? new[] { 1, 2, 4, 8, 16, 32 } : Array.Empty<int>())
                    {
                        while (observationFrame < sampleFrame)
                        {
                            observationFrame++;
                            yield return null;
                            quilt.RenderQuilt();
                            if (!safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet))
                            {
                                failure = "safety-limit-during-32-frame-observation";
                                break;
                            }
                            if (Time.realtimeSinceStartupAsDouble - conditionStart > 60.0)
                            {
                                failure = "condition-exceeded-60-seconds-during-32-frame-observation";
                                break;
                            }
                        }
                            if (failure.Length != 0) break;

                        long rawFromMeasurementBaseline = lastRaw > 0 && measurementRawBaseline > 0 ? Math.Max(0, lastRaw - measurementRawBaseline) : 0;
                        long privateFromMeasurementBaseline = Math.Max(0, lastPrivate - measurementPrivateBaseline);
                        evidence.AppendLine("sampleFrame=" + sampleFrame + "; rawGraphicsBytes=" + lastRaw + "; rawGraphicsDeltaSinceMeasurementBaselineBytes=" + rawFromMeasurementBaseline +
                            "; historyGraphicsPeakGrowthBytes=" + guard.HistoryGraphicsMemoryGrowthBytes + "; historyTexturePeakEstimateBytes=" + guard.HistoryTextureMemoryEstimateBytes +
                            "; privateBytes=" + lastPrivate + "; privateDeltaSinceMeasurementBaselineBytes=" + privateFromMeasurementBaseline + "; workingSetBytes=" + lastWorkingSet +
                            "; historyCameraCount=" + guard.HistoryCameraCount + "; alive=" + (effect != null ? effect.aliveParticleCount : 0) + "; effectiveAA=" + cameraData.antialiasing + "; actualHistoryAA=None" +
                            "; tile=128x96; views=" + condition.views + "; motionVector=" + (condition.vfx ? condition.motion.ToString() : "not-applicable") + "; layout=" + quilt.settings.columns + "x" + quilt.settings.rows);
                        if (quilt.LastRenderedViewCount != condition.views)
                        {
                            failure = "rendered-view-count-changed-during-observation";
                            break;
                        }
                        if (!HistoryAaIsNone(guard, condition.views))
                        {
                            failure = "history-camera-aa-changed-during-observation";
                            break;
                        }
                    }
                }
                evidence.AppendLine("conditionFailure=" + (failure.Length == 0 ? "none" : failure) + "; processMemoryReaderAvailable=" + safety.processMemoryAvailable + "; rawGraphicsMemoryAvailable=" + safety.rawGraphicsMemoryAvailable + "; safetyPeakRawDeltaBytes=" + safety.peakRawDelta + "; safetyPeakPrivateDeltaBytes=" + safety.peakPrivateDelta +
                    "; guardBudgetMB=" + guard.historyMemoryBudgetMB + "; osGpuDedicatedShared=not-captured-in-test-process");
            }
            finally
            {
                if (enabledPixels != null) Object.DestroyImmediate(enabledPixels);
                if (disabledPixels != null) Object.DestroyImmediate(disabledPixels);
                ownedQuiltTexture = quilt != null ? quilt.QuiltTexture : null;
                ownedDriverCamera = driver != null ? driver.DriverCamera : null;
                ownedDriverTexture = ownedDriverCamera != null ? ownedDriverCamera.targetTexture : null;
                ownedDriverObject = ownedDriverCamera != null ? ownedDriverCamera.gameObject : null;
                ownedEffectObject = effectObject;
                bool allOwnedReferencesCaptured = ownedQuiltTexture != null && ownedDriverCamera != null && ownedDriverTexture != null && ownedDriverObject != null;
                if (!allOwnedReferencesCaptured && failure.Length == 0) failure = "owned-quilt-or-driver-resources-were-not-captured-for-cleanup";
                ownedHistoryCameras = guard != null
                    ? Enumerable.Range(0, condition.views).Select(view => guard.GetHistoryCamera(view)).Where(camera => camera != null).ToArray()
                    : Array.Empty<Camera>();
                if (guard != null) guard.ReleaseQuiltRenderResources();
                int historyCamerasAfterRelease = guard != null ? guard.HistoryCameraCount : -1;
                cleanupHistoryCount = historyCamerasAfterRelease;
                bool historyReleased = historyCamerasAfterRelease == 0;
                if (!historyReleased && failure.Length == 0) failure = "history-cameras-remained-after-release";
                if (host != null) Object.Destroy(host);
                evidence.AppendLine("cleanupHistoryCameraCountAfterRelease=" + historyCamerasAfterRelease + "; expectedOwnedObjectsCaptured=" + allOwnedReferencesCaptured);
            }

            long cleanupTwoRaw = 0;
            long cleanupTwoPrivate = 0;
            long cleanupFourRaw = 0;
            long cleanupFourPrivate = 0;
            for (int cleanupFrame = 1; cleanupFrame <= 8; ++cleanupFrame)
            {
                yield return null;
                if (cleanupFrame == 2 || cleanupFrame == 4 || cleanupFrame == 8)
                {
                    bool withinSafetyLimit = safety.Sample(out lastRaw, out lastRawDelta, out lastPrivate, out lastPrivateDelta, out lastWorkingSet);
                    bool historyObjectsDestroyed = ownedHistoryCameras.All(camera => camera == null);
                    bool ownedObjectsDestroyed = host == null && ownedDriverObject == null && ownedDriverCamera == null && ownedDriverTexture == null && ownedQuiltTexture == null && ownedEffectObject == null && historyObjectsDestroyed;
                    if (cleanupFrame == 2)
                    {
                        cleanupTwoRaw = lastRaw;
                        cleanupTwoPrivate = lastPrivate;
                    }
                    if (cleanupFrame == 4)
                    {
                        cleanupFourRaw = lastRaw;
                        cleanupFourPrivate = lastPrivate;
                    }
                    evidence.AppendLine("cleanupFrame=" + cleanupFrame + "; rawGraphicsBytes=" + lastRaw + "; privateBytes=" + lastPrivate + "; workingSetBytes=" + lastWorkingSet +
                        "; safetyPeakRawDeltaBytes=" + safety.peakRawDelta + "; safetyPeakPrivateDeltaBytes=" + safety.peakPrivateDelta + "; historyCameraCountAfterRelease=" + cleanupHistoryCount + "; historyObjectsDestroyed=" + historyObjectsDestroyed + "; ownedObjectsDestroyed=" + ownedObjectsDestroyed + "; withinSafetyLimit=" + withinSafetyLimit);
                    if (!withinSafetyLimit && failure.Length == 0) failure = "safety-limit-during-cleanup-sampling";
                    if (!ownedObjectsDestroyed && failure.Length == 0) failure = "owned-quilt-or-driver-object-survived-cleanup";
                    if (cleanupFrame == 8 && failure.Length == 0)
                    {
                        bool rawGrowthContinues = lastRaw > cleanupFourRaw && cleanupFourRaw > cleanupTwoRaw && lastRaw - cleanupTwoRaw > CleanupGrowthStopBytes;
                        bool privateGrowthContinues = lastPrivate > cleanupFourPrivate && cleanupFourPrivate > cleanupTwoPrivate && lastPrivate - cleanupTwoPrivate > CleanupGrowthStopBytes;
                        if (rawGrowthContinues || privateGrowthContinues)
                            failure = "cleanup-memory-growth-continued-across-2-4-8-frames-over-8MiB-stop-threshold";
                    }
                    if (cleanupFrame == 8 && guard != null && guard.HistoryCameraCount != 0 && failure.Length == 0)
                        failure = "history-cameras-remained-after-cleanup";
                }
            }
            evidence.AppendLine("conditionEndSeconds=" + (Time.realtimeSinceStartupAsDouble - conditionStart).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            WriteEvidence();
            if (failure.Length != 0) Assert.Fail(condition.name + " views=" + condition.views + " failed: " + failure);
        }

        VisualEffectAsset CreateGraphAsset(string assetPath, bool generateMotionVectors)
        {
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(SourceAssetPath), "Source Simple Burst template is unavailable");
            string directory = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            Directory.CreateDirectory(directory);
            Assert.IsNull(AssetDatabase.LoadAssetAtPath<Object>(assetPath), "Refusing to overwrite a prior measurement graph");
            Assert.IsTrue(AssetDatabase.CopyAsset(SourceAssetPath, assetPath), "Could not copy the Simple Burst template");
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            ConfigureGraph(assetPath, generateMotionVectors);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            VerifyGraphSettings(assetPath, generateMotionVectors);
            VisualEffectAsset asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            Assert.IsNotNull(asset, "Configured graph did not import as a VisualEffectAsset");
            return asset;
        }

        void ConfigureGraph(string assetPath, bool generateMotionVectors)
        {
            object resource = LoadVfxResource(assetPath);
            Object[] dependencies = LoadGraphDependencies(resource);
            Object[] data = dependencies.Where(item => item.GetType().FullName == "UnityEditor.VFX.VFXDataParticle").ToArray();
            Object[] outputs = dependencies.Where(IsParticleOutput).ToArray();
            Assert.AreEqual(1, data.Length, "Expected exactly one particle data model");
            Assert.AreEqual(1, outputs.Length, "Expected exactly one particle output model");
            SetGraphSetting(data[0], "capacity", 512u);
            SetGraphSetting(outputs[0], "generateMotionVector", generateMotionVectors);
            EditorUtility.SetDirty((Object)InvokeVfxResourceExtension("GetGraph", resource));
            EditorUtility.SetDirty(data[0]);
            EditorUtility.SetDirty(outputs[0]);
            InvokeVfxResourceExtension("WriteAssetWithSubAssets", resource);
        }

        void VerifyGraphSettings(string assetPath, bool generateMotionVectors)
        {
            Object[] dependencies = LoadGraphDependencies(LoadVfxResource(assetPath));
            Object[] data = dependencies.Where(item => item.GetType().FullName == "UnityEditor.VFX.VFXDataParticle").ToArray();
            Object[] outputs = dependencies.Where(IsParticleOutput).ToArray();
            Assert.AreEqual(1, data.Length, "Particle data model count changed after import");
            Assert.AreEqual(1, outputs.Length, "Particle output model count changed after import");
            AssertGraphSetting(data[0], "capacity", 512u);
            AssertGraphSetting(outputs[0], "generateMotionVector", generateMotionVectors);
        }

        static bool IsParticleOutput(Object item)
        {
            if (item == null) return false;
            for (Type type = item.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "UnityEditor.VFX.VFXAbstractParticleOutput") return true;
            return false;
        }

        static bool HistoryAaIsNone(UrpQuiltCamera guard, int views)
        {
            if (guard == null) return false;
            for (int logicalView = 0; logicalView < views; ++logicalView)
            {
                Camera historyCamera = guard.GetHistoryCamera(logicalView);
                UniversalAdditionalCameraData historyData = historyCamera != null ? historyCamera.GetComponent<UniversalAdditionalCameraData>() : null;
                if (historyData == null || historyData.antialiasing != AntialiasingMode.None) return false;
            }
            return true;
        }

        static Texture2D ReadQuiltPixels(RenderTexture quilt)
        {
            Assert.IsNotNull(quilt, "Quilt output texture is unavailable for the pixel gate");
            Assert.IsTrue(quilt.IsCreated(), "Quilt output texture is not created for the pixel gate");
            RenderTexture previous = RenderTexture.active;
            var pixels = new Texture2D(quilt.width, quilt.height, TextureFormat.RGBA32, false)
            {
                name = "UrpVfxMemoryScale.PixelGate",
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                RenderTexture.active = quilt;
                pixels.ReadPixels(new Rect(0, 0, quilt.width, quilt.height), 0, 0, false);
                pixels.Apply(false, false);
                return pixels;
            }
            catch
            {
                Object.DestroyImmediate(pixels);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        static int[] CountChangedPixelsPerTile(Texture2D before, Texture2D after, int views, int columns, int rows)
        {
            Assert.IsNotNull(before, "Baseline quilt pixels are unavailable");
            Assert.IsNotNull(after, "Renderer-off quilt pixels are unavailable");
            Assert.AreEqual(before.width, after.width, "Quilt width changed between pixel captures");
            Assert.AreEqual(before.height, after.height, "Quilt height changed between pixel captures");
            Assert.AreEqual(columns * rows, views, "Fixture layout must map each tile to exactly one logical view");
            Color[] beforePixels = before.GetPixels();
            Color[] afterPixels = after.GetPixels();
            int tileWidth = before.width / columns;
            int tileHeight = before.height / rows;
            var changedPerTile = new int[views];
            for (int tile = 0; tile < views; ++tile)
            {
                int tileX = tile % columns;
                int tileY = tile / columns;
                int changed = 0;
                for (int y = 0; y < tileHeight; ++y)
                {
                    int rowStart = (tileY * tileHeight + y) * before.width + tileX * tileWidth;
                    for (int x = 0; x < tileWidth; ++x)
                    {
                        Color a = beforePixels[rowStart + x];
                        Color b = afterPixels[rowStart + x];
                        if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 0.02f) changed++;
                    }
                }
                changedPerTile[tile] = changed;
            }
            return changedPerTile;
        }

        static int[] CountNonBlackPixelsPerTile(Texture2D texture, int views, int columns, int rows)
        {
            Assert.IsNotNull(texture, "Renderer-off pixels are unavailable");
            Assert.AreEqual(columns * rows, views, "Fixture layout must map each tile to exactly one logical view");
            Color32[] pixels = texture.GetPixels32();
            int tileWidth = texture.width / columns;
            int tileHeight = texture.height / rows;
            var nonBlackPerTile = new int[views];
            for (int tile = 0; tile < views; ++tile)
            {
                int tileX = tile % columns;
                int tileY = tile / columns;
                int nonBlack = 0;
                for (int y = 0; y < tileHeight; ++y)
                {
                    int rowStart = (tileY * tileHeight + y) * texture.width + tileX * tileWidth;
                    for (int x = 0; x < tileWidth; ++x)
                    {
                        Color32 color = pixels[rowStart + x];
                        if (Math.Max(color.r, Math.Max(color.g, color.b)) > 3) nonBlack++;
                    }
                }
                nonBlackPerTile[tile] = nonBlack;
            }
            return nonBlackPerTile;
        }

        static void WritePixelImage(string relativePath, Texture2D image)
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            Assert.IsFalse(File.Exists(path), "Refusing to overwrite a prior measurement image");
            File.WriteAllBytes(path, image.EncodeToPNG());
        }

        static void SetGraphSetting(Object target, string name, object value)
        {
            MethodInfo setter = target.GetType().GetMethod("SetSettingValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string), typeof(object) }, null);
            Assert.IsNotNull(setter, "Could not locate the public VFX setting setter");
            setter.Invoke(target, new[] { (object)name, value });
        }

        static void AssertGraphSetting(Object target, string name, object expected)
        {
            MethodInfo getter = target.GetType().GetMethod("GetSettingValue", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(getter, "Could not locate the public VFX setting getter");
            Assert.AreEqual(expected, getter.Invoke(target, new object[] { name }), "Cloned VFX setting did not survive import");
        }

        static object LoadVfxResource(string assetPath)
        {
            const string resourceTypeName = "UnityEditor.VFX.VisualEffectResource";
            Type resourceType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                resourceType = assembly.GetType(resourceTypeName, false);
                if (resourceType != null) break;
            }
            Assert.IsNotNull(resourceType, "Could not resolve the VFX Editor resource type");
            MethodInfo getResource = resourceType.GetMethod("GetResourceAtPath", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            Assert.IsNotNull(getResource, "Could not resolve VisualEffectResource.GetResourceAtPath");
            object resource = getResource.Invoke(null, new object[] { assetPath });
            Assert.IsNotNull(resource, "VisualEffectResource was not loaded");
            return resource;
        }

        static Object[] LoadGraphDependencies(object resource)
        {
            Object graph = (Object)InvokeVfxResourceExtension("GetGraph", resource);
            Assert.IsNotNull(graph, "VisualEffectResource graph was unavailable");
            MethodInfo collect = graph.GetType().GetMethod("CollectDependencies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(HashSet<ScriptableObject>), typeof(bool) }, null);
            Assert.IsNotNull(collect, "Could not resolve VFXGraph.CollectDependencies");
            var dependencies = new HashSet<ScriptableObject>();
            collect.Invoke(graph, new object[] { dependencies, false });
            return dependencies.Where(item => item != null).Cast<Object>().ToArray();
        }

        static object InvokeVfxResourceExtension(string methodName, object resource)
        {
            Type extensionType = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                extensionType = assembly.GetType("UnityEditor.VFX.VisualEffectResourceExtensions", false);
                if (extensionType != null) break;
            }
            Assert.IsNotNull(extensionType, "Could not resolve VFX resource extensions");
            MethodInfo method = extensionType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.Name == methodName && candidate.GetParameters().Length == 1 && candidate.GetParameters()[0].ParameterType.IsInstanceOfType(resource));
            Assert.IsNotNull(method, "Could not resolve VFX resource extension method " + methodName);
            return method.Invoke(null, new[] { resource });
        }

        static IEnumerator WaitForCompilation(double timeoutSeconds)
        {
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while ((EditorApplication.isCompiling || ShaderUtil.anythingCompiling) && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            Assert.IsFalse(EditorApplication.isCompiling || ShaderUtil.anythingCompiling, "Editor or shader compilation exceeded 60 seconds");
        }

        void PrepareUrpPipeline()
        {
            oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            oldQualityPipeline = QualitySettings.renderPipeline;
            oldAntiAliasing = QualitySettings.antiAliasing;
            testRenderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            testRenderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            Assert.IsNotNull(testRenderer.postProcessData, "URP PostProcessData asset is unavailable");
            testPipeline = UniversalRenderPipelineAsset.Create(testRenderer);
            testPipeline.msaaSampleCount = 1;
            GraphicsSettings.defaultRenderPipeline = testPipeline;
            QualitySettings.renderPipeline = testPipeline;
            QualitySettings.antiAliasing = 0;
            savedCameras = Object.FindObjectsByType<Camera>();
            savedCameraEnabled = new bool[savedCameras.Length];
            for (int i = 0; i < savedCameras.Length; ++i)
            {
                savedCameraEnabled[i] = savedCameras[i].enabled;
                savedCameras[i].enabled = false;
            }
        }

        GameObject CreateQuiltHost(MeasurementCondition condition, out Camera source, out QuiltRenderer quilt, out UrpQuiltCamera guard, out UniversalAdditionalCameraData cameraData, out UrpVfxCameraDriver driver)
        {
            GameObject host = new GameObject("UrpVfxMemoryScale." + condition.name + "." + condition.views);
            host.SetActive(false);
            source = host.AddComponent<Camera>();
            source.enabled = false;
            source.allowMSAA = false;
            source.clearFlags = CameraClearFlags.SolidColor;
            source.backgroundColor = Color.black;
            source.cullingMask = 1 << 31;
            source.fieldOfView = 50f;
            source.nearClipPlane = 0.3f;
            source.farClipPlane = 100f;
            cameraData = host.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderType = CameraRenderType.Base;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.renderShadows = false;
            cameraData.volumeLayerMask = 0;
            guard = host.AddComponent<UrpQuiltCamera>();
            guard.renderingMode = UrpQuiltRenderingMode.IsolatedViewHistories;
            guard.historyMemoryBudgetMB = 4096;
            guard.maximumHistoryCameras = 8;
            quilt = host.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = source;
            quilt.GuardBehaviour = guard;
            quilt.autoRender = false;
            quilt.settings.columns = condition.views == 1 ? 1 : condition.views == 2 ? 2 : 4;
            quilt.settings.rows = condition.views == 1 ? 1 : condition.views == 2 ? 1 : 2;
            quilt.settings.quiltWidth = 128 * quilt.settings.columns;
            quilt.settings.quiltHeight = 96 * quilt.settings.rows;
            quilt.settings.views = condition.views;
            quilt.settings.viewAspect = 4f / 3f;
            quilt.settings.cameraDistance = 3f;
            quilt.settings.fieldOfView = 50f;
            quilt.settings.viewCone = condition.views == 1 ? 0f : 40f;
            driver = host.AddComponent<UrpVfxCameraDriver>();
            driver.vfxLayers = 1 << 31;
            driver.driverFieldOfView = 50f;
            driver.runWhenManualRendering = true;
            driver.enableDriver = true;
            return host;
        }

        void RestorePipeline()
        {
            if (savedCameras != null)
                for (int i = 0; i < savedCameras.Length; ++i) if (savedCameras[i] != null) savedCameras[i].enabled = savedCameraEnabled[i];
            GraphicsSettings.defaultRenderPipeline = oldDefaultPipeline;
            QualitySettings.renderPipeline = oldQualityPipeline;
            QualitySettings.antiAliasing = oldAntiAliasing;
            if (testPipeline != null) Object.DestroyImmediate(testPipeline);
            if (testRenderer != null) Object.DestroyImmediate(testRenderer);
        }

        void WriteEvidence()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", EvidencePath));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, evidence.ToString(), Encoding.ASCII);
        }
    }
}
#endif
