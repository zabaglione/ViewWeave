#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace ViewWeave.SceneComparison
{
    [InitializeOnLoad]
    public static class InstallCandidateUrpPackage
    {
        const string PendingKey = "ViewWeave.SceneComparison.CandidateUrpPending";
        const string CandidatePathKey = "ViewWeave.SceneComparison.CandidateUrpPath";
        const string CandidateUriKey = "ViewWeave.SceneComparison.CandidateUrpUri";
        const string ResultPathKey = "ViewWeave.SceneComparison.CandidateUrpResultPath";
        static AddAndRemoveRequest request;
        static double deadline;

        static InstallCandidateUrpPackage()
        {
            if (!SessionState.GetBool(PendingKey, false)) return;
            deadline = EditorApplication.timeSinceStartup + 900.0;
            EditorApplication.update += PollRegisteredPackage;
        }

        public static void Run()
        {
            string candidatePath = Environment.GetEnvironmentVariable("VW_URP_HDR_CANDIDATE_PATH") ?? "";
            string candidateUri = Environment.GetEnvironmentVariable("VW_URP_HDR_CANDIDATE_URI") ?? "";
            string resultPath = Environment.GetEnvironmentVariable("VW_URP_HDR_CANDIDATE_RESULT") ?? "";
            if (string.IsNullOrWhiteSpace(resultPath) || !Directory.Exists(candidatePath) ||
                !candidateUri.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                WriteAndExit("candidate-input-invalid", 2, candidatePath, candidateUri, resultPath);
                return;
            }

            SessionState.SetString(CandidatePathKey, candidatePath);
            SessionState.SetString(CandidateUriKey, candidateUri);
            SessionState.SetString(ResultPathKey, resultPath);
            SessionState.SetBool(PendingKey, true);
            deadline = EditorApplication.timeSinceStartup + 900.0;
            request = Client.AddAndRemove(new[] { candidateUri }, Array.Empty<string>());
            EditorApplication.update += PollRequest;
        }

        static void PollRequest()
        {
            if (request == null || !request.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup > deadline) Finish("package-request-timeout", 2);
                return;
            }
            if (request.Status != StatusCode.Success)
            {
                Finish("package-request-failed", 1);
                return;
            }
            EditorApplication.update -= PollRequest;
            EditorApplication.update += PollRegisteredPackage;
            PollRegisteredPackage();
        }

        static void PollRegisteredPackage()
        {
            string expectedPath = SessionState.GetString(CandidatePathKey, "");
            string uri = SessionState.GetString(CandidateUriKey, "");
            string resultPath = SessionState.GetString(ResultPathKey, "");
            if (string.IsNullOrWhiteSpace(expectedPath) || string.IsNullOrWhiteSpace(resultPath))
            {
                Finish("session-state-missing", 2);
                return;
            }
            string resolvedPath = "";
            string version = "";
            string source = "";
            try
            {
                UnityEditor.PackageManager.PackageInfo info = UnityEditor.PackageManager.PackageInfo.FindForPackageName("com.zabaglione.lookingglass.viewweave.urp");
                if (info != null)
                {
                    resolvedPath = Path.GetFullPath(info.resolvedPath);
                    version = info.version;
                    source = info.source.ToString();
                }
            }
            catch { }

            string normalizedExpected = Path.GetFullPath(expectedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedResolved = string.IsNullOrEmpty(resolvedPath) ? "" : resolvedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedExpected, normalizedResolved, StringComparison.OrdinalIgnoreCase))
            {
                WriteResult("candidate-package-loaded", expectedPath, uri, resultPath, resolvedPath, version, source, 0);
                return;
            }
            if (EditorApplication.timeSinceStartup > deadline) Finish("candidate-package-path-not-registered", 1);
        }

        static void Finish(string status, int exitCode)
        {
            EditorApplication.update -= PollRequest;
            EditorApplication.update -= PollRegisteredPackage;
            string path = SessionState.GetString(CandidatePathKey, Environment.GetEnvironmentVariable("VW_URP_HDR_CANDIDATE_PATH") ?? "");
            string uri = SessionState.GetString(CandidateUriKey, Environment.GetEnvironmentVariable("VW_URP_HDR_CANDIDATE_URI") ?? "");
            string result = SessionState.GetString(ResultPathKey, Environment.GetEnvironmentVariable("VW_URP_HDR_CANDIDATE_RESULT") ?? "");
            if (string.IsNullOrWhiteSpace(result)) result = Path.Combine(Application.dataPath, "../TestResults/candidate-urp-package.json");
            WriteResult(status, path, uri, result, "", "", "", exitCode);
        }

        static void WriteAndExit(string status, int exitCode, string path, string uri, string result)
        {
            if (string.IsNullOrWhiteSpace(result)) result = Path.Combine(Application.dataPath, "../TestResults/candidate-urp-package.json");
            WriteResult(status, path, uri, result, "", "", "", exitCode);
        }

        static void WriteResult(string status, string candidatePath, string candidateUri, string resultPath,
            string resolvedPath, string version, string source, int exitCode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath)));
            var result = new PackageResult
            {
                status = status,
                packageName = "com.zabaglione.lookingglass.viewweave.urp",
                candidatePath = candidatePath,
                candidateUri = candidateUri,
                resolvedPath = resolvedPath,
                version = version,
                source = source,
                exitCode = exitCode
            };
            File.WriteAllText(resultPath, UnityEngine.JsonUtility.ToJson(result, true), new UTF8Encoding(false));
            SessionState.SetBool(PendingKey, false);
            SessionState.EraseString(CandidatePathKey);
            SessionState.EraseString(CandidateUriKey);
            SessionState.EraseString(ResultPathKey);
            EditorApplication.Exit(exitCode);
        }

        [Serializable]
        sealed class PackageResult
        {
            public string status;
            public string packageName;
            public string candidatePath;
            public string candidateUri;
            public string resolvedPath;
            public string version;
            public string source;
            public int exitCode;
        }
    }
}
#endif
