using System;
using System.Collections.Generic;

namespace ViewWeave
{
    public enum QuiltRendererOperationKind
    {
        Recording,
        Playback
    }

    public sealed class QuiltRendererOperationLease : IDisposable
    {
        static readonly List<QuiltRendererOperationLease> activeLeases = new List<QuiltRendererOperationLease>();
        bool disposed;

        public QuiltRenderer Renderer { get; private set; }
        public QuiltRendererOperationKind Kind { get; private set; }
        public bool IsActive => !disposed && Renderer != null;

        QuiltRendererOperationLease(QuiltRenderer renderer, QuiltRendererOperationKind kind)
        {
            Renderer = renderer;
            Kind = kind;
        }

        public static bool TryAcquire(QuiltRenderer renderer, QuiltRendererOperationKind kind, out QuiltRendererOperationLease lease, out string error)
        {
            lease = null;
            error = string.Empty;
            if (renderer == null)
            {
                error = "An enabled Quilt Renderer is required for this operation.";
                return false;
            }

            if (!Enum.IsDefined(typeof(QuiltRendererOperationKind), kind))
            {
                error = "The Quilt Renderer operation kind is invalid.";
                return false;
            }

            for (int i = activeLeases.Count - 1; i >= 0; --i)
            {
                QuiltRendererOperationLease current = activeLeases[i];
                if (current == null || current.disposed || current.Renderer == null)
                {
                    activeLeases.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(current.Renderer, renderer))
                {
                    error = "The selected Quilt Renderer is already owned by " + current.Kind + ".";
                    return false;
                }
            }

            if (renderer.IsRendering)
            {
                error = "The selected Quilt Renderer is currently rendering.";
                return false;
            }

            lease = new QuiltRendererOperationLease(renderer, kind);
            activeLeases.Add(lease);
            return true;
        }

        public static bool IsLeased(QuiltRenderer renderer)
        {
            if (renderer == null) return false;
            for (int i = activeLeases.Count - 1; i >= 0; --i)
            {
                QuiltRendererOperationLease current = activeLeases[i];
                if (current == null || current.disposed || current.Renderer == null)
                {
                    activeLeases.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(current.Renderer, renderer)) return true;
            }

            return false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            activeLeases.Remove(this);
            Renderer = null;
        }
    }
}
