using System;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using ViewWeave;

public static class QuiltCaptureTests
{
    [MenuItem("Tools/ViewWeave/Run Quilt Capture GPU Smoke")]
    public static void RunBuiltinCaptureSmoke()
    {
        GameObject root = null;
        RenderTexture sentinel = null;
        Texture2D pattern = null;
        RenderTexture previousActive = RenderTexture.active;
        CultureInfo previousCulture = Thread.CurrentThread.CurrentCulture;
        string outputDirectory = Path.Combine(Application.dataPath, "..", "TestResults", "QuiltCaptureSmoke_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(outputDirectory);
            root = new GameObject("QuiltCaptureSmoke");
            Camera camera = root.AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.transform.position = new Vector3(0f, 0f, -3f);
            Vector3 position = camera.transform.position;
            QuiltRenderer renderer = root.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.quiltWidth = 64;
            renderer.settings.quiltHeight = 48;
            renderer.settings.columns = 2;
            renderer.settings.rows = 2;
            renderer.settings.views = 4;
            renderer.settings.viewAspect = 0.75f;
            renderer.settings.cameraDistance = 3f;
            renderer.settings.viewCone = 20f;

            int completed = 0;
            bool restoredAtNotification = false;
            Action<QuiltRenderer> onComplete = value =>
            {
                completed++;
                restoredAtNotification = !value.IsRendering && value.sourceCamera.transform.position == position && !value.sourceCamera.enabled;
            };
            renderer.QuiltRendered += onComplete;
            renderer.RenderQuilt();
            renderer.QuiltRendered -= onComplete;
            Require(completed == 1 && restoredAtNotification, "completion notification follows render restoration");
            Require(renderer.HasRenderedQuilt && renderer.IsLatestQuiltCurrent, "completed render snapshot is current");

            sentinel = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
            sentinel.Create();
            pattern = CreateCornerPattern(renderer.QuiltTexture.width, renderer.QuiltTexture.height);
            RenderTexture.active = sentinel;
            Graphics.Blit(pattern, renderer.QuiltTexture);
            RenderTexture.active = sentinel;
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Require(QuiltCaptureUtility.BuildFileName("capture", renderer.LatestSnapshot) == "capture_qs2x2a0.75.png", "filename suffix is invariant and uses layout aspect");
            string path = Path.Combine(outputDirectory, QuiltCaptureUtility.BuildFileName("capture", renderer.LatestSnapshot));
            QuiltCaptureUtility.SaveLatest(renderer, path);
            Require(RenderTexture.active == sentinel, "PNG readback restores the previous active render texture");
            Require(File.Exists(path) && File.Exists(Path.ChangeExtension(path, ".json")), "PNG and JSON sidecar were written");
            CheckPng(path, renderer.QuiltTexture.width, renderer.QuiltTexture.height);
            QuiltCaptureMetadata metadata = JsonUtility.FromJson<QuiltCaptureMetadata>(File.ReadAllText(Path.ChangeExtension(path, ".json")));
            Require(metadata.width == 64 && metadata.height == 48, "metadata records exact quilt dimensions");
            Require(metadata.columns == 2 && metadata.rows == 2 && metadata.views == 4, "metadata records grid and view count");
            Require(metadata.viewRenderMode == "Full" && metadata.lastRenderedViewCount == 4, "metadata records actual render density");
            Require(metadata.usedRect.width == 64 && metadata.usedRect.height == 48, "metadata records used tile bounds");
            Require(metadata.geometry.viewAspect == 0.75f && metadata.geometry.nearClip == camera.nearClipPlane, "metadata records capture geometry");
            bool rejectedExisting = false;
            try { QuiltCaptureUtility.SaveLatest(renderer, path); }
            catch (IOException) { rejectedExisting = true; }
            Require(rejectedExisting, "existing PNG and sidecar are not overwritten");
            Require(RenderTexture.active == sentinel, "failed save preserves active render texture");

            Texture2D centralPattern = CreateTilePattern(renderer.QuiltTexture.width, renderer.QuiltTexture.height, 2, 2);
            try
            {
                Graphics.Blit(centralPattern, renderer.QuiltTexture);
                RenderTexture.active = sentinel;
                string centralPath = Path.Combine(outputDirectory, "capture_central.png");
                QuiltCaptureUtility.SaveCentralViewLatest(renderer, centralPath);
                Require(RenderTexture.active == sentinel, "central view readback restores the previous active render texture");
                CheckCentralPng(centralPath, 32, 24);
                QuiltCentralViewCaptureMetadata centralMetadata = JsonUtility.FromJson<QuiltCentralViewCaptureMetadata>(File.ReadAllText(Path.ChangeExtension(centralPath, ".json")));
                Require(centralMetadata.logicalViewIndex == 1 && centralMetadata.renderedViewIndex == 1, "central metadata identifies the selected logical and rendered views");
                Require(centralMetadata.width == 32 && centralMetadata.height == 24, "central metadata records the cropped image dimensions");
                Require(centralMetadata.quilt.width == 64 && centralMetadata.quilt.views == 4, "central metadata retains source quilt metadata");
                bool rejectedCentralOverwrite = false;
                try { QuiltCaptureUtility.SaveCentralViewLatest(renderer, centralPath); }
                catch (IOException) { rejectedCentralOverwrite = true; }
                Require(rejectedCentralOverwrite, "existing central PNG and sidecar are not overwritten");
                Require(RenderTexture.active == sentinel, "failed central save preserves active render texture");

                bool rejectedInvalidSnapshot = false;
                try { QuiltCaptureUtility.SaveCentralView(renderer.QuiltTexture, default(QuiltRenderSnapshot), Path.Combine(outputDirectory, "invalid.png")); }
                catch (ArgumentException) { rejectedInvalidSnapshot = true; }
                Require(rejectedInvalidSnapshot, "central view save rejects an invalid snapshot");
                Require(!File.Exists(Path.Combine(outputDirectory, "invalid.png")), "invalid central snapshot creates no output");
            }
            finally { UnityEngine.Object.DestroyImmediate(centralPattern); }
            Debug.Log("ViewWeave Quilt capture GPU smoke passed");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previousCulture;
            RenderTexture.active = previousActive;
            if (pattern != null) UnityEngine.Object.DestroyImmediate(pattern);
            if (sentinel != null)
            {
                sentinel.Release();
                UnityEngine.Object.DestroyImmediate(sentinel);
            }
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
        }
    }

