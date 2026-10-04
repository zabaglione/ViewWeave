#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.VFX;
using UnityEngine.TestTools;
using UnityEditor;
using Object = UnityEngine.Object;
using ViewWeave;
using ViewWeave.URP;

namespace ViewWeave.VfxDriverValidation
{
    public sealed class UrpVfxCameraDriverTests
    {
        RenderPipelineAsset oldDefault;
        RenderPipelineAsset oldQuality;
        UniversalRenderPipelineAsset testPipeline;
        UniversalRendererData testRenderer;
        Camera[] savedCameras;
        bool[] savedCameraEnabled;
        Light[] savedLights;
        bool[] savedLightEnabled;

        [SetUp]
        public void SetupPipeline()
        {
            oldDefault = GraphicsSettings.defaultRenderPipeline;
            oldQuality = QualitySettings.renderPipeline;
            testRenderer = ScriptableObject.CreateInstance<UniversalRendererData>();
            testRenderer.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
            Assert.IsNotNull(testRenderer.postProcessData, "URP PostProcessData asset is unavailable");
            testPipeline = UniversalRenderPipelineAsset.Create(testRenderer);
            GraphicsSettings.defaultRenderPipeline = testPipeline;
            QualitySettings.renderPipeline = testPipeline;
            savedCameras = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            savedCameraEnabled = new bool[savedCameras.Length];
            for (int i = 0; i < savedCameras.Length; ++i)
            {
                savedCameraEnabled[i] = savedCameras[i].enabled;
                savedCameras[i].enabled = false;
            }
            savedLights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            savedLightEnabled = new bool[savedLights.Length];
            for (int i = 0; i < savedLights.Length; ++i)
            {
                savedLightEnabled[i] = savedLights[i].enabled;
                savedLights[i].enabled = false;
            }
        }

        [TearDown]
        public void TeardownPipeline()
        {
            if (savedCameras != null)
                for (int i = 0; i < savedCameras.Length; ++i) if (savedCameras[i] != null) savedCameras[i].enabled = savedCameraEnabled[i];
            if (savedLights != null)
                for (int i = 0; i < savedLights.Length; ++i) if (savedLights[i] != null) savedLights[i].enabled = savedLightEnabled[i];
            GraphicsSettings.defaultRenderPipeline = oldDefault;
            QualitySettings.renderPipeline = oldQuality;
            if (testPipeline != null) Object.DestroyImmediate(testPipeline);
            if (testRenderer != null) Object.DestroyImmediate(testRenderer);
        }

        [UnityTest]
        public IEnumerator DriverCreatesOneByOneCameraAndTracksPose()
        {
            yield return null;
            RequireUrp();
            GameObject host = CreateHost();
            try
            {
                UrpVfxCameraDriver driver = ConfigureDriver(host);
                Camera source = host.GetComponent<Camera>();
                int camerasBefore = Resources.FindObjectsOfTypeAll<Camera>().Length;
                host.SetActive(true);
                yield return null;
                Assert.IsTrue(driver.IsRunning, driver.LastError);
                Assert.IsNotNull(driver.DriverCamera);
                Assert.AreEqual(1, driver.DriverCamera.targetTexture.width);
                Assert.AreEqual(1, driver.DriverCamera.targetTexture.height);
                Assert.IsFalse(source.enabled);
                Assert.AreEqual(camerasBefore + 1, Resources.FindObjectsOfTypeAll<Camera>().Length);
                source.transform.SetPositionAndRotation(new Vector3(2f, 3f, 4f), Quaternion.Euler(10f, 20f, 30f));
                yield return null;
                Assert.That(Vector3.Distance(source.transform.position, driver.DriverCamera.transform.position), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(source.transform.rotation, driver.DriverCamera.transform.rotation), Is.LessThan(0.001f));
            }
            finally { Object.DestroyImmediate(host); }
        }

