#!/usr/bin/env bash
#
# Print the CHANGELOG.md section for <version>: the body under the "## <version>" heading, up to the
# next "## " heading. Exits non-zero when there is no such section, so a release cannot ship without
# notes.
#
# Usage: scripts/changelog-extract.sh <version>
set -euo pipefail

version="${1:?usage: changelog-extract.sh <version>}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

section="$(awk -v ver="## ${version}" '
  {
    line = $0
    sub(/[[:space:]]+$/, "", line)
  }
  line == ver { found = 1; print; next }
  found && line ~ /^## / { exit }
  found { print }
' CHANGELOG.md)"

if [[ -z "$section" ]]; then
  echo "error: no '## ${version}' section found in CHANGELOG.md" >&2
  exit 1
fi

printf '%s\n' "$section"
