#!/usr/bin/env bash
#
# Post the "## <version>" section of CHANGELOG.md to a Discord webhook.
#
# Usage:
#   scripts/publish-discord.sh [version]
#
# Runs for any v* tag (stable or prerelease). The embed title uses the tag so prereleases are
# distinct, while the changelog is looked up by the base <version>.
#
# Env:
#   DISCORD_WEBHOOK_URL   Discord channel webhook URL (skipped when unset)
#   GITHUB_REF_NAME       tag, e.g. v2.0.4 (defaults to v<version>)
#   GITHUB_REPOSITORY     owner/repo
#   GITHUB_SERVER_URL     e.g. https://github.com
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

version="${1:-$(sed -n 's/.*<CardShopCoopVersion>\(.*\)<\/CardShopCoopVersion>.*/\1/p' Directory.Build.props)}"
tag="${GITHUB_REF_NAME:-v${version}}"
display="${tag#v}"
release_url="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-meepen/CardShopCoop}/releases/tag/${tag}"

if [[ -z "${DISCORD_WEBHOOK_URL:-}" ]]; then
  echo "DISCORD_WEBHOOK_URL is not set; skipping Discord."
  exit 0
fi

notes="$(bash scripts/changelog-extract.sh "$version")"

payload="$(python3 - "$display" "$notes" "$release_url" <<'PY'
import json, sys
display, notes, url = sys.argv[1], sys.argv[2], sys.argv[3]
# Drop the leading "## <version>" heading; the embed title already carries the version.
parts = notes.split("\n", 1)
if parts and parts[0].startswith("## "):
    body = parts[1] if len(parts) > 1 else ""
else:
    body = notes
body = body.strip()
if len(body) > 4096:
    body = body[:4093] + "..."
print(json.dumps({
    "username": "CardShopCoopCommunity",
    "embeds": [{
        "title": f"CardShopCoopCommunity {display}",
        "url": url,
        "description": body,
        "color": 0x2EC484,
    }],
}))
PY
)"

echo "Posting CardShopCoopCommunity ${display} changelog to Discord"
curl -fsS -X POST "$DISCORD_WEBHOOK_URL" \
  -H "Content-Type: application/json" \
  --data "$payload" > /dev/null
echo "Posted."
