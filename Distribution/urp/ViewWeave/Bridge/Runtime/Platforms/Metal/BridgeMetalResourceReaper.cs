using UnityEngine;

namespace ViewWeave
{
    internal sealed class BridgeMetalResourceReaper : MonoBehaviour
    {
        void Update() { BridgeMetalRetirementRecord.PollAll(); }
    }
}
