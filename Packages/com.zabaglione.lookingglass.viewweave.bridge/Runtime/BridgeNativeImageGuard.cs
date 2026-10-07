using System;
using System.Collections.Generic;
#if UNITY_EDITOR
using System.Diagnostics;
using UnityEngine;
#endif

namespace ViewWeave
{
    internal readonly struct BridgeNativeModuleIdentity
    {
        public readonly string ModuleName;
        public readonly IntPtr BaseAddress;

        public BridgeNativeModuleIdentity(string moduleName, IntPtr baseAddress)
        {
            ModuleName = moduleName;
            BaseAddress = baseAddress;
        }
    }

    internal static class BridgeNativeImageGuard
    {
        const string TargetModuleName = "lookingglass_bridge_shim.dll";

        internal static bool TryInspectEditorProcess(out string error)
        {
#if UNITY_EDITOR
            if (!Application.isEditor || !Application.isPlaying || Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = string.Empty;
                return true;
            }

            return TryInspect(EnumerateCurrentProcessModules, out error);
#else
            error = string.Empty;
            return true;
#endif
        }

        internal static bool TryInspect(Func<IEnumerable<BridgeNativeModuleIdentity>> inspectModules, out string error)
        {
            try
            {
                if (inspectModules == null) throw new ArgumentNullException(nameof(inspectModules));
                return TryValidateModules(inspectModules(), out error);
            }
            catch (Exception exception)
            {
                error = InspectionFailure(exception);
                return false;
            }
        }

        internal static bool TryValidateModules(IEnumerable<BridgeNativeModuleIdentity> modules, out string error)
        {
            if (modules == null)
            {
                error = InspectionFailure(null);
                return false;
            }

            try
            {
                HashSet<IntPtr> imageBases = new HashSet<IntPtr>();
                foreach (BridgeNativeModuleIdentity module in modules)
                {
                    if (!string.Equals(module.ModuleName, TargetModuleName, StringComparison.OrdinalIgnoreCase)) continue;
                    imageBases.Add(module.BaseAddress);
                    if (imageBases.Count > 1)
                    {
                        error = "Multiple loaded lookingglass_bridge_shim.dll images were detected. Restart the Unity Editor before starting a Bridge worker.";
                        return false;
                    }
                }

                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = InspectionFailure(exception);
                return false;
            }
        }

#if UNITY_EDITOR
        static IEnumerable<BridgeNativeModuleIdentity> EnumerateCurrentProcessModules()
        {
            using (Process process = Process.GetCurrentProcess())
            {
                foreach (ProcessModule module in process.Modules)
                    yield return new BridgeNativeModuleIdentity(module.ModuleName, module.BaseAddress);
            }
        }
#endif

        static string InspectionFailure(Exception exception)
        {
            string reason = exception == null ? string.Empty : " (" + exception.GetType().Name + ")";
            return "Unable to inspect loaded native modules" + reason + ". Restart the Unity Editor before starting a Bridge worker.";
        }
    }
}
