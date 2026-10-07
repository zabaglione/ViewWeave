using System;

namespace ViewWeave
{
    public enum QuiltViewRenderMode
    {
        Full = 1,
        Half = 2,
        Quarter = 4
    }

    public static class QuiltViewSampling
    {
        public static int GetRenderCount(int viewCount, QuiltViewRenderMode mode)
        {
            if (viewCount < 1) throw new ArgumentOutOfRangeException(nameof(viewCount));
            int divisor = GetDivisor(mode);
            int count = (int)(((long)viewCount + divisor - 1) / divisor);
            if (viewCount > 1 && count < 2) count = 2;
            return Math.Min(viewCount, count);
        }

        public static int GetRenderedViewIndex(int viewCount, QuiltViewRenderMode mode, int renderSlot)
        {
            int renderCount = GetRenderCount(viewCount, mode);
            if (renderSlot < 0 || renderSlot >= renderCount) throw new ArgumentOutOfRangeException(nameof(renderSlot));
            if (renderCount == 1) return 0;
            long numerator = (long)renderSlot * (viewCount - 1);
            return (int)((numerator + (renderCount - 1) / 2) / (renderCount - 1));
        }

        public static int GetSourceViewIndex(int viewCount, QuiltViewRenderMode mode, int outputView)
        {
            if (outputView < 0 || outputView >= viewCount) throw new ArgumentOutOfRangeException(nameof(outputView));
            int renderCount = GetRenderCount(viewCount, mode);
            int nearest = GetRenderedViewIndex(viewCount, mode, 0);
            int nearestDistance = Math.Abs(outputView - nearest);
            for (int slot = 1; slot < renderCount; ++slot)
            {
                int candidate = GetRenderedViewIndex(viewCount, mode, slot);
                int distance = Math.Abs(outputView - candidate);
                if (distance < nearestDistance)
                {
                    nearest = candidate;
                    nearestDistance = distance;
                }
            }
            return nearest;
        }

        static int GetDivisor(QuiltViewRenderMode mode)
        {
            switch (mode)
            {
                case QuiltViewRenderMode.Full: return 1;
                case QuiltViewRenderMode.Half: return 2;
                case QuiltViewRenderMode.Quarter: return 4;
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }
    }
}
