#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.TestTools;
using LookingGlass.Community;
using LookingGlass.Community.URP;
using LookingGlass.Community.HDRP;
using URPColorAdjustments = UnityEngine.Rendering.Universal.ColorAdjustments;
using URPTonemapping = UnityEngine.Rendering.Universal.Tonemapping;
using HDRPColorAdjustments = UnityEngine.Rendering.HighDefinition.ColorAdjustments;
using HDRPTonemapping = UnityEngine.Rendering.HighDefinition.Tonemapping;
using HDRPTonemappingMode = UnityEngine.Rendering.HighDefinition.TonemappingMode;
using URPTonemappingMode = UnityEngine.Rendering.Universal.TonemappingMode;

namespace LookingGlass.GraphicsCompatibility
{
    public sealed class AdditionalGraphicsCompatibilityTests
    {
        [UnityTest] public IEnumerator URP_ColorAdjustments() => Run("urp-color-adjustments", false, Feature.ColorAdjustments);
        [UnityTest] public IEnumerator URP_Tonemapping() => Run("urp-tonemapping", false, Feature.Tonemapping);
        [UnityTest] public IEnumerator URP_LineRenderer() => Run("urp-line", false, Feature.Line);
        [UnityTest] public IEnumerator URP_TrailRenderer() => Run("urp-trail", false, Feature.Trail);
        [UnityTest] public IEnumerator URP_ReflectionProbe() => Run("urp-reflection", false, Feature.Reflection);
        [UnityTest] public IEnumerator URP_ShaderGraphLit() => Run("urp-shadergraph-lit", false, Feature.ShaderGraph);
        [UnityTest] public IEnumerator HDRP_ColorAdjustments() => Run("hdrp-color-adjustments", true, Feature.ColorAdjustments);
        [UnityTest] public IEnumerator HDRP_Tonemapping() => Run("hdrp-tonemapping", true, Feature.Tonemapping);
        [UnityTest] public IEnumerator HDRP_LineRenderer() => Run("hdrp-line", true, Feature.Line);
        [UnityTest] public IEnumerator HDRP_TrailRenderer() => Run("hdrp-trail", true, Feature.Trail);
        [UnityTest] public IEnumerator HDRP_ReflectionProbe() => Run("hdrp-reflection", true, Feature.Reflection);
        [UnityTest] public IEnumerator HDRP_ShaderGraphLit() => Run("hdrp-shadergraph-lit", true, Feature.ShaderGraph);

        enum Feature { ColorAdjustments, Tonemapping, Line, Trail, Reflection, ShaderGraph }

