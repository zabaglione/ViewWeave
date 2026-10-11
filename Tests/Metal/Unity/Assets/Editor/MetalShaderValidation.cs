using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

public static class MetalShaderValidation
{
    // An independent 2x4 lookup oracle covers every position in each pattern period.
    static readonly int[][] Pattern = {
        new[] { 0,0,0,0,0,0,0,0 }, new[] { 0,1,1,0,0,1,1,0 },
        new[] { 0,1,0,1,0,1,0,1 }, new[] { 0,2,1,3,2,0,3,1 },
        new[] { 0,0,1,1,0,0,1,1 }
    };
    static readonly Vector2[] RawOffsets = {
        new Vector2(-.31f,-.28f), new Vector2(.28f,-.28f), new Vector2(0,.28f),
        new Vector2(-.31f,.28f), new Vector2(.28f,.28f), new Vector2(0,-.28f),
        new Vector2(-.22f,.14f), new Vector2(.34f,-.23f), new Vector2(.04f,.31f),
        new Vector2(.19f,-.31f), new Vector2(-.34f,.23f), new Vector2(-.08f,.11f)
    };
    const int Width = 32, Height = 24, Columns = 4, Rows = 3, TileSize = 8;
    const double Pitch = 8.25, Tilt = -.3125;

    [Serializable] sealed class Report
    {
        public bool passed;
        public string unity, graphicsApi, colorSpace;
        public int classicCases, cellCases, channelSamples, width, height;
        public float maximumError;
        public double phaseBoundaryMargin;
    }
    public static void UseGamma() { PlayerSettings.colorSpace = ColorSpace.Gamma; AssetDatabase.SaveAssets(); }
    public static void UseLinear() { PlayerSettings.colorSpace = ColorSpace.Linear; AssetDatabase.SaveAssets(); }

