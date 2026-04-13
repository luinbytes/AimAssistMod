using System;
using System.Reflection;
using UnityEngine;

public partial class MimiMod
{
    private bool TryGetSwingChargeSettings(out float riseDuration, out float fullChargeTime, out float coyoteTime, out float maxNormalizedPower, out float minReleasePower)
    {
        riseDuration = 1f;
        fullChargeTime = 1f;
        coyoteTime = 0f;
        maxNormalizedPower = 2f;
        minReleasePower = 0f;

        object golfSettings = GetGolfSettingsObject();
        if (golfSettings == null)
        {
            return false;
        }

        riseDuration = ModReflectionHelper.GetFloatMemberValue(golfSettings, "SwingChargeRiseDuration", 1f);
        fullChargeTime = ModReflectionHelper.GetFloatMemberValue(golfSettings, "ChargeTimeForRegularFullCharge", riseDuration);
        coyoteTime = ModReflectionHelper.GetFloatMemberValue(golfSettings, "SwingRegularFullChargeCoyoteTime", 0f);
        maxNormalizedPower = 1f + ModReflectionHelper.GetFloatMemberValue(golfSettings, "MaxSwingOvercharge", 0f);
        minReleasePower = ModReflectionHelper.GetFloatMemberValue(golfSettings, "MinSwingReleaseNormalizedPower", 0f);
        return true;
    }

    private float EvaluateEaseIn(float value)
    {
        float clampedValue = Mathf.Clamp01(value);
        InitializeSwingMathReflection();

        try
        {
            if (cachedBMathEaseInMethod != null)
            {
                cachedEaseInArgs[0] = clampedValue;
                object result = cachedBMathEaseInMethod.Invoke(null, cachedEaseInArgs);
                if (result is float)
                {
                    return (float)result;
                }
                if (result is double)
                {
                    return (float)(double)result;
                }
            }
        }
        catch
        {
        }

        return clampedValue * clampedValue;
    }

    private float EvaluateSwingPowerFromChargeAge(float chargeAge, float riseDuration, float fullChargeTime, float coyoteTime, float maxNormalizedPower, float minReleasePower)
    {
        float effectiveValue;
        if (chargeAge < fullChargeTime)
        {
            effectiveValue = chargeAge;
        }
        else if (chargeAge < fullChargeTime + coyoteTime)
        {
            effectiveValue = fullChargeTime;
        }
        else
        {
            effectiveValue = chargeAge - coyoteTime;
        }

        float normalizedTime = riseDuration > Mathf.Epsilon ? Mathf.Clamp01(effectiveValue / riseDuration) : 1f;
        float swingPower = Mathf.LerpUnclamped(0f, maxNormalizedPower, EvaluateEaseIn(normalizedTime));
        return Mathf.Max(swingPower, minReleasePower);
    }

