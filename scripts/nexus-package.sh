#!/usr/bin/env bash
#
# Assemble Nexus Mods upload zips from the built GitHub release zips.
#
# Usage:
#   scripts/nexus-package.sh
#
# A Nexus upload is a plain archive the user extracts into the game folder, so these zips carry the
# BepInEx/plugins layout and no Thunderstore metadata. Expects the app zips in dist/ (produced by
# scripts/build.sh before this runs). Writes:
#   dist/nexus/CardShopCoop.zip
#   dist/nexus/CardShopCoop.ExternalModInterop.zip
#
# The upload file names carry no version: Nexus records the version separately on each file version.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

work="dist/nexus"

if [[ ! -f "dist/CardShopCoop.zip" ]]; then
  echo "error: dist/CardShopCoop.zip not found" >&2
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
unzip_into "dist/CardShopCoop.zip" "$core_src"
mkdir -p "$core_pkg/BepInEx/plugins/CardShopCoop"
cp "$core_src/CardShopCoop/CardShopCoop.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
cp "$core_src/CardShopCoop/CardShopCoop.Api.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
cp "$core_src/CardShopCoop/CardShopCoop.Steam.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
cp README.md "$core_pkg/README.md"
( cd "$core_pkg" && zip -q -r "../CardShopCoop.zip" BepInEx README.md )

# --- external mod interop package -----------------------------------------
tv_src="$work/.tv-src"
tv_pkg="$work/.tv"
unzip_into "dist/CardShopCoop.ExternalModInterop.zip" "$tv_src"
mkdir -p "$tv_pkg/BepInEx/plugins"
cp "$tv_src/CardShopCoop.CustomTv/CardShopCoop.CustomTv.dll" "$tv_pkg/BepInEx/plugins/"
cp "$tv_src/CardShopCoop.PurchaseOverhaul/CardShopCoop.PurchaseOverhaul.dll" "$tv_pkg/BepInEx/plugins/"
cp thunderstore/ExternalModInterop-README.md "$tv_pkg/README.md"
( cd "$tv_pkg" && zip -q -r "../CardShopCoop.ExternalModInterop.zip" BepInEx README.md )

rm -rf "$core_src" "$core_pkg" "$tv_src" "$tv_pkg"

echo "Built Nexus Mods packages:"
ls -1 "$work"/*.zip
