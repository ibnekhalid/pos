#!/usr/bin/env bash
# Post-deploy health check for the Restaurant POS stack. Run ON the VM:
#
#   ./devops/scripts/health-check.sh
#
# Exits non-zero if anything looks unhealthy. Read-only — never restarts or modifies anything.
set -uo pipefail

WEB_PORT="${WEB_PORT:-8096}"
FAILED=0

pass() { echo "  OK   $1"; }
fail() { echo "  FAIL $1"; FAILED=1; }

check_container() {
  local label="$1" name="$2"
  if ! docker container inspect "$name" >/dev/null 2>&1; then
    fail "$label: container $name not found"
    return
  fi
  local running health
  running="$(docker inspect --format='{{.State.Running}}' "$name" 2>/dev/null)"
  if [ "$running" != "true" ]; then
    fail "$label ($name): not running"
    return
  fi
  health="$(docker inspect --format='{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' "$name" 2>/dev/null)"
  if [ "$health" = "none" ] || [ "$health" = "healthy" ]; then
    pass "$label ($name)$( [ "$health" != "none" ] && echo " - healthy")"
  else
    fail "$label ($name): health status = $health"
  fi
}

check_http() {
  local label="$1" url="$2" expect_pattern="$3"
  local body
  if body="$(curl -sfS --max-time 5 "$url" 2>/dev/null)" && grep -qE "$expect_pattern" <<<"$body"; then
    pass "$label ($url)"
  else
    fail "$label ($url) - unreachable or unexpected response"
  fi
}

echo "=== Restaurant POS stack ==="
check_container "postgres" restaurant_postgres
check_container "API" restaurant_api
check_container "web" restaurant_web
check_http "web liveness" "http://127.0.0.1:${WEB_PORT}/web-health" '"web":"ok"'
check_http "web → API → database" "http://127.0.0.1:${WEB_PORT}/health" '"healthy"'

if [ -n "${PUBLIC_SITE_URL:-}" ]; then
  echo "=== Public ==="
  check_http "public /health" "${PUBLIC_SITE_URL}/health" '"healthy"'
fi

echo
if [ "$FAILED" -eq 0 ]; then
  echo "All checks passed."
else
  echo "One or more checks FAILED - see above."
fi
exit "$FAILED"
