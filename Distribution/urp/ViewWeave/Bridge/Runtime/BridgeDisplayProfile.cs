using System;

namespace ViewWeave
{
    [Serializable]
    public readonly struct BridgeDisplayProfile
    {
        public readonly uint DisplayIndex, Width, Height;
        public readonly float Aspect, Viewcone;
        public readonly int QuiltWidth, QuiltHeight, Columns, Rows;
        public bool IsValid => Width > 0 && Height > 0 && Aspect > 0 && QuiltWidth > 0 && QuiltHeight > 0 && Columns > 0 && Rows > 0;
        internal BridgeDisplayProfile(BridgeNative.DisplayInfo info) { DisplayIndex=info.index; Width=info.width; Height=info.height; Aspect=info.aspect; Viewcone=info.viewcone; QuiltWidth=info.quiltWidth; QuiltHeight=info.quiltHeight; Columns=info.quiltColumns; Rows=info.quiltRows; }
        public override string ToString() => $"display={DisplayIndex} size={Width}x{Height} quilt={QuiltWidth}x{QuiltHeight} grid={Columns}x{Rows} aspect={Aspect} viewcone={Viewcone}";
    }
}
