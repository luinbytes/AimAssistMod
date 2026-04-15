using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

public partial class SuperHackerGolf
{
    // ── E24: weapon aimbot + mine hit automation + bunnyhop ───────────────────
    //
    // Reverse-engineered approach, all findings from static IL analysis of
    // GameAssembly.dll:
    //
    // === Weapon aimbot ===
    //
    // PlayerInventory fires hitscan weapons (DuelingPistol, ElephantGun) 100%
    // client-side via a nested local function `<ShootDuelingPistolRoutine>g__Shoot|164_0`
    // (and |165_0 for the elephant gun). The local function calls
    // `PlayerInventory::GetFirearmAimPoint(float maxDistance, int layerMask,
    // out float localYaw)` to resolve WHERE the shot goes, then does a
    // `Physics.RaycastNonAlloc` + calls `Hittable::HitWithItem` directly on
    // whatever it hits. No Cmd. No server validation for hitscan aim.
    //
    // The only "aim check" is `if (Abs(localYaw) > 45f) AlignWithCameraImmediately();`
    // which forces the camera to snap toward the target but DOES NOT abort.
    //
    // Fix: Harmony **postfix** on `GetFirearmAimPoint`. When weaponAssistEnabled
    // is true and the local player has a weapon equipped, overwrite the returned
    // Vector3 with the nearest lock-on target's GetLockOnPosition() and set
    // `localYaw = 0f` so the realign branch is skipped. Next time the coroutine
    // raycasts, it raycasts to our target — and `HitWithItem` fires.
    //
    // === Mine hit automation ===
    //
    // `Landmine::CanExplodeOnCollisionWith` does NOT check `isArmed` — it just
    // returns true for players / golf carts / target dummies. The actual
    // `isArmed` gate lives in TWO places:
    //   1. `Landmine::OnCollisionEnter` IL_0008: `if (!isArmed) return;`
    //   2. `Landmine::OnServerWasHitByGolfSwing` IL_0000: `if (!isArmed) { ServerArmDelayed(...); return; }`
    //
    // `forceArmed` is ONLY read in OnStartServer (spawn-time override); writing
    // it at runtime is a no-op. Fix: Harmony prefix `OnServerWasHitByGolfSwing`
    // (and `OnCollisionEnter` belt-and-braces) to set `isArmed = true` before
    // the original body runs. This is server-side code — patches only fire
    // when YOU are the host. Non-host clients can't pre-arm other players'
    // mines (Landmine is server-authoritative).
    //
    // === Bunnyhop ===
    //
    // `PlayerMovement::TryTriggerJump()` has ZERO rate limiter, ZERO cooldown,
    // ZERO hang-time enforcement. `CanJump()` gates only on:
    //   - isGrounded (true required)
    //   - !IsMatchResolved
    //   - !IsSpectating
    //   - CanInterruptSwing (true required)
    //   - !(EquippedItem == Landmine && IsUsingItemAtAll)
    //
    // TriggerJumpInternal sets velocity.y = JumpUpwardsSpeed + Unground(false)
    // immediately. Simple "call TryTriggerJump every frame while grounded" is
    // the canonical bhop pattern — the very first call returns true + un-grounds
    // the player, subsequent calls return false until the next landing.

    private bool weaponAssistReflectionInitialized;
    private bool weaponAssistReflectionAvailable;
    private static bool weaponAssistRuntimeEnabled;   // static so Harmony postfix can read it
    private static Vector3 weaponAssistOverrideAim;   // static target pos fed to the postfix
    private static bool weaponAssistOverrideAimValid; // static flag — is there a valid target right now

    // Reflection for LockOn target enumeration + target-position lookup.
    private Type cachedLockOnTargetType;
    private Type cachedLockOnTargetUiManagerType;
    private MethodInfo cachedLockOnTargetGetPositionMethod;
    private MethodInfo cachedLockOnTargetIsValidMethod;
    private FieldInfo cachedLockOnTargetUiManagerActiveTargetsField;

    // Harmony patch install state
    private static bool weaponAssistHarmonyInstalled;
    private static bool mineAssistHarmonyInstalled;

    // Auto-fire reflection — PlayerInventory.TryUseItem(bool, out bool).
    private MethodInfo cachedPlayerInventoryTryUseItemMethod;
    private object[] cachedTryUseItemArgs;
    private double weaponAutoFireLastShotTime;
    // E28 auto-fire failure disabler — once Invoke throws, subsequent
    // reflection calls in the same Mono AppDomain can cascade-fail (zero-rva)
    // under Wine/Proton. Disable auto-fire permanently on first failure.
    private bool autoFireBrokenForSession;

    // Target classification types — resolved at weapon-assist init so the
    // per-frame filter doesn't re-search assemblies.
    private Type cachedTargetDummyType;
    private Type cachedLandmineTypeForFilter;
    private Type cachedPlayerInfoTypeForFilter;
    private Type cachedGolfCartTypeForFilter;

    // Mine reflection for the Landmine SyncVar setter
    private static Type cachedLandmineTypeStatic;
    private static PropertyInfo cachedLandmineNetworkIsArmedSetter;
    private static FieldInfo cachedLandmineIsArmedFieldStatic;

    // Bunnyhop reflection
    private bool bunnyhopReflectionInitialized;
    private bool bunnyhopReflectionAvailable;
    private MethodInfo cachedPlayerMovementTryTriggerJumpMethod;
    private PropertyInfo cachedPlayerMovementIsGroundedProperty;

