#!/usr/bin/env bash
#
# Publish the already-built dist/ packages to Thunderstore.
#
# Usage:
#   scripts/publish-thunderstore.sh [version]
#
# Runs independently of the GitHub/Nexus publishing, so a failure in one cannot block the others.
#
# tcli requires a thunderstore.toml for `publish --file` (it supplies the community), so this script
# generates a throwaway one per package. The package identity/version comes from the zip's
# manifest.json.
#
# Env:
#   TCLI_AUTH_TOKEN   Thunderstore service-account token (required)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="${1:-$(sed -n 's/.*<CardShopCoopVersion>\(.*\)<\/CardShopCoopVersion>.*/\1/p' Directory.Build.props)}"

thunderstore_namespace="CardShopCoop"
thunderstore_community="tcg-card-shop-simulator"
website="https://github.com/meepen/CardShopCoop"

publish_thunderstore() { # <name> <description> <zip>
  local name="$1" description="$2" zip="$3"
  local cfg
  cfg="$(mktemp -d)/thunderstore.toml"
  cat > "$cfg" <<EOF
[config]
schemaVersion = "0.0.1"

[package]
namespace = "${thunderstore_namespace}"
name = "${name}"
versionNumber = "${version}"
description = "${description}"
websiteUrl = "${website}"
containsNsfwContent = false

[publish]
repository = "https://thunderstore.io"
communities = ["${thunderstore_community}"]
EOF
  echo "Publishing ${name} ${version} to Thunderstore"
  tcli publish --file "$zip" --config-path "$cfg"
}

: "${TCLI_AUTH_TOKEN:?TCLI_AUTH_TOKEN must be set to publish to Thunderstore}"

publish_thunderstore "CardShopCoopCommunity" \
  "True co-op multiplayer for TCG Card Shop Simulator." \
  "dist/thunderstore/CardShopCoopCommunity-${version}.zip"
publish_thunderstore "CardShopCoopCommunity_CustomTv" \
  "RTCGO Custom TV playback sync for CardShopCoopCommunity." \
  "dist/thunderstore/CardShopCoopCommunity_CustomTv-${version}.zip"
