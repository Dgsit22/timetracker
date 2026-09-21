#!/usr/bin/env bash
#
# Nightly Postgres backup for TimeTracker.Server.
#
# Dumps the database out of the running postgres container, compresses it, and keeps the
# last N days. Install it with cron - see "Backups" in deploy/linux/README.md.
#
# Deliberately boring: no incremental chain, no external service, nothing to get out of
# sync. A gzipped pg_dump that restores with one command beats a clever scheme nobody has
# ever tested a restore from.

set -euo pipefail

# Directory holding docker-compose.yml. Override when cron runs this from elsewhere.
PROJECT_DIR="${PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/timetracker}"
KEEP_DAYS="${KEEP_DAYS:-14}"
DB_SERVICE="${DB_SERVICE:-postgres}"
DB_NAME="${DB_NAME:-timetracker}"
DB_USER="${DB_USER:-postgres}"

timestamp="$(date +%Y%m%d-%H%M%S)"
target="${BACKUP_DIR}/timetracker-${timestamp}.sql.gz"

log() { echo "[$(date --iso-8601=seconds)] $*"; }

mkdir -p "$BACKUP_DIR"
cd "$PROJECT_DIR"

if ! docker compose ps --status running --services 2>/dev/null | grep -qx "$DB_SERVICE"; then
    log "ERROR: service '${DB_SERVICE}' is not running in ${PROJECT_DIR}"
    exit 1
fi

log "dumping ${DB_NAME} -> ${target}"

# Write to a temporary name first: a cron job killed mid-dump would otherwise leave a
# truncated file sitting there looking exactly like a good backup.
tmp="${target}.partial"
if ! docker compose exec -T "$DB_SERVICE" pg_dump -U "$DB_USER" "$DB_NAME" | gzip -c > "$tmp"; then
    log "ERROR: pg_dump failed"
    rm -f "$tmp"
    exit 1
fi

# pg_dump exiting 0 with an empty file means something went wrong upstream of the pipe.
if [ ! -s "$tmp" ]; then
    log "ERROR: dump is empty"
    rm -f "$tmp"
    exit 1
fi

mv "$tmp" "$target"
log "wrote $(du -h "$target" | cut -f1)"

deleted="$(find "$BACKUP_DIR" -name 'timetracker-*.sql.gz' -type f -mtime "+${KEEP_DAYS}" -print -delete | wc -l)"
log "pruned ${deleted} backup(s) older than ${KEEP_DAYS} days"

log "done. ${BACKUP_DIR} now holds $(find "$BACKUP_DIR" -name 'timetracker-*.sql.gz' -type f | wc -l) backup(s)"
