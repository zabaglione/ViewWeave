using System;
using System.Collections.Generic;
using System.IO;

namespace ViewWeave
{
    internal static class BridgeRuntimeLocatorWindowsPath
    {
        const string RuntimeName = "bridge_inproc.dll";
        internal static bool TryValidatePath(string value, out string path, out string error)
        {
            path = null;
            error = string.Empty;
            try
            {
                if (!Path.IsPathFullyQualified(value))
                {
                    error = "Bridge runtime path must be absolute";
                    return false;
                }
                string candidate = value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? value : Path.Combine(value, RuntimeName);
                candidate = Path.GetFullPath(candidate);
                if (!File.Exists(candidate))
                {
                    error = "Bridge runtime DLL was not found: " + Sanitize(candidate);
                    return false;
                }
                path = candidate;
                return true;
            }
            catch (Exception exception)
            {
                error = "Bridge runtime path validation failed: " + Sanitize(exception.Message);
                return false;
            }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }
    }
}
