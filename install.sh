#!/usr/bin/env bash
# install.sh — build, test and deploy KnowledgeHub (SPEC-20260913-install-deploy)
#   ./install.sh                 docker mode (default when docker is on PATH)
#   ./install.sh --docker        force container deploy
#   ./install.sh --host          self-contained binary on the host
#   ./install.sh --host --systemd   + install/enable a systemd unit
set -euo pipefail

IMAGE_NAME="${IMAGE_NAME:-knowledgerag:latest}"
CONTAINER_NAME="${CONTAINER_NAME:-knowledgerag}"
CONTAINER_PORT=8080

# Load .env for vars not already set in the environment — same precedence as
# docker compose: shell env > .env file. Flags (--port) override both.
if [[ -f .env ]]; then
  while IFS='=' read -r key val; do
    [[ "$key" =~ ^[[:space:]]*# || -z "${key// /}" ]] && continue
    key="${key//[[:space:]]/}"
    # RF-701 (SPEC-20260926-review-backlog-remediation): sanitize the value —
    # CR (Windows files), trailing comments, and surrounding quotes are legal
    # in .env files docker compose reads but break `docker run` args here.
    val="${val%$'\r'}"
    val="${val#"${val%%[![:space:]]*}"}"
    val="${val%"${val##*[![:space:]]}"}"
    if [[ "${#val}" -ge 2 && ( "${val:0:1}" == '"' && "${val: -1}" == '"' || "${val:0:1}" == "'" && "${val: -1}" == "'" ) ]]; then
      val="${val:1:${#val}-2}"
    else
      val="${val%% #*}"
    fi
    [[ -z "${!key:-}" ]] && export "$key=$val"
  done < .env
fi

# Same contract as docker-compose.yml: VECTORSTORE_CONNECTIONSTRING wins;
# otherwise it is composed from the discrete POSTGRES_* vars (proxyLLM pattern).
VECTORSTORE_CONNECTIONSTRING="${VECTORSTORE_CONNECTIONSTRING:-Host=${POSTGRES_HOST:-host.docker.internal};Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB:-rag_db};Username=${POSTGRES_USER:-rag_user};Password=${POSTGRES_PASSWORD:-}}"

MODE=""
PORT="${KNOWLEDGEHUB_PORT:-5000}"
PREFIX="/opt/knowledgerag"
DATA_DIR="./data"
SKIP_TESTS=0
SYSTEMD=0

info()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn()  { printf '\033[1;33mWARN:\033[0m %s\n' "$*" >&2; }
die()   { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Usage: ./install.sh [mode] [options]

Modes (default: --docker when docker is installed, otherwise --host):
  --docker          Build the image and run the container locally.
  --host            dotnet publish a self-contained single-file binary and
                    install it under --prefix.

Options:
  --port <p>        Host port to expose (default: KNOWLEDGEHUB_PORT from env/.env, else 5000 → container 8080).
  --data-dir <dir>  SQLite data directory (default: ./data, bind-mounted to /data).
  --prefix <dir>    Install prefix for --host mode (default: /opt/knowledgerag).
  --systemd         (--host only) write + enable + start knowledgerag.service.
  --skip-tests      Skip dotnet build/test gate.
  -h, --help        Show this help.

Env overrides (docker + host modes, shell env > .env): IMAGE_NAME,
               CONTAINER_NAME, KNOWLEDGEHUB_PORT, ALLOWED_HOSTS,
               EMBEDDINGS_*, VECTORSTORE_*, DEEPWIKI_*, FIRECRAWL_*,
               TAVILY_*, CACHE_PROVIDER, REDIS_CONNECTIONSTRING,
               AUTH_ADMIN_INITIAL_PASSWORD, CHAT__*.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --docker)     MODE="docker" ;;
    --host)       MODE="host" ;;
    --port)       PORT="${2:?--port requires a value}"; shift ;;
    --data-dir)   DATA_DIR="${2:?--data-dir requires a value}"; shift ;;
    --prefix)     PREFIX="${2:?--prefix requires a value}"; shift ;;
    --systemd)    SYSTEMD=1 ;;
    --skip-tests) SKIP_TESTS=1 ;;
    -h|--help)    usage; exit 0 ;;
    *) die "Unknown option: $1 (see --help)" ;;
  esac
  shift
