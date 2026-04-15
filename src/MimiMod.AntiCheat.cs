using System;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── Anti-cheat rate awareness (D5 graft) ───────────────────────────────────
    //
    // Super Battle Golf ships an AntiCheat.dll with two classes:
    //
    //   AntiCheatRateChecker           — has fields: expectedMinTimeBetweenHits,
    //                                    rateExceededHitCount, minSuspiciousHitCount,
    //                                    minConfirmedCheatHitCount, hitResetTime.
    //                                    Method: RegisterHit.
    //
    //   AntiCheatPerPlayerRateChecker  — dictionary keyed on Mirror's
    //                                    playerConnectionId / NetworkConnectionToClient,
    //                                    holds an AntiCheatRateChecker per player.
    //                                    Events: PlayerSuspiciousActivityDetected,
    //                                            PlayerConfirmedCheatingDetected.
    //
    // It's a pure rate limiter — no memory scans, no integrity checks, no
    // process hooks. Mimi has no handling for it; automated actions that happen
    // too fast (auto-fire, rapid aim-then-release, weapon assist snap-fire)
    // could trip the suspicious/confirmed thresholds.
    //
    // This module:
    //   1. Subscribes to both detection events (if accessible) and logs a
    //      CANARY message every time we trigger one. This is an early warning
    //      system during graft development, not a response mechanism.
    //
    //   2. Exposes IsActionRateSafe(actionId, minInterval) — a per-action
    //      cooldown tracker that graft code (D4 weapon assist) must call
    //      before performing any automated input. Returns true if enough
    //      time has passed since the last call with the same actionId.
    //
    //   3. Tries to reflect into expectedMinTimeBetweenHits at init and use it
    //      as the dynamic floor (with +10% safety margin). Falls back to a
    //      hardcoded 0.5s if reflection fails.
    //
    // This is AWARENESS, not bypass. We don't patch RegisterHit, we don't
    // silence the events, we don't unhook anything. We just stay below the
    // rate that would trip detection.

    private bool antiCheatInitialized;
    private bool antiCheatEventsSubscribed;
    private Type cachedAntiCheatRateCheckerType;
    private Type cachedAntiCheatPerPlayerRateCheckerType;
    private float cachedExpectedMinTimeBetweenHits = 0.5f;
    private readonly float antiCheatSafetyMargin = 1.1f;
    private readonly Dictionary<string, float> lastAutomatedActionAt = new Dictionary<string, float>(16);

    private void EnsureAntiCheatInitialized()
    {
        if (antiCheatInitialized)
        {
            return;
        }
        antiCheatInitialized = true;

        try
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (cachedAntiCheatRateCheckerType == null)
                {
                    cachedAntiCheatRateCheckerType = assemblies[i].GetType("AntiCheatRateChecker");
                }
                if (cachedAntiCheatPerPlayerRateCheckerType == null)
                {
                    cachedAntiCheatPerPlayerRateCheckerType = assemblies[i].GetType("AntiCheatPerPlayerRateChecker");
                }
                if (cachedAntiCheatRateCheckerType != null && cachedAntiCheatPerPlayerRateCheckerType != null)
                {
                    break;
                }
            }
        }
        catch
        {
        }

        if (cachedAntiCheatRateCheckerType == null)
        {
            MelonLogger.Msg("[SuperHackerGolf] AntiCheat types not resolved — using fallback rate floor 0.5s");
            return;
        }

        TryReadExpectedMinTimeFromInstance();
        TrySubscribeToDetectionEvents();

        MelonLogger.Msg(
            $"[SuperHackerGolf] AntiCheat awareness online. Floor between automated actions: {(cachedExpectedMinTimeBetweenHits * antiCheatSafetyMargin):F3}s");
    }

    private void TryReadExpectedMinTimeFromInstance()
    {
        try
        {
            UnityEngine.Object[] existing = UnityEngine.Object.FindObjectsByType(
                cachedAntiCheatRateCheckerType,
                FindObjectsSortMode.None);

            if (existing == null || existing.Length == 0)
            {
                return;
            }

            FieldInfo field = ModReflectionHelper.GetFieldCascade(
                cachedAntiCheatRateCheckerType,
                "expectedMinTimeBetweenHits",
                "expectedMinTimeBetweenHits", "ExpectedMinTimeBetweenHits", "minTimeBetweenHits");

            if (field == null)
            {
                return;
            }

            for (int i = 0; i < existing.Length; i++)
            {
                try
                {
                    object value = field.GetValue(existing[i]);
                    if (value is float f && f > 0.001f)
                    {
                        // Take the largest observed minimum — safest across all checkers.
                        if (f > cachedExpectedMinTimeBetweenHits)
                        {
                            cachedExpectedMinTimeBetweenHits = f;
                        }
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private void TrySubscribeToDetectionEvents()
    {
        if (antiCheatEventsSubscribed)
        {
            return;
        }

        // Events could live on either type in theory; check both.
        Type[] candidates =
        {
            cachedAntiCheatPerPlayerRateCheckerType,
            cachedAntiCheatRateCheckerType
        };

        for (int c = 0; c < candidates.Length; c++)
        {
            Type t = candidates[c];
            if (t == null) continue;

            TrySubscribeEventOn(t, "PlayerSuspiciousActivityDetected", "[AntiCheat canary] SUSPICIOUS activity event fired");
            TrySubscribeEventOn(t, "PlayerConfirmedCheatingDetected", "[AntiCheat canary] CONFIRMED cheating event fired");
        }

        antiCheatEventsSubscribed = true;
    }

    private void TrySubscribeEventOn(Type t, string eventName, string logMessage)
    {
        try
        {
            EventInfo evt = t.GetEvent(eventName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (evt == null)
            {
                return;
            }

            MethodInfo handler = typeof(SuperHackerGolf).GetMethod(nameof(OnAntiCheatDetectionEventFired), BindingFlags.NonPublic | BindingFlags.Static);
            if (handler == null)
            {
                return;
            }

            // The handler delegate type may be Action<...> or a custom delegate — build a matching one.
            Type handlerType = evt.EventHandlerType;
            if (handlerType == null)
            {
                return;
            }

            Delegate wrapped;
            try
            {
                // Try the simplest case first — Action or parameterless Action.
                wrapped = Delegate.CreateDelegate(handlerType, handler, false);
            }
            catch
            {
                wrapped = null;
            }

            if (wrapped == null)
            {
                // Couldn't bind — fall back to logging that the event exists but we can't hook.
                MelonLogger.Msg($"[SuperHackerGolf] {t.Name}.{eventName} event present but delegate type {handlerType.Name} couldn't be bound; skipping canary");
                return;
            }

            MethodInfo addMethod = evt.GetAddMethod(true);
            if (addMethod == null)
            {
                return;
            }

            object instance = null;
            if (!addMethod.IsStatic)
            {
                UnityEngine.Object[] existing = UnityEngine.Object.FindObjectsByType(t, FindObjectsSortMode.None);
                if (existing != null && existing.Length > 0)
                {
                    instance = existing[0];
                }
                else
                {
                    return;
                }
            }

            addMethod.Invoke(instance, new object[] { wrapped });
            MelonLogger.Msg($"[SuperHackerGolf] Hooked anti-cheat canary: {t.Name}.{eventName}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] anti-cheat canary subscribe failed on {t.Name}.{eventName}: {ex.GetType().Name}");
        }
    }

    // E18: the real signature is Action<int> (connection id). Previously
    // this was parameterless, which made Delegate.CreateDelegate silently
    // return null and skip binding, so the canary never fired even when
    // thresholds were crossed. Fixed to take the connId parameter.
    private static void OnAntiCheatDetectionEventFired(int playerConnectionId)
    {
        MelonLogger.BigError(
            "MimiMod",
            $"ANTI-CHEAT DETECTION EVENT FIRED for connection {playerConnectionId} — an automated action crossed a suspicious or confirmed-cheating threshold. " +
            "The BNetworkManager kick patches SHOULD swallow this, but if you got kicked anyway inspect which Harmony patches actually applied at startup.");
    }

    /// <summary>
    /// Returns true if enough time has passed since the last automated action
    /// with the same actionId. Graft code MUST call this before performing any
    /// synthetic input (auto-fire, weapon assist snap, etc.). The minimum
    /// interval respects the game's own expectedMinTimeBetweenHits + 10%
    /// safety margin, or 0.5s if the real value isn't readable.
    /// </summary>
    internal bool IsActionRateSafe(string actionId)
    {
        EnsureAntiCheatInitialized();

        float floor = cachedExpectedMinTimeBetweenHits * antiCheatSafetyMargin;
        float now = Time.time;

        if (lastAutomatedActionAt.TryGetValue(actionId, out float last))
        {
            if (now - last < floor)
            {
                return false;
            }
        }

        lastAutomatedActionAt[actionId] = now;
        return true;
    }
}
