using KSP.UI.Screens;
using UnityEngine;

namespace WaterWakes
{
    /// <summary>The toolbar button and the settings window (flight scene). Changes apply live; Save keeps them.</summary>
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class SettingsWindow : MonoBehaviour
    {
        private ApplicationLauncherButton button;
        private Texture2D icon;
        private bool open, uiHidden;
        private Rect rect;
        private Vector2 scroll;
        private readonly int id = "WaterWakes.Settings".GetHashCode();
        private string status = "";

        private static WakeSettings S => WakeSettings.Current;

        private void Start()
        {
            rect = new Rect(S.windowX, S.windowY, 380, 560);
            open = S.openOnStart;
            icon = MakeIcon();
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onHideUI.Add(OnHideUI);
            GameEvents.onShowUI.Add(OnShowUI);
            if (ApplicationLauncher.Ready)
                AddButton();
        }

        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onHideUI.Remove(OnHideUI);
            GameEvents.onShowUI.Remove(OnShowUI);
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
        }

        private void OnHideUI() => uiHidden = true;
        private void OnShowUI() => uiHidden = false;

        private void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null)
                return;
            button = ApplicationLauncher.Instance.AddModApplication(() => open = true, () => open = false, null, null, null, null,
                                                                    ApplicationLauncher.AppScenes.FLIGHT, icon);
            if (open)
                button.SetTrue(false);
            Debug.Log("[WaterWakes] toolbar button added");
        }

        private void OnGUI()
        {
            if (!open || (uiHidden && S.hideWithUI))
                return;
            GUI.skin = HighLogic.Skin;
            rect = GUILayout.Window(id, rect, Draw, "Water Wakes");
            S.windowX = rect.x;
            S.windowY = rect.y;
        }

        private void Draw(int wid)
        {
            WakeSettings s = S;
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(470));

            Header("General");
            s.enabled = GUILayout.Toggle(s.enabled, " Effects on");
            Slider("Intensity", ref s.intensity, 0, 3, "x");
            Slider("Brightness", ref s.brightness, 0, 2, "x");
            Slider("Fade as hull lifts", ref s.loadExponent, 0, 4, "", "0: no fade; higher: weaker on the step");
            Slider("Planing strength", ref s.planing, 0, 1, "", "the least strength while the hull planes on the water");
            Slider("Planing drag ref", ref s.planingArea, 0.05f, 1, "x", "wet share of the waterplane at rest x (V / full foam speed)^2 for full planing strength");
            Slider("Min speed", ref s.minSpeed, 0, 5, "m/s");
            Slider("Full foam at", ref s.fullSpeed, 1, 30, "m/s");
            Slider("Fade under water", ref s.submergedFade, 0.01f, 3, "m", "a part wholly under water: no wake once its top is this deep");

            Header("Foam");
            Slider("Opacity", ref s.foamAlpha, 0, 1);
            Slider("Lifetime", ref s.foamLife, 1, 40, "s");
            Slider("Side spacing", ref s.foamSpacing, 0.03f, 0.6f, "m");
            Slider("Side spread", ref s.foamSpread, 0, 0.8f, "x V");
            Slider("Stern density", ref s.sternDensity, 0, 20);
            Slider("Height on water", ref s.foamLift, 0, 0.4f, "m");

            Header("Spray");
            Slider("Min speed", ref s.sprayMinSpeed, 0, 15, "m/s");
            Slider("Droplet rate", ref s.sprayRate, 0, 1500);
            Slider("Sheet rate", ref s.sheetRate, 0, 1000);
            Slider("Sheet opacity", ref s.sheetAlpha, 0, 1);
            Slider("Throw height", ref s.sprayHeight, 0, 3, "x");

            Header("Mist");
            Slider("Min speed", ref s.mistMinSpeed, 0, 20, "m/s");
            Slider("Rate", ref s.mistRate, 0, 400);
            Slider("Opacity at water", ref s.mistAlpha, 0, 1);
            Slider("Opacity falloff", ref s.mistFalloff, 0.1f, 4, "m");
            Slider("Lifetime", ref s.mistLife, 0.5f, 8, "s");
            Slider("Rise speed", ref s.mistRise, 0, 3, "m/s");

            Header("Splashes");
            Slider("Min impact speed", ref s.splashMinSpeed, 0.2f, 5, "m/s", "how fast a part must hit the water, across its length");
            Slider("Rate", ref s.splashRate, 0, 1000);
            Slider("Throw height", ref s.splashHeight, 0, 3, "x");
            Slider("Breach plume", ref s.breachSize, 0, 3, "x", "a hull breaking out from under the water");
            Slider("Crown speed", ref s.splashSheetSpeed, 0, 1.5f, "x", "the crown sheet's launch speed, a fraction of the speed into the water");
            Slider("Jet height", ref s.jetHeight, 0, 3, "x", "the column of water thrown up where the cavity behind a deep entry closes");
            Slider("Spray skirt", ref s.skirtAmount, 0, 3, "x", "the flat sheet a blunt bottom (a heat shield) throws out as it hits");
            Slider("Skirt speed", ref s.skirtSpeed, 0, 3, "x");
            Slider("Surface rings", ref s.ringWaves, 0, 3, "x", "the ring of ripples an entry sends out (whitecapped while it runs fast); 0: none");
            Slider("Ring whitecap speed", ref s.ringWhitecapSpeed, 0, 10, "m/s", "the crest whitecaps from this speed, fully 3 m/s faster");
            Slider("Ripple lifetime", ref s.ringLife, 1, 8, "x", "the ripples last this many times as long as the breaking crest");
            Slider("Ripple wavelength", ref s.ringWavelength, 0.2f, 4, "x");
            s.hideStockSplash = GUILayout.Toggle(s.hideStockSplash, " Hide stock's splash column (keeps its sound)");

            Header("Foam on the waves (Scatterer 0.09+)");
            s.paintFoam = GUILayout.Toggle(s.paintFoam, " Paint the foam into Scatterer's ocean");
            s.crispSpray = GUILayout.Toggle(s.crispSpray, " Crisp splashes (sharp-edged, sun-lit sheets and foam)");
            s.fixOceanAmbient = GUILayout.Toggle(s.fixOceanAmbient, " Light the water by Scatterer's sky (fixes a near-black sea seen from above)");
            Slider("Strength", ref s.paintStrength, 0, 2, "x");
            Slider("Area around camera", ref s.paintMapSize, 50, 1000, "m");
            s.waves = GUILayout.Toggle(s.waves, " Wake waves (Kelvin pattern) on the ocean");
            s.waveSim = GUILayout.Toggle(s.waveSim, " Simulate the waves (wakes and rings in one height field; off: the analytic patterns)");
            Slider("Sim area", ref s.simSize, 40, 400, "m");
            Slider("Sim push", ref s.simStrength, 0, 3, "x", "how hard the hulls' displacement drives the waves");
            Slider("Sim damping", ref s.simDamping, 0, 0.5f, "/s");
            Slider("Whitecap slope", ref s.simFoamSlope, 0.05f, 1, "", "a simulated crest breaks past this slope");
            Slider("Whitecap life", ref s.simFoamLife, 0.2f, 10, "s");
            Slider("Analytic wake above", ref s.simKelvinAbove, 0, 40, "m/s", "craft faster than this keep the analytic Kelvin waves (longer than the sim's area)");
            Slider("Short-wave damping", ref s.simShortDamping, 0, 0.3f, "m2/s", "damps short ripples much more than long waves (x wavenumber squared)");
            Slider("Displace the ocean", ref s.simDisplace, 0, 3, "x", "the sim's waves moving Scatterer's ocean surface itself (needs Harmony); 0: drawn as shading only");
            s.simDebugTint = GUILayout.Toggle(s.simDebugTint, " Debug: tint the sea by the sim's height");
            s.simDebugInset = GUILayout.Toggle(s.simDebugInset, " Debug: show the sim's area in a corner");
            Slider("Wave height", ref s.waveHeight, 0, 4, "x");
            Slider("Steepest slope", ref s.waveMaxSlope, 0.01f, 0.8f);
            Slider("Wakes displace the ocean", ref s.kelvinDisplace, 0, 3, "x", "the wakes' waves moving Scatterer's ocean surface itself (needs Harmony); 0: drawn as shading only");
            Slider("Wake length", ref s.waveTrail, 50, 5000, "m");

            Header("Window");
            s.hideWithUI = GUILayout.Toggle(s.hideWithUI, " Hide with the UI (F2)");
            s.debugLog = GUILayout.Toggle(s.debugLog, " Log hull detection (KSP.log, every 1 s)");
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save"))
            {
                s.Save();
                status = "Saved";
            }
            if (GUILayout.Button("Revert to saved"))
            {
                WakeSettings.Replace(WakeSettings.Load());
                status = "Reverted";
            }
            if (GUILayout.Button("Defaults"))
            {
                WakeSettings d = WakeSettings.Configured();
                d.windowX = s.windowX;
                d.windowY = s.windowY;
                WakeSettings.Replace(d);
                status = "Defaults (not saved)";
            }
            if (GUILayout.Button("Close"))
            {
                open = false;
                if (button != null)
                    button.SetFalse(false);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(status);
            GUI.DragWindow();
        }

        private static GUIStyle headerStyle;

        private static void Header(string text)
        {
            if (headerStyle == null)
                headerStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            GUILayout.Space(6);
            GUILayout.Label(text, headerStyle);
        }

        private static void Slider(string label, ref float v, float min, float max, string unit = "", string tip = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, tip), GUILayout.Width(130));
            v = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(140));
            GUILayout.Label($"{Format(v, max)} {unit}", GUILayout.Width(70));
            GUILayout.EndHorizontal();
        }

        private static string Format(float v, float max) => max >= 100 ? v.ToString("F0") : max >= 10 ? v.ToString("F1") : v.ToString("F2");

        // a hull's bow with its two wake arms and a little spray, white on clear
        private static Texture2D MakeIcon()
        {
            const int N = 38;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false);
            var px = new Color32[N * N];
            float Seg(Vector2 p, Vector2 a, Vector2 b)
            {
                Vector2 ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                return (p - (a + ab * t)).magnitude;
            }
            Vector2 bow = new Vector2(19, 30);
            for (int j = 0; j < N; j++)
                for (int i = 0; i < N; i++)
                {
                    var p = new Vector2(i + 0.5f, j + 0.5f);
                    // hull: a narrow pointed shape from the bow down the middle
                    float hull = Mathf.Max(Seg(p, bow, new Vector2(19, 16)) - 2.2f, 0);
                    // wake arms spreading back from the bow, and a wavy third line
                    float arms = Mathf.Min(Seg(p, bow + new Vector2(-2, -3), new Vector2(4, 3)), Seg(p, bow + new Vector2(2, -3), new Vector2(34, 3)));
                    float d = Mathf.Min(hull, Mathf.Max(arms - 1.1f, 0));
                    float a = Mathf.Clamp01(1.2f - d);
                    // spray dots off the bow
                    foreach (Vector2 c in new[] { new Vector2(12, 31), new Vector2(26, 31), new Vector2(9, 27), new Vector2(29, 27) })
                        a = Mathf.Max(a, Mathf.Clamp01(1.8f - (p - c).magnitude));
                    px[j * N + i] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
