using System.IO;
using UnityEngine;

namespace WaterWakes
{
    /// <summary>
    /// Tunables. Defaults here, then any WATERWAKES cfg node (GameDatabase), then the player's saved settings
    /// (GameData/WaterWakes/PluginData/settings.cfg, written by the in-game window). One instance for the game session;
    /// the window edits it live.
    /// </summary>
    public class WakeSettings
    {
        // general
        [Persistent] public bool enabled = true;
        [Persistent] public float intensity = 1f;        // multiplies every emission rate
        [Persistent] public float brightness = 1f;
        [Persistent] public float minSpeed = 0.4f;       // m/s over the water: below it, no wake
        [Persistent] public float fullSpeed = 10f;       // m/s: foam at full strength from here
        [Persistent] public float loadExponent = 2f;     // strength x (draft / draft at rest)^this: fades as the hull rises out
        [Persistent] public float planing = 0.5f;        // strength at least this while planing (on the step, wet), ...
        [Persistent] public float planingArea = 0.4f;    // ... with (wet share of the waterplane at rest) x (V / fullSpeed)^2 this or more (~ its water drag)
        [Persistent] public float maxRange = 3000f;      // m from the camera

        // foam
        [Persistent] public float foamAlpha = 0.7f;
        [Persistent] public float foamLife = 12f;        // s
        [Persistent] public float foamLift = 0.06f;      // m above the still water level (keeps the quads out of it)
        [Persistent] public float foamSpacing = 0.12f;   // m travelled between side-foam patches, per side
        [Persistent] public float foamSpread = 0.3f;     // side foam's outward speed, fraction of the hull's speed
        [Persistent] public float sternDensity = 6f;     // stern foam patches per m travelled per m of beam

        // spray
        [Persistent] public float sprayMinSpeed = 2.5f;  // m/s
        [Persistent] public float sprayRate = 400f;      // droplets per s per side per m of beam, 10 m/s over sprayMinSpeed
        [Persistent] public float sheetRate = 260f;      // spray-sheet blobs, the same way
        [Persistent] public float sheetAlpha = 0.35f;
        [Persistent] public float sprayHeight = 1f;      // multiplies the spray's upward throw

        // mist
        [Persistent] public float mistMinSpeed = 5f;     // m/s
        [Persistent] public float mistRate = 110f;       // puffs per s per side per m of beam, 10 m/s over mistMinSpeed
        [Persistent] public float hazeAlpha = 0.15f;     // a hard hit's glowing spray haze over its sheet (additive; emitted by the splash edge's length)
        [Persistent] public float mistAlpha = 0.3f;      // at the water; less higher up
        [Persistent] public float mistFalloff = 0.8f;    // m: the mist's opacity falls by 1/e every this much height
        [Persistent] public float mistLife = 2.5f;       // s (mean)
        [Persistent] public float mistRise = 0.6f;       // m/s (mean)

        // splashes: a part slamming into the water across its length (a belly flop, a touchdown), and a hull breaching
        [Persistent] public float splashMinSpeed = 1f;   // m/s into the water, across the part's long axis
        [Persistent] public float splashRate = 80f;      // droplets per s per m of waterline per m/s over splashMinSpeed
        [Persistent] public float splashHeight = 1f;     // multiplies the splash's upward throw
        [Persistent] public float breachSize = 1f;       // multiplies the plume of a hull breaking out from under water
        [Persistent] public float splashSheetSpeed = 0.45f;   // the crown's launch speed, mean fraction of the entry speed (water-entry studies: ~0.25-1)
        [Persistent] public float splashMaxSpeed = 80f;  // m/s: entry speeds above count as this
        [Persistent] public float jetHeight = 1f;        // multiplies the Worthington jet's height (the column thrown up where the cavity closes)
        [Persistent] public float skirtAmount = 1f;      // the spray skirt of a blunt impact (a flat end, a heat shield): how much
        [Persistent] public float skirtSpeed = 1f;       // ... and how fast (x twice the waterline's spreading speed)
        [Persistent] public float ringWaves = 1f;        // an entry's ring of ripples on the ocean (whitecapped while fast): 0 none
        [Persistent] public float ringWhitecapSpeed = 2.5f; // m/s: the ring's crest whitecaps from this speed (fully 3 m/s faster)
        [Persistent] public float ringLife = 3f;          // the ripples last this many times as long as the breaking crest
        [Persistent] public float ringWavelength = 1f;    // x the ripples' wavelength (0.6 D + 0.15 x the ring's radius)
        [Persistent] public bool hideStockSplash = true; // stock's splash column (FXMonger) silenced: its sound kept, its particles off
        [Persistent] public bool splashSounds = true;    // WaterWakes' splash sounds (WakeSounds): stock's boom for slams KSP doesn't hear, a breach's pour, the spray's hiss
        [Persistent] public float splashVolume = 1f;    // x KSP's ship volume
        [Persistent] public float hullSlamSpeed = 2.5f;  // m/s: a hull going in this fast by its own motion (its waterline's fastest point) slams, however deep
        [Persistent] public float submergedFade = 0.4f;  // m: a part wholly under water makes less wake the deeper its top, none from here

