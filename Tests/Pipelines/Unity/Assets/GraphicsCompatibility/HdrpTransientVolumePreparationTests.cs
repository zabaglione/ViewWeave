#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.HDRP;

namespace ViewWeave.GraphicsCompatibility
{
    public sealed class HdrpTransientVolumePreparationTests
    {
        [UnityTest]
        public IEnumerator AdaptsDefaultEffectsAndRestoresCameraState()
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(false);
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.WarmupPipeline();
                fixture.CaptureCameraState();
                fixture.AssertEffectiveOverrides();
                fixture.AssertPreparationCacheReused();
                fixture.RenderAndAssertNonBlack();
                fixture.AssertValidationStackReused();
                fixture.AssertRestored();
            }
            finally { fixture?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator PreservesFixedExposureBaseline()
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(true);
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.WarmupPipeline();
                fixture.CaptureCameraState();
                fixture.AssertEffectiveExposure(4f);
                fixture.AssertRestored();
            }
            finally { fixture?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator StrictPublicIntegrationStillRejectsUnsupportedStack()
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(false);
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.WarmupPipeline();
                fixture.CaptureCameraState();
                Assert.Throws<NotSupportedException>(() => HdrpQuiltIntegration.BeginViewRender(fixture.Camera));
                fixture.AssertRestored();
            }
            finally { fixture?.Dispose(); }
        }

        [UnityTest]
        public IEnumerator FailedFallbackRestoresMaskAndLeavesNoTransientVolume()
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(false) { FallbackFixedExposure = false };
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.WarmupPipeline();
                fixture.CaptureCameraState();
                int before = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                Assert.Throws<NotSupportedException>(() => fixture.Prepare());
                int after = UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                Assert.AreEqual(before, after);
                fixture.AssertRestored();
            }
            finally { fixture?.Dispose(); }
        }

        sealed class Fixture : IDisposable
        {
            readonly bool fixedExposure;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            GameObject root;
            HDRenderPipelineAsset pipeline;
            RenderPipelineAsset oldDefault;
            RenderPipelineAsset oldQuality;
            GraphicsFixtureScope scope;
            QuiltRenderer quilt;
            HdrpQuiltCamera guard;
            HDAdditionalCameraData data;
            Volume baselineVolume;
            VolumeProfile baselineProfile;
            int originalMask;
            HDAdditionalCameraData.AntialiasingMode originalAntialiasing;
            Volume cachedVolume;
            public bool FallbackFixedExposure { get; set; } = true;
            public Camera Camera { get; private set; }

            public Fixture(bool fixedExposure) { this.fixedExposure = fixedExposure; }

            public void Setup()
            {
                oldDefault = GraphicsSettings.defaultRenderPipeline;
                oldQuality = QualitySettings.renderPipeline;
                pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;

                root = new GameObject("HDRPTransientVolumeFixture");
                owned.Add(root);
                var cameraObject = new GameObject("QuiltCamera");
                cameraObject.transform.SetParent(root.transform, false);
                Camera = cameraObject.AddComponent<Camera>();
                Camera.enabled = false;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = Color.black;
                Camera.fieldOfView = 50f;
                data = cameraObject.AddComponent<HDAdditionalCameraData>();
                data.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                data.backgroundColorHDR = Color.black;
                quilt = cameraObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = Camera;
                guard = cameraObject.AddComponent<HdrpQuiltCamera>();
                guard.fallbackFixedExposure = FallbackFixedExposure;
                quilt.GuardBehaviour = guard;
                quilt.settings.quiltWidth = 256;
                quilt.settings.quiltHeight = 192;
                quilt.settings.columns = 2;
                quilt.settings.rows = 2;
                quilt.settings.views = 4;
                quilt.settings.viewAspect = 1f;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;

                var lightObject = new GameObject("Directional");
                lightObject.transform.SetParent(root.transform, false);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 10f;
                lightObject.AddComponent<HDAdditionalLightData>();
                owned.Add(lightObject);
                AddCube();
                AddBaselineVolume();
                scope = GraphicsFixtureScope.Create(root);
                scope.Apply(Camera);
                baselineVolume.gameObject.layer = 0;
                data.volumeLayerMask = 1;
                originalMask = data.volumeLayerMask;
                originalAntialiasing = data.antialiasing;
            }

            public void CaptureCameraState()
            {
                originalMask = data.volumeLayerMask;
                originalAntialiasing = data.antialiasing;
            }

            public void WarmupPipeline()
            {
                RenderTexture target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32);
                target.Create();
                RenderTexture oldTarget = Camera.targetTexture;
                bool oldEnabled = Camera.enabled;
                try
                {
                    Camera.targetTexture = target;
                    Camera.enabled = true;
                    Camera.Render();
                }
                finally
                {
                    Camera.enabled = oldEnabled;
                    Camera.targetTexture = oldTarget;
                    target.Release();
                    UnityEngine.Object.DestroyImmediate(target);
                }
            }

            void AddCube()
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Standard HDRP Cube";
                cube.transform.SetParent(root.transform, false);
                cube.transform.localPosition = new Vector3(0f, 0f, 3f);
                var shader = Shader.Find("HDRP/Lit");
                Assert.IsNotNull(shader, "HDRP Lit shader is unavailable");
                var material = new Material(shader) { color = Color.red };
                cube.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(cube);
                owned.Add(material);
            }

            void AddBaselineVolume()
            {
                var volumeObject = new GameObject("HDRP Baseline Volume");
                volumeObject.transform.SetParent(root.transform, false);
                baselineVolume = volumeObject.AddComponent<Volume>();
                baselineVolume.isGlobal = true;
                baselineVolume.priority = 10f;
                baselineProfile = ScriptableObject.CreateInstance<VolumeProfile>();
                var motion = baselineProfile.Add<MotionBlur>(true);
                motion.intensity.value = fixedExposure ? 0f : 1f;
                motion.intensity.overrideState = true;
                var reflection = baselineProfile.Add<ScreenSpaceReflection>(true);
                reflection.enabled.value = !fixedExposure;
                reflection.enabled.overrideState = true;
                var gi = baselineProfile.Add<GlobalIllumination>(true);
                gi.enable.value = !fixedExposure;
                gi.enable.overrideState = true;
                var fog = baselineProfile.Add<Fog>(true);
                fog.enableVolumetricFog.value = !fixedExposure;
                fog.enableVolumetricFog.overrideState = true;
                var clouds = baselineProfile.Add<VolumetricClouds>(true);
                clouds.enable.value = !fixedExposure;
                clouds.enable.overrideState = true;
                var exposure = baselineProfile.Add<Exposure>(true);
                exposure.mode.value = fixedExposure ? ExposureMode.Fixed : ExposureMode.Automatic;
                exposure.mode.overrideState = true;
                exposure.fixedExposure.value = 4f;
                exposure.fixedExposure.overrideState = true;
                baselineVolume.sharedProfile = baselineProfile;
                owned.Add(volumeObject);
                owned.Add(baselineProfile);
            }

            public IDisposable Prepare() => guard.PrepareQuilt(Camera);

            public void AssertEffectiveOverrides() => AssertEffectiveExposure(12f);

            public void AssertEffectiveExposure(float expectedExposure)
            {
                using (guard.PrepareForQuilt())
                {
                    VolumeStack stack = VolumeManager.instance.CreateStack();
                    try
                    {
                        VolumeManager.instance.Update(stack, Camera.transform, data.volumeLayerMask);
                        Assert.IsFalse(stack.GetComponent<MotionBlur>().IsActive());
                        Assert.IsFalse(stack.GetComponent<ScreenSpaceReflection>().enabled.value);
                        Assert.IsFalse(stack.GetComponent<GlobalIllumination>().enable.value);
                        Assert.IsFalse(stack.GetComponent<Fog>().enableVolumetricFog.value);
                        Assert.IsFalse(stack.GetComponent<VolumetricClouds>().enable.value);
                        var exposure = stack.GetComponent<Exposure>();
                        Assert.AreEqual(ExposureMode.Fixed, exposure.mode.value);
                        Assert.AreEqual(expectedExposure, exposure.fixedExposure.value, 0.001f);
                        Volume transient = FindTransientVolume();
                        Assert.IsNotNull(transient);
                        cachedVolume = transient;
                    }
                    finally { VolumeManager.instance.DestroyStack(stack); }
                }
                Volume transientAfter = FindTransientVolume();
                Assert.IsNotNull(transientAfter);
                Assert.IsFalse(transientAfter.enabled);
            }

            public void AssertPreparationCacheReused()
            {
                using (guard.PrepareForQuilt())
                {
                    Volume transient = FindTransientVolume();
                    Assert.IsNotNull(transient);
                    Assert.IsTrue(transient.enabled);
                    Assert.AreSame(cachedVolume, transient);
                }
            }

            static Volume FindTransientVolume()
            {
                foreach (Volume volume in Resources.FindObjectsOfTypeAll<Volume>())
                    if (volume.name == "ViewWeave HDRP Quilt Volume") return volume;
                return null;
            }

            public void RenderAndAssertNonBlack()
            {
                quilt.RenderQuilt();
                Assert.IsNotNull(quilt.QuiltTexture);
                RenderTexture old = RenderTexture.active;
                Texture2D image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = quilt.QuiltTexture;
                    image.ReadPixels(new Rect(0f, 0f, image.width, image.height), 0, 0);
                    image.Apply();
                    int changed = 0;
                    int redDominant = 0;
                    Color reference = image.GetPixel(0, 0);
                    bool nonuniform = false;
                    foreach (Color pixel in image.GetPixels())
                    {
                        if (pixel.r > 0.02f || pixel.g > 0.02f || pixel.b > 0.02f) changed++;
                        if (pixel.r > 0.05f && pixel.r > pixel.g * 1.35f && pixel.r > pixel.b * 1.35f) redDominant++;
                        if (Mathf.Abs(pixel.r - reference.r) + Mathf.Abs(pixel.g - reference.g) + Mathf.Abs(pixel.b - reference.b) > 0.02f) nonuniform = true;
                    }
                    Assert.Greater(changed, 20);
                    Assert.Greater(redDominant, 20, "HDRP Cube quilt has no red-dominant pixels");
                    Assert.IsTrue(nonuniform, "HDRP Cube quilt is uniform");
                }
                finally { RenderTexture.active = old; UnityEngine.Object.DestroyImmediate(image); }
            }

            public void AssertRestored()
            {
                Assert.AreEqual(originalMask, data.volumeLayerMask.value);
                Assert.AreEqual(originalAntialiasing, data.antialiasing);
            }

            public void AssertValidationStackReused()
            {
                Assert.AreEqual(1, guard.ValidationStackCreationCount);
            }

            public void Dispose()
            {
                RenderTexture.active = null;
                scope?.Dispose();
                foreach (var item in owned) if (item != null) UnityEngine.Object.DestroyImmediate(item);
                GraphicsSettings.defaultRenderPipeline = oldDefault;
                QualitySettings.renderPipeline = oldQuality;
                if (pipeline != null) UnityEngine.Object.DestroyImmediate(pipeline);
            }
        }
    }
}
#endif
