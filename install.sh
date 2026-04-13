#!/usr/bin/env bash
# Builds and installs AimAssist directly into the r2modman Default profile.
set -e

PROFILE_PLUGINS="/home/lu/.config/r2modmanPlus-local/SuperBattleGolf/profiles/Default/BepInEx/plugins"
PLUGIN_DIR="$PROFILE_PLUGINS/lumods-AimAssist"

echo "Building..."
dotnet build -c Release --nologo -v q

SRC="bin/Release/netstandard2.1/AimAssist.dll"

mkdir -p "$PLUGIN_DIR"
cp "$SRC" "$PLUGIN_DIR/AimAssist.dll"

echo "Installed to: $PLUGIN_DIR/AimAssist.dll"
echo "Launch the game via r2modman to test!"
