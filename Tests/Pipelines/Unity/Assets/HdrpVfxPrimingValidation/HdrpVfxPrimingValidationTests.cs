#if UNITY_EDITOR && UNITY_INCLUDE_TESTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.VFX;
using UnityEditor;
using ViewWeave;
using ViewWeave.HDRP;

namespace ViewWeave.HdrpVfxPrimingValidation
{
    public sealed class HdrpVfxPrimingValidationTests
    {
        const int Layer = 31;
        const int TileWidth = 128;
        const int TileHeight = 96;
        const int Columns = 4;
        const int Rows = 2;
        const int Views = 8;
        const int Seed = 17;

        [UnityTest]
        public IEnumerator SameCameraWarmupThenQuilt()
        {
            var fixture = new Fixture();
            try
            {
                fixture.Setup();
                yield return fixture.PrepareVfxAsset();
                yield return fixture.WarmupNormalCamera();
                fixture.SetRendererEnabled(false);
                yield return null;
                yield return null;
                yield return null;
                fixture.CaptureNormal(false);
                fixture.SetRendererEnabled(true);
                yield return null;
                yield return null;
                yield return null;
                fixture.CaptureNormal(true);
                fixture.AssertNormalChanged();
                fixture.SwitchToQuilt();
                fixture.CaptureQuilt(false);
                fixture.SetRendererEnabled(true);
                yield return null;
                yield return null;
                yield return null;
                fixture.CaptureQuilt(true);
                fixture.AssertQuiltChanged();
            }
            finally
            {
                fixture.Dispose();
            }
        }

        sealed class Fixture
        {
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            readonly List<BehaviourState> disabled = new List<BehaviourState>();
            readonly string evidenceDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "hdrp-vfx-priming"));
            Scene previousScene;
            Scene fixtureScene;
            Light previousSun;
            RenderPipelineAsset previousDefault;
            RenderPipelineAsset previousQuality;
            HDRenderPipelineAsset pipeline;
            GameObject root;
            GameObject effectObject;
            Camera camera;
            HDAdditionalCameraData cameraData;
            VisualEffect effect;
            VFXRenderer vfxRenderer;
            QuiltRenderer quilt;
            RenderTexture normalTarget;
            Texture2D normalBefore;
            Texture2D normalAfter;
            Texture2D quiltBefore;
            Texture2D quiltAfter;
            string clonedAssetPath;
            bool clonedAssetOwned;
            bool disposed;

            public void Setup()
            {
                previousScene = SceneManager.GetActiveScene();
                previousSun = RenderSettings.sun;
                previousDefault = GraphicsSettings.defaultRenderPipeline;
                previousQuality = QualitySettings.renderPipeline;
                DisableExternalRenderers();
                fixtureScene = SceneManager.CreateScene("HdrpVfxPrimingFixture");
                SceneManager.SetActiveScene(fixtureScene);
                pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                owned.Add(pipeline);
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                root = new GameObject("HdrpVfxPrimingFixture.Root");
                owned.Add(root);
                SceneManager.MoveGameObjectToScene(root, fixtureScene);
                root.layer = Layer;
                CreateCamera();
                CreateVolume();
                CreateLight();
                CreateEffect();
                Directory.CreateDirectory(evidenceDirectory);
                WriteEvidence("setup.txt", "sameCamera=true\nnormalWidth=512\nnormalHeight=384\nquiltWidth=512\nquiltHeight=192\ntileWidth=128\ntileHeight=96\nseed=17\nprepareCamera=false\nlayer=31\n");
            }

