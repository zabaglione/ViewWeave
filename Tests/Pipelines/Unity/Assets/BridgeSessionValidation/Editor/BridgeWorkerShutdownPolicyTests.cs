using System.Collections;
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
        const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

        sealed class NativeShutdownTestState : System.IDisposable
        {
            static readonly string[] FieldNames =
            {
                "requestAttempted", "nativeShutdownRequested", "nativeCloseAllAttempted", "finalStatusComplete",
                "finalizationReady", "timedOut", "warningWritten", "deadline", "nativeStatusCompleteOverrideForTests",
                "nativeValidationPendingOverrideForTests", "inspectEditorProcessOverrideForTests", "nativeValidationOverrideForTests",
                "closeAllNativeWorkersOverrideForTests", "requestFinalShutdownOverrideForTests", "nativeStatusReadOverrideForTests"
            };

            readonly FieldInfo[] fields = new FieldInfo[FieldNames.Length];
            readonly object[] values = new object[FieldNames.Length];

            internal NativeShutdownTestState()
            {
                for (int i = 0; i < FieldNames.Length; i++)
                {
                    fields[i] = typeof(BridgeWorkerShutdownCoordinator).GetField(FieldNames[i], StaticPrivate);
                    Assert.IsNotNull(fields[i], "Missing shutdown test state field " + FieldNames[i]);
                    values[i] = fields[i].GetValue(null);
                }
            }

            internal void Set(string fieldName, object value)
            {
                for (int i = 0; i < FieldNames.Length; i++)
                    if (FieldNames[i] == fieldName)
                    {
                        fields[i].SetValue(null, value);
                        return;
                    }
                Assert.Fail("Unknown shutdown test state field " + fieldName);
            }

            public void Dispose()
            {
                for (int i = 0; i < fields.Length; i++) fields[i].SetValue(null, values[i]);
            }
        }

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
        public void PhysicalCalibrationConvertsDpiAndRejectsUnknownOrMismatchedGeometry()
        {
            var display = new BridgeNative.DisplayInfo
            {
                index = 0,
                width = 1920,
                height = 1080,
                aspect = 16f / 9f,
                viewcone = 40f,
                quiltWidth = 4096,
                quiltHeight = 4096,
                quiltColumns = 8,
                quiltRows = 6
            };
            var calibration = new BridgeWorkerNative.PhysicalDisplayInfoV1
            {
                pixelWidth = 1920,
                pixelHeight = 1080,
                dpi = 100f,
                valid = 1
            };
            BridgeDisplayProfile profile = new BridgeDisplayProfile(display, calibration);
            Assert.IsTrue(profile.HasPhysicalCalibration);
            Assert.AreEqual(487.68f, profile.PhysicalWidthMillimeters, 0.01f);
            Assert.AreEqual(274.32f, profile.PhysicalHeightMillimeters, 0.01f);
            Assert.IsTrue(profile.MatchesPhysicalAspect(160f, 90f));
            Assert.IsFalse(profile.MatchesPhysicalAspect(150f, 100f));

            calibration.pixelWidth = 1080;
            calibration.pixelHeight = 1920;
            BridgeDisplayProfile rotated = new BridgeDisplayProfile(display, calibration);
            Assert.IsTrue(rotated.HasPhysicalCalibration);
            Assert.AreEqual(487.68f, rotated.PhysicalWidthMillimeters, 0.01f);
            Assert.AreEqual(274.32f, rotated.PhysicalHeightMillimeters, 0.01f);

            calibration.dpi = 0f;
            Assert.IsFalse(new BridgeDisplayProfile(display, calibration).HasPhysicalCalibration);
            Assert.IsFalse(new BridgeDisplayProfile(display).HasPhysicalCalibration);
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
        public void NativeShutdownRetriesPendingValidationThenSubmitsOnce()
        {
            using (var state = new NativeShutdownTestState())
            {
                bool validationPending = true;
                int inspections = 0;
                int closeAllCalls = 0;
                int statusReads = 0;
                int requests = 0;
                state.Set("requestAttempted", true);
                state.Set("nativeShutdownRequested", false);
                state.Set("nativeCloseAllAttempted", false);
                state.Set("finalStatusComplete", false);
                state.Set("finalizationReady", false);
                state.Set("nativeValidationPendingOverrideForTests", (System.Func<bool>)(() => validationPending));
                state.Set("inspectEditorProcessOverrideForTests", (System.Func<bool>)(() =>
                {
                    inspections++;
                    if (inspections == 1) return false;
                    validationPending = false;
                    return true;
                }));
                state.Set("nativeValidationOverrideForTests", (System.Func<BridgeWorkerShutdownCoordinator.NativeValidationResultForTests>)(() =>
                    new BridgeWorkerShutdownCoordinator.NativeValidationResultForTests { CallsAllowed = true, ModuleLoaded = true }));
                state.Set("closeAllNativeWorkersOverrideForTests", (System.Func<int>)(() => { closeAllCalls++; return 0; }));
                state.Set("nativeStatusReadOverrideForTests", (System.Func<BridgeWorkerShutdownCoordinator.NativeStatusReadResultForTests>)(() =>
                {
                    statusReads++;
                    return new BridgeWorkerShutdownCoordinator.NativeStatusReadResultForTests
                    {
                        Succeeded = true,
                        Status = new BridgeWorkerNative.FinalShutdownStatusV1 { structSize = 32, version = 1 }
                    };
                }));
                state.Set("requestFinalShutdownOverrideForTests", (System.Func<int>)(() => { requests++; return 0; }));

                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();
                Assert.AreEqual(0, closeAllCalls, "A pending identity check must not invoke native close-all.");
                Assert.AreEqual(0, requests, "A pending identity check must not submit the native shutdown request.");
                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();
                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();

                Assert.AreEqual(2, inspections);
                Assert.AreEqual(1, closeAllCalls);
                Assert.AreEqual(1, statusReads);
                Assert.AreEqual(1, requests);
                Assert.IsTrue((bool)typeof(BridgeWorkerShutdownCoordinator).GetField("nativeShutdownRequested", StaticPrivate).GetValue(null));
            }
        }

        [Test]
        public void NativeShutdownRequestErrorIsRetriedUntilAccepted()
        {
            using (var state = new NativeShutdownTestState())
            {
                int requests = 0;
                int statusReads = 0;
                state.Set("requestAttempted", true);
                state.Set("nativeShutdownRequested", false);
                state.Set("nativeCloseAllAttempted", false);
                state.Set("finalStatusComplete", false);
                state.Set("nativeValidationPendingOverrideForTests", (System.Func<bool>)(() => false));
                state.Set("nativeValidationOverrideForTests", (System.Func<BridgeWorkerShutdownCoordinator.NativeValidationResultForTests>)(() =>
                    new BridgeWorkerShutdownCoordinator.NativeValidationResultForTests { CallsAllowed = true, ModuleLoaded = true }));
                state.Set("closeAllNativeWorkersOverrideForTests", (System.Func<int>)(() => 0));
                state.Set("nativeStatusReadOverrideForTests", (System.Func<BridgeWorkerShutdownCoordinator.NativeStatusReadResultForTests>)(() =>
                {
                    statusReads++;
                    return new BridgeWorkerShutdownCoordinator.NativeStatusReadResultForTests
                    {
                        Succeeded = statusReads > 1,
                        Status = new BridgeWorkerNative.FinalShutdownStatusV1 { structSize = 32, version = 1 }
                    };
                }));
                state.Set("requestFinalShutdownOverrideForTests", (System.Func<int>)(() => ++requests == 1 ? -7 : 0));

                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();
                Assert.AreEqual(1, requests);
                Assert.AreEqual(1, statusReads);
                Assert.IsFalse((bool)typeof(BridgeWorkerShutdownCoordinator).GetField("nativeShutdownRequested", StaticPrivate).GetValue(null));

                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();

                Assert.AreEqual(2, requests);
                Assert.AreEqual(2, statusReads);
                Assert.IsTrue((bool)typeof(BridgeWorkerShutdownCoordinator).GetField("nativeShutdownRequested", StaticPrivate).GetValue(null));
            }
        }

        [Test]
        public void NativeShutdownIdentityBlockKeepsRequestUnsent()
        {
            using (var state = new NativeShutdownTestState())
            {
                int inspections = 0;
                int closeAllCalls = 0;
                int requests = 0;
                state.Set("requestAttempted", true);
                state.Set("nativeShutdownRequested", false);
                state.Set("nativeCloseAllAttempted", false);
                state.Set("finalStatusComplete", false);
                state.Set("nativeValidationPendingOverrideForTests", (System.Func<bool>)(() => true));
                state.Set("inspectEditorProcessOverrideForTests", (System.Func<bool>)(() => { inspections++; return false; }));
                state.Set("nativeValidationOverrideForTests", (System.Func<BridgeWorkerShutdownCoordinator.NativeValidationResultForTests>)(() =>
                    new BridgeWorkerShutdownCoordinator.NativeValidationResultForTests { CallsAllowed = true, ModuleLoaded = true }));
                state.Set("closeAllNativeWorkersOverrideForTests", (System.Func<int>)(() => { closeAllCalls++; return 0; }));
                state.Set("requestFinalShutdownOverrideForTests", (System.Func<int>)(() => { requests++; return 0; }));

                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();
                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();

                Assert.AreEqual(2, inspections);
                Assert.AreEqual(0, closeAllCalls);
                Assert.AreEqual(0, requests);
                Assert.IsFalse((bool)typeof(BridgeWorkerShutdownCoordinator).GetField("nativeShutdownRequested", StaticPrivate).GetValue(null));
            }
        }

        [Test]
        public void VerifiedUnloadedNativeModuleAllowsQuitWithoutManagedResources()
        {
            Assert.AreEqual(0, BridgeWorkerReaper.PendingCount);
            Assert.IsFalse(BridgeWorkerReaper.HasUnprovenResources);
            Assert.IsFalse(BridgeWorkerQuiltOutput.HasNativeHandle);
            Assert.IsFalse(BridgeEditorPreviewRetention.HasPending);
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.IsTrue(BridgeQuiltPlaybackLease.FinalQuitCleanupComplete);
            Assert.IsFalse(BridgeWorkerLifecycle.FinalShutdownPending);

            using (var state = new NativeShutdownTestState())
            {
                int closeAllCalls = 0;
                int requests = 0;
                state.Set("requestAttempted", false);
                state.Set("nativeShutdownRequested", false);
                state.Set("nativeCloseAllAttempted", false);
                state.Set("finalStatusComplete", false);
                state.Set("finalizationReady", false);
                state.Set("nativeValidationPendingOverrideForTests", (System.Func<bool>)(() => false));
                state.Set("nativeValidationOverrideForTests", (System.Func<BridgeWorkerShutdownCoordinator.NativeValidationResultForTests>)(() =>
                    new BridgeWorkerShutdownCoordinator.NativeValidationResultForTests { CallsAllowed = true, ModuleLoaded = false }));
                state.Set("closeAllNativeWorkersOverrideForTests", (System.Func<int>)(() => { closeAllCalls++; return 0; }));
                state.Set("requestFinalShutdownOverrideForTests", (System.Func<int>)(() => { requests++; return 0; }));

                Assert.IsTrue(BridgeWorkerShutdownCoordinator.RequestQuit());

                Assert.AreEqual(0, closeAllCalls);
                Assert.AreEqual(0, requests);
                Assert.IsFalse((bool)typeof(BridgeWorkerShutdownCoordinator).GetField("nativeShutdownRequested", StaticPrivate).GetValue(null));

                state.Set("requestAttempted", true);
                BridgeWorkerShutdownCoordinator.PumpNativeShutdownRequestForTests();
                Assert.IsTrue((bool)typeof(BridgeWorkerShutdownCoordinator).GetField("finalStatusComplete", StaticPrivate).GetValue(null),
                    "A validated unloaded module with no retained resources should complete without calling native shutdown.");
                Assert.AreEqual(0, closeAllCalls);
                Assert.AreEqual(0, requests);
            }
        }

        [Test]
        public void PreviewLeaseNativeLayoutsMatchVersionedAbi()
        {
            Assert.AreEqual(24, System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.PreviewLeaseIdentityV1)));
            Assert.AreEqual(312, System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.Status)));
            Assert.AreEqual(352, System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.PreviewLeaseStatusV1)));
        }

        [Test]
        public void PreviewLeaseRecoveryRequiresReturnProofGpuProofAndAcknowledgement()
        {
            var texture = new RenderTexture(8, 8, 0);
            var identity = new BridgeWorkerNative.PreviewLeaseIdentityV1
            {
                structSize = 24,
                version = 1,
                endpointId = 41,
                leaseGeneration = 7
            };
            BridgeWorkerRecoveryRecord record = BridgeWorkerRecoveryRecord.CreatePreviewLease(
                new System.IntPtr(9), texture, true, 13, default, identity);
            try
            {
                record.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                record.PreviewLeaseReturnProven = true;
                Assert.IsFalse(record.CanRelease, "Native return proof cannot replace transfer-texture GPU proof.");
                record.SetGpuProof(false, true, true, true);
                Assert.IsFalse(record.CanRelease, "NativeReleased cannot substitute for a lease acknowledgement.");
                record.NativeReleased = true;
                Assert.IsFalse(record.CanRelease);
                record.PreviewLeaseAcknowledged = true;
                record.PreviewLeaseReturnProven = true;
                Assert.IsTrue(record.CanRelease);

                BridgeWorkerRecoveryRecord acknowledgementOnly = BridgeWorkerRecoveryRecord.CreatePreviewLease(
                    new System.IntPtr(10), null, false, 14, default, identity);
                try
                {
                    acknowledgementOnly.PreviewLeaseAcknowledged = true;
                    Assert.IsFalse(acknowledgementOnly.CanRelease, "Lease acknowledgement cannot substitute for persisted Native return proof.");
                    acknowledgementOnly.NativeReleased = true;
                    Assert.IsFalse(acknowledgementOnly.CanRelease, "Legacy NativeReleased cannot substitute for matching final-module teardown proof.");
                    acknowledgementOnly.FinalModuleTeardownProven = true;
                    Assert.IsTrue(acknowledgementOnly.CanRelease);
                }
                finally { Object.DestroyImmediate(acknowledgementOnly); }
            }
            finally
            {
                if (texture != null) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(record);
            }
        }

        [Test]
        public void PreviewLeaseStatusRejectsUnknownOrMismatchedIdentity()
        {
            var identity = new BridgeWorkerNative.PreviewLeaseIdentityV1
            {
                structSize = 24,
                version = 1,
                endpointId = 99,
                leaseGeneration = 5
            };
            var status = new BridgeWorkerNative.PreviewLeaseStatusV1
            {
                structSize = 352,
                version = 1,
                state = BridgeWorkerNative.PreviewLeaseStateReturnedProven,
                endpointId = identity.endpointId,
                leaseGeneration = identity.leaseGeneration,
                workerStatus = new BridgeWorkerNative.Status { state = 4 }
            };
            MethodInfo validate = typeof(BridgeWorkerReaper).GetMethod("IsValidLeaseStatus", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(validate);
            Assert.IsTrue((bool)validate.Invoke(null, new object[] { status, identity }));
            status.leaseGeneration++;
            Assert.IsFalse((bool)validate.Invoke(null, new object[] { status, identity }));
            status.leaseGeneration = identity.leaseGeneration;
            status.version = 0;
            Assert.IsFalse((bool)validate.Invoke(null, new object[] { status, identity }));
            status.version = 1;
            status.state = 4;
            Assert.IsFalse((bool)validate.Invoke(null, new object[] { status, identity }));
            status.state = BridgeWorkerNative.PreviewLeaseStateReturnedProven;
            status.lastResult = -4;
            Assert.IsFalse((bool)validate.Invoke(null, new object[] { status, identity }));
            status.lastResult = 0;
            status.workerStatus.state = 1;
            Assert.IsFalse((bool)validate.Invoke(null, new object[] { status, identity }));
        }

        [Test]
        public void PreviewLeaseRecoveryIdentityAndReturnIntentSurviveEditorSerialization()
        {
            var identity = new BridgeWorkerNative.PreviewLeaseIdentityV1
            {
                structSize = 24,
                version = 1,
                endpointId = 0x1020304050607080UL,
                leaseGeneration = 0x1122334455667788UL
            };
            BridgeWorkerRecoveryRecord original = BridgeWorkerRecoveryRecord.CreatePreviewLease(
                new System.IntPtr(53), null, false, 29, default, identity);
            BridgeWorkerRecoveryRecord restored = ScriptableObject.CreateInstance<BridgeWorkerRecoveryRecord>();
            try
            {
                original.PreviewLeaseReturnRequested = true;
                original.PreviewLeaseReturnProven = true;
                string serialized = UnityEditor.EditorJsonUtility.ToJson(original);
                UnityEditor.EditorJsonUtility.FromJsonOverwrite(serialized, restored);

                Assert.IsTrue(restored.IsPreviewLease);
                Assert.IsTrue(restored.HasValidPreviewLeaseIdentity);
                Assert.AreEqual(identity.structSize, restored.PreviewLeaseIdentity.structSize);
                Assert.AreEqual(identity.version, restored.PreviewLeaseIdentity.version);
                Assert.AreEqual(identity.endpointId, restored.PreviewLeaseIdentity.endpointId);
                Assert.AreEqual(identity.leaseGeneration, restored.PreviewLeaseIdentity.leaseGeneration);
                Assert.IsTrue(restored.PreviewLeaseReturnRequested);
                Assert.IsTrue(restored.PreviewLeaseReturnProven);
                Assert.IsFalse(restored.PreviewLeaseAcknowledged);
                Assert.IsFalse(restored.FinalModuleTeardownProven);
                Assert.IsFalse(restored.CanRelease, "A restored return request is not Native ACK or final-module proof.");
            }
            finally
            {
                if (original != null) Object.DestroyImmediate(original);
                if (restored != null) Object.DestroyImmediate(restored);
            }
        }

        [Test]
        public void CancellingBusyPreviewLeaseAcquireClearsOnlyLocalRetryState()
        {
            var host = new GameObject("Preview lease acquire cancellation test");
            host.SetActive(false);
            BridgeWorkerQuiltOutput output = host.AddComponent<BridgeWorkerQuiltOutput>();
            try
            {
                SetPrivateField(output, "previewLease", true);
                SetPrivateField(output, "previewLeaseAcquirePending", true);
                SetPrivateField(output, "state", BridgeWorkerQuiltOutput.WorkerState.Connecting);

                MethodInfo dispose = typeof(BridgeWorkerQuiltOutput).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public);
                Assert.IsNotNull(dispose);
                dispose.Invoke(output, null);

                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, output.State);
                Assert.AreEqual(System.IntPtr.Zero, output.NativeHandle);
                Assert.IsFalse((bool)GetPrivateField(output, "previewLeaseAcquirePending"));
                Assert.IsFalse((bool)GetPrivateField(output, "previewLease"));
            }
            finally
            {
                if (host != null) Object.DestroyImmediate(host);
            }
        }

        static object GetPrivateField(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            return field.GetValue(target);
        }

        static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(target, value);
        }

        [Test]
        public void EditorPreviewCoreResourcesRetireBeforeLeaseAcknowledgementCompletesRecovery()
        {
            var producer = new GameObject("Preview lease barrier producer");
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producer, null);
            var texture = new RenderTexture(8, 8, 0);
            var identity = new BridgeWorkerNative.PreviewLeaseIdentityV1
            {
                structSize = 24,
                version = 1,
                endpointId = 23,
                leaseGeneration = 11
            };
            BridgeWorkerRecoveryRecord recovery = BridgeWorkerRecoveryRecord.CreatePreviewLease(
                new System.IntPtr(31), null, false, 17, default, identity);
            try
            {
                Assert.IsTrue(texture.Create());
                owner.SetWorkerProofExpected(true);
                Assert.IsTrue(recovery.AttachEditorPreviewOwner(owner));
                owner.MarkStopRequested();
                Assert.IsTrue(owner.SetTextures(new[] { texture }));
                owner.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                owner.SetGpuProof(false, true, true);
                Assert.IsTrue(recovery.NotifyEditorPreviewOwnerReturnProven());
                Assert.IsTrue(owner.CanReleaseCoreResources);
                Assert.IsFalse(owner.CanRelease, "The retention record must stay alive until lease acknowledgement.");

                owner.ReleaseCoreResourcesAfterProof();
                Assert.IsTrue(owner.CoreResourcesReleased);
                Assert.IsFalse(owner.CanRelease);
                Assert.IsTrue(recovery.NotifyEditorPreviewOwnerComplete());
                Assert.IsTrue(owner.CanRelease);
            }
            finally
            {
                if (owner != null) Object.DestroyImmediate(owner);
                if (recovery != null) Object.DestroyImmediate(recovery);
                if (texture != null) Object.DestroyImmediate(texture);
                if (producer != null) Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void PreviewLeaseTransferTextureRetiresOnlyAfterNativeGpuAndCoreProof()
        {
            var producer = new GameObject("Preview lease transfer retirement producer");
            BridgeEditorPreviewRetentionRecord owner = BridgeEditorPreviewRetentionRecord.Create(producer, null);
            var transfer = new RenderTexture(8, 8, 0);
            var identity = new BridgeWorkerNative.PreviewLeaseIdentityV1
            {
                structSize = 24,
                version = 1,
                endpointId = 29,
                leaseGeneration = 19
            };
            BridgeWorkerRecoveryRecord recovery = BridgeWorkerRecoveryRecord.CreatePreviewLease(
                new System.IntPtr(37), transfer, false, 23, default, identity);
            try
            {
                Assert.IsTrue(transfer.Create());
                owner.SetWorkerProofExpected(true);
                Assert.IsTrue(recovery.AttachEditorPreviewOwner(owner));
                owner.MarkStopRequested();
                Assert.IsTrue(owner.SetTextures(System.Array.Empty<RenderTexture>()));
                recovery.SetGpuProofMode(BridgeGpuProofMode.ReadbackTail);
                recovery.PreviewLeaseReturnProven = true;

                Assert.IsFalse(recovery.RetirePreviewLeaseTransferTextureAfterProof(),
                    "A Native return proof alone cannot retire the transfer texture.");
                Assert.AreSame(transfer, recovery.TransferTexture);
                recovery.SetGpuProof(false, true, true, true);
                Assert.IsFalse(recovery.RetirePreviewLeaseTransferTextureAfterProof(),
                    "The transfer texture remains until the Editor Core resource owner confirms retirement.");
                Assert.AreSame(transfer, recovery.TransferTexture);

                Assert.IsTrue(recovery.NotifyEditorPreviewOwnerReturnProven());
                owner.ReleaseCoreResourcesAfterProof();
                Assert.IsTrue(owner.CoreResourcesReleased);
                Assert.IsTrue(recovery.RetirePreviewLeaseTransferTextureAfterProof());
                Assert.IsTrue(recovery.HadTransferTexture, "The original transfer-resource proof requirement must remain serialized.");
                Assert.IsTrue(recovery.HasGpuReleaseProof, "Retiring the texture must preserve its completed GPU proof.");
                Assert.IsTrue(recovery.TransferTexture == null);
                Assert.IsFalse(recovery.CanRelease, "Resource retirement is not Native lease acknowledgement.");

                recovery.PreviewLeaseAcknowledged = true;
                Assert.IsTrue(recovery.CanRelease);
                recovery.ReleaseProvenResources();
                Assert.AreEqual(System.IntPtr.Zero, recovery.GetHandle());
            }
            finally
            {
                if (owner != null) Object.DestroyImmediate(owner);
                if (recovery != null) Object.DestroyImmediate(recovery);
                if (transfer != null) Object.DestroyImmediate(transfer);
                if (producer != null) Object.DestroyImmediate(producer);
            }
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
        public void CoreStopRequestInvokesGlobalStopOwnerOnceForActiveRecord()
        {
            var producer = new GameObject("Preview Core stop callback producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord record = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            BridgeEditorPreviewRetention.CallbackRegistration callbacks = BridgeEditorPreviewRetention.CaptureCallbacks();
            int stopCalls = 0;
            try
            {
                BridgeEditorPreviewRetention.RegisterEditorCallbacks(() => { stopCalls++; return false; }, _ => { });
                Assert.IsFalse(BridgeEditorPreviewRetention.RequestStopFromCore(record, renderer));
                Assert.AreEqual(1, stopCalls);
                Assert.IsFalse(record.StopRequested);
            }
            finally
            {
                BridgeEditorPreviewRetention.RestoreCallbacks(callbacks);
                Object.DestroyImmediate(record);
                Object.DestroyImmediate(producer);
            }
        }

        [Test]
        public void CoreStopRetryForStoppedRecordDoesNotStopAnotherOwner()
        {
            var firstProducer = new GameObject("Preview first Core stop producer");
            var secondProducer = new GameObject("Preview second Core stop producer");
            QuiltRenderer firstRenderer = firstProducer.AddComponent<QuiltRenderer>();
            QuiltRenderer secondRenderer = secondProducer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord firstRecord = BridgeEditorPreviewRetentionRecord.Create(firstProducer, firstRenderer);
            BridgeEditorPreviewRetentionRecord secondRecord = BridgeEditorPreviewRetentionRecord.Create(secondProducer, secondRenderer);
            BridgeEditorPreviewRetention.CallbackRegistration callbacks = BridgeEditorPreviewRetention.CaptureCallbacks();
            int stopCalls = 0;
            try
            {
                firstRecord.MarkStopRequested();
                BridgeEditorPreviewRetention.RegisterEditorCallbacks(() =>
                {
                    stopCalls++;
                    secondRecord.MarkStopRequested();
                    return false;
                }, _ => { });

                Assert.IsFalse(BridgeEditorPreviewRetention.RequestStopFromCore(firstRecord, firstRenderer));
                Assert.AreEqual(0, stopCalls);
                Assert.IsFalse(secondRecord.StopRequested);
            }
            finally
            {
                BridgeEditorPreviewRetention.RestoreCallbacks(callbacks);
                Object.DestroyImmediate(firstRecord);
                Object.DestroyImmediate(secondRecord);
                Object.DestroyImmediate(firstProducer);
                Object.DestroyImmediate(secondProducer);
            }
        }

        [Test]
        public void CoreStopRequestRejectsMismatchedRendererWithoutGlobalStop()
        {
            var producer = new GameObject("Preview Core stop renderer producer");
            var otherProducer = new GameObject("Preview Core stop other producer");
            QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
            QuiltRenderer otherRenderer = otherProducer.AddComponent<QuiltRenderer>();
            BridgeEditorPreviewRetentionRecord record = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
            BridgeEditorPreviewRetention.CallbackRegistration callbacks = BridgeEditorPreviewRetention.CaptureCallbacks();
            int stopCalls = 0;
            try
            {
                BridgeEditorPreviewRetention.RegisterEditorCallbacks(() => { stopCalls++; return false; }, _ => { });
                Assert.IsFalse(BridgeEditorPreviewRetention.RequestStopFromCore(record, otherRenderer));
                Assert.AreEqual(0, stopCalls);
                Assert.IsFalse(record.StopRequested);
            }
            finally
            {
                BridgeEditorPreviewRetention.RestoreCallbacks(callbacks);
                Object.DestroyImmediate(record);
                Object.DestroyImmediate(producer);
                Object.DestroyImmediate(otherProducer);
            }
        }

        [Test]
        public void ExplicitGlobalPreviewStopStillInvokesRegisteredStopOwner()
        {
            BridgeEditorPreviewRetention.CallbackRegistration callbacks = BridgeEditorPreviewRetention.CaptureCallbacks();
            int stopCalls = 0;
            try
            {
                BridgeEditorPreviewRetention.RegisterEditorCallbacks(() => { stopCalls++; return false; }, _ => { });
                Assert.IsTrue(BridgeEditorPreviewRetention.RequestStop(false));
                Assert.AreEqual(1, stopCalls);
            }
            finally { BridgeEditorPreviewRetention.RestoreCallbacks(callbacks); }
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
                Assert.IsTrue(owner.CanReleaseCoreResources);
                Assert.IsFalse(owner.CanRelease);

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

        [UnityTest]
        public IEnumerator TimedOutShutdownRecoversAfterNativeAndPreviewGpuProofComplete()
        {
            Assert.AreEqual(0, BridgeEditorPreviewRetention.PendingCount,
                "This test must not pump or mutate another Editor preview's retention state.");
            Assert.AreEqual(0, BridgeWorkerReaper.PendingCount,
                "This test must not pump or mutate another worker's recovery state.");
            Assert.IsFalse(BridgeWorkerReaper.HasUnprovenResources);
            Assert.IsEmpty(Resources.FindObjectsOfTypeAll<BridgeWorkerRecoveryRecord>(),
                "This test must not override native status while another worker recovery record exists.");
            Assert.IsEmpty(Resources.FindObjectsOfTypeAll<BridgeWorkerQuiltOutput>(),
                "This test requires an isolated worker output state.");
            Assert.IsFalse(BridgeWorkerQuiltOutput.HasNativeHandle);
            Assert.AreEqual(0, BridgeWorkerQuiltOutput.ShutdownPendingCount);
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy,
                "This test must not pump display discovery while it is active.");

            FieldInfo requestAttemptedField = typeof(BridgeWorkerShutdownCoordinator).GetField("requestAttempted", StaticPrivate);
            FieldInfo nativeShutdownRequestedField = typeof(BridgeWorkerShutdownCoordinator).GetField("nativeShutdownRequested", StaticPrivate);
            FieldInfo nativeCloseAllAttemptedField = typeof(BridgeWorkerShutdownCoordinator).GetField("nativeCloseAllAttempted", StaticPrivate);
            FieldInfo finalStatusCompleteField = typeof(BridgeWorkerShutdownCoordinator).GetField("finalStatusComplete", StaticPrivate);
            FieldInfo finalizationReadyField = typeof(BridgeWorkerShutdownCoordinator).GetField("finalizationReady", StaticPrivate);
            FieldInfo timedOutField = typeof(BridgeWorkerShutdownCoordinator).GetField("timedOut", StaticPrivate);
            FieldInfo warningWrittenField = typeof(BridgeWorkerShutdownCoordinator).GetField("warningWritten", StaticPrivate);
            FieldInfo deadlineField = typeof(BridgeWorkerShutdownCoordinator).GetField("deadline", StaticPrivate);
            FieldInfo recoveryPendingField = typeof(BridgeWorkerLifecycle).GetField("recoveryPending", StaticPrivate);
            FieldInfo lifecycleFinalPendingField = typeof(BridgeWorkerLifecycle).GetField("finalShutdownPending", StaticPrivate);
            FieldInfo finalShutdownRequestedField = typeof(BridgeWorkerFinalShutdownRecord)
                .GetField("finalShutdownRequested", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(requestAttemptedField);
            Assert.IsNotNull(nativeShutdownRequestedField);
            Assert.IsNotNull(nativeCloseAllAttemptedField);
            Assert.IsNotNull(finalStatusCompleteField);
            Assert.IsNotNull(finalizationReadyField);
            Assert.IsNotNull(timedOutField);
            Assert.IsNotNull(warningWrittenField);
            Assert.IsNotNull(deadlineField);
            Assert.IsNotNull(recoveryPendingField);
            Assert.IsNotNull(lifecycleFinalPendingField);
            Assert.IsNotNull(finalShutdownRequestedField);

            object[] priorCoordinatorState =
            {
                requestAttemptedField.GetValue(null), nativeShutdownRequestedField.GetValue(null), nativeCloseAllAttemptedField.GetValue(null),
                finalStatusCompleteField.GetValue(null), finalizationReadyField.GetValue(null),
                timedOutField.GetValue(null), warningWrittenField.GetValue(null), deadlineField.GetValue(null),
                BridgeWorkerShutdownCoordinator.nativeStatusCompleteOverrideForTests
            };
            bool priorRecoveryPending = (bool)recoveryPendingField.GetValue(null);
            bool priorLifecycleFinalPending = (bool)lifecycleFinalPendingField.GetValue(null);
            BridgeWorkerFinalShutdownRecord[] existingShutdownRecords = Resources.FindObjectsOfTypeAll<BridgeWorkerFinalShutdownRecord>();
            var existingRecordStates = new bool[existingShutdownRecords.Length];
            for (int i = 0; i < existingShutdownRecords.Length; i++)
                existingRecordStates[i] = (bool)finalShutdownRequestedField.GetValue(existingShutdownRecords[i]);

            GameObject producer = null;
            BridgeEditorPreviewRetentionRecord retention = null;
            RenderTexture texture = null;
            int nativePolls = 0;
            int flushesBefore = BridgeWorkerEditorGpuFlush.FlushAttempts;
            bool synchronousWaitStarted = false;
            try
            {
                producer = new GameObject("Shutdown recovery preview producer");
                QuiltRenderer renderer = producer.AddComponent<QuiltRenderer>();
                retention = BridgeEditorPreviewRetentionRecord.Create(producer, renderer);
                texture = new RenderTexture(8, 8, 0);
                retention.MarkStopRequested();
                Assert.IsTrue(retention.SetTextures(new[] { texture }));

                requestAttemptedField.SetValue(null, true);
                nativeShutdownRequestedField.SetValue(null, true);
                nativeCloseAllAttemptedField.SetValue(null, true);
                finalStatusCompleteField.SetValue(null, false);
                finalizationReadyField.SetValue(null, false);
                timedOutField.SetValue(null, true);
                warningWrittenField.SetValue(null, true);
                deadlineField.SetValue(null, Time.realtimeSinceStartupAsDouble - 1.0);
                BridgeWorkerShutdownCoordinator.nativeStatusCompleteOverrideForTests = () => ++nativePolls >= 3;

                BridgeWorkerEditorGpuFlush.BeginSynchronousWait();
                synchronousWaitStarted = true;
                BridgeWorkerShutdownCoordinator.PumpOnce();

                Assert.GreaterOrEqual(nativePolls, 1, "Timed-out quit must keep polling native completion.");
                Assert.IsTrue(BridgeEditorPreviewRetention.HasPending, "An uncreated retained texture must continue to block finalization.");
                Assert.IsFalse(retention.GpuProofComplete);
                Assert.IsFalse(BridgeWorkerShutdownCoordinator.FinalizationReady);

                Assert.IsTrue(texture.Create());
                for (int frame = 0; frame < 300 && !BridgeWorkerShutdownCoordinator.FinalizationReady; frame++)
                {
                    BridgeWorkerShutdownCoordinator.PumpOnce();
                    if (!BridgeWorkerShutdownCoordinator.FinalizationReady) yield return null;
                }

                Assert.GreaterOrEqual(nativePolls, 3, "Late native completion should be observed after the deadline.");
                Assert.AreEqual(flushesBefore + 1, BridgeWorkerEditorGpuFlush.FlushAttempts,
                    "The synchronous quit wait must submit pending preview GPU work exactly once while polling asynchronously.");
                Assert.IsTrue(BridgeWorkerShutdownCoordinator.FinalizationReady,
                    "Shutdown should recover after both native shutdown and retained preview GPU proof complete.");
                Assert.IsFalse(BridgeEditorPreviewRetention.HasPending);
                Assert.IsTrue(texture == null || !texture.IsCreated(),
                    "The retained texture should only be released after successful GPU proof.");
            }
            finally
            {
                BridgeWorkerShutdownCoordinator.nativeStatusCompleteOverrideForTests = (System.Func<bool>)priorCoordinatorState[8];
                requestAttemptedField.SetValue(null, priorCoordinatorState[0]);
                nativeShutdownRequestedField.SetValue(null, priorCoordinatorState[1]);
                nativeCloseAllAttemptedField.SetValue(null, priorCoordinatorState[2]);
                finalStatusCompleteField.SetValue(null, priorCoordinatorState[3]);
                finalizationReadyField.SetValue(null, priorCoordinatorState[4]);
                timedOutField.SetValue(null, priorCoordinatorState[5]);
                warningWrittenField.SetValue(null, priorCoordinatorState[6]);
                deadlineField.SetValue(null, priorCoordinatorState[7]);
                recoveryPendingField.SetValue(null, priorRecoveryPending);
                lifecycleFinalPendingField.SetValue(null, priorLifecycleFinalPending);

                BridgeWorkerFinalShutdownRecord[] currentShutdownRecords = Resources.FindObjectsOfTypeAll<BridgeWorkerFinalShutdownRecord>();
                foreach (BridgeWorkerFinalShutdownRecord record in currentShutdownRecords)
                {
                    int priorIndex = System.Array.IndexOf(existingShutdownRecords, record);
                    if (priorIndex < 0) Object.DestroyImmediate(record);
                    else finalShutdownRequestedField.SetValue(record, existingRecordStates[priorIndex]);
                }

                try
                {
                    if (retention != null)
                    {
                        if (texture == null || !texture.IsCreated())
                        {
                            BridgeEditorPreviewRetention.InvalidateProof(retention);
                            if (texture != null) Object.DestroyImmediate(texture);
                            Object.DestroyImmediate(retention);
                        }
                        else
                        {
                            if (!BridgeWorkerEditorGpuFlush.SynchronousWaitActive)
                                BridgeWorkerEditorGpuFlush.BeginSynchronousWait();
                            BridgeEditorPreviewRetention.Pump(true);
                            if (retention != null)
                                Debug.LogError("Test cleanup retained its own preview texture because GPU completion could not be proven.");
                        }
                    }
                }
                finally
                {
                    if (synchronousWaitStarted || BridgeWorkerEditorGpuFlush.SynchronousWaitActive)
                        BridgeWorkerEditorGpuFlush.EndSynchronousWait();
                    if (retention == null && producer != null) Object.DestroyImmediate(producer);
                }
            }
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
