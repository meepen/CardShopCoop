#!/usr/bin/env bash
#
# Create (or update) the GitHub release for the already-built dist/ packages.
#
# Usage:
#   scripts/publish-github.sh [version]
#
# Uses the "## <version>" section of CHANGELOG.md as the release body (so the player-facing notes are
# published here too) and attaches the user-facing app zips AND the Thunderstore packages. Runs
# independently of the Thunderstore/Nexus publishing, so a failure there cannot block this.
#
# Env:
#   GH_TOKEN          GitHub token for `gh release` (required)
#   GITHUB_REF_NAME   tag, e.g. v2.0.3 (defaults to v<version>)
#   GITHUB_REPOSITORY owner/repo (defaults to the checkout's remote)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="${1:-$(sed -n 's/.*<CardShopCoopVersion>\(.*\)<\/CardShopCoopVersion>.*/\1/p' Directory.Build.props)}"
tag="${GITHUB_REF_NAME:-v${version}}"
repository="${GITHUB_REPOSITORY:-}"

: "${GH_TOKEN:?GH_TOKEN must be set to create the GitHub release}"

notes_file="$(mktemp)"
if ! bash scripts/changelog-extract.sh "$version" > "$notes_file" 2>/dev/null; then
  echo "error: no CHANGELOG.md section for ${version}; add one before releasing." >&2
  exit 1
fi

shopt -s nullglob
files=(dist/*.zip dist/thunderstore/*.zip)
shopt -u nullglob
if [[ ${#files[@]} -eq 0 ]]; then
  echo "error: no release assets found under dist/" >&2
  exit 1
fi

repo_args=()
if [[ -n "$repository" ]]; then
  repo_args=(--repo "$repository")
fi

if gh release view "$tag" "${repo_args[@]}" >/dev/null 2>&1; then
  echo "GitHub release ${tag} already exists; uploading assets and notes"
  gh release upload "$tag" "${files[@]}" --clobber "${repo_args[@]}"
  gh release edit "$tag" --notes-file "$notes_file" "${repo_args[@]}"
else
  echo "Creating GitHub release ${tag}"
  create_args=(release create "$tag" "${files[@]}" --title "$tag" --notes-file "$notes_file")
  if [[ "$tag" == *-* ]]; then
    create_args+=(--prerelease)
  fi
  gh "${create_args[@]}" "${repo_args[@]}"
fi
