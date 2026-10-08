using System;
using System.Collections.Generic;
using UnityEngine;

namespace ViewWeave
{
#if UNITY_EDITOR
    internal sealed class BridgeEditorPreviewRetentionRecord : ScriptableObject, IQuiltEditorResourceRetentionOwner
#else
    internal sealed class BridgeEditorPreviewRetentionRecord : ScriptableObject
#endif
    {
        [SerializeField] GameObject producer;
        [SerializeField] QuiltRenderer renderer;
        [SerializeField] RenderTexture[] textures = Array.Empty<RenderTexture>();
        [SerializeField] BridgeWorkerRecoveryRecord workerRecoveryRecord;
        [SerializeField] bool workerProofExpected;
        [SerializeField] bool workerRecoveryAttached;
        [SerializeField] bool workerRecoveryComplete = true;
        [SerializeField] bool stopRequested;
        [SerializeField] bool detached;
        [SerializeField] bool gpuFencePassed;
        [SerializeField] bool gpuReadbackComplete;
        [SerializeField] bool gpuReadbackValid;
        [SerializeField] BridgeGpuProofMode gpuProofMode;
        [SerializeField] string diagnostic = "Editor preview resources are retained until GPU and worker completion are proven.";
        bool releaseInProgress;

        internal GameObject Producer => producer;
        internal QuiltRenderer Renderer => renderer;
        internal RenderTexture[] Textures => textures ?? Array.Empty<RenderTexture>();
        internal BridgeWorkerRecoveryRecord WorkerRecoveryRecord => workerRecoveryRecord;
        internal bool WorkerHandoffComplete => !workerProofExpected || workerRecoveryAttached;
        internal bool WorkerRecoveryComplete => !workerProofExpected || workerRecoveryComplete;
        internal bool WorkerProofExpectedForDiagnostics => workerProofExpected;
        internal bool StopRequested => stopRequested;
        internal bool Detached => detached;
        internal BridgeGpuProofMode GpuProofMode => gpuProofMode;
        internal bool GpuProofComplete => textures == null || textures.Length == 0 ||
            (gpuProofMode == BridgeGpuProofMode.ReadbackTail && gpuReadbackComplete && gpuReadbackValid) ||
            (gpuProofMode == BridgeGpuProofMode.FenceAndReadback && gpuFencePassed && gpuReadbackComplete && gpuReadbackValid);
        internal bool CanRelease => stopRequested && detached && WorkerHandoffComplete && WorkerRecoveryComplete && GpuProofComplete;
        internal string Diagnostic => diagnostic ?? string.Empty;

        internal static BridgeEditorPreviewRetentionRecord Create(GameObject owner, QuiltRenderer quiltRenderer)
        {
            BridgeEditorPreviewRetentionRecord record = CreateInstance<BridgeEditorPreviewRetentionRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.producer = owner;
            record.renderer = quiltRenderer;
            return record;
        }

#if UNITY_EDITOR
        public bool TrackTexture(RenderTexture texture)
        {
            if (texture == null || stopRequested || renderer == null) return false;
            if (Array.IndexOf(Textures, texture) >= 0) return true;
            texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            var tracked = new List<RenderTexture>(Textures.Length + 1);
            tracked.AddRange(Textures);
            tracked.Add(texture);
            textures = tracked.ToArray();
            detached = false;
            gpuFencePassed = false;
            gpuReadbackComplete = false;
            gpuReadbackValid = false;
            BridgeEditorPreviewRetention.InvalidateProof(this);
            return true;
        }

        public void RequestStopAndTakeOwnership(QuiltRenderer requestedRenderer)
        {
            if (!ReferenceEquals(renderer, requestedRenderer))
            {
                SetDiagnostic("Core requested preview ownership transfer for an unregistered renderer.");
                return;
            }
            BridgeEditorPreviewRetention.RequestStopFromCore(this, requestedRenderer);
        }
#endif

        internal void SetWorkerProofExpected(bool expected)
        {
            workerProofExpected = expected;
            if (expected) workerRecoveryComplete = false;
            else if (!workerRecoveryAttached) workerRecoveryComplete = true;
        }

        internal bool AttachWorkerRecoveryRecord(BridgeWorkerRecoveryRecord recoveryRecord)
        {
            if (recoveryRecord == null) return false;
            if (workerRecoveryRecord != null && !ReferenceEquals(workerRecoveryRecord, recoveryRecord)) return false;
            workerRecoveryRecord = recoveryRecord;
            workerProofExpected = true;
            workerRecoveryAttached = true;
            workerRecoveryComplete = false;
            return true;
        }

        internal bool MarkWorkerRecoveryComplete(BridgeWorkerRecoveryRecord recoveryRecord)
        {
            if (recoveryRecord == null || !ReferenceEquals(workerRecoveryRecord, recoveryRecord)) return false;
            workerRecoveryComplete = true;
            return true;
        }

        internal void MarkStopRequested()
        {
            stopRequested = true;
        }

        internal bool SetTextures(RenderTexture[] value)
        {
            if (stopRequested == false) return false;
            var all = new List<RenderTexture>(Textures.Length + (value == null ? 0 : value.Length));
            all.AddRange(Textures);
            if (value != null)
                foreach (RenderTexture texture in value)
                    if (texture != null && !all.Contains(texture)) all.Add(texture);
            textures = all.ToArray();
            for (int i = 0; i < textures.Length; i++)
                if (textures[i] != null) textures[i].hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            detached = true;
            gpuFencePassed = textures.Length == 0;
            gpuReadbackComplete = gpuFencePassed;
            gpuReadbackValid = gpuFencePassed;
            return true;
        }

        internal void SetGpuProof(bool fencePassed, bool requestDone, bool requestValid)
        {
            gpuFencePassed = fencePassed;
            gpuReadbackComplete = requestDone;
            gpuReadbackValid = requestValid;
            diagnostic = GpuProofComplete
                ? gpuProofMode == BridgeGpuProofMode.ReadbackTail
                    ? "Editor preview render textures passed graphics queue tail readback proof."
                    : "Editor preview render textures passed GPU fence and readback proof."
                : "Editor preview render texture GPU proof is incomplete; resources remain retained.";
        }

        internal void SetGpuProofMode(BridgeGpuProofMode mode)
        {
            gpuProofMode = mode;
            gpuFencePassed = false;
            gpuReadbackComplete = false;
            gpuReadbackValid = false;
        }

        internal void ResetGpuProofAfterReload()
        {
            gpuProofMode = BridgeGpuProofMode.Unsupported;
            gpuFencePassed = textures != null && textures.Length == 0;
            gpuReadbackComplete = gpuFencePassed;
            gpuReadbackValid = gpuFencePassed;
            diagnostic = "Transient GPU proof state was reset after Editor reload; retained textures require a new proof.";
        }

        internal void SetDiagnostic(string message)
        {
            if (string.IsNullOrEmpty(message)) { diagnostic = string.Empty; return; }
            var chars = new List<char>(message.Length);
            foreach (char value in message) if (value >= 0x20 && value <= 0x7e) chars.Add(value);
            diagnostic = new string(chars.ToArray());
        }

        internal void ReleaseAfterProof()
        {
            if (!CanRelease || releaseInProgress) return;
            releaseInProgress = true;
            foreach (RenderTexture texture in Textures)
            {
                if (texture == null) continue;
                if (RenderTexture.active == texture) RenderTexture.active = null;
                if (texture.IsCreated()) texture.Release();
                DestroyImmediate(texture);
            }
            textures = Array.Empty<RenderTexture>();
            if (producer != null) DestroyImmediate(producer);
            producer = null;
            renderer = null;
            workerRecoveryRecord = null;
            diagnostic = "Editor preview resources were released after independent GPU and worker proof.";
            DestroyImmediate(this);
        }
    }
}
