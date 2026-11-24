// Particles over Scatterer's forward ocean (0.0878): its ocean pass writes the camera's depth buffer, so camera-facing
// puffs drawn after it are cut hard where the waves rise through them (seen from above: bands along the crests). With
// the material's ZTest Always and _WWManualZ 1 (WakeFx sets both on that ocean), the depth test is done here instead:
// against the camera's depth texture (the scene without the ocean: a hull still hides them), and the ocean itself
// (ScattererDepthCopy, as ForwardOceanHook exposes it, _WWOceanOn 1 for the frame it was drawn) only fades a puff out
// over 1 / _WWOceanFade m of depth behind its surface.
#ifndef WAKE_OCEAN_INCLUDED
#define WAKE_OCEAN_INCLUDED

sampler2D_float _WWOceanDepth;
float _WWOceanOn;
float _WWOceanFade;
float _WWManualZ;

// proj: ComputeScreenPos (w kept); eye: the fragment's eye depth (COMPUTE_EYEDEPTH); scene: the depth texture's eye
// depth there. Returns the fade (discarding behind the scene when testing by hand)
float WWOcean(float4 proj, float eye, float scene)
{
    if (_WWManualZ > 0.5 && eye > scene)
        discard;
    if (_WWOceanOn < 0.5 || _WWManualZ < 0.5)
        return 1;
    float ocean = LinearEyeDepth(tex2Dproj(_WWOceanDepth, proj).r);
    return saturate((ocean - eye) * _WWOceanFade + 0.15);
}

#endif
