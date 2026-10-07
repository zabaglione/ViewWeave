using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave;

namespace ViewWeave.URP
{
    public static class UrpQuiltIntegration
    {
        public static void Validate(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) throw new InvalidOperationException("URP render pipeline asset is required");
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null) throw new InvalidOperationException("URP camera data is required");
            if (data.renderType != CameraRenderType.Base) throw new NotSupportedException("URP camera stacks require a Base camera");
            if (data.cameraStack != null && data.cameraStack.Count != 0) throw new NotSupportedException("URP camera stacks are not supported for quilt rendering");
            if (!VolumeManager.instance.isInitialized) throw new InvalidOperationException("URP VolumeManager is not initialized");
        }

        internal static bool UsesAutomaticVolumeUpdates(Camera camera, UniversalAdditionalCameraData data)
        {
            if (camera == null || data == null || !data.requiresVolumeFrameworkUpdate) return false;
            VolumeFrameworkUpdateMode cameraMode = camera.GetVolumeFrameworkUpdateMode();
            if (cameraMode == VolumeFrameworkUpdateMode.EveryFrame) return true;
            if (cameraMode != VolumeFrameworkUpdateMode.UsePipelineSettings) return false;
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            return pipeline != null && pipeline.volumeFrameworkUpdateMode == VolumeFrameworkUpdateMode.EveryFrame;
        }

        internal static bool IsPipelineContextCurrent(VolumeManager manager, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance)
        {
            return manager != null && manager.isInitialized &&
                GraphicsSettings.currentRenderPipeline == pipelineAsset &&
                RenderPipelineManager.currentPipeline == pipelineInstance;
        }

        internal static void RestoreManagerStackSafely(VolumeManager manager, VolumeStack savedStack, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance)
        {
            if (manager == null || !manager.isInitialized) return;
            if (IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance) && savedStack != null && savedStack.isValid)
                manager.stack = savedStack;
            else
                manager.ResetMainStack();
        }

        static void ValidateScopeState(VolumeManager manager, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance, VolumeStack viewStack, UniversalAdditionalCameraData cameraData, VolumeStack expectedCameraStack)
        {
            bool valid = IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance) && viewStack != null && viewStack.isValid;
            if (expectedCameraStack != null)
                valid = valid && cameraData != null && cameraData.volumeStack == expectedCameraStack && expectedCameraStack.isValid;
            if (!valid) throw new InvalidOperationException("URP render pipeline or VolumeStack changed during quilt rendering");
        }

        public static Scope BeginViewRender(Camera camera)
        {
            Validate(camera);
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            VolumeManager manager = VolumeManager.instance;
            RenderPipelineAsset pipelineAsset = GraphicsSettings.currentRenderPipeline;
            RenderPipeline pipelineInstance = RenderPipelineManager.currentPipeline;
            VolumeStack previousManagerStack = manager.stack;
            VolumeStack stack = null;
            try
            {
                if (!IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance))
                    throw new InvalidOperationException("URP render pipeline changed before a quilt view began");
                stack = manager.CreateStack();
                Transform trigger = data.volumeTrigger != null ? data.volumeTrigger : camera.transform;
                manager.Update(stack, trigger, data.volumeLayerMask);
                ValidateScopeState(manager, pipelineAsset, pipelineInstance, stack, null, null);
                ValidateMotionBlur(stack);
                return new Scope(data, stack, true, null, manager, previousManagerStack, pipelineAsset, pipelineInstance, null);
            }
            catch
            {
                try
                {
                    if (stack != null && stack.isValid && IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance))
                        manager.DestroyStack(stack);
                }
                finally { RestoreManagerStackSafely(manager, previousManagerStack, pipelineAsset, pipelineInstance); }
                throw;
            }
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack borrowedStack)
        {
            return BeginViewRender(camera, borrowedStack, null);
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack borrowedStack, Action onDispose)
        {
            Validate(camera);
            if (borrowedStack == null || !borrowedStack.isValid) throw new InvalidOperationException("a valid cached volume stack is required");
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            VolumeManager manager = VolumeManager.instance;
            RenderPipelineAsset pipelineAsset = GraphicsSettings.currentRenderPipeline;
            RenderPipeline pipelineInstance = RenderPipelineManager.currentPipeline;
            VolumeStack previousManagerStack = manager.stack;
            try
            {
                if (!IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance))
                    throw new InvalidOperationException("URP render pipeline changed before a quilt view began");
                Transform trigger = data.volumeTrigger != null ? data.volumeTrigger : camera.transform;
                manager.Update(borrowedStack, trigger, data.volumeLayerMask);
                ValidateScopeState(manager, pipelineAsset, pipelineInstance, borrowedStack, null, null);
                ValidateMotionBlur(borrowedStack);
                return new Scope(data, borrowedStack, false, onDispose, manager, previousManagerStack, pipelineAsset, pipelineInstance, null);
            }
            catch
            {
                RestoreManagerStackSafely(manager, previousManagerStack, pipelineAsset, pipelineInstance);
                throw;
            }
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack expectedCameraStack, RenderPipelineAsset expectedPipelineAsset, RenderPipeline expectedPipelineInstance, Action onDispose)
        {
            Validate(camera);
            UniversalAdditionalCameraData data = camera.GetComponent<UniversalAdditionalCameraData>();
            VolumeManager manager = VolumeManager.instance;
            VolumeStack previousManagerStack = manager.stack;
            try
            {
                ValidateScopeState(manager, expectedPipelineAsset, expectedPipelineInstance, expectedCameraStack, data, expectedCameraStack);
                camera.UpdateVolumeStack();
                ValidateScopeState(manager, expectedPipelineAsset, expectedPipelineInstance, expectedCameraStack, data, expectedCameraStack);
                manager.stack = expectedCameraStack;
                ValidateMotionBlur(expectedCameraStack);
                return new Scope(data, expectedCameraStack, false, onDispose, manager, previousManagerStack, expectedPipelineAsset, expectedPipelineInstance, expectedCameraStack);
            }
            catch
            {
                RestoreManagerStackSafely(manager, previousManagerStack, expectedPipelineAsset, expectedPipelineInstance);
                throw;
            }
        }

        static void ValidateMotionBlur(VolumeStack stack)
        {
            var motionBlur = stack.GetComponent<MotionBlur>();
            if (motionBlur != null && motionBlur.IsActive()) throw new NotSupportedException("URP Motion Blur is temporal and is not supported for quilt views");
        }

        public readonly struct Scope : IDisposable
        {
            readonly UniversalAdditionalCameraData data;
            readonly VolumeStack stack;
            readonly AntialiasingMode antialiasing;
            readonly bool valid;
            readonly bool ownsStack;
            readonly Action onDispose;
            readonly VolumeManager manager;
            readonly VolumeStack previousManagerStack;
            readonly RenderPipelineAsset pipelineAsset;
            readonly RenderPipeline pipelineInstance;
            readonly VolumeStack expectedCameraStack;

            internal Scope(UniversalAdditionalCameraData data, VolumeStack stack, bool ownsStack, Action onDispose, VolumeManager manager, VolumeStack previousManagerStack, RenderPipelineAsset pipelineAsset, RenderPipeline pipelineInstance, VolumeStack expectedCameraStack)
            {
                this.data = data;
                this.stack = stack;
                this.ownsStack = ownsStack;
                this.onDispose = onDispose;
                this.manager = manager;
                this.previousManagerStack = previousManagerStack;
                this.pipelineAsset = pipelineAsset;
                this.pipelineInstance = pipelineInstance;
                this.expectedCameraStack = expectedCameraStack;
                antialiasing = data.antialiasing;
                valid = true;
                if (antialiasing == AntialiasingMode.TemporalAntiAliasing) data.antialiasing = AntialiasingMode.None;
            }

            public void Dispose()
            {
                if (!valid) return;
                bool stateWasValid = manager != null && manager.isInitialized &&
                    GraphicsSettings.currentRenderPipeline == pipelineAsset &&
                    RenderPipelineManager.currentPipeline == pipelineInstance &&
                    stack != null && stack.isValid;
                if (expectedCameraStack != null)
                    stateWasValid = stateWasValid && data != null && data.volumeStack == expectedCameraStack && expectedCameraStack.isValid;
                try { if (data != null) data.antialiasing = antialiasing; }
                finally
                {
                    try
                    {
                        if (ownsStack && stack != null && stack.isValid && IsPipelineContextCurrent(manager, pipelineAsset, pipelineInstance))
                            manager.DestroyStack(stack);
                    }
                    finally
                    {
                        try { RestoreManagerStackSafely(manager, previousManagerStack, pipelineAsset, pipelineInstance); }
                        finally { onDispose?.Invoke(); }
                    }
                }
                if (!stateWasValid) throw new InvalidOperationException("URP render pipeline or VolumeStack changed during quilt rendering");
            }
        }
    }
}
