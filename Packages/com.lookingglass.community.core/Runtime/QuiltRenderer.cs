using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace LookingGlass.Community
{
    [DisallowMultipleComponent]
    public sealed class QuiltRenderer : MonoBehaviour
    {
        public QuiltSettings settings = new QuiltSettings();
        public Camera sourceCamera;
        RenderTexture quiltTexture;
        public RenderTexture QuiltTexture => quiltTexture;
        [SerializeField] MonoBehaviour outputBehaviour;
        [SerializeField] MonoBehaviour guardBehaviour;
        [FormerlySerializedAs("renderInLateUpdate")]
        public bool autoRender;
        [Obsolete("Use autoRender; automatic rendering now runs after scene rendering.")]
        public bool renderInLateUpdate { get => autoRender; set => autoRender = value; }
        public IQuiltOutput Output => outputBehaviour as IQuiltOutput;
        public IQuiltRenderGuard RenderGuard => guardBehaviour as IQuiltRenderGuard;
        public MonoBehaviour OutputBehaviour { get => outputBehaviour; set => outputBehaviour = value; }
        public MonoBehaviour GuardBehaviour { get => guardBehaviour; set => guardBehaviour = value; }
        public bool useRenderRequests = true;
        public bool IsRendering { get; private set; }
        public string LastAutoRenderError { get; private set; }
        public long LastCpuSubmissionTicks { get; private set; }
        public long LastViewCpuTicks { get; private set; }

        RenderTexture viewTexture;
        RenderTexture previousActive;

        void OnEnable()
        {
            ResetAutoRenderError();
            if (sourceCamera == null) sourceCamera = GetComponent<Camera>();
            QuiltAutoRenderScheduler.Register(this);
        }
        public void ResetAutoRenderError()
        {
            autoRenderFailed = false;
            LastAutoRenderError = string.Empty;
        }
        bool autoRenderFailed;
        internal void RenderAutoQuilt()
        {
            if (!autoRender || !isActiveAndEnabled || autoRenderFailed || IsRendering) return;
            try { RenderQuilt(); }
            catch (Exception error)
            {
                autoRenderFailed = true;
                autoRender = false;
                LastAutoRenderError = SanitizeAutoRenderError(error);
                UnityEngine.Debug.LogError("Looking Glass quilt auto render stopped: " + LastAutoRenderError);
            }
        }

        static string SanitizeAutoRenderError(Exception error)
        {
            string value = error == null ? "unknown error" : error.GetType().Name + ": " + error.Message;
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }
        void OnDisable() { QuiltAutoRenderScheduler.Unregister(this); ReleaseTextures(); }
        void OnDestroy() { QuiltAutoRenderScheduler.Unregister(this); ReleaseTextures(); }

        public void RenderQuilt()
        {
            if (IsRendering) throw new InvalidOperationException("quilt render is already in progress");
            if (Camera.current != null) throw new InvalidOperationException("RenderQuilt must be called outside render callbacks");
            settings.Validate();
            if (sourceCamera == null) throw new InvalidOperationException("sourceCamera is required");
            if (sourceCamera.enabled) throw new InvalidOperationException("sourceCamera must be disabled and dedicated to quilt rendering");
            if (sourceCamera.orthographic) throw new InvalidOperationException("only perspective cameras are supported");
            bool srp = GraphicsSettings.currentRenderPipeline != null;
            if (srp && !useRenderRequests) throw new NotSupportedException("an SRP adapter and render request are required");
            if (srp && RenderGuard == null) throw new InvalidOperationException("an IQuiltRenderGuard is required for SRP rendering");
            IsRendering = true;
            previousActive = RenderTexture.active;
            CameraState state = default;
            bool stateCaptured = false;
            try
            {
                QuiltLayout layout = settings.GetLayout();
                EnsureTextures(layout);
                ClearQuilt();
                state = new CameraState(sourceCamera);
                stateCaptured = true;
                if (srp)
                {
                    var initializationRequest = new RenderPipeline.StandardRequest { destination = viewTexture };
                    if (!RenderPipeline.SupportsRenderRequest(sourceCamera, initializationRequest))
                        throw new NotSupportedException("active render pipeline does not support standard camera requests");
                }
                Stopwatch total = Stopwatch.StartNew();
                Stopwatch viewTimer = new Stopwatch();
                sourceCamera.enabled = false;
                for (int i = 0; i < layout.ViewCount; ++i)
                {
                    viewTimer.Restart();
                    RectInt tile = layout.GetTile(i);
                    float offset = QuiltMath.ViewOffset(i, layout.ViewCount, settings.viewCone, settings.cameraDistance);
                    sourceCamera.transform.position = state.position + state.rotation * Vector3.right * offset;
                    sourceCamera.transform.rotation = QuiltMath.ParallelViewRotation(state.rotation);
                    sourceCamera.projectionMatrix = QuiltMath.OffAxisProjection(settings.fieldOfView, settings.viewAspect, state.nearClip, state.farClip, offset, settings.cameraDistance);
                    sourceCamera.targetTexture = viewTexture;
                    sourceCamera.aspect = settings.viewAspect;
                    sourceCamera.rect = new Rect(0f, 0f, 1f, 1f);
                    IDisposable viewGuard = srp ? RenderGuard.Begin(sourceCamera) : null;
                    try { RenderOneView(); }
                    finally { if (viewGuard != null) viewGuard.Dispose(); }
                    CopyTile(tile);
                    viewTimer.Stop();
                    LastViewCpuTicks = viewTimer.ElapsedTicks;
                }
                total.Stop();
                LastCpuSubmissionTicks = total.ElapsedTicks;
                Output?.Submit(quiltTexture, layout);
            }
            finally
            {
                if (stateCaptured) state.Restore(sourceCamera);
                RenderTexture.active = previousActive;
                IsRendering = false;
            }
        }

        void RenderOneView()
        {
            if (useRenderRequests && GraphicsSettings.currentRenderPipeline != null)
            {
                var request = new RenderPipeline.StandardRequest { destination = viewTexture };
                if (!RenderPipeline.SupportsRenderRequest(sourceCamera, request)) throw new NotSupportedException("active render pipeline does not support standard camera requests");
                RenderPipeline.SubmitRenderRequest(sourceCamera, request);
            }
            else sourceCamera.Render();
        }

        void CopyTile(RectInt tile)
        {
            if (!SystemInfo.copyTextureSupport.HasFlag(CopyTextureSupport.Basic)) throw new NotSupportedException("Graphics.CopyTexture is not supported");
            Graphics.CopyTexture(viewTexture, 0, 0, 0, 0, tile.width, tile.height, quiltTexture, 0, 0, tile.x, tile.y);
        }

        void ClearQuilt()
        {
            RenderTexture old = RenderTexture.active;
            RenderTexture.active = quiltTexture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = old;
        }

        void EnsureTextures(QuiltLayout layout)
        {
            if (layout.QuiltWidth > SystemInfo.maxTextureSize || layout.QuiltHeight > SystemInfo.maxTextureSize || layout.TileWidth > SystemInfo.maxTextureSize || layout.TileHeight > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException("quilt dimensions exceed the device maximum texture size");
            if (quiltTexture == null || quiltTexture.width != layout.QuiltWidth || quiltTexture.height != layout.QuiltHeight)
            {
                ReleaseOwnedTexture(ref quiltTexture);
                quiltTexture = new RenderTexture(layout.QuiltWidth, layout.QuiltHeight, 0, RenderTextureFormat.ARGB32) { name = "LookingGlass.Quilt" };
                quiltTexture.Create();
                if (!quiltTexture.IsCreated()) throw new InvalidOperationException("failed to create quilt render texture");
            }
            if (viewTexture == null || viewTexture.width != layout.TileWidth || viewTexture.height != layout.TileHeight)
            {
                ReleaseOwnedTexture(ref viewTexture);
                viewTexture = new RenderTexture(layout.TileWidth, layout.TileHeight, 24, RenderTextureFormat.ARGB32) { name = "LookingGlass.View" };
                viewTexture.Create();
                if (!viewTexture.IsCreated()) throw new InvalidOperationException("failed to create view render texture");
            }
        }

        void ReleaseTextures()
        {
            ReleaseOwnedTexture(ref viewTexture);
            ReleaseOwnedTexture(ref quiltTexture);
        }

        void ReleaseOwnedTexture(ref RenderTexture texture)
        {
            if (texture == null) return;
            if (RenderTexture.active == texture) RenderTexture.active = null;
            if (previousActive == texture) previousActive = null;
            texture.Release();
            DisposeTexture(texture);
            texture = null;
        }

        void DisposeTexture(RenderTexture texture)
        {
            if (Application.isPlaying) Destroy(texture);
            else DestroyImmediate(texture);
        }

        readonly struct CameraState
        {
            public readonly Vector3 position; public readonly Quaternion rotation; public readonly Matrix4x4 projection; public readonly RenderTexture target; public readonly Rect rect; public readonly float aspect, nearClip, farClip; public readonly bool enabled;
            public CameraState(Camera c) { position = c.transform.position; rotation = c.transform.rotation; projection = c.projectionMatrix; target = c.targetTexture; rect = c.rect; aspect = c.aspect; nearClip = c.nearClipPlane; farClip = c.farClipPlane; enabled = c.enabled; }
            public void Restore(Camera c) { c.transform.SetPositionAndRotation(position, rotation); c.projectionMatrix = projection; c.targetTexture = target; c.rect = rect; c.aspect = aspect; c.enabled = enabled; }
        }
    }
}
