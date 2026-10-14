using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave;

namespace ViewWeave.URP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class UrpQuiltCamera : MonoBehaviour, IQuiltRenderGuard, IQuiltMsaaSettingsProvider, IQuiltViewTextureFormatProvider, IQuiltRenderSessionProvider, IQuiltRenderResourceOwner
    {
        [Tooltip("Isolated histories keep a persistent Camera per rendered logical view and support TAA. Shared Camera Compatibility disables TAA temporarily.")]
        public UrpQuiltRenderingMode renderingMode = UrpQuiltRenderingMode.IsolatedViewHistories;
        [Tooltip("Follow URP rendering settings on the Reference Camera. Framing, clipping and culling remain controlled by the Rig.")]
        public bool followReferenceRenderingSettings = true;
        [Tooltip("Stops output when observed graphics growth or estimated new/grown RenderTextures exceed this budget. Includes shared resources; excludes unobservable buffers/driver overhead. This is not a strict VRAM cap.")]
        [Min(1)] public int historyMemoryBudgetMB = 4096;
        [Min(1)] public int maximumHistoryCameras = 256;
        UrpViewHistoryRenderer historyRenderer;
        public int HistoryCameraCount => historyRenderer == null ? 0 : historyRenderer.CameraCount;
        public long HistoryGraphicsMemoryGrowthBytes => historyRenderer == null ? 0 : historyRenderer.GraphicsMemoryGrowthBytes;
        public long HistoryTextureMemoryEstimateBytes => historyRenderer == null ? 0 : historyRenderer.TextureMemoryGrowthBytes;
        public string HistoryStatus => historyRenderer == null ? string.Empty : historyRenderer.Status;
        public Camera GetHistoryCamera(int logicalView) => historyRenderer?.GetCamera(logicalView);
        public RenderTextureFormat GetQuiltViewTextureFormat(Camera sourceCamera)
        {
            if (sourceCamera == null || sourceCamera != Camera)
                throw new InvalidOperationException("view texture format source camera does not match the URP quilt camera");
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return sourceCamera.allowHDR && pipeline != null && pipeline.supportsHDR
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;
        }
        public void ReleaseQuiltRenderResources() { historyRenderer?.Dispose(); historyRenderer = null; }
        public IQuiltRenderSession BeginQuiltRender(QuiltRenderContext context)
        {
            if (!isActiveAndEnabled || scopeActive) throw new InvalidOperationException("URP quilt camera is inactive or already rendering");
            if (renderingMode == UrpQuiltRenderingMode.SharedCameraCompatibility) { ReleaseQuiltRenderResources(); return null; }
            if (renderingMode != UrpQuiltRenderingMode.IsolatedViewHistories) throw new ArgumentOutOfRangeException(nameof(renderingMode));
            if (historyRenderer == null) historyRenderer = new UrpViewHistoryRenderer(Camera);
            try { return historyRenderer.Begin(context, followReferenceRenderingSettings, historyMemoryBudgetMB, maximumHistoryCameras); }
            catch { ReleaseQuiltRenderResources(); throw; }
        }
        VolumeStack validationStack;
        RenderPipelineAsset validationPipeline;
        Camera cachedCamera;
        bool scopeActive;
        Action releaseScopeAction;
        public int ValidationStackCreationCount { get; private set; }
        Camera Camera => cachedCamera != null ? cachedCamera : (cachedCamera = GetComponent<Camera>());
        public void ValidateForQuilt() => UrpQuiltIntegration.Validate(Camera);
        public int GetMsaaSampleCount(Camera camera) => GetMsaaSampleCount(camera, null);
        public int GetMsaaSampleCount(Camera camera, Camera referenceCamera)
        {
            if (camera == null || camera != Camera || !camera.allowMSAA) return 1;
            Camera settings = renderingMode == UrpQuiltRenderingMode.IsolatedViewHistories && followReferenceRenderingSettings && referenceCamera != null
                ? referenceCamera : camera;
            return ResolveMsaaSampleCount(camera, settings);
        }
        internal static int ResolveMsaaSampleCount(Camera source, Camera settings)
        {
            if (source == null || !source.allowMSAA || settings == null) return 1;
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) return 1;
            UniversalAdditionalCameraData data = settings.GetComponent<UniversalAdditionalCameraData>();
            if (data == null || data.scriptableRenderer == null || !data.scriptableRenderer.supportedRenderingFeatures.msaa) return 1;
            int samples = pipeline.msaaSampleCount;
            return samples == 2 || samples == 4 || samples == 8 ? samples : 1;
        }
        public UrpQuiltIntegration.Scope PrepareForQuilt()
        {
            UrpQuiltIntegration.Validate(Camera);
            if (!isActiveAndEnabled) throw new InvalidOperationException("URP quilt camera must be active and enabled");
            if (scopeActive) throw new InvalidOperationException("URP quilt camera view scope is already active");
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (validationStack == null || !validationStack.isValid || validationPipeline != pipeline)
            {
                DestroyValidationStack();
                validationStack = VolumeManager.instance.CreateStack();
                validationPipeline = pipeline;
                ValidationStackCreationCount++;
            }
            scopeActive = true;
            if (releaseScopeAction == null) releaseScopeAction = ReleaseScope;
            try { return UrpQuiltIntegration.BeginViewRender(Camera, validationStack, releaseScopeAction); }
            catch { scopeActive = false; throw; }
        }
        IDisposable IQuiltRenderGuard.Begin(Camera camera)
        {
            if (camera != Camera) throw new InvalidOperationException("guard camera does not match the quilt source camera");
            return PrepareForQuilt();
        }
        void ReleaseScope() { scopeActive = false; }
        void OnDisable() { scopeActive = false; ReleaseQuiltRenderResources(); DestroyValidationStack(); }
        void OnDestroy() { scopeActive = false; ReleaseQuiltRenderResources(); DestroyValidationStack(); cachedCamera = null; }
        void DestroyValidationStack()
        {
            if (validationStack != null) VolumeManager.instance.DestroyStack(validationStack);
            validationStack = null;
            validationPipeline = null;
        }
    }
}