        // foam painted on Scatterer's waves (its ocean gbuffer)
        [Persistent] public bool paintFoam = true;
        [Persistent] public bool crispSpray = true;       // splash sheets eroded (sharp edged, sun lit) rather than faded; the splash's foam crisp streaks
        [Persistent] public bool fixOceanAmbient = true;  // Scatterer's ocean lit by its own sky, not the flight reflection probe (near black from above)
        [Persistent] public float paintStrength = 1f;
        [Persistent] public bool paintFlipY = true;       // oceanGbufferDepth is device depth rendered with Y flipped (D3D11)
        [Persistent] public float paintMapSize = 700f;    // m: the top-down foam map around the camera
        [Persistent] public int paintMapResolution = 8192;
        [Persistent] public float paintNearMapSize = 160f;   // m: a finer map where the camera looks (0: off)
        [Persistent] public int paintNearMapResolution = 4096;
        [Persistent] public int paintHook = 1;         // 0 BeforeImageEffectsOpaque, 1 Scatterer's ordered AfterForwardOpaque, 2 AfterForwardOpaque; -1 cycle (debug)
        [Persistent] public bool paintDebug = false;   // log depth diagnostics every 5 s
        [Persistent] public bool paintDebugToggle = false;
        [Persistent] public float paintDebugMode = 0;  // 1: full foam on the left half of the screen; -1: foam wherever the ocean depth reads; 2: ours only; 3: Scatterer's only; 4 (forward ocean): the foam's sun term over the left half; 5 (deferred): a coverage ramp, 0 to 1 up the left half
        [Persistent] public float debugCamDistance = 20f;   // the debug toggle's camera: m from the craft
        [Persistent] public float debugCamPitch = 20f;      // deg down
        // Scatterer's forward ocean (0.0878, the CKAN release): our foam lit by the scene's sun and ambient, the wake
        // waves as the light their slopes change (its own shader can't take them)
        [Persistent] public float fwdFoamBright = 1f;      // x the foam's brightness
        [Persistent] public float foamShade = 2.5f;        // x the waves' own slopes in our foam's lighting, either ocean (0: lit as if flat;
                                                           // a light breeze's ~0.05 slopes alone change diffuse light by a few per cent)
        [Persistent] public float fwdFoamSheen = 0.3f;     // the water's reflection showing on the (wet) foam
        [Persistent] public float foamCover = 0.5f;        // deferred ocean (0.09+): our densest foam's coverage in its foam buffer
                                                           // (1: as painted; its tone map turns ~0.5 already near white)
        [Persistent] public float foamContrast = 1f;      // deferred ocean: our foam as white as it is dense (its tone map undone: soft edges,
                                                          // texture inside dense foam); 0: eased as before (flat, hard edged)
        [Persistent] public float foamLace = 1f;          // our surface foam laid out as tufts (FoamLace's warped noise: holes opening as
                                                          // it thins), either ocean: 0 as painted, 1 full
        [Persistent] public float foamLaceSize = 1f;      // x the size of its texture's tiles (6 m and 1.7 m)
        [Persistent] public float foamBubbles = 1f;       // the bubbles under the foam: their turquoise glow through the water (0: none)
        [Persistent] public float fwdWaveShade = 1f;       // x the waves' change to the sky's reflection
        [Persistent] public float fwdGlint = 1f;           // x the sun glints off them

