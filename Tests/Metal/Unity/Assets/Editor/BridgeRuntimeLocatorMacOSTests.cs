#if UNITY_EDITOR_OSX
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using ViewWeave;

public static class BridgeRuntimeLocatorMacOSTests
{
    [MenuItem("Tools/ViewWeave/Run macOS Bridge Runtime Locator Validation")]
    public static void RunValidation()
    {
        string root = Path.Combine(Path.GetTempPath(), "viewweave-bridge-locator-" + Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "Looking Glass Bridge Test.app");
        string executableDirectory = Path.Combine(app, "Contents", "MacOS");
        string runtime = Path.Combine(executableDirectory, "libbridge_inproc.dylib");
        string environmentDirectory = Path.Combine(root, "EnvironmentRuntime");
        string environmentRuntime = Path.Combine(environmentDirectory, "libbridge_inproc.dylib");
        string previousEnvironmentPath = Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
        try
        {
            Directory.CreateDirectory(executableDirectory);
            Directory.CreateDirectory(environmentDirectory);
            File.WriteAllText(runtime, "test runtime placeholder");
            File.WriteAllText(environmentRuntime, "test environment runtime placeholder");

            Require(BridgeRuntimeLocator.TryResolve(app, out string resolved, out string error), "app bundle path resolves: " + error);
            Require(resolved == runtime, "app bundle resolves to its Contents/MacOS runtime");
            Require(BridgeRuntimeLocator.TryResolve(executableDirectory, out resolved, out error), "runtime directory resolves: " + error);
            Require(resolved == runtime, "runtime directory resolves to its dylib");
            Require(BridgeRuntimeLocator.TryResolve(runtime, out resolved, out error), "dylib path resolves: " + error);
            Require(resolved == runtime, "dylib path is preserved");

            Environment.SetEnvironmentVariable("LKG_BRIDGE_PATH", environmentDirectory);
            Require(BridgeRuntimeLocator.TryResolve(app, out resolved, out error), "explicit path takes priority over the environment: " + error);
            Require(resolved == runtime, "explicit app bundle path has priority over LKG_BRIDGE_PATH");
            Require(BridgeRuntimeLocator.TryResolve(string.Empty, out resolved, out error), "environment directory resolves: " + error);
            Require(resolved == environmentRuntime, "LKG_BRIDGE_PATH accepts a runtime directory");

            Require(!BridgeRuntimeLocator.TryResolve("relative-bridge.dylib", out _, out string relativeError), "relative paths are rejected");
            Require(IsAscii(relativeError), "locator errors contain ASCII only");
            Require(!BridgeRuntimeLocator.TryResolve(Path.Combine(root, "Missing.app"), out _, out string missingAppError), "missing app bundles are rejected");
            Require(missingAppError.Contains("app bundle was not found") && IsAscii(missingAppError), "missing app bundles report an ASCII error");
            Debug.Log("macOS Bridge runtime locator tests passed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("LKG_BRIDGE_PATH", previousEnvironmentPath);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    static bool IsAscii(string value)
    {
        if (value == null) return false;
        foreach (char character in value) if (character > 0x7f) return false;
        return true;
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("macOS Bridge runtime locator test failed: " + message);
    }
}
#endif
