using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
#endif
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
#endif

namespace ViewWeave
{
    internal readonly struct BridgeNativeModuleIdentity
    {
        public readonly string ModuleName;
        public readonly IntPtr BaseAddress;
        public readonly string FullPath;

        public BridgeNativeModuleIdentity(string moduleName, IntPtr baseAddress)
            : this(moduleName, baseAddress, string.Empty) { }

        public BridgeNativeModuleIdentity(string moduleName, IntPtr baseAddress, string fullPath)
        {
            ModuleName = moduleName ?? string.Empty;
            BaseAddress = baseAddress;
            FullPath = fullPath ?? string.Empty;
        }

        public bool IsKnown => BaseAddress != IntPtr.Zero && !string.IsNullOrWhiteSpace(FullPath);
    }

    /// <summary>Fails closed when the Unity Editor may bind P/Invoke to a different Bridge shim image.</summary>
    internal static class BridgeNativeImageGuard
    {
        const string TargetModuleName = "lookingglass_bridge_shim.dll";
#if UNITY_EDITOR_WIN
        const uint GetModuleHandleUnchangedRefcount = 0x00000002;
        const uint GetModuleHandleFromAddress = 0x00000004;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleExW", SetLastError = true)]
        static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr moduleHandle);
#endif
#if UNITY_EDITOR
        const string SessionFingerprintKey = "ViewWeave.Bridge.NativeImageFingerprint";
        static string cachedExpectedPath = string.Empty;
        static string cachedFingerprint = string.Empty;
        static long cachedFileLength = -1;
        static DateTime cachedFileWriteUtc;
        static int lastFileStatFrame = int.MinValue;
        static int cachedModuleFrame = int.MinValue;
        static List<BridgeNativeModuleIdentity> cachedModules;
        static bool callbacksRegistered;
        static bool cacheValidationReady;
        static bool cacheRefreshPending = true;
        static string cacheValidationError = string.Empty;
        internal static bool IsValidationPending => cacheRefreshPending;
        // Module enumeration is intentionally time-bucketed in both Edit and Play Mode.
        // Worker polling can happen every frame, but process module enumeration and file
        // fingerprinting should not become per-frame work.
        static int CurrentCacheEpoch => (int)(EditorApplication.timeSinceStartup * 4.0);
#endif

        internal static bool TryInspectEditorProcess(out string error)
        {
#if UNITY_EDITOR
            if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = string.Empty;
                return true;
            }

            if (!TryValidatePackageFingerprint(true, out string expectedPath, out error))
            {
                cacheValidationReady = false;
                cacheRefreshPending = false;
                cacheValidationError = error;
                return false;
            }
            if (!TryGetCurrentModules(true, out List<BridgeNativeModuleIdentity> modules, out error))
            {
                cacheValidationReady = false;
                cacheRefreshPending = true;
                cacheValidationError = error;
                return false;
            }
            bool valid = TryValidateLoadedModules(modules, expectedPath, default, false, out _, out error);
            cacheValidationReady = valid;
            cacheRefreshPending = false;
            cacheValidationError = valid ? string.Empty : error;
            return valid;
#else
            error = string.Empty;
            return true;
#endif
        }

        internal static bool TryCaptureCurrentIdentity(out BridgeNativeModuleIdentity identity, out string error)
        {
            identity = default;
#if UNITY_EDITOR
            if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = string.Empty;
                return true;
            }
            if (!TryValidatePackageFingerprint(true, out string expectedPath, out error))
            {
                cacheValidationReady = false;
                cacheRefreshPending = false;
                cacheValidationError = error;
                return false;
            }
            if (!TryGetCurrentModules(true, out List<BridgeNativeModuleIdentity> modules, out error))
            {
                cacheValidationReady = false;
                cacheRefreshPending = true;
                cacheValidationError = error;
                return false;
            }
            bool valid = TryValidateLoadedModules(modules, expectedPath, default, true, out identity, out error);
            cacheValidationReady = valid;
            cacheRefreshPending = false;
            cacheValidationError = valid ? string.Empty : error;
            return valid;
