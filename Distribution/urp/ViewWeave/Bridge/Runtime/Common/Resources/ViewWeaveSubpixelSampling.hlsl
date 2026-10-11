#ifndef VIEWWEAVE_SUBPIXEL_SAMPLING_INCLUDED
#define VIEWWEAVE_SUBPIXEL_SAMPLING_INCLUDED

// Shared subpixel coordinate and transfer math for Unity Metal and native D3D11.
// Callers pass logical bottom-left optical UVs and logical bottom-origin Quilt
// texel coordinates; backend wrappers map those coordinates to storage rows.
// Texture access stays in each backend wrapper so it can declare its source
// encoding and whether the API has already performed sRGB decoding.

int VWSubpixelCellIndex(int2 pixel, int patternMode)
{
    if (patternMode == 1) return (pixel.x + pixel.y) & 1;
    if (patternMode == 2) return pixel.x & 1;
    if (patternMode == 3) return (pixel.y + (pixel.x & 1) * 2) & 3;
    if (patternMode == 4) return pixel.y & 1;
    return 0;
}

float VWSubpixelPhase(float2 subpixelUv, float pitch, float verticalTilt, float center)
{
    return frac((subpixelUv.x + subpixelUv.y * verticalTilt) * pitch - center);
}

uint VWSubpixelViewIndex(float phase, uint viewCount, bool invertedViews)
{
    float fraction = invertedViews ? 1.0 - phase : phase;
    return min((uint)floor(fraction * viewCount), viewCount - 1);
}

uint2 VWSubpixelTile(uint viewIndex, uint columns)
{
    return uint2(viewIndex % columns, viewIndex / columns);
}

float2 VWSubpixelWithinView(float2 pixelUv, float aspectCorrection, float zoom)
{
    float2 withinView = (pixelUv - 0.5) * float2(aspectCorrection, 1.0) + 0.5;
    return (withinView - 0.5) / zoom + 0.5;
}

float2 VWSubpixelTileSamplePosition(uint2 tile, float2 withinView, float2 tilePixels)
{
    float2 position = (float2(tile) + withinView) * tilePixels;
    return clamp(position, float2(tile) * tilePixels + 0.5,
        (float2(tile) + 1.0) * tilePixels - 0.5);
}

int2 VWSubpixelClampTileTexel(int2 pixel, int2 tileOrigin, int2 tileSize)
{
    return clamp(pixel, tileOrigin, tileOrigin + tileSize - 1);
}

float VWSubpixelSrgbDecode(float value)
{
    value = saturate(value);
    return value <= 0.04045
        ? value / 12.92
        : pow(max((value + 0.055) / 1.055, 0.0), 2.4);
}

float VWSubpixelSrgbEncode(float value)
{
    value = saturate(value);
    return value <= 0.0031308
        ? value * 12.92
        : 1.055 * pow(value, 1.0 / 2.4) - 0.055;
}

float VWSubpixelPrepareTexel(float value, bool linearOutput, bool sourceIsSrgb,
    bool hardwareDecodedSrgb)
{
    if (!sourceIsSrgb) return value;
    if (hardwareDecodedSrgb)
        return linearOutput ? value : VWSubpixelSrgbEncode(value);
    return linearOutput ? VWSubpixelSrgbDecode(value) : value;
}

float VWSubpixelBilinear(float topLeft, float topRight, float bottomLeft,
    float bottomRight, float2 fraction, bool linearOutput, bool sourceIsSrgb,
    bool hardwareDecodedSrgb)
{
    topLeft = VWSubpixelPrepareTexel(topLeft, linearOutput, sourceIsSrgb, hardwareDecodedSrgb);
    topRight = VWSubpixelPrepareTexel(topRight, linearOutput, sourceIsSrgb, hardwareDecodedSrgb);
    bottomLeft = VWSubpixelPrepareTexel(bottomLeft, linearOutput, sourceIsSrgb, hardwareDecodedSrgb);
    bottomRight = VWSubpixelPrepareTexel(bottomRight, linearOutput, sourceIsSrgb, hardwareDecodedSrgb);
    float top = lerp(topLeft, topRight, fraction.x);
    float bottom = lerp(bottomLeft, bottomRight, fraction.x);
    float value = lerp(top, bottom, fraction.y);
    return linearOutput ? VWSubpixelSrgbEncode(value) : value;
}

#endif
