using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    // Serialized ownership survives an Editor domain reload. No image is read back during streaming.
    internal sealed class BridgeMetalRetirementRecord : ScriptableObject
    {
        [SerializeField] long nativeSession;
        [SerializeField] RenderTexture texture;
        [SerializeField] Material material;
        [SerializeField] bool stopped, rendered, tailComplete;
        [SerializeField] string failure;
        [NonSerialized] bool readbackPending, proofFailed;

        internal static BridgeMetalRetirementRecord Create(ulong session, RenderTexture source, Material interlace)
        {
            var record = CreateInstance<BridgeMetalRetirementRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.nativeSession = (long)session; record.texture = source; record.material = interlace;
            source.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            interlace.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            EnsureReaper();
            return record;
        }

        internal void MarkRendered() { rendered = true; }
        internal void Stop() { stopped = true; Poll(); }

        void Poll()
        {
            if (!stopped) return;
            if (!rendered || texture == null) tailComplete = true;
            // A one-pixel queue-tail readback proves Unity has finished with the resource, including
            // an admitted frame whose plugin event was cancelled before it reached the render thread.
            if (!tailComplete && !readbackPending && !proofFailed)
            {
                if (!SystemInfo.supportsAsyncGPUReadback) { SetFailure("GPU retirement proof is unavailable; resources remain retained"); return; }
                readbackPending = true;
                try
                {
                    AsyncGPUReadback.Request(texture, 0, 0, 1, 0, 1, 0, 1, request =>
                    {
                        if (this == null) return;
                        readbackPending = false; tailComplete = request.done && !request.hasError;
                        if (!tailComplete) SetFailure("GPU retirement readback failed; resources remain retained; restart Unity before retrying");
                        else failure = string.Empty;
                    });
                }
                catch (Exception) { readbackPending = false; SetFailure("GPU retirement proof failed; resources remain retained; restart Unity before retrying"); }
            }
            if (!tailComplete) return;
            try { if (BridgeMetalQuiltOutput.NativeFramePending((ulong)nativeSession)) return; }
            catch (Exception) { return; } // Unproven native ownership retains the resource.
            if (texture != null)
            {
                if (RenderTexture.active == texture) RenderTexture.active = null;
                texture.Release(); DestroyImmediate(texture); texture = null;
            }
            if (material != null) { DestroyImmediate(material); material = null; }
            DestroyImmediate(this);
        }

        internal static void PollAll()
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<BridgeMetalRetirementRecord>()) record.Poll();
        }
        internal static bool IsRetiring(ulong session)
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<BridgeMetalRetirementRecord>())
                if (record.stopped && record.nativeSession == (long)session) return true;
            return false;
        }
        internal static int PendingCount
        {
            get
            {
                int count = 0;
                foreach (var record in Resources.FindObjectsOfTypeAll<BridgeMetalRetirementRecord>())
                    if (record.stopped) ++count;
                return count;
            }
        }
        void SetFailure(string error)
        {
            proofFailed = true; failure = error;
            Debug.LogWarning(error);
        }
        internal static string Failure(ulong session)
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<BridgeMetalRetirementRecord>())
                if (record.stopped && record.nativeSession == (long)session && !string.IsNullOrEmpty(record.failure)) return record.failure;
            return string.Empty;
        }
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        static void EditorInitialize()
        {
            UnityEditor.EditorApplication.update -= PollAll;
            UnityEditor.EditorApplication.update += PollAll;
        }
#endif
        static void EnsureReaper()
        {
#if !UNITY_EDITOR
            if (Resources.FindObjectsOfTypeAll<BridgeMetalResourceReaper>().Length != 0) return;
            var owner = new GameObject("ViewWeave Metal Resource Reaper") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(owner); owner.AddComponent<BridgeMetalResourceReaper>();
#endif
        }
    }
}
