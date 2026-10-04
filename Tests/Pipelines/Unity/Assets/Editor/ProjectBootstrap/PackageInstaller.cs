using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace ProjectBootstrap
{
    [InitializeOnLoad]
    public static class PackageInstaller
    {
        static readonly string[] PackagesToAdd =
        {
            "com.unity.ugui",
            "com.unity.modules.particlesystem@1.0.0",
            "com.unity.modules.uielements@1.0.0",
            "com.unity.inputsystem"
        };
        static readonly string[] BridgePackagesToAdd =
        {
            "file:../../../../Packages/com.zabaglione.lookingglass.viewweave.bridge"
        };
        const double TimeoutSeconds = 300.0;
        const string ActiveKey = "ProjectBootstrap.PackageInstaller.Active";
        const string DeadlineKey = "ProjectBootstrap.PackageInstaller.Deadline";
        const string BridgeModeKey = "ProjectBootstrap.PackageInstaller.BridgeMode";
        static AddAndRemoveRequest request;
        static double deadline;

        static PackageInstaller()
        {
            if (SessionState.GetBool(ActiveKey, false))
                EditorApplication.delayCall += ResumeAfterReload;
        }

        public static void Install()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Debug.LogError("[PackageInstaller] Install is already active.");
                EditorApplication.Exit(1);
                return;
            }
            StartRequest(false, PackagesToAdd);
        }

        public static void InstallBridge()
        {
            if (SessionState.GetBool(ActiveKey, false))
            {
                Debug.LogError("[PackageInstaller] Install is already active.");
                EditorApplication.Exit(1);
                return;
            }
            StartRequest(false, BridgePackagesToAdd);
        }

        static void ResumeAfterReload()
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            StartRequest(true, SessionState.GetBool(BridgeModeKey, false) ? BridgePackagesToAdd : PackagesToAdd);
        }

        static void StartRequest(bool resumed, string[] packages)
        {
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetBool(BridgeModeKey, packages == BridgePackagesToAdd);
            Debug.Log("[PackageInstaller] Adding: " + string.Join(", ", packages));
            if (resumed && double.TryParse(SessionState.GetString(DeadlineKey, "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var savedDeadline)) deadline = savedDeadline;
            else { deadline = EditorApplication.timeSinceStartup + TimeoutSeconds; SessionState.SetString(DeadlineKey, deadline.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            if (EditorApplication.timeSinceStartup > deadline) { Finish(2, "Timed out waiting for UPM."); return; }
            try { request = Client.AddAndRemove(packages, Array.Empty<string>()); }
            catch (Exception error) { Finish(1, Sanitize(error.Message)); return; }
            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (request == null) return;
            if (!request.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup > deadline) Finish(2, "Timed out waiting for UPM.");
                return;
            }
            if (request.Status == StatusCode.Success)
            {
                var resolved = request.Result == null ? string.Empty : string.Join(", ", request.Result.Select(package => package.name + "@" + package.version));
                Debug.Log("[PackageInstaller] Resolved: " + resolved);
                Finish(0, null);
            }
            else Finish(1, request.Error == null ? "Package resolution failed." : request.Error.message);
        }

        static void Finish(int code, string error)
        {
            EditorApplication.update -= Poll;
            request = null;
            SessionState.SetBool(ActiveKey, false);
            SessionState.SetBool(BridgeModeKey, false);
            SessionState.EraseString(DeadlineKey);
            if (!string.IsNullOrEmpty(error)) Debug.LogError("[PackageInstaller] " + Sanitize(error));
            EditorApplication.Exit(code);
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "Package operation failed.";
            var chars = value.Where(character => character >= 0x20 && character <= 0x7e).ToArray();
            return new string(chars);
        }
    }
}
