#if UNITY_EDITOR
using UnityEngine;

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
        [SerializeField] bool readyResetSucceeded;
        [SerializeField] long reaperHandoffGeneration;
        [SerializeField] string diagnostic = string.Empty;

        internal bool HasCompleteProof => sourceTexture != null && tailFenceIssued && tailFencePassed && readbackCompleted && readbackValid &&
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
        internal void SetReadback(bool completed, bool valid) { readbackCompleted = completed; readbackValid = valid; }
        internal void SetReadyReset(bool value) { readyResetSucceeded = value; }
        internal void SetReaperHandoff(long generation) { reaperHandoffGeneration = generation; }
        internal void SetDiagnostic(string value) { diagnostic = Sanitize(value); }

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