        // Kelvin wake waves drawn into Scatterer's ocean normals (its deferred ocean, 0.09+)
        [Persistent] public bool waves = true;
        // the wave simulation (WaveSim): one height field for the wakes and rings round where the camera looks
        [Persistent] public bool waveSim = true;
        [Persistent] public float simSize = 160f;         // m across
        [Persistent] public int simResolution = 512;      // texels across
        [Persistent] public float simDamping = 0.05f;     // 1/s
        [Persistent] public float simStrength = 1f;       // x the hulls' displacement driving it
        [Persistent] public float simFoamSlope = 0.5f;    // a crest breaks (whitecaps) past this slope
        [Persistent] public float simFoamLife = 2f;       // s: its foam fading
        [Persistent] public float simKelvinAbove = 10f;   // m/s: craft faster than this still get the analytic Kelvin waves (longer than the square)
        [Persistent] public float simShortDamping = 0.03f; // m^2/s: extra damping of short waves (x k^2): 1 m ripples gone in seconds
        [Persistent] public float simDisplace = 1f;      // x the sim's height put into Scatterer's ocean geometry (needs Harmony; 0: normals only)
        [Persistent] public float simDebugLift = 0f;     // (diagnostic) m the displaced square is lifted by as well
        [Persistent] public float simDebugTilt = 0f;     // (diagnostic) m amplitude of a smooth 20 m swell added to it
        [Persistent] public bool simDebugTint = false;    // the sea tinted by the sim's height (crests red, troughs blue)
        [Persistent] public bool simDebugInset = false;   // the sim's square drawn in a corner
        [Persistent] public float waveHeight = 0.23f;      // multiplies the waves' height (~ sqrt(beam x draft at rest) / 2 at the hull); 0.23, 0.025, 3000: fitted to deep-water ferry wakes (Nece et al. 1986)
        [Persistent] public float waveMaxSlope = 0.025f; // each wave system's steepest slope (measured ship waves ~0.02-0.03)
        [Persistent] public float kelvinDisplace = 1f;   // x the Kelvin wakes' height put into Scatterer's ocean geometry (needs Harmony; 0: normals only)
        [Persistent] public float waveTrail = 3000f;     // m: the longest wake behind a craft running straight (300 m abeam needs ~850 m)

        // window
        [Persistent] public bool hideWithUI = true;
        [Persistent] public bool openOnStart = false;
        [Persistent] public float windowX = 80, windowY = 120;
        [Persistent] public bool debugLog = false;

        private static WakeSettings current;

        public static WakeSettings Current => current ?? (current = Load());

        public static string SavePath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "WaterWakes", "PluginData", "settings.cfg");

        /// <summary>Defaults and the cfg nodes only (the window's Defaults button).</summary>
        public static WakeSettings Configured()
        {
            var s = new WakeSettings();
            foreach (ConfigNode n in GameDatabase.Instance.GetConfigNodes("WATERWAKES"))
                ConfigNode.LoadObjectFromConfig(s, n);
            return s;
        }

        public static WakeSettings Load()
        {
            WakeSettings s = Configured();
            if (File.Exists(SavePath))
            {
                ConfigNode root = ConfigNode.Load(SavePath);
                ConfigNode n = root?.GetNode("WATERWAKES");
                if (n != null)
                    ConfigNode.LoadObjectFromConfig(s, n);
            }
            return s;
        }

        public static void Replace(WakeSettings s) => current = s;

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
            ConfigNode n = ConfigNode.CreateConfigFromObject(this);
            n.name = "WATERWAKES";
            var root = new ConfigNode();
            root.AddNode(n);
            root.Save(SavePath);
            Debug.Log($"[WaterWakes] settings saved to {SavePath}");
        }
    }
}
