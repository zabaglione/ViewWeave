using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using SnapshotItemV1 = ViewWeave.BridgeDisplayDiscoveryNative.SnapshotItemV1;
using SnapshotStatusV1 = ViewWeave.BridgeDisplayDiscoveryNative.SnapshotStatusV1;

namespace ViewWeave
{
    /// <summary>Coordinates caller-requested Bridge display discovery without calling the vendor SDK from Unity.</summary>
    internal static class BridgeDisplayDiscovery
    {
        enum RequestKind { None, IdleMetadataOwner, ActiveWorker }

        static readonly int mainThreadId = Thread.CurrentThread.ManagedThreadId;
        static readonly BridgeDisplayDiscoveryNative.SnapshotItemV1[] nativeItems =
            new BridgeDisplayDiscoveryNative.SnapshotItemV1[BridgeDisplayDiscoveryNative.MaximumDisplays];
        static RequestKind requestKind;
        static IntPtr handle;
        static BridgeNativeModuleIdentity moduleIdentity;
        static BridgeDisplayMetadataRecoveryRecord recoveryRecord;
        static object requestOwner;
        static Action<BridgeDisplaySnapshot> successCallback;
        static Action<string> failureCallback;
        static ulong requestedGeneration;
        static BridgeDisplaySnapshot candidateSnapshot;
        static bool closeRequested;
        static bool tickInProgress;
        static bool recoveryPending;
        static bool requestCancelled;
        static bool requestTimedOut;
        static long ownerSessionGeneration;
        static int lastTickFrame = int.MinValue;
#if UNITY_EDITOR
        static int lastTickEditorEpoch = int.MinValue;
#endif
        static double requestStartedAt;
        static double cleanupStartedAt;
        static bool cleanupTimedOut;
        static bool cleanupValidationPaused;
        static double cleanupValidationPausedAt;
        const double RequestTimeoutSeconds = 20.0;
        const double CleanupTimeoutSeconds = 20.0;
        static string status = "Idle";
        static string lastError = string.Empty;
        static BridgeDisplaySnapshot snapshot;

        internal static event Action<BridgeDisplaySnapshot> SnapshotPublished;
        internal static BridgeDisplaySnapshot Snapshot => snapshot;
        internal static string Status => status;
        internal static string LastError => lastError;
        internal static bool IsBusy => requestKind != RequestKind.None || recoveryPending;

        internal static bool TryRequest(string runtimePath, string applicationName, uint displayOrdinal, object owner,
            Action<BridgeDisplaySnapshot> onSuccess, Action<string> onFailure, out string error)
        {
            error = string.Empty;
            if (!IsMainThread(out error)) return false;
            if (BridgeDisplayDiscoveryNative.IsTestAdapter)
                return FailRequest("The test adapter can only be used through the managed lifecycle test seam.", onFailure);
            if (Application.isPlaying)
            {
                error = "Display metadata discovery requires Edit Mode or an existing runtime worker.";
                return FailRequest(error, onFailure);
            }
            if (owner == null)
            {
                error = "A display metadata request owner is required.";
                return FailRequest(error, onFailure);
            }
            if (string.IsNullOrWhiteSpace(runtimePath) || string.IsNullOrWhiteSpace(applicationName))
            {
                error = "Set a Bridge runtime path and application name before requesting display metadata.";
                return FailRequest(error, onFailure);
            }
            if (IsBusy)
            {
                error = "A Bridge display metadata request or recovery is already pending.";
                return FailRequest(error, onFailure);
            }
            if (BridgeWorkerLifecycle.RecoveryPending || BridgeWorkerReaper.PendingCount != 0 ||
                BridgeWorkerQuiltOutput.HasNativeHandle)
            {
                error = "Another Bridge worker or cleanup is active; display metadata discovery is unavailable.";
                return FailRequest(error, onFailure);
            }
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryInspectEditorProcess(out error)) return FailRequest(error, onFailure);
            BridgeNativeModuleIdentity identity = default;
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                IntPtr renderEventFunction;
                try
                {
                    renderEventFunction = BridgeWorkerNative.lkg_worker_render_event_func();
                }
                catch (DllNotFoundException)
                {
                    error = "Bridge worker native library is missing.";
                    return FailRequest(error, onFailure);
                }
                catch (EntryPointNotFoundException)
                {
                    error = "Bridge worker render event ABI is missing. Update the package and restart the Unity Editor.";
                    return FailRequest(error, onFailure);
                }
                catch (Exception exception)
                {
                    error = "Bridge worker native image load failed (" + exception.GetType().Name + ").";
                    return FailRequest(error, onFailure);
                }

