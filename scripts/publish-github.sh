#!/usr/bin/env bash
#
# Create the GitHub release for the already-built dist/ packages.
#
# Usage:
#   scripts/publish-github.sh [version]
#
# Runs independently of the Thunderstore/Nexus publishing, so a failure there cannot block this.
#
# Env:
#   GH_TOKEN          GitHub token for `gh release create` (required)
#   GITHUB_REF_NAME   tag, e.g. v2.0.1 (defaults to v<version>)
#   GITHUB_REPOSITORY owner/repo (defaults to the checkout's remote)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="${1:-$(sed -n 's/.*<CardShopCoopVersion>\(.*\)<\/CardShopCoopVersion>.*/\1/p' Directory.Build.props)}"
tag="${GITHUB_REF_NAME:-v${version}}"
repository="${GITHUB_REPOSITORY:-}"

: "${GH_TOKEN:?GH_TOKEN must be set to create the GitHub release}"

args=(release create "$tag" dist/*.zip --title "$tag" --generate-notes)
if [[ "$tag" == *-* ]]; then
  args+=(--prerelease)
fi
if [[ -n "$repository" ]]; then
  args+=(--repo "$repository")
fi

echo "Creating GitHub release ${tag}"
gh "${args[@]}"
