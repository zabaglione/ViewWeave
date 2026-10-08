namespace ViewWeave
{
    internal static class BridgeWorkerLifecycle
    {
        static bool recoveryPending;
        static bool finalShutdownPending;
        internal static bool RecoveryPending => BridgeWorkerShutdownPolicy.ShouldBlockAdmission(recoveryPending, FinalShutdownPending);
        internal static bool FinalShutdownPending => finalShutdownPending || BridgeWorkerFinalShutdownRecord.IsPending;
        internal static void BeginRecovery() { recoveryPending = true; }
        internal static void BeginFinalShutdown()
        {
            finalShutdownPending = true;
            recoveryPending = true;
            BridgeWorkerFinalShutdownRecord.MarkPending();
        }
        internal static void EndFinalShutdown()
        {
            finalShutdownPending = true;
            recoveryPending = true;
        }
        internal static void EndRecovery() { if (!FinalShutdownPending) recoveryPending = false; }
    }
}
