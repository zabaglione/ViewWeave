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
        static bool finalQuitReissued;

        static BridgeEditorLifecycle()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            StartRecoveryPump();
            if (BridgeWorkerLifecycle.FinalShutdownPending) BridgeWorkerReaper.PrepareForFinalShutdown();
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.wantsToQuit += OnWantsToQuit;
            EditorApplication.quitting += OnEditorQuitting;
            EditorApplication.update += Update;
            EditorApplication.delayCall += ResumeRecovery;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) BeginRecovery(false);
        }

        static void OnBeforeAssemblyReload() { BeginRecovery(true); }

        static void ResumeRecovery()
        {
            try
            {
                BridgeQuiltPlaybackLease.RecoverRetainedRecords();
                BridgeWorkerReaper.PumpNow();
                uint nativePending = BridgeWorkerQuiltOutput.NativePendingCount;
                if (nativePending != 0)
                {
                    int closeAllResult = BridgeWorkerQuiltOutput.CloseAllNativeWorkers();
                    if (closeAllResult != 0) Debug.LogWarning("[ViewWeave] Recovered Bridge workers are still closing (code " + closeAllResult + ")");
                    StartRecoveryPump();
                }
                else if (BridgeWorkerReaper.PendingCount != 0) StartRecoveryPump();
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

        static bool OnWantsToQuit()
        {
            if (BridgeWorkerShutdownCoordinator.FinalizationReady) return true;
            return BridgeWorkerShutdownCoordinator.RequestQuit();
        }

        static void OnEditorQuitting()
        {
            if (BridgeWorkerShutdownCoordinator.FinalizationReady) return;
            BridgeWorkerShutdownCoordinator.HandleEditorQuitBypass();
        }

        static void StartRecoveryPump()
        {
            recovering = true;
            recoveryAbiUnavailable = false;
            deadline = EditorApplication.timeSinceStartup + 1.0;
            ViewWeave.BridgeWorkerLifecycle.BeginRecovery();
        }

        static void BeginRecovery(bool waitForGpuProof)
        {
            if (shutdownStarted)
            {
                if (BridgeWorkerShutdownPolicy.ShouldUpgradeToSynchronousReloadProof(true, waitForGpuProof))
                    BridgeWorkerReaper.PrepareForDomainReload(true);
                return;
            }
            shutdownStarted = true;
            if (!recovering)
            {
                recovering = true;
                recoveryAbiUnavailable = false;
                deadline = EditorApplication.timeSinceStartup + 1.0;
            }
            ViewWeave.BridgeWorkerLifecycle.BeginRecovery();
            BridgeQuiltPlaybackLease.PrepareForEditorTransitionAll("Editor Play exit, shutdown, or domain reload requested.");
            foreach (ViewWeaveBridgeSession session in Resources.FindObjectsOfTypeAll<ViewWeaveBridgeSession>())
            {
                if (session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle) session.StopSession();
            }
            foreach (BridgeWorkerQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeWorkerQuiltOutput>())
            {
                if (output != null) output.Dispose();
            }
            foreach (BridgeQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeQuiltOutput>())
            {
                if (output == null) continue;
                try { output.Dispose(); }
                catch (System.DllNotFoundException) { Debug.LogWarning("[ViewWeave] Legacy Bridge runtime library is unavailable during Editor recovery"); }
                catch (System.EntryPointNotFoundException) { Debug.LogWarning("[ViewWeave] Legacy Bridge close ABI is unavailable during Editor recovery"); }
            }
            try
            {
                int closeAllResult = BridgeWorkerQuiltOutput.CloseAllNativeWorkers();
                if (closeAllResult != 0) Debug.LogWarning("[ViewWeave] Bridge worker close-all is still pending (code " + closeAllResult + ")");
                BridgeWorkerReaper.PrepareForDomainReload(waitForGpuProof);
                BridgeQuiltPlaybackLease.CompleteAfterBridgeShutdownAll();
            }
            catch (System.DllNotFoundException)
            {
                Debug.LogWarning("[ViewWeave] Bridge worker native library is unavailable during shutdown");
                recoveryAbiUnavailable = true;
                if (BridgeWorkerReaper.PendingCount == 0)
                {
                    recovering = false;
                    shutdownStarted = false;
                    ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                }
            }
            catch (System.EntryPointNotFoundException)
            {
                Debug.LogWarning("[ViewWeave] Bridge worker shutdown ABI is unavailable");
                recoveryAbiUnavailable = true;
                if (BridgeWorkerReaper.PendingCount == 0)
                {
                    recovering = false;
                    shutdownStarted = false;
                    ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                }
            }
        }

        static void Update()
        {
            BridgeQuiltPlaybackLease.PumpAll();
            BridgeWorkerShutdownCoordinator.PumpOnce();
            BridgeWorkerReaper.PumpNow();
            if (BridgeWorkerShutdownCoordinator.FinalizationReady && !finalQuitReissued)
            {
                finalQuitReissued = true;
                Debug.Log("[ViewWeave] Bridge shutdown is complete. Close the Unity Editor again to finish exiting.");
                return;
            }
            if (!recovering) return;
            try
            {
                if (BridgeWorkerShutdownPolicy.CanCollectUnownedContexts(BridgeWorkerReaper.OwnedNativeHandleCount))
                    BridgeWorkerQuiltOutput.CollectClosedNativeWorkers();
                BridgeQuiltPlaybackLease.CompleteAfterBridgeShutdownAll();
                if (BridgeWorkerReaper.PendingCount == 0 && BridgeWorkerQuiltOutput.NativePendingCount == 0)
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
                if (BridgeWorkerReaper.PendingCount == 0)
                {
                    recovering = false;
                    shutdownStarted = false;
                    ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                }
                return;
            }
            catch (System.EntryPointNotFoundException)
            {
                if (!recoveryAbiUnavailable) Debug.LogWarning("[ViewWeave] Bridge worker recovery ABI is unavailable");
                recoveryAbiUnavailable = true;
                if (BridgeWorkerReaper.PendingCount == 0)
                {
                    recovering = false;
                    shutdownStarted = false;
                    ViewWeave.BridgeWorkerLifecycle.EndRecovery();
                }
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
