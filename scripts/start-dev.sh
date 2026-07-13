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

# Arm the local git hooks (free, $0 — these replace CI). Idempotent + quiet.
if [ "$(git config --get core.hooksPath 2>/dev/null || true)" != "scripts/hooks" ]; then
    ./scripts/install-git-hooks.sh >/dev/null 2>&1 || true
fi

# ─── Config (override via env) ───────────────────────────────────────────────
PG_CONTAINER="${PG_CONTAINER:-rentalcommand-dev-db}"
PG_PORT="${PG_PORT:-5432}"          # shared dev Postgres (e.g. edi-postgres); reused if already up
PG_USER="${PG_USER:-postgres}"
PG_PASSWORD="${PG_PASSWORD:-postgres}"
PG_DB="${PG_DB:-rentalcommand}"

API_HTTPS_URL="${API_HTTPS_URL:-https://localhost:5666}"
API_HTTP_URL="${API_HTTP_URL:-http://localhost:5665}"
# The web's SERVER-SIDE (SSR + Vite proxy) target for the API. This is DECOUPLED
# from API_HTTPS_URL on purpose: the API may bind to 0.0.0.0 (so the phone can
# reach it over the LAN), but the mkcert TLS cert only covers localhost/127.0.0.1/::1.
# Node enforces cert hostname verification, so the web must always reach the API at
# a cert-valid host. Defaults to localhost; override only for a genuinely remote API.
WEB_API_URL="${WEB_API_URL:-https://localhost:5666}"
WEB_PORT="${WEB_PORT:-5667}"
LAN_DEV="${LAN_DEV:-0}"
LAN_IP="${LAN_IP:-}"
if [ "$LAN_DEV" = "1" ] && [ -z "$LAN_IP" ]; then
    LAN_IP="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)"
fi
# Web dev-server bind host. Default localhost; set LAN_DEV=1 or WEB_HOST=0.0.0.0
# to serve the web UI over the LAN.
WEB_HOST="${WEB_HOST:-$([ "$LAN_DEV" = "1" ] && echo "0.0.0.0" || echo "localhost")}"
WEB_PUBLIC_HOST="${WEB_PUBLIC_HOST:-$([ "$LAN_DEV" = "1" ] && echo "${LAN_IP:-localhost}" || echo "localhost")}"
WEB_URL="${WEB_URL:-https://$WEB_PUBLIC_HOST:$WEB_PORT}"
export App__WebBaseUrl="${App__WebBaseUrl:-$WEB_URL}"

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
command -v mkcert >/dev/null 2>&1 || { echo "mkcert not found (brew install mkcert && mkcert -install) — required for HTTPS dev"; exit 1; }

# ─── HTTPS dev certificates (mkcert) ─────────────────────────────────────────
# Web + API are served over HTTPS with locally-trusted mkcert certs. The mkcert
# root CA is a real CA, so Node (SSR + the Vite proxy) trusts the API cert via
# NODE_EXTRA_CA_CERTS — no TLS verification is ever disabled.
CERT_DIR="$ROOT_DIR/web/.cert"
mkdir -p "$CERT_DIR"
WEB_CERT_HOSTS="localhost 127.0.0.1 ::1"
if [ "$LAN_DEV" = "1" ] && [ -n "$LAN_IP" ]; then
    WEB_CERT_HOSTS="$WEB_CERT_HOSTS $LAN_IP"
fi
WEB_CERT_STAMP="$CERT_DIR/web-cert-hosts.txt"
[ -f "$CERT_DIR/cert.pem" ] && [ -f "$CERT_DIR/key.pem" ] && [ -f "$WEB_CERT_STAMP" ] && [ "$(cat "$WEB_CERT_STAMP")" = "$WEB_CERT_HOSTS" ] || {
    echo "Generating web mkcert cert..."
    mkcert -cert-file "$CERT_DIR/cert.pem" -key-file "$CERT_DIR/key.pem" $WEB_CERT_HOSTS >/dev/null 2>&1
    echo "$WEB_CERT_HOSTS" > "$WEB_CERT_STAMP"
}
[ -f "$CERT_DIR/api-cert.pem" ] && [ -f "$CERT_DIR/api-key.pem" ] || {
    echo "Generating API mkcert cert..."
    mkcert -cert-file "$CERT_DIR/api-cert.pem" -key-file "$CERT_DIR/api-key.pem" localhost 127.0.0.1 ::1 >/dev/null 2>&1
}
export NODE_EXTRA_CA_CERTS="$(mkcert -CAROOT)/rootCA.pem"

