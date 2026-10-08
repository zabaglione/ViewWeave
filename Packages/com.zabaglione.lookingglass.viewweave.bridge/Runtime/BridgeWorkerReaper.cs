using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    internal sealed class BridgeWorkerReaper : MonoBehaviour
    {
        static BridgeWorkerReaper instance;
        sealed class Lease
        {
            internal BridgeWorkerRecoveryRecord record;
            internal CommandBuffer command;
            internal GraphicsFence fence;
            internal AsyncGPUReadbackRequest readback;
            internal bool proofStarted;
            internal bool warned;
        }

        readonly List<Lease> leases = new List<Lease>();
        static long nextGeneration;
        static bool finalShutdownOwnsNativeContexts;
        internal static int PendingCount => FindRecords().Length;
        internal static int OwnedNativeHandleCount
        {
            get
            {
                int count = 0;
                foreach (BridgeWorkerRecoveryRecord record in FindRecords())
                    if (record != null && !record.IsLegacySession && record.NativeHandle != 0 && !record.NativeReleased) count++;
                return count;
            }
        }

        internal static bool HasUnprovenResources
        {
            get
            {
                foreach (BridgeWorkerRecoveryRecord record in FindRecords())
                    if (record != null && !record.CanRelease) return true;
                return false;
            }
        }

        internal static void PrepareForFinalShutdown()
        {
            BridgeWorkerReaper reaper = Ensure();
            reaper.RestoreRecords();
            finalShutdownOwnsNativeContexts = true;
            foreach (Lease lease in reaper.leases)
                if (lease.record.NativeReleased && !lease.proofStarted) StartGpuProof(lease);
        }

        internal static void MarkFinalShutdownCollected()
        {
            BridgeWorkerReaper reaper = Ensure();
            reaper.RestoreRecords();
            foreach (Lease lease in reaper.leases)
            {
                if (lease.record.IsLegacySession) continue;
                lease.record.NativeReleased = true;
                lease.record.NeedsClose = false;
            }
        }

        internal static bool PumpNow()
        {
            BridgeWorkerReaper reaper = instance;
            if (reaper == null && FindRecords().Length == 0) return true;
            if (reaper == null) reaper = Ensure();
            reaper.RestoreRecords();
            return reaper.PumpLeases();
        }

        internal static void PrepareForDomainReload(bool waitForGpuProof)
        {
            BridgeWorkerReaper reaper = Ensure();
            reaper.RestoreRecords();
            if (waitForGpuProof) reaper.PumpLeases();
            foreach (Lease lease in reaper.leases)
            {
                if (lease.record.IsLegacySession && !lease.record.NativeReleased && !reaper.TryAcknowledgeLegacyClose(lease))
                {
                    lease.record.SetDiagnostic("Legacy session close is not yet proven safe before domain reload; resources remain retained.");
                    Warn(lease, lease.record.Diagnostic);
                    continue;
                }
                if (!lease.record.NativeReleased) continue;
                if (waitForGpuProof && !reaper.CompleteGpuProofSynchronously(lease))
                {
                    lease.record.SetDiagnostic("Domain reload could not prove transfer texture GPU completion; resources remain retained.");
                    Warn(lease, lease.record.Diagnostic);
                    continue;
                }
                if (lease.record.HasGpuReleaseProof) ReleaseCommand(lease);
                else StartGpuProof(lease);
            }
        }

        internal static void Enqueue(IntPtr handle, RenderTexture texture, CommandBuffer command, bool needsClose)
        {
            if (handle == IntPtr.Zero) return;
            BridgeWorkerReaper reaper = Ensure();
            foreach (Lease lease in reaper.leases)
            {
                if (lease.record != null && lease.record.NativeHandle == handle.ToInt64())
                {
                    if (lease.command == null) lease.command = command;
                    lease.record.NeedsClose |= needsClose;
                    return;
                }
            }
            BridgeWorkerRecoveryRecord record = FindRecord(handle.ToInt64());
            if (record == null) record = BridgeWorkerRecoveryRecord.Create(handle, texture, needsClose, ++nextGeneration);
            else record.NeedsClose |= needsClose;
            var created = new Lease { record = record, command = command };
            reaper.leases.Add(created);
            reaper.RestoreRecords();
        }

        internal static void EnqueueLegacySessionClose(ulong session, RenderTexture texture, CommandBuffer command, bool closeQueued)
        {
            if (session == 0) return;
            BridgeWorkerReaper reaper = Ensure();
            if (closeQueued)
            {
                foreach (Lease lease in reaper.leases)
                    if (lease.record != null && lease.record.IsLegacySession && lease.record.LegacySessionId == session)
                        lease.record.LegacyCloseQueued = true;
            }
            foreach (Lease lease in reaper.leases)
            {
                BridgeWorkerRecoveryRecord existing = lease.record;
                if (existing == null || !existing.IsLegacySession || existing.LegacySessionId != session) continue;
                if (ReferenceEquals(existing.TransferTexture, texture))
                {
                    existing.LegacyCloseQueued = closeQueued;
                    if (lease.command == null) lease.command = command;
                    return;
                }
            }
            BridgeWorkerRecoveryRecord record = BridgeWorkerRecoveryRecord.CreateLegacy(session, texture, closeQueued, ++nextGeneration);
            reaper.leases.Add(new Lease { record = record, command = command });
            reaper.RestoreRecords();
        }

        static BridgeWorkerReaper Ensure()
        {
            if (instance != null)
            {
                PreserveWhilePlaying(instance.gameObject);
                return instance;
            }
            BridgeWorkerReaper[] existing = Resources.FindObjectsOfTypeAll<BridgeWorkerReaper>();
            foreach (BridgeWorkerReaper reaper in existing)
            {
                if (reaper == null) continue;
                instance = reaper;
                PreserveWhilePlaying(instance.gameObject);
                instance.RestoreRecords();
                return instance;
            }
            var host = new GameObject("ViewWeave Bridge Worker Reaper");
            host.hideFlags = HideFlags.HideAndDontSave;
            PreserveWhilePlaying(host);
            instance = host.AddComponent<BridgeWorkerReaper>();
            instance.RestoreRecords();
            return instance;
        }

        static void PreserveWhilePlaying(GameObject host)
        {
            if (Application.isPlaying) DontDestroyOnLoad(host);
        }

        void Awake()
        {
            instance = this;
            PreserveWhilePlaying(gameObject);
            RestoreRecords();
        }

        void RestoreRecords()
        {
            BridgeWorkerRecoveryRecord[] records = FindRecords();
            foreach (BridgeWorkerRecoveryRecord record in records)
            {
                if (record == null || FindLease(record)) continue;
                leases.Add(new Lease { record = record });
                if (record.Generation > nextGeneration) nextGeneration = record.Generation;
            }
        }

        static BridgeWorkerRecoveryRecord[] FindRecords()
        {
            return Resources.FindObjectsOfTypeAll<BridgeWorkerRecoveryRecord>();
        }

        static BridgeWorkerRecoveryRecord FindRecord(long handle)
        {
            foreach (BridgeWorkerRecoveryRecord record in FindRecords())
                if (record != null && record.NativeHandle == handle) return record;
            return null;
        }

        bool FindLease(BridgeWorkerRecoveryRecord record)
        {
            foreach (Lease lease in leases) if (ReferenceEquals(lease.record, record)) return true;
            return false;
        }

        void Update()
        {
            PumpLeases();
        }

        bool PumpLeases()
        {
            for (int i = leases.Count - 1; i >= 0; --i)
            {
                Lease lease = leases[i];
                if (lease.record == null) { leases.RemoveAt(i); continue; }
                try
                {
                    if (lease.record.IsLegacySession && !lease.record.NativeReleased)
                    {
                        if (!TryAcknowledgeLegacyClose(lease))
                        {
                            PumpGpuProof(lease);
                            continue;
                        }
                    }
                    if (lease.record.IsLegacySession)
                    {
                        if (!lease.proofStarted) StartGpuProof(lease);
                        PumpGpuProof(lease);
                        if (!lease.record.CanRelease) continue;
                        ReleaseCommand(lease);
                        lease.record.ReleaseProvenResources();
                        DestroyImmediate(lease.record);
                        leases.RemoveAt(i);
                        continue;
                    }
                    if (!finalShutdownOwnsNativeContexts && !lease.record.NativeReleased && lease.record.NeedsClose)
                    {
                        int closeResult = BridgeWorkerNative.lkg_worker_close(lease.record.GetHandle());
                        if (closeResult != 0) { Warn(lease, "worker close is still pending"); continue; }
                        lease.record.NeedsClose = false;
                    }
                    BridgeWorkerNative.Status status = default;
                    if (!finalShutdownOwnsNativeContexts && !lease.record.NativeReleased && BridgeWorkerNative.lkg_worker_poll(lease.record.GetHandle(), out status) != 0)
                    {
                        int closeResult = BridgeWorkerNative.lkg_worker_close(lease.record.GetHandle());
                        if (closeResult != 0) Warn(lease, "worker poll/close is still pending");
                        continue;
                    }
                    if (!finalShutdownOwnsNativeContexts && !lease.record.NativeReleased && status.state == 2)
                    {
                        int closeResult = BridgeWorkerNative.lkg_worker_close(lease.record.GetHandle());
                        if (closeResult != 0) Warn(lease, "worker close is still pending");
                    }
                    if (!finalShutdownOwnsNativeContexts && !lease.record.NativeReleased)
                    {
                        if (status.state != 2 && status.state != 4) continue;
                        int result = BridgeWorkerNative.lkg_worker_release(lease.record.GetHandle());
                        if (result == -2) continue;
                        if (result != 0) { Warn(lease, "worker release returned a pending error"); continue; }
                        lease.record.NativeReleased = true;
                    }
                    if (!lease.record.NativeReleased) continue;
                    if (!lease.proofStarted) StartGpuProof(lease);
                    PumpGpuProof(lease);
                    if (!lease.record.CanRelease) continue;
                    ReleaseCommand(lease);
                    lease.record.ReleaseProvenResources();
                    DestroyImmediate(lease.record);
                    leases.RemoveAt(i);
                }
                catch (DllNotFoundException) { Warn(lease, "worker release ABI is unavailable"); }
                catch (EntryPointNotFoundException) { Warn(lease, "worker release entry point is unavailable"); }
                catch (Exception error)
                {
                    Warn(lease, "reaper retry failed: " + Sanitize(error.Message));
                }
            }
            return leases.Count == 0;
        }

        bool TryAcknowledgeLegacyClose(Lease lease)
        {
            ulong session = lease.record.LegacySessionId;
            var status = new BridgeNative.SessionCloseStatusV1
            {
                structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeNative.SessionCloseStatusV1)),
                version = 1,
                sessionId = session
            };
            int queryResult = BridgeNative.lkg_bridge_get_session_close_status_v1(session, ref status);
            if (queryResult != 0 || !BridgeWorkerShutdownPolicy.IsValidLegacyCloseStatus(session, status) ||
                status.structSize != (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeNative.SessionCloseStatusV1)))
            {
                lease.record.SetDiagnostic("Legacy session close status is unavailable or invalid; resources remain retained.");
                Warn(lease, lease.record.Diagnostic);
                return false;
            }
            if (status.state == 0) return false;
            if (!BridgeWorkerShutdownPolicy.IsLegacyCloseSafe(session, status))
            {
                lease.record.SetDiagnostic("Legacy session close was not proven safe; resources remain retained. Result=" + status.lastResult);
                Warn(lease, lease.record.Diagnostic);
                return false;
            }
            int acknowledgeResult = BridgeNative.lkg_bridge_ack_session_close_v1(session);
            if (acknowledgeResult != 0)
            {
                lease.record.SetDiagnostic("Legacy session close acknowledgement failed; resources remain retained. Result=" + acknowledgeResult);
                Warn(lease, lease.record.Diagnostic);
                return false;
            }
            foreach (Lease sibling in leases)
            {
                if (sibling.record != null && sibling.record.IsLegacySession && sibling.record.LegacySessionId == session)
                    sibling.record.NativeReleased = true;
            }
            return true;
        }

        static void StartGpuProof(Lease lease)
        {
            if (lease.proofStarted) return;
            if (lease.record != null && lease.record.HasGpuReleaseProof)
            {
                lease.proofStarted = true;
                return;
            }
            lease.proofStarted = true;
            RenderTexture texture = lease.record == null ? null : lease.record.TransferTexture;
            if (texture == null)
            {
                if (lease.record.HadTransferTexture)
                {
                    lease.record.SetDiagnostic("Transfer texture reference was lost before GPU completion could be proven.");
                    Warn(lease, lease.record.Diagnostic);
                    return;
                }
                lease.record.SetGpuProof(true, true, true, true);
                return;
            }
            try
            {
                if (!texture.IsCreated()) throw new InvalidOperationException("transfer texture is not created");
                var fenceCommand = new CommandBuffer { name = "ViewWeave Bridge Reaper Fence" };
                try
                {
                    lease.fence = fenceCommand.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                    Graphics.ExecuteCommandBuffer(fenceCommand);
                }
                finally { fenceCommand.Release(); }
                lease.readback = AsyncGPUReadback.Request(texture, 0, 0, 1, 0, 1, 0, 1);
                lease.record.SetGpuProof(true, false, false, false);
            }
            catch (Exception error)
            {
                lease.record.SetDiagnostic("Could not start transfer texture GPU proof: " + Sanitize(error.Message));
                Warn(lease, lease.record.Diagnostic);
            }
        }

        void PumpGpuProof(Lease lease)
        {
            if (lease.record == null || lease.record.HasGpuReleaseProof || !lease.proofStarted) return;
            try
            {
                bool fencePassed = lease.fence.passed;
                bool requestDone = lease.readback.done;
                bool requestValid = requestDone && !lease.readback.hasError;
                lease.record.SetGpuProof(true, fencePassed, requestDone, requestValid);
            }
            catch (Exception error)
            {
                lease.record.SetDiagnostic("Transfer texture GPU proof status is unknown: " + Sanitize(error.Message));
                Warn(lease, lease.record.Diagnostic);
            }
        }

        bool CompleteGpuProofSynchronously(Lease lease)
        {
            StartGpuProof(lease);
            if (lease.record == null) return true;
            if (lease.record.HasGpuReleaseProof) return true;
            if (lease.record.TransferTexture == null) return !lease.record.HadTransferTexture && lease.record.HasGpuReleaseProof;
            try
            {
                if (!lease.readback.done) lease.readback.WaitForCompletion();
                bool requestDone = lease.readback.done;
                bool requestValid = requestDone && !lease.readback.hasError;
                bool fencePassed = lease.fence.passed;
                lease.record.SetGpuProof(true, fencePassed, requestDone, requestValid);
                return lease.record.HasGpuReleaseProof;
            }
            catch (Exception error)
            {
                lease.record.SetDiagnostic("Synchronous GPU proof failed: " + Sanitize(error.Message));
                return false;
            }
        }

        static void ReleaseCommand(Lease lease)
        {
            if (lease.command == null) return;
            lease.command.Release();
            lease.command = null;
        }

        static void Warn(Lease lease, string message)
        {
            if (lease.warned) return;
            lease.warned = true;
            Debug.LogWarning("[BridgeWorker] " + Sanitize(message));
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new List<char>(value.Length);
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars.Add(c);
            return new string(chars.ToArray());
        }
    }
}
