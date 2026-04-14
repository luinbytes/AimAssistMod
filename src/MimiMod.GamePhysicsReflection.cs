using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── Game physics reflection ────────────────────────────────────────────────
    //
    // Reads the exact constants used by Super Battle Golf's physics engine so
    // the forward sim matches the game to within float precision.
    //
    // Sources (all decompiled from GameAssembly.dll via Mono.Cecil):
    //
    //   GameManager.ItemSettings (ScriptableObject):
    //     - RocketDriverBaseNormalizedSwingPower  → Remap inMin for hit speed
    //     - RocketDriverFullNormalizedSwingPower  → Remap inMax for hit speed
    //
    //   GameManager.GolfBallSettings (ScriptableObject):
    //     - LinearAirDragFactor                     → quadratic air drag coeff
    //     - RocketDriverSwingLinearAirDragFactor    → rocket driver air drag
    //     - GroundFullStopMaxPitch / FullRollMinPitch → pitch gates for roll
    //     - FullStopMinDampingSpeed / MaxDampingSpeed → velocity gates
    //     - FullStopLinearDamping                   → terminal ground damping
    //     - GroundFrictionLinearDamping             → rolling friction
    //     - GroundFullStopToFullRollCurve           → pitch-blend curve
    //     - FullStopRollingDownhillStartTime/EndTime/EndDampingSpeedFactor
    //
    //   PlayerGolfer.PlayerInfo.Inventory.GetEffectivelyEquippedItem(false)
    //     → returns ItemType enum; compare to RocketDriver (11)
    //
    // All values are cached at first-use and have sensible fallbacks drawn
    // from typical Unity defaults so the sim still runs if reflection fails.

    private bool physicsReflectionInitialized;

    // ItemSettings (static singleton)
    private object cachedItemSettingsInstance;
    private float cachedRocketDriverBaseNormalizedSwingPower = 0.25f;
    private float cachedRocketDriverFullNormalizedSwingPower = 1.15f;

    // GolfBallSettings (static singleton)
    private object cachedGolfBallSettingsInstance;
    private float cachedRocketDriverAirDragFactor = 0.00025f;
    private float cachedGroundFullStopMaxPitch = 5f;
    private float cachedGroundFullRollMinPitch = 20f;
    private float cachedFullStopMinDampingSpeed = 1f;
    private float cachedFullStopMaxDampingSpeed = 5f;
    private float cachedFullStopLinearDamping = 10f;
    private float cachedGroundFrictionLinearDamping = 2f;
    private float cachedFullStopRollingDownhillStartTime = 1f;
    private float cachedFullStopRollingDownhillEndTime = 3f;
    private float cachedFullStopRollingDownhillEndDampingSpeedFactor = 0.2f;
    private AnimationCurve cachedGroundFullStopToFullRollCurve;

    // Rocket driver detection chain: PlayerGolfer → PlayerInfo → Inventory → GetEffectivelyEquippedItem(bool)
    private PropertyInfo cachedPlayerInfoProperty;
    private PropertyInfo cachedInventoryProperty;
    private MethodInfo cachedGetEffectivelyEquippedItem;
    private const int ROCKET_DRIVER_ITEM_TYPE_VALUE = 11;

    // Per-ball SwingHittableSettings — rocket driver hit speed range
    private PropertyInfo cachedMinRocketSwingHitSpeedProperty;
    private PropertyInfo cachedMaxRocketSwingHitSpeedProperty;
    private PropertyInfo cachedMinRocketPuttHitSpeedProperty;
    private PropertyInfo cachedMaxRocketPuttHitSpeedProperty;
    private float cachedBallMinRocketSwingHitSpeed = 100f;
    private float cachedBallMaxRocketSwingHitSpeed = 180f;
    private float cachedBallMinRocketPuttHitSpeed = 60f;
    private float cachedBallMaxRocketPuttHitSpeed = 110f;

    // GameManager.LayerSettings.BallGroundableMask — the layer mask the game's
    // IsGrounded SphereCast uses. Filters out trees, walls, decorations, etc.
    // Our forward sim uses this instead of Physics.DefaultRaycastLayers so we
    // don't terminate early on mid-flight tree clips.
    private int cachedBallGroundableMask = ~0; // default = everything until we reflect
    private bool ballGroundableMaskResolved;

    internal int GetBallGroundableMask()
    {
        if (!ballGroundableMaskResolved)
        {
            ballGroundableMaskResolved = true;
            try
            {
                Type gmType = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    gmType = assemblies[i].GetType("GameManager");
                    if (gmType != null) break;
                }
                if (gmType != null)
                {
                    object layerSettings = ReadStaticMember(gmType, "LayerSettings");
                    if (layerSettings != null)
                    {
                        // BallGroundableMask is a LayerMask field — its .value is an int.
                        Type lsType = layerSettings.GetType();
                        PropertyInfo maskProp = lsType.GetProperty("BallGroundableMask",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        object maskObj = null;
                        if (maskProp != null)
                        {
                            maskObj = maskProp.GetValue(layerSettings, null);
                        }
                        else
                        {
                            FieldInfo maskField = lsType.GetField("BallGroundableMask",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (maskField != null)
                            {
                                maskObj = maskField.GetValue(layerSettings);
                            }
                        }
                        if (maskObj is LayerMask lm)
                        {
                            cachedBallGroundableMask = lm.value;
                        }
                        else if (maskObj is int iv)
                        {
                            cachedBallGroundableMask = iv;
                        }
                        MelonLogger.Msg($"[SuperHackerGolf] BallGroundableMask resolved: 0x{cachedBallGroundableMask:X8}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SuperHackerGolf] BallGroundableMask resolution failed: {ex.GetType().Name}");
            }
        }
        return cachedBallGroundableMask;
    }

    // Ball's Unity PhysicsMaterial — bounce + friction coefficients.
    // Read directly from the ball's SphereCollider.sharedMaterial. Since
    // GolfBall.OnCollisionEnter doesn't apply bounce in managed code, all
    // bouncing is driven by Unity's physics engine via this material. To
    // replicate it in the forward sim we read the values at runtime.
    private bool ballPhysicsMaterialInitialized;
    private Collider cachedBallCollider;
    private float cachedBallBounciness = 0.35f;          // typical Unity default
    private float cachedBallDynamicFriction = 0.6f;
    private float cachedBallStaticFriction = 0.6f;
    private int cachedBallBounceCombine = 0;             // 0 = Average
    private int cachedBallFrictionCombine = 0;

    internal void RefreshBallPhysicsMaterial()
    {
        if (golfBall == null) return;

        // Only re-read when the ball reference changes.
        if (ballPhysicsMaterialInitialized &&
            cachedBallCollider != null &&
            ReferenceEquals(cachedBallCollider.gameObject, golfBall.gameObject))
        {
            return;
        }

        try
        {
            Collider[] colliders = golfBall.gameObject.GetComponents<Collider>();
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                if (c == null || c.isTrigger) continue;
                cachedBallCollider = c;
                PhysicsMaterial mat = c.sharedMaterial;
                if (mat != null)
                {
                    cachedBallBounciness = mat.bounciness;
                    cachedBallDynamicFriction = mat.dynamicFriction;
                    cachedBallStaticFriction = mat.staticFriction;
                    cachedBallBounceCombine = (int)mat.bounceCombine;
                    cachedBallFrictionCombine = (int)mat.frictionCombine;
                }
                break;
            }
            ballPhysicsMaterialInitialized = true;

            MelonLogger.Msg($"[SuperHackerGolf] Ball physics material: bounciness={cachedBallBounciness:F3} dynamicFriction={cachedBallDynamicFriction:F3}");
        }
        catch { }
    }

    internal float GetBallBounciness() => cachedBallBounciness;
    internal float GetBallDynamicFriction() => cachedBallDynamicFriction;

    internal void EnsurePhysicsReflectionInitialized()
    {
        if (physicsReflectionInitialized)
        {
            return;
        }
        physicsReflectionInitialized = true;

        Type gmType = null;
        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                gmType = assemblies[i].GetType("GameManager");
                if (gmType != null) break;
            }
        }
        catch { }

        if (gmType == null)
        {
            MelonLogger.Warning("[SuperHackerGolf] GameManager type not found — physics reflection using fallback defaults");
            return;
        }

        // GameManager.ItemSettings (static property or field)
        try
        {
            object itemSettings = ReadStaticMember(gmType, "ItemSettings");
            if (itemSettings != null)
            {
                cachedItemSettingsInstance = itemSettings;
                Type isType = itemSettings.GetType();
                ReadFloatProp(isType, itemSettings, "RocketDriverBaseNormalizedSwingPower", ref cachedRocketDriverBaseNormalizedSwingPower);
                ReadFloatProp(isType, itemSettings, "RocketDriverFullNormalizedSwingPower", ref cachedRocketDriverFullNormalizedSwingPower);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] ItemSettings reflection failed: {ex.GetType().Name}");
        }

        // GameManager.GolfBallSettings (static)
        try
        {
            object gbs = ReadStaticMember(gmType, "GolfBallSettings");
            if (gbs != null)
            {
                cachedGolfBallSettingsInstance = gbs;
                Type gbsType = gbs.GetType();
                ReadFloatProp(gbsType, gbs, "RocketDriverSwingLinearAirDragFactor", ref cachedRocketDriverAirDragFactor);
                ReadFloatProp(gbsType, gbs, "GroundFullStopMaxPitch", ref cachedGroundFullStopMaxPitch);
                ReadFloatProp(gbsType, gbs, "GroundFullRollMinPitch", ref cachedGroundFullRollMinPitch);
                ReadFloatProp(gbsType, gbs, "FullStopMinDampingSpeed", ref cachedFullStopMinDampingSpeed);
                ReadFloatProp(gbsType, gbs, "FullStopMaxDampingSpeed", ref cachedFullStopMaxDampingSpeed);
                ReadFloatProp(gbsType, gbs, "FullStopLinearDamping", ref cachedFullStopLinearDamping);
                ReadFloatProp(gbsType, gbs, "GroundFrictionLinearDamping", ref cachedGroundFrictionLinearDamping);
                ReadFloatProp(gbsType, gbs, "FullStopRollingDownhillStartTime", ref cachedFullStopRollingDownhillStartTime);
                ReadFloatProp(gbsType, gbs, "FullStopRollingDownhillEndTime", ref cachedFullStopRollingDownhillEndTime);
                ReadFloatProp(gbsType, gbs, "FullStopRollingDownhillEndDampingSpeedFactor", ref cachedFullStopRollingDownhillEndDampingSpeedFactor);

                try
                {
                    PropertyInfo curveProp = gbsType.GetProperty("GroundFullStopToFullRollCurve",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (curveProp != null)
                    {
                        cachedGroundFullStopToFullRollCurve = curveProp.GetValue(gbs, null) as AnimationCurve;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] GolfBallSettings reflection failed: {ex.GetType().Name}");
        }

        MelonLogger.Msg(
            $"[SuperHackerGolf] Physics reflection: " +
            $"RD_base={cachedRocketDriverBaseNormalizedSwingPower:F3} RD_full={cachedRocketDriverFullNormalizedSwingPower:F3} " +
            $"rdAirDrag={cachedRocketDriverAirDragFactor:F6} " +
            $"fullStopMaxPitch={cachedGroundFullStopMaxPitch:F1}° fullRollMinPitch={cachedGroundFullRollMinPitch:F1}° " +
            $"friction={cachedGroundFrictionLinearDamping:F2} fullStop={cachedFullStopLinearDamping:F2} " +
            $"stopMin={cachedFullStopMinDampingSpeed:F2} stopMax={cachedFullStopMaxDampingSpeed:F2} " +
            $"curve={(cachedGroundFullStopToFullRollCurve != null ? "Y" : "n")}");
    }

    private static object ReadStaticMember(Type t, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        try
        {
            PropertyInfo prop = t.GetProperty(name, flags);
            if (prop != null)
            {
                return prop.GetValue(null, null);
            }
        }
        catch { }
        try
        {
            FieldInfo field = t.GetField(name, flags);
            if (field != null)
            {
                return field.GetValue(null);
            }
        }
        catch { }
        return null;
    }

    private static void ReadFloatProp(Type t, object instance, string name, ref float target)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        try
        {
            PropertyInfo prop = t.GetProperty(name, flags);
            if (prop != null)
            {
                object v = prop.GetValue(instance, null);
                if (v is float f) { target = f; return; }
                if (v is double d) { target = (float)d; return; }
            }
            FieldInfo field = t.GetField(name, flags);
            if (field == null)
            {
                // ScriptableObject auto-properties sometimes expose only the backing field
                field = t.GetField("<" + name + ">k__BackingField", flags);
            }
            if (field != null)
            {
                object v = field.GetValue(instance);
                if (v is float f) { target = f; return; }
                if (v is double d) { target = (float)d; return; }
            }
        }
        catch { }
    }

    // ── Rocket driver detection ────────────────────────────────────────────────

    internal bool IsLocalPlayerUsingRocketDriver()
    {
        if (playerGolfer == null)
        {
            return false;
        }

        try
        {
            // PlayerGolfer.PlayerInfo
            if (cachedPlayerInfoProperty == null)
            {
                cachedPlayerInfoProperty = playerGolfer.GetType().GetProperty("PlayerInfo",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (cachedPlayerInfoProperty == null) return false;

            object playerInfo = cachedPlayerInfoProperty.GetValue(playerGolfer, null);
            if (playerInfo == null) return false;

            // PlayerInfo.Inventory
            if (cachedInventoryProperty == null)
            {
                cachedInventoryProperty = playerInfo.GetType().GetProperty("Inventory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (cachedInventoryProperty == null) return false;

            object inventory = cachedInventoryProperty.GetValue(playerInfo, null);
            if (inventory == null) return false;

            // Inventory.GetEffectivelyEquippedItem(bool ignoreEquipmentHiding)
            if (cachedGetEffectivelyEquippedItem == null)
            {
                MethodInfo[] methods = inventory.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name != "GetEffectivelyEquippedItem") continue;
                    ParameterInfo[] pars = m.GetParameters();
                    if (pars.Length != 1) continue;
                    if (pars[0].ParameterType != typeof(bool)) continue;
                    cachedGetEffectivelyEquippedItem = m;
                    break;
                }
            }
            if (cachedGetEffectivelyEquippedItem == null) return false;

            object itemTypeValue = cachedGetEffectivelyEquippedItem.Invoke(inventory, new object[] { false });
            if (itemTypeValue == null) return false;

            // ItemType is an int-backed enum; cast via Convert to handle the boxed enum.
            int itemInt = Convert.ToInt32(itemTypeValue);
            return itemInt == ROCKET_DRIVER_ITEM_TYPE_VALUE;
        }
        catch
        {
            return false;
        }
    }

    // ── Per-ball rocket-driver hit speed reflection ────────────────────────────
    //
    // Called from RefreshBallWindFactors after the base swing settings are read.
    // Reads Min/MaxPowerRocketDriverSwingHitSpeed + putt variants off the ball's
    // SwingHittableSettings.

    internal void ReadBallRocketDriverSpeeds(object swingSettings)
    {
        if (swingSettings == null) return;
        Type swingType = swingSettings.GetType();

        if (cachedMinRocketSwingHitSpeedProperty == null)
            cachedMinRocketSwingHitSpeedProperty = swingType.GetProperty("MinPowerRocketDriverSwingHitSpeed");
        if (cachedMaxRocketSwingHitSpeedProperty == null)
            cachedMaxRocketSwingHitSpeedProperty = swingType.GetProperty("MaxPowerRocketDriverSwingHitSpeed");
        if (cachedMinRocketPuttHitSpeedProperty == null)
            cachedMinRocketPuttHitSpeedProperty = swingType.GetProperty("MinPowerRocketDriverPuttHitSpeed");
        if (cachedMaxRocketPuttHitSpeedProperty == null)
            cachedMaxRocketPuttHitSpeedProperty = swingType.GetProperty("MaxPowerRocketDriverPuttHitSpeed");

        try
        {
            if (cachedMinRocketSwingHitSpeedProperty != null)
            {
                object v = cachedMinRocketSwingHitSpeedProperty.GetValue(swingSettings, null);
                if (v is float f) cachedBallMinRocketSwingHitSpeed = f;
            }
            if (cachedMaxRocketSwingHitSpeedProperty != null)
            {
                object v = cachedMaxRocketSwingHitSpeedProperty.GetValue(swingSettings, null);
                if (v is float f) cachedBallMaxRocketSwingHitSpeed = f;
            }
            if (cachedMinRocketPuttHitSpeedProperty != null)
            {
                object v = cachedMinRocketPuttHitSpeedProperty.GetValue(swingSettings, null);
                if (v is float f) cachedBallMinRocketPuttHitSpeed = f;
            }
            if (cachedMaxRocketPuttHitSpeedProperty != null)
            {
                object v = cachedMaxRocketPuttHitSpeedProperty.GetValue(swingSettings, null);
                if (v is float f) cachedBallMaxRocketPuttHitSpeed = f;
            }
        }
        catch { }
    }

    // ── Exact launch speed formula ─────────────────────────────────────────────
    //
    // Reimplements Hittable.GetSwingHitSpeed exactly:
    //
    //   if (isRocketDriver)
    //       return BMath.Remap(baseNormPower, fullNormPower, minSpeed, maxSpeed, power);
    //   return power * MaxPowerSwingHitSpeed;
    //
    // Plus the MatchSetupRules.GetValue(Rule.SwingPower) multiplier from
    // HitWithGolfSwingInternal.

    internal float GetGameExactLaunchSpeed(float power, bool isPutt, bool isRocketDriver)
    {
        EnsurePhysicsReflectionInitialized();

        power = Mathf.Max(0f, power);

        float matchMul;
        if (!TryGetServerSwingPowerMultiplier(out matchMul))
        {
            matchMul = 1f;
        }

        float rawSpeed;
        if (isRocketDriver)
        {
            float minSpeed = isPutt ? cachedBallMinRocketPuttHitSpeed : cachedBallMinRocketSwingHitSpeed;
            float maxSpeed = isPutt ? cachedBallMaxRocketPuttHitSpeed : cachedBallMaxRocketSwingHitSpeed;
            // BMath.Remap is UNCLAMPED — this matches the game's "overcharge with
            // rocket driver extrapolates above max speed" behavior the user reported.
            float inMin = cachedRocketDriverBaseNormalizedSwingPower;
            float inMax = cachedRocketDriverFullNormalizedSwingPower;
            float denom = inMax - inMin;
            if (Mathf.Abs(denom) < 1e-6f)
            {
                rawSpeed = (minSpeed + maxSpeed) * 0.5f;
            }
            else
            {
                float t = (power - inMin) / denom;
                rawSpeed = minSpeed + t * (maxSpeed - minSpeed);
            }
        }
        else
        {
            float maxSpeed = isPutt ? cachedBallMaxPuttHitSpeed : cachedBallMaxSwingHitSpeed;
            rawSpeed = power * maxSpeed;
        }

        return rawSpeed * matchMul;
    }

    internal float GetGameExactAirDragFactor(bool isRocketDriver)
    {
        EnsurePhysicsReflectionInitialized();
        if (isRocketDriver)
        {
            return cachedRocketDriverAirDragFactor;
        }
        // Fall back to Mimi's existing GetRuntimeLinearAirDragFactor for the
        // non-rocket path — it already reads LinearAirDragFactor correctly.
        return GetRuntimeLinearAirDragFactor();
    }

    // ── Ground damping (exact GetDamping formula) ──────────────────────────────
    //
    // Reconstructed from g__GetDamping|114_1. Assumes flat ground for the common
    // case (terrainDominantGlobalLayer = 0) — full per-terrain layer support is
    // a future extension. Inputs:
    //
    //   pitch: angle in degrees between Vector3.up and the ground normal. For
    //          flat terrain this is 0. For slopes, compute from raycast normal.
    //   speed: magnitude of the velocity projected onto the ground plane (m/s).
    //   rollingDownhillTime: seconds the ball has been rolling downhill; 0 for
    //          a fresh landing, fed to the downhill damping multiplier.
    //
    // Returns the damping coefficient (1/s) to multiply by fixedDeltaTime and
    // subtract from the along-ground velocity.

    internal float ComputeGroundDamping(float pitch, float speed, float rollingDownhillTime, out float fullStopFactor)
    {
        EnsurePhysicsReflectionInitialized();
        fullStopFactor = 0f;

        float fullStopMaxPitch = cachedGroundFullStopMaxPitch;
        float fullRollMinPitch = cachedGroundFullRollMinPitch;

        // Step 4: downhill roll multiplier
        float downhillRollMul = RemapClampedFloat(
            cachedFullStopRollingDownhillStartTime,
            cachedFullStopRollingDownhillEndTime,
            1f,
            cachedFullStopRollingDownhillEndDampingSpeedFactor,
            rollingDownhillTime);

        float fullStopMaxDampSpeed = downhillRollMul * cachedFullStopMaxDampingSpeed;
        float fullStopMinDampSpeed = downhillRollMul * cachedFullStopMinDampingSpeed;

        // Step 5: full-stop gate (shallow slope + low speed)
        if (pitch < fullStopMaxPitch && speed * speed < fullStopMaxDampSpeed * fullStopMaxDampSpeed)
        {
            fullStopFactor = 1f;
            return cachedFullStopLinearDamping;
        }

        // Step 7: friction damping scaled by ease-in of (pitch/90)
        // BMath.EaseIn is sin²(x * π/2) per Unity convention; approximated as x*x here.
        float t = Mathf.Clamp01(pitch / 90f);
        float easeIn = t * t;
        float frictionScale = 1f - easeIn;
        float frictionDampingScaled = frictionScale * cachedGroundFrictionLinearDamping;

        // Step 8: below min damping speed → pure friction
        if (speed * speed < fullStopMinDampSpeed * fullStopMinDampSpeed)
        {
            return frictionDampingScaled;
        }

        // Step 9: blend friction and full-stop via pitch curve + speed fraction
        float fsf = Mathf.InverseLerp(fullStopMinDampSpeed, fullStopMaxDampSpeed, speed);
        float curveT = Mathf.InverseLerp(fullStopMaxPitch, fullRollMinPitch, pitch);
        float curveValue = cachedGroundFullStopToFullRollCurve != null
            ? cachedGroundFullStopToFullRollCurve.Evaluate(curveT)
            : (1f - curveT); // fallback: linear
        fsf *= curveValue;
        fullStopFactor = fsf;
        return Mathf.Lerp(frictionDampingScaled, cachedFullStopLinearDamping, fsf);
    }

    // Standard Remap(inMin, inMax, outMin, outMax, t) — clamped at the output
    // range. Matches BMath.RemapClamped from the game (verified via IL).
    private static float RemapClampedFloat(float inMin, float inMax, float outMin, float outMax, float t)
    {
        if (Mathf.Abs(inMax - inMin) < 1e-6f) return outMin;
        float u = Mathf.Clamp01((t - inMin) / (inMax - inMin));
        return outMin + u * (outMax - outMin);
    }
}
