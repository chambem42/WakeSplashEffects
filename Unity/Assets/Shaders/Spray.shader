// Spray sheet blobs (the splash skirt, the bow sheet): alpha eroded instead of faded. The texture's R is a height field
// (a round body streaked along its length); the particle's alpha and age raise a threshold through it, kept as a crisp
// edge (about a pixel wide), so a blob stays sharp-edged and breaks up into ragged streaks as it thins, rather than
// going soft and see-through. Lit by the sun: slightly shaded where thick, glowing where thin with the sun behind it.
// Custom vertex streams: TEXCOORD0 = (uv, age 0..1, stable random 0..1). Global _WW_SunDir (world, towards the sun).
Shader "WaterWakes/Spray"
{
    Properties
    {
        _MainTex ("Streaked blobs (R height, G streaks)", 2D) = "white" {}
        _Erode ("Erosion: base, by alpha, by age, edge alpha", Vector) = (0.12, 0.75, 0.25, 0.55)
        _Bright ("Brightness", Float) = 1.15
        _Rim ("Thin parts brighter than thick (0: evenly lit)", Range(0, 1)) = 1
        _Fade ("Thinning out late in its life as well as eroding", Range(0, 1)) = 0
        [Enum(UnityEngine.Rendering.CompareFunction)] _WWZTest ("Depth test (Always over the forward ocean)", Float) = 4
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
            ZTest [_WWZTest]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "WakeOcean.cginc"

            sampler2D _MainTex;
            float4 _Erode;
            float _Bright, _Rim, _Fade;
            float4 _WW_SunDir;
            // soft: faded out within ~1 m of the scene behind it (a hull's side), from the camera's depth texture;
            // _WW_SoftFade 0 (not set: no depth texture asked for): off
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float _WW_SoftFade;

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
                float3 wpos : TEXCOORD1;
                float4 proj : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.color = v.color;
                o.tc = v.tc;
                o.proj = ComputeScreenPos(o.pos);
                COMPUTE_EYEDEPTH(o.proj.z);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 t = tex2D(_MainTex, i.tc.xy);
                float age = saturate(i.tc.z);
                // (the threshold: up as its alpha falls (the age fade, its opacity setting) and a little with age)
                float th = _Erode.x + _Erode.y * (1 - i.color.a) + _Erode.z * age;
                float w = max(fwidth(t.r) * 0.8, 0.01);
                float a = smoothstep(th - w, th + w, t.r);
                // (solid where thick, a little see-through just inside its edge)
                float thick = saturate((t.r - th) * 5);
                a *= lerp(_Erode.w, 0.85, thick) * lerp(0.6, 1, t.g);
                // (_Fade: what erosion leaves of it late on thinning to nothing, not left as hard flakes)
                a *= 1 - _Fade * smoothstep(0.25, 1, age);
                // (nothing left of it at no alpha: the height field's peaks and drops stand above the highest threshold)
                a *= saturate(i.color.a * 6);
                // (lit: thick parts a touch darker; thin parts glowing with the sun behind them)
                float3 V = normalize(i.wpos - _WorldSpaceCameraPos);
                float back = pow(saturate(dot(V, _WW_SunDir.xyz)), 5);
                // (_Rim 0: evenly lit, only its streaks a touch darker: overlapping blobs' bright rims read as scales)
                float shade = lerp(lerp(0.78, 1.0, t.g), lerp(0.66, 1.0, saturate(t.g * 0.6 + 0.4 * (1 - thick))), _Rim) + back * (1 - 0.6 * thick) * 0.7;
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.proj)));
                if (_WW_SoftFade > 0)
                    a *= saturate((scene - i.proj.z) / _WW_SoftFade);
                a *= WWOcean(i.proj, i.proj.z, scene);
                return float4(i.color.rgb * shade * _Bright, a);
            }
            ENDCG
        }
    }
}
