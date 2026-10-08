using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.HDRP;
using Object = UnityEngine.Object;

namespace ViewWeave.BridgeSessionValidation
{
    public sealed class BridgeWorkerShutdownPolicyTests
    {
        [Test]
        public void D3D11SelectsReadbackTailWithoutFenceSupport()
        {
            Assert.AreEqual(BridgeGpuProofMode.ReadbackTail,
                BridgeGraphicsBackendPolicy.SelectGpuProofMode(GraphicsDeviceType.Direct3D11, false, false, true));
        }

        [Test]
        public void D3D12SelectsFenceAndReadbackOnlyWhenBothFenceCapabilitiesExist()
        {
            Assert.AreEqual(BridgeGpuProofMode.FenceAndReadback,
                BridgeGraphicsBackendPolicy.SelectGpuProofMode(GraphicsDeviceType.Direct3D12, true, true, true));
            Assert.AreEqual(BridgeGpuProofMode.Unsupported,
                BridgeGraphicsBackendPolicy.SelectGpuProofMode(GraphicsDeviceType.Direct3D12, true, false, true));
            Assert.AreEqual(BridgeGpuProofMode.Unsupported,
                BridgeGraphicsBackendPolicy.SelectGpuProofMode(GraphicsDeviceType.Direct3D12, false, true, true));
        }

        [Test]
        public void UnknownApiOrMissingReadbackIsUnsupported()
        {
            Assert.AreEqual(BridgeGpuProofMode.Unsupported,
                BridgeGraphicsBackendPolicy.SelectGpuProofMode(GraphicsDeviceType.Null, true, true, true));
            Assert.AreEqual(BridgeGpuProofMode.Unsupported,
                BridgeGraphicsBackendPolicy.SelectGpuProofMode(GraphicsDeviceType.Direct3D11, false, false, false));
        }

