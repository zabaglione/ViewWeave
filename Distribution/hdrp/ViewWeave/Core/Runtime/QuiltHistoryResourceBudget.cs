using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
        readonly Func<long> graphicsMemorySampler;
        readonly Func<long> textureGrowthSampler;
        long graphicsBaseline;
        long lastGraphicsMeasuredBytes;
        long lastTextureGrowthBytes;
        public long GraphicsGrowthBytes { get; private set; }
        public long TextureGrowthBytes { get; private set; }

        public QuiltHistoryResourceBudget() { }

        // Internal deterministic seam for CPU-only budget tests. Production keeps using Unity counters/resources.
        internal QuiltHistoryResourceBudget(Func<long> graphicsMemorySampler, Func<long> textureGrowthSampler)
        {
            this.graphicsMemorySampler = graphicsMemorySampler;
            this.textureGrowthSampler = textureGrowthSampler;
        }

        public void CaptureBaseline()
        {
            Reset();
            graphicsBaseline = ReadGraphicsMemory();
            lastGraphicsMeasuredBytes = graphicsBaseline;
            foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (texture != null && texture.IsCreated()) baseline[texture] = EstimateTextureBytes(texture);
        }

        public void Observe(long budgetBytes, bool observeTextures, string pipelineName)
        {
            long previousGraphicsSample = lastGraphicsMeasuredBytes;
            long measuredGraphics = ReadGraphicsMemory();
            if (measuredGraphics > 0 && graphicsBaseline > 0)
                GraphicsGrowthBytes = Math.Max(GraphicsGrowthBytes, Math.Max(0, measuredGraphics - graphicsBaseline));

            if (observeTextures)
            {
                long measuredTextureGrowth = textureGrowthSampler != null ? textureGrowthSampler() : MeasureTextureGrowth();
                lastTextureGrowthBytes = measuredTextureGrowth;
                TextureGrowthBytes = Math.Max(TextureGrowthBytes, measuredTextureGrowth);
            }

            lastGraphicsMeasuredBytes = measuredGraphics;
            if (Math.Max(GraphicsGrowthBytes, TextureGrowthBytes) > budgetBytes)
            {
                string failureMessage;
                try
                {
                    failureMessage = BuildFailureMessage(
                        pipelineName, budgetBytes, previousGraphicsSample, measuredGraphics, observeTextures);
                }
                catch
                {
                    // Keep the guard exception type even if optional diagnostics cannot be constructed.
                    throw new InsufficientMemoryException("History Resource Budget exceeded; diagnostic detail unavailable.");
                }
                throw new InsufficientMemoryException(failureMessage);
            }
        }

        public void Reset()
        {
            baseline.Clear();
            graphicsBaseline = 0;
            lastGraphicsMeasuredBytes = 0;
            lastTextureGrowthBytes = 0;
            GraphicsGrowthBytes = 0;
            TextureGrowthBytes = 0;
        }

        long ReadGraphicsMemory() => graphicsMemorySampler != null
            ? graphicsMemorySampler()
            : Profiler.GetAllocatedMemoryForGraphicsDriver();

        long MeasureTextureGrowth()
        {
            long growth = 0;
            foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
            {
                if (texture == null || !texture.IsCreated()) continue;
                baseline.TryGetValue(texture, out long originalBytes);
                growth += Math.Max(0, EstimateTextureBytes(texture) - originalBytes);
            }
            return growth;
        }

        string BuildFailureMessage(string pipelineName, long budgetBytes, long previousGraphicsSample,
            long measuredGraphics, bool texturesSampledThisCall)
        {
            const long bytesPerMegabyte = 1024L * 1024L;
            var message = new StringBuilder(480);
            message.Append(pipelineName).Append(" History Resource Budget exceeded: observed graphics growth ")
                .Append((GraphicsGrowthBytes / bytesPerMegabyte).ToString(CultureInfo.InvariantCulture))
                .Append(" MB; estimated texture growth ")
                .Append((TextureGrowthBytes / bytesPerMegabyte).ToString(CultureInfo.InvariantCulture))
                .Append(" MB; budget ")
                .Append((budgetBytes / bytesPerMegabyte).ToString(CultureInfo.InvariantCulture))
                .Append(" MB; graphics baseline raw bytes=").Append(graphicsBaseline.ToString(CultureInfo.InvariantCulture))
                .Append("; previous graphics sample raw bytes=").Append(previousGraphicsSample.ToString(CultureInfo.InvariantCulture))
                .Append("; current graphics sample raw bytes=").Append(measuredGraphics.ToString(CultureInfo.InvariantCulture))
                .Append("; peak graphics growth raw bytes=").Append(GraphicsGrowthBytes.ToString(CultureInfo.InvariantCulture))
                .Append("; last estimated texture growth raw bytes=").Append(lastTextureGrowthBytes.ToString(CultureInfo.InvariantCulture))
                .Append("; peak estimated texture growth raw bytes=").Append(TextureGrowthBytes.ToString(CultureInfo.InvariantCulture))
                .Append("; textures sampled this call=").Append(texturesSampledThisCall ? "true" : "false")
                .Append("; peaks are high-water marks and may come from different samples")
                .Append("; frame=").Append(Time.frameCount.ToString(CultureInfo.InvariantCulture))
                .Append("; mode=").Append(Application.isPlaying ? "Play" : "Editor")
                .Append(". Reduce Output Quality or rendered view count, or increase the budget.");
            return message.ToString();
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