                if (renderEventFunction == IntPtr.Zero)
                {
                    error = "Bridge worker render event function is unavailable.";
                    return FailRequest(error, onFailure);
                }
                if (!BridgeNativeImageGuard.TryCaptureCurrentIdentity(out identity, out error))
                    return FailRequest(error, onFailure);
                if (!BridgeNativeImageGuard.TryValidateRenderEventFunctionAddress(renderEventFunction, identity, out error))
                    return FailRequest(error, onFailure);
            }
#else
            BridgeNativeModuleIdentity identity = default;
#endif
            IntPtr metadataHandle;
            try
            {
                metadataHandle = BridgeDisplayDiscoveryNative.Adapter.BeginMetadata(runtimePath, applicationName);
            }
            catch (DllNotFoundException)
            {
                error = "Bridge worker native library is missing.";
                return FailRequest(error, onFailure);
            }
            catch (EntryPointNotFoundException)
            {
                error = "Bridge worker display metadata ABI is missing. Update the package and restart the Unity Editor.";
                return FailRequest(error, onFailure);
            }
            catch (Exception exception)
            {
                error = "Bridge display metadata begin failed (" + exception.GetType().Name + ").";
                return FailRequest(error, onFailure);
            }
            if (metadataHandle == IntPtr.Zero)
            {
                error = "Bridge metadata worker could not acquire exclusive SDK ownership.";
                return FailRequest(error, onFailure);
            }

