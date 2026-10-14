using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

// Copied to an isolated project with no ViewWeave packages or runtime assembly.
public sealed class UnityShutdownBaselineRunner : MonoBehaviour
{
    public string reportDirectory, pipelineName;
    int completed;
    [Serializable] public sealed class Result
    {
        public bool passed, editor, development;
        public string error, pipeline, actualPipeline, shaderTag, unityVersion, graphicsApi, gpu;
        public int frames;
    }
    void OnEnable() { RenderPipelineManager.endCameraRendering += Rendered; }
    void OnDisable() { RenderPipelineManager.endCameraRendering -= Rendered; }
    void Rendered(ScriptableRenderContext context, Camera camera)
    { if (camera == GetComponent<Camera>()) ++completed; }
    IEnumerator Start()
    {
        double deadline = Time.realtimeSinceStartupAsDouble + 60d;
        while (completed < 60 && Time.realtimeSinceStartupAsDouble < deadline) yield return null;
        var result = new Result
        {
            pipeline = pipelineName, actualPipeline = RenderPipelineManager.currentPipeline?.GetType().Name,
            shaderTag = Shader.globalRenderPipeline, unityVersion = Application.unityVersion,
            graphicsApi = SystemInfo.graphicsDeviceType.ToString(), gpu = SystemInfo.graphicsDeviceName,
            editor = Application.isEditor, development = Debug.isDebugBuild, frames = completed
        };
        result.passed = !result.editor && completed >= 60 && result.graphicsApi == "Metal" &&
            result.actualPipeline == (pipelineName == "HDRP" ? "HDRenderPipeline" : "UniversalRenderPipeline") &&
            result.shaderTag == (pipelineName == "HDRP" ? "HDRenderPipeline" : "UniversalPipeline");
        result.error = result.passed ? string.Empty : "Baseline pipeline or camera-render gate failed.";
        Directory.CreateDirectory(reportDirectory);
        File.WriteAllText(Path.Combine(reportDirectory, "runtime.json"), JsonUtility.ToJson(result, true));
        Debug.Log("Unity-only shutdown baseline " + (result.passed ? "PASS" : "FAIL"));
        // Keep the process alive long enough to inspect loaded native images.
        yield return new WaitForSecondsRealtime(5f);
        Application.Quit(result.passed ? 0 : 1);
    }
}
