using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace WakeSplashEffects
{
    /// <summary>
    /// Scatterer's deferred ocean (0.09xx) lights its water from the flight reflection probe when the camera is near the
    /// surface and the probe is on (KSP's Reflection Refresh not Off): reflectionProbeOceanAmbient = 1 on its material. Its
    /// shading pass, drawn from a command buffer, gets nothing from the probe, so the water seen from above is near black
    /// whatever its upwelling colour. With fixOceanAmbient on, that's set to 0 on each ocean's material (checked twice a
    /// second, as Scatterer makes a new one on a rebuild), so the water is lit by Scatterer's own sky irradiance; off, it's
    /// put back as Scatterer sets it.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class OceanAmbientFix : MonoBehaviour
    {
        private static readonly int AmbientId = Shader.PropertyToID("reflectionProbeOceanAmbient");
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private PropertyInfo instance;
        private readonly Dictionary<Material, int> set = new Dictionary<Material, int>();
        private float next;
        private bool failed;

        private void Start()
        {
            Type scat = AssemblyLoader.loadedAssemblies.Select(a => a.assembly.GetType("Scatterer.Scatterer")).FirstOrDefault(t => t != null);
            instance = scat?.GetProperty("Instance", Any);
            if (instance == null)
                enabled = false;
        }

        private static object Member(object o, string name)
        {
            if (o == null)
                return null;
            Type t = o.GetType();
            FieldInfo f = t.GetField(name, Any);
            if (f != null)
                return f.GetValue(o);
            return t.GetProperty(name, Any)?.GetValue(o, null);
        }

        private IEnumerable<Material> OceanMaterials()
        {
            object bodies = Member(Member(instance.GetValue(null, null), "planetsConfigsReader"), "scattererCelestialBodies");
            if (!(bodies is IEnumerable list))
                yield break;
            foreach (object b in list)
            {
                object manager = Member(b, "prolandManager");
                if (manager == null)
                    continue;
                object node = manager.GetType().GetMethod("GetOceanNode", Any)?.Invoke(manager, null);
                if (node is UnityEngine.Object uo && uo != null && Member(node, "m_oceanMaterial") is Material m && m != null)
                    yield return m;
            }
        }

        private void Update()
        {
            if (failed || Time.unscaledTime < next)
                return;
            next = Time.unscaledTime + 0.5f;
            try
            {
                bool fix = WakeSettings.Current.enabled && WakeSettings.Current.fixOceanAmbient;
                // (as Scatterer sets it: from the flight probe, if there is one)
                ReflectionProbe probe = FlightCamera.fetch != null && FlightCamera.fetch.reflectionProbe != null ? FlightCamera.fetch.reflectionProbe.probeComponent : null;
                int want = fix ? 0 : probe != null && probe.enabled ? 1 : 0;
                foreach (Material m in OceanMaterials())
                {
                    if (!fix && !set.ContainsKey(m))
                        continue;   // (never touched: left as Scatterer made it)
                    if (set.TryGetValue(m, out int was) && was == want && m.GetInt(AmbientId) == want)
                        continue;
                    m.SetInt(AmbientId, want);
                    set[m] = want;
                    if (WakeSettings.Current.debugLog)
                        Debug.Log($"[WakeSplashEffects] Scatterer ocean ambient from the reflection probe {(want == 1 ? "on" : "off")}");
                }
                foreach (Material m in set.Keys.Where(k => k == null).ToList())
                    set.Remove(m);
            }
            catch (Exception e)
            {
                failed = true;
                Debug.LogWarning($"[WakeSplashEffects] Scatterer ocean ambient fix off: {e.Message}");
            }
        }
    }
}
