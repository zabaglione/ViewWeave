using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.URP;

namespace ViewWeave.GraphicsCompatibility
{
    public sealed class UrpGraphicsCompatibilityTests
    {
        [UnityTest] public IEnumerator TransparentLitPlane() => RunFeature("transparent-lit", fixture => fixture.AddTransparentPlane());
        [UnityTest] public IEnumerator ParticleSimulationOnce() => RunFeature("particles-lit", fixture => fixture.AddParticles(false));
        [UnityTest] public IEnumerator ParticleUnlitSimulationOnce() => RunFeature("particles-unlit", fixture => fixture.AddParticles(true));
        [UnityTest] public IEnumerator ParticleLiveSimulation() => RunFeature("particles-live-lit", fixture => fixture.AddParticles(false, true));
        [UnityTest] public IEnumerator WorldSpaceUGUI() => RunFeature("ugui-world", fixture => fixture.AddWorldSpaceUi());
        [UnityTest] public IEnumerator ScreenSpaceCameraUGUI() => RunFeature("ugui-camera", fixture => fixture.AddScreenSpaceCameraUi());
        [UnityTest] public IEnumerator UIToolkitTargetTexture() => RunFeature("uitk-target", fixture => fixture.AddUiToolkit());
        [UnityTest] public IEnumerator BloomPerViewToggle() => RunFeature("bloom", fixture => fixture.AddBloom());

