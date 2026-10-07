using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;

namespace ViewWeave
{
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    internal static class BridgeRuntimeLocatorWindows
    {
        internal static bool TryResolve(out string path, out string error)
        {
            try
            {
                return TryResolveRegistryCore(out path, out error);
            }
            catch (Exception exception)
            {
                path = null;
                error = "Bridge runtime registry discovery failed: " + Sanitize(exception.Message);
                return false;
            }
        }

        static bool TryResolveRegistryCore(out string path, out string error)
        {
            path = null;
            error = string.Empty;
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] locations =
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            IntPtr[] roots = { HKeyLocalMachine, HKeyCurrentUser };
            uint[] views = { KeyWow64_64Key, KeyWow64_32Key };
            int lastError = ErrorFileNotFound;
            foreach (IntPtr root in roots)
            foreach (uint view in views)
            {
                foreach (string location in locations)
                {
                    IntPtr uninstall;
                    int openResult = RegOpenKeyEx(root, location, 0, KeyRead | view, out uninstall);
                    if (openResult == ErrorFileNotFound) continue;
                    if (openResult != ErrorSuccess)
                    {
                        lastError = openResult;
                        continue;
                    }
                    try
                    {
                        for (uint index = 0; index < MaxRegistryEntries; ++index)
                        {
                            StringBuilder name = new StringBuilder((int)MaxRegistryNameChars);
                            uint nameLength = MaxRegistryNameChars - 1;
                            int enumResult = RegEnumKeyEx(uninstall, index, name, ref nameLength, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                            if (enumResult == ErrorNoMoreItems) break;
                            if (enumResult != ErrorSuccess)
                            {
                                lastError = enumResult;
                                break;
                            }
                            IntPtr entry;
                            int entryResult = RegOpenKeyEx(uninstall, name.ToString(), 0, KeyRead | view, out entry);
                            if (entryResult != ErrorSuccess)
                            {
                                if (entryResult != ErrorFileNotFound) lastError = entryResult;
                                continue;
                            }
                            try
                            {
                                string displayName = QueryRegistryString(entry, "DisplayName", ref lastError);
                                if (!string.Equals(displayName, "Looking Glass Bridge", StringComparison.OrdinalIgnoreCase)) continue;
                                string installLocation = QueryRegistryString(entry, "InstallLocation", ref lastError);
                                if (string.IsNullOrWhiteSpace(installLocation)) continue;
                                installLocation = Environment.ExpandEnvironmentVariables(installLocation);
                                if (BridgeRuntimeLocator.TryValidatePath(installLocation, out string candidate, out _)) candidates.Add(candidate);
                            }
                            finally
                            {
                                RegCloseKey(entry);
                            }
                        }
                    }
                    finally
                    {
                        RegCloseKey(uninstall);
                    }
                }
            }
            if (candidates.Count == 0)
            {
                error = lastError == ErrorFileNotFound || lastError == ErrorNoMoreItems
                    ? "Looking Glass Bridge runtime was not found in Windows registry"
                    : "Bridge runtime registry discovery failed: win32=" + lastError;
                return false;
            }
            if (candidates.Count > 1)
            {
                error = "Multiple Looking Glass Bridge runtimes found; set LKG_BRIDGE_PATH or an explicit path";
                return false;
            }
            foreach (string candidate in candidates)
            {
                path = candidate;
                break;
            }
            return true;
        }

        static string QueryRegistryString(IntPtr key, string valueName, ref int lastError)
        {
            uint type;
            uint byteCount = RegistryBufferBytes;
            byte[] data = new byte[RegistryBufferBytes];
            int result = RegQueryValueEx(key, valueName, IntPtr.Zero, out type, data, ref byteCount);
            if (result != ErrorSuccess)
            {
                if (result != ErrorFileNotFound) lastError = result;
                return null;
            }
            if (type != RegistryString && type != RegistryExpandString || byteCount == 0 || byteCount > RegistryBufferBytes) return null;
            int characterCount = (int)(byteCount / 2);
            return Encoding.Unicode.GetString(data, 0, characterCount * 2).TrimEnd('\0');
        }

        const uint KeyRead = 0x20019;
        const uint KeyWow64_64Key = 0x0100;
        const uint KeyWow64_32Key = 0x0200;
        const uint RegistryString = 1;
        const uint RegistryExpandString = 2;
        const uint RegistryBufferBytes = 16 * 1024;
        const uint MaxRegistryEntries = 4096;
        const uint MaxRegistryNameChars = 256;
        const int ErrorSuccess = 0;
        const int ErrorFileNotFound = 2;
        const int ErrorNoMoreItems = 259;
        static readonly IntPtr HKeyCurrentUser = new IntPtr(unchecked((int)0x80000001));
        static readonly IntPtr HKeyLocalMachine = new IntPtr(unchecked((int)0x80000002));

        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
        static extern int RegOpenKeyEx(IntPtr key, string subKey, uint options, uint samDesired, out IntPtr result);

        [DllImport("advapi32.dll", EntryPoint = "RegEnumKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
        static extern int RegEnumKeyEx(IntPtr key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, IntPtr className, IntPtr classLength, IntPtr lastWriteTime);

        [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = false)]
        static extern int RegQueryValueEx(IntPtr key, string valueName, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);

        [DllImport("advapi32.dll", SetLastError = false)]
        static extern int RegCloseKey(IntPtr key);

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }
    }
#endif
}
