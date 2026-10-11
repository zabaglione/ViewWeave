// Original lenticular sampling. Cell-pattern interoperability rules are documented in docs/bridge-subpixel-calibration.md.
Shader "Hidden/ViewWeave/MetalInterlace"
{
    Properties
    {
        _MainTex ("Quilt", 2D) = "black" {}
        _ChannelOrder ("Subpixel order and image flip", Vector) = (0,1,2,0)
        _SubpixelPattern ("Cell pattern mode and count", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert_img
            #pragma fragment Interlace
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Calibration; // pitch, vertical tilt, center, inverted views
            float4 _Screen;      // physical width, height, RGB subpixel step
            float4 _ChannelOrder; // R/G/B subpixel indices, horizontal image flip
            float4 _SubpixelPattern; // mode, count
            float4 _SubpixelOffsets[12]; // four cells, normalized R/G/B XY offsets
            float4 _Grid;        // columns, rows, tile width, tile height
            float4 _QuiltSize;   // width, height, aspect correction

            int CellIndex(int2 pixel)
            {
                int mode = (int)_SubpixelPattern.x;
                if (mode == 1) return (pixel.x + pixel.y) % 2;
                if (mode == 2) return pixel.x % 2;
                if (mode == 3) return (pixel.y + (pixel.x % 2) * 2) % 4;
                if (mode == 4) return pixel.y % 2;
                return 0;
            }

            float4 Interlace(v2f_img input) : SV_Target
            {
                float2 pixelUv = input.uv;
                bool useCells = _SubpixelPattern.y > 0.5;
                if (!useCells && _ChannelOrder.w > 0.5) pixelUv.x = 1.0 - pixelUv.x;
                int cell = useCells ? CellIndex(min((int2)(input.uv * _Screen.xy), (int2)_Screen.xy - 1)) : 0;
                float2 withinView = (pixelUv - 0.5) * float2(_QuiltSize.z, 1.0) + 0.5;
                if (any(withinView < 0.0) || any(withinView > 1.0)) return float4(0,0,0,1);
                float4 result = float4(0,0,0,1);
                [unroll] for (int component = 0; component < 3; ++component)
                {
                    float2 subpixelUv = useCells ? pixelUv + _SubpixelOffsets[cell * 3 + component].xy :
                        pixelUv + float2(_ChannelOrder[component] * _Screen.z, 0.0);
                    float phase = frac((subpixelUv.x + subpixelUv.y * _Calibration.y) * _Calibration.x - _Calibration.z);
                    float viewFraction = (useCells || _Calibration.w > 0.5) ? 1.0 - phase : phase;
                    float view = min(floor(viewFraction * _Grid.x * _Grid.y), _Grid.x * _Grid.y - 1.0);
                    float2 tile = float2(fmod(view, _Grid.x), floor(view / _Grid.x));
                    // Half-pixel clamp prevents bilinear sampling from bleeding into neighbouring views.
                    float2 position = (tile + withinView) * _Grid.zw;
                    position = clamp(position, tile * _Grid.zw + 0.5, (tile + 1.0) * _Grid.zw - 0.5);
                    // Optical view selection is discontinuous. Derivative/anisotropic filtering
                    // would combine different views even when the Quilt has no mipmaps.
                    result[component] = tex2Dlod(_MainTex, float4(position / _QuiltSize.xy, 0, 0))[component];
                }
                // Unity quilts in a linear project need display transfer encoding exactly once.
                #ifndef UNITY_COLORSPACE_GAMMA
                result.rgb = LinearToGammaSpace(result.rgb);
                #endif
                return result;
            }
            ENDHLSL
        }
    }
}
