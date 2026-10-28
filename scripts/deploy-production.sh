#!/usr/bin/env bash
# Run on the VM from repo root (/root/restaurant). Used by GitHub Actions deploy.yml.
# Blue-green deployment: migrate, build, start standby, smoke-test, then promote
# (brief cutover downtime while the standby takes over the public port).
set -euo pipefail

WEB_PORT="${WEB_PORT:-8096}"
API_PORT="${API_PORT:-8097}"
COMPOSE_ARGS=(--project-directory . -f devops/docker-compose.yml)

# docker compose auto-loads .env for its own ${VAR} interpolation, but this script's own
# checks (migrations connection string, etc.) need the same values in its shell too.
set -a
if [ -f .env ]; then . ./.env; fi
set +a

: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD missing from .env}"
: "${JWT_SIGNING_KEY:?JWT_SIGNING_KEY missing from .env}"
: "${PUBLIC_SITE_URL:?PUBLIC_SITE_URL missing from .env}"

CURRENT_COLOR=$(cat /tmp/restaurant_color 2>/dev/null || echo "blue")
NEW_COLOR=$([ "$CURRENT_COLOR" = "blue" ] && echo "green" || echo "blue")

API_TEMP="restaurant_api_${NEW_COLOR}_temp"
WEB_TEMP="restaurant_web_${NEW_COLOR}_temp"
DEPLOY_CUTOVER=0
DEPLOY_PROMOTED=0

echo "=== Restaurant POS blue-green deployment: Current=$CURRENT_COLOR, New=$NEW_COLOR ==="

# `wget | grep -q` can trigger SIGPIPE under pipefail when grep exits early.
# Always capture output first, then grep the variable.
web_get() {
  docker exec "$WEB_TEMP" wget -q -O - "http://127.0.0.1${1}" 2>/dev/null
}

# HTTP status line for a path served by the standby web container (follows the /api proxy).
web_status() {
  docker exec "$WEB_TEMP" sh -c "wget -S -O /dev/null 'http://127.0.0.1${1}' 2>&1 | grep 'HTTP/' | tail -1" 2>/dev/null || true
}

smoke_step() {
  local label="$1"
  shift
  if ! "$@"; then
    echo "ERROR: smoke test failed — ${label}"
    echo "Recent web logs:"
    docker logs --tail 40 "$WEB_TEMP" || true
    echo "Recent API logs:"
    docker logs --tail 80 "$API_TEMP" || true
    return 1
  fi
  echo "  ok: ${label}"
}

smoke_web_alive() {
  local body
  body="$(web_get "/web-health")" || return 1
  grep -q '"web":"ok"' <<<"$body"
}

smoke_api_through_web() {
  local body
  body="$(web_get "/health")" || return 1
  grep -q '"healthy"' <<<"$body"
}

# An unauthenticated API call must reach the API and be rejected by it (401), not by nginx (502/404).
smoke_api_routing() {
  local status
  status="$(web_status "/api/me")"
  grep -q ' 401' <<<"$status"
}

smoke_app_shell() {
  local body
  body="$(web_get "/")" || return 1
  grep -qi 'Restaurant POS' <<<"$body"
}

# Client-side routes must fall back to the SPA, not 404.
smoke_spa_fallback() {
  local body
  body="$(web_get "/app/orders")" || return 1
  grep -qi '<div id="app"' <<<"$body"
}

promote_standby_to_production() {
  echo "=== Promote $NEW_COLOR to production ==="
  docker rm -f restaurant_api restaurant_web 2>/dev/null || true

  if docker container inspect "$API_TEMP" >/dev/null 2>&1; then
    docker rename "$API_TEMP" restaurant_api
  fi
  if docker container inspect "$WEB_TEMP" >/dev/null 2>&1; then
    docker rename "$WEB_TEMP" restaurant_web
  fi

  echo "$NEW_COLOR" > /tmp/restaurant_color
  DEPLOY_PROMOTED=1
}

