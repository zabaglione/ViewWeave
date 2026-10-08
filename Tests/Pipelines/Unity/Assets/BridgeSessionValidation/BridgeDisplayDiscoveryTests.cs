using System.Collections.Generic;
using System;
using NUnit.Framework;
using ViewWeave;

namespace ViewWeave.BridgeSessionValidation
{
    public sealed class BridgeDisplayDiscoveryTests
    {
        FakeAdapter adapter;

        [SetUp]
        public void SetUp()
        {
            adapter = new FakeAdapter();
            BridgeDisplayDiscoveryNative.InstallTestAdapter(adapter);
        }

        [TearDown]
        public void TearDown()
        {
            BridgeDisplayDiscovery.ResetForTests();
        }

        [Test]
        public void EmptySnapshotIsAValidCompletedDiscovery()
        {
            BridgeDisplayDiscoveryNative.SnapshotStatusV1 status = CreateStatus(5, 5, 0);
            Assert.IsTrue(BridgeDisplayDiscovery.TryBuildSnapshot(status,
                new BridgeDisplayDiscoveryNative.SnapshotItemV1[0], 0, out BridgeDisplaySnapshot snapshot, out string error), error);
            Assert.AreEqual(5ul, snapshot.Generation);
            Assert.AreEqual(0, snapshot.Displays.Count);
            Assert.IsFalse(snapshot.TryGetDisplay(0, out _));
        }

        [Test]
        public void SnapshotKeepsDisplayOrdinalSeparateFromProfileIndexAndCopiesEntries()
        {
            BridgeDisplayDiscoveryNative.SnapshotItemV1 item = ValidItem(2, 17);
            BridgeDisplayDiscoveryNative.SnapshotStatusV1 status = CreateStatus(9, 9, 1);
            Assert.IsTrue(BridgeDisplayDiscovery.TryBuildSnapshot(status, new[] { item }, 1,
                out BridgeDisplaySnapshot snapshot, out string error), error);
            Assert.IsTrue(snapshot.TryGetDisplay(2, out BridgeDisplayProfile profile));
            Assert.AreEqual(17u, profile.DisplayIndex);
            Assert.AreEqual(3840u, profile.Width);
            Assert.IsTrue(profile.HasPhysicalCalibration);
            Assert.AreEqual(344.65f, profile.PhysicalWidthMillimeters, 0.02f);
            Assert.IsFalse(snapshot.TryGetDisplay(17, out _));

            IList<BridgeDisplayProfile> list = snapshot.Displays as IList<BridgeDisplayProfile>;
            Assert.IsNotNull(list);
            Assert.IsTrue(list.IsReadOnly);
        }

        [Test]
        public void SnapshotRejectsUnsupportedAbiPartialCountsAndDuplicateOrdinals()
        {
            BridgeDisplayDiscoveryNative.SnapshotStatusV1 status = CreateStatus(1, 1, 1);
            Assert.IsFalse(BridgeDisplayDiscovery.TryBuildSnapshot(status, new[] { ValidItem(0, 0) }, 0,
                out _, out _));

            status.version = 2;
            Assert.IsFalse(BridgeDisplayDiscovery.TryBuildSnapshot(status, new[] { ValidItem(0, 0) }, 1,
                out _, out _));

            status = CreateStatus(1, 1, 2);
            Assert.IsFalse(BridgeDisplayDiscovery.TryBuildSnapshot(status,
                new[] { ValidItem(0, 0), ValidItem(0, 1) }, 2, out _, out _));
        }

