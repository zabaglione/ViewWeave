#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    internal sealed class BridgePlaybackRecoveryRecord : ScriptableObject
    {
        [SerializeField] RenderTexture sourceTexture;
        [SerializeField] long sessionGeneration;
        [SerializeField] int lastFrame = -1;
        [SerializeField] int skippedFrames;
        [SerializeField] bool tailFenceIssued;
        [SerializeField] bool tailFencePassed;
        [SerializeField] bool readbackCompleted;
        [SerializeField] bool readbackValid;
        [SerializeField] BridgeGpuProofMode gpuProofMode;
        [SerializeField] bool readyResetSucceeded;
        [SerializeField] long reaperHandoffGeneration;
        [SerializeField] string diagnostic = string.Empty;

        internal BridgeGpuProofMode GpuProofMode => gpuProofMode;
        internal bool HasCompleteProof => sourceTexture != null &&
            ((gpuProofMode == BridgeGpuProofMode.ReadbackTail && tailFenceIssued && readbackCompleted && readbackValid) ||
             (gpuProofMode == BridgeGpuProofMode.FenceAndReadback && tailFenceIssued && tailFencePassed && readbackCompleted && readbackValid)) &&
            (readyResetSucceeded || reaperHandoffGeneration == sessionGeneration);

        internal static BridgePlaybackRecoveryRecord Create(RenderTexture texture, long generation)
        {
            BridgePlaybackRecoveryRecord record = CreateInstance<BridgePlaybackRecoveryRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.sourceTexture = texture;
            record.sessionGeneration = generation;
            record.diagnostic = "Playback lease is active.";
            return record;
        }

        internal void SetFrame(int frame, int skipped)
        {
            lastFrame = frame;
            skippedFrames = skipped;
        }

        internal void SetTailFenceIssued(bool value) { tailFenceIssued = value; }
        internal void SetTailFencePassed(bool value) { tailFencePassed = value; }
        internal void SetGpuProofMode(BridgeGpuProofMode mode) { gpuProofMode = mode; tailFenceIssued = false; tailFencePassed = false; readbackCompleted = false; readbackValid = false; }
        internal void BeginTailProof()
        {
            tailFenceIssued = false;
            tailFencePassed = false;
            readbackCompleted = false;
            readbackValid = false;
        }
        internal void SetReadback(bool completed, bool valid) { readbackCompleted = completed; readbackValid = valid; }
        internal void SetReadyReset(bool value) { readyResetSucceeded = value; }
        internal void SetReaperHandoff(long generation) { reaperHandoffGeneration = generation; }
        internal void SetDiagnostic(string value) { diagnostic = Sanitize(value); }

        internal bool TryReproveAfterDomainReload()
        {
            if (sourceTexture == null || !sourceTexture.IsCreated()) return false;
            BridgeGpuProofMode mode = BridgeGraphicsBackendPolicy.SelectCurrentGpuProofMode();
            if (mode == BridgeGpuProofMode.Unsupported)
            {
                gpuProofMode = mode;
                tailFenceIssued = false;
                tailFencePassed = false;
                readbackCompleted = false;
                readbackValid = false;
                SetDiagnostic("No supported GPU proof mode is available after Editor reload; playback source remains retained.");
                return false;
            }

            gpuProofMode = mode;
            tailFenceIssued = false;
            tailFencePassed = false;
            readbackCompleted = false;
            readbackValid = false;
            try
            {
                GraphicsFence fence = default;
                if (mode == BridgeGpuProofMode.FenceAndReadback)
                {
                    var command = new CommandBuffer { name = "ViewWeave Playback Reload Proof Fence" };
                    try
                    {
                        fence = command.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation, SynchronisationStageFlags.AllGPUOperations);
                        Graphics.ExecuteCommandBuffer(command);
                    }
                    finally { command.Release(); }
                }

                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(sourceTexture, 0);
                tailFenceIssued = true;
                request.WaitForCompletion();
                readbackCompleted = request.done;
                readbackValid = request.done && !request.hasError;
                if (mode == BridgeGpuProofMode.FenceAndReadback && readbackValid)
                    tailFencePassed = fence.passed;
                SetDiagnostic(HasCompleteProof
                    ? "Playback GPU ownership was reproven after Editor reload."
                    : "Fresh playback GPU proof after Editor reload is incomplete; source texture remains retained.");
                return HasCompleteProof;
            }
            catch (System.Exception exception)
            {
                SetDiagnostic("Playback GPU proof after Editor reload failed: " + exception.Message);
                return false;
            }
        }

        internal void ReleaseProvenSource()
        {
            if (!HasCompleteProof || sourceTexture == null) return;
            if (RenderTexture.active == sourceTexture) RenderTexture.active = null;
            if (sourceTexture.IsCreated()) sourceTexture.Release();
            DestroyImmediate(sourceTexture);
            sourceTexture = null;
            diagnostic = "Playback source texture was released after GPU completion proof.";
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }
    }
}
#endif
