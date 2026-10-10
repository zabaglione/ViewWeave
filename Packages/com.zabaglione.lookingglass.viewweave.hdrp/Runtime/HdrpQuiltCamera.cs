using System;
using UnityEngine;
using UnityEngine.Rendering;
using ViewWeave;

namespace ViewWeave.HDRP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class HdrpQuiltCamera : MonoBehaviour, IQuiltRenderGuard, IQuiltRenderPreparation, IQuiltRenderSessionProvider, IQuiltRenderResourceOwner
    {
        [Tooltip("Isolated histories retain HDRP effects on one persistent Camera per rendered logical view. Compatibility mode uses a shared Camera and suppresses temporal effects.")]
        public HdrpQuiltRenderingMode renderingMode = HdrpQuiltRenderingMode.IsolatedViewHistories;
        [Tooltip("Copy HDRP rendering settings from the Reference Camera before each Quilt. Camera framing, clipping and culling continue to use the Rig settings.")]
        public bool followReferenceRenderingSettings = true;
        public HdrpQuiltExposureMode exposureMode = HdrpQuiltExposureMode.FollowScene;
        [Tooltip("Stops output when observed graphics growth or the estimate of new/grown RenderTextures exceeds this budget. Includes shared HDRP resources. Texture estimates remain available in Release Players, but exclude unobservable buffers/driver overhead; this is not a strict VRAM cap.")]
        [Min(1)] public int historyMemoryBudgetMB = 4096;
        [Tooltip("Maximum number of persistent logical-view Cameras, enforced in Editor and all Player builds. Exceeding this limit stops output.")]
        [Min(1)] public int maximumHistoryCameras = 256;
        [Tooltip("Temporarily disables unsupported HDRP effects for quilt rendering.")]
        public bool adaptUnsupportedEffects = true;
        [Tooltip("Use a fixed exposure when the effective HDRP stack is not already fixed.")]
        public bool fallbackFixedExposure = true;
        public float fallbackFixedExposureEV = 12f;

        VolumeStack validationStack;
        RenderPipelineAsset validationPipeline;
        HdrpQuiltIntegration.Preparation activePreparation;
        HdrpQuiltIntegration.Preparation cachedPreparation;
        Camera cachedCamera;
        HdrpViewHistoryRenderer historyRenderer;
        bool scopeActive;
        public int ValidationStackCreationCount { get; private set; }
        Camera Camera => cachedCamera != null ? cachedCamera : (cachedCamera = GetComponent<Camera>());
        public int HistoryCameraCount => historyRenderer == null ? 0 : historyRenderer.CameraCount;
        public long HistoryGraphicsMemoryGrowthBytes => historyRenderer == null ? 0 : historyRenderer.GraphicsMemoryGrowthBytes;
        public long HistoryTextureMemoryEstimateBytes => historyRenderer == null ? 0 : historyRenderer.TextureMemoryGrowthBytes;
        public float SharedExposureEV => historyRenderer == null ? fallbackFixedExposureEV : historyRenderer.SharedExposureEV;
        public bool SharedExposureReady => historyRenderer != null && historyRenderer.ExposureReady;
        public string HistoryStatus => historyRenderer == null ? string.Empty : historyRenderer.Status;
        public Camera GetHistoryCamera(int logicalView) => historyRenderer?.GetCamera(logicalView);

        public IQuiltRenderSession BeginQuiltRender(QuiltRenderContext context)
        {
            if (!isActiveAndEnabled) throw new InvalidOperationException("HDRP quilt camera must be active and enabled");
            if (scopeActive || activePreparation != null) throw new InvalidOperationException("HDRP quilt preparation is already active");
            if (renderingMode == HdrpQuiltRenderingMode.SharedCameraCompatibility)
            { DestroyHistoryRenderer(); return null; }
            if (renderingMode != HdrpQuiltRenderingMode.IsolatedViewHistories) throw new ArgumentOutOfRangeException(nameof(renderingMode));
            DestroyCachedPreparation();
            if (historyRenderer == null) historyRenderer = new HdrpViewHistoryRenderer(Camera);
            return historyRenderer.Begin(context, followReferenceRenderingSettings, exposureMode, fallbackFixedExposureEV, historyMemoryBudgetMB, maximumHistoryCameras);
        }
        public void ReleaseQuiltRenderResources() => DestroyHistoryRenderer();

        public void ValidateForQuilt() => HdrpQuiltIntegration.Validate(Camera);

        public HdrpQuiltIntegration.Scope PrepareForQuilt()
        {
            HdrpQuiltIntegration.Validate(Camera);
            if (!isActiveAndEnabled) throw new InvalidOperationException("HDRP quilt camera must be active and enabled");
            if (scopeActive) throw new InvalidOperationException("HDRP quilt camera view scope is already active");

            bool ownsPreparation = activePreparation == null && adaptUnsupportedEffects;
            IDisposable preparationLease = null;
            if (ownsPreparation) preparationLease = PrepareQuilt(Camera);

            scopeActive = true;
            if (releaseScopeAction == null) releaseScopeAction = ReleaseScope;
            Action onDispose = releaseScopeAction;
            if (preparationLease != null)
                onDispose = () => { releaseScopeAction(); preparationLease.Dispose(); };
            try
            {
                EnsureValidationStack();
                bool adapted = activePreparation != null && activePreparation.adapted;
                if (adapted)
                    return HdrpQuiltIntegration.BeginViewRender(Camera, validationStack, activePreparation, onDispose);
                return HdrpQuiltIntegration.BeginViewRender(Camera, validationStack, onDispose);
            }
            catch
            {
                scopeActive = false;
                if (preparationLease != null) preparationLease.Dispose();
                throw;
            }
        }

        public IDisposable PrepareQuilt(Camera camera)
        {
            if (camera != Camera) throw new InvalidOperationException("preparation camera does not match the HDRP quilt camera");
            HdrpQuiltIntegration.Validate(Camera);
            if (!isActiveAndEnabled) throw new InvalidOperationException("HDRP quilt camera must be active and enabled");
            if (scopeActive || activePreparation != null) throw new InvalidOperationException("HDRP quilt camera preparation is already active");
            EnsureValidationStack();
            if (adaptUnsupportedEffects)
            {
                if (cachedPreparation == null || cachedPreparation.camera != Camera)
                {
                    DestroyCachedPreparation();
                    cachedPreparation = HdrpQuiltIntegration.BeginPreparation(Camera, validationStack, true, fallbackFixedExposure, fallbackFixedExposureEV);
                }
                else
                {
                    cachedPreparation.BeginLease(fallbackFixedExposure, fallbackFixedExposureEV);
                }
                activePreparation = cachedPreparation;
            }
            else
            {
                activePreparation = HdrpQuiltIntegration.BeginPreparation(Camera, validationStack, false, fallbackFixedExposure, fallbackFixedExposureEV);
            }
            return new PreparationLease(this, activePreparation);
        }

        IDisposable IQuiltRenderGuard.Begin(Camera camera)
        {
            if (camera != Camera) throw new InvalidOperationException("guard camera does not match the quilt source camera");
            return PrepareForQuilt();
        }

        Action releaseScopeAction;
        void ReleaseScope() { scopeActive = false; }

        void EndPreparation(HdrpQuiltIntegration.Preparation preparation)
        {
            if (activePreparation != preparation) return;
            activePreparation = null;
            preparation.Dispose();
            if (!preparation.adapted) preparation.Destroy();
        }

        void EnsureValidationStack()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (validationStack == null || !validationStack.isValid || validationPipeline != pipeline)
            {
                DestroyCachedPreparation();
                DestroyValidationStack();
                validationStack = VolumeManager.instance.CreateStack();
                validationPipeline = pipeline;
                ValidationStackCreationCount++;
            }
        }

        void OnDisable() { scopeActive = false; activePreparation?.Dispose(); activePreparation = null; DestroyHistoryRenderer(); DestroyCachedPreparation(); DestroyValidationStack(); }
        void OnDestroy() { scopeActive = false; activePreparation?.Dispose(); activePreparation = null; DestroyHistoryRenderer(); DestroyCachedPreparation(); DestroyValidationStack(); cachedCamera = null; }
        void DestroyHistoryRenderer() { historyRenderer?.Dispose(); historyRenderer = null; }
        void DestroyCachedPreparation()
        {
            if (cachedPreparation == null) return;
            cachedPreparation.Destroy();
            cachedPreparation = null;
        }
        void DestroyValidationStack()
        {
            if (validationStack != null && VolumeManager.instance.isInitialized) VolumeManager.instance.DestroyStack(validationStack);
            validationStack = null;
            validationPipeline = null;
        }

        sealed class PreparationLease : IDisposable
        {
            readonly HdrpQuiltCamera owner;
            readonly HdrpQuiltIntegration.Preparation preparation;
            bool disposed;
            public PreparationLease(HdrpQuiltCamera owner, HdrpQuiltIntegration.Preparation preparation) { this.owner = owner; this.preparation = preparation; }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                owner.EndPreparation(preparation);
            }
        }
    }
}
