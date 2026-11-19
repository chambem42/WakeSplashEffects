using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Random = UnityEngine.Random;

namespace WaterWakes
{
    /// <summary>
    /// Bow spray, mist and a foam trail for every loaded craft moving through the ocean. Each frame each craft's parts are
    /// cut at the water (their colliders' boxes against the waterline: vessel.waterOffset + PartBuoyancy.waterLevel, which
    /// Scatterer sets to its wave height under the part); the cuts are grouped into hulls (overlapping across the flow,
    /// touching along it) and each hull emits from its wet bow corners and its stern. The strength follows the hull's load
    /// on the water: (draft / draft at rest)^loadExponent, so a hull rising onto the step and lifting off fades out.
    /// World-space particles, registered with FloatingOrigin so origin and Krakensbane shifts move them.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class WakeFx : MonoBehaviour
    {
        private struct Box
        {
            public Transform t;
            public Vector3 c, e;     // local centre, half extents
            public Vector3[] verts;  // a mesh collider's mesh (readable): its waterline cut from its triangles, not its box
            public int[] tris;
        }

        private const int Bins = 16;

        private class Cut
        {
            public float fMin, fMax, sMin, sMax, draft, wl;
            public float top;        // its highest point above its water level (m)
            public float w = 1;      // 1 at the surface, fading to 0 as a part sinks wholly under (submergedFade)
            public int group;
            // its waterline's outline: from pf0 (aft) to pf1 (fore), its lowest and highest s in each of Bins stations
            public float pf0, pf1;
            public readonly float[] lo = new float[Bins], hi = new float[Bins];
            public readonly float[] dep = new float[Bins];   // each station's draft: its deepest point under the water (m)

            public float Edge(float x, int side)
            {
                int i = pf1 > pf0 ? Mathf.Clamp((int)((x - pf0) / (pf1 - pf0) * Bins), 0, Bins - 1) : 0;
                return side < 0 ? lo[i] : hi[i];
            }

            public bool Spans(float x, float margin) => x >= pf0 - margin && x <= pf1 + margin;

            // its waterplane's area (0 when wholly under)
            public float Area()
            {
                if (!line)
                    return 0;
                float a = 0;
                for (int i = 0; i < Bins; i++)
                    a += Mathf.Max(0, hi[i] - lo[i]);
                return a * (pf1 - pf0) / Bins;
            }

            public bool Inside(float x, float y, float margin) => Spans(x, -margin) && y > Edge(x, -1) + margin && y < Edge(x, 1) - margin;

            public bool line;        // it has a waterline (else wholly under, its outline its footprint)
            // the whole part seen from above (it hides the water under it): from rf0 to rf1, its lowest and highest s per station
            public float rf0, rf1;
            public readonly float[] rlo = new float[Bins], rhi = new float[Bins];

            public float RoofEdge(float x, int side)
            {
                int i = rf1 > rf0 ? Mathf.Clamp((int)((x - rf0) / (rf1 - rf0) * Bins), 0, Bins - 1) : 0;
                return side < 0 ? rlo[i] : rhi[i];
            }

            public bool UnderRoof(float x, float y, float margin)
            {
                if (x < rf0 - 0.05f || x > rf1 + 0.05f)   // (no margin along it: parts butt end to end)
                    return false;
                int i = rf1 > rf0 ? Mathf.Clamp((int)((x - rf0) / (rf1 - rf0) * Bins), 0, Bins - 1) : 0;
                return y > rlo[i] + margin && y < rhi[i] - margin;
            }
        }

        private class Hull
        {
            public float fMin = float.MaxValue, fMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue, draft, wl, w;
            public bool line;        // some part of it has a waterline
            public readonly List<Bow> bows = new List<Bow>();   // where its bow waves start: its outline's exposed front corners
            public int n;
            public float area;       // its waterplane's area
            public readonly List<Cut> parts = new List<Cut>();   // its parts at the waterline

            // the waterline's aft edge at y across the flow: the rearmost station of any of its parts' outlines there
            // (in a skid the transom and the trailing side), NaN in a gap between them
            public float AftEdge(float y)
            {
                float x = float.NaN;
                foreach (Cut c in parts)
                {
                    float span = (c.pf1 - c.pf0) / Bins;
                    for (int i = 0; i < Bins; i++)
                        if (y >= c.lo[i] && y <= c.hi[i])
                        {
                            float xi = c.pf0 + i * span;
                            if (float.IsNaN(x) || xi < x)
                                x = xi;
                            break;
                        }
                }
                return x;
            }
        }

        private class CraftState
        {
            public float restDraft;   // the deepest hull's draft afloat at rest (or the deepest seen, until it has rested)
            public float simW = 1;    // how much of its waves the wave sim makes (SimShare)
            public Vector3d prevCoM;  // its CoM last frame, relative to its body (the footprints' motion over the frame)
            public int prevFrame = -10;
            public float load = 1;    // smoothed draft / restDraft
            public bool rested;       // has floated at rest: restDraft is then only learnt at rest
            public float restArea;    // the wet parts' waterplane area afloat at rest (as restDraft)
            public float area;        // smoothed waterplane area
            public Vector3 waveDir;   // the Kelvin wake's heading (smoothed)
            public float turn;        // its turn rate (rad/s, smoothed)
            public float trail;       // how far it has run (m): the wake's length
            public float waveH;       // the wake's wave height at the hull (m, smoothed)
            // where its bow has been, oldest first: latitude, longitude, altitude (the floating origin moves the world)
            public readonly List<Vector3d> track = new List<Vector3d>();
            public readonly Vector4[] trackWorld = new Vector4[TrackMax];
            // the wake's memory: the speed its pattern holds (following the craft's within ~a wave period), and the
            // pattern left running on when the craft slows faster than that (ghostT >= 0: its age, s)
            public float memV;
            public float ghostT = -1, ghostV, ghostH, ghostL, ghostBeam, ghostTrans;
            public Vector3 ghostDir;
            public Vector3d ghostBow;  // (latitude, longitude, altitude) where its bow was when it was left
            public CraftState ghost;   // (its track and trail)
        }

        /// <summary>The most points of a Kelvin wake's track (OceanFoam.shader's KW_TRACK).</summary>
        public const int TrackMax = 48;

        /// <summary>A craft making a Kelvin wake this frame (OceanFoamPainter draws its waves into Scatterer's ocean).</summary>
        public struct WaveSource
        {
            public Vector3 pos;       // its bow at the sea (world)
            public Vector3 dir;       // its heading (level, unit)
            public float speed;       // m/s
            public float height;      // its waves' height at the hull (m)
            public float length;      // its wetted length (m)
            public float trail;       // how far behind the waves reach (m)
            public float transverse;  // the transverse waves' share (they die away as it goes faster for its length)
            public float beam;        // its hulls' width (m): the calm slick behind it
            public Vector4[] track;   // its track: the bow now, then where it was (world); w: the curvature there (1/m, + toward +side)
            public int trackCount;
            public float front;       // (a pattern its craft has left: no waves nearer the bow than this, m; 0: none)
            public float simShare;    // how much of its waves the wave sim makes (the analytic pattern the rest)
        }

        public readonly List<WaveSource> WaveSources = new List<WaveSource>();

        // an entry's ring of ripples on the ocean this frame (OceanFoamPainter draws them into Scatterer's normals and foam)
        public struct RippleSource
        {
            public Vector3 pos;       // its centre at the sea (world)
            public float radius;      // its leading crest's (m)
            public float amp;         // wave height (m)
            public float wavelength;  // m
            public float length;      // the packet behind the crest (m)
            public float foam;        // whitecap on the leading crest, 0..1
        }

        public readonly List<RippleSource> RippleSources = new List<RippleSource>();

        // a stretch of a wetted part's waterline for the wave simulation (WaveSim): between two of its stations, its
        // width and depth going from the aft one's to the fore one's (a bow tapers to its stem)
        public struct SimBlob
        {
            public Vector3 pos;       // its centre at the sea (world)
            public Vector3 fwd;       // its length's direction (level)
            public float halfL;       // (the stations at 0.6 of it either side of the centre: the stretches overlap)
            public float halfW, depth;         // at its aft station
            public float halfWFore, depthFore; // at its fore one
            public float weight;      // how much the sim makes this craft's waves (1 - SimKelvinBlend): on its push, not its depth
            public Vector3 moved;     // how far its craft moved since the last frame (m; where it was: pos - moved)
            public float kWave;       // g / V^2: the wavenumber its moving waves have (their depth (1 - e^-kd)/k of its own)
            public float still;       // 1 at rest (an obstacle to the sim's waves), 0 from 2.5 m/s (a moving pressure source)
        }

        public readonly List<SimBlob> SimBlobs = new List<SimBlob>();
        // the fastest a still hull's surface moves up or down this frame (m/s): its heave, and its pitch and roll x half
        // its length; WaveSim bounds its push by it (a hull's footprint deepens no faster than it moves)
        public float StillHeaveRate { get; private set; }
        private readonly float[] stX = new float[Bins + 4], stW = new float[Bins + 4], stM = new float[Bins + 4], stD = new float[Bins + 4];

        /// <summary>0 below 0.6 x simKelvinAbove (the sim makes this craft's waves), 1 from simKelvinAbove (the analytic
        /// Kelvin wake does), smooth between.</summary>
        // the sim's share of a craft's waves: handed to the analytic wake as it speeds up (SimKelvinBlend, within ~0.5 s),
        // taken back only once it is slow (under 0.3 x simKelvinAbove: 3 m/s), over 2 s. Taken back as it slowed through
        // 6 - 10 m/s, the sim began from flat water with the hull's hollow held 3 m deep under it at once: rings, then
        // short chop left round a ship stopped as if it shook. Slow, its hollow is centimetres (V^2 / g)
        private static float SimShare(CraftState st, float speed, float dt, WakeSettings cfg)
        {
            float target = 1 - SimKelvinBlend(speed, cfg);
            if (target < st.simW)
                st.simW = Mathf.Max(target, Mathf.Lerp(st.simW, target, Mathf.Clamp01(dt / 0.5f)) - 1e-3f);
            else if (speed < 0.3f * Mathf.Max(0.1f, cfg.simKelvinAbove))
                st.simW = Mathf.MoveTowards(st.simW, target, dt / 2f);
            return st.simW;
        }

        public static float SimKelvinBlend(float speed, WakeSettings cfg)
        {
            float k = Mathf.Max(0.1f, cfg.simKelvinAbove);
            return Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.6f * k, k, speed));
        }

        /// <summary>The foam particles use WaterWakes/Foam (age-eroded patches; the foam map reads the same streams).</summary>
        public bool CustomFoam { get; private set; }

        // a part's submerged volume from frame to frame: its rate over its waterline area is the speed it enters or leaves at
        private class Wetting
        {
            public float vol, rate;
            public bool seen;
            // its entry (a slam): dry for a while, then in; its speed into the water and its waterline's size over the
            // first moments, where it went in and how high it was then
            public float dry, enterT = -1, U, LB, sealAt = float.MaxValue;
            public float un;         // its speed into the water now (m/s)
            // its first 0.25 s in: the waterline's largest size so far (L x B, m2), and this frame's growth of it and the
            // speed its equivalent radius grew at (the spray skirt's)
            public float lbSkirt, skirtArea, skirtSpread, spreadMax;
            public Vector2 tilt;     // where it first touched, off its outline's middle (f, s; over its half-width, at most 1): a tilted body's low side
            public float flat;       // how flat its wetted bottom is (its submerged vertices all at one depth: a flat face square on), at most so far
            public bool impacted;
            public double lat, lon, alt0;
        }

        // a slam into the water, kept after the part has gone on: the cavity behind it closes 2.4 sqrt(R / g) later
        // (the deep seal, at any speed: Glasheen & McMahon 1996, Bergmann et al. 2009), throwing a Worthington jet up
        // where it went in
        private class Impact
        {
            public CelestialBody body;
            public double lat, lon, alt0, travel;
            public Part part;
            public float U, D, t, jetAt, jetLeft, jetDur, jetSpeed, strength;
            public bool jetted;
        }

        private readonly List<Impact> impacts = new List<Impact>();

        // a ring of raised water and foam spreading out over the surface from where a body went in (the SPH runs: r ~
        // D (0.5 + 1.15 sqrt(t / sqrt(D / g))), the water raised up to ~0.25 D, fading over ~2.5 s), from the entry, and a
        // weaker one when the jet falls back (delay)
        private class Ring
        {
            public CelestialBody body;
            public double lat, lon;
            public float D, U, t, delay, dur, amp;
        }

        private readonly List<Ring> rings = new List<Ring>();

        // a front corner of a wet part that no part ahead of it shields: a bow wave (or a shoulder wave) starts there
        private struct Bow
        {
            public float x, y, len, beam, w;
            public int side;
            public Cut cut;          // its part's waterline: the bow wave runs along its entrance
        }

        private struct Splash
        {
            public float fc, sc, L, B, wl, speed;
            public bool domed;       // its crown has domed over (surface seal): no more thrown up
            public bool outOf;       // leaving the water (a breach's plume); else entering (a slam)
            public Vector3 vel;      // the part's velocity, carried into the spray
            public Cut own;          // its part's waterline (its splash is not hidden by it)
            public bool entry;       // an entry from the air under way: its speed then (eU), its size (eD), how long ago (since), the share of eU it still has (keep)
            public float eU, eD, since, keep;
            public float skirtArea, skirtSpread;   // the waterline's growth this frame (m2) and its spreading speed (m/s): the spray skirt
            public float spreadMax;  // ... its fastest so far
            public Vector2 tilt;     // its entry's low side (Wetting.tilt)
            public float flat;       // ... and how flat it hit (Wetting.flat)
        }

        private static WakeSettings Cfg => WakeSettings.Current;

        private ParticleSystem foam, spray, sheet, mist, skirt;
        private ParticleSystem haze;   // a hard hit's glowing spray haze (additive)
        private ParticleSystem.Particle[] hazeBuf;

        // the haze: its alpha each frame, kept out of the clear zones (as far as each puff reaches)
        private void Haze(WakeSettings cfg)
        {
            int n = haze != null ? haze.particleCount : 0;
            if (n == 0)
                return;
            if (hazeBuf == null || hazeBuf.Length < haze.main.maxParticles)
                hazeBuf = new ParticleSystem.Particle[haze.main.maxParticles];
            n = haze.GetParticles(hazeBuf, n);
            for (int i = 0; i < n; i++)
            {
                float a = cfg.hazeAlpha * (holes.Count > 0 ? HoleFactor(hazeBuf[i].position, 1.4f, 2.6f, 0.55f * hazeBuf[i].GetCurrentSize(haze), true) : 1);
                Color32 col = hazeBuf[i].startColor;
                col.a = (byte)(Mathf.Clamp01(a) * 255);
                hazeBuf[i].startColor = col;
            }
            haze.SetParticles(hazeBuf, n);
        }
        // the splash's foam (crisp radial streaks; never painted into the ocean), in a frame of its own (z up, y SplashRef)
        private ParticleSystem splashFoam;
        // the skirt's and the sheet's look: faded soft blobs (the legacy shader) or eroded crisp ones (WaterWakes/Spray)
        private Material skirtSoft, sheetSoft, skirtCrisp, sheetCrisp;
        private int crispApplied = -1;
        private static readonly List<ParticleSystemVertexStream> SoftStreams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
        };
        private static readonly List<ParticleSystemVertexStream> AgedStreams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
            ParticleSystemVertexStream.AgePercent, ParticleSystemVertexStream.StableRandomX,
        };

        private void ApplySprayLook(bool crisp)
        {
            int c = crisp && skirtCrisp != null ? 1 : 0;
            if (c == crispApplied)
                return;
            crispApplied = c;
            foreach ((ParticleSystem ps, Material soft, Material sharp) in new[] { (skirt, skirtSoft, skirtCrisp), (sheet, sheetSoft, sheetCrisp) })
            {
                var r = ps.GetComponent<ParticleSystemRenderer>();
                r.sharedMaterial = c == 1 ? sharp : soft;
                r.SetActiveVertexStreams(c == 1 ? AgedStreams : SoftStreams);
            }
            Debug.Log($"[WaterWakes] spray look {(c == 1 ? "crisp" : "soft")}");
        }

        // the splash foam frame's y: the body's axis on the water's plane (fixed while the frame stays put)
        private static Vector3 SplashRef(Vector3 up)
        {
            Vector3 r = Vector3.ProjectOnPlane(FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.transform.up : Vector3.up, up);
            return r.sqrMagnitude > 1e-4f ? r.normalized : Vector3.ProjectOnPlane(Vector3.forward, up).normalized;
        }

        // a foam patch's rotation (deg, in the splash foam frame) laying its long axis along dir
        private static float SplashRot(Vector3 up, Vector3 dir)
        {
            Vector3 y = SplashRef(up), x = Vector3.Cross(up, y);
            return Mathf.Atan2(-Vector3.Dot(dir, x), Vector3.Dot(dir, y)) * Mathf.Rad2Deg * SplashRotSign;
        }
        private const float SplashRotSign = -1;

        // the skirt's foam, laid when its sheet comes down there (Time.time), world position
        private struct LateFoam
        {
            public float at, size, life, alpha, light, aspect, rot;
            public Vector3 pos, vel, up;
        }
        private readonly List<LateFoam> lateFoam = new List<LateFoam>();

        private void LayLateFoam()
        {
            for (int i = lateFoam.Count - 1; i >= 0; i--)
            {
                LateFoam lf = lateFoam[i];
                if (Time.time < lf.at)
                    continue;
                // (inside a clear zone, as far as the patch would reach: it waits, laid as the zone closes)
                if (holes.Count > 0 && HoleFactor(lf.pos, 1.3f, 2.4f, 0.35f * lf.size) < 0.98f)
                {
                    lf.at = Time.time + Random.Range(0.1f, 0.3f);
                    lateFoam[i] = lf;
                    continue;
                }
                if (Time.time - lf.at < 0.5f)
                {
                    if (splashFoam != null)
                        splashFoam.transform.rotation = Quaternion.LookRotation(lf.up, SplashRef(lf.up));
                    EmitFoam(lf.pos, lf.vel, lf.size, lf.life, lf.alpha, lf.light, lf.aspect, lf.rot, true);
                }
                lateFoam.RemoveAt(i);
            }
        }

        public static WakeFx Instance { get; private set; }
        /// <summary>The foam particles (OceanFoamPainter renders them into its foam map, on a layer the cameras skip).</summary>
        public ParticleSystem Foam => foam;
        public ParticleSystem SplashFoam => splashFoam;
        public ParticleSystem UpwellFoam => upwell;
        private Transform foamFrame;
        private readonly Dictionary<Part, Box[]> boxes = new Dictionary<Part, Box[]>();
        private readonly Dictionary<Guid, CraftState> crafts = new Dictionary<Guid, CraftState>();
        private readonly List<Cut> cuts = new List<Cut>();
        // each craft's parts at the water this frame, in its frame (Mist: the walls that hold the mist off)
        private struct Wall
        {
            public Vector3 origin, f, s, up;
            public Cut[] cuts;
        }
        private readonly List<Wall> walls = new List<Wall>();
        private readonly List<Splash> splashes = new List<Splash>();
        private readonly List<Vector2> wpts = new List<Vector2>();
        private readonly List<Vector2> rpts = new List<Vector2>();
        private readonly List<Vector2> dpts = new List<Vector2>();   // the part's points under the water: (f, depth)
        // this part's submerged vertices: the shallowest and deepest (m under) and their outline (f, s)
        private float subLo, subHi, subF0, subF1, subS0, subS1;

        private void Sub(float depth, float x, float y)
        {
            subLo = Mathf.Min(subLo, depth);
            subHi = Mathf.Max(subHi, depth);
            subF0 = Mathf.Min(subF0, x);
            subF1 = Mathf.Max(subF1, x);
            subS0 = Mathf.Min(subS0, y);
            subS1 = Mathf.Max(subS1, y);
        }
        private float[] mh = new float[0], mx = new float[0], my = new float[0];
        private int emittedSplash;
        private readonly Dictionary<Part, Wetting> wetting = new Dictionary<Part, Wetting>();
        private readonly Dictionary<int, Hull> hulls = new Dictionary<int, Hull>();
        private readonly float[] cx = new float[8], cy = new float[8], ch = new float[8];
        private ParticleSystem.Particle[] buf, skirtBuf;

        // a splash's clear zone round its body (Apollo 15's ring of dark water): the vessel, its hull radius, when it hit
        private struct Hole
        {
            public Vessel v;
            public float r, t0;
            public Vector3 shift;   // its centre off the body's (world direction x metres: toward the high side)
        }
        private readonly List<Hole> holes = new List<Hole>();
        private Vessel splashVessel;   // the vessel EmitSplash is emitting for

        // a hard hit's welling up of bubbles round its body: the vessel, its size, when it hit
        private struct Upwell
        {
            public Vessel v;
            public float eD, t0;
        }
        private readonly List<Upwell> upwells = new List<Upwell>();
        private ParticleSystem upwell;

        private void Upwelling(float dt, WakeSettings cfg)
        {
            upwells.RemoveAll(u => u.v == null || Time.time - u.t0 > 5.5f);
            if (upwell == null || upwells.Count == 0 || !cfg.enabled)
                return;
            foreach (Upwell u in upwells)
            {
                float age = Time.time - u.t0;
                if (age < 1.6f || age > 4.5f)
                    continue;
                Vector3 c = u.v.CoM, up = ((Vector3)((Vector3d)c - u.v.mainBody.position)).normalized;
                Vector3 site = c - up * (float)u.v.mainBody.GetAltitude(c);
                Vector3 e1 = SplashRef(up), e2 = Vector3.Cross(up, e1);
                upwell.transform.rotation = Quaternion.LookRotation(up, e1);
                float light = Light(site, up, cfg), big = Mathf.Clamp(Mathf.Sqrt(u.eD / 2), 0.7f, 3f);
                int n = Count(dt * 85 * cfg.intensity);
                for (int i = 0; i < n; i++)
                {
                    float th = Random.value * 2 * Mathf.PI, r = Mathf.Lerp(0.5f * u.eD, 3.0f * u.eD, Mathf.Sqrt(Random.value));
                    Vector3 radial = e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th);
                    float size = Random.Range(1.8f, 3.2f) * big, sa = Mathf.Sqrt(Random.Range(1f, 1.5f));
                    upwell.Emit(new ParticleSystem.EmitParams
                    {
                        position = site + radial * r + up * cfg.foamLift,
                        velocity = radial * Random.Range(0.05f, 0.25f),
                        startSize3D = new Vector3(size / sa, size * sa, size),
                        startLifetime = Random.Range(10f, 13f),
                        rotation3D = new Vector3(0, 0, Random.value * 360f),
                        startColor = Tint(light * 0.95f, Mathf.Min(1, cfg.foamAlpha * 1.2f)),
                    }, 1);
                    emittedFoam++;
                }
            }
        }

        // 0 inside the clear zones (within inner x their radius, horizontally), 1 outside outer x it; each opening over 0.4 s
        private float HoleFactor(Vector3 p, float inner, float outer, float reach = 0, bool full = false)
        {
            float f = 1;
            for (int i = 0; i < holes.Count; i++)
            {
                Hole h = holes[i];
                float age = Time.time - h.t0;
                float r = h.r * (full ? 1 : Mathf.Clamp01(age / 0.4f)) * (1 - Mathf.Clamp01(age - 2));   // (full: its whole size from the hit)
                if (h.v == null || r < 0.01f)
                    continue;
                Vector3 c = h.v.CoM + h.shift * Mathf.Clamp01(age / 0.4f), up = ((Vector3)((Vector3d)h.v.CoM - h.v.mainBody.position)).normalized;
                Vector3 d = p - c;
                d -= up * Vector3.Dot(d, up);
                f = Mathf.Min(f, Smooth(inner * r, outer * r, Mathf.Max(0, d.magnitude - reach)));
            }
            return f;
        }

        private void Skirt(WakeSettings cfg)
        {
            holes.RemoveAll(h => h.v == null || Time.time - h.t0 > 3.2f);
            int n = skirt.particleCount;
            if (holes.Count == 0 || n == 0)
                return;
            if (skirtBuf == null || skirtBuf.Length < skirt.main.maxParticles)
                skirtBuf = new ParticleSystem.Particle[skirt.main.maxParticles];
            n = skirt.GetParticles(skirtBuf, n);
            float a0 = Mathf.Min(1, cfg.sheetAlpha * 2.9f);
            for (int i = 0; i < n; i++)
            {
                Color32 col = skirtBuf[i].startColor;
                col.a = (byte)(Mathf.Clamp01(a0 * HoleFactor(skirtBuf[i].position, 1.3f, 2.4f)) * 255);
                skirtBuf[i].startColor = col;
            }
            skirt.SetParticles(skirtBuf, n);
        }
        private float nextPurge, nextLog;
        private int emittedFoam, emittedSpray, emittedMist;

        private void Awake() => Instance = this;

        private void Start()
        {
            Shader sh = FindShader("Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "KSP/Particles/Alpha Blended");
            if (sh == null)
            {
                Debug.LogError("[WaterWakes] no particle shader found; off");
                enabled = false;
                return;
            }
            Debug.Log($"[WaterWakes] shader {sh.name}");

            // foam: with WaterWakes/Foam each patch erodes continuously with its age (custom vertex streams: uv, age,
            // random); without it, a texture sheet of ageing stages (which the legacy shader steps through, not blends)
            Shader foamSh = WakeShaders.Get("WaterWakes/Foam");
            CustomFoam = foamSh != null;
            foam = Make("WaterWakes foam", CustomFoam ? new Material(foamSh) { mainTexture = FoamAtlas() } : Mat(sh, FoamTexture()), 30000, true);
            foamFrame = foam.transform;
            {
                var fm = foam.main;
                fm.startSize3D = true;   // (streaks: longer along the flow)
                var sol = foam.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.5f), new Keyframe(0.15f, 1f), new Keyframe(1, 2.6f)));
                var vol = foam.velocityOverLifetime;
                vol.enabled = true;
                vol.space = ParticleSystemSimulationSpace.World;
                vol.speedModifier = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1f), new Keyframe(0.08f, 0.35f), new Keyframe(1, 0.03f)));
                if (CustomFoam)
                    foam.GetComponent<ParticleSystemRenderer>().SetActiveVertexStreams(new List<ParticleSystemVertexStream>
                    {
                        ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV,
                        ParticleSystemVertexStream.AgePercent, ParticleSystemVertexStream.StableRandomX,
                    });
                else
                {
                    // FoamStages ageing stages (columns) of FoamVariants patches (rows), dense churned white to thin lace
                    var tsa = foam.textureSheetAnimation;
                    tsa.enabled = true;
                    tsa.mode = ParticleSystemAnimationMode.Grid;
                    tsa.numTilesX = FoamStages;
                    tsa.numTilesY = FoamVariants;
                    tsa.animation = ParticleSystemAnimationType.SingleRow;
                    tsa.rowMode = ParticleSystemAnimationRowMode.Random;
                    tsa.cycleCount = 1;
                    tsa.frameOverTime = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0), new Keyframe(0.12f, 0.22f),
                                                                                              new Keyframe(0.45f, 0.62f), new Keyframe(1, 0.999f)));
                }
                Debug.Log($"[WaterWakes] foam shader {(CustomFoam ? foamSh.name : sh.name + " (texture sheet)")}");
                // in at once (else a wide hull passes over its bow foam before it shows), then fading fast: the patches grow
                // ~7x in area and overlap more as they age, so each must fade much more than the foam should
                var col = foam.colorOverLifetime;
                col.enabled = true;
                var fg = new Gradient();
                fg.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                           new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.008f), new GradientAlphaKey(0.72f, 0.07f), new GradientAlphaKey(0.34f, 0.18f),
                                   new GradientAlphaKey(0.13f, 0.36f), new GradientAlphaKey(0.04f, 0.58f), new GradientAlphaKey(0, 0.85f) });
                col.color = fg;
            }

            // the splash's foam: crisp streaks, fading by eroding, bright ~1 s, gone in several (its own frame, not painted)
            if (CustomFoam)
            {
                var mat = new Material(foamSh) { mainTexture = foam.GetComponent<ParticleSystemRenderer>().sharedMaterial.mainTexture };
                mat.SetFloat("_Crisp", 0.6f);   // (feathered: softer than a sheet blob's edge)
                mat.SetFloat("_Bright", 1.2f);
                splashFoam = Make("WaterWakes splash foam", mat, 8000, true);
                var fm = splashFoam.main;
                fm.startSize3D = true;
                var sol = splashFoam.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.7f), new Keyframe(0.1f, 1f), new Keyframe(1, 1.4f)));
                var vol = splashFoam.velocityOverLifetime;
                vol.enabled = true;
                vol.space = ParticleSystemSimulationSpace.World;
                vol.speedModifier = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1f), new Keyframe(0.25f, 0.5f), new Keyframe(1, 0.1f)));
                splashFoam.GetComponent<ParticleSystemRenderer>().SetActiveVertexStreams(AgedStreams);
                var col = splashFoam.colorOverLifetime;
                col.enabled = true;
                var fg = new Gradient();
                fg.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                           new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.01f), new GradientAlphaKey(0.9f, 0.3f), new GradientAlphaKey(0.55f, 0.6f),
                                   new GradientAlphaKey(0.2f, 0.85f), new GradientAlphaKey(0, 1) });
                col.color = fg;

                // the milky patch round the hull after a hard hit (the cavity's bubbles welling up): soft, slow, long
                var um = new Material(foamSh) { mainTexture = mat.mainTexture };
                um.SetFloat("_Crisp", 0.85f);
                um.SetFloat("_Bright", 1.1f);
                upwell = Make("WaterWakes upwell", um, 4000, true);
                var um2 = upwell.main;
                um2.startSize3D = true;
                var usol = upwell.sizeOverLifetime;
                usol.enabled = true;
                usol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.8f), new Keyframe(0.1f, 1f), new Keyframe(1, 1.5f)));
                upwell.GetComponent<ParticleSystemRenderer>().SetActiveVertexStreams(AgedStreams);
                var ucol = upwell.colorOverLifetime;
                ucol.enabled = true;
                var ug = new Gradient();
                ug.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                           new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.05f), new GradientAlphaKey(0.95f, 0.35f), new GradientAlphaKey(0.6f, 0.6f),
                                   new GradientAlphaKey(0.2f, 0.85f), new GradientAlphaKey(0, 1) });
                ucol.color = ug;
            }

            // droplets: each particle a loose cluster of fine drops (one of four), barely streaked by its flight
            spray = Make("WaterWakes spray", Mat(sh, DropletTexture(), true), 25000, false);
            {
                var sol = spray.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 1f, 1, 1.6f));
                var fol = spray.forceOverLifetime;
                fol.enabled = true;
                fol.space = ParticleSystemSimulationSpace.World;
                Fade(spray, 0.02f, 0.6f);
                RandomFrame(spray);
                var r = spray.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.02f;
                r.lengthScale = 1f;
            }
            // the spray sheet: ragged torn blobs of water (one of four), spreading and thinning as they fly
            sheet = Make("WaterWakes sheet", Mat(sh, SheetTexture(), true), 12000, false);
            {
                var sol = sheet.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.7f), new Keyframe(0.3f, 1.3f), new Keyframe(1, 2.4f)));
                var fol = sheet.forceOverLifetime;
                fol.enabled = true;
                fol.space = ParticleSystemSimulationSpace.World;
                Fade(sheet, 0.04f, 0.35f);
                RandomFrame(sheet);
                var rot = sheet.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
                var r = sheet.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.012f;
                r.lengthScale = 1.2f;
            }

            // the spray skirt's sheet: the sheet's blobs, slowed by the air (quadratic drag, 0.15 /m: Basilisk's sheet tip
            // off a capsule, launched at ~55 m/s, 4 m out in 0.1 s, 6.5 in 0.2, 8.3 in 0.3, 10.9 in 0.5), so they can hang
            // on as long as Apollo's white disc without flying further
            // (smooth and streaky: soft blobs streaked along their length, stretched along their flight)
            skirt = Make("WaterWakes skirt", Mat(sh, StreakTexture(), true), 16000, false);
            {
                var sol = skirt.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.7f), new Keyframe(0.3f, 1.15f), new Keyframe(1, 1.7f)));
                var fol = skirt.forceOverLifetime;
                fol.enabled = true;
                fol.space = ParticleSystemSimulationSpace.World;
                var lv = skirt.limitVelocityOverLifetime;
                lv.enabled = true;
                lv.limit = 1000;
                lv.drag = 0.08f;
                lv.multiplyDragByParticleVelocity = true;
                lv.multiplyDragByParticleSize = false;
                Fade(skirt, 0.03f, 0.5f);
                RandomFrame(skirt);
                var rot = skirt.rotationOverLifetime;
                rot.enabled = true;
                rot.z = new ParticleSystem.MinMaxCurve(-0.6f, 0.6f);
                var r = skirt.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.025f;
                r.lengthScale = 1f;
            }

            // crisp sheets: the skirt's and the sheet's blobs eroded by their fading alpha and age instead of faded
            skirtSoft = skirt.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            sheetSoft = sheet.GetComponent<ParticleSystemRenderer>().sharedMaterial;
            Shader spraySh = WakeShaders.Get("WaterWakes/Spray");
            if (spraySh != null)
            {
                Texture2D et = ErodeTexture();
                skirtCrisp = new Material(spraySh) { mainTexture = et };
                skirtCrisp.SetVector("_Erode", new Vector4(0.12f, 0.75f, 0.25f, 0.4f));
                skirtCrisp.SetFloat("_Bright", 1.2f);
                sheetCrisp = new Material(spraySh) { mainTexture = et };
                sheetCrisp.SetVector("_Erode", new Vector4(0.1f, 0.5f, 0.3f, 0.4f));
                sheetCrisp.SetFloat("_Bright", 1.1f);
            }
            Debug.Log($"[WaterWakes] spray shader {(spraySh != null ? spraySh.name : "none (soft only)")}, splash foam {(splashFoam != null ? "crisp" : "in the main foam")}");
            ApplySprayLook(Cfg.crispSpray);

            // the haze: soft puffs added (glowing) over the sheet, slowed by the air, growing, fading
            {
                Shader add = FindShader("Legacy Shaders/Particles/Additive", "Particles/Additive", "KSP/Particles/Additive");
                haze = Make("WaterWakes haze", Mat(add != null ? add : sh, MistTexture(), true), 6000, false);
                var hsol = haze.sizeOverLifetime;
                hsol.enabled = true;
                hsol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.6f), new Keyframe(0.3f, 1.2f), new Keyframe(1, 2.0f)));
                var hlv = haze.limitVelocityOverLifetime;
                hlv.enabled = true;
                hlv.limit = 1000;
                hlv.drag = 0.5f;
                hlv.multiplyDragByParticleVelocity = true;
                hlv.multiplyDragByParticleSize = false;
                var hcol = haze.colorOverLifetime;
                hcol.enabled = true;
                var hg = new Gradient();
                hg.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                           new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.08f), new GradientAlphaKey(0.7f, 0.4f), new GradientAlphaKey(0, 1) });
                hcol.color = hg;
                var hrot = haze.rotationOverLifetime;
                hrot.enabled = true;
                hrot.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.3f);
                Debug.Log($"[WaterWakes] haze shader {(add != null ? add.name : sh.name + " (no additive found)")}");
            }

            // mist: its opacity set each frame from each puff's height (Mist()); the age fade only eases it in and out
            mist = Make("WaterWakes mist", Mat(sh, MistTexture(), true), 8000, false);
            {
                var sol = mist.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.5f), new Keyframe(0.25f, 1.3f), new Keyframe(1, 3.5f)));
                var vol = mist.velocityOverLifetime;
                vol.enabled = true;
                vol.space = ParticleSystemSimulationSpace.World;
                vol.speedModifier = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1f), new Keyframe(0.3f, 0.35f), new Keyframe(1, 0.15f)));
                var col = mist.colorOverLifetime;
                col.enabled = true;
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                          new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, 0.1f), new GradientAlphaKey(0.8f, 0.5f), new GradientAlphaKey(0, 1) });
                col.color = g;
            }
        }

        // stock's splash (FXMonger.Splash, a part hitting the water): a white column drawn over the splash this draws.
        // Its prefabs swapped for copies with their particles off (their sound kept) while hideStockSplash is on
        private GameObject[] stockSplashes, quietSplashes;
        private FXMonger fxMonger;
        private float nextFxSearch;

        private void StockSplash(WakeSettings cfg)
        {
            if (fxMonger == null && Time.time > nextFxSearch)
            {
                nextFxSearch = Time.time + 5;
                fxMonger = FindObjectOfType<FXMonger>();
            }
            FXMonger fx = fxMonger;
            if (fx == null || fx.splashes == null)
                return;
            bool hide = cfg.enabled && cfg.hideStockSplash;
            if (hide && stockSplashes == null)
            {
                stockSplashes = (GameObject[])fx.splashes.Clone();
                quietSplashes = new GameObject[stockSplashes.Length];
                for (int i = 0; i < stockSplashes.Length; i++)
                {
                    if (stockSplashes[i] == null)
                        continue;
                    GameObject q = Instantiate(stockSplashes[i]);
                    q.SetActive(false);
                    DontDestroyOnLoad(q);
                    foreach (ParticleSystem ps in q.GetComponentsInChildren<ParticleSystem>(true))
                    {
                        var em = ps.emission;
                        em.enabled = false;
                    }
                    foreach (Renderer r in q.GetComponentsInChildren<Renderer>(true))
                        r.enabled = false;
                    quietSplashes[i] = q;
                }
                fx.splashes = quietSplashes;
            }
            else if (!hide && stockSplashes != null)
            {
                fx.splashes = stockSplashes;
                stockSplashes = null;
            }
            else if (hide && fx.splashes != quietSplashes)
                fx.splashes = quietSplashes;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            if (stockSplashes != null && fxMonger != null)
                fxMonger.splashes = stockSplashes;
            foreach (ParticleSystem ps in new[] { foam, spray, sheet, mist, skirt, splashFoam, upwell, haze })
                if (ps != null)
                {
                    FloatingOrigin.UnregisterParticleSystem(ps);
                    Destroy(ps.gameObject);
                }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0 || FlightGlobals.VesselsLoaded == null)
                return;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null)
                return;
            ApplySprayLook(Cfg.crispSpray);
            // (the spray's soft particles read the scene's depth)
            if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0)
                cam.depthTextureMode |= DepthTextureMode.Depth;
            Shader.SetGlobalFloat("_WW_SoftFade", 1f);
            if (Planetarium.fetch != null && Planetarium.fetch.Sun != null)
                Shader.SetGlobalVector("_WW_SunDir", ((Vector3)(Planetarium.fetch.Sun.position - (Vector3d)cam.transform.position)).normalized);
            if (Time.time > nextPurge)
            {
                nextPurge = Time.time + 5;
                var dead = new List<Part>();
                foreach (Part p in boxes.Keys)
                    if (p == null)
                        dead.Add(p);
                foreach (Part p in dead)
                    boxes.Remove(p);
                dead.Clear();
                foreach (Part p in wetting.Keys)
                    if (p == null)
                        dead.Add(p);
                foreach (Part p in dead)
                    wetting.Remove(p);
            }

            WaveSources.Clear();
            RippleSources.Clear();
            SimBlobs.Clear();
            StillHeaveRate = 0;
            walls.Clear();
            WakeSettings cfg = Cfg;
            bool log = cfg.debugLog && Time.time > nextLog;
            if (log)
                nextLog = Time.time + 1;
            if (cfg.enabled)
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed || v.mainBody == null || !v.mainBody.ocean || v.altitude > 300)
                        continue;
                    if ((v.CoM - cam.transform.position).sqrMagnitude > cfg.maxRange * cfg.maxRange)
                        continue;
                    Wake(v, dt, log);
                }
            StockSplash(cfg);
            Impacts(dt, cfg);
            Rings(dt, cfg);
            LayLateFoam();
            Mist(cfg);
            Skirt(cfg);
            Upwelling(dt, cfg);
            Haze(cfg);
        }

        // a mist puff (its disc's radius reach) held off the crafts' walls: below a part's top, a puff whose disc reaches
        // over the part seen from above (its roof outline) is moved out across the flow to clear the nearer side, fully
        // up to the top, less over the next reach above it (a puff over the deck drifts on freely). The bow wave's mist
        // grew from the waterline into puffs metres wide that lay over a ship's deck, its 9 m freeboard no wall to them
        private Vector3 OffTheWalls(Vector3 p, float reach)
        {
            foreach (Wall w in walls)
            {
                Vector3 d = p - w.origin;
                float x = Vector3.Dot(d, w.f), y = Vector3.Dot(d, w.s), z = Vector3.Dot(d, w.up);
                foreach (Cut c in w.cuts)
                {
                    if (x < c.rf0 || x > c.rf1)
                        continue;
                    float h = z - c.wl, hold = 1 - Mathf.Clamp01((h - c.top) / Mathf.Max(0.1f, reach));
                    if (hold <= 0)
                        continue;
                    float lo = c.RoofEdge(x, -1), hi = c.RoofEdge(x, 1);
                    if (y <= lo - reach || y >= hi + reach)
                        continue;
                    float to = y < 0.5f * (lo + hi) ? lo - reach : hi + reach;
                    p += w.s * ((to - y) * hold);
                    y += (to - y) * hold;
                }
            }
            return p;
        }

        // each puff's opacity from its height above the sea: mistAlpha at the water, falling by 1/e every mistFalloff
        private void Mist(WakeSettings cfg)
        {
            int n = mist.particleCount;
            CelestialBody body = FlightGlobals.currentMainBody;
            if (n == 0 || body == null)
                return;
            if (buf == null || buf.Length < mist.main.maxParticles)
                buf = new ParticleSystem.Particle[Math.Max(mist.main.maxParticles, spray.main.maxParticles)];
            n = mist.GetParticles(buf, n);
            Vector3d c = body.position;
            double r = body.Radius;
            float k = 1 / Mathf.Max(0.05f, cfg.mistFalloff);
            for (int i = 0; i < n; i++)
            {
                if (walls.Count > 0)
                    buf[i].position = OffTheWalls(buf[i].position, 0.45f * buf[i].GetCurrentSize(mist));
                double h = ((Vector3d)buf[i].position - c).magnitude - r;
                float a = cfg.mistAlpha * Mathf.Exp(-Mathf.Max(0, (float)h) * k) * HoleFactor(buf[i].position, 1.3f, 2.6f, 0.45f * buf[i].GetCurrentSize(mist));
                Color32 col = buf[i].startColor;
                col.a = (byte)(Mathf.Clamp01(a) * 255);
                buf[i].startColor = col;
            }
            mist.SetParticles(buf, n);
        }

        private void Wake(Vessel v, float dt, bool log)
        {
            WakeSettings cfg = Cfg;
            CelestialBody body = v.mainBody;
            Vector3 origin = v.CoM;
            Vector3 up = ((Vector3)((Vector3d)origin - body.position)).normalized;
            Vector3 vel = v.srf_velocity;
            Vector3 vh = vel - up * Vector3.Dot(vel, up);
            float V = vh.magnitude;
            // the flow's direction, blended from the craft's heading at rest (its course from ~1.5 m/s): a hull rocking
            // in waves drifts a few tenths of a m/s every way, and the frame turned with it (its parts' outlines are
            // binned along it: a long hull seen across it is a fat box), every turn read by the wave sim as the hull
            // shoving its water aside, steep chop round it all the while it lay in the waves
            Vector3 hd = Vector3.ProjectOnPlane(v.transform.up, up);
            if (hd.sqrMagnitude < 0.25f)
                hd = Vector3.ProjectOnPlane(v.transform.forward, up);
            hd.Normalize();
            // (the heading alone under 3 m/s, the course from 5: a ship coming to rest drifts a few cm/s every way, and
            // even blended in, the course swung the frame (and its footprint, 70 m long) ~1 deg about its heading, its
            // ends half a metre sideways at full depth: the wave sim's still push read that as the hull reshaping)
            float cb = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(3f, 5f, V));
            Vector3 f = (hd * (1 - cb) + (V > 0.05f ? vh / V : hd) * cb).normalized;
            if (f.sqrMagnitude < 0.5f)
                f = hd;
            Vector3 s = Vector3.Cross(up, f).normalized;
            double altO = FlightGlobals.getAltitudeAtPos((Vector3d)origin, body);

            // each wet part's cut at the water, in the flow's frame (f along the flow, s across it), about the CoM; a part
            // wholly under water counts only within submergedFade of the surface, fading out (a dived hull makes no wake)
            cuts.Clear();
            splashes.Clear();
            float fade = Mathf.Max(0.01f, cfg.submergedFade);
            foreach (Part p in v.parts)
            {
                if (p == null || p.rb == null)
                    continue;
                Box[] bs = Boxes(p);
                if (bs.Length == 0)
                    continue;
                double wl = v.waterOffset + (p.partBuoyancy != null ? p.partBuoyancy.waterLevel : 0);
                float wlRel = (float)(wl - altO);
                Vector3 pos = p.transform.position;
                float hRef = (float)(FlightGlobals.getAltitudeAtPos((Vector3d)pos, body) - wl);
                Cut cut = null;
                float top = float.MinValue, vol = 0, volBox = 0;
                wpts.Clear();
                rpts.Clear();
                dpts.Clear();
                subLo = subF0 = subS0 = float.MaxValue;
                subHi = subF1 = subS1 = float.MinValue;
                foreach (Box b in bs)
                {
                    if (b.t == null)
                        continue;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 l = b.c + new Vector3((i & 1) != 0 ? b.e.x : -b.e.x, (i & 2) != 0 ? b.e.y : -b.e.y, (i & 4) != 0 ? b.e.z : -b.e.z);
                        Vector3 w = b.t.TransformPoint(l);
                        ch[i] = hRef + Vector3.Dot(w - pos, up);
                        cx[i] = Vector3.Dot(w - origin, f);
                        cy[i] = Vector3.Dot(w - origin, s);
                    }
                    if (b.verts != null)
                    {
                        // the mesh's own waterline: each triangle edge crossing the water, and its vertices under it
                        int nv = b.verts.Length;
                        if (mh.Length < nv)
                        {
                            mh = new float[nv];
                            mx = new float[nv];
                            my = new float[nv];
                        }
                        Matrix4x4 m = b.t.localToWorldMatrix;
                        for (int i = 0; i < nv; i++)
                        {
                            Vector3 w = m.MultiplyPoint3x4(b.verts[i]);
                            mh[i] = hRef + Vector3.Dot(w - pos, up);
                            mx[i] = Vector3.Dot(w - origin, f);
                            my[i] = Vector3.Dot(w - origin, s);
                            rpts.Add(new Vector2(mx[i], my[i]));
                            top = Mathf.Max(top, mh[i]);
                            if (mh[i] < 0)
                            {
                                Add(ref cut, mx[i], my[i], -mh[i], wlRel);
                                Sub(-mh[i], mx[i], my[i]);
                                dpts.Add(new Vector2(mx[i], -mh[i]));
                            }
                        }
                        int[] tr = b.tris;
                        for (int t = 0; t + 2 < tr.Length; t += 3)
                            for (int e = 0; e < 3; e++)
                            {
                                int a = tr[t + e], c2 = tr[t + (e + 1) % 3];
                                if (mh[a] * mh[c2] >= 0)
                                    continue;
                                float u = mh[a] / (mh[a] - mh[c2]);
                                float x = Mathf.Lerp(mx[a], mx[c2], u), y = Mathf.Lerp(my[a], my[c2], u);
                                Add(ref cut, x, y, 0, wlRel);
                                wpts.Add(new Vector2(x, y));
                            }
                    }
                    else
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            rpts.Add(new Vector2(cx[i], cy[i]));
                            top = Mathf.Max(top, ch[i]);
                            if (ch[i] < 0)
                            {
                                Add(ref cut, cx[i], cy[i], -ch[i], wlRel);
                                Sub(-ch[i], cx[i], cy[i]);
                                dpts.Add(new Vector2(cx[i], -ch[i]));
                            }
                            for (int bit = 1; bit < 8; bit <<= 1)
                            {
                                int j = i | bit;
                                if (j == i || ch[i] * ch[j] >= 0)
                                    continue;
                                float t = ch[i] / (ch[i] - ch[j]);
                                float x = Mathf.Lerp(cx[i], cx[j], t), y = Mathf.Lerp(cy[i], cy[j], t);
                                Add(ref cut, x, y, 0, wlRel);
                                wpts.Add(new Vector2(x, y));
                            }
                        }
                    }
                    // the box's submerged volume: the height is linear across it
                    float bv = 8 * b.t.TransformVector(new Vector3(b.e.x, 0, 0)).magnitude * b.t.TransformVector(new Vector3(0, b.e.y, 0)).magnitude
                                 * b.t.TransformVector(new Vector3(0, 0, b.e.z)).magnitude;
                    float h0 = 0;
                    for (int i = 0; i < 8; i++)
                        h0 += ch[i] * 0.125f;
                    vol += bv * Submerged(h0, (ch[1] - ch[0]) * 0.5f, (ch[2] - ch[0]) * 0.5f, (ch[4] - ch[0]) * 0.5f);
                    volBox += bv;
                }
                float wf0 = float.MaxValue, wf1 = float.MinValue, ws0 = float.MaxValue, ws1 = float.MinValue;   // the waterline's outline
                foreach (Vector2 q in wpts)
                {
                    wf0 = Mathf.Min(wf0, q.x);
                    wf1 = Mathf.Max(wf1, q.x);
                    ws0 = Mathf.Min(ws0, q.y);
                    ws1 = Mathf.Max(ws1, q.y);
                }
                if (cut != null)
                    Profile(cut, wf1 > wf0);

                // how fast it goes in or out: its submerged volume's rate over its waterline's area
                if (!wetting.TryGetValue(p, out Wetting wt))
                    wetting[p] = wt = new Wetting();
                if (wt.seen)
                    wt.rate = Mathf.Lerp(wt.rate, (vol - wt.vol) / dt, Mathf.Clamp01(dt / 0.08f));
                wt.vol = vol;
                wt.seen = true;
                Entry(wt, p, body, origin, f, s, up, volBox > 0 ? vol / volBox : 0, wpts, dt, cfg);
                if (cut != null || Mathf.Abs(wt.rate) > 1e-3f)
                {
                    bool atLine = wf1 >= wf0;
                    float L = atLine ? wf1 - wf0 : cut != null ? cut.fMax - cut.fMin : 0, B = atLine ? ws1 - ws0 : cut != null ? cut.sMax - cut.sMin : 0;
                    float area = Mathf.Max(0.785f * L * B, 0.3f * Mathf.Pow(volBox, 2f / 3f));
                    float speed = Mathf.Abs(wt.rate) / area;
                    Vector3 pv = p.rb.velocity + (Vector3)Krakensbane.GetFrameVelocity();
                    // (a breach only while it rises: the submerged volume of a part wholly under wobbles, which read as
                    // leaving the water threw a plume up off a body still plunging)
                    // (a deep hull splashes only as it slams, its relative speed past ~half its draft's wave speed: a 70 m
                    // ship riding waves wetted and dried ~1 m/s along its length each wave, splashing mist off both
                    // ends while it lay at rest; and a part whose waterline is under another part, a turbine in the
                    // hull, not at all)
                    float slam = Mathf.Max(cfg.splashMinSpeed, 0.5f * Mathf.Sqrt(9.81f * Mathf.Max(0, cut != null ? cut.draft : 0)));
                    float fcs = atLine ? (wf0 + wf1) * 0.5f : cut != null ? (cut.fMin + cut.fMax) * 0.5f : 0;
                    float scs = atLine ? (ws0 + ws1) * 0.5f : cut != null ? (cut.sMin + cut.sMax) * 0.5f : 0;
                    if (speed > slam && (atLine || cut != null) && !(wt.rate < 0 && Vector3.Dot(pv, up) < 0.3f) && !Hidden(fcs, scs, cut))
                    {
                        splashes.Add(new Splash
                        {
                            fc = atLine ? (wf0 + wf1) * 0.5f : (cut.fMin + cut.fMax) * 0.5f,
                            sc = atLine ? (ws0 + ws1) * 0.5f : (cut.sMin + cut.sMax) * 0.5f,
                            L = Mathf.Max(0.2f, L), B = Mathf.Max(0.2f, B), wl = wlRel, speed = speed - (slam - cfg.splashMinSpeed), outOf = wt.rate < 0, vel = pv - up * Vector3.Dot(pv, up), own = cut,
                            domed = wt.enterT >= 0 && Time.time - wt.enterT > wt.sealAt,
                            entry = wt.enterT >= 0 && Time.time - wt.enterT < 2f && wt.U > 3f && !(wt.rate < 0),
                            eU = Mathf.Min(wt.U, cfg.splashMaxSpeed), eD = Mathf.Max(0.2f, Mathf.Sqrt(wt.LB)), since = Time.time - wt.enterT,
                            keep = wt.U > 0.1f ? Mathf.Clamp01(wt.un / wt.U) : 1,
                            skirtArea = wt.skirtArea, skirtSpread = wt.skirtSpread, spreadMax = wt.spreadMax, tilt = wt.tilt, flat = wt.flat,
                        });
                    }
                }

                if (cut == null)
                    continue;
                cut.top = top;
                if (top < 0)
                {
                    cut.w = 1 + top / fade;
                    if (cut.w <= 0)
                        continue;
                }
                cuts.Add(cut);
            }
            if (cuts.Count > 0)
                walls.Add(new Wall { origin = origin, f = f, s = s, up = up, cuts = cuts.ToArray() });

            Vector3 sunDir = ((Vector3)(Planetarium.fetch.Sun.position - (Vector3d)origin)).normalized;
            float light = Mathf.Lerp(0.08f, 1f, Mathf.InverseLerp(-0.1f, 0.35f, Vector3.Dot(sunDir, up))) * cfg.brightness;
            if (splashes.Count > 0)
            {
                Gravity(up);
                splashVessel = v;
                foreach (Splash sp in splashes)
                    EmitSplash(sp, origin, f, s, up, dt, light, cfg);
            }
            // the wave simulation's sources: each wetted part's waterline, its stations in pairs, x its draft (no deeper
            // than ~0.6 of its width: what makes surface waves)
            // (faded out as the craft gets fast enough for the analytic Kelvin wake, which takes over: a fast hull's path
            // through the square only makes the sim a trench of breaking chop)
            if (!crafts.TryGetValue(v.id, out CraftState cs0))
                crafts[v.id] = cs0 = new CraftState();
            float simW = SimShare(cs0, (float)v.horizontalSrfSpeed, dt, cfg);
            // (how far it moved since the last frame: its CoM then and now (relative to its body). It moves only on
            // physics steps, so a frame's motion is none or a step's worth; the old vel x dt slid its footprint where it
            // was every frame regardless, read by the sim as a slow hull reshaping at its full depth: a kick every
            // frame, ripples round a ship coming to rest as if it shook)
            Vector3d comRel = (Vector3d)origin - body.position;
            Vector3 moved = cs0.prevFrame == Time.frameCount - 1 ? (Vector3)(comRel - cs0.prevCoM) : vh * dt;
            cs0.prevCoM = comRel;
            cs0.prevFrame = Time.frameCount;
            float still = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.5f, 2.5f, (float)v.horizontalSrfSpeed));
            if (still > 0.01f && simW > 0.01f && cfg.waveSim)
            {
                Vector3 w = v.angularVelocity;   // (vessel frame: x pitch, y roll, z yaw)
                float half = 0;
                foreach (Cut c in cuts)
                    half = Mathf.Max(half, Mathf.Max(Mathf.Abs(c.pf0), Mathf.Abs(c.pf1)));
                StillHeaveRate = Mathf.Max(StillHeaveRate, Mathf.Abs((float)v.verticalSpeed) + new Vector2(w.x, w.y).magnitude * half);
            }
            // (a moving hull's deep parts make little surface wave: its waves, k = g / V^2, feel its depth d as
            // (1 - e^-kd) / k: a 4.4 m deep ship at 4 m/s as a 1.5 m one; WaveSim's push takes it so)
            float kWave = 9.81f / Mathf.Max(0.25f, (float)(v.horizontalSrfSpeed * v.horizontalSrfSpeed));
            if (cfg.waveSim && simW > 0.01f)
                foreach (Cut c in cuts)
                {
                    if (!c.line || c.w <= 0 || SimBlobs.Count >= 128)
                        continue;
                    // its stations' widths at their centres, and at its two ends taken on from the end stations' trend
                    // (a station's width is its widest: a stem's narrows on to the tip, a transom's stays); each
                    // station's depth no more than 0.6 of its width (the fine bow makes no deep hole); a stretch of the
                    // footprint between each two, its width and depth going from one's to the other's (no boxes: a
                    // hull of squared-off blocks rang squared-off waves)
                    // (the depth not faded with simW: a footprint deepening as the craft slows into the sim's range
                    // read as the hull pushing that water aside, a ring of chop round it each time it stopped)
                    float dx = (c.pf1 - c.pf0) / Bins, k0 = Mathf.Clamp01(c.w);
                    // (and a hull makes next to no waves much shorter than ~0.15 of its waterline: its bow's and stern's
                    // cancel, its lines are smooth. Its waves moving are 2 pi V^2 / g long: as a 70 m ship slowed through
                    // 2 - 4 m/s its footprint's sharp ends rang 3 - 10 m waves, left round it stopped as ripples, as if it
                    // shook; fading in as (lambda / 0.15 L)^4 / (1 + it), its depth there ~1 / k: k over that)
                    float lam = 2 * Mathf.PI / kWave, rl = lam / Mathf.Max(0.5f, 0.15f * (c.pf1 - c.pf0));
                    rl *= rl;
                    rl *= rl;
                    float kWaveC = kWave / Mathf.Max(0.01f, rl / (1 + rl));
                    int n = 0;
                    for (int i = -1; i <= Bins; i++)
                    {
                        float x, w, m, dr;
                        if (i < 0 || i == Bins)
                        {
                            int a = i < 0 ? 0 : Bins - 1, b = i < 0 ? 1 : Bins - 2;
                            float wa = c.hi[a] - c.lo[a], wb = c.hi[b] - c.lo[b];
                            x = i < 0 ? c.pf0 : c.pf1;
                            w = Mathf.Clamp(wa + 0.5f * (wa - wb), 0, wa);
                            m = (c.lo[a] + c.hi[a]) * 0.5f;
                            dr = Mathf.Clamp(c.dep[a] + 0.5f * (c.dep[a] - c.dep[b]), 0, c.dep[a]);   // (on as the width)
                        }
                        else
                        {
                            x = c.pf0 + (i + 0.5f) * dx;
                            w = c.hi[i] - c.lo[i];
                            m = (c.lo[i] + c.hi[i]) * 0.5f;
                            dr = c.dep[i];
                        }
                        stX[n] = x;
                        stW[n] = Mathf.Max(0, w);
                        stM[n] = m;
                        stD[n] = Mathf.Min(Mathf.Min(dr, c.draft), 0.6f * Mathf.Max(0, w)) * k0;
                        n++;
                    }
                    // (a tail past each end, its depth easing out over ~4x the end's: a transom's step, the hole behind it
                    // filling at once as the hull moved, broke into a sheet of foam behind the stern)
                    for (int e = 0; e < 2; e++)
                    {
                        int at = e == 0 ? 0 : n - 1;
                        if (stD[at] < 0.05f)
                            continue;
                        float tail = Mathf.Max(2f, 4f * stD[at]) * (e == 0 ? -1 : 1);
                        if (e == 0)
                        {
                            for (int j = n; j > 0; j--)
                            {
                                stX[j] = stX[j - 1];
                                stW[j] = stW[j - 1];
                                stM[j] = stM[j - 1];
                                stD[j] = stD[j - 1];
                            }
                            at = 1;
                        }
                        int to = e == 0 ? 0 : n;
                        stX[to] = stX[at] + tail;
                        stW[to] = stW[at] * 0.8f;
                        stM[to] = stM[at];
                        stD[to] = 0;
                        n++;
                    }
                    for (int i = 0; i + 1 < n && SimBlobs.Count < 128; i++)
                    {
                        if (stW[i] < 0.02f && stW[i + 1] < 0.02f || stX[i + 1] - stX[i] < 0.01f)
                            continue;
                        float half = (stX[i + 1] - stX[i]) * 0.5f;
                        SimBlobs.Add(new SimBlob
                        {
                            pos = origin + f * (stX[i] + half) + s * ((stM[i] + stM[i + 1]) * 0.5f) + up * c.wl,
                            fwd = f, halfL = Mathf.Max(half / 0.6f, 0.05f),
                            halfW = stW[i] * 0.55f, depth = stD[i], halfWFore = stW[i + 1] * 0.55f, depthFore = stD[i + 1],
                            still = still, weight = simW, moved = moved, kWave = kWaveC,
                        });
                    }
                }
            if (cuts.Count == 0)
                return;

            // hulls: cuts overlapping across the flow and touching along it
            for (int i = 0; i < cuts.Count; i++)
                cuts[i].group = i;
            for (int i = 0; i < cuts.Count; i++)
                for (int j = i + 1; j < cuts.Count; j++)
                {
                    Cut a = cuts[i], b = cuts[j];
                    bool across = a.sMin < b.sMax + 0.05f && b.sMin < a.sMax + 0.05f;
                    bool along = a.fMin < b.fMax + 0.5f && b.fMin < a.fMax + 0.5f;
                    if (across && along)
                    {
                        int ga = Root(a.group), gb = Root(b.group);
                        if (ga != gb)
                            cuts[gb].group = ga;
                    }
                }
            hulls.Clear();
            float draftMax = 0, areaAll = 0;
            foreach (Cut c in cuts)
            {
                int g = Root(c.group);
                if (!hulls.TryGetValue(g, out Hull h))
                    hulls[g] = h = new Hull();
                h.fMin = Mathf.Min(h.fMin, c.fMin);
                h.fMax = Mathf.Max(h.fMax, c.fMax);
                h.sMin = Mathf.Min(h.sMin, c.sMin);
                h.sMax = Mathf.Max(h.sMax, c.sMax);
                h.draft = Mathf.Max(h.draft, c.draft);
                h.w = Mathf.Max(h.w, c.w);
                h.line |= c.line;
                h.wl += c.wl;
                if (c.line)
                    h.parts.Add(c);
                float ca = c.Area();
                h.area += ca;
                areaAll += ca;
                h.n++;
                draftMax = Mathf.Max(draftMax, c.draft);
            }
            // bow waves start at each part's waterline front, as wide as the waterline is there; a part's front counts as much
            // as it sticks out past the parts ahead of it on that side (at that station; by 1 m: fully), so a long centre
            // hull's bow draws the V and sponsons beside it start their own shoulder waves at their fronts. None where
            // another wet part lies just outside that side (its spray would be under the boat), nor in another part's lee
            foreach (Cut c in cuts)
            {
                int g = Root(c.group);
                if (!c.line && hulls[g].line)
                    continue;   // (a part wholly under, in a hull at the surface: no bow of its own)
                float x0 = c.pf1;
                for (int side = -1; side <= 1; side += 2)
                {
                    float y0 = c.Edge(x0 - 0.1f * (c.pf1 - c.pf0), side), exposure = float.MaxValue;
                    bool blocked = Hidden(x0 - 0.1f, c.RoofEdge(x0 - 0.1f, side) + side * 0.3f, c);   // (just past its own overhang)
                    foreach (Cut o in cuts)
                    {
                        if (o == c)   // (any wet part: a separate hull beside or ahead hides or shields as well)
                            continue;
                        if (o.Spans(x0, 0.3f))
                        {
                            float oLo = o.Edge(x0, -1), oHi = o.Edge(x0, 1);
                            // something wet right outside this side here
                            if (side > 0 ? oHi > y0 + 0.2f && oLo < y0 + 1f : oLo < y0 - 0.2f && oHi > y0 - 1f)
                                blocked = true;
                        }
                        if (o.pf1 > x0 + 0.3f && o.pf0 <= x0 + 0.5f && o.sMax >= c.sMin - 0.05f && o.sMin <= c.sMax + 0.05f)
                        {
                            float oy = o.Edge(Mathf.Clamp(x0, o.pf0, o.pf1), side);
                            exposure = Mathf.Min(exposure, side < 0 ? oy - y0 : y0 - oy);
                        }
                    }
                    float w = blocked ? 0 : exposure == float.MaxValue ? 1 : Mathf.Clamp01(exposure);
                    if (w > 0.05f)
                        hulls[g].bows.Add(new Bow
                        {
                            x = x0, y = y0, side = side, cut = c, len = Mathf.Max(0.1f, c.pf1 - c.pf0),
                            beam = Mathf.Max(0.1f, c.hi[Bins - 1] - c.lo[Bins - 1] + 0.3f * (c.sMax - c.sMin)), w = w * c.w,
                        });
                }
            }

            // the hull's load on the water: its draft against its draft at rest, smoothed over ~0.3 s
            if (!crafts.TryGetValue(v.id, out CraftState st))
                crafts[v.id] = st = new CraftState();
            // (learnt at rest; until a craft has rested, the deepest draft seen; never from a part under the water)
            if (V < 1.5f && st.restDraft > 0)
            {
                st.restDraft = Mathf.Lerp(st.restDraft, draftMax, Mathf.Clamp01(dt * 2));
                st.restArea = Mathf.Lerp(st.restArea, areaAll, Mathf.Clamp01(dt * 2));
                st.rested = true;
            }
            else if (!st.rested)
            {
                st.restDraft = Mathf.Max(st.restDraft, draftMax);
                st.restArea = Mathf.Max(st.restArea, areaAll);
            }
            float loadNow = st.restDraft > 1e-3f ? Mathf.Clamp01(draftMax / st.restDraft) : 1;
            st.load = Mathf.Lerp(st.load, loadNow, Mathf.Clamp01(dt / 0.3f));
            st.area = Mathf.Lerp(st.area, areaAll, Mathf.Clamp01(dt / 0.3f));
            // planing: the draft small but the hull still on the water, carried by the flow, dragging and throwing spray;
            // weaker than at the hump, until it lifts off. Its water drag goes as its wetted area x V^2: the wetted share
            // of the waterplane at rest, x (V / fullSpeed)^2, against planingArea (a small wet patch at speed drags hard)
            float wet = st.area / Mathf.Max(1e-3f, st.restArea), vr = V / Mathf.Max(0.1f, cfg.fullSpeed);
            float planing = cfg.planing * Mathf.Clamp01(wet * vr * vr / Mathf.Max(1e-3f, cfg.planingArea));
            float strength = Mathf.Max(Mathf.Pow(st.load, cfg.loadExponent), planing) * cfg.intensity;
            Waves(st, body, origin, up, f, V, dt, strength / Mathf.Max(1e-3f, cfg.intensity), (float)(body.GeeASL * 9.80665), cfg);
            if (V < cfg.minSpeed || strength <= 0)
                return;

            foamFrame.rotation = Quaternion.LookRotation(up, f);
            Gravity(up);

            float k = Mathf.Clamp01((V - cfg.minSpeed) / Mathf.Max(0.1f, cfg.fullSpeed - cfg.minSpeed));
            float dist = V * dt;
            // foam keeps a trace of the track however light the hull runs; spray and mist go with the load
            float foamStrength = Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(strength));
            foreach (Hull h in hulls.Values)
            {
                float L = Mathf.Max(0.1f, h.fMax - h.fMin), B = Mathf.Max(0.1f, h.sMax - h.sMin), wlRel = h.wl / h.n, hw = h.w;
                // a point on the water: origin + f x + s y, put at the waterline (f and s are level)
                Vector3 At(float x, float y, float lift) => origin + f * x + s * y + up * (wlRel + lift);
                // spread over the frame's travel so a fast hull leaves a line, not dots
                Vector3 Smear() => -vh * dt * Random.value;
                Vector3 MistAt(Bow b, float back, float outBy, float lift)
                {
                    float x = b.x - back;
                    return At(x, b.cut.Edge(x, b.side) + b.side * outBy, lift) + Smear();
                }

                // side foam: each bow wave's arm, thrown out and slowed, which draws the V
                float a = cfg.foamAlpha * Mathf.Lerp(0.3f, 1f, k) * foamStrength;
                // (a deep hull pushes a bigger bow wave: patches ~ sqrt(bow beam x draft), as before for shallow ones)
                float D = Mathf.Max(0.05f, h.draft);
                foreach (Bow b in h.bows)
                {
                    float ps = Mathf.Clamp(Mathf.Sqrt(b.beam * Mathf.Max(D, b.beam)), 0.3f, 5f);
                    int n = Count(dist / Mathf.Max(0.02f, cfg.foamSpacing) * 0.6f / ps * Mathf.Min(1, cfg.intensity) * hw * b.w);
                    for (int i = 0; i < n; i++)
                    {
                        float x = b.x - Random.value * 0.15f * b.len;
                        Vector3 pv = s * b.side * V * cfg.foamSpread * Random.Range(0.6f, 1.1f) + f * V * Random.Range(0f, 0.08f);
                        // (streaks along the arm: it runs back from the bow at atan(foamSpread) off the track)
                        EmitFoam(At(x, b.cut.Edge(x, b.side), cfg.foamLift) + Smear(), pv, ps * Random.Range(0.9f, 1.6f), cfg.foamLife * Random.Range(0.7f, 1.2f), a, light,
                                 1.8f, b.side * Mathf.Atan(cfg.foamSpread) * Mathf.Rad2Deg + Random.Range(-12f, 12f));
                    }
                }
                // stern foam: the churned water behind the hull, leaving it from its waterline's aft edge (the frame is the
                // flow's: in a skidding turn a line across it at the rearmost point lay mostly in open water beside the stern)
                {
                    float ps = Mathf.Clamp(0.8f * B, 0.3f, 2.2f);   // (bigger read as separate blobs: a big hull gets more of them)
                    int n = Count(dist * cfg.sternDensity * 0.36f * B / (ps * ps) * Mathf.Min(1, cfg.intensity) * hw);
                    for (int i = 0; i < n; i++)
                    {
                        float u = Random.value, y = h.sMin + B * u;
                        float x = h.parts.Count > 0 ? h.AftEdge(y) : h.fMin;
                        if (float.IsNaN(x))
                            continue;
                        Vector2 jit = Random.insideUnitCircle * 0.3f * k;
                        Vector3 pv = s * ((u - 0.5f) * 2f * V * 0.06f + jit.x) + f * jit.y;
                        EmitFoam(At(x + Random.value * 0.1f * Mathf.Min(L, 2 * B), y, cfg.foamLift * 1.2f) + Smear(), pv, ps * Random.Range(1.0f, 1.8f),
                                 cfg.foamLife * Random.Range(0.9f, 1.4f), Mathf.Min(1, a * 1.2f), light, 1.8f, Random.Range(-10f, 10f));
                    }
                }
                // spray from the wet bows' chines (the planing hull's spray root): small droplets, and soft blobs stretched
                // along their flight that read as the white curtain; mist lagging the hull, low over the water
                // (a long hull's bow wave breaks only at a Froude number on its length of ~0.2 (spray) and ~0.25 (mist):
                // a 70 m displacement hull at 3-5 m/s throws none, a small boat's onsets are the settings')
                float froudeV = Mathf.Sqrt(9.81f * L);
                float sprayFrom = Mathf.Max(cfg.sprayMinSpeed, 0.2f * froudeV), mistFrom = Mathf.Max(cfg.mistMinSpeed, 0.25f * froudeV);
                if (V > sprayFrom)
                {
                    float sp = Mathf.Min((V - sprayFrom) / 10f, 1.5f) * strength * hw, hgt = cfg.sprayHeight;
                    foreach (Bow b in h.bows)
                    {
                        float q = sp * b.w * (b.beam + 0.2f), size = Mathf.Sqrt(b.beam + 0.3f);
                        int n = Count(dt * cfg.sprayRate * q);
                        for (int i = 0; i < n; i++)
                        {
                            float x = b.x - Random.value * Random.value * 0.25f * b.len;
                            if (Hidden(x, b.cut.RoofEdge(x, b.side) + b.side * 0.3f, b.cut))
                                continue;
                            Vector3 pv = s * b.side * V * Random.Range(0.1f, 0.3f) + up * V * Random.Range(0.05f, 0.2f) * hgt + f * V * Random.Range(-0.05f, 0.2f)
                                         + Random.insideUnitSphere * 0.4f;
                            EmitSpray(At(x, b.cut.Edge(x, b.side), 0.02f) + Smear(), pv, Random.Range(0.08f, 0.2f) * size, Random.Range(0.5f, 1.2f), Tint(light, 0.75f));
                        }
                        n = Count(dt * cfg.sheetRate * q);
                        for (int i = 0; i < n; i++)
                        {
                            float x = b.x - Random.value * Random.value * 0.25f * b.len;
                            if (Hidden(x, b.cut.RoofEdge(x, b.side) + b.side * 0.3f, b.cut))
                                continue;
                            Vector3 pv = s * b.side * V * Random.Range(0.08f, 0.22f) + up * V * Random.Range(0.1f, 0.24f) * hgt + f * V * Random.Range(0f, 0.25f)
                                         + Random.insideUnitSphere * 0.3f;
                            EmitSheet(At(x, b.cut.Edge(x, b.side), 0.05f) + Smear(), pv, Random.Range(0.2f, 0.5f) * size, Random.Range(0.7f, 1.5f), Tint(light, cfg.sheetAlpha));
                        }
                    }
                }
                if (V > mistFrom)
                {
                    float mp = Mathf.Min((V - mistFrom) / 10f, 1.5f) * strength * hw;
                    foreach (Bow b in h.bows)
                    {
                        int m = Count(dt * cfg.mistRate * mp * b.w * (b.beam + 0.2f));
                        for (int i = 0; i < m; i++)
                        {
                            float back = Random.value * 0.25f * b.len, outBy = Random.Range(0f, 0.4f);
                            if (Hidden(b.x - back, b.cut.RoofEdge(b.x - back, b.side) + b.side * 0.3f, b.cut))
                                continue;
                            Vector3 pv = f * V * Random.Range(0.2f, 0.5f) + s * b.side * V * Random.Range(0.03f, 0.12f) + up * cfg.mistRise * Random.Range(0.4f, 1.6f);
                            var ep = new ParticleSystem.EmitParams
                            {
                                position = MistAt(b, back, outBy, Random.Range(0f, 0.4f)),
                                velocity = pv,
                                startSize = Random.Range(1.0f, 2.0f) * Mathf.Sqrt(b.beam + 0.3f),
                                startLifetime = cfg.mistLife * Random.Range(0.7f, 1.3f),
                                startColor = Tint(light, cfg.mistAlpha),   // alpha replaced by height each frame
                            };
                            mist.Emit(ep, 1);
                            emittedMist++;
                        }
                    }
                }
            }

            if (log)
            {
                var sb = new System.Text.StringBuilder();
                foreach (Hull h in hulls.Values)
                    sb.Append($" [L {h.fMax - h.fMin:F2} B {h.sMax - h.sMin:F2} area {h.area:F2} draft {h.draft:F2} wl {h.wl / h.n:+0.00;-0.00} at {h.fMin:F1}..{h.fMax:F1}, {h.sMin:F1}..{h.sMax:F1}; bows {string.Join(" ", h.bows.Select(b => $"({b.x:F1},{b.y:F1} w{b.w:F2})"))}]");
                var cs = new System.Text.StringBuilder();
                foreach (Cut c in cuts)
                    cs.Append($" <{c.pf0:F1}..{c.pf1:F1} roof {c.rf0:F1}..{c.rf1:F1} {c.rlo[Bins / 2]:F1}..{c.rhi[Bins / 2]:F1} line {c.line} s {c.sMin:F1}..{c.sMax:F1} front {c.lo[Bins - 1]:F1}..{c.hi[Bins - 1]:F1} mid {c.lo[Bins / 2]:F1}..{c.hi[Bins / 2]:F1} w{c.w:F2}>");
                Debug.Log($"[WaterWakes] cuts:{cs}");
                var far = new System.Text.StringBuilder();
                foreach (Part p in v.parts)
                    if (p.rb != null)
                        far.Append($" {p.partInfo.name}: rb CoM {(p.rb.worldCenterOfMass - p.transform.position).magnitude:F1} m off, {(p.rb.worldCenterOfMass - origin).magnitude:F1} m from vessel CoM, mass {p.rb.mass:F1};");
                Debug.Log($"[WaterWakes] {v.vesselName} CoM alt {altO:F1} (vessel alt {v.altitude:F1}), root {(v.rootPart.transform.position - origin).magnitude:F1} m away:{far}");
                Debug.Log($"[WaterWakes] {v.vesselName}: {V:F1} m/s, load {st.load:F2} (draft {draftMax:F2} / rest {st.restDraft:F2}), area {st.area:F2} / rest {st.restArea:F2}, planing {planing:F2}, strength {strength:F2}; " +
                          $"{cuts.Count} wet parts, {hulls.Count} hulls{sb}; emitted foam {emittedFoam} spray {emittedSpray} (splash {emittedSplash}) mist {emittedMist}; " +
                          $"alive foam {foam.particleCount} spray {spray.particleCount} mist {mist.particleCount}");
                emittedFoam = emittedSpray = emittedMist = emittedSplash = 0;
            }
        }

        private void EmitSpray(Vector3 pos, Vector3 vel, float size, float life, Color32 color)
        {
            var ep = new ParticleSystem.EmitParams { position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color };
            spray.Emit(ep, 1);
            emittedSpray++;
        }

        private void EmitSheet(Vector3 pos, Vector3 vel, float size, float life, Color32 color)
        {
            var ep = new ParticleSystem.EmitParams
            {
                position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color, rotation = Random.value * 360f,
            };
            sheet.Emit(ep, 1);
            emittedSpray++;
        }

        private void Gravity(Vector3 up)
        {
            Vector3 g = -up * 9.81f;
            foreach (ParticleSystem ps in new[] { spray, sheet, skirt })
            {
                var fol = ps.forceOverLifetime;
                fol.x = g.x;
                fol.y = g.y;
                fol.z = g.z;
            }
        }

        // the Kelvin wake (WaveSource): from the foremost surface hull's bow, along the track it has run (in a turn the wake
        // follows the curve), as far back as it has run; its waves' height ~ sqrt(beam x draft at rest) (the water it pushes
        // aside) with the wake's strength, easing in over ~1.5 s and out over ~4 s
        private void Waves(CraftState st, CelestialBody body, Vector3 origin, Vector3 up, Vector3 f, float V, float dt, float strength, float g,
                           WakeSettings cfg)
        {
            float fBow = float.MinValue, fStern = float.MaxValue, beam = 0, wlBow = 0;
            foreach (Hull h in hulls.Values)
            {
                if (!h.line)
                    continue;
                if (h.fMax > fBow)
                {
                    fBow = h.fMax;
                    wlBow = h.wl / h.n;
                }
                fStern = Mathf.Min(fStern, h.fMin);
                beam += h.sMax - h.sMin;
            }
            if (st.waveDir == Vector3.zero)
                st.waveDir = f;
            Vector3 prev = st.waveDir;
            st.waveDir = Vector3.ProjectOnPlane(Vector3.Slerp(st.waveDir, f, Mathf.Clamp01(dt / 0.6f)), up).normalized;
            st.turn = Mathf.Lerp(st.turn, Vector3.Angle(prev, st.waveDir) * Mathf.Deg2Rad / dt, Mathf.Clamp01(dt / 0.5f));
            st.trail = Mathf.Min(st.trail + V * dt, cfg.waveTrail);
            bool on = fBow > float.MinValue && V > cfg.minSpeed;
            Vector3 bow = on ? origin + f * fBow + up * wlBow : origin;
            // a point every spacing m: a tenth of the turn's radius (small kinks), at most a 40th of the wake's length
            float spacing = Mathf.Clamp(0.1f * V / Mathf.Max(st.turn, 1e-4f), 2f, Mathf.Max(2f, cfg.waveTrail / 40f));
            if (on)
                Track(st, body, bow, V, dt, spacing);
            float target = on ? 0.5f * Mathf.Sqrt(Mathf.Max(0.01f, beam * st.restDraft)) * Mathf.Clamp01(strength) * Mathf.Clamp01((V - cfg.minSpeed) / 2f) : 0;
            float hWas = st.waveH;
            st.waveH = Mathf.Lerp(st.waveH, target, Mathf.Clamp01(dt / (target > st.waveH ? 1.5f : 4f)));
            float L = Mathf.Max(0.5f, fBow - fStern);

            // the wake's memory: the pattern's speed follows the craft's within ~a wave period (2 pi V / g); a craft
            // slowing faster than that (to under 0.8 of it) leaves the pattern it made running on: Ghost
            float period = 2 * Mathf.PI * Mathf.Max(st.memV, 1f) / g;
            // (rising over half a period: a pattern takes time to build, and a one-frame jump in speed (a craft put on
            // the water, a jolt) is no wake to leave behind)
            st.memV += (V - st.memV) * Mathf.Clamp01(dt / (V >= st.memV ? 0.5f * period : period));
            if (st.ghostT < 0 && cfg.waves && hWas > 1e-3f && st.memV > cfg.minSpeed + 1 && V < 0.8f * st.memV && st.track.Count > 1)
            {
                st.ghostT = 0;
                st.ghostV = st.memV;
                st.ghostH = hWas;
                st.ghostL = L > 0.5f ? L : st.ghostL;
                st.ghostBeam = beam > 0 ? beam : st.ghostBeam;
                st.ghostTrans = Mathf.Clamp01(1.6f - 1.2f * st.ghostV / Mathf.Sqrt(g * st.ghostL));
                st.ghostDir = st.waveDir;
                Vector3 b0 = on ? bow : (Vector3)body.GetWorldSurfacePosition(st.track[st.track.Count - 1].x, st.track[st.track.Count - 1].y, st.track[st.track.Count - 1].z);
                st.ghostBow = new Vector3d(body.GetLatitude((Vector3d)b0), body.GetLongitude((Vector3d)b0), body.GetAltitude((Vector3d)b0));
                if (st.ghost == null)
                    st.ghost = new CraftState();
                st.ghost.track.Clear();
                st.ghost.track.AddRange(st.track);
                st.ghost.trail = st.trail;
            }
            float ghostFade = 1;
            if (st.ghostT >= 0)
            {
                st.ghostT += dt;
                float gPeriod = 2 * Mathf.PI * st.ghostV / g;
                float tau = 2.5f * gPeriod;
                // (back up to the pattern's speed: it is the craft's own wake again)
                if (V >= 0.95f * st.ghostV || st.ghostT > 3 * tau || !cfg.waves)
                    st.ghostT = -1;
                else
                {
                    // the craft's own waves fade as it falls away from the pattern
                    ghostFade = Mathf.Clamp01(Mathf.InverseLerp(0.55f, 0.8f, V / st.ghostV));
                    Vector3 b0 = body.GetWorldSurfacePosition(st.ghostBow.x, st.ghostBow.y, st.ghostBow.z);
                    Vector3 gBow = b0 + st.ghostDir * (st.ghostV * st.ghostT);
                    st.ghost.trail = Mathf.Min(st.trail + st.ghostV * st.ghostT, cfg.waveTrail + st.ghostV * st.ghostT);
                    float gSpacing = Mathf.Max(2f, cfg.waveTrail / 40f);
                    int gn = TrackWorld(st.ghost, body, gBow, up, gSpacing, out float gRun);
                    float h = st.ghostH * Mathf.Exp(-st.ghostT / tau);
                    if (gn > 1 && h > 1e-3f)
                        WaveSources.Add(new WaveSource
                        {
                            pos = gBow, dir = st.ghostDir, speed = st.ghostV, height = h, length = st.ghostL,
                            trail = Mathf.Min(st.ghost.trail, gRun + gSpacing), transverse = st.ghostTrans, beam = st.ghostBeam,
                            track = st.ghost.trackWorld, trackCount = gn, front = 0.5f * st.ghostV * st.ghostT,
                            simShare = 1 - SimKelvinBlend(st.ghostV, cfg),
                        });
                }
            }

            if (!on || st.waveH * ghostFade < 1e-3f || !cfg.waves)
                return;
            int n = TrackWorld(st, body, bow, up, spacing, out float run);
            WaveSources.Add(new WaveSource
            {
                pos = bow, dir = st.waveDir, speed = V, height = st.waveH * ghostFade, length = L, trail = Mathf.Min(st.trail, run + spacing),
                transverse = Mathf.Clamp01(1.6f - 1.2f * V / Mathf.Sqrt(g * L)), beam = beam, track = st.trackWorld, trackCount = n,
                simShare = st.simW,
            });
        }

        // the bow's track: a new point once it is spacing m from the last; the oldest dropped past TrackMax. A jump (the craft
        // moved by hand) starts it, and the wake, afresh
        private static void Track(CraftState st, CelestialBody body, Vector3 bow, float V, float dt, float spacing)
        {
            Vector3d here = new Vector3d(body.GetLatitude((Vector3d)bow), body.GetLongitude((Vector3d)bow), body.GetAltitude((Vector3d)bow));
            if (st.track.Count > 0)
            {
                Vector3d last = st.track[st.track.Count - 1];
                float d = (bow - (Vector3)body.GetWorldSurfacePosition(last.x, last.y, last.z)).magnitude;
                if (d > Mathf.Max(60f, 4 * spacing + 3 * V * dt))
                {
                    st.track.Clear();
                    st.trail = 0;
                }
                else if (d < spacing)
                    return;
            }
            st.track.Add(here);
            while (st.track.Count > TrackMax - 1)
                st.track.RemoveAt(0);
        }

        // the track as the shader takes it, into st.trackWorld: the bow now, then the points back from the newest (one nearer
        // the bow than half a spacing skipped: its direction would be noise), no further than the trail; each with the
        // track's signed curvature there (+ bending toward the segment's + side, cross(up, heading)). run: its length (m)
        private static int TrackWorld(CraftState st, CelestialBody body, Vector3 bow, Vector3 up, float spacing, out float run)
        {
            Vector4[] w = st.trackWorld;
            w[0] = bow;
            int n = 1;
            run = 0;
            for (int i = st.track.Count - 1; i >= 0 && n < TrackMax; i--)
            {
                Vector3d t = st.track[i];
                Vector3 p = body.GetWorldSurfacePosition(t.x, t.y, t.z);
                float seg = Vector3.ProjectOnPlane(p - (Vector3)w[n - 1], up).magnitude;
                if (n == 1 && seg < 0.5f * spacing)
                    continue;
                w[n++] = p;
                run += seg;
                if (run > st.trail + spacing)
                    break;
            }
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 e = Dir(w, i, up), prev = i > 0 ? Dir(w, i - 1, up) : e, next = i < n - 2 ? Dir(w, i + 1, up) : e;
                float len = Vector3.ProjectOnPlane(w[i + 1] - w[i], up).magnitude;
                float span = len + (i > 0 ? 0.5f * Vector3.ProjectOnPlane(w[i] - w[i - 1], up).magnitude : 0)
                                 + (i < n - 2 ? 0.5f * Vector3.ProjectOnPlane(w[i + 2] - w[i + 1], up).magnitude : 0);
                float turn = Vector3.Angle(prev, next) * Mathf.Deg2Rad;
                Vector3 side = Vector3.Cross(up, -e);
                w[i].w = Mathf.Sign(Vector3.Dot(next - prev, side)) * turn / Mathf.Max(span, 0.1f);
            }
            if (n > 1)
                w[n - 1].w = w[n - 2].w;
            return n;
        }

        private static Vector3 Dir(Vector4[] w, int i, Vector3 up) => Vector3.ProjectOnPlane(w[i + 1] - w[i], up).normalized;

        // a foam patch: aspect long along rot deg (0: along the flow, in the foam frame) by 1 / aspect across (as much
        // area as a round one of this size); streaks merge into a wash where round patches read as separate blobs
        private void EmitFoam(Vector3 pos, Vector3 vel, float size, float life, float alpha, float light, float aspect = 1, float rot = float.NaN, bool splash = false)
        {
            bool crisp = splash && splashFoam != null && Cfg.crispSpray;
            if (holes.Count > 0)
            {
                alpha *= splash ? 1 : HoleFactor(pos, 1.3f, 2.4f);   // (a splash patch is held until the zone closes: LayLateFoam)
                if (alpha < 0.01f)
                    return;
            }
            float sa = Mathf.Sqrt(Mathf.Max(1, aspect));
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = vel,
                startSize3D = new Vector3(size / sa, size * sa, size),
                startLifetime = life,
                rotation3D = new Vector3(0, 0, float.IsNaN(rot) ? Random.value * 360f : rot),
                startColor = Tint(light * 0.95f, alpha),
            };
            (crisp ? splashFoam : foam).Emit(ep, 1);
            emittedFoam++;
        }

        private static Color32 Tint(float light, float alpha)
        {
            float l = Mathf.Clamp01(light);
            return new Color(l, l, l, Mathf.Clamp01(alpha));
        }

        private int Root(int i)
        {
            while (cuts[i].group != i)
                i = cuts[i].group;
            return i;
        }

        private static void Add(ref Cut cut, float x, float y, float depth, float wl)
        {
            if (cut == null)
                cut = new Cut { fMin = x, fMax = x, sMin = y, sMax = y, draft = depth, wl = wl };
            else
            {
                cut.fMin = Mathf.Min(cut.fMin, x);
                cut.fMax = Mathf.Max(cut.fMax, x);
                cut.sMin = Mathf.Min(cut.sMin, y);
                cut.sMax = Mathf.Max(cut.sMax, y);
                cut.draft = Mathf.Max(cut.draft, depth);
            }
        }

        // the submerged share of a box whose height is h0 + a u + b v + c w over u, v, w in [-1, 1]: a 4x4x4 sample, each
        // sample a cell smoothed over its height span, so the share changes smoothly as the box moves
        private static float Submerged(float h0, float a, float b, float c)
        {
            float span = Mathf.Max(1e-3f, (Mathf.Abs(a) + Mathf.Abs(b) + Mathf.Abs(c)) * 0.5f), sum = 0;
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    for (int k = 0; k < 4; k++)
                    {
                        float h = h0 + a * (i * 0.5f - 0.75f) + b * (j * 0.5f - 0.75f) + c * (k * 0.5f - 0.75f);
                        sum += Mathf.Clamp01(0.5f - h / span);
                    }
            return sum / 64f;
        }

        // a splash around a part's waterline: entering, water thrown up and out round it; leaving (a breach), a plume
        // thrown up off it; droplets, sheet blobs and mist, as much as it is fast past splashMinSpeed
        internal static bool SplashDebug = false;
        private void EmitSplash(Splash sp, Vector3 origin, Vector3 f, Vector3 s, Vector3 up, float dt, float light, WakeSettings cfg)
        {
            if (sp.domed && !sp.outOf)
                return;
            float v = Mathf.Min(sp.speed, cfg.splashMaxSpeed), over = v - cfg.splashMinSpeed;
            float perim = 2 * (sp.L + sp.B);
            float rate = cfg.splashRate * over * perim * cfg.intensity * (sp.outOf ? cfg.breachSize : 1);
            // (a blunt hit's crown thinned: its sheet carries the water out, not up (Apollo 15's crown slight), as much as
            // its waterline spread faster than it went in: a heat shield or a flat end 0.4, a ball ~0.65, a wedge all of it)
            float crownShare = sp.entry && !sp.outOf ? 1 - 0.8f * Mathf.Clamp01((sp.spreadMax / Mathf.Max(sp.eU, 0.1f) - 1) / 0.5f) : 1;
            int n = Mathf.Min(Count(dt * rate * (sp.entry ? 1.4f : 1) * crownShare), sp.entry ? 400 : 200);
            float big = Mathf.Clamp(Mathf.Sqrt(Mathf.Sqrt(sp.L * sp.B) / 2), 0.7f, 3f);   // (bigger blobs from a bigger splash, not ever more of them)
            if (SplashDebug && sp.since > 0.15f && sp.since < 0.6f)
                Debug.Log($"[WaterWakes] dbg splash t {sp.since:F2} fc {sp.fc:F2} sc {sp.sc:F2} L {sp.L:F2} B {sp.B:F2} wl {sp.wl:F2} speed {sp.speed:F1} outOf {sp.outOf} entry {sp.entry} domed {sp.domed} n {n} vel {sp.vel.magnitude:F1}");
            for (int i = 0; i < n; i++)
            {
                float th = Random.value * 2 * Mathf.PI, r = sp.outOf ? Mathf.Sqrt(Random.value) : Random.Range(0.75f, 1.05f);
                Vector3 outward = f * Mathf.Cos(th) + s * Mathf.Sin(th);
                float px = sp.fc + 0.5f * sp.L * r * Mathf.Cos(th), py = sp.sc + 0.5f * sp.B * r * Mathf.Sin(th);
                if (Hidden(px, py, sp.own))
                    continue;
                Vector3 pos = origin + f * px + s * py + up * (sp.wl + 0.05f);
                Vector3 pv;
                bool tip = false;
                if (sp.entry && !sp.outOf)
                    pos += outward * Random.Range(0f, 0.6f) * Mathf.Sqrt(sp.L * sp.B) * Mathf.Clamp01(sp.since / 0.3f);
                if (sp.outOf)
                {
                    float vb = Mathf.Min(v, 12f);
                    pv = up * vb * cfg.splashHeight * Random.Range(0.5f, 1.5f) + outward * vb * Random.Range(0.15f, 0.5f);
                }
                else if (sp.entry)
                {
                    // an entry from the air (SPH reference runs, D 1.25-5 m, 10-28 m/s): the crown's dense sheet thrown up
                    // and well out (25-55 deg from the vertical), as high as ~1.8 D (its water's 95th percentile 1.6-2.4 D
                    // over the first 1.5 s, much the same from 10 to 28 m/s); over its first 0.35 s fast drops off its
                    // rim, ballistic at U (0.45 + 0.95 r), r the share of the entry speed U the body still has
                    tip = sp.since < 0.35f && Random.value < 0.5f * (1 - sp.since / 0.35f);
                    float ang, v0;
                    if (tip)
                    {
                        ang = Random.Range(15f, 78f) * Mathf.Deg2Rad;
                        v0 = sp.eU * (0.45f + 0.95f * sp.keep) * Random.Range(0.3f, 0.95f) * cfg.splashSheetSpeed / 0.45f;
                    }
                    else
                    {
                        float hc = 1.8f * sp.eD * Mathf.Clamp01(sp.eU / 12f) * cfg.splashHeight * cfg.splashSheetSpeed / 0.45f;
                        ang = Random.Range(25f, 55f) * Mathf.Deg2Rad;
                        float u = Random.value;
                        v0 = Mathf.Sqrt(2 * 9.81f * hc * (0.05f + 0.95f * u * u)) / Mathf.Cos(ang);   // (most of it low: a dense base, a few up to hc)
                    }
                    pv = up * v0 * Mathf.Cos(ang) + outward * v0 * Mathf.Sin(ang);
                }
                else
                {
                    // the crown: a sheet thrown up off the waterline at a fraction of the entry speed (~0.25 for small
                    // spheres, Aristoff & Bush 2009; the first ejecta faster than the body, Thoroddsen et al. 2004),
                    // flaring out 10-40 deg from the vertical; a fifth, the ejecta, lower and faster
                    bool ejecta = Random.value < 0.2f;
                    float k = cfg.splashSheetSpeed * Random.Range(0.45f, 1.6f) * (ejecta ? 1.3f : 1f);
                    float ang = (ejecta ? Random.Range(45f, 70f) : Random.Range(10f, 40f)) * Mathf.Deg2Rad;
                    float v0 = v * k * cfg.splashHeight;
                    pv = up * v0 * Mathf.Cos(ang) + outward * v0 * Mathf.Sin(ang);
                }
                pv += sp.vel * Random.Range(0.3f, 0.8f) + Random.insideUnitSphere * 0.5f;
                // (each lives until it falls back to the water)
                float fall = 2 * Mathf.Max(0, Vector3.Dot(pv, up)) / 9.81f + 0.25f;
                if (tip)
                    EmitSpray(pos, pv, Random.Range(0.12f, 0.26f) * big, fall * Random.Range(0.9f, 1.05f), Tint(light, 0.7f));
                else if (Random.value < (sp.entry ? 0.75f : 0.7f))
                    EmitSpray(pos, pv, Random.Range(0.1f, 0.25f) * big, fall * Random.Range(0.85f, 1.05f), Tint(light, sp.entry ? 0.6f : 0.75f));
                else
                    // (an entry's crown: fewer of the soft sheet blobs, fainter: seen edge on, those flying at or away from the
                    // camera piled into a white blob over the body; SPH's crown reads as drops)
                    EmitSheet(pos, pv * 0.9f, Random.Range(0.25f, 0.55f) * big, 0.9f * fall * Random.Range(0.85f, 1.05f), Tint(light, cfg.sheetAlpha * (sp.entry ? 0.8f : 0.9f)));
                emittedSplash++;
            }
            // the spray skirt (an entry's first 0.25 s): the sheet the spreading wetted area throws off along the body's
            // surface, as fast as twice the waterline's spreading speed (Wagner's jet; a flat end's whole area at once: as
            // fast as it goes, capped at 6 U and 60 m/s), as much as the wetted area grew, a white disc of foam on the water
            // out to where it lands. Its angle the body's slope at the waterline, tan = U / spread (a heat shield's edge
            // 25 deg), a tilted body's flat and slight on its low side, the big fast sheet off its high one.
            // (Basilisk, two-phase VOF, an upright capsule at 9.4 m/s: launched at 5-7 U along the shield's edge, the bulk
            // at 40-55 deg, its tip 3 D across by 0.1 s and 5 D by 0.3 s; the life here short for that, no air drag)
            if (sp.entry && !sp.outOf && sp.skirtArea > 0 && cfg.skirtAmount > 0)
            {
                float vs = Mathf.Min(Mathf.Clamp(2.4f * sp.skirtSpread * cfg.skirtSpeed, sp.eU, 6f * sp.eU), 60f);
                // (a face that hit flat, most of its outline wet in one frame: its spread only frame-limited, the sheet flat)
                float slope = Mathf.Atan2(sp.eU, Mathf.Max(sp.spreadMax, sp.skirtSpread, 0.2f * sp.eU)) * Mathf.Rad2Deg
                              * (1 - 0.85f * sp.flat);
                // (a tilted body throws most of it off its HIGH side, the side that hits later (Apollo 15's footage): its
                // contact line runs from the low edge across the whole shield, nearly flat to the water, its jet fastest that
                // way, leaving off the high edge with the air squeezed out of the closing gap; its low edge, the backshell
                // rising at once past it, throws a little, low and slow. As much again in all)
                Vector2 low = sp.tilt;
                float lean = low.magnitude;
                // (as much as the waterline spread faster than the body went in: a flat end or a heat shield's whole area
                // at once, a wedge's or a ball's by degrees, barely)
                // (its waterline's peak spread measured: a wedge 0.73 U, a cylinder on its side 1.17, a ball 1.30, a flat end
                // 1.90 (all of it in a frame), the capsule 2.19)
                float blunt = Mathf.Clamp01(sp.skirtSpread / Mathf.Max(sp.eU, 0.1f) - 1f);
                int ns = Mathf.Min(Count(sp.skirtArea * 240 * (1 + lean) * blunt * Mathf.Clamp01(sp.eU / 8f) * cfg.skirtAmount * cfg.intensity), 1800);
                if (ns > 0 && splashVessel != null && !holes.Exists(h => h.v == splashVessel && Time.time - h.t0 < 3))
                {
                    holes.Add(new Hole { v = splashVessel, r = 0.75f * sp.eD, t0 = Time.time, shift = -(f * sp.tilt.x + s * sp.tilt.y) * 0.75f * sp.eD });
                    upwells.Add(new Upwell { v = splashVessel, eD = sp.eD, t0 = Time.time });
                }
                for (int i = 0; i < ns; i++)
                {
                    // (a starburst: two thirds of it in ~14 rays, their directions fixed for the entry (from its speed and
                    // size), the rest between them)
                    float th = Random.value * 2 * Mathf.PI;
                    if (Random.value < 0.67f)
                    {
                        int seed = Mathf.RoundToInt(sp.eU * 977 + sp.eD * 613), ray = Random.Range(0, 14);
                        float baseTh = (ray + Frac(Mathf.Sin(seed * 12.9898f + ray * 78.233f) * 43758.55f) * 0.8f) * (2 * Mathf.PI / 14);
                        th = baseTh + Random.Range(-0.06f, 0.06f);
                    }
                    float downhill = low.x * Mathf.Cos(th) + low.y * Mathf.Sin(th);
                    if (Random.value * (1 + 1.2f * lean) > 1 - downhill)
                    {
                        th += Mathf.PI;
                        downhill = -downhill;
                    }
                    Vector3 outward = f * Mathf.Cos(th) + s * Mathf.Sin(th);
                    // (lumpy round the splash: two low harmonics, phases fixed for the entry)
                    float lumpSeed = sp.eU * 0.731f + sp.eD * 1.913f;
                    float lump = 1 + 0.18f * Mathf.Sin(2 * th + lumpSeed * 5.1f) + 0.14f * Mathf.Sin(3 * th + lumpSeed * 8.7f) + 0.08f * Mathf.Sin(5 * th + lumpSeed * 3.3f);
                    float px = sp.fc + 0.5f * sp.L * Random.Range(0.9f, 1.05f) * Mathf.Cos(th), py = sp.sc + 0.5f * sp.B * Random.Range(0.9f, 1.05f) * Mathf.Sin(th);
                    if (Hidden(px, py, sp.own))
                        continue;
                    Vector3 pos = origin + f * px + s * py + up * (sp.wl + 0.05f);
                    float uphill = Mathf.Max(0, -downhill), down = Mathf.Max(0, downhill);
                    float ang = Mathf.Clamp(slope * (1 - 0.55f * down) * (1 + 0.35f * uphill) * Random.Range(0.75f, 1.25f), 2f, 65f) * Mathf.Deg2Rad;
                    float v0 = vs * Random.Range(0.55f, 1f) * (1 + 0.6f * uphill) * (1 - 0.2f * down) * lump
                               * Mathf.Clamp(1 + 0.6f * Vector3.Dot(outward, Vector3.ProjectOnPlane(sp.vel, up)) / Mathf.Max(sp.eU, 1f), 0.5f, 1.6f);
                    Vector3 pv = outward * v0 * Mathf.Cos(ang) + up * v0 * Mathf.Sin(ang) + sp.vel * 0.5f;
                    // (its sheet slowed by the air, lasting 0.5-1.1 s; the drops torn off it flying free, briefly)
                    float life = Random.Range(0.15f, 0.45f);
                    skirt.Emit(new ParticleSystem.EmitParams
                    {
                        position = pos, velocity = pv, startSize = Random.Range(0.5f, 1.05f) * (1 + 0.8f * uphill) * big,
                        startLifetime = Random.Range(0.8f, 1.6f) * (1 + 0.3f * uphill), startColor = Tint(light, Mathf.Min(1, cfg.sheetAlpha * 2.9f)),
                        rotation = Random.value * 360f,
                    }, 1);
                    emittedSpray++;
                    if (Random.value < 0.35f)
                        EmitSpray(pos, pv * Random.Range(0.8f, 1.1f), Random.Range(0.12f, 0.25f) * big, life * 1.2f, Tint(light, 0.85f));
                    // (a haze of fine spray carried out with the sheet: Apollo's white glowing soft at its edge)
                    if (haze != null && cfg.hazeAlpha > 0 && Random.value < 0.2f)
                    {
                        haze.Emit(new ParticleSystem.EmitParams
                        {
                            position = pos + outward * 0.5f,
                            velocity = pv * 0.6f,
                            startSize = Random.Range(3f, 6f) * big,
                            startLifetime = Random.Range(1.2f, 2.2f),
                            rotation = Random.value * 360f,
                            startColor = Tint(light, cfg.hazeAlpha),
                        }, 1);
                        emittedMist++;
                    }
                    // (the foam it leaves: a white disc on the water out to where the sheet comes down, ~3 D from the
                    // middle at most (Apollo's 6-8 D across by 1.5 s), each patch laid when the sheet gets there (its
                    // drag-slowed flight: t = (e^(k x) - 1) / (k v)), so the white grows out; none within ~0.4 D of the
                    // waterline (Apollo's ring of dark open water round the capsule); lasting: bright ~1 s, gone in several)
                    if (Random.value < 0.3f && lateFoam.Count < 6000)
                    {
                        float vh = Mathf.Max(1, v0 * Mathf.Cos(ang));
                        float inner = (1.1f + 0.7f * uphill) * sp.eD;
                        float reach = Mathf.Lerp(inner, Mathf.Max(inner, Mathf.Min(Mathf.Log(1 + 0.08f * vh * 1.2f) / 0.08f, 2.6f * sp.eD)), Mathf.Sqrt(Random.value));
                        lateFoam.Add(new LateFoam
                        {
                            at = Time.time + (Mathf.Exp(0.08f * reach) - 1) / (0.08f * vh),
                            pos = pos + outward * reach - up * (sp.wl + 0.05f - cfg.foamLift), vel = outward * Mathf.Lerp(0.8f, 0.2f, Mathf.InverseLerp(0.9f * sp.eD, 2.6f * sp.eD, reach)) * Random.Range(0.8f, 1.2f),
                            size = Random.Range(2.6f, 4.4f) * big * Mathf.Lerp(0.45f, 1f, Mathf.InverseLerp(1.1f * sp.eD, 2.1f * sp.eD, reach)), life = cfg.foamLife * Mathf.Lerp(0.25f, 1.1f, Mathf.Pow(Random.value, 0.8f)), alpha = Mathf.Min(1, cfg.foamAlpha * Random.Range(0.45f, 0.95f) * Mathf.Lerp(1f, 0.6f, Mathf.InverseLerp(0.9f * sp.eD, 2.6f * sp.eD, reach))), light = light,
                            // (streaks out from the middle, the sheet's rays where it came down)
                            aspect = Random.Range(1.2f, 2f), rot = SplashRot(up, outward) + Random.Range(-25f, 25f), up = up,
                        });
                    }
                    emittedSplash++;
                }
            }

            // mist: a cloud hanging over the splash
            int m = Mathf.Min(Count(dt * rate * (sp.entry ? 0.012f : 0.04f)), 25);
            for (int i = 0; i < m; i++)
            {
                float th = Random.value * 2 * Mathf.PI, px = sp.fc + 0.5f * sp.L * Mathf.Cos(th), py = sp.sc + 0.5f * sp.B * Mathf.Sin(th);
                if (Hidden(px, py, sp.own))
                    continue;
                var ep = new ParticleSystem.EmitParams
                {
                    position = origin + f * px + s * py + up * (sp.wl + Random.Range(0f, 0.5f)),
                    velocity = up * v * Random.Range(0.1f, 0.4f) * cfg.splashHeight + (f * Mathf.Cos(th) + s * Mathf.Sin(th)) * v * 0.15f + sp.vel * 0.5f,
                    startSize = Random.Range(1.2f, 2.4f) * Mathf.Sqrt(sp.B + 0.3f),
                    startLifetime = cfg.mistLife * Random.Range(0.8f, 1.4f),
                    startColor = Tint(light, cfg.mistAlpha),
                };
                mist.Emit(ep, 1);
                emittedMist++;
            }
        }

        // a cut's waterline profile from this part's waterline points (wpts); with none (wholly under), its footprint's box
        private void Profile(Cut c, bool line)
        {
            c.line = line;
            Bin(rpts, out c.rf0, out c.rf1, c.rlo, c.rhi);
            if (!line)
            {
                c.pf0 = c.fMin;
                c.pf1 = c.fMax;
                for (int i = 0; i < Bins; i++)
                {
                    c.lo[i] = c.sMin;
                    c.hi[i] = c.sMax;
                    c.dep[i] = c.draft;
                }
                return;
            }
            c.pf0 = float.MaxValue;
            c.pf1 = float.MinValue;
            foreach (Vector2 q in wpts)
            {
                c.pf0 = Mathf.Min(c.pf0, q.x);
                c.pf1 = Mathf.Max(c.pf1, q.x);
            }
            for (int i = 0; i < Bins; i++)
            {
                c.lo[i] = float.MaxValue;
                c.hi[i] = float.MinValue;
            }
            float span = Mathf.Max(1e-4f, c.pf1 - c.pf0);
            foreach (Vector2 q in wpts)
            {
                int i = Mathf.Clamp((int)((q.x - c.pf0) / span * Bins), 0, Bins - 1);
                c.lo[i] = Mathf.Min(c.lo[i], q.y);
                c.hi[i] = Mathf.Max(c.hi[i], q.y);
            }
            // each station's draft: its deepest point under the water (a station with none: interpolated between the
            // nearest with some, else the cut's)
            for (int i = 0; i < Bins; i++)
                c.dep[i] = -1;
            foreach (Vector2 q in dpts)
            {
                int i = Mathf.Clamp((int)((q.x - c.pf0) / span * Bins), 0, Bins - 1);
                c.dep[i] = Mathf.Max(c.dep[i], q.y);
            }
            for (int i = 0; i < Bins; i++)
            {
                if (c.dep[i] >= 0)
                    continue;
                int a = i - 1, b = i + 1;
                while (a >= 0 && c.dep[a] < 0)
                    a--;
                while (b < Bins && c.dep[b] < 0)
                    b++;
                c.dep[i] = a >= 0 && b < Bins ? Mathf.Lerp(c.dep[a], c.dep[b], (i - a) / (float)(b - a))
                         : a >= 0 ? c.dep[a] : b < Bins ? c.dep[b] : c.draft;
            }
            // stations with no point: the nearest filled one's
            for (int i = 0; i < Bins; i++)
            {
                if (c.lo[i] <= c.hi[i])
                    continue;
                for (int d = 1; d < Bins; d++)
                {
                    int a = i - d, b = i + d;
                    if (a >= 0 && c.lo[a] <= c.hi[a] && c.lo[a] != float.MaxValue)
                    {
                        c.lo[i] = c.lo[a];
                        c.hi[i] = c.hi[a];
                        break;
                    }
                    if (b < Bins && c.lo[b] <= c.hi[b] && c.lo[b] != float.MaxValue)
                    {
                        c.lo[i] = c.lo[b];
                        c.hi[i] = c.hi[b];
                        break;
                    }
                }
            }
        }

        // points binned along f: their extent, and their lowest and highest s per station (empty stations: the nearest's)
        private static void Bin(List<Vector2> pts, out float f0, out float f1, float[] lo, float[] hi)
        {
            f0 = float.MaxValue;
            f1 = float.MinValue;
            foreach (Vector2 q in pts)
            {
                f0 = Mathf.Min(f0, q.x);
                f1 = Mathf.Max(f1, q.x);
            }
            for (int i = 0; i < Bins; i++)
            {
                lo[i] = float.MaxValue;
                hi[i] = float.MinValue;
            }
            if (pts.Count == 0)
                return;
            float span = Mathf.Max(1e-4f, f1 - f0);
            foreach (Vector2 q in pts)
            {
                int i = Mathf.Clamp((int)((q.x - f0) / span * Bins), 0, Bins - 1);
                lo[i] = Mathf.Min(lo[i], q.y);
                hi[i] = Mathf.Max(hi[i], q.y);
            }
            for (int i = 0; i < Bins; i++)
            {
                if (lo[i] <= hi[i])
                    continue;
                for (int d = 1; d < Bins; d++)
                {
                    int a = i - d, b = i + d;
                    if (a >= 0 && lo[a] <= hi[a])
                    {
                        lo[i] = lo[a];
                        hi[i] = hi[a];
                        break;
                    }
                    if (b < Bins && lo[b] <= hi[b])
                    {
                        lo[i] = lo[b];
                        hi[i] = hi[b];
                        break;
                    }
                }
            }
        }

        // a point on the water under another wet part (seen from above: under the boat): its spray would never be seen
        // a part's entry into the water: dry (under 2% of its volume wet) for 0.3 s, then wet. Over its first 0.1 s its
        // speed into the water (normal to it) and its waterline's size are taken, and from them when its crown domes over
        // (surface seal: t U / D ~ 5.75 past Fr_D ~ 11, Marston 2012, the deep seal's time) and its Impact (the jet)
        private void Entry(Wetting wt, Part p, CelestialBody body, Vector3 origin, Vector3 f, Vector3 s, Vector3 up, float frac, List<Vector2> line,
                           float dt, WakeSettings cfg)
        {
            bool wasDry = wt.dry > 0.3f;
            wt.dry = frac < 0.02f ? wt.dry + dt : 0;
            if (frac < 0.02f)
                return;
            Vector3 pv = p.rb != null ? p.rb.velocity + (Vector3)Krakensbane.GetFrameVelocity() : Vector3.zero;
            float un = Mathf.Max(0, -Vector3.Dot(pv, up));
            wt.un = un;
            if (wasDry)
            {
                wt.enterT = Time.time;
                wt.impacted = false;
                wt.U = un;
                wt.LB = 0;
                wt.lbSkirt = 0;
                wt.spreadMax = 0;
                wt.flat = 0;
                wt.sealAt = float.MaxValue;
                wt.alt0 = FlightGlobals.getAltitudeAtPos((Vector3d)p.transform.position, body);
                float x = 0, y = 0;
                foreach (Vector2 q in line)
                {
                    x += q.x;
                    y += q.y;
                }
                if (line.Count > 0)
                {
                    x /= line.Count;
                    y /= line.Count;
                }
                // (the low side of a tilted body: where it touched, off the middle of its outline seen from above)
                wt.tilt = Vector2.zero;
                if (line.Count > 0 && rpts.Count > 1)
                {
                    float o0 = float.MaxValue, o1 = float.MinValue, p0 = float.MaxValue, p1 = float.MinValue;
                    foreach (Vector2 q in rpts)
                    {
                        o0 = Mathf.Min(o0, q.x);
                        o1 = Mathf.Max(o1, q.x);
                        p0 = Mathf.Min(p0, q.y);
                        p1 = Mathf.Max(p1, q.y);
                    }
                    var off = new Vector2(x - (o0 + o1) * 0.5f, y - (p0 + p1) * 0.5f);
                    float half = 0.25f * ((o1 - o0) + (p1 - p0));
                    if (half > 0.05f)
                        wt.tilt = Vector2.ClampMagnitude(off * (1.3f / half), 1);
                }
                var site = (Vector3d)(origin + f * x + s * y);
                wt.lat = body.GetLatitude(site);
                wt.lon = body.GetLongitude(site);
            }
            // (the spray skirt: the waterline's growth over the first 0.25 s, whether or not the entry is done measuring)
            wt.skirtArea = wt.skirtSpread = 0;
            if (wt.enterT >= 0 && Time.time - wt.enterT < 0.25f && line.Count > 1)
            {
                float a0 = float.MaxValue, a1 = float.MinValue, b0 = float.MaxValue, b1 = float.MinValue;
                foreach (Vector2 q in line)
                {
                    a0 = Mathf.Min(a0, q.x);
                    a1 = Mathf.Max(a1, q.x);
                    b0 = Mathf.Min(b0, q.y);
                    b1 = Mathf.Max(b1, q.y);
                }
                float lb = Mathf.Max(0, (a1 - a0) * (b1 - b0));
                if (lb > wt.lbSkirt)
                {
                    wt.skirtArea = lb - wt.lbSkirt;
                    wt.skirtSpread = (Mathf.Sqrt(lb) - Mathf.Sqrt(wt.lbSkirt)) * 0.5f / Mathf.Max(dt, 1e-3f);
                    wt.lbSkirt = lb;
                    wt.spreadMax = Mathf.Max(wt.spreadMax, wt.skirtSpread);
                    // (flat: its submerged vertices' depths alike across their spread, a few percent of it at most; a flat
                    // face 5 deg off square gets 0.17, a ball or a heat shield far more)
                    float half = 0.25f * ((subF1 - subF0) + (subS1 - subS0));
                    if (subHi >= subLo && half > 0.05f)
                        wt.flat = Mathf.Max(wt.flat, 1 - Mathf.Clamp01(((subHi - subLo) / half - 0.03f) / 0.1f));
                }
            }
            if (wt.enterT < 0 || wt.impacted)
                return;
            wt.U = Mathf.Max(wt.U, un);
            float f0 = float.MaxValue, f1 = float.MinValue, s0 = float.MaxValue, s1 = float.MinValue;
            foreach (Vector2 q in line)
            {
                f0 = Mathf.Min(f0, q.x);
                f1 = Mathf.Max(f1, q.x);
                s0 = Mathf.Min(s0, q.y);
                s1 = Mathf.Max(s1, q.y);
            }
            if (f1 > f0)
                wt.LB = Mathf.Max(wt.LB, (f1 - f0) * (s1 - s0));
            if (Time.time - wt.enterT < 0.1f)
                return;
            wt.impacted = true;
            float U = Mathf.Min(wt.U, cfg.splashMaxSpeed), D = Mathf.Max(0.2f, Mathf.Sqrt(wt.LB));
            if (cfg.debugLog)
                Debug.Log($"[WaterWakes] entry: {p.partInfo?.name} at {U:F1} m/s, D {D:F2} m, waterline spread up to {wt.spreadMax:F1} m/s ({wt.spreadMax / Mathf.Max(U, 0.1f):F2} U), low side {wt.tilt}, flat {wt.flat:F2}");   // (an ellipse's area: pi/4 L B = pi/4 D^2)
            float fr = U * U / (9.81f * D);
            wt.sealAt = fr > 11 ? 5.75f * D / Mathf.Max(U, 0.1f) : float.MaxValue;
            if (U < Mathf.Max(3f, cfg.splashMinSpeed) || fr < 1)
                return;
            // the cavity's pinch-off (SPH: 1.6 sqrt(D / g) (1 + 1.2 g D / U^2), every cylinder within 0.03 s; rounder bodies
            // ~20% sooner)
            impacts.Add(new Impact
            {
                body = body, lat = wt.lat, lon = wt.lon, alt0 = wt.alt0, part = p, U = U, D = D, t = Time.time - wt.enterT,
                jetAt = 1.6f * Mathf.Sqrt(D / 9.81f) * (1 + 1.2f * 9.81f * D / (U * U)),
            });
            if (impacts.Count > 32)
                impacts.RemoveAt(0);
            float heavy = Mathf.Clamp(Mathf.Sqrt((p.mass + (float)p.GetResourceMass()) * 1000f / Mathf.Max(1f, 1000f * D * D * D)), 0.3f, 1.6f);   // (its mass over a D cube of water's)
            rings.Add(new Ring { body = body, lat = wt.lat, lon = wt.lon, D = D, U = U, t = Time.time - wt.enterT, dur = 2.5f * Mathf.Sqrt(D / 2.5f), amp = heavy });
            if (rings.Count > 32)
                rings.RemoveAt(0);
        }

        // each slam's Worthington jet (SPH reference runs): when its cavity pinches off, if the body is past ~2 D deep by then
        // (none from a light body that stops short), a broad cone of water rises where it went in: as high as 0.47 U^2 /
        // 2 g (U its entry speed: ~9 m at 20 m/s, whatever its size), higher when it went in slower (g D / U^2) or is still
        // sinking fast then (a body heavier than the water it displaces); its base ~D across, the middle fastest, fed for
        // ~0.8 s, the top breaking into spray; and when it has fallen back, a weaker ring spreading over the surface
        private void Impacts(float dt, WakeSettings cfg)
        {
            for (int i = impacts.Count - 1; i >= 0; i--)
            {
                Impact im = impacts[i];
                im.t += dt;
                if (im.body == null || im.body != FlightGlobals.currentMainBody || im.t > im.jetAt + 6)
                {
                    impacts.RemoveAt(i);
                    continue;
                }
                // how deep it has gone (a floater is back up by the time its cavity closes)
                im.travel = im.part != null ? Math.Max(im.travel, im.alt0 - FlightGlobals.getAltitudeAtPos((Vector3d)im.part.transform.position, im.body)) : 3 * im.D;
                if (SplashDebug && im.t - dt < 0.6f && im.t >= 0.6f)
                {
                    Vector3 c0 = im.body.GetWorldSurfacePosition(im.lat, im.lon, 0);
                    Vector3 u0 = (c0 - (Vector3)im.body.position).normalized;
                    foreach (var (name, ps) in new[] { ("spray", spray), ("sheet", sheet), ("mist", mist) })
                    {
                        var buf = new ParticleSystem.Particle[ps.particleCount];
                        int cnt = ps.GetParticles(buf), near = 0;
                        float age = 0, vup = 0, vout = 0, size = 0, hgt = 0;
                        for (int q = 0; q < cnt; q++)
                        {
                            Vector3 rel = buf[q].position - c0;
                            float hh = Vector3.Dot(rel, u0);
                            Vector3 hor = rel - u0 * hh;
                            if (hh < 1f || hor.magnitude > 1.5f)
                                continue;
                            near++;
                            age += buf[q].startLifetime - buf[q].remainingLifetime;
                            vup += Vector3.Dot(buf[q].velocity, u0);
                            vout += (buf[q].velocity - u0 * Vector3.Dot(buf[q].velocity, u0)).magnitude;
                            size += buf[q].startSize;
                            hgt += hh;
                        }
                        if (near > 0)
                            Debug.Log($"[WaterWakes] dbg near-axis at 0.6 s: {name} {near} of {cnt}, age {age / near:F2} s, up {vup / near:F1} m/s, out {vout / near:F1}, size {size / near:F2}, height {hgt / near:F1} m");
                    }
                }
                if (!im.jetted && im.t >= im.jetAt)
                {
                    im.jetted = true;
                    double travel = im.travel;
                    float sink = 0;
                    if (im.part != null && im.part.rb != null)
                    {
                        Vector3 site0 = im.body.GetWorldSurfacePosition(im.lat, im.lon, 0);
                        Vector3 up0 = (site0 - (Vector3)im.body.position).normalized;
                        sink = Mathf.Max(0, -Vector3.Dot(im.part.rb.velocity + (Vector3)Krakensbane.GetFrameVelocity(), up0)) / Mathf.Max(im.U, 0.1f);
                    }
                    float slow = 9.81f * im.D / (im.U * im.U);
                    im.strength = Mathf.Clamp01(((float)travel / im.D - 1.8f) / 0.5f);
                    float h = cfg.jetHeight * 0.47f * im.U * im.U / (2 * 9.81f) * (1 + 1.6f * Mathf.Max(0, slow - 0.06f)) *
                              Mathf.Clamp(1 + 6 * Mathf.Max(0, sink - 0.2f), 1, 1.9f) * (0.5f + 0.5f * im.strength);
                    Debug.Log($"[WaterWakes] jet: entry {im.U:F1} m/s, D {im.D:F2} m, at {im.jetAt:F2} s, sank {travel:F1} m ({travel / im.D:F1} D), " +
                              $"sinking {sink:F2} U then: {h:F1} m high, {im.strength:F2} of full");
                    if (im.strength <= 0 || h < 0.3f)
                    {
                        // (SPH: a light body's cavity still collapses ~1 s later, a ring spreading from it)
                        if (travel > 0.8f * im.D)
                            rings.Add(new Ring { body = im.body, lat = im.lat, lon = im.lon, D = im.D, U = im.U, delay = 1.0f,
                                                 dur = 2f * Mathf.Sqrt(im.D / 2.5f), amp = 0.5f * Mathf.Clamp01(((float)travel / im.D - 0.8f) / 0.6f) });
                        impacts.RemoveAt(i);
                        continue;
                    }
                    im.jetSpeed = Mathf.Sqrt(2 * 9.81f * h);
                    im.jetDur = im.jetLeft = 0.8f * Mathf.Sqrt(Mathf.Sqrt(im.D / 2.5f));
                    // (falls back: the middle's apex and as long again down)
                    rings.Add(new Ring { body = im.body, lat = im.lat, lon = im.lon, D = im.D, U = im.U, delay = 0.5f * im.jetDur + 1.2f * im.jetSpeed / 9.81f,
                                         dur = 2f * Mathf.Sqrt(im.D / 2.5f), amp = 0.8f * im.strength * Mathf.Min(1, h / (0.5f * im.D + 4)) });
                }
                if (!im.jetted || im.jetLeft <= 0)
                    continue;
                im.jetLeft -= dt;
                Vector3 site = im.body.GetWorldSurfacePosition(im.lat, im.lon, 0);
                Vector3 up = (site - (Vector3)im.body.position).normalized;
                Vector3 e1 = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized, e2 = Vector3.Cross(up, e1);
                Gravity(up);
                float light = Light(site, up, cfg);
                float done = 1 - Mathf.Clamp01(im.jetLeft / im.jetDur), big = Mathf.Clamp(Mathf.Sqrt(im.D / 2), 0.7f, 3f);
                float R = 0.55f * im.D;
                int n = Mathf.Min(Count(dt * 1800 * im.D * im.strength * cfg.intensity), 250);
                for (int j = 0; j < n; j++)
                {
                    float th = Random.value * 2 * Mathf.PI, rr = R * Mathf.Sqrt(Random.value);
                    Vector3 radial = e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th);
                    // (the cone: the middle fastest, the edge a third of it; the column slowing as it is fed)
                    float vz = im.jetSpeed * (1 - 0.65f * Mathf.Pow(rr / R, 1.5f)) * (1 - 0.5f * done) * Random.Range(0.85f, 1.0f);
                    // (the top breaking into a plume of spray; the edge a skirt flaring out)
                    bool tip = done < 0.5f && rr < 0.6f * R && Random.value < 0.6f;
                    float q = rr / R;
                    Vector3 pv = up * vz + radial * (tip ? vz * Random.Range(0.03f, 0.12f) : vz * Random.Range(0.02f, 0.06f) + im.jetSpeed * q * q * Random.Range(0.08f, 0.25f));
                    Vector3 pos = site + radial * rr + up * 0.1f;
                    float fall = 2 * vz / 9.81f + 0.2f;
                    if (!tip && Random.value < 0.65f)
                        EmitSheet(pos, pv, Random.Range(0.25f, 0.55f) * big, fall * Random.Range(0.85f, 1.0f), Tint(light, cfg.sheetAlpha * 1.4f));
                    else
                        EmitSpray(pos, pv, Random.Range(0.1f, 0.22f) * big, fall * Random.Range(0.9f, 1.05f), Tint(light, 0.8f));
                    emittedSplash++;
                }
            }
        }

        // the rings over the surface: lumps of raised water thrown up a little off the ring as it spreads, and foam left
        private void Rings(float dt, WakeSettings cfg)
        {
            for (int i = rings.Count - 1; i >= 0; i--)
            {
                Ring rg = rings[i];
                rg.t += dt;
                float tt = rg.t - rg.delay;
                if (rg.body == null || rg.body != FlightGlobals.currentMainBody || tt > Mathf.Max(1, cfg.ringLife) * rg.dur || rg.amp <= 0)
                {
                    rings.RemoveAt(i);
                    continue;
                }
                if (tt < 0)
                    continue;
                float T = Mathf.Sqrt(rg.D / 9.81f), k = 1.15f * Mathf.Sqrt(Mathf.Clamp(rg.U / 20f, 0.25f, 2f));
                float r = rg.D * (0.5f + k * Mathf.Sqrt(tt / T)), drdt = rg.D * k * 0.5f / Mathf.Sqrt(Mathf.Max(tt, 0.03f) * T);
                float fade = Mathf.Max(0, 1 - tt / rg.dur), rfade = 1 - tt / (Mathf.Max(1, cfg.ringLife) * rg.dur);   // (the ripples outlast the breaking crest ringLife-fold)
                Vector3 site = rg.body.GetWorldSurfacePosition(rg.lat, rg.lon, 0);
                Vector3 up = (site - (Vector3)rg.body.position).normalized;
                // (its ripples: height falling with time and spread, wavelength growing; whitecapped while its leading crest
                // runs fast and steep)
                float rpA = 0.2f * rg.D * rg.amp * rfade * rfade * Mathf.Sqrt(rg.D / Mathf.Max(r, rg.D)) * cfg.ringWaves;
                float rpL = (0.6f * rg.D + 0.15f * r) * Mathf.Max(0.1f, cfg.ringWavelength);
                float whitecap = Mathf.Clamp01((drdt - cfg.ringWhitecapSpeed) / 3f) * Mathf.Clamp01(rpA * 2 * Mathf.PI / rpL / 0.15f) * fade;
                if (rpA > 0.005f)
                    RippleSources.Add(new RippleSource { pos = site, radius = r, amp = rpA, wavelength = rpL, length = 3f * rpL, foam = whitecap });
                if (cfg.ringWaves <= 0 || whitecap <= 0.01f)
                    continue;
                Vector3 e1 = Vector3.Cross(up, Mathf.Abs(up.y) < 0.9f ? Vector3.up : Vector3.right).normalized, e2 = Vector3.Cross(up, e1);
                Gravity(up);
                float light = Light(site, up, cfg), big = Mathf.Clamp(Mathf.Sqrt(rg.D / 2), 0.7f, 3f);
                // (its particles only as spray off the breaking crest, as much as it breaks)
                int n = Mathf.Min(Count(dt * 80 * 2 * Mathf.PI * r * rg.amp * fade * Mathf.Clamp01(rg.U / 10f) * cfg.intensity * whitecap * Mathf.Min(1, cfg.ringWaves)), 300);
                for (int j = 0; j < n; j++)
                {
                    float th = Random.value * 2 * Mathf.PI;
                    Vector3 radial = e1 * Mathf.Cos(th) + e2 * Mathf.Sin(th);
                    Vector3 pos = site + radial * (r + Random.Range(-0.1f, 0.1f) * rg.D) + up * 0.05f;
                    float vz = Mathf.Sqrt(2 * 9.81f * 0.3f * rg.D * Mathf.Min(rg.amp, 1.3f) * fade * Random.Range(0.15f, 1f));
                    Vector3 pv = up * vz + radial * drdt * Random.Range(0.6f, 1.0f);
                    float clear = HoleFactor(site + radial * r, 1.3f, 2.4f);
                    if (clear > 0.02f)
                        EmitSheet(pos, pv, Random.Range(0.3f, 0.6f) * big, 2 * vz / 9.81f + 0.15f, Tint(light, cfg.sheetAlpha * 1.3f * clear));
                    if (Random.value < 0.25f && !cfg.paintFoam)   // (painted: the whitecap drawn on the crest instead)
                    {
                        if (clear > 0.02f)
                            EmitFoam(site + radial * r + up * cfg.foamLift, radial * drdt * 0.4f, Random.Range(0.5f, 1.0f) * big, cfg.foamLife * Random.Range(0.2f, 0.35f),
                                     cfg.foamAlpha * fade * clear, light);
                    }
                    emittedSplash++;
                }
            }
        }

        private static float Light(Vector3 site, Vector3 up, WakeSettings cfg)
        {
            Vector3 sunDir = ((Vector3)(Planetarium.fetch.Sun.position - (Vector3d)site)).normalized;
            return Mathf.Lerp(0.08f, 1f, Mathf.InverseLerp(-0.1f, 0.35f, Vector3.Dot(sunDir, up))) * cfg.brightness;
        }

        private bool Hidden(float x, float y, Cut own)
        {
            foreach (Cut c in cuts)
                if (c != own && c.UnderRoof(x, y, 0.1f))
                    return true;
            return false;
        }

        // stochastic rounding: the fraction kept on average
        private static float Frac(float x) => x - Mathf.Floor(x);

        private static int Count(float x) => x <= 0 ? 0 : (int)x + (Random.value < x - (int)x ? 1 : 0);

        private Box[] Boxes(Part p)
        {
            if (boxes.TryGetValue(p, out Box[] bs))
                return bs;
            var list = new List<Box>();
            foreach (Collider c in p.GetPartColliders())
            {
                if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy)
                    continue;
                switch (c)
                {
                    case BoxCollider b:
                        list.Add(new Box { t = b.transform, c = b.center, e = b.size * 0.5f });
                        break;
                    case SphereCollider sc:
                        list.Add(new Box { t = sc.transform, c = sc.center, e = Vector3.one * sc.radius });
                        break;
                    case CapsuleCollider cc:
                        Vector3 e = Vector3.one * cc.radius;
                        e[cc.direction] = Mathf.Max(cc.radius, cc.height * 0.5f);
                        list.Add(new Box { t = cc.transform, c = cc.center, e = e });
                        break;
                    case MeshCollider mc when mc.sharedMesh != null:
                        Mesh mesh = mc.sharedMesh;
                        Bounds mb = mesh.bounds;
                        var box = new Box { t = mc.transform, c = mb.center, e = mb.extents };
                        if (mesh.isReadable && mesh.vertexCount <= 20000)
                        {
                            box.verts = mesh.vertices;
                            box.tris = mesh.triangles;
                        }
                        list.Add(box);
                        break;
                }
            }
            bs = list.ToArray();
            boxes[p] = bs;
            return bs;
        }

        private static ParticleSystem Make(string name, Material mat, int max, bool flatOnWater)
        {
            var go = new GameObject(name);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.playOnAwake = false;
            main.loop = true;
            main.startSpeed = 0;
            main.startLifetime = 1;
            main.gravityModifier = 0;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            // keep simulating when no camera sees them: the foam is on a hidden layer while painted, and trails behind the camera live on
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            if (flatOnWater)
                main.startRotation3D = true;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            if (flatOnWater)
            {
                // quads lying on the water: Local alignment, the frame's Z kept along the local vertical
                r.renderMode = ParticleSystemRenderMode.Mesh;
                r.mesh = Quad();
                r.alignment = ParticleSystemRenderSpace.Local;
            }
            else
            {
                r.renderMode = ParticleSystemRenderMode.Billboard;
            }
            r.sortMode = ParticleSystemSortMode.Distance;
            ps.Play();
            FloatingOrigin.RegisterParticleSystem(ps);
            return ps;
        }

        private static void Fade(ParticleSystem ps, float inAt, float holdTo)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0), new GradientColorKey(Color.white, 1) },
                      new[] { new GradientAlphaKey(0, 0), new GradientAlphaKey(1, inAt), new GradientAlphaKey(0.75f, holdTo), new GradientAlphaKey(0, 1) });
            col.color = g;
        }

        private static Shader FindShader(params string[] names)
        {
            foreach (string n in names)
            {
                Shader s = Shader.Find(n);
                if (s != null)
                    return s;
            }
            return null;
        }

        private static Material Mat(Shader sh, Texture2D tex, bool soft = false)
        {
            var m = new Material(sh) { mainTexture = tex };
            if (m.HasProperty("_TintColor"))
                m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
            // (soft: fading out within ~1 m of the scene behind it, a hull's side say; the legacy particle shaders'
            // own soft particles, on the camera's depth texture (LateUpdate asks for it))
            if (soft)
            {
                m.EnableKeyword("SOFTPARTICLES_ON");
                if (m.HasProperty("_InvFade"))
                    m.SetFloat("_InvFade", 1f);
            }
            return m;
        }

        private static Mesh Quad()
        {
            var m = new Mesh { name = "WaterWakes quad" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(-0.5f, 0.5f, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            m.normals = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };   // both faces
            m.RecalculateBounds();
            return m;
        }

        // GLSL smoothstep (Mathf.SmoothStep interpolates between its first two arguments instead)
        private static float Smooth(float e0, float e1, float x)
        {
            float t = Mathf.Clamp01((x - e0) / (e1 - e0));
            return t * t * (3 - 2 * t);
        }

        // foam: a web of bright lines over a faint sheet, fading at the edge
        private const int FoamStages = 8, FoamVariants = 4;

        // foam patches: FoamVariants rows of FoamStages columns, each a ragged round patch that ages from left to right:
        // dense churned white with a little texture, then holes opening in it (fbm thresholded higher and higher), until
        // only the thin veins between bubble cells (Worley F2 - F1) are left, fainter; white with a slight grey mottle
        private static Texture2D FoamTexture()
        {
            const int T = 128;
            int W = T * FoamStages, H = T * FoamVariants;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes foam" };
            var px = new Color32[W * H];
            const int G = 6;   // bubble cells across a patch
            var cell = new Vector2[(G + 2) * (G + 2)];
            for (int row = 0; row < FoamVariants; row++)
            {
                float ox = Random.value * 100, oy = Random.value * 100;
                for (int k = 0; k < cell.Length; k++)
                    cell[k] = new Vector2(Random.value, Random.value);
                for (int j = 0; j < T; j++)
                    for (int i = 0; i < T; i++)
                    {
                        float u = (i + 0.5f) / T, w = (j + 0.5f) / T;
                        // fbm (texture and holes)
                        float n = 0, amp = 0.5f, fr = 3;
                        for (int o = 0; o < 4; o++, amp *= 0.5f, fr *= 2)
                            n += amp * Mathf.PerlinNoise(ox + u * fr, oy + w * fr);
                        n = Mathf.Clamp01((n - 0.47f) * 1.9f + 0.5f);
                        // Worley: the distance to the nearest two cell points; their difference is small along the cell walls
                        float gx = u * G, gy = w * G, f1 = 9, f2 = 9;
                        int cx0 = (int)gx, cy0 = (int)gy;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int qx = cx0 + dx, qy = cy0 + dy;
                                Vector2 pt = cell[(qy + 1) * (G + 2) + qx + 1];
                                float ddx = qx + pt.x - gx, ddy = qy + pt.y - gy, d = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                                if (d < f1)
                                {
                                    f2 = f1;
                                    f1 = d;
                                }
                                else if (d < f2)
                                    f2 = d;
                            }
                        float vein = 1 - Smooth(0.03f, 0.16f, f2 - f1);
                        // a ragged round edge
                        float r = Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (w - 0.5f) * (w - 0.5f)) * 2 + (n - 0.5f) * 0.45f;
                        float fall = 1 - Smooth(0.5f, 0.98f, r);
                        for (int st = 0; st < FoamStages; st++)
                        {
                            float sg = st / (FoamStages - 1f);
                            float th = Mathf.Lerp(0.18f, 0.78f, sg);
                            float body = Smooth(th - 0.09f, th + 0.09f, n) * Mathf.Lerp(1f, 0.3f, sg);
                            float veins = vein * Mathf.Lerp(0.3f, 0.85f, sg) * Smooth(0.15f, 0.5f, n + 0.25f * (1 - sg));
                            float a = fall * Mathf.Clamp01(Mathf.Max(body, veins)) * Mathf.Lerp(1f, 0.8f, sg);
                            byte g = (byte)(255 * (0.86f + 0.14f * n));
                            int x = st * T + i, y = (FoamVariants - 1 - row) * T + j;   // (row 0 at the top, as the sheet counts)
                            px[y * W + x] = new Color32(g, g, g, (byte)(a * 255));
                        }
                    }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // WaterWakes/Foam's atlas (WakeFoam.cginc): 2x2 patch variants, R fbm, G bubble-cell walls, B a ragged round mask,
        // A a grey mottle; the shader erodes R by the patch's age
        private static Texture2D FoamAtlas()
        {
            const int T = 256, G = 9;   // px per variant; bubble cells across a patch
            var tex = new Texture2D(2 * T, 2 * T, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes foam atlas" };
            var px = new Color32[4 * T * T];
            var cell = new Vector2[(G + 2) * (G + 2)];
            for (int var = 0; var < 4; var++)
            {
                float ox = Random.value * 100, oy = Random.value * 100, mx = Random.value * 100;
                for (int k = 0; k < cell.Length; k++)
                    cell[k] = new Vector2(Random.value, Random.value);
                int x0 = (var % 2) * T, y0 = (var / 2) * T;
                for (int j = 0; j < T; j++)
                    for (int i = 0; i < T; i++)
                    {
                        float u = (i + 0.5f) / T, w = (j + 0.5f) / T;
                        float n = 0, amp = 0.5f, fr = 3;
                        for (int o = 0; o < 5; o++, amp *= 0.5f, fr *= 2)
                            n += amp * Mathf.PerlinNoise(ox + u * fr, oy + w * fr);
                        n = Mathf.Clamp01((n - 0.47f) * 1.9f + 0.5f);
                        float gx = u * G, gy = w * G, f1 = 9, f2 = 9;
                        int cx0 = (int)gx, cy0 = (int)gy;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int qx = cx0 + dx, qy = cy0 + dy;
                                Vector2 pt = cell[(qy + 1) * (G + 2) + qx + 1];
                                float ddx = qx + pt.x - gx, ddy = qy + pt.y - gy, d = Mathf.Sqrt(ddx * ddx + ddy * ddy);
                                if (d < f1)
                                {
                                    f2 = f1;
                                    f1 = d;
                                }
                                else if (d < f2)
                                    f2 = d;
                            }
                        float vein = 1 - Smooth(0.03f, 0.16f, f2 - f1);
                        float r = Mathf.Sqrt((u - 0.5f) * (u - 0.5f) + (w - 0.5f) * (w - 0.5f)) * 2 + (n - 0.5f) * 0.45f;
                        float fall = 1 - Smooth(0.45f, 0.97f, r);
                        float mot = Mathf.PerlinNoise(mx + u * 7, mx + w * 7);
                        px[(y0 + j) * 2 * T + x0 + i] = new Color32((byte)(n * 255), (byte)(vein * 255), (byte)(fall * 255), (byte)(mot * 255));
                    }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // a 2x2 sheet of droplet clusters: a dozen-odd fine round drops each, of mixed sizes, scattered in a soft round
        private static Texture2D DropletTexture()
        {
            const int T = 64;
            var tex = new Texture2D(2 * T, 2 * T, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes droplets" };
            var a = new float[4 * T * T];
            for (int var = 0; var < 4; var++)
            {
                int x0 = (var % 2) * T, y0 = (var / 2) * T, drops = Random.Range(9, 16);
                for (int k = 0; k < drops; k++)
                {
                    Vector2 c = Random.insideUnitCircle * 0.36f;
                    float rad = Mathf.Lerp(1.2f, 3.4f, Random.value * Random.value) / T, bright = Random.Range(0.6f, 1f);
                    for (int j = 0; j < T; j++)
                        for (int i = 0; i < T; i++)
                        {
                            float u = (i + 0.5f) / T - 0.5f - c.x, w = (j + 0.5f) / T - 0.5f - c.y;
                            float d = Mathf.Sqrt(u * u + w * w) / rad;
                            int idx = (y0 + j) * 2 * T + x0 + i;
                            a[idx] = Mathf.Max(a[idx], bright * (1 - Smooth(0.55f, 1f, d)));
                        }
                }
            }
            var px = new Color32[a.Length];
            for (int i = 0; i < a.Length; i++)
                px[i] = new Color32(255, 255, 255, (byte)(a[i] * 255));
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // a 2x2 sheet of torn water: a soft blob broken by fbm into ragged fingers and holes, denser at its heart
        private static Texture2D SheetTexture()
        {
            const int T = 96;
            var tex = new Texture2D(2 * T, 2 * T, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes sheet" };
            var px = new Color32[4 * T * T];
            for (int var = 0; var < 4; var++)
            {
                int x0 = (var % 2) * T, y0 = (var / 2) * T;
                float ox = Random.value * 100, oy = Random.value * 100;
                for (int j = 0; j < T; j++)
                    for (int i = 0; i < T; i++)
                    {
                        float u = (i + 0.5f) / T - 0.5f, w = (j + 0.5f) / T - 0.5f;
                        float n = 0, amp = 0.5f, fr = 2.5f;
                        for (int o = 0; o < 4; o++, amp *= 0.5f, fr *= 2.1f)
                            n += amp * Mathf.PerlinNoise(ox + u * fr, oy + w * fr);
                        float r = Mathf.Sqrt(u * u + w * w) * 2;
                        float core = 1 - Smooth(0.2f, 1f, r + (0.5f - n) * 0.8f);
                        float torn = Smooth(0.38f, 0.58f, n + 0.35f * (1 - r));
                        float a = core * Mathf.Lerp(0.35f, 1f, torn);
                        byte g = (byte)(255 * (0.88f + 0.12f * n));
                        px[(y0 + j) * 2 * T + x0 + i] = new Color32(g, g, g, (byte)(Mathf.Clamp01(a) * 255));
                    }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // each particle one of a 2x2 sheet's four frames, kept for its life
        private static void RandomFrame(ParticleSystem ps)
        {
            var tsa = ps.textureSheetAnimation;
            tsa.enabled = true;
            tsa.mode = ParticleSystemAnimationMode.Grid;
            tsa.numTilesX = 2;
            tsa.numTilesY = 2;
            tsa.animation = ParticleSystemAnimationType.WholeSheet;
            tsa.cycleCount = 1;
            tsa.frameOverTime = new ParticleSystem.MinMaxCurve(0f, 0.999f);
        }

        private static Texture2D DotTexture(int N, float hard)
        {
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes dot" };
            var px = new Color32[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float u = (i + 0.5f) / N - 0.5f, w = (j + 0.5f) / N - 0.5f;
                    float r = Mathf.Sqrt(u * u + w * w) * 2;
                    float a = 1 - Smooth(hard, 1f, r);
                    px[j * N + i] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // a 2x2 sheet of soft streaked blobs (the skirt's): a smooth oval fading to its edge, finely streaked along its
        // length (the stretched billboard's), each tile its own streaks
        private static Texture2D StreakTexture()
        {
            const int T = 64;
            var tex = new Texture2D(2 * T, 2 * T, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes streak" };
            var px = new Color32[4 * T * T];
            for (int tile = 0; tile < 4; tile++)
            {
                int ox = (tile & 1) * T, oy = (tile >> 1) * T;
                float seed = tile * 17.3f;
                for (int j = 0; j < T; j++)
                    for (int i = 0; i < T; i++)
                    {
                        float u = (i + 0.5f) / T - 0.5f, w = (j + 0.5f) / T - 0.5f;
                        float r = Mathf.Sqrt(u * u + w * w) * 2;
                        // streaks: noise across the length (u along it, w across), barely along it
                        float st = Mathf.PerlinNoise(seed + w * 22, seed + u * 2.5f);
                        float a = Mathf.Pow(Mathf.Clamp01(1 - r * r), 1.3f) * (0.45f + 0.55f * st);
                        px[(oy + j) * 2 * T + ox + i] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255));
                    }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        // the crisp sheet's blobs (WaterWakes/Spray): a 2x2 sheet, each R a height field (a round body, finely streaked
        // along its length (u, the stretched billboard's), ragged at its edge) eroded by a rising threshold, G the streaks
        private static Texture2D ErodeTexture()
        {
            const int T = 128;
            var tex = new Texture2D(2 * T, 2 * T, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes erode" };
            var px = new Color32[4 * T * T];
            for (int tile = 0; tile < 4; tile++)
            {
                int ox = (tile & 1) * T, oy = (tile >> 1) * T;
                float seed = 3.7f + tile * 23.1f;
                for (int j = 0; j < T; j++)
                    for (int i = 0; i < T; i++)
                    {
                        float u = (i + 0.5f) / T - 0.5f, w = (j + 0.5f) / T - 0.5f;
                        float r = Mathf.Sqrt(u * u * 0.8f + w * w * 1.25f) * 2;
                        float st = Mathf.PerlinNoise(seed + w * 7, seed + u * 3f);
                        float fine = Mathf.PerlinNoise(seed * 2 + u * 9, seed * 2 + w * 9) - 0.5f;
                        float body = Mathf.Pow(Mathf.Clamp01(1 - r * r), 0.7f);
                        float h = body * (0.5f + 0.5f * st) + 0.18f * fine * body;
                        px[(oy + j) * 2 * T + ox + i] = new Color32((byte)(Mathf.Clamp01(h) * 255), (byte)(st * 255), 255, 255);
                    }
            }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }

        private static Texture2D MistTexture()
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes mist" };
            var px = new Color32[N * N];
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float u = (i + 0.5f) / N - 0.5f, w = (j + 0.5f) / N - 0.5f;
                    float r = Mathf.Sqrt(u * u + w * w) * 2;
                    float n = Mathf.PerlinNoise(u * 5 + 3, w * 5 + 7);
                    float a = Mathf.Pow(Mathf.Clamp01(1 - r * r), 1.5f) * (0.6f + 0.4f * n);
                    px[j * N + i] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
        }
    }
}
