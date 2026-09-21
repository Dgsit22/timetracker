# Deploying TimeTracker.Server on a Linux box (Docker)

## Prerequisites

- Docker Engine + the Compose plugin installed on the Linux server:
  ```
  curl -fsSL https://get.docker.com | sh
  sudo apt-get install -y docker-compose-plugin   # Debian/Ubuntu
  ```

## First-time setup

1. Copy the repository to the server (git clone, scp, rsync — any of these work):
   ```
   git clone <your-repo-url> timetracker
   cd timetracker
   ```

2. Create your `.env` from the template and fill in real values:
   ```
   cp .env.example .env
   nano .env
   ```
   At minimum set `POSTGRES_PASSWORD`, `ADMIN_EMAIL`, `ADMIN_PASSWORD`, and
   `AGENT_API_KEY`. `AGENT_API_KEY` must match what you give the Windows
   Agent installer (`AGENTAPIKEY` MSI property) on every machine.

3. Build and start everything:
   ```
   docker compose up -d --build
   ```
   This starts Postgres (with a persistent named volume) and the Server. The
   Server applies its EF Core migrations automatically on startup and seeds
   the first Admin account from `ADMIN_EMAIL`/`ADMIN_PASSWORD`.

4. Confirm it's up:
   ```
   docker compose ps
   curl -I http://localhost:5081/Activity   # expect a 302 redirect to /Account/Login
   ```

5. Open `http://<server-ip>:5081` in a browser and log in with the seeded
   Admin account. Change that password (or add other Admin/Viewer users from
   the Users page) once you're in.

   If login fails with "Invalid login attempt", the seeded account was
   probably never created - `ADMIN_PASSWORD` must satisfy ASP.NET Core
   Identity's default rules (6+ chars, upper, lower, digit, non-alphanumeric)
   or admin creation silently fails. Check `docker compose logs server` for
   an Admin seeding warning/error, fix `ADMIN_PASSWORD` in `.env`, then
   `docker compose up -d` again (safe to rerun - it only seeds when no users
   exist yet).

## Pointing Agents at this server

Each Windows Agent needs:
- **Server URL**: `http://<server-ip>:5081`
- **Agent API key**: the same `AGENT_API_KEY` value from `.env`

Both are set at install time via the MSI's `SERVERURL` / `AGENTAPIKEY`
properties (see `installer/README.md`), and can be changed later without
reinstalling by editing that machine's `%ProgramData%\TimeTracker\agent-settings.json`
and having the user log off/on (or just relaunching
`TimeTracker.Agent.exe` from `C:\Program Files\TimeTracker Agent\`). The
Agent runs as a per-user Startup-folder app, not a Windows Service — see
"Why a config file instead of environment variables" in `installer/README.md`
for why.

## Moving to a different server later (e.g. a cloud VM)

Nothing on the Agent side is hardcoded to this box. To move:

1. Stand up the same `docker compose up -d --build` on the new host (a fresh
   Postgres volume is fine — the Agents' local outboxes hold undelivered
   events and will resend once pointed at the new URL).
2. On each Agent machine, edit `%ProgramData%\TimeTracker\agent-settings.json`
   to point `ServerBaseUrl` (and `ApiKey`, if you rotate it) at the new
   server, then have the user log off/on — no reinstall needed.

## Updating

```
git pull
docker compose up -d --build
```

Migrations run automatically on the new container's startup; Postgres data
persists in the `postgres-data` named volume across rebuilds.

## Logs

```
docker compose logs -f server
docker compose logs -f postgres
```

## Backups

The Postgres data directory lives in the `postgres-data` named volume.

`backup.sh` in this directory dumps the database out of the running container,
gzips it, and prunes anything older than `KEEP_DAYS` (14 by default):

```
chmod +x deploy/linux/backup.sh
sudo BACKUP_DIR=/var/backups/timetracker deploy/linux/backup.sh
```

Install it as a nightly cron job — 02:30 here, with its output in a log you can
check after the fact:

```
sudo crontab -e
```

```
30 2 * * * BACKUP_DIR=/var/backups/timetracker KEEP_DAYS=14 /home/dgsadmin/timetracker/deploy/linux/backup.sh >> /var/log/timetracker-backup.log 2>&1
```

Use the absolute path to wherever the repository actually lives — cron does not
run from your home directory and has almost none of your shell's environment.

**Restore** into a running stack:

```
gunzip -c /var/backups/timetracker/timetracker-YYYYMMDD-HHMMSS.sql.gz \
  | docker compose exec -T postgres psql -U postgres timetracker
```

Restore into an empty database, not over a populated one — `pg_dump` output
recreates its tables and will collide with existing ones. To start clean:
`docker compose down`, `docker volume rm timetracker_postgres-data`,
`docker compose up -d`, wait for the server to apply its migrations, then pipe
the dump in.

A backup nobody has restored is a guess. Do one restore into a throwaway
database now, while it does not matter, rather than finding out during an
incident.

Note that database size is driven mostly by screenshots, which are stored as
bytes in the `Screenshots` table — see the retention settings in `.env.example`.
Dump size follows whatever window you set there.