        [UnityTest]
        public IEnumerator CameraOwnedVolumeStackReuseUpdatesEveryViewAndRestoresCameraState()
        {
            var fixture = new Fixture("volume-stack-reuse-every-frame");
            try
            {
                fixture.Setup();
                fixture.AddBloom();
                fixture.RefreshScope();
                fixture.Guard.reuseCameraOwnedVolumeStackForQuilt = true;
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
                fixture.CameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                Assert.IsNull(fixture.CameraData.volumeStack, "test camera must not start with a camera-owned stack");

                AssertCameraOwnedStackUsedForEveryView(fixture);
            }
            finally { fixture.Dispose(); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CameraOwnedVolumeStackReuseHonorsPipelineEveryFrameSetting()
        {
            var fixture = new Fixture("volume-stack-reuse-pipeline-settings");
            try
            {
                fixture.Setup();
                fixture.AddBloom();
                fixture.RefreshScope();
                SetPipelineVolumeUpdateMode(fixture.Pipeline, VolumeFrameworkUpdateMode.EveryFrame);
                fixture.Guard.reuseCameraOwnedVolumeStackForQuilt = true;
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.UsePipelineSettings);
                fixture.CameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                Assert.IsTrue(fixture.CameraData.requiresVolumeFrameworkUpdate, "pipeline EveryFrame mode should be effective for this camera");
                Assert.IsNull(fixture.CameraData.volumeStack, "test camera must not start with a camera-owned stack");

                AssertCameraOwnedStackUsedForEveryView(fixture);
            }
            finally { fixture.Dispose(); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CameraOwnedVolumeStackReusePreservesPerViewLocalVolumeBlendAndRefreshesNextQuilt()
        {
            var fixture = new Fixture("volume-stack-reuse-local-volume");
            GameObject volumeObject = null;
            VolumeProfile profile = null;
            try
            {
                fixture.Setup();
                SetPipelineVolumeUpdateMode(fixture.Pipeline, VolumeFrameworkUpdateMode.EveryFrame);
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.UsePipelineSettings);
                fixture.CameraData.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                fixture.CameraData.volumeTrigger = fixture.Camera.transform;
                volumeObject = new GameObject("LocalBlendBoundaryVolume");
                volumeObject.layer = GraphicsFixtureScope.Layer;
                volumeObject.transform.position = Vector3.zero;
                BoxCollider collider = volumeObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(0.35f, 4f, 4f);
                Volume volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = false;
                volume.blendDistance = 1.2f;
                volume.priority = 10f;
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                Bloom bloom = profile.Add<Bloom>(true);
                bloom.active = true;
                bloom.intensity.overrideState = true;
                bloom.intensity.value = 0.4f;
                volume.sharedProfile = profile;
                fixture.RefreshScope();

                float[] baseline = CapturePerViewBloom(fixture);
                Assert.Greater(Maximum(baseline) - Minimum(baseline), 0.05f, "local Volume boundary did not produce distinct per-view blends");
                if (fixture.CameraData.volumeStack != null) fixture.Camera.DestroyVolumeStack();

                fixture.Guard.reuseCameraOwnedVolumeStackForQuilt = true;
                float[] optimized = CapturePerViewBloom(fixture);
                AssertPerViewValuesEqual(baseline, optimized, "optimized stack reuse changed local Volume blending");

                bloom.intensity.value = 1.2f;
                float[] nextQuilt = CapturePerViewBloom(fixture);
                Assert.Greater(Maximum(nextQuilt) - Maximum(optimized), 0.2f, "the next quilt did not reflect the updated Volume profile");
                Assert.Greater(Minimum(nextQuilt) - Minimum(optimized), 0.2f, "the next quilt did not refresh all view stack values");
            }
            finally
            {
                fixture.Dispose();
                if (volumeObject != null) UnityEngine.Object.DestroyImmediate(volumeObject);
                if (profile != null) UnityEngine.Object.DestroyImmediate(profile);
            }
            yield return null;
        }

        static float[] CapturePerViewBloom(Fixture fixture)
        {
            float[] values = new float[8];
            int count = 0;
            Camera camera = fixture.Camera;
            Action<ScriptableRenderContext, Camera> callback = (context, renderedCamera) =>
            {
                if (renderedCamera != camera) return;
                Assert.Less(count, values.Length, "more than eight view callbacks were received");
                VolumeStack stack = VolumeManager.instance.stack;
                Assert.IsNotNull(stack, "URP did not expose the active VolumeStack at endCameraRendering");
                Bloom current = stack.GetComponent<Bloom>();
                Assert.IsNotNull(current, "Bloom component was missing from the active per-view VolumeStack");
                values[count++] = current.intensity.value;
            };
            RenderPipelineManager.endCameraRendering += callback;
            try { fixture.Quilt.RenderQuilt(); }
            finally { RenderPipelineManager.endCameraRendering -= callback; }
            Assert.AreEqual(values.Length, count, "expected one Volume sample for each quilt view");
            return values;
        }

        static void AssertPerViewValuesEqual(float[] expected, float[] actual, string message)
        {
            Assert.AreEqual(expected.Length, actual.Length, message);
            for (int i = 0; i < expected.Length; i++) Assert.AreEqual(expected[i], actual[i], 0.01f, message + " at view " + i);
        }

        static float Maximum(float[] values)
        {
            float value = float.MinValue;
            for (int i = 0; i < values.Length; i++) value = Mathf.Max(value, values[i]);
            return value;
        }

        static float Minimum(float[] values)
        {
            float value = float.MaxValue;
            for (int i = 0; i < values.Length; i++) value = Mathf.Min(value, values[i]);
            return value;
        }

        [UnityTest]
        public IEnumerator CameraOwnedVolumeStackReuseFallsBackWhenCameraUsesScriptingUpdates()
        {
            var fixture = new Fixture("volume-stack-reuse-scripting-fallback");
            try
            {
                fixture.Setup();
                fixture.AddBloom();
                fixture.RefreshScope();
                fixture.Guard.reuseCameraOwnedVolumeStackForQuilt = true;
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                VolumeFrameworkUpdateMode originalMode = fixture.Camera.GetVolumeFrameworkUpdateMode();
                Assert.IsFalse(fixture.CameraData.requiresVolumeFrameworkUpdate, "scripting-driven camera must not enter the automatic-update fast path");
                bool leaseObserved = false;
                Action<ScriptableRenderContext, Camera> callback = (context, camera) =>
                {
                    if (camera == fixture.Camera) leaseObserved |= fixture.Guard.CameraOwnedVolumeStackLeaseActive;
                };
                RenderPipelineManager.beginCameraRendering += callback;
                try { fixture.Quilt.RenderQuilt(); }
                finally { RenderPipelineManager.beginCameraRendering -= callback; }

                Assert.AreEqual(1, fixture.Guard.ValidationStackCreationCount, "fallback should use the existing validation stack");
                Assert.IsFalse(leaseObserved, "scripting-driven fallback unexpectedly activated the camera-stack lease");
                Assert.AreEqual(originalMode, fixture.Camera.GetVolumeFrameworkUpdateMode(), "fallback changed the camera update mode");
            }
            finally { fixture.Dispose(); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CameraOwnedVolumeStackReusePreservesAnExistingCameraStack()
        {
            var fixture = new Fixture("volume-stack-reuse-existing-stack-fallback");
            VolumeStack existingStack = null;
            try
            {
                fixture.Setup();
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                fixture.Camera.UpdateVolumeStack();
                existingStack = fixture.CameraData.volumeStack;
                Assert.IsNotNull(existingStack, "test setup did not create the pre-existing camera-owned stack");
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.ViaScripting);
                VolumeFrameworkUpdateMode originalMode = fixture.Camera.GetVolumeFrameworkUpdateMode();
                fixture.Guard.reuseCameraOwnedVolumeStackForQuilt = true;

                fixture.Quilt.RenderQuilt();

                Assert.AreSame(existingStack, fixture.CameraData.volumeStack, "fallback replaced the pre-existing camera-owned stack");
                Assert.AreEqual(1, fixture.Guard.ValidationStackCreationCount, "existing-stack condition should use the legacy validation stack");
                Assert.AreEqual(originalMode, fixture.Camera.GetVolumeFrameworkUpdateMode(), "fallback changed the camera update mode");
            }
            finally
            {
                if (existingStack != null && fixture.Camera != null) fixture.Camera.DestroyVolumeStack();
                fixture.Dispose();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CameraOwnedVolumeStackMotionBlurRejectionRestoresAllState()
        {
            var fixture = new Fixture("volume-stack-reuse-motion-blur");
            GameObject volumeObject = null;
            VolumeProfile profile = null;
            try
            {
                fixture.Setup();
                fixture.Guard.reuseCameraOwnedVolumeStackForQuilt = true;
                fixture.Camera.SetVolumeFrameworkUpdateMode(VolumeFrameworkUpdateMode.EveryFrame);
                fixture.CameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                volumeObject = new GameObject("VolumeStackReuseMotionBlur");
                volumeObject.transform.SetParent(fixture.Camera.transform, false);
                volumeObject.layer = GraphicsFixtureScope.Layer;
                var volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true;
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                MotionBlur motionBlur = profile.Add<MotionBlur>(true);
                motionBlur.active = true;
                motionBlur.intensity.value = 0.5f;
                motionBlur.intensity.overrideState = true;
                volume.sharedProfile = profile;
                VolumeStack originalManagerStack = VolumeManager.instance.stack;

                Assert.Throws<NotSupportedException>(() => fixture.Quilt.RenderQuilt());

                Assert.IsNull(fixture.CameraData.volumeStack, "rejected quilt retained its temporary camera-owned stack");
                Assert.AreEqual(VolumeFrameworkUpdateMode.EveryFrame, fixture.Camera.GetVolumeFrameworkUpdateMode(), "rejection did not restore the camera update mode");
                Assert.AreSame(originalManagerStack, VolumeManager.instance.stack, "rejection did not restore the global VolumeManager stack");
                Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, fixture.CameraData.antialiasing, "rejection changed camera antialiasing");
            }
            finally
            {
                fixture.Dispose();
                if (volumeObject != null) UnityEngine.Object.DestroyImmediate(volumeObject);
                if (profile != null) UnityEngine.Object.DestroyImmediate(profile);
            }
            yield return null;
        }

        static void AssertCameraOwnedStackUsedForEveryView(Fixture fixture)
        {
            int callbacks = 0;
            VolumeStack firstStack = null;
            bool sameStack = true;
            bool managerUsedCameraStack = true;
            bool antialiasingSuppressed = true;
            bool leaseActiveAtBegin = true;
            bool modeCorrectAtBegin = true;
            Camera target = fixture.Camera;
            UniversalAdditionalCameraData cameraData = fixture.CameraData;
            VolumeFrameworkUpdateMode originalMode = target.GetVolumeFrameworkUpdateMode();
            int endCallbacks = 0;
            bool leaseActiveAtEnd = true;
            bool modeCorrectAtEnd = true;
            Action<ScriptableRenderContext, Camera> onEndCameraRendering = (context, camera) =>
            {
                if (camera != target) return;
                endCallbacks++;
                leaseActiveAtEnd &= fixture.Guard.CameraOwnedVolumeStackLeaseActive;
                modeCorrectAtEnd &= camera.GetVolumeFrameworkUpdateMode() == VolumeFrameworkUpdateMode.ViaScripting;
            };
            Action<ScriptableRenderContext, Camera> onBeginCameraRendering = (context, camera) =>
            {
                if (camera != target) return;
                callbacks++;
                leaseActiveAtBegin &= fixture.Guard.CameraOwnedVolumeStackLeaseActive;
                modeCorrectAtBegin &= camera.GetVolumeFrameworkUpdateMode() == VolumeFrameworkUpdateMode.ViaScripting;
                VolumeStack current = cameraData.volumeStack;
                if (current == null || (firstStack != null && current != firstStack)) sameStack = false;
                if (firstStack == null) firstStack = current;
                if (VolumeManager.instance.stack != current) managerUsedCameraStack = false;
                if (cameraData.antialiasing != AntialiasingMode.None) antialiasingSuppressed = false;
            };
            RenderPipelineManager.beginCameraRendering += onBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += onEndCameraRendering;
            try { fixture.Quilt.RenderQuilt(); }
            finally
            {
                RenderPipelineManager.beginCameraRendering -= onBeginCameraRendering;
                RenderPipelineManager.endCameraRendering -= onEndCameraRendering;
            }

            Assert.AreEqual(8, callbacks, "expected one render callback for each of the eight view requests");
            Assert.IsNotNull(firstStack, "optimized path did not create a camera-owned VolumeStack");
            Assert.IsTrue(sameStack, "the camera-owned VolumeStack identity changed between views");
            Assert.IsTrue(managerUsedCameraStack, "the global VolumeManager did not use the camera-owned stack while rendering");
            Assert.IsTrue(antialiasingSuppressed, "TAA was not suppressed during a quilt view");
            Assert.IsTrue(leaseActiveAtBegin, "camera-owned VolumeStack lease was not active at beginCameraRendering");
            Assert.IsTrue(modeCorrectAtBegin, "ViaScripting mode was not active at beginCameraRendering");
            Assert.AreEqual(8, endCallbacks, "expected paired end callbacks for all views");
            Assert.IsTrue(leaseActiveAtEnd, "camera-owned VolumeStack lease was not active through endCameraRendering");
            Assert.IsTrue(modeCorrectAtEnd, "ViaScripting mode was not active through endCameraRendering");
            Assert.AreEqual(0, fixture.Guard.ValidationStackCreationCount, "optimized path also created the legacy validation stack");
            Assert.IsNull(cameraData.volumeStack, "temporary camera-owned VolumeStack was not released after the quilt");
            Assert.AreEqual(originalMode, fixture.Camera.GetVolumeFrameworkUpdateMode(), "camera update mode was not restored");
            Assert.AreEqual(AntialiasingMode.TemporalAntiAliasing, cameraData.antialiasing, "camera antialiasing was not restored");
        }

        static void SetPipelineVolumeUpdateMode(UniversalRenderPipelineAsset pipeline, VolumeFrameworkUpdateMode mode)
        {
#if UNITY_EDITOR
            var serialized = new SerializedObject(pipeline);
            SerializedProperty iterator = serialized.GetIterator();
            SerializedProperty modeProperty = null;
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyType != SerializedPropertyType.Enum) continue;
                string[] names = iterator.enumNames;
                if (names == null || Array.IndexOf(names, mode.ToString()) < 0) continue;
                if (iterator.displayName.IndexOf("Volume", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    modeProperty = serialized.FindProperty(iterator.propertyPath);
                    break;
                }
            }
            Assert.IsNotNull(modeProperty, "URP asset's serialized Volume update mode was not found");
            modeProperty.enumValueIndex = Array.IndexOf(modeProperty.enumNames, mode.ToString());
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual(mode, pipeline.volumeFrameworkUpdateMode, "URP asset did not accept the Volume update mode");
#else
            Assert.Fail("Configuring the URP asset Volume update mode requires the Unity Editor");
#endif
        }

        IEnumerator RunFeature(string name, Action<Fixture> add)
        {
            Fixture fixture = null;
            try
            {
                fixture = new Fixture(name);
                fixture.Setup();
                add(fixture);
                fixture.RefreshScope();
                yield return null;
                yield return null;
                fixture.SetFeatureEnabled(false);
                yield return null;
                yield return null;
                fixture.RenderAndCapture();
                fixture.SaveImage("before");
                fixture.SetFeatureEnabled(true);
                yield return null;
                yield return null;
                fixture.RenderAndCapture();
                fixture.SaveImage("after");
                fixture.AssertFeatureChanged();
                if (name == "transparent-lit") fixture.AssertTransparentAlphaBlend();
            }
            finally { fixture?.Dispose(); }
        }

        sealed class Fixture
        {
            const int TileWidth = 128;
            const int TileHeight = 96;
            readonly string name;
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            GameObject root;
            GameObject featureRoot;
            GameObject uiPlane;
            Camera camera;
            QuiltRenderer quilt;
            UrpQuiltCamera guard;
            Texture2D imageBefore;
            Texture2D imageAfter;
            Texture2D alphaFull;
            Material transparentMaterial;
            ParticleSystem particle;
            ParticleSystemRenderer particleRenderer;
            bool particleLive;
            Volume bloomVolume;
            RenderTexture uiTargetTexture;
            UnityEngine.UIElements.PanelSettings uiPanelSettings;
            UnityEngine.UIElements.UIDocument uiDocument;
            float particleTimeBefore;
            int particleCountBefore;
            RenderPipelineAsset previousDefaultPipeline;
            RenderPipelineAsset previousQualityPipeline;
            UniversalRenderPipelineAsset ownedPipeline;
            UniversalRendererData ownedRendererData;
            GraphicsFixtureScope scope;
            Action<bool> featureToggle = enabled => { };
            bool featureEnabled;

            public Fixture(string featureName) { name = featureName; }

            public void Setup()
            {
                previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
                previousQualityPipeline = QualitySettings.renderPipeline;
                ownedRendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
#if UNITY_EDITOR
                ownedRendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
#else
                Assert.Fail("Graphics compatibility fixtures require the Unity Editor and are unsupported in Player builds");
#endif
                if (ownedRendererData.postProcessData == null) Assert.Fail("URP PostProcessData asset is unavailable");
                ownedPipeline = UniversalRenderPipelineAsset.Create(ownedRendererData);
                ownedPipeline.supportsHDR = true;
                GraphicsSettings.defaultRenderPipeline = ownedPipeline;
                QualitySettings.renderPipeline = ownedPipeline;
                root = new GameObject("GraphicsCompatibility_" + name); owned.Add(root);
                GameObject cameraObject = new GameObject("QuiltCamera"); cameraObject.transform.SetParent(root.transform, false); camera = cameraObject.AddComponent<Camera>(); camera.enabled = false; camera.fieldOfView = 50f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black;
                UniversalAdditionalCameraData cameraData = camera.gameObject.AddComponent<UniversalAdditionalCameraData>(); cameraData.renderType = CameraRenderType.Base; cameraData.renderPostProcessing = true; cameraData.volumeLayerMask = 1 << GraphicsFixtureScope.Layer;
                quilt = cameraObject.AddComponent<QuiltRenderer>(); quilt.sourceCamera = camera; quilt.settings.quiltWidth = TileWidth * 4; quilt.settings.quiltHeight = TileHeight * 2; quilt.settings.columns = 4; quilt.settings.rows = 2; quilt.settings.views = 8; quilt.settings.viewAspect = 1f; quilt.settings.cameraDistance = 3f; quilt.settings.fieldOfView = 50f; quilt.settings.viewCone = 30f;
                guard = cameraObject.AddComponent<UrpQuiltCamera>(); quilt.GuardBehaviour = guard;
                AddLitObject("BaseLit", new Vector3(0f, 0f, 3f), Color.white);
                GameObject lightObject = new GameObject("DirectionalLight"); lightObject.transform.SetParent(root.transform, false); Light light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.2f; lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f); owned.Add(lightObject);
                scope = GraphicsFixtureScope.Create(root);
                scope.Apply(camera);
            }

            public Camera Camera => camera;
            public QuiltRenderer Quilt => quilt;
            public UrpQuiltCamera Guard => guard;
            public UniversalAdditionalCameraData CameraData => camera.GetComponent<UniversalAdditionalCameraData>();
            public UniversalRenderPipelineAsset Pipeline => ownedPipeline;

            void AddLitObject(string objectName, Vector3 position, Color color)
            {
                GameObject item = GameObject.CreatePrimitive(PrimitiveType.Cube); item.name = objectName; item.transform.SetParent(root.transform, false); item.transform.localPosition = position; Material material = CreateLitMaterial(color); item.GetComponent<Renderer>().sharedMaterial = material; owned.Add(item); owned.Add(material);
            }

            Material CreateLitMaterial(Color color)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit"); if (shader == null) Assert.Fail("URP Lit shader is unavailable");
                Material material = new Material(shader); material.color = color; return material;
            }

            public void SetFeatureEnabled(bool enabled)
            {
                featureEnabled = enabled;
                if (featureToggle != null) featureToggle(enabled);
            }

            public void RefreshScope()
            {
                scope.Apply(camera);
            }

            public void RenderAndCapture()
            {
                if (particle != null)
                {
                    particleTimeBefore = particle.time;
                    particleCountBefore = particle.particleCount;
                }
                quilt.RenderQuilt();
                if (particle != null)
                {
                    Assert.AreEqual(particleTimeBefore, particle.time, 0.0001f, "particle time changed during quilt requests");
                    Assert.AreEqual(particleCountBefore, particle.particleCount, "particle count changed during quilt requests");
                    if (particleLive) Assert.IsTrue(particle.isPlaying, "live particle system stopped during quilt requests");
                }
                RenderTexture old = RenderTexture.active; Texture2D current = null;
                try { RenderTexture.active = quilt.QuiltTexture; current = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false); current.ReadPixels(new Rect(0, 0, current.width, current.height), 0, 0); current.Apply(); }
                finally { RenderTexture.active = old; }
                if (imageBefore == null) imageBefore = current; else { if (imageAfter != null) UnityEngine.Object.DestroyImmediate(imageAfter); imageAfter = current; }
                if (uiTargetTexture != null && featureEnabled)
                {
                    RenderTexture previous = RenderTexture.active; Texture2D uiImage = null;
                    try { RenderTexture.active = uiTargetTexture; uiImage = new Texture2D(uiTargetTexture.width, uiTargetTexture.height, TextureFormat.RGBA32, false); uiImage.ReadPixels(new Rect(0, 0, uiImage.width, uiImage.height), 0, 0); uiImage.Apply(); }
                    finally { RenderTexture.active = previous; }
                    Color uiPixel = uiImage.GetPixel(uiImage.width / 2, uiImage.height / 2); UnityEngine.Object.DestroyImmediate(uiImage);
                    string uiDiagnosticPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility", "uitk-diagnostic.txt"));
                    Directory.CreateDirectory(Path.GetDirectoryName(uiDiagnosticPath));
                    File.WriteAllText(uiDiagnosticPath, "target=" + uiTargetTexture.width + "x" + uiTargetTexture.height + " rootBounds=" + uiDocument.rootVisualElement.worldBound + " pixel=" + uiPixel + "\n");
                    Assert.Greater(uiPixel.r, 0.2f, "UI Toolkit target texture did not receive the panel color");
                    Assert.Greater(uiPixel.b, 0.2f, "UI Toolkit target texture did not receive the panel color");
                }
            }

            public void AssertFeatureChanged()
            {
                Assert.IsNotNull(imageBefore, "feature-off image was not captured"); Assert.IsNotNull(imageAfter, "feature-on image was not captured");
                Color[] before = imageBefore.GetPixels(); Color[] after = imageAfter.GetPixels(); int changed = 0;
                for (int i = 0; i < before.Length; ++i) if (Mathf.Abs(before[i].r - after[i].r) + Mathf.Abs(before[i].g - after[i].g) + Mathf.Abs(before[i].b - after[i].b) > 0.01f) changed++;
                Assert.Greater(changed, 20, "feature toggle did not change enough quilt pixels");
                for (int view = 0; view < 8; ++view)
                {
                    RectInt tile = quilt.settings.GetLayout().GetTile(view); int tileChanged = 0;
                    for (int y = tile.y; y < tile.yMax; ++y) for (int x = tile.x; x < tile.xMax; ++x)
                    {
                        int index = y * imageBefore.width + x;
                        if (Mathf.Abs(before[index].r - after[index].r) + Mathf.Abs(before[index].g - after[index].g) + Mathf.Abs(before[index].b - after[index].b) > 0.01f) tileChanged++;
                    }
                    Assert.Greater(tileChanged, 20, "feature toggle did not change tile " + view);
                }
            }

            public void AssertTransparentAlphaBlend()
            {
                Assert.IsNotNull(transparentMaterial, "transparent material is unavailable");
                transparentMaterial.color = new Color(1f, 0f, 1f, 1f);
                quilt.RenderQuilt();
                alphaFull = CaptureTexture(quilt.QuiltTexture);
                SaveImage("alpha1", alphaFull);
                Color[] half = imageAfter.GetPixels(); Color[] full = alphaFull.GetPixels();
                for (int view = 0; view < 8; ++view)
                {
                    RectInt tile = quilt.settings.GetLayout().GetTile(view); int changed = 0;
                    for (int y = tile.y; y < tile.yMax; ++y) for (int x = tile.x; x < tile.xMax; ++x) { int i = y * imageAfter.width + x; if (Mathf.Abs(half[i].r - full[i].r) + Mathf.Abs(half[i].g - full[i].g) + Mathf.Abs(half[i].b - full[i].b) > 0.01f) changed++; }
                    Assert.Greater(changed, 20, "transparent alpha did not change tile " + view);
                }
                transparentMaterial.color = new Color(1f, 0f, 1f, 0.5f);
            }

            public void SaveImage(string suffix)
            {
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility")); Directory.CreateDirectory(directory); Texture2D image = suffix == "before" ? imageBefore : imageAfter; File.WriteAllBytes(Path.Combine(directory, name + "-" + suffix + ".png"), image.EncodeToPNG());
            }

            public void Dispose()
            {
                GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline; QualitySettings.renderPipeline = previousQualityPipeline;
                RenderTexture.active = null; if (imageBefore != null) UnityEngine.Object.DestroyImmediate(imageBefore); if (imageAfter != null && imageAfter != imageBefore) UnityEngine.Object.DestroyImmediate(imageAfter); if (alphaFull != null) UnityEngine.Object.DestroyImmediate(alphaFull);
                if (uiPanelSettings != null) UnityEngine.Object.DestroyImmediate(uiPanelSettings);
                if (uiTargetTexture != null) { uiTargetTexture.Release(); UnityEngine.Object.DestroyImmediate(uiTargetTexture); }
                foreach (UnityEngine.Object item in owned) if (item != null) UnityEngine.Object.DestroyImmediate(item);
                scope?.Dispose();
                if (ownedPipeline != null) UnityEngine.Object.DestroyImmediate(ownedPipeline); if (ownedRendererData != null) UnityEngine.Object.DestroyImmediate(ownedRendererData);
            }

            Texture2D CaptureTexture(RenderTexture source)
            {
                RenderTexture old = RenderTexture.active; Texture2D result = null;
                try { RenderTexture.active = source; result = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false); result.ReadPixels(new Rect(0, 0, result.width, result.height), 0, 0); result.Apply(); }
                finally { RenderTexture.active = old; }
                return result;
            }

