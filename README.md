# AimAssist — Super Battle Golf BepInEx Mod

A client-side aim assist mod for **Super Battle Golf** built with BepInEx 5.

## Features

| Feature | Description |
|---------|-------------|
| **Yaw assist** | Smoothly (or instantly) rotates your camera toward the hole while winding up a swing |
| **Pitch solve** | Analytically computes the correct launch angle so the ball lands at the hole |
| **Auto-fire** | Releases the swing at the exact optimal charge level — calculates the minimum power needed for the ball to reach the hole at the solved pitch |
| **Weapon assist** | Pulls aim toward the nearest valid target when using weapon items |
| **HUD overlay** | Corner info box showing hole distance, alignment status, and needed charge % |

## Quick-start presets

| Preset | Description |
|--------|-------------|
| `assist` (default) | Hold `Left Alt` to gently guide your aim; pitch solve and auto-fire off |
| `rage` | Always-on, instant snap, pitch solve enabled; auto-fire must be enabled separately |

Switch via the `Preset` setting — all individual settings reset to that preset's defaults.

## Auto-fire power calculation

Instead of always waiting for 100% charge, the mod binary-searches for the **minimum launch speed** at which the hole is reachable within the pitch limit, then converts that to a charge percentage. The HUD shows it:

```
Fire: AUTO [62%] ● ALIGNED!
```

If the hole is out of range even at full power, it falls back to 100%.

## Keybinds (defaults)

| Key | Action |
|-----|--------|
| `Left Alt` | Activate yaw assist (hold/toggle mode) |
| `F1` | Toggle HUD |

All keybinds and settings are configurable via the BepInEx `.cfg` file or any ModConfig-compatible in-game menu.

## Installation

### via r2modman (recommended)
Install through the mod manager — it handles the `plugins/` folder automatically.

### Manual
1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) for Super Battle Golf
2. Drop `AimAssist.dll` into `BepInEx/plugins/`
3. Launch the game — config file is created at `BepInEx/config/AimAssist.cfg`

## Building from source

Requires .NET SDK (netstandard2.1 target) and local game + BepInEx paths. Edit the paths in `AimAssistMod.csproj` first:

```xml
<GamePath>/path/to/Super Battle Golf_Data/Managed</GamePath>
<BepInExPath>/path/to/BepInEx</BepInExPath>
```

Then:

```bash
dotnet build -c Release
# or use the helper script:
bash install.sh
```

The release DLL lands at `bin/Release/netstandard2.1/AimAssist.dll`.

## CI

GitHub Actions builds the DLL on every push to `main` and on pull requests. The artifact is uploaded so you can grab a build without cloning locally. See [`.github/workflows/build.yml`](.github/workflows/build.yml).

> Game DLLs are not included in the repo (not redistributable). The CI build uses **stubs** generated from the public API surface so the compiler is satisfied without shipping game binaries.

## Project structure

```
src/
  Plugin.cs            — BepInEx plugin entry point, config bindings, preset logic
  AimAssistBehavior.cs — MonoBehaviour: yaw assist, pitch solve, auto-fire, weapon assist, HUD
  BallPredictor.cs     — Analytic projectile math + physics step simulation
  GameAccess.cs        — Reflection wrapper for all game types (PlayerGolfer, etc.)
  InputHelper.cs       — New Input System key/mouse helpers
package/               — Thunderstore package assets (manifest, README, icon)
install.sh             — One-shot build + install into r2modman Default profile
```

## Multiplayer

All logic is client-side only — no server authority required. Other players do not need the mod.
