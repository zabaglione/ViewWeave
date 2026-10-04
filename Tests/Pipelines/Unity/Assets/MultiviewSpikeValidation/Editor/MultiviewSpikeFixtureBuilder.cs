using System;
using System.IO;
using LookingGlass.Community;
using LookingGlass.MultiviewSpikeValidation;
using LookingGlass.Community.URP;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LookingGlass.MultiviewSpikeValidation.Editor
{
    public static class MultiviewSpikeFixtureBuilder
    {
        const string Generated = "Assets/MultiviewSpikeValidationGenerated";
        const string ScenePath = Generated + "/MultiviewSpikeFixture.unity";

        public static void Build()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "MultiviewSpikeValidationGenerated"));
            DeleteOwnedAsset(Generated + "/MultiviewSpike.renderer.asset");
            DeleteOwnedAsset(Generated + "/MultiviewSpike.asset");
            DeleteOwnedAsset(Generated + "/MultiviewSpike.atlas.renderTexture");
            DeleteOwnedAsset(Generated + "/MultiviewSpike.GreenLit.mat");
            DeleteOwnedAsset(Generated + "/MultiviewSpike.RedLit.mat");
            RenderPipelineAsset oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQualityPipeline = QualitySettings.renderPipeline;
            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            UniversalRenderPipelineAsset pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            pipeline.name = "MultiviewSpike.URP";
            RenderTexture atlas = new RenderTexture(512, 192, 0, RenderTextureFormat.ARGB32) { name = "MultiviewSpike.Atlas" };
            AssetDatabase.CreateAsset(rendererData, Generated + "/MultiviewSpike.renderer.asset");
            AssetDatabase.CreateAsset(pipeline, Generated + "/MultiviewSpike.asset");
            AssetDatabase.CreateAsset(atlas, Generated + "/MultiviewSpike.atlas.renderTexture");
            MultiviewOpaqueLitFeature feature = ScriptableObject.CreateInstance<MultiviewOpaqueLitFeature>();
            feature.atlasTarget = atlas;
            rendererData.rendererFeatures.Add(feature);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            EditorUtility.SetDirty(rendererData);
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = new GameObject("MultiviewSpike.Root");
            GameObject cameraObject = new GameObject("MultiviewSpike.Camera");
            cameraObject.transform.SetParent(root.transform, false);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 50f;
            camera.nearClipPlane = .01f;
            camera.farClipPlane = 100f;
            camera.cullingMask = 1 << 31;
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            cameraObject.AddComponent<UniversalAdditionalCameraData>();
            QuiltRenderer quilt = cameraObject.AddComponent<QuiltRenderer>();
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = cameraObject.AddComponent<UrpQuiltCamera>();
            MultiviewSpikeValidationRunner runner = cameraObject.AddComponent<MultiviewSpikeValidationRunner>();
            GameObject lightObject = new GameObject("MultiviewSpike.Directional");
            lightObject.transform.SetParent(root.transform, false);
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 2f;
            light.shadows = LightShadows.None;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable");
            Material material = new Material(shader) { name = "MultiviewSpike.GreenLit" };
            material.SetColor("_BaseColor", Color.green);
            AssetDatabase.CreateAsset(material, Generated + "/MultiviewSpike.GreenLit.mat");
            Material edgeMaterial = new Material(shader) { name = "MultiviewSpike.RedLit" };
            edgeMaterial.SetColor("_BaseColor", Color.red);
            AssetDatabase.CreateAsset(edgeMaterial, Generated + "/MultiviewSpike.RedLit.mat");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform, false);
            cube.transform.localPosition = new Vector3(0f, 0f, 2.2f);
            cube.layer = 31;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            GameObject edgeCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            edgeCube.transform.SetParent(root.transform, false);
            edgeCube.transform.localPosition = new Vector3(1.49f, 0f, 2.2f);
            edgeCube.transform.localScale = Vector3.one * .14f;
            edgeCube.layer = 31;
            Renderer edgeRenderer = edgeCube.GetComponent<Renderer>();
            edgeRenderer.sharedMaterial = edgeMaterial;
            SerializedObject serializedRunner = new SerializedObject(runner);
            serializedRunner.FindProperty("sourceCamera").objectReferenceValue = camera;
            serializedRunner.FindProperty("quilt").objectReferenceValue = quilt;
            serializedRunner.FindProperty("feature").objectReferenceValue = feature;
            serializedRunner.FindProperty("atlasTarget").objectReferenceValue = atlas;
            serializedRunner.FindProperty("keyLight").objectReferenceValue = light;
            serializedRunner.FindProperty("edgeRenderer").objectReferenceValue = edgeRenderer;
            serializedRunner.ApplyModifiedPropertiesWithoutUndo();
            try
            {
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                EditorSceneManager.SaveScene(scene, ScenePath, false);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldDefaultPipeline;
                QualitySettings.renderPipeline = oldQualityPipeline;
            }
        }

        public static void BuildPlayer()
        {
            string outputPath = Argument("-buildOutput");
            if (string.IsNullOrEmpty(outputPath)) outputPath = Environment.GetEnvironmentVariable("LKG_MULTIVIEW_BUILD_PATH");
            if (string.IsNullOrEmpty(outputPath)) throw new InvalidOperationException("-buildOutput or LKG_MULTIVIEW_BUILD_PATH is required");
            BuildTarget target = BuildTarget.StandaloneWindows64;
            SceneSetup[] oldSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            bool canRestoreSceneSetup = HasValidSceneSetup(oldSceneSetup);
            RenderPipelineAsset oldDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset oldQualityPipeline = QualitySettings.renderPipeline;
            bool oldMultithreadedRendering = PlayerSettings.MTRendering;
            bool oldGraphicsJobs = PlayerSettings.graphicsJobs;
            bool oldRunInBackground = PlayerSettings.runInBackground;
            bool oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            GraphicsDeviceType[] oldApis = PlayerSettings.GetGraphicsAPIs(target);
            try
            {
                Build();
                RenderPipelineAsset pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(Generated + "/MultiviewSpike.asset");
                if (pipeline == null) throw new InvalidOperationException("multiview pipeline asset is unavailable");
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                PlayerSettings.MTRendering = true;
                PlayerSettings.graphicsJobs = false;
                PlayerSettings.runInBackground = true;
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = outputPath,
                    target = target,
                    options = BuildOptions.Development
                });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("multiview player build failed");
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldDefaultPipeline;
                QualitySettings.renderPipeline = oldQualityPipeline;
                PlayerSettings.MTRendering = oldMultithreadedRendering;
                PlayerSettings.graphicsJobs = oldGraphicsJobs;
                PlayerSettings.runInBackground = oldRunInBackground;
                PlayerSettings.SetGraphicsAPIs(target, oldApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, oldDefaultApis);
                if (canRestoreSceneSetup) EditorSceneManager.RestoreSceneManagerSetup(oldSceneSetup);
                else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        static bool HasValidSceneSetup(SceneSetup[] setup)
        {
            if (setup == null || setup.Length == 0) return false;
            for (int i = 0; i < setup.Length; ++i) if (setup[i].isLoaded && !string.IsNullOrEmpty(setup[i].path)) return true;
            return false;
        }

        static void DeleteOwnedAsset(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null) AssetDatabase.DeleteAsset(path);
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