# If cutover started but the deploy failed before promote, keep traffic flowing:
# promote the standby if it exists, otherwise bring production back with compose.
restore_service_on_failure() {
  local exit_code=$?
  if [ "$exit_code" -eq 0 ] || [ "$DEPLOY_PROMOTED" -eq 1 ] || [ "$DEPLOY_CUTOVER" -eq 0 ]; then
    exit "$exit_code"
  fi

  echo "WARN: Deploy failed (exit ${exit_code}) after production was stopped."

  if docker container inspect "$API_TEMP" >/dev/null 2>&1 \
    && docker container inspect "$WEB_TEMP" >/dev/null 2>&1; then
    echo "WARN: Standby containers exist — promoting to restore public traffic."
    promote_standby_to_production || true
    echo "WARN: Service restored from standby. Fix smoke-test failures before the next deploy."
    exit "$exit_code"
  fi

  echo "ERROR: No standby to promote. Attempting docker compose up..."
  docker compose "${COMPOSE_ARGS[@]}" up -d --no-deps rp-api rp-web || true
  exit "$exit_code"
}

trap restore_service_on_failure EXIT

echo "=== Ensure Docker network ==="
docker network inspect www_default >/dev/null 2>&1 || docker network create www_default

echo "=== Clean up dangling images ==="
# Images only: this VM hosts other stacks, so no `docker system prune` (it would also remove their
# stopped containers and unused networks). Named volumes are never touched.
docker image prune -f || true

echo "=== Check disk space ==="
FREE_SPACE=$(df /var/lib/docker | tail -1 | awk '{print $4}')
echo "Free disk space: ${FREE_SPACE}KB"
if [ "${FREE_SPACE}" -lt 1048576 ]; then
  echo "Less than 1GB free — pruning unused Restaurant POS images and build cache..."
  docker image prune -f -a --filter "label=com.docker.compose.project=restaurant" || true
  docker builder prune -f || true
  FREE_SPACE=$(df /var/lib/docker | tail -1 | awk '{print $4}')
  echo "Free space after cleanup: ${FREE_SPACE}KB"
  if [ "${FREE_SPACE}" -lt 1048576 ]; then
    echo "ERROR: Still insufficient disk space after cleanup"
    exit 1
  fi
fi

echo "=== Clean up any existing temporary containers ==="
docker rm -f restaurant_api_blue_temp restaurant_api_green_temp restaurant_web_blue_temp restaurant_web_green_temp 2>/dev/null || true

echo "=== Ensure postgres is running ==="
docker compose "${COMPOSE_ARGS[@]}" up -d rp-postgres

echo "Waiting for postgres to be ready..."
POSTGRES_OK=0
for i in $(seq 1 30); do
  if docker exec restaurant_postgres pg_isready -U restaurant -d restaurant >/dev/null 2>&1; then
    echo "Postgres is ready"
    POSTGRES_OK=1
    break
  fi
  sleep 2
done
if [ "$POSTGRES_OK" -ne 1 ]; then
  echo "ERROR: Postgres did not become ready. Recent logs:"
  docker logs --tail 80 restaurant_postgres || true
  exit 1
fi

echo "=== Pre-migration backup ==="
# A dump from just before the schema changes (kept in /root/backups/restaurant/pre-deploy, 5 kept).
# If it fails, nothing has changed yet and the deploy stops here.
bash scripts/backup-database.sh pre-deploy

echo "=== EF Core migrations ==="
# Before any new container starts, so a failed migration never reaches production traffic.
# Migrations are additive, so the currently running API keeps working against the new schema.
docker run --rm \
  --network restaurant_internal \
  -e RESTAURANT_CONNECTION_STRING="Host=rp-postgres;Port=5432;Database=restaurant;Username=restaurant;Password=${POSTGRES_PASSWORD}" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -v "$(pwd):/src" -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -lc "dotnet tool restore && dotnet restore src/api/Restaurant.Infrastructure && dotnet ef database update --project src/api/Restaurant.Infrastructure --startup-project src/api/Restaurant.Infrastructure"

echo "=== Build new $NEW_COLOR images ==="
docker compose "${COMPOSE_ARGS[@]}" build --pull rp-api rp-web

echo "=== Stop production app containers (free the web port for standby) ==="
DEPLOY_CUTOVER=1
docker stop restaurant_web restaurant_api 2>/dev/null || true
docker rm restaurant_web restaurant_api 2>/dev/null || true

