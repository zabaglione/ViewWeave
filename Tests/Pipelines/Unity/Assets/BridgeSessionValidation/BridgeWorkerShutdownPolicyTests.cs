using NUnit.Framework;
using UnityEngine;
using ViewWeave;
using Object = UnityEngine.Object;

namespace ViewWeave.BridgeSessionValidation
{
    public sealed class BridgeWorkerShutdownPolicyTests
    {
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
                record.SetGpuProof(true, true, true, true);
                Assert.IsFalse(record.CanRelease);
                record.NativeReleased = true;
                Assert.IsTrue(record.CanRelease);
            }
            finally { Object.DestroyImmediate(record); }
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
    }
}
