using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Experimental.Rendering;

namespace ViewWeave.HDRP
{
    public enum HdrpQuiltRenderingMode { IsolatedViewHistories = 0, SharedCameraCompatibility = 1 }
    public enum HdrpQuiltExposureMode { FollowScene = 0, ManualFixed = 1 }

    /// <summary>Owns one disabled HDRP camera per rendered logical view; never shares temporal histories.</summary>
    internal sealed class HdrpViewHistoryRenderer : IDisposable
    {
        sealed class View
        {
            internal Camera camera;
            internal HDAdditionalCameraData data;
        }

        readonly Dictionary<int, View> views = new Dictionary<int, View>();
        readonly HashSet<int> requestedViews = new HashSet<int>();
        readonly List<int> removedViews = new List<int>();
        readonly Dictionary<RenderTexture, long> textureBaseline = new Dictionary<RenderTexture, long>();
        readonly Camera source;
        HdrpSharedExposureMeter meter;
        VolumeStack baseline;
        RenderPipelineAsset pipeline;
        GameObject volumeObject;
        Volume volume;
        VolumeProfile profile;
        Exposure exposureOverride;
        int tileWidth, tileHeight, viewCount;
        long graphicsBaseline;
        bool active, disposed, budgetExceeded;
        int observedQuilts;
        public int CameraCount => views.Count;
        public long GraphicsMemoryGrowthBytes { get; private set; }
        public long TextureMemoryGrowthBytes { get; private set; }
        public float SharedExposureEV { get; private set; }
        public bool ExposureReady { get; private set; }
        public string Status { get; private set; } = string.Empty;

        internal HdrpViewHistoryRenderer(Camera source) { this.source = source; }

