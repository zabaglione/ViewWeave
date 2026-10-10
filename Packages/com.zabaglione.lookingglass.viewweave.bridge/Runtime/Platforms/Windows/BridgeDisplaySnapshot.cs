using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ViewWeave
{
    /// <summary>A complete immutable snapshot of the displays reported by Looking Glass Bridge.</summary>
    public sealed class BridgeDisplaySnapshot
    {
        readonly ReadOnlyCollection<BridgeDisplayProfile> displays;
        readonly uint[] displayOrdinals;

        public ulong Generation { get; }
        public IReadOnlyList<BridgeDisplayProfile> Displays => displays;

        internal BridgeDisplaySnapshot(ulong generation, uint[] ordinals, BridgeDisplayProfile[] values)
        {
            Generation = generation;
            displayOrdinals = ordinals == null ? new uint[0] : (uint[])ordinals.Clone();
            displays = new ReadOnlyCollection<BridgeDisplayProfile>(values == null
                ? new BridgeDisplayProfile[0]
                : (BridgeDisplayProfile[])values.Clone());
            if (displayOrdinals.Length != displays.Count)
                throw new ArgumentException("Display ordinals and profiles must have the same length.");
        }

        public bool TryGetDisplay(uint displayOrdinal, out BridgeDisplayProfile profile)
        {
            for (int i = 0; i < displays.Count; ++i)
            {
                if (displayOrdinals[i] == displayOrdinal)
                {
                    profile = displays[i];
                    return true;
                }
            }
            profile = default;
            return false;
        }

        internal void CopyEntries(out uint[] ordinals, out BridgeDisplayProfile[] profiles)
        {
            ordinals = (uint[])displayOrdinals.Clone();
            profiles = new BridgeDisplayProfile[displays.Count];
            for (int i = 0; i < profiles.Length; ++i) profiles[i] = displays[i];
        }
    }
}
