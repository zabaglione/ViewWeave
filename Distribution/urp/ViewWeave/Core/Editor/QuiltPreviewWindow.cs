using System.IO;
using UnityEditor;
using UnityEngine;

namespace LookingGlass.Community.Editor
{
    public sealed class QuiltPreviewWindow : EditorWindow
    {
        QuiltRenderer renderer;

        [MenuItem("Window/Looking Glass Community/Quilt Preview")]
        static void Open() => GetWindow<QuiltPreviewWindow>("Quilt Preview");

        void OnGUI()
        {
            renderer = (QuiltRenderer)EditorGUILayout.ObjectField("Renderer", renderer, typeof(QuiltRenderer), true);
            if (renderer == null)
            {
                EditorGUILayout.HelpBox("Assign a QuiltRenderer.", MessageType.Info);
                return;
            }
            if (GUILayout.Button("Render Quilt"))
            {
                try { renderer.RenderQuilt(); }
                catch (System.Exception error) { ShowNotification(new GUIContent("Render failed: " + error.GetType().Name)); }
            }
            if (renderer.QuiltTexture == null)
            {
                EditorGUILayout.HelpBox("Render a quilt to preview it.", MessageType.Info);
                return;
            }
            Rect rect = GUILayoutUtility.GetAspectRect(renderer.QuiltTexture.width / (float)renderer.QuiltTexture.height, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(rect, renderer.QuiltTexture, null, ScaleMode.ScaleToFit);
            if (GUILayout.Button("Save PNG")) ExportPng(renderer.QuiltTexture);
        }

        static void ExportPng(RenderTexture source)
        {
            string path = EditorUtility.SaveFilePanel("Save quilt PNG", "", "quilt.png", "png");
            if (string.IsNullOrEmpty(path)) return;
            RenderTexture old = RenderTexture.active;
            Texture2D image = null;
            try
            {
                image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                RenderTexture.active = source;
                image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                image.Apply();
                File.WriteAllBytes(path, image.EncodeToPNG());
                AssetDatabase.Refresh();
            }
            finally
            {
                if (image != null) Object.DestroyImmediate(image);
                RenderTexture.active = old;
            }
        }
    }
}
