using System;
using System.Reflection;
using UnityEngine;

public partial class MimiMod
{
    // ── Wind compensation (D2 graft) ───────────────────────────────────────────
    //
    // Mimi's forward-sim trajectory and impact preview assume zero wind. Super
    // Battle Golf exposes wind via a `WindManager` component with two public
    // accessors we care about:
    //     Vector3 CurrentWindDirection   (unit vector, may be zero)
    //     float   CurrentWindSpeed       (m/s or game units, game-tuned)
    //
    // We resolve both via ModReflectionHelper cascades so the mod survives
    // renames. The WindManager instance is lazy-found and re-resolved if it
    // gets destroyed between holes. The vector is cached for 0.5s so the
    // predicted trajectory refresh rate doesn't re-reflect each frame.
    //
    // The result is a Vector3 (dir.normalized * speed) applied as a lateral
    // force in the trajectory integrator — see WIND_COEFF in MimiMod.Trajectory.cs.
    // Tune WIND_COEFF if the visual curve mismatches reality.

    private bool windReflectionInitialized;
    private Type cachedWindManagerType;
    private Component cachedWindManagerInstance;
    private PropertyInfo cachedWindDirectionProperty;
    private PropertyInfo cachedWindSpeedProperty;
    private Vector3 cachedWindVector = Vector3.zero;
    private float nextWindRefreshTime;
    private readonly float windCacheRefreshInterval = 0.5f;

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
        catch
        {
        }

        if (cachedWindManagerType == null)
        {
            return;
        }

        cachedWindDirectionProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "WindDirection",
            "CurrentWindDirection", "WindDirection", "Direction", "direction");

        cachedWindSpeedProperty = ModReflectionHelper.GetPropertyCascade(
            cachedWindManagerType,
            "WindSpeed",
            "CurrentWindSpeed", "WindSpeed", "Speed", "speed", "Magnitude");
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
            return cachedWindVector;
        }

        if (cachedWindManagerInstance == null || cachedWindManagerInstance.gameObject == null)
        {
            cachedWindManagerInstance = ResolveWindManagerInstance();
            if (cachedWindManagerInstance == null)
            {
                cachedWindVector = Vector3.zero;
                return cachedWindVector;
            }
        }

        try
        {
            Vector3 direction = Vector3.zero;
            float speed = 0f;

            if (cachedWindDirectionProperty != null)
            {
                object dirValue = cachedWindDirectionProperty.GetValue(cachedWindManagerInstance, null);
                if (dirValue is Vector3 dv)
                {
                    direction = dv;
                }
            }

            if (cachedWindSpeedProperty != null)
            {
                object speedValue = cachedWindSpeedProperty.GetValue(cachedWindManagerInstance, null);
                if (speedValue is float sv)
                {
                    speed = sv;
                }
                else if (speedValue is double dsv)
                {
                    speed = (float)dsv;
                }
                else if (speedValue is int isv)
                {
                    speed = isv;
                }
            }

            cachedWindVector = (direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.zero) * speed;
        }
        catch
        {
            cachedWindVector = Vector3.zero;
        }

        return cachedWindVector;
    }

    private Component ResolveWindManagerInstance()
    {
        if (cachedWindManagerType == null)
        {
            return null;
        }

        try
        {
            UnityEngine.Object obj = UnityEngine.Object.FindFirstObjectByType(cachedWindManagerType);
            return obj as Component;
        }
        catch
        {
            return null;
        }
    }
}
