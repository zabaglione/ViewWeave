using System;
using System.IO;
using LookingGlass.Community;
using UnityEditor;
using UnityEngine;

namespace LookingGlass.BridgeWorkerValidation.Editor
{
    public static class BridgeWorkerViewIndexDebugVerification
    {
        const int Columns = 8;
        const int Rows = 6;
        const int TileSize = 128;
        const int Views = 48;

        public static void Run()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "..", ".local", "windows-sdk-demo"));
            Directory.CreateDirectory(root);
            GameObject host = new GameObject("BridgeWorkerViewIndexDebug.Verification");
            RenderTexture source = new RenderTexture(Columns * TileSize, Rows * TileSize, 0, RenderTextureFormat.ARGB32);
            Texture2D image = null;
            try
            {
                source.Create();
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = source;
                GL.Clear(false, true, new Color(.2f, .2f, .2f, 1f));
                RenderTexture.active = previous;
                BridgeWorkerViewIndexOutput output = host.AddComponent<BridgeWorkerViewIndexOutput>();
                SerializedObject serialized = new SerializedObject(output);
                Shader shader = Shader.Find("LookingGlass/BridgeWorkerViewIndexDebug");
                if (shader == null) throw new InvalidOperationException("view index debug shader is unavailable");
                serialized.FindProperty("indexShader").objectReferenceValue = shader;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                output.ViewIndexDebug = true;
                QuiltLayout layout = new QuiltLayout(source.width, source.height, Rows, Columns, Views, 1f);
                output.Submit(source, layout);
                if (output.IndexedQuilt == null || output.SubmittedTexture != output.IndexedQuilt) throw new InvalidOperationException("indexed quilt was not submitted");
                image = Read(output.IndexedQuilt);
                int nonEmpty = 0;
                string[] hashes = new string[Views];
                for (int view = 0; view < Views; ++view)
                {
                    RectInt tile = layout.GetTile(view);
                    int white = 0;
                    unchecked
                    {
                        uint hash = 2166136261u;
                        for (int y = tile.y + TileSize * 3 / 4; y < tile.y + TileSize; ++y)
                            for (int x = tile.x; x < tile.x + TileSize / 2; ++x)
                            {
                                Color32 pixel = image.GetPixel(x, y);
                                if (pixel.r > 220 && pixel.g > 220 && pixel.b > 220) white++;
                                hash = (hash ^ pixel.r) * 16777619u;
                                hash = (hash ^ pixel.g) * 16777619u;
                                hash = (hash ^ pixel.b) * 16777619u;
                            }
                        hashes[view] = hash.ToString("X8");
                    }
                    if (white > 20) nonEmpty++;
                }
                for (int i = 0; i < Views; ++i)
                    for (int j = i + 1; j < Views; ++j)
                        if (hashes[i] == hashes[j]) throw new InvalidOperationException("duplicate view index glyph hash at " + i + " and " + j);
                if (nonEmpty != Views) throw new InvalidOperationException("view index glyph coverage failed: " + nonEmpty + "/" + Views);
                File.WriteAllBytes(Path.Combine(root, "view-index-debug-proof.png"), image.EncodeToPNG());
                output.ViewIndexDebug = false;
                output.Submit(source, layout);
                if (output.SubmittedTexture != source) throw new InvalidOperationException("debug toggle did not restore source submission");
                Debug.Log("view_index_debug_verification=passed views=48 unique=48 covered=48");
            }
            finally
            {
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
                if (source != null) { source.Release(); UnityEngine.Object.DestroyImmediate(source); }
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        static Texture2D Read(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            RenderTexture.active = source;
            image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            image.Apply(false, false);
            RenderTexture.active = previous;
            return image;
        }
    }
}
