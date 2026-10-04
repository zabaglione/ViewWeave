using System;
using System.IO;
using LookingGlass.Community;
using UnityEditor;
using UnityEngine;

public static class QuiltDemoTests
{
    [MenuItem("Tools/Looking Glass Community/Create Demo")]
    public static void CreateDemo()
    {
        GameObject root = CreateDemoRoot();
        Selection.activeGameObject = root;
    }

    public static void RunBuiltinSmoke()
    {
        GameObject root = null;
        try
        {
            root = CreateDemoRoot();
            QuiltRenderer renderer = root.GetComponent<QuiltRenderer>();
            Camera camera = root.GetComponent<Camera>();
            Vector3 position = camera.transform.position;
            Quaternion rotation = camera.transform.rotation;
            RenderTexture previousTarget = camera.targetTexture;
            Matrix4x4 projection = camera.projectionMatrix;
            renderer.RenderQuilt();
            Require(Vector3.Distance(position, camera.transform.position) < 0.0001f, "camera position restored");
            Require(Quaternion.Angle(rotation, camera.transform.rotation) < 0.0001f, "camera rotation restored");
            Require(camera.targetTexture == previousTarget, "camera target restored");
            Require(MatrixApproximately(projection, camera.projectionMatrix), "camera projection restored");
            SaveAndCheckQuilt(renderer.QuiltTexture, renderer.settings.GetLayout());
            Debug.Log("Built-in quilt GPU smoke test passed");
        }
        finally
        {
            if (root != null) UnityEngine.Object.DestroyImmediate(root);
        }
    }

    static GameObject CreateDemoRoot()
    {
        GameObject root = new GameObject("LookingGlassCommunityDemo");
        Camera camera = root.AddComponent<Camera>();
        camera.enabled = false;
        camera.transform.position = new Vector3(0f, 0f, -4f);
        camera.transform.rotation = Quaternion.identity;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.fieldOfView = 50f;
        QuiltRenderer renderer = root.AddComponent<QuiltRenderer>();
        renderer.sourceCamera = camera;
        renderer.settings.quiltWidth = 256;
        renderer.settings.quiltHeight = 256;
        renderer.settings.columns = 4;
        renderer.settings.rows = 2;
        renderer.settings.views = 8;
        renderer.settings.viewAspect = 1f;
        renderer.settings.cameraDistance = 4f;
        renderer.settings.viewCone = 20f;
        renderer.settings.fieldOfView = 50f;
        CreatePrimitive(root.transform, PrimitiveType.Cube, "NearCube", new Vector3(-0.65f, 0f, 4f), new Vector3(0.9f, 0.9f, 0.9f), Color.red);
        CreatePrimitive(root.transform, PrimitiveType.Sphere, "FarSphere", new Vector3(0.65f, 0.1f, 5f), Vector3.one, Color.green);
        return root;
    }

    static GameObject CreatePrimitive(Transform parent, PrimitiveType type, string name, Vector3 position, Vector3 scale, Color color)
    {
        GameObject item = GameObject.CreatePrimitive(type);
        item.name = name;
        item.transform.SetParent(parent);
        item.transform.localPosition = position;
        item.transform.localScale = scale;
        Material material = new Material(Shader.Find("Unlit/Color"));
        material.color = color;
        item.GetComponent<Renderer>().sharedMaterial = material;
        return item;
    }

    static void SaveAndCheckQuilt(RenderTexture quilt, QuiltLayout layout)
    {
        Require(quilt != null && quilt.IsCreated(), "quilt texture created");
        RenderTexture old = RenderTexture.active;
        Texture2D image = new Texture2D(quilt.width, quilt.height, TextureFormat.RGBA32, false);
        try
        {
            RenderTexture.active = quilt;
            image.ReadPixels(new Rect(0, 0, quilt.width, quilt.height), 0, 0);
            image.Apply();
            Color[] pixels = image.GetPixels();
            for (int view = 0; view < layout.ViewCount; ++view)
            {
                RectInt tile = layout.GetTile(view);
                bool nonEmpty = false;
                for (int y = tile.y; y < tile.yMax && !nonEmpty; ++y)
                    for (int x = tile.x; x < tile.xMax; ++x)
                    {
                        Color c = pixels[y * quilt.width + x];
                        if (c.r > 0.25f || c.g > 0.25f || c.b > 0.25f) { nonEmpty = true; break; }
                    }
                Require(nonEmpty, "view tile nonempty " + view);
            }
            for (int y = layout.UsedRect.yMax; y < quilt.height; ++y)
                for (int x = 0; x < quilt.width; ++x)
                    Require(pixels[y * quilt.width + x].maxColorComponent < 0.01f, "unused top border clear");
            string directory = Path.Combine(Application.dataPath, "..", "TestResults");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "builtin-quilt.png"), image.EncodeToPNG());
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(image);
            RenderTexture.active = old;
        }
    }

    static bool MatrixApproximately(Matrix4x4 a, Matrix4x4 b)
    {
        for (int row = 0; row < 4; ++row)
            for (int col = 0; col < 4; ++col)
                if (Math.Abs(a[row, col] - b[row, col]) > 0.0001f) return false;
        return true;
    }

    static void Require(bool value, string name)
    {
        if (!value) throw new Exception("FAIL: " + name);
    }
}
