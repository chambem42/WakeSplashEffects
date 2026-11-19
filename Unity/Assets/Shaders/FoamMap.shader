// The foam map's replacement shader: each foam particle written as its own foam amount (its patch eroded by age as on
// screen, WakeFoam.cginc, x particle alpha), stacked as the particles stack on screen (white over the water at that
// alpha: a + dst (1 - a)), so the painted foam starts as white as the particle foam and fades with it.
Shader "WaterWakes/FoamMap"
{
    Properties
    {
        _MainTex ("Foam atlas", 2D) = "white" {}
        _Crisp ("Crisp edges", Range(0, 1)) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcColor
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "WakeFoam.cginc"

            sampler2D _MainTex;
            float _Crisp;   // (from each particle's own material: the splash foam's and the upwell's as on screen)

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
                float age = lerp(i.tc.z, max(i.tc.z, 1 - i.color.a), _Crisp);
                float a = FoamAmountCrisp(t, age, _Crisp) * lerp(i.color.a, sqrt(saturate(i.color.a)), _Crisp) * (0.86 + 0.14 * t.a);
                return float4(a, a, a, a);
            }
            ENDCG
        }
    }
}
