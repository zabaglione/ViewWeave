using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ViewWeave
{
    internal static class BridgeWorkerShutdownPolicy
    {
        internal static bool IsValidNativeStatus(BridgeWorkerNative.FinalShutdownStatusV1 status)
        {
            return status.structSize == 32 && status.version == 1 && status.state <= 5 &&
                status.hostRunning <= 1 && status.hostJoined <= 1;
        }

        internal static bool IsNativeComplete(BridgeWorkerNative.FinalShutdownStatusV1 status)
        {
            return IsValidNativeStatus(status) && status.state == 3 &&
                status.registeredContexts == 0 && status.closedUncollectedContexts == 0 &&
                status.hostRunning == 0 && status.hostJoined != 0 && status.lastResult == 0;
        }

        internal static bool IsValidLegacyCloseStatus(ulong expectedSession, BridgeNative.SessionCloseStatusV1 status)
        {
            return status.structSize == 32 && status.version == 1 && status.sessionId == expectedSession &&
                status.state <= 3 && status.reserved0 == 0 && status.reserved1 == 0;
        }

        internal static bool IsLegacyCloseSafe(ulong expectedSession, BridgeNative.SessionCloseStatusV1 status)
        {
            return IsValidLegacyCloseStatus(expectedSession, status) && status.state == 1 && status.lastResult == 0;
        }

        internal static bool CanQuitImmediately(bool finalizationReady, bool hasPotentialWorkerActivity)
        {
            return finalizationReady || !hasPotentialWorkerActivity;
        }

        internal static bool HasNativeActivity(BridgeWorkerNative.FinalShutdownStatusV1 status)
        {
            return !IsValidNativeStatus(status) || status.state != 0 || status.registeredContexts != 0 ||
                status.closedUncollectedContexts != 0 || status.hostRunning != 0 || status.hostJoined != 0 || status.lastResult != 0;
        }

        internal static bool ShouldBlockAdmission(bool recoveryPending, bool finalShutdownPending)
        {
            return recoveryPending || finalShutdownPending;
        }

        internal static bool ShouldUpgradeToSynchronousReloadProof(bool recoveryAlreadyStarted, bool beforeAssemblyReload)
        {
            return recoveryAlreadyStarted && beforeAssemblyReload;
        }

        internal static bool CanCollectUnownedContexts(int ownedNativeHandleCount)
        {
            return ownedNativeHandleCount == 0;
        }
    }

    internal static class BridgeWorkerQuitWait
    {
        internal static bool WaitForFinalization(
            Func<bool> requestQuit,
            Action pumpOnce,
            Func<bool> finalizationProven,
            Action<string> reportFailure,
            double timeoutSeconds)
        {
            return WaitForFinalization(requestQuit, pumpOnce, finalizationProven,
                Stopwatch.GetTimestamp, Stopwatch.Frequency, Thread.Sleep, reportFailure, timeoutSeconds);
        }

        internal static bool WaitForFinalization(
            Func<bool> requestQuit,
            Action pumpOnce,
            Func<bool> finalizationProven,
            Func<long> getTimestamp,
            long timestampFrequency,
            Action<int> sleep,
            Action<string> reportFailure,
            double timeoutSeconds)
        {
            long startedAt;
            try
            {
                startedAt = getTimestamp();
            }
            catch (Exception error)
            {
                Report(reportFailure, "Could not start the bounded Bridge quit wait: " + Sanitize(error.Message));
                return false;
            }

            if (requestQuit == null || pumpOnce == null || finalizationProven == null ||
                getTimestamp == null || sleep == null || timestampFrequency <= 0 || timeoutSeconds <= 0.0)
            {
                Report(reportFailure, "The bounded Bridge quit wait configuration is invalid; quit was canceled.");
                return false;
            }

            try
            {
                bool requestReady = requestQuit();
                if (Expired(startedAt, getTimestamp, timestampFrequency, timeoutSeconds))
                    return Timeout(reportFailure);
                if (requestReady || finalizationProven()) return true;

                while (true)
                {
                    if (Expired(startedAt, getTimestamp, timestampFrequency, timeoutSeconds))
                        return Timeout(reportFailure);

                    pumpOnce();

                    if (Expired(startedAt, getTimestamp, timestampFrequency, timeoutSeconds))
                        return Timeout(reportFailure);
                    if (finalizationProven()) return true;

                    sleep(1);
                }
            }
            catch (Exception error)
            {
                Report(reportFailure, "Bounded Bridge quit wait failed; quit was canceled and resources remain retained: " +
                    Sanitize(error.Message));
                return false;
            }
        }

        static bool Expired(long startedAt, Func<long> getTimestamp, long timestampFrequency, double timeoutSeconds)
        {
            long now = getTimestamp();
            if (now < startedAt) return true;
            return (now - startedAt) / (double)timestampFrequency >= timeoutSeconds;
        }

        static bool Timeout(Action<string> reportFailure)
        {
            Report(reportFailure, "Final worker shutdown exceeded 10 seconds; quit was canceled and resources remain retained.");
            return false;
        }

        static void Report(Action<string> reportFailure, string message)
        {
            if (reportFailure == null) return;
            try { reportFailure(Sanitize(message)); }
            catch { }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var output = new StringBuilder(value.Length);
            foreach (char character in value)
                if (character >= 0x20 && character <= 0x7e) output.Append(character);
                else if (character == '\r' || character == '\n' || character == '\t') output.Append(' ');
            return output.Length == 0 ? "unknown error" : output.ToString();
        }
    }
}
