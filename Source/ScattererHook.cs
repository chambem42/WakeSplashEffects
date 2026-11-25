using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WakeSplashEffects
{
    /// <summary>
    /// The wave simulation's heights put into Scatterer's ocean geometry (0.0908's deferred ocean). Scatterer computes
    /// every ocean vertex's displaced position into a texture (its VertexPositions pass) and draws its gbuffer from that
    /// texture straight after, in one command buffer of its own, so nothing outside can step in between. A Harmony
    /// transpiler on its OceanCommandBuffer.RecreateCommandBuffers adds a call right after that pass's DrawRenderer, which
    /// records two more commands: the positions copied, then lifted along the local up by the sim's height there
    /// (WaveSim.shader pass 5, reading the sim's global textures each frame). Off (a log line) without Harmony or Scatterer.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ScattererHook : MonoBehaviour
    {
        private void Start()
        {
            // (Harmony's types only touched once it is known to be loaded)
            bool harmony = AssemblyLoader.loadedAssemblies.Any(a => a.assembly.GetName().Name == "0Harmony");
            Type ocb = AssemblyLoader.loadedAssemblies.Select(a => a.assembly.GetType("Scatterer.OceanCommandBuffer")).FirstOrDefault(t => t != null);
            if (!harmony || ocb == null)
            {
                Debug.Log($"[WakeSplashEffects] ocean hooks off ({(!harmony ? "no Harmony" : "no Scatterer ocean")})");
                return;
            }
            try
            {
                // 0.0878 and before: one forward ocean pass (RecreateCommandBuffer); 0.09xx: the deferred ocean (RecreateCommandBuffers)
                if (AccessTools.Method(ocb, "RecreateCommandBuffer") != null)
                    ForwardOceanHook.Apply(ocb);
                else
                    ScattererHookPatch.Apply(ocb);
            }
            catch (Exception e)
            {
                Debug.Log("[WakeSplashEffects] ocean displacement hook off: " + e.Message);
            }
        }
    }

    internal static class ScattererHookPatch
    {
        private static FieldInfo cbField, vxField, vyField;
        private static Material displaceMat, kelvinMat;
        public static bool Patched { get; private set; }

        public static void Apply(Type ocb)
        {
            cbField = AccessTools.Field(ocb, "oceanGbufferCommandBuffer");
            vxField = AccessTools.Field(ocb, "vertCountX");
            vyField = AccessTools.Field(ocb, "vertCountY");
            MethodInfo recreate = AccessTools.Method(ocb, "RecreateCommandBuffers");
            if (cbField == null || vxField == null || vyField == null || recreate == null)
            {
                Debug.Log("[WakeSplashEffects] ocean displacement hook off: Scatterer's OceanCommandBuffer is not as expected");
                return;
            }
            new Harmony("WakeSplashEffects.ScattererOceanDisplacement").Patch(recreate, transpiler: new HarmonyMethod(typeof(ScattererHookPatch), nameof(Transpiler)));
            Debug.Log($"[WakeSplashEffects] ocean displacement hook {(Patched ? "on" : "NOT placed (no VertexPositions DrawRenderer found)")}");
        }

        // after the first CommandBuffer.DrawRenderer (the VertexPositions pass): this.AfterVertexPositions()
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo draw = AccessTools.Method(typeof(CommandBuffer), "DrawRenderer", new[] { typeof(Renderer), typeof(Material), typeof(int), typeof(int) });
            MethodInfo hook = AccessTools.Method(typeof(ScattererHookPatch), nameof(AfterVertexPositions));
            foreach (CodeInstruction c in code)
            {
                yield return c;
                if (!Patched && c.Calls(draw))
                {
                    Patched = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, hook);
                }
            }
        }

        // recorded into Scatterer's ocean gbuffer command buffer, after its vertex positions are computed and before its
        // gbuffer is drawn from them: the positions lifted by the wave sim's height
        public static void AfterVertexPositions(object oceanCommandBuffer)
        {
            try
            {
                var cb = cbField.GetValue(oceanCommandBuffer) as CommandBuffer;
                int vx = (int)vxField.GetValue(oceanCommandBuffer), vy = (int)vyField.GetValue(oceanCommandBuffer);
                if (cb == null || vx <= 0 || vy <= 0)
                    return;
                if (displaceMat == null)
                {
                    Shader sh = WakeShaders.Get("WakeSplashEffects/WaveSim");
                    if (sh == null)
                        return;
                    displaceMat = new Material(sh);
                }
                if (kelvinMat == null)
                {
                    Shader ks = WakeShaders.Get("WakeSplashEffects/OceanFoam");
                    if (ks != null && ks.passCount > 5)
                        kelvinMat = new Material(ks);
                }
                int positions = Shader.PropertyToID("vertWorldPositionsXYZOceanPositionsXId");
                int copy = Shader.PropertyToID("WakeSplashEffectsVertPositionsCopy");
                cb.GetTemporaryRT(copy, vx, vy, 0, FilterMode.Point, RenderTextureFormat.ARGBFloat);
                cb.CopyTexture(positions, copy);
                cb.Blit(copy, positions, displaceMat, 5);
                // then the Kelvin wakes' height (OceanFoam pass 5)
                if (kelvinMat != null)
                {
                    cb.CopyTexture(positions, copy);
                    cb.Blit(copy, positions, kelvinMat, 5);
                }
                cb.ReleaseTemporaryRT(copy);
                Debug.Log($"[WakeSplashEffects] ocean displacement recorded into Scatterer's gbuffer commands ({vx} x {vy} vertices)");
            }
            catch (Exception e)
            {
                Debug.Log("[WakeSplashEffects] ocean displacement not recorded: " + e.Message);
            }
        }
    }
}