            void SaveImage(string suffix, Texture2D image)
            {
                string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility")); Directory.CreateDirectory(directory); File.WriteAllBytes(Path.Combine(directory, name + "-" + suffix + ".png"), image.EncodeToPNG());
            }

            public void AddTransparentPlane()
            {
                featureRoot = GameObject.CreatePrimitive(PrimitiveType.Quad); featureRoot.name = "TransparentLitPlane"; featureRoot.transform.SetParent(root.transform, false); featureRoot.transform.localPosition = new Vector3(0f, 0f, 2.5f); Material material = CreateLitMaterial(new Color(1f, 0f, 1f, 0.5f)); transparentMaterial = material; material.SetFloat("_Surface", 1f); material.SetFloat("_ZWrite", 0f); material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha); material.renderQueue = (int)RenderQueue.Transparent; material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); featureRoot.GetComponent<Renderer>().sharedMaterial = material; owned.Add(featureRoot); owned.Add(material); featureToggle = enabled => featureRoot.SetActive(enabled);
            }

            public void AddParticles(bool unlit) { AddParticles(unlit, false); }
            public void AddParticles(bool unlit, bool live)
            {
                featureRoot = new GameObject("DeterministicParticles"); featureRoot.transform.SetParent(root.transform, false); particle = featureRoot.AddComponent<ParticleSystem>(); particle.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); var main = particle.main; main.loop = false; main.playOnAwake = false; main.startLifetime = 10f; main.startSpeed = 0f; main.startSize = 0.6f; main.startColor = Color.magenta; main.simulationSpace = ParticleSystemSimulationSpace.World; particle.randomSeed = 17; var emission = particle.emission; emission.enabled = false; particleRenderer = featureRoot.GetComponent<ParticleSystemRenderer>(); string shaderName = unlit ? "Universal Render Pipeline/Particles/Unlit" : "Universal Render Pipeline/Particles/Lit"; Shader shader = Shader.Find(shaderName); if (shader == null) Assert.Fail(shaderName + " shader is unavailable"); Material material = new Material(shader); material.color = Color.magenta; particleRenderer.sharedMaterial = material; Vector3[] positions = { new Vector3(-0.7f, -0.4f, 2.2f), new Vector3(0.7f, -0.4f, 2.2f), new Vector3(-0.7f, 0.4f, 2.2f), new Vector3(0.7f, 0.4f, 2.2f) }; for (int index = 0; index < positions.Length; ++index) { ParticleSystem.EmitParams emit = new ParticleSystem.EmitParams { position = positions[index], velocity = Vector3.zero, startColor = Color.magenta, startSize = 0.6f, startLifetime = 10f }; particle.Emit(emit, 1); } particle.Simulate(0.01f, false, false, true); particle.Pause(); particleLive = live; if (live) particle.Play(false); ParticleSystem.Particle[] diagnostics = new ParticleSystem.Particle[16]; int diagnosticCount = particle.GetParticles(diagnostics); string diagnosticPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "graphics-compatibility", "particles-" + (unlit ? "unlit" : "lit") + (live ? "-live" : "") + "-diagnostic.txt")); Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)); using (StreamWriter writer = new StreamWriter(diagnosticPath, false)) { writer.WriteLine("shader=" + shaderName); writer.WriteLine("live=" + live); writer.WriteLine("renderMode=" + particleRenderer.renderMode); writer.WriteLine("materialKeywords=" + string.Join(",", material.shaderKeywords)); writer.WriteLine("rendererBounds=" + particleRenderer.bounds); writer.WriteLine("particleCount=" + diagnosticCount); for (int index = 0; index < diagnosticCount; ++index) writer.WriteLine("particle=" + index + " position=" + diagnostics[index].position + " size=" + diagnostics[index].GetCurrentSize(particle) + " color=" + diagnostics[index].GetCurrentColor(particle)); } Assert.Greater(diagnosticCount, 0, "particle setup emitted no particles"); if (live) Assert.IsTrue(particle.isPlaying, "live particle system did not start"); else Assert.IsFalse(particle.isPlaying, "particle system must be paused"); owned.Add(featureRoot); owned.Add(material); featureToggle = enabled => particleRenderer.enabled = enabled;
            }

            public void AddWorldSpaceUi()
            {
                featureRoot = new GameObject("WorldSpaceCanvas"); featureRoot.transform.SetParent(root.transform, false); var canvas = featureRoot.AddComponent<UnityEngine.Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera; canvas.transform.localPosition = new Vector3(0f, 0f, 2.2f); canvas.transform.localScale = Vector3.one * 0.01f; canvas.GetComponent<RectTransform>().sizeDelta = new Vector2(256f, 192f); GameObject imageObject = new GameObject("WorldSpaceImage"); imageObject.transform.SetParent(featureRoot.transform, false); RectTransform imageRect = imageObject.AddComponent<RectTransform>(); imageRect.anchorMin = Vector2.zero; imageRect.anchorMax = Vector2.one; imageRect.sizeDelta = Vector2.zero; var imageComponent = imageObject.AddComponent<UnityEngine.UI.Image>(); imageComponent.color = Color.magenta; Canvas.ForceUpdateCanvases(); owned.Add(featureRoot); featureToggle = enabled => featureRoot.SetActive(enabled);
            }

            public void AddScreenSpaceCameraUi()
            {
                featureRoot = new GameObject("ScreenSpaceCameraCanvas"); featureRoot.transform.SetParent(root.transform, false); var canvas = featureRoot.AddComponent<UnityEngine.Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = quilt.settings.cameraDistance; GameObject imageObject = new GameObject("ScreenSpaceImage"); imageObject.transform.SetParent(featureRoot.transform, false); RectTransform imageRect = imageObject.AddComponent<RectTransform>(); imageRect.anchorMin = Vector2.zero; imageRect.anchorMax = Vector2.one; imageRect.sizeDelta = Vector2.zero; var imageComponent = imageObject.AddComponent<UnityEngine.UI.Image>(); imageComponent.color = Color.white; Canvas.ForceUpdateCanvases(); owned.Add(featureRoot); featureToggle = enabled => featureRoot.SetActive(enabled);
            }

            public void AddUiToolkit()
            {
                featureRoot = new GameObject("UIToolkitPanel"); featureRoot.transform.SetParent(root.transform, false); uiDocument = featureRoot.AddComponent<UnityEngine.UIElements.UIDocument>(); var panel = ScriptableObject.CreateInstance<UnityEngine.UIElements.PanelSettings>(); uiPanelSettings = panel; uiTargetTexture = new RenderTexture(256, 192, 0, RenderTextureFormat.ARGB32); uiTargetTexture.Create(); panel.targetTexture = uiTargetTexture; panel.referenceResolution = new Vector2Int(256, 192); panel.scaleMode = UnityEngine.UIElements.PanelScaleMode.ConstantPixelSize; uiDocument.panelSettings = panel; uiDocument.rootVisualElement.style.width = 256f; uiDocument.rootVisualElement.style.height = 192f; var element = new UnityEngine.UIElements.VisualElement(); element.style.width = 256f; element.style.height = 192f; element.style.backgroundColor = new Color(0.9f, 0f, 0.9f, 1f); uiDocument.rootVisualElement.Add(element); uiPlane = GameObject.CreatePrimitive(PrimitiveType.Quad); uiPlane.name = "UIToolkitWorldPlane"; uiPlane.transform.SetParent(root.transform, false); uiPlane.transform.localPosition = new Vector3(0f, 0f, 2.0f); Material material = CreateLitMaterial(Color.white); material.SetTexture("_BaseMap", uiTargetTexture); uiPlane.GetComponent<Renderer>().sharedMaterial = material; owned.Add(featureRoot); owned.Add(uiPlane); owned.Add(material); featureToggle = enabled => uiPlane.SetActive(enabled);
            }

            public void AddBloom()
            {
                featureRoot = GameObject.CreatePrimitive(PrimitiveType.Sphere); featureRoot.name = "BloomEmissionTarget"; featureRoot.transform.SetParent(root.transform, false); featureRoot.transform.localPosition = new Vector3(0.7f, 0f, 2.5f); Material material = CreateLitMaterial(Color.white); material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", new Color(5f, 2f, 5f, 1f)); featureRoot.GetComponent<Renderer>().sharedMaterial = material; GameObject volumeObject = new GameObject("PerViewBloomVolume"); volumeObject.transform.SetParent(root.transform, false); bloomVolume = volumeObject.AddComponent<Volume>(); bloomVolume.isGlobal = true; VolumeProfile profile = ScriptableObject.CreateInstance<VolumeProfile>(); Bloom bloom = profile.Add<Bloom>(true); bloom.active = true; bloom.intensity.value = 3f; bloom.threshold.value = 0.5f; bloomVolume.sharedProfile = profile; owned.Add(featureRoot); owned.Add(material); owned.Add(volumeObject); owned.Add(profile); featureToggle = enabled => bloomVolume.enabled = enabled;
            }
        }
    }
}
