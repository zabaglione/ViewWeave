namespace ViewWeave
{
    public readonly partial struct BridgeDisplayProfile
    {
        internal BridgeDisplayProfile(BridgeNative.DisplayInfo info, BridgeWorkerNative.PhysicalDisplayInfoV1 physical)
            : this(info, new BridgeNative.PhysicalDisplayInfoV1
            {
                structSize = physical.structSize, version = physical.version,
                pixelWidth = physical.pixelWidth, pixelHeight = physical.pixelHeight,
                dpi = physical.dpi, valid = physical.valid
            }) { }
    }
}
