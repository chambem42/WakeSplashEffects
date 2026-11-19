using System;
using System.Collections;
using System.IO;
using System.Reflection;
using KspTest;
using UnityEngine;

namespace WaterWakes.KspTests
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

        /// <summary>Saves the screen at the end of every frame (Dir/NNNNN.jpg).</summary>
        internal class Recorder : MonoBehaviour
        {
            public string Dir;
            public int Frame;

            private IEnumerator Start()
            {
                var end = new WaitForEndOfFrame();
                while (true)
                {
                    yield return end;
                    Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
                    File.WriteAllBytes(Path.Combine(Dir, $"{Frame:D5}.jpg"), tex.EncodeToJPG(92));
                    Destroy(tex);
                    Frame++;
                }
            }
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
}
