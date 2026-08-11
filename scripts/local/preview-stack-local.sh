#!/usr/bin/env bash
set -euo pipefail

# Rental Command local preview lifecycle.
#
# The checked-in API and Engine Dockerfiles are runtime-only images. This script therefore
# publishes both applications inside a disposable .NET SDK container before assembling the
# three images, so no dotnet build ever runs on the Mac host.

action="${1:-}"
case "$action" in
  start|rebuild|stop|status|logs) ;;
  *)
    printf 'Usage: %s {start|rebuild|stop|status|logs} [service ...]\n' "$0" >&2
    exit 2
    ;;
esac

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
repo_root="$(git -C "$script_dir/../.." rev-parse --show-toplevel)"
stack_root="${RENTAL_COMMAND_LOCAL_STACK_ROOT:-${HOME}/dev/work/rental-command-local-stack}"
mkdir -p "$stack_root"
stack_root="$(cd -- "$stack_root" && pwd -P)"
state_dir="$stack_root/state"
data_dir="$stack_root/data"
postgres_data_dir="$data_dir/postgres"
credentials_file="$data_dir/credentials.env"
env_file="$state_dir/.env"
metadata_file="$state_dir/metadata.txt"
build_sha_file="$state_dir/build.sha"
build_metrics_file="$state_dir/build-metrics.tsv"
project_name="rental-command-local"
preview_port="15667"
loopback_ip="127.0.0.1"
web_origin="http://${loopback_ip}:${preview_port}"
build_platform="linux/amd64"
nuget_cache_dir="${RENTAL_COMMAND_NUGET_CACHE_DIR:-${HOME}/.nuget/packages}"
api_image="rc-api:local"
engine_image="rc-engine:local"
web_image="rc-web:local"

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command is missing: %s\n' "$1" >&2
    exit 1
  }
}

require_prerequisites() {
  require_command git
  require_command docker
  require_command curl
  require_command openssl
  require_command lsof
  docker info >/dev/null
  docker compose version >/dev/null
  docker buildx version >/dev/null
}

compose() {
  docker compose \
    --project-name "$project_name" \
    --project-directory "$repo_root/deploy" \
    --env-file "$env_file" \
    -f "$repo_root/deploy/docker-compose.preview.yml" \
    "$@"
}

read_credential() {
  local key="$1"
  sed -n "s/^${key}=//p" "$credentials_file" | tail -n 1
}

prepare_credentials() {
  mkdir -p "$state_dir" "$postgres_data_dir"
  if [[ ! -e "$credentials_file" ]]; then
    (
      umask 077
      printf 'POSTGRES_PASSWORD=%s\n' "$(openssl rand -hex 24)"
      printf 'JWT_SECRET_KEY=%s\n' "$(openssl rand -hex 48)"
      printf 'API_DB_PASSWORD=%s\n' "$(openssl rand -hex 24)"
      printf 'ENGINE_DB_PASSWORD=%s\n' "$(openssl rand -hex 24)"
    ) > "$credentials_file"
    printf 'Generated persistent credentials: %s\n' "$credentials_file"
  fi
  [[ -f "$credentials_file" ]] || {
    printf 'Credentials path is not a regular file: %s\n' "$credentials_file" >&2
    exit 1
  }
  chmod 600 "$credentials_file"

  POSTGRES_PASSWORD="$(read_credential POSTGRES_PASSWORD)"
  JWT_SECRET_KEY="$(read_credential JWT_SECRET_KEY)"
  API_DB_PASSWORD="$(read_credential API_DB_PASSWORD)"
  ENGINE_DB_PASSWORD="$(read_credential ENGINE_DB_PASSWORD)"
  [[ -n "$POSTGRES_PASSWORD" && -n "$JWT_SECRET_KEY" && -n "$API_DB_PASSWORD" && -n "$ENGINE_DB_PASSWORD" ]] || {
    printf 'Credentials file is missing one or more required values: %s\n' "$credentials_file" >&2
    exit 1
  }
  export POSTGRES_PASSWORD JWT_SECRET_KEY API_DB_PASSWORD ENGINE_DB_PASSWORD
}

write_compose_env() {
  local repo_sha
  repo_sha="$(git -C "$repo_root" rev-parse HEAD)"
  (
    umask 077
    cat > "$env_file" <<EOF
API_IMAGE=$api_image
ENGINE_IMAGE=$engine_image
WEB_IMAGE=$web_image
POSTGRES_PASSWORD=$POSTGRES_PASSWORD
JWT_SECRET_KEY=$JWT_SECRET_KEY
API_DB_PASSWORD=$API_DB_PASSWORD
ENGINE_DB_PASSWORD=$ENGINE_DB_PASSWORD
PREVIEW_POSTGRES_DATA=$postgres_data_dir
WEB_ORIGIN=$web_origin
PREVIEW_WEB_PORT=$preview_port
TAILSCALE_IP=$loopback_ip
EOF
  )
  chmod 600 "$env_file"
  cat > "$metadata_file" <<EOF
repo_root=$repo_root
repo_sha=$repo_sha
project=$project_name
gateway_url=$web_origin
gateway_bind=${loopback_ip}:${preview_port}
postgres_data=$postgres_data_dir
credentials=$credentials_file
EOF
  chmod 600 "$metadata_file"
}

