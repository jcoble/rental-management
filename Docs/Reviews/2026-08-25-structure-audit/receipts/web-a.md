# web-a receipt

- Lane: web-a — web dead code, duplicate endpoints, folder merges, shared helpers
- Start time: 2026-08-25T16:04:42-04:00

## Item results

- Item 1: implemented in the worktree; 25 unused files deleted. The separator primitive was kept because `web/src/lib/components/ui/select/select-separator.svelte` imports it. Commit unavailable because Git could not write the worktree index lock.
- Item 2: implemented in the worktree; duplicate accounting cash-flow surface and local year-end cash-flow types removed. Commit unavailable because Git could not write the worktree index lock.
- Item 3: implemented in the worktree; dead tenant-account deposit/reversal members and their path-builder assertions removed. Commit unavailable because Git could not write the worktree index lock.
- Item 4: implemented in the worktree; `sentenceCaseIdentifier` is exported from `money-display.ts` and imported by `accounting-display.ts`, with the accounting fallback preserved. Commit unavailable because Git could not write the worktree index lock.
- Item 5: implemented in the worktree; six authenticated download flows now call `downloadFile`. Commit unavailable because Git could not write the worktree index lock.
- Item 6: implemented in the worktree; all 11 endpoint modules use `buildListQuery`, including repeated array values. Commit unavailable because Git could not write the worktree index lock.
- Item 7: scan and unit folders were merged, the source-only QA test was deleted, and the banking test was moved beside its route. The favicon move was skipped after the required collision stop: `web/static/favicon.svg` already exists. Commit unavailable because Git could not write the worktree index lock.

## Verification

- `pnpm --dir web install --frozen-lockfile`: exit 1. Exact blocker: `[ERR_SQLITE_ERROR] unable to open database file`; pnpm also reported `[ERR_PNPM_META_FETCH_FAIL] GET https://registry.npmjs.org/pnpm: fetch failed`.
- Commit attempt: blocked by `fatal: Unable to create '/home/blackcolours/dev/work/rental-management/.git/worktrees/simplify-web-a/index.lock': Read-only file system`.

## Controller verification and corrections (2026-08-25)
- The lane's sandbox blocked git commits and pnpm's store; the controller staged and committed the work and ran verification.
- `pnpm --dir web install --frozen-lockfile`, `check`, `check:native`, `build`: all exit 0.
- `pnpm --dir web test`: one failure remains, `src/lib/leases/lease-action-hub-contract.test.ts`, which fails identically on main (pre-existing source-text assertion on a dialog this lane did not touch).
- Fix: `src/routes/(portal)/portal/payments/account-history-contract.test.ts` asserted the source text `/history${queryString(params)}`; updated to match the shared `buildListQuery` call the lane introduced.
- Review fix: restored `web/src/lib/components/ui/command/` — `web/component-tests/ModalDismissGuardHarness.svelte:4` still imports it (the audit's rg missed `component-tests/`).
