#!/usr/bin/env bash
# Arm Rental Command's local git hooks by pointing core.hooksPath at the tracked
# scripts/hooks/ dir. These hooks replace CI: free, local, $0, zero GitHub minutes.
#
#   Run once per clone:  ./scripts/install-git-hooks.sh
#
# core.hooksPath lives in the repo's git config (shared by all worktrees) and
# resolves relative to each worktree root, so a single install covers them all.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

chmod +x scripts/hooks/* 2>/dev/null || true
git config core.hooksPath scripts/hooks

echo "Git hooks armed → core.hooksPath = scripts/hooks"
echo "  pre-commit : blocks unresolved merge-conflict markers (fast)"
echo "  pre-push   : svelte-check on web/ changes, dotnet build on .NET changes (docs-only skipped)"
echo ""
echo "  Bypass a hook:        git commit/push --no-verify"
echo "  Include .NET tests:   RC_PRE_PUSH_FULL=1 git push"