        [UnityTest]
        public IEnumerator DriverDisableReleasesTextureAndReenableRecreatesIt()
        {
            yield return null;
            RequireUrp();
            GameObject host = CreateHost();
            try
            {
                UrpVfxCameraDriver driver = ConfigureDriver(host);
                host.SetActive(true);
                yield return null;
                RenderTexture first = driver.DriverCamera.targetTexture;
                driver.enabled = false;
                Assert.IsFalse(driver.IsRunning);
                Assert.IsFalse(first.IsCreated());
                Assert.IsNull(driver.DriverCamera);
                driver.enabled = true;
                yield return null;
                Assert.IsTrue(driver.IsRunning, driver.LastError);
                Assert.IsNotNull(driver.DriverCamera.targetTexture);
                Assert.AreNotSame(first, driver.DriverCamera.targetTexture);
            }
            finally { Object.DestroyImmediate(host); }
        }

        [UnityTest]
        public IEnumerator DriverIsOptInAndDoesNotOwnQuiltState()
        {
            yield return null;
            RequireUrp();
            GameObject host = CreateHost();
            try
            {
                QuiltRenderer renderer = host.GetComponent<QuiltRenderer>();
                renderer.settings.views = 48;
                renderer.settings.quiltWidth = 1920;
                renderer.settings.quiltHeight = 1080;
                UrpVfxCameraDriver driver = host.AddComponent<UrpVfxCameraDriver>();
                driver.vfxLayers = 1 << 31;
                host.GetComponent<Camera>().cullingMask = 1 << 31;
                host.SetActive(true);
                yield return null;
                Assert.IsFalse(driver.IsRunning);
                Assert.IsFalse(renderer.autoRender);
                driver.runWhenManualRendering = true;
                driver.enableDriver = true;
                yield return null;
                Assert.IsTrue(driver.IsRunning, driver.LastError);
                Assert.AreEqual(48, renderer.settings.views);
                Assert.AreEqual(1920, renderer.settings.quiltWidth);
                Assert.AreEqual(1080, renderer.settings.quiltHeight);
            }
            finally { Object.DestroyImmediate(host); }
        }

        [UnityTest]
        public IEnumerator DriverEnforcesActiveColorSpaceRequirement()
        {
            yield return null;
            RequireUrp();
            GameObject host = CreateHost();
            try
            {
                UrpVfxCameraDriver driver = ConfigureDriver(host);
                Camera source = host.GetComponent<Camera>();
                bool gamma = QualitySettings.activeColorSpace == ColorSpace.Gamma;
                const string expectedError = "VFX Graph requires Linear color space; URP Gamma is unsupported";
                if (gamma)
                    LogAssert.Expect(LogType.Error, "ViewWeave URP VFX driver stopped: " + expectedError);
                host.SetActive(true);
                yield return null;
                if (gamma)
                {
                    Assert.IsFalse(driver.IsRunning, driver.LastError);
                    Assert.IsNull(driver.DriverCamera);
                    Assert.IsFalse(source.enabled);
                    Assert.AreEqual(expectedError, driver.LastError);
                    Assert.IsTrue(IsAscii(driver.LastError));
                    yield return null;
                    yield return null;
                }
                else
                {
                    Assert.IsTrue(driver.IsRunning, driver.LastError);
                    Assert.IsNotNull(driver.DriverCamera);
                    Assert.IsFalse(source.enabled);
                    Assert.AreEqual(string.Empty, driver.LastError);
                    yield return null;
                }
            }
            finally { Object.DestroyImmediate(host); }
        }

        [UnityTest]
        public IEnumerator SimpleBurstUsesOneByOneDriverAtFieldOfView50()
        {
            yield return RunSimpleBurst(50f, "simple-burst-fov50");
        }

        [UnityTest]
        public IEnumerator SimpleBurstUsesOneByOneDriverAtFieldOfView90()
        {
            yield return RunSimpleBurst(90f, "simple-burst-fov90");
        }