    static Texture2D CreateCornerPattern(int width, int height)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        Color32[] corners = { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 0, 255) };
        Color32[] pixels = new Color32[width * height];
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                bool top = y >= height / 2;
                bool right = x >= width / 2;
                pixels[y * width + x] = corners[(top ? 2 : 0) + (right ? 1 : 0)];
            }
        for (int y = height / 2 - 2; y < height / 2 + 2; ++y)
            for (int x = width / 2 - 2; x < width / 2 + 2; ++x)
                pixels[y * width + x] = new Color32(118, 118, 118, 255);
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    static Texture2D CreateTilePattern(int width, int height, int columns, int rows)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        int tileWidth = width / columns;
        int tileHeight = height / rows;
        Color32[] tileColors = { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 0, 255) };
        Color32[] pixels = new Color32[width * height];
        for (int y = 0; y < height; ++y)
            for (int x = 0; x < width; ++x)
            {
                int view = (y / tileHeight) * columns + (x / tileWidth);
                int localX = x % tileWidth;
                int localY = y % tileHeight;
                Color32 color = tileColors[view];
                if (localX < 2 && localY < 2) color = new Color32(255, 255, 255, 255);
                else if (localX >= tileWidth - 2 && localY < 2) color = new Color32(0, 0, 0, 255);
                else if (localX < 2 && localY >= tileHeight - 2) color = new Color32(128, 128, 128, 255);
                else if (localX >= tileWidth - 2 && localY >= tileHeight - 2) color = new Color32(255, 0, 255, 255);
                pixels[y * width + x] = color;
            }
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return texture;
    }

    static void CheckCentralPng(string path, int width, int height)
    {
        Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            Require(ImageConversion.LoadImage(decoded, File.ReadAllBytes(path), false), "central PNG decoded");
            Require(decoded.width == width && decoded.height == height, "central PNG is cropped to one tile");
            CheckColor(decoded.GetPixel(width / 2, height / 2), new Color32(0, 255, 0, 255), "central logical tile is selected");
            CheckColor(decoded.GetPixel(1, 1), new Color32(255, 255, 255, 255), "central crop keeps bottom-left orientation");
            CheckColor(decoded.GetPixel(width - 2, 1), new Color32(0, 0, 0, 255), "central crop keeps bottom-right orientation");
            CheckColor(decoded.GetPixel(1, height - 2), new Color32(128, 128, 128, 255), "central crop keeps top-left orientation");
            CheckColor(decoded.GetPixel(width - 2, height - 2), new Color32(255, 0, 255, 255), "central crop keeps top-right orientation");
        }
        finally { UnityEngine.Object.DestroyImmediate(decoded); }
    }

    static void CheckPng(string path, int width, int height)
    {
        Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        try
        {
            Require(ImageConversion.LoadImage(decoded, File.ReadAllBytes(path), false), "PNG decoded");
            Require(decoded.width == width && decoded.height == height, "PNG dimensions are exact");
            CheckColor(decoded.GetPixel(width / 2, height / 2), new Color32(118, 118, 118, 255), "midrange color is retained");
            CheckColor(decoded.GetPixel(1, 1), new Color32(255, 0, 0, 255), "bottom-left orientation");
            CheckColor(decoded.GetPixel(width - 2, 1), new Color32(0, 255, 0, 255), "bottom-right orientation");
            CheckColor(decoded.GetPixel(1, height - 2), new Color32(0, 0, 255, 255), "top-left orientation");
            CheckColor(decoded.GetPixel(width - 2, height - 2), new Color32(255, 255, 0, 255), "top-right orientation");
        }
        finally { UnityEngine.Object.DestroyImmediate(decoded); }
    }

    static void CheckColor(Color actual, Color32 expected, string label)
    {
        Require(Mathf.Abs(actual.r * 255f - expected.r) <= 2f, label + " red");
        Require(Mathf.Abs(actual.g * 255f - expected.g) <= 2f, label + " green");
        Require(Mathf.Abs(actual.b * 255f - expected.b) <= 2f, label + " blue");
        Require(Mathf.Abs(actual.a * 255f - expected.a) <= 2f, label + " alpha");
    }

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
    }
}
