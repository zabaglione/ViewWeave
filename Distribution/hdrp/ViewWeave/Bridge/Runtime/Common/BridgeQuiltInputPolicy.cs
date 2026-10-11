using System;
using UnityEngine;

namespace ViewWeave
{
    internal static class BridgeQuiltInputPolicy
    {
        const int MaximumGridDimension = 4096;

        internal static QuiltQualityProfile CreateQualityProfile(BridgeDisplayProfile profile, QuiltQualityPreset preset)
        {
            if (!Enum.IsDefined(typeof(QuiltQualityPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
            if (!profile.IsValid) throw new ArgumentOutOfRangeException(nameof(profile));
            switch (preset)
            {
                case QuiltQualityPreset.Preview:
                    return QuiltQualityProfile.Preview(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                case QuiltQualityPreset.Balanced:
                    return QuiltQualityProfile.Balanced(profile.QuiltWidth, profile.QuiltHeight, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
                default:
                    // Keep the Bridge metadata intact; draw only its complete tile area.
                    int width = profile.QuiltWidth / profile.Columns * profile.Columns;
                    int height = profile.QuiltHeight / profile.Rows * profile.Rows;
                    return QuiltQualityProfile.Native(width, height, profile.Columns, profile.Rows, profile.Aspect, profile.Viewcone);
            }
        }

        internal static bool TryValidate(RenderTexture texture, QuiltLayout layout, out string error)
        {
            error = string.Empty;
            if (texture == null)
            {
                error = "A quilt texture is required.";
                return false;
            }

            int width = texture.width;
            int height = texture.height;
            if (width <= 0 || height <= 0 || layout.QuiltWidth <= 0 || layout.QuiltHeight <= 0 ||
                layout.QuiltWidth != width || layout.QuiltHeight != height)
            {
                error = "The quilt layout dimensions must match the texture dimensions.";
                return false;
            }

            if (layout.Columns <= 0 || layout.Rows <= 0 ||
                layout.Columns > MaximumGridDimension || layout.Rows > MaximumGridDimension ||
                (long)layout.Columns * layout.Rows != layout.ViewCount)
            {
                error = "The Bridge output requires a complete supported quilt grid.";
                return false;
            }

            if (width % layout.Columns != 0 || height % layout.Rows != 0)
            {
                error = "The quilt texture dimensions must divide evenly across the view grid.";
                return false;
            }

            int expectedTileWidth = width / layout.Columns;
            int expectedTileHeight = height / layout.Rows;
            if (layout.TileWidth < 1 || layout.TileHeight < 1 ||
                layout.TileWidth != expectedTileWidth || layout.TileHeight != expectedTileHeight)
            {
                error = "The quilt tile dimensions do not match the complete view grid.";
                return false;
            }

            if (float.IsNaN(layout.AspectRatio) || float.IsInfinity(layout.AspectRatio) || layout.AspectRatio <= 0f)
            {
                error = "The quilt view aspect ratio must be finite and positive.";
                return false;
            }

            return true;
        }
    }
}
