using System.Collections.Generic;
using UnityEngine;

namespace WaterWakes
{
    /// <summary>
    /// The splashes' sound. A slam's boom is stock's (FXMonger.Splash's: better than any made here): a part hitting the
    /// water from the air gets it from KSP itself; a slam KSP doesn't hear (a hull slamming down by its own motion, a
    /// surfacing sub's bow falling back) has it played here, its strength by the hit's speed and size (stock's visual
    /// hidden with hideStockSplash). Made at load from noise (no sound files): a breach's pour (water sheeting off a
    /// rising hull: a swelling, fluttering roar), and a hiss looped under ongoing spray. WakeFx reports each splash as it
    /// happens (Splash): a new one sounds once, heard late by its distance over the speed of sound; a splash kept up (a
    /// hull slamming down for seconds, a boat's spray) feeds the hiss instead. Volume: KSP's ship volume x splashVolume.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class WakeSounds : MonoBehaviour
    {
        public enum Kind { Slam, Breach, Spray }

        public static WakeSounds Instance { get; private set; }

        private const int Rate = 44100;
        private const float SoundSpeed = 343f;
        private static AudioClip[] pours;
        private static AudioClip hiss;

        private readonly List<AudioSource> pool = new List<AudioSource>();
        private AudioSource hissSrc;
        private float hissLevel, hissTarget;
        private Vector3 hissPos;

        // a splash under way, by its source (a part): when it was last reported, so a kept-up one doesn't replay
        private readonly Dictionary<object, float> lastHeard = new Dictionary<object, float>();
        private readonly HashSet<object> boomed = new HashSet<object>();   // (splashes under way that have had their sound)
        private readonly Dictionary<object, Vector2> lastShot = new Dictionary<object, Vector2>();   // (its last boom: when, how loud)
        private struct Pending
        {
            public float at, volume, pitch, minDist;
            public Vector3 pos;
            public AudioClip clip;
            public bool stock;       // stock's splash (its boom) instead, at howHard
            public float howHard;
        }
        private readonly List<Pending> pending = new List<Pending>();

        private static WakeSettings Cfg => WakeSettings.Current;

        private void Awake() => Instance = this;

        private void Start()
        {
            if (pours == null)
            {
                var rnd = new System.Random(4127);
                pours = new[] { MakePour(rnd, 3.5f), MakePour(rnd, 3f) };
                hiss = MakeHiss(rnd, 3f);
                Debug.Log("[WaterWakes] splash sounds made");
            }
            hissSrc = NewSource();
            hissSrc.clip = hiss;
            hissSrc.loop = true;
            hissSrc.volume = 0;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        private AudioSource NewSource()
        {
            var go = new GameObject("WaterWakes sound");
            go.transform.parent = transform;
            AudioSource a = go.AddComponent<AudioSource>();
            a.spatialBlend = 1;
            a.rolloffMode = AudioRolloffMode.Logarithmic;
            a.dopplerLevel = 0;
            a.playOnAwake = false;
            a.maxDistance = 5000;
            return a;
        }

        /// <summary>A splash at pos: kind; speed (m/s) into or out of the water; size (m: its waterline's length and
        /// breadth); source: what makes it (one sound per source until it has been quiet a moment); stockHeard: KSP plays
        /// its own boom for it (a part hitting the water from the air): its hiss only here.</summary>
        public void Splash(object source, Kind kind, Vector3 pos, float speed, float length, float breadth, bool stockHeard = false)
        {
            if (!Cfg.splashSounds || Cfg.splashVolume <= 0)
                return;
            float now = Time.time;
            // (by its source and kind: a slam right after a breach, a sub's bow falling back, is a new sound)
            var key = (source, kind == Kind.Breach);
            bool fresh = !lastHeard.TryGetValue(key, out float last) || now - last > 0.6f;
            lastHeard[key] = now;
            // (one sound per splash: from its start (after a quiet moment) until it is quiet again, the first time it is
            // a real hit; a hull slam starts slow (~2.5 m/s) and gets there a moment later)
            if (fresh)
                boomed.Remove(key);
            // how big: speed^2 x the area it hits (m4/s2): a 4 m capsule at 10 m/s ~1500, a sub's bow slam ~15000, a
            // small float at 3 m/s ~10
            float area = Mathf.Max(0.2f, length) * Mathf.Max(0.2f, breadth);
            float e = speed * speed * area;
            float loud = Mathf.Clamp01(0.2f + 0.27f * Mathf.Log10(Mathf.Max(e, 1) / 50f));
            // (the spray under way: the hiss, as loud as the biggest going)
            hissTarget = Mathf.Max(hissTarget, loud * (kind == Kind.Spray ? 0.6f : 0.9f));
            hissPos = pos;
            // (KSP's own boom for it: counted as this splash's sound, so its bob doesn't boom again)
            if (kind == Kind.Slam && stockHeard && !boomed.Contains(key))
            {
                boomed.Add(key);
                lastShot[source] = new Vector2(now, loud);
            }
            if (boomed.Contains(key) || kind == Kind.Spray || loud <= 0.02f)
                return;
            // (one boom per hit: after one, another only from a real hit (3 m/s or more) once 3 s have passed or clearly
            // harder; a capsule bobbing back up and settling re-wets and re-splashes, which boomed again ~1.6 s on)
            if (speed < 3f)
                return;
            if (lastShot.TryGetValue(source, out Vector2 shot) && now - shot.x < 3f && loud < shot.y + 0.15f)
                return;
            lastShot[source] = new Vector2(now, loud);
            boomed.Add(key);
            if (Cfg.debugLog)
                Debug.Log($"[WaterWakes] sound: {kind} from {source}, {speed:F1} m/s, {length:F1} x {breadth:F1} m, loudness {loud:F2}");
            float size = Mathf.Sqrt(area);
            Vector3 cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera.transform.position : pos;
            pending.Add(new Pending
            {
                at = now + Vector3.Distance(cam, pos) / SoundSpeed,
                // (a slam: stock's boom, ~1 for the capsule's 10 m/s hit, up to 2 for a sub's bow)
                stock = kind == Kind.Slam, howHard = Mathf.Clamp(2.2f * loud, 0.3f, 2f),
                pos = pos, clip = kind == Kind.Breach ? pours[Random.Range(0, pours.Length)] : null,
                volume = loud, minDist = Mathf.Clamp(3 * size, 5, 200),
                // (a bigger splash deeper: ~1 at 4 m, 0.7 at 30 m)
                pitch = Mathf.Clamp(Mathf.Pow(4f / Mathf.Max(size, 0.5f), 0.17f), 0.55f, 1.4f) * Random.Range(0.92f, 1.08f),
            });
        }

        private void LateUpdate()
        {
            // (x3: level with stock's splash sound at KSP's ship volume, ~-30 dB RMS for the capsule's)
            float vol = 3f * GameSettings.SHIP_VOLUME * Cfg.splashVolume;
            float now = Time.time;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Pending p = pending[i];
                if (now < p.at)
                    continue;
                pending.RemoveAt(i);
                if (p.stock)
                {
                    FXMonger.Splash(p.pos, p.howHard * Cfg.splashVolume);
                    continue;
                }
                AudioSource a = pool.Find(s => !s.isPlaying) ?? (pool.Count < 12 ? NewSource() : null);
                if (a == null)
                    continue;
                if (!pool.Contains(a))
                    pool.Add(a);
                a.transform.position = p.pos;
                a.minDistance = p.minDist;
                a.pitch = p.pitch;
                a.volume = p.volume * vol;
                a.clip = p.clip;
                a.Play();
            }
            // the hiss: up fast, down over ~1.5 s once the spray stops
            hissLevel = hissTarget > hissLevel ? Mathf.Lerp(hissLevel, hissTarget, Mathf.Clamp01(Time.deltaTime * 8)) : Mathf.MoveTowards(hissLevel, hissTarget, Time.deltaTime * 0.6f);
            hissTarget = 0;
            if (hissSrc != null)
            {
                hissSrc.transform.position = hissPos;
                hissSrc.minDistance = 30;
                hissSrc.volume = hissLevel * vol;
                if (hissLevel > 0.005f && !hissSrc.isPlaying)
                    hissSrc.Play();
                else if (hissLevel <= 0.005f && hissSrc.isPlaying)
                    hissSrc.Stop();
            }
            if (lastHeard.Count > 64)
            {
                var old = new List<object>();
                foreach (var kv in lastHeard)
                    if (now - kv.Value > 5)
                        old.Add(kv.Key);
                foreach (object k in old)
                    lastHeard.Remove(k);
            }
        }

        // ---- the clips, from noise ----

        private static float White(System.Random r) => (float)(r.NextDouble() * 2 - 1);

        // a pour: band-passed noise (~300-2500 Hz) swelling over ~0.4 s, fluttering (the sheets breaking up), dying over
        // the rest
        private static AudioClip MakePour(System.Random r, float len)
        {
            int n = (int)(len * Rate);
            var d = new float[n];
            float lo = 0, hi = 0, flutter = 0, ft = 0;
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / Rate;
                float w = White(r);
                hi += 0.30f * (w - hi);       // (~2.5 kHz low-pass)
                lo += 0.042f * (hi - lo);     // (~300 Hz: subtracted, the band left)
                if (i % 2205 == 0)
                    ft = (float)r.NextDouble();
                flutter += 0.0008f * (ft - flutter);
                float env = Mathf.Clamp01(t / 0.4f) * Mathf.Exp(-Mathf.Max(0, t - 0.5f) / 1.1f);
                d[i] = (hi - lo) * env * (0.6f + 0.8f * flutter);
            }
            Fade(d, 0.01f, 0.5f);
            Normalise(d, 0.8f);
            AudioClip c = AudioClip.Create("WaterWakes pour", n, 1, Rate, false);
            c.SetData(d, 0);
            return c;
        }