            public IEnumerator PrepareVfxAsset()
            {
                yield return null;
                yield return null;
                bool pipelineActive = GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset;
                WriteEvidence("asset-preparation.txt", "stage=pipeline-active\nactive=" + pipelineActive + "\n");
                Assert.IsTrue(pipelineActive, "HDRP pipeline is not active before priming VFX asset import");
                string sourceAssetPath = "Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx";
                clonedAssetPath = AssetDatabase.GenerateUniqueAssetPath("Assets/HdrpVfxPrimingValidation/Simple_Burst_hdrp_priming.vfx");
                bool copied = AssetDatabase.CopyAsset(sourceAssetPath, clonedAssetPath);
                WriteEvidence("asset-preparation.txt", "stage=copy\nsource=" + sourceAssetPath + "\nclone=" + clonedAssetPath + "\ncopySucceeded=" + copied + "\n");
                Assert.IsTrue(copied, "failed to copy priming VFX asset");
                clonedAssetOwned = true;
                AssetDatabase.ImportAsset(clonedAssetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var clone = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(clonedAssetPath);
                WriteEvidence("asset-preparation.txt", "stage=import\ncloneLoaded=" + (clone != null) + "\n");
                Assert.IsNotNull(clone, "priming VFX clone did not import");
                effect.visualEffectAsset = clone;
                effect.Reinit();
                yield return null;
            }

            void CreateCamera()
            {
                var cameraObject = new GameObject("HdrpVfxPrimingFixture.Camera");
                cameraObject.transform.SetParent(root.transform, false);
                cameraObject.layer = Layer;
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.transform.localPosition = new Vector3(0f, 0f, -4f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = 50f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 100f;
                camera.cullingMask = 1 << Layer;
                cameraData = cameraObject.AddComponent<HDAdditionalCameraData>();
                cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                cameraData.backgroundColorHDR = Color.black;
                cameraData.volumeLayerMask = 1 << Layer;
                quilt = cameraObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = camera;
                quilt.GuardBehaviour = cameraObject.AddComponent<HdrpQuiltCamera>();
                quilt.autoRender = false;
                quilt.settings.quiltWidth = TileWidth * Columns;
                quilt.settings.quiltHeight = TileHeight * Rows;
                quilt.settings.columns = Columns;
                quilt.settings.rows = Rows;
                quilt.settings.views = Views;
                quilt.settings.viewAspect = (float)TileWidth / TileHeight;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;
                normalTarget = new RenderTexture(512, 384, 24, RenderTextureFormat.ARGB32) { name = "HdrpVfxPriming.NormalTarget" };
                normalTarget.Create();
                owned.Add(normalTarget);
                camera.targetTexture = normalTarget;
            }

            void CreateVolume()
            {
                var volumeObject = new GameObject("HdrpVfxPrimingFixture.Volume");
                volumeObject.transform.SetParent(root.transform, false);
                volumeObject.layer = Layer;
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                owned.Add(volumeObject);
                owned.Add(profile);
                var exposure = profile.Add<Exposure>(true);
                exposure.mode.overrideState = true;
                exposure.mode.value = ExposureMode.Fixed;
                exposure.fixedExposure.overrideState = true;
                exposure.fixedExposure.value = 4f;
                var visualEnvironment = profile.Add<VisualEnvironment>(true);
                visualEnvironment.skyType.overrideState = true;
                visualEnvironment.skyType.value = 0;
                visualEnvironment.cloudType.overrideState = true;
                visualEnvironment.cloudType.value = 0;
                var indirectLighting = profile.Add<IndirectLightingController>(true);
                indirectLighting.indirectDiffuseLightingMultiplier.overrideState = true;
                indirectLighting.indirectDiffuseLightingMultiplier.value = 0f;
                var bloom = profile.Add<UnityEngine.Rendering.HighDefinition.Bloom>(true);
                bloom.intensity.overrideState = true;
                bloom.intensity.value = 0f;
                var motionBlur = profile.Add<MotionBlur>(true);
                motionBlur.active = true;
                motionBlur.intensity.overrideState = true;
                motionBlur.intensity.value = 0f;
                var reflection = profile.Add<ScreenSpaceReflection>(true);
                reflection.enabled.overrideState = true;
                reflection.enabled.value = false;
                var gi = profile.Add<GlobalIllumination>(true);
                gi.enable.overrideState = true;
                gi.enable.value = false;
                var fog = profile.Add<Fog>(true);
                fog.enabled.overrideState = true;
                fog.enabled.value = false;
                fog.enableVolumetricFog.overrideState = true;
                fog.enableVolumetricFog.value = false;
                var clouds = profile.Add<VolumetricClouds>(true);
                clouds.enable.overrideState = true;
                clouds.enable.value = false;
                volume.sharedProfile = profile;
            }

            void CreateLight()
            {
                var lightObject = new GameObject("HdrpVfxPrimingFixture.Light");
                lightObject.transform.SetParent(root.transform, false);
                lightObject.layer = Layer;
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 10f;
                light.cullingMask = 1 << Layer;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                lightObject.AddComponent<HDAdditionalLightData>();
                RenderSettings.sun = light;
                owned.Add(lightObject);
            }

            void CreateEffect()
            {
                var asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>("Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
                Assert.IsNotNull(asset, "Simple_Burst VFX asset is unavailable");
                effectObject = new GameObject("HdrpVfxPrimingFixture.Effect");
                effectObject.transform.SetParent(root.transform, false);
                effectObject.transform.localPosition = new Vector3(0f, 0.5f, 0f);
                effectObject.layer = Layer;
                effect = effectObject.AddComponent<VisualEffect>();
                effect.visualEffectAsset = asset;
                effect.resetSeedOnPlay = false;
                effect.startSeed = Seed;
                effect.pause = true;
                vfxRenderer = effectObject.GetComponent<VFXRenderer>();
                owned.Add(effectObject);
            }

            public IEnumerator WarmupNormalCamera()
            {
                camera.enabled = true;
                yield return null;
                vfxRenderer = effectObject.GetComponent<VFXRenderer>();
                Assert.IsNotNull(vfxRenderer, "VFXRenderer was not created by the VFX asset");
                for (int frame = 0; frame < 60; ++frame)
                    yield return null;
                Material material = vfxRenderer.sharedMaterial;
                Shader shader = material == null ? null : material.shader;
                string tag = material == null ? "null" : material.GetTag("RenderPipeline", false, "<missing>");
                string shaderEvidence = "stage=shader\nmaterial=" + (material == null ? "null" : material.name) + "\nshader=" + (shader == null ? "null" : shader.name) + "\nshaderSupported=" + (shader != null && shader.isSupported) + "\nshaderPassCount=" + (material == null ? 0 : material.passCount) + "\nrenderPipelineTag=" + tag + "\n";
                if (material != null)
                    for (int pass = 0; pass < material.passCount; ++pass)
                        shaderEvidence += "pass=" + pass + " name=" + material.GetPassName(pass) + "\n";
                WriteEvidence("asset-shader.txt", shaderEvidence);
                Assert.IsNotNull(material, "priming VFX material is unavailable");
                Assert.AreEqual("HDRenderPipeline", tag, "priming VFX shader is not HDRP tagged");
                effect.Reinit();
                effect.Play();
                effect.pause = false;
                for (int step = 0; step < 30; ++step)
                {
                    effect.Simulate(1f / 60f, 1);
                    yield return null;
                }
                effect.pause = true;
                var start = Time.realtimeSinceStartupAsDouble;
                while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartupAsDouble - start < 5.0)
                    yield return null;
                Assert.Greater(effect.aliveParticleCount, 0, "VFX warmup produced no live particles");
                WriteEvidence("warmup.txt", "frames=60\nsimulateSteps=30\nseed=17\nalive=" + effect.aliveParticleCount + "\nculled=" + effect.culled + "\n");
            }

            public void SwitchToQuilt()
            {
                camera.enabled = false;
                Assert.AreSame(normalTarget, camera.targetTexture);
                Assert.AreSame(camera, quilt.sourceCamera);
                Assert.AreSame(camera, quilt.GuardBehaviour.GetComponent<Camera>());
            }

            public void SetRendererEnabled(bool enabled)
            {
                vfxRenderer.enabled = enabled;
                effect.enabled = true;
                effect.pause = true;
            }

            public void CaptureNormal(bool enabled)
            {
                var image = ReadTexture(normalTarget);
                if (enabled) normalAfter = image; else normalBefore = image;
                SaveImage(image, enabled ? "normal-after.png" : "normal-before.png");
            }

            public void CaptureQuilt(bool enabled)
            {
                if (!enabled) SetRendererEnabled(false);
                quilt.RenderQuilt();
                var image = ReadTexture(quilt.QuiltTexture);
                if (enabled) quiltAfter = image; else quiltBefore = image;
                SaveImage(image, enabled ? "quilt-after.png" : "quilt-before.png");
            }

            public void AssertNormalChanged()
            {
                int changed = CountChanged(normalBefore, normalAfter);
                WriteEvidence("normal-result.txt", "changedPixels=" + changed + "\n");
                Assert.Greater(changed, 20, "normal camera renderer toggle changed fewer than 20 pixels");
            }

            public void AssertQuiltChanged()
            {
                var counts = new List<int>();
                for (int view = 0; view < Views; ++view)
                    counts.Add(CountTileChanged(quiltBefore, quiltAfter, view));
                WriteEvidence("quilt-result.txt", "tileChanged=" + string.Join(",", counts) + "\n");
                foreach (int count in counts)
                    Assert.Greater(count, 20, "quilt tile renderer toggle changed fewer than 20 pixels");
            }

            static int CountChanged(Texture2D first, Texture2D second)
            {
                if (first == null || second == null || first.width != second.width || first.height != second.height) return 0;
                var a = first.GetPixels();
                var b = second.GetPixels();
                int changed = 0;
                for (int index = 0; index < a.Length; ++index)
                    if (ColorDistance(a[index], b[index]) > 0.01f) changed++;
                return changed;
            }

            static int CountTileChanged(Texture2D first, Texture2D second, int view)
            {
                if (first == null || second == null) return 0;
                int tileX = (view % Columns) * TileWidth;
                int tileY = (view / Columns) * TileHeight;
                int changed = 0;
                for (int y = tileY; y < tileY + TileHeight; ++y)
                    for (int x = tileX; x < tileX + TileWidth; ++x)
                        if (ColorDistance(first.GetPixel(x, y), second.GetPixel(x, y)) > 0.01f) changed++;
                return changed;
            }

            static float ColorDistance(Color a, Color b)
            {
                return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b);
            }

            static Texture2D ReadTexture(RenderTexture target)
            {
                Assert.IsNotNull(target);
                var previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = target;
                    var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                    image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    image.Apply();
                    return image;
                }
                finally
                {
                    RenderTexture.active = previous;
                }
            }

