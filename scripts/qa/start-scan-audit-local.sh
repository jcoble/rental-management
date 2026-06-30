#!/usr/bin/env bash
# Start an isolated local stack for the TSK-397 production-scale scan audit.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$ROOT_DIR"

export PG_CONTAINER="${PG_CONTAINER:-rentalcommand-tsk397-db}"
export PG_PORT="${PG_PORT:-5742}"
export PG_USER="${PG_USER:-postgres}"
export PG_PASSWORD="${PG_PASSWORD:-postgres}"
export PG_DB="${PG_DB:-rentalcommand_tsk397}"

export API_HTTPS_URL="${API_HTTPS_URL:-https://localhost:5696}"
export API_HTTP_URL="${API_HTTP_URL:-http://localhost:5695}"
export WEB_API_URL="${WEB_API_URL:-https://localhost:5696}"
export WEB_PORT="${WEB_PORT:-5697}"
export WEB_HOST="${WEB_HOST:-localhost}"
export WEB_PUBLIC_HOST="${WEB_PUBLIC_HOST:-localhost}"

export ConnectionStrings__DefaultConnection="Host=localhost;Port=$PG_PORT;Database=$PG_DB;Username=$PG_USER;Password=$PG_PASSWORD"
export Cors__AllowedOrigins__0="https://localhost:$WEB_PORT"
export Seed__Enabled="${Seed__Enabled:-true}"
export Seed__DemoData="${Seed__DemoData:-false}"
export Assistant__Provider="${Assistant__Provider:-claude-cli}"
export Assistant__ModelId="${Assistant__ModelId:-sonnet}"
export Assistant__ImageDetail="${Assistant__ImageDetail:-low}"
export Assistant__UseImageOcr="${Assistant__UseImageOcr:-false}"
export Auth__ExposeDevTokens="${Auth__ExposeDevTokens:-true}"

echo "TSK-397 local audit stack"
echo "  Web:  https://localhost:$WEB_PORT"
echo "  API:  $API_HTTPS_URL"
echo "  DB:   localhost:$PG_PORT/$PG_DB ($PG_CONTAINER)"
echo "  LLM:  $Assistant__Provider / $Assistant__ModelId"
echo ""
echo "This wrapper uses an isolated local Postgres container/database and disables demo data."
echo "It does not blank notification/auth/AI provider secrets; user-secrets and explicit env overrides still apply."
echo ""

exec "$ROOT_DIR/scripts/start-dev.sh"
