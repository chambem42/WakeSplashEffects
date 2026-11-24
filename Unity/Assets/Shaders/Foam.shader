// Foam particles on the water (alpha blended, like Legacy Particles/Alpha Blended at tint 0.5): each patch eroded by its
// age (WakeFoam.cginc), so it thins from dense white to lace continuously instead of in texture-sheet steps.
Shader "WaterWakes/Foam"
{
    Properties
    {
        _MainTex ("Foam atlas", 2D) = "white" {}
        _Crisp ("Crisp edges", Range(0, 1)) = 0
        _Bright ("Brightness", Float) = 1
        _Under ("Bubbles under the surface (foam map)", Range(0, 1)) = 0.3
        _Churn ("Whitewater (foam map)", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ColorMask RGB
            Cull Off
            Lighting Off
            ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "WakeFoam.cginc"

            sampler2D _MainTex;
            float _Crisp, _Bright;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float4 tc : TEXCOORD0;      // uv, age, random
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 color : COLOR;
                float4 tc : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.tc = v.tc;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 t = SampleFoam(_MainTex, i.tc.xy, i.tc.w);
                // (crisp: its fading alpha erodes it (as if older) instead of making it see-through)
                float age = lerp(i.tc.z, max(i.tc.z, 1 - i.color.a), _Crisp);
                float a = FoamAmountCrisp(t, age, _Crisp) * lerp(i.color.a, sqrt(saturate(i.color.a)), _Crisp);
                return float4(i.color.rgb * (0.86 + 0.14 * t.a) * _Bright, a);
            }
            ENDCG
        }
    }
}
