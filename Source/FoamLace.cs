using System;
using System.Threading.Tasks;
using UnityEngine;

namespace WakeSplashEffects
{
    /// <summary>
    /// The texture the surface foam is laid out from on the ocean (OceanFoam's SurfaceFoam): made once, tiling, from
    /// domain-warped fractal noise (several octaves of periodic gradient noise, its coordinates bent by more of it, so it
    /// folds into the tufts and wisps of churned foam with no one cell size). Each channel histogram-equalised (uniform
    /// 0..1), so a threshold t keeps 1 - t of it: the foam's density maps straight to its cover.
    /// R: tufts (6 octaves, warped). G: a second, independent field (the finer scale and the mottle). B: strands (ridged,
    /// warped harder: the thin threads left as it thins). A: 1.
    /// </summary>
    public static class FoamLace
    {
        public const int Size = 1024;
        private static Texture2D tex;
        private static Task<Color32[]> job;
        private static DateTime t0;

        /// <summary>The texture, once made: its pixels computed on worker threads (~1 s), started by the first call;
        /// null until then (the foam drawn plain meanwhile).</summary>
        public static Texture2D Texture
        {
            get
            {
                if (tex != null)
                    return tex;
                if (job == null)
                {
                    t0 = DateTime.Now;
                    job = Task.Run(() => Pixels());
                }
                if (!job.IsCompleted)
                    return null;
                if (job.IsFaulted)
                {
                    Debug.Log($"[WakeSplashEffects] foam lace texture failed: {job.Exception?.GetBaseException().Message}");
                    job = Task.FromResult<Color32[]>(null);
                    tex = new Texture2D(1, 1);   // (plain from here on: a flat mid grey)
                    tex.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1));
                    tex.Apply();
                    return tex;
                }
                return tex = Make(job.Result);
            }
        }

        private static Color32[] Pixels()
        {
            int n = Size;
            var r = new float[n * n];
            var g = new float[n * n];
            var b = new float[n * n];
            Parallel.For(0, n, y =>
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                    // the warp: two fields, 4 octaves from 3 per tile; then a second warp of the warped point (folds)
                    float wx = Fbm(u, v, 3, 4, 11), wy = Fbm(u, v, 3, 4, 23);
                    float ux = u + 0.12f * wx, vy = v + 0.12f * wy;
                    float qx = Fbm(ux, vy, 5, 3, 37), qy = Fbm(ux, vy, 5, 3, 41);
                    int i = y * n + x;
                    r[i] = Fbm(ux + 0.05f * qx, vy + 0.05f * qy, 4, 6, 53);
                    g[i] = Fbm(u - 0.1f * wy, v + 0.1f * wx, 6, 5, 67);
                    b[i] = Ridged(u + 0.2f * qx, v + 0.2f * qy, 5, 4, 79);
                }
            });
            Equalise(r);
            Equalise(g);
            Equalise(b);
            var px = new Color32[n * n];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)(r[i] * 255.99f), (byte)(g[i] * 255.99f), (byte)(b[i] * 255.99f), 255);
            return px;
        }

        private static Texture2D Make(Color32[] px)
        {
            int n = Size;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, true, true)
            {
                wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4, name = "WakeSplashEffects foam lace"
            };
            t.SetPixels32(px);
            t.Apply(true, true);
            Debug.Log($"[WakeSplashEffects] foam lace texture {n}x{n} made in {(DateTime.Now - t0).TotalSeconds:F2} s");
            // (diagnostic: the texture written there as a binary PPM, its r g b)
            string dump = Environment.GetEnvironmentVariable("WAKESPLASHEFFECTS_DUMP_LACE");
            if (!string.IsNullOrEmpty(dump))
            {
                byte[] head = System.Text.Encoding.ASCII.GetBytes($"P6\n{n} {n}\n255\n");
                var bytes = new byte[head.Length + 3 * n * n];
                Buffer.BlockCopy(head, 0, bytes, 0, head.Length);
                for (int i = 0; i < px.Length; i++)
                {
                    // (PPM rows run top down; the texture's bottom up)
                    int y = n - 1 - i / n, o = head.Length + 3 * (y * n + i % n);
                    bytes[o] = px[i].r; bytes[o + 1] = px[i].g; bytes[o + 2] = px[i].b;
                }
                System.IO.File.WriteAllBytes(dump, bytes);
            }
            return t;
        }

        // fractal sum of periodic gradient noise (period 1 in u, v): base frequency f (an integer: it tiles), each octave
        // twice the last at 0.55 the amplitude
        private static float Fbm(float u, float v, int f, int octaves, int seed)
        {
            float s = 0, a = 1, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                s += a * Noise(u * f, v * f, f, seed + o * 101);
                norm += a;
                a *= 0.55f;
                f *= 2;
            }
            return s / norm;
        }

        // ridged: 1 - |noise| per octave, sharpened (thin crests: threads)
        private static float Ridged(float u, float v, int f, int octaves, int seed)
        {
            float s = 0, a = 1, norm = 0;
            for (int o = 0; o < octaves; o++)
            {
                float k = 1 - Mathf.Abs(Noise(u * f, v * f, f, seed + o * 101));
                s += a * k * k * k;
                norm += a;
                a *= 0.5f;
                f *= 2;
            }
            return s / norm;
        }

        // gradient noise on the integer lattice, repeating every p cells, ~-1..1
        private static float Noise(float x, float y, int p, int seed)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            float ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10), uy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
            float a = Grad(ix, iy, p, seed, fx, fy), b = Grad(ix + 1, iy, p, seed, fx - 1, fy);
            float c = Grad(ix, iy + 1, p, seed, fx, fy - 1), d = Grad(ix + 1, iy + 1, p, seed, fx - 1, fy - 1);
            return 1.4f * Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
        }

        private static float Grad(int ix, int iy, int p, int seed, float dx, float dy)
        {
            ix = ((ix % p) + p) % p;
            iy = ((iy % p) + p) % p;
            uint h = (uint)ix * 0x8da6b343u ^ (uint)iy * 0xd8163841u ^ (uint)seed * 0xcb1ab31fu;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            float ang = (h & 0xffff) * (6.2831853f / 65536f);
            return Mathf.Cos(ang) * dx + Mathf.Sin(ang) * dy;
        }

        // each value replaced by its rank (a 4096-bin histogram): uniform over 0..1
        private static void Equalise(float[] a)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (float x in a)
            {
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
            const int B = 4096;
            var cdf = new float[B];
            float k = (B - 1) / Math.Max(hi - lo, 1e-6f);
            foreach (float x in a)
                cdf[(int)((x - lo) * k)]++;
            float run = 0;
            for (int i = 0; i < B; i++)
            {
                run += cdf[i];
                cdf[i] = (run - 0.5f * cdf[i]) / a.Length;
            }
            for (int i = 0; i < a.Length; i++)
                a[i] = cdf[(int)((a[i] - lo) * k)];
        }
    }
}
