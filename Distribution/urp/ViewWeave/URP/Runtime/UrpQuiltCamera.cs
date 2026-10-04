using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave;

namespace ViewWeave.URP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class UrpQuiltCamera : MonoBehaviour, IQuiltRenderGuard, IQuiltMsaaProvider
    {
        VolumeStack validationStack;
        RenderPipelineAsset validationPipeline;
        Camera cachedCamera;
        bool scopeActive;
        Action releaseScopeAction;
        public int ValidationStackCreationCount { get; private set; }
        Camera Camera => cachedCamera != null ? cachedCamera : (cachedCamera = GetComponent<Camera>());
        public void ValidateForQuilt() => UrpQuiltIntegration.Validate(Camera);
        public int GetMsaaSampleCount(Camera camera)
        {
            if (camera == null || camera != Camera || !camera.allowMSAA) return 1;
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) return 1;
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
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
        void OnDisable() { scopeActive = false; DestroyValidationStack(); }
        void OnDestroy() { scopeActive = false; DestroyValidationStack(); cachedCamera = null; }
        void DestroyValidationStack()
        {
            if (validationStack != null) VolumeManager.instance.DestroyStack(validationStack);
            validationStack = null;
            validationPipeline = null;
        }
    }
}
