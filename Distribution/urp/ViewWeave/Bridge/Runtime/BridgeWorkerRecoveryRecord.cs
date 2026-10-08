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
        [SerializeField] string nativeModulePath = string.Empty;
        [SerializeField] long nativeModuleBaseAddress;
        [SerializeField] bool gpuFenceIssued;
        [SerializeField] bool gpuFencePassed;
        [SerializeField] bool readbackCompleted;
        [SerializeField] bool readbackValid;
        [SerializeField] BridgeGpuProofMode gpuProofMode;
        [SerializeField] string diagnostic = string.Empty;
#if UNITY_EDITOR
        [SerializeField] BridgeEditorPreviewRetentionRecord editorPreviewOwner;
#endif

        internal long NativeHandle => nativeHandle;
        internal BridgeNativeModuleIdentity NativeModuleIdentity =>
            new BridgeNativeModuleIdentity("lookingglass_bridge_shim.dll", new IntPtr(nativeModuleBaseAddress), nativeModulePath);
        internal bool IsLegacySession => legacySession;
        internal ulong LegacySessionId => unchecked((ulong)legacySessionIdBits);
        internal bool LegacyCloseQueued { get => legacyCloseQueued; set => legacyCloseQueued |= value; }
        internal long Generation => generation;
        internal RenderTexture TransferTexture => transferTexture;
        internal bool HadTransferTexture => hadTransferTexture;
        internal bool NeedsClose { get => needsClose; set => needsClose = value; }
        internal bool NativeReleased { get => nativeReleased; set => nativeReleased = value; }
        internal BridgeGpuProofMode GpuProofMode => gpuProofMode;
        internal bool HasGpuReleaseProof => !hadTransferTexture ||
            (gpuProofMode == BridgeGpuProofMode.ReadbackTail && readbackCompleted && readbackValid) ||
            (gpuProofMode == BridgeGpuProofMode.FenceAndReadback && gpuFenceIssued && gpuFencePassed && readbackCompleted && readbackValid);
        internal bool CanRelease => nativeReleased && HasGpuReleaseProof;
        internal string Diagnostic => diagnostic;

        internal static BridgeWorkerRecoveryRecord Create(IntPtr handle, RenderTexture texture, bool closePending, long generation,
            BridgeNativeModuleIdentity moduleIdentity)
        {
            BridgeWorkerRecoveryRecord record = CreateInstance<BridgeWorkerRecoveryRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.nativeHandle = handle.ToInt64();
            record.hadTransferTexture = !ReferenceEquals(texture, null);
            record.transferTexture = texture;
            record.needsClose = closePending;
            record.generation = generation;
            record.nativeModulePath = moduleIdentity.FullPath;
            record.nativeModuleBaseAddress = moduleIdentity.BaseAddress.ToInt64();
            record.diagnostic = "Native worker lease is retained until native and GPU completion are proven.";
            return record;
        }

        internal static BridgeWorkerRecoveryRecord Create(IntPtr handle, RenderTexture texture, bool closePending, long generation)
        {
            return Create(handle, texture, closePending, generation, default);
        }

        internal static BridgeWorkerRecoveryRecord CreateLegacy(ulong session, RenderTexture texture, bool closeQueued, long generation,
            BridgeNativeModuleIdentity moduleIdentity)
        {
            BridgeWorkerRecoveryRecord record = CreateInstance<BridgeWorkerRecoveryRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.legacySession = true;
            record.legacySessionIdBits = unchecked((long)session);
            record.hadTransferTexture = !ReferenceEquals(texture, null);
            record.transferTexture = texture;
            record.legacyCloseQueued = closeQueued;
            record.generation = generation;
            record.nativeModulePath = moduleIdentity.FullPath;
            record.nativeModuleBaseAddress = moduleIdentity.BaseAddress.ToInt64();
            record.diagnostic = "Legacy Bridge session is retained until native close and GPU completion are proven.";
            return record;
        }

        internal static BridgeWorkerRecoveryRecord CreateLegacy(ulong session, RenderTexture texture, bool closeQueued, long generation)
        {
            return CreateLegacy(session, texture, closeQueued, generation, default);
        }

#if UNITY_EDITOR
        internal bool AttachEditorPreviewOwner(BridgeEditorPreviewRetentionRecord owner)
        {
            if (owner == null) return false;
            if (editorPreviewOwner != null && !ReferenceEquals(editorPreviewOwner, owner)) return false;
            if (!owner.AttachWorkerRecoveryRecord(this)) return false;
            editorPreviewOwner = owner;
            return true;
        }

        internal bool NotifyEditorPreviewOwnerComplete()
        {
            return editorPreviewOwner == null || editorPreviewOwner.MarkWorkerRecoveryComplete(this);
        }
#endif

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

        internal void SetGpuProofMode(BridgeGpuProofMode mode)
        {
            gpuProofMode = mode;
            gpuFenceIssued = false;
            gpuFencePassed = false;
            readbackCompleted = false;
            readbackValid = false;
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
