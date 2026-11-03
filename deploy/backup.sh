#!/usr/bin/env bash
# Writes a dump of the database to deploy/backups and removes dumps older than KEEP_DAYS (14). Run it from cron; see docs/deployment.md,
# "Backups". The dump holds everything, including the signing keys, which are encrypted: the key that decrypts them
# (AUTH_KEY_ENCRYPTION_KEY in deploy/.env) is NOT in the dump and must be kept safe separately.
set -euo pipefail

cd "$(dirname "$0")"
backup_dir="${BACKUP_DIR:-$PWD/backups}"
keep_days="${KEEP_DAYS:-14}"

mkdir -p "$backup_dir"
umask 077
file="$backup_dir/auth-$(date -u +%Y%m%dT%H%M%SZ).dump"

# Written under another name first, so a failed dump never leaves a file that looks like a good one.
docker compose -f docker-compose.yml --env-file .env exec -T db pg_dump -U auth -d auth --format=custom > "$file.partial"
if [ ! -s "$file.partial" ]; then
  rm -f "$file.partial"
  echo "The dump is empty; nothing was written." >&2
  exit 1
fi
mv "$file.partial" "$file"

find "$backup_dir" -name 'auth-*.dump' -mtime +"$keep_days" -delete
echo "Wrote $file ($(du -h "$file" | cut -f1))"
