// Metal texture-access wrapper over the cross-platform subpixel sampling contract.
Shader "Hidden/ViewWeave/MetalInterlace"
{
    Properties
    {
        _MainTex ("Quilt", 2D) = "black" {}
        _ChannelOrder ("Subpixel order and image flip", Vector) = (0,1,2,0)
        _SubpixelPattern ("Cell pattern mode and count", Vector) = (0,0,0,0)
        _SourceOptions ("Quilt transfer options", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment Interlace
            #pragma target 4.5
            #include "UnityCG.cginc"
            #include "../../../Common/Resources/ViewWeaveSubpixelSampling.hlsl"
            sampler2D _MainTex;
            float4 _Calibration; // pitch, vertical tilt, center, inverted views
            float4 _Screen;      // physical width, height, RGB subpixel step
            float4 _ChannelOrder; // R/G/B subpixel indices, horizontal image flip
            float4 _SubpixelPattern; // mode, count
            float4 _SourceOptions; // linear output, source is sRGB, hardware decoded sRGB
            float4 _SubpixelOffsets[12]; // four cells, normalized R/G/B XY offsets
            float4 _Grid;        // columns, rows, tile width, tile height
            float4 _QuiltSize;   // width, height, aspect correction, zoom

            float LoadChannel(int2 pixel, int channel, int2 tileOrigin, int2 tileSize)
            {
                int2 bounded = VWSubpixelClampTileTexel(pixel, tileOrigin, tileSize);
                float2 texelCenterUv = (float2(bounded) + 0.5) / _QuiltSize.xy;
                return tex2Dlod(_MainTex, float4(texelCenterUv, 0, 0))[channel];
            }

            float4 Interlace(v2f_img input) : SV_Target
            {
                // Unity's Blit UV is logical bottom-left optical space on Metal; keep the
                // validated Mac coordinate convention and fetch each texel at its UV center.
                float2 pixelUv = input.uv;
                bool useCells = _SubpixelPattern.y > 0.5;
                if (!useCells && _ChannelOrder.w > 0.5) pixelUv.x = 1.0 - pixelUv.x;
                int2 pixel = min((int2)(input.uv * _Screen.xy), (int2)_Screen.xy - 1);
                int cell = useCells ? VWSubpixelCellIndex(pixel, (int)_SubpixelPattern.x) : 0;
                float2 withinView = VWSubpixelWithinView(pixelUv, _QuiltSize.z, _QuiltSize.w);
                if (any(withinView < 0.0) || any(withinView > 1.0)) return float4(0,0,0,1);
                float4 result = float4(0,0,0,1);
                uint viewCount = (uint)(_Grid.x * _Grid.y);
                bool linearOutput = _SourceOptions.x > 0.5;
                bool sourceIsSrgb = _SourceOptions.y > 0.5;
                bool hardwareDecodedSrgb = _SourceOptions.z > 0.5;
                [unroll] for (int component = 0; component < 3; ++component)
                {
                    float2 subpixelUv = useCells ? pixelUv + _SubpixelOffsets[cell * 3 + component].xy :
                        pixelUv + float2(_ChannelOrder[component] * _Screen.z, 0.0);
                    float phase = VWSubpixelPhase(subpixelUv, _Calibration.x, _Calibration.y, _Calibration.z);
                    uint view = VWSubpixelViewIndex(phase, viewCount, useCells || _Calibration.w > 0.5);
                    uint2 tile = VWSubpixelTile(view, (uint)_Grid.x);
                    float2 position = VWSubpixelTileSamplePosition(tile, withinView, _Grid.zw);
                    int2 tileSize = int2(_Grid.zw);
                    int2 tileOrigin = int2(tile) * tileSize;
                    float2 samplePosition = position - 0.5;
                    int2 pixel0 = int2(floor(samplePosition));
                    int2 pixel1 = pixel0 + 1;
                    float2 fraction = samplePosition - float2(pixel0);
                    result[component] = VWSubpixelBilinear(
                        LoadChannel(int2(pixel0.x, pixel0.y), component, tileOrigin, tileSize),
                        LoadChannel(int2(pixel1.x, pixel0.y), component, tileOrigin, tileSize),
                        LoadChannel(int2(pixel0.x, pixel1.y), component, tileOrigin, tileSize),
                        LoadChannel(int2(pixel1.x, pixel1.y), component, tileOrigin, tileSize),
                        fraction, linearOutput, sourceIsSrgb, hardwareDecodedSrgb);
                }
                return result;
            }
            ENDHLSL
        }
    }
}
