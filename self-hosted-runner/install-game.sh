#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
GAME_DIR="$ROOT_DIR/game"
BEPINEX_VERSION="5.4.23.0"
BEPINEX_URL="https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_x64_${BEPINEX_VERSION}.zip"

command -v docker >/dev/null || { echo "docker is required" >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required" >&2; exit 1; }
command -v unzip >/dev/null || { echo "unzip is required" >&2; exit 1; }

mkdir -p "$GAME_DIR"
printf 'Steam username: '
read -r STEAM_USERNAME
if [[ -z "$STEAM_USERNAME" ]]; then
  echo "Steam username cannot be empty" >&2
  exit 1
fi

echo "Downloading TCG Card Shop Simulator (Steam App ID 3070070)..."
docker run --rm -it \
  -v "$GAME_DIR:/game" \
  cm2network/steamcmd:latest \
  +@sSteamCmdForcePlatformType windows \
  +force_install_dir /game \
  +login "$STEAM_USERNAME" \
  +app_update 3070070 validate \
  +quit

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

echo "Downloading BepInEx ${BEPINEX_VERSION}..."
curl --fail --location --output "$TEMP_DIR/BepInEx.zip" "$BEPINEX_URL"
unzip -o "$TEMP_DIR/BepInEx.zip" -d "$GAME_DIR"

test -f "$GAME_DIR/Card Shop Simulator_Data/Managed/Assembly-CSharp.dll"
test -f "$GAME_DIR/BepInEx/core/BepInEx.dll"
echo "Game references and BepInEx are ready in $GAME_DIR"
