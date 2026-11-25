// Build/load check: one pass returning a constant colour.
Shader "WakeSplashEffects/Test"
{
    SubShader
    {
        ZTest Always ZWrite Off Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct v2f { float4 pos : SV_POSITION; };
            v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); return o; }
            float4 frag(v2f i) : SV_Target { return float4(0.25, 0.5, 0.75, 1); }
            ENDCG
        }
    }
}
