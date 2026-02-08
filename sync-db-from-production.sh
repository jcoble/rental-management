#!/bin/bash
# Pull production DB from Hetzner to local dev
# Usage: ./sync-db-from-production.sh

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
LOCAL_DB="$SCRIPT_DIR/api/lifecycle.db"
CONTAINER="lifecycle-tracker-api-1"
REMOTE_HOST="hetzner-claude"

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "${BLUE}  Pull Production DB → Local Dev${NC}"
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo ""

# Backup local DB if it exists
if [ -f "$LOCAL_DB" ]; then
    BACKUP="$LOCAL_DB.backup-$(date +%Y%m%d-%H%M%S)"
    cp "$LOCAL_DB" "$BACKUP"
    echo -e "${YELLOW}Local backup: $BACKUP${NC}"
fi

# Copy from Docker volume on Hetzner
echo -e "${YELLOW}Copying production DB...${NC}"
ssh "$REMOTE_HOST" "docker cp $CONTAINER:/app/data/lifecycle.db /tmp/lifecycle-pull.db"
scp "$REMOTE_HOST:/tmp/lifecycle-pull.db" "$LOCAL_DB"
ssh "$REMOTE_HOST" "rm /tmp/lifecycle-pull.db"

PULLED_SIZE=$(du -h "$LOCAL_DB" | cut -f1)
echo -e "${GREEN}✓ Pulled production DB ($PULLED_SIZE)${NC}"
echo ""
echo -e "${YELLOW}Restart local API to pick up new DB${NC}"
echo -e "  Kill the running process and run ./start.sh again"
