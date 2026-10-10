using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave.Editor
{
    // Serialized ownership survives reloads; an unproven GPU tail never releases a texture.
    internal sealed class QuiltEditorResourceRecord : ScriptableObject, IQuiltEditorResourceRetentionOwner
    {
        [SerializeField] GameObject producer;
        [SerializeField] QuiltRenderer renderer;
        [SerializeField] List<RenderTexture> textures = new List<RenderTexture>();
        [SerializeField] bool stopped, rendered, complete;
        [SerializeField] string failure;
        [NonSerialized] int pending;
        [NonSerialized] bool requested;
        [NonSerialized] bool failed;
        [NonSerialized] IDisposable mutationOwner;

        internal bool IsStopped => stopped;
        internal string Failure => failure ?? string.Empty;
        internal static int PendingCount => Resources.FindObjectsOfTypeAll<QuiltEditorResourceRecord>().Length;

        internal static QuiltEditorResourceRecord Create(GameObject host = null, QuiltRenderer value = null)
        {
            var record = CreateInstance<QuiltEditorResourceRecord>();
            record.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            record.producer = host;
            record.renderer = value;
            if (value != null)
            {
                record.mutationOwner = QuiltRendererMutationPolicy.RegisterOwner(value, record, _ => string.Empty, out string error);
                if (record.mutationOwner == null) throw new InvalidOperationException(error);
            }
            return record;
        }

        public bool TrackTexture(RenderTexture texture)
        {
            if (stopped || texture == null) return false;
            texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            if (!textures.Contains(texture)) textures.Add(texture);
            return true;
        }

        internal void MarkRendered() => rendered = true;

        public void RequestStopAndTakeOwnership(QuiltRenderer value)
        {
            stopped = true;
            // Hidden Edit Mode producers do not receive normal MonoBehaviour disable/destroy
            // callbacks reliably. Explicitly end the pipeline session before retiring the host.
            if (value != null) value.ReleasePipelineRenderResources();
            if (value != null && !value.IsRendering)
            {
                value.autoRender = false;
                foreach (RenderTexture texture in value.DetachOwnedRenderTextures())
                    if (texture != null && !textures.Contains(texture)) textures.Add(texture);
            }
        }

        internal void Stop()
        {
            RequestStopAndTakeOwnership(renderer);
            Poll();
        }

        void Poll()
        {
            if (!stopped) return;
            if (renderer != null && renderer.IsRendering) return;
            if (!rendered) complete = true;
            if (!complete && !requested && !failed)
            {
                if (!SystemInfo.supportsAsyncGPUReadback) { Fail("Editor GPU retirement proof is unavailable; resources remain retained."); return; }
                requested = true;
                var live = textures.FindAll(t => t != null && t.IsCreated());
                pending = live.Count;
                if (pending == 0) complete = true;
                foreach (RenderTexture texture in live)
                {
                    try
                    {
                        AsyncGPUReadback.Request(texture, 0, 0, 1, 0, 1, 0, 1, request =>
                        {
                            if (this == null) return;
                            if (!request.done || request.hasError) Fail("Editor GPU retirement failed; resources remain retained. Restart Unity before retrying.");
                            --pending;
                            if (pending == 0 && !failed) complete = true;
                        });
                    }
                    catch (Exception) { --pending; Fail("Editor GPU retirement request failed; resources remain retained. Restart Unity before retrying."); }
                }
                EditorApplication.QueuePlayerLoopUpdate();
            }
            if (!complete || failed) return;
            foreach (RenderTexture texture in textures)
            {
                if (texture == null) continue;
                if (RenderTexture.active == texture) RenderTexture.active = null;
                texture.Release();
                DestroyImmediate(texture);
            }
            textures.Clear();
            if (producer != null) DestroyImmediate(producer);
            mutationOwner?.Dispose();
            mutationOwner = null;
            DestroyImmediate(this);
        }

        void Fail(string reason) { failed = true; failure = reason; }

        internal static void StopAll()
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<QuiltEditorResourceRecord>()) record.Stop();
        }

        internal static void PollAll()
        {
            foreach (var record in Resources.FindObjectsOfTypeAll<QuiltEditorResourceRecord>()) record.Poll();
        }

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            // Managed callback latches are lost on reload; submit fresh ordered proofs.
            foreach (var record in Resources.FindObjectsOfTypeAll<QuiltEditorResourceRecord>())
            {
                record.requested = false;
                record.pending = 0;
                record.stopped = true;
                record.failed = !string.IsNullOrEmpty(record.failure);
            }
            EditorApplication.update += PollAll;
            AssemblyReloadEvents.beforeAssemblyReload += StopAll;
            EditorApplication.quitting += StopAll;
        }
    }
}
