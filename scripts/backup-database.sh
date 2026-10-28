#!/usr/bin/env bash
# Backs up the Restaurant POS database (pg_dump) and the uploads volume (logos, menu images).
# Installed to /opt/restaurant-ops by scripts/install-ops.sh and run nightly from /etc/cron.d/restaurant-ops.
# The deploy script also runs it from the repo as `pre-deploy`, before the EF Core migrations.
#
#   backup-database.sh              nightly: daily/ (14 kept), Sunday copy in weekly/ (8 kept), off-VM copy
#   backup-database.sh pre-deploy   before a deploy: pre-deploy/ (5 kept), no off-VM copy
#
# Each backup is a folder <BACKUP_DIR>/<kind>/<YYYY-MM-DD_HHMMSS>/ holding restaurant.dump (pg_dump custom
# format, for pg_restore), uploads.tgz and SHA256SUMS. It is written under a .tmp- name and renamed only
# after the dump has been read back with pg_restore --list, so a folder that exists is a complete backup.
#
# Fails loudly: non-zero exit, FAILED in the log, a LAST_FAILURE file in BACKUP_DIR (the monitor reports it)
# and, when ALERT_WEBHOOK_URL is set, a webhook message. Settings come from /etc/restaurant/ops.env.
set -euo pipefail

OPS_ENV_FILE="${OPS_ENV_FILE:-/etc/restaurant/ops.env}"
if [ -r "$OPS_ENV_FILE" ]; then
  set -a
  . "$OPS_ENV_FILE"
  set +a
fi

KIND="${1:-nightly}"
BACKUP_DIR="${BACKUP_DIR:-/root/backups/restaurant}"
BACKUP_KEEP_DAILY="${BACKUP_KEEP_DAILY:-14}"
BACKUP_KEEP_WEEKLY="${BACKUP_KEEP_WEEKLY:-8}"
BACKUP_KEEP_PRE_DEPLOY="${BACKUP_KEEP_PRE_DEPLOY:-5}"
# Day of the week (1 = Monday … 7 = Sunday) whose nightly backup is also kept as a weekly one.
BACKUP_WEEKLY_DAY="${BACKUP_WEEKLY_DAY:-7}"
# Optional off-VM copies (either or both). Skipped when unset.
#   BACKUP_RSYNC_TARGET=backup@nas.example.com:/srv/backups/restaurant/   (needs SSH key auth from root)
#   BACKUP_RCLONE_REMOTE=b2:my-bucket/restaurant                         (needs `rclone config` as root)
BACKUP_RSYNC_TARGET="${BACKUP_RSYNC_TARGET:-}"
BACKUP_RCLONE_REMOTE="${BACKUP_RCLONE_REMOTE:-}"
ALERT_WEBHOOK_URL="${ALERT_WEBHOOK_URL:-}"
PG_CONTAINER="${RP_PG_CONTAINER:-restaurant_postgres}"
PG_USER="${RP_PG_USER:-restaurant}"
PG_DB="${RP_PG_DB:-restaurant}"
UPLOADS_VOLUME="${RP_UPLOADS_VOLUME:-restaurant_uploads}"
LOG_DIR="${LOG_DIR:-/var/log/restaurant}"
LOG_FILE="${LOG_DIR}/backup.log"

case "$KIND" in
  nightly) TARGET_DIR="${BACKUP_DIR}/daily" ;;
  pre-deploy) TARGET_DIR="${BACKUP_DIR}/pre-deploy" ;;
  *) echo "Usage: $0 [nightly|pre-deploy]" >&2; exit 2 ;;
esac

mkdir -p "$LOG_DIR" "$TARGET_DIR"
chmod 700 "$BACKUP_DIR"
# Everything goes to the log and to stdout (the deploy log, or /dev/null under cron).
exec > >(tee -a "$LOG_FILE") 2>&1

log() { echo "$(date '+%F %T') [backup:${KIND}] $*"; }

