// A foam patch's look at its age, shared by the foam particles (Foam.shader) and the foam map (FoamMap.shader).
// The texture: a 2x2 atlas of patch variants, each R = fbm (the foam's body), G = bubble-cell walls (Worley F2 - F1),
// B = the patch's ragged round mask, A = a grey mottle. The particle's custom vertex streams give TEXCOORD0 =
// (uv, age 0..1, stable random 0..1): the random picks the variant, the age erodes it continuously.
#ifndef WAKE_FOAM_INCLUDED
#define WAKE_FOAM_INCLUDED

float4 SampleFoam(sampler2D tex, float2 uv, float rnd)
{
    float v = floor(saturate(rnd) * 3.999);
    float2 o = float2(fmod(v, 2), floor(v * 0.5));
    return tex2D(tex, (uv + o) * 0.5);
}

// dense churned white at first; holes open in its body as it ages (the fbm thresholded higher and higher, smoothly)
// until only the thin veins between bubble cells are left, fainter. crisp 0..1: the body's edge (and the patch's)
// from soft to sharp, and it fading less (a splash's fresh white: hard edged, eroding rather than thinning)
float FoamAmountCrisp(float4 t, float age, float crisp)
{
    float sg = pow(saturate(age), lerp(0.6, 1.3, crisp));
    float th = lerp(lerp(0.18, 0.24, crisp), lerp(0.78, 1.02, crisp), sg);
    float w = lerp(0.14, 0.025, crisp);
    // (crisp: dense only well inside, mottled; thin towards its holes and edge, the water showing through)
    float dense = lerp(0.3, 1, saturate((t.r - th) * 2.5)) * lerp(0.65, 1, t.a);
    float body = smoothstep(th - w, th + w, t.r) * lerp(lerp(1, 0.3, sg), dense, crisp);
    float veins = t.g * lerp(0.3, 0.85, sg) * smoothstep(0.15, 0.5, t.r + 0.25 * (1 - sg)) * saturate(1 - crisp * 3);
    float mask = lerp(t.b, smoothstep(0.3, 0.38, t.b), crisp);
    return mask * saturate(max(body, veins)) * lerp(lerp(1, 0.8, sg), 1, crisp);
}

float FoamAmount(float4 t, float age)
{
    return FoamAmountCrisp(t, age, 0);
}

#endif
