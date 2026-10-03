#!/usr/bin/env bash
# restore.sh — SPEC-20260914-backup-restore
# Restore a backup produced by backup.sh — knowledgehub.db (SQLite) or
# knowledgehub.pg.dump (PostgreSQL, via --pg) — then restart the service
# (docker container `knowledgehub` or systemd unit `knowledgehub.service`,
# whichever is present).
set -euo pipefail

BACKUP_DIR=""
DATA_DIR="./data"
DB_PATH=""
PG_CONN=""

usage() {
    cat <<'USAGE'
Usage: ./restore.sh --backup <dir> [options]

  --backup <dir>     Backup directory created by backup.sh (required)
  --data-dir <dir>   Directory containing knowledgehub.db (default: ./data)
  --db <path>        Explicit database path (overrides --data-dir)
  --pg <conn>        PostgreSQL mode: restore knowledgehub.pg.dump into the
                     given libpq conninfo/URI (pg_restore --clean --if-exists)
  --no-restart       Do not restart the service after restoring
  -h, --help         Show this help

The service is stopped before the restore and started afterwards when
--no-restart is not given.
USAGE
}

NO_RESTART=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --backup)     BACKUP_DIR="${2:?--backup requires a value}"; shift 2 ;;
        --data-dir)   DATA_DIR="${2:?--data-dir requires a value}"; shift 2 ;;
        --db)         DB_PATH="${2:?--db requires a value}"; shift 2 ;;
        --pg)         PG_CONN="${2:?--pg requires a value}"; shift 2 ;;
        --no-restart) NO_RESTART=1; shift ;;
        -h|--help)    usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ -n "$PG_CONN" ]]; then
    if [[ -z "$BACKUP_DIR" || ! -f "$BACKUP_DIR/knowledgehub.pg.dump" ]]; then
        echo "error: --pg needs a backup dir containing knowledgehub.pg.dump" >&2
        exit 1
    fi
    if ! command -v pg_restore >/dev/null 2>&1; then
        echo "error: --pg requires postgresql-client (pg_restore)" >&2
        exit 1
    fi
elif [[ -z "$BACKUP_DIR" || ! -f "$BACKUP_DIR/knowledgehub.db" ]]; then
    echo "error: --backup must point at a backup dir containing knowledgehub.db" >&2
    exit 1
fi

DB_PATH="${DB_PATH:-$DATA_DIR/knowledgehub.db}"

stop_service() {
    if command -v docker >/dev/null 2>&1 && docker ps -a --format '{{.Names}}' | grep -qx knowledgehub; then
        echo "==> Stopping docker container knowledgehub"
        # RF-703 (SPEC-20260926-review-backlog-remediation): a FAILED stop must
        # abort the restore — overwriting a live SQLite risks corruption.
        if ! docker stop knowledgehub; then
            echo "ERROR: could not stop container — refusing to restore over a live database" >&2
            exit 1
        fi
        return 0
    fi
    if command -v systemctl >/dev/null 2>&1 && systemctl list-unit-files knowledgehub.service >/dev/null 2>&1; then
        echo "==> Stopping systemd unit knowledgehub.service"
        if ! sudo systemctl stop knowledgehub.service; then
            echo "ERROR: could not stop service — refusing to restore over a live database" >&2
            exit 1
        fi
        return 0
    fi
    echo "warn: no knowledgehub container/service found — restore is file-only" >&2
    return 1
}

start_service() {
    if command -v docker >/dev/null 2>&1 && docker ps -a --format '{{.Names}}' | grep -qx knowledgehub; then
        echo "==> Starting docker container knowledgehub"
        docker start knowledgehub
        return 0
    fi
    if command -v systemctl >/dev/null 2>&1 && systemctl list-unit-files knowledgehub.service >/dev/null 2>&1; then
        echo "==> Starting systemd unit knowledgehub.service"
        sudo systemctl start knowledgehub.service || true
        return 0
    fi
    return 1
}

STOPPED=0
if stop_service; then
    STOPPED=1
fi

if [[ -n "$PG_CONN" ]]; then
    echo "==> Restoring $BACKUP_DIR/knowledgehub.pg.dump into PostgreSQL"
    pg_restore -d "$PG_CONN" --clean --if-exists --no-owner "$BACKUP_DIR/knowledgehub.pg.dump"
else
    mkdir -p "$(dirname "$DB_PATH")"
    echo "==> Restoring $BACKUP_DIR/knowledgehub.db -> $DB_PATH"
    cp "$BACKUP_DIR/knowledgehub.db" "$DB_PATH"
fi

# SPEC-20260916-firecrawl-mcp-proxy: restore the Data Protection key ring so
# IntegrationSecrets (upstream API keys) remain decryptable.
DP_DIR="$(dirname "$DB_PATH")"
[[ -n "$PG_CONN" ]] && DP_DIR="$DATA_DIR"
if [[ -f "$BACKUP_DIR/dataprotection-keys.tar.gz" ]]; then
    mkdir -p "$DP_DIR"
    echo "==> Restoring Data Protection keys -> $DP_DIR/dataprotection-keys"
    tar -xzf "$BACKUP_DIR/dataprotection-keys.tar.gz" -C "$DP_DIR"
else
    echo "warn: backup has no dataprotection-keys — stored integration secrets won't decrypt" >&2
fi

if [[ $NO_RESTART -eq 0 && $STOPPED -eq 1 ]]; then
    start_service || true
fi

echo "==> Restore complete."
