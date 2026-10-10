using System;
using UnityEngine;

namespace ViewWeave
{
    internal sealed class BridgeWorkerShutdownCoordinator : MonoBehaviour
    {
        const double ShutdownTimeoutSeconds = 10.0;
        static BridgeWorkerShutdownCoordinator instance;
        static bool requestAttempted;
        static bool nativeShutdownRequested;
        static bool nativeCloseAllAttempted;
        static bool finalStatusComplete;
        static bool finalizationReady;
        static bool timedOut;
        static bool warningWritten;
        static bool playerQuitReissued;
        static double deadline;

#if UNITY_EDITOR
        internal static Func<bool> nativeStatusCompleteOverrideForTests;
        internal static Func<bool> nativeValidationPendingOverrideForTests;
        internal static Func<bool> inspectEditorProcessOverrideForTests;
        internal static Func<NativeValidationResultForTests> nativeValidationOverrideForTests;
        internal static Func<int> closeAllNativeWorkersOverrideForTests;
        internal static Func<int> requestFinalShutdownOverrideForTests;
        internal static Func<NativeStatusReadResultForTests> nativeStatusReadOverrideForTests;

        internal struct NativeValidationResultForTests
        {
            internal bool CallsAllowed;
            internal bool ModuleLoaded;
            internal string Error;
        }

        internal struct NativeStatusReadResultForTests
        {
            internal bool Succeeded;
            internal BridgeWorkerNative.FinalShutdownStatusV1 Status;
        }
#endif

        internal static bool FinalizationReady => finalizationReady;

        internal static bool WaitForFinalization()
        {
            bool enableEditorSubmission = Application.platform == RuntimePlatform.WindowsEditor;
            if (enableEditorSubmission)
            {
                BridgeEditorPreviewRetention.RequestStop(false);
                BridgeWorkerEditorGpuFlush.BeginSynchronousWait();
            }
            try
            {
                return BridgeWorkerQuitWait.WaitForFinalization(
                    RequestQuit,
                    PumpOnce,
                    () => finalizationReady && BridgeWorkerReaper.PendingCount == 0 &&
                        !BridgeWorkerReaper.HasUnprovenResources && PlaybackQuitCleanupComplete() &&
                        !BridgeEditorPreviewRetention.HasPending && !BridgeDisplayDiscovery.IsBusy,
                    WarnOnce,
                    ShutdownTimeoutSeconds);
            }
            finally
            {
                if (enableEditorSubmission) BridgeWorkerEditorGpuFlush.EndSynchronousWait();
            }
        }

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
            if (Application.platform == RuntimePlatform.WindowsEditor) BridgeEditorPreviewRetention.RequestStop(false);
            if (Application.platform == RuntimePlatform.WindowsEditor) BridgeDisplayDiscovery.CancelForReload();
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor) RefreshPendingNativeValidation();
#endif
            if (BridgeWorkerShutdownPolicy.CanQuitImmediately(finalizationReady, requestAttempted || HasPotentialWorkerActivity())) return true;
            EnsureHost();
            if (!requestAttempted)
            {
                requestAttempted = true;
                timedOut = false;
                deadline = Time.realtimeSinceStartupAsDouble + ShutdownTimeoutSeconds;
                BridgeWorkerLifecycle.BeginFinalShutdown();
#if UNITY_EDITOR
                if (Application.platform == RuntimePlatform.WindowsEditor)
                    BridgeQuiltPlaybackLease.PrepareForFinalQuitAll("Windows Editor quit requested; proving playback GPU completion.");
#endif
                try { StopManagedWorkers(); }
                catch (Exception error) { WarnOnce("Stopping Bridge worker outputs failed: " + Sanitize(error.Message)); }
                BridgeWorkerReaper.PrepareForFinalShutdown();
            }
            else
            {
                timedOut = false;
                deadline = Time.realtimeSinceStartupAsDouble + ShutdownTimeoutSeconds;
            }
            PumpNativeShutdownRequest();
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
#if UNITY_EDITOR
                if (Application.platform == RuntimePlatform.WindowsEditor)
                {
                    if (!TryValidateNativeCalls(out bool moduleLoaded, out string imageError) || !moduleLoaded)
                    {
                        WarnOnce(string.IsNullOrEmpty(imageError) ? "The expected Bridge shim is not loaded; final shutdown remains unproven." : imageError);
                        return;
                    }
                }
#endif
                if (!nativeShutdownRequested && !finalStatusComplete)
                {
                    WarnOnce("Editor quit bypassed the quit gate before a native final shutdown request was accepted.");
                    return;
                }
                int result = BridgeWorkerNative.lkg_worker_wait_final_shutdown_v1(5000, ref status);
                if (result == 0 && BridgeWorkerShutdownPolicy.IsNativeComplete(status))
                {
                    finalStatusComplete = true;
                    BridgeWorkerReaper.MarkFinalShutdownCollected();
                    BridgeWorkerReaper.PrepareForDomainReload(true);
                    BridgeWorkerReaper.PumpNow();
                    BridgeEditorPreviewRetention.Pump(true);
                    BridgeDisplayDiscovery.Tick();
                    if (BridgeWorkerReaper.PendingCount == 0 && !BridgeWorkerReaper.HasUnprovenResources &&
                        !BridgeEditorPreviewRetention.HasPending && !BridgeDisplayDiscovery.IsBusy)
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
            if (Application.platform == RuntimePlatform.WindowsEditor && BridgeDisplayDiscovery.IsBusy) return true;
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor && !BridgeQuiltPlaybackLease.FinalQuitCleanupComplete) return true;
#endif
            if (BridgeWorkerLifecycle.FinalShutdownPending || BridgeWorkerReaper.PendingCount != 0 || BridgeWorkerQuiltOutput.HasNativeHandle || BridgeEditorPreviewRetention.HasPending) return true;
            foreach (BridgeQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeQuiltOutput>())
                if (output != null && output.Session != 0) return true;
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                RefreshPendingNativeValidation();
                if (!TryValidateNativeCalls(out bool moduleLoaded, out _)) return true;
                if (!moduleLoaded) return false;
            }
