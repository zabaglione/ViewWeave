Shader "Hidden/ViewWeave/QuiltOrientationDiagnostic"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _OrientationViewId;
            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };
            v2f vert(appdata input)
            {
                v2f output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }
            float4 frag(v2f input) : SV_Target
            {
                float top = step(0.5, input.uv.y);
                float left = step(input.uv.x, 0.5);
                float viewCode = (_OrientationViewId + 1.0) / 9.0;
                return float4(top, viewCode + (1.0 - left) * 0.03, 1.0 - top, 1.0);
            }
            ENDHLSL
        }
    }
}
