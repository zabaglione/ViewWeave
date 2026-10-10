using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ViewWeave;

[InitializeOnLoad]
public static class MetalEditorLifecycleValidation
{
    const string Prefix = "ViewWeave.MetalLifecycle.";
    static MetalEditorLifecycleValidation() { EditorApplication.update += Tick; }

    [MenuItem("Tools/ViewWeave/Metal Validation/Run Editor Lifecycle")]
    public static void Begin()
    {
        EditorSceneManager.OpenScene("Assets/ViewWeaveMetalValidation/ViewWeaveMetalValidation.unity");
        MacOSMetalValidationBuilder.Prepare();
        SessionState.SetInt(Prefix + "cycle", 0);
        SetPhase(1);
    }
    static void SetPhase(int phase)
    {
        SessionState.SetInt(Prefix + "phase", phase);
        SessionState.SetFloat(Prefix + "deadline", (float)EditorApplication.timeSinceStartup + 60f);
    }
    static void Tick()
    {
        int phase = SessionState.GetInt(Prefix + "phase", 0);
        if (phase == 0) return;
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(Prefix + "deadline", 0))
                throw new Exception("Metal Editor lifecycle timed out at phase " + phase);
            int cycle = SessionState.GetInt(Prefix + "cycle", 0);
            if (phase == 1)
            {
                SetPhase(2); EditorApplication.EnterPlaymode();
            }
            else if (phase == 2 && EditorApplication.isPlaying && !EditorApplication.isCompiling)
            {
                BridgeMetalQuiltOutput output = UnityEngine.Object.FindAnyObjectByType<BridgeMetalQuiltOutput>();
                if (output == null) throw new Exception("Metal Editor output component was not found");
                if (!string.IsNullOrEmpty(output.LastError)) throw new Exception(output.LastError);
                if (output.CompletedFrames < 120) return;
                Debug.Log("Metal Editor Play passed: cycle=" + cycle + " submitted=" + output.SubmittedFrames + " completed=" + output.CompletedFrames);
                if (cycle == 0)
                {
                    output.StopOutput();
                    if (BridgeMetalQuiltOutput.PendingRetirementCount == 0) throw new Exception("GPU retirement gate fixture did not retain a pending record");
                    var probe = new GameObject("Metal retirement admission probe");
                    try
                    {
                        BridgeMetalQuiltOutput other = probe.AddComponent<BridgeMetalQuiltOutput>();
                        if (other.StartOutput() || !other.LastError.Contains("retained GPU resources"))
                            throw new Exception("Another component bypassed the process-wide GPU retirement gate");
                        Debug.Log("Metal process-wide retirement admission passed");
                    }
                    finally { UnityEngine.Object.DestroyImmediate(probe); }
                }
                SetPhase(3); EditorApplication.ExitPlaymode();
            }
            else if (phase == 3 && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
            {
                if (BridgeMetalQuiltOutput.PendingRetirementCount != 0) return;
                Debug.Log("Metal Editor Stop passed: cycle=" + cycle + " retained=0");
                if (cycle == 0) { SessionState.SetInt(Prefix + "cycle", 1); SetPhase(1); }
                else { SetPhase(0); Debug.Log("Metal Editor lifecycle passed: two Play/Stop cycles with Domain Reload and retained=0"); }
            }
        }
        catch (Exception exception)
        {
            SetPhase(0); Debug.LogException(exception);
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }
    }
}
