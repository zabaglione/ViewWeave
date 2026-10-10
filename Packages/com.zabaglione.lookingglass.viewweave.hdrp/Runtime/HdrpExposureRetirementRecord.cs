using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ViewWeave.HDRP
{
    // Serialized ownership keeps an unproven GPU tail recoverable across an Editor reload.
    internal sealed class HdrpExposureRetirementRecord : ScriptableObject
    {
        static readonly List<HdrpExposureRetirementRecord> Records = new List<HdrpExposureRetirementRecord>();

        [SerializeField] GameObject cameraObject;
        [SerializeField] Material sampleMaterial;
        [SerializeField] RenderTexture renderTarget;
        [SerializeField] RenderTexture sampleTarget;
        [SerializeField] bool submitted, stopped;
        [SerializeField] string failure;
        [NonSerialized] Readback[] readbacks;
        [NonSerialized] bool requested;
        [NonSerialized] bool createdInCurrentDomain;
        [NonSerialized] bool released;

        internal static int RecordCount => Records.Count;

        internal static HdrpExposureRetirementRecord Create(GameObject camera, Material material,
            RenderTexture rendered, RenderTexture sampled)
        {
            var record = CreateInstance<HdrpExposureRetirementRecord>();
            record.name = "ViewWeave HDRP Exposure Retirement";
            record.hideFlags = HideFlags.HideAndDontSave;
            record.cameraObject = camera;
            record.sampleMaterial = material;
            record.renderTarget = rendered;
            record.sampleTarget = sampled;
            record.createdInCurrentDomain = true;
            Records.Add(record);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= PollAll;
            UnityEditor.EditorApplication.update += PollAll;
#endif
            return record;
        }

        // Mark before entering SubmitRenderRequest, which can submit GPU work before throwing.
        internal void MarkSubmitted()
        {
            if (stopped) throw new InvalidOperationException("HDRP exposure resources have already been retired.");
            submitted = true;
        }

        internal void Retire()
        {
            if (this == null || stopped) return;
            stopped = true;
            StopRendering();
            Poll();
        }

        void StopRendering()
        {
            if (cameraObject == null) return;
            var camera = cameraObject.GetComponent<Camera>();
            if (camera != null) camera.enabled = false;
            var volume = cameraObject.GetComponent<CustomPassVolume>();
            if (volume != null) volume.enabled = false;
        }

        void Poll()
        {
            if (!stopped) return;
            if (!submitted) { Release(); return; }
            if (!requested && string.IsNullOrEmpty(failure)) StartReadbacks();
            if (this == null || readbacks == null) return;
            var current = readbacks;
            foreach (Readback readback in current)
            {
                if (this == null) return;
                if (!readback.issued || readback.complete) continue;
                try
                {
                    readback.request.Update();
                    if (!readback.complete && readback.request.done)
                        Complete(readback, readback.request);
                }
                catch (Exception exception) { Fail("GPU retirement status could not be read: " + exception.Message); }
            }
            if (this != null) TryRelease();
        }

        void StartReadbacks()
        {
            requested = true;
            if (!SystemInfo.supportsAsyncGPUReadback || renderTarget == null || sampleTarget == null ||
                !renderTarget.IsCreated() || !sampleTarget.IsCreated())
            {
                Fail("GPU retirement requires both owned render textures and asynchronous readback support.");
                return;
            }

            // Both targets are read after the final render, including a partially failed render.
            // Register the complete set before queuing; callbacks may complete during submission.
            readbacks = new[] { new Readback(renderTarget), new Readback(sampleTarget) };
            foreach (Readback readback in readbacks)
            {
                try
                {
                    readback.request = AsyncGPUReadback.Request(readback.texture, 0, 0, 1, 0, 1, 0, 1,
                        result => Complete(readback, result));
                    readback.issued = true;
                }
                catch (Exception exception)
                {
                    readback.complete = true;
                    Fail("GPU retirement readback could not be queued: " + exception.Message);
                }
            }
        }

        void Complete(Readback readback, AsyncGPUReadbackRequest request)
        {
            if (this == null || readbacks == null || readback.complete || Array.IndexOf(readbacks, readback) < 0) return;
            readback.complete = true;
            readback.succeeded = request.done && !request.hasError;
            if (!readback.succeeded) Fail("GPU retirement readback failed.");
            TryRelease();
        }

        void TryRelease()
        {
            if (!stopped || !string.IsNullOrEmpty(failure) || readbacks == null) return;
            foreach (Readback readback in readbacks)
                if (!readback.complete || !readback.succeeded) return;
            Release();
        }

        void Fail(string reason)
        {
            if (!string.IsNullOrEmpty(failure)) return;
            failure = reason;
            Debug.LogWarning("HDRP exposure resources remain retained until GPU completion is proven. " + reason);
        }

        void Release()
        {
            if (released) return;
            released = true;
            if (cameraObject != null)
            {
                var camera = cameraObject.GetComponent<Camera>();
                if (camera != null) camera.targetTexture = null;
                DestroyOwned(cameraObject);
            }
            DestroyOwned(sampleMaterial);
            ReleaseTexture(renderTarget);
            ReleaseTexture(sampleTarget);
            cameraObject = null;
            sampleMaterial = null;
            renderTarget = null;
            sampleTarget = null;
            readbacks = null;
            Records.Remove(this);
#if UNITY_EDITOR
            if (Records.Count == 0) UnityEditor.EditorApplication.update -= PollAll;
#endif
            DestroyOwned(this);
        }

        static void ReleaseTexture(RenderTexture texture)
        {
            if (texture == null) return;
            if (RenderTexture.active == texture) RenderTexture.active = null;
            texture.Release();
            DestroyOwned(texture);
        }

        static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
#if UNITY_EDITOR
            // Reload can occur while playing. After proven completion, destroy immediately so
            // the native objects cannot outlive their ownership record across that boundary.
            DestroyImmediate(value);
#else
            CoreUtils.Destroy(value);
#endif
        }

        sealed class Readback
        {
            internal readonly RenderTexture texture;
            internal AsyncGPUReadbackRequest request;
            internal bool issued, complete, succeeded;
            internal Readback(RenderTexture texture) { this.texture = texture; }
        }

#if UNITY_EDITOR
        // Ordinary stop stays asynchronous. Only assembly reload drains this record's requests;
        // it never waits for GPU requests owned by another renderer or package.
        void DrainForAssemblyReload()
        {
            Retire();
            if (this != null) Poll();
            if (this == null || readbacks == null) return;
            var current = readbacks;
            foreach (Readback readback in current)
            {
                if (this == null) return;
                if (!readback.issued || readback.complete) continue;
                try
                {
                    readback.request.WaitForCompletion();
                    if (!readback.complete) Complete(readback, readback.request);
                }
                catch (Exception exception) { Fail("GPU retirement could not finish before assembly reload: " + exception.Message); }
            }
            if (this != null) TryRelease();
        }

        static void PollAll()
        {
            // A callback can remove a record during Poll. A skipped record is polled next update.
            for (int index = Records.Count - 1; index >= 0; index--)
            {
                if (index >= Records.Count) continue;
                var record = Records[index];
                if (record == null) { Records.RemoveAt(index); continue; }
                record.Poll();
            }
        }

        static void DrainBeforeAssemblyReload()
        {
            foreach (var record in Records.ToArray())
                if (record != null) record.DrainForAssemblyReload();
        }

        static void StopBeforeEditorQuit()
        {
            // On an unsuccessful proof, retain ownership through graphics-context shutdown.
            foreach (var record in Records.ToArray())
                if (record != null) record.Retire();
        }

        [UnityEditor.InitializeOnLoadMethod]
        static void RecoverAfterAssemblyReload()
        {
            Records.Clear();
            foreach (var record in Resources.FindObjectsOfTypeAll<HdrpExposureRetirementRecord>())
            {
                // Old managed callbacks are gone. Preserve serialized Unity object ownership,
                // then establish new ordered GPU completion proof instead of assuming success.
                if (!record.createdInCurrentDomain)
                {
                    record.readbacks = null;
                    record.requested = false;
                    record.failure = null;
                    record.stopped = true;
                    record.StopRendering();
                }
                Records.Add(record);
            }
            UnityEditor.EditorApplication.update -= PollAll;
            if (Records.Count > 0) UnityEditor.EditorApplication.update += PollAll;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= DrainBeforeAssemblyReload;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DrainBeforeAssemblyReload;
            UnityEditor.EditorApplication.quitting -= StopBeforeEditorQuit;
            UnityEditor.EditorApplication.quitting += StopBeforeEditorQuit;
        }
#endif
    }
}