done

[[ "$SYSTEMD" == 1 && "$MODE" == "docker" ]] && die "--systemd only applies to --host mode."

# RF-004: pick a mode. Docker wins when available; fall back to host publish.
if [[ -z "$MODE" ]]; then
  if command -v docker >/dev/null 2>&1; then
    MODE="docker"
  else
    warn "docker not found — falling back to --host mode."
    MODE="host"
  fi
fi

require_dotnet() {
  command -v dotnet >/dev/null 2>&1 \
    || die ".NET SDK 10 required (see global.json). Install it or use --docker."
}

# RF-004: shared build/test gate. In docker mode the SDK is optional — the
# multi-stage Dockerfile builds inside the image — so absence only skips tests.
run_gate() {
  [[ "$SKIP_TESTS" == 1 ]] && { info "Skipping build/test (--skip-tests)."; return; }
  if ! command -v dotnet >/dev/null 2>&1; then
    [[ "$MODE" == "host" ]] && require_dotnet
    warn "dotnet SDK not found on host — skipping build/test gate (image build still compiles everything)."
    return
  fi
  # LOCKED_RESTORE=1 enforces committed packages.lock.json (SPEC-20260914-locked-restore).
  if [[ "${LOCKED_RESTORE:-0}" == "1" ]]; then
    dotnet restore KnowledgeHub.slnx --locked-mode
  fi
  info "dotnet build + test"
  dotnet build KnowledgeHub.slnx -c Release
  dotnet test KnowledgeHub.slnx -c Release --no-build
}

check_port() {
  if command -v ss >/dev/null 2>&1 && ss -tlnH "sport = :$PORT" | grep -q .; then
    die "Port $PORT is already in use. Pick another: ./install.sh --$MODE --port <p>"
  fi
}