# ─── PostgreSQL ──────────────────────────────────────────────────────────────
# Reuse whatever Postgres is already listening on :$PG_PORT (a container under a
# different name like `rc-postgres`, or a host install) instead of trying to bind
# the port a second time — that double-bind was a hard crash.
pg_port_in_use() { (exec 3<>"/dev/tcp/localhost/$PG_PORT") 2>/dev/null && { exec 3>&-; return 0; } || return 1; }

sql_literal() {
    local value="${1//\'/\'\'}"
    printf "'%s'" "$value"
}

ensure_database_exists() {
    local db_literal
    local db_exists

    db_literal="$(sql_literal "$PG_DB")"

    if docker ps --format '{{.Names}}' | grep -qx "$PG_CONTAINER"; then
        db_exists="$(docker exec "$PG_CONTAINER" psql -U "$PG_USER" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = $db_literal;" | tr -d '[:space:]')"
        if [ "$db_exists" != "1" ]; then
            echo "Creating database '$PG_DB' in Postgres container '$PG_CONTAINER'..."
            docker exec "$PG_CONTAINER" createdb -U "$PG_USER" "$PG_DB"
        fi
        return
    fi

    command -v psql >/dev/null 2>&1 || {
        echo "psql not found; cannot verify database '$PG_DB' exists on localhost:$PG_PORT"
        exit 1
    }
    command -v createdb >/dev/null 2>&1 || {
        echo "createdb not found; cannot create database '$PG_DB' on localhost:$PG_PORT"
        exit 1
    }

    db_exists="$(PGPASSWORD="$PG_PASSWORD" psql -h localhost -p "$PG_PORT" -U "$PG_USER" -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = $db_literal;" | tr -d '[:space:]')"
    if [ "$db_exists" != "1" ]; then
        echo "Creating database '$PG_DB' on localhost:$PG_PORT..."
        PGPASSWORD="$PG_PASSWORD" createdb -h localhost -p "$PG_PORT" -U "$PG_USER" "$PG_DB"
    fi
}

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

ensure_database_exists

# ─── .NET environment ────────────────────────────────────────────────────────
# The owner/migrator DB credential comes from .NET User Secrets in Development. Runtime
# connections immediately SET ROLE to their NOLOGIN API/Engine role. Do NOT export
# ConnectionStrings__DefaultConnection here or it would override the secret. Set
# it once with:
#   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<conn>" --project RentalCommand.Api
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_ENVIRONMENT=Development
export MSBUILDDISABLENODEREUSE="${MSBUILDDISABLENODEREUSE:-1}"

# A blank environment variable has higher priority than .NET User Secrets. QA wrappers sometimes
# clear provider settings by exporting empty Notifications__* values; if that leaks into this normal
# dev launcher, email/SMS/Google config looks intentionally disabled even when user-secrets are set.
# Keep real explicit overrides, but remove blank ones so Development user-secrets can load.
unset_if_blank() {
    local key="$1"
    if [ "${!key+x}" = "x" ] && [ -z "${!key}" ]; then
        unset "$key"
    fi
}
unset_if_blank ConnectionStrings__DefaultConnection
unset_if_blank ConnectionStrings__MigratorConnection
unset_if_blank Notifications__SendGrid__ApiKey
unset_if_blank Notifications__SendGrid__FromEmail
unset_if_blank Notifications__SendGrid__FromName
unset_if_blank Notifications__Email__Transport
unset_if_blank Notifications__Smtp__Host
unset_if_blank Notifications__Smtp__Username
unset_if_blank Notifications__Smtp__Password
unset_if_blank Notifications__Smtp__FromEmail
unset_if_blank Notifications__SignalWire__ProjectId
unset_if_blank Notifications__SignalWire__Token
unset_if_blank Notifications__SignalWire__SpaceUrl
unset_if_blank Notifications__SignalWire__FromNumber
unset_if_blank Notifications__Twilio__AccountSid
unset_if_blank Notifications__Twilio__AuthToken
unset_if_blank Notifications__Twilio__FromNumber
unset_if_blank Notifications__Telnyx__ApiKey
unset_if_blank Notifications__Telnyx__FromNumber
unset_if_blank Notifications__Vonage__ApiKey
unset_if_blank Notifications__Vonage__ApiSecret
unset_if_blank Notifications__Vonage__FromNumber
unset_if_blank Authentication__Google__ClientId
unset_if_blank Authentication__Google__ClientSecret
unset_if_blank GooglePlaces__ApiKey
unset_if_blank Assistant__ApiKey
unset_if_blank Assistant__GeminiApiKeyBackup
unset_if_blank Assistant__ModelId
unset_if_blank Assistant__Provider
unset_if_blank Plaid__AndroidPackageName
unset_if_blank Plaid__ClientId
unset_if_blank Plaid__Environment
unset_if_blank Plaid__RedirectUri
unset_if_blank Plaid__Secret
unset_if_blank QuickBooks__ClientId
unset_if_blank QuickBooks__ClientSecret
unset_if_blank QuickBooks__Environment
unset_if_blank QuickBooks__RedirectUri
unset_if_blank PlatformAdmin__Emails__0

