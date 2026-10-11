#include "../../../Packages/com.zabaglione.lookingglass.viewweave.bridge/Runtime/Common/Resources/ViewWeaveSubpixelSampling.hlsl"

cbuffer PresentConstants : register(b0)
{
    float4 calibration;       // calibrated pitch, vertical tilt, center, inverted views
    float4 screen;            // physical width, height, RGB subpixel step, classic flip
    float4 grid;              // columns, rows, tile width, tile height
    float4 quilt;             // width, height, aspect correction, zoom
    float4 outputOptions;     // pattern mode, use cells, linear output, source was sRGB
    float4 channelOrder;       // RGB channel indexes
    float4 cellOffsets[12];   // four cells, normalized RGB XY offsets
};

Texture2D<float4> QuiltTexture : register(t0);

struct VertexOutput
{
    float4 position : SV_Position;
};

VertexOutput ManualSubpixelVS(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    if (vertexId == 0)
    {
        output.position = float4(-1.0, 1.0, 0.0, 1.0);
    }
    else if (vertexId == 1)
    {
        output.position = float4(3.0, 1.0, 0.0, 1.0);
    }
    else
    {
        output.position = float4(-1.0, -3.0, 0.0, 1.0);
    }
    return output;
}

float VWLoadChannel(int2 pixel, int channel, int2 tileOrigin, int2 tileSize)
{
    int2 bounded = VWSubpixelClampTileTexel(pixel, tileOrigin, tileSize);
    return QuiltTexture.Load(int3(bounded, 0))[channel];
}

float4 ManualSubpixelPS(VertexOutput input) : SV_Target
{
    float2 pixelUv = float2(input.position.x, screen.y - input.position.y) / screen.xy;
    bool useCells = outputOptions.y > 0.5;
    if (!useCells && screen.w > 0.5)
    {
        pixelUv.x = 1.0 - pixelUv.x;
    }

    int2 pixel = min((int2)(pixelUv * screen.xy), (int2)screen.xy - 1);
    int cell = useCells ? VWSubpixelCellIndex(pixel, (int)outputOptions.x) : 0;
    float2 withinView = VWSubpixelWithinView(pixelUv, quilt.z, quilt.w);
    if (any(withinView < 0.0) || any(withinView > 1.0))
    {
        return float4(0.0, 0.0, 0.0, 1.0);
    }

    float4 result = float4(0.0, 0.0, 0.0, 1.0);
    uint viewCount = (uint)(grid.x * grid.y);
    [unroll]
    for (int component = 0; component < 3; ++component)
    {
        float2 subpixelUv = useCells
            ? pixelUv + cellOffsets[cell * 3 + component].xy
            : pixelUv + float2(channelOrder[component] * screen.z, 0.0);
        float phase = VWSubpixelPhase(subpixelUv, calibration.x, calibration.y, calibration.z);
        bool invert = useCells || calibration.w > 0.5;
        uint viewIndex = VWSubpixelViewIndex(phase, viewCount, invert);
        uint2 tile = VWSubpixelTile(viewIndex, (uint)grid.x);

        float2 position = VWSubpixelTileSamplePosition(tile, withinView, grid.zw);
        int2 tileSize = int2(grid.zw);
        int2 tileOrigin = int2(tile) * tileSize;
        float2 samplePosition = position - 0.5;
        int2 pixel0 = int2(floor(samplePosition));
        int2 pixel1 = pixel0 + 1;
        float2 fraction = samplePosition - float2(pixel0);
        bool linearOutput = outputOptions.z > 0.5;
        bool sourceIsSrgb = outputOptions.w > 0.5;
        result[component] = VWSubpixelBilinear(
            VWLoadChannel(int2(pixel0.x, pixel0.y), component, tileOrigin, tileSize),
            VWLoadChannel(int2(pixel1.x, pixel0.y), component, tileOrigin, tileSize),
            VWLoadChannel(int2(pixel0.x, pixel1.y), component, tileOrigin, tileSize),
            VWLoadChannel(int2(pixel1.x, pixel1.y), component, tileOrigin, tileSize),
            fraction, linearOutput, sourceIsSrgb, false);
    }
    return result;
}