# Warn when the container's `app` user (uid 1654, shipped in the .NET
# runtime-deps base image) can't write to the bind-mounted data dir —
# that happens when the owner isn't 1654 and the dir isn't world-writable.
check_data_dir() {
  mkdir -p "$DATA_DIR"
  local owner perms
  owner="$(stat -c '%u' "$DATA_DIR" 2>/dev/null || stat -f '%u' "$DATA_DIR")"
  perms="$(stat -c '%a' "$DATA_DIR" 2>/dev/null || stat -f '%Lp' "$DATA_DIR")"
  if [[ "$owner" != "1654" && $((8#$perms & 2)) -eq 0 ]]; then
    # RF-702: auto-fix when we can — a fresh install used to fail at container
    # start because the operator-owned dir is not writable by uid 1654.
    if chown -R 1654:1654 "$DATA_DIR" 2>/dev/null \
       || sudo -n chown -R 1654:1654 "$DATA_DIR" 2>/dev/null; then
      info "$DATA_DIR ownership fixed to uid 1654 (container app user)."
    else
      warn "$DATA_DIR is owned by uid $owner (mode $perms); container runs as uid 1654."
      warn "Fix with: sudo chown -R 1654:1654 $DATA_DIR   (or: chmod 777 $DATA_DIR)"
    fi
  fi
}

docker_deploy() {
  command -v docker >/dev/null 2>&1 || die "docker not found. Use --host instead."
  info "docker build -t $IMAGE_NAME ."
  docker build -t "$IMAGE_NAME" .
  check_data_dir
  local abs_data
  abs_data="$(cd "$DATA_DIR" && pwd)"
  # Replace our own container first so it doesn't trip the port check.
  if docker ps -a --format '{{.Names}}' | grep -qx "$CONTAINER_NAME"; then
    info "Replacing existing container '$CONTAINER_NAME' (data preserved in $abs_data)"
    docker rm -f "$CONTAINER_NAME" >/dev/null
  fi
  check_port
  # SPEC-20260918-install-docker-env-passthrough RF-001: propagate the same
  # env surface docker-compose.yml maps — previously only AllowedHosts made
  # it through, so every documented override was silently inert. Defaults
  # mirror compose; secrets ride inside -e flags and are never printed.
  info "docker run -d -p $PORT:$CONTAINER_PORT -v $abs_data:/data $IMAGE_NAME (env: documented overrides)"
  docker run -d --name "$CONTAINER_NAME" \
    -p "$PORT:$CONTAINER_PORT" \
    -v "$abs_data:/data" \
    -e AllowedHosts="${ALLOWED_HOSTS:-*}" \
    -e "Embeddings__Provider=${EMBEDDINGS_PROVIDER:-deterministic}" \
    -e "Embeddings__Endpoint=${EMBEDDINGS_ENDPOINT:-http://host.docker.internal:11434}" \
    -e "Embeddings__ApiKey=${EMBEDDINGS_APIKEY:-}" \
    -e "Embeddings__Model=${EMBEDDINGS_MODEL:-nomic-embed-text}" \
    -e "Database__Provider=${DATABASE_PROVIDER:-auto}" \
    -e "Database__ConnectionString=${DATABASE_CONNECTIONSTRING:-}" \
    -e "VectorStore__Provider=${VECTORSTORE_PROVIDER:-}" \
    -e "VectorStore__ConnectionString=${VECTORSTORE_CONNECTIONSTRING:-}" \
    -e "DeepWiki__Enabled=${DEEPWIKI_ENABLED:-true}" \
    -e "DeepWiki__Endpoint=${DEEPWIKI_ENDPOINT:-https://mcp.deepwiki.com/mcp}" \
    -e "DeepWiki__PrivateEndpoint=${DEEPWIKI_PRIVATE_ENDPOINT:-https://mcp.devin.ai/mcp}" \
    -e "DeepWiki__ApiKey=${DEEPWIKI_APIKEY:-}" \
    -e "Firecrawl__Enabled=${FIRECRAWL_ENABLED:-true}" \
    -e "Firecrawl__Endpoint=${FIRECRAWL_ENDPOINT:-https://mcp.firecrawl.dev/v2/mcp}" \
    -e "Firecrawl__ApiKey=${FIRECRAWL_APIKEY:-}" \
    -e "Firecrawl__TimeoutSeconds=${FIRECRAWL_TIMEOUT_SECONDS:-300}" \
    -e "Tavily__Enabled=${TAVILY_ENABLED:-true}" \
    -e "Tavily__Endpoint=${TAVILY_ENDPOINT:-https://mcp.tavily.com/mcp}" \
    -e "Tavily__ApiKey=${TAVILY_APIKEY:-}" \
    -e "Tavily__TimeoutSeconds=${TAVILY_TIMEOUT_SECONDS:-120}" \
    -e "Context7__Enabled=${CONTEXT7_ENABLED:-true}" \
    -e "Context7__Endpoint=${CONTEXT7_ENDPOINT:-https://mcp.context7.com/mcp}" \
    -e "Context7__ApiKey=${CONTEXT7_APIKEY:-}" \
    -e "Context7__TimeoutSeconds=${CONTEXT7_TIMEOUT_SECONDS:-60}" \
    -e "Cache__Provider=${CACHE_PROVIDER:-memory}" \
    -e "Cache__Redis__ConnectionString=${REDIS_CONNECTIONSTRING:-}" \
    -e "Auth__AdminInitialPassword=${AUTH_ADMIN_INITIAL_PASSWORD:-123qwe}" \
    -e "Chat__Provider=${CHAT__PROVIDER:-none}" \
    -e "Chat__Endpoint=${CHAT__ENDPOINT:-}" \
    -e "Chat__Model=${CHAT__MODEL:-}" \
    -e "Chat__ApiKey=${CHAT__APIKEY:-}" \
    --add-host host.docker.internal:host-gateway \
    --restart unless-stopped \
    "$IMAGE_NAME"
  info "Done. KnowledgeHub is up at http://localhost:$PORT"
  info "Logs: docker logs -f $CONTAINER_NAME"
}

detect_rid() {
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)            echo "linux-x64" ;;
    Linux-aarch64|Linux-arm64) echo "linux-arm64" ;;
    Darwin-arm64)            echo "osx-arm64" ;;
    Darwin-x86_64)           echo "osx-x64" ;;
    *) die "Unsupported platform: $(uname -s)-$(uname -m)" ;;
  esac
}

