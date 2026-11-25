using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KspTest;
using UnityEngine;

namespace WakeSplashEffects.KspTests
{
    /// <summary>
    /// Bodies (crafts/generator/splash_crafts.py: ProceduralParts Ore tanks, their ore making their mass; most of one plan
    /// area, 4.91 m2 facing the water from above) dropped onto still water, once per mass and height: FAR's water slam
    /// (its water model makes the force; read by reflection, FarWater) and the splash WakeSplashEffects draws, beside what
    /// water-entry studies give for the same speed, size and mass, and beside each other.
    /// </summary>
    public static class SplashTests
    {
        // the tests' mods besides the project's (WakeSplashEffects.KspTests.Run: FAR, ProceduralParts, this mod): CustomProceduralParts
        // (the Spine bodies) from its repository at a commit, built from source. To test a checkout, put its
        // GameData/CustomProceduralParts path here (or in KSPTEST_MODS)
        private const string Mods =
            "https://github.com/chambem42/CustomProceduralParts@516829fdbbf52b1725ed67ca4122d65d5eda0f32/GameData/CustomProceduralParts" +
            "?build=Source/CustomProceduralParts.csproj";

        private const double OreUnitsPerM3 = 139.664;              // ProceduralParts' Ore tank

        // Each body's particles, by kind, from its 4 t drop from 20 m (the first of the default drops; the later ones carry
        // the last one's wallowing, a capsule's most), measured 2026-11-24 at 165 and at 50 frames a game second (maxFps=20,
        // as a CI runner draws) and averaged: the two within ~20% of each other for every kind of 50 or more. Kinds left
        // out had 0.
        private const double CheckMass = 4, CheckHeight = 20;

        private static readonly Dictionary<string, Dictionary<string, long>> Baseline = new Dictionary<string, Dictionary<string, long>>
        {
            ["Cylinder"] = new Dictionary<string, long>
            {
                ["skirt"] = 1406, ["sheet"] = 341, ["spray"] = 1420, ["splash cloud"] = 2224, ["haze"] = 2, ["mist"] = 206,
                ["splash mist"] = 480, ["foam"] = 86, ["splash foam"] = 364, ["upwell"] = 328,
            },
            ["SideCylinder"] = new Dictionary<string, long>
            {
                ["skirt"] = 1551, ["sheet"] = 274, ["spray"] = 1152, ["splash cloud"] = 2205, ["haze"] = 4, ["mist"] = 215,
                ["splash mist"] = 461, ["foam"] = 109, ["splash foam"] = 415, ["upwell"] = 319,
            },
            ["Sphere"] = new Dictionary<string, long>
            {
                ["skirt"] = 856, ["sheet"] = 461, ["spray"] = 1360, ["slam spray"] = 5, ["splash cloud"] = 699, ["haze"] = 2,
                ["mist"] = 150, ["splash mist"] = 153, ["splash foam"] = 252, ["upwell"] = 322,
            },
            // (an edge-down wedge throws no skirt and no splash cloud: its waterline spreads slower than it goes in)
            ["Wedge"] = new Dictionary<string, long>
            {
                ["sheet"] = 976, ["spray"] = 2541, ["mist"] = 22,
            },
            ["Capsule"] = new Dictionary<string, long>
            {
                ["skirt"] = 2716, ["sheet"] = 508, ["spray"] = 1761, ["slam spray"] = 2, ["splash cloud"] = 2358, ["haze"] = 10,
                ["mist"] = 372, ["splash mist"] = 470, ["foam"] = 76, ["splash foam"] = 703, ["upwell"] = 332,
            },
        };

        /// <summary>
        /// The 4 t drop from 20 m's particles against the body's Baseline, when it is the first drop (as the baseline's
        /// was; a later one carries the last one's wallowing): a kind of 50 or more within half to twice its count, any
        /// other (the rare ones and those it throws none of) under 150. Recorded; returns what is off (null if nothing or
        /// not checked), for the caller to fail on once every drop is done. Off with countCheck=false.
        /// </summary>
        private static string CheckCounts(TestContext ctx, string name, bool firstDrop, double mass, double dropHeight, Dictionary<string, long> emitted)
        {
            if (!firstDrop || mass != CheckMass || dropHeight != CheckHeight || ctx.Param("countCheck") == "false" || !Baseline.TryGetValue(name, out var expected))
                return null;
            var off = new List<string>();
            foreach (string kind in WakeCounts.Kinds.Union(emitted.Keys))
            {
                long n = WakeCounts.Get(emitted, kind), b = expected.TryGetValue(kind, out long e) ? e : 0;
                bool ok = b >= 50 ? n >= b / 2 && n <= b * 2 : n < 150;
                if (!ok)
                    off.Add(b >= 50 ? $"{kind} {n} (expected {b / 2}-{b * 2})" : $"{kind} {n} (expected under 150)");
            }
            ctx.Record("particle check", off.Count == 0 ? $"{name}'s {CheckMass:F0} t drop from {CheckHeight:F0} m: every kind as expected" : string.Join("; ", off));
            return off.Count == 0 ? null : $"{name}'s particles off their baseline: {string.Join("; ", off)}";
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll")]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>The process's committed memory and working set (Mono's Process says 0 for both): Windows from psapi,
        /// Linux from /proc/self/status.</summary>
        private static string ProcessMemory()
        {
            const double MB = 1 << 20;
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer)
                {
                    var c = new ProcessMemoryCounters { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
                    if (GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb))
                        return $"committed {c.PagefileUsage.ToUInt64() / MB:F0} MB (peak {c.PeakPagefileUsage.ToUInt64() / MB:F0}), working set {c.WorkingSetSize.ToUInt64() / MB:F0} MB";
                }
                else if (File.Exists("/proc/self/status"))
                {
                    var kb = File.ReadAllLines("/proc/self/status").Where(l => l.StartsWith("VmSize:") || l.StartsWith("VmRSS:"))
                        .ToDictionary(l => l.Substring(0, l.IndexOf(':')), l => double.Parse(l.Substring(l.IndexOf(':') + 1).Trim().Split(' ')[0], CultureInfo.InvariantCulture));
                    return $"virtual {kb["VmSize"] / 1024:F0} MB, resident {kb["VmRSS"] / 1024:F0} MB";
                }
            }
            catch (Exception e)
            {
                return $"process memory unread ({e.GetType().Name})";
            }
            return "process memory unread";
        }

        /// <summary>The process's memory and what of Unity's could grow: managed heap, Unity's allocations, render
        /// textures (their pixels), textures and meshes alive.</summary>
        private static string MemoryUse()
        {
            var rts = Resources.FindObjectsOfTypeAll<RenderTexture>();
            long rtBytes = rts.Sum(rt => (long)UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(rt));
            const double MB = 1 << 20;
            return $"{ProcessMemory()}, " +
                   $"managed {GC.GetTotalMemory(false) / MB:F0} MB, Unity {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / MB:F0} MB; " +
                   $"{rts.Length} render textures ({rtBytes / MB:F0} MB), {Resources.FindObjectsOfTypeAll<Texture2D>().Length} textures, " +
                   $"{Resources.FindObjectsOfTypeAll<Mesh>().Length} meshes";
        }

        // what faces the water from above (m2): the upright cylinder, ball and wedge a 2.5 m disc's; the cylinder on its
        // side its 2.5 x 4 m; the pod its 3.91 m heat shield's
        private static double PlanArea(string name) =>
            name == "SideCylinder" ? 2.5 * 4.0 : name == "Capsule" ? Math.PI * 3.91 * 3.91 / 4 : Math.PI * 2.5 * 2.5 / 4;

