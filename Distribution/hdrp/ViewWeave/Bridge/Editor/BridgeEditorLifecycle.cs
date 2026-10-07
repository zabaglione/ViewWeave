using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    internal static class BridgeEditorLifecycle
    {
        static bool recovering;
        static bool shutdownStarted;
        static bool recoveryAbiUnavailable;
        static double deadline;

        static BridgeEditorLifecycle()
        {
            StartRecoveryPump();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeginRecovery;
            EditorApplication.quitting += BeginRecovery;
            EditorApplication.update += Update;
            EditorApplication.delayCall += ResumeRecovery;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) BeginRecovery();
        }

        static void ResumeRecovery()
        {
            try
            {
                BridgeWorkerQuiltOutput.CollectClosedNativeWorkers();
                if (BridgeWorkerQuiltOutput.NativePendingCount != 0 || BridgeWorkerQuiltOutput.ShutdownPendingCount != 0) StartRecoveryPump();
                else
                {
                    recovering = false;
                    shutdownStarted = false;
                    ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                }
            }
            catch (System.DllNotFoundException) { }
            catch (System.EntryPointNotFoundException) { }
        }

        static void StartRecoveryPump()
        {
            recovering = true;
            recoveryAbiUnavailable = false;
            deadline = EditorApplication.timeSinceStartup + 1.0;
            ViewWeave.BridgeWorkerLifecycle.BeginRecovery();
        }

        static void BeginRecovery()
        {
            if (shutdownStarted) return;
            shutdownStarted = true;
            if (!recovering)
            {
                recovering = true;
                recoveryAbiUnavailable = false;
                deadline = EditorApplication.timeSinceStartup + 1.0;
            }
            ViewWeave.BridgeWorkerLifecycle.BeginRecovery();
            foreach (ViewWeaveBridgeSession session in Resources.FindObjectsOfTypeAll<ViewWeaveBridgeSession>())
            {
                if (session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle) session.StopSession();
            }
            foreach (BridgeWorkerQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeWorkerQuiltOutput>())
            {
                if (output != null) output.Dispose();
            }
            try
            {
                BridgeWorkerQuiltOutput.CloseAllNativeWorkers();
                BridgeWorkerReaper.DetachOwnedResourcesForRecovery();
            }
            catch (System.DllNotFoundException)
            {
                Debug.LogWarning("[ViewWeave] Bridge worker native library is unavailable during shutdown");
                recoveryAbiUnavailable = true;
                recovering = false;
                shutdownStarted = false;
                ViewWeave.BridgeWorkerLifecycle.EndRecovery();
            }
            catch (System.EntryPointNotFoundException)
            {
                Debug.LogWarning("[ViewWeave] Bridge worker shutdown ABI is unavailable");
                recoveryAbiUnavailable = true;
                recovering = false;
                shutdownStarted = false;
                ViewWeave.BridgeWorkerLifecycle.EndRecovery();
            }
        }

        static void Update()
        {
            if (!recovering) return;
            try
            {
                BridgeWorkerQuiltOutput.CollectClosedNativeWorkers();
                if (BridgeWorkerQuiltOutput.NativePendingCount == 0 && BridgeWorkerQuiltOutput.ShutdownPendingCount == 0)
                {
                    recovering = false;
                    shutdownStarted = false;
                    ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                    return;
                }
            }
            catch (System.DllNotFoundException)
            {
                if (!recoveryAbiUnavailable) Debug.LogWarning("[ViewWeave] Bridge worker native library is unavailable during recovery");
                recoveryAbiUnavailable = true;
                recovering = false;
                shutdownStarted = false;
                ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                return;
            }
            catch (System.EntryPointNotFoundException)
            {
                if (!recoveryAbiUnavailable) Debug.LogWarning("[ViewWeave] Bridge worker recovery ABI is unavailable");
                recoveryAbiUnavailable = true;
                recovering = false;
                shutdownStarted = false;
                ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                return;
            }
            if (EditorApplication.timeSinceStartup > deadline)
            {
                Debug.LogWarning("[ViewWeave] Bridge worker shutdown is still pending; recovery will continue");
                deadline = EditorApplication.timeSinceStartup + 1.0;
            }
        }
    }
}
