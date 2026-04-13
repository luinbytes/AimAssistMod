using System;
using System.Reflection;
using System.Text;
using MelonLoader;
using UnityEngine;

public partial class MimiMod
{
    // ── Wind compensation (D2 graft, E2 rewrite) ───────────────────────────────
    //
    // Full audit of the game's wind API from GameAssembly.dll strings. What we
    // know for sure about Super Battle Golf's WindManager (class name "WindManager"):
    //
    //   Source of truth (Mirror SyncVars):
    //     get_NetworkcurrentWindAngle    → float (degrees, 0 = north?)
    //     get_NetworkcurrentWindSpeed    → float (m/s or game units)
    //
    //   Exposed helpers (what most callers read):
    //     get_CurrentWindDirection       → Vector3 (unit vector derived from angle)
    //     get_CurrentWindSpeed           → float
    //     get_Wind                       → likely the already-scaled Vector3
    //                                      (CurrentWindDirection * CurrentWindSpeed)
    //
    //   Multipliers (per-match or per-course):
    //     get_CrossWindFactor            → float (applied to the cross-wind component)
    //     get_WindFactor                 → float (per-hittable wind response factor)
    //
    //   Event:
    //     add_WindUpdated                → fires whenever wind changes
    //
    // Ball-side: WindHittableSettings + ShouldApplyWind gate per-hittable. We
    // don't need to touch those — they only govern whether the *game* applies
    // wind, not whether our prediction should. We always predict wind effects.
    //
    // Why our first attempt failed: we used a hardcoded WIND_COEFF=0.08f that
    // was tuned for our old custom simulator, not Mimi's forward sim. Mimi's
    // sim integrates velocity explicitly with gravity + drag — wind needs to
    // be scaled to match the game's actual wind-to-velocity conversion.
    //
    // Fix: read a user-tunable windStrength coefficient from config + the
    // settings GUI, and multiply it by the game-provided wind vector. The
    // user can adjust it live until predicted and actual shots agree. Default
    // value (1.0f) is high enough to be clearly visible.

    private bool windReflectionInitialized;
    private Type cachedWindManagerType;
    private Component cachedWindManagerInstance;
    private PropertyInfo cachedWindVectorProperty;           // "Wind" — preferred, already scaled
    private PropertyInfo cachedWindDirectionProperty;        // "CurrentWindDirection"
    private PropertyInfo cachedWindSpeedProperty;            // "CurrentWindSpeed"
    private PropertyInfo cachedWindNetworkAngleProperty;     // "NetworkcurrentWindAngle"
    private PropertyInfo cachedWindNetworkSpeedProperty;     // "NetworkcurrentWindSpeed"
    private Vector3 cachedWindVector = Vector3.zero;
    private float nextWindRefreshTime;
    private readonly float windCacheRefreshInterval = 0.2f;
    private string windDiagnosticReadout = "(wind: not initialized)";

    // E8 — per-ball wind factors reflected from HittableSettings.Wind
    // (WindHittableSettings.WindFactor, .CrossWindFactor). These are the exact
    // multipliers the game uses in Hittable.ApplyAirDamping.
    private bool ballWindSettingsInitialized;
    private Component cachedBallHittableComponent;
    private FieldInfo cachedHittableSettingsField;          // Hittable.settings
    private PropertyInfo cachedHittableSettingsWindProperty; // HittableSettings.Wind
    private PropertyInfo cachedWindFactorProperty;          // WindHittableSettings.WindFactor
    private PropertyInfo cachedCrossWindFactorProperty;     // WindHittableSettings.CrossWindFactor
    private float cachedBallWindFactor = 1f;
    private float cachedBallCrossWindFactor = 1f;

    private void EnsureWindReflectionInitialized()
    {
        if (windReflectionInitialized)
        {
            return;
        }
        windReflectionInitialized = true;

        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType("WindManager");
                if (t != null)
                {
                    cachedWindManagerType = t;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MimiMod] Wind: assembly scan failed: {ex.GetType().Name}");
        }

        if (cachedWindManagerType == null)
        {
            MelonLogger.Warning("[MimiMod] Wind: WindManager type not found in any loaded assembly. Wind prediction disabled.");
            windDiagnosticReadout = "(WindManager type missing)";
            return;
        }

