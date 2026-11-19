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
    sampler2D _WakeNearMap;     // the finer map where the camera looks
    float4x4 _WakeNearMatrix;   // world -> near map uv (xy)
    float _WakeNearOn;
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

    // Kelvin wakes: each source a craft running straight over the water. In its frame (x back along its track from its
    // bow, y across) deep-water ship waves stand still: by stationary phase, at a point inside the 19.47 deg wedge two
    // wave systems meet, transverse and divergent, travelling at angles theta to the track with
    // tan(psi) = tan(theta) / (1 + 2 tan^2(theta)) (psi the point's angle off the track), wavenumber k0 / cos^2(theta)
    // (k0 = g / V^2), phase k (x cos(theta) + |y| sin(theta)), strongest along the cusp line (the wedge's edge),
    // weakening as 1 / sqrt(r). Their slopes tilt Scatterer's normals, which it then lights (glints, sky) as its own waves.
    #define KW_MAX 8
    float4 _KwPos[KW_MAX];      // xyz: the bow at the sea (world); w: speed (m/s)
    float4 _KwDir[KW_MAX];      // xyz: heading (level, unit); w: wave height at the hull (m)
    float4 _KwPar[KW_MAX];      // x: hull length, y: trail length (m), z: transverse share, w: beam (m)
    #define KW_TRACK 48
    float4 _KwTrack[KW_MAX * KW_TRACK];   // each source's track, KW_TRACK points: its bow now, then where it was (xyz
                                          // world); w: the track's curvature there (1/m, + bending toward the + side)
    float4 _KwInfo[KW_MAX];               // x: the source's track points; y: (a pattern its craft has left) no waves
                                          // nearer its bow than this (m: where their energy has got to)
    float _KwCount;
    float _KwDisplace;          // x their height put into Scatterer's ocean geometry (pass 5)
    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    float _KwGravity;
    float _KwMaxSlope;


    // smooth value noise, 0..1
    float Hash2(float2 q) { return frac(sin(dot(q, float2(127.1, 311.7))) * 43758.5453); }
    float VNoise(float2 q)
    {
        float2 i = floor(q), f = frac(q), u = f * f * (3 - 2 * f);
        return lerp(lerp(Hash2(i), Hash2(i + float2(1, 0)), u.x), lerp(Hash2(i + float2(0, 1)), Hash2(i + 1), u.x), u.y);
    }

    // the wave simulation (WaveSim): its height, the height a step before, its foam, over a square of sea
    sampler2D _SimHeight;
    sampler2D _SimPrev;
    sampler2D _SimFoam;
    float4x4 _SimMatrix;     // world -> its uv (xy)
    float3 _SimE1, _SimE2;   // its axes (world, level)
    float _SimCell, _SimTexel, _SimOn;

    float SimFade(float2 uv) { return 1 - smoothstep(0.8, 0.97, max(abs(uv.x - 0.5), abs(uv.y - 0.5)) * 2); }

    float SimH(float2 uv) { return tex2Dlod(_SimHeight, float4(uv, 0, 0)).r; }

    // its height's slope here, as a world gradient (the sim's square fading out towards its edge)
    float3 SimGrad(float3 p)
    {
        float2 uv = mul(_SimMatrix, float4(p, 1)).xy;
        if (any(uv < 0) || any(uv > 1))
            return 0;
        float hx = (SimH(uv + float2(_SimTexel, 0)) - SimH(uv - float2(_SimTexel, 0))) / (2 * _SimCell);
        float hy = (SimH(uv + float2(0, _SimTexel)) - SimH(uv - float2(0, _SimTexel))) / (2 * _SimCell);
        float2 g = float2(hx, hy) * SimFade(uv);
        float m = length(g);
        g *= m > 0.6 ? 0.6 / m : 1;
        return _SimE1 * g.x + _SimE2 * g.y;
    }

    // its whitecaps: the foam its breaking crests left, broken up as Scatterer's are (its small waves, n)
    float SimWhitecap(float3 p, float3 n)
    {
        float2 uv = mul(_SimMatrix, float4(p, 1)).xy;
        if (any(uv < 0) || any(uv > 1))
            return 0;
        float f = tex2Dlod(_SimFoam, float4(uv, 0, 0)).r * SimFade(uv);
        if (f <= 0.001)
            return 0;
        float2 q = float2(dot(p, _SimE1), dot(p, _SimE2));
        float lumpy = VNoise(q * 0.9) * 0.6 + VNoise(q * 2.7) * 0.4;
        float bubbles = smoothstep(0.35, 0.55, VNoise(q * 6) * 0.6 + VNoise(q * 14) * 0.4);
        // (capped near Scatterer's own small waves' slopes: with the ocean displaced, n carries the sim's facets too)
        float slope = min(length(n - _WakeUp * dot(n, _WakeUp)), 0.12);
        float cover = smoothstep(0.55, 0.45, 1 - f * 1.4 + 0.35 * (lumpy - 0.5) - 1.5 * slope);
        // where it is dense, broken into patches (~2-6 m) with water between, the patches streaked and thinning
        float dense = saturate(f * 1.6 - 0.6);
        float patches = VNoise(q * 0.35 + 17.3) * 0.65 + VNoise(q * 1.1 - 5.1) * 0.35;
        cover *= 1 - dense * (1 - smoothstep(0.42, 0.62, patches) * lerp(0.55, 1, lumpy));
        return cover * lerp(0.5, 1, bubbles);
    }

    // an entry's ring of ripples: height A env(s) cos(2 pi s / lambda), s = (leading crest's radius - distance), the
    // envelope rising over a quarter wavelength to the crest and dying over the packet behind it
    #define RP_MAX 8
    float4 _RpPos[RP_MAX];   // xyz: the ring's centre at the sea (world); w: its leading crest's radius (m)
    float4 _RpPar[RP_MAX];   // x: wave height (m), y: wavelength (m), z: the packet behind the crest (m), w: whitecap 0..1
    float _RpCount;

    void RippleFrame(int k, float3 p, out float3 d3, out float d, out float s)
    {
        d3 = p - _RpPos[k].xyz;
        d3 -= _WakeUp * dot(d3, _WakeUp);
        d = length(d3);
        s = _RpPos[k].w - d;
    }

    float3 RippleGrad(int k, float3 p, float3 dpx, float3 dpy)
    {
        float3 d3;
        float d, s;
        RippleFrame(k, p, d3, d, s);
        float lam = max(_RpPar[k].y, 0.05), L = max(_RpPar[k].z, lam);
        if (s < -0.25 * lam || s > 4 * L)
            return 0;
        float env = saturate(1 + s / (0.25 * lam)) * exp(-max(s, 0) / L);
        float kk = 6.2831853 / lam;
        // (shorter than ~3 px across: faded out, no shimmer)
        float px = max(length(dpx), length(dpy));
        float aa = saturate((lam / max(px, 1e-4) - 3) / 3);
        float slope = clamp(_RpPar[k].x * env * kk * sin(kk * s) * aa, -0.25, 0.25);
        return (d3 / max(d, 1e-3)) * slope;
    }

    // a ring's whitecap, made as Scatterer makes its own: where the surface is squeezed together (its Jacobian below a
    // threshold, cut sharply), here the ring's crests' squeeze (1 - chop x the height's curvature along the radius)
    // crowded further by Scatterer's own small waves where they face the ring's way (n: its world normal here), broken
    // into bubbly patches; on the leading crests only (s from a quarter wavelength ahead to one behind)
    float RippleFoam(int k, float3 p, float3 n)
    {
        float3 d3;
        float d, s;
        RippleFrame(k, p, d3, d, s);
        float lam = max(_RpPar[k].y, 0.05), L = max(_RpPar[k].z, lam);
        if (s < -0.25 * lam || s > lam)
            return 0;
        float env = saturate(1 + s / (0.25 * lam)) * exp(-max(s, 0) / L);
        float kk = 6.2831853 / lam;
        float curv = _RpPar[k].x * env * kk * kk * cos(kk * s);
        float3 rad = d3 / max(d, 1e-3);
        // (the sea's own plane: KSP's up is seldom world y)
        float3 e1 = normalize(cross(_WakeUp, abs(_WakeUp.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0))), e2 = cross(_WakeUp, e1);
        float2 q = float2(dot(d3, e1), dot(d3, e2)) / lam;
        float facing = min(dot(n - _WakeUp * dot(n, _WakeUp), rad), 0.12);   // (its small waves' slope down the ring's way; capped as above)
        float jm = 1 - 0.3 * min(curv, 1.5) - 4.0 * max(0, facing) - 0.6 * (VNoise(q * 6) - 0.5) - 0.3 * (VNoise(q * 15) - 0.5);
        float cover = smoothstep(0.66, 0.58, jm);
        float bubbles = smoothstep(0.35, 0.55, VNoise(q * 25) * 0.6 + VNoise(q * 55) * 0.4);
        return _RpPar[k].w * cover * lerp(0.5, 1, bubbles);
    }

    // one wave system's height gradient (hull frame: x back, y across) from a source at the frame's origin hw wide each
    // side (the hull's width across the flow: its beam running straight, much of its length when it skids in a turn, so
    // the wedge spreads from both its sides and not from the track alone, which a skidding stern swung out of):
    // transverse and divergent waves, phase warped by warp (radians), wave height amp at the hull. dpx / dpy: one
    // pixel's step across and down the screen on the water, in the same frame (waves shorter than ~8 px along their
    // own direction fade out: the footprint along the wave, not its size, so grazing views don't shimmer)
    // (z: the height itself, each term's slope / k)
    float3 KelvinSystem(float x, float y, float hw, float k0, float L, float T, float amp, float trans, float warp, float edge, float2 dpx, float2 dpy)
    {
        float ay = max(abs(y) - hw, 0);
        float r = sqrt(x * x + y * y);
        float t = ay / max(x, 1e-3);
        float disc = 1 - 8 * t * t;                       // > 0 inside the wedge
        // past the cusp line (the wedge's edge) the waves die away as an Airy function's tail: over a width growing only as
        // x^(1/3) k0^(-2/3) (narrow near the hull; a fixed one, a cusp wavelength, ran waves far outside the wedge there at
        // speed), the line itself wandering by edge (in those widths): no hard boundary. And a hull makes its waves
        // along its whole length, not at a point: its bow's and stern's cusp lines lie ~0.35 L apart, so the edge is
        // blurred over hb ~0.3 L as well. As a real wake's: the echelon crests along the edge strongest inside the
        // line, their ends tapering to ~0.2 at it, ~2% an Airy width past it (nothing outside the wedge but that thin
        // fade). With the Airy width alone each crest stopped dead at the line, drawing it as a seam; with the fade
        // past it, the merged cusp crests (both systems at once, in step) ran on outside as broad stubby feathers
        float lc = 4.19 / k0;
        float wa = 0.7 * pow(max(x, 1), 0.3333) * pow(k0, -0.6667);
        float hb = 0.3 * L * saturate(x / L);
        float dy = ay - 0.35355 * max(x, 0) + edge * wa;
        float din = max(dy + wa + hb, 0) / (wa + 0.5 * hb);
        float win = x > 0 ? exp(-din * din) : 0;
        // the crests' directions follow the wandering cusp line (t shifted the same way, more toward the edge), and
        // past it hold the cusp's: there the two families have merged into one set of crests that just dies away
        // (left to diverge, they crossed into a herringbone that drew the edge as a seam)
        float te = saturate(t / 0.35355);
        float tw = clamp((ay + edge * wa * te * te) / max(x, 1e-3), 0, 0.35355);
        float dw = 1 - 8 * tw * tw;
        float sq = sqrt(max(dw, 0));
        float cusp = pow(max(dw, 0) + 0.3, -0.25) * 0.74;
        // (the stationary-phase pattern holds only a wavelength or so from its source: nearer, its crests crowd into a
        // fan of lines; faded in over the cusp waves' length)
        amp *= win * cusp * smoothstep(0.15 * lc + 0.02 * L, 0.9 * lc + 0.3 * L, r) * sqrt(L / (r + L)) * (1 - smoothstep(0.75 * T, T + 1, x));
        // a hull makes waves much shorter than ~ a fifth of its length only weakly (the divergent waves close to the
        // track would otherwise be ever finer lines)
        float kHull = 6.2832 / max(0.2 * L, 0.5);
        float3 g = 0;
        [unroll]
        for (int s = 0; s < 2; s++)
        {
            // tan(theta): transverse (2t / (1 + sq), the stable form of (1 - sq) / 4t) and divergent ((1 + sq) / 4t)
            float tn = s == 0 ? 2 * tw / (1 + sq) : min((1 + sq) / (4 * max(tw, 1e-4)), 40);
            float c2 = 1 / (1 + tn * tn), cs = sqrt(c2), sn = tn * cs;
            float kk = k0 / c2;
            float2 kd = float2(cs, sn * sign(y));
            float ph = kk * (x * cs + ay * sn) + warp * (1 + s * sq);   // (the shorter divergent crests bend more; equally at the cusp, where they meet)
            float dph = kk * max(abs(dot(dpx, kd)), abs(dot(dpy, kd)));   // phase change per pixel
            float aa = exp(-(dph / 1.2) * (dph / 1.2));          // (gradually: a sharp cut draws a line where it falls)
            float gen = 1 / (1 + kk / kHull);                    // (gently, over a wide angle: a steep one drew a line)
            // ... and next to none much shorter than ~7% of its length (its wave-making spectrum falls away there): with
            // 1 / k alone their slope never fell, the divergent waves just off the track (3 m and less behind a 70 m
            // ship) glinting as fine ripples, round a ship stopped in the wake it left as if it shook. The cusp's,
            // ~0.5 L and longer, are untouched (the ferry fit)
            float rs = kk / (3 * kHull);
            rs *= rs;
            gen /= 1 + rs * rs;
            // the divergent waves fainter toward the track, smoothly with the angle (any cut-off of these ever shorter waves,
            // by the hull's length or the pixels, would draw a line at the angle where it falls)
            float share = s == 0 ? trans : pow(saturate(tw / 0.354), 1.5);
            float sl = min(amp * share * aa * gen * kk, _KwMaxSlope);
            g.xy += -sl * sin(ph + 0.785398) * kd;
            g.z += sl / kk * cos(ph + 0.785398);
        }
        return g;
    }

    // two octaves of value noise on a turned grid (one axis-aligned octave shows its square cells)
    float Patchy(float2 q)
    {
        float2 a = float2(0.8 * q.x - 0.6 * q.y, 0.6 * q.x + 0.8 * q.y);
        float2 b = float2(0.38 * q.x + 0.92 * q.y, -0.92 * q.x + 0.38 * q.y) * 2.17 + 5.3;
        return (VNoise(a) * 2 + VNoise(b)) / 3;
    }

    // where p is on source k's wake: x along its track behind the bow, y off it (+ to the source's + side); fwd / side: the
    // track's heading and + side there (world, level); kap: its curvature. The track is a polyline (the bow now, then where
    // it was) with a tangent at each point (its two segments' mean); p goes to the point of a segment whose tangent,
    // blended along it, is square to p's offset: the frame then turns smoothly round each point, with no jump in x or in
    // the crests' direction where one segment hands over to the next (the nearest point of each straight segment drew
    // those: steps in the crests outside a turn, a seam on each corner's bisector inside it). So in a turn the wake
    // follows the curve the hull ran, not the heading it has now. (Off the ends the track runs on straight: ahead of the
    // bow, past the trail)
    void TrackFrame(int k, float3 p, out float x, out float y, out float3 fwd, out float3 side, out float kap)
    {
        fwd = _KwDir[k].xyz;
        side = normalize(cross(_WakeUp, fwd));
        float3 d = p - _KwPos[k].xyz;
        x = -dot(d, fwd);
        y = dot(d, side);
        kap = 0;
        int n = (int)_KwInfo[k].x;
        float best = 1e30, s0 = 0;
        float3 ePrev = 0;
        [loop]
        for (int i = 0; i < n - 1; i++)
        {
            float4 a = _KwTrack[k * KW_TRACK + i], b = _KwTrack[k * KW_TRACK + i + 1];
            float3 E = b.xyz - a.xyz;
            E -= dot(E, _WakeUp) * _WakeUp;
            float len = max(length(E), 1e-3);
            float3 e = E / len;
            float3 eNext = e;
            if (i < n - 2)
            {
                float3 E2 = _KwTrack[k * KW_TRACK + i + 2].xyz - b.xyz;
                E2 -= dot(E2, _WakeUp) * _WakeUp;
                eNext = E2 / max(length(E2), 1e-3);
            }
            float3 ta = i == 0 ? e : normalize(ePrev + e), tb = normalize(e + eNext);
            ePrev = e;
            float3 v = p - a.xyz;
            v -= dot(v, _WakeUp) * _WakeUp;
            // s along the segment where (v - s E) . lerp(ta, tb, s) = 0: A s^2 + B s + C = 0, the root near -C / B
            float3 dt = tb - ta;
            float A = -dot(E, dt), B = dot(v, dt) - dot(E, ta), C = dot(v, ta);
            float disc = B * B - 4 * A * C, den = -B + sqrt(max(disc, 0));
            float sOn = 2 * C / max(den, 1e-4);
            bool ok = disc >= 0 && den > 1e-4 && (i == 0 || sOn >= 0) && (i == n - 2 || sOn <= 1);
            float3 w, t;
            float xs;
            if (i == 0 && sOn < 0)                 // ahead of the bow: straight on along its tangent
            {
                float u = dot(v, ta);
                w = v - u * ta; t = ta; xs = u;
            }
            else if (i == n - 2 && sOn > 1)        // past the trail's end
            {
                float u = dot(v - E, tb);
                w = v - E - u * tb; t = tb; xs = s0 + len + u;
            }
            else
            {
                w = v - sOn * E; t = normalize(lerp(ta, tb, sOn)); xs = s0 + sOn * len;
            }
            float d2 = dot(w, w);
            if (ok && d2 < best)
            {
                best = d2;
                fwd = -t;
                side = cross(_WakeUp, fwd);
                x = xs;
                y = (dot(w, side) < 0 ? -1 : 1) * sqrt(d2);
                kap = lerp(a.w, b.w, saturate(sOn));
            }
            s0 += len;
        }
    }

    // the turbulent wake's slick behind source k at (x, y) on its wake (0..1): the churned water behind a hull smooths the
    // small waves, so a calm band trails it, widening slowly, its edges ragged, fading over the wake's length
    float Slick(int k, float x, float y)
    {
        float4 par = _KwPar[k];
        y = abs(y);
        float L = max(par.x, 0.5), T = par.y, B = max(par.w, 0.3);
        float xs = x - 0.85 * L;                           // from (about) the stern
        float w = 0.55 * B + 0.035 * max(xs, 0);
        w *= 0.75 + 0.5 * Patchy(float2(x / (3 * B + 10), y / (B + 3)) + 1.7);
        return smoothstep(w, 0.6 * w, y) * smoothstep(0, 0.3 * L, xs) * exp(-max(xs, 0) / (20 * L + 150)) * (1 - smoothstep(0.75 * T, T + 1, x));
    }

    // the wave height's gradient from source k (world, horizontal) at (x, y) on its wake (TrackFrame: fwd, side, kap);
    // dpx / dpy: the pixel's step on the water. Not a perfect pattern: the crests wander (the phase warped by slowly
    // drifting noise half a wavelength across, as the sea and the hull's pitching bend them); and the strength comes and
    // goes in patches
    float3 KelvinGrad(int k, float x, float y, float3 fwd, float3 side, float kap, float3 dpx, float3 dpy, out float h)
    {
        float4 src = _KwPos[k], dir = _KwDir[k], par = _KwPar[k];
        float2 hx = float2(-dot(dpx, fwd), dot(dpx, side)), hy = float2(-dot(dpy, fwd), dot(dpy, side));
        float V = max(src.w, 0.5), L = max(par.x, 0.5), T = par.y;
        float k0 = _KwGravity / (V * V);
        float lam = 6.2832 / k0;
        float2 q = float2(x, y) / (0.5 * lam + 0.3 * L);
        float tt = _Time.y * 0.07;
        float warp = 4.0 * (Patchy(q + float2(tt, 3.1)) + 0.6 * Patchy(q * 2.3 + float2(7.7, -tt)) - 0.8);
        float patch = Patchy(float2(x, y) / (1.4 * lam + L) + float2(-tt * 0.5, 11.3));
        float amp = dir.w * (0.25 + 0.95 * patch * patch);
        // (a pattern its craft has left: gone from ahead of where its energy has got to, over ~ a wavelength)
        float front = _KwInfo[k].y;
        amp *= front > 0 ? smoothstep(front, front + 0.7 * lam + 0.2 * L, x) : 1;
        float edge = 0.9 * (Patchy(float2(x, y) / (2.5 * lam + L) + float2(3.7, tt * 0.3)) - 0.5);
        // inside a turn the waves of the two sides of the curve run into each other toward its centre: faded out there
        // (where the nearest point of the track, and so the frame, is ill-defined)
        float rin = 1 / max(abs(kap), 1e-5);
        amp *= y * kap > 0 ? smoothstep(rin, 0.5 * rin, abs(y)) : 1;
        // (a second, stern system beating against this one drew its own cusp line inside the wake: left out)
        float3 g = KelvinSystem(x, y, 0.5 * max(par.w, 0.3), k0, L, T, amp, par.z, warp, edge, hx, hy);
        h = g.z;
        return g.x * -fwd + g.y * side;
    }
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
                if (_WakeDebugHalf > 0.5 && _WakeDebugHalf < 1.5)
                    return i.uv.x < 0.5 ? 1 : theirs;
                if (_WakeDebugHalf > 2.5)       // (diagnostic) Scatterer's foam alone
                    return theirs;
                if (_WakeDebugHalf > 1.5)       // (diagnostic) ours alone
                    theirs = 0;
                float raw = tex2D(oceanGbufferDepth, i.uv).r;
                if (_WakeDebugHalf < -0.5)       // depth binding test: foam wherever the ocean depth is not the clear value
                    return max(theirs, IsOcean(raw));
                if (IsOcean(raw) < 0.5)
                    return theirs;
                float3 p = WorldFromDevice(i.uv, raw, _WakeFlipY);
                float2 m = mul(_WakeMapMatrix, float4(p, 1)).xy;
                float ours = 0;
                if (all(m >= 0) && all(m <= 1))
                {
                    // fade out at the map's edge
                    float edge = 1 - smoothstep(0.6, 0.98, length(m - 0.5) * 2);   // (round and soft: no line where it ends)
                    ours = tex2D(_WakeFoamMap, m).r * edge;
                }
                // the near map where it covers, the far one taking over towards its edge
                if (_WakeNearOn > 0.5)
                {
                    float2 n = mul(_WakeNearMatrix, float4(p, 1)).xy;
                    if (all(n >= 0) && all(n <= 1))
                    {
                        float w = 1 - smoothstep(0.75, 0.97, max(abs(n.x - 0.5), abs(n.y - 0.5)) * 2);
                        ours = lerp(ours, tex2D(_WakeNearMap, n).r, w);
                    }
                }
                // the rings' whitecaps along their leading crests (Scatterer's normal here: its own small waves)
                float4 senc = tex2D(oceanGbufferNormalsAndSigma, i.uv);
                float3 sn = float3(senc.xy * 2 - 1, 0);
                sn.z = sqrt(saturate(1 - dot(sn.xy, sn.xy))) * (senc.w > 0 ? 1 : -1);
                // the simulated waves' whitecaps
                if (_SimOn > 0.5)
                    ours = max(ours, SimWhitecap(p, sn));
                [unroll]
                for (int k = 0; k < RP_MAX; k++)
                    if (k < (int)_RpCount)
                        ours = max(ours, RippleFoam(k, p, sn));
                return max(theirs, saturate(ours * _WakeStrength));
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

        // 3: Scatterer's ocean normals with the Kelvin wakes' slopes added, into our own copy (the global
        // oceanGbufferNormalsAndSigma then pointed at it). Its encoding: world normal xy * 0.5 + 0.5 in RG (10 bits),
        // the slope variance in B, the normal's z sign in A (2 bits)
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float4 enc = tex2D(oceanGbufferNormalsAndSigma, i.uv);
                float raw = tex2D(oceanGbufferDepth, i.uv).r;
                float ocean = IsOcean(raw);
                float3 p = WorldFromDevice(i.uv, raw, _WakeFlipY);
                float3 dpx = ddx(p) * ocean + (1 - ocean) * 1e6, dpy = ddy(p) * ocean + (1 - ocean) * 1e6;
                float3 grad = 0;
                float slick = 0;
                [unroll]
                for (int k = 0; k < KW_MAX; k++)
                    if (k < (int)_KwCount)
                    {
                        float x, y, kap;
                        float3 fwd, side;
                        TrackFrame(k, p, x, y, fwd, side, kap);
                        float hk;
                        grad += KelvinGrad(k, x, y, fwd, side, kap, dpx, dpy, hk);
                        slick = max(slick, Slick(k, x, y));
                    }
                [unroll]
                for (int j = 0; j < RP_MAX; j++)
                    if (j < (int)_RpCount)
                        grad += RippleGrad(j, p, dpx, dpy);
                if (_SimOn > 0.5)
                    grad += SimGrad(p);
                if (ocean < 0.5)
                    return enc;
                float3 n;
                n.xy = enc.xy * 2 - 1;
                n.z = sqrt(saturate(1 - dot(n.xy, n.xy)));
                n.z = enc.w > 0 ? n.z : -n.z;
                n = normalize(lerp(n, _WakeUp, 0.6 * slick) - grad);   // (the slick: flattened, smoother)
                return float4(n.xy * 0.5 + 0.5, enc.z * (1 - 0.5 * slick), n.z >= 0 ? 1 : 0);
            }
            ENDCG
        }
    
        // 4: debug: the sea tinted by the wave sim's height (crests red, troughs blue), after Scatterer shaded it
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                float raw = tex2D(oceanGbufferDepth, i.uv).r;
                if (IsOcean(raw) < 0.5 || _SimOn < 0.5)
                    return 0;
                float3 p = WorldFromDevice(i.uv, raw, _WakeFlipY);
                float2 uv = mul(_SimMatrix, float4(p, 1)).xy;
                if (any(uv < 0) || any(uv > 1))
                    return 0;
                float h = SimH(uv) / 0.25;
                float3 c = h > 0 ? float3(1, 0.15, 0.1) : float3(0.1, 0.35, 1);
                return float4(c, saturate(abs(h)) * 0.75 * SimFade(uv));
            }
            ENDCG
        }

        // 5: Scatterer's ocean vertex positions (xyz world, w its own ocean x) lifted along up by the Kelvin wakes' height
        //    (run inside Scatterer's gbuffer command buffer, after the sim's: ScattererHook). The spacing of the
        //    neighbouring vertices (the nearer of each pair: past the ocean's edge they jump) is the footprint that fades
        //    waves too short for the grid, as a pixel's fades them in the normals
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            float4 frag(v2f i) : SV_Target
            {
                float4 v = tex2Dlod(_MainTex, float4(i.uv, 0, 0));
                if (_KwDisplace <= 0 || _KwCount < 0.5)
                    return v;
                float2 ts = _MainTex_TexelSize.xy;
                float3 p = v.xyz;
                float3 ax = tex2Dlod(_MainTex, float4(i.uv + float2(ts.x, 0), 0, 0)).xyz - p, bx = p - tex2Dlod(_MainTex, float4(i.uv - float2(ts.x, 0), 0, 0)).xyz;
                float3 ay = tex2Dlod(_MainTex, float4(i.uv + float2(0, ts.y), 0, 0)).xyz - p, by = p - tex2Dlod(_MainTex, float4(i.uv - float2(0, ts.y), 0, 0)).xyz;
                float3 dpx = dot(ax, ax) < dot(bx, bx) ? ax : bx, dpy = dot(ay, ay) < dot(by, by) ? ay : by;
                float h = 0;
                [unroll]
                for (int k = 0; k < KW_MAX; k++)
                    if (k < (int)_KwCount)
                    {
                        float x, y, kap, hk;
                        float3 fwd, side;
                        TrackFrame(k, p, x, y, fwd, side, kap);
                        KelvinGrad(k, x, y, fwd, side, kap, dpx, dpy, hk);
                        h += hk;
                    }
                v.xyz += _WakeUp * h * _KwDisplace;
                return v;
            }
            ENDCG
        }
    }
}
