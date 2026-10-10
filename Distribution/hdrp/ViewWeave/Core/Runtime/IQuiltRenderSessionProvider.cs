using System;
using System.Collections.Generic;
using UnityEngine;

namespace ViewWeave
{
    /// <summary>Optional pipeline extension for stable, independent cameras and histories per logical view.</summary>
    public interface IQuiltRenderSessionProvider
    {
        // Returning null uses the existing single-camera render guard and preparation.
        IQuiltRenderSession BeginQuiltRender(QuiltRenderContext context);
    }

    public interface IQuiltRenderSession : IDisposable
    {
        bool IsReady { get; }
        string Status { get; }
        Camera GetViewCamera(int logicalView);
        IDisposable BeginView(Camera camera, int logicalView);
    }

    public interface IQuiltRenderResourceOwner
    {
        void ReleaseQuiltRenderResources();
    }

    public readonly struct QuiltRenderContext
    {
        public Camera SourceCamera { get; }
        public Camera ReferenceCamera { get; }
        public QuiltLayout Layout { get; }
        public QuiltSettings Settings { get; }
        public IReadOnlyList<int> RenderedViews { get; }
        public Matrix4x4 CenterProjection { get; }

        public QuiltRenderContext(Camera sourceCamera, Camera referenceCamera, QuiltLayout layout,
            QuiltSettings settings, IReadOnlyList<int> renderedViews, Matrix4x4 centerProjection)
        {
            SourceCamera = sourceCamera;
            ReferenceCamera = referenceCamera;
            Layout = layout;
            Settings = settings.Clone();
            RenderedViews = renderedViews;
            CenterProjection = centerProjection;
        }
    }
}
