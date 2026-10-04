using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace ViewWeave
{
    [DisallowMultipleComponent]
    public sealed class QuiltRenderer : MonoBehaviour
    {
        public QuiltSettings settings = new QuiltSettings();
        [Tooltip("The output quilt stays full size. Half and Quarter render ceil(view count / divisor) views, reuse the nearest rendered image for each output tile, and can reduce angular smoothness. At least the first and last views are rendered when view count is greater than one.")]
        public QuiltViewRenderMode viewRenderMode = QuiltViewRenderMode.Full;
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
        public int LastRenderedViewCount { get; private set; }
        public string LastAutoRenderError { get; private set; }
        public long LastCpuSubmissionTicks { get; private set; }
        public long LastViewCpuTicks { get; private set; }
        public int ViewAntiAliasingSamples => viewTexture == null ? 0 : viewTexture.antiAliasing;
        public int ResolveAntiAliasingSamples => resolveTexture == null ? 0 : resolveTexture.antiAliasing;

        RenderTexture viewTexture;
        RenderTexture resolveTexture;
        RenderTexture previousActive;
        int[] renderedViewIndices = Array.Empty<int>();
        int[] sourceViewAssignments = Array.Empty<int>();
        int cachedSamplingViewCount;
        QuiltViewRenderMode cachedSamplingMode;
        bool hasSamplingCache;

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
            RenderQuiltCore(false);
        }

        internal void RenderAutoQuiltAtBoundary()
        {
            if (!autoRender || !isActiveAndEnabled || autoRenderFailed || IsRendering) return;
            try { RenderQuiltCore(true); }
            catch (Exception error)
            {
                autoRenderFailed = true;
                autoRender = false;
                LastAutoRenderError = SanitizeAutoRenderError(error);
                UnityEngine.Debug.LogError("Looking Glass quilt auto render stopped: " + LastAutoRenderError);
            }
        }

        void RenderQuiltCore(bool trustedSchedulerBoundary)
        {
            if (IsRendering) throw new InvalidOperationException("quilt render is already in progress");
            bool srp = GraphicsSettings.currentRenderPipeline != null;
            bool trustedSrpBoundary = trustedSchedulerBoundary && Application.isEditor && Application.isPlaying && srp;
            if (Camera.current != null && !trustedSrpBoundary) throw new InvalidOperationException("RenderQuilt must be called outside render callbacks");
            settings.Validate();
            QuiltViewSampling.GetRenderCount(settings.views, viewRenderMode);
            if (sourceCamera == null) throw new InvalidOperationException("sourceCamera is required");
            if (sourceCamera.enabled) throw new InvalidOperationException("sourceCamera must be disabled and dedicated to quilt rendering");
            if (sourceCamera.orthographic) throw new InvalidOperationException("only perspective cameras are supported");
            if (srp && !useRenderRequests) throw new NotSupportedException("an SRP adapter and render request are required");
            if (srp && RenderGuard == null) throw new InvalidOperationException("an IQuiltRenderGuard is required for SRP rendering");
            IsRendering = true;
            LastRenderedViewCount = 0;
            previousActive = RenderTexture.active;
            CameraState state = default;
            bool stateCaptured = false;
            IDisposable quiltPreparation = null;
            try
            {
                QuiltLayout layout = settings.GetLayout();
                EnsureViewSampling(layout.ViewCount);
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
                long totalStart = Stopwatch.GetTimestamp();
                sourceCamera.enabled = false;
                IQuiltRenderPreparation preparation = RenderGuard as IQuiltRenderPreparation;
                if (preparation != null) quiltPreparation = preparation.PrepareQuilt(sourceCamera);
                for (int renderSlot = 0; renderSlot < renderedViewIndices.Length; ++renderSlot)
                {
                    long viewStart = Stopwatch.GetTimestamp();
                    int viewIndex = renderedViewIndices[renderSlot];
                    float offset = QuiltMath.ViewOffset(viewIndex, layout.ViewCount, settings.viewCone, settings.cameraDistance);
                    sourceCamera.transform.position = state.position + state.rotation * Vector3.right * offset;
                    sourceCamera.transform.rotation = QuiltMath.ParallelViewRotation(state.rotation);
                    sourceCamera.projectionMatrix = QuiltMath.OffAxisProjection(settings.fieldOfView, settings.viewAspect, state.nearClip, state.farClip, offset, settings.cameraDistance);
                    sourceCamera.targetTexture = viewTexture;
                    sourceCamera.aspect = settings.viewAspect;
                    sourceCamera.rect = new Rect(0f, 0f, 1f, 1f);
                    IDisposable viewGuard = srp ? RenderGuard.Begin(sourceCamera) : null;
                    try
                    {
                        RenderOneView();
                        LastRenderedViewCount++;
                    }
                    finally { if (viewGuard != null) viewGuard.Dispose(); }
                    RenderTexture renderedView = ResolveViewTexture();
                    if (renderedViewIndices.Length == layout.ViewCount)
                    {
                        CopyTile(layout.GetTile(viewIndex), renderedView);
                    }
                    else
                    {
                        for (int outputView = 0; outputView < layout.ViewCount; ++outputView)
                            if (sourceViewAssignments[outputView] == viewIndex) CopyTile(layout.GetTile(outputView), renderedView);
                    }
                    LastViewCpuTicks = Stopwatch.GetTimestamp() - viewStart;
                }
                LastCpuSubmissionTicks = Stopwatch.GetTimestamp() - totalStart;
                Output?.Submit(quiltTexture, layout);
            }
            finally
            {
                try
                {
                    if (quiltPreparation != null) quiltPreparation.Dispose();
                }
                finally
                {
                    if (stateCaptured) state.Restore(sourceCamera);
                    RenderTexture.active = previousActive;
                    IsRendering = false;
                }
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

        void EnsureViewSampling(int viewCount)
        {
            if (hasSamplingCache && cachedSamplingViewCount == viewCount && cachedSamplingMode == viewRenderMode) return;
            int renderCount = QuiltViewSampling.GetRenderCount(viewCount, viewRenderMode);
            renderedViewIndices = new int[renderCount];
            sourceViewAssignments = new int[viewCount];
            for (int slot = 0; slot < renderCount; ++slot)
                renderedViewIndices[slot] = QuiltViewSampling.GetRenderedViewIndex(viewCount, viewRenderMode, slot);
            for (int view = 0; view < viewCount; ++view)
                sourceViewAssignments[view] = QuiltViewSampling.GetSourceViewIndex(viewCount, viewRenderMode, view);
            cachedSamplingViewCount = viewCount;
            cachedSamplingMode = viewRenderMode;
            hasSamplingCache = true;
        }

        RenderTexture ResolveViewTexture()
        {
            if (viewTexture == null) throw new InvalidOperationException("view texture is unavailable");
            if (viewTexture.antiAliasing <= 1) return viewTexture;
            if (resolveTexture == null || !resolveTexture.IsCreated()) throw new InvalidOperationException("MSAA resolve texture is unavailable");
            Graphics.Blit(viewTexture, resolveTexture);
            return resolveTexture;
        }

        void CopyTile(RectInt tile, RenderTexture source)
        {
            if (!SystemInfo.copyTextureSupport.HasFlag(CopyTextureSupport.Basic)) throw new NotSupportedException("Graphics.CopyTexture is not supported");
            Graphics.CopyTexture(source, 0, 0, 0, 0, tile.width, tile.height, quiltTexture, 0, 0, tile.x, tile.y);
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
                quiltTexture = new RenderTexture(layout.QuiltWidth, layout.QuiltHeight, 0, RenderTextureFormat.ARGB32) { name = "ViewWeave.Quilt" };
                quiltTexture.Create();
                if (!quiltTexture.IsCreated()) throw new InvalidOperationException("failed to create quilt render texture");
            }
            int viewSamples = GetRequestedMsaaSamples(sourceCamera, layout);
            if (viewTexture == null || viewTexture.width != layout.TileWidth || viewTexture.height != layout.TileHeight || viewTexture.antiAliasing != viewSamples)
            {
                ReleaseOwnedTexture(ref viewTexture);
                if (viewSamples <= 1)
                {
                    viewTexture = new RenderTexture(layout.TileWidth, layout.TileHeight, 24, RenderTextureFormat.ARGB32)
                    {
                        name = "ViewWeave.View",
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                }
                else
                {
                    RenderTextureDescriptor descriptor = new RenderTextureDescriptor(layout.TileWidth, layout.TileHeight, RenderTextureFormat.ARGB32, 24)
                    {
                        msaaSamples = viewSamples,
                        sRGB = quiltTexture != null && quiltTexture.sRGB,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    viewTexture = new RenderTexture(descriptor) { name = "ViewWeave.View" };
                }
                viewTexture.Create();
                if (!viewTexture.IsCreated()) throw new InvalidOperationException("failed to create view render texture");
            }
            if (viewSamples > 1)
            {
                if (resolveTexture == null || resolveTexture.width != layout.TileWidth || resolveTexture.height != layout.TileHeight || resolveTexture.antiAliasing != 1)
                {
                    ReleaseOwnedTexture(ref resolveTexture);
                    RenderTextureDescriptor descriptor = new RenderTextureDescriptor(layout.TileWidth, layout.TileHeight, RenderTextureFormat.ARGB32, 0)
                    {
                        msaaSamples = 1,
                        sRGB = quiltTexture != null && quiltTexture.sRGB,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    resolveTexture = new RenderTexture(descriptor) { name = "ViewWeave.ViewResolve" };
                    resolveTexture.Create();
                    if (!resolveTexture.IsCreated()) throw new InvalidOperationException("failed to create view resolve texture");
                }
            }
            else ReleaseOwnedTexture(ref resolveTexture);
        }

        int GetRequestedMsaaSamples(Camera camera, QuiltLayout layout)
        {
            if (camera == null || !camera.allowMSAA) return 1;
            IQuiltMsaaProvider provider = RenderGuard as IQuiltMsaaProvider;
            if (provider == null) return 1;
            int requested = provider.GetMsaaSampleCount(camera);
            if (requested <= 1) return 1;
            RenderTextureDescriptor probe = new RenderTextureDescriptor(layout.TileWidth, layout.TileHeight, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = requested,
                sRGB = quiltTexture != null && quiltTexture.sRGB
            };
            int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(probe);
            return supported == 2 || supported == 4 || supported == 8 ? supported : 1;
        }

        void ReleaseTextures()
        {
            ReleaseOwnedTexture(ref viewTexture);
            ReleaseOwnedTexture(ref resolveTexture);
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
