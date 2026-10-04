using System;
using UnityEngine;
using UnityEngine.Rendering;
using ViewWeave;

namespace ViewWeave.HDRP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class HdrpQuiltCamera : MonoBehaviour, IQuiltRenderGuard, IQuiltRenderPreparation
    {
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
        bool scopeActive;
        public int ValidationStackCreationCount { get; private set; }
        Camera Camera => cachedCamera != null ? cachedCamera : (cachedCamera = GetComponent<Camera>());

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

        void OnDisable() { scopeActive = false; activePreparation?.Dispose(); activePreparation = null; DestroyCachedPreparation(); DestroyValidationStack(); }
        void OnDestroy() { scopeActive = false; activePreparation?.Dispose(); activePreparation = null; DestroyCachedPreparation(); DestroyValidationStack(); cachedCamera = null; }
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
