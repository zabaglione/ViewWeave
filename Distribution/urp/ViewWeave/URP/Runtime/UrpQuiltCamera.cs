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
        RenderPipelineAsset validationPipelineAsset;
        RenderPipeline validationPipelineInstance;
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

            RenderPipelineAsset pipelineAsset = GraphicsSettings.currentRenderPipeline;
            RenderPipeline pipelineInstance = RenderPipelineManager.currentPipeline;
            VolumeManager manager = VolumeManager.instance;
            if (pipelineInstance == null || !manager.isInitialized)
                throw new InvalidOperationException("URP render pipeline is not initialized");

            if (validationStack == null || !validationStack.isValid || validationPipelineAsset != pipelineAsset || validationPipelineInstance != pipelineInstance)
            {
                DestroyValidationStack();
                validationStack = manager.CreateStack();
                validationPipelineAsset = pipelineAsset;
                validationPipelineInstance = pipelineInstance;
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

            VolumeManager manager = VolumeManager.instance;
            RenderPipelineAsset pipelineAsset = GraphicsSettings.currentRenderPipeline;
            RenderPipeline pipelineInstance = RenderPipelineManager.currentPipeline;
            if (pipelineInstance == null || !manager.isInitialized)
                throw new InvalidOperationException("URP render pipeline is not initialized");

            VolumeFrameworkUpdateMode originalUpdateMode = Camera.GetVolumeFrameworkUpdateMode();
            VolumeStack originalManagerStack = manager.stack;
            VolumeStack ownedStack = null;
            try
            {
                Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                ownedStack = data.volumeStack;
                AssertPipelineContext(manager, pipelineAsset, pipelineInstance);
                if (ownedStack == null || !ownedStack.isValid)
                {
                    try
                    {
                        UrpQuiltIntegration.RestoreManagerStackSafely(manager, originalManagerStack, pipelineAsset, pipelineInstance);
                        if (ownedStack != null && data.volumeStack == ownedStack)
                            Camera.DestroyVolumeStack(data);
                    }
                    finally { Camera.SetVolumeFrameworkUpdateMode(originalUpdateMode); }
                    return null;
                }

                AssertLeaseContext(Camera, data, ownedStack, manager, pipelineAsset, pipelineInstance);
                cameraVolumeStackLease = new CameraVolumeStackLease(this, Camera, data, ownedStack, originalUpdateMode, originalManagerStack, pipelineAsset, pipelineInstance);
                return cameraVolumeStackLease;
            }
            catch
            {
                try
                {
                    UrpQuiltIntegration.RestoreManagerStackSafely(manager, originalManagerStack, pipelineAsset, pipelineInstance);
                    if (ownedStack != null && data.volumeStack == ownedStack)
                        Camera.DestroyVolumeStack(data);
                }
                finally { Camera.SetVolumeFrameworkUpdateMode(originalUpdateMode); }
                throw;
            }
        }

        static void AssertPipelineContext(VolumeManager manager, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance)
        {
            if (!UrpQuiltIntegration.IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance))
                throw new InvalidOperationException("URP render pipeline changed during quilt rendering");
        }

        static void AssertLeaseContext(Camera camera, UniversalAdditionalCameraData data, VolumeStack stack, VolumeManager manager, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance)
        {
            AssertPipelineContext(manager, pipelineAsset, pipelineInstance);
            if (camera == null || data == null || stack == null || !stack.isValid || data.volumeStack != stack)
                throw new InvalidOperationException("URP render pipeline or camera VolumeStack changed during quilt rendering");
        }

        public bool CameraOwnedVolumeStackLeaseActive => cameraVolumeStackLease != null && cameraVolumeStackLease.IsCurrent;

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
            readonly RenderPipelineAsset pipelineAsset;
            readonly RenderPipeline pipelineInstance;
            bool disposed;

            public bool IsCurrent => !disposed && IsLeaseContextCurrent();

            bool IsLeaseContextCurrent()
            {
                return camera != null && data != null && ownedStack != null && ownedStack.isValid &&
                    data.volumeStack == ownedStack &&
                    UrpQuiltIntegration.IsPipelineContextCurrent(VolumeManager.instance, pipelineAsset, pipelineInstance);
            }

            public CameraVolumeStackLease(UrpQuiltCamera owner, Camera camera, UniversalAdditionalCameraData data, VolumeStack ownedStack, VolumeFrameworkUpdateMode originalUpdateMode, VolumeStack originalManagerStack, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance)
            {
                this.owner = owner;
                this.camera = camera;
                this.data = data;
                this.ownedStack = ownedStack;
                this.originalUpdateMode = originalUpdateMode;
                this.originalManagerStack = originalManagerStack;
                this.pipelineAsset = pipelineAsset;
                this.pipelineInstance = pipelineInstance;
            }

            public void AssertCurrent()
            {
                if (!IsCurrent)
                    throw new InvalidOperationException("URP render pipeline or camera VolumeStack changed during quilt rendering");
            }

            public UrpQuiltIntegration.Scope BeginViewRender(Camera targetCamera, Action onDispose)
            {
                AssertCurrent();
                if (targetCamera != camera) throw new InvalidOperationException("guard camera does not match the camera VolumeStack lease");
                return UrpQuiltIntegration.BeginViewRender(camera, ownedStack, pipelineAsset, pipelineInstance, onDispose);
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
                try { UrpQuiltIntegration.RestoreManagerStackSafely(manager, originalManagerStack, pipelineAsset, pipelineInstance); }
                finally
                {
                    try
                    {
                        if (camera != null && data != null && data.volumeStack == ownedStack)
                            camera.DestroyVolumeStack(data);
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
            CameraVolumeStackLease lease = cameraVolumeStackLease;
            if (lease == null) return PrepareForQuilt();
            if (scopeActive) throw new InvalidOperationException("URP quilt camera view scope is already active");
            scopeActive = true;
            if (releaseScopeAction == null) releaseScopeAction = ReleaseScope;
            try
            {
                return lease.BeginViewRender(Camera, releaseScopeAction);
            }
            catch { scopeActive = false; throw; }
        }

        void ReleaseScope() { scopeActive = false; }
        void OnDisable() { scopeActive = false; cameraVolumeStackLease?.Dispose(); DestroyValidationStack(); }
        void OnDestroy() { scopeActive = false; cameraVolumeStackLease?.Dispose(); DestroyValidationStack(); cachedCamera = null; }

        void DestroyValidationStack()
        {
            if (validationStack != null && validationStack.isValid && VolumeManager.instance.isInitialized &&
                validationPipelineAsset == GraphicsSettings.currentRenderPipeline &&
                validationPipelineInstance == RenderPipelineManager.currentPipeline)
                VolumeManager.instance.DestroyStack(validationStack);
            validationStack = null;
            validationPipelineAsset = null;
            validationPipelineInstance = null;
        }
    }
}
