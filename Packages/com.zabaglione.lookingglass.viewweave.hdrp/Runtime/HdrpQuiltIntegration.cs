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
                UpdateStack(camera, data, stack);
                ValidateStack(stack);
                return new Scope(data, stack, true);
            }
            catch { VolumeManager.instance.DestroyStack(stack); throw; }
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack borrowedStack, Action onDispose)
        {
            Validate(camera);
            if (borrowedStack == null || !borrowedStack.isValid) throw new InvalidOperationException("a valid cached volume stack is required");
            var data = camera.GetComponent<HDAdditionalCameraData>();
            UpdateStack(camera, data, borrowedStack);
            ValidateStack(borrowedStack);
            return new Scope(data, borrowedStack, false, onDispose);
        }

        internal static Scope BeginViewRender(Camera camera, VolumeStack borrowedStack, Preparation preparation, Action onDispose)
        {
            Validate(camera);
            if (borrowedStack == null || !borrowedStack.isValid) throw new InvalidOperationException("a valid cached volume stack is required");
            if (preparation == null || !preparation.adapted) throw new InvalidOperationException("an adapted HDRP quilt preparation is required");
            var data = camera.GetComponent<HDAdditionalCameraData>();
            preparation.EnterView();
            try
            {
                UpdateStack(camera, data, borrowedStack);
                ValidateStack(borrowedStack);
                return new Scope(data, borrowedStack, false, () =>
                {
                    preparation.ExitView();
                    onDispose?.Invoke();
                });
            }
            catch
            {
                preparation.ExitView();
                throw;
            }
        }

        internal static Preparation BeginPreparation(Camera camera, VolumeStack cachedStack, bool adaptUnsupportedEffects, bool fallbackFixedExposure, float fallbackFixedExposureEV)
        {
            Validate(camera);
            if (cachedStack == null || !cachedStack.isValid) throw new InvalidOperationException("a valid cached volume stack is required");
            var data = camera.GetComponent<HDAdditionalCameraData>();
            int originalMask = data.volumeLayerMask;
            if (!adaptUnsupportedEffects) return new Preparation(camera, data, cachedStack, null, false, null, null, originalMask, originalMask, null);
            VolumeStack baselineStack = VolumeManager.instance.CreateStack();
            GameObject volumeObject = null;
            VolumeProfile profile = null;
            try
            {
                UpdateStack(camera, data, baselineStack);
                float fixedExposure = ResolveFixedExposure(baselineStack, fallbackFixedExposure, fallbackFixedExposureEV);
                int privateLayer = SelectPrivateLayer(originalMask, null);
                volumeObject = new GameObject("ViewWeave HDRP Quilt Volume") { hideFlags = HideFlags.HideAndDontSave, layer = privateLayer };
                Volume volume = volumeObject.AddComponent<Volume>();
                volume.hideFlags = HideFlags.HideAndDontSave;
                volume.isGlobal = true;
                volume.priority = float.MaxValue;
                volume.weight = 1f;
                profile = CreateQuiltProfile(fixedExposure);
                volume.sharedProfile = profile;
                volume.enabled = false;
                int scopedMask = originalMask == 0 ? (1 << privateLayer) : originalMask;
                return new Preparation(camera, data, cachedStack, baselineStack, true, volumeObject, volume.sharedProfile, originalMask, scopedMask, volume);
            }
            catch
            {
                data.volumeLayerMask = originalMask;
                if (volumeObject != null) DestroyObject(volumeObject);
                DestroyProfile(profile);
                if (baselineStack != null && baselineStack.isValid && VolumeManager.instance.isInitialized) VolumeManager.instance.DestroyStack(baselineStack);
                throw;
            }
        }

        static void UpdateStack(Camera camera, HDAdditionalCameraData data, VolumeStack stack)
        {
            VolumeManager.instance.Update(stack, GetTrigger(camera, data), data.volumeLayerMask);
        }

        static Transform GetTrigger(Camera camera, HDAdditionalCameraData data)
        {
            return data.volumeAnchorOverride != null ? data.volumeAnchorOverride : camera.transform;
        }

        static float ResolveFixedExposure(VolumeStack baselineStack, bool fallbackFixedExposure, float fallbackFixedExposureEV)
        {
            var exposure = baselineStack.GetComponent<Exposure>();
            if (exposure != null && exposure.IsActive() && exposure.mode.value == ExposureMode.Fixed)
                return exposure.fixedExposure.value;
            if (!fallbackFixedExposure)
                throw new NotSupportedException("HDRP exposure must use Fixed mode for quilt views");
            return fallbackFixedExposureEV;
        }

        static int SelectPrivateLayer(int originalMask, GameObject ownedVolumeObject)
        {
            if (originalMask != 0)
            {
                for (int layer = 0; layer < 32; ++layer)
                    if ((originalMask & (1 << layer)) != 0) return layer;
            }

            bool[] occupied = new bool[32];
            foreach (Volume volume in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include))
                if (volume != null && volume.gameObject != null && volume.gameObject != ownedVolumeObject) occupied[volume.gameObject.layer] = true;
            if (!occupied[30]) return 30;
            for (int layer = 0; layer < 32; ++layer)
                if (!occupied[layer]) return layer;
            throw new InvalidOperationException("HDRP quilt preparation requires an unoccupied Volume layer");
        }

        static VolumeProfile CreateQuiltProfile(float fixedExposure)
        {
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;

            var motionBlur = profile.Add<MotionBlur>(false);
            motionBlur.intensity.value = 0f;
            motionBlur.intensity.overrideState = true;

            var reflection = profile.Add<ScreenSpaceReflection>(false);
            reflection.enabled.value = false;
            reflection.enabled.overrideState = true;

            var globalIllumination = profile.Add<GlobalIllumination>(false);
            globalIllumination.enable.value = false;
            globalIllumination.enable.overrideState = true;

            var fog = profile.Add<Fog>(false);
            fog.denoisingMode.value = FogDenoisingMode.Gaussian;
            fog.denoisingMode.overrideState = true;

            var clouds = profile.Add<VolumetricClouds>(false);
            clouds.enable.value = false;
            clouds.enable.overrideState = true;

            var exposure = profile.Add<Exposure>(false);
            exposure.mode.value = ExposureMode.Fixed;
            exposure.mode.overrideState = true;
            exposure.fixedExposure.value = fixedExposure;
            exposure.fixedExposure.overrideState = true;
            return profile;
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
            if (fog != null && fog.active && fog.enabled.value && fog.enableVolumetricFog.value &&
                (fog.denoisingMode.value == FogDenoisingMode.Reprojection || fog.denoisingMode.value == FogDenoisingMode.Both))
                throw new NotSupportedException("HDRP volumetric fog reprojection requires isolated quilt view histories; use Gaussian or None filtering for a shared camera");
            var exposure = stack.GetComponent<Exposure>();
            if (exposure != null && exposure.IsActive() && exposure.mode.value != ExposureMode.Fixed) throw new NotSupportedException("HDRP exposure must use Fixed mode for quilt views");
        }

        public sealed class Preparation : IDisposable
        {
            internal readonly Camera camera;
            internal readonly HDAdditionalCameraData data;
            internal readonly VolumeStack stack;
            readonly VolumeStack baselineStack;
            internal readonly bool adapted;
            readonly GameObject volumeObject;
            readonly VolumeProfile profile;
            int originalMask;
            int scopedMask;
            Volume volume;
            bool destroyed;
            bool leaseActive = true;
            bool viewActive;

            internal Preparation(Camera camera, HDAdditionalCameraData data, VolumeStack stack, VolumeStack baselineStack, bool adapted, GameObject volumeObject, VolumeProfile profile, int originalMask, int scopedMask, Volume volume)
            {
                this.camera = camera; this.data = data; this.stack = stack; this.baselineStack = baselineStack; this.adapted = adapted; this.volumeObject = volumeObject; this.profile = profile; this.originalMask = originalMask; this.scopedMask = scopedMask; this.volume = volume;
            }

            public void Dispose()
            {
                if (destroyed || !leaseActive) return;
                leaseActive = false;
                ExitView();
                if (data != null) data.volumeLayerMask = originalMask;
            }

            internal void Destroy()
            {
                if (destroyed) return;
                Dispose();
                destroyed = true;
                if (volumeObject != null) DestroyObject(volumeObject);
                DestroyProfile(profile);
                if (baselineStack != null && baselineStack.isValid && VolumeManager.instance.isInitialized) VolumeManager.instance.DestroyStack(baselineStack);
                volume = null;
            }

            internal void BeginLease(bool fallbackFixedExposure, float fallbackFixedExposureEV)
            {
                if (destroyed || leaseActive) throw new InvalidOperationException("HDRP quilt preparation is already active");
                if (!adapted) return;
                originalMask = data.volumeLayerMask;
                ExitView();
                UpdateStack(camera, data, baselineStack);
                float fixedExposure = ResolveFixedExposure(baselineStack, fallbackFixedExposure, fallbackFixedExposureEV);
                int privateLayer = SelectPrivateLayer(originalMask, volumeObject);
                volumeObject.layer = privateLayer;
                Exposure exposure = null;
                foreach (var component in profile.components)
                    if (component is Exposure candidate) { exposure = candidate; break; }
                if (exposure == null) throw new InvalidOperationException("HDRP quilt exposure component is unavailable");
                exposure.fixedExposure.value = fixedExposure;
                int scopedMask = originalMask == 0 ? (1 << privateLayer) : originalMask;
                this.scopedMask = scopedMask;
                leaseActive = true;
            }

            internal void EnterView()
            {
                if (destroyed || !leaseActive || !adapted || volume == null) throw new InvalidOperationException("HDRP quilt preparation is not active");
                data.volumeLayerMask = scopedMask;
                volume.enabled = true;
                viewActive = true;
            }

            internal void ExitView()
            {
                if (volume == null || !viewActive) return;
                volume.enabled = false;
                data.volumeLayerMask = originalMask;
                viewActive = false;
            }
        }

        public struct Scope : IDisposable
        {
            readonly HDAdditionalCameraData data; readonly VolumeStack stack; readonly bool valid; readonly bool ownsStack; readonly Action onDispose; readonly HDAdditionalCameraData.AntialiasingMode antialiasing;
            bool disposed;
            internal Scope(HDAdditionalCameraData data, VolumeStack stack, bool ownsStack, Action onDispose = null)
            {
                this.data = data; this.stack = stack; this.ownsStack = ownsStack; this.onDispose = onDispose; valid = true; disposed = false; antialiasing = data.antialiasing;
                if (antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing) data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
            }
            public void Dispose()
            {
                if (!valid || disposed) return;
                disposed = true;
                if (data != null) data.antialiasing = antialiasing;
                if (ownsStack && stack != null) VolumeManager.instance.DestroyStack(stack);
                onDispose?.Invoke();
            }
        }

        static void DestroyObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        static void DestroyProfile(VolumeProfile profile)
        {
            if (profile == null) return;
            var components = profile.components;
            for (int i = components.Count - 1; i >= 0; --i)
                DestroyObject(components[i]);
            DestroyObject(profile);
        }
    }
}