        // a hiss: high-passed noise, gently wavering, its ends crossfaded so it loops without a seam
        private static AudioClip MakeHiss(System.Random r, float len)
        {
            int n = (int)(len * Rate), x = Rate / 4;
            var d = new float[n + x];
            float lp = 0, wav = 0.5f, wt = 0.5f;
            for (int i = 0; i < d.Length; i++)
            {
                float w = White(r);
                lp += 0.15f * (w - lp);
                if (i % 4410 == 0)
                    wt = 0.6f + 0.4f * (float)r.NextDouble();
                wav += 0.0005f * (wt - wav);
                d[i] = (w - lp) * wav;
            }
            var o = new float[n];
            for (int i = 0; i < n; i++)
                o[i] = i < x ? Mathf.Lerp(d[n + i], d[i], (float)i / x) : d[i];
            Normalise(o, 0.5f);
            AudioClip c = AudioClip.Create("WaterWakes hiss", n, 1, Rate, false);
            c.SetData(o, 0);
            return c;
        }

        private static void Fade(float[] d, float inSec, float outSec)
        {
            int a = (int)(inSec * Rate), b = (int)(outSec * Rate);
            for (int i = 0; i < a && i < d.Length; i++)
                d[i] *= (float)i / a;
            for (int i = 0; i < b && i < d.Length; i++)
                d[d.Length - 1 - i] *= (float)i / b;
        }

        private static void Normalise(float[] d, float peak)
        {
            float m = 0;
            foreach (float v in d)
                m = Mathf.Max(m, Mathf.Abs(v));
            if (m <= 0)
                return;
            float k = peak / m;
            for (int i = 0; i < d.Length; i++)
                d[i] *= k;
        }
    }
}
