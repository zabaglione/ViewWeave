using System;
using UnityEngine;

namespace ViewWeave
{
    internal sealed class BridgeWorkerShutdownCoordinator : MonoBehaviour
    {
        const double ShutdownTimeoutSeconds = 10.0;
        static BridgeWorkerShutdownCoordinator instance;
        static bool requestAttempted;
        static bool finalStatusComplete;
        static bool finalizationReady;
        static bool timedOut;
        static bool warningWritten;
        static bool playerQuitReissued;
        static double deadline;

        internal static bool FinalizationReady => finalizationReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void RegisterPlayerQuitHook()
        {
            Application.wantsToQuit -= OnApplicationWantsToQuit;
            Application.wantsToQuit += OnApplicationWantsToQuit;
        }

        static bool OnApplicationWantsToQuit()
        {
            if (Application.isEditor || Application.platform != RuntimePlatform.WindowsPlayer) return true;
            return RequestQuit();
        }

        internal static bool RequestQuit()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer) return true;
            if (BridgeWorkerShutdownPolicy.CanQuitImmediately(finalizationReady, requestAttempted || HasPotentialWorkerActivity())) return true;
            EnsureHost();
            if (!requestAttempted)
            {
                requestAttempted = true;
                timedOut = false;
                deadline = Time.realtimeSinceStartupAsDouble + ShutdownTimeoutSeconds;
                BridgeWorkerLifecycle.BeginFinalShutdown();
                try { StopManagedWorkers(); }
                catch (Exception error) { WarnOnce("Stopping Bridge worker outputs failed: " + Sanitize(error.Message)); }
                try
                {
                    int closeAllResult = BridgeWorkerQuiltOutput.CloseAllNativeWorkers();
                    if (closeAllResult != 0) WarnOnce("Bridge worker close-all returned code " + closeAllResult + ".");
                }
                catch (DllNotFoundException) { WarnOnce("Bridge worker native library is unavailable during final shutdown."); }
                catch (EntryPointNotFoundException) { WarnOnce("Bridge worker close-all ABI is unavailable during final shutdown."); }
                catch (Exception error) { WarnOnce("Bridge worker close-all failed: " + Sanitize(error.Message)); }
                BridgeWorkerReaper.PrepareForFinalShutdown();
                try
                {
                    if (TryReadNativeStatus(out BridgeWorkerNative.FinalShutdownStatusV1 existing) && existing.state != 0)
                    {
                        finalStatusComplete = BridgeWorkerShutdownPolicy.IsNativeComplete(existing);
                    }
                    else
                    {
                        int result = BridgeWorkerNative.lkg_worker_request_final_shutdown();
                        if (result != 0) WarnOnce("Final worker shutdown request failed with code " + result + ".");
                    }
                }
                catch (DllNotFoundException) { WarnOnce("Bridge worker native library is unavailable during final shutdown."); }
                catch (EntryPointNotFoundException) { WarnOnce("Bridge worker final shutdown ABI is unavailable."); }
                catch (Exception error) { WarnOnce("Final worker shutdown request failed: " + Sanitize(error.Message)); }
            }
            else
            {
                timedOut = false;
                deadline = Time.realtimeSinceStartupAsDouble + ShutdownTimeoutSeconds;
            }
            return false;
        }

        internal static void HandleEditorQuitBypass()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return;
            if (finalizationReady) return;
            RequestQuit();
            if (!requestAttempted) return;
            var status = new BridgeWorkerNative.FinalShutdownStatusV1
            {
                structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.FinalShutdownStatusV1)),
                version = 1
            };
            try
            {
                BridgeWorkerReaper.PrepareForDomainReload(true);
                int result = BridgeWorkerNative.lkg_worker_wait_final_shutdown_v1(5000, ref status);
                if (result == 0 && BridgeWorkerShutdownPolicy.IsNativeComplete(status))
                {
                    finalStatusComplete = true;
                    BridgeWorkerReaper.MarkFinalShutdownCollected();
                    BridgeWorkerReaper.PrepareForDomainReload(true);
                    BridgeWorkerReaper.PumpNow();
                    if (BridgeWorkerReaper.PendingCount == 0 && !BridgeWorkerReaper.HasUnprovenResources)
                    {
                        finalizationReady = true;
                        BridgeWorkerLifecycle.EndFinalShutdown();
                        return;
                    }
                    Debug.LogWarning("[ViewWeave] Native shutdown completed, but managed GPU resource release remains unproven.");
                }
                else
                {
                    WarnOnce("Editor quit bypassed the quit gate; bounded final shutdown did not prove completion.");
                }
            }
            catch (DllNotFoundException) { WarnOnce("Editor quit bypassed the quit gate and the Bridge worker library is unavailable."); }
            catch (EntryPointNotFoundException) { WarnOnce("Editor quit bypassed the quit gate and the bounded final shutdown ABI is unavailable."); }
            catch (Exception error) { WarnOnce("Editor quit bypass fallback failed: " + Sanitize(error.Message)); }
        }

        static bool HasPotentialWorkerActivity()
        {
            if (BridgeWorkerLifecycle.FinalShutdownPending || BridgeWorkerReaper.PendingCount != 0 || BridgeWorkerQuiltOutput.HasNativeHandle) return true;
            foreach (BridgeQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeQuiltOutput>())
                if (output != null && output.Session != 0) return true;
            try
            {
                if (!TryReadNativeStatus(out BridgeWorkerNative.FinalShutdownStatusV1 status)) return true;
                return BridgeWorkerShutdownPolicy.HasNativeActivity(status);
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        static void StopManagedWorkers()
        {
            foreach (ViewWeaveBridgeSession session in Resources.FindObjectsOfTypeAll<ViewWeaveBridgeSession>())
                if (session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle) session.StopSession();
            foreach (BridgeWorkerQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeWorkerQuiltOutput>())
                if (output != null) output.Dispose();
            foreach (BridgeQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeQuiltOutput>())
            {
                if (output == null) continue;
                try { output.Dispose(); }
                catch (DllNotFoundException) { WarnOnce("Legacy Bridge runtime library is unavailable while stopping the active session."); }
                catch (EntryPointNotFoundException) { WarnOnce("Legacy Bridge close ABI is unavailable while stopping the active session."); }
            }
        }

        static void EnsureHost()
        {
            if (instance != null)
            {
                PreserveWhilePlaying(instance.gameObject);
                return;
            }
            BridgeWorkerShutdownCoordinator[] existing = Resources.FindObjectsOfTypeAll<BridgeWorkerShutdownCoordinator>();
            foreach (BridgeWorkerShutdownCoordinator coordinator in existing)
            {
                if (coordinator == null) continue;
                instance = coordinator;
                PreserveWhilePlaying(instance.gameObject);
                return;
            }
            var host = new GameObject("ViewWeave Bridge Shutdown Coordinator");
            host.hideFlags = HideFlags.HideAndDontSave;
            PreserveWhilePlaying(host);
            instance = host.AddComponent<BridgeWorkerShutdownCoordinator>();
        }

        static void PreserveWhilePlaying(GameObject host)
        {
            if (Application.isPlaying) DontDestroyOnLoad(host);
        }

        void Awake()
        {
            PreserveWhilePlaying(gameObject);
        }

        void Update()
        {
            PumpOnce();
        }

        internal static void PumpOnce()
        {
            if (!requestAttempted || finalizationReady) return;
            BridgeWorkerReaper.PumpNow();
            if (timedOut) return;
            if (!finalStatusComplete) PumpNativeStatus();
            if (finalStatusComplete)
            {
                BridgeWorkerReaper.MarkFinalShutdownCollected();
                BridgeWorkerReaper.PumpNow();
                if (BridgeWorkerReaper.PendingCount == 0 && !BridgeWorkerReaper.HasUnprovenResources)
                {
                    finalizationReady = true;
                    BridgeWorkerLifecycle.EndFinalShutdown();
                    if (!Application.isEditor && !playerQuitReissued)
                    {
                        playerQuitReissued = true;
                        Application.Quit();
                    }
                    return;
                }
            }
            if (!timedOut && Time.realtimeSinceStartupAsDouble >= deadline)
            {
                timedOut = true;
                WarnOnce("Final worker shutdown exceeded 10 seconds; quit was canceled and resources remain retained.");
            }
        }

        static void PumpNativeStatus()
        {
            try
            {
                if (!TryReadNativeStatus(out BridgeWorkerNative.FinalShutdownStatusV1 status)) return;
                if (BridgeWorkerShutdownPolicy.IsNativeComplete(status))
                {
                    finalStatusComplete = true;
                    warningWritten = false;
                }
                else if (status.state == 4 || status.state == 5)
                {
                    WarnOnce("Final worker shutdown reported a timeout or fault; resources remain retained.");
                }
            }
            catch (DllNotFoundException) { WarnOnce("Bridge worker native library is unavailable during final shutdown status polling."); }
            catch (EntryPointNotFoundException) { WarnOnce("Bridge worker final shutdown status ABI is unavailable."); }
            catch (Exception error) { WarnOnce("Final worker shutdown status polling failed: " + Sanitize(error.Message)); }
        }

        static bool TryReadNativeStatus(out BridgeWorkerNative.FinalShutdownStatusV1 status)
        {
            status = new BridgeWorkerNative.FinalShutdownStatusV1
            {
                structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.FinalShutdownStatusV1)),
                version = 1
            };
            int result = BridgeWorkerNative.lkg_worker_get_final_shutdown_status_v1(ref status);
            if (result == 0) return true;
            WarnOnce("Final worker shutdown status query failed with code " + result + ".");
            return false;
        }

        static void WarnOnce(string message)
        {
            if (warningWritten) return;
            warningWritten = true;
            Debug.LogWarning("[ViewWeave] " + Sanitize(message));
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }
    }
}
