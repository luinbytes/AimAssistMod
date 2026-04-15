using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

public partial class SuperHackerGolf
{
    // ── E29: item spawner (debug helper) ──────────────────────────────────────
    //
    // Reverse-engineered call path (static IL analysis of GameAssembly.dll):
    //
    //   PlayerInventory::CmdAddItem(ItemType)  [instance, requiresAuthority=true]
    //     └─ UserCode_CmdAddItem__ItemType(ItemType)  [runs server-side]
    //         ├─ serverAddItemCheatCommandRateLimiter.RegisterHit()  [already bypassed]
    //         ├─ MatchSetupRules.IsCheatsEnabled()                  ← ONLY hard gate
    //         ├─ GameManager.AllItems.TryGetItemData(item, out ItemData)
    //         └─ ServerTryAddItem(item, data.MaxUses)
    //               └─ slots[firstClearIndex] = new InventorySlot(item, maxUses)
    //
    // To grant an item: patch IsCheatsEnabled → true, then call
    //     playerInventory.CmdAddItem(ItemType.XXX)
    // via reflection on our local PlayerInventory instance. `requiresAuthority`
    // means the owning client (us) can invoke it, server processes it, SyncList
    // replicates the slot back to us.
    //
    // Host-only alt path (bypasses IsCheatsEnabled entirely):
    //     playerInventory.ServerTryAddItem(ItemType.XXX, maxUses)
    //
    // We try CmdAddItem first (works anywhere we have mod+host), fall back to
    // ServerTryAddItem (host-only) on failure.

    internal struct ItemTypeEntry
    {
        public int Value;
        public string Name;
        public bool IsPlayerUsable;
    }

    private bool itemSpawnerInitialized;
    private bool itemSpawnerAvailable;
    private Type cachedItemTypeEnum;
    private MethodInfo cachedCmdAddItemMethod;
    private MethodInfo cachedServerTryAddItemMethod;
    private PropertyInfo cachedGameManagerLocalPlayerInventoryProperty;
    private Type cachedGameManagerType;
    private static bool cheatsEnabledHarmonyInstalled;
    private readonly List<ItemTypeEntry> itemSpawnCatalog = new List<ItemTypeEntry>();

    internal void EnsureItemSpawnerInitialized()
    {
        if (itemSpawnerInitialized) return;
        itemSpawnerInitialized = true;

        try
        {
            // Resolve ItemType enum + PlayerInventory + GameManager types.
            Type inventoryType = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                if (cachedItemTypeEnum == null) cachedItemTypeEnum = asms[i].GetType("ItemType");
                if (inventoryType == null) inventoryType = asms[i].GetType("PlayerInventory");
                if (cachedGameManagerType == null) cachedGameManagerType = asms[i].GetType("GameManager");
                if (cachedItemTypeEnum != null && inventoryType != null && cachedGameManagerType != null) break;
            }
            if (cachedItemTypeEnum == null || inventoryType == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] Item spawner: ItemType or PlayerInventory type missing");
                return;
            }

            // Enumerate the enum values → build the catalog.
            itemSpawnCatalog.Clear();
            string[] names = Enum.GetNames(cachedItemTypeEnum);
            Array values = Enum.GetValues(cachedItemTypeEnum);
            for (int i = 0; i < names.Length; i++)
            {
                int intValue = Convert.ToInt32(values.GetValue(i));
                if (intValue == 0) continue; // None is not spawnable

                itemSpawnCatalog.Add(new ItemTypeEntry
                {
                    Value = intValue,
                    Name = names[i],
                    IsPlayerUsable = IsPlayerUsableItem(intValue),
                });
            }

            // Resolve CmdAddItem(ItemType) — exact single-param instance method.
            cachedCmdAddItemMethod = inventoryType.GetMethod(
                "CmdAddItem",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { cachedItemTypeEnum }, null);

            // Resolve ServerTryAddItem(ItemType, int) — host-only fallback.
            cachedServerTryAddItemMethod = inventoryType.GetMethod(
                "ServerTryAddItem",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new Type[] { cachedItemTypeEnum, typeof(int) }, null);

