// Foam painted into Scatterer's ocean gbuffer (oceanGbufferFoam, R8), between its geometry pass (AfterForwardOpaque) and
// its shading pass (AfterImageEffectsOpaque), so Scatterer lights it on the displaced waves like its own whitecaps.
// Each ocean pixel's world position comes from oceanGbufferDepth; the foam there from a top-down foam map.
Shader "WaterWakes/OceanFoam"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D_float oceanGbufferDepth;
    sampler2D oceanGbufferFoam;
    sampler2D_float ScattererDepthCopy;
    sampler2D oceanGbufferNormalsAndSigma;
    sampler2D _WakeFoamMap;
    float4x4 _WakeInvProj;      // inverse of the camera's GPU projection (render into texture)
    float4x4 _WakeCamToWorld;   // camera.cameraToWorldMatrix
    float4x4 _WakeMapMatrix;    // world -> foam map uv (xy)
    float3 _WakeSurf;           // sea level below the camera (world)
    float3 _WakeUp;             // local vertical there
    float _WakeRadius;          // body radius
    float _WakeClearDepth;      // oceanGbufferDepth's clear value (no ocean)
    float _WakeFlipY;
    float _WakeStrength;
    float _WakeDebugHalf;       // > 0: full foam on the left half of the screen (hook test)

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

    v2f vert(appdata_img v)
    {
        v2f o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    float2 Ndc(float2 uv, float flip)
    {
        float2 n = uv * 2 - 1;
        if (flip > 0.5)
            n.y = -n.y;
        return n;
    }

    // raw device depth -> world
    float3 WorldFromDevice(float2 uv, float raw, float flip)
    {
        float z = raw;
    #if defined(SHADER_API_GLCORE) || defined(SHADER_API_GLES3) || defined(SHADER_API_VULKAN)
        z = raw * 2 - 1;
    #endif
        float4 v = mul(_WakeInvProj, float4(Ndc(uv, flip), z, 1));
        v.xyz /= v.w;
        return mul(_WakeCamToWorld, float4(v.xyz, 1)).xyz;
    }

    // the view ray through uv (view space, z = -1)
    float3 Ray(float2 uv, float flip)
    {
        float4 v = mul(_WakeInvProj, float4(Ndc(uv, flip), 0.5, 1));
        v.xyz /= v.w;
        return v.xyz / -v.z;
    }

    float Height(float3 p)
    {
        float3 d = p - _WakeSurf;
        float u = dot(d, _WakeUp);
        return u + (dot(d, d) - u * u) / (2 * _WakeRadius);
    }

    float IsOcean(float raw) { return abs(raw - _WakeClearDepth) > 1e-9 ? 1 : 0; }
    ENDCG

    SubShader
    {
        ZTest Always ZWrite Off Cull Off

        // 0: Scatterer's foam and ours, the larger, into our own copy (the global oceanGbufferFoam is then pointed at it:
        // Scatterer's buffer is a temporary of its own command buffer, which ours cannot write)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float theirs = tex2D(oceanGbufferFoam, i.uv).r;
                if (_WakeDebugHalf > 0.5)
                    return i.uv.x < 0.5 ? 1 : theirs;
                float raw = tex2D(oceanGbufferDepth, i.uv).r;
                if (_WakeDebugHalf < -0.5)       // depth binding test: foam wherever the ocean depth is not the clear value
                    return max(theirs, IsOcean(raw));
                if (IsOcean(raw) < 0.5)
                    return theirs;
                float3 p = WorldFromDevice(i.uv, raw, _WakeFlipY);
                float2 m = mul(_WakeMapMatrix, float4(p, 1)).xy;
                if (any(m < 0) || any(m > 1))
                    return theirs;
                // fade out at the map's edge
                float2 e = min(m, 1 - m);
                float edge = saturate(min(e.x, e.y) * 20);
                return max(theirs, saturate(tex2D(_WakeFoamMap, m).r * _WakeStrength * edge));
            }
            ENDCG
        }

        // 1: diagnostics A: the ocean's height above sea level as the depth read four ways
        // (device depth, flip 0 / 1; linear eye depth, flip 0 / 1)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float raw = tex2D(oceanGbufferDepth, i.uv).r;
                float3 c = _WorldSpaceCameraPos;
                float3 e0 = mul(_WakeCamToWorld, float4(Ray(i.uv, 0) * raw, 1)).xyz;
                float3 e1 = mul(_WakeCamToWorld, float4(Ray(i.uv, 1) * raw, 1)).xyz;
                return float4(Height(WorldFromDevice(i.uv, raw, 0)), Height(WorldFromDevice(i.uv, raw, 1)), Height(e0), Height(e1));
            }
            ENDCG
        }

        // 2: diagnostics B: what Scatterer's buffers hold here: ocean depth, its depth copy, normals.r, foam
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                // ocean depth, depth copy, the foam map sampled where this pixel's ocean is, Scatterer's foam buffer (after ours)
                float raw = tex2D(oceanGbufferDepth, i.uv).r;
                float mapped = 0;
                if (IsOcean(raw) > 0.5)
                {
                    float2 m = mul(_WakeMapMatrix, float4(WorldFromDevice(i.uv, raw, _WakeFlipY), 1)).xy;
                    if (all(m >= 0) && all(m <= 1))
                        mapped = tex2D(_WakeFoamMap, m).r;
                }
                return float4(raw, tex2D(ScattererDepthCopy, i.uv).r, mapped, tex2D(oceanGbufferFoam, i.uv).r);
            }
            ENDCG
        }
    }
}
