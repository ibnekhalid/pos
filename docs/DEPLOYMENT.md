# Deployment

Restaurant POS runs on the same Ubuntu VM as Tailor Book and dsnoopdoc. It uses the same pattern:
GitHub Actions copies the repo to the VM and runs a blue-green deploy with Docker Compose. Nginx Proxy
Manager (NPM) on the VM handles HTTPS.

```
https://restaurant.dsnoopdoc.com      → NPM → 127.0.0.1:8096 → rp-web (nginx + WebAssembly app)
                                                               └ /api /hubs /uploads /health → rp-api:8080
https://api.restaurant.dsnoopdoc.com  → NPM → 127.0.0.1:8097 → rp-api   (desktop app)
                                                               └ rp-postgres (private network)
```

| | |
|---|---|
| VM folder | `/root/restaurant` (wiped and recopied on every deploy; data lives in Docker volumes) |
| Containers | `restaurant_postgres`, `restaurant_api`, `restaurant_web` |
| Volumes | `restaurant_postgres_data`, `restaurant_uploads`, `restaurant_logs` |
| Ports in use on the VM | dsnoopdoc 8088 · Tailor Book 8098/8099 · **Restaurant 8096/8097** |

## One-time setup

### 1. GitHub secrets

Add these under **Settings → Secrets and variables → Actions → Secrets tab → New repository secret**.
They must be repository secrets, not inside an Environment.

| Secret | Value |
|---|---|
| `RP_POSTGRES_PASSWORD` | `openssl rand -base64 32`. Use letters, digits and `+/=` only, because the deploy script loads `.env` with bash. |
| `RP_JWT_SIGNING_KEY` | `openssl rand -base64 64` |
| `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY` | The same values as in the Tailor Book repo |

About the two app secrets:
- The database password is applied only when the database volume is first created. To change it
  later, run `ALTER USER restaurant PASSWORD '...'` inside `restaurant_postgres`, then update the secret.
- Changing the JWT key signs everyone out.

### 2. DNS

Add two A records in the `dsnoopdoc.com` zone, both pointing at the VM's IP:

| Type | Name | Value |
|---|---|---|
| A | `restaurant` | VM IP |
| A | `api.restaurant` | VM IP |

If the zone is on Cloudflare, set both records to **DNS only** (grey cloud) until NPM has issued the
certificates.

### 3. Nginx Proxy Manager

Go to **Hosts → Proxy Hosts → Add Proxy Host** and add these two hosts:

| Domain | Scheme | Forward host | Port |
|---|---|---|---|
| `restaurant.dsnoopdoc.com` | http | `127.0.0.1` | `8096` |
| `api.restaurant.dsnoopdoc.com` | http | `127.0.0.1` | `8097` |

Set up both hosts the same way:
- **Details:** turn on Block common exploits and **Websockets support**. Websockets are required, because
  the kitchen display uses SignalR.
- **SSL:** request a new Let's Encrypt certificate, with Force SSL and HTTP/2 on.
- **Advanced:** add `client_max_body_size 8m;`.

If NPM runs in Docker and can't reach `127.0.0.1`, forward both hosts to `restaurant_web` port `80`
instead. That container forwards `/api`, `/hubs` and `/health` to the API.

### 4. First deploy

Push to `main`, or go to **Actions → Deploy to Ubuntu VM via SSH → Run workflow**.

## What a deploy does

1. Runs the tests and a Release build of the web app. If either fails, nothing is deployed.
2. Wipes `/root/restaurant` and copies the repo there.
3. Writes `.env` from the secrets.
4. Runs `scripts/deploy-production.sh`:
   1. starts Postgres;
   2. takes a pre-migration backup (`/root/backups/restaurant/pre-deploy`, 5 kept; skipped while the database
      is still empty). If it fails, the deploy stops;
   3. restores packages and runs the EF Core migrations;
   4. builds the images;
   5. starts the standby containers;
   6. runs smoke tests (`/web-health`, `/health`, `/api/me` → 401, the app shell, the `/app/...` fallback);
   7. promotes the standby containers;
   8. installs or updates the backup, restore-drill and monitoring cron jobs (`scripts/install-ops.sh`).
5. If step 4 fails before promotion, it restores traffic with `scripts/restore-production.sh`.

Check the stack on the VM:
```bash
cd /root/restaurant && ./devops/scripts/health-check.sh
```

## Desktop app

Release builds call `https://api.restaurant.dsnoopdoc.com`. The app is unpackaged (a plain `.exe` folder)
and ships as an Inno Setup installer. Setting up a counter PC and its printers is in
[PRINTING.md](PRINTING.md).

### Building the installer

On a Windows machine with the .NET 10 SDK, the `maui-windows` workload and Inno Setup 6
(`winget install JRSoftware.InnoSetup`):

1. Bump `ApplicationDisplayVersion` (for example `0.3`) and `ApplicationVersion` (an integer, +1) in
   `src/clients/Restaurant.Desktop/Restaurant.Desktop.csproj`. The app reports this version to the
   update check.
