#if UNITY_INCLUDE_TESTS
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.TestTools;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif
using LookingGlass.Community;
using LookingGlass.Community.URP;
using LookingGlass.Community.HDRP;
using ViewWeave.Samples.WindowsUrpLit;
using ViewWeave.Samples.WindowsHdrpLit;

namespace ViewWeave.SampleLifecycleValidation
{
    public sealed class SampleLifecycleTests
    {
        [UnityTest] public IEnumerator UrpManualGenerateTwice() => Run(false, false);
        [UnityTest] public IEnumerator HdrpManualGenerateTwice() => Run(true, false);
        [UnityTest] public IEnumerator UrpAutomaticGenerate() => Run(false, true);
        [UnityTest] public IEnumerator HdrpAutomaticGenerate() => Run(true, true);

        IEnumerator Run(bool hdrp, bool automatic)
        {
            var previousDefault = GraphicsSettings.defaultRenderPipeline;
            var previousQuality = QualitySettings.renderPipeline;
            RenderPipelineAsset pipeline = null;
            UniversalRendererData urpData = null;
            GameObject rigObject = null;
            MonoBehaviour generator;
            var disabledExternal = new List<Behaviour>();
            Light previousSun = null;
            try
            {
#if UNITY_EDITOR
                EnsureSampleMaterial(hdrp);
#else
                Assert.Fail("Sample lifecycle validation requires the Unity Editor");
#endif
                if (hdrp)
                    pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                else
                {
                    urpData = ScriptableObject.CreateInstance<UniversalRendererData>();
                    pipeline = UniversalRenderPipelineAsset.Create(urpData);
                }
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                rigObject = CreateRig(hdrp, out generator);
                CaptureExternalState(rigObject, disabledExternal, out previousSun);
                var rig = rigObject.GetComponent<ViewWeaveRig>();
                var camera = rigObject.GetComponent<Camera>();
                camera.fieldOfView = 61f;
                camera.nearClipPlane = 0.2f;
                camera.farClipPlane = 80f;
                rig.Renderer.settings.cameraDistance = 4.5f;
                if (hdrp)
                    rigObject.GetComponent<HDAdditionalCameraData>().volumeLayerMask = 1 << 29;
                var expectedFov = camera.fieldOfView;
                var expectedNear = camera.nearClipPlane;
                var expectedFar = camera.farClipPlane;
                var expectedDistance = rig.Renderer.settings.cameraDistance;
                var expectedVolumeMask = hdrp
                    ? (int)rigObject.GetComponent<HDAdditionalCameraData>().volumeLayerMask
                    : 0;
                SetAutomatic(generator, automatic);
                rigObject.SetActive(true);
                yield return null;
                yield return null;
                if (!automatic)
                    InvokeGenerate(generator);
                yield return null;
                AssertSingleGeneratedRoot(hdrp);
                rig.ApplyProfile(new QuiltQualityProfile(1232, 616, 4, 2, 8, 2f, 17f));
                var firstRoot = FindGeneratedRoot(hdrp);
                Assert.IsNotNull(firstRoot);
                var rootPosition = firstRoot.transform.position;
                rigObject.GetComponent<Camera>().transform.localPosition = new Vector3(0.3f, 0f, 0f);
                Assert.AreEqual(rootPosition, firstRoot.transform.position);
                if (!automatic)
                {
                    InvokeGenerate(generator);
                    yield return null;
                    Assert.IsTrue(firstRoot == null, "the first generated root was not destroyed");
                }
                else
                {
                    Assert.IsTrue(firstRoot != null, "automatic generation did not create content");
                }
                AssertSingleGeneratedRoot(hdrp);
                var secondRoot = FindGeneratedRoot(hdrp);
                var materials = GetMaterials(secondRoot);
                var profiles = GetProfiles(secondRoot);
                AssertProfilePreserved(rig.Renderer.settings);
                rigObject.SetActive(false);
                yield return null;
                Assert.IsNull(FindGeneratedRoot(hdrp));
                AssertProfilePreserved(rig.Renderer.settings);
                Assert.AreEqual(expectedFov, rig.GetComponent<Camera>().fieldOfView, 0.0001f);
                Assert.AreEqual(expectedNear, rig.GetComponent<Camera>().nearClipPlane, 0.0001f);
                Assert.AreEqual(expectedFar, rig.GetComponent<Camera>().farClipPlane, 0.0001f);
                Assert.AreEqual(expectedDistance, rig.Renderer.settings.cameraDistance, 0.0001f);
                if (hdrp)
                    Assert.AreEqual(expectedVolumeMask, (int)rig.GetComponent<HDAdditionalCameraData>().volumeLayerMask);
                foreach (var material in materials) Assert.IsTrue(material == null);
                foreach (var profile in profiles) Assert.IsTrue(profile == null);
            }
            finally
            {
                RestoreExternalState(disabledExternal, previousSun);
                if (rigObject != null)
                    Object.DestroyImmediate(rigObject);
                if (pipeline != null)
                    Object.DestroyImmediate(pipeline);
                if (urpData != null)
                    Object.DestroyImmediate(urpData);
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
            }
        }

