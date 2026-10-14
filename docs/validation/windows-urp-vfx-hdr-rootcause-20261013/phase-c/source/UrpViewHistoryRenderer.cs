using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ViewWeave.URP
{
    public enum UrpQuiltRenderingMode { IsolatedViewHistories = 0, SharedCameraCompatibility = 1 }

    /// <summary>Each logical view owns its Camera, URP history and optional scripted VolumeStack.</summary>
    internal sealed class UrpViewHistoryRenderer : IDisposable
    {
        sealed class View
        {
            internal Camera camera;
            internal UniversalAdditionalCameraData data;
            internal int rendererIndex = -1;
            internal VolumeStack stack;
        }
        readonly Camera source;
        readonly Dictionary<int, View> views = new Dictionary<int, View>();
        readonly HashSet<int> requested = new HashSet<int>();
        readonly List<int> removed = new List<int>();
        readonly QuiltHistoryResourceBudget resourceBudget = new QuiltHistoryResourceBudget();
        sealed class CurveCopy { internal string json; internal TextureCurve value; }
        sealed class CurveParameterIdentity : IEqualityComparer<TextureCurveParameter>
        {
            public bool Equals(TextureCurveParameter left, TextureCurveParameter right) => ReferenceEquals(left, right);
            public int GetHashCode(TextureCurveParameter value) => RuntimeHelpers.GetHashCode(value);
        }
        // VolumeParameter hashes depend on mutable values/overrideState; ownership keys must use identity.
        readonly Dictionary<TextureCurveParameter, CurveCopy> curveCopies =
            new Dictionary<TextureCurveParameter, CurveCopy>(new CurveParameterIdentity());
        readonly Dictionary<TextureCurve, string> serializedCurves = new Dictionary<TextureCurve, string>();
        VolumeStack validationStack;
        VolumeStack originalGlobalStack;
        RenderPipelineAsset pipeline;
        UniversalAdditionalCameraData previousSettingsData;
        RenderTexture sharedHdrIntermediate;
        int tileWidth, tileHeight, viewCount, previousRendererIndex = -1, observedQuilts;
        bool active, disposed, previousResetHistory;
        public int CameraCount => views.Count;
        public long GraphicsMemoryGrowthBytes => resourceBudget.GraphicsGrowthBytes;
        public long TextureMemoryGrowthBytes => resourceBudget.TextureGrowthBytes;
        public string Status { get; private set; } = string.Empty;

        internal UrpViewHistoryRenderer(Camera source) { this.source = source; }
        internal Camera GetCamera(int logicalView) => views.TryGetValue(logicalView, out View view) ? view.camera : null;

        internal IQuiltRenderSession Begin(QuiltRenderContext context, bool followReferenceSettings, int budgetMB, int maximumCameras)
        {
            if (disposed || active) throw new InvalidOperationException("URP view history renderer is unavailable or already active");
            if (context.SourceCamera != source) throw new InvalidOperationException("URP history source camera does not match");
            if (budgetMB <= 0) throw new ArgumentOutOfRangeException(nameof(budgetMB));
            if (maximumCameras <= 0 || context.RenderedViews.Count > maximumCameras)
                throw new InvalidOperationException("URP rendered view count exceeds Maximum History Cameras; reduce Quilt View Render Mode or increase the limit");
            UrpQuiltIntegration.Validate(source);
            Camera settingsCamera = followReferenceSettings && context.ReferenceCamera != null ? context.ReferenceCamera : source;
            UrpQuiltIntegration.Validate(settingsCamera);
            var settingsData = settingsCamera.GetComponent<UniversalAdditionalCameraData>();
            var urp = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            int rendererIndex = ResolveRendererIndex(urp, settingsData);
            ValidateTemporalSettings(source, settingsCamera, settingsData);
            if (pipeline != urp || tileWidth != context.Layout.TileWidth || tileHeight != context.Layout.TileHeight ||
                viewCount != context.Layout.ViewCount || previousSettingsData != settingsData || previousRendererIndex != rendererIndex)
            {
                ReleaseResources();
                pipeline = urp; tileWidth = context.Layout.TileWidth; tileHeight = context.Layout.TileHeight;
                viewCount = context.Layout.ViewCount; previousSettingsData = settingsData; previousRendererIndex = rendererIndex;
                resourceBudget.CaptureBaseline();
            }
            serializedCurves.Clear();
            originalGlobalStack = VolumeManager.instance.stack;
            if (validationStack == null || !validationStack.isValid) validationStack = VolumeManager.instance.CreateStack();
            VolumeStack authoredStack = Application.isPlaying && !settingsData.requiresVolumeFrameworkUpdate ? settingsData.volumeStack : null;
            if (authoredStack != null && authoredStack.isValid) CopyStack(authoredStack, validationStack);
            else
            {
                VolumeManager.instance.Update(validationStack, settingsData.volumeTrigger != null ? settingsData.volumeTrigger : source.transform, settingsData.volumeLayerMask);
                OwnCurveValues(validationStack);
            }
            ValidateMotionBlur(validationStack, settingsData);
            bool resetHistory = settingsData.resetHistory && !previousResetHistory;
            previousResetHistory = settingsData.resetHistory;
            requested.Clear(); foreach (int logicalView in context.RenderedViews) requested.Add(logicalView);
            removed.Clear(); foreach (int logicalView in views.Keys) if (!requested.Contains(logicalView)) removed.Add(logicalView);
            foreach (int logicalView in removed) { DestroyView(views[logicalView]); views.Remove(logicalView); }
            foreach (int logicalView in context.RenderedViews)
            {
                if (!views.TryGetValue(logicalView, out View view))
                {
                    var host = new GameObject("ViewWeave URP View " + logicalView) { hideFlags = HideFlags.HideAndDontSave };
                    host.transform.SetParent(source.transform, false);
                    view = new View { camera = host.AddComponent<Camera>(), data = host.AddComponent<UniversalAdditionalCameraData>() };
                    views.Add(logicalView, view);
                }
                view.camera.CopyFrom(source);
                view.camera.enabled = false; view.camera.targetTexture = null;
                CopyRenderingSettings(settingsCamera, settingsData, view, rendererIndex);
                if (resetHistory) view.data.resetHistory = true;
                // Edit Mode forces live volume evaluation; it does not consume a scripted camera stack.
                if (Application.isPlaying && !settingsData.requiresVolumeFrameworkUpdate)
                {
                    if (view.data.volumeStack == null || !view.data.volumeStack.isValid) view.data.volumeStack = VolumeManager.instance.CreateStack();
                    view.stack = view.data.volumeStack;
                    CopyStack(validationStack, view.stack);
                }
                else DestroyOwnedStack(view);
            }
            Status = "URP isolated histories: " + views.Count + " cameras; " + settingsData.antialiasing +
                "; graphics growth " + (GraphicsMemoryGrowthBytes / (1024L * 1024L)) + " MB; texture growth estimate " +
                (TextureMemoryGrowthBytes / (1024L * 1024L)) + " MB";
            active = true;
            return new Session(this, (long)budgetMB * 1024 * 1024, Status);
        }

        static int ResolveRendererIndex(UniversalRenderPipelineAsset urp, UniversalAdditionalCameraData data)
        {
            for (int i = 0; i < urp.rendererDataList.Length; i++)
                if (urp.rendererDataList[i] != null && ReferenceEquals(urp.GetRenderer(i), data.scriptableRenderer)) return i;
            throw new NotSupportedException("URP Reference Camera renderer is unavailable in the active pipeline asset");
        }

        static void ValidateTemporalSettings(Camera source, Camera settingsCamera, UniversalAdditionalCameraData settingsData)
        {
            if (settingsData.antialiasing != AntialiasingMode.TemporalAntiAliasing) return;
            if (!settingsData.renderPostProcessing) throw new NotSupportedException("URP Quilt TAA requires Post Processing on the rendering settings camera");
            if (UrpQuiltCamera.ResolveMsaaSampleCount(source, settingsCamera) > 1)
                throw new NotSupportedException("URP Quilt TAA requires a single-sample target; disable MSAA on the Rig Source Camera or in the URP asset");
            if (source.allowDynamicResolution)
                throw new NotSupportedException("URP Quilt TAA does not support dynamic resolution; disable Allow Dynamic Resolution on the Rig Source Camera");
            if (!(settingsData.scriptableRenderer is UniversalRenderer))
                throw new NotSupportedException("URP Quilt TAA requires a Universal Renderer with motion vector support");
        }

        void CopyRenderingSettings(Camera settingsCamera, UniversalAdditionalCameraData from, View view, int rendererIndex)
        {
            var to = view.data;
            if (view.rendererIndex != rendererIndex) { to.SetRenderer(rendererIndex); view.rendererIndex = rendererIndex; }
            to.renderType = CameraRenderType.Base;
            to.renderPostProcessing = from.renderPostProcessing; to.renderShadows = from.renderShadows;
            to.requiresColorOption = from.requiresColorOption; to.requiresDepthOption = from.requiresDepthOption;
            to.antialiasing = from.antialiasing; to.antialiasingQuality = from.antialiasingQuality;
            to.stopNaN = from.stopNaN; to.dithering = from.dithering;
            to.allowXRRendering = from.allowXRRendering; to.allowHDROutput = from.allowHDROutput;
            to.useScreenCoordOverride = from.useScreenCoordOverride;
            to.screenSizeOverride = from.screenSizeOverride; to.screenCoordScaleBias = from.screenCoordScaleBias;
            to.volumeLayerMask = from.volumeLayerMask; to.volumeTrigger = from.volumeTrigger;
            var mode = settingsCamera.GetVolumeFrameworkUpdateMode();
            if (view.camera.GetVolumeFrameworkUpdateMode() != mode)
            {
                DestroyOwnedStack(view);
                view.camera.SetVolumeFrameworkUpdateMode(mode);
                // The public setter can acquire a stack from URP; this view now owns that lease.
                view.stack = view.data.volumeStack;
            }
            // Copy only authored TAA settings. Whole-struct assignment/resetHistory=false resets view-local counters.
            to.taaSettings.quality = from.taaSettings.quality;
            to.taaSettings.baseBlendFactor = from.taaSettings.baseBlendFactor;
            to.taaSettings.jitterScale = from.taaSettings.jitterScale;
            to.taaSettings.mipBias = from.taaSettings.mipBias;
            to.taaSettings.varianceClampScale = from.taaSettings.varianceClampScale;
            to.taaSettings.contrastAdaptiveSharpening = from.taaSettings.contrastAdaptiveSharpening;
        }

        void CopyStack(VolumeStack from, VolumeStack to)
        {
            foreach (Type type in VolumeManager.instance.baseComponentTypeArray)
            {
                VolumeComponent original = from.GetComponent(type), copy = to.GetComponent(type);
                if (original == null || copy == null) continue;
                copy.active = original.active;
                for (int i = 0; i < original.parameters.Count; i++)
                {
                    if (original.parameters[i] is TextureCurveParameter originalCurve && copy.parameters[i] is TextureCurveParameter copyCurve)
                        CopyCurve(originalCurve.value, copyCurve);
                    else copy.parameters[i].SetValue(original.parameters[i]);
                    copy.parameters[i].overrideState = original.parameters[i].overrideState;
                }
            }
        }
        void OwnCurveValues(VolumeStack stack)
        {
            if (stack == null || !stack.isValid) return;
            foreach (Type type in VolumeManager.instance.baseComponentTypeArray)
            {
                var component = stack.GetComponent(type);
                if (component == null) continue;
                foreach (var parameter in component.parameters)
                    if (parameter is TextureCurveParameter curve) CopyCurve(curve.value, curve);
            }
        }
        void CopyCurve(TextureCurve from, TextureCurveParameter to)
        {
            if (from == null) return;
            if (!serializedCurves.TryGetValue(from, out string json))
            {
                json = JsonUtility.ToJson(from); serializedCurves.Add(from, json);
            }
            if (!curveCopies.TryGetValue(to, out CurveCopy copy) || copy.json != json)
            {
                copy?.value?.Release();
                copy = new CurveCopy { json = json, value = JsonUtility.FromJson<TextureCurve>(json) };
                copy.value.SetDirty(); // Rebuild nonserialized loop/evaluation state as well as its GPU texture.
                curveCopies[to] = copy;
            }
            to.value = copy.value;
        }
        void DestroyOwnedStack(View view)
        {
            VolumeStack stack = view.stack ?? view.data.volumeStack;
            if (stack == null) return;
            DestroyStack(stack);
            // Destroy first: assigning null on a valid stack would return it to URP's static cache.
            view.data.volumeStack = null;
            view.stack = null;
        }
        void DestroyStack(VolumeStack stack)
        {
            if (stack == null || !stack.isValid) return;
            if (VolumeManager.instance.stack == stack && originalGlobalStack != null && originalGlobalStack.isValid)
                VolumeManager.instance.stack = originalGlobalStack;
            // Manager evaluation can borrow profile curves. Own them before releasing the stack.
            OwnCurveValues(stack);
            foreach (Type type in VolumeManager.instance.baseComponentTypeArray)
            {
                var component = stack.GetComponent(type);
                if (component == null) continue;
                foreach (var parameter in component.parameters)
                    if (parameter is TextureCurveParameter curve) curveCopies.Remove(curve);
            }
            VolumeManager.instance.DestroyStack(stack);
        }
        static void ValidateMotionBlur(VolumeStack stack, UniversalAdditionalCameraData data)
        {
            var blur = stack.GetComponent<MotionBlur>();
            if (data.renderPostProcessing && blur != null && blur.IsActive())
                throw new NotSupportedException("URP Motion Blur is not validated for quilt views; disable it in the scene Volume");
        }

        public void Dispose() { if (disposed) return; disposed = true; if (!active) ReleaseResources(); }
        void ReleaseResources()
        {
            foreach (View view in views.Values) DestroyView(view);
            views.Clear();
            ReleaseHdrIntermediate();
            if (validationStack != null && VolumeManager.instance.isInitialized) DestroyStack(validationStack);
            validationStack = null; pipeline = null; previousSettingsData = null;
            tileWidth = tileHeight = viewCount = observedQuilts = 0; previousRendererIndex = -1; previousResetHistory = false;
            resourceBudget.Reset(); curveCopies.Clear(); serializedCurves.Clear(); originalGlobalStack = null;
        }
        void DestroyView(View view)
        {
            if (view.camera == null) return;
            // Public UAC OnDestroy releases Camera histories. Disable first when Destroy is deferred in Play Mode.
            view.camera.enabled = false;
            DestroyOwnedStack(view);
            if (Application.isPlaying) UnityEngine.Object.Destroy(view.camera.gameObject);
            else UnityEngine.Object.DestroyImmediate(view.camera.gameObject);
        }

        void ReleaseHdrIntermediate()
        {
            if (sharedHdrIntermediate == null) return;
            sharedHdrIntermediate.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(sharedHdrIntermediate);
            else UnityEngine.Object.DestroyImmediate(sharedHdrIntermediate);
            sharedHdrIntermediate = null;
        }

        RenderTexture GetOrCreateHdrIntermediate(RenderTexture destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (destination.antiAliasing != 1)
                throw new NotSupportedException("The bounded URP HDR intermediate probe requires a single-sample destination.");
            if (sharedHdrIntermediate != null &&
                (sharedHdrIntermediate.width != destination.width || sharedHdrIntermediate.height != destination.height || sharedHdrIntermediate.antiAliasing != 1))
                ReleaseHdrIntermediate();
            if (sharedHdrIntermediate == null)
            {
                sharedHdrIntermediate = new RenderTexture(destination.width, destination.height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
                {
                    name = "ViewWeave URP HDR Intermediate Probe",
                    antiAliasing = 1,
                    useDynamicScale = false,
                    hideFlags = HideFlags.HideAndDontSave
                };
                if (!sharedHdrIntermediate.Create())
                {
                    ReleaseHdrIntermediate();
                    throw new InvalidOperationException("Could not create the bounded URP ARGBHalf linear intermediate target.");
                }
            }
            return sharedHdrIntermediate;
        }

        sealed class Session : IQuiltRenderSession, IQuiltViewRenderExecutor
        {
            readonly UrpViewHistoryRenderer owner;
            readonly long budget;
            bool disposed, failed;
            int completedViews;
            Camera currentCamera;
            public bool IsReady => !disposed;
            public string Status { get; }
            internal Session(UrpViewHistoryRenderer owner, long budget, string status) { this.owner = owner; this.budget = budget; Status = status; }
            public Camera GetViewCamera(int logicalView)
            {
                if (disposed) throw new ObjectDisposedException(nameof(Session));
                return owner.GetCamera(logicalView) ?? throw new InvalidOperationException("URP logical view camera is unavailable");
            }
            public IDisposable BeginView(Camera camera, int logicalView)
            {
                if (disposed || currentCamera != null || camera != GetViewCamera(logicalView))
                    throw new InvalidOperationException("URP view scope does not match its logical view");
                CheckBudget(false, "before_view", logicalView);
                var data = camera.GetComponent<UniversalAdditionalCameraData>();
                if (!Application.isPlaying || data.requiresVolumeFrameworkUpdate)
                {
                    VolumeManager.instance.Update(owner.validationStack, data.volumeTrigger != null ? data.volumeTrigger : camera.transform, data.volumeLayerMask);
                    owner.OwnCurveValues(owner.validationStack);
                }
                else owner.CopyStack(data.volumeStack, owner.validationStack);
                ValidateMotionBlur(owner.validationStack, data);
                currentCamera = camera;
                return new ViewScope(this, logicalView);
            }
            public void RenderView(Camera camera, RenderTexture destination)
            {
                if (disposed || camera != currentCamera || camera.enabled || camera.targetTexture != destination)
                    throw new InvalidOperationException("URP view rendering requires a disabled camera and the session's target");
                try
                {
                    ValidateMotionBlur(owner.validationStack, camera.GetComponent<UniversalAdditionalCameraData>());
                    bool hdrIntermediateProbe = string.Equals(Environment.GetEnvironmentVariable("VW_URP_HDR_INTERMEDIATE_PROBE"), "1", StringComparison.Ordinal);
                    UniversalRenderPipelineAsset urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                    UniversalAdditionalCameraData cameraData = camera.GetComponent<UniversalAdditionalCameraData>();
                    bool useHdrIntermediate = hdrIntermediateProbe && Application.isPlaying && camera.allowHDR && urp != null && urp.supportsHDR;
                    if (hdrIntermediateProbe)
                    {
                        if (!Application.isPlaying)
                            throw new NotSupportedException("The bounded URP HDR intermediate probe is Play Mode only.");
                        if (cameraData == null || cameraData.antialiasing != AntialiasingMode.None)
                            throw new NotSupportedException("The bounded URP HDR intermediate probe requires per-camera antialiasing to be Off.");
                        if (destination == null || destination.antiAliasing != 1)
                            throw new NotSupportedException("The bounded URP HDR intermediate probe requires a single-sample destination.");
                    }
                    if (useHdrIntermediate)
                    {
                        RenderWithHdrIntermediate(camera, destination);
                        return;
                    }
                    // StandardRequest explicitly suppresses URP TAA history writes and motion vectors.
                    // Camera.Render follows the supported URP path with isProcessingRenderRequest=false.
                    VolumeStack previousStack = VolumeManager.instance.stack;
                    try { camera.Render(); }
                    finally { if (previousStack != null && previousStack.isValid) VolumeManager.instance.stack = previousStack; }
                }
                catch { failed = true; throw; }
            }

            void RenderWithHdrIntermediate(Camera camera, RenderTexture destination)
            {
                RenderTexture hdrTarget = owner.GetOrCreateHdrIntermediate(destination);
                RenderTexture previousTarget = camera.targetTexture;
                RenderTexture previousActive = RenderTexture.active;
                VolumeStack previousStack = VolumeManager.instance.stack;
                bool previousSrgbWrite = GL.sRGBWrite;
                try
                {
                    camera.targetTexture = hdrTarget;
                    camera.Render();
                    camera.targetTexture = destination;
                    GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear && destination.sRGB;
                    Graphics.Blit(hdrTarget, destination);
                }
                finally
                {
                    camera.targetTexture = previousTarget;
                    RenderTexture.active = previousActive;
                    GL.sRGBWrite = previousSrgbWrite;
                    if (previousStack != null && previousStack.isValid) VolumeManager.instance.stack = previousStack;
                }
            }
            void CheckBudget(bool observeTextures, string phase, int logicalView)
            {
                try { owner.resourceBudget.Observe(budget, observeTextures, "URP"); }
                catch (InsufficientMemoryException exception)
                {
                    failed = true;
                    InsufficientMemoryException contextual = null;
                    try
                    {
                        var context = new StringBuilder(224);
                        context.Append("; URP view context: phase=").Append(phase)
                            .Append("; logical view=").Append(logicalView.ToString(CultureInfo.InvariantCulture))
                            .Append("; completed views=").Append(completedViews.ToString(CultureInfo.InvariantCulture))
                            .Append("; requested views=").Append(owner.requested.Count.ToString(CultureInfo.InvariantCulture))
                            .Append("; history cameras=").Append(owner.views.Count.ToString(CultureInfo.InvariantCulture))
                            .Append("; tile width=").Append(owner.tileWidth.ToString(CultureInfo.InvariantCulture))
                            .Append("; tile height=").Append(owner.tileHeight.ToString(CultureInfo.InvariantCulture))
                            .Append("; layout views=").Append(owner.viewCount.ToString(CultureInfo.InvariantCulture));
                        string effectiveAntialiasing = "unknown";
                        try
                        {
                            Camera viewCamera = owner.GetCamera(logicalView);
                            UniversalAdditionalCameraData viewData = viewCamera != null
                                ? viewCamera.GetComponent<UniversalAdditionalCameraData>()
                                : null;
                            if (viewData != null) effectiveAntialiasing = viewData.antialiasing.ToString();
                        }
                        catch { }
                        context.Append("; per-view effective antialiasing=").Append(effectiveAntialiasing)
                            .Append("; frame=").Append(Time.frameCount.ToString(CultureInfo.InvariantCulture))
                            .Append("; mode=").Append(Application.isPlaying ? "Play" : "Editor");
                        contextual = new InsufficientMemoryException(exception.Message + context, exception);
                    }
                    catch { }
                    if (contextual != null) throw contextual;
                    throw;
                }
                catch { failed = true; throw; }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true; currentCamera = null; owner.active = false;
                if (failed || owner.disposed) owner.ReleaseResources(); else owner.observedQuilts++;
            }
            sealed class ViewScope : IDisposable
            {
                readonly Session session;
                readonly int logicalView;
                bool disposed;
                internal ViewScope(Session session, int logicalView)
                {
                    this.session = session;
                    this.logicalView = logicalView;
                }
                public void Dispose()
                {
                    if (disposed) return; disposed = true;
                    session.currentCamera = null; session.completedViews++;
                    session.CheckBudget(session.owner.observedQuilts < 3 || session.completedViews == session.owner.requested.Count,
                        "after_view", logicalView);
                }
            }
        }
    }
}
