using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Rendering;

namespace ViewWeave.Samples
{
    public sealed class QuiltPreviewDemoGenerator : MonoBehaviour
    {
        [SerializeField] QuiltQualityPreset preset = QuiltQualityPreset.Preview;
        [SerializeField] int nativeQuiltWidth = 1920;
        [SerializeField] int nativeQuiltHeight = 1080;
        [SerializeField] int columns = 8;
        [SerializeField] int rows = 6;
        [SerializeField] float aspect = 1f;
        [SerializeField] float viewCone = 40f;
        [SerializeField] Material generatedMaterial;
        [SerializeField] List<GameObject> generatedObjects = new List<GameObject>();
        GameObject generatedContentRoot;

        [ContextMenu("Generate Preview Rig")]
        public void Generate()
        {
            ViewWeaveRig rig = GetComponent<ViewWeaveRig>();
            if (rig == null) { Debug.LogError("QuiltPreviewDemoGenerator requires a ViewWeaveRig created by the ViewWeave menu."); return; }
            string pipelineName = GraphicsSettings.currentRenderPipeline == null ? string.Empty : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            if (pipelineName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset") { Debug.LogError("QuiltPreviewDemoGenerator supports URP only; configure a URP project before generating the sample."); return; }
            CleanupGenerated();
            Camera camera = GetComponent<Camera>();
            QuiltQualityProfile profile = preset == QuiltQualityPreset.Native
                ? QuiltQualityProfile.Native(nativeQuiltWidth, nativeQuiltHeight, columns, rows, aspect, viewCone)
                : preset == QuiltQualityPreset.Balanced
                    ? QuiltQualityProfile.Balanced(nativeQuiltWidth, nativeQuiltHeight, columns, rows, aspect, viewCone)
                    : QuiltQualityProfile.Preview(nativeQuiltWidth, nativeQuiltHeight, columns, rows, aspect, viewCone);
            rig.ApplyProfile(profile);
            rig.Renderer.settings.fieldOfView = 50f;
            rig.Renderer.settings.cameraDistance = 3f;

            Material template = Resources.Load<Material>("ViewWeave/CoreUrpPreviewLit");
            if (template == null) { Debug.LogError("Core URP preview material resource is missing; import the QuiltPreview sample and configure URP."); return; }
            generatedContentRoot = new GameObject("Quilt Preview Generated Content");
            generatedContentRoot.transform.SetPositionAndRotation(transform.position, transform.rotation);
            generatedMaterial = new Material(template) { name = "Preview Lit Material" };
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Preview Lit Cube";
            cube.transform.SetParent(generatedContentRoot.transform, false);
            cube.transform.localPosition = new Vector3(0f, 0f, 3f);
            Renderer cubeRenderer = cube.GetComponent<Renderer>();
            cubeRenderer.sharedMaterial = generatedMaterial;
            generatedObjects.Add(cube);
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "Preview Lit Sphere";
            sphere.transform.SetParent(generatedContentRoot.transform, false);
            sphere.transform.localPosition = new Vector3(0.8f, 0.2f, 4f);
            sphere.GetComponent<Renderer>().sharedMaterial = generatedMaterial;
            generatedObjects.Add(sphere);
            GameObject lightObject = new GameObject("Preview Directional Light");
            lightObject.transform.SetParent(generatedContentRoot.transform, false);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
            generatedObjects.Add(lightObject);
            camera.fieldOfView = 50f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.gray;
        }

        void OnDestroy() => CleanupGenerated();

        void CleanupGenerated()
        {
            for (int i = generatedObjects.Count - 1; i >= 0; --i)
                if (generatedObjects[i] != null) { if (Application.isPlaying) Destroy(generatedObjects[i]); else DestroyImmediate(generatedObjects[i]); }
            generatedObjects.Clear();
            if (generatedContentRoot != null) { if (Application.isPlaying) Destroy(generatedContentRoot); else DestroyImmediate(generatedContentRoot); generatedContentRoot = null; }
            if (generatedMaterial != null) { if (Application.isPlaying) Destroy(generatedMaterial); else DestroyImmediate(generatedMaterial); generatedMaterial = null; }
        }
    }
}
