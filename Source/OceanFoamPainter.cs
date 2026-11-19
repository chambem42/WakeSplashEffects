using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace WaterWakes
{
    /// <summary>
    /// Paints foam into Scatterer's ocean gbuffer: on each camera rendering Scatterer's ocean this frame, a command buffer
    /// after Scatterer's geometry pass (AfterForwardOpaque: writes oceanGbufferDepth / oceanGbufferFoam) and before its
    /// shading pass (AfterImageEffectsOpaque: reads them) writes max(Scatterer's foam, ours) for every ocean pixel.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class OceanFoamPainter : MonoBehaviour
    {
        private static readonly int FoamId = Shader.PropertyToID("oceanGbufferFoam");

        private static Shader shader, testShader, mapShader;
        private static bool loadTried;

        private Material mat;
        private Type oceanCbType;
        private FieldInfo renderingEnabled;
        private MethodInfo orderedAdd, orderedRemove;     // Scatterer.OrderedAfterForwardOpaqueCommandBuffer
        private readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
        private readonly Dictionary<Camera, RenderTexture> foamCopies = new Dictionary<Camera, RenderTexture>();
        private readonly Dictionary<Camera, int> attached = new Dictionary<Camera, int>();   // camera -> hook mode
        private RenderTexture diagA, diagB;
        private float nextDiag;
        private bool diagPending;
        private int mode;
        private NativeArray<Vector4> lastA;
        private bool haveA;
        private int framesPainted;

        // the foam map: WakeFx's foam particles seen from above by an orthographic camera, on a layer the flight cameras
        // don't draw while painting is on
        private Camera mapCam;
        private RenderTexture map;
        private int hiddenLayer = -1, lastOceanFrame = -100;
        private Matrix4x4 mapMatrix = Matrix4x4.zero;
        // the near map: finer, where the camera looks
        private RenderTexture nearMap;
        private Matrix4x4 nearMatrix = Matrix4x4.zero;
        private bool painting;

        // hook modes: 0 BeforeImageEffectsOpaque; 1 AfterForwardOpaque through Scatterer's ordered list, after its ocean;
        // 2 AfterForwardOpaque added directly
        private static readonly string[] ModeNames = { "BeforeImageEffectsOpaque", "ordered AfterForwardOpaque", "AfterForwardOpaque" };

        private static WakeSettings Cfg => WakeSettings.Current;

        private void Start()
        {
            if (!loadTried)
            {
                loadTried = true;
                shader = WakeShaders.Get("WaterWakes/OceanFoam");
                testShader = WakeShaders.Get("WaterWakes/Test");
                mapShader = WakeShaders.Get("WaterWakes/FoamMap");
                Debug.Log($"[WaterWakes] ocean foam shader {(shader == null ? "NOT loaded from " + WakeShaders.BundlePath : shader.name)}");
            }
            Type T(string n) => AssemblyLoader.loadedAssemblies.Select(a => a.assembly.GetType(n)).FirstOrDefault(t => t != null);
            oceanCbType = T("Scatterer.OceanCommandBuffer");
            renderingEnabled = oceanCbType?.GetField("renderingEnabled", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Type ordered = T("Scatterer.OrderedAfterForwardOpaqueCommandBuffer");
            orderedAdd = ordered?.GetMethod("AddCommandBuffer", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Camera), typeof(CommandBuffer), typeof(int) }, null);
            orderedRemove = ordered?.GetMethod("RemoveCommandBuffer", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(Camera), typeof(CommandBuffer) }, null);
            // the ocean gbuffer (oceanGbufferDepth / oceanGbufferFoam) came with the deferred ocean of Scatterer 0.09xx, which
            // also brought OrderedAfterForwardOpaqueCommandBuffer; earlier versions shade the ocean in one forward pass
            if (shader == null || renderingEnabled == null || orderedAdd == null)
            {
                Debug.Log($"[WaterWakes] ocean foam painting off (Scatterer ocean {(oceanCbType == null ? "not found" : renderingEnabled == null || orderedAdd == null ? "without the deferred ocean (needs 0.09xx)" : "found")})");
                painterOff = true;
                enabled = Cfg.paintDebugToggle;   // (its debug screenshots still wanted)
                return;
            }
            mat = new Material(shader);
            if (Cfg.paintDebug)
                SelfTest();
            mode = Mathf.Clamp(Cfg.paintHook, 0, 2);
            Camera.onPreCull += PreCull;
            Camera.onPreRender += PreRender;
            Camera.onPostRender += PostRender;
            Debug.Log($"[WaterWakes] ocean foam painting on (ordered hook {(orderedAdd != null ? "found" : "missing")})");
        }

        private void OnDestroy()
        {
            Camera.onPreCull -= PreCull;
            Camera.onPreRender -= PreRender;
            Camera.onPostRender -= PostRender;
            foreach (Camera c in attached.Keys.ToList())
                Detach(c);
            foreach (CommandBuffer cb in buffers.Values)
                cb.Release();
            buffers.Clear();
            foreach (RenderTexture rt in foamCopies.Values)
                if (rt != null)
                    rt.Release();
            foamCopies.Clear();
            foreach (RenderTexture rt in normalCopies.Values)
                if (rt != null)
                    rt.Release();
            normalCopies.Clear();
            if (diagA != null) diagA.Release();
            if (diagB != null) diagB.Release();
            if (haveA) lastA.Dispose();
            if (mat != null) Destroy(mat);
            if (mapCam != null) Destroy(mapCam.gameObject);
            if (map != null) map.Release();
            if (nearMap != null) nearMap.Release();
            if (WakeFx.Instance != null)
                foreach (ParticleSystem ps in new[] { WakeFx.Instance.Foam, WakeFx.Instance.SplashFoam, WakeFx.Instance.UpwellFoam })
                    if (ps != null)
                        ps.gameObject.layer = 0;
        }

        // a layer none of the scene's cameras draws (KSP uses all 32; these are for colliders and drag rendering)
        private static int FindHiddenLayer()
        {
            int used = 0;
            foreach (Camera c in Camera.allCameras)
                used |= c.cullingMask;
            foreach (int l in new[] { 29, 26, 27, 28, 21, 22 })
                if ((used & (1 << l)) == 0)
                    return l;
            return -1;
        }

        private bool painterOff;

        private void UpdateMap()
        {
            if (painterOff)
                return;
            WakeFx fx = WakeFx.Instance;
            ParticleSystem foam = fx != null ? fx.Foam : null;
            CelestialBody body = FlightGlobals.currentMainBody;
            Camera main = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            // painting while a camera drew Scatterer's ocean in the last few frames
            bool on = Cfg.enabled && Cfg.paintFoam && foam != null && body != null && main != null && Time.frameCount - lastOceanFrame < 5;
            if (on && hiddenLayer < 0)
            {
                hiddenLayer = FindHiddenLayer();
                Debug.Log($"[WaterWakes] foam map layer {hiddenLayer} ({(hiddenLayer < 0 ? "none free: painting off" : LayerMask.LayerToName(hiddenLayer))})");
            }
            on &= hiddenLayer >= 0;
            if (on != painting)
            {
                painting = on;
                Debug.Log($"[WaterWakes] painting foam on the ocean: {on}");
            }
            // (the splash's foam and the hull's upwell too: draped over the waves, their own crisp look kept by FoamMap's _Crisp)
            foreach (ParticleSystem ps in new[] { foam, fx != null ? fx.SplashFoam : null, fx != null ? fx.UpwellFoam : null })
                if (ps != null)
                    ps.gameObject.layer = on ? hiddenLayer : 0;
            if (!on)
            {
                mapMatrix = Matrix4x4.zero;
                nearMatrix = Matrix4x4.zero;
                return;
            }

            int res = Mathf.Clamp(Cfg.paintMapResolution, 256, 8192);
            if (map == null || map.width != res)
            {
                if (map != null)
                    map.Release();
                map = new RenderTexture(res, res, 0, RenderTextureFormat.R8) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "WaterWakes foam map" };
                map.Create();
            }
            if (mapCam == null)
            {
                var go = new GameObject("WaterWakes foam map camera");
                mapCam = go.AddComponent<Camera>();
                mapCam.enabled = false;
                mapCam.orthographic = true;
                mapCam.clearFlags = CameraClearFlags.SolidColor;
                mapCam.backgroundColor = new Color(0, 0, 0, 0);
                mapCam.allowHDR = false;
                mapCam.allowMSAA = false;
                mapCam.useOcclusionCulling = false;
                mapCam.renderingPath = RenderingPath.Forward;
                mapCam.aspect = 1;
                mapCam.nearClipPlane = 1;
                mapCam.farClipPlane = 2000;
            }
            // centred on the sea below the main camera, looking straight down from 500 m
            float size = Mathf.Max(20, Cfg.paintMapSize);
            Vector3 camPos = main.transform.position;
            Vector3 up = ((Vector3)((Vector3d)camPos - body.position)).normalized;
            Vector3 centre = camPos - up * (float)body.GetAltitude((Vector3d)camPos);
            Vector3 fwd = Vector3.ProjectOnPlane(main.transform.forward, up);
            if (fwd.sqrMagnitude < 1e-4f)
                fwd = Vector3.ProjectOnPlane(main.transform.up, up);
            mapCam.transform.SetPositionAndRotation(centre + up * 500, Quaternion.LookRotation(-up, fwd.normalized));
            mapCam.orthographicSize = size / 2;
            mapCam.cullingMask = 1 << hiddenLayer;
            mapCam.targetTexture = map;
            // each patch's own foam amount, the largest kept (overlapping faint old foam stays faint); else as drawn
            if (mapShader != null && fx.CustomFoam)
                mapCam.RenderWithShader(mapShader, "");
            else
                mapCam.Render();

            // world -> map uv: across the camera's right and up, about its position
            Vector3 r = mapCam.transform.right / size, u = mapCam.transform.up / size, c = mapCam.transform.position;
            mapMatrix = Matrix4x4.identity;
            mapMatrix.SetRow(0, new Vector4(r.x, r.y, r.z, 0.5f - Vector3.Dot(c, r)));
            mapMatrix.SetRow(1, new Vector4(u.x, u.y, u.z, 0.5f - Vector3.Dot(c, u)));

            // the near map: the same foam, finer, centred where the camera looks at the water (kept near the point below
            // it), the far map taking over towards its edge
            float nsize = Cfg.paintNearMapSize;
            if (nsize < 10 || nsize >= size)
            {
                nearMatrix = Matrix4x4.zero;
                return;
            }
            int nres = Mathf.Clamp(Cfg.paintNearMapResolution, 256, 8192);
            if (nearMap == null || nearMap.width != nres)
            {
                if (nearMap != null)
                    nearMap.Release();
                nearMap = new RenderTexture(nres, nres, 0, RenderTextureFormat.R8) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = "WaterWakes near foam map" };
                nearMap.Create();
            }
            Vector3 look = centre, f3 = main.transform.forward;
            float down = -Vector3.Dot(f3, up);
            if (down > 0.05f)
            {
                Vector3 off = camPos + f3 * ((float)body.GetAltitude((Vector3d)camPos) / down) - centre;
                off -= up * Vector3.Dot(off, up);
                look = centre + Vector3.ClampMagnitude(off, 0.3f * size);
            }
            mapCam.transform.SetPositionAndRotation(look + up * 500, mapCam.transform.rotation);
            mapCam.orthographicSize = nsize / 2;
            mapCam.targetTexture = nearMap;
            if (mapShader != null && fx.CustomFoam)
                mapCam.RenderWithShader(mapShader, "");
            else
                mapCam.Render();
            Vector3 nr = mapCam.transform.right / nsize, nu = mapCam.transform.up / nsize, nc = mapCam.transform.position;
            nearMatrix = Matrix4x4.identity;
            nearMatrix.SetRow(0, new Vector4(nr.x, nr.y, nr.z, 0.5f - Vector3.Dot(nc, nr)));
            nearMatrix.SetRow(1, new Vector4(nu.x, nu.y, nu.z, 0.5f - Vector3.Dot(nc, nu)));
        }

        // each pass blitted into a small float texture and read back: does the shader run at all on this device?
        private void SelfTest()
        {
            Debug.Log($"[WaterWakes] self-test: {SystemInfo.graphicsDeviceType} {SystemInfo.graphicsDeviceVersion}, shader passes {mat.passCount}, " +
                      $"reversed Z {SystemInfo.usesReversedZBuffer}");
            var rt = new RenderTexture(8, 4, 0, RenderTextureFormat.ARGBFloat) { filterMode = FilterMode.Point };
            rt.Create();
            var tex = new Texture2D(8, 4, TextureFormat.RGBAFloat, false);
            RenderTexture prev = RenderTexture.active;
            mat.SetFloat("_WakeDebugHalf", 1);
            var tests = new List<(string, Material, int)>();
            if (testShader != null)
                tests.Add(("Test", new Material(testShader), 0));
            for (int pass = 0; pass < mat.passCount; pass++)
                tests.Add(("OceanFoam", mat, pass));
            foreach ((string name, Material m, int pass) in tests)
            {
                RenderTexture.active = rt;
                GL.Clear(false, true, new Color(-1, -1, -1, -1));
                Graphics.Blit(null, rt, m, pass);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, 8, 4), 0, 0);
                tex.Apply();
                Debug.Log($"[WaterWakes] self-test {name} pass {pass} (supported {m.shader.isSupported}): px(0,0) {tex.GetPixel(0, 0)}, px(7,3) {tex.GetPixel(7, 3)}");
            }
            RenderTexture.active = prev;
            rt.Release();
            Destroy(tex);
        }

        private bool toggleOff, shotTaken;
        private float toggleAt;
        private int shots, windows;
        private static readonly float[] ShotHeadings = { 0, 90, 180, 270 };

        private void LateUpdate()
        {
            // debug: painting on and off every 4 s of real time, a screenshot half way through each (KSP's Screenshots
            // folder); each off/on pair from one of four camera headings, locked to the craft, debugCamDistance out, debugCamPitch down
            if (Cfg.paintDebugToggle)
            {
                float now = Time.realtimeSinceStartup;
                FlightCamera fc = FlightCamera.fetch;
                int angle = (windows / 2) % ShotHeadings.Length;
                if (now > toggleAt + 4)
                {
                    toggleAt = now;
                    toggleOff = !toggleOff;
                    shotTaken = false;
                    windows++;
                    angle = (windows / 2) % ShotHeadings.Length;
                    if (fc != null)
                    {
                        if (fc.mode != FlightCamera.Modes.LOCKED)
                            fc.setModeImmediate(FlightCamera.Modes.LOCKED);
                        fc.SetDistanceImmediate(Cfg.debugCamDistance);
                    }
                }
                if (fc != null)
                {
                    fc.camHdg = ShotHeadings[angle] * Mathf.Deg2Rad;
                    fc.camPitch = Cfg.debugCamPitch * Mathf.Deg2Rad;
                }
                if (!shotTaken && now > toggleAt + 2f && shots < 60)
                {
                    shotTaken = true;
                    string dir = Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots");
                    Directory.CreateDirectory(dir);
                    string f = Path.Combine(dir, $"ww_{shots++:00}_h{ShotHeadings[angle]:000}_{(toggleOff ? "off" : "on")}.png");
                    ScreenCapture.CaptureScreenshot(f);
                    Debug.Log($"[WaterWakes] screenshot {f}");
                }
            }
            UpdateMap();
            if (Cfg.paintDebug && Time.realtimeSinceStartup > nextDiag)
            {
                nextDiag = Time.realtimeSinceStartup + 5;
                diagPending = true;
                if (hiddenLayer >= 0)
                {
                    var names = new List<string>();
                    foreach (Renderer r in FindObjectsOfType<Renderer>())
                        if (r.gameObject.layer == hiddenLayer)
                            names.Add($"{r.name} ({r.GetType().Name}, {r.bounds.size.magnitude:F1} m, {(r.bounds.center - (FlightCamera.fetch != null ? FlightCamera.fetch.transform.position : Vector3.zero)).magnitude:F0} m away)");
                    Debug.Log($"[WaterWakes] layer {hiddenLayer} renderers: {names.Count}: {string.Join("; ", names.Take(12))}");
                }
                if (Cfg.paintHook < 0)
                    mode = (mode + 1) % 3;
            }
            if (Cfg.paintHook >= 0)
                mode = Mathf.Clamp(Cfg.paintHook, 0, 2);
        }

        private bool HasOcean(Camera cam, bool mustRender)
        {
            if (cam == null || !Cfg.enabled || !(Cfg.paintFoam || Cfg.waves) || toggleOff)
                return false;
            Component oc = cam.GetComponent(oceanCbType);
            return oc != null && (!mustRender || (bool)renderingEnabled.GetValue(oc));
        }

        // mode 1 registers before culling: Scatterer's ordered list attaches its buffers in its OnPreRender
        private void PreCull(Camera cam)
        {
            // (only on a camera that drew the ocean in the last frames: otherwise our buffer would run alone, its targets missing)
            if (mode != 1 || orderedAdd == null || !HasOcean(cam, false) || Time.frameCount - lastOceanFrame > 2)
                return;
            orderedAdd.Invoke(null, new object[] { cam, Record(cam), 1 });
            attached[cam] = 1;
        }

        private void PreRender(Camera cam)
        {
            if (!HasOcean(cam, true))
                return;
            lastOceanFrame = Time.frameCount;
            SetParams(cam);
            Tint(cam);
            if (mode == 1)
                return;
            CommandBuffer cb = Record(cam);
            cam.AddCommandBuffer(mode == 0 ? CameraEvent.BeforeImageEffectsOpaque : CameraEvent.AfterForwardOpaque, cb);
            attached[cam] = mode;
            framesPainted++;
        }

        private void PostRender(Camera cam)
        {
            if (cam != null && attached.ContainsKey(cam))
                Detach(cam);
            if (cam != null && tinted.Remove(cam) && tintBuffers.TryGetValue(cam, out CommandBuffer tb))
                cam.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, tb);
        }

        // debug: the sea tinted by the wave sim's height, after Scatterer has shaded it
        private readonly Dictionary<Camera, CommandBuffer> tintBuffers = new Dictionary<Camera, CommandBuffer>();
        private readonly HashSet<Camera> tinted = new HashSet<Camera>();

        private void Tint(Camera cam)
        {
            if (!Cfg.simDebugTint || WaveSim.Instance == null || !WaveSim.Instance.Active || mat.passCount <= 4 || tinted.Contains(cam))
                return;
            if (!tintBuffers.TryGetValue(cam, out CommandBuffer tb))
            {
                tintBuffers[cam] = tb = new CommandBuffer { name = "WaterWakes sim tint" };
                tb.Blit((Texture)null, BuiltinRenderTextureType.CameraTarget, mat, 4);
            }
            cam.AddCommandBuffer(CameraEvent.AfterImageEffectsOpaque, tb);
            tinted.Add(cam);
        }

        private void Detach(Camera cam)
        {
            int m = attached[cam];
            attached.Remove(cam);
            if (cam == null || !buffers.TryGetValue(cam, out CommandBuffer cb))
                return;
            if (m == 1)
            {
                orderedRemove?.Invoke(null, new object[] { cam, cb });
                framesPainted++;
            }
            else
                cam.RemoveCommandBuffer(m == 0 ? CameraEvent.BeforeImageEffectsOpaque : CameraEvent.AfterForwardOpaque, cb);
        }

        private void SetParams(Camera cam)
        {
            CelestialBody body = FlightGlobals.currentMainBody;
            if (body == null)
                return;
            Vector3 camPos = cam.transform.position;
            Vector3 up = ((Vector3)((Vector3d)camPos - body.position)).normalized;
            double alt = body.GetAltitude((Vector3d)camPos);
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, true);
            mat.SetMatrix("_WakeInvProj", proj.inverse);
            mat.SetMatrix("_WakeCamToWorld", cam.cameraToWorldMatrix);
            mat.SetVector("_WakeSurf", camPos - up * (float)alt);
            mat.SetVector("_WakeUp", up);
            mat.SetFloat("_WakeRadius", (float)body.Radius);
            mat.SetFloat("_WakeClearDepth", SystemInfo.usesReversedZBuffer ? 0 : 1);
            mat.SetFloat("_WakeFlipY", Cfg.paintFlipY ? 1 : 0);
            mat.SetFloat("_WakeStrength", Cfg.paintStrength);
            mat.SetFloat("_WakeDebugHalf", Cfg.paintDebugMode);
            mat.SetMatrix("_WakeMapMatrix", mapMatrix);
            if (map != null)
                mat.SetTexture("_WakeFoamMap", map);
            bool nearOn = nearMap != null && nearMatrix != Matrix4x4.zero;
            mat.SetFloat("_WakeNearOn", nearOn ? 1 : 0);
            mat.SetMatrix("_WakeNearMatrix", nearMatrix);
            if (nearMap != null)
                mat.SetTexture("_WakeNearMap", nearMap);

            // the Kelvin wakes: the sources nearest the camera
            waveCount = 0;
            bool sim = WaveSim.Instance != null && WaveSim.Instance.Active;
            List<WakeFx.WaveSource> src = Cfg.waves && WakeFx.Instance != null ? WakeFx.Instance.WaveSources : null;
            // (the sim makes the wakes; only craft too fast for its square keep the analytic ones)
            if (src != null && sim)
                src = src.Where(q => 1 - q.simShare > 0.01f).ToList();
            if (src != null)
            {
                IEnumerable<WakeFx.WaveSource> near = src.Count <= KwMax ? src : src.OrderBy(s => (s.pos - camPos).sqrMagnitude).Take(KwMax);
                foreach (WakeFx.WaveSource s in near)
                {
                    kwPos[waveCount] = new Vector4(s.pos.x, s.pos.y, s.pos.z, s.speed);
                    kwDir[waveCount] = new Vector4(s.dir.x, s.dir.y, s.dir.z, s.height * Cfg.waveHeight * (sim ? 1 - s.simShare : 1));
                    kwPar[waveCount] = new Vector4(s.length, s.trail, s.transverse, s.beam);
                    int n = s.track != null ? Mathf.Min(s.trackCount, WakeFx.TrackMax) : 0;
                    if (n > 0)
                        Array.Copy(s.track, 0, kwTrack, waveCount * WakeFx.TrackMax, n);
                    kwInfo[waveCount] = new Vector4(n, s.front, 0, 0);
                    waveCount++;
                }
            }
            mat.SetVectorArray("_KwPos", kwPos);
            mat.SetVectorArray("_KwDir", kwDir);
            mat.SetVectorArray("_KwPar", kwPar);
            mat.SetVectorArray("_KwTrack", kwTrack);
            mat.SetVectorArray("_KwInfo", kwInfo);
            mat.SetFloat("_KwCount", waveCount);
            // (also globally: the ocean displacement's material, recorded into Scatterer's command buffer, reads them)
            Shader.SetGlobalVectorArray("_KwPos", kwPos);
            Shader.SetGlobalVectorArray("_KwDir", kwDir);
            Shader.SetGlobalVectorArray("_KwPar", kwPar);
            Shader.SetGlobalVectorArray("_KwTrack", kwTrack);
            Shader.SetGlobalVectorArray("_KwInfo", kwInfo);
            Shader.SetGlobalFloat("_KwCount", waveCount);
            Shader.SetGlobalFloat("_KwGravity", (float)(body.GeeASL * 9.80665));
            Shader.SetGlobalFloat("_KwMaxSlope", Cfg.waveMaxSlope);
            Shader.SetGlobalVector("_WakeUp", up);
            Shader.SetGlobalFloat("_KwDisplace", Cfg.waves ? Cfg.kelvinDisplace : 0);
            mat.SetFloat("_KwGravity", (float)(body.GeeASL * 9.80665));
            mat.SetFloat("_KwMaxSlope", Cfg.waveMaxSlope);

            // the entries' rings of ripples, the nearest the camera
            rippleCount = 0;
            List<WakeFx.RippleSource> rps = WakeFx.Instance != null && !sim ? WakeFx.Instance.RippleSources : null;
            if (rps != null)
                foreach (WakeFx.RippleSource rp in rps.Count <= RpMax ? rps : rps.OrderBy(q => (q.pos - camPos).sqrMagnitude).Take(RpMax))
                {
                    rpPos[rippleCount] = new Vector4(rp.pos.x, rp.pos.y, rp.pos.z, rp.radius);
                    rpPar[rippleCount] = new Vector4(rp.amp, rp.wavelength, rp.length, rp.foam);
                    rippleCount++;
                }
            mat.SetVectorArray("_RpPos", rpPos);
            mat.SetVectorArray("_RpPar", rpPar);
            mat.SetFloat("_RpCount", rippleCount);
        }

        private const int KwMax = 8;
        private const int RpMax = 8;
        private readonly Vector4[] rpPos = new Vector4[RpMax], rpPar = new Vector4[RpMax];
        private int rippleCount;
        private readonly Vector4[] kwPos = new Vector4[KwMax], kwDir = new Vector4[KwMax], kwPar = new Vector4[KwMax], kwInfo = new Vector4[KwMax];
        private readonly Vector4[] kwTrack = new Vector4[KwMax * WakeFx.TrackMax];
        private int waveCount;
        private static readonly int NormalsId = Shader.PropertyToID("oceanGbufferNormalsAndSigma");
        private readonly Dictionary<Camera, RenderTexture> normalCopies = new Dictionary<Camera, RenderTexture>();

        private static RenderTexture CameraSized(Dictionary<Camera, RenderTexture> d, Camera cam, int w, int h, RenderTextureFormat fmt, string name)
        {
            if (!d.TryGetValue(cam, out RenderTexture rt) || rt == null || rt.width != w || rt.height != h)
            {
                if (rt != null)
                    rt.Release();
                rt = new RenderTexture(w, h, 0, fmt) { filterMode = FilterMode.Point, name = name };
                rt.Create();
                d[cam] = rt;
            }
            return rt;
        }

        private CommandBuffer Record(Camera cam)
        {
            if (!buffers.TryGetValue(cam, out CommandBuffer cb))
                buffers[cam] = cb = new CommandBuffer { name = "WaterWakes ocean foam" };
            cb.Clear();
            // our copy of the foam buffer, the camera's size (as Scatterer's)
            int w = cam.targetTexture != null ? cam.targetTexture.width : cam.pixelWidth;
            int h = cam.targetTexture != null ? cam.targetTexture.height : cam.pixelHeight;
            if (Cfg.paintFoam)
            {
                RenderTexture copy = CameraSized(foamCopies, cam, w, h, RenderTextureFormat.R8, "WaterWakes ocean foam");
                cb.Blit((Texture)null, copy, mat, 0);
                cb.SetGlobalTexture(FoamId, copy);
            }
            // the Kelvin wakes into our copy of the normals (Scatterer's, as its foam's, a temporary of its own buffer)
            bool simOn = WaveSim.Instance != null && WaveSim.Instance.Active;
            if (Cfg.waves && (waveCount > 0 || rippleCount > 0 || simOn) && mat.passCount > 3)
            {
                RenderTexture nc = CameraSized(normalCopies, cam, w, h, RenderTextureFormat.ARGB2101010, "WaterWakes ocean normals");
                cb.Blit((Texture)null, nc, mat, 3);
                cb.SetGlobalTexture(NormalsId, nc);
            }
            if (diagPending)
            {
                diagPending = false;
                if (diagA == null)
                {
                    diagA = new RenderTexture(240, 135, 0, RenderTextureFormat.ARGBFloat) { filterMode = FilterMode.Point };
                    diagB = new RenderTexture(240, 135, 0, RenderTextureFormat.ARGBFloat) { filterMode = FilterMode.Point };
                    diagA.Create();
                    diagB.Create();
                }
                cb.Blit((Texture)null, diagA, mat, 1);
                cb.Blit((Texture)null, diagB, mat, 2);
                string tag = $"{cam.name}, hook {ModeNames[mode]}";
                cb.RequestAsyncReadback(diagA, r => OnDiagA(r));
                cb.RequestAsyncReadback(diagB, r => OnDiagB(r, tag));
                if (map != null)
                    cb.RequestAsyncReadback(map, r => OnMap(r));
            }
            return cb;
        }

        private void OnDiagA(AsyncGPUReadbackRequest r)
        {
            if (r.hasError)
                return;
            if (haveA) lastA.Dispose();
            lastA = new NativeArray<Vector4>(r.GetData<Vector4>(), Allocator.Persistent);
            haveA = true;
        }

        // the foam map's coverage
        private void OnMap(AsyncGPUReadbackRequest r)
        {
            if (r.hasError)
                return;
            NativeArray<byte> px = r.GetData<byte>();
            int lit = 0, max = 0;
            long sum = 0;
            for (int i = 0; i < px.Length; i++)
            {
                int v = px[i];
                if (v > 5) lit++;
                if (v > max) max = v;
                sum += v;
            }
            Debug.Log($"[WaterWakes] foam map {r.width}x{r.height} (foam particles {(WakeFx.Instance != null && WakeFx.Instance.Foam != null ? WakeFx.Instance.Foam.particleCount : -1)}, layer {(WakeFx.Instance?.Foam != null ? WakeFx.Instance.Foam.gameObject.layer : -1)}): {lit} px lit ({100.0 * lit / px.Length:F2}%), max {max}, mean {(double)sum / px.Length:F2}");
        }

        private void OnDiagB(AsyncGPUReadbackRequest r, string tag)
        {
            if (r.hasError || !haveA)
            {
                Debug.Log($"[WaterWakes] diag readback failed ({r.hasError}), {tag}");
                return;
            }
            NativeArray<Vector4> b = r.GetData<Vector4>();
            float clear = SystemInfo.usesReversedZBuffer ? 0 : 1;
            var depth = new List<float>();
            var copy = new List<float>();
            var normal = new List<float>();
            var foam = new List<float>();
            var cand = new List<float>[4];
            for (int k = 0; k < 4; k++)
                cand[k] = new List<float>();
            int nz0 = 0, nz1 = 0, nz2 = 0, nz3 = 0;
            for (int i = 0; i < b.Length; i++)
            {
                Vector4 v = b[i];
                depth.Add(v.x);
                copy.Add(v.y);
                normal.Add(v.z);
                foam.Add(v.w);
                if (v.x != 0) nz0++;
                if (v.y != 0) nz1++;
                if (v.z != 0) nz2++;
                if (v.w != 0) nz3++;
                if (Mathf.Abs(v.x - clear) > 1e-9f && i < lastA.Length)
                {
                    cand[0].Add(lastA[i].x);
                    cand[1].Add(lastA[i].y);
                    cand[2].Add(lastA[i].z);
                    cand[3].Add(lastA[i].w);
                }
            }
            string Stat(List<float> l)
            {
                if (l.Count == 0)
                    return "-";
                l.Sort();
                return $"{l[l.Count / 20]:G4}/{l[l.Count / 2]:G4}/{l[l.Count * 19 / 20]:G4}";
            }
            string[] names = { "device f0", "device f1", "eye f0", "eye f1" };
            Debug.Log($"[WaterWakes] diag {tag} (frames painted {framesPainted}): nonzero px of {b.Length}: oceanGbufferDepth {nz0}, ScattererDepthCopy {nz1}, " +
                      $"map at ocean px {nz2}, foam buffer {nz3}; p5/p50/p95 depth {Stat(depth)}, copy {Stat(copy)}, map at ocean px {Stat(normal)}, foam buffer {Stat(foam)}; " +
                      $"ocean heights ({cand[0].Count} px): " + string.Join("; ", Enumerable.Range(0, 4).Select(k => $"{names[k]} {Stat(cand[k])}")));
        }
    }
}