            recoveryRecord = BridgeDisplayMetadataRecoveryRecord.Create(metadataHandle, identity);
            handle = metadataHandle;
            moduleIdentity = identity;
            requestOwner = owner;
            successCallback = onSuccess;
            failureCallback = onFailure;
            requestKind = RequestKind.IdleMetadataOwner;
            requestedGeneration = 1;
            candidateSnapshot = null;
            closeRequested = false;
            recoveryPending = true;
            requestCancelled = false;
            requestTimedOut = false;
            ownerSessionGeneration = 0;
            requestStartedAt = CurrentTime;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            lastError = string.Empty;
            status = "Discovering displays";
            return true;
        }

        internal static bool TryRequestRefresh(IntPtr activeHandle, BridgeNativeModuleIdentity identity, uint displayOrdinal,
            long sessionGeneration, object owner, Action<BridgeDisplaySnapshot> onSuccess, Action<string> onFailure, out string error)
        {
            error = string.Empty;
            if (!IsMainThread(out error)) return false;
            if (BridgeDisplayDiscoveryNative.IsTestAdapter)
                return FailRequest("The test adapter can only be used through the managed lifecycle test seam.", onFailure);
            if (owner == null || activeHandle == IntPtr.Zero)
            {
                error = "An active Bridge worker and request owner are required.";
                return FailRequest(error, onFailure);
            }
            if (IsBusy)
            {
                error = "A Bridge display metadata request or recovery is already pending.";
                return FailRequest(error, onFailure);
            }
#if UNITY_EDITOR
            if (!BridgeNativeImageGuard.TryValidateOwnerIdentity(identity, out error)) return FailRequest(error, onFailure);
#endif
            try
            {
                int result = BridgeDisplayDiscoveryNative.Adapter.RequestDisplayRefresh(activeHandle, out requestedGeneration);
                if (result != 0)
                {
                    error = "Bridge display refresh request failed (code " + result + ").";
                    return FailRequest(error, onFailure);
                }
            }
            catch (DllNotFoundException)
            {
                error = "Bridge worker native library is missing.";
                return FailRequest(error, onFailure);
            }
            catch (EntryPointNotFoundException)
            {
                error = "Bridge display metadata ABI is missing. Update the package and restart the Unity Editor.";
                return FailRequest(error, onFailure);
            }
            catch (Exception exception)
            {
                error = "Bridge display refresh request failed (" + exception.GetType().Name + ").";
                return FailRequest(error, onFailure);
            }

            handle = activeHandle;
            moduleIdentity = identity;
            requestOwner = owner;
            successCallback = onSuccess;
            failureCallback = onFailure;
            requestKind = RequestKind.ActiveWorker;
            ownerSessionGeneration = sessionGeneration;
            requestCancelled = false;
            requestTimedOut = false;
            requestStartedAt = CurrentTime;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            candidateSnapshot = null;
            lastError = string.Empty;
            status = "Refreshing connected displays";
            return true;
        }

        internal static void Tick()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId || tickInProgress) return;
            if (!BeginTickEpoch()) return;
            if (requestKind == RequestKind.None)
            {
                RecoverPersistedOwner();
                if (requestKind == RequestKind.None) return;
            }

            tickInProgress = true;
            try
            {
                bool validationPending = IsOwnerValidationPending();
                UpdateCleanupValidationPause(validationPending);
                bool deferIdleCleanupTimeout = !validationPending &&
                    requestKind == RequestKind.IdleMetadataOwner && closeRequested;
                BridgeDisplayMetadataRecoveryRecord deferredCleanupRecord = deferIdleCleanupTimeout ? recoveryRecord : null;
                IntPtr deferredCleanupHandle = deferIdleCleanupTimeout ? handle : IntPtr.Zero;
                CheckTimeout(validationPending || deferIdleCleanupTimeout);
                if (validationPending)
                {
                    if (!requestTimedOut && !requestCancelled)
                        status = "Waiting for native image validation";
                    return;
                }
                if (!ValidateOwner(out string guardError))
                {
                    if (requestKind == RequestKind.ActiveWorker) FailActiveButRetain(guardError);
                    else SetFailure(guardError, true);
                    return;
                }
                if (requestKind == RequestKind.None) return;
                if (requestKind == RequestKind.IdleMetadataOwner && requestCancelled && !closeRequested)
                {
                    TryRequestClose();
                    if (!closeRequested) return;
                }
                if (requestKind == RequestKind.IdleMetadataOwner)
                {
                    TickIdleMetadata();
                    if (deferredCleanupRecord != null &&
                        requestKind == RequestKind.IdleMetadataOwner && closeRequested &&
                        ReferenceEquals(recoveryRecord, deferredCleanupRecord) && handle == deferredCleanupHandle)
                        CheckTimeout(false);
                }
                else if (requestKind == RequestKind.ActiveWorker)
                    TickActiveWorker();
            }
            finally
            {
                tickInProgress = false;
            }
        }

        internal static void TickForTests()
        {
            lastTickFrame = int.MinValue;
#if UNITY_EDITOR
            lastTickEditorEpoch = int.MinValue;
#endif
            Tick();
        }

        internal static void Cancel(object owner)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId || owner == null || !ReferenceEquals(requestOwner, owner)) return;
            successCallback = null;
            failureCallback = null;
            if (requestKind == RequestKind.IdleMetadataOwner)
            {
                requestOwner = null;
                recoveryRecord?.MarkCancelled();
                requestCancelled = true;
                status = "Closing cancelled metadata request";
                TryRequestClose();
            }
            else
            {
                requestCancelled = true;
                successCallback = null;
                failureCallback = null;
                status = "Display refresh cancelled; waiting for the active worker";
            }
        }

        internal static void CancelForReload()
        {
            if (requestKind == RequestKind.IdleMetadataOwner)
            {
                recoveryRecord?.MarkCancelled();
                requestCancelled = true;
                successCallback = null;
                failureCallback = null;
                requestOwner = null;
                status = "Closing metadata request before Editor reload";
                TryRequestClose();
                Tick();
            }
            else if (requestKind == RequestKind.ActiveWorker)
            {
                successCallback = null;
                failureCallback = null;
                requestCancelled = true;
                status = "Display refresh cancelled before Editor reload; waiting for the active worker";
            }
        }

        internal static void CancelForPlayTransition()
        {
            CancelForReload();
        }

        internal static void NotifyWorkerCleanupHandedOff(object owner, IntPtr workerHandle, long sessionGeneration)
        {
            if (requestKind != RequestKind.ActiveWorker || owner == null ||
                !ReferenceEquals(requestOwner, owner) || handle != workerHandle || ownerSessionGeneration != sessionGeneration) return;
            bool shouldNotify = !requestCancelled && !requestTimedOut;
            Action<string> callback = failureCallback;
            ClearActiveRequest(requestTimedOut
                ? "Display refresh timed out; worker cleanup was handed to recovery"
                : (requestCancelled ? "Display refresh cancelled; worker cleanup was handed to recovery" :
                    "Active worker ended before display refresh completed"));
            if (shouldNotify) InvokeFailure(callback, "The active Bridge worker stopped before display refresh completed.");
        }

        internal static void InitializeEditorRecovery()
        {
            BridgeDisplayMetadataRecoveryRecord[] records = Resources.FindObjectsOfTypeAll<BridgeDisplayMetadataRecoveryRecord>();
            if (records.Length == 0) return;
            recoveryPending = true;
            if (records.Length != 1)
            {
                lastError = "Multiple persisted Bridge metadata owners were found; restart the Unity Editor to recover safely.";
                status = "Recovery required";
                return;
            }
            recoveryRecord = records[0];
            recoveryRecord.MarkCancelled();
            handle = recoveryRecord.Handle;
            moduleIdentity = recoveryRecord.NativeModuleIdentity;
            requestKind = RequestKind.IdleMetadataOwner;
            requestedGeneration = 0;
            requestOwner = null;
            successCallback = null;
            failureCallback = null;
            closeRequested = recoveryRecord.CloseRequested;
            requestCancelled = true;
            requestTimedOut = false;
            cleanupStartedAt = closeRequested ? CurrentTime : 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            ownerSessionGeneration = 0;
            candidateSnapshot = null;
            status = "Recovering metadata worker";
        }

        internal static bool TryBuildSnapshot(SnapshotStatusV1 statusValue, SnapshotItemV1[] items,
            uint requiredCount, out BridgeDisplaySnapshot result, out string error)
        {
            result = null;
            error = string.Empty;
            int expectedStatusSize = Marshal.SizeOf(typeof(SnapshotStatusV1));
            int expectedItemSize = Marshal.SizeOf(typeof(SnapshotItemV1));
            int expectedPhysicalSize = Marshal.SizeOf(typeof(BridgeDisplayDiscoveryNative.PhysicalDisplayInfoV1));
            if (statusValue.structSize != expectedStatusSize || statusValue.version != 1)
            {
                error = "Bridge returned an unsupported display snapshot status layout.";
                return false;
            }
            if (requiredCount != statusValue.itemCount || requiredCount > BridgeDisplayDiscoveryNative.MaximumDisplays ||
                items == null || (uint)items.Length != requiredCount)
            {
                error = "Bridge returned an incomplete or oversized display snapshot.";
                return false;
            }
            uint[] ordinals = new uint[items.Length];
            BridgeDisplayProfile[] profiles = new BridgeDisplayProfile[items.Length];
            HashSet<uint> uniqueOrdinals = new HashSet<uint>();
            for (int i = 0; i < items.Length; ++i)
            {
                SnapshotItemV1 item = items[i];
                if (item.structSize != expectedItemSize || item.version != 1 || item.reserved0 != 0 ||
                    item.physical.structSize != expectedPhysicalSize || item.physical.version != 1 || item.physical.valid > 1 ||
                    !uniqueOrdinals.Add(item.displayOrdinal))
                {
                    error = "Bridge returned an invalid display metadata item.";
                    return false;
                }
                BridgeNative.DisplayInfo display = new BridgeNative.DisplayInfo
                {
                    index = item.profile.index,
                    width = item.profile.width,
                    height = item.profile.height,
                    aspect = item.profile.aspect,
                    viewcone = item.profile.viewcone,
                    quiltWidth = item.profile.quiltWidth,
                    quiltHeight = item.profile.quiltHeight,
                    quiltColumns = item.profile.quiltColumns,
                    quiltRows = item.profile.quiltRows
                };
                BridgeWorkerNative.PhysicalDisplayInfoV1 physical = new BridgeWorkerNative.PhysicalDisplayInfoV1
                {
                    structSize = item.physical.structSize,
                    version = item.physical.version,
                    pixelWidth = item.physical.pixelWidth,
                    pixelHeight = item.physical.pixelHeight,
                    dpi = item.physical.dpi,
                    valid = item.physical.valid
                };
                BridgeDisplayProfile profile = new BridgeDisplayProfile(display, physical);
                if (!profile.IsValid)
                {
                    error = "Bridge returned invalid display geometry.";
                    return false;
                }
                ordinals[i] = item.displayOrdinal;
                profiles[i] = profile;
            }
            result = new BridgeDisplaySnapshot(statusValue.snapshotGeneration, ordinals, profiles);
            return true;
        }

        internal static void StartIdleRequestForTests(IntPtr testHandle, object owner,
            Action<BridgeDisplaySnapshot> onSuccess, Action<string> onFailure)
        {
            if (!BridgeDisplayDiscoveryNative.IsTestAdapter) throw new InvalidOperationException("Install a fake Bridge adapter first.");
            if (requestKind != RequestKind.None || recoveryPending) throw new InvalidOperationException("A display request is already active.");
            recoveryRecord = BridgeDisplayMetadataRecoveryRecord.Create(testHandle, default);
            handle = testHandle;
            moduleIdentity = default;
            requestOwner = owner;
            successCallback = onSuccess;
            failureCallback = onFailure;
            requestedGeneration = 1;
            candidateSnapshot = null;
            closeRequested = false;
            recoveryPending = true;
            requestCancelled = false;
            requestTimedOut = false;
            ownerSessionGeneration = 0;
            requestStartedAt = CurrentTime;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            lastError = string.Empty;
            status = "Discovering displays";
            requestKind = RequestKind.IdleMetadataOwner;
        }

        internal static void StartActiveRequestForTests(IntPtr testHandle, long sessionGeneration, object owner,
            Action<BridgeDisplaySnapshot> onSuccess, Action<string> onFailure)
        {
            if (!BridgeDisplayDiscoveryNative.IsTestAdapter) throw new InvalidOperationException("Install a fake Bridge adapter first.");
            if (requestKind != RequestKind.None || recoveryPending) throw new InvalidOperationException("A display request is already active.");
            int result = BridgeDisplayDiscoveryNative.Adapter.RequestDisplayRefresh(testHandle, out requestedGeneration);
            if (result != 0) throw new InvalidOperationException("Fake display refresh request failed.");
            handle = testHandle;
            moduleIdentity = default;
            requestOwner = owner;
            successCallback = onSuccess;
            failureCallback = onFailure;
            requestKind = RequestKind.ActiveWorker;
            ownerSessionGeneration = sessionGeneration;
            candidateSnapshot = null;
            requestCancelled = false;
            requestTimedOut = false;
            requestStartedAt = CurrentTime;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            lastError = string.Empty;
            status = "Refreshing connected displays";
        }

        internal static void ResetForTests()
        {
            if (recoveryRecord != null)
            {
                recoveryRecord.ReleaseProvenRecord();
                recoveryRecord = null;
            }
            requestKind = RequestKind.None;
            handle = IntPtr.Zero;
            moduleIdentity = default;
            requestOwner = null;
            successCallback = null;
            failureCallback = null;
            requestedGeneration = 0;
            candidateSnapshot = null;
            closeRequested = false;
            tickInProgress = false;
            recoveryPending = false;
            requestCancelled = false;
            requestTimedOut = false;
            ownerSessionGeneration = 0;
            lastTickFrame = int.MinValue;
#if UNITY_EDITOR
            lastTickEditorEpoch = int.MinValue;
#endif
            requestStartedAt = 0;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            status = "Idle";
            lastError = string.Empty;
            snapshot = null;
            SnapshotPublished = null;
            BridgeDisplayDiscoveryNative.ResetAdapter();
            testTimeProvider = null;
            testValidationPending = null;
            testOwnerValidationFails = false;
        }

        static Func<double> testTimeProvider;
        static bool? testValidationPending;
        static bool testOwnerValidationFails;
        internal static void SetTestTimeProvider(Func<double> provider) => testTimeProvider = provider;
        internal static void SetValidationStateForTests(bool pending, bool permanentMismatch = false)
        {
            testValidationPending = pending;
            testOwnerValidationFails = permanentMismatch;
        }
        static double CurrentTime => testTimeProvider == null ? Time.realtimeSinceStartupAsDouble : testTimeProvider();

        static void CheckTimeout(bool deferNativeCleanup)
        {
            if (requestKind == RequestKind.None) return;
            if (requestKind == RequestKind.IdleMetadataOwner && closeRequested)
            {
                if (deferNativeCleanup) return;
                if (!cleanupTimedOut && CurrentTime - cleanupStartedAt >= CleanupTimeoutSeconds)
                    TimeoutIdleCleanup();
                return;
            }
            if (requestTimedOut || requestCancelled ||
                CurrentTime - requestStartedAt < RequestTimeoutSeconds) return;
            requestTimedOut = true;
            lastError = "Bridge display metadata request timed out; waiting for native cleanup.";
            status = requestKind == RequestKind.IdleMetadataOwner
                ? "Timed out; closing metadata worker"
                : "Timed out; waiting for active worker completion";
            Action<string> callback = failureCallback;
            failureCallback = null;
            successCallback = null;
            if (requestKind == RequestKind.IdleMetadataOwner)
            {
                requestCancelled = true;
                requestOwner = null;
                candidateSnapshot = null;
                recoveryRecord?.MarkCancelled();
                if (!deferNativeCleanup) TryRequestClose();
                else status = "Timed out; waiting for native image validation before cleanup";
            }
            else if (requestKind == RequestKind.ActiveWorker)
            {
                requestCancelled = true;
                status = "Display refresh timed out; waiting for the active worker";
            }
            InvokeFailure(callback, lastError);
        }

        static void UpdateCleanupValidationPause(bool validationPending)
        {
            if (requestKind != RequestKind.IdleMetadataOwner || !closeRequested) return;
            if (validationPending)
            {
                if (!cleanupValidationPaused)
                {
                    cleanupValidationPaused = true;
                    cleanupValidationPausedAt = CurrentTime;
                }
                return;
            }
            if (!cleanupValidationPaused) return;
            cleanupStartedAt += Math.Max(0, CurrentTime - cleanupValidationPausedAt);
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
        }

        static void TimeoutIdleCleanup()
        {
            cleanupTimedOut = true;
            requestTimedOut = true;
            requestCancelled = true;
            candidateSnapshot = null;
            requestOwner = null;
            recoveryRecord?.MarkCancelled();
            lastError = "Bridge metadata cleanup timed out; native ownership remains retained until cleanup is proven.";
            status = "Metadata cleanup timed out; waiting for native release proof";
            Action<string> callback = failureCallback;
            failureCallback = null;
            successCallback = null;
            InvokeFailure(callback, lastError);
        }

        static void TickActiveWorker()
        {
            if (!TryReadSnapshot(out SnapshotStatusV1 nativeStatus, out _, out string error, false))
            {
                FailActiveButRetain(error);
                return;
            }
            if (nativeStatus.pending != 0 || nativeStatus.snapshotGeneration < requestedGeneration) return;
            if (nativeStatus.snapshotGeneration != requestedGeneration)
            {
                SetFailure("Bridge returned a display snapshot from an unexpected refresh generation.", false);
                return;
            }
            if (nativeStatus.state != 1)
            {
                if (requestCancelled || requestTimedOut)
                {
                    ClearActiveRequest(requestTimedOut
                        ? "Display refresh timed out; worker stopped"
                        : "Display refresh cancelled; worker stopped");
                    return;
                }
                FailActiveButRetain("The active Bridge worker stopped before display metadata refresh completed.");
                return;
            }
            if (nativeStatus.result != 0)
            {
                SetFailure(string.IsNullOrWhiteSpace(nativeStatus.error)
                    ? "Bridge display metadata query failed (code " + nativeStatus.result + ")."
                    : nativeStatus.error, false);
                return;
            }
            if (!TryReadSnapshot(out SnapshotStatusV1 finalStatus, out BridgeDisplaySnapshot completed, out error, true))
            {
                FailActiveButRetain(error);
                return;
            }
            if (finalStatus.pending != 0 || finalStatus.snapshotGeneration != requestedGeneration || finalStatus.result != 0 || finalStatus.state != 1)
            {
                SetFailure("Bridge display snapshot changed while it was being copied.", false);
                return;
            }
            bool discardResult = requestCancelled || requestTimedOut;
            Action<BridgeDisplaySnapshot> callback = successCallback;
            bool timedOut = requestTimedOut;
            ClearActiveRequest(discardResult
                ? (timedOut ? "Display refresh timed out and settled" : "Display refresh cancelled and settled")
                : "Display refresh complete");
            if (!discardResult) Publish(completed, callback);
        }

        static void TickIdleMetadata()
        {
            if (recoveryRecord == null)
            {
                SetFailure("Persisted metadata owner record is missing; restart the Unity Editor to recover safely.", true);
                return;
            }
            if (!closeRequested)
            {
                if (!TryReadSnapshot(out SnapshotStatusV1 nativeStatus, out _, out string error, false))
                {
                    SetFailure(error, true);
                    TryRequestClose();
                    return;
                }
                if (nativeStatus.pending != 0 || nativeStatus.snapshotGeneration < requestedGeneration) return;
                if (nativeStatus.snapshotGeneration != requestedGeneration)
                {
                    SetFailure("Bridge returned a display snapshot from an unexpected discovery generation.", true);
                    TryRequestClose();
                    return;
                }
                if (nativeStatus.state != 1)
                {
                    lastError = Sanitize(string.IsNullOrWhiteSpace(nativeStatus.error)
                        ? "Bridge metadata discovery failed (state " + nativeStatus.state + ")."
                        : nativeStatus.error);
                    candidateSnapshot = null;
                }
                else if (nativeStatus.result == 0)
                {
                    if (!TryReadSnapshot(out SnapshotStatusV1 finalStatus, out BridgeDisplaySnapshot completed, out error, true))
                    {
                        SetFailure(error, true);
                        TryRequestClose();
                        return;
                    }
                    if (finalStatus.pending != 0 || finalStatus.snapshotGeneration != requestedGeneration || finalStatus.result != 0)
                    {
                        SetFailure("Bridge display snapshot changed while it was being copied.", true);
                        TryRequestClose();
                        return;
                    }
                    candidateSnapshot = completed;
                }
                else
                {
                    lastError = Sanitize(string.IsNullOrWhiteSpace(nativeStatus.error)
                        ? "Bridge display metadata query failed (code " + nativeStatus.result + ")."
                        : nativeStatus.error);
                    candidateSnapshot = null;
                }
                TryRequestClose();
                return;
            }

            if (!TryReadSnapshot(out SnapshotStatusV1 cleanupStatus, out _, out string cleanupError, false))
            {
                SetFailure(cleanupError, true);
                return;
            }
            if (cleanupStatus.cleanupFinished == 0 || (cleanupStatus.state != 4 && cleanupStatus.state != 2)) return;
            int endResult = TryEndMetadata(out string endError);
            if (endResult == -3)
            {
                status = "Waiting for Bridge metadata worker release";
                return;
            }
            if (endResult != 0)
            {
                SetFailure(endError, true);
                return;
            }

            BridgeDisplaySnapshot publishable = recoveryRecord.Cancelled ? null : candidateSnapshot;
            recoveryRecord.ReleaseProvenRecord();
            recoveryRecord = null;
            handle = IntPtr.Zero;
            moduleIdentity = default;
            requestKind = RequestKind.None;
            recoveryPending = false;
            closeRequested = false;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            requestedGeneration = 0;
            candidateSnapshot = null;
            object oldOwner = requestOwner;
            Action<BridgeDisplaySnapshot> oldSuccess = successCallback;
            Action<string> oldFailure = failureCallback;
            requestOwner = null;
            successCallback = null;
            failureCallback = null;
            status = "Display discovery complete";
            if (publishable != null)
                Publish(publishable, oldSuccess);
            else if (!string.IsNullOrEmpty(lastError))
                InvokeFailure(oldFailure, lastError);
            else if (oldOwner == null)
                status = "Display discovery cancelled";
        }

        static bool TryReadSnapshot(out SnapshotStatusV1 statusValue, out BridgeDisplaySnapshot completed, out string error, bool buildProfiles)
        {
            statusValue = new SnapshotStatusV1
            {
                structSize = (uint)Marshal.SizeOf(typeof(SnapshotStatusV1)),
                version = 1
            };
            completed = null;
            error = string.Empty;
            uint requiredCount;
            int result;
            try
            {
                result = BridgeDisplayDiscoveryNative.Adapter.GetDisplaySnapshot(
                    handle, ref statusValue, nativeItems, (uint)nativeItems.Length, out requiredCount);
            }
            catch (DllNotFoundException)
            {
                error = "Bridge worker native library is missing; native ownership remains retained.";
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                error = "Bridge display metadata ABI is missing; restart the Unity Editor before recovery.";
                return false;
            }
            catch (Exception exception)
            {
                error = "Bridge display snapshot read failed (" + exception.GetType().Name + ").";
                return false;
            }
            if (result != 0)
            {
                error = "Bridge display snapshot read failed (code " + result + ").";
                return false;
            }
            if (statusValue.structSize != Marshal.SizeOf(typeof(SnapshotStatusV1)) || statusValue.version != 1)
            {
                error = "Bridge returned an unsupported display snapshot status layout.";
                return false;
            }
            if (statusValue.pending > 1 || statusValue.cleanupFinished > 1 || statusValue.state > 4)
            {
                error = "Bridge returned invalid display snapshot state.";
                return false;
            }
            if (requiredCount != statusValue.itemCount || requiredCount > (uint)nativeItems.Length)
            {
                error = "Bridge returned an oversized or inconsistent display snapshot.";
                return false;
            }
            if (buildProfiles && statusValue.pending == 0 && statusValue.snapshotGeneration >= requestedGeneration &&
                !TryBuildSnapshot(statusValue, ArrayRange(nativeItems, (int)requiredCount), requiredCount, out completed, out error)) return false;
            return true;
        }

        static SnapshotItemV1[] ArrayRange(SnapshotItemV1[] source, int count)
        {
            if (count == 0) return new SnapshotItemV1[0];
            SnapshotItemV1[] result = new SnapshotItemV1[count];
            Array.Copy(source, result, count);
            return result;
        }

        static int TryEndMetadata(out string error)
        {
            error = string.Empty;
            try
            {
                int result = BridgeDisplayDiscoveryNative.Adapter.EndMetadata(handle);
                if (result == 0 || result == -3) return result;
                error = "Bridge metadata owner release failed (code " + result + "); native ownership remains retained.";
                return result;
            }
            catch (DllNotFoundException)
            {
                error = "Bridge worker native library is missing; native ownership remains retained.";
                return -1001;
            }
            catch (EntryPointNotFoundException)
            {
                error = "Bridge metadata end ABI is missing; native ownership remains retained.";
                return -1002;
            }
            catch (Exception exception)
            {
                error = "Bridge metadata owner release failed (" + exception.GetType().Name + ").";
                return -1003;
            }
        }

        static void TryRequestClose()
        {
            if (requestKind != RequestKind.IdleMetadataOwner || closeRequested || handle == IntPtr.Zero) return;
            if (IsOwnerValidationPending()) return;
            if (!ValidateOwner(out string guardError))
            {
                SetFailure(guardError, true);
                return;
            }
            try
            {
                int result = BridgeDisplayDiscoveryNative.Adapter.Close(handle);
                if (result != 0)
                {
                    SetFailure("Bridge metadata close request failed (code " + result + "); native ownership remains retained.", true);
                    return;
                }
                closeRequested = true;
                cleanupStartedAt = CurrentTime;
                cleanupTimedOut = false;
                cleanupValidationPaused = false;
                cleanupValidationPausedAt = 0;
                recoveryRecord?.MarkCloseRequested();
                status = requestCancelled ? "Closing cancelled metadata worker" : "Closing metadata worker";
            }
            catch (Exception exception)
            {
                SetFailure("Bridge metadata close request failed (" + exception.GetType().Name + ").", true);
            }
        }

        static void RecoverPersistedOwner()
        {
            if (!recoveryPending) return;
            InitializeEditorRecovery();
            if (requestKind == RequestKind.IdleMetadataOwner) Tick();
        }

        static bool ValidateOwner(out string error)
        {
            error = string.Empty;
            if (BridgeDisplayDiscoveryNative.IsTestAdapter && testOwnerValidationFails)
            {
                error = "The native worker owner is bound to a different or unknown native image. Restart the Unity Editor.";
                return false;
            }
#if UNITY_EDITOR
            if (!BridgeDisplayDiscoveryNative.IsTestAdapter && !BridgeNativeImageGuard.TryValidateOwnerIdentity(moduleIdentity, out error)) return false;
#endif
            return handle != IntPtr.Zero;
        }

        static bool IsOwnerValidationPending()
        {
            if (BridgeDisplayDiscoveryNative.IsTestAdapter && testValidationPending.HasValue)
                return testValidationPending.Value;
#if UNITY_EDITOR
            return !BridgeDisplayDiscoveryNative.IsTestAdapter && BridgeNativeImageGuard.IsValidationPending;
#else
            return false;
#endif
        }

        static bool BeginTickEpoch()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // Time.frameCount does not advance reliably while editing. Use an editor-time
                // epoch so EditorApplication.update and explicit editor pumps cannot stall polling.
                int editorEpoch = (int)(EditorApplication.timeSinceStartup * 1000.0);
                if (lastTickEditorEpoch == editorEpoch) return false;
                lastTickEditorEpoch = editorEpoch;
                return true;
            }
