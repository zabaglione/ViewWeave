namespace ViewWeave
{
    internal static class BridgeWorkerLifecycle
    {
        internal static bool RecoveryPending { get; private set; }
        internal static void BeginRecovery() { RecoveryPending = true; }
        internal static void EndRecovery() { RecoveryPending = false; }
    }
}
