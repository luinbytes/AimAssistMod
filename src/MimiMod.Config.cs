using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class MimiMod
{
    private void LoadOrCreateConfig()
    {
        ApplyDefaultConfig();

        try
        {
            string configDirectory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }

            if (!File.Exists(configPath))
            {
                File.WriteAllText(configPath, BuildDefaultConfigText(), Encoding.ASCII);
            }
            else
            {
                LoadConfigFromFile(configPath);
            }
        }
        catch
        {
        }

        UpdateConfigLabels();
        MarkHudDirty();
        MarkTrailVisualSettingsDirty();
        nextImpactPreviewRenderTime = 0f;
    }

    private void ApplyDefaultConfig()
    {
        assistToggleKeyName = "F";
        coffeeBoostKeyName = "F2";
        nearestBallModeKeyName = "F3";
        unlockAllCosmeticsKeyName = "F4";
        settingsGuiKeyName = "F8";
        allowOvercharge = false;
        instaHitEnabled = false;
        windStrength = 0.01f;
        actualTrailEnabled = true;
        predictedTrailEnabled = true;
        frozenTrailEnabled = true;
        impactPreviewEnabled = true;
        impactPreviewTargetFps = impactPreviewAutoTargetFps;
        impactPreviewTextureWidth = 640;
        impactPreviewTextureHeight = 360;
        actualTrailStartWidth = 0.22f;
        actualTrailEndWidth = 0.18f;
        predictedTrailStartWidth = 0.18f;
        predictedTrailEndWidth = 0.14f;
        frozenTrailStartWidth = 0.20f;
        frozenTrailEndWidth = 0.16f;
        actualTrailColor = new Color(1f, 0.58f, 0.20f, 1f);
        predictedTrailColor = new Color(0.36f, 0.95f, 0.46f, 0.95f);
        frozenTrailColor = new Color(0.36f, 0.74f, 1f, 0.92f);
    }

    private void LoadConfigFromFile(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string rawLine = lines[i];
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            string line = rawLine.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
            {
                continue;
            }

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line.Substring(0, separatorIndex).Trim().ToLowerInvariant();
            string value = line.Substring(separatorIndex + 1).Trim();

            switch (key)
            {
                case "assist_toggle_key":
                    assistToggleKeyName = ParseKeyNameOrDefault(value, assistToggleKeyName);
                    break;
                case "coffee_boost_key":
                    coffeeBoostKeyName = ParseKeyNameOrDefault(value, coffeeBoostKeyName);
                    break;
                case "nearest_ball_mode_key":
                    nearestBallModeKeyName = ParseKeyNameOrDefault(value, nearestBallModeKeyName);
                    break;
                case "unlock_all_cosmetics_key":
                    unlockAllCosmeticsKeyName = ParseKeyNameOrDefault(value, unlockAllCosmeticsKeyName);
                    break;
                case "settings_gui_key":
                    settingsGuiKeyName = ParseKeyNameOrDefault(value, settingsGuiKeyName);
                    break;
                case "allow_overcharge":
                    allowOvercharge = ParseBoolOrDefault(value, allowOvercharge);
                    break;
                case "insta_hit_enabled":
                    instaHitEnabled = ParseBoolOrDefault(value, instaHitEnabled);
                    break;
                case "wind_strength":
                    windStrength = ParseFloatOrDefault(value, windStrength, 0f, 0.5f);
                    break;
                case "actual_trail_enabled":
                    actualTrailEnabled = ParseBoolOrDefault(value, actualTrailEnabled);
                    break;
                case "predicted_trail_enabled":
                    predictedTrailEnabled = ParseBoolOrDefault(value, predictedTrailEnabled);
                    break;
                case "frozen_trail_enabled":
                    frozenTrailEnabled = ParseBoolOrDefault(value, frozenTrailEnabled);
                    break;
                case "impact_preview_enabled":
                    impactPreviewEnabled = ParseBoolOrDefault(value, impactPreviewEnabled);
                    break;
                case "impact_preview_fps":
                    impactPreviewTargetFps = ParseFloatOrDefault(value, impactPreviewTargetFps, 0f, 360f);
                    break;
                case "impact_preview_width":
                    impactPreviewTextureWidth = ParseIntOrDefault(value, impactPreviewTextureWidth, 320, 3840);
                    break;
                case "impact_preview_height":
                    impactPreviewTextureHeight = ParseIntOrDefault(value, impactPreviewTextureHeight, 180, 2160);
                    break;
                case "actual_trail_start_width":
                    actualTrailStartWidth = ParseFloatOrDefault(value, actualTrailStartWidth, 0.005f, 1f);
                    break;
                case "actual_trail_end_width":
                    actualTrailEndWidth = ParseFloatOrDefault(value, actualTrailEndWidth, 0.005f, 1f);
                    break;
                case "predicted_trail_start_width":
                    predictedTrailStartWidth = ParseFloatOrDefault(value, predictedTrailStartWidth, 0.005f, 1f);
                    break;
                case "predicted_trail_end_width":
                    predictedTrailEndWidth = ParseFloatOrDefault(value, predictedTrailEndWidth, 0.005f, 1f);
                    break;
                case "frozen_trail_start_width":
                    frozenTrailStartWidth = ParseFloatOrDefault(value, frozenTrailStartWidth, 0.005f, 1f);
                    break;
                case "frozen_trail_end_width":
                    frozenTrailEndWidth = ParseFloatOrDefault(value, frozenTrailEndWidth, 0.005f, 1f);
                    break;
                case "actual_trail_color":
                    actualTrailColor = ParseColorOrDefault(value, actualTrailColor);
                    break;
                case "predicted_trail_color":
                    predictedTrailColor = ParseColorOrDefault(value, predictedTrailColor);
                    break;
                case "frozen_trail_color":
                    frozenTrailColor = ParseColorOrDefault(value, frozenTrailColor);
                    break;
            }
        }
    }

    private string BuildDefaultConfigText()
    {
        return BuildConfigTextFromCurrentState(true);
    }

    private string BuildConfigTextFromCurrentState(bool includeComments)
    {
        StringBuilder builder = new StringBuilder(768);
        if (includeComments)
        {
            builder.AppendLine("# Mimi Mod config");
            builder.AppendLine("# Edits via the in-game settings GUI (default F8) persist back here.");
            builder.AppendLine();
        }
        builder.AppendLine("assist_toggle_key=" + assistToggleKeyName);
        builder.AppendLine("coffee_boost_key=" + coffeeBoostKeyName);
        builder.AppendLine("nearest_ball_mode_key=" + nearestBallModeKeyName);
        builder.AppendLine("unlock_all_cosmetics_key=" + unlockAllCosmeticsKeyName);
        builder.AppendLine("settings_gui_key=" + settingsGuiKeyName);
        builder.AppendLine();
        if (includeComments)
        {
            builder.AppendLine("# Clamp auto-fire at 100% (false) or allow the game's 115% overcharge (true).");
            builder.AppendLine("# Overcharged shots are wildly inaccurate, so false is the recommended default.");
        }
        builder.AppendLine("allow_overcharge=" + (allowOvercharge ? "true" : "false"));
        if (includeComments)
        {
            builder.AppendLine();
            builder.AppendLine("# insta_hit_enabled=true  → mod auto-starts the charge the moment you hold LMB (vanilla Mimi).");
            builder.AppendLine("# insta_hit_enabled=false → you hold LMB and charge manually, the mod only releases at the optimal power.");
        }
        builder.AppendLine("insta_hit_enabled=" + (instaHitEnabled ? "true" : "false"));
        if (includeComments)
        {
            builder.AppendLine();
            builder.AppendLine("# Wind strength multiplier for the predicted trajectory (0 = ignore wind, 0.04 default).");
            builder.AppendLine("# Dial it live from the settings GUI slider until the predicted curve matches actual shots.");
        }
        builder.AppendLine("wind_strength=" + windStrength.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine();
        builder.AppendLine("actual_trail_enabled=" + (actualTrailEnabled ? "true" : "false"));
        builder.AppendLine("actual_trail_start_width=" + actualTrailStartWidth.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("actual_trail_end_width=" + actualTrailEndWidth.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("actual_trail_color=" + ColorToHex(actualTrailColor));
        builder.AppendLine();
        builder.AppendLine("predicted_trail_enabled=" + (predictedTrailEnabled ? "true" : "false"));
        builder.AppendLine("predicted_trail_start_width=" + predictedTrailStartWidth.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("predicted_trail_end_width=" + predictedTrailEndWidth.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("predicted_trail_color=" + ColorToHex(predictedTrailColor));
        builder.AppendLine();
        builder.AppendLine("frozen_trail_enabled=" + (frozenTrailEnabled ? "true" : "false"));
        builder.AppendLine("frozen_trail_start_width=" + frozenTrailStartWidth.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("frozen_trail_end_width=" + frozenTrailEndWidth.ToString("0.###", CultureInfo.InvariantCulture));
        builder.AppendLine("frozen_trail_color=" + ColorToHex(frozenTrailColor));
        builder.AppendLine();
        builder.AppendLine("impact_preview_enabled=" + (impactPreviewEnabled ? "true" : "false"));
        builder.AppendLine("impact_preview_fps=" + impactPreviewTargetFps.ToString("0.#", CultureInfo.InvariantCulture));
        builder.AppendLine("impact_preview_width=" + impactPreviewTextureWidth.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("impact_preview_height=" + impactPreviewTextureHeight.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static string ColorToHex(Color color)
    {
        int r = Mathf.Clamp(Mathf.RoundToInt(color.r * 255f), 0, 255);
        int g = Mathf.Clamp(Mathf.RoundToInt(color.g * 255f), 0, 255);
        int b = Mathf.Clamp(Mathf.RoundToInt(color.b * 255f), 0, 255);
        return "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
    }

    /// <summary>
    /// Persist current in-memory config state back to the config file.
    /// Called by the settings GUI Save button.
    /// </summary>
    internal void SaveConfigToFile()
    {
        try
        {
            string configDirectory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(configDirectory))
            {
                Directory.CreateDirectory(configDirectory);
            }
            File.WriteAllText(configPath, BuildConfigTextFromCurrentState(true), Encoding.ASCII);
            MelonLoader.MelonLogger.Msg($"[MimiMod] Config saved to {configPath}");
        }
        catch (Exception ex)
        {
            MelonLoader.MelonLogger.Warning($"[MimiMod] Config save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string ParseKeyNameOrDefault(string value, string fallbackValue)
    {
        string normalized = value == null ? "" : value.Trim();
        return string.IsNullOrEmpty(normalized) ? fallbackValue : normalized;
    }

    private bool ParseBoolOrDefault(string value, bool fallbackValue)
    {
        bool parsedBool;
        return bool.TryParse(value, out parsedBool) ? parsedBool : fallbackValue;
    }

    private float ParseFloatOrDefault(string value, float fallbackValue, float minValue, float maxValue)
    {
        float parsedFloat;
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedFloat))
        {
            return fallbackValue;
        }

        return Mathf.Clamp(parsedFloat, minValue, maxValue);
    }

    private int ParseIntOrDefault(string value, int fallbackValue, int minValue, int maxValue)
    {
        int parsedInt;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
        {
            return fallbackValue;
        }

        return Mathf.Clamp(parsedInt, minValue, maxValue);
    }

    private Color ParseColorOrDefault(string value, Color fallbackValue)
    {
        Color parsedColor;
        if (ColorUtility.TryParseHtmlString(value, out parsedColor))
        {
            return parsedColor;
        }

        string[] parts = value.Split(',');
        if (parts.Length >= 3 && parts.Length <= 4)
        {
            float[] values = new float[4] { fallbackValue.r, fallbackValue.g, fallbackValue.b, fallbackValue.a };
            for (int i = 0; i < parts.Length; i++)
            {
                float parsedValue;
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue))
                {
                    return fallbackValue;
                }

                values[i] = parsedValue > 1f ? Mathf.Clamp01(parsedValue / 255f) : Mathf.Clamp01(parsedValue);
            }

            return new Color(values[0], values[1], values[2], values[3]);
        }

        return fallbackValue;
    }

    private void UpdateConfigLabels()
    {
        assistToggleKey = ParseConfiguredKey(assistToggleKeyName, Key.F);
        coffeeBoostKey = ParseConfiguredKey(coffeeBoostKeyName, Key.F2);
        nearestBallModeKey = ParseConfiguredKey(nearestBallModeKeyName, Key.F3);
        unlockAllCosmeticsKey = ParseConfiguredKey(unlockAllCosmeticsKeyName, Key.F4);
        settingsGuiKey = ParseConfiguredKey(settingsGuiKeyName, Key.F8);
        assistToggleKeyLabel = FormatKeyLabel(assistToggleKeyName);
        coffeeBoostKeyLabel = FormatKeyLabel(coffeeBoostKeyName);
        nearestBallModeKeyLabel = FormatKeyLabel(nearestBallModeKeyName);
        unlockAllCosmeticsKeyLabel = FormatKeyLabel(unlockAllCosmeticsKeyName);
        settingsGuiKeyLabel = FormatKeyLabel(settingsGuiKeyName);
    }

    private Key ParseConfiguredKey(string configuredKeyName, Key fallbackKey)
    {
        if (string.IsNullOrWhiteSpace(configuredKeyName))
        {
            return fallbackKey;
        }

        Key parsedKey;
        return Enum.TryParse(configuredKeyName.Trim(), true, out parsedKey) && parsedKey != Key.None
            ? parsedKey
            : fallbackKey;
    }

    private string FormatKeyLabel(string configuredKeyName)
    {
        if (string.IsNullOrWhiteSpace(configuredKeyName))
        {
            return "?";
        }

        string keyName = configuredKeyName.Trim();
        Key parsedKey;
        if (Enum.TryParse(keyName, true, out parsedKey))
        {
            keyName = parsedKey.ToString();
        }

        if (keyName.StartsWith("Digit", StringComparison.OrdinalIgnoreCase))
        {
            return keyName.Substring("Digit".Length);
        }

        return keyName.ToUpperInvariant();
    }

    private bool WasConfiguredKeyPressed(Key configuredKey)
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || configuredKey == Key.None)
        {
            return false;
        }

        try
        {
            return keyboard[configuredKey] != null && keyboard[configuredKey].wasPressedThisFrame;
        }
        catch
        {
            return false;
        }
    }

    private Color BrightenColor(Color color, float factor)
    {
        return new Color(
            Mathf.Clamp01(color.r * factor),
            Mathf.Clamp01(color.g * factor),
            Mathf.Clamp01(color.b * factor),
            color.a);
    }

    private Color DarkenColor(Color color, float factor)
    {
        return new Color(
            Mathf.Clamp01(color.r * factor),
            Mathf.Clamp01(color.g * factor),
            Mathf.Clamp01(color.b * factor),
            color.a);
    }

    private Gradient CreateTrailGradient(Color baseColor, float startFactor, float midFactor, float endFactor, float startAlpha, float midAlpha, float endAlpha)
    {
        Color startColor = BrightenColor(baseColor, startFactor);
        Color midColor = BrightenColor(baseColor, midFactor);
        Color endColor = DarkenColor(baseColor, endFactor);

        Gradient gradient = new Gradient();
        gradient.colorKeys = new GradientColorKey[]
        {
            new GradientColorKey(startColor, 0f),
            new GradientColorKey(midColor, 0.55f),
            new GradientColorKey(endColor, 1f)
        };
        gradient.alphaKeys = new GradientAlphaKey[]
        {
            new GradientAlphaKey(Mathf.Clamp01(startAlpha), 0f),
            new GradientAlphaKey(Mathf.Clamp01(midAlpha), 0.6f),
            new GradientAlphaKey(Mathf.Clamp01(endAlpha), 1f)
        };
        return gradient;
    }

    private void ApplyTrailVisualSettings()
    {
        ApplyTrailVisualSettings(
            shotPathLine,
            shotPathMaterial,
            actualTrailEnabled,
            actualTrailStartWidth,
            actualTrailEndWidth,
            actualTrailColor,
            1.20f,
            1.00f,
            0.78f,
            0.96f,
            0.80f,
            0.62f);

        ApplyTrailVisualSettings(
            predictedPathLine,
            predictedPathMaterial,
            predictedTrailEnabled,
            predictedTrailStartWidth,
            predictedTrailEndWidth,
            predictedTrailColor,
            1.15f,
            1.00f,
            0.82f,
            0.94f,
            0.78f,
            0.55f);

        ApplyTrailVisualSettings(
            frozenPredictedPathLine,
            frozenPredictedPathMaterial,
            frozenTrailEnabled,
            frozenTrailStartWidth,
            frozenTrailEndWidth,
            frozenTrailColor,
            1.18f,
            1.00f,
            0.80f,
            0.92f,
            0.76f,
            0.52f);
    }

    private void ApplyTrailVisualSettings(
        LineRenderer lineRenderer,
        Material material,
        bool enabled,
        float startWidth,
        float endWidth,
        Color baseColor,
        float startFactor,
        float midFactor,
        float endFactor,
        float startAlpha,
        float midAlpha,
        float endAlpha)
    {
        if (lineRenderer == null)
        {
            return;
        }

        lineRenderer.startWidth = startWidth;
        lineRenderer.endWidth = endWidth;
        lineRenderer.enabled = enabled;
        lineRenderer.colorGradient = CreateTrailGradient(baseColor, startFactor, midFactor, endFactor, startAlpha, midAlpha, endAlpha);

        if (!enabled)
        {
            lineRenderer.positionCount = 0;
        }

        if (material != null)
        {
            material.color = baseColor;
        }
    }
}
