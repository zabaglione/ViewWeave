using UnityEngine;
using ViewWeave;

/// <summary>Exercises a successful Bridge acquisition before the Editor Play identity registry is bound.</summary>
public sealed class BridgeGeometryAwakeProbe : MonoBehaviour
{
    public bool startedSuccessfully;
    public string error;

    void Awake()
    {
        var output = GetComponent<BridgeMetalQuiltOutput>();
        startedSuccessfully = output.StartOutput();
        error = output.LastError;
        output.StopOutput();
    }
}
