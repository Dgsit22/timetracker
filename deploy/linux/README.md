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

## Pointing Agents at this server

Each Windows Agent needs:
- **Server URL**: `http://<server-ip>:5081`
- **Agent API key**: the same `AGENT_API_KEY` value from `.env`

Both are set at install time via the MSI's `SERVERURL` / `AGENTAPIKEY`
properties (see `installer/README.md`), and can be changed later without
reinstalling by editing that machine's environment variables and restarting
the `TimeTrackerAgent` service.

## Moving to a different server later (e.g. a cloud VM)

Nothing on the Agent side is hardcoded to this box. To move:

1. Stand up the same `docker compose up -d --build` on the new host (a fresh
   Postgres volume is fine — the Agents' local outboxes hold undelivered
   events and will resend once pointed at the new URL).
2. On each Agent machine, change the `Agent__ServerBaseUrl` (and
   `Agent__ApiKey` if you rotate it) environment variable and restart the
   `TimeTrackerAgent` service — no reinstall needed:
   ```powershell
   [Environment]::SetEnvironmentVariable("Agent__ServerBaseUrl", "https://timetracker.yourcompany.com", "Machine")
   Restart-Service TimeTrackerAgent
   ```

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

The Postgres data directory lives in the `postgres-data` named volume. Back
it up with `docker exec <postgres-container> pg_dump -U postgres timetracker
> backup.sql`, or snapshot the volume directly.
