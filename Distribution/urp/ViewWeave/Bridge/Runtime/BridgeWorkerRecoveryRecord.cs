using System;
using UnityEngine;

namespace ViewWeave
{
    internal sealed class BridgeWorkerRecoveryRecord : ScriptableObject
    {
        [SerializeField] long nativeHandle;
        [SerializeField] long generation;
        [SerializeField] bool legacySession;
        [SerializeField] long legacySessionIdBits;
        [SerializeField] bool legacyCloseQueued;
        [SerializeField] bool hadTransferTexture;
        [SerializeField] RenderTexture transferTexture;
        [SerializeField] bool needsClose;
        [SerializeField] bool nativeReleased;
        [SerializeField] bool gpuFenceIssued;
        [SerializeField] bool gpuFencePassed;
        [SerializeField] bool readbackCompleted;
        [SerializeField] bool readbackValid;
        [SerializeField] string diagnostic = string.Empty;

        internal long NativeHandle => nativeHandle;
        internal bool IsLegacySession => legacySession;
        internal ulong LegacySessionId => unchecked((ulong)legacySessionIdBits);
        internal bool LegacyCloseQueued { get => legacyCloseQueued; set => legacyCloseQueued |= value; }
        internal long Generation => generation;
        internal RenderTexture TransferTexture => transferTexture;
        internal bool HadTransferTexture => hadTransferTexture;
        internal bool NeedsClose { get => needsClose; set => needsClose = value; }
        internal bool NativeReleased { get => nativeReleased; set => nativeReleased = value; }
        internal bool HasGpuReleaseProof => !hadTransferTexture ||
            (gpuFenceIssued && gpuFencePassed && readbackCompleted && readbackValid);
        internal bool CanRelease => nativeReleased && HasGpuReleaseProof;
        internal string Diagnostic => diagnostic;

        internal static BridgeWorkerRecoveryRecord Create(IntPtr handle, RenderTexture texture, bool closePending, long generation)
        {
            BridgeWorkerRecoveryRecord record = CreateInstance<BridgeWorkerRecoveryRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.nativeHandle = handle.ToInt64();
            record.hadTransferTexture = !ReferenceEquals(texture, null);
            record.transferTexture = texture;
            record.needsClose = closePending;
            record.generation = generation;
            record.diagnostic = "Native worker lease is retained until native and GPU completion are proven.";
            return record;
        }

        internal static BridgeWorkerRecoveryRecord CreateLegacy(ulong session, RenderTexture texture, bool closeQueued, long generation)
        {
            BridgeWorkerRecoveryRecord record = CreateInstance<BridgeWorkerRecoveryRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.legacySession = true;
            record.legacySessionIdBits = unchecked((long)session);
            record.hadTransferTexture = !ReferenceEquals(texture, null);
            record.transferTexture = texture;
            record.legacyCloseQueued = closeQueued;
            record.generation = generation;
            record.diagnostic = "Legacy Bridge session is retained until native close and GPU completion are proven.";
            return record;
        }

        internal IntPtr GetHandle() { return new IntPtr(nativeHandle); }

        internal void SetGpuProof(bool fenceIssued, bool fencePassed, bool requestDone, bool requestValid)
        {
            gpuFenceIssued = fenceIssued;
            gpuFencePassed = fencePassed;
            readbackCompleted = requestDone;
            readbackValid = requestValid;
            diagnostic = HasGpuReleaseProof
                ? "GPU completion was proven for the transfer texture."
                : "GPU completion is unproven; transfer texture remains retained.";
        }

        internal void SetDiagnostic(string value)
        {
            if (string.IsNullOrEmpty(value)) { diagnostic = string.Empty; return; }
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            diagnostic = new string(chars.ToArray());
        }

        internal void ReleaseProvenResources()
        {
            if (!CanRelease) return;
            if (transferTexture != null)
            {
                if (RenderTexture.active == transferTexture) RenderTexture.active = null;
                if (transferTexture.IsCreated()) transferTexture.Release();
                DestroyImmediate(transferTexture);
                transferTexture = null;
            }
            nativeHandle = 0;
            SetDiagnostic("Native worker and transfer texture were released after completion proof.");
        }
    }
}
