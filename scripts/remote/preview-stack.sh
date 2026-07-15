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
fingerprints_file="$data_dir/build-fingerprints.env"
project="rc-preview-$stack_id"
repo_root="$(git rev-parse --show-toplevel)"

mkdir -p "$artifact_dir" "$preview_root" "$preview_data_root"
[[ "$stack_id" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]] || exit 2
[[ "$ttl_hours" =~ ^(1|2|4|8|12)$ ]] || exit 2
[[ "$preview_port" =~ ^[0-9]+$ ]] || exit 2

compose() {
  docker compose --project-name "$project" --env-file "$state_dir/.env" \
    -f "$state_dir/source/deploy/docker-compose.preview.yml" "$@"
}

tree_fingerprint() {
  git -C "$repo_root" ls-tree -r HEAD -- "$@" | sha256sum | awk '{print $1}'
}

cached_fingerprint() {
  local name="$1"
  [[ -f "$fingerprints_file" ]] || return 0
  sed -n "s/^${name}=//p" "$fingerprints_file" | head -n1
}

image_exists() {
  docker image inspect "$1" >/dev/null 2>&1
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
    sed -n -E 's/^(API_IMAGE|ENGINE_IMAGE|WEB_IMAGE)=//p' "$state_dir/.env" \
      | xargs -r docker image rm >/dev/null 2>&1 || true
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
  [[ "$(basename "$other")" == .* ]] && continue
  [[ "$(basename "$other")" == "$stack_id" ]] || {
    echo "Another Rental Command preview is active: $(basename "$other")" >&2
    exit 1
  }
done

resolved_sha="${RESOLVED_SHA:-$(git rev-parse HEAD)}"
tailscale_host="$(tailscale ip -4 2>/dev/null | head -n1)"
[[ "$tailscale_host" =~ ^100\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
  echo "Tailscale is not connected or did not return a tailnet IPv4 address" >&2
  exit 1
}
web_origin="http://$tailscale_host:$preview_port"
expires_at="$(( $(date +%s) + ttl_hours * 3600 ))"
candidate_dir="$preview_root/.${stack_id}.candidate-${GITHUB_RUN_ID:-$$}"
rm -rf -- "$preview_root/.${stack_id}.candidate-"*
rm -rf "$candidate_dir"
mkdir -p "$candidate_dir/source"
trap 'rm -rf "$candidate_dir"' ERR INT TERM
git -C "$repo_root" archive HEAD | tar -x -C "$candidate_dir/source"

mkdir -p "$data_dir/postgres"
if [[ ! -f "$credentials_file" ]]; then
  postgres_password="$(openssl rand -hex 24)"
  jwt_secret="$(openssl rand -base64 48 | tr -d '\n')"
  cat > "$credentials_file" <<EOF
POSTGRES_PASSWORD=$postgres_password
JWT_SECRET_KEY=$jwt_secret
EOF
  chmod 600 "$credentials_file"
fi

# shellcheck disable=SC1090
source "$credentials_file"
: "${POSTGRES_PASSWORD:?persistent preview Postgres password is required}"
: "${JWT_SECRET_KEY:?persistent preview JWT secret is required}"

# postgres:16-alpine runs as its postgres user. Preparing the bind mount through
# the same image avoids relying on host account mappings. Existing database
# contents already have the correct ownership, so this does not recursively
# rewrite a potentially large preserved database.
docker run --rm --entrypoint sh \
  -v "$data_dir/postgres:/var/lib/postgresql/data" postgres:16-alpine \
  -c 'chown postgres:postgres /var/lib/postgresql/data && chmod 700 /var/lib/postgresql/data'

api_fingerprint="$(tree_fingerprint \
  Directory.Build.props Directory.Build.targets Directory.Packages.props \
  global.json NuGet.Config nuget.config RentalCommand.sln Dockerfile.api \
  RentalCommand.Api RentalCommand.Core RentalCommand.Data)"
engine_fingerprint="$(tree_fingerprint \
  Directory.Build.props Directory.Build.targets Directory.Packages.props \
  global.json NuGet.Config nuget.config RentalCommand.sln Dockerfile.engine \
  RentalCommand.Api RentalCommand.Core RentalCommand.Data RentalCommand.Engine)"
web_fingerprint="$(tree_fingerprint Dockerfile.web web)"
gateway_fingerprint="$(tree_fingerprint \
  deploy/docker-compose.preview.yml deploy/preview-nginx.conf)"
api_image="rc-preview-api:$stack_id-${api_fingerprint:0:16}"
engine_image="rc-preview-engine:$stack_id-${engine_fingerprint:0:16}"
web_image="rc-preview-web:$stack_id-${web_fingerprint:0:16}"

api_changed=false
engine_changed=false
web_changed=false
gateway_changed=false
gateway_recreate_required=false
api_build_required=false
engine_build_required=false
web_build_required=false
if [[ "$(cached_fingerprint API)" != "$api_fingerprint" ]]; then
  api_changed=true
fi
if [[ "$(cached_fingerprint ENGINE)" != "$engine_fingerprint" ]]; then
  engine_changed=true
fi
if [[ "$(cached_fingerprint WEB)" != "$web_fingerprint" ]]; then
  web_changed=true
fi
if [[ "$(cached_fingerprint GATEWAY)" != "$gateway_fingerprint" ]]; then
  gateway_changed=true
fi
active_api_image="$(sed -n 's/^API_IMAGE=//p' "$state_dir/.env" 2>/dev/null | head -n1)"
active_web_image="$(sed -n 's/^WEB_IMAGE=//p' "$state_dir/.env" 2>/dev/null | head -n1)"
if [[ "$gateway_changed" == true \
  || "$active_api_image" != "$api_image" \
  || "$active_web_image" != "$web_image" ]]; then
  gateway_recreate_required=true
fi
image_exists "$api_image" || api_build_required=true
image_exists "$engine_image" || engine_build_required=true
image_exists "$web_image" || web_build_required=true

{
  echo "api_changed=$api_changed"
  echo "engine_changed=$engine_changed"
  echo "web_changed=$web_changed"
  echo "gateway_changed=$gateway_changed"
  echo "gateway_recreate_required=$gateway_recreate_required"
  echo "api_build_required=$api_build_required"
  echo "engine_build_required=$engine_build_required"
  echo "web_build_required=$web_build_required"
} | tee "$artifact_dir/build-plan.txt"

cat > "$candidate_dir/.env" <<EOF
API_IMAGE=$api_image
ENGINE_IMAGE=$engine_image
WEB_IMAGE=$web_image
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
JWT_SECRET_KEY=$JWT_SECRET_KEY
PREVIEW_POSTGRES_DATA=$data_dir/postgres
WEB_ORIGIN=$web_origin
PREVIEW_WEB_PORT=$preview_port
TAILSCALE_IP=$tailscale_host
EOF
chmod 600 "$candidate_dir/.env"
printf '%s\n' "$expires_at" > "$candidate_dir/expires-at"
cat > "$candidate_dir/metadata.txt" <<EOF
stack_id=$stack_id
commit=$resolved_sha
started_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
expires_at=$(date -u -d "@$expires_at" +%Y-%m-%dT%H:%M:%SZ)
private_url=$web_origin
api_rebuilt=$api_build_required
engine_rebuilt=$engine_build_required
web_rebuilt=$web_build_required
gateway_recreated=$gateway_recreate_required
EOF

activated=false
had_previous=false
rollback_start() {
  local rc="$1"
  trap - ERR
  set +e
  capture_logs
  if [[ "$activated" == true ]]; then
    if [[ "$had_previous" == true ]]; then
      # This restores binaries and configuration. Any EF migration already
      # committed by the candidate remains, so preview migrations must retain
      # backward compatibility with the immediately preceding application.
      rm -rf "$state_dir/source"
      mv "$state_dir/source.previous" "$state_dir/source"
      mv "$state_dir/.env.previous" "$state_dir/.env"
      mv "$state_dir/expires-at.previous" "$state_dir/expires-at"
      if [[ -f "$state_dir/metadata.previous" ]]; then
        mv "$state_dir/metadata.previous" "$state_dir/metadata.txt"
      else
        rm -f "$state_dir/metadata.txt"
      fi
      compose up -d --remove-orphans
      compose up -d --no-deps --force-recreate gateway
    else
      compose down --remove-orphans
      rm -rf "$state_dir"
    fi
  fi
  [[ "$api_build_required" == true ]] && docker image rm "$api_image" >/dev/null 2>&1
  [[ "$engine_build_required" == true ]] && docker image rm "$engine_image" >/dev/null 2>&1
  [[ "$web_build_required" == true ]] && docker image rm "$web_image" >/dev/null 2>&1
  rm -rf "$candidate_dir"
  exit "$rc"
}
trap 'rollback_start "$?"' ERR
trap 'rollback_start 130' INT
trap 'rollback_start 143' TERM

cd "$candidate_dir/source"
export MSBUILDDISABLENODEREUSE=1
if [[ "$api_build_required" == true ]]; then
  dotnet restore RentalCommand.Api/RentalCommand.Api.csproj \
    2>&1 | tee "$artifact_dir/dotnet-restore-api.log"
  dotnet publish RentalCommand.Api/RentalCommand.Api.csproj -c Release --no-restore \
    -o publish/api /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false \
    2>&1 | tee "$artifact_dir/dotnet-publish-api.log"
  docker build --label "com.rentalcommand.preview=$stack_id" \
    -f Dockerfile.api -t "$api_image" publish/api
fi

if [[ "$engine_build_required" == true ]]; then
  dotnet restore RentalCommand.Engine/RentalCommand.Engine.csproj \
    2>&1 | tee "$artifact_dir/dotnet-restore-engine.log"
  dotnet publish RentalCommand.Engine/RentalCommand.Engine.csproj -c Release --no-restore \
    -o publish/engine /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false \
    2>&1 | tee "$artifact_dir/dotnet-publish-engine.log"
  docker build --label "com.rentalcommand.preview=$stack_id" \
    -f Dockerfile.engine -t "$engine_image" publish/engine
fi

if [[ "$web_build_required" == true ]]; then
  docker build --label "com.rentalcommand.preview=$stack_id" \
    -f Dockerfile.web -t "$web_image" \
    --build-arg VITE_API_URL=/api/v1 .
fi

cd "$preview_root"
mkdir -p "$state_dir"
rm -rf "$state_dir/source.previous"
if [[ -d "$state_dir/source" && -f "$state_dir/.env" ]]; then
  had_previous=true
  activated=true
  mv "$state_dir/source" "$state_dir/source.previous"
  cp "$state_dir/.env" "$state_dir/.env.previous"
  if [[ -f "$state_dir/expires-at" ]]; then
    cp "$state_dir/expires-at" "$state_dir/expires-at.previous"
  else
    printf '0\n' > "$state_dir/expires-at.previous"
  fi
  if [[ -f "$state_dir/metadata.txt" ]]; then
    cp "$state_dir/metadata.txt" "$state_dir/metadata.previous"
  fi
else
  activated=true
fi
mv "$candidate_dir/source" "$state_dir/source"
mv "$candidate_dir/.env" "$state_dir/.env"
mv "$candidate_dir/expires-at" "$state_dir/expires-at"

compose up -d --remove-orphans
if [[ "$gateway_recreate_required" == true ]]; then
  # Compose hashes the bind-mount path, not the nginx file contents.
  compose up -d --no-deps --force-recreate gateway
fi
for _ in $(seq 1 60); do
  engine_health="$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
    "${project}-engine-1" 2>/dev/null || true)"
  if curl --fail --silent "$web_origin/health" > "$artifact_dir/health.json" \
    && curl --fail --silent "$web_origin/welcome" > /dev/null \
    && [[ "$engine_health" == healthy ]]; then
    mv "$candidate_dir/metadata.txt" "$state_dir/metadata.txt"
    fingerprints_tmp="$fingerprints_file.tmp"
    cat > "$fingerprints_tmp" <<EOF
API=$api_fingerprint
ENGINE=$engine_fingerprint
WEB=$web_fingerprint
GATEWAY=$gateway_fingerprint
EOF
    chmod 600 "$fingerprints_tmp"
    mv "$fingerprints_tmp" "$fingerprints_file"
    if [[ "$had_previous" == true ]]; then
      while IFS= read -r previous_image; do
        [[ "$previous_image" == "$api_image" \
          || "$previous_image" == "$engine_image" \
          || "$previous_image" == "$web_image" ]] && continue
        docker image rm "$previous_image" >/dev/null 2>&1 || true
      done < <(sed -n -E 's/^(API_IMAGE|ENGINE_IMAGE|WEB_IMAGE)=//p' \
        "$state_dir/.env.previous")
    fi
    rm -rf "$state_dir/source.previous" "$state_dir/.env.previous" \
      "$state_dir/expires-at.previous" "$state_dir/metadata.previous" \
      "$candidate_dir"
    # Stable tags keep the active images bounded; remove only this preview's
    # older dangling image generations after a successful replacement.
    docker image prune -f --filter "label=com.rentalcommand.preview=$stack_id" \
      > "$artifact_dir/image-prune.txt" 2>&1 || true
    while IFS= read -r preview_image; do
      [[ "$preview_image" == "$api_image" \
        || "$preview_image" == "$engine_image" \
        || "$preview_image" == "$web_image" ]] && continue
      docker image rm "$preview_image" >/dev/null 2>&1 || true
    done < <(docker image ls --filter "label=com.rentalcommand.preview=$stack_id" \
      --format '{{.Repository}}:{{.Tag}}')
    docker builder prune -f --filter until=168h --reserved-space 5gb \
      --max-used-space 20gb > "$artifact_dir/builder-prune.txt" 2>&1 || true
    capture_logs
    trap - ERR
    cat "$state_dir/metadata.txt"
    exit 0
  fi
  sleep 5
done

capture_logs
echo "Preview did not become healthy" >&2
rollback_start 1