        /// <summary>
        /// The cylinder put on the water (TestTools.PutOnWater), then for each of masses (t, default 4, 8, 15, 22, 30:
        /// from a fifth of the water it would displace, 19.6 t, to half again more) lifted upright, its lowest point
        /// dropHeight m (default 20: 19.8 m/s at the water; dropHeights=a+b+..: each mass from each height) above the
        /// sea, held still, let fall and watched settleSec
        /// (default 12) game seconds. Or the drops as mass@height pairs: drops=4@20+15@5+..., or per body
        /// (cylinderDrops, sphereDrops, wedgeDrops, sideCylinderDrops, capsuleDrops; over drops), so one session runs every
        /// body's own list. Each body writes into its own folder of the results (Cylinder/, Sphere/, Wedge/, SideCylinder/,
        /// Capsule/), so they can run one after another in one KSP. Recorded per drop: its speed at the water, its peak deceleration and the water's
        /// slam force (FAR's impact force), how deep its lowest point went and how high it came back up. Writes
        /// splash.csv (a row a drop); record=true: a video (frames/, drops.txt: each drop's first frame;
        /// impacts.txt: the frame each reaches the water, its mass, height and speed, to line the drops up side by side),
        /// the camera camDistance m (default 20) off, camPitch deg (default 3) down, aimed camAim m (default 4) above the
        /// water where it falls. Recorded (with WakeSplashEffects' particles by kind, splash.csv's n_ columns, and the frames a
        /// game second, fps; maxFps=N holds the frame rate to N), not asserted, but for the particles of its 4 t drop from
        /// 20 m (CheckCounts).
        /// </summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Splash Cylinder", CraftDir = "VAB",
                LaunchSite = "LaunchPad", TimeoutSec = 2400)]
        public static IEnumerator Splash_CylinderDrops(TestContext ctx) => Drops(ctx, "Cylinder");

        /// <summary>Splash_CylinderDrops' drops of a 2.5 m ball.</summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Splash Sphere", CraftDir = "VAB",
                LaunchSite = "LaunchPad", TimeoutSec = 2400)]
        public static IEnumerator Splash_SphereDrops(TestContext ctx) => Drops(ctx, "Sphere");

        /// <summary>Splash_CylinderDrops' drops of a wedge: a triangular prism (2.5 m sides) lying with an edge down.</summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Splash Wedge", CraftDir = "VAB",
                LaunchSite = "LaunchPad", TimeoutSec = 2400)]
        public static IEnumerator Splash_WedgeDrops(TestContext ctx) => Drops(ctx, "Wedge");

        /// <summary>Splash_CylinderDrops' drops of the same cylinder turned on its side before it falls (sideCylinderTilt
        /// deg off upright, default 90; its axis across the camera's view): 2.5 x 4 m facing the water.</summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Splash Cylinder", CraftDir = "VAB",
                LaunchSite = "LaunchPad", TimeoutSec = 2400)]
        public static IEnumerator Splash_SideCylinderDrops(TestContext ctx) => Drops(ctx, "SideCylinder");


        /// <summary>
        /// Splash_CylinderDrops' drops of an Apollo command module: the stock Mk1-3 pod and 2.5 m heat shield at Apollo's
        /// size (crafts/Splash Apollo Pod.craft, mods/WakeSplashEffectsTests/ApolloPod.cfg: x1.564, 3.91 m across), hanging as
        /// under its parachutes: capsuleTilt deg (default 27.5, Apollo's) off upright, its top and its heat shield's low edge
        /// leaning capsuleTiltAzimuth deg (default 40) from the camera's right toward the camera (Apollo 15's footage: down
        /// and right in the frame). A nominal splashdown: capsuleDrops=5.6@4.5 (5.6 t, 9.4 m/s at the water). Ballasted
        /// low: its centre of mass capsuleCoM m above the heat shield's lowest point (default 0.5; an even fill floated it
        /// apex down, Apollo's "stable 2").
        /// </summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Splash Apollo Pod", CraftDir = "VAB",
                LaunchSite = "LaunchPad", TimeoutSec = 2400)]
        public static IEnumerator Splash_ApolloPodDrops(TestContext ctx) => Drops(ctx, "Capsule");

        private static IEnumerator Drops(TestContext ctx, string name)
        {
            // (mass t, drop height m) each: the body's own list, else drops, else every mass from every height
            string own = char.ToLowerInvariant(name[0]) + name.Substring(1) + "Drops";
            string pairs = !string.IsNullOrEmpty(ctx.Param(own)) ? own : !string.IsNullOrEmpty(ctx.Param("drops")) ? "drops" : null;
            (double mass, double height)[] plan;
            if (pairs != null)
                plan = ctx.ParamList(pairs, "").Select(d => d.Split('@')).Select(d => (double.Parse(d[0], CultureInfo.InvariantCulture),
                    d.Length > 1 ? double.Parse(d[1], CultureInfo.InvariantCulture) : ctx.ParamDouble("dropHeight", 20))).ToArray();
            else
            {
                double[] masses = ctx.ParamList("masses", "4+8+15+22+30").Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                string hs = ctx.Param("dropHeights");
                double[] heights = (string.IsNullOrEmpty(hs) ? new[] { ctx.ParamDouble("dropHeight", 20) }
                    : ctx.ParamList("dropHeights", "20").Select(s => double.Parse(s, CultureInfo.InvariantCulture))).ToArray();
                plan = heights.SelectMany(h => masses.Select(mm => (mm, h))).ToArray();
            }
            // (loops=N: the list N times over; demo=true: the recording's camera without recording, the UI kept, real time)
            int loops = Math.Max(1, (int)ctx.ParamDouble("loops", 1));
            if (loops > 1)
                plan = Enumerable.Repeat(plan, loops).SelectMany(x => x).ToArray();
            bool demo = ctx.Param("demo") == "true";
            string outDir = Path.Combine(ctx.ResultsDir, name);
            Directory.CreateDirectory(outDir);
            // (hanging off upright: about the camera's line of sight, so the camera sees it from the side)
            double tilt = ctx.ParamDouble(char.ToLowerInvariant(name[0]) + name.Substring(1) + "Tilt", name == "Capsule" ? 27.5 : name == "SideCylinder" ? 90 : 0);
            double azimuth = ctx.ParamDouble(char.ToLowerInvariant(name[0]) + name.Substring(1) + "TiltAzimuth", name == "Capsule" ? 40 : 0);
            double settleSec = ctx.ParamDouble("settleSec", 12);
            // (drifting sideways when it hits, as a capsule under its parachutes does with the wind: drift m/s, toward its
            // low edge turned driftTurn deg about up (0: the low edge leading); held that much upwind so it still lands on the spot)
            double drift = ctx.ParamDouble("drift", 0), driftTurn = ctx.ParamDouble("driftTurn", 0);
            bool crashWas = CheatOptions.NoCrashDamage, jointsWas = CheatOptions.UnbreakableJoints;
            CheatOptions.NoCrashDamage = true;
            CheatOptions.UnbreakableJoints = true;
            // (maxFps=N: the frame rate held to N, VSync off, as a slow machine (a CI runner's software rendering) draws)
            int vsyncWas = QualitySettings.vSyncCount, targetFpsWas = Application.targetFrameRate;
            int maxFps = (int)ctx.ParamDouble("maxFps", 0);
            if (maxFps > 0)
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = maxFps;
                ctx.Record("maxFps", $"{maxFps} frames a second (vsync {vsyncWas} -> 0)");
            }
            // (frameDt=S: Unity's longest game step a frame, KSP's PHYSICS_FRAME_DT_LIMIT, for this run: below 1/S frames a
            // second game time slows. A fresh install's 0.04 (25 frames a game second, as on a CI runner); the instance's own
            // settings may say otherwise)
            float frameDtWas = Time.maximumDeltaTime, frameDtSettingWas = GameSettings.PHYSICS_FRAME_DT_LIMIT;
            double frameDt = ctx.ParamDouble("frameDt", 0);
            if (frameDt > 0)
            {
                GameSettings.PHYSICS_FRAME_DT_LIMIT = (float)frameDt;
                Time.maximumDeltaTime = (float)frameDt;
                ctx.Record("frameDt", $"{frameDt:F3} s (was {frameDtWas:F3})");
            }
            GameObject recorderGo = null, anchorGo = null;
            // (param farSet=Name=value|Name=value: FAR's PartHydrodynamics static coefficients for this run, put back after)
            var farWas = new System.Collections.Generic.List<(System.Reflection.FieldInfo f, object was)>();
            if (!string.IsNullOrEmpty(ctx.Param("farSet")))
            {
                Type hdType = AssemblyLoader.loadedAssemblies.Select(a => a.assembly.GetType("FerramAerospaceResearch.FARAeroComponents.PartHydrodynamics"))
                                            .FirstOrDefault(t => t != null);
                Assert.True(hdType != null, "FAR's PartHydrodynamics found for farSet");
                foreach (string kv in ctx.Param("farSet").Split('|'))
                {
                    string[] s = kv.Split('=');
                    System.Reflection.FieldInfo f = hdType.GetField(s[0].Trim(), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    Assert.True(f != null, $"FAR static {s[0]} found");
                    farWas.Add((f, f.GetValue(null)));
                    f.SetValue(null, Convert.ChangeType(double.Parse(s[1], CultureInfo.InvariantCulture), f.FieldType));
                    ctx.Record("farSet", $"{f.Name} {farWas[farWas.Count - 1].was} -> {f.GetValue(null)}");
                }
            }
            try
            {
                // which way is up in the tank, as it stands on the pad (as built): each drop holds it so
                Vector3 localUp = FlightGlobals.ActiveVessel.rootPart.transform.InverseTransformDirection(FlightGlobals.ActiveVessel.upAxis);
                if (ctx.Param("noon") == "true")
                    yield return TestTools.Noon(ctx);
                yield return Wait.Seconds(3);
                string said = null;
                yield return TestTools.PutOnWater(s => said = s);
                Vessel v = FlightGlobals.ActiveVessel;
                CelestialBody body = v.mainBody;
                double lat = v.latitude, lon = v.longitude;
                Part tank = v.rootPart;
                PartResource ore = tank.Resources.Get("Ore");
                Assert.True(ore != null, "an ore tank");
                // (stock buoyancy destroys a part hitting the water faster than its crash tolerance, cheat or not)
                tank.crashTolerance = 1000;
                // (the pod ballasted low: part-local y its axis, the heat shield at -y, its origin ApolloPodLow above the
                // heat shield's lowest point)
                double lowBelowOrigin = ApolloPodLow;
                Vector3? comOffset = name == "Capsule" ? new Vector3(0, (float)(ctx.ParamDouble("capsuleCoM", 0.5) - lowBelowOrigin), 0) : (Vector3?)null;
                void Ballast()
                {
                    if (comOffset == null)
                        return;
                    tank.CoMOffset = comOffset.Value;
                    if (tank.rb != null)
                        tank.rb.centerOfMass = comOffset.Value;
                }
                Ballast();
                double volume = ore.maxAmount / OreUnitsPerM3;
                double dry = tank.mass, full = dry + ore.maxAmount * ore.info.density;
                ctx.Record("body", $"{said}; {v.vesselName}: {volume:F2} m3 (the water it displaces {volume:F1} t), {PlanArea(name):F2} m2 facing the water; " +
                                   $"{dry:F2} t empty, {full:F2} t full of ore");

                TestTools.Recorder rec = null;
                StringBuilder drops = null, hits = new StringBuilder("frame,mass_t,drop_m,impact_ms,body\n");
                string dropsPath = null;
                bool recording = ctx.Param("record") == "true" || ctx.Param("record") == "video";
                if (recording || demo)
                {
                    anchorGo = new GameObject("SplashCameraAnchor");
                    var anchor = anchorGo.AddComponent<TestTools.SurfaceAnchor>();
                    anchor.Body = body;
                    anchor.Lat = lat;
                    anchor.Lon = lon;
                    anchor.Alt = ctx.ParamDouble("camAim", 4);
                    anchor.Update0();
                    FlightCamera cam = FlightCamera.fetch;
                    cam.setModeImmediate(FlightCamera.Modes.FREE);
                    cam.minPitch = -0.5f;
                    cam.SetTargetTransform(anchorGo.transform);
                    cam.SetDistanceImmediate((float)ctx.ParamDouble("camDistance", 20));
                    cam.camPitch = (float)(ctx.ParamDouble("camPitch", 3) * Mathf.Deg2Rad);
                    cam.camHdg = (float)(ctx.ParamDouble("camHeading", 0) * Mathf.Deg2Rad);
                }
                if (recording && !demo)
                {
                    GameEvents.onHideUI.Fire();
                    string frames = Path.Combine(outDir, "frames");
                    Directory.CreateDirectory(frames);
                    recorderGo = new GameObject("SplashRecorder");
                    rec = recorderGo.AddComponent<TestTools.Recorder>();
                    rec.Dir = frames;
                    rec.Fps = Math.Max(1, (int)ctx.ParamDouble("fps", 30));
                    // (record=video: <body>/video.mp4 with sound, piped to ffmpeg as it goes (param ffmpeg: its path); frames/
                    // keeping only drops.txt / impacts.txt)
                    if (ctx.Param("record") == "video")
                    {
                        rec.Video = Path.Combine(outDir, "video.mp4");
                        rec.ToneTest = ctx.Param("toneTest") == "true";
                        rec.SoundVolume = (float)ctx.ParamDouble("soundVolume", 0.5);
                        if (!string.IsNullOrEmpty(ctx.Param("ffmpeg")))
                            rec.Ffmpeg = ctx.Param("ffmpeg");
                    }
                    Time.captureFramerate = rec.Fps;
                    drops = new StringBuilder();
                    dropsPath = Path.Combine(frames, "drops.txt");
                }

                // (and splash-steps.csv: each physics step from 0.2 s before it reaches the water to 3 s after: its depth, speed,
                // acceleration, and the water's force as FAR gives it beside what its acceleration says (mass x (acc - g)))
                var steps = new StringBuilder("mass_t,t_s,low_m,vz_ms,acc_up_g,far_water_up_kN,far_impact_up_kN,needed_up_kN,unexplained_kN\n");
                var csv = new StringBuilder("mass_t,density_ratio,drop_m,impact_ms,v_after_0.1s_ms,peak_decel_g,peak_slam_kN,slam_pressure_kPa,max_depth_m,rebound_m,t_max_depth_s" +
                                         string.Concat(WakeCounts.Kinds.Select(k => ",n_" + k.Replace(' ', '_'))) + ",fps\n");
                var fixedWait = new WaitForFixedUpdate();
                // (param dampLog=true: splash-damp.csv, below)
                StringBuilder damp = ctx.Param("dampLog") == "true" ? new StringBuilder("t_s,tilt_deg,spin_degs,vz_ms,part:rb_drag:rb_angdrag:angDragByFI:submerged...\n") : null;
                string countsOff = null;
                bool firstDrop = true;
                foreach ((double mass, double dropHeight) in plan)
                {
                    ctx.Step($"{mass:F0} t from {dropHeight:F0} m");
                    v = FlightGlobals.ActiveVessel;
                    ore.amount = Math.Max(0, Math.Min(ore.maxAmount, (mass - dry) / ore.info.density));
                    yield return Wait.Frames(2);
                    double m = v.GetTotalMass();

                    // lifted upright over the same spot, its lowest point dropHeight up, held still a second
                    double t0 = Planetarium.GetUniversalTime();
                    Vector3 driftVel = Vector3.zero;
                    while (Planetarium.GetUniversalTime() - t0 < 1)
                    {
                        Vector3 up = (body.GetWorldSurfacePosition(lat, lon, 0) - body.position).normalized;
                        Vector3 aim = up;
                        if (tilt != 0 || drift != 0)
                        {
                            Vector3 los = Vector3.ProjectOnPlane(FlightCamera.fetch.mainCamera.transform.forward, up);
                            if (los.sqrMagnitude < 1e-4f)
                                los = Vector3.ProjectOnPlane(body.transform.up, up);
                            // (its heat shield's low edge, the way its top leans, tiltAzimuth deg from the camera's right
                            // toward the camera: Apollo 15's footage, the top and the risers leaning right and toward the camera)
                            Vector3 right = Vector3.Cross(up, los.normalized).normalized;
                            float az = (float)(azimuth * Math.PI / 180);
                            Vector3 lean = right * Mathf.Cos(az) - los.normalized * Mathf.Sin(az);
                            aim = Quaternion.AngleAxis((float)tilt, Vector3.Cross(up, lean)) * up;
                            driftVel = Quaternion.AngleAxis((float)driftTurn, up) * lean * (float)drift;
                        }
                        v.SetRotation(Quaternion.FromToRotation(tank.transform.TransformDirection(localUp), aim) * v.transform.rotation);
                        Ballast();
                        Vector3d at = body.GetWorldSurfacePosition(lat, lon, 0) - (Vector3d)driftVel * Math.Sqrt(2 * dropHeight / 9.81);
                        double low = TestTools.LowestAltitude(v, body);
                        v.SetPosition(v.vesselTransform.position + (at - (Vector3d)v.vesselTransform.position) -
                                      (Vector3d)up * Vector3d.Dot(at - (Vector3d)v.vesselTransform.position, up) + (Vector3d)up * (dropHeight - low));
                        foreach (Part p in v.parts)
                            if (p.Rigidbody != null)
                            {
                                p.Rigidbody.velocity = Vector3.zero;
                                p.Rigidbody.angularVelocity = Vector3.zero;
                            }
                        yield return fixedWait;
                    }
                    if (drops != null)
                    {
                        drops.AppendLine($"{rec.Frame} {mass:F0} t from {dropHeight:F0} m");
                        File.WriteAllText(dropsPath, drops.ToString());
                    }

                    // let fall
                    if (drift != 0)
                        foreach (Part p in v.parts)
                            if (p.Rigidbody != null)
                                p.Rigidbody.velocity = driftVel;
                    bool wet = false;
                    double vImpact = 0, tImpact = 0, vAfter = double.NaN, peakDecel = 0, peakSlam = 0, minLow = double.MaxValue, tMin = 0, rebound = double.MinValue;
                    // (the tank's own velocity: the vessel's srf_velocity reads 0 once a floater has slowed, while it still moves)
                    Vector3 Vel() => tank.Rigidbody.velocity + (Vector3)Krakensbane.GetFrameVelocity();
                    Vector3 velWas = Vel();
                    // (WakeSplashEffects' particles from here to the end of the settle: each kind's count; and the frames drawn a game
                    // second, which some of them depend on)
                    Dictionary<string, long> emittedBefore = WakeCounts.Snapshot();
                    int frameBefore = Time.frameCount;
                    t0 = Planetarium.GetUniversalTime();
                    double fall = Math.Sqrt(2 * dropHeight / 9.81);
                    while (Planetarium.GetUniversalTime() - t0 < fall + settleSec)
                    {
                        yield return fixedWait;
                        Assert.True(tank != null && tank.vessel != null && tank.vessel == FlightGlobals.ActiveVessel, $"the cylinder still whole ({mass:F0} t drop)");
                        Vector3 up = v.upAxis;
                        double low = TestTools.LowestAltitude(v, body), t = Planetarium.GetUniversalTime() - t0;
                        if (!wet && low <= 0)
                        {
                            wet = true;
                            vImpact = -Vector3.Dot(Vel(), up);
                            tImpact = t;
                            if (rec != null)
                            {
                                hits.AppendLine($"{rec.Frame},{F(mass)},{F(dropHeight)},{F(vImpact)},{v.vesselName}");
                                File.WriteAllText(Path.Combine(Path.GetDirectoryName(dropsPath), "impacts.txt"), hits.ToString());
                            }
                        }
                        double dt = Math.Max(1e-3, TimeWarp.fixedDeltaTime);
                        Vector3 acc = ((Vector3)Vel() - velWas) / (float)dt;
                        velWas = Vel();
                        FarWater hq = FarWater.For(tank);
                        double tw = wet ? t - tImpact : t - fall;   // (from the water, about)
                        if (tw > -0.2 && tw < 3)
                        {
                            Vector3 g = FlightGlobals.getGeeForceAtPosition(v.CoM);
                            double needed = Vector3.Dot(acc - g, up) * m;
                            double farUp = hq != null ? Vector3.Dot(hq.Force + hq.Hydrostatic, up) : 0;
                            steps.AppendLine(string.Join(",", F(m), F(tw), F(low), F(Vector3.Dot(Vel(), up)), F(Vector3.Dot(acc, up) / 9.81 + 1), F(farUp),
                                                         F(hq != null ? Vector3.Dot(hq.Impact, up) : 0), F(needed), F(needed - farUp)));
                        }
                        if (!wet)
                            continue;
                        // (splash-damp.csv: its wallowing from the hit to the end: tilt off upright, spin rate, heave speed, and
                        // each part's rigidbody drag and angular drag as Unity applied them this step (stock's slow-float
                        // damping or not), with its submerged share)
                        if (damp != null)
                        {
                            damp.Append(string.Join(",", F(t - tImpact), F(Vector3.Angle(tank.transform.TransformDirection(localUp), up)),
                                                    F(tank.Rigidbody.angularVelocity.magnitude * 180 / Math.PI), F(Vector3.Dot(Vel(), up))));
                            foreach (Part p in v.parts)
                                if (p.Rigidbody != null)
                                    damp.Append($",{p.partInfo.name}:{F(p.Rigidbody.drag)}:{F(p.Rigidbody.angularDrag)}:{(p.angularDragByFI ? 1 : 0)}:{F(p.submergedPortion)}");
                            damp.AppendLine();
                        }
                        if (double.IsNaN(vAfter) && t - tImpact >= 0.1)
                            vAfter = -Vector3.Dot(Vel(), up);
                        peakDecel = Math.Max(peakDecel, Vector3.Dot(acc, up) / 9.81 + 1);   // (up, in g, gravity's own taken out)
                        if (hq != null)
                            peakSlam = Math.Max(peakSlam, Vector3.Dot(hq.Impact, up));
                        if (low < minLow)
                        {
                            minLow = low;
                            tMin = t - tImpact;
                        }
                        if (minLow < double.MaxValue && t - tImpact > tMin)
                            rebound = Math.Max(rebound, low);
                    }
                    double area = PlanArea(name);
                    Dictionary<string, long> emitted = WakeCounts.Delta(emittedBefore, WakeCounts.Snapshot());
                    double fps = (Time.frameCount - frameBefore) / Math.Max(1e-3, Planetarium.GetUniversalTime() - t0);
                    csv.AppendLine(string.Join(",", F(m), F(m / volume), F(dropHeight), F(vImpact), F(vAfter), F(peakDecel), F(peakSlam), F(peakSlam / area), F(-minLow),
                                               F(rebound > double.MinValue ? rebound : double.NaN), F(tMin)) +
                                   string.Concat(WakeCounts.Kinds.Select(k => "," + WakeCounts.Get(emitted, k))) + "," + F(fps));
                    ctx.Record($"particles {mass:F0} t from {dropHeight:F0} m", $"{WakeCounts.Format(emitted)} ({fps:F0} frames a game second)");
                    ctx.Record($"memory after {mass:F0} t from {dropHeight:F0} m", MemoryUse());
                    countsOff = CheckCounts(ctx, name, firstDrop, mass, dropHeight, emitted) ?? countsOff;
                    firstDrop = false;
                    ctx.Record($"drop {mass:F0} t from {dropHeight:F0} m", $"{m:F2} t ({m / volume:F2} x the water it displaces): {vImpact:F1} m/s at the water, {vAfter:F1} m/s 0.1 s later; peak deceleration {peakDecel:F1} g, " +
                                                    $"slam {peakSlam:F0} kN ({peakSlam / area:F0} kPa over its end); its bottom {-minLow:F1} m down after {tMin:F2} s, " +
                                                    $"back up to {(rebound > double.MinValue ? rebound : double.NaN):+0.0;-0.0} m");
                    File.WriteAllText(Path.Combine(outDir, "splash.csv"), csv.ToString());
                    File.WriteAllText(Path.Combine(outDir, "splash-steps.csv"), steps.ToString());
                    if (damp != null)
                        File.WriteAllText(Path.Combine(outDir, "splash-damp.csv"), damp.ToString());
                }
                Assert.True(countsOff == null, countsOff);
            }
            finally
            {
                foreach ((System.Reflection.FieldInfo f, object was) in farWas)
                    f.SetValue(null, was);
                if (recorderGo != null)
                {
                    UnityEngine.Object.Destroy(recorderGo);
                    Time.captureFramerate = 0;
                    GameEvents.onShowUI.Fire();
                }
                if (anchorGo != null)
                {
                    if (FlightGlobals.ActiveVessel != null)
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                    UnityEngine.Object.Destroy(anchorGo);
                }
                CheatOptions.NoCrashDamage = crashWas;
                CheatOptions.UnbreakableJoints = jointsWas;
                QualitySettings.vSyncCount = vsyncWas;
                Application.targetFrameRate = targetFpsWas;
                GameSettings.PHYSICS_FRAME_DT_LIMIT = frameDtSettingWas;
                Time.maximumDeltaTime = frameDtWas;
            }
        }

        /// <summary>
        /// A submarine's emergency surfacing: Splash Submarine (crafts/generator/splash_crafts.py: DARPA SUBOFF's bare hull, 10 m
        /// across and 85.8 m long, one Ore tank) made subMass of the water it displaces (default 0.88: its main ballast
        /// blown), set subDepth m deep (its middle, default 20) pitched subPitch deg nose up (default 25) and moving along
        /// its axis at subSpeed m/s (default 8), aimed to break the surface abeam of the camera (the camera camDistance m off,
        /// default 160, camPitch deg down (default 3), aimed camAim m over the water (default 10), the boat crossing its view
        /// left to right), then let go and watched watchSec s (default 30; frames saved from recordDelay s on, default 0). Its centre of mass trimmed as a real boat's: a
        /// ProceduralParts tank's is at its middle, the hull's buoyancy subCoMForward m nose-ward of it (default 3.33: the
        /// SUBOFF form's fuller bow), which pitched it nose up, wholly under, with nothing to stop it; and subCoMBelow m
        /// under its axis (default 0.5) so it holds level and upright. Writes surfacing.csv each physics step: the
        /// time from letting go, its middle (the Spine part's origin) in a frame on the still water where it was aimed to
        /// break the surface (x the way it heads, y up, z = x cross y), its nose's direction there, its velocity, its pitch:
        /// for the SPH reference runs to move a body along (sph/). record=true: frames/ as the drops' (impacts.txt: the
        /// frame it is let go). Recorded, not asserted.
        /// </summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Splash Submarine", CraftDir = "VAB",
                LaunchSite = "LaunchPad", TimeoutSec = 2400)]
        public static IEnumerator Splash_SubmarineSurfacing(TestContext ctx)
        {
            // an emergency blow (subBlow, default): neutral at subDepth (default 40) running level at subSpeed (default 8 m/s),
            // its main ballast tanks fore and aft (subTankArm m either side of its buoyancy, default 26), together subReserve of
            // its displacement (default 0.12: a boat's reserve buoyancy), blown from the moment it is let go: the forward ones
            // over subForeTime s (default 6), the after ones from subAftStart s (default 3) over subAftTime s (default 10):
            // bow up first, then up, then afloat on its reserve. Else (subBlow=false) subMass of the water at subDepth (20),
            // pitched subPitch deg up (25)
            bool blow = ctx.Param("subBlow") != "false";
            double share = ctx.ParamDouble("subMass", 0.88), depth = ctx.ParamDouble("subDepth", blow ? 40 : 20), pitch = ctx.ParamDouble("subPitch", blow ? 0 : 25);
            double speed = ctx.ParamDouble("subSpeed", 8), watch = ctx.ParamDouble("watchSec", 30), recordDelay = ctx.ParamDouble("recordDelay", 0);
            double reserve = ctx.ParamDouble("subReserve", 0.12), arm = ctx.ParamDouble("subTankArm", 26);
            double foreTime = ctx.ParamDouble("subForeTime", 6), aftStart = ctx.ParamDouble("subAftStart", 3), aftTime = ctx.ParamDouble("subAftTime", 10);
            bool crashWas = CheatOptions.NoCrashDamage, jointsWas = CheatOptions.UnbreakableJoints;
            CheatOptions.NoCrashDamage = true;
            CheatOptions.UnbreakableJoints = true;
            GameObject recorderGo = null, anchorGo = null;
            string outDir = Path.Combine(ctx.ResultsDir, "Submarine");
            Directory.CreateDirectory(outDir);
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                Part hull = v.rootPart;
                // (the Spine's axis, tail to nose, is its part's y)
                Vector3 localNose = Vector3.up;
                if (ctx.Param("noon") == "true")
                    yield return TestTools.Noon(ctx);
                yield return Wait.Seconds(3);
                string said = null;
                yield return TestTools.PutOnWater(s => said = s);
                v = FlightGlobals.ActiveVessel;
                CelestialBody body = v.mainBody;
                double lat = v.latitude, lon = v.longitude;
                PartResource ore = hull.Resources.Get("Ore");
                Assert.True(ore != null, "an ore tank");
                hull.crashTolerance = 1000;
                double volume = ore.maxAmount / OreUnitsPerM3, rho = body.oceanDensity;
                double disp = volume * rho, mass = blow ? disp : share * disp, dry = hull.mass;
                double tankFull = blow ? reserve * disp / 2 : 0, boat = mass - 2 * tankFull;   // (t: each tank's water; the boat without it)
                ore.amount = Math.Max(0, Math.Min(ore.maxAmount, (mass - dry) / ore.info.density));
                yield return Wait.Frames(2);
                double m = v.GetTotalMass();
                ctx.Record("body", $"{said}; {v.vesselName}: {volume:F0} m3 (the water it displaces {volume * rho:F0} t), {m:F0} t ({m / (volume * rho):F3} of it)");

                // the camera at the point it is to break the surface; its heading across the camera's view
                anchorGo = new GameObject("SplashCameraAnchor");
                var anchor = anchorGo.AddComponent<TestTools.SurfaceAnchor>();
                anchor.Body = body;
                anchor.Lat = lat;
                anchor.Lon = lon;
                anchor.Alt = ctx.ParamDouble("camAim", 10);
                anchor.Update0();
                FlightCamera cam = FlightCamera.fetch;
                cam.setModeImmediate(FlightCamera.Modes.FREE);
                cam.minPitch = -0.5f;
                cam.SetTargetTransform(anchorGo.transform);
                cam.SetDistanceImmediate((float)ctx.ParamDouble("camDistance", 160));
                cam.camPitch = (float)(ctx.ParamDouble("camPitch", 3) * Mathf.Deg2Rad);
                cam.camHdg = (float)(ctx.ParamDouble("camHeading", 0) * Mathf.Deg2Rad);
                yield return Wait.Frames(10);
                Vector3 up0 = (body.GetWorldSurfacePosition(lat, lon, 0) - body.position).normalized;
                Vector3 fwd = Vector3.ProjectOnPlane(cam.mainCamera.transform.forward, up0).normalized;
                Vector3 heading = Vector3.Cross(up0, fwd).normalized;    // (to the camera's right)
                double ahead = blow ? ctx.ParamDouble("subBreachAhead", 70) : depth / Math.Tan(pitch * Math.PI / 180);   // (from its start to where it breaks the surface)
                ctx.Record("start", $"its middle {depth:F1} m deep, {ahead:F1} m short of the point it is aimed to break the surface, " +
                                    $"pitched {pitch:F1} deg up, {speed:F1} m/s along its axis");

                TestTools.Recorder rec = null;
                string frames = Path.Combine(outDir, "frames");
                // (record=true: frames/NNNNN.jpg; record=video: Submarine/video.mp4, piped to ffmpeg as it goes (param ffmpeg:
                // its path), frames/ keeping only impacts.txt)
                string recMode = ctx.Param("record");
                if (recMode == "true" || recMode == "video")
                {
                    GameEvents.onHideUI.Fire();
                    Directory.CreateDirectory(frames);
                    recorderGo = new GameObject("SplashRecorder");
                    rec = recorderGo.AddComponent<TestTools.Recorder>();
                    rec.Dir = frames;
                    rec.Fps = Math.Max(1, (int)ctx.ParamDouble("fps", 30));
                    if (recMode == "video")
                    {
                        rec.Video = Path.Combine(outDir, "video.mp4");
                        rec.ToneTest = ctx.Param("toneTest") == "true";
                        rec.SoundVolume = (float)ctx.ParamDouble("soundVolume", 0.5);
                        if (!string.IsNullOrEmpty(ctx.Param("ffmpeg")))
                            rec.Ffmpeg = ctx.Param("ffmpeg");
                    }
                    // (recordDelay s after it is let go before any frame is saved: the long run up from the deep, empty sea)
                    rec.Paused = recordDelay > 0;
                    Time.captureFramerate = rec.Fps;
                }

                double comFwd = ctx.ParamDouble("subCoMForward", 3.33), comBelow = ctx.ParamDouble("subCoMBelow", 0.5);
                // (part-local: y the axis, tail to nose; "below" picked once it is set level-ish in the hold below). The boat's
                // own mass there, each tank's water on the axis subTankArm either side; the hull's mass and centre of mass from them
                Vector3 belowL = -Vector3.forward;
                double fore = tankFull, aft = tankFull;
                void Trim()
                {
                    double tot = boat + fore + aft;
                    Vector3 c = (localNose * (float)comFwd + belowL * (float)comBelow) * (float)boat + localNose * (float)((comFwd + arm) * fore + (comFwd - arm) * aft);
                    hull.CoMOffset = c / (float)tot;
                    if (hull.rb != null)
                        hull.rb.centerOfMass = hull.CoMOffset;
                    ore.amount = Math.Max(0, Math.Min(ore.maxAmount, (tot - dry) / ore.info.density));
                }
                void PickBelow()
                {
                    Vector3 upL = hull.transform.InverseTransformDirection(up0);
                    belowL = -(upL - localNose * Vector3.Dot(upL, localNose)).normalized;
                }

                // set there, held a second (still water under the anchor: the frame's origin)
                Vector3 Dir() => (heading * (float)Math.Cos(pitch * Math.PI / 180) + up0 * (float)Math.Sin(pitch * Math.PI / 180)).normalized;
                var fixedWait = new WaitForFixedUpdate();
                // the run, once per view (subViews, default 2 when recording, else 1): the camera abeam as set, then ahead of
                // it looking back along its path at where it breaks the surface (camFrontDistance / camFrontPitch, default
                // camDistance / camPitch), the boat set back at its start each time; between them, subViewGap s (default 15)
                // unrecorded for the first run's spray and foam to clear. One video: the views one after the other
                int views = (int)ctx.ParamDouble("subViews", rec != null ? 2 : 1);
                var hits = new StringBuilder("frame,mass_t,drop_m,impact_ms,body\n");
                for (int view = 0; view < views; view++)
                {
                    string csvName = view == 0 ? "surfacing.csv" : $"surfacing_view{view}.csv";
                    if (view > 0)
                    {
                        if (rec != null)
                            rec.Paused = true;
                        cam.SetDistanceImmediate((float)ctx.ParamDouble("camFrontDistance", ctx.ParamDouble("camDistance", 160)));
                        cam.camPitch = (float)(ctx.ParamDouble("camFrontPitch", ctx.ParamDouble("camPitch", 3)) * Mathf.Deg2Rad);
                        // (turned a quarter round, whichever way puts it looking against the boat's heading)
                        float hdg0 = cam.camHdg;
                        cam.camHdg = hdg0 + Mathf.PI / 2;
                        yield return Wait.Frames(3);
                        if (Vector3.Dot(Vector3.ProjectOnPlane(cam.mainCamera.transform.forward, up0), -heading) < 0)
                            cam.camHdg = hdg0 - Mathf.PI / 2;
                        fore = aft = tankFull;
                        double tg = Planetarium.GetUniversalTime();
                        while (Planetarium.GetUniversalTime() - tg < ctx.ParamDouble("subViewGap", 15))
                            yield return fixedWait;
                    }
                    double t0 = Planetarium.GetUniversalTime();
                    while (Planetarium.GetUniversalTime() - t0 < 1)
                    {
                        Vector3d site = body.GetWorldSurfacePosition(lat, lon, 0);
                        Vector3 up = (Vector3)((site - body.position).normalized);
                        v.SetRotation(Quaternion.FromToRotation(hull.transform.TransformDirection(localNose), Dir()) * v.transform.rotation);
                        Vector3d start = site - (Vector3d)heading * ahead - (Vector3d)up * depth;
                        v.SetPosition(v.vesselTransform.position + (start - (Vector3d)hull.transform.position));
                        PickBelow();
                        Trim();
                        foreach (Part p in v.parts)
                            if (p.Rigidbody != null)
                            {
                                p.Rigidbody.velocity = Dir() * (float)speed - (Vector3)Krakensbane.GetFrameVelocity();
                                p.Rigidbody.angularVelocity = Vector3.zero;
                            }
                        yield return fixedWait;
                    }
                    if (rec != null)
                    {
                        hits.AppendLine($"{rec.Frame - (int)Math.Round(recordDelay * Math.Max(1, (int)ctx.ParamDouble("fps", 30)))},{F(m)},{F(depth)},{F(speed)},{v.vesselName}");   // (let go before frame 0 when recordDelay)
                        File.WriteAllText(Path.Combine(frames, "impacts.txt"), hits.ToString());
                    }

                    // let go: its path in the surface frame, every physics step
                    var csv = new StringBuilder("t_s,x_m,y_m,z_m,nose_x,nose_y,nose_z,vx_ms,vy_ms,vz_ms,pitch_deg,hydrostatic_up_kN,water_up_kN,weight_kN,submerged,fore_t,aft_t\n");
                    Vector3 zAxis = Vector3.Cross(heading, up0).normalized;
                    double topMax = double.MinValue, tTop = 0, pitchMax = double.MinValue;
                    t0 = Planetarium.GetUniversalTime();
                    ctx.Step("surfacing");
                    while (Planetarium.GetUniversalTime() - t0 < watch)
                    {
                        yield return fixedWait;
                        if (rec != null && rec.Paused && Planetarium.GetUniversalTime() - t0 >= recordDelay)
                            rec.Paused = false;
                        Assert.True(hull != null && hull.vessel != null && hull.vessel == FlightGlobals.ActiveVessel, "the hull still whole");
                        if (blow)
                        {
                            double tb = Planetarium.GetUniversalTime() - t0;
                            fore = tankFull * Math.Max(0, 1 - tb / foreTime);
                            aft = tankFull * Math.Max(0, Math.Min(1, 1 - (tb - aftStart) / aftTime));
                            Trim();
                        }
                        Vector3d site = body.GetWorldSurfacePosition(lat, lon, 0);
                        Vector3 r = (Vector3)((Vector3d)hull.transform.position - site);
                        Vector3 nose = hull.transform.TransformDirection(localNose).normalized;
                        Vector3 vel = hull.Rigidbody.velocity + (Vector3)Krakensbane.GetFrameVelocity();
                        double t = Planetarium.GetUniversalTime() - t0;
                        double pt = Math.Asin(Mathf.Clamp(Vector3.Dot(nose, up0), -1, 1)) * 180 / Math.PI;
                        FarWater hq = FarWater.For(hull);
                        double g = FlightGlobals.getGeeForceAtPosition(hull.transform.position).magnitude;
                        csv.AppendLine(string.Join(",", F(t), F(Vector3.Dot(r, heading)), F(Vector3.Dot(r, up0)), F(Vector3.Dot(r, zAxis)),
                                                   F(Vector3.Dot(nose, heading)), F(Vector3.Dot(nose, up0)), F(Vector3.Dot(nose, zAxis)),
                                                   F(Vector3.Dot(vel, heading)), F(Vector3.Dot(vel, up0)), F(Vector3.Dot(vel, zAxis)), F(pt),
                                                   F(hq != null ? Vector3.Dot(hq.Hydrostatic, up0) : double.NaN),
                                                   F(hq != null ? Vector3.Dot(hq.Force, up0) : double.NaN), F(v.GetTotalMass() * g), F(hull.submergedPortion),
                                                   F(fore), F(aft)));
                        double top = Vector3.Dot(r, up0) + Math.Abs(Vector3.Dot(nose, up0)) * SubHalfLength + 5;   // (its highest point, the hull 10 m across)
                        if (top > topMax)
                        {
                            topMax = top;
                            tTop = t;
                        }
                        pitchMax = Math.Max(pitchMax, pt);
                        if (csv.Length > 0 && ((int)(t / 0.02) % 50) == 0)
                            File.WriteAllText(Path.Combine(outDir, csvName), csv.ToString());
                    }
                    File.WriteAllText(Path.Combine(outDir, csvName), csv.ToString());
                    ctx.Record(view == 0 ? "surfacing" : "surfacing (front view)", $"its highest point {topMax:F1} m over the water at {tTop:F1} s; pitched up to {pitchMax:F1} deg; " +
                                            $"the boat's centre of mass {comFwd:F2} m forward of its middle, {comBelow:F2} m under its axis" +
                                            (blow ? $"; tanks {tankFull:F0} t each, {arm:F0} m fore and aft of it, blown 0-{foreTime:F0} s and {aftStart:F0}-{aftStart + aftTime:F0} s" : ""));
                }
            }
            finally
            {
                if (recorderGo != null)
                {
                    UnityEngine.Object.Destroy(recorderGo);
                    Time.captureFramerate = 0;
                    GameEvents.onShowUI.Fire();
                }
                if (anchorGo != null)
                {
                    if (FlightGlobals.ActiveVessel != null)
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                    UnityEngine.Object.Destroy(anchorGo);
                }
                CheatOptions.NoCrashDamage = crashWas;
                CheatOptions.UnbreakableJoints = jointsWas;
            }
        }

        /// <summary>
        /// A many-part plane ditching on the sea: the stock Stearwing A300 airliner (56 parts; craft param to change it),
        /// gear up, engines idle, set ditchHeight m (default 3) over the water at its lowest point, flying along at
        /// ditchSpeed m/s (default 55), sinking ditchSink m/s (default 1.5), pitched ditchPitch deg nose up (default 6),
        /// aimed to touch the water abeam of the camera (ditchAhead m short of it, default speed x height / sink; the camera
        /// camDistance m off, default 70 following it (camFollow) else 140, camPitch deg down (default 10), aimed camAim m over the water (default 3), the
        /// plane crossing its view left to right), then let go and watched watchSec s (default 20). Crash damage and joint
        /// breaking off. Writes ditching.csv each physics step (time, distance along its track from the aim point, its
        /// lowest point's height, speed, pitch, parts in the water). record=true / video as the other splash tests
        /// (record=video: Ditching/video.mp4 with sound). Recorded, not asserted.
        /// </summary>
        [UITest(Kind = "Measure", Explicit = true, Mods = Mods, StartsAt = StartState.Flight, Craft = "Stearwing A300", CraftDir = "SPH",
                LaunchSite = "Runway", TimeoutSec = 2400)]
        public static IEnumerator Splash_PlaneDitching(TestContext ctx)
        {
            double h0 = ctx.ParamDouble("ditchHeight", 3), speed = ctx.ParamDouble("ditchSpeed", 55), sink = Math.Max(0.1, ctx.ParamDouble("ditchSink", 1.5));
            double pitch = ctx.ParamDouble("ditchPitch", 6), watch = ctx.ParamDouble("watchSec", 20), recordDelay = ctx.ParamDouble("recordDelay", 0);
            double ahead = ctx.ParamDouble("ditchAhead", speed * h0 / sink);
            bool crashWas = CheatOptions.NoCrashDamage, jointsWas = CheatOptions.UnbreakableJoints;
            CheatOptions.NoCrashDamage = true;
            CheatOptions.UnbreakableJoints = true;
            GameObject recorderGo = null, anchorGo = null;
            string outDir = Path.Combine(ctx.ResultsDir, "Ditching");
            Directory.CreateDirectory(outDir);
            try
            {
                if (ctx.Param("noon") == "true")
                    yield return TestTools.Noon(ctx);
                yield return Wait.Seconds(3);
                string said = null;
                yield return TestTools.PutOnWater(s => said = s);
                Vessel v = FlightGlobals.ActiveVessel;
                CelestialBody body = v.mainBody;
                double lat = v.latitude, lon = v.longitude;
                int parts0 = v.parts.Count;
                // (gear up, the engines idle: a glide in)
                v.ActionGroups.SetGroup(KSPActionGroup.Gear, false);
                FlightInputHandler.state.mainThrottle = 0;
                foreach (Part p in v.parts)
                    p.crashTolerance = 1000;
                ctx.Record("plane", $"{said}; {v.vesselName}: {parts0} parts, {v.GetTotalMass():F1} t");

                // the camera abeam of where it is to touch the water; its heading across the camera's view
                anchorGo = new GameObject("SplashCameraAnchor");
                var anchor = anchorGo.AddComponent<TestTools.SurfaceAnchor>();
                anchor.Body = body;
                anchor.Lat = lat;
                anchor.Lon = lon;
                anchor.Alt = ctx.ParamDouble("camAim", 3);
                anchor.Update0();
                FlightCamera cam = FlightCamera.fetch;
                cam.setModeImmediate(FlightCamera.Modes.FREE);
                cam.minPitch = -0.5f;
                cam.SetTargetTransform(anchorGo.transform);
                // (camFollow, default true: the aim point kept on the plane as it skims on, the view's angle fixed)
                bool follow = ctx.Param("camFollow") != "false";
                cam.SetDistanceImmediate((float)ctx.ParamDouble("camDistance", follow ? 70 : 140));
                cam.camPitch = (float)(ctx.ParamDouble("camPitch", 10) * Mathf.Deg2Rad);
                cam.camHdg = (float)(ctx.ParamDouble("camHeading", 0) * Mathf.Deg2Rad);
                yield return Wait.Frames(10);
                Vector3 up0 = (body.GetWorldSurfacePosition(lat, lon, 0) - body.position).normalized;
                Vector3 fwd = Vector3.ProjectOnPlane(cam.mainCamera.transform.forward, up0).normalized;
                Vector3 heading = Vector3.Cross(up0, fwd).normalized;    // (to the camera's right)
                ctx.Record("start", $"its lowest point {h0:F1} m over the water, {ahead:F0} m short of the aim point, {speed:F0} m/s, sinking {sink:F1} m/s, pitched {pitch:F1} deg up");

                TestTools.Recorder rec = null;
                string frames = Path.Combine(outDir, "frames");
                string recMode = ctx.Param("record");
                if (recMode == "true" || recMode == "video")
                {
                    GameEvents.onHideUI.Fire();
                    Directory.CreateDirectory(frames);
                    recorderGo = new GameObject("SplashRecorder");
                    rec = recorderGo.AddComponent<TestTools.Recorder>();
                    rec.Dir = frames;
                    rec.Fps = Math.Max(1, (int)ctx.ParamDouble("fps", 30));
                    if (recMode == "video")
                    {
                        rec.Video = Path.Combine(outDir, "video.mp4");
                        rec.ToneTest = ctx.Param("toneTest") == "true";
                        rec.SoundVolume = (float)ctx.ParamDouble("soundVolume", 0.5);
                        if (!string.IsNullOrEmpty(ctx.Param("ffmpeg")))
                            rec.Ffmpeg = ctx.Param("ffmpeg");
                    }
                    rec.Paused = recordDelay > 0;
                    Time.captureFramerate = rec.Fps;
                }

                // set there, held a second: nose along its heading pitched up, belly down, flying along it and sinking
                Vector3 nose = (heading * (float)Math.Cos(pitch * Math.PI / 180) + up0 * (float)Math.Sin(pitch * Math.PI / 180)).normalized;
                Vector3 belly = -Vector3.ProjectOnPlane(up0, nose).normalized;
                Vector3 vel = heading * (float)speed - up0 * (float)sink;
                var fixedWait = new WaitForFixedUpdate();
                double t0 = Planetarium.GetUniversalTime();
                while (Planetarium.GetUniversalTime() - t0 < 1)
                {
                    Vector3d site = body.GetWorldSurfacePosition(lat, lon, 0);
                    Vector3 up = (Vector3)((site - body.position).normalized);
                    // (a plane's craft: its root's y along the nose, z down through its belly)
                    v.SetRotation(Quaternion.LookRotation(belly, nose));
                    Vector3d start = site - (Vector3d)heading * ahead + (Vector3d)up * h0;
                    v.SetPosition(start);
                    double low = TestTools.LowestAltitude(v, body);
                    v.SetPosition(v.vesselTransform.position + up * (float)(h0 - low));
                    if (follow)
                    {
                        anchor.Lat = v.latitude;
                        anchor.Lon = v.longitude;
                    }
                    FlightInputHandler.state.mainThrottle = 0;
                    foreach (Part p in v.parts)
                        if (p.Rigidbody != null)
                        {
                            p.Rigidbody.velocity = vel - (Vector3)Krakensbane.GetFrameVelocity();
                            p.Rigidbody.angularVelocity = Vector3.zero;
                        }
                    yield return fixedWait;
                }

                // let go: its path, every physics step
                var csv = new StringBuilder("t_s,x_m,low_m,speed_ms,pitch_deg,parts_wet,parts\n");
                double tTouch = -1, minLow = double.MaxValue;
                t0 = Planetarium.GetUniversalTime();
                ctx.Step("ditching");
                while (Planetarium.GetUniversalTime() - t0 < watch)
                {
                    yield return fixedWait;
                    if (rec != null && rec.Paused && Planetarium.GetUniversalTime() - t0 >= recordDelay)
                        rec.Paused = false;
                    v = FlightGlobals.ActiveVessel;
                    if (v == null)
                        break;
                    if (follow)
                    {
                        anchor.Lat = v.latitude;
                        anchor.Lon = v.longitude;
                    }
                    FlightInputHandler.state.mainThrottle = 0;
                    double t = Planetarium.GetUniversalTime() - t0;
                    Vector3d site = body.GetWorldSurfacePosition(lat, lon, 0);
                    double low = TestTools.LowestAltitude(v, body);
                    int wet = v.parts.Count(p => p.WaterContact);
                    if (tTouch < 0 && wet > 0)
                        tTouch = t;
                    minLow = Math.Min(minLow, low);
                    Vector3 nz = v.ReferenceTransform != null ? v.ReferenceTransform.up : v.transform.up;
                    double pt = Math.Asin(Mathf.Clamp(Vector3.Dot(nz, up0), -1, 1)) * 180 / Math.PI;
                    Vector3 vv = (Vector3)v.GetSrfVelocity();
                    csv.AppendLine(string.Join(",", F(t), F(Vector3.Dot((Vector3)((Vector3d)v.CoMD - site), heading)), F(low), F(vv.magnitude), F(pt), wet, v.parts.Count));
                }
                File.WriteAllText(Path.Combine(outDir, "ditching.csv"), csv.ToString());
                ctx.Record("ditching", $"touched the water at {tTouch:F1} s; lowest point down to {minLow:F1} m; {(v != null ? v.parts.Count : 0)} of {parts0} parts still on it");
            }
            finally
            {
                if (recorderGo != null)
                {
                    UnityEngine.Object.Destroy(recorderGo);
                    Time.captureFramerate = 0;
                    GameEvents.onShowUI.Fire();
                }
                if (anchorGo != null)
                {
                    if (FlightGlobals.ActiveVessel != null)
                        FlightCamera.fetch.SetTargetVessel(FlightGlobals.ActiveVessel);
                    UnityEngine.Object.Destroy(anchorGo);
                }
                CheatOptions.NoCrashDamage = crashWas;
                CheatOptions.UnbreakableJoints = jointsWas;
            }
        }

        private const double SubHalfLength = 85.75 / 2;   // (crafts/generator/splash_crafts.py)
        private const double ApolloPodLow = 1.377;         // (crafts/Splash Apollo Pod.craft: the pod's bottom node and the shield's height, x1.564)

        private static string F(double x) => x.ToString("G5", CultureInfo.InvariantCulture);
    }
}
