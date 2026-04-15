using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── Terrain + hole reflection ──────────────────────────────────────────────
    //
    // The background decomp agent discovered that Super Battle Golf COMPLETELY
    // BYPASSES Unity's PhysicsMaterial pipeline for ball-ground contact. The
    // game hooks `Physics.ContactModifyEvent` in `PhysicsManager.ModifyContactsInternal`
    // and overwrites every ball-ground contact's bounciness/friction every
    // physics step with values from `TerrainLayerSettings.Bounciness` of
    // whichever TerrainLayer is dominant at the contact point.
    //
    // So reading the ball's SphereCollider.sharedMaterial is useless. The
    // correct source is:
    //
    //     TerrainManager.Instance
    //       .GetDominantLayerSettingsAtPoint(contactPoint)
    //         → TerrainLayerSettings {
    //             Bounciness,
    //             DynamicFriction, StaticFriction,
    //             LinearDamping,
    //             FullStopMaxPitch, FullRollMinPitch,
    //             BallFullStopToFullRollCurve
    //           }
    //
    // Per-layer: Fairway, Green, Rough, Sand, DirtPath, Ice, etc. — 17 layers.
    //
    // Hole detection also reversed: GolfHoleTrigger.OnTriggerEnter fires on
    // simple collider overlap, no velocity/angle gate. The "ball is holed" volume
    // is the `ballTrigger` Collider on each GolfHoleTrigger. We reflect its
    // bounds to get hole positions + radii.

    private bool terrainReflectionInitialized;
    private object cachedTerrainManagerInstance;
    private MethodInfo cachedGetDominantLayerSettingsAtPoint;
    private PropertyInfo cachedTLSBounciness;
    private PropertyInfo cachedTLSDynamicFriction;
    private PropertyInfo cachedTLSStaticFriction;
    private PropertyInfo cachedTLSLinearDamping;
    private PropertyInfo cachedTLSFullStopMaxPitch;
    private PropertyInfo cachedTLSFullRollMinPitch;
    private PropertyInfo cachedTLSBallFullStopToFullRollCurve;

    // Tiny cache of the last layer query result — bounciness rarely changes
    // between adjacent bounce points so we avoid reflecting every frame.
    private Vector3 cachedLastTerrainQueryPoint;
    private bool cachedLastTerrainQueryValid;
    private float cachedLastTerrainBounciness = 0.5f;
    private float cachedLastTerrainDynamicFriction = 0.3f;
    private float cachedLastTerrainLinearDamping = 2f;
    private float cachedLastTerrainFullStopMaxPitch = 5f;
    private float cachedLastTerrainFullRollMinPitch = 20f;
    private AnimationCurve cachedLastTerrainFullStopToFullRollCurve;

    // Hole detection: list of all GolfHoleTrigger ballTrigger bounds in the scene.
    private bool holeReflectionInitialized;
    private float holeRefreshNextTime;
    private readonly float holeRefreshInterval = 2f;
    private readonly List<HoleBounds> cachedHoles = new List<HoleBounds>(8);
    private int lastLoggedHoleCount = -1;
    private float lastLoggedFirstHoleX;
    private float lastLoggedFirstHoleZ;

    private struct HoleBounds
    {
        public Vector3 Center;  // bounds.center
        public float RadiusXZ;  // max(extents.x, extents.z)
        public float TopY;      // bounds.max.y
    }

    internal void EnsureTerrainReflectionInitialized()
    {
        if (terrainReflectionInitialized)
        {
            return;
        }
        terrainReflectionInitialized = true;

        try
        {
            Type tmType = null;
            Type tlsType = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (tmType == null) tmType = assemblies[i].GetType("TerrainManager");
                if (tlsType == null) tlsType = assemblies[i].GetType("TerrainLayerSettings");
                if (tmType != null && tlsType != null) break;
            }

            if (tmType == null || tlsType == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] TerrainManager/TerrainLayerSettings not found — falling back to ball physics material");
                return;
            }

            // TerrainManager is a SingletonBehaviour<TerrainManager> — find the Instance
            // property on the base type or the derived type.
            Type cursor = tmType;
            while (cursor != null && cachedTerrainManagerInstance == null)
            {
                PropertyInfo instanceProp = cursor.GetProperty("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (instanceProp != null)
                {
                    try
                    {
                        cachedTerrainManagerInstance = instanceProp.GetValue(null, null);
                    }
                    catch { }
                }
                cursor = cursor.BaseType;
            }

            if (cachedTerrainManagerInstance == null)
            {
                // Fallback — try FindFirstObjectByType since it's a MonoBehaviour.
                try
                {
                    cachedTerrainManagerInstance = UnityEngine.Object.FindFirstObjectByType(tmType);
                }
                catch { }
            }

            // GetDominantLayerSettingsAtPoint(Vector3) — returns TerrainLayerSettings
            cachedGetDominantLayerSettingsAtPoint = tmType.GetMethod("GetDominantLayerSettingsAtPoint",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static,
                null, new Type[] { typeof(Vector3) }, null);

            // TerrainLayerSettings property getters
            cachedTLSBounciness = tlsType.GetProperty("Bounciness",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            cachedTLSDynamicFriction = tlsType.GetProperty("DynamicFriction",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            cachedTLSStaticFriction = tlsType.GetProperty("StaticFriction",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            cachedTLSLinearDamping = tlsType.GetProperty("LinearDamping",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            cachedTLSFullStopMaxPitch = tlsType.GetProperty("FullStopMaxPitch",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            cachedTLSFullRollMinPitch = tlsType.GetProperty("FullRollMinPitch",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            cachedTLSBallFullStopToFullRollCurve = tlsType.GetProperty("BallFullStopToFullRollCurve",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            MelonLogger.Msg(
                $"[SuperHackerGolf] Terrain reflection: TerrainManager={(cachedTerrainManagerInstance != null ? "Y" : "n")} " +
                $"GetDomLayerSettings={(cachedGetDominantLayerSettingsAtPoint != null ? "Y" : "n")} " +
                $"TLS properties: " +
                $"Bounciness={(cachedTLSBounciness != null ? "Y" : "n")} " +
                $"DynFriction={(cachedTLSDynamicFriction != null ? "Y" : "n")} " +
                $"LinDamp={(cachedTLSLinearDamping != null ? "Y" : "n")} " +
                $"PitchGates={(cachedTLSFullStopMaxPitch != null && cachedTLSFullRollMinPitch != null ? "Y" : "n")} " +
                $"Curve={(cachedTLSBallFullStopToFullRollCurve != null ? "Y" : "n")}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Terrain reflection init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Query TerrainLayerSettings at a specific world point. Caches the result
    /// so adjacent sim steps can skip the reflection cost. Falls back to the
    /// GolfBallSettings defaults read by GamePhysicsReflection if the query
    /// fails (e.g. off-terrain or on a TerrainAddition).
    /// </summary>
    internal bool TryGetTerrainLayerAtPoint(Vector3 contactPoint,
        out float bounciness, out float dynamicFriction, out float linearDamping,
        out float fullStopMaxPitch, out float fullRollMinPitch, out AnimationCurve curve)
    {
        EnsureTerrainReflectionInitialized();

        bounciness = cachedLastTerrainBounciness;
        dynamicFriction = cachedLastTerrainDynamicFriction;
        linearDamping = cachedLastTerrainLinearDamping;
        fullStopMaxPitch = cachedLastTerrainFullStopMaxPitch;
        fullRollMinPitch = cachedLastTerrainFullRollMinPitch;
        curve = cachedLastTerrainFullStopToFullRollCurve;

        if (cachedTerrainManagerInstance == null || cachedGetDominantLayerSettingsAtPoint == null)
        {
            return false;
        }

        // Small-step cache: if the query point is within 1m of the previous
        // successful query, reuse the cached values.
        if (cachedLastTerrainQueryValid && (contactPoint - cachedLastTerrainQueryPoint).sqrMagnitude < 1f)
        {
            return true;
        }

        try
        {
            object settings = cachedGetDominantLayerSettingsAtPoint.Invoke(
                cachedTerrainManagerInstance, new object[] { contactPoint });
            if (settings == null) return false;

            bool anyRead = false;
            if (cachedTLSBounciness != null)
            {
                object v = cachedTLSBounciness.GetValue(settings, null);
                if (v is float f) { bounciness = f; anyRead = true; }
            }
            if (cachedTLSDynamicFriction != null)
            {
                object v = cachedTLSDynamicFriction.GetValue(settings, null);
                if (v is float f) { dynamicFriction = f; }
            }
            if (cachedTLSLinearDamping != null)
            {
                object v = cachedTLSLinearDamping.GetValue(settings, null);
                if (v is float f) { linearDamping = f; }
            }
            if (cachedTLSFullStopMaxPitch != null)
            {
                object v = cachedTLSFullStopMaxPitch.GetValue(settings, null);
                if (v is float f) { fullStopMaxPitch = f; }
            }
            if (cachedTLSFullRollMinPitch != null)
            {
                object v = cachedTLSFullRollMinPitch.GetValue(settings, null);
                if (v is float f) { fullRollMinPitch = f; }
            }
            if (cachedTLSBallFullStopToFullRollCurve != null)
            {
                object v = cachedTLSBallFullStopToFullRollCurve.GetValue(settings, null);
                curve = v as AnimationCurve;
            }

            if (anyRead)
            {
                cachedLastTerrainQueryPoint = contactPoint;
                cachedLastTerrainQueryValid = true;
                cachedLastTerrainBounciness = bounciness;
                cachedLastTerrainDynamicFriction = dynamicFriction;
                cachedLastTerrainLinearDamping = linearDamping;
                cachedLastTerrainFullStopMaxPitch = fullStopMaxPitch;
                cachedLastTerrainFullRollMinPitch = fullRollMinPitch;
                cachedLastTerrainFullStopToFullRollCurve = curve;
                return true;
            }
        }
        catch { }

        return false;
    }

    // ── Hole detection ─────────────────────────────────────────────────────────

    internal void RefreshHoleBounds()
    {
        float now = Time.time;
        if (holeReflectionInitialized && now < holeRefreshNextTime)
        {
            return;
        }
        holeReflectionInitialized = true;
        holeRefreshNextTime = now + holeRefreshInterval;
        cachedHoles.Clear();

        try
        {
            Type ghtType = null;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                ghtType = assemblies[i].GetType("GolfHoleTrigger");
                if (ghtType != null) break;
            }
            if (ghtType == null) return;

            FieldInfo fBallTrigger = ghtType.GetField("ballTrigger",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fBallTrigger == null) return;

            UnityEngine.Object[] triggers = UnityEngine.Object.FindObjectsByType(ghtType,
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            if (triggers == null) return;

            for (int i = 0; i < triggers.Length; i++)
            {
                object trig = triggers[i];
                if (trig == null) continue;
                Collider coll = fBallTrigger.GetValue(trig) as Collider;
                if (coll == null || !coll.enabled) continue;

                Bounds b = coll.bounds;
                cachedHoles.Add(new HoleBounds
                {
                    Center = b.center,
                    RadiusXZ = Mathf.Max(b.extents.x, b.extents.z),
                    TopY = b.max.y,
                });
            }

            // E25: only log when the hole count or first-hole identity changes.
            // Previously this fired every refresh interval (~10s) and spammed
            // the log with identical "Discovered 1 hole trigger(s)" lines.
            if (cachedHoles.Count > 0)
            {
                bool changed = cachedHoles.Count != lastLoggedHoleCount ||
                               Mathf.Abs(cachedHoles[0].Center.x - lastLoggedFirstHoleX) > 0.1f ||
                               Mathf.Abs(cachedHoles[0].Center.z - lastLoggedFirstHoleZ) > 0.1f;
                if (changed)
                {
                    MelonLogger.Msg($"[SuperHackerGolf] Discovered {cachedHoles.Count} hole trigger(s); first holeRadius={cachedHoles[0].RadiusXZ:F3}m topY={cachedHoles[0].TopY:F2}");
                    lastLoggedHoleCount = cachedHoles.Count;
                    lastLoggedFirstHoleX = cachedHoles[0].Center.x;
                    lastLoggedFirstHoleZ = cachedHoles[0].Center.z;
                }
            }
            else if (lastLoggedHoleCount != 0)
            {
                MelonLogger.Msg("[SuperHackerGolf] No hole triggers found");
                lastLoggedHoleCount = 0;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Hole reflection failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Forward-sim hole check: returns true if the given ball position is
    /// inside any known GolfHoleTrigger ballTrigger volume.
    /// </summary>
    internal bool IsPositionInHole(Vector3 ballPos)
    {
        if (cachedHoles.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < cachedHoles.Count; i++)
        {
            HoleBounds h = cachedHoles[i];
            // Require ball to be at or below the top of the trigger volume
            // (otherwise we "hole out" when the ball flies high over the cup).
            if (ballPos.y > h.TopY + 0.1f) continue;

            float dx = ballPos.x - h.Center.x;
            float dz = ballPos.z - h.Center.z;
            if (dx * dx + dz * dz <= h.RadiusXZ * h.RadiusXZ)
            {
                return true;
            }
        }
        return false;
    }
}
