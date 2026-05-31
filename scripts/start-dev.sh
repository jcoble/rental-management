#!/usr/bin/env bash
# Rental Command — local development launcher.
#
# Starts the full replatformed stack on the host (no app containers):
#   - PostgreSQL : Docker container `rentalcommand-dev-db` on localhost:5434
#   - API        : RentalCommand.Api   (https://localhost:5666, http://localhost:5665)
#   - Engine     : RentalCommand.Engine (background worker, no HTTP port)
#   - Web        : SvelteKit dev server (https://localhost:5667)
#
# Usage: ./scripts/start-dev.sh
# Press Ctrl+C to stop the .NET/web processes. The Postgres container is left
# running (use `docker stop rentalcommand-dev-db` to stop it).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT_DIR"

# ─── Config (override via env) ───────────────────────────────────────────────
PG_CONTAINER="${PG_CONTAINER:-rentalcommand-dev-db}"
PG_PORT="${PG_PORT:-5434}"          # host port; matches the dev DB convention
PG_USER="${PG_USER:-postgres}"
PG_PASSWORD="${PG_PASSWORD:-postgres}"
PG_DB="${PG_DB:-rentalcommand}"

API_HTTPS_URL="${API_HTTPS_URL:-https://localhost:5666}"
API_HTTP_URL="${API_HTTP_URL:-http://localhost:5665}"
WEB_PORT="${WEB_PORT:-5667}"
WEB_URL="${WEB_URL:-https://localhost:$WEB_PORT}"

CONN_STR="Host=localhost;Port=$PG_PORT;Database=$PG_DB;Username=$PG_USER;Password=$PG_PASSWORD"

API_PID=""
ENGINE_PID=""
WEB_PID=""

cleanup() {
    echo ""
    echo "Stopping API / Engine / Web (Postgres container left running)..."
    [ -n "$WEB_PID" ]    && kill "$WEB_PID"    2>/dev/null || true
    [ -n "$ENGINE_PID" ] && kill "$ENGINE_PID" 2>/dev/null || true
    [ -n "$API_PID" ]    && kill "$API_PID"    2>/dev/null || true
}
trap cleanup INT TERM EXIT

# ─── Prerequisites ───────────────────────────────────────────────────────────
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found"; exit 1; }
command -v pnpm   >/dev/null 2>&1 || { echo "pnpm not found";   exit 1; }
command -v docker >/dev/null 2>&1 || { echo "docker not found"; exit 1; }

# ─── PostgreSQL ──────────────────────────────────────────────────────────────
# Reuse whatever Postgres is already listening on :$PG_PORT (a container under a
# different name like `rc-postgres`, or a host install) instead of trying to bind
# the port a second time — that double-bind was a hard crash.
pg_port_in_use() { (exec 3<>"/dev/tcp/localhost/$PG_PORT") 2>/dev/null && { exec 3>&-; return 0; } || return 1; }

if pg_port_in_use; then
    echo "Postgres already listening on :$PG_PORT — reusing it (leaving container management alone)."
else
    if docker ps -a --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
        echo "Starting existing Postgres container '$PG_CONTAINER'..."
        docker start "$PG_CONTAINER" >/dev/null
    else
        echo "Creating Postgres container '$PG_CONTAINER' on :$PG_PORT..."
        docker run -d \
            --name "$PG_CONTAINER" \
            -e POSTGRES_USER="$PG_USER" \
            -e POSTGRES_PASSWORD="$PG_PASSWORD" \
            -e POSTGRES_DB="$PG_DB" \
            -p "$PG_PORT:5432" \
            -v rentalcommand_dev_pgdata:/var/lib/postgresql/data \
            postgres:16-alpine >/dev/null
    fi
    printf "Waiting for Postgres"
    for _ in $(seq 1 60); do
        if docker exec "$PG_CONTAINER" pg_isready -U "$PG_USER" -d "$PG_DB" >/dev/null 2>&1; then
            printf " ready\n"
            break
        fi
        printf "."
        sleep 1
    done
fi

# ─── .NET environment ────────────────────────────────────────────────────────
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_ENVIRONMENT=Development
export ConnectionStrings__DefaultConnection="$CONN_STR"

# ─── Engine (background worker) ──────────────────────────────────────────────
echo "Starting Engine (RentalCommand.Engine)..."
dotnet run --project RentalCommand.Engine > /tmp/rentalcommand-engine.log 2>&1 &
ENGINE_PID=$!

# ─── API ─────────────────────────────────────────────────────────────────────
echo "Starting API on $API_HTTPS_URL..."
dotnet run --project RentalCommand.Api -- --urls "$API_HTTPS_URL;$API_HTTP_URL" \
    > /tmp/rentalcommand-api.log 2>&1 &
API_PID=$!

printf "Waiting for API"
for _ in $(seq 1 60); do
    if curl -ks -o /dev/null "$API_HTTPS_URL/health"; then
        printf " ready\n"
        break
    fi
    printf "."
    sleep 1
done

# ─── Web (SvelteKit dev server, foreground) ──────────────────────────────────
if [ ! -d "web/node_modules" ]; then
    echo "Installing web dependencies..."
    (cd web && pnpm install --frozen-lockfile)
fi

echo ""
echo "==============================================="
echo "  Rental Command — dev stack running"
echo "==============================================="
echo "  Web UI : $WEB_URL"
echo "  API    : $API_HTTPS_URL  (http: $API_HTTP_URL)"
echo "  Engine : background worker (log: /tmp/rentalcommand-engine.log)"
echo "  DB     : localhost:$PG_PORT/$PG_DB"
echo ""
echo "  Press Ctrl+C to stop API/Engine/Web."
echo ""

# Point the web client at the API's plain-HTTP URL for local dev so we never
# need to disable TLS verification for the self-signed dev cert. Override
# VITE_API_URL if you want the web app to talk to the HTTPS endpoint (in which
# case add the dev cert's CA to your trust store rather than disabling TLS).
cd web
VITE_API_URL="${VITE_API_URL:-$API_HTTP_URL}" \
    pnpm dev --host localhost --port "$WEB_PORT" &
WEB_PID=$!

wait "$WEB_PID"
