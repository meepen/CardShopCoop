#!/usr/bin/env bash
#
# Publish the already-built dist/ packages to Thunderstore.
#
# Usage:
#   scripts/publish-thunderstore.sh [version]
#
# Runs independently of the GitHub/Nexus publishing, so a failure in one cannot block the others.
# Idempotent: a package whose version is already published is skipped, so re-running after a partial
# failure does not clash on the package that did go up.
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

# True if <namespace>/<name> already has this version published. A missing package (404) or any
# lookup failure returns false, so we fall through and let tcli publish.
already_published() { # <name>
  local name="$1" latest
  latest=$(curl -fsS "https://thunderstore.io/api/experimental/package/${thunderstore_namespace}/${name}/?cb=${RANDOM}${RANDOM}" 2>/dev/null \
    | python3 -c "import sys,json;print(json.load(sys.stdin)['latest']['version_number'])" 2>/dev/null || true)
  [[ -n "$latest" && "$latest" == "$version" ]]
}

publish_thunderstore() { # <name> <description> <zip>
  local name="$1" description="$2" zip="$3"
  local cfg
  if already_published "$name"; then
    echo "${name} ${version} is already on Thunderstore; skipping."
    return 0
  fi
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
publish_thunderstore "CardShopCoopCommunity_ExternalModInterop" \
  "Optional external-mod interop bundle for CardShopCoopCommunity (currently RTCGO Custom TV sync)." \
  "dist/thunderstore/CardShopCoopCommunity_ExternalModInterop-${version}.zip"
