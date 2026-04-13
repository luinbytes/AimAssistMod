using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class MimiMod
{
    // ── In-game clickable settings GUI (E1 graft) ──────────────────────────────
    //
    // Unity IMGUI (OnGUI) window toggled by settingsGuiKey (default F8). Edits
    // are applied live to the in-memory config; clicking "Save" calls
    // SaveConfigToFile in MimiMod.Config.cs to persist to disk.
    //
    // Uses IMGUI instead of extending Mimi's Canvas/TMP HUD because:
    //   1. IMGUI is built-in — no extra UI components to instantiate
    //   2. GUI.Window is draggable for free
    //   3. GUILayout.Toggle / HorizontalSlider are clickable out of the box
    //   4. No interference with Mimi's existing per-frame trail/hud logic
    //
    // The existing 4-region TMP HUD stays — this window opens on top.

    private Rect settingsWindowRect = new Rect(40f, 40f, 360f, 560f);
    private Vector2 settingsScrollPosition = Vector2.zero;
    private GUIStyle cachedWindowStyle;
    private GUIStyle cachedLabelStyle;
    private GUIStyle cachedHeaderStyle;
    private GUIStyle cachedButtonStyle;
    private GUIStyle cachedToggleStyle;
    private bool settingsGuiStylesDirty = true;

    /// <summary>
    /// Called every frame from OnUpdate to poll the toggle key. Cheap —
    /// just reads Keyboard.current once per frame.
    /// </summary>
    private void UpdateSettingsGuiHotkey()
    {
        if (WasConfiguredKeyPressed(settingsGuiKey))
        {
            settingsGuiVisible = !settingsGuiVisible;
        }
    }

    public override void OnGUI()
    {
        if (!settingsGuiVisible)
        {
            return;
        }

        EnsureSettingsGuiStyles();

        settingsWindowRect = GUI.Window(
            unchecked((int)0x4D494D49), // 'MIMI'
            settingsWindowRect,
            DrawSettingsWindow,
            "Mimi Mod — Settings",
            cachedWindowStyle);
    }

    private void EnsureSettingsGuiStyles()
    {
        if (!settingsGuiStylesDirty && cachedWindowStyle != null)
        {
            return;
        }
        settingsGuiStylesDirty = false;

        cachedWindowStyle = new GUIStyle(GUI.skin.window)
        {
            padding = new RectOffset(12, 12, 22, 12),
            fontSize = 13,
            fontStyle = FontStyle.Bold,
        };
        cachedWindowStyle.normal.textColor = Color.white;
        cachedWindowStyle.onNormal.textColor = Color.white;

        cachedLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            wordWrap = true,
        };
        cachedLabelStyle.normal.textColor = new Color(0.92f, 0.92f, 0.92f);

        cachedHeaderStyle = new GUIStyle(cachedLabelStyle)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            margin = new RectOffset(0, 0, 10, 4),
        };
        cachedHeaderStyle.normal.textColor = new Color(0.55f, 0.85f, 1f);

        cachedButtonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 12,
            padding = new RectOffset(10, 10, 6, 6),
        };

        cachedToggleStyle = new GUIStyle(GUI.skin.toggle)
        {
            fontSize = 12,
            margin = new RectOffset(0, 0, 2, 2),
        };
        cachedToggleStyle.normal.textColor = Color.white;
        cachedToggleStyle.onNormal.textColor = Color.white;
        cachedToggleStyle.hover.textColor = Color.white;
        cachedToggleStyle.onHover.textColor = Color.white;
    }

    private void DrawSettingsWindow(int id)
    {
        settingsScrollPosition = GUILayout.BeginScrollView(settingsScrollPosition);

        GUILayout.Label($"Mimi Mod v1.0.1 + grafts    [{settingsGuiKeyLabel}] to toggle", cachedLabelStyle);
        GUILayout.Space(4);

        // ── Accuracy section ────────────────────────────────────────────────
        GUILayout.Label("ACCURACY", cachedHeaderStyle);

        bool newAllowOvercharge = GUILayout.Toggle(
            allowOvercharge,
            " Allow 115% overcharge when out of range  (unchecked = clamp at 100%)",
            cachedToggleStyle);
        if (newAllowOvercharge != allowOvercharge)
        {
            allowOvercharge = newAllowOvercharge;
        }

        bool newInstaHit = GUILayout.Toggle(
            instaHitEnabled,
            " Insta-hit  (unchecked = hold LMB to charge manually, mod releases at optimal)",
            cachedToggleStyle);
        if (newInstaHit != instaHitEnabled)
        {
            instaHitEnabled = newInstaHit;
            ResetChargeState();
        }

        GUILayout.Space(6);

        // ── Wind tuning section ─────────────────────────────────────────────
        GUILayout.Label("WIND PREDICTION", cachedHeaderStyle);

        GUILayout.Label($"Wind strength: {windStrength:F4}   (0 = ignore wind, dial until prediction matches actual)", cachedLabelStyle);
        float newWindStrength = GUILayout.HorizontalSlider(windStrength, 0f, 0.05f);
        if (Mathf.Abs(newWindStrength - windStrength) > 0.00005f)
        {
            windStrength = Mathf.Round(newWindStrength * 10000f) / 10000f;
            nextPredictedPathRefreshTime = 0f;
        }

        // Zero out button for quick "no wind prediction" comparison
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Wind = 0 (disable)", cachedButtonStyle))
        {
            windStrength = 0f;
            nextPredictedPathRefreshTime = 0f;
        }
        if (GUILayout.Button("Wind = 0.01 (default)", cachedButtonStyle))
        {
            windStrength = 0.01f;
            nextPredictedPathRefreshTime = 0f;
        }
        GUILayout.EndHorizontal();

        GUILayout.Label("Live wind: " + GetWindDiagnosticReadout(), cachedLabelStyle);

        GUILayout.Space(6);

        // ── Visuals section ─────────────────────────────────────────────────
        GUILayout.Label("TRAILS & PREVIEW", cachedHeaderStyle);

        bool newPredictedTrail = GUILayout.Toggle(predictedTrailEnabled, " Predicted trajectory line", cachedToggleStyle);
        if (newPredictedTrail != predictedTrailEnabled)
        {
            predictedTrailEnabled = newPredictedTrail;
            MarkTrailVisualSettingsDirty();
        }

        bool newFrozenTrail = GUILayout.Toggle(frozenTrailEnabled, " Frozen predicted (freezes on release)", cachedToggleStyle);
        if (newFrozenTrail != frozenTrailEnabled)
        {
            frozenTrailEnabled = newFrozenTrail;
            MarkTrailVisualSettingsDirty();
        }

        bool newActualTrail = GUILayout.Toggle(actualTrailEnabled, " Actual shot trail", cachedToggleStyle);
        if (newActualTrail != actualTrailEnabled)
        {
            actualTrailEnabled = newActualTrail;
            MarkTrailVisualSettingsDirty();
        }

        bool newImpactPreview = GUILayout.Toggle(impactPreviewEnabled, " Impact preview window", cachedToggleStyle);
        if (newImpactPreview != impactPreviewEnabled)
        {
            impactPreviewEnabled = newImpactPreview;
            nextImpactPreviewRenderTime = 0f;
        }

        GUILayout.Space(6);

        // ── Impact preview sliders ──────────────────────────────────────────
        GUILayout.Label($"Impact preview FPS: {impactPreviewTargetFps:F0}", cachedLabelStyle);
        float newFps = GUILayout.HorizontalSlider(impactPreviewTargetFps, 10f, 144f);
        if (Mathf.Abs(newFps - impactPreviewTargetFps) > 0.5f)
        {
            impactPreviewTargetFps = Mathf.Round(newFps);
            nextImpactPreviewRenderTime = 0f;
        }

        GUILayout.Label($"Impact preview size: {impactPreviewTextureWidth}x{impactPreviewTextureHeight}", cachedLabelStyle);
        float newWidth = GUILayout.HorizontalSlider(impactPreviewTextureWidth, 320f, 1920f);
        int snappedWidth = Mathf.Clamp(Mathf.RoundToInt(newWidth / 32f) * 32, 320, 1920);
        if (snappedWidth != impactPreviewTextureWidth)
        {
            impactPreviewTextureWidth = snappedWidth;
            impactPreviewTextureHeight = Mathf.RoundToInt(snappedWidth * 9f / 16f);
            nextImpactPreviewRenderTime = 0f;
        }

        GUILayout.Space(6);

        // ── Status readout ──────────────────────────────────────────────────
        GUILayout.Label("STATUS", cachedHeaderStyle);
        GUILayout.Label($"Assist: {(assistEnabled ? "ON" : "OFF")}   ({assistToggleKeyLabel} to toggle)", cachedLabelStyle);
        GUILayout.Label($"Player: {(playerFound ? "found" : "searching")}", cachedLabelStyle);
        GUILayout.Label($"Ball source: {lastBallResolveSource}", cachedLabelStyle);
        if (holePosition != Vector3.zero && playerMovement != null)
        {
            float dist = Vector3.Distance(playerMovement.transform.position, holePosition);
            GUILayout.Label($"Hole distance: {dist:F1}m", cachedLabelStyle);
        }
        GUILayout.Label($"Ideal power: {idealSwingPower * 100f:F0}%   pitch: {idealSwingPitch:F1}°", cachedLabelStyle);

        GUILayout.Space(10);

        // ── Action buttons ──────────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save to config.cfg", cachedButtonStyle, GUILayout.Height(28f)))
        {
            SaveConfigToFile();
        }
        if (GUILayout.Button("Reload from file", cachedButtonStyle, GUILayout.Height(28f)))
        {
            LoadOrCreateConfig();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(4);
        if (GUILayout.Button("Close", cachedButtonStyle, GUILayout.Height(24f)))
        {
            settingsGuiVisible = false;
        }

        GUILayout.EndScrollView();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }
}