#else
            error = string.Empty;
            return true;
#endif
        }

        internal static bool TryValidateRenderEventFunctionAddress(IntPtr functionAddress,
            BridgeNativeModuleIdentity identity, out string error)
        {
            error = string.Empty;
            if (functionAddress == IntPtr.Zero)
            {
                error = "The Bridge render event function address is null.";
                return false;
            }
            if (!identity.IsKnown)
            {
                error = "The Bridge render event function has no validated package image identity.";
                return false;
            }
#if UNITY_EDITOR_WIN
            if (!GetModuleHandleExW(GetModuleHandleFromAddress | GetModuleHandleUnchangedRefcount,
                    functionAddress, out IntPtr resolvedBase) || resolvedBase == IntPtr.Zero)
            {
                error = "The Bridge render event function owner could not be resolved (Win32 error " +
                    Marshal.GetLastWin32Error() + ").";
                return false;
            }
            return TryValidateResolvedAddressBase(functionAddress, resolvedBase, identity, out error);
#else
            error = "Resolving a Bridge native function owner is supported only in the Windows Editor.";
            return false;
#endif
        }

        internal static bool TryValidateResolvedAddressBase(IntPtr functionAddress, IntPtr resolvedBase,
            BridgeNativeModuleIdentity identity, out string error)
        {
            error = string.Empty;
            if (functionAddress == IntPtr.Zero || resolvedBase == IntPtr.Zero)
            {
                error = "The Bridge render event function address or resolved image base is null.";
                return false;
            }
            if (!identity.IsKnown)
            {
                error = "The Bridge render event function has no validated package image identity.";
                return false;
            }
            if (resolvedBase != identity.BaseAddress)
            {
                error = RestartRequired("The Bridge render event function belongs to a different native image");
                return false;
            }
            return true;
        }

        internal static bool TryValidateOwnerIdentity(BridgeNativeModuleIdentity identity, out string error)
        {
#if UNITY_EDITOR
            if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = string.Empty;
                return true;
            }
            if (!identity.IsKnown)
            {
                error = RestartRequired("The native worker has no persisted loaded-module identity");
                return false;
            }
            if (!cacheValidationReady)
            {
                error = string.IsNullOrEmpty(cacheValidationError)
                    ? "Native image validation is pending on the Editor update loop. Retry after validation completes."
                    : cacheValidationError;
                return false;
            }
            if (!TryValidatePackageFingerprint(false, out string expectedPath, out error)) return false;
            if (!TryGetCurrentModules(false, out List<BridgeNativeModuleIdentity> modules, out error)) return false;
            return TryValidateLoadedModules(modules, expectedPath, identity, true, out _, out error);
#else
            error = string.Empty;
            return true;
