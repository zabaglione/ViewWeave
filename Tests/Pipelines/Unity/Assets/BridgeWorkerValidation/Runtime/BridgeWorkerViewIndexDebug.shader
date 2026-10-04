Shader "LookingGlass/BridgeWorkerViewIndexDebug"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "black" {}
        _Columns ("Columns", Float) = 8
        _Rows ("Rows", Float) = 6
        _ViewCount ("View Count", Float) = 48
        _TileAspect ("Tile Aspect", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Overlay" }
        Pass
        {
            ZTest Always Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float _Columns, _Rows, _ViewCount, _TileAspect;

            struct Attributes { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 vertex : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings vert(Attributes input)
            {
                Varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float DigitPixel(int digit, int column, int row)
            {
                if (digit == 0) return (column == 0 || column == 4 || row == 0 || row == 6) ? 1 : 0;
                if (digit == 1) return (column == 2 || (row == 0 && column > 0) || (row == 6 && column == 1)) ? 1 : 0;
                if (digit == 2) return (row == 6 || row == 3 || row == 0 || (row > 3 && column == 4) || (row < 3 && column == 0)) ? 1 : 0;
                if (digit == 3) return (row == 6 || row == 3 || row == 0 || column == 4) ? 1 : 0;
                if (digit == 4) return (row == 3 || column == 4 || (column == 0 && row > 3)) ? 1 : 0;
                if (digit == 5) return (row == 6 || row == 3 || row == 0 || (row > 3 && column == 0) || (row < 3 && column == 4)) ? 1 : 0;
                if (digit == 6) return (row == 6 || row == 3 || row == 0 || column == 0 || (row < 3 && column == 4)) ? 1 : 0;
                if (digit == 7) return (row == 6 || column == 4) ? 1 : 0;
                if (digit == 8) return (row == 6 || row == 3 || row == 0 || column == 0 || column == 4) ? 1 : 0;
                return (row == 6 || row == 3 || row == 0 || column == 4 || (row > 3 && column == 0)) ? 1 : 0;
            }

            fixed4 frag(Varyings input) : SV_Target
            {
                float4 color = tex2D(_MainTex, input.uv);
                float2 scaled = input.uv * float2(_Columns, _Rows);
                float2 local = frac(scaled);
                int tileX = (int)floor(scaled.x);
                int tileY = (int)floor(scaled.y);
                int view = tileY * (int)_Columns + tileX;
                if (view < 0 || view >= (int)_ViewCount) return color;

                float cellY = 0.18 / 7.0;
                float cellX = cellY / max(_TileAspect, 0.01);
                float left = 0.055;
                float top = 0.945;
                float2 pixel = float2((local.x - left) / cellX, (top - local.y) / cellY);
                bool inside = pixel.x >= -1.0 && pixel.x < 12.0 && pixel.y >= -1.0 && pixel.y < 8.0;
                if (!inside) return color;
                color = float4(0, 0, 0, 1);
                int column = (int)floor(pixel.x);
                int row = 6 - (int)floor(pixel.y);
                if (column >= 0 && column < 11 && row >= 0 && row < 7)
                {
                    int digit = column < 5 ? view / 10 : view % 10;
                    int digitColumn = column < 5 ? column : column - 6;
                    if (digitColumn >= 0 && digitColumn < 5 && DigitPixel(digit, digitColumn, row) > 0) color = float4(1, 1, 1, 1);
                }
                return color;
            }
            ENDHLSL
        }
    }
}