    static double Phase(int x, int y, int channel, int mode, int flip, int red, double center)
    {
        double u = (x + .5) / Width, v = (y + .5) / Height;
        double direction = flip == 1 ? -1 : 1;
        if (mode < 0)
        {
            if (flip == 1) u = 1 - u;
            int subpixel = channel == 0 ? red : channel == 1 ? 1 : 2 - red;
            return (u + direction * subpixel / (Width * 3.0) + direction * v * Tilt) * Pitch - center;
        }
        Vector2 offset = RawOffsets[Pattern[mode][(y % 4) * 2 + x % 2] * 3 + channel];
        return (u + offset.x / Width + (v + offset.y / Height) * direction * Tilt) * Pitch - center;
    }
    static double BilinearChannel(Texture2D quilt, int tileX, int tileY, double withinX, double withinY, int channel)
    {
        double positionX = Math.Max(tileX * TileSize + .5,
            Math.Min((tileX + 1) * TileSize - .5, (tileX + withinX) * TileSize));
        double positionY = Math.Max(tileY * TileSize + .5,
            Math.Min((tileY + 1) * TileSize - .5, (tileY + withinY) * TileSize));
        double sampleX = positionX - .5, sampleY = positionY - .5;
        int floorX = (int)Math.Floor(sampleX), floorY = (int)Math.Floor(sampleY);
        int x0 = Math.Max(tileX * TileSize, Math.Min((tileX + 1) * TileSize - 1, floorX));
        int x1 = Math.Max(tileX * TileSize, Math.Min((tileX + 1) * TileSize - 1, floorX + 1));
        int y0 = Math.Max(tileY * TileSize, Math.Min((tileY + 1) * TileSize - 1, floorY));
        int y1 = Math.Max(tileY * TileSize, Math.Min((tileY + 1) * TileSize - 1, floorY + 1));
        double fx = sampleX - floorX, fy = sampleY - floorY;
        double top = Mathf.Lerp(quilt.GetPixel(x0, y0)[channel], quilt.GetPixel(x1, y0)[channel], (float)fx);
        double bottom = Mathf.Lerp(quilt.GetPixel(x0, y1)[channel], quilt.GetPixel(x1, y1)[channel], (float)fx);
        return Mathf.Lerp((float)top, (float)bottom, (float)fy);
    }
    static float ExactSrgbEncode(double value)
    {
        value = Math.Max(0, Math.Min(1, value));
        return (float)(value <= .0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - .055);
    }
    static double FindCenter(out double margin)
    {
        // Keep discontinuous view boundaries away from float-rounding ambiguity.
        // No pixel is skipped and every channel remains an asserted GPU sample.
        for (int candidate = 0; candidate < 1000; ++candidate)
        {
            double center = (float)(.17 + candidate * .00137);
            double minimum = 1;
            for (int mode = -1; mode <= 4; ++mode) for (int flip = 0; flip < 2; ++flip)
            for (int red = 0; red <= 2; red += 2) for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x) for (int channel = 0; channel < 3; ++channel)
            {
                double position = Phase(x,y,channel,mode,flip,red,center) * Columns * Rows;
                double fraction = position - Math.Floor(position);
                minimum = Math.Min(minimum,Math.Min(fraction,1-fraction));
            }
            if (minimum > .00004) { margin = minimum; return center; }
        }
        throw new Exception("No numerically stable calibration fixture was found");
    }
    [MenuItem("Tools/ViewWeave/Validate Metal Interlacing")]
    public static void Run()
    {
        Shader shader = Resources.Load<Shader>("ViewWeaveMetalInterlace");
        if (shader == null || !shader.isSupported) throw new Exception("Metal interlace shader is unavailable");
        if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Metal)
            throw new Exception("GPU validation requires Metal");
        var report = new Report { unity = Application.unityVersion, graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
            colorSpace = QualitySettings.activeColorSpace.ToString(), width = Width, height = Height };
        double center = FindCenter(out report.phaseBoundaryMargin);
        var quilt = new Texture2D(Columns*TileSize,Rows*TileSize,TextureFormat.RGBAFloat,false,true)
            { filterMode = FilterMode.Point };
        var pixels = new Color[quilt.width*quilt.height];
        for (int y = 0; y < quilt.height; ++y) for (int x = 0; x < quilt.width; ++x)
        {
            int view = x/TileSize + y/TileSize*Columns;
            pixels[x+y*quilt.width] = new Color(.1f+view/32f+x%TileSize*.005f,
                .1f+(12-view)/32f+y%TileSize*.005f, .1f+(view*7%13)/32f+x%TileSize*.003f+y%TileSize*.002f,1);
        }
        quilt.SetPixels(pixels); quilt.Apply();
        var material = new Material(shader);
        var target = new RenderTexture(Width,Height,0) { graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm };
        var capture = new Texture2D(Width,Height,TextureFormat.RGBA32,false,true);
        RenderTexture previous = RenderTexture.active;
        try
        {
            if (!target.Create()) throw new Exception("GPU validation texture creation failed");
            material.SetVector("_Grid",new Vector4(Columns,Rows,TileSize,TileSize));
            material.SetVector("_QuiltSize",new Vector4(quilt.width,quilt.height,1,1));
            material.SetVector("_SourceOptions",new Vector4(
                QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0, 0, 0, 0));
            var offsets = new Vector4[12];
            for (int i = 0; i < offsets.Length; ++i)
                offsets[i] = new Vector4(RawOffsets[i].x/Width,RawOffsets[i].y/Height,0,0);
            material.SetVectorArray("_SubpixelOffsets",offsets);
            for (int mode = -1; mode <= 4; ++mode) for (int flipped = 0; flipped < 2; ++flipped)
            for (int red = 0; red <= 2; red += 2) for (int inverted = mode < 0 ? 0 : 1; inverted <= 1; ++inverted)
            {
                float direction = flipped == 1 ? -1f : 1f;
                material.SetVector("_Screen",new Vector4(Width,Height,direction/(Width*3f),0));
                material.SetVector("_ChannelOrder",new Vector4(red,1,2-red,flipped));
                material.SetVector("_Calibration",new Vector4((float)Pitch,direction*(float)Tilt,(float)center,inverted));
                int count = mode < 0 ? 0 : mode == 0 ? 1 : mode == 3 ? 4 : 2;
                material.SetVector("_SubpixelPattern",new Vector4(Math.Max(0,mode),count,0,0));
                Graphics.Blit(quilt,target,material);
                RenderTexture.active = target;
                capture.ReadPixels(new Rect(0,0,Width,Height),0,0); capture.Apply();
                for (int y = 0; y < Height; ++y) for (int x = 0; x < Width; ++x)
                for (int channel = 0; channel < 3; ++channel)
                {
                    double u = (x+.5)/Width, v = (y+.5)/Height;
                    if (mode < 0 && flipped == 1) u = 1-u;
                    double phase = Phase(x,y,channel,mode,flipped,red,center);
                    phase -= Math.Floor(phase);
                    double fraction = mode >= 0 || inverted == 1 ? 1-phase : phase;
                    int view = Math.Min(Columns*Rows-1,(int)Math.Floor(fraction*Columns*Rows));
                    float expected = (float)BilinearChannel(quilt, view % Columns, view / Columns, u, v, channel);
                    if (QualitySettings.activeColorSpace == ColorSpace.Linear) expected = ExactSrgbEncode(expected);
                    float actual = capture.GetPixel(x,y)[channel];
                    float difference = Mathf.Abs(expected-actual);
                    report.maximumError = Mathf.Max(report.maximumError,difference);
                    if (difference > .006f) throw new Exception($"Metal interlace mismatch mode={mode} flip={flipped} red={red} inverted={inverted} pixel={x},{y} channel={channel} expected={expected} actual={actual}");
                    ++report.channelSamples;
                }
                if (mode < 0) ++report.classicCases; else ++report.cellCases;
            }
            report.passed = report.classicCases == 8 && report.cellCases == 20 && report.channelSamples == 64512;
            if (!report.passed) throw new Exception("GPU validation case count mismatch");
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i+1 < args.Length; ++i) if (args[i] == "-shaderReport")
                File.WriteAllText(args[i+1],JsonUtility.ToJson(report,true)+"\n");
            Debug.Log($"Metal shader validation passed: classic={report.classicCases} cells={report.cellCases} samples={report.channelSamples} graphics={report.graphicsApi} color={report.colorSpace}");
        }
        finally
        {
            RenderTexture.active = previous; target.Release();
            UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(capture);
            UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(quilt);
        }
    }
}
