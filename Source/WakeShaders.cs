using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WaterWakes
{
    /// <summary>The mod's shaders (GameData/WaterWakes/Shaders/waterwakes.shaders), loaded once for the game session.</summary>
    public static class WakeShaders
    {
        private static Dictionary<string, Shader> shaders;

        public static string BundlePath => Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "WaterWakes", "Shaders", "waterwakes.shaders");

        /// <summary>The named shader, if the bundle has it and this device runs it; else null.</summary>
        public static Shader Get(string name)
        {
            if (shaders == null)
            {
                shaders = new Dictionary<string, Shader>();
                AssetBundle b = File.Exists(BundlePath) ? AssetBundle.LoadFromFile(BundlePath) : null;
                bool loaded = b != null;
                if (loaded)
                {
                    foreach (Shader s in b.LoadAllAssets<Shader>())
                        shaders[s.name] = s;
                    b.Unload(false);   // (b then == null: Unity's)
                }
                Debug.Log($"[WaterWakes] shaders: {(!loaded ? "none (no " + BundlePath + ")" : string.Join(", ", shaders.Keys))}");
            }
            return shaders.TryGetValue(name, out Shader sh) && sh != null && sh.isSupported ? sh : null;
        }
    }
}