# Posts one line to a Slack / Google Chat / Discord style webhook ("text" and "content" keys). Best effort.
notify() {
  [ -n "$ALERT_WEBHOOK_URL" ] || return 0
  local message="[restaurant $(hostname)] $1"
  local json
  json="$(printf '%s' "$message" | sed 's/\\/\\\\/g; s/"/\\"/g')"
  curl -sS --max-time 10 -H 'Content-Type: application/json' \
    -d "{\"text\":\"${json}\",\"content\":\"${json}\"}" "$ALERT_WEBHOOK_URL" >/dev/null 2>&1 \
    || log "WARN: alert webhook failed"
}

STAMP="$(date +%F_%H%M%S)"
WORK_DIR="${TARGET_DIR}/.tmp-${STAMP}"
FINAL_DIR="${TARGET_DIR}/${STAMP}"

on_exit() {
  local exit_code=$?
  rm -rf "$WORK_DIR"
  if [ "$exit_code" -ne 0 ]; then
    log "FAILED (exit ${exit_code}) — see ${LOG_FILE}"
    # Only nightly runs: a failed pre-deploy backup already stops the deploy.
    if [ "$KIND" = "nightly" ]; then
      printf '%s nightly backup failed (exit %s)\n' "$(date '+%F %T')" "$exit_code" > "${BACKUP_DIR}/LAST_FAILURE"
    fi
    notify "Database backup (${KIND}) FAILED with exit ${exit_code}. Log: ${LOG_FILE}"
  fi
  exit "$exit_code"
}
trap on_exit EXIT

# One backup at a time (a deploy's pre-deploy run can meet the nightly one).
if command -v flock >/dev/null 2>&1; then
  exec 9>"${BACKUP_DIR}/.lock"
  if ! flock -w 600 9; then
    log "ERROR: another backup is still running after 10 minutes"
    exit 1
  fi
fi

log "Starting: ${PG_DB} from ${PG_CONTAINER} → ${FINAL_DIR}"
if [ "$(docker inspect --format='{{.State.Running}}' "$PG_CONTAINER" 2>/dev/null)" != "true" ]; then
  log "ERROR: container ${PG_CONTAINER} is not running"
  exit 1
fi

# The first deploy to a new VM meets an empty database: nothing to keep before its migrations.
HAS_SCHEMA="$(docker exec "$PG_CONTAINER" psql -X -U "$PG_USER" -d "$PG_DB" -tA -c "select to_regclass('public.\"__EFMigrationsHistory\"') is not null")"
if [ "$HAS_SCHEMA" != "t" ]; then
  if [ "$KIND" = "pre-deploy" ]; then
    log "Database has no tables yet (first deploy) — nothing to back up"
    exit 0
  fi
  log "ERROR: ${PG_DB} has no __EFMigrationsHistory table — wrong database, or it was wiped?"
  exit 1
fi

mkdir -p "$WORK_DIR"

# --- database -------------------------------------------------------------------------------------
# Custom format: compressed, and pg_restore can restore all of it or single tables.
docker exec "$PG_CONTAINER" pg_dump -U "$PG_USER" -d "$PG_DB" -Fc --no-password > "${WORK_DIR}/restaurant.dump"

DUMP_BYTES="$(wc -c < "${WORK_DIR}/restaurant.dump")"
if [ "$DUMP_BYTES" -lt 1024 ]; then
  log "ERROR: dump is only ${DUMP_BYTES} bytes"
  exit 1
fi
# Read the dump back: a truncated or corrupt file fails here, not on the day it's needed.
TOC_ENTRIES="$(docker exec -i "$PG_CONTAINER" pg_restore --list < "${WORK_DIR}/restaurant.dump" | grep -c ' TABLE DATA ' || true)"
if [ "$TOC_ENTRIES" -lt 1 ]; then
  log "ERROR: pg_restore --list found no table data in the dump"
  exit 1
fi
log "  database: ${DUMP_BYTES} bytes, ${TOC_ENTRIES} tables with data"

