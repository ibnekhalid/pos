#!/usr/bin/env bash
# Restore drill: proves the latest backup can actually be restored. Installed to /opt/restaurant-ops by
# scripts/install-ops.sh and run weekly from /etc/cron.d/restaurant-ops; safe to run by hand at any time.
#
#   restore-drill.sh                          the newest nightly backup
#   restore-drill.sh /root/backups/restaurant/weekly/2026-10-25_043000   a chosen backup folder
#
# Starts a throwaway PostgreSQL container (no network, no published port, its own anonymous volume),
# restores the dump into it with pg_restore --exit-on-error, runs sanity checks (migrations history,
# key tables, row counts, checksums, the uploads archive), prints the counts, and always removes the
# container. Production is only read from to compare the latest migration.
#
# Writes LAST_DRILL (success) or DRILL_FAILED (the monitor reports it) in BACKUP_DIR, logs to
# /var/log/restaurant/restore-drill.log, and posts to ALERT_WEBHOOK_URL on failure.
set -euo pipefail

OPS_ENV_FILE="${OPS_ENV_FILE:-/etc/restaurant/ops.env}"
if [ -r "$OPS_ENV_FILE" ]; then
  set -a
  . "$OPS_ENV_FILE"
  set +a
fi

BACKUP_DIR="${BACKUP_DIR:-/root/backups/restaurant}"
ALERT_WEBHOOK_URL="${ALERT_WEBHOOK_URL:-}"
PG_CONTAINER="${RP_PG_CONTAINER:-restaurant_postgres}"
PG_USER="${RP_PG_USER:-restaurant}"
PG_DB="${RP_PG_DB:-restaurant}"
DRILL_CONTAINER="restaurant_restore_drill"
LOG_DIR="${LOG_DIR:-/var/log/restaurant}"
LOG_FILE="${LOG_DIR}/restore-drill.log"
# Tables that must exist and the ones that must not be empty (system receipt templates are seeded).
REQUIRED_TABLES=(__EFMigrationsHistory Tenants Branches AspNetUsers Memberships Orders Payments AuditLogs ReceiptTemplates)
NON_EMPTY_TABLES=(__EFMigrationsHistory ReceiptTemplates)

mkdir -p "$LOG_DIR"
exec > >(tee -a "$LOG_FILE") 2>&1

log() { echo "$(date '+%F %T') [restore-drill] $*"; }

notify() {
  [ -n "$ALERT_WEBHOOK_URL" ] || return 0
  local json
  json="$(printf '[restaurant %s] %s' "$(hostname)" "$1" | sed 's/\\/\\\\/g; s/"/\\"/g')"
  curl -sS --max-time 10 -H 'Content-Type: application/json' \
    -d "{\"text\":\"${json}\",\"content\":\"${json}\"}" "$ALERT_WEBHOOK_URL" >/dev/null 2>&1 \
    || log "WARN: alert webhook failed"
}

# psql in the drill container: unaligned, tuples only, stop on the first error.
drill_sql() {
  docker exec -i "$DRILL_CONTAINER" psql -X -q -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" -tA -c "$1"
}

on_exit() {
  local exit_code=$?
  docker rm -f -v "$DRILL_CONTAINER" >/dev/null 2>&1 || true
  if [ "$exit_code" -ne 0 ]; then
    log "FAILED (exit ${exit_code}) — the backup could not be restored or failed a check"
    printf '%s restore drill failed (exit %s) on %s\n' "$(date '+%F %T')" "$exit_code" "${SOURCE:-?}" > "${BACKUP_DIR}/DRILL_FAILED"
    notify "Restore drill FAILED (exit ${exit_code}) for ${SOURCE:-?}. Log: ${LOG_FILE}"
  fi
  exit "$exit_code"
}
trap on_exit EXIT

# --- which backup ------------------------------------------------------------------------------------
SOURCE="${1:-}"
if [ -z "$SOURCE" ]; then
  SOURCE="$(find "${BACKUP_DIR}/daily" -mindepth 1 -maxdepth 1 -type d -name '20*' 2>/dev/null | sort | tail -1)"
fi
if [ -z "$SOURCE" ] || [ ! -f "${SOURCE}/restaurant.dump" ]; then
  log "ERROR: no backup found (looked for ${SOURCE:-${BACKUP_DIR}/daily/<newest>}/restaurant.dump)"
  exit 1
fi
log "Drill on ${SOURCE}"

if [ -f "${SOURCE}/SHA256SUMS" ]; then
  (cd "$SOURCE" && sha256sum --quiet -c SHA256SUMS)
  log "  checksums ok"
else
  log "  WARN: no SHA256SUMS in the backup folder"
