using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using LookingGlass.Community;

namespace LookingGlass.Community.URP
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

        public static Scope BeginViewRender(Camera camera)
        {
            Validate(camera);
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            VolumeStack stack = VolumeManager.instance.CreateStack();
            try
            {
                Transform trigger = data.volumeTrigger != null ? data.volumeTrigger : camera.transform;
                VolumeManager.instance.Update(stack, trigger, data.volumeLayerMask);
                var motionBlur = stack.GetComponent<MotionBlur>();
                if (motionBlur != null && motionBlur.IsActive()) throw new NotSupportedException("URP Motion Blur is temporal and is not supported for quilt views");
                return new Scope(data, stack, true);
            }
            catch { VolumeManager.instance.DestroyStack(stack); throw; }
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack borrowedStack)
        {
            return BeginViewRender(camera, borrowedStack, null);
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack borrowedStack, Action onDispose)
        {
            Validate(camera);
            if (borrowedStack == null || !borrowedStack.isValid) throw new InvalidOperationException("a valid cached volume stack is required");
            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            Transform trigger = data.volumeTrigger != null ? data.volumeTrigger : camera.transform;
            VolumeManager.instance.Update(borrowedStack, trigger, data.volumeLayerMask);
            var motionBlur = borrowedStack.GetComponent<MotionBlur>();
            if (motionBlur != null && motionBlur.IsActive()) throw new NotSupportedException("URP Motion Blur is temporal and is not supported for quilt views");
            return new Scope(data, borrowedStack, false, onDispose);
        }

        public readonly struct Scope : IDisposable
        {
            readonly UniversalAdditionalCameraData data; readonly VolumeStack stack;
            readonly AntialiasingMode antialiasing; readonly bool valid; readonly bool ownsStack; readonly Action onDispose;
            internal Scope(UniversalAdditionalCameraData data, VolumeStack stack, bool ownsStack, Action onDispose = null)
            {
                this.data = data; this.stack = stack; this.ownsStack = ownsStack; this.onDispose = onDispose; antialiasing = data.antialiasing; valid = true;
                if (antialiasing == AntialiasingMode.TemporalAntiAliasing) data.antialiasing = AntialiasingMode.None;
            }
            public void Dispose()
            {
                if (!valid) return;
                if (data != null) data.antialiasing = antialiasing;
                if (ownsStack && stack != null) VolumeManager.instance.DestroyStack(stack);
                onDispose?.Invoke();
            }
        }
    }
}
