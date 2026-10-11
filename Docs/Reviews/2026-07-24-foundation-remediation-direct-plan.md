# Foundation remediation direct plan

Date: 2026-07-24  
Task: TSK-733, continuing TSK-668/670/672/674 foundation work  
Authority: `2026-07-24-foundation-remediation-handoff.md` and the approved foundation blueprint

## Working rules

- Work directly in the existing dirty worktree. Do not stage, commit, stash, reset, clean, or revert it.
- Keep every filter, join, aggregate, sort, authorization scope, and page DB-side.
- Run one heavy build, test, deployment, or emulator operation at a time.
- Fix reproduced failures in coherent batches. Do not create a review loop for each edit.
- Stop before the separate `run-e2e-tests` workflow.

## Milestone 1 — accept the source

1. [x] Correct the authorization integration-test fixture without weakening the API-only database guard.
2. [x] Run the focused backend acceptance commands on the Azure verification runner.
3. [x] Fix any reproduced backend failures as one batch.
4. [x] Correct the two known DB-side violations:
   - project the Tax W-9 count from the API;
   - filter and page Unit transfer choices on the server.
5. [x] Run native TypeScript, Svelte, and web unit acceptance serially.
6. [x] Fix reproduced web failures as one batch.

### Review gate 1

- Code reviewer: inspect the combined backend/web diff for correctness, performance regressions, unsafe connection lifetime, and accidental complexity.
- Compliance reviewer: verify the combined source still follows the approved blueprint, especially DB-side querying, API authorization, six Unit areas, canonical records, and loading/error behavior.
- The implementer fixes only confirmed findings, then reruns the nearest acceptance checks once.

Status: passed after one confirmed clock-consistency correction and its focused recheck.

## Milestone 2 — replace the Azure preview and prove speed

1. [x] Hand off the exact dirty source under the shared Azure lock, recording base SHA and source hash.
2. [x] Rebuild and recreate the authorized Rental Command preview stack and throwaway database.
3. [x] Verify API, Engine, web, database, gateway, and stable Tailscale URL health.
4. [x] Build and install the latest Android APK on Azure `emulator-5554`.
5. [x] After warm-up, collect at least 10 serial samples:
   - Dashboard API p50 at most 2 seconds and p95 at most 3 seconds;
   - accounting snapshot p95 at most 1 second;
   - Dashboard money, activity, and occupancy SQL p95 at most 500 ms;
   - no 20-second browser cancel/retry pattern.
6. [x] Capture and correct the evidenced Security Deposit and workspace-authorization query costs without raising timeouts or adding retries.

## Milestone 3 — verify web and mobile behavior

1. [x] Verify web loading, failure, retry, and success states in three related batches:
   - Dashboard, Guided Setup, Unit detail, Lease detail;
   - Leasing, Technician, Owner, Tenant portal;
   - Accounting, Banking, Tax/Owner reports, notification settings.
2. [x] Check stable loader geometry, useful errors, working retries, and the corrected lease, accounting, and Security Deposit layouts.
3. [x] Verify the latest Android source on the emulator, preserving the six Unit destinations and prior accepted flows.
4. [x] Finish the handoff checks that can be exercised with the available authorized fixtures:
   - live SignalR update without refresh;
   - AI Help article;
   - Engine-backed Scan / Add;
   - original signed lease PDF download;
   - plural overdue copy;
   - controlled outbound integration smoke checks.
   SignalR tenant proof, the tenant PDF/plural fixtures, and controlled outbound providers remain
   explicitly assigned to the separate end-to-end pass.
5. [x] Fix reproduced product defects in one web/mobile batch and re-prove the affected flows.

### Review gate 2

- Code reviewer: inspect the final correction batch and runtime evidence for regressions or avoidable complexity.
- Compliance reviewer: compare the proven web/mobile behavior with the approved foundation blueprint and list only evidenced remaining discrepancies.
- The implementer addresses confirmed in-scope findings and performs one final focused recheck.

Status: passed after replacing the fixed first-100 Unit transfer list with a searchable,
server-paged selector. The focused PostgreSQL test, selector contract tests, native TypeScript,
Svelte check, live 200 response, and browser UI proof all passed.

## Milestone 4 — close the handoff

1. [x] Update TSK-733 with exact source identity, automated results, deployment identity, latency samples, browser/emulator proof, and any remaining discrepancies.
2. [x] Verify the task state is accurate.
3. [x] Stop and return control to the user before `run-e2e-tests`.

Detailed evidence: `../Testing/Results/2026-07-16-foundation-parallel/foundation-remediation-final-20260725.md`
