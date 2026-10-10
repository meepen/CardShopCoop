#!/usr/bin/env bash
#
# Assemble Nexus Mods upload zips from the built GitHub release zips.
#
# Usage:
#   scripts/nexus-package.sh <version>
#
# A Nexus upload is a plain archive the user extracts into the game folder, so these zips carry the
# BepInEx/plugins layout and no Thunderstore metadata. Expects the app zips in dist/ (produced by
# scripts/build.sh before this runs). Writes:
#   dist/nexus/CardShopCoopCommunity-<version>.zip
#   dist/nexus/CardShopCoopCommunity_CustomTv-<version>.zip
set -euo pipefail

version="${1:?usage: nexus-package.sh <version>}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

work="dist/nexus"

if [[ ! -f "dist/CardShopCoop-${version}.zip" ]]; then
  echo "error: dist/CardShopCoop-${version}.zip not found" >&2
  exit 1
fi
if [[ ! -f "dist/CardShopCoop.ExternalModInterop.zip" ]]; then
  echo "error: dist/CardShopCoop.ExternalModInterop.zip not found" >&2
  exit 1
fi

rm -rf "$work"
mkdir -p "$work"

unzip_into() { # <zip> <dest>
  rm -rf "$2"
  mkdir -p "$2"
  unzip -q -o "$1" -d "$2"
}

# --- core package ---------------------------------------------------------
core_src="$work/.core-src"
core_pkg="$work/.core"
unzip_into "dist/CardShopCoop-${version}.zip" "$core_src"
mkdir -p "$core_pkg/BepInEx/plugins/CardShopCoop"
cp "$core_src/CardShopCoop/CardShopCoop.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
cp "$core_src/CardShopCoop/CardShopCoop.Api.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
cp README.md "$core_pkg/README.md"
( cd "$core_pkg" && zip -q -r "../CardShopCoopCommunity-${version}.zip" BepInEx README.md )

# --- custom tv package ----------------------------------------------------
tv_src="$work/.tv-src"
tv_pkg="$work/.tv"
unzip_into "dist/CardShopCoop.ExternalModInterop.zip" "$tv_src"
mkdir -p "$tv_pkg/BepInEx/plugins"
cp "$tv_src/CardShopCoop.CustomTv/CardShopCoop.CustomTv.dll" "$tv_pkg/BepInEx/plugins/"
cp thunderstore/CustomTv-README.md "$tv_pkg/README.md"
( cd "$tv_pkg" && zip -q -r "../CardShopCoopCommunity_CustomTv-${version}.zip" BepInEx README.md )

rm -rf "$core_src" "$core_pkg" "$tv_src" "$tv_pkg"

echo "Built Nexus Mods packages:"
ls -1 "$work"/*.zip
