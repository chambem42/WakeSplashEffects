using System;
using System.Collections.Generic;
using UnityEngine;

namespace WakeSplashEffects
{
    /// <summary>
    /// A wave simulation on a square of sea around where the camera looks: one height field that the hulls' wakes and the
    /// entries' rings all go into, so they add up as water does (the analytic Kelvin waves and rings, drawn as slopes
    /// each on its own, crossed oddly). Tessendorf's iWave: h' = h (2 - a dt)/(1 + a dt) - h_prev/(1 + a dt) - g dt^2 (G*h)
    /// /(cell (1 + a dt)), G a 13 x 13 kernel standing in for the vertical derivative (deep-water dispersion w^2 = g k),
    /// calibrated here against plane waves. Driven by displacement: each wetted part's waterline footprint x its draft
    /// (WakeFx.SimBlobs), the field pushed down as a footprint arrives and let back as it leaves (a moving hull makes its
    /// wake, a plunging capsule its ring). Edges absorb. Its height, previous height and foam are global textures the
    /// ocean shader (OceanFoamPainter) draws into Scatterer's normals and foam.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class WaveSim : MonoBehaviour
    {
        public static WaveSim Instance { get; private set; }

        private static WakeSettings Cfg => WakeSettings.Current;

        private Material mat;
        private RenderTexture h0, h1, h2, d0, d1, dm, f0, f1, vis, hm;
        private int res;
        private float size, cell, acc;
        private Vector3d centreRel;     // the square's centre, relative to the body's position (world frame co-rotates near it)
        private CelestialBody centreBody;
        private Vector3 e1, e2, up;      // the square's axes (level) and up, at its centre
        private bool primed;
        private readonly float[] kern = new float[169];
        private readonly Vector4[] blob0 = new Vector4[64], blob1 = new Vector4[64], blob2 = new Vector4[64], blob0Was = new Vector4[64];
        private int blobCount;

        public bool Active { get; private set; }

        private static readonly int HId = Shader.PropertyToID("_SimHeight"), HPId = Shader.PropertyToID("_SimPrev"), FId = Shader.PropertyToID("_SimFoam"),
            MatId = Shader.PropertyToID("_SimMatrix"), E1Id = Shader.PropertyToID("_SimE1"), E2Id = Shader.PropertyToID("_SimE2"),
            CellId = Shader.PropertyToID("_SimCell"), TexelId = Shader.PropertyToID("_SimTexel"), OnId = Shader.PropertyToID("_SimOn"),
            DtId = Shader.PropertyToID("_SimDt");

        private void Awake() => Instance = this;

        private void Start()
        {
            Shader sh = WakeShaders.Get("WakeSplashEffects/WaveSim");
            if (sh == null)
            {
                Debug.Log("[WakeSplashEffects] wave sim off: no WakeSplashEffects/WaveSim shader");
                enabled = false;
                return;
            }
            mat = new Material(sh);
            MakeKernel();
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            Shader.SetGlobalFloat(OnId, 0);
            Release();
        }

        private void Release()
        {
            foreach (RenderTexture rt in new[] { h0, h1, h2, d0, d1, dm, f0, f1, vis, hm })
                if (rt != null)
                    rt.Release();
            h0 = h1 = h2 = d0 = d1 = dm = f0 = f1 = vis = hm = null;
            primed = false;
        }

        // ---- the kernel: weights over a 13 x 13 window (shared by radius) fitted so its response to cos(kx x + ky y),
        // R = sum w cos(k.x), is |k| (1/texel: sqrt(-laplacian), what turns the wave equation's dispersion into deep
        // water's w^2 = g k), rolled off as exp(-(|k| / 2.2)^4) towards the grid's shortest waves, zero at k = 0, never
        // negative (a negative response makes those waves grow)
        private void MakeKernel()
        {
            const int P = 6, S = 40;
            // the radius classes: the window's offsets grouped by x^2 + y^2
            var classOf = new Dictionary<int, int>();
            var members = new List<List<(int, int)>>();
            for (int y = -P; y <= P; y++)
                for (int x = -P; x <= P; x++)
                {
                    int r2 = x * x + y * y;
                    if (!classOf.TryGetValue(r2, out int c))
                    {
                        classOf[r2] = c = members.Count;
                        members.Add(new List<(int, int)>());
                    }
                    members[c].Add((x, y));
                }
            int n = members.Count;
            var ata = new double[n, n];
            var atb = new double[n];
            var row = new double[n];
            void AddRow(double kx, double ky, double target, double weight)
            {
                for (int j = 0; j < n; j++)
                {
                    double a = 0;
                    foreach ((int x, int y) in members[j])
                        a += Math.Cos(kx * x + ky * y);
                    row[j] = a;
                }
                for (int i = 0; i < n; i++)
                {
                    atb[i] += weight * row[i] * target;
                    for (int j = 0; j < n; j++)
                        ata[i, j] += weight * row[i] * row[j];
                }
            }
            for (int iy = 0; iy <= S; iy++)
                for (int ix = 0; ix <= S; ix++)
                {
                    double kx = Math.PI * ix / S, ky = Math.PI * iy / S, k = Math.Sqrt(kx * kx + ky * ky);
                    AddRow(kx, ky, k * Math.Exp(-Math.Pow(k / 2.2, 4)), 1);
                }
            AddRow(0, 0, 0, 1e4);   // (a level offset makes no waves)
            for (int i = 0; i < n; i++)
                ata[i, i] += 1e-6;  // (a touch of ridge for the solve)
            double[] w = Solve(ata, atb, n);
            for (int y = -P; y <= P; y++)
                for (int x = -P; x <= P; x++)
                    kern[(y + P) * 13 + (x + P)] = (float)w[classOf[x * x + y * y]];
            // its fit: the response along an axis and a diagonal over k, and its least anywhere
            double Resp(double kx, double ky)
            {
                double r = 0;
                for (int y = -P; y <= P; y++)
                    for (int x = -P; x <= P; x++)
                        r += kern[(y + P) * 13 + (x + P)] * Math.Cos(kx * x + ky * y);
                return r;
            }
            double least = double.MaxValue;
            for (int iy = 0; iy <= S; iy++)
                for (int ix = 0; ix <= S; ix++)
                    least = Math.Min(least, Resp(Math.PI * ix / S, Math.PI * iy / S));
            string fit = "";
            foreach (double k in new[] { 0.25, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0 })
                fit += $" {k:F2}: {Resp(k, 0) / k:F2}/{Resp(k / Math.Sqrt(2), k / Math.Sqrt(2)) / k:F2}";
            Debug.Log($"[WakeSplashEffects] wave sim kernel: {n} radii, centre {kern[P * 13 + P]:F3}, least response {least:F4}, response/k (axis/diagonal){fit}");
            // (any negative response lifted: those waves then stand still instead of growing)
            if (least < 0)
                kern[P * 13 + P] -= (float)least;
        }

        private static double[] Solve(double[,] a, double[] b, int n)
        {
            var m = (double[,])a.Clone();
            var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++)
                    if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c]))
                        piv = r;
                if (piv != c)
                {
                    for (int k = 0; k < n; k++)
                    {
                        double t = m[c, k];
                        m[c, k] = m[piv, k];
                        m[piv, k] = t;
                    }
                    double tb = x[c];
                    x[c] = x[piv];
                    x[piv] = tb;
                }
                for (int r = c + 1; r < n; r++)
                {
                    double f = m[r, c] / m[c, c];
                    for (int k = c; k < n; k++)
                        m[r, k] -= f * m[c, k];
                    x[r] -= f * x[c];
                }
            }
            for (int c = n - 1; c >= 0; c--)
            {
                double sum = x[c];
                for (int k = c + 1; k < n; k++)
                    sum -= m[c, k] * x[k];
                x[c] = sum / m[c, c];
            }
            return x;
        }

        private RenderTexture Rt(string name)
        {
            var rt = new RenderTexture(res, res, 0, name[0] == 'd' ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.RFloat) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "WakeSplashEffects sim " + name };
            rt.Create();
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
            return rt;
        }

        private void LateUpdate()
        {
            WakeSettings cfg = Cfg;
            CelestialBody body = FlightGlobals.currentMainBody;
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            Active = cfg.enabled && cfg.waveSim && mat != null && body != null && body.ocean && cam != null;
            if (Active)
            {
                double alt = body.GetAltitude((Vector3d)cam.transform.position);
                Active = alt < Mathf.Max(200, cfg.simSize * 3);
            }
            Shader.SetGlobalFloat(OnId, Active ? 1 : 0);
            if (!Active)
                Shader.SetGlobalFloat("_SimDisplace", 0);
            if (!Active)
            {
                if (h0 != null)
                    Release();
                return;
            }
            int wantRes = Mathf.Clamp(Mathf.ClosestPowerOfTwo(cfg.simResolution), 128, 1024);
            if (h0 == null || res != wantRes || centreBody != body || Mathf.Abs(size - cfg.simSize) > 0.01f)
            {
                Release();
                res = wantRes;
                size = Mathf.Max(20, cfg.simSize);
                cell = size / res;
                h0 = Rt("h0"); h1 = Rt("h1"); h2 = Rt("h2"); d0 = Rt("d0"); d1 = Rt("d1"); dm = Rt("dm"); f0 = Rt("f0"); f1 = Rt("f1");
                centreBody = body;
                centreRel = Focus(cam, body) - body.position;
                acc = 0;
            }

            // axes at the centre; re-centre (whole texels) when the focus has moved far enough
            Vector3 centre = (Vector3)(body.position + centreRel);
            up = (centre - (Vector3)body.position).normalized;
            Vector3 refAxis = Vector3.ProjectOnPlane(body.transform.up, up);
            e1 = (refAxis.sqrMagnitude > 1e-4f ? refAxis : Vector3.ProjectOnPlane(Vector3.forward, up)).normalized;
            e2 = Vector3.Cross(up, e1);
            Vector3 off = (Vector3)(Focus(cam, body) - (body.position + centreRel));
            float ox = Vector3.Dot(off, e1), oy = Vector3.Dot(off, e2);
            if (Mathf.Abs(ox) > 0.15f * size || Mathf.Abs(oy) > 0.15f * size)
            {
                int sx = Mathf.RoundToInt(ox / cell), sy = Mathf.RoundToInt(oy / cell);
                if (Mathf.Abs(sx) >= res / 2 || Mathf.Abs(sy) >= res / 2)
                {
                    foreach (RenderTexture rt in new[] { h0, h1, h2, d0, d1, f0, f1 })
                        Clear(rt);
                }
                else
                {
                    mat.SetVector("_Shift", new Vector2(sx / (float)res, sy / (float)res));
                    Shift(ref h0); Shift(ref h1); Shift(ref d0); Shift(ref d1); Shift(ref f0);
                }
                centreRel += (Vector3d)(e1 * (sx * cell) + e2 * (sy * cell));
                centre = (Vector3)(body.position + centreRel);
            }

            // the hulls' footprints into the displacement now (d1; d0 the last)
            Footprints(centre, cfg);
            Swap(ref d0, ref d1);
            mat.SetVectorArray("_Blob0", blob0);
            mat.SetVectorArray("_Blob1", blob1);
            mat.SetVectorArray("_Blob2", blob2);
            mat.SetFloat("_BlobCount", blobCount);
            Graphics.Blit(null, d1, mat, 0);
            // (the same footprints where their hulls were a frame ago: the moving push is the motion alone)
            mat.SetVectorArray("_Blob0", blob0Was);
            Graphics.Blit(null, dm, mat, 0);
            mat.SetVectorArray("_Blob0", blob0);

            // steps at a fixed dt
            const float dt = 1 / 60f;
            float g = (float)(body.GeeASL * 9.80665);
            mat.SetFloat("_Texel", 1f / res);
            mat.SetFloat("_Cell", cell);
            mat.SetFloat("_Dt", dt);
            mat.SetFloat("_G", g);
            mat.SetFloat("_Alpha", cfg.simDamping);
            mat.SetFloat("_AlphaEdge", 6f);
            mat.SetFloatArray("_Kern", kern);
            mat.SetFloat("_FoamSlope", cfg.simFoamSlope);
            // (no more than a still hull's surface can move: its footprint deepens only as fast as it heaves, pitches or
            // rolls; the footprint drawn frame to frame never matched itself exactly as a hull crept to rest, and each
            // mismatch, read at its full depth, pushed: ripples round a ship coming to a stop as if it shook)
            float heave = WakeFx.Instance != null ? WakeFx.Instance.StillHeaveRate : 15f;
            mat.SetFloat("_MaxPush", Mathf.Min(15f, heave + 0.03f) * Mathf.Clamp(Time.deltaTime, 1 / 120f, 1 / 20f));
            mat.SetFloat("_FoamDecay", 1f / Mathf.Max(0.2f, cfg.simFoamLife));
            mat.SetFloat("_BreakDamp", 8f);
            mat.SetFloat("_Visc", Mathf.Min(cfg.simShortDamping, 0.15f * cell * cell / dt));
            acc = Mathf.Min(acc + Time.deltaTime, 4 * dt);
            if (!primed)
            {
                // (the displacement already there when the sim starts is no change)
                Graphics.Blit(d1, d0);
                primed = true;
            }
            // the frame's push spread past the waterlines (pass 6 along x, 7 along y)
            RenderTexture s0 = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.RGFloat);
            RenderTexture s1 = RenderTexture.GetTemporary(res, res, 0, RenderTextureFormat.RGFloat);
            s0.filterMode = s1.filterMode = FilterMode.Point;
            mat.SetTexture("_D", d1);
            mat.SetTexture("_DPrev", d0);
            mat.SetTexture("_DMoved", dm);
            mat.SetVector("_BlurDir", new Vector2(1f / res, 0));
            Graphics.Blit(null, s0, mat, 6);
            mat.SetVector("_BlurDir", new Vector2(0, 1f / res));
            Graphics.Blit(s0, s1, mat, 7);
            mat.SetTexture("_S", s1);
            mat.SetFloat("_SrcGain", 2.5f);
            // (the push spread evenly over the frame's steps)
            int steps = 0;
            for (float a = acc; a >= dt; a -= dt)
                steps++;
            float srcPerStep = steps > 0 ? cfg.simStrength / steps : 0;
            while (acc >= dt)
            {
                acc -= dt;
                mat.SetTexture("_H", h1);
                mat.SetTexture("_HPrev", h0);
                mat.SetTexture("_D", d1);
                mat.SetTexture("_DPrev", d0);
                mat.SetFloat("_Src", srcPerStep);
                Graphics.Blit(null, h2, mat, 1);
                // rotate: h0 <- h1 <- h2
                RenderTexture t = h0;
                h0 = h1;
                h1 = h2;
                h2 = t;
                mat.SetTexture("_H", h1);
                mat.SetTexture("_HPrev", h0);
                mat.SetTexture("_F", f0);
                Graphics.Blit(null, f1, mat, 3);
                Swap(ref f0, ref f1);
            }
            RenderTexture.ReleaseTemporary(s0);
            RenderTexture.ReleaseTemporary(s1);

            // for the ocean shader
            Vector3 r1 = e1 / size, r2 = e2 / size;
            var m = Matrix4x4.identity;
            m.SetRow(0, new Vector4(r1.x, r1.y, r1.z, 0.5f - Vector3.Dot(centre, r1)));
            m.SetRow(1, new Vector4(r2.x, r2.y, r2.z, 0.5f - Vector3.Dot(centre, r2)));
            Shader.SetGlobalMatrix(MatId, m);
            Shader.SetGlobalVector(E1Id, e1);
            Shader.SetGlobalVector(E2Id, e2);
            Shader.SetGlobalFloat(CellId, cell);
            Shader.SetGlobalFloat(TexelId, 1f / res);
            Shader.SetGlobalFloat(DtId, dt);
            Shader.SetGlobalTexture(HId, h1);
            Shader.SetGlobalTexture(HPId, h0);
            Shader.SetGlobalTexture(FId, f0);
            Shader.SetGlobalVector("_SimUp", up);
            Shader.SetGlobalTexture("_SimFoot", d1);
            Shader.SetGlobalFloat("_SimDisplace", cfg.simDisplace);
            Shader.SetGlobalFloat("_SimDebugLift", cfg.simDebugLift);
            Shader.SetGlobalFloat("_SimDebugTilt", cfg.simDebugTilt);

            // the height with mips, for the ocean displacement (sampled at its vertices' spacing)
            if (hm == null || hm.width != res)
            {
                if (hm != null)
                    hm.Release();
                hm = new RenderTexture(res, res, 0, RenderTextureFormat.RFloat) { useMipMap = true, autoGenerateMips = false, filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp, name = "WakeSplashEffects sim height mips" };
                hm.Create();
            }
            Graphics.Blit(h1, hm);
            hm.GenerateMips();
            Shader.SetGlobalTexture("_SimHeightMip", hm);

            if (cfg.simDebugInset)
            {
                if (vis == null)
                {
                    vis = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32) { name = "WakeSplashEffects sim view" };
                    vis.Create();
                }
                mat.SetTexture("_H", h1);
                mat.SetTexture("_F", f0);
                mat.SetFloat("_VisScale", 0.15f);
                Graphics.Blit(null, vis, mat, 4);
            }
        }

        private void OnGUI()
        {
            if (!Active || !Cfg.simDebugInset || vis == null || Event.current.type != EventType.Repaint)
                return;
            float s = Mathf.Min(320, Screen.height * 0.35f);
            // (the inset's up the square's e2: north-ish)
            GUI.DrawTexture(new Rect(Screen.width - s - 10, 10, s, s), vis, ScaleMode.StretchToFill, false);
        }

        // where the camera looks at the sea (kept within the square's half of the point below it)
        private Vector3d Focus(Camera cam, CelestialBody body)
        {
            Vector3d camPos = cam.transform.position;
            Vector3 u = ((Vector3)(camPos - body.position)).normalized;
            double alt = body.GetAltitude(camPos);
            Vector3d below = camPos - (Vector3d)u * alt;
            Vector3 f3 = cam.transform.forward;
            float down = -Vector3.Dot(f3, u);
            if (down <= 0.05f)
                return below;
            Vector3 hit = (Vector3)(camPos + (Vector3d)f3 * (alt / down) - below);
            hit -= u * Vector3.Dot(hit, u);
            return below + (Vector3d)Vector3.ClampMagnitude(hit, 0.35f * Mathf.Max(20, Cfg.simSize));
        }

        private void Footprints(Vector3 centre, WakeSettings cfg)
        {
            blobCount = 0;
            List<WakeFx.SimBlob> src = WakeFx.Instance != null ? WakeFx.Instance.SimBlobs : null;
            if (src == null)
                return;
            foreach (WakeFx.SimBlob b in src)
            {
                if (blobCount >= 64)
                    break;
                Vector3 d = b.pos - centre;
                float x = Vector3.Dot(d, e1) / size + 0.5f, y = Vector3.Dot(d, e2) / size + 0.5f;
                float reach = Mathf.Max(b.halfL, Mathf.Max(b.halfW, b.halfWFore)) / size;
                if (x < -reach || x > 1 + reach || y < -reach || y > 1 + reach)
                    continue;
                Vector2 dir = new Vector2(Vector3.Dot(b.fwd, e1), Vector3.Dot(b.fwd, e2));
                dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Vector2.right;
                blob0[blobCount] = new Vector4(x, y, dir.x, dir.y);
                Vector3 dWas = d - b.moved;
                blob0Was[blobCount] = new Vector4(Vector3.Dot(dWas, e1) / size + 0.5f, Vector3.Dot(dWas, e2) / size + 0.5f, dir.x, dir.y);
                blob1[blobCount] = new Vector4(Mathf.Max(b.halfL, cell) / size, Mathf.Max(b.halfW, cell) / size, b.depth, b.still);
                blob2[blobCount] = new Vector4(Mathf.Max(b.halfWFore, cell) / size, b.depthFore, b.weight, b.kWave);
                blobCount++;
            }
        }

        private void Shift(ref RenderTexture rt)
        {
            RenderTexture tmp = RenderTexture.GetTemporary(res, res, 0, rt.format);
            tmp.filterMode = FilterMode.Point;
            Graphics.Blit(rt, tmp, mat, 2);
            Graphics.Blit(tmp, rt);
            RenderTexture.ReleaseTemporary(tmp);
        }

        private static void Clear(RenderTexture rt)
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        private static void Swap(ref RenderTexture a, ref RenderTexture b)
        {
            RenderTexture t = a;
            a = b;
            b = t;
        }
    }
}
