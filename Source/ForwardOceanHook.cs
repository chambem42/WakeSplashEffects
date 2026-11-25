using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WakeSplashEffects
{
    /// <summary>
    /// Foam and wake waves on Scatterer's forward ocean (0.0878, the CKAN release, and before), which draws the whole sea
    /// in one pass of its own compiled shader: no gbuffer to paint into, and its vertices come from a screen grid and its
    /// FFT textures alone. What it does give: the ocean pass writes its depth into a second target (depthCopyRenderTexture,
    /// global ScattererDepthCopy), so straight after it every ocean pixel's displaced position is known, exactly as the
    /// deferred ocean's oceanGbufferDepth gives it. A Harmony transpiler on OceanCommandBuffer.RecreateCommandBuffer adds
    /// two calls into its command buffer: after its first Blit (the depth copy initialised with the scene's depth, before
    /// the ocean) that depth is copied to a temporary of ours; after its DrawRenderer (the ocean) one fullscreen pass
    /// (OceanFoam pass 6) over the camera target where the depth changed (the ocean's pixels): our foam laid on, lit as
    /// the scene, and the wake waves' slopes as the light they would change. Both before Scatterer copies the screen for
    /// its scattering, so the atmosphere is put over them as over the sea. The material is shared with OceanFoamPainter,
    /// which sets its per-camera values each frame (the command buffer is recorded once and reads them when it runs).
    /// </summary>
    internal static class ForwardOceanHook
    {
        public const int Pass = 6;
        private static FieldInfo cbField, depthField;
        private static readonly int PreDepthId = Shader.PropertyToID("_WWPreOceanDepth");
        private static readonly int OceanDepthId = Shader.PropertyToID("_WWOceanDepth");
        private static Material mat;
        public static bool Patched { get; private set; }
        public static int Recorded { get; private set; }

        /// <summary>The forward pass's material (OceanFoam), made on first use; null without the shader.</summary>
        public static Material Mat
        {
            get
            {
                if (mat == null)
                {
                    Shader sh = WakeShaders.Get("WakeSplashEffects/OceanFoam");
                    if (sh != null && sh.passCount > Pass)
                        mat = new Material(sh) { name = "WakeSplashEffects forward ocean" };
                }
                return mat;
            }
        }

        public static void Apply(Type ocb)
        {
            cbField = AccessTools.Field(ocb, "rendererCommandBuffer");
            depthField = AccessTools.Field(ocb, "depthCopyRenderTexture");
            MethodInfo recreate = AccessTools.Method(ocb, "RecreateCommandBuffer");
            if (cbField == null || depthField == null || recreate == null)
            {
                Debug.Log("[WakeSplashEffects] forward ocean hook off: Scatterer's OceanCommandBuffer is not as expected");
                return;
            }
            new Harmony("WakeSplashEffects.ScattererForwardOcean").Patch(recreate, transpiler: new HarmonyMethod(typeof(ForwardOceanHook), nameof(Transpiler)));
            Debug.Log($"[WakeSplashEffects] forward ocean hook {(Patched ? "on" : "NOT placed (its Blit / DrawRenderer not found)")}");
        }

        // after the first CommandBuffer.Blit (the depth copy initialised): this.BeforeOcean(); after the DrawRenderer (the
        // ocean): this.AfterOcean()
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo draw = AccessTools.Method(typeof(CommandBuffer), "DrawRenderer", new[] { typeof(Renderer), typeof(Material), typeof(int), typeof(int) });
            MethodInfo before = AccessTools.Method(typeof(ForwardOceanHook), nameof(BeforeOcean));
            MethodInfo after = AccessTools.Method(typeof(ForwardOceanHook), nameof(AfterOcean));
            bool blitSeen = false, drawSeen = false;
            foreach (CodeInstruction c in code)
            {
                yield return c;
                if (!blitSeen && c.opcode == OpCodes.Callvirt && c.operand is MethodInfo m && m.DeclaringType == typeof(CommandBuffer) && m.Name == "Blit")
                {
                    blitSeen = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, before);
                }
                else if (blitSeen && !drawSeen && c.Calls(draw))
                {
                    drawSeen = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, after);
                }
            }
            Patched = blitSeen && drawSeen;
        }

        // the scene's depth, before the ocean is drawn into it
        public static void BeforeOcean(object oceanCommandBuffer)
        {
            try
            {
                var cb = cbField.GetValue(oceanCommandBuffer) as CommandBuffer;
                var depth = depthField.GetValue(oceanCommandBuffer) as RenderTexture;
                if (cb == null || depth == null || Mat == null)
                    return;
                cb.GetTemporaryRT(PreDepthId, depth.width, depth.height, 0, FilterMode.Point, RenderTextureFormat.RFloat);
                cb.Blit(depth, PreDepthId);
            }
            catch (Exception e)
            {
                Debug.Log("[WakeSplashEffects] forward ocean: depth copy not recorded: " + e.Message);
            }
        }

        // the ocean drawn: our pass over it (the target set again after: Scatterer's next commands draw to the camera's)
        public static void AfterOcean(object oceanCommandBuffer)
        {
            try
            {
                var cb = cbField.GetValue(oceanCommandBuffer) as CommandBuffer;
                var depth = depthField.GetValue(oceanCommandBuffer) as RenderTexture;
                if (cb == null || depth == null || Mat == null)
                    return;
                cb.SetGlobalTexture(OceanDepthId, depth);
                cb.SetGlobalVector("_WWOceanTexel", new Vector4(1f / depth.width, 1f / depth.height, depth.width, depth.height));
                cb.Blit((Texture)null, BuiltinRenderTextureType.CameraTarget, Mat, Pass);
                // (for the particles drawn after: the ocean's depth there this frame, to fade by (WakeOcean.cginc); the
                // painter clears _WWOceanOn before each camera renders)
                cb.SetGlobalFloat("_WWOceanOn", 1);
                cb.SetGlobalFloat("_WWOceanFade", 0.7f);
                cb.ReleaseTemporaryRT(PreDepthId);
                Recorded++;
                Debug.Log($"[WakeSplashEffects] forward ocean foam / waves recorded into Scatterer's ocean commands ({depth.width} x {depth.height})");
            }
            catch (Exception e)
            {
                Debug.Log("[WakeSplashEffects] forward ocean: pass not recorded: " + e.Message);
            }
        }
    }
}
