using System.Collections.Generic;
using UnityEngine;

namespace WaterWakes
{
    /// <summary>
    ///     The particles the mod has asked each of its particle systems for this game session, by kind (the system's name
    ///     less "WaterWakes ": "foam", "skirt", "splash mist", ...), for tests and diagnostics: take a Snapshot before
    ///     and after and compare. Counted as asked for: a system already full (its maxParticles) drops the rest.
    /// </summary>
    public static class WakeStats
    {
        private static readonly Dictionary<ParticleSystem, string> kinds = new Dictionary<ParticleSystem, string>();
        private static readonly Dictionary<string, long> emitted = new Dictionary<string, long>();

        internal static void Register(ParticleSystem ps, string name)
        {
            // (a scene's systems are destroyed with it: forget them)
            var gone = new List<ParticleSystem>();
            foreach (ParticleSystem k in kinds.Keys)
                if (k == null)
                    gone.Add(k);
            foreach (ParticleSystem k in gone)
                kinds.Remove(k);
            string kind = name.StartsWith("WaterWakes ") ? name.Substring("WaterWakes ".Length) : name;
            kinds[ps] = kind;
            if (!emitted.ContainsKey(kind))
                emitted[kind] = 0;
        }

        /// <summary>ps.Emit(ep, count), counted.</summary>
        internal static void Emit(ParticleSystem ps, ParticleSystem.EmitParams ep, int count)
        {
            ps.Emit(ep, count);
            if (kinds.TryGetValue(ps, out string kind))
                emitted[kind] += count;
        }

        /// <summary>Each kind's count so far (a copy), every kind made this session, those yet to emit at 0.</summary>
        public static Dictionary<string, long> Snapshot() => new Dictionary<string, long>(emitted);
    }
}