ensure_postgres_data_permissions() {
  # The official image owns the bind-mounted data directory as its postgres user. This is
  # safe for both a fresh database and an existing local preview database; no database files
  # are deleted or reset here.
  docker run --rm --platform "$build_platform" --entrypoint sh \
    -v "$postgres_data_dir:/var/lib/postgresql/data" \
    postgres:16-alpine \
    -c 'chown postgres:postgres /var/lib/postgresql/data && chmod 700 /var/lib/postgresql/data'
}

image_is_ready() {
  local image="$1" platform
  platform="$(docker image inspect "$image" --format '{{.Architecture}}/{{.Os}}' 2>/dev/null || true)"
  [[ "$platform" == "amd64/linux" ]]
}

images_need_build() {
  local current_sha="$1"
  if [[ ! -f "$build_sha_file" || "$(cat "$build_sha_file")" != "$current_sha" ]]; then
    return 0
  fi
  image_is_ready "$api_image" && image_is_ready "$engine_image" && image_is_ready "$web_image" || return 0
  return 1
}

record_image_metric() {
  local image="$1" elapsed="$2" size
  size="$(docker image inspect "$image" --format '{{.Size}}')"
  printf '%s\t%s\t%s\t%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$image" "$elapsed" "$size" >> "$build_metrics_file"
  printf 'Built %s in %ss; image size %s bytes\n' "$image" "$elapsed" "$size"
}

build_images() {
  local build_dir started_at elapsed current_sha
  build_dir="$(mktemp -d "$state_dir/build.XXXXXX")"
  trap 'rc=$?; rm -rf -- "$build_dir"; exit "$rc"' ERR
  current_sha="$(git -C "$repo_root" rev-parse HEAD)"

  printf 'Build source: git archive HEAD (%s)\n' "$current_sha"
  git -C "$repo_root" archive --format=tar HEAD | tar -xf - -C "$build_dir"

  started_at="$SECONDS"
  printf 'COMMAND: docker run --rm --platform %s mcr.microsoft.com/dotnet/sdk:10.0 (restore, build, publish API + Engine)\n' "$build_platform"
  mkdir -p "$nuget_cache_dir"
  docker run --rm --platform "$build_platform" \
    -e MSBUILDDISABLENODEREUSE=1 \
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -v "$build_dir:/src" \
    -v "$nuget_cache_dir:/root/.nuget/packages" \
    -w /src \
    mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -ceu '
      dotnet restore RentalCommand.sln
      dotnet build RentalCommand.sln --no-restore -c Release
      dotnet publish RentalCommand.Api/RentalCommand.Api.csproj -c Release --no-build \
        -o publish/api /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false
      dotnet publish RentalCommand.Engine/RentalCommand.Engine.csproj -c Release --no-build \
        -o publish/engine /p:UseAppHost=false /p:ErrorOnDuplicatePublishOutputFiles=false
    '
  elapsed=$((SECONDS - started_at))
  printf 'Published API + Engine inside Docker in %ss\n' "$elapsed"

  if [[ ! -f "$build_metrics_file" ]]; then
    printf 'timestamp\timage\tseconds\tsize_bytes\n' > "$build_metrics_file"
  fi

  started_at="$SECONDS"
  printf 'COMMAND: docker buildx build --platform %s --load -f Dockerfile.api -t %s publish/api\n' "$build_platform" "$api_image"
  docker buildx build --platform "$build_platform" --load --provenance=false --progress=plain \
    -f "$build_dir/Dockerfile.api" \
    -t "$api_image" \
    "$build_dir/publish/api"
  elapsed=$((SECONDS - started_at))
  record_image_metric "$api_image" "$elapsed"

  started_at="$SECONDS"
  printf 'COMMAND: docker buildx build --platform %s --load -f Dockerfile.engine -t %s publish/engine\n' "$build_platform" "$engine_image"
  docker buildx build --platform "$build_platform" --load --provenance=false --progress=plain \
    -f "$build_dir/Dockerfile.engine" \
    -t "$engine_image" \
    "$build_dir/publish/engine"
  elapsed=$((SECONDS - started_at))
  record_image_metric "$engine_image" "$elapsed"

  started_at="$SECONDS"
  printf 'COMMAND: docker buildx build --platform %s --load -f Dockerfile.web --build-arg VITE_API_URL=/api/v1 -t %s .\n' "$build_platform" "$web_image"
  docker buildx build --platform "$build_platform" --load --provenance=false --progress=plain \
    -f "$build_dir/Dockerfile.web" \
    --build-arg VITE_API_URL=/api/v1 \
    -t "$web_image" \
    "$build_dir"
  elapsed=$((SECONDS - started_at))
  record_image_metric "$web_image" "$elapsed"

  printf '%s\n' "$current_sha" > "$build_sha_file"
  chmod 600 "$build_sha_file" "$build_metrics_file"
  trap - ERR
  rm -rf -- "$build_dir"
}