        static GameObject CreateRig(bool hdrp, out MonoBehaviour generator)
        {
            var rigObject = new GameObject(hdrp ? "HDRP Sample Lifecycle Rig" : "URP Sample Lifecycle Rig");
            rigObject.SetActive(false);
            var camera = rigObject.AddComponent<Camera>();
            camera.enabled = false;
            var renderer = rigObject.AddComponent<QuiltRenderer>();
            var rig = rigObject.AddComponent<ViewWeaveRig>();
            MonoBehaviour guard;
            if (hdrp)
            {
                rigObject.AddComponent<HDAdditionalCameraData>();
                guard = rigObject.AddComponent<HdrpQuiltCamera>();
                generator = rigObject.AddComponent<BridgeHdrpLitSampleGenerator>();
            }
            else
            {
                rigObject.AddComponent<UniversalAdditionalCameraData>();
                guard = rigObject.AddComponent<UrpQuiltCamera>();
                generator = rigObject.AddComponent<BridgeUrpLitSampleGenerator>();
            }
            var output = rigObject.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            var session = rigObject.AddComponent<ViewWeaveBridgeSession>();
            session.startOnEnable = false;
            renderer.sourceCamera = camera;
            rig.SetGuard(guard);
            rig.SetOutput(output);
            Assert.IsTrue(rig.ConfigureComponents());
            return rigObject;
        }

        static void SetAutomatic(MonoBehaviour generator, bool value)
        {
#if UNITY_EDITOR
            var serialized = new UnityEditor.SerializedObject(generator);
            serialized.FindProperty("generateOnStart").boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
#else
            Assert.Fail("Sample lifecycle validation requires the Unity Editor");
#endif
        }

#if UNITY_EDITOR
        static void EnsureSampleMaterial(bool hdrp)
        {
            var installerName = hdrp
                ? "ViewWeaveHdrpLitMaterialInstaller t:MonoScript"
                : "ViewWeaveUrpLitMaterialInstaller t:MonoScript";
            var guids = AssetDatabase.FindAssets(installerName);
            string sampleRoot = null;
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid).Replace('\\', '/');
                if (!path.StartsWith("Assets/", System.StringComparison.Ordinal)) continue;
                sampleRoot = path.Substring(0, path.LastIndexOf("/Editor/", System.StringComparison.Ordinal));
                break;
            }
            Assert.IsNotNull(sampleRoot, "Imported sample installer was not found under Assets");
            if (hdrp)
                ViewWeaveHdrpLitMaterialInstaller.CreateMaterialResourceAt(sampleRoot);
            else
                ViewWeaveUrpLitMaterialInstaller.CreateMaterialResourceAt(sampleRoot);

            var assetPath = sampleRoot + "/Resources/ViewWeave/" + (hdrp ? "HdrpLit.mat" : "UrpLit.mat");
            var material = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
            Assert.IsNotNull(material, "Sample-owned material was not created: " + assetPath);
            var loaded = Resources.Load<Material>(hdrp ? "ViewWeave/HdrpLit" : "ViewWeave/UrpLit");
            Assert.IsNotNull(loaded, "Sample material resource could not be loaded");
        }
#endif

        static void InvokeGenerate(MonoBehaviour generator)
        {
            if (generator is BridgeUrpLitSampleGenerator urp)
                urp.GenerateLitScene();
            else
                ((BridgeHdrpLitSampleGenerator)generator).GenerateLitScene();
        }

        static GameObject FindGeneratedRoot(bool hdrp)
        {
            var name = hdrp ? "ViewWeave HDRP Lit Sample Content" : "ViewWeave URP Lit Sample Content";
            return GameObject.Find(name);
        }

        static void AssertSingleGeneratedRoot(bool hdrp)
        {
            var name = hdrp ? "ViewWeave HDRP Lit Sample Content" : "ViewWeave URP Lit Sample Content";
            var count = 0;
            foreach (var item in Resources.FindObjectsOfTypeAll<GameObject>())
                if (item.name == name) count++;
            Assert.AreEqual(1, count);
        }

        static Material[] GetMaterials(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var materials = new List<Material>();
            foreach (var renderer in renderers)
                foreach (var material in renderer.sharedMaterials)
                    if (material != null) materials.Add(material);
            return materials.ToArray();
        }

        static VolumeProfile[] GetProfiles(GameObject root)
        {
            var profiles = new List<VolumeProfile>();
            foreach (var volume in root.GetComponentsInChildren<Volume>(true))
                if (volume.sharedProfile != null) profiles.Add(volume.sharedProfile);
            return profiles.ToArray();
        }

        static void CaptureExternalState(GameObject fixture, List<Behaviour> disabled, out Light previousSun)
        {
            previousSun = RenderSettings.sun;
            for (int sceneIndex = 0; sceneIndex < SceneManager.sceneCount; ++sceneIndex)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.isLoaded) continue;
                foreach (var sceneRoot in scene.GetRootGameObjects())
                {
                    foreach (var camera in sceneRoot.GetComponentsInChildren<Camera>(true))
                        DisableExternal(camera, fixture, disabled);
                    foreach (var light in sceneRoot.GetComponentsInChildren<Light>(true))
                        DisableExternal(light, fixture, disabled);
                }
            }
        }

        static void DisableExternal(Behaviour behaviour, GameObject fixture, List<Behaviour> disabled)
        {
            if (behaviour == null || behaviour.transform.IsChildOf(fixture.transform)) return;
            if (behaviour.enabled)
            {
                behaviour.enabled = false;
                disabled.Add(behaviour);
            }
        }

        static void RestoreExternalState(List<Behaviour> disabled, Light previousSun)
        {
            foreach (var behaviour in disabled)
                if (behaviour != null) behaviour.enabled = true;
            RenderSettings.sun = previousSun;
        }

        static void AssertProfilePreserved(QuiltSettings settings)
        {
            Assert.AreEqual(17f, settings.viewCone, 0.0001f);
            Assert.AreEqual(1232, settings.quiltWidth);
            Assert.AreEqual(616, settings.quiltHeight);
            Assert.AreEqual(4, settings.columns);
            Assert.AreEqual(2, settings.rows);
            Assert.AreEqual(8, settings.views);
            Assert.AreEqual(2f, settings.viewAspect, 0.0001f);
        }
    }
}
#endif
