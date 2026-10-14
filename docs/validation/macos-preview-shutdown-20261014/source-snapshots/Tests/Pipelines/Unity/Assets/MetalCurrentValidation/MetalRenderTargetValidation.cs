using System;
using UnityEngine;

// Read public Unity resource metadata without inspecting private renderer fields.
public static class MetalRenderTargetValidation
{
    [Serializable] public sealed class Evidence
    {
        public string viewFormat, viewGraphicsFormat, resolveGraphicsFormat, quiltGraphicsFormat;
        public bool viewSrgb, resolveSrgb, quiltSrgb, resolvePresent;
        public int viewSamples, resolveSamples, quiltSamples;
    }

    public static Evidence Capture(string pipeline)
    {
        RenderTexture view = Find("ViewWeave.View");
        RenderTexture resolve = Find("ViewWeave.ViewResolve", false);
        RenderTexture quilt = Find("ViewWeave.Quilt");
        var result = new Evidence
        {
            viewFormat = view.format.ToString(), viewGraphicsFormat = view.graphicsFormat.ToString(),
            resolveGraphicsFormat = resolve == null ? "Absent" : resolve.graphicsFormat.ToString(), quiltGraphicsFormat = quilt.graphicsFormat.ToString(),
            viewSrgb = view.sRGB, resolveSrgb = resolve != null && resolve.sRGB, quiltSrgb = quilt.sRGB,
            resolvePresent = resolve != null,
            viewSamples = view.antiAliasing, resolveSamples = resolve == null ? 0 : resolve.antiAliasing, quiltSamples = quilt.antiAliasing
        };
        bool hdr = pipeline == "URP";
        bool needsResolve = hdr || view.antiAliasing > 1;
        if (view.format != (hdr ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32) ||
            view.sRGB != !hdr || !quilt.sRGB || quilt.antiAliasing != 1 ||
            (needsResolve && (resolve == null || resolve.graphicsFormat != quilt.graphicsFormat ||
            resolve.sRGB != quilt.sRGB || resolve.antiAliasing != 1)))
            throw new InvalidOperationException("Unexpected view/resolve/quilt target format or encoding.");
        return result;
    }

    static RenderTexture Find(string name, bool required = true)
    {
        RenderTexture result = null;
        foreach (var texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
        {
            if (texture == null || texture.name != name || !texture.IsCreated()) continue;
            if (result != null) throw new InvalidOperationException("Multiple active targets named " + name);
            result = texture;
        }
        if (result == null && required) throw new InvalidOperationException("Active target missing: " + name);
        return result;
    }
}
