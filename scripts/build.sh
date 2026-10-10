#!/usr/bin/env bash
#
# Build CardShopCoopCommunity and package every distributable into dist/.
#
# Usage:
#   scripts/build.sh
#
# Requires the game assemblies (GamePath / CARDSHOP_GAMEPATH) for compilation. Produces:
#   dist/CardShopCoop-<version>.zip                  GitHub release / manual install
#   dist/CardShopCoop.Api-<version>.zip              API-only package
#   dist/CardShopCoop.ExternalModInterop.zip         Custom TV bundle
#   dist/thunderstore/...                            Thunderstore packages (see scripts/thunderstore-package.sh)
#   dist/nexus/...                                   Nexus Mods packages   (see scripts/nexus-package.sh)
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

cp "src/CardShopCoop/bin/Release/CardShopCoop-${version}.zip" dist/
cp "src/CardShopCoop.Api/bin/Release/CardShopCoop.Api-${version}.zip" dist/
cp "src/CardShopCoop.CustomTv/bin/Release/CardShopCoop.ExternalModInterop.zip" dist/

bash scripts/thunderstore-package.sh "$version"
bash scripts/nexus-package.sh "$version"

echo
echo "All packages:"
find dist -name '*.zip' | sort
