// The legacy particle shaders (Particles/Alpha Blended, Particles/Additive: blend set by _SrcBlend / _DstBlend) with
// soft particles, for WakeSplashEffects' spray, mist and haze over Scatterer's forward ocean: there the depth test is done by
// hand (ZTest Always, _WWManualZ 1), so the waves fade the puffs rather than cut them (WakeOcean.cginc).
Shader "WakeSplashEffects/SoftParticle"
{
    Properties
    {
        _TintColor ("Tint Color", Color) = (0.5, 0.5, 0.5, 0.5)
        _MainTex ("Particle Texture", 2D) = "white" {}
        _InvFade ("Soft Particles Factor", Range(0.01, 3.0)) = 1.0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source blend", Float) = 5       // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination blend", Float) = 10 // OneMinusSrcAlpha (One: additive)
        [Enum(UnityEngine.Rendering.CompareFunction)] _WWZTest ("Depth test", Float) = 4    // LEqual
    }
    SubShader
    {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Pass
        {
            Blend [_SrcBlend] [_DstBlend]
            ColorMask RGB
            Cull Off
            Lighting Off
            ZWrite Off
            ZTest [_WWZTest]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_particles
            #include "UnityCG.cginc"
            #include "WakeOcean.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _TintColor;
            float _InvFade;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 proj : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.proj = ComputeScreenPos(o.pos);
                COMPUTE_EYEDEPTH(o.proj.z);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.proj)));
                float fade = WWOcean(i.proj, i.proj.z, scene);
            #if defined(SOFTPARTICLES_ON)
                fade *= saturate(_InvFade * (scene - i.proj.z));
            #endif
                fixed4 col = 2.0 * i.color * _TintColor * tex2D(_MainTex, i.uv);
                col.a = saturate(col.a) * fade;
                return col;
            }
            ENDCG
        }
    }
}