# Serve the API's HTTPS endpoint with the locally-trusted mkcert cert.
export Kestrel__Certificates__Default__Path="$CERT_DIR/api-cert.pem"
export Kestrel__Certificates__Default__KeyPath="$CERT_DIR/api-key.pem"
# Shared upload directory (ABSOLUTE). The API and Engine are separate processes with
# different working directories, so a relative "./uploads" resolves to two different
# folders and the Engine can't read what the API wrote (scan extraction then fails).
# Pin BOTH to one absolute path so the blob written on upload is the blob the worker reads.
export Upload__BasePath="$ROOT_DIR/uploads"

# Build the two .NET hosts serially before launching them. A fresh worktree can otherwise
# run API + Engine first-builds at the same time and race on shared project outputs.
echo "Building .NET hosts serially..."
dotnet build RentalCommand.Engine
dotnet build RentalCommand.Api

# ─── Engine (background worker) ──────────────────────────────────────────────
echo "Starting Engine (RentalCommand.Engine)..."
dotnet run --no-build --project RentalCommand.Engine > /tmp/rentalcommand-engine.log 2>&1 &
ENGINE_PID=$!

# ─── API ─────────────────────────────────────────────────────────────────────
echo "Starting API on $API_HTTPS_URL..."
dotnet run --no-build --project RentalCommand.Api -- --urls "$API_HTTPS_URL;$API_HTTP_URL" \
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
if [ "$LAN_DEV" = "1" ]; then
echo ""
echo "  LAN mode is enabled. Open this on your phone: $WEB_URL"
echo "  If the phone rejects HTTPS, install/trust the mkcert root CA from: $(mkcert -CAROOT)/rootCA.pem"
fi
echo ""
echo "  Press Ctrl+C to stop API/Engine/Web."
echo ""

# SSR reaches the API over HTTPS at $API_URL; Node trusts the mkcert cert via
# NODE_EXTRA_CA_CERTS (set above). The browser uses the same-origin /api/v1 path
# that the Vite proxy forwards to $API_URL. API_URL is the API ROOT — server
# config appends /api/v1; do NOT set VITE_API_URL (it would push the browser to
# cross-origin calls and drop the /api/v1 prefix on the server side).
# Google sign-in (optional): surface the PUBLIC client id to the web FROM .NET user-secrets,
# so the login button renders + the OAuth redirect works WITHOUT a .env file. The ClientSecret
# stays server-side in the API's user-secrets; only the (public) client id reaches the browser.
# (web reads it via $env/dynamic/public at runtime.) Empty if Google isn't configured → button hidden.
export PUBLIC_GOOGLE_CLIENT_ID="${PUBLIC_GOOGLE_CLIENT_ID:-$(dotnet user-secrets list --project RentalCommand.Api 2>/dev/null | sed -n 's/^Authentication:Google:ClientId = //p')}"

cd web
API_URL="${API_URL:-$WEB_API_URL}" \
    pnpm dev --host "$WEB_HOST" --port "$WEB_PORT" &
WEB_PID=$!

wait "$WEB_PID"
