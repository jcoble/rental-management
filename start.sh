#!/bin/bash
# Rental Command — Start Script
# Starts API (HTTPS 5666, HTTP 5665) + Web (HTTPS 5667), seeds demo data on first run.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
API_DIR="$SCRIPT_DIR/api"
WEB_DIR="$SCRIPT_DIR/web"
DB_FILE="$API_DIR/lifecycle.db"
PIDS_FILE="$SCRIPT_DIR/.pids"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

cleanup() {
    echo ""
    echo -e "${YELLOW}Shutting down...${NC}"
    if [ -f "$PIDS_FILE" ]; then
        while read -r pid; do
            if kill -0 "$pid" 2>/dev/null; then
                kill "$pid" 2>/dev/null || true
            fi
        done < "$PIDS_FILE"
        rm -f "$PIDS_FILE"
    fi
    jobs -p | xargs -r kill 2>/dev/null || true
    echo -e "${GREEN}Done.${NC}"
    exit 0
}

trap cleanup SIGINT SIGTERM EXIT

echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "${BLUE}  Rental Command — Starting Up${NC}"
echo -e "${BLUE}═══════════════════════════════════════════${NC}"

echo -e "${YELLOW}Checking prerequisites...${NC}"
command -v dotnet >/dev/null 2>&1 || { echo -e "${RED}dotnet not found${NC}"; exit 1; }
command -v pnpm >/dev/null 2>&1 || { echo -e "${RED}pnpm not found${NC}"; exit 1; }
echo -e "${GREEN}✓ dotnet and pnpm found${NC}"

if [ ! -d "$WEB_DIR/node_modules" ]; then
    echo -e "${YELLOW}Installing web dependencies...${NC}"
    cd "$WEB_DIR" && pnpm install
fi

if [ ! -d "$API_DIR/bin" ]; then
    echo -e "${YELLOW}Restoring API dependencies...${NC}"
    cd "$API_DIR" && dotnet restore
fi

FIRST_RUN=false
if [ ! -f "$DB_FILE" ]; then
    FIRST_RUN=true
    echo -e "${YELLOW}First run detected — demo seed will run after API starts${NC}"
fi

rm -f "$PIDS_FILE"

API_HTTPS_URL="https://localhost:5666"
API_HTTP_URL="http://localhost:5665"
WEB_URL="https://localhost:5667"
ORIGIN_HEADER="Origin: https://localhost:5667"

echo -e "${BLUE}Starting API on ${API_HTTPS_URL}...${NC}"
cd "$API_DIR"
# dotnet run is noticeably faster/leaner than dotnet watch for local runtime responsiveness.
dotnet run --launch-profile https > "$SCRIPT_DIR/.api.log" 2>&1 &
API_PID=$!
echo "$API_PID" > "$PIDS_FILE"
echo -e "${GREEN}✓ API started (PID: $API_PID)${NC}"

MAX_WAIT=40
WAITED=0
while ! curl -ks "$API_HTTPS_URL/api/portfolios" -H "$ORIGIN_HEADER" >/dev/null 2>&1; do
    sleep 1
    WAITED=$((WAITED + 1))
    if [ $WAITED -ge $MAX_WAIT ]; then
        echo -e "${RED}API failed to start after ${MAX_WAIT}s. Check .api.log${NC}"
        tail -20 "$SCRIPT_DIR/.api.log"
        exit 1
    fi
done
echo -e "${GREEN}✓ API is ready${NC}"

if [ "$FIRST_RUN" = true ]; then
    echo -e "${BLUE}Seeding demo data...${NC}"
    "$SCRIPT_DIR/seed.sh"
    echo -e "${GREEN}✓ Seed complete${NC}"
fi

echo -e "${BLUE}Starting web dev server on port 5667 (HTTPS)...${NC}"
cd "$WEB_DIR"
pnpm dev > "$SCRIPT_DIR/.web.log" 2>&1 &
WEB_PID=$!
echo "$WEB_PID" >> "$PIDS_FILE"
echo -e "${GREEN}✓ Web started (PID: $WEB_PID)${NC}"

WAITED=0
while ! lsof -nP -iTCP:5667 -sTCP:LISTEN | grep -q 5667; do
    sleep 1
    WAITED=$((WAITED + 1))
    if [ $WAITED -ge $MAX_WAIT ]; then
        echo -e "${RED}Web server failed to start after ${MAX_WAIT}s. Check .web.log${NC}"
        tail -20 "$SCRIPT_DIR/.web.log"
        exit 1
    fi
done
echo -e "${GREEN}✓ Web is ready${NC}"

echo ""
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "${GREEN}  Rental Command is running${NC}"
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "  ${BLUE}Web UI:${NC}  ${WEB_URL}"
echo -e "  ${BLUE}API (HTTPS):${NC} ${API_HTTPS_URL}"
echo -e "  ${BLUE}API (HTTP):${NC}  ${API_HTTP_URL}"
echo -e "  ${BLUE}SSE:${NC}     ${API_HTTPS_URL}/api/events?portfolioId=1"
echo ""
echo -e "  ${YELLOW}Press Ctrl+C to stop all services${NC}"

wait
