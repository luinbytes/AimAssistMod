using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace AimAssist
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static Plugin Instance { get; private set; } = null!;
        internal static ManualLogSource Log { get; private set; } = null!;

        // ── Preset ─────────────────────────────────────────────────────────────
        /// <summary>
        /// "assist" | "rage" — only rewrites individual settings when you first switch presets.
        /// After that, every individual setting can be tweaked freely via BepInEx .cfg or ModConfig.
        /// </summary>
        public static ConfigEntry<string> Preset = null!;

        // ── Golf: Yaw Assist ──────────────────────────────────────────────────
        public static ConfigEntry<bool>    GolfEnabled     = null!;
        /// <summary>"hold" | "toggle" | "auto"</summary>
        public static ConfigEntry<string>  GolfYawMode     = null!;
        public static ConfigEntry<string>  GolfYawKey      = null!;
        /// <summary>deg/s — 0 = instant</summary>
        public static ConfigEntry<float>   GolfYawSpeed    = null!;
        /// <summary>Only help when aim is already within this many degrees of hole.</summary>
        public static ConfigEntry<float>   GolfAssistAngle = null!;

        // ── Golf: Pitch Solve ─────────────────────────────────────────────────
        /// <summary>
        /// Fully independent from yaw assist. Can be combined (same key as yaw) or standalone.
        /// </summary>
        public static ConfigEntry<bool>    PitchSolveEnabled = null!;
        /// <summary>"hold" | "toggle" | "auto" | "with_yaw" (follows GolfYawMode activation)</summary>
        public static ConfigEntry<string>  PitchSolveMode    = null!;
        public static ConfigEntry<string>  PitchSolveKey     = null!;
        /// <summary>deg/s — 0 = instant</summary>
        public static ConfigEntry<float>   PitchSolveSpeed   = null!;
        /// <summary>Prefer high-arc (lobbed) shot when solving.</summary>
        public static ConfigEntry<bool>    PitchHighArc      = null!;

        // ── Golf: Auto-Fire ────────────────────────────────────────────────────
        /// <summary>
        /// "off"  – auto-fire disabled.<br/>
        /// "key"  – press AutoFireKey to release swing when aim is within threshold.<br/>
        /// "auto" – automatically releases the swing the moment aim enters threshold.
        /// </summary>
        public static ConfigEntry<string>  AutoFireMode      = null!;
        public static ConfigEntry<string>  AutoFireKey       = null!;
        /// <summary>
        /// Max angular error (degrees) between current aim and perfect aim before
        /// the shot is considered ready and AutoFire triggers.
        /// </summary>
        public static ConfigEntry<float>   AutoFireThreshold = null!;

        // ── Weapon / Item Aim Assist ──────────────────────────────────────────
        public static ConfigEntry<bool>   WeaponEnabled   = null!;
        public static ConfigEntry<float>  WeaponConeAngle = null!;
        public static ConfigEntry<float>  WeaponSnapSpeed = null!;

        // ── Ball Trajectory Prediction ────────────────────────────────────────
        public static ConfigEntry<float> BallSpeedFallback = null!;

        // ── HUD ───────────────────────────────────────────────────────────────
        public static ConfigEntry<bool>    HUDEnabled   = null!;
        public static ConfigEntry<string>  HUDToggleKey = null!;
        /// <summary>0=Bottom-Right  1=Bottom-Left  2=Top-Right  3=Top-Left</summary>
        public static ConfigEntry<int>     HUDCorner    = null!;

        private static readonly string[] KeyOptions = {
            "LeftAlt", "RightAlt", "LeftCtrl", "RightCtrl", "LeftShift", "RightShift",
            "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12",
            "Tab","Space","Escape","Enter","Backspace",
            "Q","W","E","R","T","Y","U","I","O","P",
            "A","S","D","F","G","H","J","K","L",
            "Z","X","C","V","B","N","M",
            "Mouse0","Mouse1","Mouse2","Mouse3","Mouse4",
        };

        private string SentinelPath =>
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Config.ConfigFilePath)!,
                "AimAssist.preset");

        void Awake()
        {
            Instance = this;
            Log      = base.Logger;

            BindConfig();
            ApplyPresetIfNew();

            // Re-apply preset whenever the user switches it via ModConfig (or .cfg edit)
            Preset.SettingChanged += (_, _) => ApplyPresetIfNew();

            var go = new GameObject("AimAssistManager");
            DontDestroyOnLoad(go);
            go.AddComponent<AimAssistBehavior>();

            Log.LogInfo(
                $"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loaded!  " +
                $"Preset={Preset.Value}  " +
                $"Yaw={GolfEnabled.Value}/{GolfYawMode.Value}  " +
                $"Pitch={PitchSolveEnabled.Value}/{PitchSolveMode.Value}  " +
                $"AutoFire={AutoFireMode.Value}");
        }

        private void BindConfig()
        {
            // ── Preset ────────────────────────────────────────────────────────
            Preset = Config.Bind("1 - General", "Preset", "assist",
                new ConfigDescription(
                    "Quick-start preset. 'assist' = gentle helper, hold a key to activate. " +
                    "'rage' = always-on, instant snap, pitch solving included. " +
                    "Switching presets resets all settings below to the preset defaults — your own tweaks are kept after that.",
                    new AcceptableValueList<string>("assist", "rage")));

            // ── Yaw ───────────────────────────────────────────────────────────
            GolfEnabled = Config.Bind("2 - Aim Assist (Horizontal)", "Enabled", true,
                "Rotate your aim left/right toward the hole while you're lining up a swing.");
            GolfYawMode = Config.Bind("2 - Aim Assist (Horizontal)", "Activation Mode", "hold",
                new ConfigDescription(
                    "How the horizontal aim assist turns on. " +
                    "'hold' = active while you hold the key. " +
                    "'toggle' = press the key to switch on/off. " +
                    "'auto' = always active whenever you're aiming a swing, no key needed.",
                    new AcceptableValueList<string>("hold", "toggle", "auto")));
            GolfYawKey = Config.Bind("2 - Aim Assist (Horizontal)", "Activation Key", "LeftAlt",
                new ConfigDescription(
                    "Key or mouse button to hold/press for 'hold' and 'toggle' modes. Not used in 'auto' mode.",
                    new AcceptableValueList<string>(KeyOptions)));
            GolfYawSpeed = Config.Bind("2 - Aim Assist (Horizontal)", "Rotation Speed", 200f,
                new ConfigDescription(
                    "How fast your aim rotates toward the hole, in degrees per second. " +
                    "0 = snap instantly. 200 = smooth sweep. 720 = very fast.",
                    new AcceptableValueRange<float>(0f, 720f)));
            GolfAssistAngle = Config.Bind("2 - Aim Assist (Horizontal)", "Assist Angle", 180f,
                new ConfigDescription(
                    "Only pull aim toward the hole if you're already pointing within this many degrees of it. " +
                    "180 = always assist regardless of where you're facing. " +
                    "45 = only nudge when you're roughly facing the hole.",
                    new AcceptableValueRange<float>(1f, 180f)));

            // ── Pitch ─────────────────────────────────────────────────────────
            PitchSolveEnabled = Config.Bind("3 - Pitch Solving (Vertical Angle)", "Enabled", false,
                "Automatically calculate and set the vertical launch angle so the ball lands at the hole.");
            PitchSolveMode = Config.Bind("3 - Pitch Solving (Vertical Angle)", "Activation Mode", "with_yaw",
                new ConfigDescription(
                    "How pitch solving turns on. " +
                    "'hold' = active while you hold the pitch key. " +
                    "'toggle' = press the pitch key to switch on/off. " +
                    "'auto' = always active whenever you're aiming a swing. " +
                    "'with_yaw' = follows whatever the horizontal aim assist is doing (same key).",
                    new AcceptableValueList<string>("hold", "toggle", "auto", "with_yaw")));
            PitchSolveKey = Config.Bind("3 - Pitch Solving (Vertical Angle)", "Activation Key", "LeftAlt",
                new ConfigDescription(
                    "Key or mouse button for 'hold' and 'toggle' modes. Not used when mode is 'auto' or 'with_yaw'.",
                    new AcceptableValueList<string>(KeyOptions)));
            PitchSolveSpeed = Config.Bind("3 - Pitch Solving (Vertical Angle)", "Adjustment Speed", 0f,
                new ConfigDescription(
                    "How fast the vertical angle adjusts, in degrees per second. " +
                    "0 = snap instantly to the correct angle.",
                    new AcceptableValueRange<float>(0f, 720f)));
            PitchHighArc = Config.Bind("3 - Pitch Solving (Vertical Angle)", "Prefer High Arc", false,
                "When enabled, chooses the steep lobbed shot instead of the flat direct shot. " +
                "Useful for clearing obstacles between you and the hole.");

            // ── AutoFire ──────────────────────────────────────────────────────
            AutoFireMode = Config.Bind("4 - Auto Fire", "Mode", "off",
                new ConfigDescription(
                    "Automatically release the swing when your aim is lined up. " +
                    "'off' = disabled. " +
                    "'key' = press the fire key to shoot once your aim is within the threshold. " +
                    "'auto' = fires automatically the moment your aim is close enough.",
                    new AcceptableValueList<string>("off", "key", "auto")));
            AutoFireKey = Config.Bind("4 - Auto Fire", "Fire Key", "Mouse1",
                new ConfigDescription(
                    "Key or mouse button that confirms and fires the shot in 'key' mode. " +
                    "Right-click (Mouse1) feels natural as a 'confirm shot' button.",
                    new AcceptableValueList<string>(KeyOptions)));
            AutoFireThreshold = Config.Bind("4 - Auto Fire", "Alignment Threshold (degrees)", 3f,
                new ConfigDescription(
                    "How precisely you need to be aimed at the hole before auto fire triggers. " +
                    "3 = fire when within 3 degrees of perfect aim. Lower = stricter.",
                    new AcceptableValueRange<float>(0.1f, 30f)));

            // ── Weapons ───────────────────────────────────────────────────────
            WeaponEnabled = Config.Bind("5 - Weapon Assist", "Enabled", true,
                "Snap aim toward the nearest enemy when using weapon items like mines or bombs.");
            WeaponConeAngle = Config.Bind("5 - Weapon Assist", "Target Cone (degrees)", 60f,
                new ConfigDescription(
                    "Only consider enemies within this angle in front of you. " +
                    "60 = targets in your general forward direction. 180 = targets anywhere.",
                    new AcceptableValueRange<float>(5f, 180f)));
            WeaponSnapSpeed = Config.Bind("5 - Weapon Assist", "Snap Speed", 150f,
                new ConfigDescription(
                    "How fast your aim snaps toward the target, in degrees per second. 0 = instant.",
                    new AcceptableValueRange<float>(0f, 720f)));

            // ── Prediction ────────────────────────────────────────────────────
            BallSpeedFallback = Config.Bind("6 - Ball Prediction", "Ball Speed Fallback (m/s)", 30f,
                new ConfigDescription(
                    "Estimated ball speed used for prediction when the game value can't be read automatically. " +
                    "Check the BepInEx console log after your first swing to see the real value.",
                    new AcceptableValueRange<float>(5f, 100f)));

            // ── HUD ───────────────────────────────────────────────────────────
            HUDEnabled = Config.Bind("7 - HUD Overlay", "Show HUD", true,
                "Show the aim assist compass overlay on screen.");
            HUDToggleKey = Config.Bind("7 - HUD Overlay", "Toggle Key", "F1",
                new ConfigDescription(
                    "Press this key at any time to show or hide the HUD overlay.",
                    new AcceptableValueList<string>(KeyOptions)));
            HUDCorner = Config.Bind("7 - HUD Overlay", "Corner Position", 0,
                new ConfigDescription(
                    "Which corner of the screen the HUD appears in. " +
                    "0 = Bottom-Right.  1 = Bottom-Left.  2 = Top-Right.  3 = Top-Left.",
                    new AcceptableValueRange<int>(0, 3)));
        }

        /// <summary>
        /// Writes preset defaults only when the Preset key has just been changed.
        /// Uses a sidecar file (AimAssist.preset) as sentinel to detect this.
        /// Once the preset is stamped, the user can freely override every setting.
        /// </summary>
        private void ApplyPresetIfNew()
        {
            string lastApplied = System.IO.File.Exists(SentinelPath)
                ? System.IO.File.ReadAllText(SentinelPath).Trim()
                : "";

            if (lastApplied == Preset.Value) return; // no change, keep individual settings

            Log.LogInfo($"[AimAssist] Applying preset '{Preset.Value}'...");

            if (Preset.Value == "rage")
            {
                // Yaw
                GolfEnabled.Value     = true;
                GolfYawMode.Value     = "auto";
                GolfYawSpeed.Value    = 0f;       // instant
                GolfAssistAngle.Value = 180f;
                // Pitch
                PitchSolveEnabled.Value = true;
                PitchSolveMode.Value    = "with_yaw";
                PitchSolveSpeed.Value   = 0f;     // instant
                PitchHighArc.Value      = false;
                // AutoFire (off by default even in rage — user opts in explicitly)
                AutoFireMode.Value      = "off";
                AutoFireThreshold.Value = 3f;
                // Weapons
                WeaponEnabled.Value   = true;
                WeaponConeAngle.Value = 90f;
                WeaponSnapSpeed.Value = 0f;
            }
            else // "assist"
            {
                GolfEnabled.Value     = true;
                GolfYawMode.Value     = "hold";
                GolfYawSpeed.Value    = 200f;
                GolfAssistAngle.Value = 180f;
                PitchSolveEnabled.Value = false;
                PitchSolveMode.Value    = "with_yaw";
                PitchSolveSpeed.Value   = 150f;
                PitchHighArc.Value      = false;
                AutoFireMode.Value      = "off";
                AutoFireThreshold.Value = 5f;
                WeaponEnabled.Value   = true;
                WeaponConeAngle.Value = 60f;
                WeaponSnapSpeed.Value = 150f;
            }

            System.IO.File.WriteAllText(SentinelPath, Preset.Value);
            Log.LogInfo($"[AimAssist] Preset '{Preset.Value}' stamped. Edit .cfg or use ModConfig to tweak.");
        }
    }
}
