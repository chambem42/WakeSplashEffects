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
        [Persistent] public float mistAlpha = 0.3f;      // at the water; less higher up
        [Persistent] public float mistFalloff = 0.8f;    // m: the mist's opacity falls by 1/e every this much height
        [Persistent] public float mistLife = 2.5f;       // s (mean)
        [Persistent] public float mistRise = 0.6f;       // m/s (mean)

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
