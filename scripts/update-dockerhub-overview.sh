#!/usr/bin/env bash
# update-dockerhub-overview.sh — push the short description + overview
# (docs/dockerhub/overview.md) to the Docker Hub repository page.
#
# Requires a Docker Hub Personal Access Token in DOCKERHUB_TOKEN with
# Read/Write/Delete scope — Docker treats repo-metadata edits as an
# admin-level operation, so a plain Read&Write PAT is rejected.
#   https://app.docker.com/settings/personal-access-tokens
#
# Usage:
#   DOCKERHUB_TOKEN=dckr_pat_xxx ./scripts/update-dockerhub-overview.sh
#   DOCKERHUB_USERNAME=afonsoft DOCKERHUB_REPO=knowledgerag \
#     OVERVIEW_FILE=docs/dockerhub/overview.md \
#     SHORT_DESCRIPTION="..." \
#     DOCKERHUB_TOKEN=... ./scripts/update-dockerhub-overview.sh
#
# To automate this on every release, add a step to the publish-docker job in
# .github/workflows/release.yml (protected — change requires admin bypass or a
# GitHub ruleset exemption), e.g. peter-evans/dockerhub-description with
#   readme-filepath: ./docs/dockerhub/overview.md
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DOCKERHUB_USERNAME="${DOCKERHUB_USERNAME:-afonsoft}"
DOCKERHUB_REPO="${DOCKERHUB_REPO:-knowledgerag}"
OVERVIEW_FILE="${OVERVIEW_FILE:-$REPO_ROOT/docs/dockerhub/overview.md}"
SHORT_DESCRIPTION="${SHORT_DESCRIPTION:-All-in-one .NET RAG platform — native MCP server, hybrid retrieval, Blazor admin UI.}"

: "${DOCKERHUB_TOKEN:?Set DOCKERHUB_TOKEN to a Docker Hub PAT (Read/Write/Delete scope)}"
[[ -f "$OVERVIEW_FILE" ]] || { echo "overview file not found: $OVERVIEW_FILE" >&2; exit 1; }
((${#SHORT_DESCRIPTION} <= 100)) || { echo "SHORT_DESCRIPTION exceeds Docker Hub's 100-char limit" >&2; exit 1; }

echo "==> Logging in to Docker Hub as $DOCKERHUB_USERNAME"
JWT="$(python3 - "$DOCKERHUB_USERNAME" <<'PY'
import json, os, sys, urllib.request
req = urllib.request.Request(
    "https://hub.docker.com/v2/users/login",
    data=json.dumps({"username": sys.argv[1],
                     "password": os.environ["DOCKERHUB_TOKEN"]}).encode(),
    headers={"Content-Type": "application/json"},
)
print(json.load(urllib.request.urlopen(req))["token"])
PY
)"

payload="$(mktemp)"
trap 'rm -f "$payload"' EXIT
python3 - "$OVERVIEW_FILE" "$SHORT_DESCRIPTION" > "$payload" <<'PY'
import json, sys
print(json.dumps({
    "description": sys.argv[2],
    "full_description": open(sys.argv[1], encoding="utf-8").read(),
}))
PY

echo "==> PATCH /v2/repositories/$DOCKERHUB_USERNAME/$DOCKERHUB_REPO"
response="$(curl -fsS -X PATCH \
  "https://hub.docker.com/v2/repositories/${DOCKERHUB_USERNAME}/${DOCKERHUB_REPO}/" \
  -H "Authorization: JWT $JWT" \
  -H 'Content-Type: application/json' \
  --data @"$payload")"
python3 - "$response" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
print(f"updated {d['user']}/{d['name']} — description: {d['description']!r}, "
      f"overview: {len(d.get('full_description') or '')} chars")
PY
