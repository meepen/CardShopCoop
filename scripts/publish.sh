#!/usr/bin/env bash
#
# Publish the already-built dist/ packages to Thunderstore and create the GitHub release.
#
# Usage:
#   scripts/publish.sh [version]
#
# Nexus Mods uploads are handled by the workflow via the official Nexus-Mods/upload-action, because
# that is a GitHub Action rather than a CLI.
#
# tcli requires a thunderstore.toml for `publish --file` (it supplies the community), so this script
# generates a throwaway one per package. The package identity/version comes from the zip's
# manifest.json.
#
# Env:
#   TCLI_AUTH_TOKEN   Thunderstore service-account token (required for stable releases)
#   GH_TOKEN          GitHub token for `gh release create`
#   GITHUB_REF_NAME   tag, e.g. v2.0.1 (defaults to v<version>)
#   GITHUB_REPOSITORY owner/repo (defaults to the checkout's remote)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="${1:-$(sed -n 's/.*<CardShopCoopVersion>\(.*\)<\/CardShopCoopVersion>.*/\1/p' Directory.Build.props)}"
tag="${GITHUB_REF_NAME:-v${version}}"
repository="${GITHUB_REPOSITORY:-}"

thunderstore_namespace="Meepen"
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

echo "Publishing CardShopCoopCommunity ${version} (tag ${tag})"

if [[ -f "dist/thunderstore/CardShopCoopCommunity-${version}.zip" ]]; then
  if [[ "$tag" == *-* ]]; then
    echo "Prerelease tag: skipping Thunderstore (stable releases only)."
  else
    : "${TCLI_AUTH_TOKEN:?TCLI_AUTH_TOKEN must be set to publish to Thunderstore}"
    publish_thunderstore "CardShopCoopCommunity" \
      "True co-op multiplayer for TCG Card Shop Simulator." \
      "dist/thunderstore/CardShopCoopCommunity-${version}.zip"
    publish_thunderstore "CardShopCoopCommunity_CustomTv" \
      "RTCGO Custom TV playback sync for CardShopCoopCommunity." \
      "dist/thunderstore/CardShopCoopCommunity_CustomTv-${version}.zip"
  fi
else
  echo "warning: dist/thunderstore packages not found; skipping Thunderstore." >&2
fi

if [[ -n "${GH_TOKEN:-}" ]]; then
  args=(release create "$tag" dist/*.zip --title "$tag" --generate-notes)
  if [[ "$tag" == *-* ]]; then
    args+=(--prerelease)
  fi
  if [[ -n "$repository" ]]; then
    args+=(--repo "$repository")
  fi
  gh "${args[@]}"
else
  echo "warning: GH_TOKEN not set; skipping GitHub release." >&2
fi
