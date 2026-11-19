// The wave simulation's passes (WaveSim.cs): a height field on a square of sea around where the camera looks, evolved by
// Tessendorf's iWave (a convolution kernel standing in for the vertical derivative, so deep-water dispersion comes out
// right: w^2 = g k), driven by the hulls' displacement (their waterline footprints x draft: water pushed down as a
// footprint arrives, let back as it leaves), absorbed at its edges; and the foam its breaking crests leave.
// Textures are RFloat, uv 0..1 across the square.
Shader "WaterWakes/WaveSim"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
    }
    CGINCLUDE
    #include "UnityCG.cginc"

    sampler2D _MainTex;     // (Blit's source: unused by most passes)
    sampler2D _H;           // the height now (m)
    sampler2D _HPrev;       // the height a step ago
    sampler2D _D;           // the displacement now: r m (<= 0: the hulls' footprints pushed down), g how still that hull is
    sampler2D _DPrev;       // the displacement the step before
    sampler2D _DMoved;      // the footprints now, drawn where their hulls were the frame before (the moving push: the motion)
    sampler2D _F;           // the foam (0..1)
    float _Texel;           // 1 / resolution
    float _Cell;            // m a texel
    float _Dt;              // s a step
    float _G;               // gravity (m/s^2)
    float _Alpha;           // damping (1/s)
    float _AlphaEdge;       // ... added towards the square's edge (a sponge: no waves reflected back in)
    float _Src;             // 1 on the frame's first step (the displacement's change applied once), else 0
    float _MaxPush;         // m: the most a footprint may push or let back in a frame (a part teleported does not slam the water)
    float _Kern[169];       // the 13 x 13 kernel (texel offsets -6..6), calibrated to sqrt(-laplacian) in 1/texel
    float4 _Blob0[64];      // footprints: xy their centre (uv), zw their length's direction (unit, uv frame)
    float4 _Blob1[64];      // x half length, y half width (uv), z depth (m), w how still (1 at rest, 0 moving)
    float4 _Blob2[64];      // x half width (uv), y depth (m) at its fore station (Blob1's: its aft one; the stations at
                            // 0.6 of its half length either side of its centre); z how much the sim makes its waves (its
                            // push's weight: 0 where the analytic Kelvin wake has taken over); w its waves' wavenumber
                            // moving (g / V^2: their depth (1 - e^-kd) / k)
    float _BlobCount;
    float2 _Shift;          // the square moved by this (uv): content re-sampled from uv + _Shift
    float _FoamSlope;       // the slope a crest starts to break at
    float _FoamDecay;       // 1/s
    sampler2D _S;           // the frame's push (m), spread out past the hulls' waterlines (passes 6, 7)
    float _SrcGain;         // x the spread push outside a footprint (most of a hull's spread lands under it and is dropped)
    float2 _BlurDir;        // the blur's step (uv): one texel along x, then y
    float _Visc;            // m^2/s: damping growing as k^2 (short waves die in seconds, swell barely touched)
    float _BreakDamp;       // 1/s: the damping where a crest is steeper than water holds (its energy gone to foam)
    float _VisScale;        // m for full colour
    // (pass 5, in Scatterer's command buffer: the sim's globals)
    sampler2D _SimHeight;
    sampler2D _SimFoot;     // the hulls' footprints now (m, <= 0)
    float _SimTexel;
    float _SimCell;
    sampler2D _SimHeightMip;   // (pass 5) the height with mips
    sampler2D _SimFoam;        // (pass 5) its foam
    float4 _MainTex_TexelSize;
    float4x4 _SimMatrix;
    float3 _SimUp;
    float _SimOn, _SimDisplace;
    float _SimDebugLift;    // m: (diagnostic) the whole square lifted by this as well
    float _SimDebugTilt;    // (diagnostic) and a smooth 20 m swell of this amplitude (m)

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

    float Inside(float2 uv) { return all(uv >= 0) && all(uv <= 1) ? 1 : 0; }
    float Hat(sampler2D t, float2 uv) { return tex2Dlod(t, float4(uv, 0, 0)).r * Inside(uv); }
    ENDCG

    SubShader
    {
        ZTest Always Cull Off ZWrite Off

        // 0: the displacement now: each footprint a stretch of waterline between two stations, its width and depth
        //    going from the aft one's to the fore one's, flat-topped along its length, depth x (1 - lx^8)^2 (1 - ly^2)^2
        //    (a hull's stretches overlapping into one smooth trough that tapers as the hull does: no scallops to pump
        //    chop as it moves, no squared-off blocks), the deepest kept
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float d = 0, still = 0, wgt = 0, dw = 0;
                for (int k = 0; k < 64; k++)
                {
                    if (k >= (int)_BlobCount)
                        break;
                    float2 r = i.uv - _Blob0[k].xy;
                    float2 dir = _Blob0[k].zw;
                    float lx = dot(r, dir) / max(_Blob1[k].x, 1e-6);
                    float t = saturate(lx / 1.2 + 0.5);   // (0 at the aft station, 1 at the fore)
                    float hw = lerp(_Blob1[k].y, _Blob2[k].x, t), dep = lerp(_Blob1[k].z, _Blob2[k].y, t);
                    float ly = (r.x * -dir.y + r.y * dir.x) / max(hw, 1e-6);
                    float ax = lx * lx, ay = ly * ly;
                    ax *= ax;
                    ax *= ax;
                    if (ax < 1 && ay < 1)
                    {
                        float dk = dep * (1 - ax) * (1 - ax) * (1 - ay) * (1 - ay);
                        d = max(d, dk);
                        float x = _Blob2[k].w * dk;
                        dw = max(dw, x > 1e-4 ? dk * (1 - exp(-x)) / x : dk);
                        still = max(still, _Blob1[k].w);
                        wgt = max(wgt, _Blob2[k].z);
                    }
                }
                return float4(-d, still, wgt, -dw);
            }
            ENDCG
        }

        // 1: one step: h' = h (2 - a dt) / (1 + a dt) - h_prev / (1 + a dt) - g dt^2 (G * h) / (cell (1 + a dt))
        //    + the displacement's change (first step of the frame)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float h = Hat(_H, i.uv), hp = Hat(_HPrev, i.uv);
                float conv = 0;
                [unroll]
                for (int y = -6; y <= 6; y++)
                    [unroll]
                    for (int x = -6; x <= 6; x++)
                        conv += _Kern[(y + 6) * 13 + (x + 6)] * Hat(_H, i.uv + float2(x, y) * _Texel);
                float edge = max(abs(i.uv.x - 0.5), abs(i.uv.y - 0.5)) * 2;
                // breaking: steeper than _FoamSlope the wave loses its energy (real water cannot keep a slope much past
                // 1/7 of its length; without this the linear sim piles chop of any height around a rocking hull)
                float2 g2 = float2(Hat(_H, i.uv + float2(_Texel, 0)) - Hat(_H, i.uv - float2(_Texel, 0)),
                                   Hat(_H, i.uv + float2(0, _Texel)) - Hat(_H, i.uv - float2(0, _Texel))) / (2 * _Cell);
                float brk = smoothstep(_FoamSlope * 2, _FoamSlope * 4, length(g2));
                float a = (_Alpha + _AlphaEdge * smoothstep(0.82, 1.0, edge) + _BreakDamp * brk) * _Dt;
                // viscosity-like: + nu dt lap(h - h_prev) (amplitude decays at nu k^2 / 2: with 0.05, 1 m waves in ~1 s,
                // 5 m in ~25 s); stands in for the ambient sea scrambling short coherent rings
                float2 tx = float2(_Texel, 0), ty = float2(0, _Texel);
                float lapv = (Hat(_H, i.uv + tx) + Hat(_H, i.uv - tx) + Hat(_H, i.uv + ty) + Hat(_H, i.uv - ty) - 4 * h)
                           - (Hat(_HPrev, i.uv + tx) + Hat(_HPrev, i.uv - tx) + Hat(_HPrev, i.uv + ty) + Hat(_HPrev, i.uv - ty) - 4 * hp);
                float hn = (h * (2 - a) - hp - _G * _Dt * _Dt * conv / _Cell + _Visc * _Dt * lapv / (_Cell * _Cell)) / (1 + a);
                // a still hull is an obstacle: no water under it (the height held at 0, so waves reflect off it and
                // nothing under it feeds back), the water its heave pushes aside entering just past its waterline (the
                // spread push). A moving one is a moving pressure patch: the surface under it held down to what its
                // waves feel of its depth ((1 - e^-kd) / k, k = g / V^2: ~V^2 / g as it slows, nothing left by the time
                // it stops), its waves made by that moving. (Its displaced water once went in as mass: added at its bow,
                // taken at its stern, the hole behind a ship's transom metres deep, refilling only by waves, overshooting
                // into a mound and a sheet of breaking foam behind it every time it stopped)
                float4 dd = tex2D(_D, i.uv);
                float cov = smoothstep(0.02, 0.08, -dd.r);
                float obst = cov * dd.g;
                float2 sb = tex2D(_S, i.uv).rg;   // the frame's push, blurred: r still hulls', g moving ones'
                hn += _Src * _SrcGain * sb.r * (1 - obst);
                hn = lerp(hn, dd.a * dd.b, cov * (1 - dd.g));
                hn *= 1 - obst;
                return clamp(hn, -4, 4);
            }
            ENDCG
        }

        // 2: shift (the square re-centred): the content re-sampled from uv + _Shift, zero where it comes from outside
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv + _Shift;
                return Inside(uv) * tex2Dlod(_MainTex, float4(uv, 0, 0));
            }
            ENDCG
        }

        // 3: foam: what was there decaying, more where a crest is steep enough to break (steep, convex, rising)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float2 dx = float2(_Texel, 0), dy = float2(0, _Texel);
                // (Scharr's 3x3 gradient: the same for waves running along the axes and the diagonals)
                float a00 = Hat(_H, i.uv - dx - dy), a10 = Hat(_H, i.uv - dy), a20 = Hat(_H, i.uv + dx - dy);
                float a01 = Hat(_H, i.uv - dx), a21 = Hat(_H, i.uv + dx);
                float a02 = Hat(_H, i.uv - dx + dy), a12 = Hat(_H, i.uv + dy), a22 = Hat(_H, i.uv + dx + dy);
                float hx = (3 * (a20 - a00) + 10 * (a21 - a01) + 3 * (a22 - a02)) / (32 * _Cell);
                float hy = (3 * (a02 - a00) + 10 * (a12 - a10) + 3 * (a22 - a20)) / (32 * _Cell);
                float h = Hat(_H, i.uv);
                float lap = (Hat(_H, i.uv + dx) + Hat(_H, i.uv - dx) + Hat(_H, i.uv + dy) + Hat(_H, i.uv - dy) - 4 * h) / (_Cell * _Cell);
                float slope = sqrt(hx * hx + hy * hy);
                float rising = step(0, h - Hat(_HPrev, i.uv));
                float brk = smoothstep(_FoamSlope, _FoamSlope * 1.5, slope) * saturate(-lap * _Cell * 4) * step(0, h) * rising;
                // (none within ~10 texels (~3 m) of a hull: a still one's height held at 0 under it makes a step at its
                // side, and its heave's push enters in a ring just past it, steep there but no breaking crest; a hull
                // bobbing at rest would otherwise ring itself with foam)
                float ob = 0;
                [unroll]
                for (int oy = -3; oy <= 3; oy++)
                    [unroll]
                    for (int ox = -3; ox <= 3; ox++)
                    {
                        float2 od = tex2Dlod(_D, float4(i.uv + float2(ox, oy) * 3.3 * _Texel, 0, 0)).rg;
                        ob = max(ob, smoothstep(0.02, 0.08, -od.r));   // (any hull's, moving too: the water closing in behind a slow transom read as a breaking crest, a carpet of foam behind it as it stopped)
                    }
                brk *= 1 - ob;
                float f = Hat(_F, i.uv) * exp(-_FoamDecay * _Dt) + brk * 4 * _Dt;
                return saturate(f);
            }
            ENDCG
        }

        // 4: a picture of it (the debug inset): crests red, troughs blue, foam white
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float h = Hat(_H, i.uv) / _VisScale, f = Hat(_F, i.uv);
                float3 c = float3(0.05, 0.07, 0.12) + saturate(h) * float3(0.95, 0.15, 0.1) + saturate(-h) * float3(0.1, 0.35, 0.95);
                return float4(lerp(c, 1, f * 0.8), 1);
            }
            ENDCG
        }
    
        // 5: Scatterer's ocean vertex positions (xyz world, w its own ocean x) lifted along up by the sim's height there,
        //    fading out towards the square's edge (run inside Scatterer's gbuffer command buffer: ScattererHook)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float4 v = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
                if (_SimOn < 0.5 || _SimDisplace <= 0)
                    return v;
                float2 uv = mul(_SimMatrix, float4(v.xyz, 1)).xy;
                if (any(uv < 0) || any(uv > 1))
                    return v;
                float fade = 1 - smoothstep(0.8, 0.97, max(abs(uv.x - 0.5), abs(uv.y - 0.5)) * 2);
                // the height low-passed to what the grid can carry: the spacing of the neighbouring vertices (the
                // nearer of each pair: past the ocean's edge they jump), in texels, picks the mip and the tent's width
                // (single-texel chop sampled metres apart would be random steep facets, which shade white)
                float2 ts = _MainTex_TexelSize.xy;
                float3 p0 = v.xyz;
                float sx = min(distance(tex2Dlod(_MainTex, float4(i.uv + float2(ts.x, 0), 0, 0)).xyz, p0),
                               distance(tex2Dlod(_MainTex, float4(i.uv - float2(ts.x, 0), 0, 0)).xyz, p0));
                float sy = min(distance(tex2Dlod(_MainTex, float4(i.uv + float2(0, ts.y), 0, 0)).xyz, p0),
                               distance(tex2Dlod(_MainTex, float4(i.uv - float2(0, ts.y), 0, 0)).xyz, p0));
                float sp = clamp(max(sx, sy) / _SimCell, 2, 64);
                float lod = log2(sp) - 1;
                float t = 0.5 * sp * _SimTexel;
                float h = 0.25 * tex2Dlod(_SimHeightMip, float4(uv, 0, lod)).r;
                h += 0.125 * (tex2Dlod(_SimHeightMip, float4(uv + float2(t, 0), 0, lod)).r + tex2Dlod(_SimHeightMip, float4(uv - float2(t, 0), 0, lod)).r
                            + tex2Dlod(_SimHeightMip, float4(uv + float2(0, t), 0, lod)).r + tex2Dlod(_SimHeightMip, float4(uv - float2(0, t), 0, lod)).r);
                h += 0.0625 * (tex2Dlod(_SimHeightMip, float4(uv + float2(t, t), 0, lod)).r + tex2Dlod(_SimHeightMip, float4(uv - float2(t, t), 0, lod)).r
                             + tex2Dlod(_SimHeightMip, float4(uv + float2(t, -t), 0, lod)).r + tex2Dlod(_SimHeightMip, float4(uv - float2(t, -t), 0, lod)).r);
                // none under a hull or within ~1.5 m of it: the water a hull displaces sits in the sim as a mound at
                // the hull itself, which would otherwise lift the sea up through it
                float foot = tex2Dlod(_SimFoot, float4(uv, 0, 0)).r;
                float r = 10 * _SimTexel;
                [unroll]
                for (int k = 0; k < 8; k++)
                {
                    float an = k * 0.785398;
                    foot = min(foot, tex2Dlod(_SimFoot, float4(uv + r * float2(cos(an), sin(an)), 0, 0)).r);
                }
                float hull = saturate(1 + foot / 0.1);
                // flattened where it is foam (the churned water behind a hull: its chop stays in the shading, not the
                // geometry), and soft-limited to ~1 m
                // (the most foam over the height's own footprint: the foam is texel-patchy, the height low-passed, and a
                // gap in the one under a low-passed lump left that vertex standing alone, a spike)
                float fm = tex2Dlod(_SimFoam, float4(uv, 0, 0)).r;
                [unroll]
                for (int q = 0; q < 8; q++)
                {
                    float aq = q * 0.785398;
                    fm = max(fm, tex2Dlod(_SimFoam, float4(uv + max(t, 3 * _SimTexel) * float2(cos(aq), sin(aq)), 0, 0)).r);
                }
                h *= 1 - saturate(fm * 2);
                h = tanh(h / 1.0) * 1.0;
                v.xyz += _SimUp * h * fade * hull * _SimDisplace;
                v.xyz += _SimUp * fade * (_SimDebugLift + _SimDebugTilt * sin(uv.x * 160 / 20 * 6.2832));
                return v;
            }
            ENDCG
        }

        // 6: the frame's push (the displacement's change, clamped), blurred along _BlurDir (a Gaussian, sigma 3 texels):
        //    r the still hulls' share, g the moving ones'
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float2 s = 0;
                [unroll]
                for (int k = -6; k <= 6; k++)
                {
                    float2 uv = i.uv + k * _BlurDir;
                    float w = exp(-k * k / 18.0) / 7.5199;
                    float4 dd = tex2Dlod(_D, float4(uv, 0, 0));
                    float2 dp = tex2Dlod(_DPrev, float4(uv, 0, 0)).rb;
                    float2 dm = tex2Dlod(_DMoved, float4(uv, 0, 0)).ra;
                    // (still: the change in its footprint's shape alone, its heave, where it was a frame ago (not its
                    // creep: a stopped ship drifting a few cm/s sideways slid its steep-sided footprint, read at its full
                    // depth as water shoved aside, a stream of small ripples along it as if it shook); moving: its motion
                    // alone, the same footprint where it was against where it is (its waves' depth); x the handover's
                    // weight, then or now)
                    float2 push = float2(dd.g * clamp(dp.x - dm.x, -_MaxPush, _MaxPush), (1 - dd.g) * clamp(dm.y - dd.a, -_MaxPush, _MaxPush));
                    s += w * Inside(uv) * push * max(dd.b, dp.y);
                }
                return float4(s, 0, 0);
            }
            ENDCG
        }

        // 7: ... and along the other axis (of _MainTex)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float2 s = 0;
                [unroll]
                for (int k = -6; k <= 6; k++)
                {
                    float2 uv = i.uv + k * _BlurDir;
                    s += exp(-k * k / 18.0) / 7.5199 * Inside(uv) * tex2Dlod(_MainTex, float4(uv, 0, 0)).rg;
                }
                return float4(s, 0, 0);
            }
            ENDCG
        }
    }
}