    private bool TryCalculateChargeTimestampForPower(float targetPower, out double targetTimestamp, out float resolvedPower)
    {
        targetTimestamp = Time.timeAsDouble;
        resolvedPower = targetPower;

        float riseDuration;
        float fullChargeTime;
        float coyoteTime;
        float maxNormalizedPower;
        float minReleasePower;
        if (!TryGetSwingChargeSettings(out riseDuration, out fullChargeTime, out coyoteTime, out maxNormalizedPower, out minReleasePower))
        {
            return false;
        }

        float clampedTargetPower = Mathf.Clamp(targetPower, minReleasePower, maxNormalizedPower);
        float low = clampedTargetPower > 1f ? fullChargeTime + coyoteTime : 0f;
        float high = clampedTargetPower > 1f ? Mathf.Max(low, riseDuration + coyoteTime) : Mathf.Max(0.0001f, fullChargeTime);

        for (int i = 0; i < 32; i++)
        {
            float mid = (low + high) * 0.5f;
            float midPower = EvaluateSwingPowerFromChargeAge(mid, riseDuration, fullChargeTime, coyoteTime, maxNormalizedPower, minReleasePower);
            if (midPower < clampedTargetPower)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        float finalChargeAge = high;
        resolvedPower = EvaluateSwingPowerFromChargeAge(finalChargeAge, riseDuration, fullChargeTime, coyoteTime, maxNormalizedPower, minReleasePower);
        targetTimestamp = Time.timeAsDouble - finalChargeAge;
        return true;
    }

    private bool TrySetChargingSwingState(bool isCharging, out bool stateApplied)
    {
        stateApplied = false;

        if (playerGolfer == null)
        {
            return false;
        }

        try
        {
            if (cachedSetIsChargingSwingMethod == null)
            {
                cachedSetIsChargingSwingMethod = playerGolfer.GetType().GetMethod("SetIsChargingSwing", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (cachedSetIsChargingSwingMethod == null)
            {
                return false;
            }

            cachedChargingStateArgs[0] = isCharging;
            cachedSetIsChargingSwingMethod.Invoke(playerGolfer, cachedChargingStateArgs);
            stateApplied = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryStartChargingSwingForAutoRelease(out bool startedCharging)
    {
        startedCharging = false;

        if (playerGolfer == null)
        {
            return false;
        }

        try
        {
            if (cachedTryStartChargingSwingMethod == null)
            {
                cachedTryStartChargingSwingMethod = playerGolfer.GetType().GetMethod("TryStartChargingSwing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (cachedTryStartChargingSwingMethod == null)
            {
                return false;
            }

            object result = cachedTryStartChargingSwingMethod.Invoke(playerGolfer, null);
            startedCharging = !(result is bool) || (bool)result;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TrySetSwingPower(float targetPower, out float appliedPower)
    {
        appliedPower = 0f;

        if (playerGolfer == null)
        {
            return false;
        }

        bool ignoredChargingState;
        TrySetChargingSwingState(true, out ignoredChargingState);

        float riseDuration;
        float fullChargeTime;
        float coyoteTime;
        float maxNormalizedPower;
        float minReleasePower;
        bool hasSettings = TryGetSwingChargeSettings(out riseDuration, out fullChargeTime, out coyoteTime, out maxNormalizedPower, out minReleasePower);

        float clampedPower = Mathf.Clamp(targetPower, hasSettings ? minReleasePower : 0.05f, hasSettings ? maxNormalizedPower : 2f);
        double targetTimestamp;
        float resolvedPower;
        if (TryCalculateChargeTimestampForPower(clampedPower, out targetTimestamp, out resolvedPower))
        {
            clampedPower = resolvedPower;
        }
        else
        {
            targetTimestamp = Time.timeAsDouble;
        }

        bool wroteTimestamp = false;
        bool wroteBackingField = false;
        bool invokedUpdate = false;

        try
        {
            FieldInfo timestampField;
            if (playerGolferFields.TryGetValue("swingPowerTimestamp", out timestampField) && timestampField != null)
            {
                if (timestampField.FieldType == typeof(double))
                {
                    timestampField.SetValue(playerGolfer, targetTimestamp);
                    wroteTimestamp = true;
                }
            }
        }
        catch
        {
        }

        try
        {
            if (swingNormalizedPowerBackingField == null)
            {
                swingNormalizedPowerBackingField = playerGolfer.GetType().GetField("<SwingNormalizedPower>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (swingNormalizedPowerBackingField != null && swingNormalizedPowerBackingField.FieldType == typeof(float))
            {
                swingNormalizedPowerBackingField.SetValue(playerGolfer, clampedPower);
                wroteBackingField = true;
            }
        }
        catch
        {
        }

        try
        {
            if (cachedUpdateSwingNormalizedPowerMethod == null)
            {
                cachedUpdateSwingNormalizedPowerMethod = playerGolfer.GetType().GetMethod("UpdateSwingNormalizedPower", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (cachedUpdateSwingNormalizedPowerMethod != null)
            {
                cachedUpdateSwingNormalizedPowerMethod.Invoke(playerGolfer, cachedUpdateSwingPowerArgs);
                invokedUpdate = true;
            }
        }
        catch
        {
        }

        if (!wroteTimestamp && !wroteBackingField && !invokedUpdate)
        {
            return false;
        }

        appliedPower = clampedPower;
        try
        {
            float currentPower;
            float currentPitch;
            bool isChargingSwing;
            bool isSwinging;
            if (TryGetCurrentSwingValues(out currentPower, out currentPitch, out isChargingSwing, out isSwinging))
            {
                appliedPower = currentPower;
            }
        }
        catch
        {
        }

        lastObservedSwingPower = appliedPower;
        return true;
    }

    private bool ReleaseSwing()
    {
        if (playerGolfer == null)
        {
            return false;
        }

        try
        {
            if (cachedReleaseSwingChargeMethod == null)
            {
                cachedReleaseSwingChargeMethod = playerGolfer.GetType().GetMethod("ReleaseSwingCharge", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (cachedReleaseSwingChargeMethod == null)
            {
                return false;
            }

            cachedReleaseSwingChargeMethod.Invoke(playerGolfer, null);
            return true;
        }
        catch
        {
        }

        isLeftMousePressed = false;
        return false;
    }

    private void AutoSwingRelease()
    {
        if (!assistEnabled || !isLeftMousePressed || playerGolfer == null)
        {
            return;
        }

        if (Time.frameCount == lastAutoSwingReleaseFrame)
        {
            return;
        }

        lastAutoSwingReleaseFrame = Time.frameCount;

        if (autoReleaseTriggeredThisCharge)
        {
            return;
        }

        try
        {
            float currentPower;
            float currentPitch;
            bool isChargingSwing;
            bool isSwinging;
            if (!TryGetCurrentSwingValues(out currentPower, out currentPitch, out isChargingSwing, out isSwinging))
            {
                return;
            }

            bool isAimingSwing = false;
            PropertyInfo aimingProperty;
            if (playerGolferProperties.TryGetValue("IsAimingSwing", out aimingProperty))
            {
                try
                {
                    isAimingSwing = (bool)aimingProperty.GetValue(playerGolfer, null);
                }
                catch
                {
                }
            }

            if (!autoChargeSequenceStarted && !isChargingSwing && isAimingSwing && !isSwinging && Time.time >= nextTryStartChargingTime)
            {
                nextTryStartChargingTime = Time.time + tryStartChargingInterval;
                bool startedCharging;
                TryStartChargingSwingForAutoRelease(out startedCharging);
                if (startedCharging)
                {
                    autoChargeSequenceStarted = true;
                }
                TryGetCurrentSwingValues(out currentPower, out currentPitch, out isChargingSwing, out isSwinging);
            }

            if (autoChargeSequenceStarted && !isChargingSwing && !isSwinging && Time.time >= nextTryStartChargingTime)
            {
                nextTryStartChargingTime = Time.time + tryStartChargingInterval;
                bool startedCharging;
                TryStartChargingSwingForAutoRelease(out startedCharging);
                TryGetCurrentSwingValues(out currentPower, out currentPitch, out isChargingSwing, out isSwinging);
            }

            bool chargeActive = isLeftMousePressed && (isChargingSwing || (autoChargeSequenceStarted && currentPower > 0.005f));
            if (!chargeActive)
            {
                lastObservedSwingPower = 0f;
                return;
            }

            CalculateIdealSwingParameters(false);
            float targetPower = idealSwingPower > 0.0001f ? Mathf.Clamp(idealSwingPower, 0.05f, 2f) : 1f;

            float appliedPower;
            if (!TrySetSwingPower(targetPower, out appliedPower))
            {
                return;
            }

            float postSetPower;
            float postSetPitch;
            bool postSetCharging;
            bool postSetSwinging;
            if (TryGetCurrentSwingValues(out postSetPower, out postSetPitch, out postSetCharging, out postSetSwinging))
            {
                if (!postSetCharging && !postSetSwinging && postSetPower <= 0.001f)
                {
                    return;
                }
            }

            if (ReleaseSwing())
            {
                autoReleaseTriggeredThisCharge = true;
                autoChargeSequenceStarted = false;
                FreezePredictedTrajectorySnapshot(appliedPower, currentPitch);
            }

            lastObservedSwingPower = appliedPower;
        }
        catch
        {
        }
    }
}
