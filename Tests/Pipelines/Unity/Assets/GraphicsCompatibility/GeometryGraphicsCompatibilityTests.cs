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

namespace LookingGlass.GraphicsCompatibility
{
    public sealed class GeometryGraphicsCompatibilityTests
    {
        [UnityTest] public IEnumerator UrpSkinned() => Run(false, "skinned", f => f.AddSkinned());
        [UnityTest] public IEnumerator UrpLod() => Run(false, "lod", f => f.AddLod());
        [UnityTest] public IEnumerator UrpTerrain() => Run(false, "terrain", f => f.AddTerrain());
        [UnityTest] public IEnumerator UrpShadow() => Run(false, "shadow", f => f.AddShadow());
        [UnityTest] public IEnumerator UrpLight() => Run(false, "light", f => f.AddLight());
        [UnityTest] public IEnumerator HdrpSkinned() => Run(true, "skinned", f => f.AddSkinned());
        [UnityTest] public IEnumerator HdrpLod() => Run(true, "lod", f => f.AddLod());
        [UnityTest] public IEnumerator HdrpTerrain() => Run(true, "terrain", f => f.AddTerrain());
        [UnityTest] public IEnumerator HdrpShadow() => Run(true, "shadow", f => f.AddShadow());
        [UnityTest] public IEnumerator HdrpLight() => Run(true, "light", f => f.AddLight());

