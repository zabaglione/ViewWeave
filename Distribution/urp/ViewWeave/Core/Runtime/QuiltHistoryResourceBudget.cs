using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace ViewWeave
{
    /// <summary>Conservative growth observation, not a strict VRAM limit; includes shared pipeline allocations.</summary>
    public sealed class QuiltHistoryResourceBudget
    {
        readonly Dictionary<RenderTexture, long> baseline = new Dictionary<RenderTexture, long>();
        long graphicsBaseline;
        public long GraphicsGrowthBytes { get; private set; }
        public long TextureGrowthBytes { get; private set; }

        public void CaptureBaseline()
        {
            Reset();
            graphicsBaseline = Profiler.GetAllocatedMemoryForGraphicsDriver();
            foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (texture != null && texture.IsCreated()) baseline[texture] = EstimateTextureBytes(texture);
        }

        public void Observe(long budgetBytes, bool observeTextures, string pipelineName)
        {
            long measured = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (measured > 0 && graphicsBaseline > 0)
                GraphicsGrowthBytes = Math.Max(GraphicsGrowthBytes, Math.Max(0, measured - graphicsBaseline));
            if (observeTextures)
            {
                long growth = 0;
                foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                {
                    if (texture == null || !texture.IsCreated()) continue;
                    baseline.TryGetValue(texture, out long originalBytes);
                    growth += Math.Max(0, EstimateTextureBytes(texture) - originalBytes);
                }
                TextureGrowthBytes = Math.Max(TextureGrowthBytes, growth);
            }
            if (Math.Max(GraphicsGrowthBytes, TextureGrowthBytes) > budgetBytes)
                throw new InsufficientMemoryException(pipelineName + " History Resource Budget exceeded: observed graphics growth " +
                    (GraphicsGrowthBytes / (1024L * 1024L)) + " MB; estimated texture growth " +
                    (TextureGrowthBytes / (1024L * 1024L)) + " MB; budget " + (budgetBytes / (1024L * 1024L)) +
                    " MB. Reduce Output Quality or rendered view count, or increase the budget.");
        }

        public void Reset()
        {
            baseline.Clear(); graphicsBaseline = 0; GraphicsGrowthBytes = 0; TextureGrowthBytes = 0;
        }

        // Descriptor estimates work in Release Players where graphics-driver counters can be zero.
        // Do not subtract unrelated textures that were freed. Buffers and driver overhead are unobservable.
        static long EstimateTextureBytes(RenderTexture texture)
        {
            RenderTextureDescriptor descriptor = texture.descriptor;
            long color = EstimateFormatBytes(descriptor.graphicsFormat, descriptor.width, descriptor.height, texture.mipmapCount);
            long depth = EstimateFormatBytes(descriptor.depthStencilFormat, descriptor.width, descriptor.height, 1);
            int slices = Math.Max(1, descriptor.volumeDepth);
            if (descriptor.dimension == TextureDimension.Cube || descriptor.dimension == TextureDimension.CubeArray) slices *= 6;
            int samples = Math.Max(1, descriptor.msaaSamples);
            // A non-bindMS multisampled target may also own a resolved color surface.
            long bytes = slices * (color * (samples > 1 && !descriptor.bindMS ? samples + 1 : samples) + depth * samples);
            return ((bytes + 65535L) / 65536L) * 65536L;
        }

        static long EstimateFormatBytes(GraphicsFormat format, int width, int height, int mipCount)
        {
            if (format == GraphicsFormat.None) return 0;
            long blockWidth = Math.Max(1L, GraphicsFormatUtility.GetBlockWidth(format));
            long blockHeight = Math.Max(1L, GraphicsFormatUtility.GetBlockHeight(format));
            long blockBytes = GraphicsFormatUtility.GetBlockSize(format);
            long bytes = 0;
            for (int mip = 0; mip < Math.Max(1, mipCount); ++mip)
            {
                bytes += ((width + blockWidth - 1) / blockWidth) * ((height + blockHeight - 1) / blockHeight) * blockBytes;
                width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
            }
            return bytes;
        }

    }
}
