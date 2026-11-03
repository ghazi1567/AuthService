#!/usr/bin/env bash
# Checks a running deployment from the outside, the way a visitor and a product would reach it:
#
#   ./check.sh auth.example.com app.example.com
#
# Run it from any machine (your own, or the server) after a deploy or an update. It exits non-zero on the first failure.
# For a trial on one machine with names like auth.localhost, whose certificates are not publicly trusted:
#   CHECK_INSECURE=1 CHECK_IP=127.0.0.1 ./check.sh auth.localhost app.localhost
set -uo pipefail

auth_host="${1:?Usage: check.sh <api host> <web console host>}"
app_host="${2:?Usage: check.sh <api host> <web console host>}"

curl_options=(--silent --show-error --max-time 20)
[ -n "${CHECK_INSECURE:-}" ] && curl_options+=(--insecure)
if [ -n "${CHECK_IP:-}" ]; then
  # Pretend the names point at this address, for a machine whose DNS does not know them yet.
  for host in "$auth_host" "$app_host"; do
    curl_options+=(--resolve "$host:443:$CHECK_IP" --resolve "$host:80:$CHECK_IP")
  done
fi

failed=0
check() {
  local what="$1" expected="$2" url="$3" contains="${4:-}"
  local body status
  body="$(mktemp)"
  status="$(curl "${curl_options[@]}" --output "$body" --write-out '%{http_code}' "$url" 2>&1)" || true
  if [ "$status" = "$expected" ] && { [ -z "$contains" ] || grep -q "$contains" "$body"; }; then
    echo "ok    $what ($status)"
  else
    echo "FAIL  $what: wanted $expected${contains:+ containing '$contains'}, got $status ($url)"
    failed=1
  fi
  rm -f "$body"
}

check "the API is ready (database reachable, migrated, a signing key)" 200 "https://$auth_host/health/ready" '"status":"ready"'
check "the public signing keys are published" 200 "https://$auth_host/.well-known/jwks.json" '"keys"'
check "metrics are not served to the internet" 404 "https://$auth_host/metrics"
check "the web console is up" 200 "https://$app_host/healthz" ok
check "the sign-in page is served" 200 "https://$app_host/login" 'Sign in'
check "plain http is sent to https" 308 "http://$app_host/login"

headers="$(curl "${curl_options[@]}" --head "https://$app_host/login" 2>&1 || true)"
if echo "$headers" | grep -qi '^strict-transport-security:'; then
  echo "ok    HTTPS is enforced (Strict-Transport-Security)"
else
  echo "FAIL  no Strict-Transport-Security header on https://$app_host/login"
  failed=1
fi

exit "$failed"
