#if UNITY_EDITOR
using System;
using System.Collections.Generic;
#if UNITY_EDITOR_WIN
using System.Runtime.InteropServices;
using System.Text;
#endif

namespace ViewWeave
{
    internal struct BridgeToolhelpModuleEntry
    {
        public string ModuleName;
        public IntPtr BaseAddress;
        public IntPtr ModuleHandle;
    }

    internal interface IBridgeToolhelpModuleApi
    {
        IntPtr CreateSnapshot(out int errorCode);
        bool ModuleFirst(IntPtr snapshot, out BridgeToolhelpModuleEntry entry, out int errorCode);
        bool ModuleNext(IntPtr snapshot, out BridgeToolhelpModuleEntry entry, out int errorCode);
        bool TryGetModulePath(IntPtr moduleHandle, out string path, out int errorCode);
        bool CloseSnapshot(IntPtr snapshot, out int errorCode);
    }

    internal static class BridgeNativeModuleEnumerator
    {
        internal const int ErrorBadLength = 24;
        internal const int ErrorNoMoreFiles = 18;
        internal const int MaximumSnapshotAttempts = 5;

#if UNITY_EDITOR_WIN
        static readonly IBridgeToolhelpModuleApi windowsApi = new WindowsToolhelpModuleApi();
#else
        static readonly IBridgeToolhelpModuleApi windowsApi;
#endif
        static IBridgeToolhelpModuleApi testApi;

        internal static void SetApiForTests(IBridgeToolhelpModuleApi api) => testApi = api;

        internal static bool TryEnumerate(out List<BridgeNativeModuleIdentity> modules, out string error)
        {
            return TryEnumerate(testApi ?? windowsApi, out modules, out error);
        }

        internal static bool TryEnumerate(IBridgeToolhelpModuleApi api,
            out List<BridgeNativeModuleIdentity> modules, out string error)
        {
            modules = null;
            error = string.Empty;
            if (api == null)
            {
                error = "The current-process module snapshot adapter is unavailable.";
                return false;
            }

            for (int attempt = 0; attempt < MaximumSnapshotAttempts; ++attempt)
            {
                IntPtr snapshot = IntPtr.Zero;
                bool retry = false;
                bool complete = false;
                var candidate = new List<BridgeNativeModuleIdentity>();
                try
                {
                    snapshot = api.CreateSnapshot(out int createError);
                    if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                    {
                        retry = createError == ErrorBadLength;
                        if (!retry) error = Win32Failure("create the module snapshot", createError);
                    }
                    else if (!EnumerateSnapshot(api, snapshot, candidate, out retry, out error))
                    {
                        complete = false;
                    }
                    else
                    {
                        complete = true;
                    }
                }
                catch (Exception exception)
                {
                    error = "Unable to inspect the current-process module snapshot (" + exception.GetType().Name + ").";
                    retry = true;
                }
                finally
                {
                    if (snapshot != IntPtr.Zero && snapshot != new IntPtr(-1))
                    {
                        try
                        {
                            if (!api.CloseSnapshot(snapshot, out int closeError))
                            {
                                complete = false;
                                retry = closeError == ErrorBadLength;
                                if (!retry) error = Win32Failure("close the module snapshot", closeError);
                            }
                        }
                        catch (Exception exception)
                        {
                            complete = false;
                            retry = true;
                            error = "Unable to close the current-process module snapshot (" + exception.GetType().Name + ").";
                        }
                    }
                }

                if (complete)
                {
                    modules = candidate;
                    error = string.Empty;
                    return true;
                }
                if (!retry) return false;
            }

            error = "The current-process module list changed during every bounded snapshot attempt.";
            return false;
        }

        static bool EnumerateSnapshot(IBridgeToolhelpModuleApi api, IntPtr snapshot,
            List<BridgeNativeModuleIdentity> modules, out bool retry, out string error)
        {
            retry = false;
            error = string.Empty;
            if (!api.ModuleFirst(snapshot, out BridgeToolhelpModuleEntry entry, out int firstError))
            {
                if (firstError == ErrorNoMoreFiles) return true;
                retry = firstError == ErrorBadLength;
                if (!retry) error = Win32Failure("read the first module entry", firstError);
                return false;
            }

            while (true)
            {
                if (string.Equals(entry.ModuleName, "lookingglass_bridge_shim.dll", StringComparison.OrdinalIgnoreCase))
                {
                    if (!api.TryGetModulePath(entry.ModuleHandle, out string path, out int pathError) ||
                        string.IsNullOrWhiteSpace(path))
                    {
                        retry = true;
                        error = pathError == 0
                            ? "Unable to resolve the loaded Bridge shim path from the module handle."
                            : Win32Failure("resolve the loaded Bridge shim path", pathError);
                        return false;
                    }
                    modules.Add(new BridgeNativeModuleIdentity(entry.ModuleName, entry.BaseAddress, path));
                }

                if (api.ModuleNext(snapshot, out entry, out int nextError)) continue;
                if (nextError == ErrorNoMoreFiles) return true;
                retry = nextError == ErrorBadLength;
                if (!retry) error = Win32Failure("read the next module entry", nextError);
                return false;
            }
        }