        IEnumerator Run(bool hdrp, string name, Action<Fixture> add)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(hdrp, name);
                fixture.Setup();
                add(fixture);
                fixture.RefreshScope();
                fixture.Toggle(false);
                yield return null;
                yield return null;
                fixture.Capture(false);
                fixture.Toggle(true);
                yield return null;
                yield return null;
                fixture.Capture(true);
                fixture.AssertChanged();
            }
            finally
            {
                if (fixture != null)
                    fixture.Dispose();
            }
        }

        sealed class Fixture
        {
            readonly bool hdrp; readonly string name; readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            GameObject root, cameraObject, feature; Camera camera; QuiltRenderer quilt; Texture2D before, after; Action<bool> toggle = enabled => { }; RenderPipelineAsset oldDefault, oldQuality, pipeline; UniversalRendererData urpRendererData; GraphicsFixtureScope scope; bool featureEnabled;
            public Fixture(bool useHdrp, string featureName) { hdrp = useHdrp; name = featureName; }
            public void Setup()
            {
                oldDefault = GraphicsSettings.defaultRenderPipeline; oldQuality = QualitySettings.renderPipeline;
                if (hdrp) pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>(); else { urpRendererData = ScriptableObject.CreateInstance<UniversalRendererData>(); pipeline = UniversalRenderPipelineAsset.Create(urpRendererData); }
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                root = new GameObject("GeometryCompatibility_" + (hdrp ? "HDRP_" : "URP_") + name); owned.Add(root); cameraObject = new GameObject("QuiltCamera"); cameraObject.transform.SetParent(root.transform, false); camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; camera.fieldOfView = 45f;
                if (hdrp) { var data = cameraObject.AddComponent<HDAdditionalCameraData>(); data.volumeLayerMask = 1 << GraphicsFixtureScope.Layer; data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; } else cameraObject.AddComponent<UniversalAdditionalCameraData>();
                quilt = cameraObject.AddComponent<QuiltRenderer>(); quilt.sourceCamera = camera; if (hdrp) quilt.GuardBehaviour = cameraObject.AddComponent<HdrpQuiltCamera>(); else quilt.GuardBehaviour = cameraObject.AddComponent<UrpQuiltCamera>(); quilt.settings.quiltWidth = 512; quilt.settings.quiltHeight = 192; quilt.settings.columns = 4; quilt.settings.rows = 2; quilt.settings.views = 8; quilt.settings.viewAspect = 128f / 96f; quilt.settings.cameraDistance = 4f; quilt.settings.fieldOfView = 45f; quilt.settings.viewCone = 25f;
                AddMaterialCube("Base", new Vector3(0f, 0f, 3f), Color.white); AddDirectionalLight(); if (hdrp) AddHdrpVolume(); else ConfigureUrpShadows();
                scope = GraphicsFixtureScope.Create(root);
                scope.Apply(camera);
            }
            void AddHdrpVolume()
            {
                var volumeObject = new GameObject("HDRPFixedExposure");
                volumeObject.transform.SetParent(root.transform, false);
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                volume.priority = 10f;
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                var exposure = profile.Add<Exposure>(true);
                exposure.mode.value = ExposureMode.Fixed;
                exposure.mode.overrideState = true;
                exposure.fixedExposure.value = 4f;
                exposure.fixedExposure.overrideState = true;
                var motionBlur = profile.Add<UnityEngine.Rendering.HighDefinition.MotionBlur>(true);
                motionBlur.active = true;
                motionBlur.intensity.value = 0f;
                motionBlur.intensity.overrideState = true;
                var reflection = profile.Add<ScreenSpaceReflection>(true);
                reflection.enabled.value = false;
                reflection.enabled.overrideState = true;
                volume.sharedProfile = profile;
                owned.Add(volumeObject);
                owned.Add(profile);
            }

            void ConfigureUrpShadows()
            {
                var asset = (UniversalRenderPipelineAsset)pipeline;
                asset.shadowDistance = 20f;
                var serialized = new SerializedObject(asset);
                var shadows = serialized.FindProperty("m_MainLightShadowsSupported");
                if (shadows == null)
                    Assert.Fail("URP main light shadow setting is unavailable");
                shadows.boolValue = true;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            void AddDirectionalLight()
            {
                var go = new GameObject("Directional");
                go.transform.SetParent(root.transform, false);
                var light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = hdrp ? 10f : 2f;
                light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                if (hdrp)
                    go.AddComponent<HDAdditionalLightData>();
                owned.Add(go);
            }

            Shader LitShader()
            {
                var shader = Shader.Find(hdrp ? "HDRP/Lit" : "Universal Render Pipeline/Lit");
                if (shader == null)
                    Assert.Fail("Lit shader unavailable");
                return shader;
            }

            Material Material(Color color)
            {
                var material = new Material(LitShader());
                material.color = color;
                owned.Add(material);
                return material;
            }

            GameObject AddMaterialCube(string objectName, Vector3 position, Color color)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = objectName;
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = position;
                go.GetComponent<Renderer>().sharedMaterial = Material(color);
                owned.Add(go);
                return go;
            }

            public void Toggle(bool enabled)
            {
                featureEnabled = enabled;
                toggle(enabled);
            }

            public void RefreshScope()
            {
                scope.Apply(camera);
            }
            public void Capture(bool enabled)
            {
                quilt.RenderQuilt();
                var old = RenderTexture.active;
                try
                {
                    RenderTexture.active = quilt.QuiltTexture;
                    var image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
                    image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
                    image.Apply();
                    if (enabled)
                        after = image;
                    else
                        before = image;
                    var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility"));
                    Directory.CreateDirectory(directory);
                    var fileName = (hdrp ? "hdrp-" : "urp-") + "geometry-" + name + (enabled ? "-after" : "-before") + ".png";
                    File.WriteAllBytes(Path.Combine(directory, fileName), image.EncodeToPNG());
                }
                finally
                {
                    RenderTexture.active = old;
                }
            }
            public void AssertChanged()
            {
                Assert.IsNotNull(before);
                Assert.IsNotNull(after);
                var a = before.GetPixels();
                var b = after.GetPixels();
                var path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility", (hdrp ? "hdrp-" : "urp-") + "geometry-" + name + "-diagnostic.txt"));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new StreamWriter(path, false))
                {
                    var counts = new int[8];
                    for (int view = 0; view < 8; ++view)
                    {
                        var tile = quilt.settings.GetLayout().GetTile(view);
                        for (int y = tile.y; y < tile.yMax; ++y)
                        {
                            for (int x = tile.x; x < tile.xMax; ++x)
                            {
                                int index = y * before.width + x;
                                if (Mathf.Abs(a[index].r - b[index].r) + Mathf.Abs(a[index].g - b[index].g) + Mathf.Abs(a[index].b - b[index].b) > 0.01f)
                                    counts[view]++;
                            }
                        }
                        writer.WriteLine("tile=" + view + " changed=" + counts[view]);
                    }
                    for (int view = 0; view < counts.Length; ++view)
                        Assert.Greater(counts[view], 20, "geometry feature did not change tile " + view);
                }
            }
            public void Dispose()
            {
                RenderTexture.active = null;
                if (before != null)
                    UnityEngine.Object.DestroyImmediate(before);
                if (after != null)
                    UnityEngine.Object.DestroyImmediate(after);
                foreach (var item in owned)
                {
                    if (item != null)
                        UnityEngine.Object.DestroyImmediate(item);
                }
                scope?.Dispose();
                GraphicsSettings.defaultRenderPipeline = oldDefault;
                QualitySettings.renderPipeline = oldQuality;
                if (pipeline != null)
                    UnityEngine.Object.DestroyImmediate(pipeline);
                if (urpRendererData != null)
                    UnityEngine.Object.DestroyImmediate(urpRendererData);
            }
            public void AddSkinned() {
                feature = new GameObject("SkinnedFeature");
                feature.transform.SetParent(root.transform, false);
                feature.transform.localPosition = new Vector3(0f, 0f, 2.5f);
                var renderer = feature.AddComponent<SkinnedMeshRenderer>();
                renderer.updateWhenOffscreen = true;
                var mesh = new Mesh();
                mesh.vertices = new[] { new Vector3(-1, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
                mesh.triangles = new[] { 0, 1, 2, 0, 3, 1, 0, 2, 3, 1, 3, 2 };
                var bone = new GameObject("Bone").transform;
                bone.SetParent(feature.transform, false);
                var bind = new Matrix4x4[1];
                bind[0] = bone.worldToLocalMatrix * feature.transform.localToWorldMatrix;
                var weights = new BoneWeight[4];
                for (int i = 0; i < weights.Length; ++i)
                {
                    weights[i].boneIndex0 = 0;
                    weights[i].weight0 = 1f;
                }
                mesh.bindposes = bind;
                mesh.boneWeights = weights;
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                renderer.rootBone = bone;
                renderer.bones = new[] { bone };
                renderer.sharedMesh = mesh;
                renderer.localBounds = mesh.bounds;
                renderer.sharedMaterial = Material(Color.magenta);
                owned.Add(feature);
                owned.Add(mesh);
                toggle = enabled =>
                {
                    bone.localPosition = enabled ? new Vector3(0.4f, 0.2f, 0f) : Vector3.zero;
                    renderer.enabled = true;
                };
            }
            public void AddLod() {
                feature = new GameObject("LODFeature");
                feature.transform.SetParent(root.transform, false);
                var lod = feature.AddComponent<LODGroup>();
                var a = AddMaterialCube("LOD0", new Vector3(0.5f, 0f, 2.5f), Color.red);
                var b = AddMaterialCube("LOD1", new Vector3(0.5f, 0f, 2.5f), Color.green);
                a.transform.SetParent(feature.transform, true);
                b.transform.SetParent(feature.transform, true);
                lod.SetLODs(new[] { new LOD(0.5f, new[] { a.GetComponent<Renderer>() }), new LOD(0.01f, new[] { b.GetComponent<Renderer>() }) });
                lod.RecalculateBounds();
                toggle = enabled => lod.ForceLOD(enabled ? 1 : 0);
            }
            public void AddTerrain() {
                feature = new GameObject("TerrainFeature");
                feature.transform.SetParent(root.transform, false);
                feature.transform.localPosition = new Vector3(-3.5f, -0.2f, 1.5f);
                var data = new TerrainData();
                data.heightmapResolution = 33;
                data.size = new Vector3(7f, 1f, 7f);
                float[,] heights = new float[33, 33];
                for (int y = 0; y < 33; ++y)
                {
                    for (int x = 0; x < 33; ++x)
                        heights[y, x] = 0.03f * Mathf.Sin(x * 0.3f) * Mathf.Sin(y * 0.3f) + (x > 16 ? 0.05f : 0f);
                }
                data.SetHeights(0, 0, heights);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                texture.SetPixels(new[] { Color.gray, Color.gray, Color.gray, Color.gray });
                texture.Apply();
                var layer = new TerrainLayer();
                layer.diffuseTexture = texture;
                layer.diffuseRemapMax = Color.white;
                data.terrainLayers = new[] { layer };
                data.alphamapResolution = 33;
                float[,,] alpha = new float[33, 33, 1];
                for (int y = 0; y < 33; ++y)
                {
                    for (int x = 0; x < 33; ++x)
                        alpha[y, x, 0] = 1f;
                }
                data.SetAlphamaps(0, 0, alpha);
                var terrain = feature.AddComponent<Terrain>();
                terrain.terrainData = data;
                Shader terrainShader = Shader.Find(hdrp ? "HDRP/TerrainLit" : "Universal Render Pipeline/Terrain/Lit");
                if (terrainShader == null) Assert.Fail("Terrain shader is unavailable");
                terrain.materialTemplate = new Material(terrainShader);
                owned.Add(feature);
                owned.Add(data);
                owned.Add(texture);
                owned.Add(layer);
                owned.Add(terrain.materialTemplate);
                toggle = enabled => feature.SetActive(enabled);
            }
            public void AddShadow() {
                feature = AddMaterialCube("ShadowCaster", new Vector3(0f, 1f, 2.5f), Color.white);
                var receiver = AddMaterialCube("ShadowReceiver", new Vector3(0f, -0.5f, 3f), Color.gray);
                receiver.transform.localScale = new Vector3(4f, 0.1f, 4f);
                var caster = feature.GetComponent<Renderer>();
                var light = root.GetComponentInChildren<Light>();
                toggle = enabled => { light.shadows = LightShadows.Soft;
                caster.shadowCastingMode = enabled ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
                };
            }
            public void AddLight() {
                feature = AddMaterialCube("LightFeature", new Vector3(0f, 0f, 2.5f), Color.white);
                var light = root.GetComponentInChildren<Light>();
                toggle = enabled => light.intensity = enabled ? (hdrp ? 10f : 2f) : 0f;
            }
        }
    }
}
#endif

