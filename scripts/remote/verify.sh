#!/usr/bin/env bash
set -euo pipefail

profile="${1:-standard}"
artifact_dir="${2:?artifact directory is required}"
test_filter="${3:-}"
mkdir -p "$artifact_dir/test-results" "$artifact_dir/logs"

case "$profile" in
  focused|standard|full) ;;
  *) echo "Unknown profile: $profile" >&2; exit 2 ;;
esac

run_logged() {
  local name="$1"
  shift
  "$@" 2>&1 | tee "$artifact_dir/logs/$name.log"
}

run_logged dotnet-restore dotnet restore RentalCommand.sln
run_logged dotnet-build dotnet build RentalCommand.sln --no-restore -c Release

if [[ "$profile" == focused ]]; then
  args=(dotnet test RentalCommand.Core.Tests/RentalCommand.Core.Tests.csproj --no-build -c Release
        --logger "trx;LogFileName=focused.trx" --results-directory "$artifact_dir/test-results")
  [[ -z "$test_filter" ]] || args+=(--filter "$test_filter")
  run_logged dotnet-focused "${args[@]}"
  exit 0
fi

for project in RentalCommand.Core.Tests RentalCommand.Data.Tests RentalCommand.Api.Tests RentalCommand.Engine.Tests; do
  run_logged "${project,,}" dotnet test "$project/$project.csproj" --no-build -c Release \
    --logger "trx;LogFileName=$project.trx" --results-directory "$artifact_dir/test-results"
done

run_logged pnpm-install pnpm --dir web install --frozen-lockfile
run_logged web-check pnpm --dir web check
run_logged web-unit pnpm --dir web test:unit

if [[ "$profile" == full ]]; then
  run_logged integration-tests dotnet test RentalCommand.IntegrationTests/RentalCommand.IntegrationTests.csproj \
    --no-build -c Release --logger "trx;LogFileName=integration.trx" \
    --results-directory "$artifact_dir/test-results"
  run_logged web-build pnpm --dir web build
fi
