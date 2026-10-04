using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace LookingGlass.Community
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera), typeof(QuiltRenderer))]
    public sealed class ViewWeaveRig : MonoBehaviour
    {
        [SerializeField] QuiltQualityProfileData profile = QuiltQualityProfileData.Default;
        [SerializeField] bool autoRender;
        [SerializeField] MonoBehaviour outputBehaviour;
        [SerializeField] MonoBehaviour guardBehaviour;

        public QuiltRenderer Renderer { get; private set; }
        public QuiltQualityProfile Profile => profile.ToProfile();
        public bool AutoRender
        {
            get => autoRender;
            set
            {
                if (value && !ValidateRig(out string error)) throw new InvalidOperationException("cannot enable autoRender: " + error);
                autoRender = value;
                EnsureRenderer().autoRender = value;
            }
        }

        void Awake() => ConfigureComponents();
        void OnValidate() { if (!Application.isPlaying) ConfigureComponents(); }

        public void ApplyProfile(QuiltQualityProfile value)
        {
            value.Validate(); profile = QuiltQualityProfileData.From(value); EnsureRenderer(); value.ApplyTo(Renderer.settings);
        }

        public bool ConfigureComponents()
        {
            Renderer = EnsureRenderer();
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

        public void SetOutput(MonoBehaviour output) { if (output == null) { outputBehaviour = null; EnsureRenderer().OutputBehaviour = null; return; } if (!TrySetOutput(output)) throw new ArgumentException("output must implement IQuiltOutput", nameof(output)); outputBehaviour = output; }
        public void SetGuard(MonoBehaviour guard) { if (guard == null) { guardBehaviour = null; EnsureRenderer().GuardBehaviour = null; return; } if (!TrySetGuard(guard)) throw new ArgumentException("guard must implement IQuiltRenderGuard and match the rig camera", nameof(guard)); guardBehaviour = guard; }
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
