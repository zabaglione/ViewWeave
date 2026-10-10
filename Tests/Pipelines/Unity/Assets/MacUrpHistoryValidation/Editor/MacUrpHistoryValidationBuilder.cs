using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave.URP;

namespace ViewWeave.MacUrpHistoryValidation.Editor
{
    public static class MacUrpHistoryValidationBuilder
    {
        const string Generated = "Assets/SamplePlayerValidationGenerated/MacUrpHistory";
        public static void Build()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch Editor for this fixture");
            string output = Argument("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            var oldGraphics = GraphicsSettings.defaultRenderPipeline; var oldQuality = QualitySettings.renderPipeline;
            var oldColor = PlayerSettings.colorSpace; var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
            bool oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX);
            var oldScenes = EditorBuildSettings.scenes; var oldSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Directory.CreateDirectory(Generated); AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                rendererData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                AssetDatabase.CreateAsset(rendererData, UniquePath("Renderer.asset"));
                var pipeline = UniversalRenderPipelineAsset.Create(rendererData); pipeline.msaaSampleCount = 1; pipeline.renderScale = 1f;
                AssetDatabase.CreateAsset(pipeline, UniquePath("Pipeline.asset"));
                GraphicsSettings.defaultRenderPipeline = pipeline; QualitySettings.renderPipeline = pipeline;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
                var reference = new GameObject("Reference Camera").AddComponent<Camera>();
                reference.clearFlags = CameraClearFlags.SolidColor; reference.backgroundColor = Color.black;
                reference.allowHDR = false; reference.allowMSAA = false; reference.nearClipPlane = .1f; reference.farClipPlane = 10f;
                reference.enabled = false;
                var referenceData = reference.gameObject.AddComponent<UniversalAdditionalCameraData>();
                referenceData.renderPostProcessing = true; referenceData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
                var source = new GameObject("Quilt Camera").AddComponent<Camera>(); source.CopyFrom(reference); source.enabled = false;
                source.gameObject.AddComponent<UniversalAdditionalCameraData>();
                var guard = source.gameObject.AddComponent<UrpQuiltCamera>();
                var quilt = source.gameObject.AddComponent<QuiltRenderer>(); quilt.sourceCamera = source; quilt.referenceCamera = reference;
                quilt.GuardBehaviour = guard; quilt.autoRender = false;
                quilt.settings.quiltWidth = 384; quilt.settings.quiltHeight = 256;
                quilt.settings.columns = quilt.settings.rows = 2; quilt.settings.views = 4;
                quilt.settings.viewAspect = 1.5f; quilt.settings.cameraDistance = 3f; quilt.settings.fieldOfView = 60f; quilt.settings.viewCone = 25f;
                var bar = GameObject.CreatePrimitive(PrimitiveType.Cube); bar.name = "Aliasing Bar";
                bar.transform.position = new Vector3(0f, 0f, 3f); bar.transform.rotation = Quaternion.Euler(0f, 0f, 23f);
                bar.transform.localScale = new Vector3(1.35f, .14f, .08f);
                var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { color = Color.green };
                AssetDatabase.CreateAsset(material, UniquePath("Bar.mat")); bar.GetComponent<Renderer>().sharedMaterial = material;
                var runner = source.gameObject.AddComponent<MacUrpHistoryValidationRunner>(); runner.quilt = quilt; runner.guard = guard; runner.bar = bar.transform;
                string scenePath = Generated + "/Validation.unity"; EditorSceneManager.SaveScene(scene, scenePath); AssetDatabase.SaveAssets();
                bool development = Argument("-validationConfiguration") == "Development";
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = Path.GetFullPath(output), target = BuildTarget.StandaloneOSX, options = development ? BuildOptions.Development : BuildOptions.None });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("macOS URP Player build failed: " + report.summary.result);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldGraphics; QualitySettings.renderPipeline = oldQuality;
                PlayerSettings.colorSpace = oldColor; PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, oldApis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, oldDefaultApis);
                EditorBuildSettings.scenes = oldScenes;
                if (oldSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
                AssetDatabase.SaveAssets();
            }
        }
        static string UniquePath(string name)
        {
            string path = Generated + "/" + name;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            return path;
        }
        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
