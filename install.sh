#!/usr/bin/env bash
# Builds MimiMod and copies the DLL into the game's MelonLoader Mods folder.
#
# REQUIREMENTS:
#   - MelonLoader must be installed into the game folder first
#     (run Installer/MimiModInstaller.exe via Proton, or unzip MelonLoader.x64.zip
#      into the game folder and launch once so MelonLoader generates its dirs)
#   - Unity reference DLLs are read from the game's Managed folder at build time
#   - r2modman's BepInEx winhttp.dll should be removed or the game should be
#     launched directly via Steam (not through r2modman) so MelonLoader's
#     version.dll proxy gets loaded instead.
set -e

GAME_ROOT="/mnt/ssd/.games/steamapps/common/Super Battle Golf"
MODS_DIR="$GAME_ROOT/Mods"

echo "Building..."
dotnet build -c Release --nologo -v q

SRC="bin/Release/MimiMod.dll"
if [[ ! -f "$SRC" ]]; then
    echo "ERROR: build produced no $SRC" >&2
    exit 1
fi

if [[ ! -d "$GAME_ROOT" ]]; then
    echo "ERROR: game folder not found at $GAME_ROOT" >&2
    exit 1
fi

mkdir -p "$MODS_DIR"
cp "$SRC" "$MODS_DIR/MimiMod.dll"

echo "Installed to: $MODS_DIR/MimiMod.dll"
echo "Launch the game via Steam to test (NOT via r2modman — it overrides with BepInEx proxy)."
