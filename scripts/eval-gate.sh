#!/usr/bin/env bash
# SPEC-20260924-eval-regression-gate RF-004: CI gate script.
# Runs a versioned dataset against a running KnowledgeHub and exits non-zero
# when the gate fails. Usage:
#
#   ./scripts/eval-gate.sh <dataset-name> <baseline-name> [gate-json]
#
# Env:  KH_URL     (default http://localhost:5000)
#       KH_APIKEY  (optional aft_* key for authenticated instances)
#
# Example:
#   KH_URL=http://localhost:5000 ./scripts/eval-gate.sh golden golden-v1 \
#     '[{"metric":"recall_at_k","direction":"gte","threshold":0.8}]'
set -euo pipefail

DATASET="${1:?dataset name required}"
BASELINE="${2:?baseline name required}"
GATE="${3:-}"
KH_URL="${KH_URL:-http://localhost:5000}"

BODY=$(python3 - "$DATASET" "$BASELINE" "$GATE" <<'PY'
import json, sys
payload = {"dataset": sys.argv[1], "baseline": sys.argv[2]}
if sys.argv[3]:
    payload["gate"] = json.loads(sys.argv[3])
print(json.dumps(payload))
PY
)

AUTH=()
if [[ -n "${KH_APIKEY:-}" ]]; then
  AUTH=(-H "Authorization: Bearer ${KH_APIKEY}")
fi

RESPONSE=$(curl -sS -w '\n%{http_code}' -X POST "${KH_URL}/api/eval/run" \
  -H 'Content-Type: application/json' "${AUTH[@]}" -d "$BODY")
HTTP_CODE=$(echo "$RESPONSE" | tail -n1)
PAYLOAD=$(echo "$RESPONSE" | sed '$d')

if [[ "$HTTP_CODE" != "200" ]]; then
  echo "eval run failed: HTTP $HTTP_CODE"
  echo "$PAYLOAD"
  exit 2
fi

STATUS=$(echo "$PAYLOAD" | python3 -c 'import json,sys; r=json.load(sys.stdin); g=r.get("gate"); print(g["status"] if g else "none")')
RECALL=$(echo "$PAYLOAD" | python3 -c 'import json,sys; print(json.load(sys.stdin)["metrics"]["recallAtK"])')

echo "run metrics: recall_at_k=$RECALL  gate=$STATUS"
if [[ "$STATUS" = "fail" ]]; then
  echo "$PAYLOAD" | python3 -c 'import json,sys; [print("  violation:", v) for v in json.load(sys.stdin)["gate"]["violations"]]'
  exit 1
fi
exit 0