    internal void EnsureWeaponAssistReflectionInitialized()
    {
        if (weaponAssistReflectionInitialized) return;
        weaponAssistReflectionInitialized = true;

        try
        {
            Type lockOnTargetType = null;
            Type lockOnUiMgrType = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                if (lockOnTargetType == null) lockOnTargetType = asms[i].GetType("LockOnTarget");
                if (lockOnUiMgrType == null) lockOnUiMgrType = asms[i].GetType("LockOnTargetUiManager");
                if (lockOnTargetType != null && lockOnUiMgrType != null) break;
            }

            if (lockOnTargetType == null || lockOnUiMgrType == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Weapon assist: LockOn types not found");
                return;
            }

            cachedLockOnTargetType = lockOnTargetType;
            cachedLockOnTargetUiManagerType = lockOnUiMgrType;

            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            cachedLockOnTargetGetPositionMethod = lockOnTargetType.GetMethod("GetLockOnPosition", bf, null, Type.EmptyTypes, null);
            cachedLockOnTargetIsValidMethod = lockOnTargetType.GetMethod("IsValidForLocalPlayer", bf, null, new Type[] { typeof(bool) }, null);
            cachedLockOnTargetUiManagerActiveTargetsField = lockOnUiMgrType.GetField("activeTargets", bf);

            weaponAssistReflectionAvailable =
                cachedLockOnTargetGetPositionMethod != null &&
                cachedLockOnTargetUiManagerActiveTargetsField != null;

            // E26b: resolve classifier types so target-type filter is cheap.
            for (int i = 0; i < asms.Length; i++)
            {
                if (cachedTargetDummyType == null) cachedTargetDummyType = asms[i].GetType("TargetDummy");
                if (cachedLandmineTypeForFilter == null) cachedLandmineTypeForFilter = asms[i].GetType("Landmine");
                if (cachedPlayerInfoTypeForFilter == null) cachedPlayerInfoTypeForFilter = asms[i].GetType("PlayerInfo");
                if (cachedGolfCartTypeForFilter == null) cachedGolfCartTypeForFilter = asms[i].GetType("GolfCartBase");
                if (cachedTargetDummyType != null && cachedLandmineTypeForFilter != null && cachedPlayerInfoTypeForFilter != null && cachedGolfCartTypeForFilter != null) break;
            }

            InstallWeaponAssistHarmonyPatches();

            MelonLogger.Msg(
                $"[SuperHackerGolf] Weapon assist reflection: " +
                $"GetLockOnPosition={(cachedLockOnTargetGetPositionMethod != null ? "Y" : "n")} " +
                $"activeTargets field={(cachedLockOnTargetUiManagerActiveTargetsField != null ? "Y" : "n")} " +
                $"ready={weaponAssistReflectionAvailable}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Weapon assist reflection init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void InstallWeaponAssistHarmonyPatches()
    {
        if (weaponAssistHarmonyInstalled) return;

        try
        {
            Type playerInventoryType = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                playerInventoryType = asms[i].GetType("PlayerInventory");
                if (playerInventoryType != null) break;
            }
            if (playerInventoryType == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Weapon assist: PlayerInventory type not found");
                return;
            }

            // GetFirearmAimPoint(float maxDistance, int layerMask, out float localYaw) : Vector3
            MethodInfo target = null;
            MethodInfo[] methods = playerInventoryType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "GetFirearmAimPoint") continue;
                if (methods[i].ReturnType != typeof(Vector3)) continue;
                var pars = methods[i].GetParameters();
                if (pars.Length != 3) continue;
                target = methods[i];
                break;
            }
            if (target == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Weapon assist: PlayerInventory.GetFirearmAimPoint not found");
                return;
            }

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("com.lumods.superhackergolf.weaponassist");
            MethodInfo postfix = typeof(SuperHackerGolf).GetMethod(
                nameof(WeaponAssistGetFirearmAimPointPostfix),
                BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, null, new HarmonyMethod(postfix));
            weaponAssistHarmonyInstalled = true;
            MelonLogger.Msg("[SuperHackerGolf] Patched PlayerInventory.GetFirearmAimPoint (weapon aimbot postfix)");

            // Resolve TryUseItem(bool, out bool) for auto-fire.
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name != "TryUseItem") continue;
                var pars = methods[i].GetParameters();
                if (pars.Length != 2) continue;
                if (pars[0].ParameterType != typeof(bool)) continue;
                if (!pars[1].ParameterType.IsByRef) continue;
                cachedPlayerInventoryTryUseItemMethod = methods[i];
                cachedTryUseItemArgs = new object[] { false, false };
                MelonLogger.Msg("[SuperHackerGolf] Resolved PlayerInventory.TryUseItem for weapon auto-fire");
                break;
            }
            if (cachedPlayerInventoryTryUseItemMethod == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Weapon auto-fire: PlayerInventory.TryUseItem not found");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Weapon assist Harmony install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Harmony postfix: runs AFTER the original GetFirearmAimPoint completes.
    // Original returns a Vector3 aim point and writes the signed yaw delta to
    // the out parameter. We overwrite both: the aim point becomes our lock-on
    // target position, and the yaw delta becomes 0 so the "abs(yaw) > 45 →
    // AlignWithCameraImmediately" branch in the Shoot coroutine is skipped.
    //
    // Signature must match the patched method: `(float maxDistance, int layerMask, ref float localYaw)` + `ref Vector3 __result`.
    private static double weaponAimPostfixLastLogTime;
    private static int weaponAimPostfixCallCount;
    private static bool weaponAimPostfixEverCalled;
    private static void WeaponAssistGetFirearmAimPointPostfix(ref Vector3 __result, ref float localYaw)
    {
        weaponAimPostfixEverCalled = true;
        weaponAimPostfixCallCount++;

        // Rate-limited diagnostic: once per second, log whether we're actually
        // overriding. This is how we confirm the patch fires at all.
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
        if (now - weaponAimPostfixLastLogTime > 1.0)
        {
            weaponAimPostfixLastLogTime = now;
            MelonLogger.Msg($"[SuperHackerGolf] Aimbot postfix hit — calls/s={weaponAimPostfixCallCount} runtimeEnabled={weaponAssistRuntimeEnabled} overrideValid={weaponAssistOverrideAimValid}");
            weaponAimPostfixCallCount = 0;
        }

        if (!weaponAssistRuntimeEnabled) return;
        if (!weaponAssistOverrideAimValid) return;
        __result = weaponAssistOverrideAim;
        localYaw = 0f;
    }

    internal void InstallMineAssistHarmonyPatches()
    {
        if (mineAssistHarmonyInstalled) return;

        try
        {
            Type landmineType = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                landmineType = asms[i].GetType("Landmine");
                if (landmineType != null) break;
            }
            if (landmineType == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Mine assist: Landmine type not found");
                return;
            }
            cachedLandmineTypeStatic = landmineType;

            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            // NetworkisArmed is the SyncVar setter — preferred write path (fires hooks + dirty-bit mark).
            cachedLandmineNetworkIsArmedSetter = landmineType.GetProperty("NetworkisArmed", bf);
            cachedLandmineIsArmedFieldStatic = landmineType.GetField("isArmed", bf);

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("com.lumods.superhackergolf.mineassist");
            MethodInfo prefix = typeof(SuperHackerGolf).GetMethod(
                nameof(MineAssistForceArmedPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);

            int patched = 0;

            MethodInfo onHit = landmineType.GetMethod("OnServerWasHitByGolfSwing", bf);
            if (onHit != null)
            {
                harmony.Patch(onHit, new HarmonyMethod(prefix));
                patched++;
                MelonLogger.Msg("[SuperHackerGolf] Patched Landmine.OnServerWasHitByGolfSwing (force-arm prefix)");
            }

            MethodInfo onColl = landmineType.GetMethod("OnCollisionEnter", bf);
            if (onColl != null)
            {
                harmony.Patch(onColl, new HarmonyMethod(prefix));
                patched++;
                MelonLogger.Msg("[SuperHackerGolf] Patched Landmine.OnCollisionEnter (force-arm prefix)");
            }

            mineAssistHarmonyInstalled = patched > 0;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Mine assist Harmony install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Harmony prefix: before the original Landmine hit handler runs, force
    // isArmed = true so the `if (!isArmed) return;` gate at the top of the
    // original body passes. We let the original run afterward by returning
    // true. This runs server-side — only effective when the local client is
    // the host.
    private static bool MineAssistForceArmedPrefix(object __instance)
    {
        // Gate on the user-facing toggle so non-hosts aren't wasting cycles on
        // instances they can't patch effectively.
        try
        {
            if (__instance == null) return true;
            if (cachedLandmineNetworkIsArmedSetter != null && cachedLandmineNetworkIsArmedSetter.CanWrite)
            {
                cachedLandmineNetworkIsArmedSetter.SetValue(__instance, true, null);
            }
            else if (cachedLandmineIsArmedFieldStatic != null)
            {
                cachedLandmineIsArmedFieldStatic.SetValue(__instance, true);
            }
        }
        catch { }
        return true;  // let the original body run
    }

    internal void EnsureBunnyhopReflectionInitialized()
    {
        if (bunnyhopReflectionInitialized && bunnyhopReflectionAvailable) return;
        if (playerMovement == null) return;

        bunnyhopReflectionInitialized = true;

        try
        {
            Type pmType = playerMovement.GetType();
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            cachedPlayerMovementTryTriggerJumpMethod = pmType.GetMethod("TryTriggerJump", bf, null, Type.EmptyTypes, null);
            cachedPlayerMovementIsGroundedProperty = pmType.GetProperty("IsGrounded", bf);

            bunnyhopReflectionAvailable =
                cachedPlayerMovementTryTriggerJumpMethod != null &&
                cachedPlayerMovementIsGroundedProperty != null;

            MelonLogger.Msg(
                $"[SuperHackerGolf] Bunnyhop reflection: " +
                $"TryTriggerJump={(cachedPlayerMovementTryTriggerJumpMethod != null ? "Y" : "n")} " +
                $"IsGrounded={(cachedPlayerMovementIsGroundedProperty != null ? "Y" : "n")} " +
                $"ready={bunnyhopReflectionAvailable}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Bunnyhop reflection init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal void ToggleWeaponAssist()
    {
        // E28: F7 now toggles between Off and the last-selected active mode
        // (default Legit if never set). The GUI exposes the mode picker for
        // finer control.
        if (aimbotMode == AimbotMode.Off)
        {
            SetAimbotMode(AimbotMode.Legit);  // default enable = legit
        }
        else
        {
            SetAimbotMode(AimbotMode.Off);
        }
    }

    internal void SetAimbotMode(AimbotMode mode)
    {
        AimbotMode prev = aimbotMode;
        aimbotMode = mode;
        weaponAssistRuntimeEnabled = (mode != AimbotMode.Off);
        // E28b: reset smoothing + sticky-lock state on mode change so we
        // don't carry stale aim across enable/disable cycles.
        aimbotHasSmoothedAim = false;
        aimbotHasLockedTarget = false;
        aimbotSmoothVelocity = Vector3.zero;
        weaponAssistOverrideAimValid = false;
        MelonLogger.Msg($"[SuperHackerGolf] Aimbot mode: {prev} -> {mode}");
        MarkHudDirty();

        // Apply mode presets — only overwrite the affected sub-settings when
        // switching TO a preset mode so the user can fine-tune within it.
        // Auto-fire is DEFAULTED OFF in both presets because the reflected
        // TryUseItem path is unstable on this runtime and poisons other
        // reflection calls when it throws. User opts in knowingly.
        switch (mode)
        {
            case AimbotMode.Legit:
                weaponAssistConeAngleDeg = 15f;
                weaponAssistMaxRange = 60f;
                aimbotSmoothingFactor = 50f;             // 0.5 smooth = visible drift but responsive
                aimbotSmoothingCurve = AimbotSmoothingCurve.EaseOut;
                targetSelectionMode = TargetSelectionMode.ClosestToCrosshair;
                weaponAutoFireEnabled = false;            // manual fire
                aimbotActivation = AimbotActivation.Always;
                weaponAssistVisibilityCheck = false;      // E28b: disable LOS until self-collision is fixed
                weaponAssistSkipProtected = true;
                aimbotStickyLockMs = 250f;
                aimbotHitboxFlags = HitboxFlags.Chest;   // legit stays center-mass
                weaponAssistTargetPlayers = true;
                weaponAssistTargetDummies = true;
                break;
            case AimbotMode.Rage:
                weaponAssistConeAngleDeg = 180f;         // full FOV
                weaponAssistMaxRange = 150f;
                aimbotSmoothingFactor = 0f;               // instant snap
                targetSelectionMode = TargetSelectionMode.BestScore;
                weaponAutoFireEnabled = false;            // OFF by default — user opts in
                weaponAutoFireMinIntervalSec = 0.2f;      // 5 shots/sec when enabled
                aimbotActivation = AimbotActivation.Always;
                weaponAssistVisibilityCheck = false;
                weaponAssistSkipProtected = true;
                aimbotStickyLockMs = 50f;
                aimbotHitboxFlags = HitboxFlags.Head;    // headshot default
                weaponAssistTargetPlayers = true;
                weaponAssistTargetDummies = true;
                break;
            case AimbotMode.Custom:
            case AimbotMode.Off:
                // Keep current settings — user is managing them manually.
                break;
        }

        if (mode != AimbotMode.Off)
        {
            try { EnsureWeaponAssistReflectionInitialized(); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SuperHackerGolf] Weapon assist init threw {ex.GetType().Name}: {ex.Message} — reverting to Off");
                aimbotMode = AimbotMode.Off;
                weaponAssistRuntimeEnabled = false;
            }
        }
    }

    internal void ToggleMineAssist()
    {
        mineAssistEnabled = !mineAssistEnabled;
        MelonLogger.Msg($"[SuperHackerGolf] Mine assist: {(mineAssistEnabled ? "ON — host-only effect" : "OFF")}");
        MarkHudDirty();
        if (mineAssistEnabled)
        {
            try { InstallMineAssistHarmonyPatches(); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SuperHackerGolf] Mine assist init threw {ex.GetType().Name}: {ex.Message} — disabling");
                mineAssistEnabled = false;
            }
        }
    }

    internal void ToggleBunnyhop()
    {
        bunnyhopEnabled = !bunnyhopEnabled;
        MelonLogger.Msg($"[SuperHackerGolf] Bunnyhop: {(bunnyhopEnabled ? "ON" : "OFF")}");
        MarkHudDirty();
        if (bunnyhopEnabled)
        {
            try { EnsureBunnyhopReflectionInitialized(); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SuperHackerGolf] Bunnyhop init threw {ex.GetType().Name}: {ex.Message} — disabling");
                bunnyhopEnabled = false;
            }
        }
    }

    /// <summary>
    /// Called every frame from OnUpdate. Each feature gates itself when disabled.
    /// </summary>
    internal void TickCombatAssists()
    {
        TickBunnyhop();
        TickWeaponAssistTargetSelection();
        // Mine assist has no per-frame work — it's Harmony patches only.
    }

    /// <summary>
    /// Called every frame while weapon assist is on: walks the active lock-on
    /// target set, picks the best target within our cone, and stores its
    /// position in a static field that the GetFirearmAimPoint Harmony postfix
    /// reads. When no target is valid we clear the override so shots fall
    /// through to vanilla behaviour.
    /// </summary>
    private double weaponAssistLastLogTime;
    private int weaponAssistEvalFrames;
    private int weaponAssistEvalValidWeapon;
    private int weaponAssistEvalHaveTargets;
    private int weaponAssistEvalFoundInCone;
    private int weaponAssistLastTargetCount;
    private bool weaponAssistLastLoggedActiveState;
    internal void TickWeaponAssistTargetSelection()
    {
        weaponAssistOverrideAimValid = false;

        if (aimbotMode == AimbotMode.Off) return;
        if (!weaponAssistReflectionAvailable) return;
        if (playerGolfer == null || playerMovement == null) return;

        // E28: activation mode — LEGIT typically wants hold-to-aim, RAGE
        // wants always-on. Check the HoldKey state if HoldKey mode active.
        if (aimbotActivation == AimbotActivation.HoldKey)
        {
            if (!IsAimbotActivationKeyHeld()) return;
        }

        weaponAssistEvalFrames++;

        // Only supply a target when the local player is actually holding a
        // weapon; otherwise we'd hijack the golf swing aim too (GetFirearmAimPoint
        // is only called from firearm shoot coroutines so it shouldn't, but
        // belt-and-braces).
        bool weaponHeld = IsLocalPlayerHoldingAimableWeapon();
        if (weaponHeld) weaponAssistEvalValidWeapon++;

        // E26b: trimmed diagnostic — previously spammed every 1s regardless
        // of state. Now only logs when the aimbot's active state CHANGES
        // (weapon swapped in/out, or found/lost-target transition). During
        // steady-state gameplay this stays silent.
        double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
        if (now - weaponAssistLastLogTime > 1.0)
        {
            weaponAssistLastLogTime = now;
            bool wasActive = weaponAssistLastLoggedActiveState;
            bool isActive = weaponAssistEvalValidWeapon > 0 && weaponAssistEvalFoundInCone > 0;
            if (isActive != wasActive)
            {
                weaponAssistLastLoggedActiveState = isActive;
                MelonLogger.Msg($"[SuperHackerGolf] Aimbot {(isActive ? "LOCKED" : "idle")} — " +
                    $"targets={weaponAssistLastTargetCount} inCone/s={weaponAssistEvalFoundInCone} " +
                    $"item={GetLocalPlayerEquippedItemType()}");
            }
            weaponAssistEvalFrames = 0;
            weaponAssistEvalValidWeapon = 0;
            weaponAssistEvalHaveTargets = 0;
            weaponAssistEvalFoundInCone = 0;
        }

        if (!weaponHeld) return;

        try
        {
            Vector3 playerPos = playerMovement.transform.position;
            Vector3 playerForward = playerMovement.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.0001f) return;
            playerForward.Normalize();

            Vector3 bestTarget = Vector3.zero;
            float bestScore = float.MaxValue;
            bool found = false;

            IEnumerable lockOnTargets = EnumerateActiveLockOnTargets();
            if (lockOnTargets == null) return;

            int countThisFrame = 0;
            foreach (object _ in lockOnTargets) countThisFrame++;
            weaponAssistLastTargetCount = countThisFrame;
            if (countThisFrame > 0) weaponAssistEvalHaveTargets++;

            float coneDot = Mathf.Cos(weaponAssistConeAngleDeg * Mathf.Deg2Rad);
            float maxRangeSq = weaponAssistMaxRange * weaponAssistMaxRange;

            int groundMask = GetBallGroundableMask();

            foreach (object targetObj in lockOnTargets)
            {
                if (targetObj == null) continue;

                if (!IsTargetTypeAllowed(targetObj)) continue;
                if (!IsTargetValidPerGame(targetObj)) continue;
                if (weaponAssistSkipProtected && IsTargetProtected(targetObj)) continue;

                Vector3 targetPos = GetLockOnTargetPosition(targetObj);
                if (targetPos == Vector3.zero) continue;

                Vector3 toTarget = targetPos - playerPos;
                Vector3 toTargetFlat = new Vector3(toTarget.x, 0f, toTarget.z);
                float distSq = toTargetFlat.sqrMagnitude;
                if (distSq < 0.01f || distSq > maxRangeSq) continue;

                Vector3 toTargetDir = toTargetFlat.normalized;
                float dot = Vector3.Dot(playerForward, toTargetDir);
                if (dot < coneDot) continue;

                // E28: LOS raycast. Skip targets blocked by geometry.
                if (weaponAssistVisibilityCheck && !HasLineOfSight(playerPos, targetPos, groundMask))
                {
                    continue;
                }

                // E28: mode-aware scoring. Legit's "closest to crosshair"
                // uses ONLY angle (1 - dot); rage's "best score" weights
                // near+centered; pure distance ignores angle. Lower is better.
                float score;
                switch (targetSelectionMode)
                {
                    case TargetSelectionMode.ClosestToCrosshair:
                        score = 1f - dot; // pure angle, closer to forward = lower
                        break;
                    case TargetSelectionMode.ClosestByDistance:
                        score = distSq;
                        break;
                    case TargetSelectionMode.BestScore:
                    default:
                        score = Mathf.Sqrt(distSq) * (1.1f - dot);
                        break;
                }

                if (score < bestScore)
                {
                    bestScore = score;
                    bestTarget = targetPos;
                    found = true;
                }
            }

            if (found)
            {
                weaponAssistEvalFoundInCone++;

                // E28: sticky target hysteresis. If we have a previous lock
                // and the new candidate's score improvement is below a
                // margin, keep the old target. Prevents jitter between two
                // near-equal candidates. Margin = 15% per research.
                if (aimbotHasLockedTarget && (now - aimbotLastLockTime) * 1000.0 < aimbotStickyLockMs)
                {
                    // Still within lock-on time window — keep previous target.
                    bestTarget = aimbotLastLockedAim;
                }
                else
                {
                    aimbotLastLockedAim = bestTarget;
                    aimbotLastLockTime = now;
                    aimbotHasLockedTarget = true;
                }

                // E30: split Legit and Rage aim delivery.
                //
                // LEGIT — physically rotates OrbitCameraModule yaw/pitch toward
                // the target using the existing SetYaw/SetPitch reflection
                // path. `GetFirearmAimPoint` raycasts from
                // `GameManager.Camera.transform.forward`, so once the camera
                // points at target the game's own aim is correct, no silent-aim
                // postfix needed. This is what the user sees as "visible
                // crosshair moving toward target" — the signature of a legit
                // aimbot. Smoothing is done per-frame at the yaw/pitch level
                // using a frame-rate-independent exponential decay.
                //
                // RAGE — keeps the old silent-aim postfix path. Camera does
                // not move; `GetFirearmAimPoint` returns target position
                // directly via `__result`. No way for the user to see the
                // aim correction. Instant snap, zero smoothing.
                bool legitMode = aimbotMode == AimbotMode.Legit;

                // E30d / E31c: hitbox offset — applied only in Rage/Custom
                // (silent aim) since the Legit path rotates the actual
                // camera. Derived from the hitbox flag priority (Head first,
                // then Chest, then Legs).
                if (!legitMode)
                {
                    float hitboxY = GetHitboxYOffset();
                    if (Mathf.Abs(hitboxY) > 0.01f)
                    {
                        bestTarget.y += hitboxY;
                    }
                }

                if (legitMode)
                {
                    LegitAimbotRotateCameraTo(bestTarget);
                    weaponAssistOverrideAimValid = false;
                    aimbotSmoothedAim = bestTarget;
                    aimbotHasSmoothedAim = true;
                }
                else
                {
                    // RAGE / CUSTOM silent-aim path with smoothing through
                    // the override aim vector.
                    Vector3 finalAim = bestTarget;
                    if (aimbotSmoothingFactor > 0.5f)
                    {
                        float smoothNorm = Mathf.Clamp(aimbotSmoothingFactor / 100f, 0f, 0.95f);
                        if (!aimbotHasSmoothedAim)
                        {
                            aimbotSmoothedAim = bestTarget;
                            aimbotHasSmoothedAim = true;
                        }
                        Vector3 delta = bestTarget - aimbotSmoothedAim;
                        switch (aimbotSmoothingCurve)
                        {
                            case AimbotSmoothingCurve.EaseOut:
                                aimbotSmoothedAim += delta * (1f - smoothNorm);
                                break;
                            case AimbotSmoothingCurve.Spring:
                                aimbotSmoothVelocity += (delta * (1f - smoothNorm) - aimbotSmoothVelocity * 0.5f) * Time.deltaTime * 10f;
                                aimbotSmoothedAim += aimbotSmoothVelocity;
                                break;
                            case AimbotSmoothingCurve.Linear:
                            default:
                                aimbotSmoothedAim = Vector3.Lerp(aimbotSmoothedAim, bestTarget, 1f - smoothNorm);
                                break;
                        }
                        finalAim = aimbotSmoothedAim;
                    }
                    else
                    {
                        aimbotSmoothedAim = bestTarget;
                        aimbotHasSmoothedAim = true;
                    }

                    weaponAssistOverrideAim = finalAim;
                    weaponAssistOverrideAimValid = true;
                }

                // E28: auto-fire via reflected TryUseItem.Invoke. Fragile on
                // Mono/Wine — when it throws BadImageFormatException the
                // failure CASCADES into other reflection calls in the same
                // AppDomain (observed: UpdateTrails, AutoSwingRelease, and
                // RefreshBallWindFactors all start throwing zero-rva after).
                // We permanently disable auto-fire for the session on first
                // failure and log a fat warning so golf auto-hit keeps
                // working. User can re-enable via GUI + game relaunch.
                if (weaponAutoFireEnabled && !autoFireBrokenForSession &&
                    cachedPlayerInventoryTryUseItemMethod != null)
                {
                    double sinceLastShot = now - weaponAutoFireLastShotTime;
                    if (sinceLastShot >= weaponAutoFireMinIntervalSec)
                    {
                        try
                        {
                            object inventory = GetLocalPlayerInventory();
                            if (inventory != null)
                            {
                                cachedTryUseItemArgs[0] = false;
                                cachedTryUseItemArgs[1] = false;
                                cachedPlayerInventoryTryUseItemMethod.Invoke(inventory, cachedTryUseItemArgs);
                                weaponAutoFireLastShotTime = now;
                            }
                        }
                        catch (Exception ex)
                        {
                            autoFireBrokenForSession = true;
                            weaponAutoFireEnabled = false;
                            MelonLogger.BigError("MimiMod",
                                $"Auto-fire Invoke threw {ex.GetType().Name}: {ex.Message}\n" +
                                $"The reflected TryUseItem path is unstable on this runtime " +
                                $"(Mono/Wine/Proton). Auto-fire DISABLED for this session to " +
                                $"prevent cascading reflection corruption. Aim tracking still " +
                                $"works — click the fire button yourself. Restart the game to re-enable.");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] TickWeaponAssistTargetSelection threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Walks PlayerGolfer → PlayerInfo → Inventory to get the local
    /// PlayerInventory instance for auto-fire invocation. Uses the existing
    /// cached PropertyInfo from the rocket-driver detection path.
    /// </summary>
    private object GetLocalPlayerInventory()
    {
        try
        {
            if (cachedPlayerInfoProperty == null || cachedInventoryProperty == null) return null;
            if (playerGolfer == null) return null;
            object playerInfo = cachedPlayerInfoProperty.GetValue(playerGolfer, null);
            if (playerInfo == null) return null;
            return cachedInventoryProperty.GetValue(playerInfo, null);
        }
        catch
        {
            return null;
        }
    }

    // ── E30: Legit aimbot camera rotation ──────────────────────────────────────
    //
    // Drives the OrbitCameraModule (the ONLY gameplay camera per RE: used for
    // both walk AND swing-aim) to visibly point its yaw/pitch at a target.
    // `PlayerInventory.GetFirearmAimPoint` raycasts from
    // `GameManager.Camera.transform.forward`, so once the camera points at the
    // target the game's own aim is correct — no silent-aim postfix required.
    //
    // Smoothing: frame-rate-independent exponential decay. The smoothing
    // factor (0..100) maps to a response frequency in Hz — 0 gives a
    // near-instant snap (~40Hz), 100 gives a very slow drift (~2.5Hz).
    // Per-frame lerp t = 1 - exp(-responseHz * dt), which converges
    // predictably regardless of framerate.
    //
    // Uses Camera.main for the ray origin. Fallback: try reflected
    // GameManager.Camera property if Camera.main is null (e.g. camera not
    // yet tagged MainCamera on certain maps).
    private PropertyInfo cachedGameManagerCameraProperty;
    private bool cachedGameManagerCameraPropertyResolved;
    private int legitRotateCallCount;
    private double legitRotateLastLogTime;
    private bool legitRotateEverLoggedSuccess;
    private bool legitRotateLoggedOrbitNull;
    private bool legitRotateLoggedSettersNull;
    private bool legitRotateLoggedCamNull;
    private void LegitAimbotRotateCameraTo(Vector3 targetPos)
    {
        try
        {
            // E30b: self-init the orbit reflection cache. Previously this was
            // only populated by AutoAimCamera() (golf swing path) — if the
            // user enables weapon aimbot without ever using golf assist,
            // cachedOrbitSetYawMethod stays null and this function no-ops
            // silently. Force the cache init here.
            InitializeReflectionCache();

            object orbitModule = TryGetOrbitModule();
            if (orbitModule == null)
            {
                if (!legitRotateLoggedOrbitNull)
                {
                    legitRotateLoggedOrbitNull = true;
                    MelonLogger.Warning("[SuperHackerGolf] Legit aim: TryGetOrbitModule returned null — " +
                        "CameraModuleController.CurrentModuleType is not Orbit (photo/overview/menu?). " +
                        "Legit rotation inactive until camera returns to gameplay mode.");
                }
                return;
            }
            if (cachedOrbitSetYawMethod == null || cachedOrbitSetPitchMethod == null)
            {
                if (!legitRotateLoggedSettersNull)
                {
                    legitRotateLoggedSettersNull = true;
                    MelonLogger.Warning($"[SuperHackerGolf] Legit aim: orbit Set methods not resolved. " +
                        $"SetYaw={(cachedOrbitSetYawMethod != null ? "Y" : "n")} " +
                        $"SetPitch={(cachedOrbitSetPitchMethod != null ? "Y" : "n")} " +
                        $"orbitType={orbitModule.GetType().FullName}");
                }
                return;
            }

            Camera cam = Camera.main;
            if (cam == null)
            {
                cam = ResolveGameManagerCamera();
                if (cam == null)
                {
                    if (!legitRotateLoggedCamNull)
                    {
                        legitRotateLoggedCamNull = true;
                        MelonLogger.Warning("[SuperHackerGolf] Legit aim: Camera.main and GameManager.Camera both null");
                    }
                    return;
                }
            }

            Vector3 camPos = cam.transform.position;
            Vector3 dir = targetPos - camPos;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();

            // Yaw: Unity convention 0° = +Z, positive = clockwise looking down.
            float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

            // Pitch: Unity convention positive = looking DOWN (per RE of
            // VectorExtensions.GetPitchDeg).
            float flatLen = new Vector2(dir.x, dir.z).magnitude;
            float targetPitch = Mathf.Atan2(-dir.y, flatLen) * Mathf.Rad2Deg;

            // Current camera yaw/pitch from transform.forward. We don't read
            // the orbit's internal state because different camera states
            // (walk vs swing) may not expose a single canonical getter.
            Vector3 fwd = cam.transform.forward;
            float curYaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
            float flatLenCur = new Vector2(fwd.x, fwd.z).magnitude;
            float curPitch = Mathf.Atan2(-fwd.y, flatLenCur) * Mathf.Rad2Deg;

            // Frame-rate-independent exponential decay. 0..100 smoothing →
            // 40Hz..2.5Hz response frequency. Clamp dt so a long frame spike
            // doesn't instantly snap us (would look non-legit).
            float smoothingNorm = Mathf.Clamp01(aimbotSmoothingFactor / 100f);
            float responseHz = Mathf.Lerp(40f, 2.5f, smoothingNorm);
            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            float t = 1f - Mathf.Exp(-responseHz * dt);

            float newYaw = Mathf.LerpAngle(curYaw, targetYaw, t);
            float newPitch = Mathf.Lerp(curPitch, targetPitch, t);

            cachedOrbitYawArgs[0] = newYaw;
            cachedOrbitPitchArgs[0] = newPitch;
            cachedOrbitSetYawMethod.Invoke(orbitModule, cachedOrbitYawArgs);
            cachedOrbitSetPitchMethod.Invoke(orbitModule, cachedOrbitPitchArgs);
            if (cachedOrbitForceUpdateMethod != null)
            {
                cachedOrbitForceUpdateMethod.Invoke(orbitModule, null);
            }

            legitRotateCallCount++;
            double logNow = Time.realtimeSinceStartupAsDouble;
            if (!legitRotateEverLoggedSuccess)
            {
                legitRotateEverLoggedSuccess = true;
                legitRotateLastLogTime = logNow;
                MelonLogger.Msg($"[SuperHackerGolf] Legit aim ACTIVE — first SetYaw/SetPitch invoke succeeded. " +
                    $"curYaw={curYaw:F1} tgtYaw={targetYaw:F1} curPitch={curPitch:F1} tgtPitch={targetPitch:F1} " +
                    $"newYaw={newYaw:F1} newPitch={newPitch:F1} t={t:F3} camPos=({camPos.x:F1},{camPos.y:F1},{camPos.z:F1})");
            }
            else if (logNow - legitRotateLastLogTime > 2.0)
            {
                legitRotateLastLogTime = logNow;
                MelonLogger.Msg($"[SuperHackerGolf] Legit aim tick — calls/2s={legitRotateCallCount} " +
                    $"tgtYaw={targetYaw:F1} curYaw={curYaw:F1} Δ={Mathf.DeltaAngle(curYaw, targetYaw):F1}° " +
                    $"tgtPitch={targetPitch:F1} curPitch={curPitch:F1}");
                legitRotateCallCount = 0;
            }
        }
        catch (Exception ex)
        {
            // Rate-limit via the existing diagnostic timestamp so a repeated
            // throw here doesn't spam the log. TickGuarded also wraps us at
            // the caller level as a belt-and-braces.
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - weaponAssistLastLogTime > 5.0)
            {
                weaponAssistLastLogTime = now;
                MelonLogger.Warning($"[SuperHackerGolf] Legit aim rotate threw {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private Camera ResolveGameManagerCamera()
    {
        if (!cachedGameManagerCameraPropertyResolved)
        {
            cachedGameManagerCameraPropertyResolved = true;
            try
            {
                Type gmType = null;
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    gmType = asms[i].GetType("GameManager");
                    if (gmType != null) break;
                }
                if (gmType != null)
                {
                    cachedGameManagerCameraProperty = gmType.GetProperty("Camera",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
            }
            catch { }
        }

        if (cachedGameManagerCameraProperty == null) return null;
        try
        {
            return cachedGameManagerCameraProperty.GetValue(null, null) as Camera;
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable EnumerateActiveLockOnTargets()
    {
        // E26: Originally walked `LockOnTargetUiManager.activeTargets` which
        // is only populated with targets the game currently renders a lock-on
        // marker for. In a normal gameplay scenario the dict is often empty
        // even when there ARE enemies/dummies in the scene — we'd never find
        // a target and the aimbot would silently no-op.
        //
        // Fix: scan ALL `LockOnTarget` MonoBehaviour instances in the scene
        // directly. That's every registered lock-on-able entity: enemies,
        // dummies, mines, coins, etc. Cheaper than you'd think — there are
        // typically <20 in a lobby. Then our cone + range filter narrows it.
        if (cachedLockOnTargetType == null) return null;

        UnityEngine.Object[] targets;
        try
        {
            targets = UnityEngine.Object.FindObjectsByType(
                cachedLockOnTargetType,
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
        }
        catch
        {
            return null;
        }
        return targets;
    }

    private Vector3 GetLockOnTargetPosition(object lockOnTarget)
    {
        try
        {
            if (cachedLockOnTargetGetPositionMethod == null || lockOnTarget == null) return Vector3.zero;
            object result = cachedLockOnTargetGetPositionMethod.Invoke(lockOnTarget, null);
            if (result is Vector3 v) return v;
        }
        catch
        {
        }
        return Vector3.zero;
    }

    // E27: Ask the game itself whether this LockOnTarget is valid for the
    // local player right now. Wraps LockOnTarget::IsValidForLocalPlayer(bool)
    // which the game uses internally to decide which targets show UI markers.
    // Handles team filtering, pre-match states, dead players, etc. Returns
    // true if the call fails (fail-open) so reflection issues don't kill
    // the aimbot entirely.
    private object[] cachedIsValidForLocalPlayerArgs;
    private bool IsTargetValidPerGame(object lockOnTarget)
    {
        try
        {
            if (cachedLockOnTargetIsValidMethod == null) return true;
            if (cachedIsValidForLocalPlayerArgs == null)
                cachedIsValidForLocalPlayerArgs = new object[] { false };
            object result = cachedLockOnTargetIsValidMethod.Invoke(lockOnTarget, cachedIsValidForLocalPlayerArgs);
            if (result is bool b) return b;
        }
        catch { }
        return true;
    }

    // E27: Check if a lock-on target is currently protected and wouldn't take
    // a hit if we shot it. Covers:
    //   1. PlayerInfo.isElectromagnetShieldActive (bubble shield)
    //   2. PlayerMovement.knockoutImmunityStatus.hasImmunity (grace period)
    // Uses the reflection caches from MimiMod.Shield.cs (cachedShieldActiveField,
    // cachedKnockoutImmunityField/HasImmunityField). Walks up from the LockOnTarget
    // component to find a PlayerInfo/PlayerMovement on the same root.
    private bool IsTargetProtected(object lockOnTarget)
    {
        var comp = lockOnTarget as Component;
        if (comp == null) return false;

        // Shield check — PlayerInfo.isElectromagnetShieldActive
        try
        {
            if (cachedPlayerInfoTypeForFilter != null && cachedShieldActiveField != null)
            {
                Component pi = comp.GetComponentInParent(cachedPlayerInfoTypeForFilter);
                if (pi != null)
                {
                    object v = cachedShieldActiveField.GetValue(pi);
                    if (v is bool b && b) return true;
                }
            }
        }
        catch { }

        // Knockout immunity check — PlayerMovement.knockoutImmunityStatus.hasImmunity
        try
        {
            // Can't use cachedLandmineTypeForFilter here — need PlayerMovement type.
            // Resolve lazily from playerMovement's runtime type (same type for all
            // networked players in the scene).
            if (playerMovement != null && cachedKnockoutImmunityField != null && cachedKnockoutImmunityHasImmunityField != null)
            {
                Type pmType = playerMovement.GetType();
                Component pm = comp.GetComponentInParent(pmType);
                if (pm != null)
                {
                    object status = cachedKnockoutImmunityField.GetValue(pm);
                    if (status != null)
                    {
                        object has = cachedKnockoutImmunityHasImmunityField.GetValue(status);
                        if (has is bool hb && hb) return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    // E26b: classify a LockOnTarget by what's attached to the same GameObject
    // (or one of its parents). Gates based on user's toggle state:
    //   - weaponAssistTargetPlayers: match any transform owning a PlayerInfo
    //   - weaponAssistTargetDummies: match any TargetDummy component
    //   - weaponAssistTargetMines:   match any Landmine component
    // Unknown types (coins, pickups, etc.) default to ALLOWED so we don't
    // silently exclude everything new. Uses GetComponentInParent so targets
    // whose LockOnTarget sits on a child collider still resolve correctly.
    private bool IsTargetTypeAllowed(object targetObj)
    {
        var comp = targetObj as Component;
        if (comp == null) return true;
        GameObject go;
        try { go = comp.gameObject; }
        catch { return true; }
        if (go == null) return true;

        bool isDummy = false;
        bool isMine = false;
        bool isPlayer = false;
        bool isGolfCart = false;

        try
        {
            if (cachedTargetDummyType != null)
            {
                isDummy = comp.GetComponentInParent(cachedTargetDummyType) != null;
            }
            if (cachedLandmineTypeForFilter != null)
            {
                isMine = comp.GetComponentInParent(cachedLandmineTypeForFilter) != null;
            }
            if (cachedPlayerInfoTypeForFilter != null)
            {
                isPlayer = comp.GetComponentInParent(cachedPlayerInfoTypeForFilter) != null;
            }
            if (cachedGolfCartTypeForFilter != null)
            {
                isGolfCart = comp.GetComponentInParent(cachedGolfCartTypeForFilter) != null;
            }
        }
        catch
        {
            return false;
        }

        // Players win over cart detection (player sitting in cart → still a
        // valid human target). Cart-only match (empty cart) falls to the
        // golf-cart toggle.
        if (isPlayer) return weaponAssistTargetPlayers;
        if (isDummy) return weaponAssistTargetDummies;
        if (isMine)  return weaponAssistTargetMines;
        if (isGolfCart) return weaponAssistTargetGolfCarts;
        // E30b: flipped default — unclassified targets are now REJECTED.
        // Golf carts, coins, pickups, and any future game-added lock-on
        // entities won't auto-target unless explicitly whitelisted.
        return false;
    }

    // E28: LOS raycast from player eye-level to target. Uses the ball
    // groundable layer so we only count opaque geometry as blockers.
    // Adds +1.2m vertical offset to origin so we're raycasting from torso
    // height rather than ground level (player transform.position is at feet).
    private bool HasLineOfSight(Vector3 playerPos, Vector3 targetPos, int layerMask)
    {
        try
        {
            Vector3 origin = playerPos + Vector3.up * 1.2f;
            Vector3 dir = targetPos - origin;
            float dist = dir.magnitude;
            if (dist < 0.01f) return true;
            dir /= dist;
            RaycastHit hit;
            if (Physics.Raycast(origin, dir, out hit, dist - 0.5f, layerMask, QueryTriggerInteraction.Ignore))
            {
                return false;  // blocked
            }
            return true;
        }
        catch
        {
            return true;  // fail-open
        }
    }

    // E28: check activation key for HoldKey mode. Supports keyboard keys
    // (aimbotActivationKey) and mouse4/mouse5 buttons via the new input system.
    private bool IsAimbotActivationKeyHeld()
    {
        try
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var ms = UnityEngine.InputSystem.Mouse.current;
            if (aimbotActivationKeyIsMouse4 && ms != null) return ms.backButton.isPressed;
            if (aimbotActivationKeyIsMouse5 && ms != null) return ms.forwardButton.isPressed;
            if (kb != null && aimbotActivationKey != Key.None) return kb[aimbotActivationKey].isPressed;
        }
        catch { }
        return false;
    }

    private bool IsLocalPlayerHoldingAimableWeapon()
    {
        // ItemType enum (reversed from GameAssembly.dll):
        //   None=0, Coffee=1, DuelingPistol=2, ElephantGun=3, Airhorn=4,
        //   SpringBoots=5, GolfCart=6, RocketLauncher=7, Landmine=8,
        //   Electromagnet=9, OrbitalLaser=10, RocketDriver=11, FreezeBomb=12
        // Hitscan weapons gated by GetFirearmAimPoint: DuelingPistol, ElephantGun.
        // (Rockets / FreezeBomb / OrbitalLaser use separate fire paths — Cmd-driven.)
        int itemType = GetLocalPlayerEquippedItemType();
        return itemType == 2 || itemType == 3;
    }

    /// <summary>
    /// Auto-bhop: while the feature is enabled (F10) AND the user is holding
    /// space AND the player is grounded, call TryTriggerJump. This matches
    /// classic CS-style bhop — you hold space to hop, release to run
    /// normally. The game has no cooldown / rate limit on jumps, so every
    /// landing instantly triggers the next jump.
    /// </summary>
    internal void TickBunnyhop()
    {
        if (!bunnyhopEnabled) return;
        if (!bunnyhopReflectionInitialized || !bunnyhopReflectionAvailable)
        {
            EnsureBunnyhopReflectionInitialized();
            if (!bunnyhopReflectionAvailable) return;
        }
        if (playerMovement == null) return;

        // Only fire while space is physically held. Use the new input system's
        // Keyboard.current — we already use it for the rest of the keybinds.
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null) return;
        if (!kb.spaceKey.isPressed) return;

        try
        {
            bool grounded = false;
            try
            {
                object v = cachedPlayerMovementIsGroundedProperty.GetValue(playerMovement, null);
                if (v is bool b) grounded = b;
            }
            catch { return; }

            if (grounded)
            {
                try { cachedPlayerMovementTryTriggerJumpMethod.Invoke(playerMovement, null); }
                catch { }
            }
        }
        catch
        {
        }
    }
}
