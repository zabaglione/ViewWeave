cbuffer ScaleConstants : register(b0)
{
    uint sourceWidth;
    uint sourceHeight;
    uint sourceColumns;
    uint sourceRows;
    uint outputWidth;
    uint outputHeight;
    uint destinationColumns;
    uint destinationRows;
    uint sourceIsSrgb;
    uint3 padding;
};

Texture2D<float4> InputTexture : register(t0);

struct VertexOutput
{
    float4 position : SV_Position;
};

VertexOutput stable_sdk_input_vs(uint vertexId : SV_VertexID)
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

float3 EncodeSrgb(float3 linearValue)
{
    linearValue = saturate(linearValue);
    float3 lowValue = linearValue * 12.92;
    float3 highValue = 1.055 * pow(linearValue, 1.0 / 2.4) - 0.055;
    return lerp(lowValue, highValue, step(0.0031308, linearValue));
}

float4 LoadRawColour(int2 pixel)
{
    float4 value = InputTexture.Load(int3(pixel, 0));
    if (sourceIsSrgb != 0)
    {
        value.rgb = EncodeSrgb(value.rgb);
    }
    return value;
}

float4 stable_sdk_input_ps(VertexOutput input) : SV_Target
{
    uint2 outputPixel = min(uint2(input.position.xy), uint2(outputWidth - 1, outputHeight - 1));
    uint destinationTileWidth = outputWidth / destinationColumns;
    uint destinationTileHeight = outputHeight / destinationRows;
    uint sourceTileWidth = sourceWidth / sourceColumns;
    uint sourceTileHeight = sourceHeight / sourceRows;
    uint tileX = outputPixel.x / destinationTileWidth;
    uint tileY = outputPixel.y / destinationTileHeight;
    uint2 destinationLocal = uint2(outputPixel.x - tileX * destinationTileWidth,
                                   outputPixel.y - tileY * destinationTileHeight);
    uint2 sourceOrigin = uint2(tileX * sourceTileWidth, tileY * sourceTileHeight);

    float2 sourcePosition = (float2(destinationLocal) + 0.5) *
                            float2(sourceTileWidth, sourceTileHeight) /
                            float2(destinationTileWidth, destinationTileHeight) - 0.5;
    sourcePosition = clamp(sourcePosition,
                           float2(0.0, 0.0),
                           float2(sourceTileWidth - 1, sourceTileHeight - 1));
    int2 local0 = int2(floor(sourcePosition));
    int2 local1 = min(local0 + 1, int2(sourceTileWidth - 1, sourceTileHeight - 1));
    float2 fraction = sourcePosition - float2(local0);

    int2 pixel00 = int2(sourceOrigin) + local0;
    int2 pixel10 = int2(sourceOrigin) + int2(local1.x, local0.y);
    int2 pixel01 = int2(sourceOrigin) + int2(local0.x, local1.y);
    int2 pixel11 = int2(sourceOrigin) + local1;
    float4 top = lerp(LoadRawColour(pixel00), LoadRawColour(pixel10), fraction.x);
    float4 bottom = lerp(LoadRawColour(pixel01), LoadRawColour(pixel11), fraction.x);
    return lerp(top, bottom, fraction.y);
}