        [Test]
        public void IdleSuccessCallbackRunsAfterEndAndCanStartNextRequest()
        {
            adapter.CompleteCloseImmediately = true;
            object owner = new object();
            bool callbackRan = false;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(11), owner, _ =>
            {
                callbackRan = true;
                Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
                BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(12), new object(), null, null);
            }, error => Assert.Fail(error));

            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, adapter.CloseCount);
            BridgeDisplayDiscovery.TickForTests();

            Assert.IsTrue(callbackRan);
            Assert.AreEqual(1, adapter.EndCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy, "the callback's next request should own the service");
        }

        [Test]
        public void MetadataQueryFailureCodeIsNotMistakenForCleanupFailure()
        {
            adapter.Status = CreateStatus(1, 1, 0);
            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            adapter.Status.result = -6;
            adapter.Status.error = "injected query failure";
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(21), new object(),
                _ => Assert.Fail("A failed metadata query must not publish a snapshot."), _ => failureCount++);

            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, adapter.EndCount);
            Assert.AreEqual(1, failureCount);
        }

        [Test]
        public void EndMetadataBusyResultRetriesWithoutDiscardingCompletedSnapshot()
        {
            adapter.CompleteCloseImmediately = true;
            adapter.EndResults.Enqueue(-3);
            adapter.EndResults.Enqueue(0);
            int successCount = 0;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(31), new object(), _ => successCount++, _ => failureCount++);

            BridgeDisplayDiscovery.TickForTests();
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, successCount);
            Assert.AreEqual(0, failureCount);

            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, successCount);
            Assert.AreEqual(0, failureCount);
            Assert.AreEqual(2, adapter.EndCount);
        }

        [Test]
        public void CancelledActiveRefreshRemainsBusyUntilGenerationSettlesAndDropsResult()
        {
            adapter.Status = CreateStatus(4, 5, 1);
            adapter.Status.pending = 1;
            int successCount = 0;
            object owner = new object();
            BridgeDisplayDiscovery.StartActiveRequestForTests(new IntPtr(41), 7, owner, _ => successCount++, _ => Assert.Fail());
            BridgeDisplayDiscovery.Cancel(owner);

            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            adapter.Status = CreateStatus(5, 5, 1);
            BridgeDisplayDiscovery.TickForTests();

            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, successCount);
            Assert.IsNull(BridgeDisplayDiscovery.Snapshot);
        }

        [Test]
        public void CancelledActiveRefreshCanSettleAtMatchingWorkerHandoff()
        {
            object owner = new object();
            const long sessionGeneration = 15;
            IntPtr workerHandle = new IntPtr(45);
            BridgeDisplayDiscovery.StartActiveRequestForTests(workerHandle, sessionGeneration, owner, null, null);
            BridgeDisplayDiscovery.Cancel(owner);

            BridgeDisplayDiscovery.NotifyWorkerCleanupHandedOff(owner, workerHandle, sessionGeneration - 1);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy, "a stale worker generation must not settle the query");
            BridgeDisplayDiscovery.NotifyWorkerCleanupHandedOff(owner, workerHandle, sessionGeneration);

            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
        }

        [Test]
        public void EditorAndOutputTicksPollAtMostOncePerFrame()
        {
            adapter.Status = CreateStatus(0, 1, 0);
            adapter.Status.pending = 1;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(46), new object(), null, null);
            BridgeDisplayDiscovery.Tick();
            int calls = adapter.ReadCount;
            BridgeDisplayDiscovery.Tick();

            Assert.AreEqual(calls, adapter.ReadCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
        }

        [Test]
        public void PendingNativeImageValidationPreservesRequestAndResumesSameCandidate()
        {
            object owner = new object();
            int successCount = 0;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(47), owner,
                value =>
                {
                    successCount++;
                    Assert.AreEqual(1, value.Displays.Count);
                }, _ => failureCount++);
            BridgeDisplayDiscovery.SetValidationStateForTests(true);

            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, adapter.ReadCount);
            Assert.AreEqual(0, adapter.CloseCount);
            Assert.AreEqual(0, failureCount);

            BridgeDisplayDiscovery.SetValidationStateForTests(false);
            BridgeDisplayDiscovery.TickForTests();
            Assert.AreEqual(2, adapter.ReadCount);
            Assert.AreEqual(1, adapter.CloseCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);

            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, successCount);
            Assert.AreEqual(0, failureCount);
        }

        [Test]
        public void PermanentNativeImageMismatchRetainsOwnerAndFailsRequest()
        {
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(48), new object(),
                _ => Assert.Fail("A request with a mismatched native owner must not publish."), _ => failureCount++);
            BridgeDisplayDiscovery.SetValidationStateForTests(false, true);

            BridgeDisplayDiscovery.TickForTests();

            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy, "Native ownership must remain retained until safe recovery.");
            Assert.AreEqual(0, adapter.ReadCount);
            Assert.AreEqual(0, adapter.CloseCount);
            Assert.AreEqual(1, failureCount);
        }

        [Test]
        public void TimeoutDuringPendingValidationCancelsCandidateThenClosesAfterValidation()
        {
            double now = 0;
            BridgeDisplayDiscovery.SetTestTimeProvider(() => now);
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(49), new object(),
                _ => Assert.Fail("A timed out request must not publish."), _ => failureCount++);
            BridgeDisplayDiscovery.SetValidationStateForTests(true);
            now = 21;

            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, adapter.CloseCount);
            Assert.AreEqual(1, failureCount);

            BridgeDisplayDiscovery.SetValidationStateForTests(false);
            BridgeDisplayDiscovery.TickForTests();
            Assert.AreEqual(1, adapter.CloseCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, failureCount);
        }

        [Test]
        public void TimedOutIdleQueryRequestsCloseButKeepsBusyThroughNativeEndRetry()
        {
            double now = 0;
            BridgeDisplayDiscovery.SetTestTimeProvider(() => now);
            adapter.Status = CreateStatus(0, 1, 0);
            adapter.Status.pending = 1;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(51), new object(), _ => Assert.Fail(), _ => failureCount++);
            now = 21;
            BridgeDisplayDiscovery.TickForTests();

            Assert.AreEqual(1, adapter.CloseCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, failureCount);

            adapter.Status = CreateStatus(1, 1, 0);
            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            adapter.Status.result = -6;
            adapter.EndResults.Enqueue(-3);
            adapter.EndResults.Enqueue(0);
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, failureCount);
        }

        [Test]
        public void TimedOutActiveRefreshRetainsBusyUntilNativeGenerationCompletes()
        {
            double now = 0;
            BridgeDisplayDiscovery.SetTestTimeProvider(() => now);
            adapter.Status = CreateStatus(4, 5, 1);
            adapter.Status.pending = 1;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartActiveRequestForTests(new IntPtr(56), 18, new object(),
                _ => Assert.Fail("A timed-out refresh must discard its eventual result."), _ => failureCount++);
            now = 21;
            BridgeDisplayDiscovery.TickForTests();

            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, failureCount);
            adapter.Status = CreateStatus(5, 5, 1);
            BridgeDisplayDiscovery.TickForTests();

            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.IsNull(BridgeDisplayDiscovery.Snapshot);
            Assert.AreEqual(1, failureCount);
        }

        [Test]
        public void CandidateCapturedNearQueryDeadlineGetsSeparateCleanupWindow()
        {
            double now = 0;
            BridgeDisplayDiscovery.SetTestTimeProvider(() => now);
            int successCount = 0;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(57), new object(),
                value =>
                {
                    successCount++;
                    Assert.AreEqual(1, value.Displays.Count);
                }, _ => failureCount++);

            now = 19.5;
            BridgeDisplayDiscovery.TickForTests();
            Assert.AreEqual(1, adapter.CloseCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual("Closing metadata worker", BridgeDisplayDiscovery.Status);

            now = 24.5;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, successCount);
            Assert.AreEqual(0, failureCount);

            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, successCount);
            Assert.AreEqual(0, failureCount);
        }

        [Test]
        public void CleanupDeadlineDiscardsCandidateButRetainsAdmissionUntilNativeProof()
        {
            double now = 0;
            BridgeDisplayDiscovery.SetTestTimeProvider(() => now);
            int successCount = 0;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(58), new object(),
                _ => successCount++, _ => failureCount++);

            now = 19.5;
            BridgeDisplayDiscovery.TickForTests();
            now = 40;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, failureCount);
            StringAssert.Contains("cleanup timed out", BridgeDisplayDiscovery.LastError);
            Assert.AreEqual("Metadata cleanup timed out; waiting for native release proof", BridgeDisplayDiscovery.Status);

            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            adapter.EndResults.Enqueue(-3);
            adapter.EndResults.Enqueue(0);
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, successCount);
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, successCount);
            Assert.AreEqual(1, failureCount);
        }

        [Test]
        public void PendingImageValidationPausesCleanupDeadline()
        {
            double now = 0;
            BridgeDisplayDiscovery.SetTestTimeProvider(() => now);
            int successCount = 0;
            int failureCount = 0;
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(59), new object(),
                _ => successCount++, _ => failureCount++);
            now = 19.5;
            BridgeDisplayDiscovery.TickForTests();
            BridgeDisplayDiscovery.SetValidationStateForTests(true);
            now = 20;
            BridgeDisplayDiscovery.TickForTests();
            now = 40;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, failureCount);

            BridgeDisplayDiscovery.SetValidationStateForTests(false);
            BridgeDisplayDiscovery.TickForTests();
            now = 59.4;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(0, failureCount);

            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.AreEqual(1, successCount);
        }

        [Test]
        public void CancelForPlayTransitionClosesIdleQueryAndRetainsAdmissionUntilEnd()
        {
            BridgeDisplayDiscovery.StartIdleRequestForTests(new IntPtr(61), new object(), _ => Assert.Fail(), _ => { });
            BridgeDisplayDiscovery.CancelForPlayTransition();

            Assert.AreEqual(1, adapter.CloseCount);
            Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            adapter.Status = CreateStatus(1, 1, 0);
            adapter.Status.state = 4;
            adapter.Status.cleanupFinished = 1;
            BridgeDisplayDiscovery.TickForTests();
            Assert.IsFalse(BridgeDisplayDiscovery.IsBusy);
            Assert.IsNull(BridgeDisplayDiscovery.Snapshot);
        }

        static BridgeDisplayDiscoveryNative.SnapshotStatusV1 CreateStatus(ulong generation, ulong requested, uint count)
        {
            return new BridgeDisplayDiscoveryNative.SnapshotStatusV1
            {
                structSize = 304,
                version = 1,
                state = 1,
                itemCount = count,
                snapshotGeneration = generation,
                requestedGeneration = requested
            };
        }

        static BridgeDisplayDiscoveryNative.SnapshotItemV1 ValidItem(uint ordinal, uint profileIndex)
        {
            return new BridgeDisplayDiscoveryNative.SnapshotItemV1
            {
                structSize = 76,
                version = 1,
                displayOrdinal = ordinal,
                profile = new BridgeDisplayDiscoveryNative.DisplayInfo
                {
                    index = profileIndex,
                    width = 3840,
                    height = 2160,
                    aspect = 16f / 9f,
                    viewcone = 50f,
                    quiltWidth = 4096,
                    quiltHeight = 4096,
                    quiltColumns = 8,
                    quiltRows = 6
                },
                physical = new BridgeDisplayDiscoveryNative.PhysicalDisplayInfoV1
                {
                    structSize = 24,
                    version = 1,
                    pixelWidth = 3840,
                    pixelHeight = 2160,
                    dpi = 283f,
                    valid = 1
                }
            };
        }

        sealed class FakeAdapter : IBridgeDisplayDiscoveryNativeAdapter
        {
            public BridgeDisplayDiscoveryNative.SnapshotStatusV1 Status = BridgeDisplayDiscoveryTests.CreateStatus(1, 1, 1);
            public readonly Queue<int> EndResults = new Queue<int>();
            public int CloseCount;
            public int EndCount;
            public int ReadCount;
            public bool CompleteCloseImmediately;

            public IntPtr BeginMetadata(string runtimePath, string applicationName) => new IntPtr(1);
            public int RequestDisplayRefresh(IntPtr handle, out ulong requestedGeneration)
            {
                requestedGeneration = 5;
                return 0;
            }
            public int GetDisplaySnapshot(IntPtr handle, ref BridgeDisplayDiscoveryNative.SnapshotStatusV1 status,
                BridgeDisplayDiscoveryNative.SnapshotItemV1[] items, uint capacity, out uint requiredCount)
            {
                ReadCount++;
                status = Status;
                requiredCount = Status.itemCount;
                if (capacity < requiredCount) return -3;
                if (requiredCount > 0) items[0] = ValidItem(0, 0);
                return 0;
            }
            public int Close(IntPtr handle)
            {
                CloseCount++;
                if (CompleteCloseImmediately)
                {
                    Status.state = 4;
                    Status.cleanupFinished = 1;
                }
                return 0;
            }
            public int EndMetadata(IntPtr handle)
            {
                EndCount++;
                return EndResults.Count == 0 ? 0 : EndResults.Dequeue();
            }
        }
    }
}