#endif
        }

        internal static bool TryValidateGlobalCalls(out bool moduleLoaded, out BridgeNativeModuleIdentity identity, out string error)
        {
            moduleLoaded = false;
            identity = default;
#if UNITY_EDITOR
            if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = string.Empty;
                return true;
            }
            if (!cacheValidationReady)
            {
                error = string.IsNullOrEmpty(cacheValidationError)
                    ? "Native image validation is pending on the Editor update loop. Retry after validation completes."
                    : cacheValidationError;
                return false;
            }
            if (!TryValidatePackageFingerprint(false, out string expectedPath, out error)) return false;
            if (!TryGetCurrentModules(false, out List<BridgeNativeModuleIdentity> modules, out error)) return false;
            if (!TryValidateLoadedModules(modules, expectedPath, default, false, out identity, out error)) return false;
            moduleLoaded = identity.IsKnown;
            return true;
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
            return TryValidateLoadedModules(modules, string.Empty, default, false, out _, out error);
        }

        internal static bool TryValidateLoadedModules(IEnumerable<BridgeNativeModuleIdentity> modules, string expectedPath,
            BridgeNativeModuleIdentity requiredIdentity, bool requireLoaded, out BridgeNativeModuleIdentity identity, out string error)
        {
            identity = default;
            error = string.Empty;
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
                        error = RestartRequired("Multiple loaded lookingglass_bridge_shim.dll images were detected");
                        return false;
                    }
                    identity = module;
                }

                if (requireLoaded && !identity.IsKnown)
                {
                    error = RestartRequired("The expected Bridge shim is not uniquely loaded with an inspectable path");
                    return false;
                }
                if (identity.IsKnown && !string.IsNullOrWhiteSpace(expectedPath) && !PathsEqual(identity.FullPath, expectedPath))
                {
                    error = RestartRequired("The loaded Bridge shim path does not match the package DLL path");
                    return false;
                }
                if (requiredIdentity.IsKnown && (!identity.IsKnown || identity.BaseAddress != requiredIdentity.BaseAddress ||
                    !PathsEqual(identity.FullPath, requiredIdentity.FullPath)))
                {
                    error = RestartRequired("The Bridge worker owner is bound to a different or unknown native image");
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                identity = default;
                error = InspectionFailure(exception);
                return false;
            }
        }

