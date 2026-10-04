using System;
using UnityEngine;

namespace LookingGlass.Community
{
    public enum QuiltQualityPreset { Preview, Balanced, Native }

    public readonly struct QuiltQualityProfile
    {
        public readonly int QuiltWidth, QuiltHeight, Columns, Rows, Views;
        public readonly float Aspect, ViewCone;

        public QuiltQualityProfile(int quiltWidth, int quiltHeight, int columns, int rows, int views, float aspect, float viewCone)
        {
            QuiltWidth = quiltWidth; QuiltHeight = quiltHeight; Columns = columns; Rows = rows; Views = views; Aspect = aspect; ViewCone = viewCone;
            Validate();
        }

        public static QuiltQualityProfile Preview(int width, int height, int columns, int rows, float aspect, float viewCone)
            => FromScaled(width, height, columns, rows, aspect, viewCone, 0.25f);
        public static QuiltQualityProfile Balanced(int width, int height, int columns, int rows, float aspect, float viewCone)
            => FromScaled(width, height, columns, rows, aspect, viewCone, 0.5f);
        public static QuiltQualityProfile Native(int width, int height, int columns, int rows, float aspect, float viewCone)
        {
            if (width < 1 || height < 1 || columns < 1 || rows < 1) throw new ArgumentOutOfRangeException("profile dimensions must be positive");
            return new QuiltQualityProfile(width, height, columns, rows, checked(columns * rows), aspect, viewCone);
        }

        public static QuiltQualityProfile FromScaled(int width, int height, int columns, int rows, float aspect, float viewCone, float scale)
        {
            if (width < 1 || height < 1 || columns < 1 || rows < 1) throw new ArgumentOutOfRangeException("profile dimensions must be positive");
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) throw new ArgumentOutOfRangeException(nameof(scale));
            if (scale == 1f)
                return new QuiltQualityProfile(width, height, columns, rows, checked(columns * rows), aspect, viewCone);
            int scaledWidth = RoundToGrid(width, scale, columns);
            int scaledHeight = RoundToGrid(height, scale, rows);
            return new QuiltQualityProfile(scaledWidth, scaledHeight, columns, rows, checked(columns * rows), aspect, viewCone);
        }

        static int RoundToGrid(int value, float scale, int grid)
        {
            if (grid < 1) throw new ArgumentOutOfRangeException(nameof(grid));
            double scaledDouble = value * (double)scale;
            if (double.IsNaN(scaledDouble) || double.IsInfinity(scaledDouble) || scaledDouble > int.MaxValue) throw new ArgumentOutOfRangeException("scaled profile exceeds integer range");
            int scaled = checked(Math.Max(1, (int)Math.Round(scaledDouble)));
            int tiles = Math.Max(1, (int)Math.Round(scaled / (double)grid));
            return checked(tiles * grid);
        }

        public void Validate()
        {
            if (QuiltWidth < 1 || QuiltHeight < 1 || Columns < 1 || Rows < 1 || Views != checked(Columns * Rows)) throw new ArgumentOutOfRangeException("invalid quilt dimensions or view count");
            if (float.IsNaN(Aspect) || float.IsInfinity(Aspect) || Aspect <= 0f || float.IsNaN(ViewCone) || float.IsInfinity(ViewCone) || ViewCone < 0f || ViewCone >= 179f) throw new ArgumentOutOfRangeException("invalid profile geometry");
            if (QuiltWidth / Columns < 1 || QuiltHeight / Rows < 1) throw new ArgumentOutOfRangeException("profile dimensions are smaller than the grid");
            if (QuiltWidth > SystemInfo.maxTextureSize || QuiltHeight > SystemInfo.maxTextureSize || QuiltWidth / Columns > SystemInfo.maxTextureSize || QuiltHeight / Rows > SystemInfo.maxTextureSize) throw new ArgumentOutOfRangeException("profile exceeds the device maximum texture size");
        }

        public void ApplyTo(QuiltSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            Validate();
            settings.quiltWidth = QuiltWidth; settings.quiltHeight = QuiltHeight; settings.columns = Columns; settings.rows = Rows; settings.views = Views; settings.viewAspect = Aspect; settings.viewCone = ViewCone;
        }
    }
}
