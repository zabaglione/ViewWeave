using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BridgeRuntimeBuild
{
    public static void BuildWindows()
    {
        PlayerSettings.runInBackground = true;
        const string pluginPath = "Packages/com.zabaglione.viewweave.bridge/Plugins/x86_64/lookingglass_bridge_shim.dll";
        var importer = AssetImporter.GetAtPath(pluginPath) as PluginImporter;
        if (importer != null) { importer.SetCompatibleWithAnyPlatform(false); importer.SetCompatibleWithEditor(true); importer.SetPlatformData("Editor", "OS", "Windows"); importer.SetPlatformData("Editor", "CPU", "x86_64"); importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows, false); importer.SetCompatibleWithPlatform(BuildTarget.StandaloneWindows64, true); importer.SetCompatibleWithPlatform(BuildTarget.StandaloneLinux64, false); importer.SetCompatibleWithPlatform(BuildTarget.StandaloneOSX, false); importer.SetPlatformData(BuildTarget.StandaloneWindows64.ToString(), "CPU", "x86_64"); importer.SaveAndReimport(); }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var bootstrap = new GameObject("BridgeRuntimeDemo"); bootstrap.AddComponent<BridgeRuntimeDemo>();
        var demo = bootstrap.GetComponent<BridgeRuntimeDemo>();
        string materialPath = "Assets/BridgeRuntimeLit.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null) { material = new Material(Shader.Find("Standard")); AssetDatabase.CreateAsset(material, materialPath); AssetDatabase.SaveAssets(); }
        demo.litMaterial = material;
        EditorSceneManager.SaveScene(scene, "Assets/BridgeRuntimeSmoke.unity");
        string output = Path.GetFullPath("Builds/runtime-smoke/LookingGlassRuntimeSmoke.exe"); Directory.CreateDirectory(Path.GetDirectoryName(output));
        BuildReport report = BuildPipeline.BuildPlayer(new[] { scene.path }, output, BuildTarget.StandaloneWindows64, BuildOptions.Development);
        if (report.summary.result != BuildResult.Succeeded) throw new System.Exception("runtime build failed: " + report.summary.result);
        Debug.Log("BRIDGE_RUNTIME_BUILD_OK " + output);
    }
}