#endif
            int currentFrame = Time.frameCount;
            if (lastTickFrame == currentFrame) return false;
            lastTickFrame = currentFrame;
            return true;
        }

        static bool IsMainThread(out string error)
        {
            error = Thread.CurrentThread.ManagedThreadId == mainThreadId
                ? string.Empty
                : "Bridge display metadata requests must be made on Unity's main thread.";
            return error.Length == 0;
        }

        static bool FailRequest(string error, Action<string> callback)
        {
            lastError = Sanitize(error);
            status = "Request failed";
            InvokeFailure(callback, lastError);
            return false;
        }

        static void SetFailure(string error, bool retainOwner)
        {
            lastError = Sanitize(error);
            status = retainOwner ? "Recovery required" : "Request failed";
            if (!retainOwner)
            {
                Action<string> callback = failureCallback;
                ClearActiveRequest(status);
                InvokeFailure(callback, lastError);
            }
            else
            {
                recoveryPending = true;
                recoveryRecord?.MarkCancelled();
                recoveryRecord?.SetDiagnostic(lastError);
                candidateSnapshot = null;
                requestOwner = null;
                successCallback = null;
                Action<string> callback = failureCallback;
                failureCallback = null;
                InvokeFailure(callback, lastError);
            }
        }

        static void Publish(BridgeDisplaySnapshot value, Action<BridgeDisplaySnapshot> completionCallback = null)
        {
            if (value == null) return;
            snapshot = value;
            lastError = string.Empty;
            status = "Display discovery complete";
            Action<BridgeDisplaySnapshot> callback = completionCallback ?? successCallback;
            Action<BridgeDisplaySnapshot> listeners = SnapshotPublished;
            successCallback = null;
            failureCallback = null;
            requestOwner = null;
            InvokeSuccess(callback, value);
            if (listeners != null)
            {
                foreach (Delegate listener in listeners.GetInvocationList())
                    InvokeSuccess((Action<BridgeDisplaySnapshot>)listener, value);
            }
        }

        static void InvokeSuccess(Action<BridgeDisplaySnapshot> callback, BridgeDisplaySnapshot value)
        {
            if (callback == null) return;
            try { callback(value); }
            catch (Exception exception) { Debug.LogError("ViewWeave Bridge display callback failed (" + Sanitize(exception.GetType().Name) + ")."); }
        }

        static void InvokeFailure(Action<string> callback, string error)
        {
            if (callback == null) return;
            try { callback(Sanitize(error)); }
            catch (Exception exception) { Debug.LogError("ViewWeave Bridge display error callback failed (" + Sanitize(exception.GetType().Name) + ")."); }
        }

        static void ClearActiveRequest(string finalStatus)
        {
            handle = IntPtr.Zero;
            moduleIdentity = default;
            requestKind = RequestKind.None;
            candidateSnapshot = null;
            requestOwner = null;
            successCallback = null;
            failureCallback = null;
            requestedGeneration = 0;
            requestCancelled = false;
            requestTimedOut = false;
            ownerSessionGeneration = 0;
            cleanupStartedAt = 0;
            cleanupTimedOut = false;
            cleanupValidationPaused = false;
            cleanupValidationPausedAt = 0;
            status = finalStatus;
        }

        static void FailActiveButRetain(string error)
        {
            lastError = Sanitize(error);
            status = "Display refresh failed; waiting for the active worker to settle";
            requestCancelled = true;
            Action<string> callback = failureCallback;
            failureCallback = null;
            successCallback = null;
            InvokeFailure(callback, lastError);
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] ascii = Array.FindAll(value.ToCharArray(), character => character >= 0x20 && character <= 0x7e);
            return new string(ascii);
        }
    }
}
