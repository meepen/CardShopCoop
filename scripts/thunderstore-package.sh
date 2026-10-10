#!/usr/bin/env bash
#
# Assemble Thunderstore packages from the built GitHub release zips.
#
# Usage:
#   scripts/thunderstore-package.sh <version>
#
# Expects the GitHub release artifacts already downloaded into dist/:
#   dist/CardShopCoop-<version>.zip            -> CardShopCoop/CardShopCoop.dll, CardShopCoop.Api.dll
#   dist/CardShopCoop.ExternalModInterop.zip   -> CardShopCoop.CustomTv/CardShopCoop.CustomTv.dll
#
# and the Thunderstore metadata in thunderstore/ (manifest.core.json, manifest.customtv.json,
# CustomTv-README.md, icon.png at 256x256). Writes:
#   dist/thunderstore/CardShopCoopCommunity-<version>.zip
#   dist/thunderstore/CardShopCoopCommunity_CustomTv-<version>.zip
#
# Thunderstore requires a zip whose root holds manifest.json, icon.png and README.md, with the
# plugin files laid out under BepInEx/plugins/.
set -euo pipefail

version="${1:?usage: thunderstore-package.sh <version>}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

meta="thunderstore"
work="dist/thunderstore"
icon="$meta/icon.png"

if [[ ! -f "$icon" ]]; then
  echo "error: $icon is missing - Thunderstore requires a 256x256 PNG icon" >&2
  exit 1
fi
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

settle_manifest() { # <template> <dest>
  sed "s/__VERSION__/${version}/g" "$1" > "$2"
}

# --- core package ---------------------------------------------------------
core_src="$work/.core-src"
core_pkg="$work/.core"
unzip_into "dist/CardShopCoop-${version}.zip" "$core_src"
mkdir -p "$core_pkg/BepInEx/plugins/CardShopCoop"
cp "$core_src/CardShopCoop/CardShopCoop.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
cp "$core_src/CardShopCoop/CardShopCoop.Api.dll" "$core_pkg/BepInEx/plugins/CardShopCoop/"
settle_manifest "$meta/manifest.core.json" "$core_pkg/manifest.json"
cp "$icon" "$core_pkg/icon.png"
cp README.md "$core_pkg/README.md"
cp CHANGELOG.md "$core_pkg/CHANGELOG.md"
( cd "$core_pkg" && zip -q -r "../CardShopCoopCommunity-${version}.zip" BepInEx manifest.json icon.png README.md CHANGELOG.md )

# --- custom tv package ----------------------------------------------------
tv_src="$work/.tv-src"
tv_pkg="$work/.tv"
unzip_into "dist/CardShopCoop.ExternalModInterop.zip" "$tv_src"
mkdir -p "$tv_pkg/BepInEx/plugins"
cp "$tv_src/CardShopCoop.CustomTv/CardShopCoop.CustomTv.dll" "$tv_pkg/BepInEx/plugins/"
settle_manifest "$meta/manifest.customtv.json" "$tv_pkg/manifest.json"
cp "$icon" "$tv_pkg/icon.png"
cp "$meta/CustomTv-README.md" "$tv_pkg/README.md"
( cd "$tv_pkg" && zip -q -r "../CardShopCoopCommunity_CustomTv-${version}.zip" BepInEx manifest.json icon.png README.md )

rm -rf "$core_src" "$core_pkg" "$tv_src" "$tv_pkg"

echo "Built Thunderstore packages:"
ls -1 "$work"/*.zip
