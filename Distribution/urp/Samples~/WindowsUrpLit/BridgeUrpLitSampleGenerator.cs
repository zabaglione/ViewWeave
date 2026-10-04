using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave;

namespace ViewWeave.Samples.WindowsUrpLit
{
    public sealed class BridgeUrpLitSampleGenerator : MonoBehaviour
    {
        [SerializeField] GameObject generatedContent;
        [SerializeField] List<Object> owned = new List<Object>();
        [SerializeField] Camera rigCamera;
        [SerializeField] QuiltSettings oldSettings;
        [SerializeField] float oldFov, oldNear, oldFar;
        [SerializeField] bool settingsSaved;
        [SerializeField] bool generateOnStart = true;

        const string MaterialResourcePath = "ViewWeave/UrpLit";

        void Start()
        {
            if (generateOnStart)
                GenerateLitScene();
        }

        [ContextMenu("Generate Lit Scene")]
        public void GenerateLitScene()
        {
            if (!PrepareRig()) return;
            if (Shader.Find("Universal Render Pipeline/Lit") == null)
            {
                Debug.LogError("ViewWeave URP sample shader unavailable");
                return;
            }
            CleanupGenerated();
            generatedContent = new GameObject("ViewWeave URP Lit Sample Content");
            generatedContent.transform.SetPositionAndRotation(transform.position, transform.rotation);
            owned.Add(generatedContent);
            AddPrimitive(PrimitiveType.Cube, "Red cube", new Vector3(-0.65f, 0f, 2.4f), Color.red, new Vector3(0.8f, 0.8f, 0.8f));
            AddPrimitive(PrimitiveType.Sphere, "Green sphere", new Vector3(0.65f, 0.1f, 3.2f), Color.green, Vector3.one * 0.9f);
            AddLight();
        }

        bool PrepareRig()
        {
            var rig = GetComponent<ViewWeaveRig>();
            var session = GetComponent<ViewWeaveBridgeSession>();
            if (rig == null || session == null || !(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            {
                Debug.LogError("ViewWeave URP sample requires a Bridge Rig and the URP pipeline.");
                return false;
            }
            if (!rig.ValidateRig(out string rigError) || rig.Renderer == null)
            {
                Debug.LogError("ViewWeave URP sample rig is invalid: " + SanitizeAscii(rigError));
                return false;
            }
            rigCamera = GetComponent<Camera>();
            if (rigCamera == null) return false;
            if (!settingsSaved)
            {
                oldSettings = rig.Renderer.settings.Clone();
                oldFov = rigCamera.fieldOfView;
                oldNear = rigCamera.nearClipPlane;
                oldFar = rigCamera.farClipPlane;
                settingsSaved = true;
            }
            rig.Renderer.settings.cameraDistance = 3f;
            rig.Renderer.settings.fieldOfView = 50f;
            rigCamera.fieldOfView = 50f;
            rigCamera.nearClipPlane = 0.1f;
            rigCamera.farClipPlane = 100f;
            rigCamera.enabled = false;
            return true;
        }

        void AddPrimitive(PrimitiveType type, string name, Vector3 position, Color color, Vector3 scale)
        {
            var template = Resources.Load<Material>(MaterialResourcePath);
            if (template == null)
            {
                Debug.LogError("ViewWeave URP sample material resource unavailable: " + MaterialResourcePath);
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
            var item = new GameObject("ViewWeave URP Sample Light");
            item.transform.SetParent(generatedContent.transform, false);
            var light = item.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.5f;
            light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
            owned.Add(item);
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
                rigCamera.fieldOfView = oldFov;
                rigCamera.nearClipPlane = oldNear;
                rigCamera.farClipPlane = oldFar;
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