#if UNITY_EDITOR
        static BridgeNativeImageGuard()
        {
            RegisterInvalidationCallbacks();
        }

        static void RegisterInvalidationCallbacks()
        {
            if (callbacksRegistered) return;
            callbacksRegistered = true;
            EditorApplication.projectChanged += InvalidateCache;
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateCache;
            EditorApplication.update += RefreshInvalidatedCache;
        }

        static void InvalidateCache()
        {
            cacheValidationReady = false;
            cacheRefreshPending = true;
            cacheValidationError = string.Empty;
            cachedExpectedPath = string.Empty;
            cachedFingerprint = string.Empty;
            cachedFileLength = -1;
            lastFileStatFrame = int.MinValue;
            cachedModules = null;
            cachedModuleFrame = int.MinValue;
        }

        static void RefreshInvalidatedCache()
        {
            if (!cacheRefreshPending) return;
            cacheRefreshPending = false;
            if (!TryValidatePackageFingerprint(true, out string expectedPath, out string packageError))
            {
                cacheValidationError = packageError;
                return;
            }
            if (!TryGetCurrentModules(true, out List<BridgeNativeModuleIdentity> modules, out string moduleError))
            {
                cacheValidationError = moduleError;
                cacheValidationReady = false;
                cacheRefreshPending = true;
                return;
            }
            bool valid = TryValidateLoadedModules(modules, expectedPath, default, false, out _, out moduleError);
            cacheValidationReady = valid;
            cacheRefreshPending = false;
            cacheValidationError = valid ? string.Empty : moduleError;
        }

        static bool TryValidatePackageFingerprint(bool forceFresh, out string expectedPath, out string error)
        {
            expectedPath = string.Empty;
            error = string.Empty;
            try
            {
                int cacheEpoch = CurrentCacheEpoch;
                if (!forceFresh && !string.IsNullOrEmpty(cachedFingerprint) && cacheEpoch != lastFileStatFrame)
                {
                    lastFileStatFrame = cacheEpoch;
                    var cachedFile = new FileInfo(cachedExpectedPath);
                    if (!cachedFile.Exists || cachedFile.Length != cachedFileLength || cachedFile.LastWriteTimeUtc != cachedFileWriteUtc)
                    {
                        InvalidateCache();
                        error = "Native image validation is pending on the Editor update loop. Retry after validation completes.";
                        return false;
                    }
                }

                if (forceFresh || string.IsNullOrEmpty(cachedFingerprint))
                {
                    UnityEditor.PackageManager.PackageInfo package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BridgeWorkerQuiltOutput).Assembly);
                    if (package == null || string.IsNullOrWhiteSpace(package.resolvedPath))
                    {
                        error = RestartRequired("The Bridge package resolved path is unavailable");
                        return false;
                    }
                    if (!TryResolveExpectedNativePath(package.resolvedPath, File.Exists, out cachedExpectedPath, out error))
                    {
                        cacheValidationReady = false;
                        cacheRefreshPending = false;
                        cacheValidationError = error;
                        return false;
                    }
                    var file = new FileInfo(cachedExpectedPath);
                    using (var stream = file.OpenRead())
                    using (SHA256 sha = SHA256.Create())
                        cachedFingerprint = NormalizePath(cachedExpectedPath) + "|" + BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                    cachedFileLength = file.Length;
                    cachedFileWriteUtc = file.LastWriteTimeUtc;
                    lastFileStatFrame = cacheEpoch;
                }

                string previous = SessionState.GetString(SessionFingerprintKey, string.Empty);
                if (string.IsNullOrEmpty(previous)) SessionState.SetString(SessionFingerprintKey, cachedFingerprint);
                else if (!string.Equals(previous, cachedFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    error = RestartRequired("The Bridge shim package path or SHA-256 changed during this Unity Editor session");
                    return false;
                }
                expectedPath = cachedExpectedPath;
                return true;
            }
            catch (Exception exception)
            {
                expectedPath = string.Empty;
                error = RestartRequired("The expected Bridge shim could not be inspected (" + exception.GetType().Name + ")");
                return false;
            }
        }

        internal static bool TryResolveExpectedNativePath(string packageRoot, Func<string, bool> fileExists,
            out string expectedPath, out string error)
        {
            expectedPath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(packageRoot) || fileExists == null)
            {
                error = RestartRequired("The Bridge package root or file lookup is unavailable");
                return false;
            }

            try
            {
                string root = Path.GetFullPath(packageRoot);
                string directCandidate = Path.GetFullPath(Path.Combine(root, "Plugins", "x86_64", TargetModuleName));
                string bundledCandidate = Path.GetFullPath(Path.Combine(root, "ViewWeave", "Bridge", "Plugins", "x86_64", TargetModuleName));
                bool directExists = fileExists(directCandidate);
                bool bundledExists = fileExists(bundledCandidate);
                if (directExists && bundledExists)
                {
                    error = RestartRequired("The Bridge shim exists at both direct and bundled package paths");
                    return false;
                }
                if (!directExists && !bundledExists)
                {
                    error = RestartRequired("The expected package Bridge shim is missing from supported package paths");
                    return false;
                }

                expectedPath = directExists ? directCandidate : bundledCandidate;
                return true;
            }
            catch (Exception exception)
            {
                expectedPath = string.Empty;
                error = RestartRequired("The expected package Bridge shim path could not be resolved (" + exception.GetType().Name + ")");
                return false;
            }
        }

        static bool TryGetCurrentModules(bool forceFresh, out List<BridgeNativeModuleIdentity> modules, out string error)
        {
            modules = null;
            error = string.Empty;
            int cacheEpoch = CurrentCacheEpoch;
            if (!forceFresh && cachedModules != null && cachedModuleFrame == cacheEpoch)
            {
                modules = cachedModules;
                return true;
            }
            if (!BridgeNativeModuleEnumerator.TryEnumerate(out List<BridgeNativeModuleIdentity> refreshed, out error))
            {
                cacheValidationReady = false;
                cacheRefreshPending = true;
                cacheValidationError = error;
                return false;
            }
            cachedModules = refreshed;
            cachedModuleFrame = cacheEpoch;
            modules = cachedModules;
            return true;
        }
#endif

        static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
            return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizePath(string value)
        {
            return (value ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
        }

        static string RestartRequired(string reason) => reason + ". Restart the Unity Editor before starting or cleaning up a Bridge worker.";

        static string InspectionFailure(Exception exception)
        {
            string reason = exception == null ? string.Empty : " (" + exception.GetType().Name + ")";
            return "Unable to inspect loaded native modules" + reason + ". Restart the Unity Editor before starting or cleaning up a Bridge worker.";
        }
    }
}
