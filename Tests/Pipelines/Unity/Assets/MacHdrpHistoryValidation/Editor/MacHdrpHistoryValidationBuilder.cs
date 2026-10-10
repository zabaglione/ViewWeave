using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave.HDRP;

namespace ViewWeave.MacHdrpHistoryValidation.Editor
{
    public static class MacHdrpHistoryValidationBuilder
    {
        const string Generated = "Assets/SamplePlayerValidationGenerated/MacHdrpHistory";
        public static void Build()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Use an isolated batch Editor for this fixture");
            string output = Argument("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            bool development = Argument("-validationConfiguration") == "Development";
            var oldGraphics = GraphicsSettings.defaultRenderPipeline;
            var oldQuality = QualitySettings.renderPipeline;
            var oldColor = PlayerSettings.colorSpace;
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneOSX);
            bool oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX);
            var oldScenes = EditorBuildSettings.scenes;
            var oldSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Directory.CreateDirectory(Generated);
                AssetDatabase.Refresh();
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var pipeline = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                AssetDatabase.CreateAsset(pipeline, UniquePath("Pipeline.asset"));
                GraphicsSettings.defaultRenderPipeline = pipeline;
                QualitySettings.renderPipeline = pipeline;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, new[] { GraphicsDeviceType.Metal });
                var referenceObject = new GameObject("Reference Camera");
                var reference = referenceObject.AddComponent<Camera>();
                reference.clearFlags = CameraClearFlags.SolidColor;
                reference.backgroundColor = Color.black;
                reference.allowHDR = true;
                var referenceData = referenceObject.AddComponent<HDAdditionalCameraData>();
                referenceData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                referenceData.backgroundColorHDR = Color.black;
                referenceData.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
                referenceData.hasPersistentHistory = true;
                var sourceObject = new GameObject("Quilt Camera");
                var source = sourceObject.AddComponent<Camera>();
                source.CopyFrom(reference);
                source.enabled = false;
                sourceObject.AddComponent<HDAdditionalCameraData>();
                var guard = sourceObject.AddComponent<HdrpQuiltCamera>();
                var quilt = sourceObject.AddComponent<QuiltRenderer>();
                quilt.sourceCamera = source;
                quilt.referenceCamera = reference;
                quilt.GuardBehaviour = guard;
                quilt.autoRender = false;
                quilt.settings.quiltWidth = 256;
                quilt.settings.quiltHeight = 192;
                quilt.settings.columns = quilt.settings.rows = 2;
                quilt.settings.views = 4;
                quilt.settings.viewAspect = 1f;
                quilt.settings.cameraDistance = 3f;
                quilt.settings.fieldOfView = 50f;
                quilt.settings.viewCone = 30f;
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.position = new Vector3(0f, 0f, 3f);
                // Cover most of the metering image: a mostly black histogram is a poor
                // fixture for asserting a large EV change from only one small object.
                cube.transform.localScale = Vector3.one * 2.5f;
                var material = new Material(Shader.Find("HDRP/Lit"));
                material.SetColor("_BaseColor", Color.red);
                AssetDatabase.CreateAsset(material, UniquePath("Cube.mat"));
                cube.GetComponent<Renderer>().sharedMaterial = material;
                var lightObject = new GameObject("Fixture Light");
                lightObject.transform.rotation = Quaternion.Euler(30f, -25f, 0f);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 10f;
                lightObject.AddComponent<HDAdditionalLightData>();
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                var exposure = profile.Add<Exposure>(true);
                exposure.mode.Override(ExposureMode.Fixed);
                exposure.fixedExposure.Override(8.5f);
                exposure.compensation.Override(2f);
                exposure.limitMin.Override(-10f);
                exposure.limitMax.Override(20f);
                exposure.adaptationMode.Override(AdaptationMode.Fixed);
                string profilePath = UniquePath("Volume.asset");
                AssetDatabase.CreateAsset(profile, profilePath);
                foreach (var component in profile.components) AssetDatabase.AddObjectToAsset(component, profile);
                var volume = new GameObject("Fixture Volume").AddComponent<Volume>();
                volume.isGlobal = true;
                volume.sharedProfile = profile;
                var runner = sourceObject.AddComponent<MacHdrpHistoryValidationRunner>();
                runner.quilt = quilt;
                runner.guard = guard;
                runner.volume = volume;
                runner.fixtureLight = light;
                string scenePath = Generated + "/Validation.unity";
                EditorSceneManager.SaveScene(scene, scenePath);
                AssetDatabase.SaveAssets();
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scenePath }, locationPathName = Path.GetFullPath(output), target = BuildTarget.StandaloneOSX, options = development ? BuildOptions.Development : BuildOptions.None });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("macOS HDRP Player build failed: " + report.summary.result);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                PlayerSettings.colorSpace = oldColor;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX, oldApis);
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
            for (int i = 0; i < args.Length - 1; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
