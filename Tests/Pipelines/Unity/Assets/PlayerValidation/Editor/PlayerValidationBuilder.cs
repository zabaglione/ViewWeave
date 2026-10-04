using System;
using System.IO;
using ViewWeave;
using ViewWeave.URP;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace ViewWeave.PlayerValidation.Editor
{
    public static class PlayerValidationBuilder
    {
        const string Generated = "Assets/PlayerValidationGenerated";
        const string ScenePath = Generated + "/PlayerValidation.unity";

        public static void Build()
        {
            var output = Arg("-buildOutput");
            if (string.IsNullOrEmpty(output)) throw new InvalidOperationException("-buildOutput is required");
            var oldSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            var oldBuildScenes = EditorBuildSettings.scenes;
            var oldGraphics = GraphicsSettings.defaultRenderPipeline;
            var oldQuality = QualitySettings.renderPipeline;
            var scene = default(Scene);
            var restoreSceneSetup = HasLoadedScene(oldSceneSetup);
            try
            {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "PlayerValidationGenerated"));
            AssetDatabase.Refresh();
                var sceneMode = restoreSceneSetup ? NewSceneMode.Additive : NewSceneMode.Single;
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, sceneMode);
            EditorSceneManager.SetActiveScene(scene);
            var off = Pipeline("URP.PlayerValidation.Off", GPUResidentDrawerMode.Disabled);
            var on = Pipeline("URP.PlayerValidation.On", GPUResidentDrawerMode.InstancedDrawing);
            var red = Material("PlayerValidation.Lit", new Color(.8f, .05f, .05f, 1f));
            var green = Material("PlayerValidation.GreenLit", Color.green);
            var root = new GameObject("PlayerValidation.Root");
            var lightObject = new GameObject("PlayerValidation.DirectionalLight"); lightObject.transform.SetParent(root.transform, false); var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 2f; light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
            var cameraObject = new GameObject("PlayerValidation.QuiltCamera"); cameraObject.transform.SetParent(root.transform, false); cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 1.1f, -6f), Quaternion.identity);
            var source = cameraObject.AddComponent<Camera>(); source.enabled = false; source.useOcclusionCulling = false; source.clearFlags = CameraClearFlags.SolidColor; source.backgroundColor = Color.black; cameraObject.AddComponent<UniversalAdditionalCameraData>();
            var guard = cameraObject.AddComponent<UrpQuiltCamera>(); var quilt = cameraObject.AddComponent<QuiltRenderer>(); quilt.sourceCamera = source; quilt.GuardBehaviour = guard; quilt.autoRender = false; var outputComponent = cameraObject.AddComponent<PlayerValidationOutput>(); quilt.OutputBehaviour = outputComponent; var runner = cameraObject.AddComponent<PlayerValidationRunner>();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = "PlayerValidation.CubeTemplate"; cube.transform.SetParent(root.transform, false); cube.GetComponent<Renderer>().sharedMaterial = red; var mesh = cube.GetComponent<MeshFilter>().sharedMesh;
            for (var i = 0; i < 72; ++i) { var item = new GameObject("PlayerValidation.Cube." + i); item.transform.SetParent(root.transform, false); item.transform.localPosition = new Vector3((i % 12 - 5.5f) * .32f, (i / 12 - 2.5f) * .32f, 1.2f + (i % 3) * .1f); item.transform.localScale = Vector3.one * .22f; item.AddComponent<MeshFilter>().sharedMesh = mesh; item.AddComponent<MeshRenderer>().sharedMaterial = red; }
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere); sphere.name = "PlayerValidation.DynamicSphere"; sphere.transform.SetParent(root.transform, false); sphere.transform.localPosition = new Vector3(-.6f, .5f, -.5f); sphere.GetComponent<Renderer>().sharedMaterial = green;
            var plane = GameObject.CreatePrimitive(PrimitiveType.Plane); plane.name = "PlayerValidation.Plane"; plane.transform.SetParent(root.transform, false); plane.transform.localPosition = new Vector3(0f, -1f, 1f); plane.transform.localScale = Vector3.one * 3f; plane.GetComponent<Renderer>().sharedMaterial = red;
            var so = new SerializedObject(runner); Ref(so, "grdOffPipeline", off); Ref(so, "grdOnPipeline", on); Ref(so, "quilt", quilt); Ref(so, "sourceCamera", source); Ref(so, "dynamicSphere", sphere.transform); Ref(so, "output", outputComponent); so.ApplyModifiedPropertiesWithoutUndo(); GraphicsSettings.defaultRenderPipeline = off; QualitySettings.renderPipeline = off; EditorSceneManager.SaveScene(scene, ScenePath, false);
            GraphicsSettings.defaultRenderPipeline = on;
            QualitySettings.renderPipeline = on;
            BuildPlayer(output, oldGraphics, oldQuality);
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                EditorBuildSettings.scenes = oldBuildScenes;
                if (scene.IsValid() && scene.isLoaded)
                {
                    try
                    {
                        EditorSceneManager.CloseScene(scene, true);
                    }
                    catch (Exception error)
                    {
                        Debug.LogError("Player validation generated scene close failed: " + error.GetType().Name);
                    }
                }
                if (restoreSceneSetup)
                {
                    try
                    {
                        EditorSceneManager.RestoreSceneManagerSetup(oldSceneSetup);
                    }
                    catch (Exception error)
                    {
                        Debug.LogError("Player validation scene restore failed: " + error.GetType().Name);
                    }
                }
            }
        }

        static bool HasLoadedScene(SceneSetup[] setup)
        {
            if (setup == null) return false;
            for (var i = 0; i < setup.Length; ++i)
                if (setup[i].isLoaded) return true;
            return false;
        }

        static void BuildPlayer(string output, RenderPipelineAsset oldGraphics, RenderPipelineAsset oldQuality)
        {
            var oldScenes = EditorBuildSettings.scenes;
            var oldTarget = EditorUserBuildSettings.activeBuildTarget;
            var oldMt = PlayerSettings.MTRendering;
            var oldJobs = PlayerSettings.graphicsJobs;
            var oldStatic = PlayerSettings.GetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64);
            var oldRun = Application.runInBackground;
            var oldPlayerRun = PlayerSettings.runInBackground;
            var oldFrameTimingStats = PlayerSettings.enableFrameTimingStats;
            var oldDefaultApis = PlayerSettings.GetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var oldApis = PlayerSettings.GetGraphicsAPIs(BuildTarget.StandaloneWindows64);
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            var gs = assets.Length == 0 ? null : new SerializedObject(assets[0]);
            var brg = gs == null ? null : gs.FindProperty("m_BrgStripping");
            if (brg == null) throw new InvalidOperationException("GraphicsSettings.m_BrgStripping is unavailable");
            var oldBrg = brg.intValue;
            try
            {
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64);
                PlayerSettings.MTRendering = true;
                PlayerSettings.graphicsJobs = false;
                Application.runInBackground = true;
                PlayerSettings.runInBackground = true;
                PlayerSettings.enableFrameTimingStats = true;
                PlayerSettings.SetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11 });
                brg.intValue = (int)BatchRendererGroupStrippingMode.KeepAll;
                gs.ApplyModifiedPropertiesWithoutUndo();
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { ScenePath }, locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development });
                if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("player build failed: " + report.summary.result);
            }
            finally
            {
                brg.intValue = oldBrg;
                gs.ApplyModifiedPropertiesWithoutUndo();
                GraphicsSettings.defaultRenderPipeline = oldGraphics;
                QualitySettings.renderPipeline = oldQuality;
                EditorBuildSettings.scenes = oldScenes;
                PlayerSettings.MTRendering = oldMt;
                PlayerSettings.graphicsJobs = oldJobs;
                Application.runInBackground = oldRun;
                PlayerSettings.runInBackground = oldPlayerRun;
                PlayerSettings.enableFrameTimingStats = oldFrameTimingStats;
                PlayerSettings.SetStaticBatchingForPlatform(BuildTarget.StandaloneWindows64, oldStatic);
                PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, oldDefaultApis);
                PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, oldApis);
                if (oldTarget != BuildTarget.NoTarget) EditorUserBuildSettings.SwitchActiveBuildTarget(BuildPipeline.GetBuildTargetGroup(oldTarget), oldTarget);
            }
        }

        static UniversalRenderPipelineAsset Pipeline(string name, GPUResidentDrawerMode mode) { var data = ScriptableObject.CreateInstance<UniversalRendererData>(); data.renderingMode = RenderingMode.ForwardPlus; var asset = UniversalRenderPipelineAsset.Create(data); asset.name = name; asset.gpuResidentDrawerMode = mode; asset.gpuResidentDrawerEnableOcclusionCullingInCameras = false; asset.smallMeshScreenPercentage = 0f; asset.useSRPBatcher = true; CreateOrReplaceAsset(data, Generated + "/" + name + ".renderer.asset"); CreateOrReplaceAsset(asset, Generated + "/" + name + ".asset"); return asset; }
        static Material Material(string name, Color color) { var shader = Shader.Find("Universal Render Pipeline/Lit"); if (shader == null) throw new InvalidOperationException("URP Lit shader unavailable"); var material = new Material(shader) { name = name }; material.SetColor("_BaseColor", color); material.SetFloat("_Smoothness", .25f); CreateOrReplaceAsset(material, Generated + "/" + name + ".mat"); return material; }
        static void CreateOrReplaceAsset(UnityEngine.Object asset, string path) { if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path); AssetDatabase.CreateAsset(asset, path); }
        static void Ref(SerializedObject so, string name, UnityEngine.Object value) { so.FindProperty(name).objectReferenceValue = value; }
        static string Arg(string key) { var args = Environment.GetCommandLineArgs(); for (var i = 0; i < args.Length - 1; ++i) if (args[i] == key) return args[i + 1]; return null; }
    }
}
