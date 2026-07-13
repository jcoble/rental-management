#!/usr/bin/env bash
set -euo pipefail

action="${1:?action is required}"
stack_id="${2:?stack id is required}"
ttl_hours="${3:-4}"
artifact_dir="${4:?artifact directory is required}"
preview_root="${PREVIEW_ROOT:-/srv/dev-stacks/rental-command}"
preview_data_root="${PREVIEW_DATA_ROOT:-/srv/dev-stack-data/rental-command}"
preview_port="${PREVIEW_WEB_PORT:-15667}"
state_dir="$preview_root/$stack_id"
data_dir="$preview_data_root/$stack_id"
credentials_file="$data_dir/credentials.env"
project="rc-preview-$stack_id"

mkdir -p "$artifact_dir" "$preview_root" "$preview_data_root"
[[ "$stack_id" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]] || exit 2
[[ "$ttl_hours" =~ ^(1|2|4|8|12)$ ]] || exit 2
[[ "$preview_port" =~ ^[0-9]+$ ]] || exit 2

compose() {
  docker compose --project-name "$project" --env-file "$state_dir/.env" \
    -f "$state_dir/source/deploy/docker-compose.preview.yml" "$@"
}

capture_logs() {
  if [[ -f "$state_dir/.env" ]]; then
    compose ps > "$artifact_dir/compose-ps.txt" 2>&1 || true
    compose logs --no-color --timestamps > "$artifact_dir/compose.log" 2>&1 || true
  fi
}

stop_stack() {
  if [[ -f "$state_dir/.env" ]]; then
    capture_logs
    # Never remove volumes here. Normal stop, replacement, and TTL cleanup are
    # intentionally non-destructive so an interactive preview keeps its data.
    compose down --remove-orphans || true
    # Images are unique to this preview, so removing them does not affect production.
    docker image rm "rc-preview-api:$stack_id" "rc-preview-engine:$stack_id" \
      "rc-preview-web:$stack_id" >/dev/null 2>&1 || true
  fi
  rm -rf "$state_dir"
}

reset_data() {
  stop_stack
  # This is the only lifecycle path allowed to remove persistent preview data.
  # PostgreSQL owns its files inside the bind mount, so delete them as container
  # root instead of requiring passwordless sudo for the Actions runner.
  if [[ -d "$data_dir" ]]; then
    docker run --rm --entrypoint sh \
      -v "$data_dir:/preview-data" postgres:16-alpine \
      -c 'find /preview-data -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +'
    rmdir "$data_dir" 2>/dev/null || true
  fi
  docker volume rm "${project}_uploads" "${project}_dataprotection_keys" \
    >/dev/null 2>&1 || true
}

