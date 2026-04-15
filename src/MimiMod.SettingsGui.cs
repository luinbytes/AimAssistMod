using System;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class SuperHackerGolf
{
    // ── Tabbed settings GUI (E24 neverlose-inspired redesign) ─────────────────
    //
    // IMGUI window with a left sidebar for tab navigation and a right content
    // area. Each tab is a vertical card stack. The style is modelled on
    // neverlose.cc / primordial.win — dark slate panels, cyan accent, clean
    // typography, colored feature chips.
    //
    // Unity IMGUI doesn't natively render rounded corners or drop shadows, so
    // visual distinction comes from careful use of flat background textures,
    // colored indicators, and padding. The window is draggable via a 36px tall
    // title bar at the top.

    private enum SettingsTab { Aim, Combat, Visuals, Physics, Data, Config }
    private SettingsTab settingsCurrentTab = SettingsTab.Aim;

    private Rect settingsWindowRect = new Rect(60f, 60f, 620f, 580f);
    private Vector2 settingsScrollPosition = Vector2.zero;
    private bool settingsGuiStylesDirty = true;

    // Styles
    private GUIStyle cachedWindowStyle;
    private GUIStyle cachedTitleBarStyle;
    private GUIStyle cachedTitleTextStyle;
    private GUIStyle cachedCloseButtonStyle;
    private GUIStyle cachedSidebarStyle;
    private GUIStyle cachedTabButtonStyle;
    private GUIStyle cachedTabButtonActiveStyle;
    private GUIStyle cachedContentAreaStyle;
    private GUIStyle cachedSectionHeaderStyle;
    private GUIStyle cachedCardStyle;
    private GUIStyle cachedLabelStyle;
    private GUIStyle cachedMutedLabelStyle;
    private GUIStyle cachedValueLabelStyle;
    private GUIStyle cachedButtonStyle;
    private GUIStyle cachedBigButtonStyle;
    private GUIStyle cachedChipOnStyle;
    private GUIStyle cachedChipOffStyle;
    private GUIStyle cachedToggleStyle;
    private GUIStyle cachedSeparatorStyle;
    private GUIStyle cachedFeatureRowStyle;

    // Flat textures
    private Texture2D texBgDark;
    private Texture2D texBgSidebar;
    private Texture2D texBgContent;
    private Texture2D texBgCard;
    private Texture2D texBgTitleBar;
    private Texture2D texAccentBlue;
    private Texture2D texAccentBlueDim;
    private Texture2D texTabActive;
    private Texture2D texTabHover;
    private Texture2D texStatusGreen;
    private Texture2D texStatusRed;
    private Texture2D texStatusYellow;
    private Texture2D texSeparator;
    private Texture2D texCloseHover;

    // Color palette (neverlose-inspired)
    private static readonly Color COL_BG_DARK     = new Color(0.051f, 0.066f, 0.090f, 0.97f);
    private static readonly Color COL_BG_SIDEBAR  = new Color(0.039f, 0.055f, 0.074f, 1.00f);
    private static readonly Color COL_BG_CONTENT  = new Color(0.086f, 0.106f, 0.133f, 1.00f);
    private static readonly Color COL_BG_CARD     = new Color(0.110f, 0.130f, 0.157f, 1.00f);
    private static readonly Color COL_BG_TITLEBAR = new Color(0.027f, 0.039f, 0.055f, 1.00f);
    private static readonly Color COL_ACCENT      = new Color(0.345f, 0.635f, 1.00f, 1.00f); // #58A6FF
    private static readonly Color COL_ACCENT_DIM  = new Color(0.122f, 0.435f, 0.922f, 1.00f); // #1F6FEB
    private static readonly Color COL_TAB_ACTIVE  = new Color(0.137f, 0.160f, 0.200f, 1.00f);
    private static readonly Color COL_TAB_HOVER   = new Color(0.110f, 0.130f, 0.165f, 1.00f);
    private static readonly Color COL_STATUS_ON   = new Color(0.247f, 0.725f, 0.314f, 1.00f); // #3FB950
    private static readonly Color COL_STATUS_OFF  = new Color(0.973f, 0.318f, 0.286f, 1.00f); // #F85149
    private static readonly Color COL_STATUS_WARN = new Color(0.824f, 0.600f, 0.133f, 1.00f); // #D29922
    private static readonly Color COL_SEPARATOR   = new Color(0.188f, 0.212f, 0.239f, 1.00f);
    private static readonly Color COL_TEXT        = new Color(0.902f, 0.929f, 0.953f, 1.00f); // #E6EDF3
    private static readonly Color COL_TEXT_DIM    = new Color(0.490f, 0.522f, 0.565f, 1.00f); // #7D8590
    private static readonly Color COL_TEXT_HIGH   = new Color(0.941f, 0.965f, 0.988f, 1.00f); // #F0F6FC
    private static readonly Color COL_CLOSE_HOVER = new Color(0.973f, 0.318f, 0.286f, 0.85f);

    private void UpdateSettingsGuiHotkey()
    {
        if (WasConfiguredKeyPressed(settingsGuiKey))
        {
            settingsGuiVisible = !settingsGuiVisible;
        }
    }

    public override void OnGUI()
    {
        // E31: ESP overlay — drawn every OnGUI pass but self-gated to
        // Repaint inside DrawEspOverlay to keep Layout passes cheap.
        if (espEnabled)
        {
            DrawEspOverlay();
        }

        // E30c: FOV circle overlay renders whenever the weapon aimbot is on,
        // regardless of settings GUI state. Always drawn FIRST so the settings
        // window stays above it when both are visible.
        if (fovCircleShow && aimbotMode != AimbotMode.Off)
        {
            DrawFovCircleOverlay();
        }

        if (!settingsGuiVisible) return;
        EnsureSettingsGuiStyles();

        settingsWindowRect = GUI.Window(
            unchecked((int)0x4D494D49), // 'MIMI'
            settingsWindowRect,
            DrawSettingsWindow,
            GUIContent.none,
            cachedWindowStyle);
    }

    private void EnsureSettingsGuiStyles()
    {
        if (!settingsGuiStylesDirty && cachedWindowStyle != null) return;
        settingsGuiStylesDirty = false;

        texBgDark     = MakeFlatTexture(COL_BG_DARK);
        texBgSidebar  = MakeFlatTexture(COL_BG_SIDEBAR);
        texBgContent  = MakeFlatTexture(COL_BG_CONTENT);
        texBgCard     = MakeFlatTexture(COL_BG_CARD);
        texBgTitleBar = MakeFlatTexture(COL_BG_TITLEBAR);
        texAccentBlue = MakeFlatTexture(COL_ACCENT);
        texAccentBlueDim = MakeFlatTexture(COL_ACCENT_DIM);
        texTabActive  = MakeFlatTexture(COL_TAB_ACTIVE);
        texTabHover   = MakeFlatTexture(COL_TAB_HOVER);
        texStatusGreen = MakeFlatTexture(COL_STATUS_ON);
        texStatusRed  = MakeFlatTexture(COL_STATUS_OFF);
        texStatusYellow = MakeFlatTexture(COL_STATUS_WARN);
        texSeparator  = MakeFlatTexture(COL_SEPARATOR);
        texCloseHover = MakeFlatTexture(COL_CLOSE_HOVER);

        cachedWindowStyle = new GUIStyle(GUI.skin.window)
        {
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
        };
        cachedWindowStyle.normal.background = texBgDark;
        cachedWindowStyle.onNormal.background = texBgDark;
        cachedWindowStyle.focused.background = texBgDark;

        cachedTitleBarStyle = new GUIStyle();
        cachedTitleBarStyle.normal.background = texBgTitleBar;

        cachedTitleTextStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(18, 0, 0, 0),
            richText = true,
        };
        cachedTitleTextStyle.normal.textColor = COL_TEXT_HIGH;

        cachedCloseButtonStyle = new GUIStyle();
        cachedCloseButtonStyle.normal.background = texBgTitleBar;
        cachedCloseButtonStyle.hover.background = texCloseHover;
        cachedCloseButtonStyle.active.background = texCloseHover;
        cachedCloseButtonStyle.fontSize = 18;
        cachedCloseButtonStyle.alignment = TextAnchor.MiddleCenter;
        cachedCloseButtonStyle.normal.textColor = COL_TEXT_DIM;
        cachedCloseButtonStyle.hover.textColor = Color.white;

        cachedSidebarStyle = new GUIStyle();
        cachedSidebarStyle.normal.background = texBgSidebar;

        cachedTabButtonStyle = new GUIStyle()
        {
            fontSize = 12,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(22, 10, 0, 0),
            richText = true,
            fixedHeight = 40f,
        };
        cachedTabButtonStyle.normal.textColor = COL_TEXT_DIM;
        cachedTabButtonStyle.hover.background = texTabHover;
        cachedTabButtonStyle.hover.textColor = COL_TEXT;

        cachedTabButtonActiveStyle = new GUIStyle(cachedTabButtonStyle);
        cachedTabButtonActiveStyle.normal.background = texTabActive;
        cachedTabButtonActiveStyle.normal.textColor = COL_ACCENT;
        cachedTabButtonActiveStyle.hover.background = texTabActive;
        cachedTabButtonActiveStyle.hover.textColor = COL_ACCENT;

        cachedContentAreaStyle = new GUIStyle();
        cachedContentAreaStyle.normal.background = texBgContent;
        cachedContentAreaStyle.padding = new RectOffset(20, 20, 16, 16);

        cachedSectionHeaderStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            margin = new RectOffset(0, 0, 4, 8),
            richText = true,
        };
        cachedSectionHeaderStyle.normal.textColor = COL_TEXT_DIM;

        cachedCardStyle = new GUIStyle();
        cachedCardStyle.normal.background = texBgCard;
        cachedCardStyle.padding = new RectOffset(14, 14, 12, 12);
        cachedCardStyle.margin = new RectOffset(0, 0, 0, 8);

        cachedLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            wordWrap = true,
            richText = true,
            margin = new RectOffset(0, 0, 2, 2),
        };
        cachedLabelStyle.normal.textColor = COL_TEXT;

        cachedMutedLabelStyle = new GUIStyle(cachedLabelStyle);
        cachedMutedLabelStyle.normal.textColor = COL_TEXT_DIM;

        cachedValueLabelStyle = new GUIStyle(cachedLabelStyle)
        {
            fontStyle = FontStyle.Bold,
        };
        cachedValueLabelStyle.normal.textColor = COL_ACCENT;

        cachedButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            padding = new RectOffset(12, 12, 7, 7),
            margin = new RectOffset(3, 3, 3, 3),
            border = new RectOffset(0, 0, 0, 0),
            fontStyle = FontStyle.Bold,
            richText = true,
        };
        cachedButtonStyle.normal.background = MakeFlatTexture(new Color(0.137f, 0.160f, 0.200f, 1f));
        cachedButtonStyle.hover.background = MakeFlatTexture(new Color(0.188f, 0.227f, 0.282f, 1f));
        cachedButtonStyle.active.background = MakeFlatTexture(new Color(0.137f, 0.160f, 0.200f, 1f));
        cachedButtonStyle.normal.textColor = COL_TEXT;
        cachedButtonStyle.hover.textColor = COL_TEXT_HIGH;
        cachedButtonStyle.active.textColor = COL_TEXT_HIGH;

        cachedBigButtonStyle = new GUIStyle(cachedButtonStyle)
        {
            fontSize = 13,
            padding = new RectOffset(14, 14, 10, 10),
            fixedHeight = 36f,
        };

        cachedChipOnStyle = new GUIStyle(cachedButtonStyle)
        {
            fontSize = 11,
            padding = new RectOffset(0, 0, 0, 0),
            fixedHeight = 28f,
            fixedWidth = 64f,
            alignment = TextAnchor.MiddleCenter,
        };
        cachedChipOnStyle.normal.background = MakeFlatTexture(new Color(0.247f * 0.4f, 0.725f * 0.4f, 0.314f * 0.4f, 1f));
        cachedChipOnStyle.hover.background = MakeFlatTexture(new Color(0.247f * 0.55f, 0.725f * 0.55f, 0.314f * 0.55f, 1f));
        cachedChipOnStyle.active.background = cachedChipOnStyle.hover.background;
        cachedChipOnStyle.normal.textColor = COL_STATUS_ON;
        cachedChipOnStyle.hover.textColor = Color.white;

        cachedChipOffStyle = new GUIStyle(cachedChipOnStyle);
        cachedChipOffStyle.normal.background = MakeFlatTexture(new Color(0.11f, 0.13f, 0.16f, 1f));
        cachedChipOffStyle.hover.background = MakeFlatTexture(new Color(0.15f, 0.18f, 0.22f, 1f));
        cachedChipOffStyle.active.background = cachedChipOffStyle.hover.background;
        cachedChipOffStyle.normal.textColor = COL_TEXT_DIM;
        cachedChipOffStyle.hover.textColor = COL_TEXT;

        cachedToggleStyle = new GUIStyle(GUI.skin.toggle)
        {
            fontSize = 12,
            margin = new RectOffset(2, 2, 4, 4),
            padding = new RectOffset(22, 2, 2, 2),
            richText = true,
        };
        cachedToggleStyle.normal.textColor = COL_TEXT;
        cachedToggleStyle.onNormal.textColor = COL_TEXT_HIGH;
        cachedToggleStyle.hover.textColor = COL_TEXT_HIGH;

        cachedSeparatorStyle = new GUIStyle();
        cachedSeparatorStyle.normal.background = texSeparator;
        cachedSeparatorStyle.fixedHeight = 1f;
        cachedSeparatorStyle.margin = new RectOffset(0, 0, 6, 6);

        cachedFeatureRowStyle = new GUIStyle();
        cachedFeatureRowStyle.normal.background = texBgCard;
        cachedFeatureRowStyle.padding = new RectOffset(12, 12, 10, 10);
        cachedFeatureRowStyle.margin = new RectOffset(0, 0, 0, 6);
    }

    private static Texture2D MakeFlatTexture(Color c)
    {
        Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
                tex.SetPixel(x, y, c);
        tex.Apply(false, false);
        tex.filterMode = FilterMode.Point;
        return tex;
    }

    private void DrawSeparator()
    {
        GUILayout.Box(GUIContent.none, cachedSeparatorStyle, GUILayout.ExpandWidth(true), GUILayout.Height(1f));
    }

    // ── Main window layout ────────────────────────────────────────────────────

    private void DrawSettingsWindow(int id)
    {
        float width = settingsWindowRect.width;
        float height = settingsWindowRect.height;

        // Title bar
        Rect titleBarRect = new Rect(0, 0, width, 36f);
        GUI.Box(titleBarRect, GUIContent.none, cachedTitleBarStyle);
        GUI.Label(new Rect(0, 0, width - 44f, 36f),
            "<color=#58A6FF>SUPER</color><color=#F0F6FC>HACKERGOLF</color>  <color=#7D8590>v1.0</color>",
            cachedTitleTextStyle);
        if (GUI.Button(new Rect(width - 38f, 4f, 28f, 28f), "×", cachedCloseButtonStyle))
        {
            settingsGuiVisible = false;
        }

        // Sidebar
        Rect sidebarRect = new Rect(0, 36f, 140f, height - 36f);
        GUI.Box(sidebarRect, GUIContent.none, cachedSidebarStyle);
        GUILayout.BeginArea(new Rect(0, 42f, 140f, height - 42f));
        DrawTab("  AIM",       SettingsTab.Aim);
        DrawTab("  COMBAT",    SettingsTab.Combat);
        DrawTab("  VISUALS",   SettingsTab.Visuals);
        DrawTab("  PHYSICS",   SettingsTab.Physics);
        DrawTab("  DATA",      SettingsTab.Data);
        DrawTab("  CONFIG",    SettingsTab.Config);
        GUILayout.EndArea();

        // Accent stripe along right edge of sidebar (subtle)
        GUI.DrawTexture(new Rect(139f, 36f, 1f, height - 36f), texSeparator);

        // Content area
        Rect contentRect = new Rect(140f, 36f, width - 140f, height - 36f);
        GUI.Box(contentRect, GUIContent.none, cachedContentAreaStyle);

        // E25b: wrap tab body render in try/catch. If any tab draw method
        // throws mid-render, we must still close BeginScrollView/BeginArea
        // or the GUILayout stack corrupts for every subsequent frame — that's
        // why the Config tab made the whole UI go blank. The try/finally
        // guarantees layout balance.
        GUILayout.BeginArea(new Rect(160f, 52f, width - 180f, height - 68f));
        settingsScrollPosition = GUILayout.BeginScrollView(settingsScrollPosition);
        try
        {
            switch (settingsCurrentTab)
            {
                case SettingsTab.Aim:     DrawAimTab(); break;
                case SettingsTab.Combat:  DrawCombatTab(); break;
                case SettingsTab.Visuals: DrawVisualsTab(); break;
                case SettingsTab.Physics: DrawPhysicsTab(); break;
                case SettingsTab.Data:    DrawDataTab(); break;
                case SettingsTab.Config:  DrawConfigTab(); break;
            }
        }
        catch (Exception ex)
        {
            GUILayout.Label($"<color=#F85149><b>Tab render failed:</b></color>\n<color=#E6EDF3>{ex.GetType().Name}: {ex.Message}</color>", cachedLabelStyle);
            MelonLogger.Warning($"[SuperHackerGolf] Settings tab {settingsCurrentTab} render threw {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // Drag handle on title bar (skip close button area)
        GUI.DragWindow(new Rect(0, 0, width - 44f, 36f));
    }

    private void DrawTab(string label, SettingsTab tab)
    {
        bool isActive = settingsCurrentTab == tab;
        GUIStyle style = isActive ? cachedTabButtonActiveStyle : cachedTabButtonStyle;
        if (GUILayout.Button(label, style, GUILayout.ExpandWidth(true)))
        {
            settingsCurrentTab = tab;
        }
        // Active tab gets a left-edge accent bar
        if (isActive)
        {
            Rect last = GUILayoutUtility.GetLastRect();
            GUI.DrawTexture(new Rect(last.x, last.y, 3f, last.height), texAccentBlue);
        }
    }

    private void SectionHeader(string text)
    {
        GUILayout.Label(text.ToUpperInvariant(), cachedSectionHeaderStyle);
    }

    // Feature row: colored left-stripe indicator + name + description + toggle button + keybind
    private bool DrawFeatureRow(string name, bool state, string keyLabel, string description)
    {
        GUILayout.BeginHorizontal(cachedFeatureRowStyle);

        // Left indicator stripe (6px wide, full height of the row)
        GUILayout.Space(2);
        Rect stripeRect = GUILayoutUtility.GetRect(3f, 42f, GUILayout.Width(3f), GUILayout.ExpandHeight(true));
        GUI.DrawTexture(stripeRect, state ? texStatusGreen : texSeparator);
        GUILayout.Space(10);

        // Name + description stacked
        GUILayout.BeginVertical();
        GUILayout.Space(2);
        GUILayout.Label($"<b><color=#F0F6FC>{name}</color></b>", cachedLabelStyle);
        GUILayout.Label($"<color=#7D8590>{description}</color>", cachedLabelStyle);
        GUILayout.EndVertical();

        GUILayout.FlexibleSpace();

        // Keybind pill
        GUILayout.Label($"<color=#7D8590>[<b>{keyLabel}</b>]</color>", cachedLabelStyle, GUILayout.Width(44f));

        // Toggle chip
        bool clicked = GUILayout.Button(state ? "ON" : "OFF",
            state ? cachedChipOnStyle : cachedChipOffStyle);

        GUILayout.EndHorizontal();
        return clicked;
    }

    private bool DrawCheckbox(string label, bool state)
    {
        return GUILayout.Toggle(state, "  " + label, cachedToggleStyle) != state;
    }

    // ── Tab: AIM ──────────────────────────────────────────────────────────────

    private void DrawAimTab()
    {
        SectionHeader("Primary assists");

        if (DrawFeatureRow("Golf Assist", assistEnabled, assistToggleKeyLabel,
            "aim assist + auto-release swings"))
        {
            ToggleAssist();
        }

        if (DrawFeatureRow("Weapon Aimbot", weaponAssistEnabled, weaponAssistToggleKeyLabel,
            "pistol / elephant gun · lock-on override"))
        {
            ToggleWeaponAssist();
        }

        if (weaponAssistEnabled)
        {
            GUILayout.BeginVertical(cachedCardStyle);
            GUILayout.Label("<b><color=#F0F6FC>Mode</color></b>", cachedLabelStyle);
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            if (DrawModePill("LEGIT",  aimbotMode == AimbotMode.Legit))  SetAimbotMode(AimbotMode.Legit);
            if (DrawModePill("RAGE",   aimbotMode == AimbotMode.Rage))   SetAimbotMode(AimbotMode.Rage);
            if (DrawModePill("CUSTOM", aimbotMode == AimbotMode.Custom)) SetAimbotMode(AimbotMode.Custom);
            GUILayout.EndHorizontal();
            GUILayout.Space(2);
            string modeBlurb = aimbotMode switch
            {
                AimbotMode.Legit  => "<color=#7D8590>Visible crosshair rotation toward target. Smooth, tight cone, center-mass.</color>",
                AimbotMode.Rage   => "<color=#7D8590>Silent aim · full-FOV · instant snap · headshot offset. No visible camera rotation.</color>",
                AimbotMode.Custom => "<color=#7D8590>Manual tuning — presets won't overwrite your knobs.</color>",
                _ => ""
            };
            GUILayout.Label(modeBlurb, cachedLabelStyle);

            GUILayout.Space(8);
            GUILayout.Label("<b><color=#F0F6FC>FOV overlay</color></b>", cachedLabelStyle);
            bool newShowFov = GUILayout.Toggle(fovCircleShow,
                "  <b>Show FOV circle</b>  <color=#7D8590>(crosshair cone overlay)</color>",
                cachedToggleStyle);
            if (newShowFov != fovCircleShow) fovCircleShow = newShowFov;

            GUILayout.Space(8);
            GUILayout.Label("<b><color=#F0F6FC>Aimbot options</color></b>", cachedLabelStyle);
            GUILayout.Space(4);

            bool newAutoFire = GUILayout.Toggle(weaponAutoFireEnabled,
                "  <b>Auto-fire</b>  <color=#7D8590>(off = tracking only, you click to fire)</color>",
                cachedToggleStyle);
            if (newAutoFire != weaponAutoFireEnabled) weaponAutoFireEnabled = newAutoFire;

            if (weaponAutoFireEnabled)
            {
                float rate = 1f / Mathf.Max(0.01f, weaponAutoFireMinIntervalSec);
                GUILayout.Label($"<color=#7D8590>Fire rate:</color> <color=#58A6FF><b>{rate:F1} shots/s</b></color>  <color=#7D8590>(every {weaponAutoFireMinIntervalSec * 1000f:F0}ms)</color>", cachedLabelStyle);
                float newRate = GUILayout.HorizontalSlider(rate, 0.5f, 20f);
                if (Mathf.Abs(newRate - rate) > 0.05f)
                {
                    weaponAutoFireMinIntervalSec = 1f / Mathf.Max(0.5f, newRate);
                }
            }

            GUILayout.Space(6);
            GUILayout.Label("<b><color=#F0F6FC>Target types</color></b>", cachedLabelStyle);
            bool newTP = GUILayout.Toggle(weaponAssistTargetPlayers,
                "  Players  <color=#7D8590>(real opponents)</color>", cachedToggleStyle);
            if (newTP != weaponAssistTargetPlayers) weaponAssistTargetPlayers = newTP;

            bool newTD = GUILayout.Toggle(weaponAssistTargetDummies,
                "  Target dummies  <color=#7D8590>(practice range)</color>", cachedToggleStyle);
            if (newTD != weaponAssistTargetDummies) weaponAssistTargetDummies = newTD;

            bool newTM = GUILayout.Toggle(weaponAssistTargetMines,
                "  Landmines  <color=#7D8590>(risky — suicide potential)</color>", cachedToggleStyle);
            if (newTM != weaponAssistTargetMines) weaponAssistTargetMines = newTM;

            bool newTC = GUILayout.Toggle(weaponAssistTargetGolfCarts,
                "  Golf carts  <color=#7D8590>(vehicles — usually noise)</color>", cachedToggleStyle);
            if (newTC != weaponAssistTargetGolfCarts) weaponAssistTargetGolfCarts = newTC;

            GUILayout.Space(6);
            bool newSP = GUILayout.Toggle(weaponAssistSkipProtected,
                "  <b>Skip protected targets</b>  <color=#7D8590>(shielded / knockout-immune won't be shot)</color>", cachedToggleStyle);
            if (newSP != weaponAssistSkipProtected) weaponAssistSkipProtected = newSP;

            GUILayout.Space(6);
            GUILayout.Label($"<color=#7D8590>Target cone:</color> <color=#58A6FF><b>{weaponAssistConeAngleDeg:F0}°</b></color>", cachedLabelStyle);
            float newCone = GUILayout.HorizontalSlider(weaponAssistConeAngleDeg, 5f, 90f);
            if (Mathf.Abs(newCone - weaponAssistConeAngleDeg) > 0.5f)
                weaponAssistConeAngleDeg = Mathf.Round(newCone);

            GUILayout.Label($"<color=#7D8590>Max range:</color> <color=#58A6FF><b>{weaponAssistMaxRange:F0}m</b></color>", cachedLabelStyle);
            float newRange = GUILayout.HorizontalSlider(weaponAssistMaxRange, 10f, 200f);
            if (Mathf.Abs(newRange - weaponAssistMaxRange) > 0.5f)
                weaponAssistMaxRange = Mathf.Round(newRange);

            GUILayout.Space(6);
            GUILayout.Label($"<color=#7D8590>Smoothing:</color> <color=#58A6FF><b>{aimbotSmoothingFactor:F0}</b></color>  <color=#7D8590>(0 = instant · 100 = slow drift · Legit only)</color>", cachedLabelStyle);
            float newSmooth = GUILayout.HorizontalSlider(aimbotSmoothingFactor, 0f, 100f);
            if (Mathf.Abs(newSmooth - aimbotSmoothingFactor) > 0.5f)
                aimbotSmoothingFactor = Mathf.Round(newSmooth);

            GUILayout.Label("<color=#7D8590>Hitboxes:</color>  <color=#7D8590>(priority: first enabled wins · Rage-only)</color>", cachedLabelStyle);
            GUILayout.BeginHorizontal();
            if (DrawHitboxChip("Head",  (aimbotHitboxFlags & HitboxFlags.Head)  != 0)) aimbotHitboxFlags ^= HitboxFlags.Head;
            if (DrawHitboxChip("Chest", (aimbotHitboxFlags & HitboxFlags.Chest) != 0)) aimbotHitboxFlags ^= HitboxFlags.Chest;
            if (DrawHitboxChip("Legs",  (aimbotHitboxFlags & HitboxFlags.Legs)  != 0)) aimbotHitboxFlags ^= HitboxFlags.Legs;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (aimbotHitboxFlags == HitboxFlags.None) aimbotHitboxFlags = HitboxFlags.Chest;

            GUILayout.Label($"<color=#7D8590>Sticky lock time:</color> <color=#58A6FF><b>{aimbotStickyLockMs:F0}ms</b></color>  <color=#7D8590>(target hysteresis)</color>", cachedLabelStyle);
            float newSticky = GUILayout.HorizontalSlider(aimbotStickyLockMs, 0f, 1000f);
            if (Mathf.Abs(newSticky - aimbotStickyLockMs) > 2f)
                aimbotStickyLockMs = Mathf.Round(newSticky / 10f) * 10f;

            GUILayout.EndVertical();
        }

        DrawSeparator();
        SectionHeader("Swing tuning");

        GUILayout.BeginVertical(cachedCardStyle);
        bool newAllowOvercharge = GUILayout.Toggle(allowOvercharge,
            "  Allow 115% overcharge  <color=#7D8590>(off = clamp at 100%)</color>",
            cachedToggleStyle);
        if (newAllowOvercharge != allowOvercharge) allowOvercharge = newAllowOvercharge;

        bool newInstaHit = GUILayout.Toggle(instaHitEnabled,
            "  Golf swing insta-hit  <color=#7D8590>(off = manual charge, auto-release at optimal)</color>",
            cachedToggleStyle);
        if (newInstaHit != instaHitEnabled)
        {
            instaHitEnabled = newInstaHit;
            ResetChargeState();
        }
        GUILayout.EndVertical();
    }

    // ── Tab: COMBAT ───────────────────────────────────────────────────────────

    private void DrawCombatTab()
    {
        SectionHeader("Movement");

        if (DrawFeatureRow("Bunnyhop", bunnyhopEnabled, bunnyhopToggleKeyLabel,
            "hold SPACE to hop while enabled"))
        {
            ToggleBunnyhop();
        }

        DrawSeparator();
        SectionHeader("Defense");

        if (DrawFeatureRow("Shield Forced", shieldForcedOn, shieldToggleKeyLabel,
            "force electromagnet shield + knockout immunity"))
        {
            ToggleForcedShield();
        }

        DrawSeparator();
        SectionHeader("Offense");

        if (DrawFeatureRow("Mine Pre-Arm", mineAssistEnabled, mineAssistToggleKeyLabel,
            "host-only · unarmed mines explode on hit"))
        {
            ToggleMineAssist();
        }

        GUILayout.BeginVertical(cachedCardStyle);
        GUILayout.Label("<color=#D29922><b>ⓘ</b></color>  <color=#D6DCE4>Mine pre-arm is a server-authoritative patch.</color>", cachedLabelStyle);
        GUILayout.Label("<color=#7D8590>It only takes effect when <b>you</b> are hosting the lobby. As a guest, the host's unmodified game rejects pre-arm hits.</color>", cachedLabelStyle);
        GUILayout.EndVertical();
    }

    // ── Tab: VISUALS ──────────────────────────────────────────────────────────

    private void DrawVisualsTab()
    {
        SectionHeader("ESP Overlay");

        GUILayout.BeginVertical(cachedCardStyle);
        bool newEsp = GUILayout.Toggle(espEnabled,
            "  <b>ESP</b>  <color=#7D8590>(player boxes + names + distance)</color>", cachedToggleStyle);
        if (newEsp != espEnabled) espEnabled = newEsp;

        if (espEnabled)
        {
            GUILayout.Space(4);
            GUILayout.Label("<b><color=#F0F6FC>Elements</color></b>", cachedLabelStyle);
            bool newBox = GUILayout.Toggle(espDrawBox, "  Box", cachedToggleStyle);
            if (newBox != espDrawBox) espDrawBox = newBox;
            if (espDrawBox)
            {
                bool newCorners = GUILayout.Toggle(espUseCorners,
                    "    Corner brackets  <color=#7D8590>(off = full outline)</color>", cachedToggleStyle);
                if (newCorners != espUseCorners) espUseCorners = newCorners;
            }
            bool newName = GUILayout.Toggle(espDrawName, "  Name tag", cachedToggleStyle);
            if (newName != espDrawName) espDrawName = newName;
            bool newDist = GUILayout.Toggle(espDrawDistance, "  Distance", cachedToggleStyle);
            if (newDist != espDrawDistance) espDrawDistance = newDist;
            bool newHp = GUILayout.Toggle(espDrawHealthBar, "  Health bar  <color=#7D8590>(if resolvable)</color>", cachedToggleStyle);
            if (newHp != espDrawHealthBar) espDrawHealthBar = newHp;
            bool newTracer = GUILayout.Toggle(espDrawTracer, "  Tracer line  <color=#7D8590>(bottom-center to target)</color>", cachedToggleStyle);
            if (newTracer != espDrawTracer) espDrawTracer = newTracer;
            bool newInvis = GUILayout.Toggle(espShowInvisible, "  Show through walls  <color=#7D8590>(dim color when blocked)</color>", cachedToggleStyle);
            if (newInvis != espShowInvisible) espShowInvisible = newInvis;

            GUILayout.Space(6);
            GUILayout.Label("<b><color=#F0F6FC>Entities</color></b>", cachedLabelStyle);
            bool nTP = GUILayout.Toggle(espShowPlayers, "  Players", cachedToggleStyle);
            if (nTP != espShowPlayers) espShowPlayers = nTP;
            bool nTD = GUILayout.Toggle(espShowDummies, "  Target dummies", cachedToggleStyle);
            if (nTD != espShowDummies) espShowDummies = nTD;
            bool nTM = GUILayout.Toggle(espShowMines, "  Landmines", cachedToggleStyle);
            if (nTM != espShowMines) espShowMines = nTM;
            bool nTC = GUILayout.Toggle(espShowGolfCarts, "  Golf carts", cachedToggleStyle);
            if (nTC != espShowGolfCarts) espShowGolfCarts = nTC;

            GUILayout.Space(6);
            GUILayout.Label($"<color=#7D8590>Max distance:</color> <color=#58A6FF><b>{espMaxDistance:F0}m</b></color>", cachedLabelStyle);
            float newMaxDist = GUILayout.HorizontalSlider(espMaxDistance, 25f, 500f);
            if (Mathf.Abs(newMaxDist - espMaxDistance) > 1f) espMaxDistance = Mathf.Round(newMaxDist / 5f) * 5f;
        }
        GUILayout.EndVertical();

        DrawSeparator();
        SectionHeader("Trails");

        GUILayout.BeginVertical(cachedCardStyle);
        bool newPredicted = GUILayout.Toggle(predictedTrailEnabled, "  Live predicted trajectory", cachedToggleStyle);
        if (newPredicted != predictedTrailEnabled) { predictedTrailEnabled = newPredicted; MarkTrailVisualSettingsDirty(); }

        bool newFrozen = GUILayout.Toggle(frozenTrailEnabled, "  Frozen predicted  <color=#7D8590>(locks on release)</color>", cachedToggleStyle);
        if (newFrozen != frozenTrailEnabled) { frozenTrailEnabled = newFrozen; MarkTrailVisualSettingsDirty(); }

        bool newActual = GUILayout.Toggle(actualTrailEnabled, "  Actual shot trail", cachedToggleStyle);
        if (newActual != actualTrailEnabled) { actualTrailEnabled = newActual; MarkTrailVisualSettingsDirty(); }
        GUILayout.EndVertical();

        DrawSeparator();
        SectionHeader("Impact preview");

        GUILayout.BeginVertical(cachedCardStyle);
        bool newPreview = GUILayout.Toggle(impactPreviewEnabled, "  Impact preview window", cachedToggleStyle);
        if (newPreview != impactPreviewEnabled) { impactPreviewEnabled = newPreview; nextImpactPreviewRenderTime = 0f; }

        if (impactPreviewEnabled)
        {
            GUILayout.Space(6);
            GUILayout.Label($"<color=#7D8590>FPS:</color> <color=#58A6FF><b>{impactPreviewTargetFps:F0}</b></color>", cachedLabelStyle);
            float newFps = GUILayout.HorizontalSlider(impactPreviewTargetFps, 10f, 144f);
            if (Mathf.Abs(newFps - impactPreviewTargetFps) > 0.5f) { impactPreviewTargetFps = Mathf.Round(newFps); nextImpactPreviewRenderTime = 0f; }

            GUILayout.Label($"<color=#7D8590>Size:</color> <color=#58A6FF><b>{impactPreviewTextureWidth}×{impactPreviewTextureHeight}</b></color>", cachedLabelStyle);
            float newWidth = GUILayout.HorizontalSlider(impactPreviewTextureWidth, 320f, 1920f);
            int snappedWidth = Mathf.Clamp(Mathf.RoundToInt(newWidth / 32f) * 32, 320, 1920);
            if (snappedWidth != impactPreviewTextureWidth)
            {
                impactPreviewTextureWidth = snappedWidth;
                impactPreviewTextureHeight = Mathf.RoundToInt(snappedWidth * 9f / 16f);
                nextImpactPreviewRenderTime = 0f;
            }
        }
        GUILayout.EndVertical();
    }

    // ── Tab: PHYSICS ──────────────────────────────────────────────────────────

    private void DrawPhysicsTab()
    {
        SectionHeader("Reflected game values");

        GUILayout.BeginVertical(cachedCardStyle);
        GUILayout.Label($"<color=#7D8590>Ball WindFactor:</color>  <color=#58A6FF><b>{GetBallWindFactor():F3}</b></color>", cachedLabelStyle);
        GUILayout.Label($"<color=#7D8590>CrossWindFactor:</color>  <color=#58A6FF><b>{GetBallCrossWindFactor():F3}</b></color>", cachedLabelStyle);
        GUILayout.Label($"<color=#7D8590>Air drag:</color>  <color=#58A6FF><b>{GetRuntimeLinearAirDragFactor():F5}</b></color>", cachedLabelStyle);
        GUILayout.Label($"<color=#7D8590>Max swing speed:</color>  <color=#58A6FF><b>{GetBallMaxSwingHitSpeed():F1} m/s</b></color>", cachedLabelStyle);
        GUILayout.Space(4);
        GUILayout.Label("<color=#7D8590>Live wind:</color> " + GetWindDiagnosticReadout(), cachedLabelStyle);
        GUILayout.EndVertical();

        DrawSeparator();
        SectionHeader("Roll damping (tunable)");

        GUILayout.BeginVertical(cachedCardStyle);
        GUILayout.Label($"<color=#7D8590>Multiplier:</color>  <color=#58A6FF><b>×{rollDampingMultiplier:F2}</b></color>  <color=#7D8590>(1.0 = game default)</color>", cachedLabelStyle);
        float newMul = GUILayout.HorizontalSlider(rollDampingMultiplier, 0.5f, 3.5f);
        if (Mathf.Abs(newMul - rollDampingMultiplier) > 0.005f)
        {
            rollDampingMultiplier = Mathf.Round(newMul * 100f) / 100f;
            nextPredictedPathRefreshTime = 0f;
        }

        GUILayout.Space(4);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("1.0×", cachedButtonStyle)) { rollDampingMultiplier = 1.0f; nextPredictedPathRefreshTime = 0f; }
        if (GUILayout.Button("1.5×", cachedButtonStyle)) { rollDampingMultiplier = 1.5f; nextPredictedPathRefreshTime = 0f; }
        if (GUILayout.Button("2.0×", cachedButtonStyle)) { rollDampingMultiplier = 2.0f; nextPredictedPathRefreshTime = 0f; }
        if (GUILayout.Button("2.5×", cachedButtonStyle)) { rollDampingMultiplier = 2.5f; nextPredictedPathRefreshTime = 0f; }
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        GUILayout.Label("<color=#7D8590>Higher = sim's ball stops sooner. Use when <b>pred_rest_delta_mag</b> in the CSV shows consistent over-prediction on long shots.</color>", cachedLabelStyle);
        GUILayout.EndVertical();
    }

    // ── Tab: DATA ─────────────────────────────────────────────────────────────

    private void DrawDataTab()
    {
        SectionHeader("Status");

        GUILayout.BeginVertical(cachedCardStyle);
        string playerBadge = playerFound
            ? "<color=#3FB950><b>● ACTIVE</b></color>"
            : "<color=#F85149><b>● SEARCHING</b></color>";
        GUILayout.Label($"<color=#7D8590>Player:</color>  {playerBadge}", cachedLabelStyle);
        GUILayout.Label($"<color=#7D8590>Ball source:</color>  <color=#D6DCE4>{lastBallResolveSource}</color>", cachedLabelStyle);

        if (holePosition != Vector3.zero && playerMovement != null)
        {
            float dist = Vector3.Distance(playerMovement.transform.position, holePosition);
            GUILayout.Label($"<color=#7D8590>Hole distance:</color>  <color=#58A6FF><b>{dist:F1}m</b></color>", cachedLabelStyle);
        }
        GUILayout.Label($"<color=#7D8590>Ideal shot:</color>  <color=#58A6FF><b>{idealSwingPower * 100f:F0}%</b></color> pwr · <color=#58A6FF><b>{idealSwingPitch:F1}°</b></color> pitch", cachedLabelStyle);
        GUILayout.EndVertical();

        DrawSeparator();
        SectionHeader("Telemetry");

        GUILayout.BeginVertical(cachedCardStyle);
        bool newTelemetry = GUILayout.Toggle(telemetryEnabled,
            "  <b>Log shots to CSV</b>  <color=#7D8590>(SuperHackerGolf-telemetry.csv)</color>",
            cachedToggleStyle);
        if (newTelemetry != telemetryEnabled) telemetryEnabled = newTelemetry;

        if (telemetryEnabled)
        {
            GUILayout.Space(4);
            GUILayout.Label($"<color=#7D8590>Shots logged:</color>  <color=#58A6FF><b>{GetTelemetryShotCount()}</b></color>", cachedLabelStyle);

            var recent = GetTelemetryRecentSummaries();
            if (recent.Count > 0)
            {
                GUILayout.Space(4);
                GUILayout.Label("<color=#7D8590>Recent shots:</color>", cachedLabelStyle);
                for (int i = recent.Count - 1; i >= 0 && i >= recent.Count - 5; i--)
                {
                    GUILayout.Label(recent[i], cachedMutedLabelStyle);
                }
            }
        }
        GUILayout.EndVertical();

        DrawSeparator();
        SectionHeader("Item Spawner");

        GUILayout.BeginVertical(cachedCardStyle);
        GUILayout.Label("<color=#7D8590>Grants an item to your inventory via CmdAddItem. " +
            "Requires host to have this mod, or you to be the host. " +
            "MatchSetupRules.IsCheatsEnabled is Harmony-patched.</color>", cachedLabelStyle);
        GUILayout.Space(4);

        var catalog = GetItemSpawnCatalog();
        if (catalog == null || catalog.Count == 0)
        {
            GUILayout.Label("<color=#F85149>No items cataloged — spawner not initialized yet.</color>", cachedLabelStyle);
        }
        else
        {
            int perRow = 3;
            int col = 0;
            bool rowOpen = false;
            for (int i = 0; i < catalog.Count; i++)
            {
                var entry = catalog[i];
                if (!entry.IsPlayerUsable) continue;

                if (col == 0)
                {
                    GUILayout.BeginHorizontal();
                    rowOpen = true;
                }

                if (GUILayout.Button($"Give {entry.Name}", cachedBigButtonStyle))
                {
                    SpawnItemForLocalPlayer(entry.Value);
                }

                col++;
                if (col >= perRow)
                {
                    GUILayout.EndHorizontal();
                    rowOpen = false;
                    col = 0;
                }
            }
            if (rowOpen)
            {
                GUILayout.EndHorizontal();
            }
        }
        GUILayout.EndVertical();
    }

    // ── Tab: CONFIG ───────────────────────────────────────────────────────────

    private void DrawConfigTab()
    {
        SectionHeader("Keybinds");

        GUILayout.BeginVertical(cachedCardStyle);
        GUILayout.Label("<color=#7D8590>Click a key slot to rebind. Press Escape to cancel. " +
            "Binds that support Hold/Released show a mode picker — Hold activates while pressed, " +
            "Released is inverted (active while NOT pressed).</color>", cachedLabelStyle);
        GUILayout.Space(6);

        foreach (var info in AllBinds())
        {
            DrawBindRow(info);
        }
        GUILayout.EndVertical();

        DrawSeparator();
        SectionHeader("Persistence");

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("SAVE CONFIG", cachedBigButtonStyle))
        {
            SaveConfigToFile();
        }
        if (GUILayout.Button("RELOAD", cachedBigButtonStyle))
        {
            LoadOrCreateConfig();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        // Avoid nested rich-text color tags — Unity's parser can choke on them.
        GUILayout.Label("<color=#7D8590>Config file:</color>  <color=#D6DCE4>Mods/SuperHackerGolf.cfg</color>", cachedLabelStyle);
    }

    private void DrawKeyRow(string label, string keyLabel)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label($"<color=#D6DCE4>{label}</color>", cachedLabelStyle);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"<color=#58A6FF><b>[{keyLabel}]</b></color>", cachedLabelStyle, GUILayout.Width(60f));
        GUILayout.EndHorizontal();
    }

    // Clickable mode-selector pill. Active pill uses the accent-background
    // tab-active style; inactive uses the dim tab style. Returns true if
    // clicked this frame.
    private bool DrawModePill(string label, bool isActive)
    {
        GUIStyle style = isActive ? cachedTabButtonActiveStyle : cachedTabButtonStyle;
        return GUILayout.Button(label, style, GUILayout.Height(28f), GUILayout.MinWidth(80f));
    }

    private void DrawBindRow(BindInfo info)
    {
        if (info == null) return;

        GUILayout.BeginHorizontal();
        GUILayout.Label($"<color=#D6DCE4>{info.DisplayName}</color>", cachedLabelStyle, GUILayout.Width(150f));

        // Key slot — click to listen
        bool listening = IsListeningForRebind(info.Name);
        string keyLabel;
        GUIStyle slotStyle;
        if (listening)
        {
            keyLabel = "<color=#F0B429>[...]</color>";
            slotStyle = cachedTabButtonActiveStyle;
        }
        else
        {
            keyLabel = $"[{info.Key}]";
            slotStyle = cachedTabButtonStyle;
        }
        if (GUILayout.Button(keyLabel, slotStyle, GUILayout.Height(24f), GUILayout.Width(110f)))
        {
            if (listening) CancelRebind();
            else BeginRebind(info.Name);
        }

        GUILayout.Space(6);

        // Mode picker — only for binds that support Hold/Released
        if (info.SupportsHoldModes)
        {
            if (DrawModeMiniPill("T", info.Mode == BindActivationMode.Toggle)) SetBindMode(info, BindActivationMode.Toggle);
            if (DrawModeMiniPill("H", info.Mode == BindActivationMode.Hold))   SetBindMode(info, BindActivationMode.Hold);
            if (DrawModeMiniPill("R", info.Mode == BindActivationMode.Released)) SetBindMode(info, BindActivationMode.Released);
        }
        else
        {
            GUILayout.Label("<color=#7D8590>toggle</color>", cachedLabelStyle, GUILayout.Width(60f));
        }

        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(2);
    }

    private bool DrawModeMiniPill(string label, bool isActive)
    {
        GUIStyle style = isActive ? cachedTabButtonActiveStyle : cachedTabButtonStyle;
        return GUILayout.Button(label, style, GUILayout.Height(24f), GUILayout.Width(26f));
    }

    private bool DrawHitboxChip(string label, bool isActive)
    {
        GUIStyle style = isActive ? cachedTabButtonActiveStyle : cachedTabButtonStyle;
        return GUILayout.Button(label, style, GUILayout.Height(26f), GUILayout.Width(72f));
    }

    private void SetBindMode(BindInfo info, BindActivationMode mode)
    {
        if (info.Mode == mode) return;
        info.Mode = mode;
        info.StateOwnedByBind = mode != BindActivationMode.Toggle;
        info.LastDerivedState = false;
    }
}