sudo_if_needed() {
  if [[ -d "$PREFIX" ]]; then
    # Exists — sudo only when we can't write into it. (mkdir -p on an
    # existing dir exits 0 regardless of writability, so test -w directly.)
    [[ -w "$PREFIX" ]] && echo "" || echo "sudo"
  elif mkdir -p "$PREFIX" 2>/dev/null; then
    echo ""
  else
    echo "sudo"
  fi
}

install_systemd() {
  command -v systemctl >/dev/null 2>&1 || die "systemd not available on this host."
  local abs_data unit user
  abs_data="$(cd "$DATA_DIR" && pwd)"
  user="${SUDO_USER:-$USER}"
  unit="$(cat <<EOF
[Unit]
Description=KnowledgeHub — standalone knowledge platform
After=network.target

[Service]
Type=simple
User=$user
WorkingDirectory=$PREFIX
ExecStart=$PREFIX/KnowledgeHub
Environment=ASPNETCORE_URLS=http://+:$PORT
Environment=KnowledgeHub__DatabasePath=$abs_data/knowledgehub.db
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
)"
  info "Installing /etc/systemd/system/knowledgerag.service (user: $user)"
  echo "$unit" | sudo tee /etc/systemd/system/knowledgerag.service >/dev/null
  sudo systemctl daemon-reload
  sudo systemctl enable --now knowledgerag.service
  info "systemctl status knowledgerag — http://localhost:$PORT"
}

host_deploy() {
  require_dotnet
  check_port
  local rid sudo_cmd
  rid="$(detect_rid)"
  info "dotnet publish -r $rid"
  dotnet publish src/KnowledgeHub.Server -c Release -r "$rid" -o "publish/$rid"
  sudo_cmd="$(sudo_if_needed)"
  # shellcheck disable=SC2086
  $sudo_cmd mkdir -p "$PREFIX"
  # The SPA's static web assets (wwwroot/, *.staticwebassets.endpoints.json) are
  # NOT embedded in the single-file binary — they must sit beside it.
  # shellcheck disable=SC2086
  $sudo_cmd install -m 755 "publish/$rid/KnowledgeHub" "$PREFIX/KnowledgeHub"
  # shellcheck disable=SC2086
  $sudo_cmd cp -rf "publish/$rid/wwwroot" "$PREFIX/"
  # shellcheck disable=SC2086
  $sudo_cmd cp -f "publish/$rid/"*.staticwebassets.endpoints.json "$PREFIX/"
  # shellcheck disable=SC2086
  $sudo_cmd cp -f "publish/$rid/appsettings.json" "$PREFIX/" 2>/dev/null || true
  mkdir -p "$DATA_DIR"
  info "Installed $PREFIX/KnowledgeHub (+ wwwroot, appsettings)"
  if [[ "$SYSTEMD" == 1 ]]; then
    install_systemd
  else
    info "Run: KnowledgeHub__DatabasePath=$(cd "$DATA_DIR" && pwd)/knowledgehub.db ASPNETCORE_URLS=http://+:$PORT $PREFIX/KnowledgeHub" # NOSONAR S5332 — URL de bind local exibida ao usuário, não chamada de rede
    info "Or re-run with --systemd to install the service."
  fi
}

info "KnowledgeHub install — mode: $MODE, port: $PORT, data: $DATA_DIR"
run_gate
case "$MODE" in
  docker) docker_deploy ;;
  host)   host_deploy ;;
esac
