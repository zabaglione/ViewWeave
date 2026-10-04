using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;

namespace ViewWeave.Samples.WindowsHdrpLit
{
    public sealed class BridgeHdrpLitSampleGenerator : MonoBehaviour
    {
        const int VolumeLayer = 30;
        [SerializeField] GameObject generatedContent;
        [SerializeField] List<Object> owned = new List<Object>();
        [SerializeField] Camera rigCamera;
        [SerializeField] HDAdditionalCameraData cameraData;
        [SerializeField] QuiltSettings oldSettings;
        [SerializeField] int oldVolumeMask;
        [SerializeField] float oldFov, oldNear, oldFar;
        [SerializeField] HDAdditionalCameraData.ClearColorMode oldClearColorMode;
        [SerializeField] Color oldCameraBackground;
        [SerializeField] Color oldBackgroundColorHDR;
        [SerializeField] CameraClearFlags oldClearFlags;
        [SerializeField] bool oldCameraEnabled;
        [SerializeField] bool settingsSaved;
        [SerializeField] bool generateOnStart = true;

        const string MaterialResourcePath = "ViewWeave/HdrpLit";

        void Start()
        {
            if (generateOnStart)
                GenerateLitScene();
        }

        [ContextMenu("Generate Lit Scene")]
        public void GenerateLitScene()
        {
            if (!PrepareRig()) return;
            if (Shader.Find("HDRP/Lit") == null)
            {
                Debug.LogError("ViewWeave HDRP sample shader unavailable");
                return;
            }
            CleanupGenerated();
            generatedContent = new GameObject("ViewWeave HDRP Lit Sample Content");
            generatedContent.transform.SetPositionAndRotation(transform.position, transform.rotation);
            owned.Add(generatedContent);
            AddPrimitive(PrimitiveType.Cube, "Red cube", new Vector3(-0.65f, 0f, 2.4f), Color.red, new Vector3(0.8f, 0.8f, 0.8f));
            AddPrimitive(PrimitiveType.Sphere, "Green sphere", new Vector3(0.65f, 0.1f, 3.2f), Color.green, Vector3.one * 0.9f);
            AddLight();
            AddVolume();
        }

        bool PrepareRig()
        {
            var rig = GetComponent<ViewWeaveRig>();
            var session = GetComponent<ViewWeaveBridgeSession>();
            if (rig == null || session == null || !(GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset))
            {
                Debug.LogError("ViewWeave HDRP sample requires a Bridge Rig and the HDRP pipeline.");
                return false;
            }
            if (!rig.ValidateRig(out string rigError) || rig.Renderer == null)
            {
                Debug.LogError("ViewWeave HDRP sample rig is invalid: " + SanitizeAscii(rigError));
                return false;
            }
            rigCamera = GetComponent<Camera>();
            cameraData = rigCamera == null ? null : rigCamera.GetComponent<HDAdditionalCameraData>();
            if (cameraData == null) return false;
            if (!settingsSaved)
            {
                oldSettings = rig.Renderer.settings.Clone();
                oldVolumeMask = cameraData.volumeLayerMask;
                oldFov = rigCamera.fieldOfView;
                oldNear = rigCamera.nearClipPlane;
                oldFar = rigCamera.farClipPlane;
                oldClearColorMode = cameraData.clearColorMode;
                oldCameraBackground = rigCamera.backgroundColor;
                oldBackgroundColorHDR = cameraData.backgroundColorHDR;
                oldClearFlags = rigCamera.clearFlags;
                oldCameraEnabled = rigCamera.enabled;
                settingsSaved = true;
            }
            rig.Renderer.settings.cameraDistance = 3f;
            rig.Renderer.settings.fieldOfView = 50f;
            rigCamera.fieldOfView = 50f;
            rigCamera.nearClipPlane = 0.1f;
            rigCamera.farClipPlane = 100f;
            rigCamera.clearFlags = CameraClearFlags.SolidColor;
            rigCamera.backgroundColor = Color.black;
            rigCamera.enabled = false;
            cameraData.volumeLayerMask = 1 << VolumeLayer;
            cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
            cameraData.backgroundColorHDR = Color.black;
            return true;
        }

        void AddPrimitive(PrimitiveType type, string name, Vector3 position, Color color, Vector3 scale)
        {
            var template = Resources.Load<Material>(MaterialResourcePath);
            if (template == null)
            {
                Debug.LogError("ViewWeave HDRP sample material resource unavailable: " + MaterialResourcePath);
                return;
            }
            var item = GameObject.CreatePrimitive(type);
            item.name = name;
            item.transform.SetParent(generatedContent.transform, false);
            item.transform.localPosition = position;
            item.transform.localScale = scale;
            var material = new Material(template);
            material.color = color;
            item.GetComponent<Renderer>().sharedMaterial = material;
            owned.Add(item);
            owned.Add(material);
        }

