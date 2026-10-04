using System;
using System.IO;
using System.Runtime.InteropServices;
using LookingGlass.Community;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class BridgeRuntimeDemo : MonoBehaviour
{
    public Material litMaterial;
    BridgeQuiltOutput output;
    GameObject content;
    Material red, green, white;
    QuiltRenderer quilt;
    bool readbackPending;
    int frameCount;
    string receiptPath;
    bool pngSaved;
    int frameLimit;
    bool closing;

    void Start()
    {
        Application.runInBackground = true;
        string path = Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
        if (string.IsNullOrWhiteSpace(path)) { Debug.LogError("BRIDGE_RUNTIME_ERROR LKG_BRIDGE_PATH is required"); return; }
        content = new GameObject("LookingGlassRuntimeDemo");
        output = content.AddComponent<BridgeQuiltOutput>();
        output.initializeOnEnable = false;
        output.bridgeLibraryPath = path;
        if (!output.TryInitialize()) { Debug.LogError("BRIDGE_RUNTIME_ERROR " + output.LastError); Application.Quit(1); return; }
        Camera camera = MakeCamera();
        Shader shader = litMaterial != null ? litMaterial.shader : Shader.Find("Standard");
        red = MakeMaterial(shader, Color.red); green = MakeMaterial(shader, Color.green); white = MakeMaterial(shader, Color.white);
        MakePrimitive(PrimitiveType.Cube, new Vector3(0, -0.7f, 0.2f), new Vector3(4f, 0.05f, 4f), white);
        MakePrimitive(PrimitiveType.Cube, new Vector3(-0.4f, 0, -0.5f), Vector3.one, red);
        MakePrimitive(PrimitiveType.Sphere, new Vector3(0.5f, 0, 0.7f), Vector3.one, green);
        MakeBar(new Vector3(0, 0.6f, 0), new Vector3(2f, 0.03f, 0.02f)); MakeBar(new Vector3(0, -0.6f, 0), new Vector3(2f, 0.03f, 0.02f));
        MakeBar(new Vector3(-1f, 0, 0), new Vector3(0.03f, 1.2f, 0.02f)); MakeBar(new Vector3(1f, 0, 0), new Vector3(0.03f, 1.2f, 0.02f));
        var lightObject = new GameObject("DemoLight"); lightObject.transform.SetParent(content.transform); var light = lightObject.AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.2f; light.shadows = LightShadows.Soft; light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
        RenderSettings.ambientLight = new Color(0.18f, 0.18f, 0.18f);
        quilt = content.AddComponent<QuiltRenderer>(); quilt.sourceCamera = camera; quilt.OutputBehaviour = output; quilt.renderInLateUpdate = true;
        float scale = 0.25f; float parsedScale; if (float.TryParse(Environment.GetEnvironmentVariable("LKG_STEREO_RENDER_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedScale)) scale = parsedScale; scale = Mathf.Clamp(scale, 0.05f, 1f);
        int tileWidth = Mathf.Max(1, Mathf.FloorToInt(output.Profile.QuiltWidth / output.Profile.Columns * scale)); int tileHeight = Mathf.Max(1, Mathf.FloorToInt(output.Profile.QuiltHeight / output.Profile.Rows * scale));
        quilt.settings.quiltWidth = tileWidth * output.Profile.Columns; quilt.settings.quiltHeight = tileHeight * output.Profile.Rows; quilt.settings.columns = output.Profile.Columns; quilt.settings.rows = output.Profile.Rows; quilt.settings.views = output.Profile.Columns * output.Profile.Rows; quilt.settings.viewAspect = output.Profile.Aspect; quilt.settings.viewCone = output.Profile.Viewcone; quilt.settings.cameraDistance = 4f; quilt.settings.fieldOfView = 30f;
        receiptPath = Environment.GetEnvironmentVariable("LKG_RUNTIME_RECEIPT"); if (string.IsNullOrWhiteSpace(receiptPath)) receiptPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", "..", ".local", "bridge-runtime-result.txt"));
        int.TryParse(Environment.GetEnvironmentVariable("LKG_RUNTIME_FRAME_LIMIT"), out frameLimit);
        Debug.Log("BRIDGE_RUNTIME_STARTED profile=" + output.Profile + " render=" + quilt.settings.quiltWidth + "x" + quilt.settings.quiltHeight);
    }

    void Update()
    {
        if (closing || quilt == null || quilt.QuiltTexture == null || readbackPending) return;
        readbackPending = true;
        AsyncGPUReadback.Request(quilt.QuiltTexture, 0, OnReadback);
    }

    void OnReadback(AsyncGPUReadbackRequest request)
    {
        readbackPending = false;
        if (request.hasError) return;
        frameCount++;
        uint draws = Native.Draws(); string error = Native.Error();
        if (frameCount == 1 || (frameCount % 10) == 0) File.WriteAllText(receiptPath, "profile=" + output.Profile + " render=" + quilt.settings.quiltWidth + "x" + quilt.settings.quiltHeight + " frames=" + frameCount + " draws=" + draws + " error=" + error);
        if (!pngSaved && draws > 0 && error.Length == 0) SaveQuiltPng();
        if (frameCount == 1 && draws > 0 && error.Length == 0) Debug.Log("BRIDGE_RUNTIME_FRAME_OK draws=" + draws + " error=" + error);
        else if (frameCount == 1) Debug.LogError("BRIDGE_RUNTIME_FRAME_FAIL draws=" + draws + " error=" + error);
        if (!closing && frameLimit > 0 && frameCount >= frameLimit && draws > 0 && error.Length == 0) StartCoroutine(CloseAfterFrame());
    }

    System.Collections.IEnumerator CloseAfterFrame()
    {
        closing = true;
        quilt.renderInLateUpdate = false;
        output.Dispose();
        float deadline = Time.realtimeSinceStartup + 5f;
        while (Native.Initialized() != 0 && Time.realtimeSinceStartup < deadline) yield return null;
        int initialized = Native.Initialized();
        File.AppendAllText(receiptPath, " close_initialized=" + initialized);
        if (initialized != 0) Debug.LogError("BRIDGE_RUNTIME_CLOSE_FAIL initialized=" + initialized);
        Application.Quit(initialized == 0 ? 0 : 1);
    }

    void SaveQuiltPng()
    {
        RenderTexture active = RenderTexture.active;
        Texture2D image = null;
        try
        {
            RenderTexture.active = quilt.QuiltTexture;
            image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, quilt.QuiltTexture.width, quilt.QuiltTexture.height), 0, 0);
            image.Apply();
            string path = Path.Combine(Path.GetDirectoryName(receiptPath), "bridge-runtime-quilt.png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            pngSaved = true;
        }
        finally { if (image != null) Destroy(image); RenderTexture.active = active; }
    }

    Camera MakeCamera() { var go = new GameObject("DemoCamera"); go.transform.SetParent(content.transform); go.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity); var camera = go.AddComponent<Camera>(); camera.enabled = false; camera.fieldOfView = 30f; camera.farClipPlane = 100f; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.black; return camera; }
    GameObject MakePrimitive(PrimitiveType type, Vector3 position, Vector3 scale, Material material) { var go = GameObject.CreatePrimitive(type); go.transform.SetParent(content.transform); go.transform.position = position; go.transform.localScale = scale; go.GetComponent<Renderer>().sharedMaterial = material; return go; }
    void MakeBar(Vector3 position, Vector3 scale) { MakePrimitive(PrimitiveType.Cube, position, scale, white); }
    Material MakeMaterial(Shader shader, Color color) { var material = new Material(shader) { color = color }; material.SetFloat("_Metallic", 0f); material.SetFloat("_Glossiness", 0.35f); return material; }
    void OnDestroy() { if (output != null) output.Dispose(); if (red != null) Destroy(red); if (green != null) Destroy(green); if (white != null) Destroy(white); if (content != null) Destroy(content); }

    static class Native
    {
        [DllImport("lookingglass_bridge_shim", CallingConvention = CallingConvention.Cdecl)] internal static extern uint lkg_bridge_successful_draw_count();
        [DllImport("lookingglass_bridge_shim", CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_last_error(IntPtr pointer, uint capacity);
        [DllImport("lookingglass_bridge_shim", CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_is_initialized();
        internal static uint Draws() { return lkg_bridge_successful_draw_count(); }
        internal static string Error() { int n = lkg_bridge_last_error(IntPtr.Zero, 0); if (n <= 0) return string.Empty; IntPtr p = Marshal.AllocHGlobal(n + 1); try { lkg_bridge_last_error(p, (uint)(n + 1)); return Marshal.PtrToStringAnsi(p) ?? string.Empty; } finally { Marshal.FreeHGlobal(p); } }
        internal static int Initialized() { return lkg_bridge_is_initialized(); }
    }
}
