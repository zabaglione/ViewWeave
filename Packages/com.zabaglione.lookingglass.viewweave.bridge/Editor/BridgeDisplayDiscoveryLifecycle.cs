using UnityEditor;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    internal static class BridgeDisplayDiscoveryLifecycle
    {
        static BridgeDisplayDiscoveryLifecycle()
        {
            BridgeDisplayDiscovery.InitializeEditorRecovery();
            EditorApplication.update += BridgeDisplayDiscovery.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += BridgeDisplayDiscovery.CancelForReload;
            EditorApplication.quitting += BridgeDisplayDiscovery.CancelForReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
                BridgeDisplayDiscovery.CancelForPlayTransition();
        }
    }
}