        void AddLight()
        {
            var item = new GameObject("ViewWeave HDRP Sample Light");
            item.transform.SetParent(generatedContent.transform, false);
            var light = item.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 10f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
            var additional = item.AddComponent<HDAdditionalLightData>();
            light.lightUnit = UnityEngine.Rendering.LightUnit.Lux;
            light.intensity = 10f;
            owned.Add(item);
        }

        void AddVolume()
        {
            var item = new GameObject("ViewWeave HDRP Sample Volume");
            item.transform.SetParent(generatedContent.transform, false);
            item.layer = VolumeLayer;
            var volume = item.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var exposure = profile.Add<Exposure>(true);
            exposure.mode.value = ExposureMode.Fixed;
            exposure.mode.overrideState = true;
            exposure.fixedExposure.value = 4f;
            exposure.fixedExposure.overrideState = true;
            var environment = profile.Add<VisualEnvironment>(true);
            environment.skyType.value = 0;
            environment.skyType.overrideState = true;
            environment.cloudType.value = 0;
            environment.cloudType.overrideState = true;
            var indirectLighting = profile.Add<IndirectLightingController>(true);
            indirectLighting.indirectDiffuseLightingMultiplier.value = 0f;
            indirectLighting.indirectDiffuseLightingMultiplier.overrideState = true;
            var bloom = profile.Add<UnityEngine.Rendering.HighDefinition.Bloom>(true);
            bloom.intensity.value = 0f;
            bloom.intensity.overrideState = true;
            var tonemapping = profile.Add<UnityEngine.Rendering.HighDefinition.Tonemapping>(true);
            tonemapping.mode.value = TonemappingMode.Neutral;
            tonemapping.mode.overrideState = true;
            var motionBlur = profile.Add<MotionBlur>(true);
            motionBlur.active = true;
            motionBlur.intensity.value = 0f;
            motionBlur.intensity.overrideState = true;
            var reflection = profile.Add<ScreenSpaceReflection>(true);
            reflection.enabled.value = false;
            reflection.enabled.overrideState = true;
            var gi = profile.Add<GlobalIllumination>(true);
            gi.active = true;
            gi.enable.value = false;
            gi.enable.overrideState = true;
            var fog = profile.Add<Fog>(true);
            fog.active = true;
            fog.enabled.value = false;
            fog.enabled.overrideState = true;
            var clouds = profile.Add<VolumetricClouds>(true);
            clouds.active = true;
            clouds.enable.value = false;
            clouds.enable.overrideState = true;
            volume.sharedProfile = profile;
            owned.Add(item);
            owned.Add(profile);
        }

        void CleanupGenerated()
        {
            if (generatedContent != null)
            {
                generatedContent.SetActive(false);
                DestroyObject(generatedContent);
            }
            generatedContent = null;
            foreach (var item in owned)
            {
                if (item != null) DestroyObject(item);
            }
            owned.Clear();
        }

        void OnDestroy()
        {
            CleanupGenerated();
            RestoreSettings();
        }

        void OnDisable() { CleanupGenerated(); RestoreSettings(); }

        void RestoreSettings()
        {
            var rig = GetComponent<ViewWeaveRig>();
            if (settingsSaved && rigCamera != null && rig != null && rig.Renderer != null)
            {
                var settings = rig.Renderer.settings;
                settings.fieldOfView = oldSettings.fieldOfView;
                settings.cameraDistance = oldSettings.cameraDistance;
                cameraData.volumeLayerMask = oldVolumeMask;
                rigCamera.fieldOfView = oldFov;
                rigCamera.nearClipPlane = oldNear;
                rigCamera.farClipPlane = oldFar;
                rigCamera.clearFlags = oldClearFlags;
                rigCamera.backgroundColor = oldCameraBackground;
                cameraData.clearColorMode = oldClearColorMode;
                cameraData.backgroundColorHDR = oldBackgroundColorHDR;
                rigCamera.enabled = oldCameraEnabled;
                settingsSaved = false;
            }
        }

        static void DestroyObject(Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) Destroy(item);
            else DestroyImmediate(item);
        }

        static string SanitizeAscii(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
                if (chars[i] < 0x20 || chars[i] > 0x7e) chars[i] = '?';
            return new string(chars);
        }
    }
}


