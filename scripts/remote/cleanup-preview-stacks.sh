#!/usr/bin/env bash
# Install this script with the preview-stack script on the Azure host and invoke
# it from a systemd timer every 15 minutes. It makes TTL teardown independent of
# a later GitHub Actions run.
set -euo pipefail

repo_tools="${RENTAL_COMMAND_RUNNER_TOOLS:-/opt/runner-tools/rental-command}"
artifact_root="${PREVIEW_CLEANUP_LOG_ROOT:-/var/log/rental-command-preview-cleanup}"
mkdir -p "$artifact_root"

exec flock --wait 300 /run/lock/azure-build-runner.lock \
  env PREVIEW_ROOT="${PREVIEW_ROOT:-/srv/dev-stacks/rental-command}" \
  "$repo_tools/preview-stack.sh" cleanup cleanup 1 \
  "$artifact_root/$(date -u +%Y%m%dT%H%M%SZ)"
