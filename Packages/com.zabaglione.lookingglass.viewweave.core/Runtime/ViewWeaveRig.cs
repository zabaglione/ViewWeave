using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera), typeof(QuiltRenderer))]
    public sealed class ViewWeaveRig : MonoBehaviour
    {
        [SerializeField] QuiltQualityProfileData profile = QuiltQualityProfileData.Default;
        [SerializeField] bool autoRender;
        [SerializeField] MonoBehaviour outputBehaviour;
        [SerializeField] MonoBehaviour guardBehaviour;
#if UNITY_EDITOR
        [SerializeField, HideInInspector] string editorDisplayIdentity;
        [SerializeField, HideInInspector] MonoBehaviour editorDisplayIdentityOutput;
        [SerializeField, HideInInspector] QuiltRenderer editorDisplayIdentityRenderer;

        // Edit Mode identifiers travel with the original scene objects through Play reloads.
        public void CaptureEditorDisplayIdentity(string identity, MonoBehaviour source)
        {
            if (Application.isPlaying) throw new InvalidOperationException("Display identity must be captured in Edit Mode.");
            editorDisplayIdentity = identity;
            editorDisplayIdentityOutput = source;
            editorDisplayIdentityRenderer = GetComponent<QuiltRenderer>();
        }

        public bool TryGetEditorDisplayIdentity(MonoBehaviour source, QuiltRenderer requestedRenderer, out string identity)
        {
            identity = editorDisplayIdentity;
            return !string.IsNullOrEmpty(identity) && source != null && source == editorDisplayIdentityOutput &&
                requestedRenderer != null && requestedRenderer == editorDisplayIdentityRenderer;
        }
#endif

        public QuiltRenderer Renderer { get; private set; }
        public QuiltQualityProfile Profile => profile.ToProfile();
        public bool AutoRender
        {
            get => autoRender;
            set
            {
                if (value)
                {
                    QuiltRenderer renderer = Renderer != null ? Renderer : GetComponent<QuiltRenderer>();
                    if (!QuiltRendererMutationPolicy.TryCheck(renderer,
                            new QuiltRendererMutationRequest(QuiltRendererMutationKind.AutoRender, true), out string mutationError))
                        throw new InvalidOperationException("cannot enable autoRender: " + mutationError);
                    if (!ValidateRig(out string error)) throw new InvalidOperationException("cannot enable autoRender: " + error);
                }
                autoRender = value;
                EnsureRenderer().autoRender = value;
                if (!value) Renderer.ReleasePipelineRenderResources();
            }
        }

        internal bool SetAutoRenderForBridgeOwner(bool value, object owner, out string error)
        {
            error = string.Empty;
            QuiltRenderer renderer = Renderer != null ? Renderer : GetComponent<QuiltRenderer>();
            if (!QuiltRendererMutationPolicy.IsOwnerRegistered(renderer, owner))
            {
                error = "The Bridge auto-render owner is not registered for this renderer.";
                return false;
            }
            if (value && !ValidateRig(out error)) return false;
            autoRender = value;
            EnsureRenderer().autoRender = value;
            if (!value) Renderer.ReleasePipelineRenderResources();
            return true;
        }

        void Awake()
        {
            Renderer = GetComponent<QuiltRenderer>();
            if (Renderer != null && Renderer.HasMutationOwnerRequirement) return;
#if UNITY_EDITOR
            if (Renderer != null && Renderer.HasEditorPreviewRetentionOwner) return;
#endif
            ConfigureComponents();
        }
        void OnValidate()
        {
            if (Application.isPlaying) return;
#if UNITY_EDITOR
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
#endif
            QuiltRenderer existingRenderer = Renderer != null ? Renderer : GetComponent<QuiltRenderer>();
            if (existingRenderer != null && existingRenderer.HasMutationOwnerRequirement) return;
#if UNITY_EDITOR
            if (existingRenderer != null && existingRenderer.HasEditorPreviewRetentionOwner) return;
#endif
            ConfigureComponents();
        }

        internal bool HasOutputBinding(MonoBehaviour output)
        {
            QuiltRenderer renderer = Renderer != null ? Renderer : GetComponent<QuiltRenderer>();
            return outputBehaviour == output && renderer != null && renderer.OutputBehaviour == output;
        }

        public void ApplyProfile(QuiltQualityProfile value)
        {
            if (!QuiltRendererMutationPolicy.TryCheck(Renderer != null ? Renderer : GetComponent<QuiltRenderer>(),
                    QuiltRendererMutationKind.Structural, out string error))
                throw new InvalidOperationException("Cannot apply a profile while the renderer is owned: " + error);
            ApplyProfileCore(value);
        }

        internal bool ApplyProfileForBridgeOwner(QuiltQualityProfile value, object owner, out string error)
        {
            error = string.Empty;
            QuiltRenderer renderer = Renderer != null ? Renderer : GetComponent<QuiltRenderer>();
            if (!QuiltRendererMutationPolicy.IsOwnerRegistered(renderer, owner))
            {
                error = "The Bridge profile owner is not registered for this renderer.";
                return false;
            }
            try { ApplyProfileCore(value); return true; }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        void ApplyProfileCore(QuiltQualityProfile value)
        {
            value.Validate();
            profile = QuiltQualityProfileData.From(value);
            EnsureRenderer();
            value.ApplyTo(Renderer.settings);
        }

        public bool ConfigureComponents()
        {
            Renderer = EnsureRenderer();
            if (!QuiltRendererMutationPolicy.TryCheck(Renderer, QuiltRendererMutationKind.Structural, out string mutationError))
            {
                Fail("rig components cannot be reconfigured while the renderer is owned: " + mutationError);
                return false;
            }
            Camera camera = GetComponent<Camera>();
            if (Renderer.sourceCamera != null && Renderer.sourceCamera != camera) { Fail("sourceCamera must belong to the ViewWeaveRig GameObject"); return false; }
            if (Renderer.OutputBehaviour != null && Renderer.OutputBehaviour != outputBehaviour) { Fail("existing output conflicts with the rig output"); return false; }
            if (Renderer.GuardBehaviour != null && Renderer.GuardBehaviour != guardBehaviour) { Fail("existing guard conflicts with the rig guard"); return false; }
            camera.enabled = false;
            Renderer.sourceCamera = camera;
            try { Profile.Validate(); Profile.ApplyTo(Renderer.settings); } catch (Exception error) { Fail("profile is invalid: " + error.Message); return false; }
            if (!TrySetOutput(outputBehaviour) || !TrySetGuard(guardBehaviour)) { Renderer.autoRender = false; return false; }
            Renderer.autoRender = autoRender;
            return true;
        }

        public void SetOutput(MonoBehaviour output)
        {
            if (!QuiltRendererMutationPolicy.TryCheck(Renderer != null ? Renderer : GetComponent<QuiltRenderer>(),
                    QuiltRendererMutationKind.Structural, out string error))
                throw new InvalidOperationException("Cannot change the Rig output while the renderer is owned: " + error);
            if (output == null) { outputBehaviour = null; EnsureRenderer().OutputBehaviour = null; return; }
            if (!TrySetOutput(output)) throw new ArgumentException("output must implement IQuiltOutput", nameof(output));
            outputBehaviour = output;
        }

        public void SetGuard(MonoBehaviour guard)
        {
            if (!QuiltRendererMutationPolicy.TryCheck(Renderer != null ? Renderer : GetComponent<QuiltRenderer>(),
                    QuiltRendererMutationKind.Structural, out string error))
                throw new InvalidOperationException("Cannot change the Rig render guard while the renderer is owned: " + error);
            if (guard == null) { guardBehaviour = null; EnsureRenderer().GuardBehaviour = null; return; }
            if (!TrySetGuard(guard)) throw new ArgumentException("guard must implement IQuiltRenderGuard and match the rig camera", nameof(guard));
            guardBehaviour = guard;
        }

        internal bool SetOutputForBridgeOwner(MonoBehaviour output, object owner, out string error)
        {
            error = string.Empty;
            if (!QuiltRendererMutationPolicy.IsOwnerRegistered(Renderer != null ? Renderer : GetComponent<QuiltRenderer>(), owner))
            {
                error = "The Bridge output owner is not registered for this renderer.";
                return false;
            }
            if (output != null && !(output is IQuiltOutput))
            {
                error = "Output must implement IQuiltOutput.";
                return false;
            }
            outputBehaviour = output;
            EnsureRenderer().OutputBehaviour = output;
            return true;
        }

        internal bool SetGuardForBridgeOwner(MonoBehaviour guard, object owner, out string error)
        {
            error = string.Empty;
            if (!QuiltRendererMutationPolicy.IsOwnerRegistered(Renderer != null ? Renderer : GetComponent<QuiltRenderer>(), owner))
            {
                error = "The Bridge render-guard owner is not registered for this renderer.";
                return false;
            }
            if (guard != null && (!(guard is IQuiltRenderGuard) || guard.GetComponent<Camera>() != GetComponent<Camera>()))
            {
                error = "Guard must implement IQuiltRenderGuard and match the rig camera.";
                return false;
            }
            guardBehaviour = guard;
            EnsureRenderer().GuardBehaviour = guard;
            return true;
        }
        public bool ValidateRig(out string error)
        {
            try
            {
                if (GraphicsSettings.currentRenderPipeline == null) throw new InvalidOperationException("ViewWeaveRig requires a URP or HDRP render pipeline");
                Renderer = Renderer ?? GetComponent<QuiltRenderer>();
                if (Renderer == null) throw new InvalidOperationException("QuiltRenderer is required");
                Camera camera = GetComponent<Camera>();
                if (camera == null || camera.enabled || Renderer.sourceCamera != camera) throw new InvalidOperationException("ViewWeaveRig requires its disabled same-GameObject sourceCamera");
                if (Renderer.OutputBehaviour != outputBehaviour || Renderer.GuardBehaviour != guardBehaviour) throw new InvalidOperationException("serialized rig output or guard conflicts with QuiltRenderer");
                ValidateOutput(outputBehaviour);
                ValidateGuard(guardBehaviour, camera);
                Profile.Validate(); Renderer.settings.Validate(); ValidateSettingsMatch(Renderer.settings, Profile); error = null; return true;
            }
            catch (Exception exception) { error = exception.Message; return false; }
        }

        bool TrySetOutput(MonoBehaviour value)
        {
            if (value == null) { EnsureRenderer().OutputBehaviour = null; return true; }
            if (!(value is IQuiltOutput)) { Fail("output must implement IQuiltOutput"); return false; }
            EnsureRenderer().OutputBehaviour = value; return true;
        }

        bool TrySetGuard(MonoBehaviour value)
        {
            if (value == null) { EnsureRenderer().GuardBehaviour = null; return GraphicsSettings.currentRenderPipeline == null; }
            if (!(value is IQuiltRenderGuard)) { Fail("guard must implement IQuiltRenderGuard"); return false; }
            Camera camera = GetComponent<Camera>();
            if (value.GetComponent<Camera>() != camera) { Fail("guard camera must match the rig camera"); return false; }
            EnsureRenderer().GuardBehaviour = value; return true;
        }

        static void ValidateOutput(MonoBehaviour value)
        {
            if (value != null && !(value is IQuiltOutput)) throw new InvalidOperationException("output must implement IQuiltOutput");
        }

        static void ValidateGuard(MonoBehaviour value, Camera camera)
        {
            if (value == null) throw new InvalidOperationException("guard is required for URP or HDRP");
            if (!(value is IQuiltRenderGuard) || value.GetComponent<Camera>() != camera) throw new InvalidOperationException("guard must implement IQuiltRenderGuard and match the rig camera");
        }

        static void ValidateSettingsMatch(QuiltSettings settings, QuiltQualityProfile value)
        {
            if (settings.quiltWidth != value.QuiltWidth || settings.quiltHeight != value.QuiltHeight || settings.columns != value.Columns || settings.rows != value.Rows || settings.views != value.Views || Mathf.Abs(settings.viewAspect - value.Aspect) > 0.0001f || Mathf.Abs(settings.viewCone - value.ViewCone) > 0.0001f)
                throw new InvalidOperationException("QuiltRenderer settings do not match the rig profile");
        }

        static string SanitizeLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (chars[i] < 0x20 || chars[i] > 0x7e) chars[i] = '?';
            return new string(chars);
        }

        void Fail(string message)
        {
            autoRender = false;
            if (Renderer != null) Renderer.autoRender = false;
            if (Renderer != null) Renderer.ReleasePipelineRenderResources();
            Debug.LogError("ViewWeaveRig configuration failed: " + SanitizeLog(message));
        }

        QuiltRenderer EnsureRenderer() => Renderer ??= GetComponent<QuiltRenderer>() ?? gameObject.AddComponent<QuiltRenderer>();

        [Serializable]
        struct QuiltQualityProfileData
        {
            public int width, height, columns, rows, views;
            public float aspect, viewCone;
            public static QuiltQualityProfileData Default => new QuiltQualityProfileData { width = 1920, height = 1080, columns = 8, rows = 6, views = 48, aspect = 1f, viewCone = 40f };
            public QuiltQualityProfile ToProfile() => new QuiltQualityProfile(width, height, columns, rows, views, aspect, viewCone);
            public static QuiltQualityProfileData From(QuiltQualityProfile p) => new QuiltQualityProfileData { width = p.QuiltWidth, height = p.QuiltHeight, columns = p.Columns, rows = p.Rows, views = p.Views, aspect = p.Aspect, viewCone = p.ViewCone };
        }
    }
}
