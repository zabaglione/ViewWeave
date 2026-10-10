using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public static class MetalShaderValidation
{
    public static void UseGamma()
    {
        PlayerSettings.colorSpace = ColorSpace.Gamma;
        AssetDatabase.SaveAssets();
        Debug.Log("Metal validation configured for Gamma color space");
    }
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
        var quilt = new Texture2D(columns * tileSize, rows * tileSize, TextureFormat.RGBA32, false, true)
            { filterMode = FilterMode.Point };
        var pixels = new Color[quilt.width * quilt.height];
        for (int y = 0; y < quilt.height; ++y)
        for (int x = 0; x < quilt.width; ++x)
        {
            int view = x / tileSize + y / tileSize * columns;
            pixels[x + y * quilt.width] = new Color((view + 1) / 17f + x % tileSize * 0.02f,
                (12 - view) / 17f + y % tileSize * 0.02f, 0.1f + x % tileSize * 0.07f, 1f);
        }
        quilt.SetPixels(pixels); quilt.Apply();
        var material = new Material(shader);
        var target = new RenderTexture(width, height, 0) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm };
        var capture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        RenderTexture previous = RenderTexture.active;
        try
        {
            target.Create();
            material.SetVector("_Grid", new Vector4(columns, rows, tileSize, tileSize));
            material.SetVector("_QuiltSize", new Vector4(quilt.width, quilt.height, 1, 0));
            for (int flipped = 0; flipped <= 1; ++flipped)
            for (int redIndex = 0; redIndex <= 2; redIndex += 2)
            for (int inverted = 0; inverted <= 1; ++inverted)
            {
                float direction = flipped == 1 ? -1f : 1f;
                material.SetVector("_Screen", new Vector4(width, height, direction / (width * 3f), 0));
                // The first case also exercises the RGB default for legacy material clients.
                if (flipped != 0 || redIndex != 0 || inverted != 0)
                    material.SetVector("_ChannelOrder", new Vector4(redIndex, 1, 2 - redIndex, flipped));
                material.SetVector("_Calibration", new Vector4(8.25f, direction * -0.3125f, 0.17f, inverted));
                Graphics.Blit(quilt, target, material);
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0,0,width,height),0,0); capture.Apply();
                int checkedPixels = 0;
                for (int y = 2; y < height; y += 7)
                for (int x = 2; x < width; x += 11)
                for (int channel = 0; channel < 3; ++channel)
                {
                    double u = (x + 0.5) / width, v = (y + 0.5) / height;
                    if (flipped == 1) u = 1 - u;
                    int subpixel = channel == 0 ? redIndex : channel == 1 ? 1 : 2 - redIndex;
                    double phase = (u + direction * subpixel / (width * 3.0) + direction * v * -0.3125) * 8.25 - 0.17;
                    phase -= Math.Floor(phase);
                    int index = Math.Min(11, (int)Math.Floor((inverted == 1 ? 1 - phase : phase) * 12));
                    int sourceX = index % columns * tileSize + Math.Min(tileSize - 1, (int)(u * tileSize));
                    int sourceY = index / columns * tileSize + Math.Min(tileSize - 1, (int)(v * tileSize));
                    float expected = quilt.GetPixel(sourceX, sourceY)[channel];
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear) expected = Mathf.LinearToGammaSpace(expected);
                    float actual = capture.GetPixel(x,y)[channel];
                    if (Mathf.Abs(expected - actual) > 0.025f)
                    {
                        Debug.Log($"Metal shader context color={QualitySettings.activeColorSpace} source={quilt.GetPixel(31,23)} output={capture.GetPixel(x,y)} opposite={capture.GetPixel(x,height-1-y)}");
                        throw new Exception($"Metal interlace mismatch pixel={x},{y} channel={channel} invert={inverted} flip={flipped} red={redIndex} expected={expected} actual={actual}");
                    }
                    ++checkedPixels;
                }
                Debug.Log($"Metal shader validation passed: invert={inverted} flip={flipped} red={redIndex} samples={checkedPixels} graphics={SystemInfo.graphicsDeviceType} color={QualitySettings.activeColorSpace}");
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
