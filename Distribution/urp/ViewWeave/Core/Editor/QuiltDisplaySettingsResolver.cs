using System;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    public enum QuiltDisplaySettingsSource
    {
        LocalRendererSettings,
        LocalPreview,
        BridgeSession,
        DevicePreview
    }

    public readonly struct QuiltEditorDisplayProfile
    {
        public readonly float aspect;
        public readonly float viewCone;
        public readonly QuiltDisplaySettingsSource source;
        public readonly bool isConnected;
        public readonly bool hasPhysicalCalibration;
        public readonly float widthMillimeters;
        public readonly float heightMillimeters;
        public readonly float dpi;
        public readonly string detail;

        public QuiltEditorDisplayProfile(float aspect, float viewCone, QuiltDisplaySettingsSource source,
            bool isConnected, bool hasPhysicalCalibration, float widthMillimeters, float heightMillimeters, float dpi, string detail)
        {
            this.aspect = aspect;
            this.viewCone = viewCone;
            this.source = source;
            this.isConnected = isConnected;
            this.hasPhysicalCalibration = hasPhysicalCalibration;
            this.widthMillimeters = widthMillimeters;
            this.heightMillimeters = heightMillimeters;
            this.dpi = dpi;
            this.detail = detail ?? string.Empty;
        }
    }

    public delegate bool QuiltEditorDisplayProfileProvider(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile);

    public static class QuiltDisplaySettingsResolver
    {
        static QuiltEditorDisplayProfileProvider provider;

        public static void RegisterProvider(QuiltEditorDisplayProfileProvider value)
        {
            provider = value;
            SceneView.RepaintAll();
        }

        public static void NotifyChanged()
        {
            SceneView.RepaintAll();
        }

        public static bool TryResolve(QuiltRenderer renderer, out QuiltSettings effectiveSettings,
            out QuiltEditorDisplayProfile profile, out string error)
        {
            effectiveSettings = null;
            error = string.Empty;
            profile = default;
            if (renderer == null || renderer.settings == null)
            {
                error = "A Quilt Renderer and its settings are required.";
                return false;
            }

            profile = new QuiltEditorDisplayProfile(renderer.settings.viewAspect, renderer.settings.viewCone,
                QuiltDisplaySettingsSource.LocalRendererSettings, false, false, 0f, 0f, 0f, "Stored local settings");
            QuiltEditorDisplayProfileProvider currentProvider = provider;
            if (currentProvider != null)
            {
                try
                {
                    if (currentProvider(renderer, out QuiltEditorDisplayProfile resolved) &&
                        IsFinite(resolved.aspect) && resolved.aspect > 0f && IsFinite(resolved.viewCone) && resolved.viewCone >= 0f && resolved.viewCone < 179f)
                        profile = resolved;
                }
                catch (Exception exception)
                {
                    error = "Display profile lookup failed: " + exception.GetType().Name;
                }
            }

            QuiltSettings displaySettings = renderer.settings.Clone();
            displaySettings.viewAspect = profile.aspect;
            displaySettings.viewCone = profile.viewCone;
            float fieldOfView = QuiltFramingResolver.EffectiveFieldOfView(renderer);
            if (!QuiltFramingResolver.TryResolve(displaySettings, renderer.physicalFraming, fieldOfView,
                    out effectiveSettings, out string framingError))
            {
                if (string.IsNullOrEmpty(error)) error = framingError;
                return false;
            }
            return true;
        }

        public static bool TryGetConnectedPhysicalCalibration(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            QuiltEditorDisplayProfileProvider currentProvider = provider;
            if (renderer == null || currentProvider == null) return false;
            try
            {
                return currentProvider(renderer, out profile) && profile.isConnected && profile.hasPhysicalCalibration &&
                    IsFinite(profile.widthMillimeters) && profile.widthMillimeters > 0f &&
                    IsFinite(profile.heightMillimeters) && profile.heightMillimeters > 0f &&
                    IsFinite(profile.dpi) && profile.dpi > 0f;
            }
            catch { return false; }
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
