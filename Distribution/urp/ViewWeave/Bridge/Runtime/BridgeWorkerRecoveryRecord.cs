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
        [SerializeField] bool previewLease;
        [SerializeField] uint previewLeaseIdentityStructSize;
        [SerializeField] uint previewLeaseIdentityVersion;
        [SerializeField] long previewLeaseEndpointIdBits;
        [SerializeField] long previewLeaseGenerationBits;
        [SerializeField] bool previewLeaseReturnRequested;
        [SerializeField] bool previewLeaseReturnProven;
        [SerializeField] bool previewLeaseAcknowledged;
        [SerializeField] bool finalModuleTeardownProven;
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
        internal bool IsPreviewLease => previewLease;
        internal BridgeWorkerNative.PreviewLeaseIdentityV1 PreviewLeaseIdentity => new BridgeWorkerNative.PreviewLeaseIdentityV1
        {
            structSize = previewLeaseIdentityStructSize,
            version = previewLeaseIdentityVersion,
            endpointId = unchecked((ulong)previewLeaseEndpointIdBits),
            leaseGeneration = unchecked((ulong)previewLeaseGenerationBits)
        };
        internal bool HasValidPreviewLeaseIdentity => previewLeaseIdentityStructSize == 24 && previewLeaseIdentityVersion == 1 &&
            previewLeaseEndpointIdBits != 0 && previewLeaseGenerationBits != 0;
        internal bool PreviewLeaseReturnRequested { get => previewLeaseReturnRequested; set => previewLeaseReturnRequested |= value; }
        internal bool PreviewLeaseReturnProven { get => previewLeaseReturnProven; set => previewLeaseReturnProven |= value; }
        internal bool PreviewLeaseAcknowledged { get => previewLeaseAcknowledged; set => previewLeaseAcknowledged |= value; }
        internal bool FinalModuleTeardownProven { get => finalModuleTeardownProven; set => finalModuleTeardownProven |= value; }
        internal bool NativeContextOwnershipReleased => previewLease
            ? previewLeaseAcknowledged || finalModuleTeardownProven
            : nativeReleased;
        internal BridgeGpuProofMode GpuProofMode => gpuProofMode;
        internal bool HasGpuReleaseProof => !hadTransferTexture ||
            (gpuProofMode == BridgeGpuProofMode.ReadbackTail && readbackCompleted && readbackValid) ||
            (gpuProofMode == BridgeGpuProofMode.FenceAndReadback && gpuFenceIssued && gpuFencePassed && readbackCompleted && readbackValid);
        internal bool CanRelease => (previewLease
            ? finalModuleTeardownProven || previewLeaseAcknowledged && previewLeaseReturnProven
            : nativeReleased) && HasGpuReleaseProof;
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

        internal static BridgeWorkerRecoveryRecord CreatePreviewLease(IntPtr handle, RenderTexture texture, bool returnPending, long generation,
            BridgeNativeModuleIdentity moduleIdentity, BridgeWorkerNative.PreviewLeaseIdentityV1 leaseIdentity)
        {
            BridgeWorkerRecoveryRecord record = Create(handle, texture, returnPending, generation, moduleIdentity);
            record.previewLease = true;
            record.previewLeaseIdentityStructSize = leaseIdentity.structSize;
            record.previewLeaseIdentityVersion = leaseIdentity.version;
            record.previewLeaseEndpointIdBits = unchecked((long)leaseIdentity.endpointId);
            record.previewLeaseGenerationBits = unchecked((long)leaseIdentity.leaseGeneration);
            record.diagnostic = "Native preview lease is retained until return, GPU, and owner-release proofs complete.";
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

        internal bool NotifyEditorPreviewOwnerReturnProven()
        {
            return editorPreviewOwner == null || editorPreviewOwner.MarkWorkerReturnProven(this);
        }

        internal bool EditorPreviewCoreResourcesReleased => editorPreviewOwner == null || editorPreviewOwner.CoreResourcesReleased;
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

        internal bool RetirePreviewLeaseTransferTextureAfterProof()
        {
            if (!previewLease || !(previewLeaseReturnProven || finalModuleTeardownProven) || !HasGpuReleaseProof) return false;
#if UNITY_EDITOR
            if (!EditorPreviewCoreResourcesReleased) return false;
#endif
            ReleaseTransferTexture();
            return transferTexture == null;
        }

        internal void ReleaseProvenResources()
        {
            if (!CanRelease) return;
            ReleaseTransferTexture();
            nativeHandle = 0;
            SetDiagnostic(previewLease
                ? previewLeaseAcknowledged
                    ? "Preview lease was acknowledged and its transfer texture was released after completion proof."
                    : "Preview lease was invalidated by matching final module shutdown and its transfer texture was released after completion proof."
                : "Native worker and transfer texture were released after completion proof.");
        }

        void ReleaseTransferTexture()
        {
            if (transferTexture == null) return;
            if (RenderTexture.active == transferTexture) RenderTexture.active = null;
            if (transferTexture.IsCreated()) transferTexture.Release();
            DestroyImmediate(transferTexture);
            transferTexture = null;
        }
    }
}
