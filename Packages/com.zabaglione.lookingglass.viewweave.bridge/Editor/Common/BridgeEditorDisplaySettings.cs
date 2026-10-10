using UnityEditor;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    internal static class BridgeEditorDisplaySettings
    {
        static BridgeEditorDisplaySettings() => QuiltDisplaySettingsResolver.RegisterProvider(ResolveDisplayProfile);

        internal static bool ResolveDisplayProfile(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            if (renderer == null) return false;
            var metal = renderer.OutputBehaviour as BridgeMetalQuiltOutput;
            if (metal != null)
            {
                if (!metal.IsRunning || metal.rig == null || metal.rig.Renderer != renderer || !metal.Profile.IsValid) return false;
                BridgeDisplayProfile connected = metal.Profile;
                profile = new QuiltEditorDisplayProfile(connected.Aspect, connected.Viewcone, QuiltDisplaySettingsSource.BridgeSession,
                    true, connected.HasPhysicalCalibration, connected.PhysicalWidthMillimeters, connected.PhysicalHeightMillimeters,
                    connected.CalibrationDpi, connected.ToString());
                return true;
            }
            return BridgeEditorPreview.ResolveDisplayProfile(renderer, out profile);
        }
    }
}