        internal IQuiltRenderSession Begin(QuiltRenderContext context, bool followReferenceSettings,
            HdrpQuiltExposureMode exposureMode, float manualEV, int memoryBudgetMB, int maximumCameras)
        {
            if (disposed || active) throw new InvalidOperationException("HDRP view history renderer is unavailable or already active");
            if (context.SourceCamera != source) throw new InvalidOperationException("HDRP view history source camera does not match");
            if (memoryBudgetMB <= 0) throw new ArgumentOutOfRangeException(nameof(memoryBudgetMB), "HDRP history memory budget must be positive");
            if (maximumCameras <= 0 || context.RenderedViews.Count > maximumCameras)
                throw new InvalidOperationException("HDRP rendered view count exceeds Maximum History Cameras; reduce Quilt View Render Mode or increase the limit");
            budgetExceeded = false;
            HdrpQuiltIntegration.Validate(source);
            if (pipeline != GraphicsSettings.currentRenderPipeline || tileWidth != context.Layout.TileWidth ||
                tileHeight != context.Layout.TileHeight || viewCount != context.Layout.ViewCount)
            {
                ReleaseResources();
                pipeline = GraphicsSettings.currentRenderPipeline;
                tileWidth = context.Layout.TileWidth; tileHeight = context.Layout.TileHeight; viewCount = context.Layout.ViewCount;
                graphicsBaseline = Profiler.GetAllocatedMemoryForGraphicsDriver();
                CaptureTextureBaseline();
            }
            Camera settingsCamera = followReferenceSettings && context.ReferenceCamera != null ? context.ReferenceCamera : source;
            var settingsData = settingsCamera.GetComponent<HDAdditionalCameraData>();
            if (settingsData == null) throw new InvalidOperationException("HDRP rendering settings camera requires HDAdditionalCameraData");
            if (baseline == null || !baseline.isValid) baseline = VolumeManager.instance.CreateStack();
            // Exposure is metered at the central pose, before a private exposure override is enabled.
            VolumeManager.instance.Update(baseline, settingsData.volumeAnchorOverride != null ? settingsData.volumeAnchorOverride : source.transform,
                settingsData.volumeLayerMask);
            Exposure sceneExposure = baseline.GetComponent<Exposure>();
            ExposureReady = false;
            if (exposureMode == HdrpQuiltExposureMode.ManualFixed)
            {
                if (float.IsNaN(manualEV) || float.IsInfinity(manualEV)) throw new ArgumentOutOfRangeException(nameof(manualEV));
                SharedExposureEV = manualEV;
                ExposureReady = true;
            }
            else if (sceneExposure != null && sceneExposure.mode.value == ExposureMode.Fixed)
            {
                SharedExposureEV = sceneExposure.fixedExposure.value - sceneExposure.compensation.value;
                ExposureReady = true;
            }
            else
            {
                if (meter == null) meter = new HdrpSharedExposureMeter();
                ExposureReady = meter.TryMeasure(source, settingsData, context.Settings, context.Layout, out float measuredEV, out string meterStatus);
                try { CheckMemoryBudget((long)memoryBudgetMB * 1024 * 1024, true); }
                catch { ReleaseResources(); throw; }
                if (!ExposureReady && !string.IsNullOrEmpty(meter.LastError)) throw new NotSupportedException(meter.LastError);
                if (ExposureReady) SharedExposureEV = measuredEV;
                Status = meterStatus;
            }
            SynchronizeViews(context, settingsData);
            if (ExposureReady) Status = "HDRP isolated histories: " + CameraCount + " cameras; shared exposure EV " +
                SharedExposureEV.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "; graphics growth " +
                (GraphicsMemoryGrowthBytes / (1024f * 1024f)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB; texture growth estimate " +
                (TextureMemoryGrowthBytes / (1024f * 1024f)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            if (ExposureReady) EnsureExposureVolume();
            active = true;
            return new Session(this, ExposureReady, Status, (long)memoryBudgetMB * 1024 * 1024);
        }

        void SynchronizeViews(QuiltRenderContext context, HDAdditionalCameraData settingsData)
        {
            requestedViews.Clear();
            foreach (int logicalView in context.RenderedViews) requestedViews.Add(logicalView);
            removedViews.Clear();
            foreach (int logicalView in views.Keys) if (!requestedViews.Contains(logicalView)) removedViews.Add(logicalView);
            foreach (int logicalView in removedViews) { DestroyView(views[logicalView]); views.Remove(logicalView); }
            foreach (int logicalView in context.RenderedViews)
            {
                if (!views.TryGetValue(logicalView, out View view))
                {
                    var host = new GameObject("ViewWeave HDRP View " + logicalView) { hideFlags = HideFlags.HideAndDontSave };
                    host.transform.SetParent(source.transform, false);
                    view = new View { camera = host.AddComponent<Camera>(), data = host.AddComponent<HDAdditionalCameraData>() };
                    views.Add(logicalView, view);
                }
                view.camera.CopyFrom(source);
                settingsData.CopyTo(view.data);
                view.data.hasPersistentHistory = true;
                view.camera.enabled = false;
                view.camera.targetTexture = null;
            }
        }

        void EnsureExposureVolume()
        {
            if (volume == null)
            {
                bool[] occupied = new bool[32];
                foreach (Volume existing in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include))
                    if (existing != null) occupied[existing.gameObject.layer] = true;
                int layer = 30;
                if (occupied[layer])
                {
                    layer = -1;
                    for (int i = 0; i < 32; ++i) if (!occupied[i]) { layer = i; break; }
                }
                if (layer < 0) throw new InvalidOperationException("HDRP shared exposure requires an unoccupied Volume layer");
                volumeObject = new GameObject("ViewWeave HDRP Shared Exposure") { hideFlags = HideFlags.HideAndDontSave, layer = layer };
                volumeObject.transform.SetParent(source.transform, false);
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                profile.hideFlags = HideFlags.HideAndDontSave;
                exposureOverride = profile.Add<Exposure>(false);
                exposureOverride.mode.Override(ExposureMode.Fixed);
                exposureOverride.compensation.Override(0f);
                volume = volumeObject.AddComponent<Volume>();
                volume.isGlobal = true; volume.priority = float.MaxValue; volume.weight = 1f;
                volume.sharedProfile = profile; volume.enabled = false;
            }
            exposureOverride.fixedExposure.Override(SharedExposureEV);
        }

        internal Camera GetCamera(int logicalView) => views.TryGetValue(logicalView, out View view) ? view.camera : null;

        void CaptureTextureBaseline()
        {
            textureBaseline.Clear();
            foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                if (texture != null && texture.IsCreated()) textureBaseline[texture] = EstimateTextureBytes(texture);
        }

        // Public descriptors remain available in Release Players, where the graphics-driver counter returns zero.
        // Include new/grown textures conservatively; never subtract an unrelated texture that was freed.
        // This is a texture allocation estimate, not a measurement of complete per-camera GPU memory.
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

        void CheckMemoryBudget(long budget, bool observeTextures)
        {
            long measured = Profiler.GetAllocatedMemoryForGraphicsDriver();
            if (measured > 0 && graphicsBaseline > 0)
                GraphicsMemoryGrowthBytes = Math.Max(GraphicsMemoryGrowthBytes, Math.Max(0, measured - graphicsBaseline));
            if (observeTextures)
            {
                long growth = 0;
                foreach (RenderTexture texture in Resources.FindObjectsOfTypeAll<RenderTexture>())
                {
                    if (texture == null || !texture.IsCreated()) continue;
                    textureBaseline.TryGetValue(texture, out long originalBytes);
                    growth += Math.Max(0, EstimateTextureBytes(texture) - originalBytes);
                }
                TextureMemoryGrowthBytes = Math.Max(TextureMemoryGrowthBytes, growth);
            }
            if (Math.Max(GraphicsMemoryGrowthBytes, TextureMemoryGrowthBytes) > budget)
            {
                budgetExceeded = true;
                throw new InsufficientMemoryException("HDRP History Resource Budget exceeded: observed graphics growth " +
                    (GraphicsMemoryGrowthBytes / (1024L * 1024L)) + " MB; estimated texture growth " +
                    (TextureMemoryGrowthBytes / (1024L * 1024L)) + " MB; budget " + (budget / (1024L * 1024L)) +
                    " MB. Reduce Output Quality or rendered view count, or increase the budget.");
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReleaseResources();
        }

        void ReleaseResources()
        {
            if (volume != null) volume.enabled = false;
            foreach (View view in views.Values) DestroyView(view);
            views.Clear();
            meter?.Dispose(); meter = null;
            if (baseline != null && baseline.isValid && VolumeManager.instance.isInitialized) VolumeManager.instance.DestroyStack(baseline);
            baseline = null;
            DestroyObject(volumeObject); volumeObject = null; volume = null;
            if (profile != null)
            {
                foreach (VolumeComponent component in profile.components) DestroyObject(component);
                DestroyObject(profile);
            }
            profile = null; exposureOverride = null;
            ExposureReady = false; GraphicsMemoryGrowthBytes = 0; TextureMemoryGrowthBytes = 0;
            textureBaseline.Clear(); observedQuilts = 0;
            pipeline = null; tileWidth = tileHeight = viewCount = 0;
        }

        static void DestroyView(View view)
        {
            if (view.camera == null) return;
            view.data.hasPersistentHistory = false;
            DestroyObject(view.camera.gameObject);
        }

        static void DestroyObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }

        sealed class Session : IQuiltRenderSession
        {
            readonly HdrpViewHistoryRenderer owner;
            readonly long budget;
            bool disposed;
            int completedViews;
            ViewScope currentScope;
            public bool IsReady { get; }
            public string Status { get; }
            internal Session(HdrpViewHistoryRenderer owner, bool ready, string status, long budget)
            { this.owner = owner; IsReady = ready; Status = status; this.budget = budget; }
            public Camera GetViewCamera(int logicalView)
            {
                if (disposed || !IsReady) throw new InvalidOperationException("HDRP render session is not ready");
                Camera camera = owner.GetCamera(logicalView);
                if (camera == null) throw new InvalidOperationException("HDRP logical view camera is unavailable");
                return camera;
            }
            public IDisposable BeginView(Camera camera, int logicalView)
            {
                if (disposed || !IsReady || currentScope != null || camera != GetViewCamera(logicalView))
                    throw new InvalidOperationException("HDRP view scope is unavailable or does not match its logical view");
                owner.CheckMemoryBudget(budget, false);
                currentScope = new ViewScope(this, camera.GetComponent<HDAdditionalCameraData>());
                return currentScope;
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { currentScope?.Dispose(); }
                finally
                {
                    owner.active = false;
                    if (owner.budgetExceeded) owner.ReleaseResources();
                    else if (IsReady) owner.observedQuilts++;
                }
            }
            sealed class ViewScope : IDisposable
            {
                readonly Session session;
                readonly HDAdditionalCameraData data;
                readonly LayerMask originalMask;
                bool disposed;
                internal ViewScope(Session session, HDAdditionalCameraData data)
                {
                    this.session = session; this.data = data; originalMask = data.volumeLayerMask;
                    data.volumeLayerMask = originalMask.value | (1 << session.owner.volumeObject.layer);
                    session.owner.volume.enabled = true;
                }
                public void Dispose()
                {
                    if (disposed) return;
                    disposed = true;
                    if (session.owner.volume != null) session.owner.volume.enabled = false;
                    if (data != null) data.volumeLayerMask = originalMask;
                    session.currentScope = null;
                    // Check every initial view allocation; once warm, scan at the final rendered view only.
                    session.completedViews++;
                    bool lastView = session.completedViews == session.owner.requestedViews.Count;
                    session.owner.CheckMemoryBudget(session.budget, session.owner.observedQuilts < 3 || lastView);
                }
            }
        }
    }
}
