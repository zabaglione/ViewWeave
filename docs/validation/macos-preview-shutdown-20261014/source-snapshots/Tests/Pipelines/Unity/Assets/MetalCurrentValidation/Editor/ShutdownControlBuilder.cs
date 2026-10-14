using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ViewWeave;

public static class ShutdownControlBuilder
{
    public static void Prepare()
    {
        MetalCurrentValidationBuilder.Prepare();
        string mode = MetalCurrentValidationBuilder.Argument("-controlMode");
        var rig = GameObject.Find("Quilt Rig").GetComponent<ViewWeaveRig>();
        UnityEngine.Object.DestroyImmediate(rig.GetComponent<MetalCurrentValidationRunner>());
        rig.SetOutput(null);
        UnityEngine.Object.DestroyImmediate(rig.GetComponent<BridgeMetalQuiltOutput>());
        var runner = new GameObject("Shutdown Control").AddComponent<ShutdownControlRunner>();
        runner.mode = mode;
        runner.pipelineName = MetalCurrentValidationBuilder.Argument("-validationPipeline") ?? "URP";
        runner.reportDirectory = MetalCurrentValidationBuilder.Argument("-validationRoot");
        runner.sdkPath = MetalCurrentValidationBuilder.Argument("-validationSdkPath");
        if (mode == "QuiltOnly")
        {
            rig.ApplyProfile(new QuiltQualityProfile(1023, 1020, 11, 6, 66, 0.5625f, 54f));
            if (!rig.ConfigureComponents()) throw new InvalidOperationException("Control Rig configuration failed.");
            runner.quilt = rig.Renderer;
            runner.quilt.autoRender = false;
        }
        else if (mode == "MetadataOnly") UnityEngine.Object.DestroyImmediate(rig.gameObject);
        else throw new InvalidOperationException("Unknown shutdown control mode.");
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(),
            "Assets/SamplePlayerValidationGenerated/MetalCurrent/Validation.unity");
        AssetDatabase.SaveAssets();
    }
}