        [Test]
        public void NativeFinalStatusRequiresVersionedCompleteAndJoinedHost()
        {
            var status = new BridgeWorkerNative.FinalShutdownStatusV1
            {
                structSize = 32,
                version = 1,
                state = 3,
                hostJoined = 1
            };
            Assert.IsTrue(BridgeWorkerShutdownPolicy.IsNativeComplete(status));

            status.registeredContexts = 1;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            status.registeredContexts = 0;
            status.closedUncollectedContexts = 1;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            status.closedUncollectedContexts = 0;
            status.hostJoined = 0;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            status.hostJoined = 1;
            status.hostRunning = 1;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            status.hostRunning = 0;
            status.lastResult = -1;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            status.lastResult = 0;
            status.version = 0;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.HasNativeActivity(status));
            status.version = 1;
            status.hostRunning = 2;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsNativeComplete(status));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.HasNativeActivity(status));
        }

        [Test]
        public void LegacyCloseRequiresMatchingVersionedSafeSessionStatus()
        {
            const ulong session = 42;
            var status = new BridgeNative.SessionCloseStatusV1
            {
                structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeNative.SessionCloseStatusV1)),
                version = 1,
                sessionId = session,
                state = 1
            };
            Assert.AreEqual(32, status.structSize);
            Assert.IsTrue(BridgeWorkerShutdownPolicy.IsValidLegacyCloseStatus(session, status));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.IsLegacyCloseSafe(session, status));

            status.sessionId++;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsLegacyCloseSafe(session, status));
            status.sessionId = session;
            status.lastResult = -1;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsLegacyCloseSafe(session, status));
            status.lastResult = 0;
            status.state = 3;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.IsLegacyCloseSafe(session, status));
        }

        [Test]
        public void LegacyResourcesNeedBothNativeCloseAndGpuProof()
        {
            BridgeWorkerRecoveryRecord record = BridgeWorkerRecoveryRecord.CreateLegacy(42, null, true, 3);
            try
            {
                record.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                record.SetGpuProof(true, true, true, true);
                Assert.IsFalse(record.CanRelease);
                record.NativeReleased = true;
                Assert.IsTrue(record.CanRelease);
            }
            finally { Object.DestroyImmediate(record); }
        }

        [Test]
        public void TransferTextureReleaseRequiresSelectedModeReadbackAndNativeRelease()
        {
            var texture = new RenderTexture(8, 8, 0);
            BridgeWorkerRecoveryRecord record = BridgeWorkerRecoveryRecord.CreateLegacy(44, texture, true, 5);
            try
            {
                record.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                record.SetGpuProof(false, false, true, true);
                record.NativeReleased = true;
                Assert.IsTrue(record.HasGpuReleaseProof);
                Assert.IsTrue(record.CanRelease);

                record.SetGpuProof(false, false, true, false);
                Assert.IsFalse(record.HasGpuReleaseProof);
                Assert.IsFalse(record.CanRelease);

                record.SetGpuProofMode(BridgeGpuProofMode.Unsupported);
                record.SetGpuProof(false, false, true, true);
                Assert.IsFalse(record.HasGpuReleaseProof);
            }
            finally
            {
                Object.DestroyImmediate(record);
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void NewPlaybackTailInvalidatesThePreviousDurableReadbackProof()
        {
            var texture = new RenderTexture(8, 8, 0);
            BridgePlaybackRecoveryRecord record = BridgePlaybackRecoveryRecord.Create(texture, 91);
            try
            {
                record.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                record.SetTailFenceIssued(true);
                record.SetReadback(true, true);
                record.SetReadyReset(true);
                Assert.IsTrue(record.HasCompleteProof);

                record.BeginTailProof();
                record.SetTailFenceIssued(true);
                Assert.IsFalse(record.HasCompleteProof,
                    "A previous source frame readback cannot prove a newly issued but pending playback tail, even after same-session reset.");
            }
            finally
            {
                Object.DestroyImmediate(record);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void DestroyedLegacyTextureReferenceCannotBecomeNoTextureProof()
        {
            var texture = new RenderTexture(8, 8, 0);
            BridgeWorkerRecoveryRecord record = BridgeWorkerRecoveryRecord.CreateLegacy(43, texture, true, 4);
            try
            {
                Object.DestroyImmediate(texture);
                Assert.IsTrue(record.HadTransferTexture);
                Assert.IsFalse(record.HasGpuReleaseProof);
                record.NativeReleased = true;
                Assert.IsFalse(record.CanRelease);
            }
            finally { Object.DestroyImmediate(record); }
        }

        [Test]
        public void NativeCompletionDoesNotAllowQuitWhileManagedResourcesRemain()
        {
            Assert.IsFalse(BridgeWorkerShutdownPolicy.CanQuitImmediately(false, true));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.CanQuitImmediately(true, true));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.CanQuitImmediately(false, false));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.ShouldBlockAdmission(false, true));
            Assert.IsFalse(BridgeWorkerShutdownPolicy.ShouldBlockAdmission(false, false));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.ShouldUpgradeToSynchronousReloadProof(true, true));
            Assert.IsFalse(BridgeWorkerShutdownPolicy.ShouldUpgradeToSynchronousReloadProof(true, false));
            Assert.IsFalse(BridgeWorkerShutdownPolicy.CanCollectUnownedContexts(1));
            Assert.IsTrue(BridgeWorkerShutdownPolicy.CanCollectUnownedContexts(0));
        }

        [Test]
        public void IdleNativeHostStillRequiresFinalJoin()
        {
            var status = new BridgeWorkerNative.FinalShutdownStatusV1 { structSize = 32, version = 1, hostRunning = 1 };
            Assert.IsTrue(BridgeWorkerShutdownPolicy.HasNativeActivity(status));
            status.hostRunning = 0;
            Assert.IsFalse(BridgeWorkerShutdownPolicy.HasNativeActivity(status));
            status.closedUncollectedContexts = 1;
            Assert.IsTrue(BridgeWorkerShutdownPolicy.HasNativeActivity(status));
            status.closedUncollectedContexts = 0;
            status.lastResult = -2;
            Assert.IsTrue(BridgeWorkerShutdownPolicy.HasNativeActivity(status));
        }

        [Test]
        public void LostTransferTextureReferenceDoesNotBecomeGpuCompletionProof()
        {
            BridgeWorkerRecoveryRecord noTexture = BridgeWorkerRecoveryRecord.Create(System.IntPtr.Zero, null, false, 1);
            var texture = new RenderTexture(8, 8, 0);
            BridgeWorkerRecoveryRecord hadTexture = BridgeWorkerRecoveryRecord.Create(System.IntPtr.Zero, texture, false, 2);
            try
            {
                Assert.IsTrue(noTexture.HasGpuReleaseProof);
                Assert.IsFalse(hadTexture.HasGpuReleaseProof);
                Object.DestroyImmediate(texture);
                Assert.IsFalse(hadTexture.HasGpuReleaseProof);
            }
            finally
            {
                Object.DestroyImmediate(noTexture);
                Object.DestroyImmediate(hadTexture);
            }
        }

        [Test]
        public void PreviewWorkerCompletionRequiresTheExactRecoveryRecord()
        {
            var producer = new GameObject("Preview retention test producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            const long sessionGeneration = 17;
            BridgeWorkerRecoveryRecord ownedRecovery = BridgeWorkerRecoveryRecord.Create(System.IntPtr.Zero, null, false, 73);
            BridgeWorkerRecoveryRecord unrelatedRecovery = BridgeWorkerRecoveryRecord.Create(System.IntPtr.Zero, null, false, sessionGeneration);
            try
            {
                owner.SetWorkerProofExpected(true);
                Assert.IsTrue(ownedRecovery.AttachEditorPreviewOwner(owner));
                Assert.IsFalse(owner.WorkerRecoveryComplete);
                Assert.AreNotEqual(sessionGeneration, ownedRecovery.Generation);

                Assert.IsFalse(owner.MarkWorkerRecoveryComplete(unrelatedRecovery));
                Assert.IsFalse(owner.WorkerRecoveryComplete);
                Assert.AreEqual(sessionGeneration, unrelatedRecovery.Generation);

                Assert.IsTrue(ownedRecovery.NotifyEditorPreviewOwnerComplete());
                Assert.IsTrue(owner.WorkerRecoveryComplete);
                Object.DestroyImmediate(ownedRecovery);
                Assert.IsTrue(owner.WorkerHandoffComplete);
                Assert.IsTrue(owner.WorkerRecoveryComplete);
            }
            finally
            {
                Object.DestroyImmediate(owner);
                if (ownedRecovery != null) Object.DestroyImmediate(ownedRecovery);
                Object.DestroyImmediate(unrelatedRecovery);
                Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewRetentionFailsClosedWhenStopCallbackThrows()
        {
            var producer = new GameObject("Preview callback test producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord record = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            BridgeEditorPreviewRetention.CallbackRegistration callbacks = BridgeEditorPreviewRetention.CaptureCallbacks();
            try
            {
                BridgeEditorPreviewRetention.RegisterEditorCallbacks(
                    () => throw new System.InvalidOperationException("stop callback failed"),
                    _ => { });

                LogAssert.Expect(LogType.Error, "[ViewWeave] Editor preview stop request failed: stop callback failed");
                Assert.IsFalse(BridgeEditorPreviewRetention.RequestStop(false));
                Assert.IsFalse(record.StopRequested);
                Assert.IsFalse(record.Detached);
            }
            finally
            {
                BridgeEditorPreviewRetention.RestoreCallbacks(callbacks);
                Object.DestroyImmediate(record);
                Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewMsaaDescriptorUsesIncomingSourceCameraBeforeCopy()
        {
            var sourceObject = new GameObject("Preview MSAA source camera");
            Camera sourceCamera = sourceObject.AddComponent<Camera>();
            var provider = new TrackingMsaaProvider();
            QuiltLayout layout = new QuiltLayout(64, 64, 2, 2, 4);
            try
            {
                MethodInfo method = System.Type.GetType("ViewWeave.Editor.BridgeEditorPreview, ViewWeave.Bridge.Editor")
                    ?.GetMethod("GetDesiredMsaaSamples", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.IsNotNull(method);

                sourceCamera.allowMSAA = false;
                method.Invoke(null, new object[] { sourceCamera, provider, layout });
                Assert.IsFalse(provider.Called);

                sourceCamera.allowMSAA = true;
                method.Invoke(null, new object[] { sourceCamera, provider, layout });
                Assert.IsTrue(provider.Called);
                Assert.AreSame(sourceCamera, provider.Camera);
            }
            finally { Object.DestroyImmediate(sourceObject); }
        }

        [Test]
        public void PreviewCopiesLiveRenderControlsFromSourceRig()
        {
            var sourceObject = new GameObject("Preview live settings source");
            var destinationObject = new GameObject("Preview live settings destination");
            sourceObject.SetActive(false);
            destinationObject.SetActive(false);
            QuiltRenderer sourceRenderer = sourceObject.AddComponent<QuiltRenderer>();
            QuiltRenderer destinationRenderer = destinationObject.AddComponent<QuiltRenderer>();
            HdrpQuiltCamera sourceGuard = sourceObject.AddComponent<HdrpQuiltCamera>();
            HdrpQuiltCamera destinationGuard = destinationObject.AddComponent<HdrpQuiltCamera>();
            BridgeWorkerQuiltOutput sourceOutput = sourceObject.AddComponent<BridgeWorkerQuiltOutput>();
            BridgeWorkerQuiltOutput destinationOutput = destinationObject.AddComponent<BridgeWorkerQuiltOutput>();
            sourceOutput.initializeOnEnable = false;
            destinationOutput.initializeOnEnable = false;
            try
            {
                sourceRenderer.useRenderRequests = false;
                sourceGuard.adaptUnsupportedEffects = false;
                sourceGuard.fallbackFixedExposure = false;
                sourceGuard.fallbackFixedExposureEV = 6f;
                sourceOutput.zoom = 2.25f;

                MethodInfo copy = System.Type.GetType("ViewWeave.Editor.BridgeEditorPreview, ViewWeave.Bridge.Editor")
                    ?.GetMethod("CopyLivePreviewControls", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.IsNotNull(copy);
                copy.Invoke(null, new object[] { sourceRenderer, sourceGuard, destinationRenderer, destinationGuard, sourceOutput, destinationOutput });

                Assert.IsFalse(destinationRenderer.useRenderRequests);
                Assert.IsFalse(destinationGuard.adaptUnsupportedEffects);
                Assert.IsFalse(destinationGuard.fallbackFixedExposure);
                Assert.AreEqual(6f, destinationGuard.fallbackFixedExposureEV);
                Assert.AreEqual(2.25f, destinationOutput.zoom);
            }
            finally
            {
                Object.DestroyImmediate(sourceObject);
                Object.DestroyImmediate(destinationObject);
            }
        }

        [Test]
        public void PreviewRetentionLatchPreventsCoreDisableFromReleasingTexturesWithoutOwner()
        {
            var producer = new GameObject("Preview retention latch test producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            var texture = new RenderTexture(8, 8, 0);
            Assert.IsTrue(texture.Create());
            FieldInfo requiredField = typeof(QuiltRenderer).GetField("editorPreviewRetentionRequired", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo quiltTextureField = typeof(QuiltRenderer).GetField("quiltTexture", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(requiredField);
            Assert.IsNotNull(quiltTextureField);
            requiredField.SetValue(renderer, true);
            quiltTextureField.SetValue(renderer, texture);
            try
            {
                producer.SetActive(false);
                Assert.IsTrue(texture != null);
                Assert.IsTrue(texture.IsCreated());
            }
            finally
            {
                requiredField.SetValue(renderer, false);
                quiltTextureField.SetValue(renderer, null);
                if (texture != null)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
                Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewReadbackTailRequiresEveryTextureAndDoesNotRequireUnsupportedFence()
        {
            var producer = new GameObject("Preview proof mode producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            var first = new RenderTexture(8, 8, 0);
            var second = new RenderTexture(8, 8, 0);
            BridgeEditorPreviewRetentionRecord record = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            try
            {
                Assert.IsTrue(first.Create());
                Assert.IsTrue(second.Create());
                record.MarkStopRequested();
                Assert.IsTrue(record.SetTextures(new[] { first, second }));
                record.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                record.SetGpuProof(false, true, true);
                Assert.IsTrue(record.GpuProofComplete);

                record.SetGpuProof(false, false, true);
                Assert.IsFalse(record.GpuProofComplete);

                record.SetGpuProofMode(BridgeGpuProofMode.Unsupported);
                record.SetGpuProof(false, true, true);
                Assert.IsFalse(record.GpuProofComplete);
            }
            finally
            {
                Object.DestroyImmediate(record);
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
                Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewAllocationsAreTrackedBeforeRenderTextureCreate()
        {
            var producer = new GameObject("Preview tracked texture test producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            var texture = new RenderTexture(8, 8, 0);
            try
            {
                MethodInfo begin = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo track = typeof(QuiltRenderer).GetMethod("TryTrackEditorPreviewTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(begin);
                Assert.IsNotNull(track);
                Assert.IsTrue((bool)begin.Invoke(renderer, new object[] { owner }));

                Assert.IsTrue((bool)track.Invoke(renderer, new object[] { texture }));
                Assert.AreEqual(1, owner.Textures.Length);
                Assert.AreSame(texture, owner.Textures[0]);
                Assert.AreNotEqual(0, (int)(texture.hideFlags & HideFlags.DontUnloadUnusedAsset));
                Assert.IsFalse(texture.IsCreated());
                Assert.IsFalse(owner.CanRelease);

                Assert.IsTrue(texture.Create());
            }
            finally
            {
                typeof(QuiltRenderer).GetField("editorPreviewRetentionRequired", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, false);
                typeof(QuiltRenderer).GetField("editorPreviewRetentionOwner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, null);
                if (texture != null)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
                Object.DestroyImmediate(owner);
                Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewRetentionReleaseIsSafeWhenProducerDestroyReentersStopPump()
        {
            var producer = new GameObject("Preview reentrant release test producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            try
            {
                MethodInfo begin = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsTrue((bool)begin.Invoke(renderer, new object[] { owner }));
                owner.MarkStopRequested();
                owner.SetWorkerProofExpected(false);
                Assert.IsTrue(owner.SetTextures(System.Array.Empty<RenderTexture>()));
                Assert.IsTrue(owner.CanRelease);

                Assert.IsTrue(BridgeEditorPreviewRetention.Pump(true));
                Assert.IsTrue(producer == null);
                Assert.IsTrue(owner == null);
            }
            finally
            {
                if (owner != null) Object.DestroyImmediate(owner);
                if (producer != null) Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewResizeAbortsWithoutReleasingTrackedTextureWhenStopOwnerIsMissing()
        {
            var producer = new GameObject("Preview resize retention test producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            BridgeEditorPreviewRetention.CallbackRegistration callbacks = BridgeEditorPreviewRetention.CaptureCallbacks();
            var texture = new RenderTexture(8, 8, 0);
            try
            {
                MethodInfo begin = typeof(QuiltRenderer).GetMethod("BeginEditorPreviewRetention", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo track = typeof(QuiltRenderer).GetMethod("TryTrackEditorPreviewTexture", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo abort = typeof(QuiltRenderer).GetMethod("AbortPreviewTextureReplacement", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsTrue((bool)begin.Invoke(renderer, new object[] { owner }));
                Assert.IsTrue((bool)track.Invoke(renderer, new object[] { texture }));
                Assert.IsTrue(texture.Create());
                typeof(QuiltRenderer).GetField("quiltTexture", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, texture);
                BridgeEditorPreviewRetention.RegisterEditorCallbacks(null, null);

                TargetInvocationException invocation = Assert.Throws<TargetInvocationException>(() => abort.Invoke(renderer, new object[] { texture, "quilt" }));
                Assert.IsInstanceOf<System.InvalidOperationException>(invocation.InnerException);
                Assert.IsTrue(texture.IsCreated());
                Assert.AreSame(texture, owner.Textures[0]);
                Assert.IsFalse(owner.StopRequested);
            }
            finally
            {
                BridgeEditorPreviewRetention.RestoreCallbacks(callbacks);
                typeof(QuiltRenderer).GetField("editorPreviewRetentionRequired", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, false);
                typeof(QuiltRenderer).GetField("editorPreviewRetentionOwner", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, null);
                typeof(QuiltRenderer).GetField("quiltTexture", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(renderer, null);
                if (texture != null)
                {
                    texture.Release();
                    Object.DestroyImmediate(texture);
                }
                Object.DestroyImmediate(owner);
                Object.DestroyImmediate(producer);
            }
        }

        sealed class TrackingMsaaProvider : IQuiltMsaaProvider
        {
            internal bool Called;
            internal Camera Camera;

            public int GetMsaaSampleCount(Camera camera)
            {
                Called = true;
                Camera = camera;
                return 4;
            }
        }

        [Test]
        public void BoundedQuitWaitPumpsUntilNativeAndGpuProofAreBothComplete()
        {
            long ticks = 0;
            int requestCount = 0;
            int pumpCount = 0;
            int sleepCount = 0;
            bool nativeComplete = false;
            bool gpuProofComplete = false;
            var messages = new List<string>();

            bool allowed = BridgeWorkerQuitWait.WaitForFinalization(
                () => { requestCount++; ticks++; return false; },
                () =>
                {
                    pumpCount++;
                    ticks++;
                    if (pumpCount == 2) nativeComplete = true;
                    if (pumpCount == 4) gpuProofComplete = true;
                },
                () => nativeComplete && gpuProofComplete,
                () => ticks,
                1000,
                milliseconds => { sleepCount++; ticks += milliseconds; },
                messages.Add,
                1.0);

            Assert.IsTrue(allowed);
            Assert.AreEqual(1, requestCount);
            Assert.AreEqual(4, pumpCount);
            Assert.AreEqual(3, sleepCount);
            Assert.IsEmpty(messages);
        }

        [Test]
        public void BoundedQuitWaitDeadlineIncludesTheInitialRequest()
        {
            long ticks = 0;
            int pumpCount = 0;
            var messages = new List<string>();

            bool allowed = BridgeWorkerQuitWait.WaitForFinalization(
                () => { ticks += 5; return true; },
                () => pumpCount++,
                () => true,
                () => ticks,
                100,
                milliseconds => ticks += milliseconds,
                messages.Add,
                0.05);

            Assert.IsFalse(allowed);
            Assert.AreEqual(0, pumpCount);
            Assert.AreEqual(1, messages.Count);
            Assert.IsTrue(messages[0].Contains("quit was canceled"));
        }

        [Test]
        public void BoundedQuitWaitCancelsAtDeadlineWhileResourcesRemainUnproven()
        {
            long ticks = 0;
            int pumpCount = 0;
            var messages = new List<string>();

            bool allowed = BridgeWorkerQuitWait.WaitForFinalization(
                () => false,
                () => { pumpCount++; ticks += 20; },
                () => false,
                () => ticks,
                1000,
                milliseconds => ticks += milliseconds,
                messages.Add,
                0.01);

            Assert.IsFalse(allowed);
            Assert.AreEqual(1, pumpCount);
            Assert.AreEqual(1, messages.Count);
            Assert.IsTrue(messages[0].Contains("resources remain retained"));
        }

        [Test]
        public void BoundedQuitWaitCancelsAndSanitizesPumpExceptions()
        {
            long ticks = 0;
            var messages = new List<string>();

            bool allowed = BridgeWorkerQuitWait.WaitForFinalization(
                () => false,
                () => { throw new System.InvalidOperationException("GPU wait failed 東京"); },
                () => false,
                () => ticks++,
                1000,
                milliseconds => { },
                messages.Add,
                1.0);

            Assert.IsFalse(allowed);
            Assert.AreEqual(1, messages.Count);
            Assert.IsTrue(messages[0].Contains("GPU wait failed"));
            foreach (char character in messages[0]) Assert.LessOrEqual((int)character, 0x7e);
        }

        [Test]
        public void EditorGpuFlushSubmitsOnlyOnceInsideSynchronousWait()
        {
            bool attempted = false;
            int submissions = 0;
            int attemptsBefore = BridgeWorkerEditorGpuFlush.FlushAttempts;
            int successesBefore = BridgeWorkerEditorGpuFlush.SuccessfulFlushes;

            Assert.IsFalse(BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref attempted, () => submissions++));
            Assert.IsFalse(attempted);
            BridgeWorkerEditorGpuFlush.BeginSynchronousWait();
            try
            {
                Assert.IsTrue(BridgeWorkerEditorGpuFlush.SynchronousWaitActive);
                Assert.IsTrue(BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref attempted, () => submissions++));
                Assert.IsTrue(attempted);
                Assert.IsFalse(BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref attempted, () => submissions++));
                Assert.AreEqual(1, submissions);
                Assert.AreEqual(attemptsBefore + 1, BridgeWorkerEditorGpuFlush.FlushAttempts);
                Assert.AreEqual(successesBefore + 1, BridgeWorkerEditorGpuFlush.SuccessfulFlushes);
            }
            finally { BridgeWorkerEditorGpuFlush.EndSynchronousWait(); }

            Assert.IsFalse(BridgeWorkerEditorGpuFlush.SynchronousWaitActive);
        }

        [Test]
        public void EditorGpuFlushFailureMarksProofAttemptAndDoesNotRetry()
        {
            bool attempted = false;
            int attemptsBefore = BridgeWorkerEditorGpuFlush.FlushAttempts;
            int successesBefore = BridgeWorkerEditorGpuFlush.SuccessfulFlushes;
            BridgeWorkerEditorGpuFlush.BeginSynchronousWait();
            try
            {
                Assert.Throws<System.InvalidOperationException>(() =>
                    BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref attempted,
                        () => throw new System.InvalidOperationException("flush failed")));
                Assert.IsTrue(attempted);
                Assert.IsFalse(BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref attempted, () => { }));
                Assert.AreEqual(attemptsBefore + 1, BridgeWorkerEditorGpuFlush.FlushAttempts);
                Assert.AreEqual(successesBefore, BridgeWorkerEditorGpuFlush.SuccessfulFlushes);
            }
            finally { BridgeWorkerEditorGpuFlush.EndSynchronousWait(); }
        }

        [Test]
        public void ReadbackCompletionRemainsLatchedAfterTheRequestWouldExpire()
        {
            object attempt = new object();
            var latch = new BridgeEditorPreviewRetention.ReadbackCompletionLatch(attempt, 1);

            Assert.IsTrue(latch.TryRecord(attempt, 0, true));
            Assert.IsFalse(latch.TryRecord(attempt, 0, false));
            Assert.AreEqual(BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome.Succeeded, latch.GetOutcome(0));
            Assert.IsTrue(latch.AllComplete);
            Assert.IsTrue(latch.AllSucceeded);
        }

        [Test]
        public void ReadbackCompletionLatchesOutOfOrderRequestsIndependently()
        {
            object attempt = new object();
            var latch = new BridgeEditorPreviewRetention.ReadbackCompletionLatch(attempt, 3);

            Assert.IsTrue(latch.TryRecord(attempt, 2, true));
            Assert.IsTrue(latch.TryRecord(attempt, 0, true));
            Assert.IsFalse(latch.AllComplete);
            Assert.IsTrue(latch.TryRecord(attempt, 1, true));
            Assert.IsTrue(latch.AllComplete);
            Assert.IsTrue(latch.AllSucceeded);
        }

        [Test]
        public void FailedReadbackCannotBeUpgradedToSuccess()
        {
            object attempt = new object();
            var latch = new BridgeEditorPreviewRetention.ReadbackCompletionLatch(attempt, 1);

            Assert.IsTrue(latch.TryRecord(attempt, 0, false));
            Assert.IsFalse(latch.TryRecord(attempt, 0, true));
            Assert.IsTrue(latch.AllComplete);
            Assert.IsFalse(latch.AllSucceeded);
        }

        [Test]
        public void ReadbackCallbackFromAnOlderAttemptCannotCompleteANewerAttempt()
        {
            object olderAttempt = new object();
            object currentAttempt = new object();
            var latch = new BridgeEditorPreviewRetention.ReadbackCompletionLatch(currentAttempt, 1);

            Assert.IsFalse(latch.TryRecord(olderAttempt, 0, true));
            Assert.AreEqual(BridgeEditorPreviewRetention.ReadbackCompletionLatch.Outcome.Pending, latch.GetOutcome(0));
            Assert.IsTrue(latch.TryRecord(currentAttempt, 0, true));
            Assert.IsTrue(latch.AllSucceeded);
        }
    }
}