fi
if [ -f "${SOURCE}/uploads.tgz" ]; then
  UPLOADS_LISTING="$(tar -tzf - < "${SOURCE}/uploads.tgz")"
  log "  uploads archive ok ($(grep -vc '/$' <<<"$UPLOADS_LISTING" || true) files)"
fi

# --- throwaway database ----------------------------------------------------------------------------
# Same image as production when it's running, so the restore uses the same PostgreSQL version.
PG_IMAGE="$(docker inspect --format='{{.Config.Image}}' "$PG_CONTAINER" 2>/dev/null || echo 'postgres:16-alpine')"
docker rm -f -v "$DRILL_CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$DRILL_CONTAINER" --network none \
  -e POSTGRES_USER="$PG_USER" -e POSTGRES_DB="$PG_DB" \
  -e POSTGRES_PASSWORD="drill-$(date +%s%N)" \
  "$PG_IMAGE" >/dev/null

READY=0
for i in $(seq 1 60); do
  # The image's entrypoint restarts the server once after initdb; wait for that to settle.
  if docker exec "$DRILL_CONTAINER" pg_isready -U "$PG_USER" -d "$PG_DB" -h 127.0.0.1 >/dev/null 2>&1 \
    && drill_sql 'select 1' >/dev/null 2>&1; then
    READY=1
    break
  fi
  sleep 1
done
if [ "$READY" -ne 1 ]; then
  log "ERROR: drill database did not start"
  docker logs --tail 40 "$DRILL_CONTAINER" || true
  exit 1
fi

START=$(date +%s)
docker exec -i "$DRILL_CONTAINER" pg_restore -U "$PG_USER" -d "$PG_DB" --exit-on-error --no-owner --no-privileges < "${SOURCE}/restaurant.dump"
log "  restored in $(( $(date +%s) - START ))s"

# --- sanity checks ---------------------------------------------------------------------------------
for table in "${REQUIRED_TABLES[@]}"; do
  if [ "$(drill_sql "select to_regclass('public.\"${table}\"') is not null")" != "t" ]; then
    log "ERROR: table ${table} is missing from the restored database"
    exit 1
  fi
done
for table in "${NON_EMPTY_TABLES[@]}"; do
  if [ "$(drill_sql "select count(*) from public.\"${table}\"")" -lt 1 ]; then
    log "ERROR: table ${table} is empty in the restored database"
    exit 1
  fi
done

MIGRATIONS="$(drill_sql 'select count(*) from "__EFMigrationsHistory"')"
LATEST_MIGRATION="$(drill_sql 'select max("MigrationId") from "__EFMigrationsHistory"')"
log "  migrations: ${MIGRATIONS}, latest ${LATEST_MIGRATION}"

# A backup taken before the last deploy can trail production by a migration; that's a note, not a failure.
if [ "$(docker inspect --format='{{.State.Running}}' "$PG_CONTAINER" 2>/dev/null)" = "true" ]; then
  PROD_MIGRATION="$(docker exec "$PG_CONTAINER" psql -X -U "$PG_USER" -d "$PG_DB" -tA -c 'select max("MigrationId") from "__EFMigrationsHistory"' 2>/dev/null || true)"
  if [ -n "$PROD_MIGRATION" ] && [ "$PROD_MIGRATION" != "$LATEST_MIGRATION" ]; then
    log "  NOTE: production is on ${PROD_MIGRATION} (a deploy since this backup?)"
  fi
fi

# Exact row count of every table (query_to_xml runs one count per table in a single statement).
log "  row counts:"
drill_sql "
  select format('    %-32s %s', table_name,
         (xpath('/row/c/text()', query_to_xml(format('select count(*) as c from public.%I', table_name), false, true, '')))[1]::text)
  from information_schema.tables
  where table_schema = 'public' and table_type = 'BASE TABLE'
  order by table_name"
TABLES="$(drill_sql "select count(*) from information_schema.tables where table_schema = 'public' and table_type = 'BASE TABLE'")"
TENANTS="$(drill_sql 'select count(*) from "Tenants"')"
ORDERS="$(drill_sql 'select count(*) from "Orders"')"

rm -f "${BACKUP_DIR}/DRILL_FAILED"
printf '%s ok %s (%s tables, %s restaurants, %s orders, migration %s)\n' \
  "$(date '+%F %T')" "$SOURCE" "$TABLES" "$TENANTS" "$ORDERS" "$LATEST_MIGRATION" > "${BACKUP_DIR}/LAST_DRILL"
log "Passed: ${TABLES} tables, ${TENANTS} restaurants, ${ORDERS} orders. Drill database removed."
