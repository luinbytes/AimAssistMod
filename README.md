# MimiMod (forked) — Super Battle Golf MelonLoader Mod

Client-side aim assist + trajectory prediction + impact preview mod for **Super Battle Golf**, built on MelonLoader.

This repo is a fork of [MidTano/Mimi-Mod-Super-Battle-Golf](https://github.com/MidTano/Mimi-Mod-Super-Battle-Golf) with additional features grafted on:

- Weapon assist (lock-on cone for item phase)
- Analytic pitch solver (complements the existing binary-search release timing)
- Wind compensation in trajectory prediction
- Defensive reflection fallback cascades (multi-name method/field lookups for game-update resilience)
- Anticheat rate-limit awareness (self-throttling against the game's `AntiCheatRateChecker`)

## Requirements

- Super Battle Golf installed
- MelonLoader 0.7.x installed into the game folder (not via r2modman — see notes)
- .NET SDK 6.0+ for building

## Building

```bash
dotnet build -c Release
```

Outputs `bin/Release/MimiMod.dll`. The csproj references:
- MelonLoader DLLs from `ci/melonloader/MelonLoader/net35/` (committed? no — fetched once by `scripts/fetch-melonloader.sh`)
- Unity game DLLs from `$(GamePath)` (set in the csproj — defaults to `/mnt/ssd/.games/steamapps/common/Super Battle Golf/Super Battle Golf_Data/Managed`)

## Installing

```bash
./install.sh
```

Builds and copies `MimiMod.dll` into the game's `Mods/` folder. Launch the game via **Steam directly**, not r2modman (r2modman's BepInEx `winhttp.dll` proxy shadows MelonLoader's `version.dll` proxy).

## Project structure

```
src/
  MimiMod.cs              — main partial class, reflection caches, fields
  MimiMod.Camera.cs       — orbit-camera aim assist via reflection
  MimiMod.Config.cs       — plaintext key=value config parser
  MimiMod.Context.cs      — PlayerMovement / PlayerGolfer / GolfBall resolution
  MimiMod.Cosmetics.cs    — unlock all cosmetics
  MimiMod.ImpactPreview.cs— offscreen RenderTexture impact preview window
  MimiMod.Runtime.cs      — OnUpdate / OnLateUpdate lifecycle
  MimiMod.Swing.cs        — auto swing release with binary-search timing
  MimiMod.Trajectory.cs   — forward-sim trajectory with optional drag
  MimiMod.UI.cs           — Canvas + TextMeshProUGUI HUD
  Helpers/
    ModReflectionHelper.cs — reflection member caching + fallback cascades
    ModTextHelper.cs       — string helpers
```

## Anticheat awareness

The game ships an `AntiCheat.dll` containing `AntiCheatRateChecker` + `AntiCheatPerPlayerRateChecker` — a server-side rate limiter on networked actions. It emits `PlayerSuspiciousActivityDetected` / `PlayerConfirmedCheatingDetected` events when per-connection hit rates exceed configured thresholds. This mod reads `expectedMinTimeBetweenHits` at runtime and throttles auto-actions to respect it. See `src/MimiMod.AntiCheat.cs` (once grafted).

## Credits

- Original base: [MidTano/Mimi-Mod-Super-Battle-Golf](https://github.com/MidTano/Mimi-Mod-Super-Battle-Golf)
- Fork maintenance: luinbytes
