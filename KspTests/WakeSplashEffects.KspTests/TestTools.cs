using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using KspTest;
using UnityEngine;

namespace WakeSplashEffects.KspTests
{
    /// <summary>
    /// The water tests' helpers (as FAR's KspTests have them: FarWaterTools, BoatTests.Noon, PlaningVideoTests.Recorder):
    /// a vessel put on the sea, its lowest point, the clock moved to noon, the screen saved each frame.
    /// </summary>
    internal static class TestTools
    {
        /// <summary>The water tests' site on a body: RSS's Earth off Cape Canaveral, else 0 N 60 W (Kerbin's sea).</summary>
        internal static (double lat, double lon) WaterSite(CelestialBody body) =>
            body.Radius > 1e6 ? (28.45, -80.45) : (0.0, -60.0);

        /// <summary>
        /// Puts the active vessel on the water at the water site, its lowest point at the surface, held while the water
        /// takes its weight (let down no faster than 0.5 m/s, its drift and spin damped) until it floats still or has
        /// stopped going down, then let go; say reports what it did. A vessel standing up (as the VAB builds them) keeps
        /// standing.
        /// </summary>
        internal static IEnumerator PutOnWater(Action<string> say)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null)
                yield break;
            CelestialBody body = v.mainBody;
            if (!body.ocean)
            {
                say($"{body.bodyName} has no sea");
                yield break;
            }
            (double lat, double lon) = WaterSite(body);
            Vector3 up = v.upAxis;
            Vector3 nose = v.ReferenceTransform != null ? v.ReferenceTransform.up : v.transform.up;
            bool upright = Mathf.Abs(Vector3.Dot(nose, up)) > 0.7f;
            Vector3 nh = Vector3.ProjectOnPlane(nose, up);
            double heading = upright || nh.sqrMagnitude < 1e-4f ? 90
                : Math.Atan2(Vector3.Dot(nh, (Vector3)v.east), Vector3.Dot(nh, (Vector3)v.north)) * 180 / Math.PI;

            EaseInOff(v);
            TimeWarp.SetRate(0, true);
            yield return Place(lat, lon, Math.Max(0, body.TerrainAltitude(lat, lon)) + 600, heading);
            v = FlightGlobals.ActiveVessel;
            up = v.upAxis;
            if (upright)
            {
                Transform rt = v.ReferenceTransform != null ? v.ReferenceTransform : v.transform;
                v.SetRotation(Quaternion.FromToRotation(rt.up, up) * v.transform.rotation);
            }
            Still(v);
            yield return new WaitForFixedUpdate();

            double low = LowestAltitude(v, body), high = low;
            foreach (Part p in v.parts)
                high = Math.Max(high, FlightGlobals.getAltitudeAtPos((Vector3d)p.transform.position, body));
            if (low < double.MaxValue)
                v.SetPosition(v.vesselTransform.position - (Vector3d)up * low);
            Still(v);

