using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;

namespace ViewWeave.HDRP
{
    public static class HdrpQuiltIntegration
    {
        public static void Validate(Camera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                throw new NotSupportedException("HDRP requires Linear color space; set Player Settings > Color Space to Linear");
            if (!(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset)) throw new InvalidOperationException("HDRP render pipeline asset is required");
            var data = camera.GetComponent<HDAdditionalCameraData>();
            if (data == null) throw new InvalidOperationException("HDRP camera data is required");
            if (!VolumeManager.instance.isInitialized) throw new InvalidOperationException("HDRP VolumeManager is not initialized");
        }

        public static Scope BeginViewRender(Camera camera)
        {
            Validate(camera);
            var data = camera.GetComponent<HDAdditionalCameraData>();
            VolumeStack stack = VolumeManager.instance.CreateStack();
            try
            {
                Transform trigger = data.volumeAnchorOverride != null ? data.volumeAnchorOverride : camera.transform;
                VolumeManager.instance.Update(stack, trigger, data.volumeLayerMask);
                ValidateStack(stack);
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
            var data = camera.GetComponent<HDAdditionalCameraData>();
            Transform trigger = data.volumeAnchorOverride != null ? data.volumeAnchorOverride : camera.transform;
            VolumeManager.instance.Update(borrowedStack, trigger, data.volumeLayerMask);
            ValidateStack(borrowedStack);
            return new Scope(data, borrowedStack, false, onDispose);
        }

        static void ValidateStack(VolumeStack stack)
        {
            var motionBlur = stack.GetComponent<MotionBlur>();
            if (motionBlur != null && motionBlur.IsActive()) throw new NotSupportedException("HDRP Motion Blur is temporal and is not supported for quilt views");
            var reflection = stack.GetComponent<ScreenSpaceReflection>();
            if (reflection != null && reflection.enabled.value) throw new NotSupportedException("HDRP Screen Space Reflection is temporal and is not supported for quilt views");
            var gi = stack.GetComponent<GlobalIllumination>();
            if (gi != null && gi.enable.value) throw new NotSupportedException("HDRP screen space global illumination is temporal and is not supported for quilt views");
            var clouds = stack.GetComponent<VolumetricClouds>();
            if (clouds != null && clouds.active && clouds.enable.value) throw new NotSupportedException("HDRP volumetric clouds are temporal and are not supported for quilt views");
            var fog = stack.GetComponent<Fog>();
            if (fog != null && fog.active && fog.enabled.value && fog.enableVolumetricFog.value) throw new NotSupportedException("HDRP volumetric fog is temporal and is not supported for quilt views");
            var exposure = stack.GetComponent<Exposure>();
            if (exposure != null && exposure.IsActive() && exposure.mode.value != ExposureMode.Fixed) throw new NotSupportedException("HDRP exposure must use Fixed mode for quilt views");
        }

        public readonly struct Scope : IDisposable
        {
            readonly HDAdditionalCameraData data; readonly VolumeStack stack; readonly HDAdditionalCameraData.AntialiasingMode antialiasing; readonly bool valid; readonly bool ownsStack; readonly Action onDispose;
            internal Scope(HDAdditionalCameraData data, VolumeStack stack, bool ownsStack, Action onDispose = null)
            {
                this.data = data; this.stack = stack; this.ownsStack = ownsStack; this.onDispose = onDispose; antialiasing = data.antialiasing; valid = true;
                if (antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing) data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
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
