using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave;

namespace ViewWeave.URP
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class UrpQuiltCamera : MonoBehaviour, IQuiltRenderGuard, IQuiltRenderPreparation, IQuiltMsaaProvider
    {
        [Tooltip("Reuse the camera-owned URP VolumeStack for each quilt view. This opt-in path is supported in Play Mode when the camera uses automatic per-frame Volume updates and has no existing camera-owned stack.")]
        public bool reuseCameraOwnedVolumeStackForQuilt;

        VolumeStack validationStack;
        RenderPipelineAsset validationPipeline;
        Camera cachedCamera;
        bool scopeActive;
        CameraVolumeStackLease cameraVolumeStackLease;
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

        public IDisposable PrepareQuilt(Camera camera)
        {
            if (camera != Camera) throw new InvalidOperationException("preparation camera does not match the URP quilt camera");
            UrpQuiltIntegration.Validate(Camera);
            if (!isActiveAndEnabled) throw new InvalidOperationException("URP quilt camera must be active and enabled");
            if (cameraVolumeStackLease != null) throw new InvalidOperationException("URP camera VolumeStack preparation is already active");
            if (!reuseCameraOwnedVolumeStackForQuilt || !Application.isPlaying) return null;

            UniversalAdditionalCameraData data = Camera.GetComponent<UniversalAdditionalCameraData>();
            if (data.volumeStack != null || !UrpQuiltIntegration.UsesAutomaticVolumeUpdates(Camera, data)) return null;

            VolumeFrameworkUpdateMode originalUpdateMode = Camera.GetVolumeFrameworkUpdateMode();
            VolumeStack originalManagerStack = VolumeManager.instance.stack;
            try
            {
                Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                VolumeStack ownedStack = data.volumeStack;
                if (ownedStack == null || !ownedStack.isValid)
                {
                    try
                    {
                        VolumeManager.instance.stack = originalManagerStack;
                        if (data.volumeStack != null) Camera.DestroyVolumeStack();
                    }
                    finally { Camera.SetVolumeFrameworkUpdateMode(originalUpdateMode); }
                    return null;
                }

                cameraVolumeStackLease = new CameraVolumeStackLease(this, Camera, data, ownedStack, originalUpdateMode, originalManagerStack);
                return cameraVolumeStackLease;
            }
            catch
            {
                try
                {
                    VolumeManager.instance.stack = originalManagerStack;
                    if (data.volumeStack != null) Camera.DestroyVolumeStack();
                }
                finally { Camera.SetVolumeFrameworkUpdateMode(originalUpdateMode); }
                throw;
            }
        }

        public bool CameraOwnedVolumeStackLeaseActive => cameraVolumeStackLease != null && cameraVolumeStackLease.IsActive;

        bool IsUsingPreparedCameraStack => CameraOwnedVolumeStackLeaseActive;

        void EndCameraVolumeStackLease(CameraVolumeStackLease lease)
        {
            if (cameraVolumeStackLease != lease) return;
            cameraVolumeStackLease = null;
            lease.Release();
        }

        sealed class CameraVolumeStackLease : IDisposable
        {
            readonly UrpQuiltCamera owner;
            readonly Camera camera;
            readonly UniversalAdditionalCameraData data;
            readonly VolumeStack ownedStack;
            readonly VolumeFrameworkUpdateMode originalUpdateMode;
            readonly VolumeStack originalManagerStack;
            bool disposed;

            public bool IsActive => !disposed;

            public CameraVolumeStackLease(UrpQuiltCamera owner, Camera camera, UniversalAdditionalCameraData data, VolumeStack ownedStack, VolumeFrameworkUpdateMode originalUpdateMode, VolumeStack originalManagerStack)
            {
                this.owner = owner;
                this.camera = camera;
                this.data = data;
                this.ownedStack = ownedStack;
                this.originalUpdateMode = originalUpdateMode;
                this.originalManagerStack = originalManagerStack;
            }

            public void Dispose()
            {
                owner.EndCameraVolumeStackLease(this);
            }

            public void Release()
            {
                if (disposed) return;
                disposed = true;
                VolumeManager manager = VolumeManager.instance;
                try { manager.stack = originalManagerStack; }
                finally
                {
                    try
                    {
                        if (camera != null && data != null && data.volumeStack == ownedStack)
                            camera.DestroyVolumeStack();
                    }
                    finally
                    {
                        if (camera != null) camera.SetVolumeFrameworkUpdateMode(originalUpdateMode);
                    }
                }
            }
        }
        IDisposable IQuiltRenderGuard.Begin(Camera camera)
        {
            if (camera != Camera) throw new InvalidOperationException("guard camera does not match the quilt source camera");
            if (!IsUsingPreparedCameraStack) return PrepareForQuilt();
            if (scopeActive) throw new InvalidOperationException("URP quilt camera view scope is already active");
            scopeActive = true;
            if (releaseScopeAction == null) releaseScopeAction = ReleaseScope;
            try
            {
                return UrpQuiltIntegration.BeginViewRender(Camera, true, releaseScopeAction);
            }
            catch { scopeActive = false; throw; }
        }
        void ReleaseScope() { scopeActive = false; }
        void OnDisable() { scopeActive = false; cameraVolumeStackLease?.Dispose(); DestroyValidationStack(); }
        void OnDestroy() { scopeActive = false; cameraVolumeStackLease?.Dispose(); DestroyValidationStack(); cachedCamera = null; }
        void DestroyValidationStack()
        {
            if (validationStack != null) VolumeManager.instance.DestroyStack(validationStack);
            validationStack = null;
            validationPipeline = null;
        }
    }
}
