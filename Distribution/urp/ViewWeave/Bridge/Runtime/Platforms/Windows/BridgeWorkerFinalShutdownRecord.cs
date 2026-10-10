using UnityEngine;

namespace ViewWeave
{
    internal sealed class BridgeWorkerFinalShutdownRecord : ScriptableObject
    {
        [SerializeField] bool finalShutdownRequested;

        internal static bool IsPending
        {
            get
            {
                foreach (BridgeWorkerFinalShutdownRecord record in Resources.FindObjectsOfTypeAll<BridgeWorkerFinalShutdownRecord>())
                    if (record != null && record.finalShutdownRequested) return true;
                return false;
            }
        }

        internal static void MarkPending()
        {
            foreach (BridgeWorkerFinalShutdownRecord record in Resources.FindObjectsOfTypeAll<BridgeWorkerFinalShutdownRecord>())
            {
                if (record == null) continue;
                record.finalShutdownRequested = true;
                return;
            }
            BridgeWorkerFinalShutdownRecord created = CreateInstance<BridgeWorkerFinalShutdownRecord>();
            created.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
            created.finalShutdownRequested = true;
        }
    }
}
