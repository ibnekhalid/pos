#!/usr/bin/env bash
# Installs the nightly backup, the weekly restore drill and the 5-minute monitor on the VM.
# Run as root from the repo root (/root/restaurant); scripts/deploy-production.sh runs it on every
# deploy, and running it again changes nothing unless the scripts or schedules changed.
#
# /root/restaurant is wiped and recopied on every deploy, so cron must not run scripts from there:
#   /opt/restaurant-ops/        copies of backup-database.sh, restore-drill.sh, monitor.sh
#   /etc/cron.d/restaurant-ops  the three schedules (VM local time)
#   /etc/restaurant/ops.env     settings (off-VM copy, webhook, thresholds); created once, never overwritten
#   /etc/logrotate.d/restaurant-ops   weekly rotation of /var/log/restaurant/*.log
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
  echo "ERROR: run as root (writes /etc/cron.d and /opt)" >&2
  exit 1
fi

OPS_DIR=/opt/restaurant-ops
OPS_ENV_FILE=/etc/restaurant/ops.env
CRON_FILE=/etc/cron.d/restaurant-ops
LOGROTATE_FILE=/etc/logrotate.d/restaurant-ops
STATE_DIR=/var/lib/restaurant-ops
LOG_DIR=/var/log/restaurant

echo "=== Install backups, restore drill and monitoring ==="

# --- settings file (first run only) --------------------------------------------------------------------
mkdir -p "$(dirname "$OPS_ENV_FILE")"
if [ ! -f "$OPS_ENV_FILE" ]; then
  umask 077
  cat > "$OPS_ENV_FILE" <<'EOF'
# Restaurant POS backups and monitoring (read by /opt/restaurant-ops/*.sh).
# Created once by scripts/install-ops.sh and never overwritten — edit it here on the VM.
# Re-run the deploy (or ./scripts/install-ops.sh from /root/restaurant) after changing a *_SCHEDULE.

# Where backups go and how many are kept.
BACKUP_DIR=/root/backups/restaurant
BACKUP_KEEP_DAILY=14
BACKUP_KEEP_WEEKLY=8

# Off-VM copy after each nightly backup (either or both; leave empty to skip).
#   rsync over SSH: root needs key access to the target, e.g. backup@nas.example.com:/srv/backups/restaurant/
#   rclone: run `rclone config` as root first, e.g. b2:my-bucket/restaurant
BACKUP_RSYNC_TARGET=
BACKUP_RCLONE_REMOTE=

# Alerts: a Slack / Google Chat / Discord incoming-webhook URL. Leave empty to only log.
ALERT_WEBHOOK_URL=

# Monitoring thresholds. MONITOR_PUBLIC_URL also checks DNS, Nginx Proxy Manager and the certificate.
MONITOR_PUBLIC_URL=https://restaurant.dsnoopdoc.com
MONITOR_BACKUP_MAX_AGE_HOURS=26
MONITOR_DISK_MAX_PERCENT=90
MONITOR_ALERT_AFTER=2

# Cron schedules (VM local time; check with `timedatectl`. On UTC, 30 23 is 04:30 in Pakistan).
BACKUP_SCHEDULE="30 23 * * *"
DRILL_SCHEDULE="30 0 * * 0"
MONITOR_SCHEDULE="*/5 * * * *"
EOF
  umask 022
  echo "  created ${OPS_ENV_FILE} (edit it to add an off-VM copy and alerts)"
fi
chmod 600 "$OPS_ENV_FILE"

set -a
. "$OPS_ENV_FILE"
set +a
BACKUP_DIR="${BACKUP_DIR:-/root/backups/restaurant}"
BACKUP_SCHEDULE="${BACKUP_SCHEDULE:-30 23 * * *}"
DRILL_SCHEDULE="${DRILL_SCHEDULE:-30 0 * * 0}"
MONITOR_SCHEDULE="${MONITOR_SCHEDULE:-*/5 * * * *}"

# --- scripts -------------------------------------------------------------------------------------------
mkdir -p "$OPS_DIR" "$STATE_DIR" "$LOG_DIR" "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"
for script in backup-database.sh restore-drill.sh monitor.sh; do
  # Strip CRs in case the copy came from a Windows checkout without .gitattributes.
  sed 's/\r$//' "scripts/${script}" > "${OPS_DIR}/${script}.new"
  chmod 755 "${OPS_DIR}/${script}.new"
  mv "${OPS_DIR}/${script}.new" "${OPS_DIR}/${script}"
done
echo "  scripts in ${OPS_DIR}"

# The monitor allows a day for the first nightly backup after the first install.
[ -f "${STATE_DIR}/installed-at" ] || date +%s > "${STATE_DIR}/installed-at"

# --- cron ----------------------------------------------------------------------------------------------
# Only replaced when it changes. Output goes to the scripts' own logs; cron.log catches anything earlier.
CRON_NEW="$(mktemp)"
cat > "$CRON_NEW" <<EOF
# Restaurant POS backups and monitoring. Written by scripts/install-ops.sh on each deploy — edit
# ${OPS_ENV_FILE} (the *_SCHEDULE values) instead of this file.
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

${BACKUP_SCHEDULE} root ${OPS_DIR}/backup-database.sh >/dev/null 2>>${LOG_DIR}/cron.log
${DRILL_SCHEDULE} root ${OPS_DIR}/restore-drill.sh >/dev/null 2>>${LOG_DIR}/cron.log
${MONITOR_SCHEDULE} root ${OPS_DIR}/monitor.sh >/dev/null 2>>${LOG_DIR}/cron.log
EOF
if cmp -s "$CRON_NEW" "$CRON_FILE"; then
  rm -f "$CRON_NEW"
  echo "  ${CRON_FILE} unchanged"
else
  # cron.d files must be root-owned, not group/world-writable, and named without dots.
  install -o root -g root -m 0644 "$CRON_NEW" "$CRON_FILE"
  rm -f "$CRON_NEW"
  echo "  ${CRON_FILE} written"
fi

if ! systemctl is-active --quiet cron 2>/dev/null; then
  echo "WARN: the cron service is not active — backups will not run (apt install cron && systemctl enable --now cron)"
fi

# --- log rotation --------------------------------------------------------------------------------------
if [ -d /etc/logrotate.d ]; then
  cat > "$LOGROTATE_FILE" <<EOF
${LOG_DIR}/*.log {
    weekly
    rotate 8
    compress
    delaycompress
    missingok
    notifempty
}
EOF
fi

for tool in rsync rclone; do
  var="BACKUP_$( [ "$tool" = rsync ] && echo RSYNC_TARGET || echo RCLONE_REMOTE )"
  if [ -n "${!var:-}" ] && ! command -v "$tool" >/dev/null 2>&1; then
    echo "WARN: ${var} is set but ${tool} is not installed — the nightly backup will fail its off-VM copy"
  fi
done

echo "  backups: '${BACKUP_SCHEDULE}' → ${BACKUP_DIR}; drill: '${DRILL_SCHEDULE}'; monitor: '${MONITOR_SCHEDULE}'"
