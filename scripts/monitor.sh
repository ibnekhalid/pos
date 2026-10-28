#!/usr/bin/env bash
# Lightweight monitoring for the Restaurant POS VM. Installed to /opt/restaurant-ops by
# scripts/install-ops.sh and run every 5 minutes from /etc/cron.d/restaurant-ops. No services, no agents.
#
# Checks: the API through rp-web and on its own port (/health includes the database), optionally the
# public URL, the age of the newest nightly backup and any backup / restore-drill failure marker, and
# free disk space. The full result of the last run is in /var/lib/restaurant-ops/monitor.status.
#
# Logs only failures and recoveries (/var/log/restaurant/monitor.log). With ALERT_WEBHOOK_URL set it
# posts once when a problem has been seen on MONITOR_ALERT_AFTER runs in a row (so the brief cutover of
# a blue-green deploy doesn't alert), again if the set of problems changes, and once on recovery.
#
#   monitor.sh          the cron run
#   monitor.sh --print  also print the result (exit 1 when something is wrong)
set -uo pipefail

OPS_ENV_FILE="${OPS_ENV_FILE:-/etc/restaurant/ops.env}"
if [ -r "$OPS_ENV_FILE" ]; then
  set -a
  . "$OPS_ENV_FILE"
  set +a
fi

WEB_PORT="${WEB_PORT:-8096}"
API_PORT="${API_PORT:-8097}"
BACKUP_DIR="${BACKUP_DIR:-/root/backups/restaurant}"
# e.g. https://restaurant.dsnoopdoc.com — checks DNS, Nginx Proxy Manager and the certificate too.
MONITOR_PUBLIC_URL="${MONITOR_PUBLIC_URL:-}"
MONITOR_BACKUP_MAX_AGE_HOURS="${MONITOR_BACKUP_MAX_AGE_HOURS:-26}"
MONITOR_DISK_MAX_PERCENT="${MONITOR_DISK_MAX_PERCENT:-90}"
MONITOR_ALERT_AFTER="${MONITOR_ALERT_AFTER:-2}"
ALERT_WEBHOOK_URL="${ALERT_WEBHOOK_URL:-}"
STATE_DIR="${STATE_DIR:-/var/lib/restaurant-ops}"
LOG_DIR="${LOG_DIR:-/var/log/restaurant}"
LOG_FILE="${LOG_DIR}/monitor.log"

mkdir -p "$STATE_DIR" "$LOG_DIR"

PROBLEMS=()
REPORT=()
ok() { REPORT+=("OK   $1"); }
problem() { REPORT+=("FAIL $1"); PROBLEMS+=("$1"); }

log() { echo "$(date '+%F %T') [monitor] $*" >> "$LOG_FILE"; }

notify() {
  [ -n "$ALERT_WEBHOOK_URL" ] || return 0
  local json
  json="$(printf '[restaurant %s] %s' "$(hostname)" "$1" | sed 's/\\/\\\\/g; s/"/\\"/g' | sed ':a;N;$!ba;s/\n/\\n/g')"
  curl -sS --max-time 10 -H 'Content-Type: application/json' \
    -d "{\"text\":\"${json}\",\"content\":\"${json}\"}" "$ALERT_WEBHOOK_URL" >/dev/null 2>&1 \
    || log "WARN: alert webhook failed"
}

check_health() {
  local label="$1" url="$2" body
  if body="$(curl -sfS --max-time 10 "$url" 2>&1)" && grep -q '"healthy"' <<<"$body"; then
    ok "$label"
  else
    problem "$label: ${url} → ${body:0:120}"
  fi
}

# --- the app ---------------------------------------------------------------------------------------
check_health "web → API → database" "http://127.0.0.1:${WEB_PORT}/health"
check_health "API port (desktop apps)" "http://127.0.0.1:${API_PORT}/health"
if [ -n "$MONITOR_PUBLIC_URL" ]; then
  check_health "public site" "${MONITOR_PUBLIC_URL%/}/health"
fi

