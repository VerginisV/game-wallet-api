#!/usr/bin/env bash
# Sends a signed wallet call, the way a game provider would.
# Usage: scripts/wallet-call.sh <endpoint> '<json body>'
#   scripts/wallet-call.sh balance '{"playerId":"player-1","currency":"EUR"}'
#   scripts/wallet-call.sh bet '{"transactionId":"tx-1","playerId":"player-1","roundId":"r-1","gameId":"slot-fruits","amount":250,"currency":"EUR"}'
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5090}"
PROVIDER_ID="${PROVIDER_ID:-demo-provider}"
SECRET="${PROVIDER_SECRET:-dev_secret_change_me}"

endpoint="$1"
body="$2"
timestamp="$(date +%s)"
signature="$(printf '%s.%s' "$timestamp" "$body" | openssl dgst -sha256 -hmac "$SECRET" -hex | sed 's/^.* //')"

curl -sS -X POST "$BASE_URL/wallet/$endpoint" \
  -H "Content-Type: application/json" \
  -H "X-Provider-Id: $PROVIDER_ID" \
  -H "X-Timestamp: $timestamp" \
  -H "X-Signature: $signature" \
  -d "$body"
echo
