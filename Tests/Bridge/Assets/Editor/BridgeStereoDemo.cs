using System;
using System.IO;
using System.Runtime.InteropServices;
using ViewWeave;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Rendering;

[InitializeOnLoad]
public static class BridgeStereoDemo
{
    static GameObject root;
    static BridgeQuiltOutput output;
    static QuiltRenderer renderer;
    static Material redMat, greenMat, whiteMat;
    static double nextTick;
    static bool running, firstFrame, verificationPending;
    static bool controlOnly;
    static string receiptDirectory;
    static AsyncGPUReadbackRequest pendingReadback;
    static int renderWidth, renderHeight;
    static Color previousAmbient;
    static AmbientMode previousAmbientMode;

    static BridgeStereoDemo()
    {
        AssemblyReloadEvents.beforeAssemblyReload += StopLive;
        EditorApplication.quitting += StopLive;
        if (Environment.GetEnvironmentVariable("LKG_START_STEREO_DEMO") == "1") EditorApplication.delayCall += StartLive;
    }

    public static void StartLive()
    {
        if (running) return;
        try
        {
            string path = Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
            if (string.IsNullOrWhiteSpace(path)) throw new Exception("LKG_BRIDGE_PATH is required");
            root = new GameObject("ViewWeaveStereoDemo");
            output = root.AddComponent<BridgeQuiltOutput>();
            output.initializeOnEnable = false;
            output.bridgeLibraryPath = path;
            if (!output.TryInitialize()) throw new Exception(output.LastError);

            var cameraObject = new GameObject("DemoCamera");
            cameraObject.transform.SetPositionAndRotation(new Vector3(0, 0, -4), Quaternion.identity);
            cameraObject.transform.SetParent(root.transform);
            var camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 30f;
            camera.farClipPlane = 100f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

            Shader lit = Shader.Find("Standard");
            redMat = MakeMaterial(lit, Color.red);
            greenMat = MakeMaterial(lit, Color.green);
            whiteMat = MakeMaterial(lit, Color.white);
            var lightObject = new GameObject("DemoLight");
            lightObject.transform.SetParent(root.transform);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
            previousAmbient = RenderSettings.ambientLight;
            previousAmbientMode = RenderSettings.ambientMode;
            RenderSettings.ambientLight = new Color(0.18f, 0.18f, 0.18f);

            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.SetParent(root.transform);
            floor.transform.position = new Vector3(0, -0.7f, 0.2f);
            floor.transform.localScale = new Vector3(4f, 0.05f, 4f);
            floor.GetComponent<Renderer>().sharedMaterial = whiteMat;
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(root.transform);
            cube.transform.position = new Vector3(-0.4f, 0, -0.5f);
            cube.GetComponent<Renderer>().sharedMaterial = redMat;
            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.transform.SetParent(root.transform);
            sphere.transform.position = new Vector3(0.5f, 0, 0.7f);
            sphere.GetComponent<Renderer>().sharedMaterial = greenMat;
            MakeBar(new Vector3(0, 0.6f, 0), new Vector3(2f, 0.03f, 0.02f));
            MakeBar(new Vector3(0, -0.6f, 0), new Vector3(2f, 0.03f, 0.02f));
            MakeBar(new Vector3(-1f, 0, 0), new Vector3(0.03f, 1.2f, 0.02f));
            MakeBar(new Vector3(1f, 0, 0), new Vector3(0.03f, 1.2f, 0.02f));

            renderer = root.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.OutputBehaviour = output;
            controlOnly = Environment.GetEnvironmentVariable("LKG_STEREO_CONTROL_ONLY") == "1";
            if (controlOnly) renderer.OutputBehaviour = null;
            float scale = 0.25f;
            string scaleText = Environment.GetEnvironmentVariable("LKG_STEREO_RENDER_SCALE");
            if (!string.IsNullOrWhiteSpace(scaleText)) float.TryParse(scaleText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale);
            scale = Mathf.Clamp(scale, 0.05f, 1f);
            int tileWidth = Mathf.Max(1, Mathf.FloorToInt(output.Profile.QuiltWidth / output.Profile.Columns * scale));
            int tileHeight = Mathf.Max(1, Mathf.FloorToInt(output.Profile.QuiltHeight / output.Profile.Rows * scale));
            renderWidth = tileWidth * output.Profile.Columns;
            renderHeight = tileHeight * output.Profile.Rows;
            renderer.settings.quiltWidth = renderWidth;
            renderer.settings.quiltHeight = renderHeight;
            renderer.settings.columns = output.Profile.Columns;
            renderer.settings.rows = output.Profile.Rows;
            renderer.settings.views = output.Profile.Columns * output.Profile.Rows;
            renderer.settings.viewAspect = output.Profile.Aspect;
            renderer.settings.viewCone = output.Profile.Viewcone;
            renderer.settings.cameraDistance = 4f;
            renderer.settings.fieldOfView = 30f;
            receiptDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", ".local"));
            Directory.CreateDirectory(receiptDirectory);
            File.WriteAllText(Path.Combine(receiptDirectory, "bridge-profile.txt"), output.Profile + " render=" + renderWidth + "x" + renderHeight + " scale=" + scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            running = true;
            EditorApplication.update += InitialTick;
        }
        catch (Exception e)
        {
            Debug.LogError("BRIDGE_LIVE_ERROR " + e.Message);
            StopLive();
        }
    }

    static Material MakeMaterial(Shader shader, Color color)
    {
        var material = new Material(shader) { color = color };
        material.SetFloat("_Metallic", 0f);
        material.SetFloat("_Glossiness", 0.35f);
        return material;
    }

    static GameObject MakeBar(Vector3 position, Vector3 scale)
    {
        var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bar.transform.SetParent(root.transform);
        bar.transform.position = position;
        bar.transform.localScale = scale;
        bar.GetComponent<Renderer>().sharedMaterial = whiteMat;
        return bar;
    }

    static void InitialTick()
    {
        if (!running || Camera.current != null || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        EditorApplication.update -= InitialTick;
        try
        {
            Debug.Log("BRIDGE_LIVE_STAGE render_begin");
            renderer.RenderQuilt();
            Debug.Log("BRIDGE_LIVE_STAGE render_end");
            BeginVerification();
            nextTick = EditorApplication.timeSinceStartup + 0.2;
            EditorApplication.update += Tick;
        }
        catch (Exception e) { Debug.LogError("BRIDGE_LIVE_ERROR " + e.Message); StopLive(); }
    }

    static void Tick()
    {
        if (!running || renderer == null || verificationPending || Camera.current != null || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.timeSinceStartup < nextTick) return;
        try
        {
            Debug.Log("BRIDGE_LIVE_STAGE render_begin");
            renderer.RenderQuilt();
            Debug.Log("BRIDGE_LIVE_STAGE render_end");
            BeginVerification();
            nextTick = EditorApplication.timeSinceStartup + 0.2;
        }
        catch (Exception e) { Debug.LogError("BRIDGE_LIVE_ERROR " + e.Message); StopLive(); }
    }

    static void BeginVerification()
    {
        if (verificationPending) return;
        verificationPending = true;
        pendingReadback = AsyncGPUReadback.Request(renderer.QuiltTexture, 0, OnGpuReady);
        EditorApplication.update += VerifyNative;
        Debug.Log("BRIDGE_LIVE_STAGE gpu_wait_begin");
    }

    static void OnGpuReady(AsyncGPUReadbackRequest request)
    {
        if (request.hasError) Debug.LogError("BRIDGE_LIVE_ERROR gpu_readback_failed");
        else Debug.Log("BRIDGE_LIVE_STAGE gpu_wait_end");
    }

    static void VerifyNative()
    {
        if (!verificationPending || !pendingReadback.done) return;
        EditorApplication.update -= VerifyNative;
        verificationPending = false;
        uint draws = NativeDraws();
        string error = NativeError();
        if (!controlOnly && (draws == 0 || error.Length != 0)) throw new Exception("native draw failed draws=" + draws + " error=" + error);
        if (!firstFrame)
        {
            SaveQuiltPng();
            firstFrame = true;
            Debug.Log("BRIDGE_LIVE_FRAME_OK draws=" + draws + " control=" + controlOnly + " profile=" + output.Profile + " render=" + renderWidth + "x" + renderHeight);
        }
    }

    static void SaveQuiltPng()
    {
        RenderTexture active = RenderTexture.active;
        Texture2D image = null;
        try
        {
            RenderTexture.active = renderer.QuiltTexture;
            image = new Texture2D(renderer.QuiltTexture.width, renderer.QuiltTexture.height, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, renderer.QuiltTexture.width, renderer.QuiltTexture.height), 0, 0);
            image.Apply();
            File.WriteAllBytes(Path.Combine(receiptDirectory, "bridge-quilt.png"), image.EncodeToPNG());
        }
        finally
        {
            if (image != null) UnityEngine.Object.DestroyImmediate(image);
            RenderTexture.active = active;
        }
    }

    public static void StopLive()
    {
        EditorApplication.update -= InitialTick;
        EditorApplication.update -= Tick;
        EditorApplication.update -= VerifyNative;
        if (output != null) output.Dispose();
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
        if (redMat != null) UnityEngine.Object.DestroyImmediate(redMat);
        if (greenMat != null) UnityEngine.Object.DestroyImmediate(greenMat);
        if (whiteMat != null) UnityEngine.Object.DestroyImmediate(whiteMat);
        RenderSettings.ambientLight = previousAmbient;
        RenderSettings.ambientMode = previousAmbientMode;
        output = null;
        renderer = null;
        root = null;
        running = false;
        firstFrame = false;
        controlOnly = false;
        verificationPending = false;
    }

    static uint NativeDraws() { return Native.success(); }

    static string NativeError()
    {
        int length = Native.error(IntPtr.Zero, 0);
        if (length <= 0) return string.Empty;
        IntPtr pointer = Marshal.AllocHGlobal(length + 1);
        try
        {
            Native.error(pointer, (uint)(length + 1));
            return Marshal.PtrToStringAnsi(pointer) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    static class Native
    {
        [DllImport("lookingglass_bridge_shim", CallingConvention = CallingConvention.Cdecl)] internal static extern uint lkg_bridge_successful_draw_count();
        [DllImport("lookingglass_bridge_shim", CallingConvention = CallingConvention.Cdecl)] internal static extern int lkg_bridge_last_error(IntPtr pointer, uint capacity);
        internal static uint success() { return lkg_bridge_successful_draw_count(); }
        internal static int error(IntPtr pointer, uint capacity) { return lkg_bridge_last_error(pointer, capacity); }
    }
}