#endif
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
            if (Application.platform == RuntimePlatform.WindowsEditor) BridgeDisplayDiscovery.Tick();
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor)
                BridgeQuiltPlaybackLease.PumpAll();
#endif
            BridgeWorkerReaper.PumpNow();
            if (Application.platform == RuntimePlatform.WindowsEditor) BridgeEditorPreviewRetention.Pump(false);
            PumpNativeShutdownRequest();
            if (!finalStatusComplete) PumpNativeStatus();
            if (finalStatusComplete)
            {
                BridgeWorkerReaper.MarkFinalShutdownCollected();
                BridgeWorkerReaper.PumpNow();
#if UNITY_EDITOR
                if (Application.platform == RuntimePlatform.WindowsEditor)
                    BridgeQuiltPlaybackLease.CompleteAfterBridgeShutdownAll();
#endif
                if (BridgeWorkerReaper.PendingCount == 0 && !BridgeWorkerReaper.HasUnprovenResources &&
                    PlaybackQuitCleanupComplete() && !BridgeEditorPreviewRetention.HasPending &&
                    (Application.platform != RuntimePlatform.WindowsEditor || !BridgeDisplayDiscovery.IsBusy))
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

        static bool PlaybackQuitCleanupComplete()
        {
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor)
                return BridgeQuiltPlaybackLease.FinalQuitCleanupComplete;
#endif
            return true;
        }

        static void PumpNativeStatus()
        {
            try
            {
#if UNITY_EDITOR
                if (nativeStatusCompleteOverrideForTests != null)
                {
                    if (nativeStatusCompleteOverrideForTests())
                    {
                        finalStatusComplete = true;
                        warningWritten = false;
                    }
                    return;
                }
#endif
                if (!TryReadNativeStatus(out BridgeWorkerNative.FinalShutdownStatusV1 status)) return;
                if (BridgeWorkerShutdownPolicy.IsValidNativeStatus(status) && status.state != 0)
                    nativeShutdownRequested = true;
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
#if UNITY_EDITOR
            if (nativeStatusReadOverrideForTests != null)
            {
                NativeStatusReadResultForTests testResult = nativeStatusReadOverrideForTests();
                status = testResult.Status;
                return testResult.Succeeded;
            }
#endif
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                if (!TryValidateNativeCalls(out bool moduleLoaded, out string imageError))
                {
                    WarnOnce(imageError);
                    return false;
                }
                if (!moduleLoaded) return true;
            }
#endif
            int result = BridgeWorkerNative.lkg_worker_get_final_shutdown_status_v1(ref status);
            if (result == 0) return true;
            WarnOnce("Final worker shutdown status query failed with code " + result + ".");
            return false;
        }

        static void PumpNativeShutdownRequest()
        {
            if (!requestAttempted || nativeShutdownRequested || finalStatusComplete) return;
            if (!TryValidateNativeCalls(out bool moduleLoaded, out string imageError))
            {
                if (!string.IsNullOrEmpty(imageError)) WarnOnce(imageError);
                WarnOnce("Native shutdown calls are blocked until the expected Bridge shim image is available; resources remain retained.");
                return;
            }
            if (!moduleLoaded)
            {
                if (!HasManagedShutdownActivity())
                {
                    finalStatusComplete = true;
                    warningWritten = false;
                }
                return;
            }

            if (!nativeCloseAllAttempted)
            {
                try
                {
                    if (!TryInvokeCloseAll(out bool invoked, out bool closeAllModuleLoaded, out int closeAllResult, out string closeAllError))
                    {
                        if (!string.IsNullOrEmpty(closeAllError)) WarnOnce(closeAllError);
                        return;
                    }
                    if (!closeAllModuleLoaded)
                    {
                        if (!HasManagedShutdownActivity())
                        {
                            finalStatusComplete = true;
                            warningWritten = false;
                        }
                        return;
                    }
                    if (invoked) nativeCloseAllAttempted = true;
                    if (invoked && closeAllResult != 0) WarnOnce("Bridge worker close-all returned code " + closeAllResult + ".");
                }
                catch (DllNotFoundException) { WarnOnce("Bridge worker native library is unavailable during final shutdown."); }
                catch (EntryPointNotFoundException) { WarnOnce("Bridge worker close-all ABI is unavailable during final shutdown."); }
                catch (Exception error) { WarnOnce("Bridge worker close-all failed: " + Sanitize(error.Message)); }
            }

            try
            {
                if (TryReadNativeStatus(out BridgeWorkerNative.FinalShutdownStatusV1 existing) &&
                    BridgeWorkerShutdownPolicy.IsValidNativeStatus(existing) && existing.state != 0)
                {
                    nativeShutdownRequested = true;
                    finalStatusComplete = BridgeWorkerShutdownPolicy.IsNativeComplete(existing);
                    if (finalStatusComplete) warningWritten = false;
                    return;
                }

                if (!TryInvokeFinalShutdownRequest(out bool invoked, out bool requestModuleLoaded, out int result, out string requestError))
                {
                    if (!string.IsNullOrEmpty(requestError)) WarnOnce(requestError);
                    return;
                }
                if (!requestModuleLoaded)
                {
                    if (!HasManagedShutdownActivity())
                    {
                        finalStatusComplete = true;
                        warningWritten = false;
                    }
                    return;
                }
                if (invoked && result == 0) nativeShutdownRequested = true;
                else if (invoked) WarnOnce("Final worker shutdown request failed with code " + result + ".");
            }
            catch (DllNotFoundException) { WarnOnce("Bridge worker native library is unavailable during final shutdown."); }
            catch (EntryPointNotFoundException) { WarnOnce("Bridge worker final shutdown ABI is unavailable."); }
            catch (Exception error) { WarnOnce("Final worker shutdown request failed: " + Sanitize(error.Message)); }
        }

        static bool HasManagedShutdownActivity()
        {
            if (BridgeWorkerReaper.PendingCount != 0 || BridgeWorkerReaper.HasUnprovenResources ||
                BridgeWorkerQuiltOutput.HasNativeHandle || BridgeEditorPreviewRetention.HasPending ||
                BridgeDisplayDiscovery.IsBusy || !PlaybackQuitCleanupComplete()) return true;
            foreach (BridgeQuiltOutput output in Resources.FindObjectsOfTypeAll<BridgeQuiltOutput>())
                if (output != null && output.Session != 0) return true;
            return false;
        }

        static bool TryInvokeCloseAll(out bool invoked, out bool moduleLoaded, out int result, out string error)
        {
            invoked = false;
            moduleLoaded = true;
            result = -1;
            error = string.Empty;
#if UNITY_EDITOR
            if (closeAllNativeWorkersOverrideForTests != null)
            {
                result = closeAllNativeWorkersOverrideForTests();
                invoked = true;
                return true;
            }
            if (Application.platform == RuntimePlatform.WindowsEditor && !TryValidateNativeCalls(out moduleLoaded, out error)) return false;
#endif
            if (!moduleLoaded) return true;
            result = BridgeWorkerNative.lkg_worker_close_all();
            invoked = true;
            return true;
        }

        static bool TryInvokeFinalShutdownRequest(out bool invoked, out bool moduleLoaded, out int result, out string error)
        {
            invoked = false;
            moduleLoaded = true;
            result = -1;
            error = string.Empty;
#if UNITY_EDITOR
            if (requestFinalShutdownOverrideForTests != null)
            {
                result = requestFinalShutdownOverrideForTests();
                invoked = true;
                return true;
            }
            if (Application.platform == RuntimePlatform.WindowsEditor && !TryValidateNativeCalls(out moduleLoaded, out error)) return false;
#endif
            if (!moduleLoaded) return true;
            result = BridgeWorkerNative.lkg_worker_request_final_shutdown();
            invoked = true;
            return true;
        }

        static bool IsNativeValidationPending()
        {
#if UNITY_EDITOR
            if (nativeValidationPendingOverrideForTests != null) return nativeValidationPendingOverrideForTests();
            return BridgeNativeImageGuard.IsValidationPending;
#else
            return false;
#endif
        }

        static bool RefreshPendingNativeValidation()
        {
#if UNITY_EDITOR
            if (Application.platform != RuntimePlatform.WindowsEditor || !IsNativeValidationPending()) return true;
            bool valid = inspectEditorProcessOverrideForTests != null
                ? inspectEditorProcessOverrideForTests()
                : BridgeNativeImageGuard.TryInspectEditorProcess(out _);
            return valid;
#else
            return true;
#endif
        }

        static bool TryValidateNativeCalls(out bool moduleLoaded, out string error)
        {
            moduleLoaded = true;
            error = string.Empty;
#if UNITY_EDITOR
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                if (IsNativeValidationPending() && !RefreshPendingNativeValidation())
                {
                    error = "Native image validation did not approve the loaded Bridge shim.";
                    return false;
                }
                if (nativeValidationOverrideForTests != null)
                {
                    NativeValidationResultForTests result = nativeValidationOverrideForTests();
                    moduleLoaded = result.ModuleLoaded;
                    error = result.Error ?? string.Empty;
                    return result.CallsAllowed;
                }
                return BridgeNativeImageGuard.TryValidateGlobalCalls(out moduleLoaded, out _, out error);
            }
#endif
            return true;
        }

#if UNITY_EDITOR
        internal static void PumpNativeShutdownRequestForTests() => PumpNativeShutdownRequest();
#endif

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
