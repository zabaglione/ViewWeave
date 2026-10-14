#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    public sealed class SelectedScenePilotTests
    {
        const string ScenePath = "Assets/SceneComparison/URP_SelectedPilot.unity";
        const string SelectedGuid = "e075786f47306614894aed1c188c9ce3";
        const string EvidencePath = "TestResults/urp-selected-collision-simple-one-view.txt";
        const string PixelPath = "TestResults/urp-selected-collision-simple-one-view";
        const long SafetyBytes = 512L * 1024L * 1024L;
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
            [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
            [DllImport("psapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx counters, uint size);
            internal static bool TryRead(out long privateBytes, out long workingSetBytes)
            {
                privateBytes = 0; workingSetBytes = 0;
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

        sealed class SafetySample
        {
            internal long rawStart;
            internal long privateStart;
            internal long rawCurrent;
            internal long privateCurrent;
            internal long workingSetCurrent;
            internal long rawPeakDelta;
            internal long privatePeakDelta;
            internal string failure;
            internal bool Read()
            {
                rawCurrent = Profiler.GetAllocatedMemoryForGraphicsDriver();
                if (rawStart <= 0 || rawCurrent <= 0) { failure = "raw-graphics-counter-unavailable"; return false; }
                if (!ProcessMemoryReader.TryRead(out privateCurrent, out workingSetCurrent)) { failure = "process-memory-reader-unavailable"; return false; }
                if (privateStart <= 0 || privateCurrent <= 0 || workingSetCurrent <= 0) { failure = "process-memory-counter-invalid"; return false; }
                rawPeakDelta = Math.Max(rawPeakDelta, Math.Max(0, rawCurrent - rawStart));
                privatePeakDelta = Math.Max(privatePeakDelta, Math.Max(0, privateCurrent - privateStart));
                if (rawPeakDelta >= SafetyBytes || privatePeakDelta >= SafetyBytes) { failure = "512MiB-observed-growth-stop"; return false; }
                return true;
            }
        }

        readonly StringBuilder evidence = new StringBuilder(4096);
        QuiltRenderer quilt;
        ViewWeaveRig rig;
        UrpQuiltCamera guard;
        VisualEffect effect;
        VFXRenderer effectRenderer;
        GameObject effectObject;
        Camera referenceCamera;
        SafetySample safety;
        string phase = "start";
        long startRevision;
        int completedRevisions;
        int startFrame;
        int endFrame;
        int onAlive;
        int changedPixels;
        bool offPixelsBlack;
        bool offControlCaptured;
        int offNonBlackPixels;
        long historyGrowthBeforeCleanup;
        long historyTextureEstimateBeforeCleanup;
        int historyCountBeforeCleanup;
        Texture2D onImage;
        Texture2D offImage;
        Texture2D offRepeatImage;
        RenderTexture referenceOutput;
        RenderTexture originalReferenceTarget;
        int originalQualityAntiAliasing;
        bool qualityAntiAliasingCaptured;
        readonly List<string> measurementSamples = new List<string>(6);
        readonly List<string> offNoiseSamples = new List<string>(2);
        int offRepeatChangedPixels;
        string cameraPoseBefore;
        string cameraPoseAdjusted;
        string cameraForwardBefore;
        string cameraForwardAdjusted;
        string effectBoundsAtCapture;
        bool rendererEnabledBeforeActivation;
        bool rendererSerializedEnabledBeforeActivation;
        bool rendererEnabledAfterGameObjectActivation;
        bool rendererSerializedEnabledAfterGameObjectActivation;
        bool rendererSourceEnabled;
        string rendererSourcePath;
        string rendererSourceGuid;
        long rendererSourceFileId;
        bool selectedRendererForcedOnForPilot;
        bool selectedRendererEnabledAtOnCapture;
        bool selectedRendererEnabledAtOffCapture;

        [UnityTest]
        public IEnumerator CollisionSimpleOriginalSceneOneViewAAOff()
        {
            Exception failure = null;
            var iterators = new Stack<IEnumerator>();
            iterators.Push(ExecuteCase());
            while (iterators.Count != 0)
            {
                bool moved;
                object current = null;
                IEnumerator top = iterators.Peek();
                try { moved = top.MoveNext(); if (moved) current = top.Current; }
                catch (Exception exception) { failure = exception; break; }
                if (!moved) { iterators.Pop(); continue; }
                if (current is IEnumerator nested) { iterators.Push(nested); continue; }
                yield return current;
            }

            CleanupSynchronous();
            long cleanupRawBase = Profiler.GetAllocatedMemoryForGraphicsDriver();
            long cleanupPrivateBase = 0;
            long cleanupWorkingBase = 0;
            bool cleanupBaselineValid = cleanupRawBase > 0 && ProcessMemoryReader.TryRead(out cleanupPrivateBase, out cleanupWorkingBase) && cleanupPrivateBase > 0 && cleanupWorkingBase > 0;
            if (!cleanupBaselineValid) { cleanupPrivateBase = 0; cleanupWorkingBase = 0; }
            var cleanup = new List<long[]>();
            bool cleanupCountersValid = cleanupBaselineValid;
            bool cleanupSafetyValid = true;
            for (int frame = 1; frame <= 8; ++frame)
            {
                yield return null;
                if (safety != null && !safety.Read()) cleanupSafetyValid = false;
                if (frame == 2 || frame == 4 || frame == 8)
                {
                    long raw = Profiler.GetAllocatedMemoryForGraphicsDriver();
                    bool validProcessRead = ProcessMemoryReader.TryRead(out long privateBytes, out long workingSet);
                    if (raw <= 0 || !validProcessRead) cleanupCountersValid = false;
                    cleanup.Add(new[] { frame, raw, privateBytes, workingSet, Math.Max(0, raw - cleanupRawBase), Math.Max(0, privateBytes - cleanupPrivateBase) });
                }
            }

            int historyCount = guard == null ? -1 : guard.HistoryCameraCount;
            bool historyReleased = guard != null && historyCount == 0 && guard.GetHistoryCamera(0) == null;
            bool quiltReleased = quilt == null || quilt.QuiltTexture == null || !quilt.QuiltTexture.IsCreated();
            long cleanupRawDelta = cleanup.Count == 0 ? 0 : cleanup[cleanup.Count - 1][4];
            long cleanupPrivateDelta = cleanup.Count == 0 ? 0 : cleanup[cleanup.Count - 1][5];
            bool cleanupContinuouslyGrowing = cleanup.Count == 3 && cleanup[0][4] < cleanup[1][4] && cleanup[1][4] < cleanup[2][4] && cleanupRawDelta > CleanupGrowthStopBytes;
            bool cleanupPrivateContinuouslyGrowing = cleanup.Count == 3 && cleanup[0][5] < cleanup[1][5] && cleanup[1][5] < cleanup[2][5] && cleanupPrivateDelta > CleanupGrowthStopBytes;

            Append("failureType", failure == null ? "" : failure.GetType().Name);
            Append("failurePhase", phase);
            Append("completedRevisions", completedRevisions);
            Append("startFrame", startFrame);
            Append("endFrame", endFrame);
            Append("rawGraphicsBaselineBytes", safety == null ? 0 : safety.rawStart);
            Append("privateBytesBaseline", safety == null ? 0 : safety.privateStart);
            Append("onAliveParticleCount", onAlive);
            Append("changedPixelsAboveThreshold", changedPixels);
            Append("offRepeatChangedPixelsAboveThreshold", offRepeatChangedPixels);
            Append("offRepeatNoiseSamples", string.Join(";", offNoiseSamples.ToArray()));
            Append("measurementSamplesRevisionFrameRawPrivateWorkingSet", string.Join(";", measurementSamples.ToArray()));
            Append("offControlCaptured", offControlCaptured);
            Append("offNonBlackPixels", offNonBlackPixels);
            Append("safetyPeakRawDeltaBytes", safety == null ? 0 : safety.rawPeakDelta);
            Append("safetyPeakPrivateDeltaBytes", safety == null ? 0 : safety.privatePeakDelta);
            Append("rawGraphicsBytesCurrent", safety == null ? 0 : safety.rawCurrent);
            Append("privateBytesCurrent", safety == null ? 0 : safety.privateCurrent);
            Append("workingSetBytesCurrent", safety == null ? 0 : safety.workingSetCurrent);
            Append("safetyFailure", safety == null ? "" : safety.failure ?? "");
            Append("historyCameraCountAfterRelease", historyCount);
            Append("historyCameraCountBeforeCleanup", historyCountBeforeCleanup);
            Append("historyGraphicsGrowthBytesBeforeCleanup", historyGrowthBeforeCleanup);
            Append("historyTextureEstimateBytesBeforeCleanup", historyTextureEstimateBeforeCleanup);
            Append("historyResourcesReleased", historyReleased);
            Append("quiltTextureReleased", quiltReleased);
            Append("cleanupCountersValid", cleanupCountersValid);
            Append("cleanupSafetyWithin512MiB", cleanupSafetyValid);
            Append("cleanupRawPrivateSamplesFrame2_4_8", Flatten(cleanup));
            Append("cleanupMonotonicGrowthOver8MiB", cleanupContinuouslyGrowing || cleanupPrivateContinuouslyGrowing);
            Append("cameraPoseBefore", cameraPoseBefore ?? "");
            Append("cameraPoseAdjusted", cameraPoseAdjusted ?? "");
            Append("cameraForwardBefore", cameraForwardBefore ?? "");
            Append("cameraForwardAdjusted", cameraForwardAdjusted ?? "");
            Append("rendererEnabledBeforeActivation", rendererEnabledBeforeActivation);
            Append("rendererSerializedEnabledBeforeActivation", rendererSerializedEnabledBeforeActivation);
            Append("rendererEnabledAfterGameObjectActivation", rendererEnabledAfterGameObjectActivation);
            Append("rendererSerializedEnabledAfterGameObjectActivation", rendererSerializedEnabledAfterGameObjectActivation);
            Append("rendererSourceEnabled", rendererSourceEnabled);
            Append("rendererSourcePath", rendererSourcePath ?? "");
            Append("rendererSourceGuid", rendererSourceGuid ?? "");
            Append("rendererSourceFileId", rendererSourceFileId);
            Append("selectedRendererForcedOnForPilot", selectedRendererForcedOnForPilot);
            Append("selectedRendererEnabledAtOnCapture", selectedRendererEnabledAtOnCapture);
            Append("selectedRendererEnabledAtOffCapture", selectedRendererEnabledAtOffCapture);
            Append("selectedEffectBoundsAtCapture", effectBoundsAtCapture ?? "");
            Append("graphicsDevice", SystemInfo.graphicsDeviceType);
            Append("testScene", ScenePath);
            Append("compiledNativeBufferDescriptor", "not-captured");
            WriteEvidence();

            Assert.IsNull(failure, failure == null ? "" : "Pilot phase failed: " + failure.GetType().Name);
            Assert.AreEqual(32, completedRevisions, "Expected 32 completed scheduler quilt revisions after activating the selected effect.");
            Assert.AreEqual(6, measurementSamples.Count, "Expected memory samples at completed revisions 1, 2, 4, 8, 16 and 32.");
            Assert.Greater(onAlive, 0, "Selected VFX produced no live particles.");
            Assert.IsTrue(offControlCaptured, "Renderer-off control image was not captured.");
            Assert.Greater(changedPixels, 20, "Selected VFX did not change enough pixels in the one-view quilt.");
            Assert.Greater(changedPixels, offRepeatChangedPixels + 20, "ON/OFF pixel difference did not exceed the repeated OFF control noise by 20 pixels.");
            Assert.IsTrue(historyReleased, "URP history camera/resources remained after cleanup.");
            Assert.IsTrue(quiltReleased, "Quilt-owned render texture remained after cleanup.");
            Assert.IsTrue(cleanupCountersValid, "Raw graphics or process-memory cleanup counters were unavailable.");
            Assert.IsTrue(cleanupSafetyValid, safety == null ? "No baseline safety monitor was established." : safety.failure ?? "512 MiB growth stop was exceeded during cleanup.");
            Assert.IsFalse(cleanupContinuouslyGrowing || cleanupPrivateContinuouslyGrowing, "Cleanup samples grew continuously beyond the pilot stop threshold.");
        }

        IEnumerator ExecuteCase()
        {
            phase = "load-sanitized-scene";
            Scene loadedScene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float loadDeadline = Time.realtimeSinceStartup + 60f;
            while (!loadedScene.isLoaded && Time.realtimeSinceStartup < loadDeadline) yield return null;
            Assert.IsTrue(loadedScene.IsValid() && loadedScene.isLoaded, "Prepared scene load timed out.");
            Assert.IsTrue(Application.isPlaying, "Pilot must run in Play Mode.");
            Assert.AreEqual(GraphicsDeviceType.Direct3D11, SystemInfo.graphicsDeviceType, "Expected the explicitly selected D3D11 graphics device.");

            var allEffects = Resources.FindObjectsOfTypeAll<VisualEffect>().Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded).ToArray();
            var matches = allEffects.Where(value =>
            {
                string path = AssetDatabase.GetAssetPath(value.visualEffectAsset);
                return !string.IsNullOrEmpty(path) && AssetDatabase.AssetPathToGUID(path).Equals(SelectedGuid, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            Assert.AreEqual(1, matches.Length, "Expected exactly one selected CollisionSimple effect.");
            effect = matches[0]; effectObject = effect.gameObject;
            Assert.IsFalse(effectObject.activeInHierarchy, "Prepared scene must begin with the selected effect inactive.");
            Assert.AreEqual("Collision Simple/TurntableWithBackdrop_VFX/VFXG Collision Simple", PathOf(effect.transform));
            Assert.AreEqual(0, allEffects.Count(value => value.gameObject.activeInHierarchy), "The saved scene must begin with every VisualEffect inactive.");
            effectRenderer = effectObject.GetComponent<VFXRenderer>();
            Assert.IsNotNull(effectRenderer, "The selected effect has no VFXRenderer.");
            rendererEnabledBeforeActivation = effectRenderer.enabled;
            rendererSerializedEnabledBeforeActivation = ReadSerializedEnabled(effectRenderer);
            CaptureRendererSourceState();

            rig = Resources.FindObjectsOfTypeAll<ViewWeaveRig>().Single(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded);
            quilt = rig.GetComponent<QuiltRenderer>();
            Assert.IsNotNull(quilt);
            Camera sourceCamera = quilt.sourceCamera;
            referenceCamera = quilt.referenceCamera;
            Assert.IsNotNull(sourceCamera); Assert.IsFalse(sourceCamera.enabled);
            Assert.IsNotNull(referenceCamera); Assert.IsTrue(referenceCamera.enabled);
            guard = sourceCamera.GetComponent<UrpQuiltCamera>();
            Assert.IsNotNull(guard, "URP guard component is missing.");
            Assert.AreEqual(UrpQuiltRenderingMode.IsolatedViewHistories, guard.renderingMode);
            Assert.AreEqual(4096, guard.historyMemoryBudgetMB, "The existing default history budget must remain unchanged.");
            var retention = new SerializedObject(quilt);
            var retentionOwner = retention.FindProperty("editorPreviewRetentionOwner");
            var retentionRequired = retention.FindProperty("editorPreviewRetentionRequired");
            Assert.IsTrue(retentionOwner == null || retentionOwner.objectReferenceValue == null, "Editor preview retention owner must be absent in this runtime pilot.");
            Assert.IsTrue(retentionRequired == null || !retentionRequired.boolValue, "Editor preview retention must be disabled in this runtime pilot.");
            foreach (var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded))
            {
                MonoScript script = MonoScript.FromMonoBehaviour(behaviour);
                string path = script == null ? "" : AssetDatabase.GetAssetPath(script).Replace('\\', '/');
                bool sampleScript = path.StartsWith("Assets/Samples/Visual Effect Graph/17.6.0/", StringComparison.Ordinal) || path.StartsWith("Assets/Samples/Scriptable Render Pipeline Core/17.6.0/", StringComparison.Ordinal);
                if (sampleScript && !(behaviour is ViewWeaveRig) && !(behaviour is QuiltRenderer)) Assert.IsFalse(behaviour.enabled, "A copied sample presenter/control script remained enabled: " + Path.GetFileName(path));
            }

            phase = "pilot-camera-and-render-setup";
            cameraPoseBefore = PoseString(referenceCamera.transform.position, referenceCamera.transform.rotation);
            Vector3 savedForward = referenceCamera.transform.forward;
            cameraForwardBefore = Vector3String(savedForward);
            Vector3 aimTarget = effect.transform.position + Vector3.up;
            referenceCamera.transform.position = aimTarget - savedForward.normalized * 3f;
            referenceCamera.transform.LookAt(aimTarget);
            cameraPoseAdjusted = PoseString(referenceCamera.transform.position, referenceCamera.transform.rotation);
            cameraForwardAdjusted = Vector3String(referenceCamera.transform.forward);
            referenceCamera.clearFlags = CameraClearFlags.SolidColor;
            referenceCamera.backgroundColor = Color.black;
            referenceCamera.allowMSAA = false;
            referenceCamera.fieldOfView = 50f;
            var cameraData = referenceCamera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsNotNull(cameraData, "URP Additional Camera Data is required.");
            cameraData.antialiasing = AntialiasingMode.None;
            cameraData.renderPostProcessing = false;
            originalReferenceTarget = referenceCamera.targetTexture;
            referenceOutput = new RenderTexture(128, 96, 24, RenderTextureFormat.ARGB32) { name = "ViewWeave.ScenePilot.ReferenceOutput" };
            Assert.IsTrue(referenceOutput.Create(), "Could not create the small reference-camera output target.");
            referenceCamera.targetTexture = referenceOutput;
            referenceCamera.enabled = true;
            sourceCamera.clearFlags = CameraClearFlags.SolidColor;
            sourceCamera.backgroundColor = Color.black;
            sourceCamera.allowMSAA = false;
            originalQualityAntiAliasing = QualitySettings.antiAliasing;
            qualityAntiAliasingCaptured = true;
            QualitySettings.antiAliasing = 0;
            rig.SetOutput(null);
            rig.SetGuard(guard);
            rig.ApplyProfile(new QuiltQualityProfile(128, 96, 1, 1, 1, 128f / 96f, 0f));
            quilt.settings.fieldOfView = 50f;
            quilt.settings.cameraDistance = 3f;
            quilt.settings.viewCone = 0f;
            quilt.viewRenderMode = QuiltViewRenderMode.Full;
            quilt.followReferenceFieldOfView = true;
            quilt.useRenderRequests = true;
            quilt.autoRender = false;
            Assert.IsTrue(rig.ValidateRig(out string validationError), validationError ?? "Rig validation failed.");
            rig.AutoRender = true;

            phase = "pipeline-warmup";
            long warmupRevision = quilt.QuiltRevision;
            yield return WaitForRevisions(warmupRevision + 8, 30, false);
            Assert.AreEqual(1, guard.HistoryCameraCount, "Expected one URP history camera after the one-view warmup.");
            var historyCamera = guard.GetHistoryCamera(0);
            Assert.IsNotNull(historyCamera, "Expected a concrete one-view history camera.");
            var historyData = historyCamera.GetComponent<UniversalAdditionalCameraData>();
            Assert.IsNotNull(historyData, "History camera has no URP Additional Camera Data.");
            Assert.AreEqual(AntialiasingMode.None, historyData.antialiasing, "Effective history-camera AA must be disabled.");
            safety = new SafetySample { rawStart = Profiler.GetAllocatedMemoryForGraphicsDriver() };
            Assert.Greater(safety.rawStart, 0, "Raw graphics memory counter unavailable.");
            Assert.IsTrue(ProcessMemoryReader.TryRead(out safety.privateStart, out long baselineWorkingSet) && safety.privateStart > 0 && baselineWorkingSet > 0, "Win32 private/working-set counters unavailable.");

            phase = "selected-effect-activation-and-alive";
            effectObject.SetActive(true);
            rendererEnabledAfterGameObjectActivation = effectRenderer.enabled;
            rendererSerializedEnabledAfterGameObjectActivation = ReadSerializedEnabled(effectRenderer);
            effect.enabled = true;
            effect.pause = false;
            effect.Reinit();
            effect.Play();
            effectRenderer.enabled = true;
            selectedRendererForcedOnForPilot = effectRenderer.enabled;
            Assert.IsTrue(selectedRendererForcedOnForPilot, "The selected VFXRenderer could not be enabled for the isolated renderability check.");
            float aliveDeadline = Time.realtimeSinceStartup + 5f;
            while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartup < aliveDeadline)
            {
                AssertSafety();
                AssertOnlySelectedActive();
                yield return null;
            }
            onAlive = effect.aliveParticleCount;
            Assert.Greater(onAlive, 0, "Selected CollisionSimple did not produce live particles within the bounded wait.");
            AssertOnlySelectedActive();

            phase = "selected-effect-32-completed-quilt-revisions";
            startRevision = quilt.QuiltRevision;
            startFrame = Time.frameCount;
            yield return WaitForRevisions(startRevision + 32, 60, true);
            endFrame = Time.frameCount;
            completedRevisions = checked((int)(quilt.QuiltRevision - startRevision));
            Assert.AreEqual(32, completedRevisions, "The required 32 scheduler completions were not observed.");
            Assert.AreEqual(1, quilt.LastRenderedViewCount);
            AssertSafety();
            onImage = ReadQuilt(quilt.QuiltTexture);
            selectedRendererEnabledAtOnCapture = effectRenderer.enabled;
            effectBoundsAtCapture = BoundsString(effectRenderer.bounds);
            SavePng(onImage, PixelPath + "-on.png");

            phase = "renderer-off-black-control";
            long offStartRevision = quilt.QuiltRevision;
            effectRenderer.enabled = false;
            yield return WaitForRevisions(offStartRevision + 1, 10, true);
            offImage = ReadQuilt(quilt.QuiltTexture);
            selectedRendererEnabledAtOffCapture = effectRenderer.enabled;
            SavePng(offImage, PixelPath + "-off.png");
            long offRepeatRevision = quilt.QuiltRevision;
            yield return WaitForRevisions(offRepeatRevision + 1, 10, true);
            offRepeatImage = ReadQuilt(quilt.QuiltTexture);
            SavePng(offRepeatImage, PixelPath + "-off-repeat.png");
            offRepeatChangedPixels = CountChangedPixels(offImage, offRepeatImage, 20, out _);
            offNoiseSamples.Add("offToOffChangedPixels=" + offRepeatChangedPixels.ToString(System.Globalization.CultureInfo.InvariantCulture));
            changedPixels = CountChangedPixels(onImage, offImage, 20, out offPixelsBlack);
            offControlCaptured = true;
            offNonBlackPixels = offImage.GetPixels32().Count(pixel => pixel.r > 2 || pixel.g > 2 || pixel.b > 2);
            historyCountBeforeCleanup = guard.HistoryCameraCount;
            historyGrowthBeforeCleanup = guard.HistoryGraphicsMemoryGrowthBytes;
            historyTextureEstimateBeforeCleanup = guard.HistoryTextureMemoryEstimateBytes;
            AssertOnlySelectedActive();
        }

        IEnumerator WaitForRevisions(long target, int timeoutFrames, bool sampleSafety)
        {
            int frames = 0;
            long nextSampleRevision = quilt == null ? 0 : quilt.QuiltRevision + 1;
            long firstMeasurementRevision = target - 32;
            while (quilt != null && quilt.QuiltRevision < target && frames < timeoutFrames)
            {
                if (sampleSafety) AssertSafety();
                if (sampleSafety) AssertOnlySelectedActive();
                frames++;
                yield return null;
                if (sampleSafety && phase == "selected-effect-32-completed-quilt-revisions" && measurementSamples.Count < 6)
                {
                    long currentRevision = quilt == null ? 0 : quilt.QuiltRevision;
                    while (nextSampleRevision <= currentRevision)
                    {
                        long relative = nextSampleRevision - firstMeasurementRevision;
                        if (relative == 1 || relative == 2 || relative == 4 || relative == 8 || relative == 16 || relative == 32)
                            RecordMeasurementSample(relative, nextSampleRevision);
                        nextSampleRevision++;
                    }
                }
            }
            Assert.IsNotNull(quilt);
            Assert.GreaterOrEqual(quilt.QuiltRevision, target, "Scheduler quilt revision did not reach the required target.");
        }

        void RecordMeasurementSample(long relativeRevision, long absoluteRevision)
        {
            AssertSafety();
            bool valid = ProcessMemoryReader.TryRead(out long privateBytes, out long workingSetBytes);
            Assert.IsTrue(valid, "Process-memory sample unavailable at a required revision.");
            measurementSamples.Add(string.Join(",", relativeRevision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                absoluteRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), Time.frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                safety.rawCurrent.ToString(System.Globalization.CultureInfo.InvariantCulture), privateBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                workingSetBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        void AssertSafety()
        {
            Assert.IsNotNull(safety);
            Assert.IsTrue(safety.Read(), safety.failure ?? "Memory safety sample failed.");
        }

        void AssertOnlySelectedActive()
        {
            var active = UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsSortMode.None).Where(value => value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded && value.gameObject.activeInHierarchy).ToArray();
            Assert.AreEqual(1, active.Length, "An unrelated VisualEffect became active during the pilot.");
            string path = AssetDatabase.GetAssetPath(active[0].visualEffectAsset);
            Assert.IsFalse(string.IsNullOrEmpty(path));
            Assert.AreEqual(SelectedGuid, AssetDatabase.AssetPathToGUID(path));
            Assert.IsTrue(effectObject.activeInHierarchy && effect.enabled);
        }

        void CaptureRendererSourceState()
        {
            rendererSourceEnabled = false;
            rendererSourcePath = "";
            rendererSourceGuid = "";
            rendererSourceFileId = 0;
            try
            {
                VFXRenderer source = PrefabUtility.GetCorrespondingObjectFromSource(effectRenderer) as VFXRenderer;
                if (source == null) source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(effectRenderer) as VFXRenderer;
                if (source == null) return;
                rendererSourceEnabled = source.enabled;
                rendererSourcePath = AssetDatabase.GetAssetPath(source);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out rendererSourceGuid, out rendererSourceFileId);
            }
            catch { rendererSourcePath = "source-query-failed"; }
        }

        static bool ReadSerializedEnabled(Component component)
        {
            try
            {
                var serialized = new SerializedObject(component);
                var enabled = serialized.FindProperty("m_Enabled");
                return enabled != null && enabled.boolValue;
            }
            catch { return false; }
        }

        void CleanupSynchronous()
        {
            try { if (rig != null) rig.AutoRender = false; } catch { }
            try { if (quilt != null) { quilt.autoRender = false; quilt.enabled = false; } } catch { }
            try { if (effect != null) { effect.Stop(); effect.gameObject.SetActive(false); } } catch { }
            try { if (guard != null) guard.ReleaseQuiltRenderResources(); } catch { }
            try { if (onImage != null) UnityEngine.Object.DestroyImmediate(onImage); onImage = null; } catch { }
            try { if (offImage != null) UnityEngine.Object.DestroyImmediate(offImage); offImage = null; } catch { }
            try { if (offRepeatImage != null) UnityEngine.Object.DestroyImmediate(offRepeatImage); offRepeatImage = null; } catch { }
            try { if (referenceCamera != null) { referenceCamera.enabled = false; referenceCamera.targetTexture = originalReferenceTarget; } } catch { }
            try { if (referenceOutput != null) { if (RenderTexture.active == referenceOutput) RenderTexture.active = null; referenceOutput.Release(); UnityEngine.Object.DestroyImmediate(referenceOutput); referenceOutput = null; } } catch { }
            try { if (qualityAntiAliasingCaptured) QualitySettings.antiAliasing = originalQualityAntiAliasing; } catch { }
        }

        static Texture2D ReadQuilt(RenderTexture texture)
        {
            Assert.IsNotNull(texture); Assert.IsTrue(texture.IsCreated());
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = texture;
                var result = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                result.Apply(false, false);
                return result;
            }
            finally { RenderTexture.active = previous; }
        }

        static int CountChangedPixels(Texture2D on, Texture2D off, int threshold, out bool offIsBlack)
        {
            Assert.AreEqual(on.width, off.width); Assert.AreEqual(on.height, off.height);
            Color32[] a = on.GetPixels32(); Color32[] b = off.GetPixels32();
            int changed = 0; offIsBlack = true;
            for (int i = 0; i < a.Length; ++i)
            {
                if (b[i].r > 2 || b[i].g > 2 || b[i].b > 2) offIsBlack = false;
                int difference = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                if (difference > threshold) changed++;
            }
            return changed;
        }

        static void SavePng(Texture2D texture, string relativePath)
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllBytes(full, texture.EncodeToPNG());
        }
        static string PathOf(Transform value)
        {
            var parts = new Stack<string>(); while (value != null) { parts.Push(value.name); value = value.parent; }
            return string.Join("/", parts.ToArray());
        }
        static string PoseString(Vector3 p, Quaternion q) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "p={0:R},{1:R},{2:R};q={3:R},{4:R},{5:R},{6:R}", p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        static string Vector3String(Vector3 value) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:R},{1:R},{2:R}", value.x, value.y, value.z);
        static string BoundsString(Bounds value) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "center={0};size={1}", Vector3String(value.center), Vector3String(value.size));
        static string Flatten(List<long[]> values)
        {
            var text = new StringBuilder();
            for (int row = 0; row < values.Count; ++row)
            {
                if (row != 0) text.Append(';');
                for (int column = 0; column < values[row].Length; ++column)
                {
                    if (column != 0) text.Append(',');
                    text.Append(values[row][column].ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }
        void Append(string key, object value) => evidence.Append(key).Append('=').Append(value).Append('\n');
        void WriteEvidence()
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", EvidencePath));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, evidence.ToString(), new UTF8Encoding(false));
        }
    }
}
#endif