            double t0 = Planetarium.GetUniversalTime(), quiet = 0, lowest = double.MaxValue, sinceLow = 0;
            float maxDown = low < double.MaxValue ? Mathf.Max(0.5f, 0.15f * (float)(high - low)) : 0.5f;
            while (true)
            {
                yield return new WaitForFixedUpdate();
                v = FlightGlobals.ActiveVessel;
                Hold(v, maxDown);
                double t = Planetarium.GetUniversalTime() - t0;
                quiet = v.Splashed && Math.Abs(v.verticalSpeed) < 0.15 ? quiet + TimeWarp.fixedDeltaTime : 0;
                if (v.altitude < lowest - 0.05)
                {
                    lowest = v.altitude;
                    sinceLow = 0;
                }
                else if (v.Splashed)
                    sinceLow += TimeWarp.fixedDeltaTime;
                if (t > 2 && (quiet > 1.5 || sinceLow > 1.5) || t > 20)
                    break;
            }
            say($"{v.vesselName} on the water at {lat:F2}, {lon:F2}, held {Planetarium.GetUniversalTime() - t0:F1} s");
        }

        // the vessel at (lat, lon, alt), at rest over the ground, level, the way HyperEdit's orbit editor would: an orbit
        // from those state vectors (KspTest.Far's FarFlightProbe.Place at no speed, which needs FAR's API to build)
        private static double coastSec = 2.2;

        private static IEnumerator Place(double lat, double lon, double alt, double headingDeg)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            CelestialBody body = v.mainBody;
            double ut = Planetarium.GetUniversalTime();
            double h = headingDeg * Math.PI / 180, r0 = body.Radius + alt;
            double spin = body.rotationPeriod != 0 ? 2 * Math.PI / body.rotationPeriod : 0, cosLat = Math.Cos(lat * Math.PI / 180);
            // (it coasts on rails a while before it unpacks: start below the point, rising, so the arc's apex is the point)
            double upAcc = -body.gravParameter / (r0 * r0) + spin * spin * r0 * cosLat * cosLat;
            double climb = -upAcc * coastSec, below = -0.5 * upAcc * coastSec * coastSec;
            Vector3d pos = body.GetWorldSurfacePosition(lat, lon, alt - below);
            Vector3d north = (body.GetWorldSurfacePosition(lat + 1e-4, lon, alt - below) - pos).normalized;
            Vector3d east = (body.GetWorldSurfacePosition(lat, lon + 1e-4, alt - below) - pos).normalized;
            Vector3d upDir = (pos - body.position).normalized;
            var o = new Orbit();
            o.UpdateFromStateVectors((pos - body.position).xzy, (upDir * climb + body.getRFrmVel(pos)).xzy, body, ut);
            FlightGlobals.fetch.SetShipOrbit(body.flightGlobalsIndex, o.eccentricity, o.semiMajorAxis, o.inclination, o.LAN,
                                             o.meanAnomaly, o.argumentOfPeriapsis, o.ObT);
            Vector3d level = (north * Math.Cos(h) + east * Math.Sin(h)).normalized;
            Flight.PointAtAoA(0, level, upDir);
            yield return Wait.Until(() => FlightGlobals.ActiveVessel != null && !FlightGlobals.ActiveVessel.packed, 30, "vessel off rails", shot: false);
            coastSec = Math.Max(0, Math.Min(10, Planetarium.GetUniversalTime() - ut));
            for (int i = 0; i < 5; ++i)
            {
                Flight.PointAtAoA(0, level, upDir);
                foreach (Part part in FlightGlobals.ActiveVessel.parts)
                    if (part.Rigidbody != null)
                        part.Rigidbody.angularVelocity = Vector3.zero;
                yield return new WaitForFixedUpdate();
            }
        }

        /// <summary>The altitude of the vessel's lowest collider point (its boxes', spheres', capsules' and meshes' own shapes).</summary>
        internal static double LowestAltitude(Vessel v, CelestialBody body)
        {
            double low = double.MaxValue;
            void At(Vector3 w) => low = Math.Min(low, FlightGlobals.getAltitudeAtPos((Vector3d)w, body));
            Vector3 down = -(Vector3)v.upAxis;
            foreach (Part p in v.parts)
                foreach (Collider c in p.GetComponentsInChildren<Collider>())
                {
                    if (!c.enabled || c.isTrigger)
                        continue;
                    Transform t = c.transform;
                    switch (c)
                    {
                        case BoxCollider b:
                            for (int i = 0; i < 8; i++)
                                At(t.TransformPoint(b.center + Vector3.Scale(b.size * 0.5f, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                            break;
                        case SphereCollider s:
                        {
                            Vector3 ls = t.lossyScale;
                            At(t.TransformPoint(s.center) + down * s.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                            break;
                        }
                        case CapsuleCollider k:
                        {
                            Vector3 ls = t.lossyScale;
                            float r = k.radius * Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
                            Vector3 axis = k.direction == 0 ? Vector3.right : k.direction == 1 ? Vector3.up : Vector3.forward;
                            float half = Mathf.Max(0, k.height * 0.5f - k.radius);
                            At(t.TransformPoint(k.center + axis * half) + down * r);
                            At(t.TransformPoint(k.center - axis * half) + down * r);
                            break;
                        }
                        case MeshCollider m when m.sharedMesh != null:
                            foreach (Vector3 vert in m.sharedMesh.vertices)
                                At(t.TransformPoint(vert));
                            break;
                        default:
                        {
                            Bounds b = c.bounds;
                            for (int i = 0; i < 8; i++)
                                At(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                            break;
                        }
                    }
                }
            return low;
        }

        // its fall held to maxDown m/s (no rise faster either), its drift and spin damped
        private static void Hold(Vessel v, float maxDown)
        {
            Vector3 up = v.upAxis;
            foreach (Part p in v.parts)
                if (p.Rigidbody != null)
                {
                    Vector3 vel = p.Rigidbody.velocity;
                    float vz = Vector3.Dot(vel, up);
                    p.Rigidbody.velocity = (vel - up * vz) * 0.8f + up * Mathf.Clamp(vz, -maxDown, maxDown);
                    p.Rigidbody.angularVelocity *= 0.8f;
                }
        }

        private static void Still(Vessel v)
        {
            foreach (Part p in v.parts)
                if (p.Rigidbody != null)
                {
                    p.Rigidbody.velocity = Vector3.zero;
                    p.Rigidbody.angularVelocity = Vector3.zero;
                }
        }

        // the Set Position cheat's ease-in off (FlightGlobals' own, as its "Disable ease-in" does)
        private static void EaseInOff(Vessel v)
        {
            if (!v.easingInToSurface)
                return;
            typeof(FlightGlobals).GetMethod("ToggleVesselEaseIn", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(FlightGlobals.fetch, new object[] { v, false, 1.0 });
        }

        /// <summary>The clock moved on to when the sun stands highest over the water site, hour by hour over a day, the
        /// active vessel held on rails meanwhile: for videos lit from above.</summary>
        internal static IEnumerator Noon(TestContext ctx)
        {
            Vessel nv = FlightGlobals.ActiveVessel;
            nv.GoOnRails();
            CelestialBody nb = nv.mainBody;
            (double lat, double lon) = WaterSite(nb);
            double day = nb.solarDayLength > 0 ? nb.solarDayLength : nb.rotationPeriod;
            double ut0 = Planetarium.GetUniversalTime(), bestUt = ut0, bestEl = -90;
            for (int hh = 0; hh < 48; hh++)
            {
                Planetarium.SetUniversalTime(ut0 + hh * day / 48);
                yield return Wait.Frames(2);
                double el = 90 - Vector3d.Angle(nb.GetSurfaceNVector(lat, lon), (Planetarium.fetch.Sun.position - nb.position).normalized);
                if (el > bestEl)
                {
                    bestEl = el;
                    bestUt = Planetarium.GetUniversalTime();
                }
            }
            Planetarium.SetUniversalTime(bestUt);
            yield return Wait.Frames(2);
            ctx.Record("time of day", $"UT {bestUt:F0} s: the sun {bestEl:F0} deg up at {lat:F2} N {lon:F2} E");
        }

        /// <summary>Saves the screen at the end of every frame (Dir/NNNNN.jpg), except while Paused (nothing written; the
        /// frame count not advanced). With Video set: each frame's raw pixels piped to an ffmpeg process instead (Ffmpeg:
        /// its path, default "ffmpeg" on the PATH), encoded as it goes to that .mp4 at Fps (H.264, Height / Crf / Preset):
        /// a few MB rather than hundreds of MB of JPEGs, and no encode afterwards. With Sound too (default): the game's
        /// audio, rendered by Unity's AudioRenderer in step with the capture frame rate (the speakers silent meanwhile),
        /// each frame's samples kept beside it (those of paused frames dropped with them), muxed in as AAC at the end.
        /// Finished when the recorder is destroyed.</summary>
        internal class Recorder : MonoBehaviour
        {
            public string Dir;
            public int Frame;
            public bool Paused;
            public string Video, Ffmpeg = "ffmpeg";
            public int Fps = 30;
            public int Height = 720, Crf = 24;    // (the demo clips': 720p (aspect kept), crf 24, x264 medium; Height 0: as captured)
            public string Preset = "medium";
            public bool Sound = true;
            public float SoundVolume = 0.5f;   // the listener's volume while it records: KspTest mutes the game (MASTER_VOLUME 0),
                                               // and AudioRenderer sends the speakers silence meanwhile anyway
            private float listenerWas = -1;
            public bool ToneTest;    // (diagnostic: a quiet 440 Hz tone played while recording, to tell a silent game from a broken capture)
            private float peak;
            private int peakFrames;
            private AudioSource tone;

            private System.Diagnostics.Process ff;
            private Stream pipe;
            private int vw, vh, bpp;
            private string target, silentVideo, audioRaw;
            private FileStream audio;
            private bool rendering;
            private int channels, rate;

            private IEnumerator Start()
            {
                var end = new WaitForEndOfFrame();
                while (true)
                {
                    yield return end;
                    TakeAudio(!Paused && Video != null);
                    if (Paused)
                        continue;
                    Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
                    if (Video != null)
                        Pipe(tex);
                    else
                        File.WriteAllBytes(Path.Combine(Dir, $"{Frame:D5}.jpg"), tex.EncodeToJPG(92));
                    Destroy(tex);
                    Frame++;
                }
            }

            // this frame's audio (GetSampleCountForCaptureFrame: the capture frame rate's share), kept or dropped
            private void TakeAudio(bool keep)
            {
                if (!Sound || Video == null)
                    return;
                if (!rendering)
                {
                    rate = AudioSettings.outputSampleRate;
                    AudioSpeakerMode mode = AudioSettings.GetConfiguration().speakerMode;
                    channels = mode == AudioSpeakerMode.Mono ? 1 : mode == AudioSpeakerMode.Quad ? 4 : mode == AudioSpeakerMode.Surround ? 5
                             : mode == AudioSpeakerMode.Mode5point1 ? 6 : mode == AudioSpeakerMode.Mode7point1 ? 8 : 2;
                    rendering = AudioRenderer.Start();
                    if (ToneTest)
                    {
                        var clip = AudioClip.Create("KspTest tone", rate, 1, rate, false);
                        var d = new float[rate];
                        for (int i = 0; i < rate; i++)
                            d[i] = 0.2f * Mathf.Sin(2 * Mathf.PI * 440 * i / rate);
                        clip.SetData(d, 0);
                        tone = gameObject.AddComponent<AudioSource>();
                        tone.clip = clip;
                        tone.loop = true;
                        tone.spatialBlend = 0;
                        tone.Play();
                    }
                    if (!rendering)
                    {
                        Debug.LogError("[KspTest] AudioRenderer would not start: the video without sound");
                        Sound = false;
                        return;
                    }
                }
                // (each frame: KSP sets the listener's volume from its settings)
                if (listenerWas < 0)
                    listenerWas = AudioListener.volume;
                AudioListener.volume = SoundVolume;
                int n = AudioRenderer.GetSampleCountForCaptureFrame();
                if (n <= 0)
                    return;
                var buf = new Unity.Collections.NativeArray<float>(n * channels, Unity.Collections.Allocator.Temp);
                try
                {
                    AudioRenderer.Render(buf);
                    // (diagnostic, each second: the samples' peak, the listener, the sources playing)
                    for (int i = 0; i < buf.Length; i++)
                        peak = Mathf.Max(peak, Mathf.Abs(buf[i]));
                    if (++peakFrames >= Fps)
                    {
                        int playing = 0;
                        foreach (AudioSource a in FindObjectsOfType<AudioSource>())
                            if (a.isPlaying)
                                playing++;
                        Debug.Log($"[KspTest] sound: peak {peak:F4}, listener volume {AudioListener.volume:F2}{(AudioListener.pause ? " PAUSED" : "")}, {playing} sources playing, {n} samples/frame");
                        peak = 0;
                        peakFrames = 0;
                    }
                    if (!keep)
                        return;
                    if (audio == null)
                    {
                        audioRaw = Video + ".audio.f32";
                        audio = new FileStream(audioRaw, FileMode.Create, FileAccess.Write);
                    }
                    float[] f = buf.ToArray();
                    var bytes = new byte[f.Length * 4];
                    Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
                    audio.Write(bytes, 0, bytes.Length);
                }
                finally
                {
                    buf.Dispose();
                }
            }

            private void Pipe(Texture2D tex)
            {
                if (ff == null)
                {
                    // (the capture's rows bottom up: flipped and converted by ffmpeg; even sizes for yuv420p). With sound, the
                    // picture first to a silent file beside it, the sound muxed in at the end
                    vw = tex.width;
                    vh = tex.height;
                    bpp = tex.format == TextureFormat.RGB24 ? 3 : 4;
                    target = Video;
                    silentVideo = Sound ? Path.ChangeExtension(Video, ".silent.mp4") : Video;
                    string pf = bpp == 3 ? "rgb24" : tex.format == TextureFormat.ARGB32 ? "argb" : tex.format == TextureFormat.BGRA32 ? "bgra" : "rgba";
                    string args = $"-y -loglevel error -f rawvideo -pix_fmt {pf} -s {vw}x{vh} -r {Fps} -i - " +
                                  $"-vf \"vflip,{(Height > 0 ? $"scale=-2:{Height}" : "crop=trunc(iw/2)*2:trunc(ih/2)*2")}\" -c:v libx264 -preset {Preset} -crf {Crf} -pix_fmt yuv420p -movflags +faststart \"{silentVideo}\"";
                    try
                    {
                        ff = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Ffmpeg, args)
                        {
                            UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true,
                        });
                        pipe = ff.StandardInput.BaseStream;
                        Debug.Log($"[KspTest] recording video to {Video} ({vw}x{vh} at {Fps} fps{(Sound ? ", with sound" : "")}, {Ffmpeg})");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[KspTest] could not start {Ffmpeg} ({e.Message}): saving frames as JPEGs instead");
                        Video = null;
                        return;
                    }
                }
                if (tex.width != vw || tex.height != vh)
                    return;   // (the window resized: those frames dropped)
                byte[] raw = tex.GetRawTextureData();
                if (raw.Length != vw * vh * bpp)
                {
                    Debug.LogError($"[KspTest] video frame {raw.Length} bytes, not {vw}x{vh}x{bpp} ({tex.format}): recording stopped");
                    Video = null;
                    Finish();
                    return;
                }
                try
                {
                    pipe.Write(raw, 0, raw.Length);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[KspTest] video pipe failed ({e.Message}): recording stopped");
                    Video = null;
                    Finish();
                }
            }

            private void Finish()
            {
                if (rendering)
                {
                    AudioRenderer.Stop();
                    rendering = false;
                }
                if (listenerWas >= 0)
                {
                    AudioListener.volume = listenerWas;
                    listenerWas = -1;
                }
                audio?.Close();
                if (ff == null)
                    return;
                try
                {
                    pipe.Close();
                    if (!ff.WaitForExit(60000))
                        Debug.LogError("[KspTest] ffmpeg did not finish the video within 60 s");
                    else
                        Debug.Log($"[KspTest] video written: {silentVideo}, {Frame} frames, ffmpeg exit {ff.ExitCode}");
                    ff = null;
                    if (silentVideo != target)
                        Mux();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[KspTest] finishing the video: {e.Message}");
                }
                ff = null;
            }

            // the sound into the picture (the picture copied as is): Video; the silent file and the raw samples then deleted
            private void Mux()
            {
                if (audio == null || !File.Exists(audioRaw) || new FileInfo(audioRaw).Length == 0)
                {
                    File.Copy(silentVideo, target, true);
                    File.Delete(silentVideo);
                    Debug.Log($"[KspTest] no sound recorded: {target} silent");
                    return;
                }
                string args = $"-y -loglevel error -i \"{silentVideo}\" -f f32le -ar {rate} -ac {channels} -i \"{audioRaw}\" " +
                              $"-c:v copy -c:a aac -b:a 160k -ac 2 -shortest -movflags +faststart \"{target}\"";
                var mux = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true });
                if (mux.WaitForExit(60000) && mux.ExitCode == 0)
                {
                    File.Delete(silentVideo);
                    File.Delete(audioRaw);
                    Debug.Log($"[KspTest] video with sound: {target} ({rate} Hz, {channels} ch)");
                }
                else
                    Debug.LogError($"[KspTest] muxing the sound failed (exit {(mux.HasExited ? mux.ExitCode : -1)}): the silent video and the samples kept ({silentVideo}, {audioRaw})");
            }

            private void OnDestroy() => Finish();
        }

        /// <summary>The recording camera's target: a fixed point Alt m above the sea at (Lat, Lon), kept there as the
        /// floating origin moves.</summary>
        internal class SurfaceAnchor : MonoBehaviour
        {
            public CelestialBody Body;
            public double Lat, Lon, Alt;

            internal void Update0() => Update();

            private void Update()
            {
                if (Body == null)
                    return;
                transform.position = Body.GetWorldSurfacePosition(Lat, Lon, Alt);
            }
        }
    }

    /// <summary>
    /// FAR's water model's forces on a part (FARAeroPartModule's PartHydrodynamics: LastForce, LastHydrostaticForce,
    /// LastImpactForce, kN, world), read by reflection so the tests build without FAR; null without FAR's water model.
    /// </summary>
    internal sealed class FarWater
    {
        private static FieldInfo force, hydrostatic, impact;
        private static PropertyInfo hydroProp;
        private readonly PartModule module;

        private FarWater(PartModule m) => module = m;

        internal static FarWater For(Part p)
        {
            PartModule m = p.Modules.Contains("FARAeroPartModule") ? p.Modules["FARAeroPartModule"] : null;
            if (m == null)
                return null;
            if (hydroProp == null)
            {
                hydroProp = m.GetType().GetProperty("Hydrodynamics", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Type hd = hydroProp?.PropertyType;
                if (hd == null)
                    return null;
                force = hd.GetField("LastForce");
                hydrostatic = hd.GetField("LastHydrostaticForce");
                impact = hd.GetField("LastImpactForce");
            }
            return force != null ? new FarWater(m) : null;
        }

        private Vector3 Get(FieldInfo f)
        {
            object hd = hydroProp.GetValue(module);
            return hd != null ? (Vector3)f.GetValue(hd) : Vector3.zero;
        }

        internal Vector3 Force => Get(force);
        internal Vector3 Hydrostatic => Get(hydrostatic);
        internal Vector3 Impact => Get(impact);
    }

    /// <summary>
    /// The particles WakeSplashEffects has emitted, by kind (WakeStats.Snapshot: "foam", "skirt", "splash mist", ...), read by
    /// reflection like FarWater; empty without the mod (or a build without WakeStats).
    /// </summary>
    internal static class WakeCounts
    {
        /// <summary>The kinds in a fixed order, for CSV columns (any others a build adds follow, by name).</summary>
        internal static readonly string[] Kinds =
        {
            "skirt", "sheet", "spray", "slam spray", "splash cloud", "haze", "mist", "splash mist", "foam", "splash foam", "upwell",
        };

        private static MethodInfo snapshot;

        internal static Dictionary<string, long> Snapshot()
        {
            if (snapshot == null)
            {
                Type t = AssemblyLoader.loadedAssemblies.Select(a => a.assembly.GetType("WakeSplashEffects.WakeStats")).FirstOrDefault(x => x != null);
                snapshot = t?.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static);
                if (snapshot == null)
                    return new Dictionary<string, long>();
            }
            return snapshot.Invoke(null, null) as Dictionary<string, long> ?? new Dictionary<string, long>();
        }

        /// <summary>after - before, each kind of either (a kind made since counts from 0).</summary>
        internal static Dictionary<string, long> Delta(Dictionary<string, long> before, Dictionary<string, long> after)
        {
            var d = new Dictionary<string, long>();
            foreach (KeyValuePair<string, long> kv in after)
                d[kv.Key] = kv.Value - (before.TryGetValue(kv.Key, out long b) ? b : 0);
            return d;
        }

        internal static long Get(Dictionary<string, long> d, string kind) => d.TryGetValue(kind, out long n) ? n : 0;

        /// <summary>"skirt 812, sheet 0, ..." in Kinds' order, the non-zero ones only (or "none").</summary>
        internal static string Format(Dictionary<string, long> d)
        {
            IEnumerable<string> order = Kinds.Concat(d.Keys.Where(k => !Kinds.Contains(k)).OrderBy(k => k));
            string s = string.Join(", ", order.Where(k => Get(d, k) != 0).Select(k => $"{k} {Get(d, k)}"));
            return s.Length > 0 ? s : "none";
        }
    }
}
