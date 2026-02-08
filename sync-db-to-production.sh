#!/bin/bash
# Sync local SQLite DB to Hetzner production
# Usage: ./sync-db-to-production.sh

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

if [ ! -f "$LOCAL_DB" ]; then
    echo -e "${RED}Local DB not found at $LOCAL_DB${NC}"
    exit 1
fi

echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo -e "${BLUE}  Sync Local DB → Hetzner Production${NC}"
echo -e "${BLUE}═══════════════════════════════════════════${NC}"
echo ""

# Show local DB stats
LOCAL_SIZE=$(du -h "$LOCAL_DB" | cut -f1)
echo -e "${YELLOW}Local DB: $LOCAL_DB ($LOCAL_SIZE)${NC}"

# Backup remote DB first
echo -e "${YELLOW}Backing up remote DB...${NC}"
BACKUP_NAME="lifecycle-backup-$(date +%Y%m%d-%H%M%S).db"
ssh "$REMOTE_HOST" "docker cp $CONTAINER:/app/data/lifecycle.db /tmp/$BACKUP_NAME 2>/dev/null && echo 'Backup: /tmp/$BACKUP_NAME' || echo 'No existing DB to backup'"

# Stop the API container briefly to avoid DB locks
echo -e "${YELLOW}Stopping API container...${NC}"
ssh "$REMOTE_HOST" "docker stop $CONTAINER"

# Copy local DB to remote
echo -e "${YELLOW}Copying local DB to remote...${NC}"
scp "$LOCAL_DB" "$REMOTE_HOST:/tmp/lifecycle-sync.db"

# Copy into the Docker volume
ssh "$REMOTE_HOST" "docker cp /tmp/lifecycle-sync.db $CONTAINER:/app/data/lifecycle.db && rm /tmp/lifecycle-sync.db"

# Restart API
echo -e "${YELLOW}Restarting API container...${NC}"
ssh "$REMOTE_HOST" "docker start $CONTAINER"

# Wait for API to come back
echo -e "${YELLOW}Waiting for API...${NC}"
sleep 3
READY=false
for i in $(seq 1 15); do
    if ssh "$REMOTE_HOST" "curl -s -o /dev/null -w '%{http_code}' http://localhost:5556/api/projects" 2>/dev/null | grep -q "200"; then
        READY=true
        break
    fi
    sleep 1
done

if [ "$READY" = true ]; then
    echo -e "${GREEN}✓ Production API is back up${NC}"
else
    echo -e "${RED}API may not have restarted cleanly — check logs${NC}"
fi

echo ""
echo -e "${GREEN}Done! Remote backup: /tmp/$BACKUP_NAME${NC}"
echo -e "${BLUE}Production: https://cloud-server.chimp-map.ts.net${NC}"