2. Run `.\installer\build-installer.ps1` from the repo root. It runs `dotnet publish … -c Release -r win-x64`
   self-contained (.NET and the Windows App SDK are inside the app folder) into `installer/publish`, then
   compiles `installer/Restaurant.Desktop.iss` into `installer/output/Restaurant-POS-Setup-<version>.exe`
   with a `.sha256` file next to it.
   - `-PublishOnly` gives just the folder; `-SkipPublish` re-packages the last publish.
   - The app needs the Microsoft Edge WebView2 Runtime. Windows 11 has it; older Windows 10 may not. If
     `installer/redist/MicrosoftEdgeWebview2Setup.exe` (the Evergreen bootstrapper from Microsoft's
     WebView2 page) is present, the installer bundles it and installs WebView2 silently where it's
     missing. Otherwise the installer only warns.

The installer installs per user by default, so a cashier can update without an admin prompt. Its first
page offers "all users" for a shared PC. An update closes the running app, replaces the program folder
and keeps the sign-in, device registration and preferences, which live in the user's AppData.
Uninstalling also keeps them.

### Desktop app releases (download page and update check)

Signed-in users download the Windows counter app from **Get the apps** (`/app/downloads`). There, tablets
and phones are shown how to add the web app to their home screen; there is no separate mobile app. On
start-up the desktop app checks `GET /api/app-releases/windows` (anonymous). If a newer version is
published, it shows a notice with a download link at the top of every screen. The notice never blocks the
till: a cashier can dismiss it until the next start, and it turns red ("update required") when the app is
older than the minimum version.

Both read `releases.json` in the downloads folder: `/root/downloads/restaurant`, served at `/downloads/` by
rp-web and mounted read-only into rp-api.

To publish a release:

1. In GitHub go to **Actions → Build and publish the Windows app → Run workflow**. Enter the version (for
   example `0.3`) and, optionally, a minimum version.
2. The workflow (`.github/workflows/apps.yml`) runs on a Windows runner. It installs Inno Setup, builds
   `Restaurant-POS-Setup-<version>.exe`, and uploads it to the VM. It writes `releases.json` (version, size,
   SHA-256) last, so the page never links to a file that isn't there yet. It uses the same `SSH_*` secrets as
   the deploy and doesn't touch the API or the database.
3. Check it with `curl https://api.restaurant.dsnoopdoc.com/api/app-releases/windows`.

Older installers stay in the folder. Delete them by hand once every counter has updated.

To publish by hand instead, copy the installer to `/root/downloads/restaurant/` and write `releases.json`
next to it:

```json
{ "windows": { "version": "0.3", "file": "Restaurant-POS-Setup-0.3.exe", "minimumVersion": null } }
```

The older repo variables `RP_DESKTOP_LATEST_VERSION` / `RP_DESKTOP_DOWNLOAD_URL` /
`RP_DESKTOP_MINIMUM_VERSION` still work. When they are set they override the Windows entry (after a
re-deploy).

## Backups

The database and the uploads volume (logos, menu images) are the only state. Everything else is rebuilt
on each deploy.

| | |
|---|---|
| Nightly | `backup-database.sh` at 23:30 VM time (04:30 in Pakistan when the VM is on UTC). 14 daily and 8 weekly backups are kept (the Sunday backup is also kept as a weekly one) |
| Before each deploy | `backup-database.sh pre-deploy`, just before the migrations (5 kept) |
| Weekly drill | `restore-drill.sh` on Sunday at 00:30. It restores the newest backup into a throwaway container and checks it |
| Where | `/root/backups/restaurant/{daily,weekly,pre-deploy}/<YYYY-MM-DD_HHMMSS>/` holding `restaurant.dump` (`pg_dump -Fc`), `uploads.tgz` and `SHA256SUMS` |
| Logs | `/var/log/restaurant/backup.log`, `restore-drill.log`, `monitor.log` (rotated weekly, 8 kept) |
| Settings | `/etc/restaurant/ops.env`: retention, off-VM copy, alert webhook, thresholds, schedules |

`scripts/install-ops.sh` installs all of this on every deploy. It changes nothing unless the scripts or
schedules changed. It copies the scripts to `/opt/restaurant-ops`, because the deploy wipes
`/root/restaurant`. It writes `/etc/cron.d/restaurant-ops` and creates `/etc/restaurant/ops.env` once
(it never overwrites that file). After you change a `*_SCHEDULE` in `ops.env`, run the script again:
`cd /root/restaurant && ./scripts/install-ops.sh`.

A backup folder appears only after its dump has been read back with `pg_restore --list`. If a nightly
backup fails, the script exits non-zero, writes `FAILED` in the log and a `LAST_FAILURE` file in
`/root/backups/restaurant`, and posts to the webhook if one is set. The monitor then reports it every
5 minutes until a nightly backup succeeds.

### Off-VM copy