# --- backups ---------------------------------------------------------------------------------------
NOW=$(date +%s)
NEWEST="$(find "${BACKUP_DIR}/daily" -mindepth 1 -maxdepth 1 -type d -name '20*' 2>/dev/null | sort | tail -1)"
INSTALLED_AT="$(cat "${STATE_DIR}/installed-at" 2>/dev/null || echo 0)"
if [ -n "$NEWEST" ]; then
  AGE_HOURS=$(( (NOW - $(stat -c %Y "$NEWEST")) / 3600 ))
  if [ "$AGE_HOURS" -le "$MONITOR_BACKUP_MAX_AGE_HOURS" ]; then
    ok "newest backup ${NEWEST##*/} (${AGE_HOURS}h old)"
  else
    problem "newest backup is ${AGE_HOURS}h old (${NEWEST})"
  fi
elif [ $(( NOW - INSTALLED_AT )) -lt $(( MONITOR_BACKUP_MAX_AGE_HOURS * 3600 )) ]; then
  ok "no backup yet (backups installed recently)"
else
  problem "no nightly backup in ${BACKUP_DIR}/daily"
fi
if [ -f "${BACKUP_DIR}/LAST_FAILURE" ]; then
  problem "backup: $(head -c 200 "${BACKUP_DIR}/LAST_FAILURE")"
fi
if [ -f "${BACKUP_DIR}/DRILL_FAILED" ]; then
  problem "restore drill: $(head -c 200 "${BACKUP_DIR}/DRILL_FAILED")"
fi

# --- disk ------------------------------------------------------------------------------------------
declare -A SEEN=()
for path in / /var/lib/docker "$BACKUP_DIR"; do
  [ -e "$path" ] || continue
  # Use% and mount point are the last two columns (counted from the end: device names can hold spaces).
  read -r used mount < <(df -P "$path" | awk 'NR == 2 { sub("%", "", $(NF - 1)); print $(NF - 1), $NF }')
  [ -n "${SEEN[$mount]:-}" ] && continue
  SEEN[$mount]=1
  if [ "$used" -lt "$MONITOR_DISK_MAX_PERCENT" ]; then
    ok "disk ${mount} ${used}% used"
  else
    problem "disk ${mount} is ${used}% full (limit ${MONITOR_DISK_MAX_PERCENT}%)"
  fi
done

# --- result, log and alerts --------------------------------------------------------------------------
{
  echo "$(date '+%F %T')"
  printf '%s\n' "${REPORT[@]}"
} > "${STATE_DIR}/monitor.status"

# Which problems, ignoring numbers (timings, ages, percentages), so the same outage alerts only once.
SIGNATURE="$(printf '%s\n' "${PROBLEMS[@]+"${PROBLEMS[@]}"}" | sed -E 's/[0-9]+/N/g' | sort | md5sum | cut -d' ' -f1)"
STREAK="$(cat "${STATE_DIR}/monitor.streak" 2>/dev/null || echo 0)"
ALERTED="$(cat "${STATE_DIR}/monitor.alerted" 2>/dev/null || true)"

if [ "${#PROBLEMS[@]}" -gt 0 ]; then
  STREAK=$((STREAK + 1))
  echo "$STREAK" > "${STATE_DIR}/monitor.streak"
  for p in "${PROBLEMS[@]}"; do log "FAIL ${p}"; done
  if [ "$STREAK" -ge "$MONITOR_ALERT_AFTER" ] && [ "$ALERTED" != "$SIGNATURE" ]; then
    notify "$(printf 'Monitoring: %s problem(s)\n' "${#PROBLEMS[@]}"; printf -- '- %s\n' "${PROBLEMS[@]}")"
    echo "$SIGNATURE" > "${STATE_DIR}/monitor.alerted"
  fi
else
  if [ "$STREAK" -gt 0 ]; then
    log "RECOVERED after ${STREAK} failing run(s)"
  fi
  if [ -n "$ALERTED" ]; then
    notify "Monitoring: all checks pass again"
    rm -f "${STATE_DIR}/monitor.alerted"
  fi
  echo 0 > "${STATE_DIR}/monitor.streak"
fi

if [ "${1:-}" = "--print" ]; then
  cat "${STATE_DIR}/monitor.status"
fi
[ "${#PROBLEMS[@]}" -eq 0 ]