            void SaveImage(Texture2D image, string name)
            {
                File.WriteAllBytes(Path.Combine(evidenceDirectory, name), image.EncodeToPNG());
            }

            void WriteEvidence(string name, string content)
            {
                File.WriteAllText(Path.Combine(evidenceDirectory, name), AsciiEvidence(content));
            }

            static string AsciiEvidence(string content)
            {
                if (content == null) return string.Empty;
                char[] chars = content.ToCharArray();
                for (int index = 0; index < chars.Length; ++index)
                {
                    char value = chars[index];
                    if (value != '\n' && value != '\r' && value != '\t' && (value < 32 || value > 126))
                        chars[index] = '?';
                }
                return new string(chars);
            }

            void DisableExternalRenderers()
            {
                for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; ++sceneIndex)
                {
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (!scene.isLoaded) continue;
                    foreach (var sceneRoot in scene.GetRootGameObjects())
                    {
                        foreach (var cameraComponent in sceneRoot.GetComponentsInChildren<Camera>(true))
                            DisableIfExternal(cameraComponent);
                        foreach (var lightComponent in sceneRoot.GetComponentsInChildren<Light>(true))
                            DisableIfExternal(lightComponent);
                    }
                }
            }

            void DisableIfExternal(Behaviour component)
            {
                if (component != null && component.enabled)
                {
                    component.enabled = false;
                    disabled.Add(new BehaviourState { component = component, enabled = true });
                }
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (camera != null) camera.enabled = false;
                if (camera != null) camera.targetTexture = null;
                if (RenderTexture.active == normalTarget) RenderTexture.active = null;
                DestroyTexture(ref normalBefore);
                DestroyTexture(ref normalAfter);
                DestroyTexture(ref quiltBefore);
                DestroyTexture(ref quiltAfter);
                if (normalTarget != null)
                {
                    normalTarget.Release();
                    UnityEngine.Object.DestroyImmediate(normalTarget);
                    normalTarget = null;
                }
                for (int index = owned.Count - 1; index >= 0; --index)
                    if (owned[index] != null) UnityEngine.Object.DestroyImmediate(owned[index]);
                owned.Clear();
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                if (clonedAssetOwned && !string.IsNullOrEmpty(clonedAssetPath))
                    AssetDatabase.DeleteAsset(clonedAssetPath);
                foreach (var state in disabled)
                    if (state.component != null) state.component.enabled = state.enabled;
                RenderSettings.sun = previousSun;
                if (previousScene.IsValid() && previousScene.isLoaded) SceneManager.SetActiveScene(previousScene);
                if (fixtureScene.IsValid() && fixtureScene.isLoaded) SceneManager.UnloadSceneAsync(fixtureScene);
            }

            static void DestroyTexture(ref Texture2D image)
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                image = null;
            }

            struct BehaviourState
            {
                public Behaviour component;
                public bool enabled;
            }
        }
    }
}
#endif
