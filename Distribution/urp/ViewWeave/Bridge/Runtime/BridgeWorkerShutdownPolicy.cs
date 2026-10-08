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
}
