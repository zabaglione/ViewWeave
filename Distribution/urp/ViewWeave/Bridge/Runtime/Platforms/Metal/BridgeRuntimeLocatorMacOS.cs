using System;
using System.Collections.Generic;
using System.IO;

namespace ViewWeave
{
#if UNITY_EDITOR_OSX || (UNITY_STANDALONE_OSX && !UNITY_EDITOR)
    internal static class BridgeRuntimeLocatorMacOS
    {
        const string RuntimeName = "libbridge_inproc.dylib";
        const string ApplicationPrefix = "Looking Glass Bridge ";
        const string ApplicationSuffix = ".app";

        internal static bool TryResolve(out string path, out string error)
        {
            var roots = new List<string> { "/Applications" };
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                string userApplications = Path.Combine(userProfile, "Applications");
                if (!string.Equals(userApplications, roots[0], StringComparison.Ordinal)) roots.Add(userApplications);
            }
            return TryResolveFromRoots(roots.ToArray(), out path, out error);
        }

        internal static bool TryResolveFromRoots(string[] roots, out string path, out string error)
        {
            path = null;
            error = string.Empty;
            var candidates = new List<InstalledRuntime>();
            string discoveryFailure = null;
            string invalidBundle = null;

            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root)) continue;
                    string[] entries;
                    try
                    {
                        entries = Directory.GetFileSystemEntries(root);
                    }
                    catch (DirectoryNotFoundException)
                    {
                        continue;
                    }
                    catch (FileNotFoundException)
                    {
                        continue;
                    }
                    catch (Exception exception)
                    {
                        if (discoveryFailure == null) discoveryFailure = Sanitize(exception.Message);
                        continue;
                    }

                    foreach (string entry in entries)
                    {
                        if (!TryGetApplicationVersion(Path.GetFileName(entry), out SemanticVersion version)) continue;
                        if (!TryValidatePath(entry, out string runtimePath, out string bundleError))
                        {
                            if (invalidBundle == null) invalidBundle = bundleError;
                            continue;
                        }
                        candidates.Add(new InstalledRuntime(version, runtimePath));
                    }
                }
            }

            if (candidates.Count == 0)
            {
                if (invalidBundle != null)
                {
                    error = invalidBundle;
                    return false;
                }
                error = discoveryFailure == null
                    ? "Looking Glass Bridge runtime was not found in /Applications or ~/Applications"
                    : "Bridge runtime discovery failed: " + discoveryFailure;
                return false;
            }

            SemanticVersion newest = candidates[0].Version;
            for (int index = 1; index < candidates.Count; ++index)
            {
                if (candidates[index].Version.CompareTo(newest) > 0) newest = candidates[index].Version;
            }

            string newestPath = null;
            int newestCount = 0;
            foreach (InstalledRuntime candidate in candidates)
            {
                if (candidate.Version.CompareTo(newest) != 0) continue;
                newestPath = candidate.Path;
                ++newestCount;
            }
            if (newestCount > 1)
            {
                error = "Multiple Looking Glass Bridge runtimes have the same newest version; set LKG_BRIDGE_PATH or an explicit path";
                return false;
            }

            path = newestPath;
            return true;
        }

        internal static bool TryValidatePath(string value, out string path, out string error)
        {
            path = null;
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                {
                    error = "Bridge runtime path must be absolute";
                    return false;
                }

                string fullPath = Path.GetFullPath(value);
                string candidate;
                if (fullPath.EndsWith(ApplicationSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(fullPath))
                    {
                        error = "Bridge runtime app bundle was not found: " + Sanitize(fullPath);
                        return false;
                    }
                    candidate = Path.Combine(fullPath, "Contents", "MacOS", RuntimeName);
                }
                else if (string.Equals(Path.GetFileName(fullPath), RuntimeName, StringComparison.Ordinal))
                {
                    candidate = fullPath;
                }
                else if (Directory.Exists(fullPath))
                {
                    candidate = Path.Combine(fullPath, RuntimeName);
                }
                else if (string.Equals(Path.GetExtension(fullPath), ".dylib", StringComparison.OrdinalIgnoreCase))
                {
                    error = "Bridge runtime dylib must be named " + RuntimeName;
                    return false;
                }
                else if (File.Exists(fullPath))
                {
                    error = "Bridge runtime path must point to " + RuntimeName + ", a directory, or a .app bundle";
                    return false;
                }
                else
                {
                    error = "Bridge runtime directory was not found: " + Sanitize(fullPath);
                    return false;
                }

                if (!File.Exists(candidate))
                {
                    error = "Bridge runtime dylib was not found: " + Sanitize(candidate);
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

        static bool TryGetApplicationVersion(string name, out SemanticVersion version)
        {
            version = default(SemanticVersion);
            if (string.IsNullOrEmpty(name) || !name.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(ApplicationSuffix, StringComparison.OrdinalIgnoreCase)) return false;

            string versionText = name.Substring(ApplicationPrefix.Length, name.Length - ApplicationPrefix.Length - ApplicationSuffix.Length).Trim();
            return SemanticVersion.TryParse(versionText, out version);
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }

        sealed class InstalledRuntime
        {
            internal readonly SemanticVersion Version;
            internal readonly string Path;

            internal InstalledRuntime(SemanticVersion version, string path)
            {
                Version = version;
                Path = path;
            }
        }

        struct SemanticVersion : IComparable<SemanticVersion>
        {
            readonly string major;
            readonly string minor;
            readonly string patch;
            readonly string[] prerelease;

            SemanticVersion(string major, string minor, string patch, string[] prerelease)
            {
                this.major = major;
                this.minor = minor;
                this.patch = patch;
                this.prerelease = prerelease;
            }

            internal static bool TryParse(string value, out SemanticVersion version)
            {
                version = default(SemanticVersion);
                if (string.IsNullOrEmpty(value)) return false;

                string coreAndPrerelease = value;
                int buildSeparator = coreAndPrerelease.IndexOf('+');
                if (buildSeparator >= 0)
                {
                    string build = coreAndPrerelease.Substring(buildSeparator + 1);
                    if (!IsValidIdentifierList(build, false) || coreAndPrerelease.IndexOf('+', buildSeparator + 1) >= 0) return false;
                    coreAndPrerelease = coreAndPrerelease.Substring(0, buildSeparator);
                }

                string[] prereleaseIdentifiers = null;
                int prereleaseSeparator = coreAndPrerelease.IndexOf('-');
                if (prereleaseSeparator >= 0)
                {
                    string prereleaseText = coreAndPrerelease.Substring(prereleaseSeparator + 1);
                    if (!IsValidIdentifierList(prereleaseText, true)) return false;
                    prereleaseIdentifiers = prereleaseText.Split('.');
                    coreAndPrerelease = coreAndPrerelease.Substring(0, prereleaseSeparator);
                }

                string[] core = coreAndPrerelease.Split('.');
                if (core.Length != 3 || !IsValidCoreNumber(core[0]) || !IsValidCoreNumber(core[1]) || !IsValidCoreNumber(core[2])) return false;
                version = new SemanticVersion(core[0], core[1], core[2], prereleaseIdentifiers);
                return true;
            }

            public int CompareTo(SemanticVersion other)
            {
                int comparison = CompareNumericIdentifier(major, other.major);
                if (comparison != 0) return comparison;
                comparison = CompareNumericIdentifier(minor, other.minor);
                if (comparison != 0) return comparison;
                comparison = CompareNumericIdentifier(patch, other.patch);
                if (comparison != 0) return comparison;
                if (prerelease == null) return other.prerelease == null ? 0 : 1;
                if (other.prerelease == null) return -1;

                int sharedCount = Math.Min(prerelease.Length, other.prerelease.Length);
                for (int index = 0; index < sharedCount; ++index)
                {
                    string left = prerelease[index];
                    string right = other.prerelease[index];
                    bool leftNumeric = IsNumericIdentifier(left);
                    bool rightNumeric = IsNumericIdentifier(right);
                    if (leftNumeric && rightNumeric) comparison = CompareNumericIdentifier(left, right);
                    else if (leftNumeric != rightNumeric) comparison = leftNumeric ? -1 : 1;
                    else comparison = string.Compare(left, right, StringComparison.Ordinal);
                    if (comparison != 0) return comparison;
                }
                return prerelease.Length.CompareTo(other.prerelease.Length);
            }

            static bool IsValidCoreNumber(string value)
            {
                return IsNumericIdentifier(value) && (value.Length == 1 || value[0] != '0');
            }

            static bool IsValidIdentifierList(string value, bool rejectNumericLeadingZero)
            {
                if (string.IsNullOrEmpty(value)) return false;
                string[] identifiers = value.Split('.');
                foreach (string identifier in identifiers)
                {
                    if (identifier.Length == 0) return false;
                    foreach (char character in identifier)
                    {
                        if ((character < '0' || character > '9') && (character < 'A' || character > 'Z') &&
                            (character < 'a' || character > 'z') && character != '-') return false;
                    }
                    if (rejectNumericLeadingZero && IsNumericIdentifier(identifier) && identifier.Length > 1 && identifier[0] == '0') return false;
                }
                return true;
            }

            static bool IsNumericIdentifier(string value)
            {
                if (string.IsNullOrEmpty(value)) return false;
                foreach (char character in value) if (character < '0' || character > '9') return false;
                return true;
            }

            static int CompareNumericIdentifier(string left, string right)
            {
                if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
                return string.Compare(left, right, StringComparison.Ordinal);
            }
        }
    }
#endif
}