# --- uploads --------------------------------------------------------------------------------------
# Read-only mount in a throwaway container from the postgres image (already on the VM; it has tar + gzip).
if docker volume inspect "$UPLOADS_VOLUME" >/dev/null 2>&1; then
  PG_IMAGE="$(docker inspect --format='{{.Config.Image}}' "$PG_CONTAINER")"
  docker run --rm --network none -v "${UPLOADS_VOLUME}:/data:ro" "$PG_IMAGE" tar czf - -C /data . > "${WORK_DIR}/uploads.tgz"
  gzip -t "${WORK_DIR}/uploads.tgz"
  log "  uploads: $(wc -c < "${WORK_DIR}/uploads.tgz") bytes"
else
  log "  uploads: volume ${UPLOADS_VOLUME} not found — skipped"
fi

(cd "$WORK_DIR" && sha256sum -- * > SHA256SUMS)
chmod 600 "$WORK_DIR"/*
mv "$WORK_DIR" "$FINAL_DIR"

# --- weekly copy and retention ----------------------------------------------------------------------
# Keeps the newest N backup folders of a kind (folder names sort by date).
prune() {
  local dir="$1" keep="$2" old
  [ -d "$dir" ] || return 0
  find "$dir" -mindepth 1 -maxdepth 1 -type d -name '20*' -printf '%f\n' | sort -r | tail -n +"$((keep + 1))" | while read -r old; do
    log "  removing old backup ${dir}/${old}"
    rm -rf -- "${dir:?}/${old}"
  done
}

if [ "$KIND" = "nightly" ]; then
  if [ "$(date +%u)" = "$BACKUP_WEEKLY_DAY" ]; then
    mkdir -p "${BACKUP_DIR}/weekly"
    # Hard links: the weekly copy costs no extra disk while the daily one still exists.
    cp -al "$FINAL_DIR" "${BACKUP_DIR}/weekly/${STAMP}" 2>/dev/null || cp -a "$FINAL_DIR" "${BACKUP_DIR}/weekly/${STAMP}"
    log "  kept as weekly backup"
  fi
  prune "${BACKUP_DIR}/daily" "$BACKUP_KEEP_DAILY"
  prune "${BACKUP_DIR}/weekly" "$BACKUP_KEEP_WEEKLY"
else
  prune "${BACKUP_DIR}/pre-deploy" "$BACKUP_KEEP_PRE_DEPLOY"
fi

# --- off-VM copy ------------------------------------------------------------------------------------
# Copies only (no --delete): the remote keeps backups the VM has pruned; set its own retention there.
if [ "$KIND" = "nightly" ]; then
  if [ -n "$BACKUP_RSYNC_TARGET" ]; then
    log "  rsync → ${BACKUP_RSYNC_TARGET}"
    # weekly/ appears only after the first weekly day; naming a missing folder would fail the whole copy.
    SOURCES=("${BACKUP_DIR}/daily")
    [ -d "${BACKUP_DIR}/weekly" ] && SOURCES+=("${BACKUP_DIR}/weekly")
    rsync -a --exclude '.tmp-*' --exclude '.lock' "${SOURCES[@]}" "$BACKUP_RSYNC_TARGET"
  fi
  if [ -n "$BACKUP_RCLONE_REMOTE" ]; then
    log "  rclone → ${BACKUP_RCLONE_REMOTE}"
    rclone copy --exclude '.tmp-*/**' --exclude '.lock' "$BACKUP_DIR" "$BACKUP_RCLONE_REMOTE"
  fi
  if [ -z "$BACKUP_RSYNC_TARGET" ] && [ -z "$BACKUP_RCLONE_REMOTE" ]; then
    log "  off-VM copy: not configured (BACKUP_RSYNC_TARGET / BACKUP_RCLONE_REMOTE)"
  fi
fi

if [ "$KIND" = "nightly" ]; then
  rm -f "${BACKUP_DIR}/LAST_FAILURE"
  printf '%s %s\n' "$(date '+%F %T')" "$FINAL_DIR" > "${BACKUP_DIR}/LAST_SUCCESS"
fi
log "Done: ${FINAL_DIR}"
