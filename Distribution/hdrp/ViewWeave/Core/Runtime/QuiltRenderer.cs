using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace ViewWeave
{
    [DisallowMultipleComponent]
    public sealed class QuiltRenderer : MonoBehaviour
    {
        public QuiltSettings settings = new QuiltSettings();
        [Tooltip("Use physical aperture dimensions and units-per-meter to derive the view aspect and zero-parallax distance.")]
        public QuiltPhysicalFraming physicalFraming = new QuiltPhysicalFraming();
        [Tooltip("The output quilt stays full size. Half and Quarter render ceil(view count / divisor) views, reuse the nearest rendered image for each output tile, and can reduce angular smoothness. At least the first and last views are rendered when view count is greater than one.")]
        public QuiltViewRenderMode viewRenderMode = QuiltViewRenderMode.Full;
        [Tooltip("Disabled dedicated camera used only to render quilt views.")]
        public Camera sourceCamera;
        [Tooltip("Optional live framing camera. Assign the rendered output camera, such as the Camera controlled by a Cinemachine Brain. Its world position and rotation are copied to the dedicated source camera before each quilt render.")]
        public Camera referenceCamera;
        [Tooltip("When enabled, the reference camera's vertical field of view updates Quilt Settings before each render. A manually entered value is replaced while this is enabled.")]
        public bool followReferenceFieldOfView = true;
        public Camera EffectiveCamera => referenceCamera != null ? referenceCamera : sourceCamera;
        RenderTexture quiltTexture;
        public RenderTexture QuiltTexture => quiltTexture;
        [SerializeField] MonoBehaviour outputBehaviour;
        [SerializeField] MonoBehaviour guardBehaviour;
        // Runtime-only fail-closed marker. Editor preview retention has its own serialized owner
        // record; serializing this marker would leave ordinary saved scenes permanently locked.
        [NonSerialized] bool mutationOwnerRequired;
        internal bool HasMutationOwnerRequirement => mutationOwnerRequired;
        internal void SetMutationOwnerRequired(bool required) => mutationOwnerRequired = required;
#if UNITY_EDITOR
        [SerializeField, HideInInspector] UnityEngine.Object editorPreviewRetentionOwner;
        [SerializeField, HideInInspector] bool editorPreviewRetentionRequired;
        internal bool HasEditorPreviewRetentionOwner => editorPreviewRetentionRequired && editorPreviewRetentionOwner != null;
#endif
        [FormerlySerializedAs("renderInLateUpdate")]
        public bool autoRender;
        [Obsolete("Use autoRender; automatic rendering now runs after scene rendering.")]
        public bool renderInLateUpdate { get => autoRender; set => autoRender = value; }
        public IQuiltOutput Output => outputBehaviour as IQuiltOutput;
        public IQuiltRenderGuard RenderGuard => guardBehaviour as IQuiltRenderGuard;
        public MonoBehaviour OutputBehaviour { get => outputBehaviour; set => outputBehaviour = value; }
        public MonoBehaviour GuardBehaviour { get => guardBehaviour; set => guardBehaviour = value; }
        public bool useRenderRequests = true;
        public bool IsRendering { get; private set; }
        internal bool IsNotifyingRenderComplete => notifyingRenderComplete;
        public int LastRenderedViewCount { get; private set; }
        public bool HasRenderedQuilt => hasCompletedQuilt;
        public long QuiltRevision => quiltRevision;
        public QuiltRenderSnapshot LatestSnapshot => latestSnapshot;
        public bool IsLatestQuiltCurrent => IsSnapshotCurrent();
        public event Action<QuiltRenderer> QuiltRendered;
        public string LastAutoRenderError { get; private set; }
        public long LastCpuSubmissionTicks { get; private set; }
        public long LastViewCpuTicks { get; private set; }
        public int ViewAntiAliasingSamples => viewTexture == null ? 0 : viewTexture.antiAliasing;
        public int ResolveAntiAliasingSamples => resolveTexture == null ? 0 : resolveTexture.antiAliasing;

        public bool TryGetEffectiveSettings(out QuiltSettings effectiveSettings, out string error)
        {
            return QuiltFramingResolver.TryResolve(settings, physicalFraming,
                QuiltFramingResolver.EffectiveFieldOfView(this), out effectiveSettings, out error);
        }

        public bool TryGetFraming(out QuiltCameraFraming framing)
        {
            framing = default;
            if (settings == null) return false;
            QuiltPhysicalFraming physical = physicalFraming;
            framing = new QuiltCameraFraming(QuiltFramingResolver.EffectiveFieldOfView(this), settings.cameraDistance,
                physical != null && physical.enabled,
                physical == null ? 0f : physical.visibleApertureWidthMillimeters,
                physical == null ? 0f : physical.visibleApertureHeightMillimeters,
                physical == null ? 1f : physical.unitsPerMeter);
            return true;
        }

        public bool TrySetFraming(QuiltCameraFraming framing, out string error)
        {
            if (!TryValidateFraming(framing, out error)) return false;
            if (settings == null)
            {
                error = "Quilt settings are required.";
                return false;
            }
            bool followReference = referenceCamera != null && followReferenceFieldOfView;

            QuiltPhysicalFraming candidatePhysical = physicalFraming == null
                ? new QuiltPhysicalFraming()
                : physicalFraming.Clone();
            candidatePhysical.enabled = framing.PhysicalFramingEnabled;
            candidatePhysical.visibleApertureWidthMillimeters = framing.ApertureWidthMillimeters;
            candidatePhysical.visibleApertureHeightMillimeters = framing.ApertureHeightMillimeters;
            candidatePhysical.unitsPerMeter = framing.UnitsPerMeter;

            if (followReference)
                referenceCamera.fieldOfView = framing.VerticalFieldOfView;
            else
                settings.fieldOfView = framing.VerticalFieldOfView;
            if (!framing.PhysicalFramingEnabled) settings.cameraDistance = framing.FocusDistance;
            physicalFraming = candidatePhysical;
            return true;
        }

        internal bool TryValidateFraming(QuiltCameraFraming framing, out string error)
        {
            error = string.Empty;
            if (!QuiltRendererMutationPolicy.TryCheck(this, new QuiltRendererMutationRequest(framing), out error)) return false;
            if (settings == null)
            {
                error = "Quilt settings are required.";
                return false;
            }
            if (!IsFinite(framing.VerticalFieldOfView) || framing.VerticalFieldOfView <= 0f || framing.VerticalFieldOfView >= 179f)
            {
                error = "Vertical field of view must be finite and between 0 and 179 degrees.";
                return false;
            }
            if (!IsFinite(framing.FocusDistance) || framing.FocusDistance <= 0f)
            {
                error = "Focus distance must be finite and greater than zero.";
                return false;
            }
            if (framing.PhysicalFramingEnabled && framing.FocusDistance != settings.cameraDistance)
            {
                error = "Focus Distance is derived while physical framing is enabled. Use the explicit focus-distance operation to preserve the physical plane.";
                return false;
            }

            QuiltPhysicalFraming candidatePhysical = physicalFraming == null
                ? new QuiltPhysicalFraming()
                : physicalFraming.Clone();
            candidatePhysical.enabled = framing.PhysicalFramingEnabled;
            candidatePhysical.visibleApertureWidthMillimeters = framing.ApertureWidthMillimeters;
            candidatePhysical.visibleApertureHeightMillimeters = framing.ApertureHeightMillimeters;
            candidatePhysical.unitsPerMeter = framing.UnitsPerMeter;
            QuiltSettings candidateSettings = settings.Clone();
            candidateSettings.fieldOfView = framing.VerticalFieldOfView;
            if (!framing.PhysicalFramingEnabled) candidateSettings.cameraDistance = framing.FocusDistance;
            if (!QuiltFramingResolver.TryResolve(candidateSettings, candidatePhysical, framing.VerticalFieldOfView,
                    out _, out error))
            {
                error = "Framing was not changed: " + error;
                return false;
            }
            return true;
        }

        public bool TrySetFocusDistance(float requestedEffectiveDistance, out string error)
        {
            error = string.Empty;
            if (!IsFinite(requestedEffectiveDistance) || requestedEffectiveDistance <= 0f)
            {
                error = "Focus distance must be finite and greater than zero.";
                return false;
            }
            if (!TryGetFraming(out QuiltCameraFraming current))
            {
                error = "Quilt settings are required.";
                return false;
            }
            if (!current.PhysicalFramingEnabled)
                return TrySetFraming(new QuiltCameraFraming(current.VerticalFieldOfView, requestedEffectiveDistance,
                    current.PhysicalFramingEnabled, current.ApertureWidthMillimeters,
                    current.ApertureHeightMillimeters, current.UnitsPerMeter), out error);

            if (!physicalFraming.TryResolve(current.VerticalFieldOfView, out _, out _, out error))
            {
                error = "Focus distance was not changed: " + error;
                return false;
            }
            float apertureHeight = physicalFraming.ApertureHeightInUnityUnits;
            float fieldOfView = 2f * (float)Math.Atan(apertureHeight / (2f * requestedEffectiveDistance)) * Mathf.Rad2Deg;
            if (!IsFinite(fieldOfView) || fieldOfView <= 0f || fieldOfView >= 179f)
            {
                error = "Focus distance does not produce a valid vertical field of view.";
                return false;
            }
            return TrySetFraming(new QuiltCameraFraming(fieldOfView, current.FocusDistance,
                current.PhysicalFramingEnabled, current.ApertureWidthMillimeters,
                current.ApertureHeightMillimeters, current.UnitsPerMeter), out error);
        }

        internal bool TryValidateFocusDistance(float requestedEffectiveDistance, out string error)
        {
            error = string.Empty;
            if (!IsFinite(requestedEffectiveDistance) || requestedEffectiveDistance <= 0f)
            {
                error = "Focus distance must be finite and greater than zero.";
                return false;
            }
            if (!TryGetFraming(out QuiltCameraFraming current))
            {
                error = "Quilt settings are required.";
                return false;
            }
            if (!current.PhysicalFramingEnabled)
                return TryValidateFraming(new QuiltCameraFraming(current.VerticalFieldOfView, requestedEffectiveDistance,
                    false, current.ApertureWidthMillimeters, current.ApertureHeightMillimeters, current.UnitsPerMeter), out error);
            if (!physicalFraming.TryResolve(current.VerticalFieldOfView, out _, out _, out error)) return false;
            float fieldOfView = 2f * (float)Math.Atan(physicalFraming.ApertureHeightInUnityUnits /
                (2f * requestedEffectiveDistance)) * Mathf.Rad2Deg;
            if (!IsFinite(fieldOfView) || fieldOfView <= 0f || fieldOfView >= 179f)
            {
                error = "Focus distance does not produce a valid vertical field of view.";
                return false;
            }
            return TryValidateFraming(new QuiltCameraFraming(fieldOfView, current.FocusDistance, true,
                current.ApertureWidthMillimeters, current.ApertureHeightMillimeters, current.UnitsPerMeter), out error);
        }

        public bool TrySetViewRenderMode(QuiltViewRenderMode mode, out string error)
        {
            error = string.Empty;
            if (!Enum.IsDefined(typeof(QuiltViewRenderMode), mode))
            {
                error = "View render mode is invalid.";
                return false;
            }
            if (!QuiltRendererMutationPolicy.TryCheck(this, new QuiltRendererMutationRequest(mode), out error)) return false;
            viewRenderMode = mode;
            return true;
        }

        public bool TryCheckMutation(QuiltRendererMutationKind kind, out string error)
        {
            return QuiltRendererMutationPolicy.TryCheck(this, kind, out error);
        }

        public bool TryCheckMutation(QuiltRendererMutationKind kind, bool value, out string error)
        {
            return QuiltRendererMutationPolicy.TryCheck(this, new QuiltRendererMutationRequest(kind, value), out error);
        }

        public bool TryCheckMutation(QuiltRendererMutationKind kind, float value, out string error)
        {
            return QuiltRendererMutationPolicy.TryCheck(this, new QuiltRendererMutationRequest(kind, value), out error);
        }

        public bool TryCheckMutation(QuiltRendererMutationRequest request, out string error)
        {
            return QuiltRendererMutationPolicy.TryCheck(this, request, out error);
        }

#if UNITY_EDITOR
        internal bool BeginEditorPreviewRetention(UnityEngine.Object ownerIdentity)
        {
            editorPreviewRetentionRequired = true;
            if (ownerIdentity == null || !(ownerIdentity is IQuiltEditorResourceRetentionOwner)) return false;
            if (editorPreviewRetentionOwner != null)
            {
                if (!ReferenceEquals(editorPreviewRetentionOwner, ownerIdentity)) return false;
                return true;
            }
            editorPreviewRetentionOwner = ownerIdentity;
            return true;
        }
#endif

#if UNITY_EDITOR
        internal RenderTexture[] DetachOwnedRenderTextures()
        {
            if (IsRendering) throw new InvalidOperationException("owned render textures cannot be detached while rendering");

            if (quiltTexture == null && viewTexture == null && resolveTexture == null)
                return Array.Empty<RenderTexture>();

            var detached = new System.Collections.Generic.List<RenderTexture>(3);
            AddDetachedTexture(detached, quiltTexture);
            AddDetachedTexture(detached, viewTexture);
            AddDetachedTexture(detached, resolveTexture);

            quiltTexture = null;
            viewTexture = null;
            resolveTexture = null;
            if (previousActive != null && detached.Contains(previousActive)) previousActive = null;
            renderedViewIndices = Array.Empty<int>();
            sourceViewAssignments = Array.Empty<int>();
            cachedSamplingViewCount = 0;
            hasSamplingCache = false;
            hasCompletedQuilt = false;
            latestSnapshot = default;
            latestReferenceCamera = null;
            latestSnapshotFollowedReferenceFieldOfView = false;
            latestReferenceFieldOfView = 0f;
            latestReferencePosition = default;
            latestReferenceRotation = Quaternion.identity;
            LastRenderedViewCount = 0;
            return detached.ToArray();
        }

        static void AddDetachedTexture(System.Collections.Generic.List<RenderTexture> textures, RenderTexture texture)
        {
            if (texture == null || textures.Contains(texture)) return;
            texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            textures.Add(texture);
        }

        internal static bool EditorPreviewRenderCallbackTrackingReady => QuiltRendererEditorCallbackTracker.IsReady;
        internal static bool EditorPreviewRenderCallbackActive => QuiltRendererEditorCallbackTracker.IsInsideRenderCallback;

        internal void RenderEditorPreviewAtBoundary(UnityEngine.Object ownerIdentity)
        {
            if (!Application.isEditor) throw new InvalidOperationException("Editor preview rendering is only available in the Unity Editor");
            if (Application.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Editor preview rendering is only available in stable Edit Mode");
            if (!QuiltRendererEditorCallbackTracker.IsReady)
                throw new InvalidOperationException("Editor preview render callback tracking is unavailable or inconsistent");
            if (QuiltRendererEditorCallbackTracker.IsInsideRenderCallback)
                throw new InvalidOperationException("Editor preview rendering must be scheduled outside SRP render callbacks");
            if (!editorPreviewRetentionRequired || ownerIdentity == null ||
                !ReferenceEquals(editorPreviewRetentionOwner, ownerIdentity) ||
                !(ownerIdentity is IQuiltEditorResourceRetentionOwner))
                throw new InvalidOperationException("Editor preview render owner does not match the serialized retention owner");
            if (notifyingRenderComplete) throw new InvalidOperationException("quilt rendering cannot be started from a quilt completion callback");
            if (IsRendering) throw new InvalidOperationException("quilt render is already in progress");
            if (GraphicsSettings.currentRenderPipeline == null)
                throw new InvalidOperationException("Editor preview rendering requires an active Scriptable Render Pipeline");

            RenderQuiltCore(false, true);
        }
#endif

        RenderTexture viewTexture;
        RenderTexture resolveTexture;
        RenderTexture previousActive;
        int[] renderedViewIndices = Array.Empty<int>();
        int[] sourceViewAssignments = Array.Empty<int>();
        int cachedSamplingViewCount;
        QuiltViewRenderMode cachedSamplingMode;
        bool hasSamplingCache;
        bool notifyingRenderComplete;
        long quiltRevision;
        bool hasCompletedQuilt;
        QuiltRenderSnapshot latestSnapshot;
        Camera latestReferenceCamera;
        bool latestSnapshotFollowedReferenceFieldOfView;
        float latestReferenceFieldOfView;
        Vector3 latestReferencePosition;
        Quaternion latestReferenceRotation;

        void OnEnable()
        {
            ResetAutoRenderError();
            if (sourceCamera == null) sourceCamera = GetComponent<Camera>();
            QuiltAutoRenderScheduler.Register(this);
        }
        public void ResetAutoRenderError()
        {
            autoRenderFailed = false;
            LastAutoRenderError = string.Empty;
        }
        bool autoRenderFailed;
        static string SanitizeAutoRenderError(Exception error)
        {
            string value = error == null ? "unknown error" : error.GetType().Name + ": " + error.Message;
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }
        void OnDisable() { QuiltAutoRenderScheduler.Unregister(this); ReleaseOrRetainTextures(); }
        void OnDestroy() { QuiltAutoRenderScheduler.Unregister(this); ReleaseOrRetainTextures(); }

        public void RenderQuilt()
        {
            RenderQuiltCore(false, false);
        }

        internal void RenderAutoQuiltAtBoundary()
        {
            if (!autoRender || !isActiveAndEnabled || autoRenderFailed || IsRendering) return;
            try { RenderQuiltCore(true, false); }
            catch (Exception error)
            {
                autoRenderFailed = true;
                autoRender = false;
                LastAutoRenderError = SanitizeAutoRenderError(error);
                UnityEngine.Debug.LogError("Looking Glass quilt auto render stopped: " + LastAutoRenderError);
            }
        }

        void RenderQuiltCore(bool trustedSchedulerBoundary, bool trustedEditorPreviewBoundary)
        {
            if (notifyingRenderComplete) throw new InvalidOperationException("quilt rendering cannot be started from a quilt completion callback");
            if (IsRendering) throw new InvalidOperationException("quilt render is already in progress");
            bool srp = GraphicsSettings.currentRenderPipeline != null;
            QuiltViewRenderMode renderMode = viewRenderMode;
            bool trustedSrpBoundary = trustedSchedulerBoundary && Application.isEditor && Application.isPlaying && srp;
#if UNITY_EDITOR
            trustedSrpBoundary |= trustedEditorPreviewBoundary && Application.isEditor && !Application.isPlaying &&
                !UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode && srp && editorPreviewRetentionRequired &&
                QuiltRendererEditorCallbackTracker.IsReady && !QuiltRendererEditorCallbackTracker.IsInsideRenderCallback;
#endif
            if (Camera.current != null && !trustedSrpBoundary) throw new InvalidOperationException("RenderQuilt must be called outside render callbacks");
            settings.Validate();
            QuiltViewSampling.GetRenderCount(settings.views, renderMode);
            if (sourceCamera == null) throw new InvalidOperationException("sourceCamera is required");
            if (sourceCamera.enabled) throw new InvalidOperationException("sourceCamera must be disabled and dedicated to quilt rendering");
            if (sourceCamera.orthographic) throw new InvalidOperationException("only perspective cameras are supported");
            Camera framingCamera = EffectiveCamera;
            ValidateReferenceCamera(sourceCamera, referenceCamera);
            Vector3 renderReferencePosition = framingCamera.transform.position;
            Quaternion renderReferenceRotation = framingCamera.transform.rotation;
            float renderReferenceFieldOfView = framingCamera.fieldOfView;
            if (srp && !useRenderRequests) throw new NotSupportedException("an SRP adapter and render request are required");
            if (srp && RenderGuard == null) throw new InvalidOperationException("an IQuiltRenderGuard is required for SRP rendering");
            if (framingCamera != sourceCamera)
            {
                sourceCamera.transform.SetPositionAndRotation(renderReferencePosition, renderReferenceRotation);
                if (followReferenceFieldOfView)
                {
                    sourceCamera.fieldOfView = renderReferenceFieldOfView;
                    settings.fieldOfView = renderReferenceFieldOfView;
                }
            }
            float effectiveFieldOfView = framingCamera != sourceCamera && followReferenceFieldOfView
                ? renderReferenceFieldOfView
                : settings.fieldOfView;
            QuiltPhysicalFraming renderPhysicalFraming = physicalFraming == null ? new QuiltPhysicalFraming() : physicalFraming.Clone();
            if (!QuiltFramingResolver.TryResolve(settings, renderPhysicalFraming, effectiveFieldOfView,
                    out QuiltSettings effectiveSettings, out string framingError))
                throw new InvalidOperationException(framingError);
            IsRendering = true;
            hasCompletedQuilt = false;
            LastRenderedViewCount = 0;
            previousActive = RenderTexture.active;
            CameraState state = default;
            bool stateCaptured = false;
            bool renderSucceeded = false;
            QuiltLayout layout = default;
            IDisposable quiltPreparation = null;
            try
            {
                layout = effectiveSettings.GetLayout();
                EnsureViewSampling(layout.ViewCount, renderMode);
                EnsureTextures(layout);
                ClearQuilt();
                state = new CameraState(sourceCamera);
                stateCaptured = true;
                if (srp)
                {
                    var initializationRequest = new RenderPipeline.StandardRequest { destination = viewTexture };
                    if (!RenderPipeline.SupportsRenderRequest(sourceCamera, initializationRequest))
                        throw new NotSupportedException("active render pipeline does not support standard camera requests");
                }
                long totalStart = Stopwatch.GetTimestamp();
                sourceCamera.enabled = false;
                IQuiltRenderPreparation preparation = RenderGuard as IQuiltRenderPreparation;
                if (preparation != null) quiltPreparation = preparation.PrepareQuilt(sourceCamera);
                Matrix4x4 centerProjection = QuiltMath.OffAxisProjection(effectiveSettings.fieldOfView, effectiveSettings.viewAspect, state.nearClip, state.farClip, 0f, effectiveSettings.cameraDistance);
                float maximumViewOffset = QuiltMath.ViewOffset(layout.ViewCount - 1, layout.ViewCount, effectiveSettings.viewCone, effectiveSettings.cameraDistance);
                for (int renderSlot = 0; renderSlot < renderedViewIndices.Length; ++renderSlot)
                {
                    long viewStart = Stopwatch.GetTimestamp();
                    int viewIndex = renderedViewIndices[renderSlot];
                    float normalizedView = layout.ViewCount <= 1 ? 0f : viewIndex / (float)(layout.ViewCount - 1) * 2f - 1f;
                    float offset = maximumViewOffset * normalizedView;
                    sourceCamera.transform.position = state.position + state.rotation * Vector3.right * offset;
                    sourceCamera.transform.rotation = QuiltMath.ParallelViewRotation(state.rotation);
                    sourceCamera.projectionMatrix = QuiltMath.ProjectionWithViewOffset(centerProjection, offset, effectiveSettings.cameraDistance);
                    sourceCamera.targetTexture = viewTexture;
                    sourceCamera.aspect = effectiveSettings.viewAspect;
                    sourceCamera.rect = new Rect(0f, 0f, 1f, 1f);
                    IDisposable viewGuard = srp ? RenderGuard.Begin(sourceCamera) : null;
                    try
                    {
                        RenderOneView();
                        LastRenderedViewCount++;
                    }
                    finally { if (viewGuard != null) viewGuard.Dispose(); }
                    RenderTexture renderedView = ResolveViewTexture();
                    if (renderedViewIndices.Length == layout.ViewCount)
                    {
                        CopyTile(layout.GetTile(viewIndex), renderedView);
                    }
                    else
                    {
                        for (int outputView = 0; outputView < layout.ViewCount; ++outputView)
                            if (sourceViewAssignments[outputView] == viewIndex) CopyTile(layout.GetTile(outputView), renderedView);
                    }
                    LastViewCpuTicks = Stopwatch.GetTimestamp() - viewStart;
                }
                LastCpuSubmissionTicks = Stopwatch.GetTimestamp() - totalStart;
                Output?.Submit(quiltTexture, layout);
                renderSucceeded = true;
            }
            finally
            {
                try
                {
                    if (quiltPreparation != null) quiltPreparation.Dispose();
                }
                finally
                {
                    if (stateCaptured) state.Restore(sourceCamera);
                    RenderTexture.active = previousActive;
                    IsRendering = false;
                }
            }
            if (renderSucceeded)
            {
                latestSnapshot = new QuiltRenderSnapshot(layout, renderMode, effectiveSettings, LastRenderedViewCount, quiltRevision + 1, sourceCamera, renderPhysicalFraming);
                latestReferenceCamera = framingCamera;
                latestSnapshotFollowedReferenceFieldOfView = framingCamera != sourceCamera && followReferenceFieldOfView;
                latestReferenceFieldOfView = renderReferenceFieldOfView;
                latestReferencePosition = renderReferencePosition;
                latestReferenceRotation = renderReferenceRotation;
                quiltRevision++;
                hasCompletedQuilt = true;
                NotifyQuiltRendered();
            }
        }

        bool IsSnapshotCurrent()
        {
            if (!HasRenderedQuilt || IsRendering || notifyingRenderComplete || quiltTexture == null || !quiltTexture.IsCreated() || sourceCamera == null || settings == null) return false;
            if (quiltTexture.width != latestSnapshot.Layout.QuiltWidth || quiltTexture.height != latestSnapshot.Layout.QuiltHeight) return false;
            try
            {
                ValidateReferenceCamera(sourceCamera, referenceCamera);
                if (!TryGetEffectiveSettings(out QuiltSettings currentEffective, out _)) return false;
                QuiltLayout currentLayout = currentEffective.GetLayout();
                QuiltCaptureSettings previous = latestSnapshot.Settings;
                QuiltSettings current = currentEffective;
                if (currentLayout.QuiltWidth != latestSnapshot.Layout.QuiltWidth || currentLayout.QuiltHeight != latestSnapshot.Layout.QuiltHeight || currentLayout.Rows != latestSnapshot.Layout.Rows || currentLayout.Columns != latestSnapshot.Layout.Columns || currentLayout.ViewCount != latestSnapshot.Layout.ViewCount || viewRenderMode != latestSnapshot.ViewRenderMode) return false;
                if (current.viewAspect != previous.viewAspect || current.fieldOfView != previous.fieldOfView || current.cameraDistance != previous.cameraDistance || current.viewCone != previous.viewCone) return false;
                QuiltCapturePhysicalFraming capturedPhysical = latestSnapshot.PhysicalFraming;
                if (!PhysicalFramingMatches(physicalFraming, capturedPhysical)) return false;
                Camera framingCamera = EffectiveCamera;
                if (framingCamera != latestReferenceCamera) return false;
                if ((framingCamera != sourceCamera && followReferenceFieldOfView) != latestSnapshotFollowedReferenceFieldOfView) return false;
                if (latestSnapshotFollowedReferenceFieldOfView && framingCamera.fieldOfView != latestReferenceFieldOfView) return false;
                if (sourceCamera.nearClipPlane != latestSnapshot.Geometry.nearClip || sourceCamera.farClipPlane != latestSnapshot.Geometry.farClip) return false;
                if (framingCamera != sourceCamera)
                {
                    Transform referenceTransform = framingCamera.transform;
                    if (referenceTransform.position != latestReferencePosition) return false;
                    return referenceTransform.rotation == latestReferenceRotation;
                }
                Transform cameraTransform = sourceCamera.transform;
                if (cameraTransform.position.x != latestSnapshot.Geometry.cameraPositionX || cameraTransform.position.y != latestSnapshot.Geometry.cameraPositionY || cameraTransform.position.z != latestSnapshot.Geometry.cameraPositionZ) return false;
                Quaternion rotation = cameraTransform.rotation;
                return rotation.x == latestSnapshot.Geometry.cameraRotationX && rotation.y == latestSnapshot.Geometry.cameraRotationY && rotation.z == latestSnapshot.Geometry.cameraRotationZ && rotation.w == latestSnapshot.Geometry.cameraRotationW;
            }
            catch { return false; }
        }

        static void ValidateReferenceCamera(Camera dedicatedCamera, Camera framingCamera)
        {
            if (framingCamera == null) return;
            if (framingCamera == dedicatedCamera) throw new InvalidOperationException("referenceCamera must be different from sourceCamera");
            if (framingCamera.transform.IsChildOf(dedicatedCamera.transform))
                throw new InvalidOperationException("referenceCamera cannot be a child or descendant of sourceCamera");
            if (framingCamera.orthographic) throw new InvalidOperationException("referenceCamera must use perspective projection");
            Transform framingTransform = framingCamera.transform;
            Vector3 position = framingTransform.position;
            Quaternion rotation = framingTransform.rotation;
            if (!IsFinite(position.x) || !IsFinite(position.y) || !IsFinite(position.z)
                || !IsFinite(rotation.x) || !IsFinite(rotation.y) || !IsFinite(rotation.z) || !IsFinite(rotation.w)
                || rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w < 0.000001f)
                throw new InvalidOperationException("referenceCamera pose must contain finite position and rotation values");
            float fov = framingCamera.fieldOfView;
            if (!IsFinite(fov) || fov <= 0f || fov >= 179f)
                throw new InvalidOperationException("referenceCamera field of view must be finite and between 0 and 179 degrees");
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static bool PhysicalFramingMatches(QuiltPhysicalFraming current, QuiltCapturePhysicalFraming captured)
        {
            if (current == null) return !captured.enabled;
            return current.enabled == captured.enabled &&
                current.visibleApertureWidthMillimeters == captured.visibleApertureWidthMillimeters &&
                current.visibleApertureHeightMillimeters == captured.visibleApertureHeightMillimeters &&
                current.unitsPerMeter == captured.unitsPerMeter;
        }

        void NotifyQuiltRendered()
        {
            Action<QuiltRenderer> listeners = QuiltRendered;
            if (listeners == null) return;
            notifyingRenderComplete = true;
            try
            {
                Delegate[] invocationList = listeners.GetInvocationList();
                for (int i = 0; i < invocationList.Length; ++i)
                {
                    try { ((Action<QuiltRenderer>)invocationList[i])(this); }
                    catch (Exception error) { UnityEngine.Debug.LogError("ViewWeave quilt completion listener failed: " + SanitizeAutoRenderError(error)); }
                }
            }
            finally { notifyingRenderComplete = false; }
        }

        void RenderOneView()
        {
            if (useRenderRequests && GraphicsSettings.currentRenderPipeline != null)
            {
                var request = new RenderPipeline.StandardRequest { destination = viewTexture };
                if (!RenderPipeline.SupportsRenderRequest(sourceCamera, request)) throw new NotSupportedException("active render pipeline does not support standard camera requests");
                RenderPipeline.SubmitRenderRequest(sourceCamera, request);
            }
            else sourceCamera.Render();
        }

        void EnsureViewSampling(int viewCount, QuiltViewRenderMode renderMode)
        {
            if (hasSamplingCache && cachedSamplingViewCount == viewCount && cachedSamplingMode == renderMode) return;
            int renderCount = QuiltViewSampling.GetRenderCount(viewCount, renderMode);
            renderedViewIndices = new int[renderCount];
            sourceViewAssignments = new int[viewCount];
            for (int slot = 0; slot < renderCount; ++slot)
                renderedViewIndices[slot] = QuiltViewSampling.GetRenderedViewIndex(viewCount, renderMode, slot);
            for (int view = 0; view < viewCount; ++view)
                sourceViewAssignments[view] = QuiltViewSampling.GetSourceViewIndex(viewCount, renderMode, view);
            cachedSamplingViewCount = viewCount;
            cachedSamplingMode = renderMode;
            hasSamplingCache = true;
        }

        RenderTexture ResolveViewTexture()
        {
            if (viewTexture == null) throw new InvalidOperationException("view texture is unavailable");
            if (viewTexture.antiAliasing <= 1) return viewTexture;
            if (resolveTexture == null || !resolveTexture.IsCreated()) throw new InvalidOperationException("MSAA resolve texture is unavailable");
            Graphics.Blit(viewTexture, resolveTexture);
            return resolveTexture;
        }

        void CopyTile(RectInt tile, RenderTexture source)
        {
            if (!SystemInfo.copyTextureSupport.HasFlag(CopyTextureSupport.Basic)) throw new NotSupportedException("Graphics.CopyTexture is not supported");
            Graphics.CopyTexture(source, 0, 0, 0, 0, tile.width, tile.height, quiltTexture, 0, 0, tile.x, tile.y);
        }

        void ClearQuilt()
        {
            RenderTexture old = RenderTexture.active;
            RenderTexture.active = quiltTexture;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = old;
        }

        void EnsureTextures(QuiltLayout layout)
        {
            if (layout.QuiltWidth > SystemInfo.maxTextureSize || layout.QuiltHeight > SystemInfo.maxTextureSize || layout.TileWidth > SystemInfo.maxTextureSize || layout.TileHeight > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException("quilt dimensions exceed the device maximum texture size");
            if (quiltTexture == null || quiltTexture.width != layout.QuiltWidth || quiltTexture.height != layout.QuiltHeight)
            {
                AbortPreviewTextureReplacement(quiltTexture, "quilt");
                ReleaseOwnedTexture(ref quiltTexture);
                RenderTexture created = new RenderTexture(layout.QuiltWidth, layout.QuiltHeight, 0, RenderTextureFormat.ARGB32)
                {
                    name = "ViewWeave.Quilt"
                };
                if (!TryTrackEditorPreviewTexture(created))
                {
                    DisposeTexture(created);
                    throw new InvalidOperationException("Editor preview texture owner rejected the quilt render texture.");
                }
                quiltTexture = created;
                quiltTexture.Create();
                if (!quiltTexture.IsCreated()) throw new InvalidOperationException("failed to create quilt render texture");
            }
            int viewSamples = GetRequestedMsaaSamples(sourceCamera, layout);
            if (viewTexture == null || viewTexture.width != layout.TileWidth || viewTexture.height != layout.TileHeight || viewTexture.antiAliasing != viewSamples)
            {
                AbortPreviewTextureReplacement(viewTexture, "view");
                ReleaseOwnedTexture(ref viewTexture);
                RenderTexture created;
                if (viewSamples <= 1)
                {
                    created = new RenderTexture(layout.TileWidth, layout.TileHeight, 24, RenderTextureFormat.ARGB32)
                    {
                        name = "ViewWeave.View",
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                }
                else
                {
                    RenderTextureDescriptor descriptor = new RenderTextureDescriptor(layout.TileWidth, layout.TileHeight, RenderTextureFormat.ARGB32, 24)
                    {
                        msaaSamples = viewSamples,
                        sRGB = quiltTexture != null && quiltTexture.sRGB,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    created = new RenderTexture(descriptor) { name = "ViewWeave.View" };
                }
                if (!TryTrackEditorPreviewTexture(created))
                {
                    DisposeTexture(created);
                    throw new InvalidOperationException("Editor preview texture owner rejected the view render texture.");
                }
                viewTexture = created;
                viewTexture.Create();
                if (!viewTexture.IsCreated()) throw new InvalidOperationException("failed to create view render texture");
            }
            if (viewSamples > 1)
            {
                if (resolveTexture == null || resolveTexture.width != layout.TileWidth || resolveTexture.height != layout.TileHeight || resolveTexture.antiAliasing != 1)
                {
                    AbortPreviewTextureReplacement(resolveTexture, "resolve");
                    ReleaseOwnedTexture(ref resolveTexture);
                    RenderTextureDescriptor descriptor = new RenderTextureDescriptor(layout.TileWidth, layout.TileHeight, RenderTextureFormat.ARGB32, 0)
                    {
                        msaaSamples = 1,
                        sRGB = quiltTexture != null && quiltTexture.sRGB,
                        useMipMap = false,
                        autoGenerateMips = false
                    };
                    RenderTexture created = new RenderTexture(descriptor)
                    {
                        name = "ViewWeave.ViewResolve"
                    };
                    if (!TryTrackEditorPreviewTexture(created))
                    {
                        DisposeTexture(created);
                        throw new InvalidOperationException("Editor preview texture owner rejected the resolve render texture.");
                    }
                    resolveTexture = created;
                    resolveTexture.Create();
                    if (!resolveTexture.IsCreated()) throw new InvalidOperationException("failed to create view resolve texture");
                }
            }
            else
            {
                AbortPreviewTextureReplacement(resolveTexture, "resolve");
                ReleaseOwnedTexture(ref resolveTexture);
            }
        }

        int GetRequestedMsaaSamples(Camera camera, QuiltLayout layout)
        {
            if (camera == null || !camera.allowMSAA) return 1;
            IQuiltMsaaProvider provider = RenderGuard as IQuiltMsaaProvider;
            if (provider == null) return 1;
            int requested = provider.GetMsaaSampleCount(camera);
            if (requested <= 1) return 1;
            RenderTextureDescriptor probe = new RenderTextureDescriptor(layout.TileWidth, layout.TileHeight, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = requested,
                sRGB = quiltTexture != null && quiltTexture.sRGB
            };
            int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(probe);
            return supported == 2 || supported == 4 || supported == 8 ? supported : 1;
        }

        void ReleaseTextures()
        {
            ReleaseOwnedTexture(ref viewTexture);
            ReleaseOwnedTexture(ref resolveTexture);
            ReleaseOwnedTexture(ref quiltTexture);
        }

        void ReleaseOrRetainTextures()
        {
#if UNITY_EDITOR
            if (!editorPreviewRetentionRequired)
            {
                ReleaseTextures();
                return;
            }

            autoRender = false;
            IQuiltEditorResourceRetentionOwner owner = editorPreviewRetentionOwner as IQuiltEditorResourceRetentionOwner;
            if (owner == null)
            {
                UnityEngine.Debug.LogError("ViewWeave Editor preview retention owner is unavailable; textures remain unreleased.");
                return;
            }
            try { owner.RequestStopAndTakeOwnership(this); }
            catch (Exception error) { UnityEngine.Debug.LogError("ViewWeave Editor preview ownership transfer failed: " + SanitizeAutoRenderError(error)); }
#else
            ReleaseTextures();
#endif
        }

#if UNITY_EDITOR
        bool TryTrackEditorPreviewTexture(RenderTexture texture)
        {
            if (!editorPreviewRetentionRequired) return true;
            if (texture != null) texture.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            IQuiltEditorResourceRetentionOwner owner = editorPreviewRetentionOwner as IQuiltEditorResourceRetentionOwner;
            if (owner == null) return false;
            try { return owner.TrackTexture(texture); }
            catch (Exception error)
            {
                UnityEngine.Debug.LogError("ViewWeave Editor preview texture tracking failed: " + SanitizeAutoRenderError(error));
                return false;
            }
        }

        void AbortPreviewTextureReplacement(RenderTexture current, string label)
        {
            if (!editorPreviewRetentionRequired || current == null) return;
            IQuiltEditorResourceRetentionOwner owner = editorPreviewRetentionOwner as IQuiltEditorResourceRetentionOwner;
            if (owner != null)
            {
                try { owner.RequestStopAndTakeOwnership(this); }
                catch (Exception error) { UnityEngine.Debug.LogError("ViewWeave Editor preview resize stop failed: " + SanitizeAutoRenderError(error)); }
            }
            throw new InvalidOperationException("Editor preview " + label + " texture replacement requires stop, proof, and producer rebuild.");
        }
#else
        bool TryTrackEditorPreviewTexture(RenderTexture texture) => true;
        void AbortPreviewTextureReplacement(RenderTexture current, string label) { }
#endif

        void ReleaseOwnedTexture(ref RenderTexture texture)
        {
            if (texture == null) return;
            if (ReferenceEquals(texture, quiltTexture)) hasCompletedQuilt = false;
            if (RenderTexture.active == texture) RenderTexture.active = null;
            if (previousActive == texture) previousActive = null;
            texture.Release();
            DisposeTexture(texture);
            texture = null;
        }

        void DisposeTexture(RenderTexture texture)
        {
            if (Application.isPlaying) Destroy(texture);
            else DestroyImmediate(texture);
        }

        readonly struct CameraState
        {
            public readonly Vector3 position; public readonly Quaternion rotation; public readonly Matrix4x4 projection; public readonly RenderTexture target; public readonly Rect rect; public readonly float aspect, nearClip, farClip; public readonly bool enabled;
            public CameraState(Camera c) { position = c.transform.position; rotation = c.transform.rotation; projection = c.projectionMatrix; target = c.targetTexture; rect = c.rect; aspect = c.aspect; nearClip = c.nearClipPlane; farClip = c.farClipPlane; enabled = c.enabled; }
            public void Restore(Camera c) { c.transform.SetPositionAndRotation(position, rotation); c.projectionMatrix = projection; c.targetTexture = target; c.rect = rect; c.aspect = aspect; c.enabled = enabled; }
        }
    }
}
