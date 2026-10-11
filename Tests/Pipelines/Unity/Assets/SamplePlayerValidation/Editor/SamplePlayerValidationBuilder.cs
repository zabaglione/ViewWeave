#if UNITY_EDITOR
using System;
using System.IO;
using ViewWeave;
using ViewWeave.HDRP;
using ViewWeave.URP;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using ViewWeave.SamplePlayerValidation;
using ViewWeave.Samples.WindowsHdrpLit;
using ViewWeave.Samples.WindowsUrpLit;

namespace ViewWeave.SamplePlayerValidation.Editor
{
    public static class SamplePlayerValidationBuilder
    {
        const string GeneratedRoot = "Assets/SamplePlayerValidationGenerated";
        const string ScenePath = GeneratedRoot + "/SamplePlayerValidation.unity";

        public static void BuildUrp()
        {
            Build(false);
        }

        public static void BuildHdrp()
        {
            Build(true);
        }

        static void Build(bool hdrp)
        {
            var output = Argument("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            var graphicsApi = ParseGraphicsApi(Argument("-buildGraphicsApi") ?? "d3d11");
            bool development = ParseDevelopment(Argument("-buildDevelopment") ?? "true");
            var oldGraphics = GraphicsSettings.defaultRenderPipeline;
            var oldQuality = QualitySettings.renderPipeline;
            var oldColor = PlayerSettings.colorSpace;
            var oldMt = PlayerSettings.MTRendering;
            var oldJobs = PlayerSettings.graphicsJobs;
            var oldRunInBackground = PlayerSettings.runInBackground;
            var oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldScenes = EditorBuildSettings.scenes;
            var oldSetup = EditorSceneManager.GetSceneManagerSetup();
            Scene scene = default;
            RenderPipelineAsset pipeline = null;
            UnityEngine.Object pipelineData = null;
            try
            {
                Directory.CreateDirectory(GeneratedRoot);
                AssetDatabase.Refresh();
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                pipeline = hdrp ? CreateHdrpPipeline() : CreateUrpPipeline(out pipelineData);
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                var rigObject = CreateRig(hdrp, out var camera, out var quilt, out var outputComponent, out var session, out var runner);
                var serialized = new SerializedObject(runner);
                serialized.FindProperty("sourceCamera").objectReferenceValue = camera;
                serialized.FindProperty("quilt").objectReferenceValue = quilt;
                serialized.FindProperty("output").objectReferenceValue = outputComponent;
                serialized.FindProperty("session").objectReferenceValue = session;
                serialized.FindProperty("rig").objectReferenceValue = rigObject.GetComponent<ViewWeaveRig>();
                serialized.FindProperty("hdrp").boolValue = hdrp;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorSceneManager.SaveScene(scene, ScenePath, false);
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                PlayerSettings.MTRendering = true;
                PlayerSettings.graphicsJobs = false;
                PlayerSettings.runInBackground = true;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { graphicsApi });
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = Path.GetFullPath(output),
                    target = BuildTarget.StandaloneWindows64,
                    options = development ? BuildOptions.Development : BuildOptions.None
                });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Sample Player build failed: " + report.summary.result);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                PlayerSettings.colorSpace = oldColor;
                PlayerSettings.MTRendering = oldMt;
                PlayerSettings.graphicsJobs = oldJobs;
                PlayerSettings.runInBackground = oldRunInBackground;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, oldDefaultApis);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, oldApis);
                EditorBuildSettings.scenes = oldScenes;
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                if (oldSetup != null && oldSetup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(oldSetup);
            }
        }

        static GameObject CreateRig(bool hdrp, out Camera camera, out QuiltRenderer quilt, out BridgeWorkerQuiltOutput output, out ViewWeaveBridgeSession session, out SamplePlayerValidationRunner runner)
        {
            var rigObject = new GameObject(hdrp ? "ViewWeave HDRP Sample Player Rig" : "ViewWeave URP Sample Player Rig");
            camera = rigObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 50f;
            camera.nearClipPlane = .1f;
            camera.farClipPlane = 100f;
            quilt = rigObject.AddComponent<QuiltRenderer>();
            var rig = rigObject.AddComponent<ViewWeaveRig>();
            MonoBehaviour guard;
            MonoBehaviour generator;
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
            output = rigObject.AddComponent<BridgeWorkerQuiltOutput>();
            output.initializeOnEnable = false;
            session = rigObject.AddComponent<ViewWeaveBridgeSession>();
            session.startOnEnable = false;
            rigObject.SetActive(false);
            quilt.sourceCamera = camera;
            quilt.GuardBehaviour = guard;
            quilt.OutputBehaviour = output;
            quilt.autoRender = false;
            rig.SetGuard(guard);
            rig.SetOutput(output);
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("Sample Player rig configuration failed");
            runner = rigObject.AddComponent<SamplePlayerValidationRunner>();
            var generatorObject = new SerializedObject(generator);
            generatorObject.FindProperty("generateOnStart").boolValue = true;
            generatorObject.ApplyModifiedPropertiesWithoutUndo();
            rigObject.SetActive(true);
            return rigObject;
        }

        static UniversalRenderPipelineAsset CreateUrpPipeline(out UnityEngine.Object rendererData)
        {
            var data = ScriptableObject.CreateInstance<UniversalRendererData>();
            var asset = UniversalRenderPipelineAsset.Create(data);
            asset.name = "ViewWeave.SamplePlayer.URP";
            asset.useSRPBatcher = true;
            CreateAsset(data, GeneratedRoot + "/SamplePlayerValidation.URP.renderer.asset");
            CreateAsset(asset, GeneratedRoot + "/SamplePlayerValidation.URP.asset");
            rendererData = data;
            return asset;
        }

        static HDRenderPipelineAsset CreateHdrpPipeline()
        {
            var asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
            asset.name = "ViewWeave.SamplePlayer.HDRP";
            CreateAsset(asset, GeneratedRoot + "/SamplePlayerValidation.HDRP.asset");
            return asset;
        }

        static void CreateAsset(UnityEngine.Object value, string path)
        {
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(value, path);
            EditorUtility.SetDirty(value);
            AssetDatabase.SaveAssets();
        }

        static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            string prefix = key + "=";
            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i].StartsWith(prefix, StringComparison.Ordinal)) return args[i].Substring(prefix.Length);
                if (args[i] != key) continue;
                if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                    throw new ArgumentException(key + " requires a value");
                return args[i + 1];
            }
            return null;
        }

        static GraphicsDeviceType ParseGraphicsApi(string value)
        {
            switch (value.ToLowerInvariant())
            {
                case "d3d11": return GraphicsDeviceType.Direct3D11;
                case "d3d12": return GraphicsDeviceType.Direct3D12;
                default: throw new ArgumentException("-buildGraphicsApi must be d3d11 or d3d12");
            }
        }

        static bool ParseDevelopment(string value)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ArgumentException("-buildDevelopment must be true or false");
        }
    }
}
#endif
