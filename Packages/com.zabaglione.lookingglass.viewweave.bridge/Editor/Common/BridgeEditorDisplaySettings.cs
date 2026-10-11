using UnityEditor;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    internal static class BridgeEditorDisplaySettings
    {
        static BridgeEditorDisplaySettings()
        {
            QuiltDisplaySettingsResolver.RegisterProvider(ResolveDisplayProfile);
            BridgeDisplayMetadata.Recognized += BridgeDisplayGeometryStore.Remember;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode) BridgeDisplayGeometryStore.CaptureEditIdentities();
                else if (state == PlayModeStateChange.EnteredPlayMode) BridgeDisplayGeometryStore.BindPlayIdentities();
                else if (state == PlayModeStateChange.EnteredEditMode) BridgeDisplayGeometryStore.ClearPlayIdentities();
            };
        }

        internal static bool ResolveDisplayProfile(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            if (renderer == null) return false;
            if (BridgeMetalEditorPreview.ResolveDisplayProfile(renderer, out profile))
                return ResolvePreviewOrRemembered(renderer, ref profile);
            BridgeDisplayGeometryStore.TryConfiguration(renderer, out UnityEngine.MonoBehaviour selectedOutput, out _, out _, out _);
            var metal = selectedOutput as BridgeMetalQuiltOutput;
            if (metal != null)
            {
                if (!metal.TryGetCurrentDisplayProfile(renderer, out BridgeDisplayProfile connected))
                    return BridgeDisplayGeometryStore.TryResolve(renderer, out profile);
                profile = new QuiltEditorDisplayProfile(connected.Aspect, connected.Viewcone, QuiltDisplaySettingsSource.BridgeSession,
                    true, connected.HasPhysicalCalibration, connected.PhysicalWidthMillimeters, connected.PhysicalHeightMillimeters,
                    connected.CalibrationDpi, connected.ToString());
                return true;
            }
            if (BridgeEditorPreview.ResolveDisplayProfile(renderer, out profile))
                return ResolvePreviewOrRemembered(renderer, ref profile);
            return BridgeDisplayGeometryStore.TryResolve(renderer, out profile);
        }

        static bool ResolvePreviewOrRemembered(QuiltRenderer renderer, ref QuiltEditorDisplayProfile profile)
        {
            if (profile.source == QuiltDisplaySettingsSource.ConnectedDisplaySnapshot)
                return BridgeDisplayGeometryStore.TryResolve(renderer, out profile);
            if (BridgeDisplayGeometryStore.TryResolve(renderer, out QuiltEditorDisplayProfile remembered) &&
                profile.source == QuiltDisplaySettingsSource.LocalPreview)
                profile = remembered;
            return true;
        }
    }
}