        IEnumerator Run(string name, bool hdrp, Feature feature)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(name, hdrp, feature);
                fixture.Setup();
                yield return null;
                yield return null;
                fixture.Toggle(false);
                yield return null;
                yield return null;
                yield return null;
                fixture.Capture("before");
                fixture.Toggle(true);
                yield return null;
                yield return null;
                yield return null;
                fixture.Capture("after");
                if (feature == Feature.Trail) fixture.AssertTrailControlChanged();
                fixture.AssertChanged();
            }
            finally
            {
                fixture?.Dispose();
            }
        }

        sealed class Fixture
        {
            const int TileWidth = 128;
            const int TileHeight = 96;
            readonly string name;
            readonly bool hdrp;
            readonly Feature feature;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            GameObject root;
            GameObject featureObject;
            Camera camera;
            QuiltRenderer quilt;
            Texture2D before;
            Texture2D after;
            GraphicsFixtureScope scope;
            RenderPipelineAsset oldDefault;
            RenderPipelineAsset oldQuality;
            UniversalRenderPipelineAsset urpAsset;
            UniversalRendererData urpRenderer;
            HDRenderPipelineAsset hdrpAsset;
            Volume featureVolume;
            Volume commonVolume;
            ReflectionProbe probe;
            Material featureMaterial;
            Material baseMaterial;
            Renderer featureRenderer;
            TrailRenderer trailRenderer;
            Camera trailControlCamera;
            RenderTexture trailControlTarget;
            int trailControlPixelsBefore;
            int trailControlPixelsAfter;
            int trailControlChanged;
            Texture2D trailControlBefore;
            Texture2D trailControlAfter;
            int trailCountBefore;
            int trailCountAfter;
            readonly Vector3[] trailPositions =
            {
                new Vector3(-1.2f, -0.6f, 2.6f),
                new Vector3(-0.4f, 0.6f, 2.7f),
                new Vector3(0.4f, -0.6f, 2.8f),
                new Vector3(1.2f, 0.6f, 2.9f)
            };
            URPColorAdjustments urpColor;
            URPTonemapping urpTone;
            HDRPColorAdjustments hdrpColor;
            HDRPTonemapping hdrpTone;

            public Fixture(string value, bool useHdrp, Feature selected)
            {
                name = value;
                hdrp = useHdrp;
                feature = selected;
            }

            public void Setup()
            {
                oldDefault = GraphicsSettings.defaultRenderPipeline;
                oldQuality = QualitySettings.renderPipeline;
                if (hdrp) SetupHdrpPipeline(); else SetupUrpPipeline();
                root = new GameObject("AdditionalGraphicsCompatibility_" + name);
                owned.Add(root);
                GameObject cameraObject = new GameObject("AdditionalGraphicsCamera");
                cameraObject.transform.SetParent(root.transform, false);
                camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.fieldOfView = 50f;
                if (hdrp)
                {
                    var data = cameraObject.AddComponent<HDAdditionalCameraData>();
                    data.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                    data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    data.backgroundColorHDR = Color.black;
                    quilt = cameraObject.AddComponent<QuiltRenderer>();
                    quilt.GuardBehaviour = cameraObject.AddComponent<HdrpQuiltCamera>();
                }
                else
                {
                    var data = cameraObject.AddComponent<UniversalAdditionalCameraData>();
                    data.renderType = CameraRenderType.Base;
                    data.renderPostProcessing = true;
                    data.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                    quilt = cameraObject.AddComponent<QuiltRenderer>();
                    quilt.GuardBehaviour = cameraObject.AddComponent<UrpQuiltCamera>();
                }
                quilt.sourceCamera = camera;
                quilt.settings.quiltWidth = TileWidth * 4;
                quilt.settings.quiltHeight = TileHeight * 2;
                quilt.settings.columns = 4;
                quilt.settings.rows = 2;
                quilt.settings.views = 8;
                quilt.settings.viewAspect = (float)TileWidth / TileHeight;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;
                AddBaseLit();
                AddDirectionalLight();
                AddCommonVolume();
                if (feature == Feature.ColorAdjustments || feature == Feature.Tonemapping) AddVolumeFeature();
                if (feature == Feature.Line) AddLine();
                if (feature == Feature.Trail) AddTrail();
                if (feature == Feature.Trail) AddTrailControlCamera();
                if (feature == Feature.Reflection) AddReflection();
                if (feature == Feature.ShaderGraph) ConfigureShaderGraphRenderer();
                scope = GraphicsFixtureScope.Create(root);
                scope.Apply(camera);
            }

            void SetupUrpPipeline()
            {
                urpRenderer = ScriptableObject.CreateInstance<UniversalRendererData>();
                urpRenderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                if (urpRenderer.postProcessData == null) Assert.Fail("URP PostProcessData asset is unavailable");
                urpAsset = UniversalRenderPipelineAsset.Create(urpRenderer);
                urpAsset.supportsHDR = true;
                GraphicsSettings.defaultRenderPipeline = urpAsset;
                QualitySettings.renderPipeline = urpAsset;
                owned.Add(urpRenderer);
                owned.Add(urpAsset);
            }

            void SetupHdrpPipeline()
            {
                hdrpAsset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                GraphicsSettings.defaultRenderPipeline = hdrpAsset;
                QualitySettings.renderPipeline = hdrpAsset;
                owned.Add(hdrpAsset);
            }

            void AddBaseLit()
            {
                featureObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                featureObject.name = "AdditionalBaseLit";
                featureObject.transform.SetParent(root.transform, false);
                featureObject.transform.localPosition = new Vector3(0f, 0f, 3f);
                string shaderPath = hdrp
                    ? "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/ShaderGraph/HDRP Lit.shadergraph"
                    : "Packages/com.unity.render-pipelines.universal/Shaders/AutodeskInteractive/AutodeskInteractive.shadergraph";
                Shader shader = feature == Feature.ShaderGraph ? AssetDatabase.LoadAssetAtPath<Shader>(shaderPath) : Shader.Find(hdrp ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
                if (shader == null) Assert.Fail("ShaderGraph asset is unavailable: " + shaderPath);
                Material material = new Material(shader) { color = new Color(0.35f, 0.55f, 0.75f, 1f) };
                if (feature == Feature.Tonemapping)
                {
                    material.EnableKeyword("_EMISSION");
                    if (hdrp) material.SetColor("_EmissiveColor", new Color(2f, 0.7f, 0.2f, 1f));
                    else material.SetColor("_EmissionColor", new Color(2f, 0.7f, 0.2f, 1f));
                }
                baseMaterial = material;
                featureObject.GetComponent<Renderer>().sharedMaterial = material;
                owned.Add(featureObject);
                owned.Add(material);
            }

            void ConfigureShaderGraphRenderer()
            {
                featureRenderer = featureObject.GetComponent<Renderer>();
                featureRenderer.enabled = false;
            }

            void AddDirectionalLight()
            {
                GameObject lightObject = new GameObject("AdditionalDirectional");
                lightObject.transform.SetParent(root.transform, false);
                Light light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = hdrp ? 10f : 1.2f;
                lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                if (hdrp) lightObject.AddComponent<HDAdditionalLightData>();
                owned.Add(lightObject);
            }

            void AddCommonVolume()
            {
                GameObject volumeObject = new GameObject("AdditionalCommonVolume");
                volumeObject.transform.SetParent(root.transform, false);
                commonVolume = volumeObject.AddComponent<Volume>();
                commonVolume.isGlobal = true;
                commonVolume.priority = 10f;
                VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
                if (hdrp)
                {
                    var exposure = profile.Add<Exposure>(true);
                    exposure.mode.value = ExposureMode.Fixed;
                    exposure.mode.overrideState = true;
                    exposure.fixedExposure.value = 4f;
                    exposure.fixedExposure.overrideState = true;
                    var motion = profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
                    motion.active = true;
                    motion.intensity.value = 0f;
                    motion.intensity.overrideState = true;
                    var reflection = profile.Add<ScreenSpaceReflection>(true);
                    reflection.enabled.value = false;
                    reflection.enabled.overrideState = true;
                    var gi = profile.Add<GlobalIllumination>(true);
                    gi.enable.value = false;
                    gi.enable.overrideState = true;
                    var fog = profile.Add<Fog>(true);
                    fog.enabled.value = false;
                    fog.enabled.overrideState = true;
                    var cloud = profile.Add<VolumetricClouds>(true);
                    cloud.active = false;
                }
                else
                {
                    var motion = profile.Add<UnityEngine.Rendering.Universal.MotionBlur>(true);
                    motion.active = false;
                }
                commonVolume.sharedProfile = profile;
                owned.Add(volumeObject);
                owned.Add(profile);
            }

            void AddVolumeFeature()
            {
                GameObject volumeObject = new GameObject("AdditionalFeatureVolume");
                volumeObject.transform.SetParent(root.transform, false);
                featureVolume = volumeObject.AddComponent<Volume>();
                featureVolume.isGlobal = true;
                featureVolume.priority = 20f;
                VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>();
                if (hdrp)
                {
                    hdrpColor = profile.Add<HDRPColorAdjustments>(true);
                    hdrpColor.contrast.overrideState = true;
                    hdrpColor.saturation.overrideState = true;
                    hdrpTone = profile.Add<HDRPTonemapping>(true);
                    hdrpTone.mode.overrideState = true;
                }
                else
                {
                    urpColor = profile.Add<URPColorAdjustments>(true);
                    urpColor.contrast.overrideState = true;
                    urpColor.saturation.overrideState = true;
                    urpTone = profile.Add<URPTonemapping>(true);
                    urpTone.mode.overrideState = true;
                }
                featureVolume.sharedProfile = profile;
                owned.Add(volumeObject);
                owned.Add(profile);
            }

            void AddLine()
            {
                featureObject = new GameObject("AdditionalLine");
                featureObject.transform.SetParent(root.transform, false);
                LineRenderer line = featureObject.AddComponent<LineRenderer>();
                line.positionCount = 4;
                line.SetPositions(new[] { new Vector3(-1.2f, -0.6f, 2.4f), new Vector3(-0.4f, 0.6f, 2.6f), new Vector3(0.4f, -0.6f, 2.8f), new Vector3(1.2f, 0.6f, 3f) });
                line.widthMultiplier = 0.12f;
                line.startColor = Color.magenta;
                line.endColor = Color.cyan;
                featureRenderer = line;
                featureMaterial = CreateUnlitMaterial();
                line.sharedMaterial = featureMaterial;
                owned.Add(featureObject);
                owned.Add(featureMaterial);
            }

            void AddTrail()
            {
                featureObject = new GameObject("AdditionalTrail");
                featureObject.transform.SetParent(root.transform, false);
                TrailRenderer trail = featureObject.AddComponent<TrailRenderer>();
                trail.time = 100f;
                trail.minVertexDistance = 0.01f;
                trail.emitting = false;
                trail.widthCurve = AnimationCurve.Constant(0f, 1f, 1f);
                trail.widthMultiplier = 0.16f;
                trail.startColor = Color.yellow;
                trail.endColor = Color.red;
                trail.AddPositions(trailPositions);
                trailRenderer = trail;
                featureRenderer = trail;
                featureMaterial = CreateUnlitMaterial();
                if (hdrp) featureMaterial.SetColor("_UnlitColor", Color.magenta);
                else featureMaterial.SetColor("_BaseColor", Color.magenta);
                trail.sharedMaterial = featureMaterial;
                owned.Add(featureObject);
                owned.Add(featureMaterial);
            }

            void AddTrailControlCamera()
            {
                GameObject controlObject = new GameObject("AdditionalTrailControlCamera");
                controlObject.transform.SetParent(root.transform, false);
                trailControlCamera = controlObject.AddComponent<Camera>();
                trailControlCamera.enabled = true;
                trailControlCamera.clearFlags = CameraClearFlags.SolidColor;
                trailControlCamera.backgroundColor = Color.black;
                trailControlCamera.fieldOfView = 50f;
                trailControlCamera.cullingMask = 1 << GraphicsFixtureScope.Layer;
                if (hdrp)
                {
                    var data = controlObject.AddComponent<HDAdditionalCameraData>();
                    data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    data.backgroundColorHDR = Color.black;
                    data.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                }
                else
                {
                    var data = controlObject.AddComponent<UniversalAdditionalCameraData>();
                    data.renderType = CameraRenderType.Base;
                    data.renderPostProcessing = true;
                    data.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                }
                trailControlTarget = new RenderTexture(TileWidth, TileHeight, 24, RenderTextureFormat.ARGB32);
                trailControlTarget.Create();
                trailControlCamera.targetTexture = trailControlTarget;
                owned.Add(controlObject);
                owned.Add(trailControlTarget);
            }

            Material CreateUnlitMaterial()
            {
                string shaderName = hdrp ? "HDRP/Unlit" : "Universal Render Pipeline/Unlit";
                Shader shader = Shader.Find(shaderName);
                if (shader == null) Assert.Fail(shaderName + " shader is unavailable");
                return new Material(shader) { color = Color.magenta };
            }

            void AddReflection()
            {
                featureObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                featureObject.name = "AdditionalReflectionSphere";
                featureObject.transform.SetParent(root.transform, false);
                featureObject.transform.localPosition = new Vector3(0f, 0f, 2.5f);
                string shaderName = hdrp ? "HDRP/Lit" : "Universal Render Pipeline/Lit";
                Shader shader = Shader.Find(shaderName);
                if (shader == null) Assert.Fail(shaderName + " shader is unavailable");
                featureMaterial = new Material(shader) { color = Color.white };
                featureMaterial.SetFloat("_Metallic", 1f);
                featureMaterial.SetFloat("_Smoothness", 1f);
                featureObject.GetComponent<Renderer>().sharedMaterial = featureMaterial;
                probe = root.AddComponent<ReflectionProbe>();
                probe.mode = ReflectionProbeMode.Custom;
                probe.size = new Vector3(8f, 8f, 8f);
                probe.center = new Vector3(0f, 0f, 2.5f);
                Cubemap cube = new Cubemap(16, TextureFormat.RGBA32, false);
                Color[] cubePixels = new Color[16 * 16];
                for (int index = 0; index < cubePixels.Length; ++index) cubePixels[index] = new Color(0.1f, 0.4f, 0.8f, 1f);
                for (int face = 0; face < 6; ++face) cube.SetPixels(cubePixels, (CubemapFace)face);
                cube.Apply();
                probe.customBakedTexture = cube;
                owned.Add(featureObject);
                owned.Add(featureMaterial);
                owned.Add(cube);
            }

            public void Toggle(bool enabled)
            {
                if (feature == Feature.ColorAdjustments)
                {
                    if (hdrpColor != null) hdrpColor.contrast.value = enabled ? 25f : 0f;
                    if (urpColor != null) urpColor.contrast.value = enabled ? 25f : 0f;
                    if (hdrpColor != null) hdrpColor.saturation.value = enabled ? -100f : 0f;
                    if (urpColor != null) urpColor.saturation.value = enabled ? -100f : 0f;
                }
                if (feature == Feature.Tonemapping)
                {
                    if (hdrpTone != null) hdrpTone.mode.value = enabled ? HDRPTonemappingMode.Neutral : HDRPTonemappingMode.None;
                    if (urpTone != null) urpTone.mode.value = enabled ? URPTonemappingMode.Neutral : URPTonemappingMode.None;
                }
                if (featureRenderer != null) featureRenderer.enabled = enabled;
                if (probe != null) probe.enabled = enabled;
                if (trailRenderer != null && enabled)
                {
                    trailRenderer.Clear();
                    trailRenderer.emitting = true;
                    trailRenderer.AddPositions(trailPositions);
                }
                if (trailRenderer != null && !enabled) trailRenderer.emitting = false;
            }

            public void Capture(string suffix)
            {
                if (trailControlCamera != null)
                {
                    int controlPixels = 0;
                    RenderTexture previousControl = RenderTexture.active;
                    Texture2D controlImage = null;
                    bool retained = false;
                    try
                    {
                        RenderTexture.active = trailControlTarget;
                        controlImage = new Texture2D(TileWidth, TileHeight, TextureFormat.RGBA32, false);
                        controlImage.ReadPixels(new Rect(0f, 0f, TileWidth, TileHeight), 0, 0);
                        controlImage.Apply();
                        foreach (Color pixel in controlImage.GetPixels()) if (pixel.maxColorComponent > 0.02f) ++controlPixels;
                        if (suffix == "before")
                        {
                            if (trailControlBefore != null) UnityEngine.Object.DestroyImmediate(trailControlBefore);
                            trailControlBefore = controlImage;
                        }
                        else
                        {
                            if (trailControlAfter != null) UnityEngine.Object.DestroyImmediate(trailControlAfter);
                            trailControlAfter = controlImage;
                        }
                        retained = true;
                        string controlDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility"));
                        Directory.CreateDirectory(controlDirectory);
                        File.WriteAllBytes(Path.Combine(controlDirectory, name + "-control-" + suffix + ".png"), controlImage.EncodeToPNG());
                    }
                    finally
                    {
                        RenderTexture.active = previousControl;
                        if (!retained && controlImage != null) UnityEngine.Object.DestroyImmediate(controlImage);
                    }
                    if (suffix == "before") trailControlPixelsBefore = controlPixels; else trailControlPixelsAfter = controlPixels;
                }
                quilt.RenderQuilt();
                if (trailRenderer != null)
                {
                    if (suffix == "before") trailCountBefore = trailRenderer.positionCount;
                    else trailCountAfter = trailRenderer.positionCount;
                }
                RenderTexture old = RenderTexture.active;
                Texture2D image = null;
                try
                {
                    RenderTexture.active = quilt.QuiltTexture;
                    image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
                    image.ReadPixels(new Rect(0f, 0f, image.width, image.height), 0, 0);
                    image.Apply();
                }
                finally
                {
                    RenderTexture.active = old;
                }
                if (suffix == "before") before = image; else after = image;
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility"));
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, name + "-" + suffix + ".png"), image.EncodeToPNG());
            }

            public void AssertChanged()
            {
                Assert.IsNotNull(before, "feature-off image was not captured");
                Assert.IsNotNull(after, "feature-on image was not captured");
                Color[] a = before.GetPixels();
                Color[] b = after.GetPixels();
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility"));
                Directory.CreateDirectory(directory);
                int[] changedCounts = new int[8];
                using (StreamWriter writer = new StreamWriter(Path.Combine(directory, name + "-diagnostic.txt"), false))
                {
                    if (trailRenderer != null) writer.WriteLine("trailPositionCountBefore=" + trailCountBefore + " trailPositionCountAfter=" + trailCountAfter);
                    if (trailControlCamera != null) writer.WriteLine("trailControlPixelsBefore=" + trailControlPixelsBefore + " trailControlPixelsAfter=" + trailControlPixelsAfter);
                    if (trailControlCamera != null) writer.WriteLine("trailControlChanged=" + trailControlChanged);
                    for (int view = 0; view < 8; ++view)
                    {
                        RectInt tile = quilt.settings.GetLayout().GetTile(view);
                        int changed = 0;
                        for (int y = tile.y; y < tile.yMax; ++y)
                        for (int x = tile.x; x < tile.xMax; ++x)
                        {
                            int index = y * before.width + x;
                            if (Mathf.Abs(a[index].r - b[index].r) + Mathf.Abs(a[index].g - b[index].g) + Mathf.Abs(a[index].b - b[index].b) > 0.01f) ++changed;
                        }
                        changedCounts[view] = changed;
                        writer.WriteLine("feature=" + feature + " view=" + view + " changed=" + changed);
                    }
                }
                for (int view = 0; view < changedCounts.Length; ++view)
                    Assert.Greater(changedCounts[view], 20, "feature toggle changed fewer than 20 pixels in view " + view);
            }

            public void AssertTrailControlChanged()
            {
                Assert.IsNotNull(trailControlBefore, "trail control before image was not captured");
                Assert.IsNotNull(trailControlAfter, "trail control after image was not captured");
                Color[] beforePixels = trailControlBefore.GetPixels();
                Color[] afterPixels = trailControlAfter.GetPixels();
                int changed = 0;
                for (int index = 0; index < beforePixels.Length; ++index)
                {
                    if (Mathf.Abs(beforePixels[index].r - afterPixels[index].r) + Mathf.Abs(beforePixels[index].g - afterPixels[index].g) + Mathf.Abs(beforePixels[index].b - afterPixels[index].b) > 0.01f) ++changed;
                }
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility"));
                Directory.CreateDirectory(directory);
                trailControlChanged = changed;
                Assert.Greater(changed, 20, "trail control camera did not show a TrailRenderer RGB change");
            }

            public void Dispose()
            {
                scope?.Dispose();
                GraphicsSettings.defaultRenderPipeline = oldDefault;
                QualitySettings.renderPipeline = oldQuality;
                RenderTexture.active = null;
                if (before != null) UnityEngine.Object.DestroyImmediate(before);
                if (after != null) UnityEngine.Object.DestroyImmediate(after);
                if (trailControlBefore != null) UnityEngine.Object.DestroyImmediate(trailControlBefore);
                if (trailControlAfter != null) UnityEngine.Object.DestroyImmediate(trailControlAfter);
                if (trailControlCamera != null) trailControlCamera.targetTexture = null;
                if (trailControlTarget != null) trailControlTarget.Release();
                foreach (UnityEngine.Object item in owned) if (item != null) UnityEngine.Object.DestroyImmediate(item);
            }
        }
    }
}
#endif
