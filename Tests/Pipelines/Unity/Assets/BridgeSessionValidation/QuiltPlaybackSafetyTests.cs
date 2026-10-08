using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using ViewWeave;
using Object = UnityEngine.Object;

namespace ViewWeave.BridgeSessionValidation
{
    public sealed class QuiltPlaybackSafetyTests
    {
        [Test]
        public void CleanupPolicyRequiresFenceAndCorrectQueueOwnershipProof()
        {
            MethodInfo policy = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgePlaybackCleanupPolicy", true)
                .GetMethod("CanRelease", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(policy);
            Assert.IsFalse(InvokePolicy(policy, false, false, true, true, true, true));
            Assert.IsFalse(InvokePolicy(policy, true, true, false, true, true, true));
            Assert.IsFalse(InvokePolicy(policy, true, false, false, true, false, true));
            Assert.IsTrue(InvokePolicy(policy, true, false, false, true, true, false));
            Assert.IsFalse(InvokePolicy(policy, true, false, false, false, false, false));
            Assert.IsTrue(InvokePolicy(policy, true, false, false, false, false, true));
            Assert.IsTrue(InvokePolicy(policy, true, true, true, false, false, true));
        }

        [Test]
        public void FinalQuitKeepsPendingReadbackOrFenceInProgress()
        {
            Type policy = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgePlaybackCleanupPolicy", true);
            MethodInfo shouldWait = policy.GetMethod("ShouldWaitForFinalQuitProof", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(shouldWait);
            Assert.IsTrue((bool)shouldWait.Invoke(null, new object[] { true, false, true }), "A pending readback must remain in progress after native handoff.");
            Assert.IsTrue((bool)shouldWait.Invoke(null, new object[] { true, true, false }), "A pending fence must remain in progress.");
            Assert.IsFalse((bool)shouldWait.Invoke(null, new object[] { true, true, true }), "Completed readback and fence proceed to the remaining ownership proof.");
            Assert.IsFalse((bool)shouldWait.Invoke(null, new object[] { false, false, false }), "The final-quit defer rule does not alter ordinary cleanup.");
        }

        [Test]
        public void SinglePngCursorEndDoesNotReleasePlaybackButSequenceEndDoes()
        {
            Type policy = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgePlaybackCleanupPolicy", true);
            MethodInfo shouldStop = policy.GetMethod("ShouldStopOnCursorEnd", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(shouldStop);
            var singleImageCursor = new QuiltPlaybackCursor(30, 1);
            int submittedFrames = 0;
            bool stopped = false;
            for (int i = 0; i < 36; ++i)
            {
                QuiltPlaybackStep step = singleImageCursor.Advance(1d / 30d);
                if (step.HasFrame) submittedFrames++;
                if (step.IsEndOfStream && (bool)shouldStop.Invoke(null, new object[] { false })) stopped = true;
            }
            Assert.AreEqual(1, submittedFrames);
            Assert.IsFalse(stopped, "A single PNG remains held after the cursor reaches EOF beyond one second.");
            Assert.IsTrue((bool)shouldStop.Invoke(null, new object[] { true }));
        }

        [Test]
        public void ExpiredCleanupDeadlineWithoutProofRetainsResources()
        {
            Type policy = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgePlaybackCleanupPolicy", true);
            MethodInfo expired = policy.GetMethod("DeadlineExpiredWithoutProof", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(expired);
            Assert.IsTrue((bool)expired.Invoke(null, new object[] { 11d, 10d, false }));
            Assert.IsFalse((bool)expired.Invoke(null, new object[] { 11d, 10d, true }));
            Assert.IsFalse((bool)expired.Invoke(null, new object[] { 9d, 10d, false }));
        }

        [Test]
        public void RestoreRequiresActiveSessionRendererAndUnchangedOutputAndCameras()
        {
            Type policy = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgePlaybackCleanupPolicy", true);
            MethodInfo stable = policy.GetMethod("HasStableRendererOwnership", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(stable);
            Assert.IsTrue((bool)stable.Invoke(null, new object[] { true, true, true }));
            Assert.IsFalse((bool)stable.Invoke(null, new object[] { false, true, true }));
            Assert.IsFalse((bool)stable.Invoke(null, new object[] { true, false, true }));
            Assert.IsFalse((bool)stable.Invoke(null, new object[] { true, true, false }));

            GameObject host = new GameObject("QuiltPlaybackSafety.CameraIdentity");
            Camera source = host.AddComponent<Camera>();
            GameObject referenceHost = new GameObject("QuiltPlaybackSafety.ReferenceCamera");
            Camera reference = referenceHost.AddComponent<Camera>();
            QuiltOutputTarget alternateOutput = referenceHost.AddComponent<QuiltOutputTarget>();
            QuiltRenderer renderer = host.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = source;
            renderer.referenceCamera = reference;
            try
            {
                Assert.IsNotNull(source);
                Assert.IsNotNull(reference);
                Assert.IsNotNull(alternateOutput);
                Assert.AreNotSame(source, reference);
                Type lease = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeQuiltPlaybackLease", true);
                MethodInfo cameras = lease.GetMethod("RendererCamerasMatchForRestore", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo outputMatches = lease.GetMethod("RendererOutputMatches", BindingFlags.Static | BindingFlags.NonPublic);
                MethodInfo ownersActive = lease.GetMethod("RestoreOwnersAreActive", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.IsNotNull(cameras);
                Assert.IsNotNull(outputMatches);
                Assert.IsNotNull(ownersActive);
                AssertReflectionSignature(stable, typeof(bool), typeof(bool), typeof(bool));
                AssertReflectionSignature(cameras, typeof(QuiltRenderer), typeof(Camera), typeof(Camera), typeof(float), typeof(float), typeof(float), typeof(float), typeof(bool));
                AssertReflectionSignature(outputMatches, typeof(QuiltRenderer), typeof(MonoBehaviour));
                AssertReflectionSignature(ownersActive, typeof(ViewWeaveBridgeSession), typeof(QuiltRenderer));
                object[] expected = { renderer, source, reference, source.nearClipPlane, source.farClipPlane, reference.nearClipPlane, reference.farClipPlane, renderer.followReferenceFieldOfView };
                Assert.IsTrue((bool)cameras.Invoke(null, expected));
                renderer.referenceCamera = null;
                Assert.IsFalse((bool)cameras.Invoke(null, expected), "A changed referenceCamera must prevent live restoration.");
                renderer.referenceCamera = reference;
                source.nearClipPlane += 0.01f;
                Assert.IsFalse((bool)cameras.Invoke(null, expected), "A changed source-camera clip range must prevent live restoration.");
                renderer.OutputBehaviour = renderer;
                Assert.IsTrue((bool)outputMatches.Invoke(null, new object[] { renderer, renderer }));
                renderer.OutputBehaviour = alternateOutput;
                Assert.IsFalse((bool)outputMatches.Invoke(null, new object[] { renderer, renderer }), "A changed output must prevent live restoration.");
                renderer.OutputBehaviour = renderer;
                GameObject sessionHost = new GameObject("QuiltPlaybackSafety.RestoreSession");
                sessionHost.SetActive(false);
                ViewWeaveBridgeSession session = sessionHost.AddComponent<ViewWeaveBridgeSession>();
                session.startOnEnable = false;
                BridgeWorkerQuiltOutput sessionOutput = sessionHost.GetComponent<BridgeWorkerQuiltOutput>();
                Assert.IsNotNull(sessionOutput);
                sessionOutput.initializeOnEnable = false;
                sessionHost.SetActive(true);
                try
                {
                    Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State);
                    Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, sessionOutput.State);
                    Assert.IsTrue((bool)ownersActive.Invoke(null, new object[] { session, renderer }));
                    renderer.enabled = false;
                    Assert.IsFalse((bool)ownersActive.Invoke(null, new object[] { session, renderer }), "A disabled Renderer must prevent live restoration.");
                    renderer.enabled = true;
                    session.enabled = false;
                    Assert.IsFalse((bool)ownersActive.Invoke(null, new object[] { session, renderer }), "A disabled Bridge session must prevent live restoration.");
                }
                finally { Object.DestroyImmediate(sessionHost); }
            }
            finally
            {
                Object.DestroyImmediate(referenceHost);
                Object.DestroyImmediate(host);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PlaybackOwnerDisableOrDestroyKeepsRendererGuardWithoutGpuProof(bool destroyOwner)
        {
            GameObject rendererHost = new GameObject("QuiltPlaybackSafety.RetainedRenderer");
            QuiltRenderer renderer = rendererHost.AddComponent<QuiltRenderer>();
            Assert.IsTrue(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out QuiltRendererOperationLease guard, out string error), error);

            GameObject owner = new GameObject("QuiltPlaybackSafety.OwnerLoss");
            BridgeQuiltPlaybackLease lease = owner.AddComponent<BridgeQuiltPlaybackLease>();
            typeof(BridgeQuiltPlaybackLease).GetField("rendererLease", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(lease, guard);
            FieldInfo leasesField = typeof(BridgeQuiltPlaybackLease).GetField("knownLeases", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo pump = typeof(BridgeQuiltPlaybackLease).GetMethod("PumpAll", BindingFlags.Static | BindingFlags.NonPublic);
            var knownLeases = (System.Collections.IList)leasesField.GetValue(null);
            knownLeases.Add(lease);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Could not create the final playback graphics fence"));
            if (destroyOwner)
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Could not create the Editor transition fence"));
            try
            {
                if (destroyOwner) Object.DestroyImmediate(owner);
                else lease.enabled = false;
                pump.Invoke(null, null);
                Assert.AreEqual(BridgeQuiltPlaybackLease.LeaseState.Retained, lease.State);
                Assert.IsTrue(QuiltRendererOperationLease.IsLeased(renderer), "The renderer guard remains held until completion is proven.");
            }
            finally
            {
                knownLeases.Remove(lease);
                guard.Dispose();
                if (owner != null)
                {
                    LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Could not create the Editor transition fence"));
                    Object.DestroyImmediate(owner);
                }
                Object.DestroyImmediate(rendererHost);
            }
        }

        [UnityTest]
        public IEnumerator D3D12TailFenceAndOnePixelReadbackProveSourceGpuCompletionWithoutNativeCalls()
        {
            if (!Application.isPlaying) Assert.Ignore("This GPU ownership test requires Editor Play mode.");
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) Assert.Ignore("This regression targets the Direct3D12 Editor backend.");
            if (!SystemInfo.supportsGraphicsFence || !SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("This Editor backend does not support the required GPU proof APIs.");

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            var playbackTexture = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear)
            {
                name = "QuiltPlaybackSafety.D3D12Source",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            GameObject host = new GameObject("QuiltPlaybackSafety.D3D12Lease");
            GameObject rendererHost = new GameObject("QuiltPlaybackSafety.D3D12Guard");
            BridgeQuiltPlaybackLease lease = host.AddComponent<BridgeQuiltPlaybackLease>();
            UnityEngine.Object record = null;
            QuiltRendererOperationLease rendererGuard = null;
            bool gpuCompletionObserved = false;
            Color32 expected = new Color32(37, 91, 173, 255);
            source.SetPixels32(new[] { expected, expected, expected, expected });
            source.Apply(false, true);

            try
            {
                Assert.IsTrue(playbackTexture.Create());
                Graphics.Blit(source, playbackTexture);

                Type leaseType = typeof(BridgeQuiltPlaybackLease);
                Type recordType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgePlaybackRecoveryRecord", true);
                MethodInfo createRecord = recordType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic);
                record = (UnityEngine.Object)createRecord.Invoke(null, new object[] { playbackTexture, 41L });
                Assert.IsTrue((record.hideFlags & HideFlags.DontUnloadUnusedAsset) != 0, "The recovery record remains discoverable across an Editor domain reload.");
                bool recoveryRecordDiscoverable = false;
                foreach (UnityEngine.Object candidate in Resources.FindObjectsOfTypeAll(recordType))
                    if (ReferenceEquals(candidate, record)) recoveryRecordDiscoverable = true;
                Assert.IsTrue(recoveryRecordDiscoverable, "Editor recovery discovery can find the retained proof record.");
                leaseType.GetField("playbackTexture", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(lease, playbackTexture);
                leaseType.GetField("recoveryRecord", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(lease, record);
                leaseType.GetProperty("State", BindingFlags.Instance | BindingFlags.Public).SetValue(lease, BridgeQuiltPlaybackLease.LeaseState.Playing);

                QuiltRenderer renderer = rendererHost.AddComponent<QuiltRenderer>();
                string guardError;
                Assert.IsTrue(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out rendererGuard, out guardError), guardError);
                leaseType.GetField("rendererLease", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(lease, rendererGuard);

                MethodInfo setFence = leaseType.GetMethod("SetTailFence", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo fencePassed = leaseType.GetMethod("FencePassed", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo prepareTransition = leaseType.GetMethod("PrepareForEditorTransition", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(setFence);
                Assert.IsNotNull(fencePassed);
                Assert.IsNotNull(prepareTransition);
                setFence.Invoke(lease, null);

                AsyncGPUReadbackRequest pixelRequest = AsyncGPUReadback.Request(
                    playbackTexture, mipIndex: 0, x: 0, width: 1, y: 0, height: 1, z: 0, depth: 1, dstFormat: TextureFormat.RGBA32);
                pixelRequest.WaitForCompletion();
                Assert.IsTrue(pixelRequest.done, "The one-pixel GPU readback must complete.");
                Assert.IsFalse(pixelRequest.hasError, "The one-pixel GPU readback must not report an error.");
                var pixel = pixelRequest.GetData<Color32>()[0];
                Assert.That(Math.Abs(pixel.r - expected.r), Is.LessThanOrEqualTo(2));
                Assert.That(Math.Abs(pixel.g - expected.g), Is.LessThanOrEqualTo(2));
                Assert.That(Math.Abs(pixel.b - expected.b), Is.LessThanOrEqualTo(2));
                Assert.That(Math.Abs(pixel.a - expected.a), Is.LessThanOrEqualTo(2));
                Assert.IsTrue((bool)fencePassed.Invoke(lease, null), "The real GraphicsFence must pass after the readback is queued.");
                gpuCompletionObserved = true;

                prepareTransition.Invoke(lease, new object[] { "GPU regression transition handshake." });
                Assert.AreEqual(BridgeQuiltPlaybackLease.LeaseState.Stopping, lease.State, "Unknown Bridge queue ownership must keep the lease stopping.");
                Assert.IsTrue(playbackTexture != null && playbackTexture.IsCreated(), "The source RenderTexture remains alive without a reset or same-generation handoff proof.");
                Assert.AreSame(playbackTexture, recordType.GetField("sourceTexture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(record));
                Assert.IsTrue((bool)recordType.GetField("tailFencePassed", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(record));
                Assert.IsTrue((bool)recordType.GetField("readbackCompleted", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(record));
                Assert.IsTrue((bool)recordType.GetField("readbackValid", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(record));
                Assert.IsFalse((bool)recordType.GetProperty("HasCompleteProof", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(record), "A GPU fence/readback alone must not fake Bridge reset or reaper ownership proof.");
                Assert.IsTrue(QuiltRendererOperationLease.IsLeased(renderer), "The renderer guard remains held while Bridge queue ownership is unknown.");
                yield return null;
            }
            finally
            {
                if (gpuCompletionObserved)
                {
                    lease.GetType().GetProperty("State", BindingFlags.Instance | BindingFlags.Public).SetValue(lease, BridgeQuiltPlaybackLease.LeaseState.Stopped);
                    if (rendererGuard != null) rendererGuard.Dispose();
                    if (record != null) Object.DestroyImmediate(record);
                    if (playbackTexture != null)
                    {
                        if (playbackTexture.IsCreated()) playbackTexture.Release();
                        Object.DestroyImmediate(playbackTexture);
                    }
                    Object.DestroyImmediate(host);
                    Object.DestroyImmediate(rendererHost);
                    Object.DestroyImmediate(source);
                }
                else
                {
                    Debug.LogWarning("[ViewWeave] D3D12 playback GPU proof failed; the source RenderTexture remains retained.");
                    if (host != null) host.hideFlags = HideFlags.HideAndDontSave;
                    if (rendererHost != null) rendererHost.hideFlags = HideFlags.HideAndDontSave;
                }
            }
        }
        [Test]
        public void ReaperHandoffDetachesFieldsBeforeReentrantDisposeNotification()
        {
            GameObject host = new GameObject("QuiltPlaybackSafety.ReaperHandoff");
            host.SetActive(false);
            BridgeWorkerQuiltOutput output = host.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            host.SetActive(true);
            Type outputType = typeof(BridgeWorkerQuiltOutput);
            var command = new CommandBuffer { name = "QuiltPlaybackSafety.FakeCommand" };
            var texture = new RenderTexture(8, 8, 0);
            outputType.GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, new IntPtr(17));
            outputType.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Ready);
            outputType.GetField("transferTexture", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, texture);
            outputType.GetField("command", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, command);
            outputType.GetField("sessionGeneration", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, 7L);
            int enqueueCount = 0;
            bool callbackSawDetachedFields = false;
            output.PlaybackReaperHandoff += generation =>
            {
                callbackSawDetachedFields = generation == 7L &&
                    (IntPtr)outputType.GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(output) == IntPtr.Zero &&
                    outputType.GetField("transferTexture", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(output) == null &&
                    outputType.GetField("command", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(output) == null;
                output.Dispose();
            };
            try
            {
                Type actionType = typeof(Action<IntPtr, RenderTexture, CommandBuffer, bool>);
                MethodInfo transfer = outputType.GetMethod("TransferOwnedResourcesToReaper", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(bool), actionType }, null);
                Assert.IsNotNull(transfer);
                var enqueue = new Action<IntPtr, RenderTexture, CommandBuffer, bool>((handle, transferredTexture, transferredCommand, needsClose) =>
                {
                    enqueueCount++;
                    Assert.AreEqual(new IntPtr(17), handle);
                    Assert.AreSame(texture, transferredTexture);
                    Assert.AreSame(command, transferredCommand);
                    Assert.IsFalse(needsClose);
                });
                transfer.Invoke(output, new object[] { false, enqueue });
                transfer.Invoke(output, new object[] { false, enqueue });
                Assert.AreEqual(1, enqueueCount);
                Assert.IsTrue(callbackSawDetachedFields);
            }
            finally
            {
                command.Release();
                Object.DestroyImmediate(texture);
                outputTypeClearFakeHandle(output);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void NormalSubmitIsRejectedBeforeTextureReferenceOrBlitWhilePlaybackOwnsOutput()
        {
            GameObject host = new GameObject("QuiltPlaybackSafety.Output");
            host.SetActive(false);
            BridgeWorkerQuiltOutput output = host.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            host.SetActive(true);
            try
            {
                Type outputType = typeof(BridgeWorkerQuiltOutput);
                outputType.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Ready);
                outputType.GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, new IntPtr(1));
                object token = new object();
                MethodInfo acquire = outputType.GetMethod("TryBeginPlaybackOutput", BindingFlags.Instance | BindingFlags.NonPublic);
                object[] args = { token, string.Empty };
                Assert.IsTrue((bool)acquire.Invoke(output, args), (string)args[1]);
                uint before = output.SubmissionAttempts;
                output.Submit(null, default(QuiltLayout));
                Assert.IsNull(output.QuiltTexture);
                Assert.AreEqual(before, output.SubmissionAttempts);
                MethodInfo submit = outputType.GetMethod("TrySubmitPlayback", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsFalse((bool)submit.Invoke(output, new object[] { new object(), null, default(QuiltLayout) }));
                MethodInfo reset = outputType.GetMethod("TryResetTransferForPlayback", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsFalse((bool)reset.Invoke(output, new object[] { new object() }));
            }
            finally
            {
                outputTypeClearFakeHandle(output);
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void SessionRejectsQualityChangeWhilePlaybackTokenIsHeld()
        {
            GameObject host = new GameObject("QuiltPlaybackSafety.Session");
            host.SetActive(false);
            ViewWeaveBridgeSession session = host.AddComponent<ViewWeaveBridgeSession>();
            try
            {
                typeof(ViewWeaveBridgeSession).GetField("playbackToken", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, new object());
                QuiltQualityPreset before = session.Quality;
                Assert.IsFalse(session.SetQuality(QuiltQualityPreset.Native));
                Assert.AreEqual(before, session.Quality);
                StringAssert.Contains("cannot change while quilt playback", session.LastError);
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally { Object.DestroyImmediate(host); }
        }

        [UnityTest]
        public IEnumerator PausedRecorderKeepsRendererExclusiveUntilStop()
        {
            if (!Application.isPlaying) Assert.Ignore("This concurrency test requires Play mode.");
            if (Time.timeScale != 1f || Time.captureDeltaTime != 0f) Assert.Ignore("This concurrency test requires the default Unity time owner.");
            string rootPath = Path.Combine(Path.GetTempPath(), "ViewWeaveRecorderPause_" + Guid.NewGuid().ToString("N"));
            GameObject host = CreateRecorderHost();
            QuiltRenderer renderer = host.GetComponent<QuiltRenderer>();
            QuiltRecorder recorder = host.GetComponent<QuiltRecorder>();
            try
            {
                recorder.StartRecording(rootPath, 30, 100);
                recorder.PauseRecording();
                Assert.IsTrue(recorder.IsRecording);
                Assert.IsTrue(recorder.IsPaused);
                Assert.AreEqual(QuiltRecordingStatus.Paused, recorder.Status);
                Assert.AreEqual(0f, Time.captureDeltaTime);
                Assert.IsFalse(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out QuiltRendererOperationLease blocked, out string error));
                Assert.IsNull(blocked);
                Assert.IsNotEmpty(error);

                recorder.StopRecording();
                Assert.IsFalse(recorder.IsRecording);
                Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status);
                Assert.IsTrue(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out QuiltRendererOperationLease available, out error), error);
                available.Dispose();
                renderer.autoRender = false;
                yield return null;
            }
            finally
            {
                if (recorder.IsRecording) recorder.StopRecording();
                renderer.autoRender = false;
                Object.DestroyImmediate(host);
                if (Directory.Exists(rootPath)) Directory.Delete(rootPath, true);
            }
        }

        [UnityTest]
        public IEnumerator RecorderInitializationFailureReleasesOperationLease()
        {
            if (!Application.isPlaying) Assert.Ignore("This concurrency test requires Play mode.");
            if (Time.timeScale != 1f || Time.captureDeltaTime != 0f) Assert.Ignore("This concurrency test requires the default Unity time owner.");
            string parent = Path.Combine(Path.GetTempPath(), "ViewWeaveRecorderFailure_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(parent);
            string fileRoot = Path.Combine(parent, "not-a-directory");
            File.WriteAllText(fileRoot, "fixture");
            GameObject host = CreateRecorderHost();
            QuiltRenderer renderer = host.GetComponent<QuiltRenderer>();
            QuiltRecorder recorder = host.GetComponent<QuiltRecorder>();
            try
            {
                Assert.Throws<IOException>(() => recorder.StartRecording(fileRoot, 30, 10));
                Assert.IsFalse(recorder.IsRecording);
                Assert.IsFalse(QuiltRendererOperationLease.IsLeased(renderer));
                Assert.IsTrue(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out QuiltRendererOperationLease available, out string error), error);
                available.Dispose();
                renderer.autoRender = false;
                yield return null;
            }
            finally
            {
                if (recorder.IsRecording) recorder.StopRecording();
                renderer.autoRender = false;
                Object.DestroyImmediate(host);
                if (Directory.Exists(parent)) Directory.Delete(parent, true);
            }
        }

        static GameObject CreateRecorderHost()
        {
            var host = new GameObject("QuiltPlaybackSafety.Recorder");
            Camera camera = host.AddComponent<Camera>();
            QuiltRenderer renderer = host.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.quiltWidth = 64;
            renderer.settings.quiltHeight = 48;
            renderer.settings.columns = 2;
            renderer.settings.rows = 2;
            renderer.settings.views = 4;
            renderer.settings.viewAspect = 0.75f;
            renderer.settings.cameraDistance = 3f;
            renderer.settings.viewCone = 20f;
            renderer.autoRender = true;
            host.AddComponent<QuiltRecorder>();
            return host;
        }

        static void outputTypeClearFakeHandle(BridgeWorkerQuiltOutput output)
        {
            typeof(BridgeWorkerQuiltOutput).GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, IntPtr.Zero);
            typeof(BridgeWorkerQuiltOutput).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Closed);
        }

        static bool InvokePolicy(MethodInfo method, bool fence, bool transition, bool readback, bool ready, bool reset, bool handoff)
        {
            return (bool)method.Invoke(null, new object[] { fence, transition, readback, ready, reset, handoff });
        }

        static bool IsAscii(string value)
        {
            if (value == null) return true;
            foreach (char character in value) if (character > 0x7e) return false;
            return true;
        }

        static void AssertReflectionSignature(MethodInfo method, params Type[] expected)
        {
            ParameterInfo[] parameters = method.GetParameters();
            Assert.AreEqual(expected.Length, parameters.Length, method.Name + " parameter count");
            for (int index = 0; index < expected.Length; index++)
                Assert.AreEqual(expected[index], parameters[index].ParameterType, method.Name + " parameter " + index);
        }
    }
}
