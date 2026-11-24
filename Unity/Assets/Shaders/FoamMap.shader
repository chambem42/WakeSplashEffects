// The foam map's replacement shader: each foam particle written as its own foam amount (its patch eroded by age as on
// screen, WakeFoam.cginc, x particle alpha), stacked as the particles stack on screen (white over the water at that
// alpha: a + dst (1 - a)), so the painted foam starts as white as the particle foam and fades with it.
// R: the foam on the surface. G: the bubbles under it (the cloud a breaking wave or a hard hit drives down, which glows
// turquoise through the water): the patch's soft mask, blurred more as it ages, outlasting the surface foam; _Under the
// particle system's share below (the upwell's bubbles mostly, the wake's foam mostly on top).
// B (the near map only: the far one has two channels): the share of R that is a splash's whitewater (_Churn), which
// the ocean pass draws dense, white and ropey instead of as the wake's lace.
Shader "WaterWakes/FoamMap"
{
    Properties
    {
        _MainTex ("Foam atlas", 2D) = "white" {}
        _Crisp ("Crisp edges", Range(0, 1)) = 0
        _Under ("Bubbles under the surface", Range(0, 1)) = 0.3
        _Churn ("Whitewater (a splash's churned foam; near map's b)", Range(0, 1)) = 0
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
            float _Under, _Churn;

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
                // the bubbles: the patch's round mask from a coarser mip (blurrier as it ages), fading slower than the top
                float v = floor(saturate(i.tc.w) * 3.999);
                float2 o = float2(fmod(v, 2), floor(v * 0.5));
                float mb = tex2Dbias(_MainTex, float4((i.tc.xy + o) * 0.5, 0, 1 + 3 * saturate(i.tc.z))).b;
                float b = smoothstep(0.0, 0.9, mb) * pow(saturate(i.color.a), 0.8) * lerp(0.2, 0.5, _Under);
                // (none at the quad's edge: the coarse mips bleed the atlas' next tile in there, a square glow under thin foam)
                b *= smoothstep(0, 0.25, min(min(i.tc.x, 1 - i.tc.x), min(i.tc.y, 1 - i.tc.y)));
                a *= lerp(1, 0.4, _Under);
                return float4(a, b, a * _Churn, a);
            }
            ENDCG
        }
    }
}
