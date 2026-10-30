#!/usr/bin/env bash
#
# Build CardShopCoopCommunity and package every distributable into dist/.
#
# Usage:
#   scripts/build.sh
#
# Requires the game assemblies (GamePath / CARDSHOP_GAMEPATH) for compilation. Produces:
#   dist/CardShopCoop.zip                       GitHub release / manual install
#   dist/CardShopCoop.Api.zip                   API-only package
#   dist/CardShopCoop.ExternalModInterop.zip    Custom TV bundle
#   dist/CardShopCoop.SampleMod.zip             Example-mod source (Nexus "miscellaneous")
#   dist/thunderstore/...                       Thunderstore packages (see scripts/thunderstore-package.sh)
#   dist/nexus/...                              Nexus Mods packages   (see scripts/nexus-package.sh)
#
# Publishing is separate: scripts/publish-github.sh and scripts/publish-thunderstore.sh.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version=$(sed -n 's/.*<CardShopCoopVersion>\(.*\)<\/CardShopCoopVersion>.*/\1/p' Directory.Build.props)
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: invalid CardShopCoopVersion '$version' in Directory.Build.props" >&2
  exit 1
fi
echo "Building CardShopCoopCommunity ${version}"

rm -rf dist
mkdir -p dist

dotnet restore src/CardShopCoop/CardShopCoop.csproj --locked-mode --configfile NuGet.Config
dotnet build src/CardShopCoop.Api/CardShopCoop.Api.csproj -c Release -p:Package=true --no-restore
dotnet build src/CardShopCoop/CardShopCoop.csproj -c Release -p:Deploy=false -p:Package=true --no-restore
dotnet build src/CardShopCoop.CustomTv/CardShopCoop.CustomTv.csproj -c Release -p:Deploy=false -p:Package=true

cp "src/CardShopCoop/bin/Release/CardShopCoop.zip" dist/
cp "src/CardShopCoop.Api/bin/Release/CardShopCoop.Api.zip" dist/
cp "src/CardShopCoop.CustomTv/bin/Release/CardShopCoop.ExternalModInterop.zip" dist/

# Example-mod source, tracked files only (for a Nexus "miscellaneous" file). git archive excludes
# bin/obj and anything else not committed. The API guide is included next to the sample so the
# downloaded source is self-contained.
sample_stage="$(mktemp -d)"
sample_root="$sample_stage/CardShopCoop.SampleMod"
mkdir -p "$sample_root/docs"
git archive --format=tar HEAD:samples/CardShopCoop.SampleMod | tar -xf - -C "$sample_root"
git show HEAD:docs/third-party-integration.md > "$sample_root/docs/third-party-integration.md"
(
  cd "$sample_stage"
  zip -q -r "$root/dist/CardShopCoop.SampleMod.zip" CardShopCoop.SampleMod
)
rm -rf "$sample_stage"

bash scripts/thunderstore-package.sh "$version"
bash scripts/nexus-package.sh

echo
echo "All packages:"
find dist -name '*.zip' | sort
