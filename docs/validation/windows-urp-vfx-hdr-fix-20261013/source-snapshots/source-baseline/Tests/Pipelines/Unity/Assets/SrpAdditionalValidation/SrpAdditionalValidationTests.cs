using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.URP;
using ViewWeave.HDRP;
using HD = UnityEngine.Rendering.HighDefinition;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace ViewWeave.SrpAdditionalValidation
{
    public sealed class SrpAdditionalValidationTests
    {
        [UnityTest] public IEnumerator UrpFxaa() => RunUrp("urp-fxaa", AntialiasingMode.FastApproximateAntialiasing, false, false);
        [UnityTest] public IEnumerator UrpSmaa() => RunUrp("urp-smaa", AntialiasingMode.SubpixelMorphologicalAntiAliasing, false, false);
        [UnityTest] public IEnumerator UrpMsaa() => RunUrp("urp-msaa", AntialiasingMode.None, true, false);
        [UnityTest] public IEnumerator UrpMsaa2x() => RunUrp("urp-msaa-2x", AntialiasingMode.None, true, false, 2);
        [UnityTest] public IEnumerator UrpMsaa8x() => RunUrp("urp-msaa-8x", AntialiasingMode.None, true, false, 8);
        [UnityTest] public IEnumerator UrpDepthOfField() => RunUrp("urp-dof", AntialiasingMode.None, false, true);
        [UnityTest] public IEnumerator HdrpDepthOfField() => RunHdrp("hdrp-dof");

        IEnumerator RunUrp(string name, AntialiasingMode mode, bool msaa, bool dof, int msaaSamples = 4)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(name, false);
                fixture.Setup(mode, msaa, dof, msaaSamples);
                yield return null;
                yield return null;
                fixture.Capture(false);
                fixture.Capture(true);
                fixture.AssertChanged();
            }
            finally { fixture?.Dispose(); }
        }

        IEnumerator RunHdrp(string name)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(name, true);
                fixture.Setup(AntialiasingMode.None, false, true);
                yield return null;
                yield return null;
                fixture.Capture(false);
                fixture.Capture(true);
                fixture.AssertChanged();
            }
            finally { fixture?.Dispose(); }
        }

        sealed class Fixture
        {
            const int TileWidth = 128;
            const int TileHeight = 96;
            const int ViewCount = 8;
            readonly string name;
            readonly bool hdrp;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            readonly List<Behaviour> external = new List<Behaviour>();
            readonly List<bool> externalStates = new List<bool>();
            GameObject root;
            Camera camera;
            QuiltRenderer quilt;
            Volume volume;
            VolumeProfile profile;
            Texture2D before;
            Texture2D after;
            Scene oldScene;
            Scene fixtureScene;
            RenderPipelineAsset oldDefault;
            RenderPipelineAsset oldQuality;
            RenderTexture oldActive;
            bool oldAllowMsaa;
            RenderSettingsState oldRenderSettings;
            UniversalRenderPipelineAsset urp;
            UniversalAdditionalCameraData urpCameraData;
            HD.HDRenderPipelineAsset hdrpAsset;
            HD.HDAdditionalCameraData hdrpCameraData;
            DepthOfField urpDof;
            HD.DepthOfField hdrpDof;
            AntialiasingMode urpAaMode;
            bool urpMsaa;

            struct RenderSettingsState
            {
                public Light sun;
                public Color ambient;
            }

            public Fixture(string value, bool isHdrp) { name = value; hdrp = isHdrp; }

            int requestedMsaaSamples;
            int expectedMsaaSamples;

            public void Setup(AntialiasingMode aa, bool msaa, bool dof, int msaaSamples = 4)
            {
                oldScene = SceneManager.GetActiveScene();
                oldDefault = GraphicsSettings.defaultRenderPipeline;
                oldQuality = QualitySettings.renderPipeline;
                oldActive = RenderTexture.active;
                oldRenderSettings.sun = RenderSettings.sun;
                oldRenderSettings.ambient = RenderSettings.ambientLight;
                SnapshotExternalObjects();
                fixtureScene = SceneManager.CreateScene("SrpAdditionalValidation_" + name);
                SceneManager.SetActiveScene(fixtureScene);
                root = new GameObject("SrpAdditionalValidation_" + name);
                root.layer = 31;
                SceneManager.MoveGameObjectToScene(root, fixtureScene);
                requestedMsaaSamples = msaaSamples;
                if (hdrp) SetupHdrp(); else SetupUrp(aa, msaa);
                AddCamera(dof);
                RenderTextureFormat viewFormat = camera.allowHDR && urp != null && urp.supportsHDR
                    ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32;
                bool viewSrgb = viewFormat == RenderTextureFormat.ARGBHalf ? false : QualitySettings.activeColorSpace == ColorSpace.Linear;
                expectedMsaaSamples = msaa ? SupportedMsaaSamples(msaaSamples, viewFormat, viewSrgb) : 1;
                oldAllowMsaa = camera.allowMSAA;
                urpAaMode = aa;
                urpMsaa = msaa;
                AddSceneGeometry();
                AddLight();
                AddVolume(dof);
            }

            void SetupUrp(AntialiasingMode aa, bool msaa)
            {
                UniversalRendererData data = ScriptableObject.CreateInstance<UniversalRendererData>();
#if UNITY_EDITOR
                data.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
#endif
                urp = UniversalRenderPipelineAsset.Create(data);
                urp.supportsHDR = true;
                urp.msaaSampleCount = msaa ? requestedMsaaSamples : 1;
                owned.Add(data);
                owned.Add(urp);
                GraphicsSettings.defaultRenderPipeline = urp;
                QualitySettings.renderPipeline = urp;
                urpCameraData = null;
            }

            void SetupHdrp()
            {
                hdrpAsset = ScriptableObject.CreateInstance<HD.HDRenderPipelineAsset>();
                HD.RenderPipelineSettings settings = hdrpAsset.currentPlatformRenderPipelineSettings;
                settings.supportedLitShaderMode = HD.RenderPipelineSettings.SupportedLitShaderMode.ForwardOnly;
                hdrpAsset.currentPlatformRenderPipelineSettings = settings;
                owned.Add(hdrpAsset);
                GraphicsSettings.defaultRenderPipeline = hdrpAsset;
                QualitySettings.renderPipeline = hdrpAsset;
            }

            void AddCamera(bool dof)
            {
                GameObject cameraObject = new GameObject("DedicatedCamera");
                cameraObject.layer = 31;
                cameraObject.transform.SetParent(root.transform, false);
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = 50f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 100f;
                camera.cullingMask = 1 << 31;
                quilt = cameraObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = camera;
                quilt.settings.quiltWidth = TileWidth * 4;
                quilt.settings.quiltHeight = TileHeight * 2;
                quilt.settings.columns = 4;
                quilt.settings.rows = 2;
                quilt.settings.views = ViewCount;
                quilt.settings.viewAspect = TileWidth / (float)TileHeight;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;
                if (hdrp)
                {
                    hdrpCameraData = cameraObject.AddComponent<HD.HDAdditionalCameraData>();
                    hdrpCameraData.volumeLayerMask = 1 << 31;
                    hdrpCameraData.clearColorMode = HD.HDAdditionalCameraData.ClearColorMode.Color;
                    hdrpCameraData.backgroundColorHDR = Color.black;
                    quilt.GuardBehaviour = cameraObject.AddComponent<HdrpQuiltCamera>();
                }
                else
                {
                    urpCameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                    urpCameraData.renderType = CameraRenderType.Base;
                    urpCameraData.renderPostProcessing = true;
                    urpCameraData.volumeLayerMask = 1 << 31;
                    quilt.GuardBehaviour = cameraObject.AddComponent<UrpQuiltCamera>();
                }
                owned.Add(cameraObject);
            }

            void AddSceneGeometry()
            {
                AddShape("Near", new Vector3(-0.7f, 0f, 1.5f), Color.magenta, PrimitiveType.Cube, 0.25f);
                AddShape("Focus", new Vector3(0f, 0f, 3f), Color.white, PrimitiveType.Cube, 0.5f);
                AddShape("Far", new Vector3(0.7f, 0f, 5f), Color.cyan, PrimitiveType.Cube, 0.35f);
                GameObject edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
                edge.name = "ThinDiagonal";
                edge.layer = 31;
                edge.transform.SetParent(root.transform, false);
                edge.transform.SetPositionAndRotation(new Vector3(0f, -0.8f, 3f), Quaternion.Euler(0f, 0f, 35f));
                edge.transform.localScale = new Vector3(0.025f, 1.2f, 0.025f);
                SetMaterial(edge, Color.green);
                owned.Add(edge);
            }

            void AddShape(string objectName, Vector3 position, Color color, PrimitiveType type, float scale)
            {
                GameObject item = GameObject.CreatePrimitive(type);
                item.name = objectName;
                item.layer = 31;
                item.transform.SetParent(root.transform, false);
                item.transform.localPosition = position;
                item.transform.localScale = Vector3.one * scale;
                SetMaterial(item, color);
                owned.Add(item);
            }

            void SetMaterial(GameObject item, Color color)
            {
                Shader shader = Shader.Find(hdrp ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
                if (shader == null) Assert.Fail("Lit shader is unavailable");
                Material material = new Material(shader);
                material.color = color;
                item.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(material);
            }

            void AddLight()
            {
                GameObject item = new GameObject("DirectionalLight");
                item.layer = 31;
                item.transform.SetParent(root.transform, false);
                item.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                Light light = item.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = hdrp ? 10f : 1.2f;
                light.cullingMask = 1 << 31;
                if (hdrp)
                {
                    item.AddComponent<HD.HDAdditionalLightData>();
                    light.lightUnit = UnityEngine.Rendering.LightUnit.Lux;
                    light.intensity = 10f;
                }
                RenderSettings.sun = light;
                owned.Add(item);
            }

            void AddVolume(bool dof)
            {
                GameObject item = new GameObject("ValidationVolume");
                item.layer = 31;
                item.transform.SetParent(root.transform, false);
                volume = item.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                if (hdrp)
                {
                    HD.Exposure exposure = profile.Add<HD.Exposure>(true);
                    exposure.mode.value = HD.ExposureMode.Fixed;
                    exposure.mode.overrideState = true;
                    exposure.fixedExposure.value = 4f;
                    exposure.fixedExposure.overrideState = true;
                    HD.MotionBlur motion = profile.Add<HD.MotionBlur>(true);
                    motion.active = true;
                    motion.intensity.value = 0f;
                    motion.intensity.overrideState = true;
                    HD.ScreenSpaceReflection reflection = profile.Add<HD.ScreenSpaceReflection>(true);
                    reflection.enabled.value = false;
                    reflection.enabled.overrideState = true;
                    HD.GlobalIllumination gi = profile.Add<HD.GlobalIllumination>(true);
                    gi.active = true;
                    gi.enable.value = false;
                    gi.enable.overrideState = true;
                    HD.Fog fog = profile.Add<HD.Fog>(true);
                    fog.enabled.value = false;
                    fog.enabled.overrideState = true;
                    HD.VolumetricClouds clouds = profile.Add<HD.VolumetricClouds>(true);
                    clouds.active = true;
                    clouds.enable.value = false;
                    clouds.enable.overrideState = true;
                    fog.enableVolumetricFog.value = false;
                    fog.enableVolumetricFog.overrideState = true;
                    HD.VisualEnvironment environment = profile.Add<HD.VisualEnvironment>(true);
                    environment.skyType.value = 0;
                    environment.skyType.overrideState = true;
                    environment.cloudType.value = 0;
                    environment.cloudType.overrideState = true;
                    HD.Bloom bloom = profile.Add<HD.Bloom>(true);
                    bloom.intensity.value = 0f;
                    bloom.intensity.overrideState = true;
                    HD.Tonemapping tonemapping = profile.Add<HD.Tonemapping>(true);
                    tonemapping.mode.value = HD.TonemappingMode.Neutral;
                    tonemapping.mode.overrideState = true;
                }
                if (dof)
                {
                    if (hdrp)
                    {
                        hdrpDof = profile.Add<HD.DepthOfField>(true);
                        hdrpDof.focusMode.value = HD.DepthOfFieldMode.Manual;
                        hdrpDof.focusMode.overrideState = true;
                        hdrpDof.focusDistance.value = 3f;
                        hdrpDof.focusDistance.overrideState = true;
                        hdrpDof.nearFocusStart.value = 0.5f;
                        hdrpDof.nearFocusStart.overrideState = true;
                        hdrpDof.nearFocusEnd.value = 1.2f;
                        hdrpDof.nearFocusEnd.overrideState = true;
                        hdrpDof.farFocusStart.value = 3.8f;
                        hdrpDof.farFocusStart.overrideState = true;
                        hdrpDof.farFocusEnd.value = 5f;
                        hdrpDof.farFocusEnd.overrideState = true;
                    }
                    else
                    {
                        urpDof = profile.Add<UnityEngine.Rendering.Universal.DepthOfField>(true);
                        urpDof.mode.value = DepthOfFieldMode.Gaussian;
                        urpDof.mode.overrideState = true;
                        urpDof.gaussianStart.value = 2.5f;
                        urpDof.gaussianStart.overrideState = true;
                        urpDof.gaussianEnd.value = 3.5f;
                        urpDof.gaussianEnd.overrideState = true;
                        urpDof.gaussianMaxRadius.value = 1f;
                        urpDof.gaussianMaxRadius.overrideState = true;
                    }
                }
                volume.sharedProfile = profile;
                owned.Add(item);
                owned.Add(profile);
            }

            void SnapshotExternalObjects()
            {
                foreach (Camera item in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
                {
                    external.Add(item); externalStates.Add(item.enabled); item.enabled = false;
                }
                foreach (Light item in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
                {
                    external.Add(item); externalStates.Add(item.enabled); item.enabled = false;
                }
            }

            public void Capture(bool effectEnabled)
            {
                if (!hdrp && urpCameraData != null)
                {
                    urpCameraData.antialiasing = effectEnabled ? urpAaMode : AntialiasingMode.None;
                    camera.allowMSAA = effectEnabled && urpMsaa;
                    if (urp != null) urp.msaaSampleCount = effectEnabled && urpMsaa ? requestedMsaaSamples : 1;
                }
                if (!hdrp && urpDof != null) urpDof.active = effectEnabled;
                if (hdrp && hdrpDof != null) hdrpDof.active = effectEnabled;
                quilt.RenderQuilt();
                Assert.IsFalse(camera.enabled, "source camera must remain disabled after quilt render");
                Assert.AreEqual(50f, camera.fieldOfView, 0.0001f, "field of view was not restored");
                Assert.AreEqual(0.1f, camera.nearClipPlane, 0.0001f, "near clip was not restored");
                Assert.AreEqual(100f, camera.farClipPlane, 0.0001f, "far clip was not restored");
                if (!hdrp && urpCameraData != null)
                {
                    Assert.AreEqual(effectEnabled ? urpAaMode : AntialiasingMode.None, urpCameraData.antialiasing, "AA mode changed unexpectedly");
                    Assert.AreEqual(effectEnabled && urpMsaa, camera.allowMSAA, "MSAA state changed unexpectedly");
                    Assert.AreEqual(effectEnabled && urpMsaa ? expectedMsaaSamples : 1, quilt.ViewAntiAliasingSamples, "view RT MSAA sample count was not applied");
                    bool formatResolve = camera.allowHDR && urp != null && urp.supportsHDR;
                    bool msaaResolve = effectEnabled && urpMsaa && expectedMsaaSamples > 1;
                    Assert.AreEqual(formatResolve || msaaResolve ? 1 : 0, quilt.ResolveAntiAliasingSamples, "resolve RT lifecycle was not applied");
                    Debug.Log("MSAA requested=" + requestedMsaaSamples + " expected=" + (effectEnabled && urpMsaa ? expectedMsaaSamples : 1) + " actual=" + quilt.ViewAntiAliasingSamples + " allowMSAA=" + camera.allowMSAA + " colorSpace=" + QualitySettings.activeColorSpace);
                }
                Texture2D image = Read(quilt.QuiltTexture);
                if (effectEnabled) after = image; else before = image;
            }

            int SupportedMsaaSamples(int requested, RenderTextureFormat format, bool sRgb)
            {
                RenderTextureDescriptor descriptor = new RenderTextureDescriptor(TileWidth, TileHeight, format, 24)
                {
                    msaaSamples = requested,
                    sRGB = sRgb
                };
                int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
                return supported == 2 || supported == 4 || supported == 8 ? supported : 1;
            }

            public void AssertChanged()
            {
                Color[] a = before.GetPixels();
                Color[] b = after.GetPixels();
                int[] counts = new int[ViewCount];
                QuiltLayout layout = quilt.settings.GetLayout();
                for (int view = 0; view < ViewCount; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    int changed = 0;
                    int visible = 0;
                    for (int y = tile.y; y < tile.yMax; ++y)
                    for (int x = tile.x; x < tile.xMax; ++x)
                    {
                        int i = y * before.width + x;
                        if (a[i].r + a[i].g + a[i].b > 0.06f) visible++;
                        if (Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 0.01f) changed++;
                    }
                    counts[view] = changed;
                    Assert.Greater(visible, 20, "control image is blank in tile " + view);
                    if (!urpMsaa || expectedMsaaSamples > 1)
                        Assert.Greater(changed, 20, "effect did not change tile " + view);
                    if (urpMsaa)
                    {
                        Color control = a[(tile.y + tile.height / 2) * before.width + tile.x + tile.width / 2];
                        Color antialiased = b[(tile.y + tile.height / 2) * after.width + tile.x + tile.width / 2];
                        Assert.Greater(control.r + control.g + control.b, 0.06f, "MSAA control point is background in tile " + view);
                        Assert.Greater(antialiased.r + antialiased.g + antialiased.b, 0.06f, "MSAA result point is background in tile " + view);
                        Assert.Less(Mathf.Abs(control.r - antialiased.r) + Mathf.Abs(control.g - antialiased.g) + Mathf.Abs(control.b - antialiased.b), 6f / 255f, "MSAA changed interior color in tile " + view);
                        if (expectedMsaaSamples == 1)
                            Assert.LessOrEqual(changed, 20, "unsupported MSAA request changed tile " + view);
                    }
                }
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "srp-additional-validation"));
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, name + "-before.png"), before.EncodeToPNG());
                File.WriteAllBytes(Path.Combine(directory, name + "-after.png"), after.EncodeToPNG());
                File.WriteAllText(Path.Combine(directory, name + "-diagnostic.txt"), "views=" + ViewCount + " tileChanges=" + string.Join(",", counts) + " viewMsaa=" + quilt.ViewAntiAliasingSamples + " resolveMsaa=" + quilt.ResolveAntiAliasingSamples + "\n");
            }

            static Texture2D Read(RenderTexture source)
            {
                RenderTexture previous = RenderTexture.active;
                try
                {
                    RenderTexture.active = source;
                    Texture2D result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                    result.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                    result.Apply();
                    return result;
                }
                finally { RenderTexture.active = previous; }
            }

            public void Dispose()
            {
                for (int i = 0; i < external.Count; ++i) if (external[i] != null) external[i].enabled = externalStates[i];
                RenderSettings.sun = oldRenderSettings.sun;
                RenderSettings.ambientLight = oldRenderSettings.ambient;
                GraphicsSettings.defaultRenderPipeline = oldDefault;
                QualitySettings.renderPipeline = oldQuality;
                RenderTexture.active = oldActive;
                if (camera != null) camera.allowMSAA = oldAllowMsaa;
                if (before != null) UnityEngine.Object.DestroyImmediate(before);
                if (after != null) UnityEngine.Object.DestroyImmediate(after);
                for (int i = owned.Count - 1; i >= 0; --i) if (owned[i] != null) UnityEngine.Object.DestroyImmediate(owned[i]);
                if (oldScene.IsValid() && oldScene.isLoaded) SceneManager.SetActiveScene(oldScene);
                if (fixtureScene.IsValid() && fixtureScene.isLoaded) SceneManager.UnloadSceneAsync(fixtureScene);
            }
        }
    }
}
