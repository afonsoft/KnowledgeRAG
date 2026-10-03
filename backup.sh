#!/usr/bin/env bash
# backup.sh — SPEC-20260914-backup-restore
# Consistent backup of the KnowledgeHub database — SQLite (VACUUM INTO) or
# PostgreSQL (pg_dump, custom format) — plus an archive of every ObsidianVault
# source path that is accessible from this host.
set -euo pipefail

DATA_DIR="./data"
OUT_DIR="./backups"
DB_PATH=""
PG_CONN=""

usage() {
    cat <<'USAGE'
Usage: ./backup.sh [options]

  --data-dir <dir>   Directory containing knowledgehub.db (default: ./data)
  --db <path>        Explicit path to the database file (overrides --data-dir)
  --pg <conn>        PostgreSQL mode: libpq conninfo/URI for pg_dump (e.g.
                     "host=pg dbname=knowledgehub user=kh" or postgres://...).
                     Standard PG* env vars are honored inside the conninfo.
  --out <dir>        Backup output directory (default: ./backups)
  -h, --help         Show this help

Produces backups/<timestamp>/ containing:
  knowledgehub.db    consistent SQLite snapshot (VACUUM INTO)
  knowledgehub.pg.dump  pg_dump -Fc archive (--pg mode)
  vaults/            tar.gz per accessible Obsidian vault source
  dataprotection-keys.tar.gz   ASP.NET Data Protection key ring — required to
                     decrypt IntegrationSecrets (upstream API keys) after restore
  manifest.txt       backup metadata (timestamp, db, vault paths)
USAGE
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --data-dir) DATA_DIR="${2:?--data-dir requires a value}"; shift 2 ;;
        --db)       DB_PATH="${2:?--db requires a value}"; shift 2 ;;
        --pg)       PG_CONN="${2:?--pg requires a value}"; shift 2 ;;
        --out)      OUT_DIR="${2:?--out requires a value}"; shift 2 ;;
        -h|--help)  usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

DB_PATH="${DB_PATH:-$DATA_DIR/knowledgehub.db}"
if [[ -n "$PG_CONN" ]]; then
    if ! command -v pg_dump >/dev/null 2>&1 || ! command -v psql >/dev/null 2>&1; then
        echo "error: --pg requires postgresql-client (pg_dump + psql)" >&2
        exit 1
    fi
else
    if [[ ! -f "$DB_PATH" ]]; then
        echo "error: database not found at '$DB_PATH' (use --db, --data-dir or --pg)" >&2
        exit 1
    fi
    if ! command -v sqlite3 >/dev/null 2>&1; then
        echo "error: sqlite3 CLI is required for a consistent (VACUUM INTO) backup" >&2
        exit 1
    fi
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DEST="$OUT_DIR/$STAMP"
mkdir -p "$DEST/vaults"

if [[ -n "$PG_CONN" ]]; then
    echo "==> Backing up PostgreSQL database (pg_dump -Fc)"
    pg_dump "$PG_CONN" -Fc -f "$DEST/knowledgehub.pg.dump"
else
    echo "==> Backing up database $DB_PATH"
    sqlite3 "$DB_PATH" "VACUUM INTO '$DEST/knowledgehub.db';"
fi

# Archive linked Obsidian vaults whose path is accessible.
VAULT_COUNT=0

archive_vaults() {
    while IFS= read -r vault_path; do
        [[ -z "$vault_path" || "$vault_path" == "null" ]] && continue
        if [[ -d "$vault_path" ]]; then
            # SPEC-20260926-ops-and-ui-polish: basename alone collides across
            # homonymous vaults (/a/docs + /b/docs) — suffix a path hash.
            slug="$(basename "$vault_path" | tr -c '[:alnum:]_-' '_')-$(printf '%s' "$vault_path" | cksum | cut -d' ' -f1)"
            echo "==> Archiving vault $vault_path -> vaults/$slug.tar.gz"
            tar -czf "$DEST/vaults/$slug.tar.gz" -C "$(dirname "$vault_path")" "$(basename "$vault_path")"
            VAULT_COUNT=$((VAULT_COUNT + 1))
        else
            echo "warn: vault path '$vault_path' not accessible — skipped" >&2
        fi
    done
}

if command -v jq >/dev/null 2>&1; then
    if [[ -n "$PG_CONN" ]]; then
        archive_vaults < <(psql "$PG_CONN" -At \
            -c "SELECT \"ConfigurationJson\" FROM \"Sources\" WHERE \"SourceType\"='ObsidianVault'" \
            | jq -r 'fromjson | .path // empty')
    else
        archive_vaults < <(sqlite3 -json "$DB_PATH" \
            "SELECT ConfigurationJson FROM Sources WHERE SourceType='ObsidianVault'" \
            | jq -r '.[].ConfigurationJson | fromjson | .path // empty')
    fi
else
    echo "warn: jq not found — vault paths cannot be read from the DB; DB-only backup" >&2
fi

# SPEC-20260916-firecrawl-mcp-proxy: the Data Protection key ring decrypts the
# IntegrationSecrets table (upstream API keys). Without it a restored DB cannot
# recover stored credentials — ship it alongside the DB.
DP_KEYS_DIR="$(dirname "$DB_PATH")/dataprotection-keys"
# In --pg mode there is no sqlite file; the key ring still lives under DATA_DIR.
[[ -n "$PG_CONN" ]] && DP_KEYS_DIR="$DATA_DIR/dataprotection-keys"
if [[ -d "$DP_KEYS_DIR" ]]; then
    echo "==> Archiving Data Protection keys -> dataprotection-keys.tar.gz"
    tar -czf "$DEST/dataprotection-keys.tar.gz" -C "$(dirname "$DB_PATH")" "dataprotection-keys"
else
    echo "warn: no dataprotection-keys dir at $DP_KEYS_DIR — stored secrets won't survive a restore" >&2
fi

cat > "$DEST/manifest.txt" <<EOF
created_utc=$STAMP
db_provider=$( [[ -n "$PG_CONN" ]] && echo "postgresql" || echo "sqlite" )
db_source=$( [[ -n "$PG_CONN" ]] && echo "<pg conninfo omitted>" || echo "$DB_PATH" )
vaults_archived=$VAULT_COUNT
dataprotection_keys=$( [[ -f "$DEST/dataprotection-keys.tar.gz" ]] && echo "yes" || echo "no" )
EOF

echo "==> Backup complete: $DEST"
