using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using ViewWeave;

public sealed class MacOSMetalValidationHarness : MonoBehaviour
{
    public Transform rotatingObject;
    public BridgeMetalQuiltOutput output;
    public float reportIntervalSeconds = 5f;

    float nextReportTime;
    string reportPath;
    bool pendingRestart;
    bool pendingQuit;

    void Start()
    {
        Application.runInBackground = true;
        reportPath = Path.Combine(Application.persistentDataPath, "viewweave-metal-validation.json");
        nextReportTime = Time.unscaledTime;
        Debug.Log("Metal validation harness started; report=" + Ascii(reportPath));
        WriteReport();
    }

    void Update()
    {
        if (rotatingObject != null)
            rotatingObject.Rotate(13f * Time.unscaledDeltaTime, 27f * Time.unscaledDeltaTime,
                19f * Time.unscaledDeltaTime, Space.World);

        if (Input.GetKeyDown(KeyCode.R))
        {
            if (output != null)
            {
                output.StopOutput();
                pendingRestart = true;
            }
            WriteReport();
        }

        if (pendingRestart && output != null && !output.IsClosing)
        {
            pendingRestart = false;
            if (!output.StartOutput())
            {
                if (output.IsClosing) pendingRestart = true;
                else Debug.Log("Metal output restart failed: " + Ascii(output.LastError));
            }
            else Debug.Log("Metal output restarted");
            WriteReport();
        }

        if (pendingQuit && (output == null || !output.IsClosing))
        {
            pendingQuit = false;
            WriteReport();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
            return;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (output != null) output.StopOutput();
            pendingRestart = false;
            pendingQuit = true;
            WriteReport();
        }

        if (Time.unscaledTime >= nextReportTime)
        {
            nextReportTime = Time.unscaledTime + Mathf.Max(1f, reportIntervalSeconds);
            WriteReport();
        }
    }

    void OnApplicationQuit()
    {
        if (output != null) output.StopOutput();
        WriteReport();
    }

    void WriteReport()
    {
        string json = BuildJson();
        Debug.Log("METAL_VALIDATION_REPORT " + json);
        if (string.IsNullOrEmpty(reportPath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath));
            File.WriteAllText(reportPath, json, new UTF8Encoding(false));
        }
        catch (Exception exception)
        {
            Debug.Log("Metal validation report write failed: " + Ascii(exception.Message));
        }
    }

    string BuildJson()
    {
        BridgeDisplayProfile profile = output != null ? output.Profile : default;
        string lastError = output != null ? output.LastError : "Output component is missing";
        var json = new StringBuilder(512);
        json.Append('{');
        json.Append("\"utc\":\"").Append(Escape(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))).Append('\"');
        json.Append(",\"platform\":\"").Append(Escape(Application.platform.ToString())).Append('\"');
        json.Append(",\"unityVersion\":\"").Append(Escape(Application.unityVersion)).Append('\"');
        json.Append(",\"graphicsDeviceType\":\"").Append(Escape(SystemInfo.graphicsDeviceType.ToString())).Append('\"');
        json.Append(",\"graphicsDeviceName\":\"").Append(Escape(SystemInfo.graphicsDeviceName)).Append('\"');
        json.Append(",\"profile\":{");
        json.Append("\"valid\":").Append(profile.IsValid ? "true" : "false");
        json.Append(",\"displayIndex\":").Append(profile.DisplayIndex.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"width\":").Append(profile.Width.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"height\":").Append(profile.Height.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"quiltWidth\":").Append(profile.QuiltWidth.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"quiltHeight\":").Append(profile.QuiltHeight.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"columns\":").Append(profile.Columns.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"rows\":").Append(profile.Rows.ToString(CultureInfo.InvariantCulture));
        json.Append(",\"aspect\":").Append(profile.Aspect.ToString("R", CultureInfo.InvariantCulture));
        json.Append(",\"viewcone\":").Append(profile.Viewcone.ToString("R", CultureInfo.InvariantCulture));
        json.Append('}');
        json.Append(",\"isRunning\":").Append(output != null && output.IsRunning ? "true" : "false");
        json.Append(",\"isClosing\":").Append(output != null && output.IsClosing ? "true" : "false");
        json.Append(",\"submittedFrames\":").Append(output != null ? output.SubmittedFrames.ToString(CultureInfo.InvariantCulture) : "0");
        json.Append(",\"completedFrames\":").Append(output != null ? output.CompletedFrames.ToString(CultureInfo.InvariantCulture) : "0");
        json.Append(",\"droppedFrames\":").Append(output != null ? output.DroppedFrames.ToString(CultureInfo.InvariantCulture) : "0");
        json.Append(",\"lastError\":\"").Append(Escape(lastError)).Append("\"}");
        return json.ToString();
    }

    static string Escape(string value)
    {
        string ascii = Ascii(value);
        return ascii.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    static string Ascii(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = new StringBuilder(value.Length);
        foreach (char character in value)
            result.Append(character >= 32 && character <= 126 ? character : '?');
        return result.ToString();
    }
}