            // Resolve GameManager.LocalPlayerInventory static property.
            if (cachedGameManagerType != null)
            {
                cachedGameManagerLocalPlayerInventoryProperty = cachedGameManagerType.GetProperty(
                    "LocalPlayerInventory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            itemSpawnerAvailable = cachedCmdAddItemMethod != null || cachedServerTryAddItemMethod != null;

            InstallCheatsEnabledPatch();

            MelonLogger.Msg(
                $"[SuperHackerGolf] Item spawner: {itemSpawnCatalog.Count} items cataloged, " +
                $"CmdAddItem={(cachedCmdAddItemMethod != null ? "Y" : "n")} " +
                $"ServerTryAddItem={(cachedServerTryAddItemMethod != null ? "Y" : "n")} " +
                $"ready={itemSpawnerAvailable}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Item spawner init threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Patches MatchSetupRules.IsCheatsEnabled() to always return true. This
    /// is the ONE server-side gate remaining after our rate-limiter bypass.
    /// Harmony patches run in our process only — on a non-host client this
    /// makes cheat-gated local checks pass, but the host still runs its own
    /// unpatched IsCheatsEnabled when processing our Cmd. So CmdAddItem works
    /// cleanly when YOU are hosting (mod on host = cheats on), and fails
    /// server-side when you're a guest on a vanilla host.
    /// </summary>
    private static void InstallCheatsEnabledPatch()
    {
        if (cheatsEnabledHarmonyInstalled) return;

        try
        {
            Type matchSetupRulesType = null;
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                matchSetupRulesType = asms[i].GetType("MatchSetupRules");
                if (matchSetupRulesType != null) break;
            }
            if (matchSetupRulesType == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] MatchSetupRules type not found — cheats gate not patched");
                return;
            }

            MethodInfo target = matchSetupRulesType.GetMethod(
                "IsCheatsEnabled",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (target == null)
            {
                MelonLogger.Warning("[SuperHackerGolf] MatchSetupRules.IsCheatsEnabled not found");
                return;
            }

            HarmonyLib.Harmony harmony = new HarmonyLib.Harmony("com.lumods.superhackergolf.cheatsflag");
            MethodInfo prefix = typeof(SuperHackerGolf).GetMethod(
                nameof(CheatsEnabledPrefix),
                BindingFlags.NonPublic | BindingFlags.Static);
            harmony.Patch(target, new HarmonyMethod(prefix));
            cheatsEnabledHarmonyInstalled = true;
            MelonLogger.Msg("[SuperHackerGolf] Patched MatchSetupRules.IsCheatsEnabled (always true)");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] IsCheatsEnabled patch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool CheatsEnabledPrefix(ref bool __result)
    {
        __result = true;
        return false;  // skip original body
    }

    /// <summary>
    /// Player-usable items per the reversed ItemType enum:
    ///   None=0, Coffee=1, DuelingPistol=2, ElephantGun=3, Airhorn=4,
    ///   SpringBoots=5, GolfCart=6, RocketLauncher=7, Landmine=8,
    ///   Electromagnet=9, OrbitalLaser=10, RocketDriver=11, FreezeBomb=12
    /// </summary>
    private bool IsPlayerUsableItem(int value)
    {
        return value >= 1 && value <= 12;
    }

    /// <summary>
    /// Grant an item to the local player. Tries CmdAddItem first (works for
    /// anyone with the mod if the host also has it), falls back to
    /// ServerTryAddItem (host-only, bypasses IsCheatsEnabled entirely).
    /// </summary>
    internal void SpawnItemForLocalPlayer(int itemTypeValue)
    {
        EnsureItemSpawnerInitialized();
        if (!itemSpawnerAvailable)
        {
            MelonLogger.Warning("[SuperHackerGolf] Item spawner not available");
            return;
        }

        object inventory = GetLocalPlayerInventoryForSpawner();
        if (inventory == null)
        {
            MelonLogger.Warning("[SuperHackerGolf] Item spawner: local PlayerInventory not resolved");
            return;
        }

        object itemTypeBoxed;
        try { itemTypeBoxed = Enum.ToObject(cachedItemTypeEnum, itemTypeValue); }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SuperHackerGolf] Item spawner: enum convert failed {ex.Message}");
            return;
        }

        // PATH A: CmdAddItem. Client-authorized (requiresAuthority=true) so we
        // can invoke on our own PlayerInventory instance. Server-side handler
        // checks IsCheatsEnabled — our Harmony patch forces that true.
        if (cachedCmdAddItemMethod != null)
        {
            try
            {
                cachedCmdAddItemMethod.Invoke(inventory, new object[] { itemTypeBoxed });
                MelonLogger.Msg($"[SuperHackerGolf] Spawned ItemType({itemTypeValue}) via CmdAddItem");
                return;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SuperHackerGolf] CmdAddItem threw {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message} — trying ServerTryAddItem");
            }
        }

        // PATH B: ServerTryAddItem(ItemType, int maxUses). Host-only — requires
        // NetworkServer.active. Bypasses IsCheatsEnabled but won't work as a
        // non-host client.
        if (cachedServerTryAddItemMethod != null)
        {
            try
            {
                cachedServerTryAddItemMethod.Invoke(inventory, new object[] { itemTypeBoxed, 99 });
                MelonLogger.Msg($"[SuperHackerGolf] Spawned ItemType({itemTypeValue}) via ServerTryAddItem (host fallback)");
                return;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[SuperHackerGolf] ServerTryAddItem threw {ex.GetType().Name}: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        MelonLogger.Warning($"[SuperHackerGolf] Spawn ItemType({itemTypeValue}) failed on all paths");
    }

    /// <summary>
    /// Resolve the local PlayerInventory via GameManager.LocalPlayerInventory
    /// static property (primary path, per RE report). Falls back to the
    /// PlayerGolfer→PlayerInfo→Inventory chain we already use elsewhere.
    /// </summary>
    private object GetLocalPlayerInventoryForSpawner()
    {
        try
        {
            if (cachedGameManagerLocalPlayerInventoryProperty != null)
            {
                object inv = cachedGameManagerLocalPlayerInventoryProperty.GetValue(null, null);
                if (inv != null) return inv;
            }
        }
        catch { }

        return GetLocalPlayerInventory();
    }

    internal IList<ItemTypeEntry> GetItemSpawnCatalog()
    {
        if (!itemSpawnerInitialized) EnsureItemSpawnerInitialized();
        return itemSpawnCatalog;
    }
}
