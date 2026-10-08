using System;
using UnityEngine;

namespace ViewWeave
{
    [Serializable]
    public readonly struct BridgeDisplayProfile
    {
        public readonly uint DisplayIndex, Width, Height;
        public readonly float Aspect, Viewcone;
        public readonly int QuiltWidth, QuiltHeight, Columns, Rows;
        public readonly bool HasPhysicalCalibration;
        public readonly float PhysicalWidthMillimeters, PhysicalHeightMillimeters, CalibrationDpi;
        public bool IsValid => Width > 0 && Height > 0 && IsFinite(Aspect) && Aspect > 0f && IsFinite(Viewcone) &&
                               Viewcone >= 0f && Viewcone < 179f && QuiltWidth > 0 && QuiltHeight > 0 && Columns > 0 && Rows > 0;
        internal BridgeDisplayProfile(BridgeNative.DisplayInfo info, BridgeWorkerNative.PhysicalDisplayInfoV1 physical = default)
        {
            DisplayIndex=info.index; Width=info.width; Height=info.height; Aspect=info.aspect; Viewcone=info.viewcone;
            QuiltWidth=info.quiltWidth; QuiltHeight=info.quiltHeight; Columns=info.quiltColumns; Rows=info.quiltRows;
            HasPhysicalCalibration = TryConvertCalibration(info, physical, out float widthMm, out float heightMm);
            PhysicalWidthMillimeters = HasPhysicalCalibration ? widthMm : 0f;
            PhysicalHeightMillimeters = HasPhysicalCalibration ? heightMm : 0f;
            CalibrationDpi = HasPhysicalCalibration ? physical.dpi : 0f;
        }

        static bool TryConvertCalibration(BridgeNative.DisplayInfo display, BridgeWorkerNative.PhysicalDisplayInfoV1 physical, out float widthMm, out float heightMm)
        {
            widthMm = 0f;
            heightMm = 0f;
            if (physical.valid == 0 || physical.pixelWidth == 0 || physical.pixelHeight == 0 ||
                float.IsNaN(physical.dpi) || float.IsInfinity(physical.dpi) || physical.dpi <= 0f || display.width == 0 || display.height == 0 || display.aspect <= 0f)
                return false;
            float pixelAspect = physical.pixelWidth / (float)physical.pixelHeight;
            float displayAspect = display.width / (float)display.height;
            bool direct = Mathf.Abs(pixelAspect - displayAspect) / displayAspect <= 0.05f;
            bool rotated = Mathf.Abs((1f / pixelAspect) - displayAspect) / displayAspect <= 0.05f;
            if (!direct && !rotated) return false;
            widthMm = physical.pixelWidth * 25.4f / physical.dpi;
            heightMm = physical.pixelHeight * 25.4f / physical.dpi;
            if (rotated && !direct) { float swap = widthMm; widthMm = heightMm; heightMm = swap; }
            float physicalAspect = widthMm / heightMm;
            return widthMm > 0f && heightMm > 0f && !float.IsNaN(physicalAspect) && !float.IsInfinity(physicalAspect) &&
                   Mathf.Abs(physicalAspect - display.aspect) / display.aspect <= 0.05f;
        }

        public bool MatchesPhysicalAspect(float widthMillimeters, float heightMillimeters)
        {
            if (!IsValid || !IsFinite(widthMillimeters) || !IsFinite(heightMillimeters) || widthMillimeters <= 0f || heightMillimeters <= 0f) return false;
            float physicalAspect = widthMillimeters / heightMillimeters;
            return IsFinite(physicalAspect) && Math.Abs(physicalAspect - Aspect) / Aspect <= 0.01f;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public override string ToString() => $"display={DisplayIndex} size={Width}x{Height} quilt={QuiltWidth}x{QuiltHeight} grid={Columns}x{Rows} aspect={Aspect} viewcone={Viewcone}";
    }
}
