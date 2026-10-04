using System;
using UnityEngine;
using UnityEngine.Rendering;
using LookingGlass.Community;

namespace LookingGlass.Community.HDRP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class HdrpQuiltCamera : MonoBehaviour, IQuiltRenderGuard
    {
        VolumeStack validationStack;
        RenderPipelineAsset validationPipeline;
        Camera cachedCamera;
        bool scopeActive;
        Action releaseScopeAction;
        public int ValidationStackCreationCount { get; private set; }
        Camera Camera => cachedCamera != null ? cachedCamera : (cachedCamera = GetComponent<Camera>());
        public void ValidateForQuilt() => HdrpQuiltIntegration.Validate(Camera);
        public HdrpQuiltIntegration.Scope PrepareForQuilt()
        {
            HdrpQuiltIntegration.Validate(Camera);
            if (!isActiveAndEnabled) throw new InvalidOperationException("HDRP quilt camera must be active and enabled");
            if (scopeActive) throw new InvalidOperationException("HDRP quilt camera view scope is already active");
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
            try { return HdrpQuiltIntegration.BeginViewRender(Camera, validationStack, releaseScopeAction); }
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