cleanup_expired() {
  local candidate expires id
  shopt -s nullglob
  for candidate in "$preview_root"/*; do
    [[ -d "$candidate" && -f "$candidate/expires-at" ]] || continue
    expires="$(cat "$candidate/expires-at")"
    [[ "$expires" =~ ^[0-9]+$ ]] || continue
    (( expires <= $(date +%s) )) || continue
    id="$(basename "$candidate")"
    PREVIEW_ROOT="$preview_root" "$0" stop "$id" 1 "$artifact_dir/expired-$id" || true
  done
}

case "$action" in
  cleanup)
    cleanup_expired
    exit 0
    ;;
  stop)
    stop_stack
    exit 0
    ;;
  reset-data)
    reset_data
    exit 0
    ;;
  logs)
    capture_logs
    exit 0
    ;;
  health)
    private_url="$(sed -n 's/^private_url=//p' "$state_dir/metadata.txt")"
    curl --fail --show-error --silent "$private_url/health" \
      | tee "$artifact_dir/health.json"
    exit 0
    ;;
  status)
    compose ps | tee "$artifact_dir/compose-ps.txt"
    cat "$state_dir/metadata.txt" | tee "$artifact_dir/metadata.txt"
    exit 0
    ;;
  start) ;;
  *) echo "Unknown action: $action" >&2; exit 2 ;;
esac

cleanup_expired

# The server is intentionally sized for one Rental Command preview. Refuse a
# second name instead of producing ambiguous port collisions or memory pressure.
for other in "$preview_root"/*; do
  [[ -d "$other" ]] || continue
  [[ "$(basename "$other")" == "$stack_id" ]] || {
    echo "Another Rental Command preview is active: $(basename "$other")" >&2
    exit 1
  }
done

# Starting the same name is an explicit replace/update operation.
stop_stack
mkdir -p "$state_dir/source"
git archive HEAD | tar -x -C "$state_dir/source"

resolved_sha="${RESOLVED_SHA:-$(git rev-parse HEAD)}"
tailscale_host="$(tailscale ip -4 2>/dev/null | head -n1)"
[[ "$tailscale_host" =~ ^100\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
  echo "Tailscale is not connected or did not return a tailnet IPv4 address" >&2
  exit 1
}
web_origin="http://$tailscale_host:$preview_port"
expires_at="$(( $(date +%s) + ttl_hours * 3600 ))"
mkdir -p "$data_dir/postgres"
if [[ ! -f "$credentials_file" ]]; then
  postgres_password="$(openssl rand -hex 24)"
  jwt_secret="$(openssl rand -base64 48 | tr -d '\n')"
  api_db_password="$(openssl rand -hex 24)"
  engine_db_password="$(openssl rand -hex 24)"
  cat > "$credentials_file" <<EOF
POSTGRES_PASSWORD=$postgres_password
JWT_SECRET_KEY=$jwt_secret
API_DB_PASSWORD=$api_db_password
ENGINE_DB_PASSWORD=$engine_db_password
EOF
  chmod 600 "$credentials_file"
fi

# shellcheck disable=SC1090
source "$credentials_file"
: "${POSTGRES_PASSWORD:?persistent preview Postgres password is required}"
: "${JWT_SECRET_KEY:?persistent preview JWT secret is required}"
if [[ -z "${API_DB_PASSWORD:-}" || -z "${ENGINE_DB_PASSWORD:-}" ]]; then
  API_DB_PASSWORD="$(openssl rand -hex 24)"
  ENGINE_DB_PASSWORD="$(openssl rand -hex 24)"
  cat >> "$credentials_file" <<EOF
API_DB_PASSWORD=$API_DB_PASSWORD
ENGINE_DB_PASSWORD=$ENGINE_DB_PASSWORD
EOF
fi

# postgres:16-alpine runs as its postgres user. Preparing the bind mount through
# the same image avoids relying on host account mappings. Existing database
# contents already have the correct ownership, so this does not recursively
# rewrite a potentially large preserved database.
docker run --rm --entrypoint sh \
  -v "$data_dir/postgres:/var/lib/postgresql/data" postgres:16-alpine \
  -c 'chown postgres:postgres /var/lib/postgresql/data && chmod 700 /var/lib/postgresql/data'

cat > "$state_dir/.env" <<EOF
API_IMAGE=rc-preview-api:$stack_id
ENGINE_IMAGE=rc-preview-engine:$stack_id
WEB_IMAGE=rc-preview-web:$stack_id
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
JWT_SECRET_KEY=$JWT_SECRET_KEY
API_DB_PASSWORD=$API_DB_PASSWORD
ENGINE_DB_PASSWORD=$ENGINE_DB_PASSWORD
PREVIEW_POSTGRES_DATA=$data_dir/postgres
WEB_ORIGIN=$web_origin
PREVIEW_WEB_PORT=$preview_port
TAILSCALE_IP=$tailscale_host
EOF
chmod 600 "$state_dir/.env"
printf '%s\n' "$expires_at" > "$state_dir/expires-at"
cat > "$state_dir/metadata.txt" <<EOF
stack_id=$stack_id
commit=$resolved_sha
started_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
expires_at=$(date -u -d "@$expires_at" +%Y-%m-%dT%H:%M:%SZ)
private_url=$web_origin
EOF

# A failed restore/build/image/start must not leave a half-created preview or
# consume disk indefinitely. Preserve whatever logs exist, then tear it down.
trap 'rc=$?; capture_logs; stop_stack; exit "$rc"' ERR

cd "$state_dir/source"
export MSBUILDDISABLENODEREUSE=1
dotnet restore RentalCommand.sln 2>&1 | tee "$artifact_dir/dotnet-restore.log"
dotnet build RentalCommand.sln --no-restore -c Release 2>&1 | tee "$artifact_dir/dotnet-build.log"
dotnet publish RentalCommand.Api/RentalCommand.Api.csproj -c Release --no-build \
  -o publish/api /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false
dotnet publish RentalCommand.Engine/RentalCommand.Engine.csproj -c Release --no-build \
  -o publish/engine /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false

docker build -f Dockerfile.api -t "rc-preview-api:$stack_id" publish/api
docker build -f Dockerfile.engine -t "rc-preview-engine:$stack_id" publish/engine
docker build -f Dockerfile.web -t "rc-preview-web:$stack_id" \
  --build-arg VITE_API_URL=/api/v1 .

compose up -d
for _ in $(seq 1 60); do
  if curl --fail --silent "$web_origin/health" > "$artifact_dir/health.json"; then
    capture_logs
    trap - ERR
    cat "$state_dir/metadata.txt"
    exit 0
  fi
  sleep 5
done

capture_logs
echo "Preview did not become healthy" >&2
trap - ERR
stop_stack
exit 1
