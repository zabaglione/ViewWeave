using System;

namespace ViewWeave
{
    public static class BridgeRuntimeLocator
    {
        public static bool TryResolve(string explicitPath, out string path, out string error)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return TryValidateConfiguredPath(explicitPath, out path, out error);
            string environmentPath = Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
            if (!string.IsNullOrWhiteSpace(environmentPath)) return TryValidateConfiguredPath(environmentPath, out path, out error);
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            return BridgeRuntimeLocatorWindows.TryResolve(out path, out error);
#elif UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
            return BridgeRuntimeLocatorMacOS.TryResolve(out path, out error);
#else
            return Unsupported(out path, out error);
#endif
        }

        static bool TryValidateConfiguredPath(string value, out string path, out string error)
        {
#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_EDITOR)
            return BridgeRuntimeLocatorWindowsPath.TryValidatePath(value, out path, out error);
#elif UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
            return BridgeRuntimeLocatorMacOS.TryValidatePath(value, out path, out error);
#else
            return Unsupported(out path, out error);
#endif
        }

        static bool Unsupported(out string path, out string error)
        {
            path = null;
            error = "Bridge runtime discovery is supported on Windows and macOS only";
            return false;
        }
    }
}
