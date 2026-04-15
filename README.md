# SuperHackerGolf

![Build](https://github.com/luinbytes/SuperHackerGolf/actions/workflows/build.yml/badge.svg)

Client-side cheat mod for **Super Battle Golf** built on MelonLoader.

Made by **[@luinbytes](https://github.com/luinbytes)** — Discord: `@luinbytes` · Portfolio: <https://luinbytes.github.io>

## Features

### Golf assist
- **Aim assist** — orbit-camera auto-aim toward the hole with pitch solver and aim-target offset
- **Auto-release** — releases the swing at the optimal power to land on the hole
- **Wind-aware trajectory prediction** — exact reimplementation of `Hittable.ApplyAirDamping` so the predicted line matches the real ball physics (reads `WindFactor` / `CrossWindFactor` from the game's `WindHittableSettings` via reflection)
- **Decompiled physics** — launch-speed formula, air damping, bounce chain, and ground roll all reproduced from static analysis of `GameAssembly.dll` rather than guessed coefficients
- **Per-contact terrain layer settings** — bounce chain and roll phase query `TerrainManager.GetDominantLayerSettingsAtPoint` at each contact so wet grass, sand, and cart paths use the real game bounciness / damping values
- **Cup-rim aware targeting** — predicted path and aim target account for `GolfHole` trigger geometry, so long approaches aim at the rim rather than overshooting
- **Crosswind aim compensation** — 2D solver nudges the aim target so the ball curves into the hole under wind (long shots only — skipped under 15m where drift is negligible)
- **Overcharge clamp** — disables the game's 115% overcharge cap by default so shots stay accurate
- **Manual-charge mode** — when off, the player charges the swing manually and the mod only releases at optimal power
- **Impact preview window** — offscreen render-texture camera showing the predicted landing zone in a HUD panel
- **Trail visualizers** — predicted, frozen, and actual shot trails rendered as `LineRenderer`s with per-trail colors and widths

### Combat
- **Weapon aimbot** (Legit / Rage / Custom modes)
  - **Legit** — rotates the `OrbitCameraModule` yaw / pitch toward the target with a configurable smoothing curve (Linear / EaseOut / Spring), narrow FOV cone, visibility linecast, manual-fire by default
  - **Rage** — silent-aim Harmony postfix on `GetFirearmAimPoint`: the shot direction is rewritten just before the game reads it, so the camera doesn't move but the bullet goes where you want; wide FOV cone, auto-fire, skip-protected-only filter
  - **Custom** — every sub-setting (cone, range, smoothing, sticky-lock, auto-fire, target types) individually configurable in the settings GUI
  - Target filtering: players, target dummies, landmines, golf carts, and skip-protected flag
  - Hitbox priority: Head (+1.4m) / Chest (center mass) / Legs (-0.6m), multi-select with fixed priority order
- **Force shield** — writes `PlayerInfo.isElectromagnetShieldActive` and `PlayerMovement.knockoutImmunityStatus` directly via reflection to force the local player's electromagnet bubble and knockout immunity on, then invokes the original SyncVar hooks locally so the VFX, audio and collider all spawn
- **Mine pre-arm** (host-only) — Harmony-patches the `Landmine` collision / hit handlers to arm mines instantly regardless of the normal delay
- **Bunnyhop** — while the space bind is active, skips the `PlayerMovement` grounded check so repeated jumps register without landing friction

### ESP overlay
- **Box / corner brackets** around every in-frustum `LockOnTarget`
- **Name**, **distance** (meters), and **health bar** labels with outlined IMGUI text
- **Tracers** from the screen bottom to each target
- Per-category colors (players / dummies / mines / golf carts) with separate visible / invisible tints
- Visibility check via single `Physics.Linecast` to the target bounds center
- Bounds resolution cascade: `SkinnedMeshRenderer` → `Renderer[]` encapsulated → `Collider` → `CharacterController` synthetic box → fallback capsule
- Health reflection auto-resolves `currentHealth` / `maxHealth` fields or properties on `PlayerInfo` if present
- Snapshot is built in `LateUpdate` and drawn in `OnGUI` (Repaint-gated) to keep Layout passes cheap
- Draws a `[PROT]` badge next to shielded targets

### FOV circle overlay
- Hollow ring texture procedurally generated with analytic AA and cached until the size / tint changes
- Radius computed from `weaponAssistConeAngleDeg` and the active camera's vertical FOV so the circle matches the actual aim cone on screen
- Auto-clamped at 80% of screen height with a "FOV cone ≥ screen" hint when the cone exceeds the viewport

### Item spawner (debug)
- IMGUI grid of every `ItemType` enum value (12 player-usable items: Coffee, DuelingPistol, ElephantGun, Airhorn, SpringBoots, GolfCart, RocketLauncher, Landmine, Electromagnet, OrbitalLaser, RocketDriver, FreezeBomb)
- Calls `PlayerInventory.CmdAddItem(ItemType)` via reflection on the local `PlayerInventory` instance (client-authorized `[Command]` — runs server-side)
- Falls back to `PlayerInventory.ServerTryAddItem(ItemType, int)` on non-authorized hosts
- Works in lobbies that would normally gate the debug spawner

### Miscellaneous
- **Coffee speed boost** hotkey
- **Nearest-ball mode** — assist targets the closest ball instead of the local ball
- **Cosmetics unlock** — unlocks all cosmetics via reflection
- **HUD overlay** — `Canvas` + `TextMeshProUGUI` corners showing player name, assist status, and the current active bindings
- **Shot prediction telemetry** — optional CSV writer that logs predicted-vs-actual impact for every auto-fired shot
- **In-game clickable settings GUI** — tabbed IMGUI window (Aim / Combat / Visuals / Physics / Data / Config) with toggles, sliders, dropdowns, save / reload buttons and per-bind rebinding

### Unified bind system
Every hotkey is rebindable from the in-game CONFIG tab — click a bind slot, press any key to capture it, or press Escape to cancel. Binds that control a held action (**weapon aimbot**, **force shield**, **bunnyhop**) additionally support three activation modes:

- **Toggle** — press once to turn on, press again to turn off
- **Hold** — active only while the key is held down
- **Released** — active only while the key is not held (inverse hold)

## Default hotkeys

| Key       | Action                            |
|-----------|-----------------------------------|
| `F`       | Toggle golf aim assist            |
| `F2`      | Coffee speed boost                |
| `F3`      | Nearest-ball mode                 |
| `F4`      | Unlock all cosmetics              |
| `F6`      | Force shield (toggle / hold / released) |
| `F7`      | Weapon aimbot (toggle / hold / released) |
| `F9`      | Mine pre-arm (host-only)          |
| `F10`     | Bunnyhop (toggle / hold / released) |
| `Insert`  | Open / close settings GUI         |
| `Mouse4`  | Default weapon-aimbot hold key    |
| RMB       | Auto-aim camera (hold)            |

All binds are persisted to `Mods/SuperHackerGolf.cfg` when you hit **Save** on the CONFIG tab.

## Requirements

- Super Battle Golf installed
- MelonLoader 0.7.2 installed into the game folder (do **not** use r2modman's BepInEx proxy — it shadows MelonLoader's `version.dll`)
- .NET SDK 8.0+ for building

Steam launch options must include the Wine DLL override so the game loads MelonLoader's `version.dll`:

```
WINEDLLOVERRIDES="version=n,b" %command%
```

## Building

Local developer build (references real Unity module DLLs from the game install):

```bash
dotnet build -c Release
./install.sh
```

Outputs `bin/Release/SuperHackerGolf.dll` and copies it into `<gamefolder>/Mods/`.

### CI build (no game install required)

GitHub Actions builds the mod from a clean checkout by first compiling a handwritten stub assembly (`ci/stubs/UnityStubs.csproj`) that exports every `UnityEngine.*`, `TMPro`, `MelonLoader` and `HarmonyLib` type the mod references, then compiling the mod against that single stub DLL:

```bash
dotnet build ci/stubs/UnityStubs.csproj -c Release
dotnet build SuperHackerGolf.csproj -c Release /p:CI=true
```

The `CI=true` property flips the conditional `<ItemGroup>` in `SuperHackerGolf.csproj` between the game-install references (local mode) and the stub reference (CI mode). Stub sources are under `ci/stubs/` and are excluded from the main project's compile glob.

## Project structure

```
src/
  MimiMod.cs                   — main partial class, reflection caches, field declarations
  MimiMod.AntiCheat.cs         — anti-cheat related helpers
  MimiMod.AntiCheatBypass.cs   — kick-resistance layer
  MimiMod.Binds.cs             — unified bind system, rebinding capture, Hold/Toggle/Released state machine
  MimiMod.Camera.cs            — orbit-camera aim assist via reflection
  MimiMod.Config.cs            — plaintext key=value config parser
  MimiMod.Context.cs           — PlayerMovement / PlayerGolfer / GolfBall resolution
  MimiMod.Cosmetics.cs         — cosmetics unlock
  MimiMod.Esp.cs               — ESP snapshot build and OnGUI overlay draw
  MimiMod.FovOverlay.cs        — FOV circle procedural ring texture + draw
  MimiMod.GamePhysicsReflection.cs — GameManager / HittableSettings / LayerSettings cache
  MimiMod.GuiKit.cs            — shared IMGUI primitives (FillRect, BoxOutline, Line, LabelOutlined, world-to-gui projection)
  MimiMod.ImpactPreview.cs     — offscreen render-texture impact preview camera
  MimiMod.ItemSpawner.cs       — ItemType enum scan + CmdAddItem / ServerTryAddItem invoker
  MimiMod.PitchSolver.cs       — analytic closed-form pitch / speed solver library
  MimiMod.PredictionTelemetry.cs — shot prediction CSV logger and live GUI
  MimiMod.Runtime.cs           — OnApplicationStart / OnUpdate / OnLateUpdate / OnGUI lifecycle
  MimiMod.SettingsGui.cs       — tabbed IMGUI settings window (Aim / Combat / Visuals / Physics / Data / Config)
  MimiMod.Shield.cs            — electromagnet bubble + knockout immunity force-enable
  MimiMod.Swing.cs             — auto swing release with binary-search timing
  MimiMod.TerrainReflection.cs — TerrainManager / TerrainLayerSettings / GolfHoleTrigger cache
  MimiMod.Trajectory.cs        — exact game forward-sim + wind-aware solvers + bounce chain + roll phase
  MimiMod.UI.cs                — Canvas + TextMeshProUGUI HUD, LineRenderer trail creation
  MimiMod.WeaponAssist.cs      — aimbot modes, target enumeration, silent-aim postfix, legit camera steer
  MimiMod.Wind.cs              — WindManager + HittableSettings reflection
  Helpers/
    ModReflectionHelper.cs     — reflection member caching + fallback cascades
    ModTextHelper.cs           — string helpers

ci/
  stubs/
    UnityStubs.csproj          — netstandard2.1 stub project producing UnityEngine.dll
    UnityEngine.cs             — handwritten stubs for UnityEngine + UI + Rendering + SceneManagement namespaces
    UnityInputSystem.cs        — handwritten stubs for UnityEngine.InputSystem + Controls namespaces
    MelonLoader.cs             — handwritten stubs for MelonLoader attributes, MelonMod, MelonLogger
    HarmonyLib.cs              — handwritten stubs for Harmony, HarmonyMethod, AccessTools, attributes
    TextMeshPro.cs             — handwritten stubs for TMPro.TextMeshProUGUI and related enums
  melonloader/                 — real MelonLoader 0.7.2 binaries, used for local-mode builds only

.github/workflows/build.yml    — GitHub Actions workflow: stubs → mod → upload artifact → attach to release
```

## Credits

Made by [@luinbytes](https://github.com/luinbytes) — <https://luinbytes.github.io>
