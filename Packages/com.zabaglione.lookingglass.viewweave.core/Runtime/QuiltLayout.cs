using System;
using UnityEngine;

namespace ViewWeave
{
    public readonly struct QuiltLayout
    {
        public readonly int QuiltWidth, QuiltHeight, Rows, Columns, ViewCount, TileWidth, TileHeight;
        public readonly float AspectRatio;
        public readonly RectInt UsedRect;
        public int UnusedRight => QuiltWidth - UsedRect.xMax;
        public int UnusedTop => QuiltHeight - UsedRect.yMax;
        public float PixelAspectRatio => TileWidth / (float)TileHeight;

        public QuiltLayout(int width, int height, int rows, int columns, int views, float aspectRatio = 1f)
        {
            if (width < 1 || height < 1 || rows < 1 || columns < 1) throw new ArgumentOutOfRangeException("layout dimensions must be positive");
            long capacity = (long)rows * columns;
            if (views < 1 || views > capacity) throw new ArgumentOutOfRangeException(nameof(views));
            if (float.IsNaN(aspectRatio) || float.IsInfinity(aspectRatio) || aspectRatio <= 0f) throw new ArgumentOutOfRangeException(nameof(aspectRatio));
            QuiltWidth = width; QuiltHeight = height; Rows = rows; Columns = columns; ViewCount = views;
            AspectRatio = aspectRatio;
            TileWidth = width / columns; TileHeight = height / rows;
            if (TileWidth < 1 || TileHeight < 1) throw new ArgumentOutOfRangeException("quilt is smaller than its grid");
            UsedRect = new RectInt(0, 0, TileWidth * columns, TileHeight * rows);
        }

        public RectInt GetTile(int view)
        {
            if (view < 0 || view >= ViewCount) throw new ArgumentOutOfRangeException(nameof(view));
            int x = view % Columns;
            int y = view / Columns;
            return new RectInt(x * TileWidth, y * TileHeight, TileWidth, TileHeight);
        }

        public override string ToString() => $"{QuiltWidth}x{QuiltHeight}, {Columns}x{Rows}, {ViewCount} views";
    }
}