echo "=== Start temporary $NEW_COLOR containers ==="
cat > /tmp/restaurant-compose.${NEW_COLOR}.yml <<EOF
services:
  rp-api:
    container_name: ${API_TEMP}
  rp-web:
    container_name: ${WEB_TEMP}
EOF

docker compose "${COMPOSE_ARGS[@]}" -f /tmp/restaurant-compose.${NEW_COLOR}.yml up -d --no-deps rp-api
echo "Waiting for $NEW_COLOR API..."
API_OK=0
for i in $(seq 1 40); do
  if docker exec "$API_TEMP" curl -sf http://127.0.0.1:8080/health >/dev/null 2>&1; then
    echo "$NEW_COLOR API is healthy"
    API_OK=1
    break
  fi
  sleep 3
done
if [ "$API_OK" -ne 1 ]; then
  echo "ERROR: $NEW_COLOR API did not become healthy. Recent logs:"
  docker logs --tail 80 "$API_TEMP" || true
  exit 1
fi

docker compose "${COMPOSE_ARGS[@]}" -f /tmp/restaurant-compose.${NEW_COLOR}.yml up -d --no-deps rp-web
echo "Waiting for $NEW_COLOR web..."
WEB_OK=0
for i in $(seq 1 30); do
  if smoke_web_alive; then
    echo "$NEW_COLOR web is up"
    WEB_OK=1
    break
  fi
  sleep 2
done
if [ "$WEB_OK" -ne 1 ]; then
  echo "ERROR: $NEW_COLOR web did not start. Recent logs:"
  docker logs --tail 80 "$WEB_TEMP" || true
  exit 1
fi

echo "=== Smoke tests on $NEW_COLOR ==="
smoke_step "web container alive (/web-health)" smoke_web_alive
smoke_step "web → API → database (/health)" smoke_api_through_web
smoke_step "API routing through nginx (/api → 401)" smoke_api_routing
smoke_step "app shell served (Restaurant POS index.html)" smoke_app_shell
smoke_step "client-side route falls back to the app (/app/orders)" smoke_spa_fallback
echo "$NEW_COLOR smoke tests passed"

promote_standby_to_production

echo "=== Verify production containers ==="
for i in $(seq 1 10); do
  if docker exec restaurant_api curl -sf http://127.0.0.1:8080/health >/dev/null 2>&1; then
    echo "Production restaurant_api is healthy"
    break
  fi
  sleep 1
done
if curl -sf "http://127.0.0.1:${WEB_PORT}/health" >/dev/null 2>&1; then
  echo "Production restaurant_web is serving on :${WEB_PORT}"
else
  echo "WARN: http://127.0.0.1:${WEB_PORT}/health not reachable from the host"
fi

echo "=== Public check (optional) ==="
if curl -sf "http://127.0.0.1:${API_PORT}/health" >/dev/null 2>&1; then
  echo "Production restaurant_api is serving on 127.0.0.1:${API_PORT} (API domain)"
else
  echo "WARN: http://127.0.0.1:${API_PORT}/health not reachable from the host"
fi
if [ -n "${PUBLIC_API_URL:-}" ]; then
  if curl -sf "${PUBLIC_API_URL}/health" >/dev/null 2>&1; then
    echo "Public ${PUBLIC_API_URL}/health OK"
  else
    echo "WARN: could not reach ${PUBLIC_API_URL}/health (DNS / Nginx Proxy Manager host not set up yet?) — not failing deploy"
  fi
fi
if curl -sf "${PUBLIC_SITE_URL}/health" >/dev/null 2>&1; then
  echo "Public ${PUBLIC_SITE_URL}/health OK"
else
  echo "WARN: could not reach ${PUBLIC_SITE_URL}/health (DNS / Nginx Proxy Manager host not set up yet?) — not failing deploy"
fi

docker image prune -f
trap - EXIT

# Nightly backup, weekly restore drill, 5-minute monitor (idempotent). After promotion, so a failure here
# fails the job loudly without touching the traffic that is already on the new version.
bash scripts/install-ops.sh
echo "=== Blue-green deployment complete ($CURRENT_COLOR → $NEW_COLOR) ==="