check_port_available() {
  local gateway_container
  gateway_container="$(compose ps -q gateway 2>/dev/null || true)"
  if [[ -n "$gateway_container" ]]; then
    return 0
  fi
  if lsof -nP -iTCP:"$preview_port" -sTCP:LISTEN >/tmp/rental-command-local-port-check.txt 2>/dev/null; then
    printf 'Host port %s is already in use; refusing to disturb the owner:\n' "$preview_port" >&2
    sed -n '1,20p' /tmp/rental-command-local-port-check.txt >&2
    rm -f /tmp/rental-command-local-port-check.txt
    exit 1
  fi
  rm -f /tmp/rental-command-local-port-check.txt
}

verify_loopback_binding() {
  local gateway_container bindings
  gateway_container="$(compose ps -q gateway)"
  [[ -n "$gateway_container" ]] || {
    printf 'Gateway container was not created.\n' >&2
    exit 1
  }
  bindings="$(docker inspect "$gateway_container" --format '{{json .HostConfig.PortBindings}}')"
  [[ "$bindings" == *"\"HostIp\":\"${loopback_ip}\""* && "$bindings" == *"\"HostPort\":\"${preview_port}\""* ]] || {
    printf 'Gateway binding is not loopback-only: %s\n' "$bindings" >&2
    exit 1
  }
  printf 'Gateway binding verified: %s:%s -> container 8080\n' "$loopback_ip" "$preview_port"
}

wait_for_gateway() {
  local deadline=$((SECONDS + 240)) response
  while (( SECONDS < deadline )); do
    response="$(curl -sS -o "$state_dir/gateway-health.json" -w '%{http_code}' "$web_origin/health" || true)"
    if [[ "$response" == "200" ]]; then
      printf 'Gateway healthy: %s/health (HTTP %s)\n' "$web_origin" "$response"
      return 0
    fi
    sleep 3
  done
  printf 'Gateway did not become healthy within 240 seconds.\n' >&2
  compose ps --all >&2 || true
  compose logs --no-color --timestamps --tail=200 migrate api engine web gateway >&2 || true
  exit 1
}

start_stack() {
  local current_sha="$1" replace="$2"
  prepare_credentials
  write_compose_env
  ensure_postgres_data_permissions
  check_port_available
  if [[ "$replace" == "1" ]]; then
    compose up -d --force-recreate --remove-orphans
  else
    compose up -d --remove-orphans
  fi
  verify_loopback_binding
  wait_for_gateway
  printf 'Preview started from HEAD %s\n' "$current_sha"
  printf 'Open %s/login\n' "$web_origin"
}

case "$action" in
  start)
    require_prerequisites
    prepare_credentials
    write_compose_env
    current_sha="$(git -C "$repo_root" rev-parse HEAD)"
    if images_need_build "$current_sha"; then
      printf 'Images are absent or were not built from current HEAD; building serially.\n'
      build_images
      start_stack "$current_sha" 1
    else
      start_stack "$current_sha" 0
    fi
    ;;
  rebuild)
    require_prerequisites
    prepare_credentials
    write_compose_env
    printf 'Stopping application containers before the serial rebuild; PostgreSQL data remains at %s\n' "$postgres_data_dir"
    compose down --remove-orphans || true
    build_images
    current_sha="$(git -C "$repo_root" rev-parse HEAD)"
    start_stack "$current_sha" 1
    ;;
  stop)
    require_prerequisites
    if [[ ! -f "$env_file" ]]; then
      printf 'Local preview is not initialized; nothing to stop.\n'
      exit 0
    fi
    compose down --remove-orphans
    printf 'Preview stopped. PostgreSQL data preserved at %s\n' "$postgres_data_dir"
    ;;
  status)
    require_prerequisites
    if [[ ! -f "$env_file" ]]; then
      printf 'Local preview is not initialized. Expected state at %s\n' "$state_dir"
      exit 0
    fi
    compose ps --all
    [[ ! -f "$metadata_file" ]] || sed -E 's#^(credentials=).*$#\1[mode-600 path]#' "$metadata_file"
    printf 'gateway=%s\npostgres_data=%s\ncredentials=%s\n' "$web_origin" "$postgres_data_dir" "$credentials_file"
    ;;
  logs)
    require_prerequisites
    if [[ ! -f "$env_file" ]]; then
      printf 'Local preview is not initialized. Expected state at %s\n' "$state_dir" >&2
      exit 1
    fi
    shift
    compose logs --no-color --timestamps --tail=200 "$@"
    ;;
esac
