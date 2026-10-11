using System;
using UnityEngine;

namespace ViewWeave
{
    /// <summary>Publishes successfully acquired Bridge metadata independently of the output platform.</summary>
    internal static class BridgeDisplayMetadata
    {
        internal static event Action<ViewWeaveRig, MonoBehaviour, string, string, uint, BridgeDisplayProfile> Recognized;

        internal static void Publish(ViewWeaveRig rig, MonoBehaviour output, string runtimePath,
            string applicationName, uint displayOrdinal, BridgeDisplayProfile profile)
        {
            if (rig == null || output == null || !profile.IsValid) return;
            try { Recognized?.Invoke(rig, output, runtimePath, applicationName, displayOrdinal, profile); }
            catch (Exception exception)
            {
                Debug.LogWarning("Bridge display geometry could not be retained: " + exception.GetType().Name);
            }
        }
    }
}
