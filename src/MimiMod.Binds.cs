using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

public partial class SuperHackerGolf
{
    // ── E31b: Unified bind system with Hold/Toggle/Released activation ──────
    //
    // Each named bind stores (Key, ActivationMode). The derived state is
    // polled each frame:
    //   Toggle:   wasPressedThisFrame → flip persistent state
    //   Hold:     persistent state = keyboard[key].isPressed (current frame)
    //   Released: persistent state = !keyboard[key].isPressed (inverted hold)
    //
    // Features consume the derived state via GetBindState(name). For Toggle
    // features the state is owned by the caller (e.g. assistEnabled); the
    // bind system drives it via SetBindState on each key edge. For Hold /
    // Released features the feature's boolean mirrors the bind state each
    // frame.
    //
    // Rebinding: GUI calls BeginRebind(name); next key press is captured
    // and written back to the bind. TickBinds handles the listening edge.

    public enum BindActivationMode
    {
        Toggle,
        Hold,
        Released,
    }

    internal class BindInfo
    {
        public string Name;
        public string DisplayName;
        public Key Key;
        public BindActivationMode Mode;
        public bool SupportsHoldModes;  // if false, GUI hides Hold/Released
        public Action<bool> OnStateChange; // called when derived state flips
        public bool StateOwnedByBind;   // true for Hold/Released — we drive it
        public bool LastDerivedState;
    }

    private readonly Dictionary<string, BindInfo> binds = new Dictionary<string, BindInfo>();
    private bool bindsRegistered;
    private string listeningBindName;           // non-null → capturing next key
    private double listeningBindStartedAt;

    internal void EnsureBindsRegistered()
    {
        if (bindsRegistered) return;
        bindsRegistered = true;

        RegisterBind("settings_gui", "Settings GUI", settingsGuiKey, BindActivationMode.Toggle, false, null);
        RegisterBind("assist", "Golf Assist", assistToggleKey, BindActivationMode.Toggle, false, on => { if (assistEnabled != on) ToggleAssist(); });
        RegisterBind("coffee", "Speed Boost", coffeeBoostKey, BindActivationMode.Toggle, false, on => { if (on) AddCoffeeBoost(); });
        RegisterBind("nearest_ball", "Nearest Ball", nearestBallModeKey, BindActivationMode.Toggle, false, on => { if (on) ToggleNearestBallMode(); });
        RegisterBind("unlock_cosmetics", "Unlock Cosmetics", unlockAllCosmeticsKey, BindActivationMode.Toggle, false, on => { if (on) UnlockAllCosmetics(); });
        RegisterBind("shield", "Force Shield", shieldToggleKey, BindActivationMode.Toggle, true, on => { if (shieldForcedOn != on) ToggleForcedShield(); });
        RegisterBind("weapon_aim", "Weapon Aimbot", weaponAssistToggleKey, BindActivationMode.Toggle, true, on =>
        {
            if (on && aimbotMode == AimbotMode.Off) SetAimbotMode(AimbotMode.Legit);
            else if (!on && aimbotMode != AimbotMode.Off) SetAimbotMode(AimbotMode.Off);
        });
        RegisterBind("mine_assist", "Mine Pre-Arm", mineAssistToggleKey, BindActivationMode.Toggle, false, on => { if (mineAssistEnabled != on) ToggleMineAssist(); });
        RegisterBind("bunnyhop", "Bunnyhop", bunnyhopToggleKey, BindActivationMode.Toggle, true, on => { if (bunnyhopEnabled != on) ToggleBunnyhop(); });
    }

    private void RegisterBind(string name, string display, Key initialKey, BindActivationMode initialMode, bool supportsHold, Action<bool> onChange)
    {
        binds[name] = new BindInfo
        {
            Name = name,
            DisplayName = display,
            Key = initialKey,
            Mode = initialMode,
            SupportsHoldModes = supportsHold,
            OnStateChange = onChange,
            StateOwnedByBind = initialMode != BindActivationMode.Toggle,
            LastDerivedState = false,
        };
    }

    internal BindInfo GetBind(string name)
    {
        EnsureBindsRegistered();
        BindInfo info;
        return binds.TryGetValue(name, out info) ? info : null;
    }

    internal IEnumerable<BindInfo> AllBinds()
    {
        EnsureBindsRegistered();
        return binds.Values;
    }