A copy on the same VM doesn't survive losing the VM. Set one or both targets in `/etc/restaurant/ops.env`:
```bash
BACKUP_RSYNC_TARGET=backup@nas.example.com:/srv/backups/restaurant/   # root needs SSH key access
BACKUP_RCLONE_REMOTE=b2:my-bucket/restaurant                         # after `rclone config` as root
```
Each nightly backup then copies `daily/` and `weekly/` to the target. Nothing is ever deleted there, so
set retention on the remote side, for example with a bucket lifecycle rule. If the copy fails, the
backup counts as failed, but the local copy is kept.

### Run, list, check

```bash
/opt/restaurant-ops/backup-database.sh            # a nightly-style backup now
ls /root/backups/restaurant/daily                 # newest last
cat /root/backups/restaurant/LAST_SUCCESS /root/backups/restaurant/LAST_DRILL
tail -50 /var/log/restaurant/backup.log
```

### Restore drill

```bash
/opt/restaurant-ops/restore-drill.sh                                   # the newest nightly backup
/opt/restaurant-ops/restore-drill.sh /root/backups/restaurant/weekly/2026-10-25_233000
```
The drill:
1. verifies the checksums and lists the uploads archive;
2. starts `restaurant_restore_drill` from the production Postgres image, with no network and no port,
   and restores the dump with `pg_restore --exit-on-error`;
3. checks that the key tables exist, that `__EFMigrationsHistory` and `ReceiptTemplates` are not empty,
   and notes it when production is on a newer migration;
4. prints the row count of every table;
5. always removes the container.

On success it writes `LAST_DRILL`. On failure it writes `DRILL_FAILED` (the monitor reports this) and
posts to the webhook. Production is only read, to compare the latest migration.

### Restoring production

Restoring replaces everything written since the backup. Run it from `/root/restaurant`:

```bash
B=/root/backups/restaurant/daily/2026-10-28_233000     # the backup to restore
/opt/restaurant-ops/restore-drill.sh "$B"              # 1. prove it restores
/opt/restaurant-ops/backup-database.sh pre-deploy      # 2. keep today's state, just in case

docker stop restaurant_web restaurant_api              # 3. stop writes (the site is down from here)

# 4. replace the database
docker exec restaurant_postgres psql -U restaurant -d postgres -c 'DROP DATABASE restaurant WITH (FORCE)'
docker exec restaurant_postgres psql -U restaurant -d postgres -c 'CREATE DATABASE restaurant OWNER restaurant'
docker exec -i restaurant_postgres pg_restore -U restaurant -d restaurant --exit-on-error --no-owner < "$B/restaurant.dump"

# 5. replace the uploads (logos, menu images)
docker run --rm -v restaurant_uploads:/data -v "$B":/backup:ro postgres:16-alpine \
  sh -c 'find /data -mindepth 1 -delete && tar xzf /backup/uploads.tgz -C /data'

docker start restaurant_api restaurant_web             # 6. back up
./devops/scripts/health-check.sh
```

Then:
- If the backup is older than the code, the next deploy runs the missing migrations. To run them now,
  re-run the deploy workflow.
- Expect staff to sign in again: refresh tokens issued after the backup aren't in the restored database.
  Counter PCs registered after the backup must be registered again.
- Orders taken after the backup are gone. Re-enter them from the printed receipts.

To restore a single table instead, restore into the drill container and copy the rows across. Ask before
doing this on production.

## Monitoring

`monitor.sh` runs every 5 minutes from the same cron file. It needs no extra services. It checks:

| Check | Fails when |
|---|---|
| `http://127.0.0.1:8096/health` | the web container, the API or the database is down (the response must say `healthy`) |
| `http://127.0.0.1:8097/health` | the API port used by the desktop apps is down |
| `MONITOR_PUBLIC_URL/health` | DNS, Nginx Proxy Manager or the certificate is broken (default `https://restaurant.dsnoopdoc.com`; leave it empty to skip) |
| Newest nightly backup | it's older than `MONITOR_BACKUP_MAX_AGE_HOURS` (26). There's a day's grace after the first install |
| `LAST_FAILURE`, `DRILL_FAILED` | the last nightly backup or restore drill failed |
| Disk (`/`, `/var/lib/docker`, the backup folder) | it's `MONITOR_DISK_MAX_PERCENT` (90) or more full |

```bash
/opt/restaurant-ops/monitor.sh --print     # run now and show the result (exit 1 if anything fails)
cat /var/lib/restaurant-ops/monitor.status # the last cron run
tail /var/log/restaurant/monitor.log       # failures and recoveries only
```

**Alerts:** set `ALERT_WEBHOOK_URL` in `/etc/restaurant/ops.env` to a Slack, Google Chat or Discord
incoming webhook. The script posts `{"text": …, "content": …}`. The monitor alerts once a problem has
been seen on `MONITOR_ALERT_AFTER` (2) runs in a row, so the short cutover during a deploy doesn't
trigger an alert. After that it alerts again only if the set of problems changes, and once on recovery.
Backup and drill failures alert straight away. Without a webhook, failures are only logged, so check
`monitor.status` after each deploy.
