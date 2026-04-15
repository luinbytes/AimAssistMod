using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

public partial class SuperHackerGolf
{
    // ── Anti-cheat bypass (D5 rewrite) ─────────────────────────────────────────
    //
    // D5 was awareness-only — we stayed under the detection thresholds. User
    // wants a real bypass now. Decompiled AntiCheat.dll with Mono.Cecil:
    //
    //   public bool AntiCheatRateChecker.RegisterHit()
    //     - measures time since last hit
    //     - if timeSince > expectedMinTimeBetweenHits: returns true (OK)
    //     - else increments rateExceededHitCount; if it crosses
    //       minSuspiciousHitCount or minConfirmedCheatHitCount, fires the
    //       detection events and returns FALSE (rate-limited)
    //
    //   public bool AntiCheatPerPlayerRateChecker.RegisterHit(NetworkConnectionToClient)
    //     - looks up or creates a per-connection AntiCheatRateChecker
    //     - delegates to the inner RegisterHit
    //
    // The rate limiters are members of Hittable (serverHitWithGolfSwingCommandRateLimiter,
    // serverHitWithSwingProjectileCommandRateLimiter, etc.) and are called from the
    // [Command] handlers (CmdHitWithGolfSwing etc.) — so they run SERVER-SIDE.
    //
    // Patching both RegisterHit methods with a prefix that sets __result=true
    // and skips the original disables every rate check on whichever instance
    // runs this mod. For solo/host play this fully bypasses detection; for a
    // non-host client, the host still enforces its own limits.
    //
    // Uses runtime reflection to find the AntiCheat types so the mod doesn't
    // need to reference AntiCheat.dll at compile time.

    private static bool antiCheatBypassInstalled;
    // E19: client-side kick suppression. When the host sends a
    // DisconnectReasonMessage (vote kick, host menu kick, anti-cheat kick),
    // the client-side handler calls DisplayDisconnectReasonMessage, and then
    // Mirror's transport layer fires OnClientDisconnectInternal which shuts
    // down the NetworkClient and loads the offline scene. We can't stop the
    // host from telling its transport to close the socket, but we CAN stop
    // our own Mirror state machine from treating it as a kick:
    //   1. OnClientDisconnectReasonMessage prefix skips + sets a suppression
    //      flag with a 10s window.
    //   2. DisplayDisconnectReasonMessage prefix always skips (no UI).
    //   3. OnClientDisconnectInternal prefix conditionally skips only while
    //      the suppression window is active, so normal clean quits cascade
    //      through Mirror's cleanup path unaffected.
    private static bool kickSuppressActive;
    private static double kickSuppressExpiresAt;