        // Resolve every candidate property via cascade — any one can populate
        // the wind vector, we pick the best available at read time.
        cachedWindVectorProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "Wind",
            "Wind");

        cachedWindDirectionProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "CurrentWindDirection",
            "CurrentWindDirection", "WindDirection", "Direction");

        cachedWindSpeedProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "CurrentWindSpeed",
            "CurrentWindSpeed", "WindSpeed", "Speed", "Magnitude");

        cachedWindNetworkAngleProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "NetworkcurrentWindAngle",
            "NetworkcurrentWindAngle");

        cachedWindNetworkSpeedProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "NetworkcurrentWindSpeed",
            "NetworkcurrentWindSpeed");

        LogWindApiSummary();
    }

    private void LogWindApiSummary()
    {
        try
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("[MimiMod] Wind API resolved: ");
            sb.Append("Wind=").Append(cachedWindVectorProperty != null ? "Y" : "n");
            sb.Append(" CurDir=").Append(cachedWindDirectionProperty != null ? "Y" : "n");
            sb.Append(" CurSpd=").Append(cachedWindSpeedProperty != null ? "Y" : "n");
            sb.Append(" NetAng=").Append(cachedWindNetworkAngleProperty != null ? "Y" : "n");
            sb.Append(" NetSpd=").Append(cachedWindNetworkSpeedProperty != null ? "Y" : "n");
            MelonLogger.Msg(sb.ToString());
        }
        catch
        {
        }
    }

    private Vector3 GetCachedWindVector()
    {
        EnsureWindReflectionInitialized();

        float currentTime = Time.time;
        if (currentTime < nextWindRefreshTime && cachedWindManagerInstance != null)
        {
            return cachedWindVector;
        }
        nextWindRefreshTime = currentTime + windCacheRefreshInterval;

        if (cachedWindManagerType == null)
        {
            cachedWindVector = Vector3.zero;
            windDiagnosticReadout = "(no WindManager type)";
            return cachedWindVector;
        }

        if (cachedWindManagerInstance == null || cachedWindManagerInstance.gameObject == null)
        {
            cachedWindManagerInstance = ResolveWindManagerInstance();
            if (cachedWindManagerInstance == null)
            {
                cachedWindVector = Vector3.zero;
                windDiagnosticReadout = "(WindManager instance not in scene)";
                return cachedWindVector;
            }
        }

        try
        {
            Vector3 rawWind = Vector3.zero;
            bool readSucceeded = false;
            string readSource = "-";

            // PREFERRED: WindManager.Wind property — returns the internal
            // windVelocity field, which is what Hittable.ApplyAirDamping feeds
            // into its physics. Using it directly eliminates any rounding error
            // from re-normalizing CurrentWindDirection. Confirmed by decompiling
            // UpdateWind: windVelocity = currentWindDirection * (float)currentWindSpeed
            // and get_Wind returns that field unchanged.
            if (cachedWindVectorProperty != null)
            {
                object windValue = cachedWindVectorProperty.GetValue(cachedWindManagerInstance, null);
                if (windValue is Vector3 wv)
                {
                    rawWind = wv;
                    readSucceeded = true;
                    readSource = "Wind";
                }
            }

            // Fallback: CurrentWindDirection * CurrentWindSpeed (may introduce
            // a tiny magnitude error from re-normalization).
            if (!readSucceeded && cachedWindDirectionProperty != null && cachedWindSpeedProperty != null)
            {
                object dirValue = cachedWindDirectionProperty.GetValue(cachedWindManagerInstance, null);
                object speedValue = cachedWindSpeedProperty.GetValue(cachedWindManagerInstance, null);
                Vector3 dir = dirValue is Vector3 dv ? dv : Vector3.zero;
                float spd = ConvertToFloat(speedValue);
                rawWind = dir * spd;
                readSucceeded = true;
                readSource = "Dir*Spd";
            }

            // Last resort: build from NetworkcurrentWindAngle + NetworkcurrentWindSpeed.
            if (!readSucceeded && cachedWindNetworkAngleProperty != null && cachedWindNetworkSpeedProperty != null)
            {
                object angleValue = cachedWindNetworkAngleProperty.GetValue(cachedWindManagerInstance, null);
                object speedValue = cachedWindNetworkSpeedProperty.GetValue(cachedWindManagerInstance, null);
                float angleDeg = ConvertToFloat(angleValue);
                float spd = ConvertToFloat(speedValue);
                Quaternion q = Quaternion.Euler(0f, angleDeg, 0f);
                rawWind = (q * Vector3.forward) * spd;
                readSucceeded = true;
                readSource = "Net";
            }

            if (!readSucceeded)
            {
                windDiagnosticReadout = "(no readable wind property)";
                cachedWindVector = Vector3.zero;
                return cachedWindVector;
            }

            cachedWindVector = rawWind;
            windDiagnosticReadout = $"src={readSource} v=({rawWind.x:F2},{rawWind.z:F2}) mag={rawWind.magnitude:F2} wf={cachedBallWindFactor:F2}/cwf={cachedBallCrossWindFactor:F2}";
        }
        catch (Exception ex)
        {
            windDiagnosticReadout = $"(exception: {ex.GetType().Name})";
            cachedWindVector = Vector3.zero;
        }

        return cachedWindVector;
    }

    private static float ConvertToFloat(object value)
    {
        if (value is float f) return f;
        if (value is double d) return (float)d;
        if (value is int i) return i;
        return 0f;
    }

    private Component ResolveWindManagerInstance()
    {
        if (cachedWindManagerType == null)
        {
            return null;
        }

        try
        {
            // Try FindFirstObjectByType first.
            UnityEngine.Object obj = UnityEngine.Object.FindFirstObjectByType(cachedWindManagerType);
            if (obj != null)
            {
                return obj as Component;
            }

            // Fallback: include inactive objects in the search.
            UnityEngine.Object[] allInstances = UnityEngine.Object.FindObjectsByType(
                cachedWindManagerType,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (allInstances != null && allInstances.Length > 0)
            {
                return allInstances[0] as Component;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MimiMod] Wind: FindObjectsByType failed: {ex.GetType().Name}");
        }

        return null;
    }

    /// <summary>
    /// Human-readable current wind state for the settings GUI.
    /// </summary>
    internal string GetWindDiagnosticReadout()
    {
        return windDiagnosticReadout;
    }

    /// <summary>
    /// Read WindFactor and CrossWindFactor from the current ball's HittableSettings.Wind.
    /// Matches the exact values the game uses in Hittable.ApplyAirDamping. Cached until
    /// the golfBall reference changes.
    /// </summary>
    internal void RefreshBallWindFactors()
    {
        if (golfBall == null)
        {
            return;
        }

        if (ballWindSettingsInitialized && ReferenceEquals(cachedBallHittableComponent?.gameObject, golfBall.gameObject))
        {
            return;
        }

        ballWindSettingsInitialized = true;
        cachedBallWindFactor = 1f;
        cachedBallCrossWindFactor = 1f;

        try
        {
            // The Hittable component lives on the same GameObject as GolfBall.
            Component[] all = golfBall.gameObject.GetComponents<Component>();
            Component hittable = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].GetType().Name == "Hittable")
                {
                    hittable = all[i];
                    break;
                }
            }
            if (hittable == null)
            {
                MelonLogger.Msg("[MimiMod] Wind: Hittable component not found on golfBall");
                return;
            }
            cachedBallHittableComponent = hittable;

            Type hittableType = hittable.GetType();
            if (cachedHittableSettingsField == null)
            {
                cachedHittableSettingsField = ModReflectionHelper.GetFieldCascade(
                    hittableType, "settings", "settings");
            }
            if (cachedHittableSettingsField == null) return;

            object settings = cachedHittableSettingsField.GetValue(hittable);
            if (settings == null) return;

            Type settingsType = settings.GetType();
            if (cachedHittableSettingsWindProperty == null)
            {
                cachedHittableSettingsWindProperty = ModReflectionHelper.GetPropertyCascade(
                    settingsType, "Wind", "Wind");
            }
            if (cachedHittableSettingsWindProperty == null) return;

            object windSettings = cachedHittableSettingsWindProperty.GetValue(settings, null);
            if (windSettings == null) return;

            Type windSettingsType = windSettings.GetType();
            if (cachedWindFactorProperty == null)
            {
                cachedWindFactorProperty = ModReflectionHelper.GetPropertyCascade(
                    windSettingsType, "WindFactor", "WindFactor");
            }
            if (cachedCrossWindFactorProperty == null)
            {
                cachedCrossWindFactorProperty = ModReflectionHelper.GetPropertyCascade(
                    windSettingsType, "CrossWindFactor", "CrossWindFactor");
            }

            if (cachedWindFactorProperty != null)
            {
                object v = cachedWindFactorProperty.GetValue(windSettings, null);
                if (v is float f) cachedBallWindFactor = f;
            }
            if (cachedCrossWindFactorProperty != null)
            {
                object v = cachedCrossWindFactorProperty.GetValue(windSettings, null);
                if (v is float f) cachedBallCrossWindFactor = f;
            }

            MelonLogger.Msg($"[MimiMod] Ball wind factors: WindFactor={cachedBallWindFactor:F3}, CrossWindFactor={cachedBallCrossWindFactor:F3}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[MimiMod] RefreshBallWindFactors failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal float GetBallWindFactor() => cachedBallWindFactor;
    internal float GetBallCrossWindFactor() => cachedBallCrossWindFactor;
}