    internal void BeginRebind(string name)
    {
        listeningBindName = name;
        listeningBindStartedAt = Time.realtimeSinceStartupAsDouble;
        MelonLoader.MelonLogger.Msg($"[SuperHackerGolf] Rebind: listening for next key for '{name}'...");
    }

    internal bool IsListeningForRebind(string name)
    {
        return listeningBindName == name;
    }

    internal void CancelRebind()
    {
        listeningBindName = null;
    }

    // Called every frame from HandleKeyboardShortcuts. Replaces the
    // per-bind WasConfiguredKeyPressed calls with a unified state machine.
    internal void TickBinds()
    {
        EnsureBindsRegistered();
        Keyboard kb = Keyboard.current;
        if (kb == null) return;

        // Listen mode: capture next key press and route it to the listening bind.
        if (listeningBindName != null)
        {
            // Cancel rebinding on Escape
            try
            {
                if (kb[Key.Escape] != null && kb[Key.Escape].wasPressedThisFrame)
                {
                    MelonLoader.MelonLogger.Msg($"[SuperHackerGolf] Rebind cancelled for '{listeningBindName}'");
                    listeningBindName = null;
                    return;
                }
            }
            catch { }

            // Scan for the first key pressed this frame
            foreach (Key k in Enum.GetValues(typeof(Key)))
            {
                if (k == Key.None) continue;
                try
                {
                    var ctrl = kb[k];
                    if (ctrl != null && ctrl.wasPressedThisFrame)
                    {
                        BindInfo target;
                        if (binds.TryGetValue(listeningBindName, out target))
                        {
                            target.Key = k;
                            SyncLegacyKeyField(listeningBindName, k);
                            MelonLoader.MelonLogger.Msg($"[SuperHackerGolf] Rebind: '{listeningBindName}' -> {k}");
                        }
                        listeningBindName = null;
                        return;  // don't process this key as a normal input
                    }
                }
                catch { }
            }
            return;
        }

        // Normal input processing
        foreach (BindInfo info in binds.Values)
        {
            if (info.Key == Key.None) continue;
            ButtonControl ctrl = null;
            try { ctrl = kb[info.Key]; } catch { continue; }
            if (ctrl == null) continue;

            bool desiredState;
            switch (info.Mode)
            {
                case BindActivationMode.Hold:
                    desiredState = ctrl.isPressed;
                    if (desiredState != info.LastDerivedState)
                    {
                        info.LastDerivedState = desiredState;
                        info.OnStateChange?.Invoke(desiredState);
                    }
                    break;
                case BindActivationMode.Released:
                    desiredState = !ctrl.isPressed;
                    if (desiredState != info.LastDerivedState)
                    {
                        info.LastDerivedState = desiredState;
                        info.OnStateChange?.Invoke(desiredState);
                    }
                    break;
                case BindActivationMode.Toggle:
                default:
                    try
                    {
                        if (ctrl.wasPressedThisFrame)
                        {
                            // For toggle binds, the bind tracks state separately
                            // from the feature — OnStateChange flips it.
                            info.LastDerivedState = !info.LastDerivedState;
                            info.OnStateChange?.Invoke(info.LastDerivedState);
                        }
                    }
                    catch { }
                    break;
            }
        }
    }

    // Mirror the captured key back into the legacy Key fields so other
    // code that still reads them directly stays in sync.
    private void SyncLegacyKeyField(string name, Key k)
    {
        switch (name)
        {
            case "settings_gui": settingsGuiKey = k; settingsGuiKeyLabel = k.ToString(); break;
            case "assist": assistToggleKey = k; assistToggleKeyLabel = k.ToString(); break;
            case "coffee": coffeeBoostKey = k; coffeeBoostKeyLabel = k.ToString(); break;
            case "nearest_ball": nearestBallModeKey = k; nearestBallModeKeyLabel = k.ToString(); break;
            case "unlock_cosmetics": unlockAllCosmeticsKey = k; unlockAllCosmeticsKeyLabel = k.ToString(); break;
            case "shield": shieldToggleKey = k; shieldToggleKeyLabel = k.ToString(); break;
            case "weapon_aim": weaponAssistToggleKey = k; weaponAssistToggleKeyLabel = k.ToString(); break;
            case "mine_assist": mineAssistToggleKey = k; mineAssistToggleKeyLabel = k.ToString(); break;
            case "bunnyhop": bunnyhopToggleKey = k; bunnyhopToggleKeyLabel = k.ToString(); break;
        }
    }
}