        static string Win32Failure(string operation, int errorCode)
        {
            return "Unable to " + operation + " (Win32 error " + errorCode + ").";
        }

#if UNITY_EDITOR_WIN
        sealed class WindowsToolhelpModuleApi : IBridgeToolhelpModuleApi
        {
            const uint SnapshotModule = 0x00000008;
            const uint SnapshotModule32 = 0x00000010;
            const int InitialPathCapacity = 260;
            const int MaximumPathCapacity = 32768;

            public IntPtr CreateSnapshot(out int errorCode)
            {
                IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotModule | SnapshotModule32, GetCurrentProcessId());
                errorCode = snapshot == IntPtr.Zero || snapshot == new IntPtr(-1) ? Marshal.GetLastWin32Error() : 0;
                return snapshot;
            }

            public bool ModuleFirst(IntPtr snapshot, out BridgeToolhelpModuleEntry entry, out int errorCode)
            {
                var native = NewNativeEntry();
                bool succeeded = Module32FirstW(snapshot, ref native);
                errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
                entry = succeeded ? Convert(native) : default;
                return succeeded;
            }

            public bool ModuleNext(IntPtr snapshot, out BridgeToolhelpModuleEntry entry, out int errorCode)
            {
                var native = NewNativeEntry();
                bool succeeded = Module32NextW(snapshot, ref native);
                errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
                entry = succeeded ? Convert(native) : default;
                return succeeded;
            }

            public bool TryGetModulePath(IntPtr moduleHandle, out string path, out int errorCode)
            {
                path = string.Empty;
                errorCode = 0;
                for (int capacity = InitialPathCapacity; capacity <= MaximumPathCapacity; capacity = Math.Min(MaximumPathCapacity, capacity * 2))
                {
                    var buffer = new StringBuilder(capacity);
                    uint length = GetModuleFileNameW(moduleHandle, buffer, (uint)buffer.Capacity);
                    if (length == 0)
                    {
                        errorCode = Marshal.GetLastWin32Error();
                        return false;
                    }
                    if (length < buffer.Capacity)
                    {
                        path = buffer.ToString();
                        return true;
                    }
                    if (capacity == MaximumPathCapacity)
                    {
                        errorCode = 122;
                        return false;
                    }
                }
                errorCode = 122;
                return false;
            }

            public bool CloseSnapshot(IntPtr snapshot, out int errorCode)
            {
                bool succeeded = CloseHandle(snapshot);
                errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
                return succeeded;
            }

            static NativeModuleEntry NewNativeEntry() => new NativeModuleEntry
            {
                size = (uint)Marshal.SizeOf(typeof(NativeModuleEntry))
            };

            static BridgeToolhelpModuleEntry Convert(NativeModuleEntry entry) => new BridgeToolhelpModuleEntry
            {
                ModuleName = entry.moduleName ?? string.Empty,
                BaseAddress = entry.baseAddress,
                ModuleHandle = entry.moduleHandle
            };

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            struct NativeModuleEntry
            {
                public uint size;
                public uint moduleId;
                public uint processId;
                public uint globalUsage;
                public uint processUsage;
                public IntPtr baseAddress;
                public uint baseSize;
                public IntPtr moduleHandle;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string moduleName;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string executablePath;
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

            [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool Module32FirstW(IntPtr snapshot, ref NativeModuleEntry entry);

            [DllImport("kernel32.dll", EntryPoint = "Module32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern bool Module32NextW(IntPtr snapshot, ref NativeModuleEntry entry);

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll")]
            static extern uint GetCurrentProcessId();

            [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern uint GetModuleFileNameW(IntPtr module, StringBuilder fileName, uint size);
        }
#endif
    }
}
#endif
