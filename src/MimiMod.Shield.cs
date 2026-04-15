using System;
using System.Reflection;
using MelonLoader;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── Force-enable player invulnerability (E16) ─────────────────────────────
    //
    // The game has two independent protection systems, both reversed out of
    // GameAssembly.dll:
    //
    //   1. Electromagnet Shield (item pickup bubble)
    //      - PlayerInfo.isElectromagnetShieldActive (bool SyncVar, dirty bit 64)
    //      - PlayerInfo.electromagnetShieldItemUseId (ItemUseId SyncVar)
    //      - PlayerInfo.OnIsElectromagnetShieldActiveChanged(bool, bool)
    //        SyncVar hook that spawns the VFX + audio + collider.
    //      - PlayerInfo.ElectromagnetShieldCollider is a SphereCollider that
    //        the game actually uses in its hit physics.
    //
    //   2. Knockout Immunity (post-respawn / post-hit grace)
    //      - PlayerMovement.knockoutImmunityStatus (struct KnockOutImmunity {
    //          bool hasImmunity; KnockOutVfxColor color; }, dirty bit 256)
    //      - PlayerMovement.UpdateKnockoutImmunityVfx() refreshes the vfx
    //      - PlayerMovement.knockoutImmunityVfx (PoolableParticleSystem)
    //
    // Both backing fields are Mirror SyncVars, so a client-side write doesn't
    // replicate to the server — but the player's own PlayerInfo/PlayerMovement
    // is owned by the local connection, so writing the backing field and
    // invoking the SyncVar hook triggers the full local visual + collider
    // effect. For hit-blocking on the server, this pairs with the existing
    // anti-cheat bypass (HarmonyX patches on RegisterHit) that already short-
    // circuits server-side knockout validation.
    //
    // Keybind: F6 (configurable via SuperHackerGolf.cfg shield_toggle_key).

    private bool shieldForcedOn;
    private bool shieldReflectionInitialized;
    private bool shieldReflectionAvailable;

    // Reflected PlayerInfo members
    private Type cachedPlayerInfoType;
    private PropertyInfo cachedPlayerGolferPlayerInfoProperty;
    private FieldInfo cachedShieldActiveField;
    private FieldInfo cachedShieldItemUseIdField;
    private MethodInfo cachedShieldOnChangedMethod;
    private MethodInfo cachedShieldLocalActivateMethod;
    private Type cachedItemUseIdType;

    // Reflected PlayerMovement members
    private FieldInfo cachedKnockoutImmunityField;
    private Type cachedKnockoutImmunityStructType;
    private FieldInfo cachedKnockoutImmunityHasImmunityField;
    private MethodInfo cachedKnockoutImmunityUpdateVfxMethod;
    private MethodInfo cachedKnockoutImmunityOnChangedMethod;

    internal void EnsureShieldReflectionInitialized()
    {
        if (shieldReflectionInitialized)
        {
            return;
        }
        shieldReflectionInitialized = true;

        try
        {
            if (playerGolfer == null || playerMovement == null)
            {
                return;
            }

            // PlayerGolfer.PlayerInfo property
            Type golferType = playerGolfer.GetType();
            cachedPlayerGolferPlayerInfoProperty = golferType.GetProperty("PlayerInfo",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (cachedPlayerGolferPlayerInfoProperty == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Shield: PlayerGolfer.PlayerInfo property not found");
                return;
            }

            object playerInfo = cachedPlayerGolferPlayerInfoProperty.GetValue(playerGolfer, null);
            if (playerInfo == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Shield: PlayerInfo null on local golfer");
                return;
            }
            cachedPlayerInfoType = playerInfo.GetType();

            const BindingFlags bfInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            cachedShieldActiveField = cachedPlayerInfoType.GetField("isElectromagnetShieldActive", bfInst);
            cachedShieldItemUseIdField = cachedPlayerInfoType.GetField("electromagnetShieldItemUseId", bfInst);
            cachedShieldOnChangedMethod = cachedPlayerInfoType.GetMethod("OnIsElectromagnetShieldActiveChanged", bfInst,
                null, new Type[] { typeof(bool), typeof(bool) }, null);
            cachedShieldLocalActivateMethod = cachedPlayerInfoType.GetMethod("LocalPlayerActivateElectromagnetShield", bfInst);

            // Locate ItemUseId struct type from the item use id field
            if (cachedShieldItemUseIdField != null)
            {
                cachedItemUseIdType = cachedShieldItemUseIdField.FieldType;
            }

            // PlayerMovement.knockoutImmunityStatus field
            Type pmType = playerMovement.GetType();
            cachedKnockoutImmunityField = pmType.GetField("knockoutImmunityStatus", bfInst);
            if (cachedKnockoutImmunityField != null)
            {
                cachedKnockoutImmunityStructType = cachedKnockoutImmunityField.FieldType;
                cachedKnockoutImmunityHasImmunityField = cachedKnockoutImmunityStructType.GetField("hasImmunity", bfInst);
            }
            cachedKnockoutImmunityUpdateVfxMethod = pmType.GetMethod("UpdateKnockoutImmunityVfx", bfInst);
            cachedKnockoutImmunityOnChangedMethod = pmType.GetMethod("OnKnockoutImmunityStatusChanged", bfInst,
                null,
                cachedKnockoutImmunityStructType != null
                    ? new Type[] { cachedKnockoutImmunityStructType, cachedKnockoutImmunityStructType }
                    : Type.EmptyTypes,
                null);

            shieldReflectionAvailable =
                cachedShieldActiveField != null &&
                cachedShieldOnChangedMethod != null &&
                cachedKnockoutImmunityField != null &&
                cachedKnockoutImmunityHasImmunityField != null;

            MelonLogger.Msg(
                $"[SuperHackerGolf] Shield reflection: " +
                $"shieldField={(cachedShieldActiveField != null ? "Y" : "n")} " +
                $"shieldOnChanged={(cachedShieldOnChangedMethod != null ? "Y" : "n")} " +
                $"shieldLocalActivate={(cachedShieldLocalActivateMethod != null ? "Y" : "n")} " +
                $"koImmunityField={(cachedKnockoutImmunityField != null ? "Y" : "n")} " +
                $"koImmunityHasField={(cachedKnockoutImmunityHasImmunityField != null ? "Y" : "n")} " +
                $"koUpdateVfx={(cachedKnockoutImmunityUpdateVfxMethod != null ? "Y" : "n")} " +
                $"ready={shieldReflectionAvailable}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Shield reflection init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal void ToggleForcedShield()
    {
        if (!shieldReflectionInitialized)
        {
            EnsureShieldReflectionInitialized();
        }

        if (!shieldReflectionAvailable)
        {
            // Try once more — playerGolfer may have been null at first init.
            shieldReflectionInitialized = false;
            EnsureShieldReflectionInitialized();
            if (!shieldReflectionAvailable)
            {
                MelonLogger.Warning("[SuperHackerGolf] Shield toggle skipped — reflection not ready");
                return;
            }
        }

        shieldForcedOn = !shieldForcedOn;
        ApplyForcedShieldState(shieldForcedOn);
        MarkHudDirty();
        MelonLogger.Msg($"[SuperHackerGolf] Forced shield: {(shieldForcedOn ? "ON" : "OFF")}");
    }

    internal void ApplyForcedShieldState(bool enabled)
    {
        if (!shieldReflectionAvailable || playerGolfer == null || playerMovement == null)
        {
            return;
        }

        try
        {
            // Electromagnet shield — set the backing SyncVar field directly and
            // then invoke the OnChanged hook so the VFX/audio/collider updates.
            object playerInfo = cachedPlayerGolferPlayerInfoProperty.GetValue(playerGolfer, null);
            if (playerInfo != null && cachedShieldActiveField != null)
            {
                bool prev = false;
                try { prev = (bool)cachedShieldActiveField.GetValue(playerInfo); } catch { }
                cachedShieldActiveField.SetValue(playerInfo, enabled);

                if (cachedShieldOnChangedMethod != null && prev != enabled)
                {
                    try
                    {
                        cachedShieldOnChangedMethod.Invoke(playerInfo, new object[] { prev, enabled });
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[SuperHackerGolf] Shield OnChanged hook threw: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }

            // Knockout immunity — the status is a struct, so we box a new
            // instance with hasImmunity set appropriately and write back.
            if (cachedKnockoutImmunityStructType != null &&
                cachedKnockoutImmunityHasImmunityField != null &&
                cachedKnockoutImmunityField != null)
            {
                object prevStruct = cachedKnockoutImmunityField.GetValue(playerMovement);
                object newStruct = Activator.CreateInstance(cachedKnockoutImmunityStructType);
                cachedKnockoutImmunityHasImmunityField.SetValue(newStruct, enabled);
                cachedKnockoutImmunityField.SetValue(playerMovement, newStruct);

                if (cachedKnockoutImmunityOnChangedMethod != null)
                {
                    try
                    {
                        cachedKnockoutImmunityOnChangedMethod.Invoke(playerMovement,
                            new object[] { prevStruct, newStruct });
                    }
                    catch { }
                }
                else if (cachedKnockoutImmunityUpdateVfxMethod != null)
                {
                    try { cachedKnockoutImmunityUpdateVfxMethod.Invoke(playerMovement, null); }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Shield apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-apply the forced state each frame while the toggle is ON. The game's
    /// own routines (ElectromagnetShieldRoutine / KnockoutImmunityRoutine) run
    /// on the local player and will clear the state when they think the shield
    /// should expire — we stomp the field back to our desired value so the
    /// toggle effectively pins the shield on.
    /// </summary>
    internal void TickForcedShield()
    {
        if (!shieldForcedOn) return;
        if (!shieldReflectionAvailable) return;
        if (playerGolfer == null || playerMovement == null) return;

        try
        {
            object playerInfo = cachedPlayerGolferPlayerInfoProperty.GetValue(playerGolfer, null);
            if (playerInfo != null && cachedShieldActiveField != null)
            {
                bool current = (bool)cachedShieldActiveField.GetValue(playerInfo);
                if (!current)
                {
                    cachedShieldActiveField.SetValue(playerInfo, true);
                    if (cachedShieldOnChangedMethod != null)
                    {
                        try { cachedShieldOnChangedMethod.Invoke(playerInfo, new object[] { false, true }); }
                        catch { }
                    }
                }
            }

            if (cachedKnockoutImmunityField != null &&
                cachedKnockoutImmunityHasImmunityField != null &&
                cachedKnockoutImmunityStructType != null)
            {
                object status = cachedKnockoutImmunityField.GetValue(playerMovement);
                bool has = (bool)cachedKnockoutImmunityHasImmunityField.GetValue(status);
                if (!has)
                {
                    object prev = status;
                    object newStatus = Activator.CreateInstance(cachedKnockoutImmunityStructType);
                    cachedKnockoutImmunityHasImmunityField.SetValue(newStatus, true);
                    cachedKnockoutImmunityField.SetValue(playerMovement, newStatus);
                    if (cachedKnockoutImmunityOnChangedMethod != null)
                    {
                        try { cachedKnockoutImmunityOnChangedMethod.Invoke(playerMovement, new object[] { prev, newStatus }); }
                        catch { }
                    }
                }
            }
        }
        catch
        {
        }
    }

    internal bool IsForcedShieldActive() => shieldForcedOn;
}
