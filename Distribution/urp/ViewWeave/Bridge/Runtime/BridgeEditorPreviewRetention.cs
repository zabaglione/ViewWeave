using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    internal static class BridgeEditorPreviewRetention
    {
        internal sealed class ReadbackCompletionLatch
        {
            internal enum Outcome { Pending, Succeeded, Failed }

            readonly object attemptIdentity;
            readonly Outcome[] outcomes;

            internal ReadbackCompletionLatch(object identity, int count)
            {
                if (identity == null) throw new ArgumentNullException(nameof(identity));
                if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
                attemptIdentity = identity;
                outcomes = new Outcome[count];
            }

            internal Outcome GetOutcome(int index) => outcomes[index];

            internal bool TryRecord(object identity, int index, bool succeeded)
            {
                if (!ReferenceEquals(identity, attemptIdentity) || index < 0 || index >= outcomes.Length || outcomes[index] != Outcome.Pending)
                    return false;
                outcomes[index] = succeeded ? Outcome.Succeeded : Outcome.Failed;
                return true;
            }

            internal bool AllComplete
            {
                get
                {
                    foreach (Outcome outcome in outcomes)
                        if (outcome == Outcome.Pending) return false;
                    return true;
                }
            }

            internal bool AllSucceeded
            {
                get
                {
                    foreach (Outcome outcome in outcomes)
                        if (outcome != Outcome.Succeeded) return false;
                    return true;
                }
            }
        }

        internal readonly struct CallbackRegistration
        {
            internal readonly Func<bool> Stop;
            internal readonly Action<BridgeEditorPreviewRetentionRecord> Detach;

            internal CallbackRegistration(Func<bool> stop, Action<BridgeEditorPreviewRetentionRecord> detach)
            {
                Stop = stop;
                Detach = detach;
            }
        }

        sealed class ProofAttempt
        {
            internal BridgeGpuProofMode mode;
            internal GraphicsFence fence;
            internal AsyncGPUReadbackRequest[] readbacks;
            internal ReadbackCompletionLatch readbackCompletion;
            internal bool fencePassed;
            internal bool flushAttempted;
        }

        static readonly Dictionary<BridgeEditorPreviewRetentionRecord, ProofAttempt> Attempts = new Dictionary<BridgeEditorPreviewRetentionRecord, ProofAttempt>();
        static Action<BridgeEditorPreviewRetentionRecord> ensureDetached;
        static Func<bool> stopOwner;

        internal static int PendingCount => FindRecords().Length;
        internal static bool HasPending => PendingCount != 0;
        internal static bool HasStoppingRecords
        {
            get
            {
                foreach (BridgeEditorPreviewRetentionRecord record in FindRecords())
                    if (record != null && record.StopRequested) return true;
                return false;
            }
        }

        internal static void RegisterEditorCallbacks(Func<bool> stop, Action<BridgeEditorPreviewRetentionRecord> detach)
        {
            stopOwner = stop;
            ensureDetached = detach;
        }

        internal static CallbackRegistration CaptureCallbacks() => new CallbackRegistration(stopOwner, ensureDetached);

        internal static void RestoreCallbacks(CallbackRegistration callbacks)
        {
            stopOwner = callbacks.Stop;
            ensureDetached = callbacks.Detach;
        }

        internal static void InvalidateProof(BridgeEditorPreviewRetentionRecord record)
        {
            if (record != null) Attempts.Remove(record);
        }

        internal static bool RequestStopFromCore(BridgeEditorPreviewRetentionRecord record, QuiltRenderer renderer)
        {
            if (record == null || !ReferenceEquals(record.Renderer, renderer)) return false;
            if (record.StopRequested) return false;
            bool completed = RequestStop(false);
            if (!record.StopRequested)
                record.SetDiagnostic("Core requested preview stop, but the Editor owner did not accept the request; textures remain retained.");
            return completed;
        }

        internal static bool MarkOrphanedRecordsStopRequested()
        {
            bool marked = false;
            foreach (BridgeEditorPreviewRetentionRecord record in FindRecords())
            {
                if (record == null || record.StopRequested) continue;
                record.MarkStopRequested();
                marked = true;
            }
            return marked;
        }

        internal static bool RequestStop(bool waitForProof)
        {
            bool hasActive = HasActiveRecords();
            if (stopOwner == null)
            {
                if (hasActive) return false;
            }
            else
            {
                try
                {
                    stopOwner();
                    if (HasActiveRecords()) return false;
                }
                catch (Exception error)
                {
                    Debug.LogError("[ViewWeave] Editor preview stop request failed: " + Sanitize(error.Message));
                    if (hasActive || HasActiveRecords()) return false;
                }
            }
            return Pump(waitForProof);
        }

        internal static bool Pump(bool waitForProof = false)
        {
            bool complete = true;
            foreach (BridgeEditorPreviewRetentionRecord record in FindRecords())
            {
                if (record == null) continue;
                try
                {
                    if (!record.StopRequested) { complete = false; continue; }
                    if (!record.Detached)
                    {
                        if (ensureDetached == null) { record.SetDiagnostic("Editor preview detach owner is unavailable; resources remain retained."); complete = false; continue; }
                        ensureDetached(record);
                        if (!record.Detached) { complete = false; continue; }
                    }
                    if (!record.GpuProofComplete && !PumpGpuProof(record, waitForProof)) { complete = false; continue; }
                    Attempts.Remove(record);
                    if (record.CanReleaseCoreResources && !record.CoreResourcesReleased)
                        record.ReleaseCoreResourcesAfterProof();
                    if (record.CanRelease) record.ReleaseAfterProof();
                    else complete = false;
                }
                catch (Exception error)
                {
                    record.SetDiagnostic("Editor preview proof or release failed: " + Sanitize(error.Message));
                    complete = false;
                }
            }
            return complete && !HasPending;
        }

        internal static void ResetProofsAfterDomainReload()
        {
            Attempts.Clear();
            foreach (BridgeEditorPreviewRetentionRecord record in FindRecords())
                if (record != null) record.ResetGpuProofAfterReload();
        }

        static bool PumpGpuProof(BridgeEditorPreviewRetentionRecord record, bool waitForProof)
        {
            RenderTexture[] textures = record.Textures;
            if (textures.Length == 0) { record.SetGpuProof(true, true, true); return true; }
            if (record.GpuProofMode == BridgeGpuProofMode.Unsupported)
                record.SetGpuProofMode(BridgeGraphicsBackendPolicy.SelectCurrentGpuProofMode());
            BridgeGpuProofMode proofMode = record.GpuProofMode;
            if (proofMode == BridgeGpuProofMode.Unsupported)
            {
                record.SetDiagnostic("No safe GPU proof mode is supported; preview textures remain retained.");
                return false;
            }

            if (!Attempts.TryGetValue(record, out ProofAttempt attempt))
            {
                try
                {
                    foreach (RenderTexture texture in textures)
                        if (texture == null || !texture.IsCreated()) throw new InvalidOperationException("retained preview texture is missing or not created");
                    var command = new CommandBuffer { name = "ViewWeave Editor Preview Retention Fence" };
                    try
                    {
                        attempt = new ProofAttempt
                        {
                            mode = proofMode,
                            readbacks = new AsyncGPUReadbackRequest[textures.Length]
                        };
                        if (proofMode == BridgeGpuProofMode.FenceAndReadback)
                            attempt.fence = command.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                        attempt.readbackCompletion = new ReadbackCompletionLatch(attempt, textures.Length);
                        if (proofMode == BridgeGpuProofMode.FenceAndReadback) Graphics.ExecuteCommandBuffer(command);
                    }
                    finally { command.Release(); }
                    Attempts.Add(record, attempt);
                    for (int i = 0; i < textures.Length; i++)
                    {
                        int requestIndex = i;
                        attempt.readbacks[i] = AsyncGPUReadback.Request(textures[i], 0,
                            request => RecordReadbackResult(record, attempt, requestIndex, !request.hasError));
                    }
                }
                catch (Exception error)
                {
                    record.SetDiagnostic("Could not start preview GPU proof: " + Sanitize(error.Message));
                    if (attempt != null && attempt.readbackCompletion != null)
                    {
                        for (int i = 0; i < attempt.readbacks.Length; i++)
                            attempt.readbackCompletion.TryRecord(attempt, i, false);
                        if (!Attempts.ContainsKey(record)) Attempts.Add(record, attempt);
                    }
                    return false;
                }
            }

            try
            {
                if (!attempt.flushAttempted && BridgeWorkerEditorGpuFlush.SynchronousWaitActive)
                    BridgeWorkerEditorGpuFlush.SubmitIfNeeded(ref attempt.flushAttempted);
                if (waitForProof)
                {
                    if (!attempt.flushAttempted)
                    {
                        attempt.flushAttempted = true;
                        GL.Flush();
                    }
                    for (int i = 0; i < attempt.readbacks.Length; i++)
                    {
                        if (attempt.readbackCompletion.GetOutcome(i) != ReadbackCompletionLatch.Outcome.Pending) continue;
                        if (!attempt.readbacks[i].done) attempt.readbacks[i].WaitForCompletion();
                        if (attempt.readbacks[i].done)
                            RecordReadbackResult(record, attempt, i, !attempt.readbacks[i].hasError);
                    }
                }
                else
                {
                    for (int i = 0; i < attempt.readbacks.Length; i++)
                    {
                        if (attempt.readbackCompletion.GetOutcome(i) != ReadbackCompletionLatch.Outcome.Pending) continue;
                        attempt.readbacks[i].Update();
                        if (attempt.readbacks[i].done)
                            RecordReadbackResult(record, attempt, i, !attempt.readbacks[i].hasError);
                    }
                }
                if (attempt.mode == BridgeGpuProofMode.FenceAndReadback && !attempt.fencePassed)
                    attempt.fencePassed = attempt.fence.passed;
                record.SetGpuProof(attempt.fencePassed, attempt.readbackCompletion.AllComplete, attempt.readbackCompletion.AllSucceeded);
                return record.GpuProofComplete;
            }
            catch (Exception error)
            {
                record.SetDiagnostic("Preview GPU proof status is unknown: " + Sanitize(error.Message));
                return false;
            }
        }

        static void RecordReadbackResult(BridgeEditorPreviewRetentionRecord record, ProofAttempt attempt, int index, bool succeeded)
        {
            if (record == null || attempt == null || !Attempts.TryGetValue(record, out ProofAttempt current) || !ReferenceEquals(current, attempt)) return;
            if (!attempt.readbackCompletion.TryRecord(attempt, index, succeeded)) return;
            if (!succeeded) record.SetDiagnostic("A retained preview render texture readback failed; resources remain retained.");
        }

        static BridgeEditorPreviewRetentionRecord[] FindRecords()
        {
            return Resources.FindObjectsOfTypeAll<BridgeEditorPreviewRetentionRecord>();
        }

        static bool HasActiveRecords()
        {
            foreach (BridgeEditorPreviewRetentionRecord record in FindRecords())
                if (record != null && !record.StopRequested) return true;
            return false;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }
    }
}