        IEnumerator RunSimpleBurst(float fieldOfView, string evidenceName)
        {
            yield return null;
            RequireUrp();
            GameObject host = CreateHost();
            Texture2D before = null;
            Texture2D after = null;
            Texture2D ordinaryBefore = null;
            Texture2D ordinaryAfter = null;
            try
            {
                Camera source = host.GetComponent<Camera>();
                source.cullingMask = 1 << 31;
                source.fieldOfView = 50f;
                UniversalAdditionalCameraData cameraData = host.AddComponent<UniversalAdditionalCameraData>();
                cameraData.renderType = CameraRenderType.Base;
                cameraData.renderPostProcessing = false;
                QuiltRenderer quilt = host.GetComponent<QuiltRenderer>();
                quilt.GuardBehaviour = host.AddComponent<UrpQuiltCamera>();
                quilt.settings.quiltWidth = 512;
                quilt.settings.quiltHeight = 384;
                quilt.settings.columns = 4;
                quilt.settings.rows = 2;
                quilt.settings.views = 8;
                quilt.settings.viewAspect = 128f / 96f;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;
                quilt.autoRender = false;

                GameObject effectObject = new GameObject("UrpVfxCameraDriver.SimpleBurst");
                effectObject.transform.SetParent(host.transform, false);
                effectObject.transform.localPosition = new Vector3(0f, 0.5f, 2.2f);
                effectObject.layer = 31;
                VisualEffect effect = effectObject.AddComponent<VisualEffect>();
                VisualEffectAsset asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>("Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
                Assert.IsNotNull(asset, "VFX asset unavailable at Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx");
                effect.visualEffectAsset = asset;
                effect.resetSeedOnPlay = false;
                effect.startSeed = 17u;
                effect.pause = false;

                UrpVfxCameraDriver driver = host.AddComponent<UrpVfxCameraDriver>();
                driver.vfxLayers = 1 << 31;
                driver.driverFieldOfView = fieldOfView;
                driver.runWhenManualRendering = true;
                driver.enableDriver = false;
                host.SetActive(true);
                for (int i = 0; i < 60; ++i) yield return null;
                VFXRenderer vfxRenderer = effectObject.GetComponent<VFXRenderer>();
                Assert.IsNotNull(vfxRenderer, "VFXRenderer was not created by the VFX asset");
                Assert.IsFalse(driver.IsRunning, driver.LastError);
                effect.Reinit();
                effect.Play();
                for (int i = 0; i < 30; ++i)
                {
                    effect.Simulate(1f / 60f, 1);
                    yield return null;
                }
                effect.pause = true;
                double aliveDeadline = Time.realtimeSinceStartupAsDouble + 1.0;
                while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartupAsDouble < aliveDeadline) yield return null;
                int ordinaryAliveParticleCount = effect.aliveParticleCount;
                vfxRenderer.enabled = false;
                quilt.RenderQuilt();
                ordinaryBefore = ReadQuilt(quilt.QuiltTexture);
                vfxRenderer.enabled = true;
                quilt.RenderQuilt();
                ordinaryAfter = ReadQuilt(quilt.QuiltTexture);
                int[] ordinaryChanged = CountChangedTiles(ordinaryBefore, ordinaryAfter, quilt.settings.GetLayout(), 20);
                for (int view = 0; view < ordinaryChanged.Length; ++view) Assert.LessOrEqual(ordinaryChanged[view], 20, "Simple Burst ordinary baseline unexpectedly changed tile " + view + " changed=" + ordinaryChanged[view]);
                driver.enableDriver = true;
                yield return null;
                Assert.IsTrue(driver.IsRunning, driver.LastError);
                Assert.IsNotNull(driver.DriverCamera);
                Assert.AreEqual(1, driver.DriverCamera.targetTexture.width);
                Assert.AreEqual(1, driver.DriverCamera.targetTexture.height);
                Assert.IsFalse(source.enabled);
                effect.pause = false;
                effect.Reinit();
                effect.Play();
                yield return null;
                yield return null;
                effect.pause = true;
                for (int i = 0; i < 30; ++i)
                {
                    effect.Simulate(1f / 60f, 1);
                    yield return null;
                }
                effect.pause = true;
                double driverAliveDeadline = Time.realtimeSinceStartupAsDouble + 1.0;
                while (effect.aliveParticleCount <= 0 && Time.realtimeSinceStartupAsDouble < driverAliveDeadline) yield return null;
                int aliveParticleCount = effect.aliveParticleCount;
                Assert.Greater(aliveParticleCount, 0, "Simple Burst driver phase produced no alive particles");

                vfxRenderer.enabled = false;
                quilt.RenderQuilt();
                before = ReadQuilt(quilt.QuiltTexture);
                vfxRenderer.enabled = true;
                for (int i = 0; i < 3; ++i) yield return null;
                quilt.RenderQuilt();
                after = ReadQuilt(quilt.QuiltTexture);
                int[] changed = CountChangedTiles(before, after, quilt.settings.GetLayout(), 20);
                string evidence = EvidencePath(evidenceName);
                Directory.CreateDirectory(Path.GetDirectoryName(evidence));
                File.WriteAllText(evidence, "asset=Packages/com.unity.visualeffectgraph/Editor/Templates/Simple_Burst.vfx\nfieldOfView=" + fieldOfView.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\ndriverWidth=1\ndriverHeight=1\nsourceCameraEnabled=" + source.enabled + "\nordinaryAliveParticleCount=" + ordinaryAliveParticleCount + "\naliveParticleCount=" + aliveParticleCount + "\nordinaryDriverEnabled=false\nordinaryChangedTiles=" + string.Join(",", ordinaryChanged) + "\ndriverChangedTiles=" + string.Join(",", changed));
                SavePng(before, evidence.Replace(".txt", "-before.png"));
                SavePng(after, evidence.Replace(".txt", "-after.png"));
                for (int view = 0; view < changed.Length; ++view) Assert.Greater(changed[view], 20, "Simple Burst did not change tile " + view + " changed=" + changed[view]);
            }
            finally
            {
                if (before != null) Object.DestroyImmediate(before);
                if (after != null) Object.DestroyImmediate(after);
                if (ordinaryBefore != null) Object.DestroyImmediate(ordinaryBefore);
                if (ordinaryAfter != null) Object.DestroyImmediate(ordinaryAfter);
                Object.DestroyImmediate(host);
            }
        }

        static Texture2D ReadQuilt(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = texture;
            Texture2D image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply(false, false);
            RenderTexture.active = previous;
            return image;
        }

        static int[] CountChangedTiles(Texture2D before, Texture2D after, QuiltLayout layout, int threshold)
        {
            int[] result = new int[layout.ViewCount];
            Color[] beforePixels = before.GetPixels();
            Color[] afterPixels = after.GetPixels();
            for (int view = 0; view < layout.ViewCount; ++view)
            {
                RectInt tile = layout.GetTile(view);
                for (int y = tile.y; y < tile.yMax; ++y)
                    for (int x = tile.x; x < tile.xMax; ++x)
                    {
                        int index = y * before.width + x;
                        Color a = beforePixels[index];
                        Color b = afterPixels[index];
                        if (Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) > 0.01f) result[view]++;
                    }
            }
            return result;
        }

        static void SavePng(Texture2D image, string path)
        {
            File.WriteAllBytes(path, image.EncodeToPNG());
        }

        static string EvidencePath(string name)
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "vfx-driver-validation", name + ".txt"));
        }

        static GameObject CreateHost()
        {
            GameObject host = new GameObject("UrpVfxCameraDriverValidation");
            host.SetActive(false);
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            QuiltRenderer renderer = host.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            return host;
        }

        static UrpVfxCameraDriver ConfigureDriver(GameObject host)
        {
            UrpVfxCameraDriver driver = host.AddComponent<UrpVfxCameraDriver>();
            driver.vfxLayers = 1 << 31;
            driver.runWhenManualRendering = true;
            driver.enableDriver = true;
            host.GetComponent<Camera>().cullingMask = 1 << 31;
            return driver;
        }

        static void RequireUrp()
        {
            Assert.IsInstanceOf<UniversalRenderPipelineAsset>(GraphicsSettings.currentRenderPipeline, "URP pipeline is required");
        }

        static bool IsAscii(string value)
        {
            if (value == null) return false;
            foreach (char character in value)
                if (character < 0x20 || character > 0x7e) return false;
            return true;
        }
    }
}
#endif
