using System;
using System.Collections.Generic;
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
        }

        private class Cut
        {
            public float fMin, fMax, sMin, sMax, draft, wl;
            public int group;
        }

        private class Hull
        {
            public float fMin = float.MaxValue, fMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue, draft, wl;
            public int n;
        }

        private class CraftState
        {
            public float restDraft;   // the deepest hull's draft afloat at rest (or the deepest seen, until it has rested)
            public float load = 1;    // smoothed draft / restDraft
        }

        private static WakeSettings Cfg => WakeSettings.Current;

        private ParticleSystem foam, spray, mist;

        public static WakeFx Instance { get; private set; }
        /// <summary>The foam particles (OceanFoamPainter renders them into its foam map, on a layer the cameras skip).</summary>
        public ParticleSystem Foam => foam;
        private Transform foamFrame;
        private readonly Dictionary<Part, Box[]> boxes = new Dictionary<Part, Box[]>();
        private readonly Dictionary<Guid, CraftState> crafts = new Dictionary<Guid, CraftState>();
        private readonly List<Cut> cuts = new List<Cut>();
        private readonly Dictionary<int, Hull> hulls = new Dictionary<int, Hull>();
        private readonly float[] cx = new float[8], cy = new float[8], ch = new float[8];
        private ParticleSystem.Particle[] buf;
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

            foam = Make("WaterWakes foam", Mat(sh, FoamTexture()), 30000, true);
            foamFrame = foam.transform;
            {
                var sol = foam.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 0.5f), new Keyframe(0.15f, 1f), new Keyframe(1, 2.6f)));
                var vol = foam.velocityOverLifetime;
                vol.enabled = true;
                vol.space = ParticleSystemSimulationSpace.World;
                vol.speedModifier = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1f), new Keyframe(0.08f, 0.35f), new Keyframe(1, 0.03f)));
                Fade(foam, 0.03f, 0.35f);
            }

            spray = Make("WaterWakes spray", Mat(sh, DotTexture(32, 0.3f)), 25000, false);
            {
                var sol = spray.sizeOverLifetime;
                sol.enabled = true;
                sol.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0, 1f, 1, 1.8f));
                var fol = spray.forceOverLifetime;
                fol.enabled = true;
                fol.space = ParticleSystemSimulationSpace.World;
                Fade(spray, 0.02f, 0.6f);
                // droplets as streaks along their flight
                var r = spray.GetComponent<ParticleSystemRenderer>();
                r.renderMode = ParticleSystemRenderMode.Stretch;
                r.velocityScale = 0.04f;
                r.lengthScale = 1.5f;
            }

            // mist: its opacity set each frame from each puff's height (Mist()); the age fade only eases it in and out
            mist = Make("WaterWakes mist", Mat(sh, MistTexture()), 8000, false);
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

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            foreach (ParticleSystem ps in new[] { foam, spray, mist })
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
            if (Time.time > nextPurge)
            {
                nextPurge = Time.time + 5;
                var dead = new List<Part>();
                foreach (Part p in boxes.Keys)
                    if (p == null)
                        dead.Add(p);
                foreach (Part p in dead)
                    boxes.Remove(p);
            }

            WakeSettings cfg = Cfg;
            bool log = cfg.debugLog && Time.time > nextLog;
            if (log)
                nextLog = Time.time + 5;
            if (cfg.enabled)
                foreach (Vessel v in FlightGlobals.VesselsLoaded)
                {
                    if (v == null || v.packed || v.mainBody == null || !v.mainBody.ocean || v.altitude > 300)
                        continue;
                    if ((v.CoM - cam.transform.position).sqrMagnitude > cfg.maxRange * cfg.maxRange)
                        continue;
                    Wake(v, dt, log);
                }
            Mist(cfg);
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
                double h = ((Vector3d)buf[i].position - c).magnitude - r;
                float a = cfg.mistAlpha * Mathf.Exp(-Mathf.Max(0, (float)h) * k);
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
            // the flow's direction; at rest the craft's heading (only the draft is wanted then)
            Vector3 f = V > 0.05f ? vh / V : Vector3.ProjectOnPlane(v.transform.up, up).normalized;
            if (f.sqrMagnitude < 0.5f)
                f = Vector3.ProjectOnPlane(v.transform.forward, up).normalized;
            Vector3 s = Vector3.Cross(up, f).normalized;
            double altO = FlightGlobals.getAltitudeAtPos((Vector3d)origin, body);

            // each wet part's cut at the water, in the flow's frame (f along the flow, s across it), about the CoM
            cuts.Clear();
            foreach (Part p in v.parts)
            {
                if (p == null || p.rb == null)
                    continue;
                Box[] bs = Boxes(p);
                if (bs.Length == 0)
                    continue;
                double wl = v.waterOffset + (p.partBuoyancy != null ? p.partBuoyancy.waterLevel : 0);
                Vector3 pos = p.transform.position;
                float hRef = (float)(FlightGlobals.getAltitudeAtPos((Vector3d)pos, body) - wl);
                Cut cut = null;
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
                    for (int i = 0; i < 8; i++)
                    {
                        if (ch[i] < 0)
                            Add(ref cut, cx[i], cy[i], -ch[i], (float)(wl - altO));
                        for (int bit = 1; bit < 8; bit <<= 1)
                        {
                            int j = i | bit;
                            if (j == i || ch[i] * ch[j] >= 0)
                                continue;
                            float t = ch[i] / (ch[i] - ch[j]);
                            Add(ref cut, Mathf.Lerp(cx[i], cx[j], t), Mathf.Lerp(cy[i], cy[j], t), 0, (float)(wl - altO));
                        }
                    }
                }
                if (cut != null)
                    cuts.Add(cut);
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
            float draftMax = 0;
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
                h.wl += c.wl;
                h.n++;
                draftMax = Mathf.Max(draftMax, c.draft);
            }

            // the hull's load on the water: its draft against its draft at rest, smoothed over ~0.3 s
            if (!crafts.TryGetValue(v.id, out CraftState st))
                crafts[v.id] = st = new CraftState();
            if (V < 1.5f && st.restDraft > 0)
                st.restDraft = Mathf.Lerp(st.restDraft, draftMax, Mathf.Clamp01(dt * 2));
            else
                st.restDraft = Mathf.Max(st.restDraft, draftMax);
            float loadNow = st.restDraft > 1e-3f ? Mathf.Clamp01(draftMax / st.restDraft) : 1;
            st.load = Mathf.Lerp(st.load, loadNow, Mathf.Clamp01(dt / 0.3f));
            float strength = Mathf.Pow(st.load, cfg.loadExponent) * cfg.intensity;
            if (V < cfg.minSpeed || strength <= 0)
                return;

            Vector3 sunDir = ((Vector3)(Planetarium.fetch.Sun.position - (Vector3d)origin)).normalized;
            float light = Mathf.Lerp(0.08f, 1f, Mathf.InverseLerp(-0.1f, 0.35f, Vector3.Dot(sunDir, up))) * cfg.brightness;
            foamFrame.rotation = Quaternion.LookRotation(up, f);
            var fol = spray.forceOverLifetime;
            Vector3 g9 = -up * 9.81f;
            fol.x = g9.x;
            fol.y = g9.y;
            fol.z = g9.z;

            float k = Mathf.Clamp01((V - cfg.minSpeed) / Mathf.Max(0.1f, cfg.fullSpeed - cfg.minSpeed));
            float dist = V * dt;
            // foam keeps a trace of the track however light the hull runs; spray and mist go with the load
            float foamStrength = Mathf.Lerp(0.25f, 1f, Mathf.Clamp01(strength));
            foreach (Hull h in hulls.Values)
            {
                float L = Mathf.Max(0.1f, h.fMax - h.fMin), B = Mathf.Max(0.1f, h.sMax - h.sMin), wlRel = h.wl / h.n;
                // a point on the water: origin + f x + s y, put at the waterline (f and s are level)
                Vector3 At(float x, float y, float lift) => origin + f * x + s * y + up * (wlRel + lift);
                // spread over the frame's travel so a fast hull leaves a line, not dots
                Vector3 Smear() => -vh * dt * Random.value;

                // side foam: the bow wave's two arms, thrown out and slowed, which draws the V
                float a = cfg.foamAlpha * Mathf.Lerp(0.3f, 1f, k) * foamStrength;
                for (int side = -1; side <= 1; side += 2)
                {
                    float y = side < 0 ? h.sMin : h.sMax;
                    int n = Count(dist / Mathf.Max(0.02f, cfg.foamSpacing) * Mathf.Min(1, cfg.intensity));
                    for (int i = 0; i < n; i++)
                    {
                        float x = h.fMax - Random.value * 0.3f * L;
                        Vector3 pv = s * side * V * cfg.foamSpread * Random.Range(0.6f, 1.1f) + f * V * Random.Range(0f, 0.08f);
                        EmitFoam(At(x, y, cfg.foamLift) + Smear(), pv, B * Random.Range(0.9f, 1.6f), cfg.foamLife * Random.Range(0.7f, 1.2f), a, light);
                    }
                }
                // stern foam: the churned water behind the hull
                {
                    int n = Count(dist * cfg.sternDensity * B * Mathf.Min(1, cfg.intensity));
                    for (int i = 0; i < n; i++)
                    {
                        float u = Random.value, y = h.sMin + B * u;
                        Vector2 jit = Random.insideUnitCircle * 0.3f * k;
                        Vector3 pv = s * ((u - 0.5f) * 2f * V * 0.06f + jit.x) + f * jit.y;
                        EmitFoam(At(h.fMin + Random.value * 0.1f * L, y, cfg.foamLift * 1.2f) + Smear(), pv, B * Random.Range(1.0f, 1.8f),
                                 cfg.foamLife * Random.Range(0.9f, 1.4f), a * 0.9f, light);
                    }
                }
                // spray from the wet bow's chines (the planing hull's spray root): small droplets, and soft blobs stretched
                // along their flight that read as the white curtain; mist lagging the hull, low over the water
                if (V > cfg.sprayMinSpeed)
                {
                    float sp = Mathf.Min((V - cfg.sprayMinSpeed) / 10f, 1.5f) * strength, hgt = cfg.sprayHeight;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float y = side < 0 ? h.sMin : h.sMax;
                        int n = Count(dt * cfg.sprayRate * sp * (B + 0.2f));
                        for (int i = 0; i < n; i++)
                        {
                            float x = h.fMax - Random.value * Random.value * 0.4f * L;
                            Vector3 pv = s * side * V * Random.Range(0.1f, 0.3f) + up * V * Random.Range(0.05f, 0.2f) * hgt + f * V * Random.Range(-0.05f, 0.2f)
                                         + Random.insideUnitSphere * 0.4f;
                            EmitSpray(At(x, y, 0.02f) + Smear(), pv, Random.Range(0.03f, 0.08f) * Mathf.Sqrt(B + 0.3f), Random.Range(0.5f, 1.2f), Tint(light, 0.8f));
                        }
                        n = Count(dt * cfg.sheetRate * sp * (B + 0.2f));
                        for (int i = 0; i < n; i++)
                        {
                            float x = h.fMax - Random.value * Random.value * 0.4f * L;
                            Vector3 pv = s * side * V * Random.Range(0.08f, 0.22f) + up * V * Random.Range(0.1f, 0.24f) * hgt + f * V * Random.Range(0f, 0.25f)
                                         + Random.insideUnitSphere * 0.3f;
                            EmitSpray(At(x, y, 0.05f) + Smear(), pv, Random.Range(0.2f, 0.5f) * Mathf.Sqrt(B + 0.3f), Random.Range(0.7f, 1.5f), Tint(light, cfg.sheetAlpha));
                        }
                    }
                }
                if (V > cfg.mistMinSpeed)
                {
                    float mp = Mathf.Min((V - cfg.mistMinSpeed) / 10f, 1.5f) * strength;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float y = side < 0 ? h.sMin : h.sMax;
                        int m = Count(dt * cfg.mistRate * mp * (B + 0.2f));
                        for (int i = 0; i < m; i++)
                        {
                            Vector3 pv = f * V * Random.Range(0.2f, 0.5f) + s * side * V * Random.Range(0.03f, 0.12f) + up * cfg.mistRise * Random.Range(0.4f, 1.6f);
                            var ep = new ParticleSystem.EmitParams
                            {
                                position = At(h.fMax - Random.value * 0.4f * L, y + side * Random.Range(0f, 0.4f), Random.Range(0f, 0.4f)) + Smear(),
                                velocity = pv,
                                startSize = Random.Range(1.0f, 2.0f) * Mathf.Sqrt(B + 0.3f),
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
                    sb.Append($" [L {h.fMax - h.fMin:F2} B {h.sMax - h.sMin:F2} draft {h.draft:F2} wl {h.wl / h.n:+0.00;-0.00} at {h.fMin:F1}..{h.fMax:F1}, {h.sMin:F1}..{h.sMax:F1}]");
                Debug.Log($"[WaterWakes] {v.vesselName}: {V:F1} m/s, load {st.load:F2} (draft {draftMax:F2} / rest {st.restDraft:F2}), strength {strength:F2}; " +
                          $"{cuts.Count} wet parts, {hulls.Count} hulls{sb}; emitted foam {emittedFoam} spray {emittedSpray} mist {emittedMist}; " +
                          $"alive foam {foam.particleCount} spray {spray.particleCount} mist {mist.particleCount}");
                emittedFoam = emittedSpray = emittedMist = 0;
            }
        }

        private void EmitSpray(Vector3 pos, Vector3 vel, float size, float life, Color32 color)
        {
            var ep = new ParticleSystem.EmitParams { position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color };
            spray.Emit(ep, 1);
            emittedSpray++;
        }

        private void EmitFoam(Vector3 pos, Vector3 vel, float size, float life, float alpha, float light)
        {
            var ep = new ParticleSystem.EmitParams
            {
                position = pos,
                velocity = vel,
                startSize = size,
                startLifetime = life,
                rotation3D = new Vector3(0, 0, Random.value * 360f),
                startColor = Tint(light * 0.95f, alpha),
            };
            foam.Emit(ep, 1);
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

        // stochastic rounding: the fraction kept on average
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
                        Bounds mb = mc.sharedMesh.bounds;
                        list.Add(new Box { t = mc.transform, c = mb.center, e = mb.extents });
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

        private static Material Mat(Shader sh, Texture2D tex)
        {
            var m = new Material(sh) { mainTexture = tex };
            if (m.HasProperty("_TintColor"))
                m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));
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
        private static Texture2D FoamTexture()
        {
            const int N = 128;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "WaterWakes foam" };
            var px = new Color32[N * N];
            float ox = Random.value * 100, oy = Random.value * 100;
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    float u = (i + 0.5f) / N - 0.5f, w = (j + 0.5f) / N - 0.5f;
                    float r = Mathf.Sqrt(u * u + w * w) * 2;
                    float fall = 1 - Smooth(0.35f, 1f, r);
                    // ridged noise, so overlapping patches merge
                    float n = 0, amp = 0.5f, fr = 3;
                    for (int o = 0; o < 4; o++, amp *= 0.5f, fr *= 2)
                        n += amp * (1 - Mathf.Abs(2 * Mathf.PerlinNoise(ox + u * fr, oy + w * fr) - 1));
                    float lace = Smooth(0.5f, 0.85f, n);
                    float a = fall * (0.3f + 0.7f * lace);
                    px[j * N + i] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(true, true);
            return tex;
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
