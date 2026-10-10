using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public static class MetalShaderValidation
{
    public static void UseLinear()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear;
        AssetDatabase.SaveAssets();
        Debug.Log("Metal validation configured for Linear color space");
    }
    [MenuItem("Tools/ViewWeave/Validate Metal Interlacing")]
    public static void Run()
    {
        Shader shader = Resources.Load<Shader>("ViewWeaveMetalInterlace");
        if (shader == null || !shader.isSupported) throw new Exception("Metal interlace shader is unavailable");
        const int columns = 4, rows = 3, tileSize = 8, width = 96, height = 64;
        var quilt = new Texture2D(columns * tileSize, rows * tileSize, TextureFormat.RGBA32, false, true);
        var pixels = new Color[quilt.width * quilt.height];
        for (int y = 0; y < quilt.height; ++y)
        for (int x = 0; x < quilt.width; ++x)
        {
            int view = x / tileSize + y / tileSize * columns;
            pixels[x + y * quilt.width] = new Color((view + 1) / 13f, (12 - view) / 13f, 0.25f, 1f);
        }
        quilt.SetPixels(pixels); quilt.Apply();
        var material = new Material(shader);
        var target = new RenderTexture(width, height, 0) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm };
        var capture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        RenderTexture previous = RenderTexture.active;
        try
        {
            target.Create();
            material.SetVector("_Screen", new Vector4(width, height, 1f / (width * 3f), 0));
            material.SetVector("_Grid", new Vector4(columns, rows, tileSize, tileSize));
            material.SetVector("_QuiltSize", new Vector4(quilt.width, quilt.height, 1, 0));
            for (int inverted = 0; inverted <= 1; ++inverted)
            {
                material.SetVector("_Calibration", new Vector4(8.25f, -0.3125f, 0.17f, inverted));
                Graphics.Blit(quilt, target, material);
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0,0,width,height),0,0); capture.Apply();
                int checkedPixels = 0;
                for (int y = 2; y < height; y += 7)
                for (int x = 2; x < width; x += 11)
                for (int channel = 0; channel < 3; ++channel)
                {
                    double phase = ((x + 0.5) / width + channel / (width * 3.0) + (y + 0.5) / height * -0.3125) * 8.25 - 0.17;
                    phase -= Math.Floor(phase);
                    int index = Math.Min(11, (int)Math.Floor((inverted == 1 ? 1 - phase : phase) * 12));
                    float expected = channel == 0 ? (index + 1) / 13f : channel == 1 ? (12 - index) / 13f : 0.25f;
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear) expected = Mathf.LinearToGammaSpace(expected);
                    float actual = capture.GetPixel(x,y)[channel];
                    if (Mathf.Abs(expected - actual) > 0.025f)
                    {
                        Debug.Log($"Metal shader context color={QualitySettings.activeColorSpace} source={quilt.GetPixel(31,23)} output={capture.GetPixel(x,y)} opposite={capture.GetPixel(x,height-1-y)}");
                        throw new Exception($"Metal interlace mismatch pixel={x},{y} channel={channel} invert={inverted} expected={expected} actual={actual}");
                    }
                    ++checkedPixels;
                }
                Debug.Log($"Metal shader validation passed: invert={inverted} samples={checkedPixels} graphics={SystemInfo.graphicsDeviceType} color={QualitySettings.activeColorSpace}");
            }
        }
        finally
        {
            RenderTexture.active = previous;
            target.Release();
            UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(capture);
            UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(quilt);
        }
    }
}