    internal void TryInstallAntiCheatBypass()
    {
        if (antiCheatBypassInstalled)
        {
            return;
        }
        antiCheatBypassInstalled = true;

        try
        {
            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("com.lumods.mimimod.anticheatbypass");

            Type rateCheckerType = null;
            Type perPlayerType = null;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (rateCheckerType == null)
                {
                    rateCheckerType = assemblies[i].GetType("AntiCheatRateChecker");
                }
                if (perPlayerType == null)
                {
                    perPlayerType = assemblies[i].GetType("AntiCheatPerPlayerRateChecker");
                }
                if (rateCheckerType != null && perPlayerType != null)
                {
                    break;
                }
            }

            int patched = 0;

            if (rateCheckerType != null)
            {
                MethodInfo target = AccessTools.Method(rateCheckerType, "RegisterHit", Type.EmptyTypes);
                if (target != null)
                {
                    MethodInfo prefix = typeof(SuperHackerGolf).GetMethod(
                        nameof(AntiCheatRegisterHitPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    harmony.Patch(target, new HarmonyMethod(prefix));
                    patched++;
                    MelonLogger.Msg("[SuperHackerGolf] Patched AntiCheatRateChecker.RegisterHit");
                }
                else
                {
                    MelonLogger.Warning("[SuperHackerGolf] AntiCheatRateChecker.RegisterHit method not found for patching");
                }
            }
            else
            {
                MelonLogger.Warning("[SuperHackerGolf] AntiCheatRateChecker type not found — bypass will be partial");
            }

            if (perPlayerType != null)
            {
                // The per-player method takes a NetworkConnectionToClient — resolve by name to avoid a Mirror reference.
                MethodInfo target = null;
                foreach (var m in perPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.Name == "RegisterHit" && m.GetParameters().Length == 1)
                    {
                        target = m;
                        break;
                    }
                }

                if (target != null)
                {
                    MethodInfo prefix = typeof(SuperHackerGolf).GetMethod(
                        nameof(AntiCheatPerPlayerRegisterHitPrefix),
                        BindingFlags.NonPublic | BindingFlags.Static);
                    harmony.Patch(target, new HarmonyMethod(prefix));
                    patched++;
                    MelonLogger.Msg("[SuperHackerGolf] Patched AntiCheatPerPlayerRateChecker.RegisterHit");
                }
                else
                {
                    MelonLogger.Warning("[SuperHackerGolf] AntiCheatPerPlayerRateChecker.RegisterHit method not found for patching");
                }
            }

            // E18: BNetworkManager kick-path patches.
            //
            // The RegisterHit patches above only help when THIS client is the
            // host (BNetworkManager's static event handler only runs in the
            // process that contains the active NetworkServer). If you join
            // someone else's lobby as a guest, the unmodified host still
            // detects your rate violations and kicks you. The kick pipeline,
            // reversed from GameAssembly.dll:
            //
            //   AntiCheatRateChecker.RegisterHit() → static event
            //     PlayerConfirmedCheatingDetected(int connId)
            //   → BNetworkManager.OnPlayerConfirmedCheatingDetected(int)
            //       (subscribed in Awake, logs "…confirmed cheating; kicking")
            //   → BNetworkManager.ServerKickConnection(NetworkConnectionToClient)
            //       → BanPlayerGuidThisSession(ulong)  (adds guid to ban list)
            //       → ServerDisconnectClientWithMessage(..., KickedFromLobby)
            //       → Mirror.NetworkConnection.Disconnect()
            //
            // Neutering OnPlayerConfirmedCheatingDetected, ServerKickConnection,
            // and BanPlayerGuidThisSession as no-op prefixes gives belt-and-
            // braces host-side immunity. When hosting, your guests also become
            // unkickable — which is the whole point if you're running the mod
            // while hosting. When you're a guest, these prefixes are inert
            // (the host's unmodified code makes the kick decision).

            Type bnetmType = null;
            Type mirrorNetMgrType = null;
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (bnetmType == null)
                {
                    bnetmType = assemblies[i].GetType("BNetworkManager");
                }
                if (mirrorNetMgrType == null)
                {
                    mirrorNetMgrType = assemblies[i].GetType("Mirror.NetworkManager");
                }
                if (bnetmType != null && mirrorNetMgrType != null) break;
            }

            if (bnetmType != null)
            {
                MethodInfo skipPrefix = typeof(SuperHackerGolf).GetMethod(
                    nameof(AntiCheatSkipPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                patched += TryPatchVoidSkip(harmony, bnetmType, "OnPlayerConfirmedCheatingDetected", skipPrefix);
                patched += TryPatchVoidSkip(harmony, bnetmType, "ServerKickConnection", skipPrefix);
                patched += TryPatchVoidSkip(harmony, bnetmType, "BanPlayerGuidThisSession", skipPrefix);

                // E19: client-side manual kick suppression.
                // DisplayDisconnectReasonMessage always skipped — no UI dialog.
                patched += TryPatchVoidSkip(harmony, bnetmType, "DisplayDisconnectReasonMessage", skipPrefix);

                // OnClientDisconnectReasonMessage: use the kick-arming prefix
                // so we set the suppression flag before skipping.
                MethodInfo armPrefix = typeof(SuperHackerGolf).GetMethod(
                    nameof(KickSuppressArmPrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);
                foreach (var m in bnetmType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.Name != "OnClientDisconnectReasonMessage") continue;
                    try
                    {
                        harmony.Patch(m, new HarmonyMethod(armPrefix));
                        MelonLogger.Msg($"[SuperHackerGolf] Patched BNetworkManager.OnClientDisconnectReasonMessage (arm-suppress)");
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[SuperHackerGolf] Arm-suppress patch failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                MelonLogger.Warning("[SuperHackerGolf] BNetworkManager type not found — kick bypass skipped");
            }

            if (mirrorNetMgrType != null)
            {
                // Mirror.NetworkManager.OnClientDisconnectInternal() — the
                // cleanup cascade that runs `NetworkClient.Shutdown()` and
                // loads the offline scene when any transport-level disconnect
                // happens. Gated on the kick-suppression flag so normal quits
                // still work.
                MethodInfo gatePrefix = typeof(SuperHackerGolf).GetMethod(
                    nameof(KickSuppressGatePrefix),
                    BindingFlags.NonPublic | BindingFlags.Static);

                MethodInfo target = null;
                foreach (var m in mirrorNetMgrType.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (m.Name == "OnClientDisconnectInternal" && m.GetParameters().Length == 0)
                    {
                        target = m;
                        break;
                    }
                }
                if (target != null)
                {
                    try
                    {
                        harmony.Patch(target, new HarmonyMethod(gatePrefix));
                        MelonLogger.Msg("[SuperHackerGolf] Patched Mirror.NetworkManager.OnClientDisconnectInternal (gate-suppress)");
                        patched++;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[SuperHackerGolf] OnClientDisconnectInternal patch failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                else
                {
                    MelonLogger.Warning("[SuperHackerGolf] Mirror.NetworkManager.OnClientDisconnectInternal not found");
                }
            }
            else
            {
                MelonLogger.Warning("[SuperHackerGolf] Mirror.NetworkManager type not found — client-side kick gate skipped");
            }

            if (patched == 0)
            {
                MelonLogger.Warning("[SuperHackerGolf] Anti-cheat bypass installed 0 patches — types unavailable at startup");
            }
            else
            {
                MelonLogger.Msg($"[SuperHackerGolf] Anti-cheat bypass online ({patched} patches applied)");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Anti-cheat bypass install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int TryPatchVoidSkip(HarmonyLib.Harmony harmony, Type declaringType, string methodName, MethodInfo prefix)
    {
        // Any overload of the named method gets patched — the kick pipeline
        // names are unique enough that overload collisions aren't a concern.
        int count = 0;
        try
        {
            MethodInfo[] candidates = declaringType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i].Name != methodName) continue;
                try
                {
                    harmony.Patch(candidates[i], new HarmonyMethod(prefix));
                    MelonLogger.Msg($"[SuperHackerGolf] Patched {declaringType.Name}.{methodName}({candidates[i].GetParameters().Length} args)");
                    count++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[SuperHackerGolf] Patch failed: {declaringType.Name}.{methodName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (count == 0)
            {
                MelonLogger.Warning($"[SuperHackerGolf] {declaringType.Name}.{methodName} not found for patching");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] TryPatchVoidSkip error on {declaringType.Name}.{methodName}: {ex.GetType().Name}: {ex.Message}");
        }
        return count;
    }

    // Harmony prefix: set __result = true, return false to skip the original body.
    private static bool AntiCheatRegisterHitPrefix(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static bool AntiCheatPerPlayerRegisterHitPrefix(ref bool __result)
    {
        __result = true;
        return false;
    }

    // E18: generic skip prefix for void kick-path methods. Returning false
    // tells Harmony to skip the original body entirely. The patched methods
    // (OnPlayerConfirmedCheatingDetected, ServerKickConnection,
    // BanPlayerGuidThisSession) all return void, so no __result handling.
    private static bool AntiCheatSkipPrefix()
    {
        return false;
    }

    // E19: client-side kick arm. Prefix for BNetworkManager.OnClientDisconnectReasonMessage.
    // The host sends this message JUST BEFORE closing our transport connection,
    // so this is our only reliable signal that the next OnClientDisconnectInternal
    // is a kick, not a legitimate disconnect. We arm the suppression flag with
    // a 10-second window, then skip the original (suppresses the kick UI).
    private static bool KickSuppressArmPrefix()
    {
        try
        {
            kickSuppressActive = true;
            kickSuppressExpiresAt = UnityEngine.Time.realtimeSinceStartupAsDouble + 10.0;
            MelonLogger.Msg("[SuperHackerGolf] Kick message intercepted — suppression armed for 10s");
        }
        catch { }
        return false;
    }

    // E19: gated prefix for Mirror.NetworkManager.OnClientDisconnectInternal.
    // If the arm prefix fired within the last 10s, skip the Mirror cleanup
    // cascade (NetworkClient.Shutdown, offlineScene load). Otherwise let
    // Mirror run normally so clean quits still work.
    private static bool KickSuppressGatePrefix()
    {
        try
        {
            if (!kickSuppressActive) return true;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now > kickSuppressExpiresAt)
            {
                kickSuppressActive = false;
                return true;
            }
            MelonLogger.Msg("[SuperHackerGolf] OnClientDisconnectInternal suppressed (kick window active)");
            // Consume the flag — one kick suppression per arm, so a second
            // legitimate disconnect in the same window still falls through.
            kickSuppressActive = false;
            return false;
        }
        catch
        {
            return true;
        }
    }
}
